using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>A phone or PC with LocalSend in the same network.</summary>
public sealed class LocalSendDevice : ObservableObject
{
    public string Fingerprint { get; init; }
    public string Alias { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 53317;
    public string Protocol { get; set; } = "https";
    public string DeviceType { get; set; } = "desktop";
    public string DeviceModel { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;
    /// <summary>Symbol for the tile.</summary>
    public string Icon => DeviceType switch { "mobile" => "iphone", "web" => "globe", "headless" or "server" => "cpu", _ => "display" };
    public string Detail => string.IsNullOrEmpty(DeviceModel) ? Ip : DeviceModel;
    public void Touch() { Raise(nameof(Alias)); Raise(nameof(Detail)); Raise(nameof(Icon)); }
}

/// <summary>Someone wants to send files – the island asks.</summary>
public sealed record LocalSendRequest(string From, string DeviceType, IReadOnlyList<(string Name, long Size)> Files);

/// <summary>
/// LocalSend in the island (user wish 2026-11-17), protocol v2 (github.com/localsend/protocol): devices find each
/// other by UDP multicast 224.0.0.167:53317, files go over HTTP. The island runs a small plain-HTTP server on port
/// 53317 (protocol "http", the fingerprint is a random id) and talks to the other devices over HTTPS or HTTP –
/// LocalSend's own certificates are self-signed, so they are accepted as they are.
/// Received files land in Downloads\PeakPill; nothing is received without the user's "Annehmen".
/// </summary>
public sealed class LocalSendService : ObservableObject
{
    public const int Port = 53317;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.167");
    private const string Api = "/api/localsend/v2/";

    public ObservableCollection<LocalSendDevice> Devices { get; } = new();

    /// <summary>The island decides whether incoming files are taken (true = yes).</summary>
    public Func<LocalSendRequest, Task<bool>> Ask;
    public event Action<IReadOnlyList<string>> Received;
    public event Action<string, bool> Notice;

    private string _status = "", _fingerprint;
    private double _progress = -1;
    private bool _running, _serverUp;
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>0 … 1 while files move, −1 otherwise.</summary>
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Raise(nameof(Moving)); } }
    public bool Moving => _progress >= 0;
    public bool ServerUp { get => _serverUp; private set => Set(ref _serverUp, value); }

    public static string Alias => string.IsNullOrWhiteSpace(App.Settings.LocalSendName) ? Environment.MachineName : App.Settings.LocalSendName.Trim();
    public static string SaveDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PeakPill");

