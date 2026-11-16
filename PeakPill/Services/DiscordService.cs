using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>Someone in the Discord call (avatar, speaking ring, muted).</summary>
public sealed class DiscordMember : ObservableObject
{
    private bool _speaking, _muted, _deaf;
    public string Id { get; init; }
    public string Name { get; set; }
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public System.Windows.Media.ImageSource Avatar { get; set; }
    public bool HasAvatar => Avatar != null;
    public bool Speaking { get => _speaking; set => Set(ref _speaking, value); }
    public bool Muted { get => _muted; set => Set(ref _muted, value); }
    public bool Deafened { get => _deaf; set => Set(ref _deaf, value); }
}

/// <summary>
/// Discord's local RPC (named pipe discord-ipc-N): with the user's consent it reads and sets the user's OWN mute /
/// deafen state in Discord and tells which voice channel they are in. Needs a Discord application of the user
/// (client id + secret from discord.com/developers) – its owner may use RPC without Discord's approval.
/// Secrets and tokens are stored encrypted for this Windows user only (DPAPI).
/// </summary>
public sealed class DiscordService : ObservableObject
{
    private static readonly string[] Scopes = { "rpc", "rpc.voice.read", "rpc.voice.write" };
    private const string RedirectUri = "http://127.0.0.1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    private CancellationTokenSource _cts;
    private bool _connected, _muted, _deafened, _inVoice;
    private string _status = "Nicht verbunden", _channel;

    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    /// <summary>Muted in Discord (self-mute) – not the Windows microphone.</summary>
    public bool Muted { get => _muted; private set => Set(ref _muted, value); }
    public bool Deafened { get => _deafened; private set => Set(ref _deafened, value); }
    public bool InVoice { get => _inVoice; private set => Set(ref _inVoice, value); }
    public string Channel { get => _channel; private set => Set(ref _channel, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>Who is in the call with you (avatars, speaking).</summary>
    public System.Collections.ObjectModel.ObservableCollection<DiscordMember> Members { get; } = new();
    private string _channelId;

    public static bool Configured => !string.IsNullOrEmpty(App.Settings.DiscordClientId) && !string.IsNullOrEmpty(App.Settings.DiscordSecret);

    public void Start()
    {
        if (!Configured) return;
        _ = Task.Run(RunAsync);
    }

    /// <summary>Called from the settings after new credentials were entered.</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        Ui.Run(() => { Connected = false; Status = "Nicht verbunden"; });
    }

    /// <summary>Keeps a connection: reconnects every 10 s while Discord is closed.</summary>
    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnce(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Ui.Run(() => { Connected = false; Status = ex is UnauthorizedAccessException ? ex.Message : "Discord nicht erreichbar"; });
                if (ex is UnauthorizedAccessException) return; // wrong credentials – wait for new ones
            }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            await Task.Delay(10000, ct).ContinueWith(_ => { });
        }
    }

    private async Task ConnectOnce(CancellationToken ct)
    {
        // Discord listens on the first free discord-ipc-0…9
        for (int i = 0; i < 10 && _pipe == null; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await p.ConnectAsync(400, ct); _pipe = p; }
            catch { p.Dispose(); }
        }
        if (_pipe == null) throw new IOException("Discord läuft nicht");

        await SendFrame(0, new JsonObject { ["v"] = 1, ["client_id"] = App.Settings.DiscordClientId });
        var reader = Task.Run(() => ReadLoop(ct), ct);
        await Task.Delay(300, ct);

        // stored token → AUTHENTICATE; otherwise (or when expired) AUTHORIZE: Discord shows its own consent popup
        var token = LoadToken();
        if (token == null || !await Authenticate(token.Value.Access))
        {
            string access = null;
            if (token?.Refresh is { } rt) access = await Exchange("refresh_token", rt);
            if (access == null || !await Authenticate(access))
            {
                Ui.Run(() => Status = "Bitte in Discord bestätigen …");
                var res = await Command("AUTHORIZE", new JsonObject { ["client_id"] = App.Settings.DiscordClientId, ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s).ToArray()) }, TimeSpan.FromMinutes(2));
                var code = res?["data"]?["code"]?.GetValue<string>();
                if (code == null) throw new UnauthorizedAccessException("Discord hat die Verbindung abgelehnt");
                access = await Exchange("authorization_code", code) ?? throw new UnauthorizedAccessException("Client-ID oder Secret falsch");
                if (!await Authenticate(access)) throw new UnauthorizedAccessException("Anmeldung bei Discord fehlgeschlagen");
            }
        }

        Ui.Run(() => { Connected = true; Status = "Verbunden"; });
        await Subscribe("VOICE_SETTINGS_UPDATE");
        await Subscribe("VOICE_CHANNEL_SELECT");
        await RefreshVoice();
        await reader; // until the pipe closes
        Ui.Run(() => { Connected = false; InVoice = false; Status = "Discord geschlossen"; });
    }

    private async Task<bool> Authenticate(string access)
    {
        var r = await Command("AUTHENTICATE", new JsonObject { ["access_token"] = access }, TimeSpan.FromSeconds(10));
        return r?["evt"]?.GetValue<string>() != "ERROR" && r?["data"]?["user"] != null;
    }

    /// <summary>OAuth code / refresh token → access token (stored encrypted).</summary>
    private async Task<string> Exchange(string grant, string value)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = App.Settings.DiscordClientId,
            ["client_secret"] = Unprotect(App.Settings.DiscordSecret),
            ["grant_type"] = grant,
            [grant == "refresh_token" ? "refresh_token" : "code"] = value,
            ["redirect_uri"] = RedirectUri
        };
        using var resp = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
        if (!resp.IsSuccessStatusCode) return null;
        var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        var access = j?["access_token"]?.GetValue<string>();
        var refresh = j?["refresh_token"]?.GetValue<string>();
        if (access != null) SaveToken(access, refresh);
        return access;
    }

    private async Task Subscribe(string evt) => await Command("SUBSCRIBE", null, TimeSpan.FromSeconds(5), evt);

    private async Task RefreshVoice()
    {
        var vs = await Command("GET_VOICE_SETTINGS", new JsonObject(), TimeSpan.FromSeconds(5));
        ApplyVoiceSettings(vs?["data"]);
        var ch = await Command("GET_SELECTED_VOICE_CHANNEL", new JsonObject(), TimeSpan.FromSeconds(5));
        ApplyChannel(ch?["data"]);
    }

    private void ApplyVoiceSettings(JsonNode d)
    {
        if (d == null) return;
        bool mute = d["mute"]?.GetValue<bool>() ?? false, deaf = d["deaf"]?.GetValue<bool>() ?? false;
        Ui.Run(() => { Muted = mute || deaf; Deafened = deaf; });
    }

    private void ApplyChannel(JsonNode d)
    {
        bool inVoice = d != null && d.GetValueKind() == JsonValueKind.Object && d["id"] != null;
        var name = inVoice ? d["name"]?.GetValue<string>() : null;
        var id = inVoice ? d["id"]?.GetValue<string>() : null;
        var states = inVoice && d["voice_states"] is JsonArray arr ? arr : new JsonArray();
        var members = new List<(string Id, string Name, string Avatar, bool Muted, bool Deaf)>();
        foreach (var s in states)
        {
            var u = s?["user"];
            var uid = u?["id"]?.GetValue<string>();
            if (uid == null) continue;
            var nick = s["nick"]?.GetValue<string>() ?? u["global_name"]?.GetValue<string>() ?? u["username"]?.GetValue<string>() ?? "?";
            var av = u["avatar"]?.GetValue<string>();
            var vs = s["voice_state"];
            bool muted = (vs?["self_mute"]?.GetValue<bool>() ?? false) || (vs?["mute"]?.GetValue<bool>() ?? false);
            bool deaf = (vs?["self_deaf"]?.GetValue<bool>() ?? false) || (vs?["deaf"]?.GetValue<bool>() ?? false);
            members.Add((uid, nick, av != null ? $"https://cdn.discordapp.com/avatars/{uid}/{av}.png?size=64" : null, muted, deaf));
        }
        Ui.Run(() =>
        {
            InVoice = inVoice;
            Channel = name;
            foreach (var gone in Members.Where(m => members.All(x => x.Id != m.Id)).ToList()) Members.Remove(gone);
            foreach (var (mid, mname, avatar, muted, deaf) in members)
            {
                var m = Members.FirstOrDefault(x => x.Id == mid);
                if (m == null)
                {
                    m = new DiscordMember { Id = mid, Name = mname };
                    if (avatar != null)
                    {
                        try
                        {
                            var bi = new System.Windows.Media.Imaging.BitmapImage();
                            bi.BeginInit();
                            bi.UriSource = new Uri(avatar);
                            bi.DecodePixelWidth = 48;
                            bi.EndInit();
                            m.Avatar = bi;
                        }
                        catch { }
                    }
                    Members.Add(m);
                }
                m.Name = mname;
                m.Muted = muted;
                m.Deafened = deaf;
            }
        });
        if (id != _channelId)
        {
            var old = _channelId;
            _channelId = id;
            _ = Resubscribe(old, id);
        }
    }

    /// <summary>Speaking and join/leave events are per channel – move the subscriptions along.</summary>
    private async Task Resubscribe(string oldId, string newId)
    {
        var evts = new[] { "SPEAKING_START", "SPEAKING_STOP", "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE" };
        if (oldId != null)
            foreach (var e in evts) await Command("UNSUBSCRIBE", new JsonObject { ["channel_id"] = oldId }, TimeSpan.FromSeconds(3), e);
        if (newId != null)
            foreach (var e in evts) await Command("SUBSCRIBE", new JsonObject { ["channel_id"] = newId }, TimeSpan.FromSeconds(3), e);
    }

    private void OnSpeaking(JsonNode d, bool on)
    {
        var uid = d?["user_id"]?.GetValue<string>();
        if (uid == null) return;
        Ui.Run(() => { if (Members.FirstOrDefault(m => m.Id == uid) is { } m) m.Speaking = on; });
    }

    /// <summary>Deafen yourself in Discord (you hear nobody and are muted).</summary>
    public async Task<bool> SetDeaf(bool deaf)
    {
        if (!Connected) return false;
        var r = await Command("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = deaf }, TimeSpan.FromSeconds(5));
        ApplyVoiceSettings(r?["data"]);
        return r?["evt"]?.GetValue<string>() != "ERROR";
    }

    public Task<bool> ToggleDeaf() => SetDeaf(!Deafened);

    // ---------- actions ----------

    /// <summary>Mutes / unmutes yourself in Discord (others can't hear you) – the Windows microphone stays as it is.</summary>
    public async Task<bool> SetMute(bool mute)
    {
        if (!Connected) return false;
        var r = await Command("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = mute }, TimeSpan.FromSeconds(5));
        ApplyVoiceSettings(r?["data"]);
        return r?["evt"]?.GetValue<string>() != "ERROR";
    }

    public Task<bool> ToggleMute() => SetMute(!Muted);

    // ---------- pipe protocol: [int32 op][int32 length][json] ----------

    private async Task<JsonNode> Command(string cmd, JsonObject args, TimeSpan timeout, string evt = null)
    {
        var nonce = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[nonce] = tcs;
        var msg = new JsonObject { ["cmd"] = cmd, ["nonce"] = nonce, ["args"] = args ?? new JsonObject() };
        if (evt != null) msg["evt"] = evt;
        await SendFrame(1, msg);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        lock (_pending) _pending.Remove(nonce);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    private async Task SendFrame(int op, JsonNode payload)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        await _write.WaitAsync();
        try { await _pipe.WriteAsync(frame); await _pipe.FlushAsync(); }
        finally { _write.Release(); }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var head = new byte[8];
        while (!ct.IsCancellationRequested && _pipe is { IsConnected: true })
        {
            if (!await ReadExactly(head, ct)) return;
            int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
            if (len < 0 || len > 4 * 1024 * 1024) return;
            var body = new byte[len];
            if (!await ReadExactly(body, ct)) return;
            if (op == 2) return; // CLOSE
            JsonNode j;
            try { j = JsonNode.Parse(body); } catch { continue; }
            var nonce = j?["nonce"]?.GetValue<string>();
            if (nonce != null)
            {
                TaskCompletionSource<JsonNode> tcs;
                lock (_pending) _pending.TryGetValue(nonce, out tcs);
                tcs?.TrySetResult(j);
                continue;
            }
            switch (j?["evt"]?.GetValue<string>())
            {
                case "VOICE_SETTINGS_UPDATE": ApplyVoiceSettings(j["data"]); break;
                case "VOICE_CHANNEL_SELECT":
                case "VOICE_STATE_CREATE":
                case "VOICE_STATE_UPDATE":
                case "VOICE_STATE_DELETE": _ = RefreshVoice(); break;
                case "SPEAKING_START": OnSpeaking(j["data"], true); break;
                case "SPEAKING_STOP": OnSpeaking(j["data"], false); break;
            }
        }
    }

    private async Task<bool> ReadExactly(byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await _pipe.ReadAsync(buf.AsMemory(got), ct);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    // ---------- encrypted storage (only this Windows user can read it) ----------

    public static string Protect(string plain) =>
        string.IsNullOrEmpty(plain) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    private static (string Access, string Refresh)? LoadToken()
    {
        var path = Storage.PathOf("discord_token.bin");
        if (!File.Exists(path)) return null;
        var parts = Unprotect(File.ReadAllText(path)).Split('\n');
        return parts.Length == 2 && parts[0].Length > 0 ? (parts[0], parts[1]) : null;
    }

    private static void SaveToken(string access, string refresh) =>
        File.WriteAllText(Storage.PathOf("discord_token.bin"), Protect(access + "\n" + (refresh ?? "")));

    /// <summary>Forget the connection (new credentials or "Trennen").</summary>
    public static void ForgetToken()
    {
        try { File.Delete(Storage.PathOf("discord_token.bin")); } catch { }
    }
}
