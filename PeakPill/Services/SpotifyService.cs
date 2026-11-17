using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeakPill.Core;

namespace PeakPill.Services;

public sealed class QueueItem
{
    public string Name { get; init; }
    public string Artist { get; init; }
    public string Duration { get; init; }
    public ImageSource Cover { get; init; }
    /// <summary>spotify:track:… – for adding it to the queue.</summary>
    public string Uri { get; init; }
    public string CoverUrl { get; init; }
}

/// <summary>
/// Spotify Web API with the user's own Spotify app (client id only – PKCE sign-in in the browser, no secret):
/// reads the playback queue for the music page. Tokens are stored encrypted for this Windows user.
/// </summary>
public sealed class SpotifyService : ObservableObject
{
    public const int Port = 47824;
    public static string RedirectUri => $"http://127.0.0.1:{Port}/callback";
    // 2026-11-16: + adding to the queue and liking songs (older connections are asked to connect once more)
    private const string Scopes = "user-read-playback-state user-read-currently-playing user-modify-playback-state user-library-read user-library-modify";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string _access, _refresh, _status = L.T("Nicht verbunden");
    private DateTime _expires;
    private bool _connected, _busy;

    public ObservableCollection<QueueItem> Queue { get; } = new();
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public static bool Configured => !string.IsNullOrEmpty(App.Settings.SpotifyClientId);

    public void Start()
    {
        LoadIslandQueue();
        var t = LoadToken();
        if (t != null && Configured)
        {
            (_access, _refresh, _expires) = t.Value;
            Connected = true;
            Status = L.T("Verbunden");
        }
    }

    // ---------- sign-in (PKCE): browser → Spotify → back to 127.0.0.1:47824 ----------

    public async Task<bool> SignInAsync()
    {
        if (!Configured) { Status = L.T("Client-ID fehlt"); return false; }
        var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(12));
        var url = "https://accounts.spotify.com/authorize?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(App.Settings.SpotifyClientId)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&code_challenge_method=S256&code_challenge={challenge}&state={state}";

