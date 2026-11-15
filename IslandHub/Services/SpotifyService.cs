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
using IslandHub.Core;

namespace IslandHub.Services;

public sealed class QueueItem
{
    public string Name { get; init; }
    public string Artist { get; init; }
    public string Duration { get; init; }
    public ImageSource Cover { get; init; }
}

/// <summary>
/// Spotify Web API with the user's own Spotify app (client id only – PKCE sign-in in the browser, no secret):
/// reads the playback queue for the music page. Tokens are stored encrypted for this Windows user.
/// </summary>
public sealed class SpotifyService : ObservableObject
{
    public const int Port = 47824;
    public static string RedirectUri => $"http://127.0.0.1:{Port}/callback";
    private const string Scopes = "user-read-playback-state user-read-currently-playing";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string _access, _refresh, _status = "Nicht verbunden";
    private DateTime _expires;
    private bool _connected, _busy;

    public ObservableCollection<QueueItem> Queue { get; } = new();
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public static bool Configured => !string.IsNullOrEmpty(App.Settings.SpotifyClientId);

    public void Start()
    {
        var t = LoadToken();
        if (t != null && Configured)
        {
            (_access, _refresh, _expires) = t.Value;
            Connected = true;
            Status = "Verbunden";
        }
    }

    // ---------- sign-in (PKCE): browser → Spotify → back to 127.0.0.1:47824 ----------

    public async Task<bool> SignInAsync()
    {
        if (!Configured) { Status = "Client-ID fehlt"; return false; }
        var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(12));
        var url = "https://accounts.spotify.com/authorize?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(App.Settings.SpotifyClientId)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&code_challenge_method=S256&code_challenge={challenge}&state={state}";

        var listener = new TcpListener(IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch { Status = $"Port {Port} ist belegt"; return false; }
        try
        {
            Status = "Bitte im Browser bei Spotify erlauben …";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            var accept = listener.AcceptTcpClientAsync();
            if (await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMinutes(3))) != accept) { Status = "Abgebrochen (keine Antwort)"; return false; }
            using var client = accept.Result;
            var stream = client.GetStream();
            var buf = new byte[8192];
            int n = await stream.ReadAsync(buf);
            var firstLine = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0]; // GET /callback?code=…&state=… HTTP/1.1
            var query = firstLine.Split(' ').ElementAtOrDefault(1) ?? "";
            var qs = System.Web.HttpUtility.ParseQueryString(query.Contains('?') ? query[(query.IndexOf('?') + 1)..] : "");
            bool ok = qs["state"] == state && !string.IsNullOrEmpty(qs["code"]);
            var html = ok ? "<h2 style='font-family:sans-serif'>IslandHub ist mit Spotify verbunden – du kannst dieses Fenster schließen.</h2>"
                          : "<h2 style='font-family:sans-serif'>Verbindung abgebrochen.</h2>";
            var body = Encoding.UTF8.GetBytes(html);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
            if (!ok) { Status = "Spotify hat nicht erlaubt"; return false; }

            var tok = await Token(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = qs["code"], ["redirect_uri"] = RedirectUri,
                ["client_id"] = App.Settings.SpotifyClientId, ["code_verifier"] = verifier
            });
            if (!tok) { Status = "Anmeldung fehlgeschlagen – stimmt die Client-ID / Redirect-URI?"; return false; }
            Connected = true;
            Status = "Verbunden";
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
        if (!ok) { Connected = false; Status = "Bitte neu verbinden"; }
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
            {
                if (t == null) continue;
                var artists = t["artists"] is JsonArray a ? string.Join(", ", a.Select(x => x?["name"]?.GetValue<string>()).Where(x => x != null))
                            : t["show"]?["name"]?.GetValue<string>() ?? "";
                var images = (t["album"]?["images"] ?? t["images"]) as JsonArray;
                var img = images?.OrderBy(i => i?["width"]?.GetValue<int>() ?? 999).FirstOrDefault()?["url"]?.GetValue<string>();
                var ms = t["duration_ms"]?.GetValue<double>() ?? 0;
                ImageSource cover = null;
                if (img != null)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(img);
                        bi.DecodePixelWidth = 52;
                        bi.EndInit();
                        cover = bi;
                    }
                    catch { }
                }
                var d = TimeSpan.FromMilliseconds(ms);
                items.Add(new QueueItem { Name = t["name"]?.GetValue<string>() ?? "", Artist = artists, Duration = d.ToString(@"m\:ss"), Cover = cover });
            }
            Queue.Clear();
            foreach (var i in items) Queue.Add(i);
            Raise(nameof(Queue));
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _busy = false; }
        if (_again != null) { var t = _again; _again = null; await RefreshQueueAsync(t.Length > 0 ? t : null); }
    }

    private string _again;

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
        Status = "Nicht verbunden";
        Queue.Clear();
        try { File.Delete(Storage.PathOf("spotify_token.bin")); } catch { }
    }

    // ---------- storage (DPAPI) ----------

    private void SaveToken() =>
        File.WriteAllText(Storage.PathOf("spotify_token.bin"), DiscordService.Protect($"{_access}\n{_refresh}\n{_expires.Ticks}"));

    private static (string, string, DateTime)? LoadToken()
    {
        var p = Storage.PathOf("spotify_token.bin");
        if (!File.Exists(p)) return null;
        var parts = DiscordService.Unprotect(File.ReadAllText(p)).Split('\n');
        return parts.Length == 3 && long.TryParse(parts[2], out var ticks) ? (parts[0], parts[1], new DateTime(ticks)) : null;
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
