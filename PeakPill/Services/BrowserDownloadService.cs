using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>What the island shows of a running download – Steam and the browser look the same.</summary>
public interface IDownload
{
    bool Downloading { get; }
    string Name { get; }
    double Progress { get; }
    string Percent { get; }
    string Speed { get; }
    string Eta { get; }
    System.Windows.Media.ImageSource Icon { get; }
    /// <summary>Where it downloads: "in Steam", "in Chrome" … (2026-11-16)</summary>
    string Where { get; }
    /// <summary>Nothing has arrived for minutes – probably broken or paused.</summary>
    bool Stalled { get; }
    /// <summary>Takes it out of the island (until it moves again).</summary>
    void Dismiss();
    /// <summary>Brings the app that downloads to the front (its downloads page where possible).</summary>
    void OpenSource();
}

/// <summary>
/// Browser downloads in the island (user wish 2026-11-14) – without a browser extension: browsers write a running
/// download as a temporary file in the Downloads folder (Chrome / Edge / Brave "….crdownload", Firefox "….part") and
/// rename it at the end. Its growing size gives the speed; the full size comes from the Chromium browser's own
/// download history (a copy of its "History" database) when it is there – otherwise only the MB so far are shown.
/// Same properties as <see cref="SteamService"/>, so the island shows both the same way.
/// </summary>
public sealed class BrowserDownloadService : ObservableObject, IDownload
{
    private static readonly string[] TempExt = { ".crdownload", ".part", ".partial" };
    private static readonly Regex Unconfirmed = new(@"^(Unconfirmed|Nicht bestätigt|Nicht bestätigte?)\s+\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Item
    {
        public string Temp, Final;
        public long Size, Total = -1;
        public DateTime First = DateTime.Now, LastAt = DateTime.Now;
        public long LastSize;
        public double Rate;
        public int Lookups;
        public DateTime NextLookup = DateTime.Now.AddSeconds(2);
        public bool Shown;
        /// <summary>Browser that downloads it (from its history, ".part" = Firefox, else the browser that runs).</summary>
        public string App;
        /// <summary>Last time the file grew – a leftover from a broken download never grows again.</summary>
        public DateTime LastGrow = DateTime.Now;
        public bool StuckTold;
        /// <summary>Hidden by the user at this size – it comes back only if it grows again.</summary>
        public long DismissedAt = -1;
        public bool IsStalled => DateTime.Now - LastGrow > StallAfter;
    }

    private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(3);
    private Item _top;
    private bool _stalled;
    private string _where = "";

    public bool Stalled { get => _stalled; private set => Set(ref _stalled, value); }
    public string Where { get => _where; private set => Set(ref _where, value); }

    /// <summary>A download stopped growing (name, browser) – told once per download.</summary>
    public event Action<string, string> Stuck;

    public void Dismiss()
    {
        foreach (var it in _items.Values.Where(i => i.Shown)) it.DismissedAt = it.Size;
        Publish();
    }

    public void OpenSource() => OpenBrowser(_top?.App);

    private static readonly (string Name, string Exe)[] BrowserExes =
        { ("Chrome", "chrome"), ("Edge", "msedge"), ("Brave", "brave"), ("Vivaldi", "vivaldi"), ("Firefox", "firefox"), ("Opera", "opera") };

    /// <summary>The browser's window to the front (a running one); started if none runs.</summary>
    public static void OpenBrowser(string app)
    {
        try
        {
            var exe = BrowserExes.FirstOrDefault(b => b.Name == app).Exe;
            foreach (var name in exe != null ? new[] { exe } : BrowserExes.Select(b => b.Exe))
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    using (p)
                        if (p.MainWindowHandle != IntPtr.Zero && PrivacyService.Focus(p.MainWindowHandle)) return;
            if (exe != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe + ".exe") { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>No history entry: the one browser that runs (the first found).</summary>
    private static string RunningBrowser()
    {
        foreach (var (name, exe) in BrowserExes)
        {
            var ps = System.Diagnostics.Process.GetProcessesByName(exe);
            bool any = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            if (any) return name;
        }
        return null;
    }

    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private string _dir;
    private bool _busy;

    private bool _downloading;
    private string _name = "", _speed = "", _eta = "", _percent = "";
    private double _progress;

    public bool Downloading { get => _downloading; private set => Set(ref _downloading, value); }
    public string Name { get => _name; private set => Set(ref _name, value); }
    /// <summary>0…1; 0 while the full size is not known (then <see cref="Percent"/> shows the MB so far).</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool HasTotal { get; private set; }
    public string Percent { get => _percent; private set => Set(ref _percent, value); }
    public string Speed { get => _speed; private set => Set(ref _speed, value); }
    public string Eta { get => _eta; private set => Set(ref _eta, value); }
    /// <summary>The Live page binds an icon like Steam's; browser downloads show the arrow instead.</summary>
    public System.Windows.Media.ImageSource Icon => null;

    /// <summary>name, full path, size in bytes</summary>
    public event Action<string, string, long> Finished;

    public void Start()
    {
        _dir = DownloadsFolder();
        if (_dir == null || !Directory.Exists(_dir)) return;
        _t.Tick += async (_, _) => await Tick();
        _t.Start();
    }

    private async Task Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var dir = _dir;
            // what is in the folder right now (off the UI thread – the folder can be big or on a slow disk)
            var now = await Task.Run(() =>
            {
                var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                        if (TempExt.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                            try { d[f] = new FileInfo(f).Length; } catch { }
                }
                catch { }
                return d;
            });

            // gone: finished (the final file is there) or renamed / cancelled
            foreach (var gone in _items.Keys.Where(k => !now.ContainsKey(k)).ToList())
            {
                var it = _items[gone];
                _items.Remove(gone);
                var renamed = now.Keys.FirstOrDefault(k => !_items.ContainsKey(k) && now[k] >= it.Size * 0.9);
                if (renamed != null)
                {
                    // Chrome: "Unconfirmed 123.crdownload" → "name.zip.crdownload" – the same download goes on
                    it.Temp = renamed; it.Final = FinalOf(renamed); it.NextLookup = DateTime.Now; it.Lookups = 0;
                    _items[renamed] = it;
                    continue;
                }
                var final = it.Final;
                bool done = File.Exists(final) && new FileInfo(final).Length > 0 && DateTime.Now - File.GetLastWriteTime(final) < TimeSpan.FromMinutes(2)
                            || File.Exists(final) && new FileInfo(final).Length >= Math.Max(1, it.Size);
                if (done && it.Shown) Finished?.Invoke(DisplayName(final), final, new FileInfo(final).Length);
            }

            foreach (var (path, size) in now)
            {
                if (!_items.TryGetValue(path, out var it))
                {
                    _items[path] = it = new Item { Temp = path, Final = FinalOf(path), Size = size, LastSize = size };
                    // a file that was last written long ago is a leftover of a broken download – stalled from the start
                    try { var w = File.GetLastWriteTime(path); if (DateTime.Now - w > StallAfter) it.LastGrow = w; } catch { }
                    if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) it.App = "Firefox";
                }
                if (size > it.Size) it.LastGrow = DateTime.Now;
                if (it.DismissedAt >= 0 && size > it.DismissedAt + 64 * 1024) it.DismissedAt = -1; // moves again → back in the island
                var dt = (DateTime.Now - it.LastAt).TotalSeconds;
                if (dt >= 0.9)
                {
                    double r = Math.Max(0, (size - it.LastSize) / dt);
                    it.Rate = it.Rate <= 0 ? r : it.Rate * 0.6 + r * 0.4;
                    it.LastSize = size; it.LastAt = DateTime.Now;
                }
                it.Size = size;
                // tiny files that are done at once never show up – only what runs a moment or is big
                if (!it.Shown && (DateTime.Now - it.First > TimeSpan.FromSeconds(1.5) || size > 8_000_000)) it.Shown = true;
                if (it.Total < 0 && it.Lookups < 6 && DateTime.Now >= it.NextLookup && !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    it.Lookups++;
                    it.NextLookup = DateTime.Now.AddSeconds(new[] { 3, 6, 10, 15, 25, 40 }[Math.Min(it.Lookups, 5)]);
                    var (temp, final) = (it.Temp, it.Final);
                    var (total, app) = await Task.Run(() => ChromiumTotal(temp, final));
                    if (total > 0) it.Total = total;
                    if (app != null) it.App = app;
                }
                if (it.App == null && it.Lookups >= 2) it.App = RunningBrowser() ?? ""; // once – "" = unknown
                if (it.Shown && it.IsStalled && !it.StuckTold && it.DismissedAt < 0)
                {
                    it.StuckTold = true;
                    Stuck?.Invoke(DisplayName(it.Final), it.App);
                }
            }

            Publish();
            _t.Interval = TimeSpan.FromSeconds(_items.Count > 0 ? 1 : 2);
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _busy = false; }
    }

    private void Publish()
    {
        // running ones first; hidden ones not at all
        var shown = _items.Values.Where(i => i.Shown && i.DismissedAt < 0).OrderBy(i => i.IsStalled).ThenByDescending(i => i.First).ToList();
        if (shown.Count == 0)
        {
            _top = null;
            Downloading = false;
            return;
        }
        var p = _top = shown[0];
        Name = DisplayName(p.Final) + (shown.Count > 1 ? $" (+{shown.Count - 1})" : "");
        HasTotal = p.Total > 0;
        Progress = HasTotal ? Math.Clamp((double)p.Size / p.Total, 0, 1) : 0;
        Percent = HasTotal ? $"{Progress * 100:0} %" : Bytes(p.Size);
        Stalled = p.IsStalled;
        Where = !string.IsNullOrEmpty(p.App) ? L.T($"in {p.App}") : L.T("im Browser");
        Speed = p.Rate > 0 && !Stalled ? Bytes((long)p.Rate) + "/s" : "";
        Eta = Stalled ? "hängt" : HasTotal && p.Rate > 1000 ? FormatEta((p.Total - p.Size) / p.Rate) : HasTotal ? "" : "geladen";
        Downloading = true;
        Raise(nameof(HasTotal));
    }

    private static string FinalOf(string temp)
    {
        foreach (var e in TempExt)
            if (temp.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return temp[..^e.Length];
        return temp;
    }

    private static string DisplayName(string final)
    {
        var n = Path.GetFileName(final);
        return Unconfirmed.IsMatch(n) ? "Download" : n;
    }

    public static string Bytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    private static string FormatEta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalMinutes < 1) return L.T($"noch {Math.Max(1, (int)t.TotalSeconds)} s");
        if (t.TotalHours < 1) return L.T($"noch {Math.Ceiling(t.TotalMinutes):0} Min");
        return L.T($"noch {(int)t.TotalHours} Std {t.Minutes} Min");
    }

