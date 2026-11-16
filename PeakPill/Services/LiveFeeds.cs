using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>A moment from outside for the island: who, what, colour, link.</summary>
public sealed record FeedMoment(string Kind, string Icon, string Color, string Title, string Text, string Link);

/// <summary>A football match as the "Heute" page shows it.</summary>
public sealed record MatchLine(string Home, string Away, string Score, string State, bool Live);

/// <summary>
/// Things the user follows (2026-11-16), each read gently and only as often as it can change:
/// • Bundesliga & Co. from OpenLigaDB (free, no key) – kickoff, goals, final whistle of the user's team;
/// • Twitch (the user's own free developer key) – a followed channel goes live;
/// • YouTube (no key: the channel's public "/live" page) – a channel goes live;
/// • DHL parcels (the user's own free developer key) – the status changes.
/// </summary>
public sealed class LiveFeeds : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime _nextSport = DateTime.MinValue, _nextTwitch = DateTime.MinValue, _nextYouTube = DateTime.MinValue, _nextParcel = DateTime.MinValue;
    private readonly Dictionary<string, string> _matchState = new(), _parcelState = new();
    private readonly HashSet<string> _liveNow = new(StringComparer.OrdinalIgnoreCase);
    private string _twitchToken;
    private DateTime _twitchTokenUntil;
    private MatchLine _match;
    private bool _busy;

    public event Action<FeedMoment> Moment;

    /// <summary>Today's match of the user's team (null = none today).</summary>
    public MatchLine Match { get => _match; private set => Set(ref _match, value); }

    static LiveFeeds() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 PeakPill");

    public void Start()
    {
        _t.Tick += async (_, _) => await Tick();
        _t.Start();
        _ = Tick();
    }

    private async Task Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = App.Settings;
            var now = DateTime.Now;
            if (!string.IsNullOrWhiteSpace(s.SportTeam) && now >= _nextSport) await Sport(s.SportLeague, s.SportTeam.Trim());
            if (s.TwitchChannels.Count > 0 && !string.IsNullOrWhiteSpace(s.TwitchClientId) && now >= _nextTwitch) { _nextTwitch = now.AddMinutes(2); await Twitch(); }
            if (s.YouTubeChannels.Count > 0 && now >= _nextYouTube) { _nextYouTube = now.AddMinutes(5); await YouTube(); }
            if (s.Parcels.Count > 0 && !string.IsNullOrWhiteSpace(s.DhlKey) && now >= _nextParcel) { _nextParcel = now.AddMinutes(30); await Parcels(); }
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _busy = false; }
    }

    private void Tell(FeedMoment m) => Ui.Run(() => Moment?.Invoke(m));

    // ---------- football (OpenLigaDB) ----------

    private async Task Sport(string league, string team)
    {
        league = string.IsNullOrWhiteSpace(league) ? "bl1" : league.Trim();
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.openligadb.de/getmatchdata/{league}"));
        MatchLine today = null;
        bool live = false;
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            string t1 = m.GetProperty("team1").GetProperty("teamName").GetString() ?? "", t2 = m.GetProperty("team2").GetProperty("teamName").GetString() ?? "";
            string s1 = Short(m.GetProperty("team1")), s2 = Short(m.GetProperty("team2"));
            bool mine = t1.Contains(team, StringComparison.OrdinalIgnoreCase) || t2.Contains(team, StringComparison.OrdinalIgnoreCase)
                        || s1.Equals(team, StringComparison.OrdinalIgnoreCase) || s2.Equals(team, StringComparison.OrdinalIgnoreCase);
            if (!mine) continue;
            var kick = DateTime.Parse(m.GetProperty("matchDateTime").GetString() ?? "", CultureInfo.InvariantCulture);
            bool finished = m.GetProperty("matchIsFinished").GetBoolean();
            int g1 = 0, g2 = 0;
            foreach (var g in m.GetProperty("goals").EnumerateArray())
            {
                if (g.TryGetProperty("scoreTeam1", out var a) && a.ValueKind == JsonValueKind.Number) g1 = Math.Max(g1, a.GetInt32());
                if (g.TryGetProperty("scoreTeam2", out var b) && b.ValueKind == JsonValueKind.Number) g2 = Math.Max(g2, b.GetInt32());
            }
            bool running = !finished && DateTime.Now >= kick && DateTime.Now <= kick.AddHours(2.5);
            string id = m.GetProperty("matchID").GetInt32().ToString();
            string state = finished ? $"end {g1}:{g2}" : running ? $"live {g1}:{g2}" : "before";
            if (_matchState.TryGetValue(id, out var was) && was != state)
            {
                string score = $"{s1} {g1}:{g2} {s2}";
                if (state.StartsWith("live") && was == "before") Tell(new("sport", "sport", "#FF30D158", "Anpfiff", score, null));
                else if (state.StartsWith("live") && was.StartsWith("live")) Tell(new("sport", "sport", "#FF30D158", "Tor!", score, null));
                else if (state.StartsWith("end") && !was.StartsWith("end")) Tell(new("sport", "sport", "#FF8E8E93", "Abpfiff", score, null));
            }
            _matchState[id] = state;
            if (kick.Date == DateTime.Today || running)
                today = new MatchLine(s1, s2, finished || running ? $"{g1}:{g2}" : kick.ToString("HH:mm"), finished ? "Ende" : running ? "live" : "heute", running);
            live |= running || !finished && kick > DateTime.Now && kick - DateTime.Now < TimeSpan.FromMinutes(15);
        }
        Ui.Run(() => Match = today);
        // during a match every minute, otherwise every 15 minutes
        _nextSport = DateTime.Now.AddMinutes(live ? 1 : 15);
    }

    private static string Short(JsonElement team)
    {
        var s = team.TryGetProperty("shortName", out var sn) ? sn.GetString() : null;
        return string.IsNullOrWhiteSpace(s) ? team.GetProperty("teamName").GetString() ?? "" : s;
    }

    // ---------- Twitch (Helix, the user's own app key) ----------

    private async Task Twitch()
    {
        var s = App.Settings;
        if (_twitchToken == null || DateTime.Now >= _twitchTokenUntil)
        {
            if (string.IsNullOrWhiteSpace(s.TwitchSecret)) return;
            var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = s.TwitchClientId.Trim(), ["client_secret"] = s.TwitchSecret.Trim(), ["grant_type"] = "client_credentials"
            }));
            if (!resp.IsSuccessStatusCode) return;
            using var tok = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _twitchToken = tok.RootElement.GetProperty("access_token").GetString();
            _twitchTokenUntil = DateTime.Now.AddSeconds(tok.RootElement.GetProperty("expires_in").GetInt32() - 300);
        }
        var q = string.Join("&", s.TwitchChannels.Take(100).Select(c => "user_login=" + Uri.EscapeDataString(c.Trim().ToLowerInvariant())));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/streams?" + q);
        req.Headers.Add("Client-Id", s.TwitchClientId.Trim());
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _twitchToken);
        var r = await Http.SendAsync(req);
        if (!r.IsSuccessStatusCode) { _twitchToken = null; return; }
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var liveNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var login = st.GetProperty("user_login").GetString() ?? "";
            liveNow.Add("tw:" + login);
            if (_liveNow.Contains("tw:" + login)) continue;
            Tell(new("twitch", "record", "#FF9146FF", $"{st.GetProperty("user_name").GetString()} ist live", st.GetProperty("title").GetString() ?? "", $"https://twitch.tv/{login}"));
        }
        _liveNow.RemoveWhere(k => k.StartsWith("tw:") && !liveNow.Contains(k));
        _liveNow.UnionWith(liveNow);
    }

    // ---------- YouTube (no key: the channel's /live page tells whether a stream runs) ----------

    private async Task YouTube()
    {
        foreach (var raw in App.Settings.YouTubeChannels.Take(10))
        {
            var ch = raw.Trim().TrimEnd('/');
            if (ch.Length == 0) continue;
            string url = ch.StartsWith("http") ? ch + "/live" : ch.StartsWith("@") ? $"https://www.youtube.com/{ch}/live" : $"https://www.youtube.com/channel/{ch}/live";
            try
            {
                var html = await Http.GetStringAsync(url);
                bool live = html.Contains("\"isLiveNow\":true") || html.Contains("\"isLive\":true");
                string key = "yt:" + ch;
                if (live && !_liveNow.Contains(key))
                {
                    var title = Regex.Match(html, "<meta name=\"title\" content=\"([^\"]*)\"").Groups[1].Value;
                    var name = Regex.Match(html, "\"ownerChannelName\":\"([^\"]*)\"").Groups[1].Value;
                    Tell(new("youtube", "play.fill", "#FFFF0033", $"{(name.Length > 0 ? name : ch)} ist live", System.Net.WebUtility.HtmlDecode(title), url));
                }
                if (live) _liveNow.Add(key); else _liveNow.Remove(key);
            }
            catch { }
        }
    }

    // ---------- DHL parcels (Shipment Tracking – Unified, the user's own free key) ----------

    private async Task Parcels()
    {
        var s = App.Settings;
        foreach (var p in s.Parcels.Take(10))
        {
            var (label, number) = SplitParcel(p);
            if (number.Length == 0) continue;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api-eu.dhl.com/track/shipments?trackingNumber={Uri.EscapeDataString(number)}&language=de");
                req.Headers.Add("DHL-API-Key", s.DhlKey.Trim());
                var r = await Http.SendAsync(req);
                if (!r.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                var st = doc.RootElement.GetProperty("shipments")[0].GetProperty("status");
                string code = st.TryGetProperty("statusCode", out var sc) ? sc.GetString() ?? "" : "";
                string text = st.TryGetProperty("description", out var d) ? d.GetString() ?? "" : st.TryGetProperty("status", out var ss) ? ss.GetString() ?? "" : "";
                if (_parcelState.TryGetValue(number, out var was) && was != text && text.Length > 0)
                    Tell(new("parcel", code == "delivered" ? "check" : "cart", code == "delivered" ? "#FF30D158" : "#FFFFCC00",
                             code == "delivered" ? $"{label} ist da" : label, text, $"https://www.dhl.de/de/privatkunden/pakete-empfangen/verfolgen.html?piececode={number}"));
                _parcelState[number] = text;
            }
            catch { }
        }
    }

    /// <summary>"Schuhe: 00340434..." → ("Schuhe", "00340434..."); just a number → ("Paket", number).</summary>
    public static (string Label, string Number) SplitParcel(string line)
    {
        var i = line.IndexOf(':');
        return i > 0 ? (line[..i].Trim(), line[(i + 1)..].Trim()) : ("Paket", line.Trim());
    }
}
