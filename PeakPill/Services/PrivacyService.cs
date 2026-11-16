using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>One app that currently uses the microphone, the camera and/or captures the screen.</summary>
public sealed class LiveApp : ObservableObject
{
    private static readonly SolidColorBrush ScreenBrush = Freeze(Color.FromRgb(0xFF, 0x45, 0x3A));
    /// <summary>White by default, blue when chosen in the settings (set by the island).</summary>
    public static Brush MicBrush { get; set; } = Brushes.White;
    private static readonly SolidColorBrush CamBrush = Freeze(Color.FromRgb(0x30, 0xD1, 0x58));

    private bool _mic, _cam, _screen, _selfMuted;
    private string _elapsed = "0:00";
    private DateTime _since;

    public LiveApp(string key, string exePath, string package)
    {
        Key = key;
        ExePath = exePath;
        Package = package;
    }

    /// <summary>Registry key name: the exe path with '#' (desktop apps) or the package family name (Store apps).</summary>
    public string Key { get; }
    public string ExePath { get; internal set; }
    public string Package { get; }
    private string _name;
    public string Name { get => _name; internal set => Set(ref _name, value); }
    /// <summary>The app's own name (a browser in a call shows the call's name instead).</summary>
    internal string BaseName { get; set; }
    public ImageSource Icon { get; internal set; }

    public bool Mic { get => _mic; internal set { if (Set(ref _mic, value)) Changed(); } }
    public bool Camera { get => _cam; internal set { if (Set(ref _cam, value)) Changed(); } }
    public bool Screen { get => _screen; internal set { if (Set(ref _screen, value)) Changed(); } }
    /// <summary>Muted inside the app itself (Discord self-mute via its RPC) – the Windows mic is still on.</summary>
    public bool SelfMuted { get => _selfMuted; set { if (Set(ref _selfMuted, value)) Changed(); } }
    private bool _canStream;
    /// <summary>Not streaming yet, but the island can start it (Discord with its stream keybind set).</summary>
    public bool CanStartStream { get => _canStream; set => Set(ref _canStream, value); }
    public bool IsDiscord => ExePath?.EndsWith("Discord.exe", StringComparison.OrdinalIgnoreCase) == true || Name == "Discord";
    public DateTime Since { get => _since; internal set => Set(ref _since, value); }
    public string Elapsed { get => _elapsed; internal set => Set(ref _elapsed, value); }

    /// <summary>"Bildschirm + Mikrofon" – what the app is using right now.</summary>
    public string Parts => string.Join(" + ", new[] { Screen ? "Bildschirm" : null, Mic ? (SelfMuted ? "stumm" : "Mikrofon") : null, Camera ? "Kamera" : null }.Where(p => p != null));

    /// <summary>Screen sharing is the most important (red), then the microphone (orange), then the camera (green).</summary>
    public Brush Accent => Screen || Mic && SelfMuted ? ScreenBrush : Mic ? MicBrush : CamBrush;

    private void Changed()
    {
        Raise(nameof(Parts));
        Raise(nameof(Accent));
    }

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Which apps use the microphone / camera or capture the screen – read once a second from the same privacy store
/// Windows uses for its own taskbar indicators. Screen capture shows up there for every app that uses
/// Windows Graphics Capture (Discord, Chrome, Teams, OBS, …). An app that does several things is ONE entry.
/// </summary>
public sealed class PrivacyService : ObservableObject
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private static readonly (string Cap, char Kind)[] Caps =
    {
        ("microphone", 'm'), ("webcam", 'c'), ("graphicsCaptureProgrammatic", 's'), ("graphicsCaptureWithoutBorder", 's')
    };
    /// <summary>Short captures (a screenshot, a thumbnail) never show up – sharing must last a moment.</summary>
    private static readonly TimeSpan ScreenDebounce = TimeSpan.FromSeconds(2.5);
    private static readonly string[] IgnoreScreen = { "explorer.exe", "screensketch", "snippingtool.exe", "peakpill.exe", "islandhub.exe", "windhawk.exe" };

    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, LiveApp> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _first = true;

    public ObservableCollection<LiveApp> Apps { get; } = new();

    /// <summary>An app started using something: (app, 'm' mic | 'c' camera | 's' screen).</summary>
    public event Action<LiveApp, char> Started;