    // ---------- full size from Chrome / Edge / Brave's download history ----------

    private static IEnumerable<string> HistoryFiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data", @"Vivaldi\User Data", @"Chromium\User Data" })
        {
            var dir = Path.Combine(local, root);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(dir).Where(p => Path.GetFileName(p) is "Default" || Path.GetFileName(p).StartsWith("Profile ")).ToList(); }
            catch { continue; }
            foreach (var p in profiles)
            {
                var h = Path.Combine(p, "History");
                // only browsers in use right now
                if (File.Exists(h) && DateTime.Now - File.GetLastWriteTime(h) < TimeSpan.FromHours(6)) yield return h;
            }
        }
    }

    /// <summary>
    /// The browser keeps its database open, so a copy is read (the file is shared for reading). The row of a running
    /// download has the temporary path (or the final path) and the full size.
    /// </summary>
    private static (long Total, string App) ChromiumTotal(string temp, string final)
    {
        foreach (var h in HistoryFiles())
        {
            string app = h.Contains(@"\Google\Chrome\") ? "Chrome" : h.Contains(@"\Microsoft\Edge\") ? "Edge" : h.Contains(@"\BraveSoftware\") ? "Brave"
                       : h.Contains(@"\Vivaldi\") ? "Vivaldi" : "Chromium";
            var copy = Path.Combine(Path.GetTempPath(), $"peakpill-history-{Environment.ProcessId}.db");
            try
            {
                Copy(h, copy);
                if (File.Exists(h + "-wal")) Copy(h + "-wal", copy + "-wal");
                using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT total_bytes FROM downloads WHERE (current_path = $t OR target_path = $f) AND total_bytes > 0 ORDER BY id DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$t", temp);
                cmd.Parameters.AddWithValue("$f", final);
                if (cmd.ExecuteScalar() is long total && total > 0) return (total, app);
            }
            catch { /* locked, older layout … then only the MB so far */ }
            finally
            {
                try { File.Delete(copy); File.Delete(copy + "-wal"); } catch { }
            }
        }
        return (-1, null);
    }

    private static void Copy(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = File.Create(to);
        src.CopyTo(dst);
    }

    // ---------- the user's Downloads folder (it can be moved) ----------

    private static string DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

#if DEBUG
    /// <summary>Render test only: pretend a download runs / ends.</summary>
    public void Demo(string name, double progress, string speed)
    {
        _t.Stop();
        Name = name; HasTotal = true; Progress = progress; Percent = $"{progress * 100:0} %"; Speed = speed; Eta = L.T("noch 1 Min"); Where = "in Chrome"; Downloading = true;
    }
    public void EndDemo() => Downloading = false;
#endif
}