        var listener = new TcpListener(IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch { Status = L.T($"Port {Port} ist belegt"); return false; }
        try
        {
            Status = L.T("Bitte im Browser bei Spotify erlauben …");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            var accept = listener.AcceptTcpClientAsync();
            if (await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMinutes(3))) != accept) { Status = L.T("Abgebrochen (keine Antwort)"); return false; }
            using var client = accept.Result;
            var stream = client.GetStream();
            var buf = new byte[8192];
            int n = await stream.ReadAsync(buf);
            var firstLine = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0]; // GET /callback?code=…&state=… HTTP/1.1
            var query = firstLine.Split(' ').ElementAtOrDefault(1) ?? "";
            var qs = System.Web.HttpUtility.ParseQueryString(query.Contains('?') ? query[(query.IndexOf('?') + 1)..] : "");
            bool ok = qs["state"] == state && !string.IsNullOrEmpty(qs["code"]);
            var html = ok ? "<h2 style='font-family:sans-serif'>PeakPill ist mit Spotify verbunden – du kannst dieses Fenster schließen.</h2>"
                          : "<h2 style='font-family:sans-serif'>Verbindung abgebrochen.</h2>";
            var body = Encoding.UTF8.GetBytes(html);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
            if (!ok) { Status = L.T("Spotify hat nicht erlaubt"); return false; }

            var tok = await Token(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = qs["code"], ["redirect_uri"] = RedirectUri,
                ["client_id"] = App.Settings.SpotifyClientId, ["code_verifier"] = verifier
            });
            if (!tok) { Status = L.T("Anmeldung fehlgeschlagen – stimmt die Client-ID / Redirect-URI?"); return false; }
            Connected = true;
            Status = L.T("Verbunden");
            await RefreshQueueAsync();
            return true;
        }
        catch (Exception ex) { App.Log(ex); Status = "Fehler bei der Anmeldung"; return false; }
        finally { listener.Stop(); }
    }

    private async Task<bool> Token(Dictionary<string, string> form)
    {
        using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        if (!resp.IsSuccessStatusCode) return false;
        var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        _access = j?["access_token"]?.GetValue<string>();
        _refresh = j?["refresh_token"]?.GetValue<string>() ?? _refresh;
        _scope = j?["scope"]?.GetValue<string>() ?? _scope;
        Raise(nameof(NeedsReconnect));
        _expires = DateTime.Now.AddSeconds((j?["expires_in"]?.GetValue<int>() ?? 3600) - 60);
        if (_access == null) return false;
        SaveToken();
        return true;
    }

    private async Task<bool> EnsureToken()
    {
        if (_access != null && DateTime.Now < _expires) return true;
        if (_refresh == null) return false;
        bool ok = await Token(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = _refresh, ["client_id"] = App.Settings.SpotifyClientId });
        if (!ok) { Connected = false; Status = L.T("Bitte neu verbinden"); }
        return ok;
    }

    // ---------- queue ----------

    /// <summary>
    /// Loads the queue. <paramref name="playingTitle"/>: the song Windows says is playing – right after a skip
    /// Spotify's API still reports the old song for a moment, so it is asked again until both agree.
    /// </summary>
    public async Task RefreshQueueAsync(string playingTitle = null)
    {
        if (!Connected) return;
        if (_busy) { _again = playingTitle ?? _again ?? ""; return; } // a newer request comes after the running one
        _busy = true;
        try
        {
            if (!await EnsureToken()) return;
            JsonNode j = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/queue");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
                using var resp = await Http.SendAsync(req);
                if (resp.StatusCode == HttpStatusCode.Unauthorized) { _expires = DateTime.MinValue; return; }
                if (!resp.IsSuccessStatusCode) return;
                j = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                var now = j?["currently_playing"]?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(playingTitle) || Same(now, playingTitle)) break;
                await Task.Delay(600); // Spotify not caught up yet
            }
            var items = new List<QueueItem>();
            foreach (var t in (j?["queue"] as JsonArray ?? new JsonArray()).Take(12))
                if (t != null) items.Add(Item(t));
            Queue.Clear();
            foreach (var i in items) Queue.Add(i);
            Raise(nameof(Queue));
            // the song playing now: for the heart
            var curId = j?["currently_playing"]?["id"]?.GetValue<string>();
            if (curId != CurrentId) { CurrentId = curId; _ = RefreshLikedAsync(); }
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _busy = false; }
        if (_again != null) { var t = _again; _again = null; await RefreshQueueAsync(t.Length > 0 ? t : null); }
    }

    private string _again;

    private static QueueItem Item(JsonNode t)
    {
        var artists = t["artists"] is JsonArray a ? string.Join(", ", a.Select(x => x?["name"]?.GetValue<string>()).Where(x => x != null))
                    : t["show"]?["name"]?.GetValue<string>() ?? "";
        var images = (t["album"]?["images"] ?? t["images"]) as JsonArray;
        var img = images?.OrderBy(i => i?["width"]?.GetValue<int>() ?? 999).FirstOrDefault()?["url"]?.GetValue<string>();
        var ms = t["duration_ms"]?.GetValue<double>() ?? 0;
        return new QueueItem
        {
            Name = t["name"]?.GetValue<string>() ?? "", Artist = artists, Duration = TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss"),
            Cover = Picture(img), CoverUrl = img, Uri = t["uri"]?.GetValue<string>()
        };
    }

    private static ImageSource Picture(string url)
    {
        if (url == null) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(url);
            bi.DecodePixelWidth = 52;
            bi.EndInit();
            return bi;
        }
        catch { return null; }
    }

    // ---------- 2026-11-16: like, search, add to the queue, the island's own queue ----------

    private string _scope = "";
    private string _currentId;
    private bool _liked;

    /// <summary>Connected before liking / adding existed: one more "Verbinden" gives the new rights.</summary>
    public bool NeedsReconnect => Connected && !_scope.Contains("user-library-modify");
    public string CurrentId { get => _currentId; private set => Set(ref _currentId, value); }
    public bool Liked { get => _liked; private set => Set(ref _liked, value); }

    /// <summary>The island's own queue (sorted freely); it hands Spotify the next song shortly before a song ends.</summary>
    public ObservableCollection<QueueItem> IslandQueue { get; } = new();

    private async Task<HttpResponseMessage> Call(HttpMethod method, string url)
    {
        if (!Connected || !await EnsureToken()) return null;
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        if (method == HttpMethod.Put || method == HttpMethod.Post) req.Content = new StringContent("");
        try { return await Http.SendAsync(req); } catch { return null; }
    }

    /// <summary>Asks Spotify for the song playing now and whether it is in the user's library.</summary>
    public async Task RefreshLikedAsync()
    {
        if (CurrentId == null)
        {
            using var cur = await Call(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
            if (cur is { IsSuccessStatusCode: true } && cur.StatusCode != HttpStatusCode.NoContent)
                CurrentId = JsonNode.Parse(await cur.Content.ReadAsStringAsync())?["item"]?["id"]?.GetValue<string>();
        }
        if (CurrentId == null) { Liked = false; return; }
        using var r = await Call(HttpMethod.Get, $"https://api.spotify.com/v1/me/tracks/contains?ids={CurrentId}");
        if (r is { IsSuccessStatusCode: true } && JsonNode.Parse(await r.Content.ReadAsStringAsync()) is JsonArray a && a.Count > 0)
            Liked = a[0]?.GetValue<bool>() == true;
    }

    /// <summary>A new song plays: forget the old id and ask again (the heart follows the song).</summary>
    public Task TrackChangedAsync()
    {
        CurrentId = null;
        Liked = false;
        return RefreshLikedAsync();
    }

    /// <summary>The heart: adds the song to (or removes it from) "Lieblingssongs". false = Spotify said no.</summary>
    public async Task<bool> ToggleLikeAsync()
    {
        if (CurrentId == null) await RefreshLikedAsync();
        if (CurrentId == null) return false;
        bool want = !Liked;
        using var r = await Call(want ? HttpMethod.Put : HttpMethod.Delete, $"https://api.spotify.com/v1/me/tracks?ids={CurrentId}");
        if (r is not { IsSuccessStatusCode: true }) return false;
        Liked = want;
        return true;
    }

    public async Task<List<QueueItem>> SearchAsync(string q)
    {
        var list = new List<QueueItem>();
        if (string.IsNullOrWhiteSpace(q)) return list;
        using var r = await Call(HttpMethod.Get, $"https://api.spotify.com/v1/search?type=track&limit=5&q={Uri.EscapeDataString(q.Trim())}");
        if (r is not { IsSuccessStatusCode: true }) return list;
        foreach (var t in JsonNode.Parse(await r.Content.ReadAsStringAsync())?["tracks"]?["items"] as JsonArray ?? new JsonArray())
            if (t != null) list.Add(Item(t));
        return list;
    }

    /// <summary>Spotify's own queue: appended at the end (Spotify lets apps only add, not sort).</summary>
    public async Task<bool> AddToQueueAsync(QueueItem item)
    {
        if (item?.Uri == null) return false;
        using var r = await Call(HttpMethod.Post, $"https://api.spotify.com/v1/me/player/queue?uri={Uri.EscapeDataString(item.Uri)}");
        return r is { IsSuccessStatusCode: true };
    }

    private string _fedFor;

    /// <summary>
    /// Island queue: shortly before the song ends, its first song goes to Spotify (once per song). Songs put into
    /// Spotify's own queue directly in Spotify play before it – Spotify only lets apps add at the end.
    /// </summary>
    public async Task FeedAsync(string playingTitle, TimeSpan left)
    {
        if (IslandQueue.Count == 0 || left > TimeSpan.FromSeconds(15) || left <= TimeSpan.Zero || _fedFor == playingTitle) return;
        _fedFor = playingTitle;
        var next = IslandQueue[0];
        if (await AddToQueueAsync(next)) { IslandQueue.Remove(next); SaveIslandQueue(); }
    }

    public void SaveIslandQueue() =>
        Storage.Save("spotify_island_queue.json", IslandQueue.Select(i => new SavedSong(i.Name, i.Artist, i.Duration, i.Uri, i.CoverUrl)).ToList());

    public void LoadIslandQueue()
    {
        foreach (var s in Storage.Load<List<SavedSong>>("spotify_island_queue.json") ?? new())
            IslandQueue.Add(new QueueItem { Name = s.Name, Artist = s.Artist, Duration = s.Duration, Uri = s.Uri, CoverUrl = s.Cover, Cover = Picture(s.Cover) });
    }

    public sealed record SavedSong(string Name, string Artist, string Duration, string Uri, string Cover);

    private static bool Same(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        a = a.Trim(); b = b.Trim();
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    public void SignOut()
    {
        _access = _refresh = null;
        Connected = false;
        Status = L.T("Nicht verbunden");
        Queue.Clear();
        try { File.Delete(Storage.PathOf("spotify_token.bin")); } catch { }
    }

    // ---------- storage (DPAPI) ----------

    private void SaveToken() =>
        File.WriteAllText(Storage.PathOf("spotify_token.bin"), DiscordService.Protect($"{_access}\n{_refresh}\n{_expires.Ticks}\n{_scope}"));

    private (string, string, DateTime)? LoadToken()
    {
        var p = Storage.PathOf("spotify_token.bin");
        if (!File.Exists(p)) return null;
        var parts = DiscordService.Unprotect(File.ReadAllText(p)).Split('\n');
        if (parts.Length >= 4) _scope = parts[3]; // older files have no rights stored → "connect once more" for liking
        return parts.Length >= 3 && long.TryParse(parts[2], out var ticks) ? (parts[0], parts[1], new DateTime(ticks)) : null;
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