    public bool InUse => Apps.Count > 0;
    public bool Single => Apps.Count == 1;
    public LiveApp Primary => Apps.FirstOrDefault();
    public bool AnyMic => Apps.Any(a => a.Mic);
    public bool AnyCamera => Apps.Any(a => a.Camera);
    public bool AnyScreen => Apps.Any(a => a.Screen);
    /// <summary>The app whose time the closed island shows (screen sharing first).</summary>
    public LiveApp Lead => Apps.FirstOrDefault(a => a.Screen) ?? Apps.FirstOrDefault(a => a.Mic) ?? Apps.FirstOrDefault();

    public void Start()
    {
        _t.Tick += (_, _) => Tick();
        _t.Start();
        Tick();
    }

    private sealed class Hit
    {
        public bool M, C, S;
        public DateTime? MSince, CSince, SSince; // only known for entries from the privacy store
        public string Exe, Pkg;
    }

    /// <summary>Apps whose threads show a running screen share / stream (Discord's "Go Live", browsers).</summary>
    private static readonly string[] StreamApps = { "discord", "discordptb", "discordcanary", "chrome", "msedge", "brave", "opera", "vivaldi" };
    private static readonly Regex StreamThreads = new(
        @"^(RsVideoSource|Soundshare\.|DesktopCapture|ScreenCapture|WgcCapture|desktop_capture)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Set from Discord's RPC: you are in a voice channel.</summary>
    public static volatile bool DiscordInVoice;

    private readonly Dictionary<string, DateTime> _firstSeen = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _streamers = new();
    private bool _busy, _demo;
    private int _tick;

    private async void Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            bool threads = _tick++ % 2 == 0; // thread names every 2 s, the rest every second
            var prev = _streamers;
            var (hits, streamers) = await Task.Run(() => Collect(threads ? null : prev));
            _streamers = streamers;
            if (!_demo) Apply(hits);
        }
        catch { /* never let a scan take the app down */ }
        finally { _busy = false; }
    }

    /// <summary>
    /// Three sources, merged per app: the active audio sessions on every microphone (always reliable), the
    /// threads of Discord / browsers while they stream, and Windows' privacy store (no longer written by every
    /// Windows version, but still the only source for the camera on some PCs).
    /// </summary>
    private static (Dictionary<string, Hit>, List<string>) Collect(List<string> knownStreamers)
    {
        var now = new Dictionary<string, Hit>(StringComparer.OrdinalIgnoreCase);
        Hit Mark(string key, string exe, string pkg)
        {
            if (!now.TryGetValue(key, out var h)) now[key] = h = new Hit { Exe = exe, Pkg = pkg };
            return h;
        }

        // 1) microphone: active capture sessions (what Windows' own mic indicator is based on)
        try
        {
            var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
            {
                try
                {
                    var mgr = dev.AudioSessionManager;
                    mgr.RefreshSessions();
                    var sessions = mgr.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                        int pid = (int)s.GetProcessID;
                        if (pid == 0 || pid == Environment.ProcessId) continue;
                        var path = ProcessPath(pid);
                        if (path != null) Mark(path, path, null).M = true;
                    }
                }
                catch { }
                finally { (dev as IDisposable)?.Dispose(); }
            }
            (en as IDisposable)?.Dispose();
        }
        catch { }

        // Discord call while you are muted in Discord: it may stop recording, but you are still in the call
        if (DiscordInVoice)
        {
            foreach (var p in Process.GetProcessesByName("Discord"))
            {
                using (p)
                {
                    var path = ProcessPath(p.Id);
                    if (path != null) { Mark(path, path, null).M = true; break; }
                }
            }
        }

        // 2) screen sharing / streaming: tell-tale threads that only exist while capturing
        var streamers = knownStreamers ?? new List<string>();
        if (knownStreamers == null)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (!StreamApps.Contains(p.ProcessName.ToLowerInvariant())) continue;
                        foreach (ProcessThread t in p.Threads)
                        {
                            var n = ThreadName(t.Id);
                            if (n == null || !StreamThreads.IsMatch(n)) continue;
                            var path = ProcessPath(p.Id);
                            if (path != null && !streamers.Contains(path, StringComparer.OrdinalIgnoreCase)) streamers.Add(path);
                            break;
                        }
                    }
                    catch { }
                }
            }
        }
        foreach (var path in streamers) Mark(path, path, null).S = true;

        // 3) Windows' privacy store
        foreach (var (cap, kind) in Caps)
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(Store + cap);
                if (root == null) continue;
                foreach (var (key, name, exe, pkg) in Entries(root))
                {
                    using (key)
                    {
                        if (key.GetValue("LastUsedTimeStart") is not long start || start <= 0) continue;
                        if (key.GetValue("LastUsedTimeStop") is not long stop || stop != 0) continue;
                        var since = DateTime.FromFileTime(start);
                        if (kind == 's')
                        {
                            var file = exe != null ? Path.GetFileName(exe).ToLowerInvariant() : (pkg ?? "").ToLowerInvariant();
                            if (IgnoreScreen.Any(i => file.StartsWith(i))) continue;
                            if (DateTime.Now - since < ScreenDebounce) continue;
                        }
                        // a crashed app can leave "in use" behind – only count desktop apps that still run
                        // – and exactly that file: Discord lives in a version folder (…\app-1.0.9268\Discord.exe); an
                        // update during a call leaves the old version "in use" forever while the new one runs (a friend's
                        // island showed Discord twice, "Mikrofon 3:32:30", 2026-11-16)
                        if (exe != null && !RunsAt(exe)) continue;

                        var h = Mark(exe ?? pkg, exe, pkg);
                        if (kind == 'm') { h.M = true; h.MSince = since; }
                        else if (kind == 'c') { h.C = true; h.CSince = since; }
                        else { h.S = true; h.SSince = since; }
                    }
                }
            }
            catch { /* store not readable */ }
        }
        return (now, streamers);
    }

    private void Apply(Dictionary<string, Hit> hits)
    {
        // when did each (app, kind) start? – first time seen, or the store's own timestamp
        DateTime SinceOf(string key, char kind, bool on, DateTime? stored)
        {
            var k = key + "|" + kind;
            if (!on) { _firstSeen.Remove(k); return DateTime.MaxValue; }
            if (!_firstSeen.TryGetValue(k, out var t)) _firstSeen[k] = t = stored ?? DateTime.Now;
            else if (stored is { } st && st < t) _firstSeen[k] = t = st;
            return t;
        }
        var now = new Dictionary<string, (bool m, bool c, bool s, DateTime since, string exe, string pkg)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, h) in hits)
        {
            var since = new[] { SinceOf(key, 'm', h.M, h.MSince), SinceOf(key, 'c', h.C, h.CSince), SinceOf(key, 's', h.S, h.SSince) }.Min();
            now[key] = (h.M, h.C, h.S, since, h.Exe, h.Pkg);
        }
        foreach (var k in _firstSeen.Keys.Where(k => !hits.ContainsKey(k[..k.LastIndexOf('|')])).ToList()) _firstSeen.Remove(k);

        bool changed = false;
        foreach (var gone in Apps.Where(a => !now.ContainsKey(a.Key)).ToList())
        {
            Apps.Remove(gone);
            changed = true;
        }
        foreach (var (key, v) in now)
        {
            var app = Apps.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            bool isNew = app == null;
            if (isNew)
            {
                if (!_known.TryGetValue(key, out app))
                {
                    app = new LiveApp(key, v.exe, v.pkg);
                    Describe(app);
                    _known[key] = app;
                }
                app.Mic = app.Camera = app.Screen = false;
                Apps.Add(app);
                changed = true;
            }
            bool newMic = v.m && !app.Mic, newCam = v.c && !app.Camera, newScreen = v.s && !app.Screen;
            if (app.Mic != v.m || app.Camera != v.c || app.Screen != v.s) changed = true;
            app.Mic = v.m;
            app.Camera = v.c;
            app.Screen = v.s;
            app.Since = v.since;
            var d = DateTime.Now - v.since;
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            app.Elapsed = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
            if (app.Mic && IsBrowser(app) && _tick % 3 == 0) app.Name = CallTitle(app) ?? app.BaseName;

            if (!_first)
            {
                if (newScreen) Started?.Invoke(app, 's');
                else if (newMic) Started?.Invoke(app, 'm');
                else if (newCam) Started?.Invoke(app, 'c');
            }
        }
        _first = false;

        if (changed)
        {
            // screen sharing first, then the oldest
            var order = Apps.OrderByDescending(a => a.Screen).ThenBy(a => a.Since).ToList();
            for (int i = 0; i < order.Count; i++)
            {
                int at = Apps.IndexOf(order[i]);
                if (at != i) Apps.Move(at, i);
            }
        }
        // always raise: the elapsed time in the closed island ticks every second
        RaiseAll(changed);
    }

    private void RaiseAll(bool changed = true)
    {
        Raise(nameof(InUse));
        if (changed)
        {
            Raise(nameof(Single));
            Raise(nameof(Primary));
            Raise(nameof(AnyMic));
            Raise(nameof(AnyCamera));
            Raise(nameof(AnyScreen));
        }
        Raise(nameof(Lead));
    }

    /// <summary>Render test only: shows made-up entries instead of the real ones.</summary>
    public void Demo(params (string Exe, bool Mic, bool Cam, bool Screen, int Seconds)[] items)
    {
        _demo = true;
        _t.Stop();
        Apps.Clear();
        foreach (var (exe, mic, cam, screen, secs) in items)
        {
            var a = new LiveApp(exe.Replace('\\', '#'), exe, null);
            Describe(a);
            a.Mic = mic; a.Camera = cam; a.Screen = screen;
            a.Since = DateTime.Now.AddSeconds(-secs);
            a.Elapsed = TimeSpan.FromSeconds(secs).ToString(@"m\:ss");
            Apps.Add(a);
        }
        RaiseAll();
    }

    public void EndDemo()
    {
        _demo = false;
        Apps.Clear();
        RaiseAll();
        _t.Start();
    }

    /// <summary>For the Started hint in the render test.</summary>
    public void DemoStarted(char kind) { if (Apps.Count > 0) Started?.Invoke(Apps[0], kind); }

    private static IEnumerable<(RegistryKey Key, string Name, string Exe, string Package)> Entries(RegistryKey root)
    {
        foreach (var sub in root.GetSubKeyNames())
        {
            if (sub == "NonPackaged")
            {
                using var np = root.OpenSubKey(sub);
                if (np == null) continue;
                foreach (var exe in np.GetSubKeyNames())
                {
                    var k = np.OpenSubKey(exe);
                    if (k != null) yield return (k, exe, exe.Replace('#', '\\'), null);
                }
            }
            else
            {
                var k = root.OpenSubKey(sub);
                if (k != null) yield return (k, sub, null, sub);
            }
        }
    }

    // ------------------------------------------------------------------ app name / icon / processes

    private static void Describe(LiveApp app)
    {
        var exe = app.ExePath ?? PackagedExe(app.Package);
        if (exe != null && app.ExePath == null) app.ExePath = exe;
        string name = null;
        if (exe != null && File.Exists(exe))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim() : vi.ProductName?.Trim();
            }
            catch { }
            app.Icon = ShellIcon.Get(exe);
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 28)
        {
            name = app.Package != null
                ? app.Package.Split('_')[0].Split('.').Last()
                : Path.GetFileNameWithoutExtension(exe ?? app.Key);
            if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
        }
        if (name.StartsWith("MSTeams", StringComparison.OrdinalIgnoreCase)) name = "Teams";
        app.Name = app.BaseName = name;
    }

    private static readonly string[] BrowserExe = { "chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "vivaldi.exe", "firefox.exe" };
    private static bool IsBrowser(LiveApp a) => a.ExePath != null && BrowserExe.Contains(Path.GetFileName(a.ExePath).ToLowerInvariant());

    /// <summary>A browser in a call: "Google Meet · Weekly Sync" / "Teams · …" / "Zoom · …" from its window titles.</summary>
    private static string CallTitle(LiveApp app)
    {
        foreach (var w in Windows(app))
        {
            if (!w.Visible || w.Title.Length == 0) continue;
            var t = Regex.Replace(w.Title, @"\s+[-–—]\s+(Google Chrome|Microsoft​? Edge|Brave|Opera|Vivaldi|Mozilla Firefox).*$", "").Trim();
            t = Regex.Replace(t, @"\s+und \d+ weitere.*$|\s+and \d+ more.*$", "");
            var meet = Regex.Match(t, @"^(?:Google )?Meet\s*[-–:]\s*(.+)$");
            if (meet.Success) return "Google Meet · " + meet.Groups[1].Value.Trim();
            if (t.StartsWith("Meet", StringComparison.Ordinal) && t.Length <= 6) return "Google Meet";
            var teams = Regex.Match(t, @"^(.+?)\s*\|\s*Microsoft Teams");
            if (teams.Success) return "Teams · " + teams.Groups[1].Value.Trim();
            if (t.Contains("Zoom", StringComparison.OrdinalIgnoreCase) && (t.Contains("Meeting", StringComparison.OrdinalIgnoreCase) || t.Contains("Besprechung", StringComparison.OrdinalIgnoreCase)))
                return "Zoom · " + t;
        }
        return null;
    }

    /// <summary>The running exe of a Store app (for its icon and windows).</summary>
    private static string PackagedExe(string package)
    {
        if (package == null) return null;
        var marker = $"\\WindowsApps\\{package.Split('_')[0]}_";
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var path = ProcessPath(p.Id);
                if (path != null && path.Contains(marker, StringComparison.OrdinalIgnoreCase)) return path;
            }
        }
        return null;
    }

    private static HashSet<uint> ProcessIds(LiveApp app)
    {
        var ids = new HashSet<uint>();
        if (app.ExePath == null) return ids;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.ExePath)))
        {
            using (p)
            {
                var path = ProcessPath(p.Id);
                if (path == null || path.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase)) ids.Add((uint)p.Id);
            }
        }
        return ids;
    }

    /// <summary>A process runs from exactly this file. If no path can be read (an elevated app), the name has to do.</summary>
    private static bool RunsAt(string exe)
    {
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        try
        {
            if (procs.Length == 0) return false;
            bool anyPath = false;
            foreach (var p in procs)
            {
                var path = ProcessPath(p.Id);
                if (path == null) continue;
                anyPath = true;
                if (path.Equals(exe, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return !anyPath;
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private static string ProcessPath(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Top-level windows of the app, biggest (main window) first.</summary>
    private static List<(IntPtr Hwnd, bool Visible, int Area, string Title)> Windows(LiveApp app)
    {
        var ids = ProcessIds(app);
        var list = new List<(IntPtr, bool, int, string)>();
        if (ids.Count == 0) return list;
        EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!ids.Contains(pid)) return true;
            int len = GetWindowTextLength(h);
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            Native.GetWindowRect(h, out var r);
            list.Add((h, IsWindowVisible(h), Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top), sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Brings the app's main window to the front (restores it when minimized).</summary>
    public bool Open(LiveApp app)
    {
        var main = Windows(app)
            .Where(w => w.Title.Length > 0 && GetWindow(w.Hwnd, 4 /* GW_OWNER */) == IntPtr.Zero && (w.Visible || IsIconic(w.Hwnd)))
            .OrderByDescending(w => w.Visible && !IsIconic(w.Hwnd) ? w.Area : 1)
            .Select(w => w.Hwnd)
            .FirstOrDefault();
        if (main == IntPtr.Zero)
        {
            // not running a window right now (e.g. only in the tray) → start / activate it the normal way
            if (app.ExePath == null || !File.Exists(app.ExePath)) return false;
            try { Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true }); return true; } catch { return false; }
        }
        return Focus(main);
    }

    public static bool Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        // Windows only lets the foreground app change the foreground – a short Alt tap unlocks it
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        return Native.SetForegroundWindow(h);
    }

    private static readonly Regex StopNames = new(
        @"stop sharing|stop share|stop presenting|stop streaming|stop stream|end stream|stop screen ?share|" +
        @"freigabe beenden|freigabe stoppen|teilen beenden|teilen stoppen|stream beenden|übertragung beenden|" +
        @"präsentation beenden|präsentieren beenden|bildschirmübertragung beenden|bildschirmfreigabe beenden",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Ends screen sharing by pressing the app's own "Stop sharing" button (Chrome/Edge sharing bar, Discord, Zoom,
    /// Teams, …) via UI Automation. Small windows (sharing bars) are searched first. False if no such button exists.
    /// </summary>
    public Task<bool> StopSharingAsync(LiveApp app) => Task.Run(() =>
    {
        var wins = Windows(app).Where(w => w.Visible).OrderBy(w => w.Area).ToList();
        var deadline = DateTime.Now.AddSeconds(6);
        foreach (var w in wins)
        {
            if (DateTime.Now > deadline) break;
            try
            {
                var root = AutomationElement.FromHandle(w.Hwnd);
                var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement b in buttons)
                {
                    string name;
                    try { name = b.Current.Name; } catch { continue; }
                    if (string.IsNullOrEmpty(name) || !StopNames.IsMatch(name)) continue;
                    if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); return true; }
                }
            }
            catch { /* window closed or not accessible */ }
        }
        return false;
    });

    // ------------------------------------------------------------------ native

    private static string ThreadName(int tid)
    {
        var h = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, (uint)tid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (GetThreadDescription(h, out var d) < 0 || d == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(d); } finally { LocalFree(d); }
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr h, out IntPtr description);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