    private TcpListener _server;
    private UdpClient _udp;
    private CancellationTokenSource _cts;
    private System.Windows.Threading.DispatcherTimer _timer;
    private static HttpClientHandler Trusting() => new()
    {
        // LocalSend makes its own certificate on every device – there is no authority to check it against
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        CheckCertificateRevocationList = false,
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
    };
    private static readonly HttpClient Http = new(Trusting()) { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient Upload = new(Trusting()) { Timeout = Timeout.InfiniteTimeSpan };

    // ---------- start / stop ----------

    public void Start()
    {
        if (_running) return;
        _running = true;
        _fingerprint = App.Settings.LocalSendId;
        if (string.IsNullOrEmpty(_fingerprint)) { _fingerprint = Guid.NewGuid().ToString("N"); App.Settings.LocalSendId = _fingerprint; App.SaveSettings(); }
        _cts = new CancellationTokenSource();
        StartServer();
        StartMulticast();
        _ = Announce();
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) =>
        {
            // devices that went quiet for a while are gone; ask again who is there
            foreach (var d in Devices.Where(d => DateTime.Now - d.LastSeen > TimeSpan.FromMinutes(3)).ToList()) Devices.Remove(d);
            _ = Announce();
        };
        _timer.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer?.Stop();
        _cts?.Cancel();
        try { _server?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
        ServerUp = false;
        Devices.Clear();
    }

    /// <summary>"Hello, who is here?" – the others answer with /register (or a multicast reply).</summary>
    public async Task Announce()
    {
        if (!_running || _udp == null) return;
        try
        {
            var msg = Info(withPort: true);
            msg["announce"] = true;
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await _udp.SendAsync(bytes, bytes.Length, new IPEndPoint(Group, Port));
        }
        catch { }
    }

    private JsonObject Info(bool withPort)
    {
        var o = new JsonObject
        {
            ["alias"] = Alias,
            ["version"] = "2.0",
            ["deviceModel"] = "PeakPill",
            ["deviceType"] = "desktop",
            ["fingerprint"] = _fingerprint,
        };
        // the answers to /register and /info carry no port or protocol (as in the protocol's examples)
        if (withPort) { o["port"] = Port; o["protocol"] = "http"; }
        o["download"] = false;
        return o;
    }

    // ---------- discovery ----------

    private void StartMulticast()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            // every network card (Wi-Fi and cable) – a phone may be on either
            foreach (var ip in LocalAddresses())
                try { _udp.JoinMulticastGroup(Group, ip); } catch { }
            _udp.MulticastLoopback = false;
            _ = ListenMulticast(_cts.Token);
        }
        catch (Exception ex) { Status = L.T("LocalSend: Netzwerk nicht erreichbar") + " (" + ex.Message + ")"; }
    }

    private static IEnumerable<IPAddress> LocalAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    private async Task ListenMulticast(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch { if (ct.IsCancellationRequested) return; await Task.Delay(500, ct).ContinueWith(_ => { }); continue; }
            try
            {
                var json = JsonNode.Parse(Encoding.UTF8.GetString(r.Buffer));
                if (json == null) continue;
                var fp = json["fingerprint"]?.GetValue<string>();
                if (string.IsNullOrEmpty(fp) || fp == _fingerprint) continue;
                var dev = Seen(json, r.RemoteEndPoint.Address.ToString());
                if (json["announce"]?.GetValue<bool>() == true && dev != null) _ = Reply(dev);
            }
            catch { }
        }
    }

    /// <summary>Answer an announcement: POST /register to the device, else a multicast reply.</summary>
    private async Task Reply(LocalSendDevice d)
    {
        try
        {
            var res = await Http.PostAsync(Url(d, "register"), new StringContent(Info(true).ToJsonString(), Encoding.UTF8, "application/json"));
            if (res.IsSuccessStatusCode) return;
        }
        catch { }
        try
        {
            var msg = Info(true);
            msg["announce"] = false;
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await _udp.SendAsync(bytes, bytes.Length, new IPEndPoint(Group, Port));
        }
        catch { }
    }

    private LocalSendDevice Seen(JsonNode json, string ip)
    {
        var fp = json["fingerprint"]?.GetValue<string>();
        if (string.IsNullOrEmpty(fp) || fp == _fingerprint) return null;
        LocalSendDevice dev = null;
        // on the UI thread and waited for: the caller replies to exactly this device
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            dev = Devices.FirstOrDefault(d => d.Fingerprint == fp);
            bool isNew = dev == null;
            dev ??= new LocalSendDevice { Fingerprint = fp };
            dev.Alias = json["alias"]?.GetValue<string>() ?? dev.Alias;
            dev.Ip = ip;
            if (json["port"] is JsonValue pv && pv.TryGetValue<int>(out var port)) dev.Port = port;
            if (json["protocol"]?.GetValue<string>() is { } proto) dev.Protocol = proto;
            dev.DeviceType = json["deviceType"]?.GetValue<string>() ?? dev.DeviceType;
            dev.DeviceModel = json["deviceModel"]?.GetValue<string>();
            dev.LastSeen = DateTime.Now;
            dev.Touch();
            if (isNew) Devices.Add(dev);
        });
        return dev;
    }

    private static string Url(LocalSendDevice d, string path) => $"{d.Protocol}://{d.Ip}:{d.Port}{Api}{path}";

    // ---------- sending ----------

    public async Task<bool> SendAsync(LocalSendDevice d, IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0 || Moving) return false;
        Progress = 0;
        Status = L.T($"Warte auf {d.Alias} …");
        Log($"send {files.Count} file(s) to '{d.Alias}' at {d.Protocol}://{d.Ip}:{d.Port}, last seen {(DateTime.Now - d.LastSeen).TotalSeconds:0} s ago");
        try
        {
            // not heard of for a while: ask once more who is there (an Android phone only listens while LocalSend is open)
            if (DateTime.Now - d.LastSeen > TimeSpan.FromSeconds(45)) { await Announce(); await Task.Delay(1500); }
            var map = new JsonObject();
            var ids = new Dictionary<string, string>();
            foreach (var f in files)
            {
                var id = Guid.NewGuid().ToString("N");
                ids[id] = f;
                var fi = new FileInfo(f);
                map[id] = new JsonObject
                {
                    ["id"] = id,
                    ["fileName"] = fi.Name,
                    ["size"] = fi.Length,
                    ["fileType"] = Mime(fi.Extension),
                    ["metadata"] = new JsonObject { ["modified"] = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                };
            }
            var body = new JsonObject { ["info"] = Info(true), ["files"] = map };
            // the other side asks its user first – give them time
            using var prepCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var res = await Upload.PostAsync(Url(d, "prepare-upload"), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), prepCts.Token);
            switch ((int)res.StatusCode)
            {
                case 204: Done(L.T("Schon da")); return true;
                case 401: Fail(L.T("Dieses Gerät verlangt eine PIN – schalte sie in LocalSend aus.")); return false;
                case 403: Fail(L.T($"{d.Alias} hat abgelehnt")); return false;
                case 409: Fail(L.T($"{d.Alias} empfängt gerade etwas anderes")); return false;
                case 429: Fail(L.T("Zu viele Anfragen – gleich noch einmal")); return false;
            }
            if (!res.IsSuccessStatusCode) { Fail(L.T($"{d.Alias} hat nicht angenommen")); return false; }
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            var session = json?["sessionId"]?.GetValue<string>();
            var tokens = json?["files"] as JsonObject;
            if (session == null || tokens == null) { Fail(L.T("Unerwartete Antwort")); return false; }

            long total = files.Sum(f => new FileInfo(f).Length), sent = 0;
            Status = L.T($"Sende an {d.Alias} …");
            foreach (var (id, path) in ids)
            {
                var token = tokens[id]?.GetValue<string>();
                if (token == null) continue; // the receiver did not want this one
                await using var fs = File.OpenRead(path);
                var content = new ProgressContent(fs, n => { sent += n; Ui.Run(() => Progress = total > 0 ? (double)sent / total : 1); });
                var up = await Upload.PostAsync($"{Url(d, "upload")}?sessionId={Uri.EscapeDataString(session)}&fileId={id}&token={Uri.EscapeDataString(token)}", content);
                if (!up.IsSuccessStatusCode) { Fail(L.T($"Übertragung an {d.Alias} abgebrochen")); return false; }
            }
            Done(files.Count == 1 ? L.T($"An {d.Alias} gesendet") : L.T($"{files.Count} Dateien an {d.Alias} gesendet"));
            return true;
        }
        catch (TaskCanceledException) { Log($"send to {d.Ip}:{d.Port} ({d.Protocol}): timeout"); Fail(L.T($"{d.Alias} hat nicht geantwortet")); return false; }
        catch (Exception ex)
        {
            // say why (user report 2026-11-17: "nicht erreichbar" alone did not help)
            var root = ex;
            while (root.InnerException != null) root = root.InnerException;
            Log($"send to {d.Ip}:{d.Port} ({d.Protocol}): {ex.GetType().Name}: {ex.Message} | {root.GetType().Name}: {root.Message}");
            string why = root switch
            {
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } => L.T("LocalSend auf dem Gerät ist zu – öffne die App und lass sie im Vordergrund"),
                SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable } => L.T("Gerät nicht im selben Netzwerk erreichbar"),
                System.Security.Authentication.AuthenticationException => L.T("Verschlüsselung mit dem Gerät klappte nicht"),
                _ => root.Message,
            };
            Fail($"{d.Alias}: {why}");
            return false;
        }
    }

    /// <summary>What sending did – %AppData%\PeakPill\localsend-log.txt, last 200 lines (no file contents).</summary>
    private static void Log(string what)
    {
        try
        {
            var f = Storage.PathOf("localsend-log.txt");
            var lines = File.Exists(f) ? File.ReadAllLines(f).TakeLast(199).ToList() : new List<string>();
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}");
            File.WriteAllLines(f, lines);
        }
        catch { }
    }

    private void Done(string msg) => Ui.Run(() => { Progress = -1; Status = msg; Notice?.Invoke(msg, true); });
    private void Fail(string msg) => Ui.Run(() => { Progress = -1; Status = msg; Notice?.Invoke(msg, false); });

    /// <summary>Counts what goes over the wire.</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly Stream _s;
        private readonly Action<long> _step;
        public ProgressContent(Stream s, Action<long> step) { _s = s; _step = step; Headers.ContentType = new("application/octet-stream"); }
        protected override async Task SerializeToStreamAsync(Stream target, TransportContext context)
        {
            var buf = new byte[81920];
            int n;
            while ((n = await _s.ReadAsync(buf)) > 0) { await target.WriteAsync(buf.AsMemory(0, n)); _step(n); }
        }
        protected override bool TryComputeLength(out long length) { length = _s.Length; return true; }
    }

    private static string Mime(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".heic" => "image/heic",
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg", ".wav" => "audio/wav",
        ".pdf" => "application/pdf", ".zip" => "application/zip", ".txt" => "text/plain", ".apk" => "application/vnd.android.package-archive",
        _ => "application/octet-stream",
    };

    // ---------- receiving: a small HTTP server ----------

    private sealed class Session
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Ip;
        public string From;
        public Dictionary<string, (string Token, string Name, long Size)> Files = new();
        public List<string> Saved = new();
        public long Total, Got;
        public DateTime Started = DateTime.Now;
    }

    private Session _session;

    private void StartServer()
    {
        try
        {
            _server = new TcpListener(IPAddress.Any, Port);
            _server.Start();
            ServerUp = true;
            _ = AcceptLoop(_cts.Token);
        }
        catch (SocketException)
        {
            // the LocalSend app itself (or something else) has the port: sending still works
            ServerUp = false;
            Status = L.T("Empfangen aus – Port 53317 ist belegt (läuft LocalSend schon?)");
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _server.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Handle(c), ct);
        }
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = c.SendTimeout = 120000;
                var s = c.GetStream();
                var (method, target, headers, leftover) = await ReadHead(s);
                if (method == null) return;
                long length = headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, out var l) ? l : 0;
                var ip = ((IPEndPoint)c.Client.RemoteEndPoint).Address.MapToIPv4().ToString();
                var path = target.Split('?')[0];
                var query = ParseQuery(target);

                if (path.EndsWith("/upload") && method == "POST") { await HandleUpload(s, leftover, length, query, ip); return; }

                var body = await ReadBody(s, leftover, length);
                switch (path)
                {
                    case Api + "register" when method == "POST":
                        if (JsonNode.Parse(body) is { } reg) Seen(reg, ip);
                        await Respond(s, 200, Info(false).ToJsonString());
                        break;
                    case Api + "info":
                        await Respond(s, 200, Info(false).ToJsonString());
                        break;
                    case Api + "prepare-upload" when method == "POST":
                        await HandlePrepare(s, body, ip);
                        break;
                    case Api + "cancel" when method == "POST":
                        if (_session != null && query.TryGetValue("sessionId", out var sid) && sid == _session.Id) EndSession(false);
                        await Respond(s, 200, null);
                        break;
                    default:
                        await Respond(s, 404, null);
                        break;
                }
            }
            catch { }
        }
    }

    private async Task HandlePrepare(NetworkStream s, string body, string ip)
    {
        var json = JsonNode.Parse(body);
        if (json?["files"] is not JsonObject files || files.Count == 0) { await Respond(s, 400, null); return; }
        if (_session != null && DateTime.Now - _session.Started < TimeSpan.FromMinutes(10)) { await Respond(s, 409, null); return; }
        var info = json["info"];
        if (info != null) Seen(info, ip);
        var from = info?["alias"]?.GetValue<string>() ?? ip;
        var list = files.Select(f => (Name: Path.GetFileName(f.Value?["fileName"]?.GetValue<string>() ?? "file"), Size: f.Value?["size"]?.GetValue<long>() ?? 0)).ToList();

        // nothing comes in without a yes in the island
        bool yes = false;
        var ask = Ask;
        if (ask != null)
        {
            var t = ask(new LocalSendRequest(from, info?["deviceType"]?.GetValue<string>() ?? "mobile", list));
            var done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(60)));
            yes = done == t && t.Result;
        }
        if (!yes) { await Respond(s, 403, null); return; }

        var session = new Session { Ip = ip, From = from };
        var tokens = new JsonObject();
        foreach (var (id, f) in files)
        {
            var token = Guid.NewGuid().ToString("N");
            var name = Path.GetFileName(f?["fileName"]?.GetValue<string>() ?? "file");
            long size = f?["size"]?.GetValue<long>() ?? 0;
            session.Files[id] = (token, name, size);
            session.Total += size;
            tokens[id] = token;
        }
        _session = session;
        Ui.Run(() => { Progress = 0; Status = L.T($"Empfange von {from} …"); });
        await Respond(s, 200, new JsonObject { ["sessionId"] = session.Id, ["files"] = tokens }.ToJsonString());
    }

    private async Task HandleUpload(NetworkStream s, byte[] leftover, long length, Dictionary<string, string> q, string ip)
    {
        var session = _session;
        if (session == null || !q.TryGetValue("sessionId", out var sid) || sid != session.Id
            || !q.TryGetValue("fileId", out var fid) || !session.Files.TryGetValue(fid, out var f)
            || !q.TryGetValue("token", out var token) || token != f.Token || ip != session.Ip)
        {
            await Respond(s, 403, null);
            return;
        }
        Directory.CreateDirectory(SaveDir);
        var target = Unique(Path.Combine(SaveDir, f.Name));
        try
        {
            await using (var fs = File.Create(target))
            {
                long got = 0;
                if (leftover.Length > 0) { await fs.WriteAsync(leftover); got += leftover.Length; Step(session, leftover.Length); }
                var buf = new byte[81920];
                while (got < length)
                {
                    int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, length - got)));
                    if (n <= 0) break;
                    await fs.WriteAsync(buf.AsMemory(0, n));
                    got += n;
                    Step(session, n);
                }
                if (got < length) throw new IOException("cut off");
            }
            lock (session) session.Saved.Add(target);
            await Respond(s, 200, null);
            bool all;
            lock (session) all = session.Saved.Count >= session.Files.Count;
            if (all) EndSession(true);
        }
        catch
        {
            try { File.Delete(target); } catch { }
            await Respond(s, 500, null);
        }
    }

    private void Step(Session session, long n)
    {
        session.Got += n;
        if (session.Total > 0) Ui.Run(() => Progress = Math.Min(1, (double)session.Got / session.Total));
    }

    private void EndSession(bool complete)
    {
        var session = _session;
        _session = null;
        if (session == null) return;
        Ui.Run(() =>
        {
            Progress = -1;
            if (complete && session.Saved.Count > 0)
            {
                Status = session.Saved.Count == 1 ? L.T($"Von {session.From} empfangen") : L.T($"{session.Saved.Count} Dateien von {session.From} empfangen");
                Received?.Invoke(session.Saved.ToList());
            }
            else Status = L.T($"{session.From} hat abgebrochen");
        });
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    // ---------- tiny HTTP ----------

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers, byte[] Leftover)> ReadHead(NetworkStream s)
    {
        var buf = new byte[16384];
        int len = 0;
        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len));
            if (n <= 0) return (null, null, null, null);
            len += n;
            int end = IndexOf(buf, len);
            if (end < 0) continue;
            var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = head[0].Split(' ');
            if (first.Length < 2) return (null, null, null, null);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in head.Skip(1))
            {
                int c = h.IndexOf(':');
                if (c > 0) headers[h[..c].Trim()] = h[(c + 1)..].Trim();
            }
            return (first[0].ToUpperInvariant(), first[1], headers, buf[(end + 4)..len]);
        }
        return (null, null, null, null);
    }

    private static int IndexOf(byte[] b, int len)
    {
        for (int i = 3; i < len; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i - 3;
        return -1;
    }

    private static async Task<string> ReadBody(NetworkStream s, byte[] leftover, long length)
    {
        if (length <= 0) return "";
        if (length > 4 * 1024 * 1024) throw new IOException("too big");
        var buf = new byte[length];
        int got = Math.Min(leftover.Length, (int)length);
        Array.Copy(leftover, buf, got);
        while (got < length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got, (int)length - got));
            if (n <= 0) break;
            got += n;
        }
        return Encoding.UTF8.GetString(buf, 0, got);
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var d = new Dictionary<string, string>();
        int q = target.IndexOf('?');
        if (q < 0) return d;
        foreach (var part in target[(q + 1)..].Split('&'))
        {
            int e = part.IndexOf('=');
            if (e > 0) d[Uri.UnescapeDataString(part[..e])] = Uri.UnescapeDataString(part[(e + 1)..]);
        }
        return d;
    }

    private static async Task Respond(NetworkStream s, int code, string json)
    {
        var body = json == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        var reason = code switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (body.Length > 0) await s.WriteAsync(body);
        await s.FlushAsync();
    }
}
