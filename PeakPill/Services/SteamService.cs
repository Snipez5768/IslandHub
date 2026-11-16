using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Win32;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>
/// Steam downloads / updates, read from Steam's own log (logs\content_log.txt): it states when an update starts
/// ("download 24471778272/53434507440"), the current rate about once a minute, and when it pauses or ends.
/// Progress in between = start value + rate × time (Steam writes the game's .acf file only rarely).
/// </summary>
public sealed class SteamService : ObservableObject, IDownload
{
    private static readonly Regex Line = new(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Started = new(@"AppID (\d+) update started : download (\d+)/(\d+)", RegexOptions.Compiled);
    // the game itself ("App update") and its mods ("Workshop update") both report their end as "… changed : None"
    private static readonly Regex Changed = new(@"AppID (\d+) (?:App|Workshop) update changed : (.*)$", RegexOptions.Compiled);
    private static readonly Regex Canceled = new(@"AppID (\d+) update canceled", RegexOptions.Compiled);
    // a sure end: installed with no update flag left, or Steam took the game off its download list
    private static readonly Regex Done = new(@"AppID (\d+) (?:state changed : Fully Installed,\s*$|scheduler finished : removed from schedule)", RegexOptions.Compiled);
    private static readonly Regex Rate = new(@"Current download rate: ([\d.]+) Mbps", RegexOptions.Compiled);

    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _root;
    private long _logPos = -1;
    private bool _busy;

    // state from the log
    private string _app;
    private bool _active;
    private double _est, _bytesPerSec;
    private long _total;
    private DateTime _estAt;

    private string _appId, _name, _speed = "";
    private double _progress;
    private bool _downloading;

    public bool Downloading { get => _downloading; private set => Set(ref _downloading, value); }
    public string AppId { get => _appId; private set => Set(ref _appId, value); }
    public string Name { get => _name; private set => Set(ref _name, value); }
    /// <summary>0…1 (estimated between Steam's rate reports)</summary>
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Raise(nameof(Percent)); } }
    public string Percent => $"{Progress * 100:0} %";
    public string Speed { get => _speed; private set => Set(ref _speed, value); }
    private string _eta = "";
    /// <summary>Remaining time at the current rate: "noch 38 Min", "noch 1 Std 12 Min", "noch < 1 Min".</summary>
    public string Eta { get => _eta; private set => Set(ref _eta, value); }

    private static string FormatEta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalMinutes < 1) return "noch < 1 Min";
        if (t.TotalHours < 1) return $"noch {Math.Ceiling(t.TotalMinutes):0} Min";
        int h = (int)t.TotalHours, m = t.Minutes;
        return m == 0 ? $"noch {h} Std" : $"noch {h} Std {m} Min";
    }
    private System.Windows.Media.ImageSource _icon;
    /// <summary>The game's icon from Steam's library cache (null → a generic symbol is shown).</summary>
    public System.Windows.Media.ImageSource Icon { get => _icon; private set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => Icon != null;

    // ---------- 2026-11-16: where, stalled, hide, open ----------

    private string _dismissedApp;
    public string Where => "in Steam";
    /// <summary>Steam reports a rate of 0 for a while (paused, no connection, disk full).</summary>
    public bool Stalled => Downloading && _bytesPerSec <= 0 && DateTime.Now - _estAt > TimeSpan.FromMinutes(3);
    public void Dismiss() { _dismissedApp = AppId; Downloading = false; }
    public void OpenSource()
    {
        try { Process.Start(new ProcessStartInfo("steam://open/downloads") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Steam's library icon: appcache\librarycache\&lt;id&gt;\&lt;40-hex&gt;.jpg (new layout) or &lt;id&gt;_icon.jpg (old).</summary>
    private string IconFile(string appId)
    {
        if (_root == null || appId == null) return null;
        var lc = Path.Combine(_root, "appcache", "librarycache");
        var dir = Path.Combine(lc, appId);
        if (Directory.Exists(dir))
        {
            var hashed = Directory.EnumerateFiles(dir, "*.jpg").FirstOrDefault(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "^[0-9a-f]{40}$"));
            if (hashed != null) return hashed;
        }
        var old = Path.Combine(lc, $"{appId}_icon.jpg");
        return File.Exists(old) ? old : null;
    }

    private static System.Windows.Media.ImageSource LoadImage(string file)
    {
        if (file == null) return null;
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(file);
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private DateTime _startedAt = DateTime.Now;
    /// <summary>Size and duration of the download that just finished (for the "ready" banner).</summary>
    public long LastSizeBytes { get; private set; }
    public TimeSpan LastDuration { get; private set; }

    /// <summary>A download finished: (game name, app id).</summary>
    public event Action<string, string> Finished;

    public void Start()
    {
        _t.Tick += async (_, _) => await Tick();
        _t.Start();
    }

    private bool _demo;

    /// <summary>Render test only: a made-up download.</summary>
    public void Demo(string name, double progress, string speed, string appId = "0")
    {
        _demo = true;
        _root ??= SteamRoot();
        AppId = appId; Name = name; Progress = progress; Speed = speed; Eta = FormatEta(38 * 60);
        Icon = LoadImage(IconFile(appId));
        Downloading = true;
    }

    public void EndDemo()
    {
        Downloading = false;
        _demo = false;
    }

    private async Task Tick()
    {
        if (_busy || _demo) return;
        _busy = true;
        try
        {
            if (Process.GetProcessesByName("steam").Length == 0) { _active = false; Downloading = false; return; }
            await Task.Run(ReadLog);
            // hidden by the user: stays out of the island until another game downloads
            if (_dismissedApp != null && _dismissedApp != _app) _dismissedApp = null;
            if (_active && _app != null && _total > 0 && _dismissedApp == _app) { Downloading = false; return; }
            if (_active && _app != null && _total > 0)
            {
                double est = Math.Min(_total, _est + _bytesPerSec * Math.Max(0, (DateTime.Now - _estAt).TotalSeconds));
                if (AppId != _app)
                {
                    var id = _app;
                    Name = await Task.Run(() => GameName(id));
                    Icon = await Task.Run(() => LoadImage(IconFile(id)));
                }
                if (!Downloading || AppId != _app) _startedAt = DateTime.Now;
                AppId = _app;
                LastSizeBytes = _total;
                Progress = est / _total;
                Speed = _bytesPerSec > 0 ? $"{_bytesPerSec / 1_000_000:0.#} MB/s" : "";
                Eta = _bytesPerSec > 0 ? FormatEta((_total - est) / _bytesPerSec) : "";
                Downloading = true;
            }
            else if (Downloading)
            {
                Downloading = false;
                LastDuration = DateTime.Now - _startedAt;
                var (id, name) = (AppId, Name);
                if (await Task.Run(() => IsInstalled(id))) Finished?.Invoke(name, id);
            }
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _busy = false; }
    }

    /// <summary>Reads what Steam added to its log since last time (on the first run: its recent part).</summary>
    private void ReadLog()
    {
        _root ??= SteamRoot();
        if (_root == null) return;
        var path = Path.Combine(_root, "logs", "content_log.txt");
        if (!File.Exists(path)) return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (_logPos < 0 || _logPos > fs.Length) _logPos = Math.Max(0, fs.Length - 400_000);
        if (_logPos == fs.Length) return;
        fs.Seek(_logPos, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var text = sr.ReadToEnd();
        _logPos = fs.Length;
        foreach (var raw in text.Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)) continue;
            var msg = m.Groups[2].Value;

            if (Started.Match(msg) is { Success: true } s)
            {
                _app = s.Groups[1].Value;
                _est = double.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture);
                _total = long.Parse(s.Groups[3].Value, CultureInfo.InvariantCulture);
                _estAt = at;
                _active = _total > 0 && _est < _total;
            }
            else if (Rate.Match(msg) is { Success: true } r && _active)
            {
                // add what came in since the last report, then go on with the new rate
                _est += _bytesPerSec * Math.Max(0, (at - _estAt).TotalSeconds);
                _bytesPerSec = double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture) * 1_000_000 / 8;
                _estAt = at;
            }
            else if (Changed.Match(msg) is { Success: true } c && c.Groups[1].Value == _app)
            {
                if (!c.Groups[2].Value.Contains("Running Update")) _active = false; // "None" → paused or done
            }
            else if (Canceled.Match(msg) is { Success: true } x && x.Groups[1].Value == _app)
            {
                _active = false;
            }
            else if (Done.Match(msg) is { Success: true } d && d.Groups[1].Value == _app)
            {
                _active = false;
            }
        }
        // the log may be old (Steam idle for hours) – an "active" update without fresh lines is not trusted
        if (_active && DateTime.Now - _estAt > TimeSpan.FromMinutes(5)) _active = false;
    }

    private static string SteamRoot()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return (k?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        }
        catch { return null; }
    }

    private IEnumerable<string> Libraries()
    {
        if (_root == null) yield break;
        yield return _root;
        var vdf = Path.Combine(_root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), @"""path""\s+""([^""]+)"""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    private string Manifest(string appId) =>
        Libraries().Select(l => Path.Combine(l, "steamapps", $"appmanifest_{appId}.acf")).FirstOrDefault(File.Exists);

    private string GameName(string appId)
    {
        var f = Manifest(appId);
        return f != null && Parse(f).TryGetValue("name", out var n) ? n : "Steam-Download";
    }

    private bool IsInstalled(string appId)
    {
        var f = appId == null ? null : Manifest(appId);
        return f != null && Parse(f).TryGetValue("StateFlags", out var s) && s == "4"; // FullyInstalled
    }

    private static Dictionary<string, string> Parse(string file)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            foreach (Match m in Regex.Matches(sr.ReadToEnd(), @"^\s*""(\w+)""\s+""([^""]*)""", RegexOptions.Multiline))
                d.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }
        catch { }
        return d;
    }

    /// <summary>Installed Steam games (name, app id) from every library – tools/redistributables left out.</summary>
    public List<(string Id, string Name)> InstalledGames()
    {
        _root ??= SteamRoot();
        var list = new List<(string, string)>();
        try
        {
            foreach (var lib in Libraries().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "appmanifest_*.acf"))
                {
                    var d = Parse(f);
                    if (!d.TryGetValue("appid", out var id) || !d.TryGetValue("name", out var name)) continue;
                    if (id == "228980" || Regex.IsMatch(name, @"Redistributable|Steam Linux Runtime|^Proton|Steamworks", RegexOptions.IgnoreCase)) continue;
                    if (!list.Any(x => x.Item1 == id)) list.Add((id, name));
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return list.OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Installed games, the last played first (Steam writes "LastPlayed" into each game's manifest).</summary>
    public List<(string Id, string Name, DateTime LastPlayed)> RecentGames()
    {
        _root ??= SteamRoot();
        var list = new List<(string, string, DateTime)>();
        try
        {
            foreach (var lib in Libraries().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "appmanifest_*.acf"))
                {
                    var d = Parse(f);
                    if (!d.TryGetValue("appid", out var id) || !d.TryGetValue("name", out var name)) continue;
                    if (id == "228980" || Regex.IsMatch(name, @"Redistributable|Steam Linux Runtime|^Proton|Steamworks", RegexOptions.IgnoreCase)) continue;
                    var last = d.TryGetValue("LastPlayed", out var lp) && long.TryParse(lp, out var unix) && unix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : DateTime.MinValue;
                    if (!list.Any(x => x.Item1 == id)) list.Add((id, name, last));
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return list.OrderByDescending(x => x.Item3).ThenBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The game's small round library icon (for menus), null if Steam has none cached.</summary>
    public string SmallIcon(string appId)
    {
        _root ??= SteamRoot();
        return IconFile(appId);
    }

    /// <summary>The best picture of a game in Steam's library cache: tall cover, else the header, else the small icon.</summary>
    public string CoverFile(string appId)
    {
        _root ??= SteamRoot();
        if (_root == null || appId == null) return null;
        var dir = Path.Combine(_root, "appcache", "librarycache", appId);
        if (Directory.Exists(dir))
            foreach (var pattern in new[] { "library_600x900*.jpg", "header*.jpg" })
                if (Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).FirstOrDefault() is { } f) return f;
        var lc = Path.Combine(_root, "appcache", "librarycache");
        foreach (var old in new[] { $"{appId}_library_600x900.jpg", $"{appId}_header.jpg" })
            if (File.Exists(Path.Combine(lc, old))) return Path.Combine(lc, old);
        return IconFile(appId);
    }

    public static void Play(string appId)
    {
        try { Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true }); } catch { }
    }
}
