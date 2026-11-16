using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>
/// "Make room" (approved 2026-11-14): the island slides out of the way while
///  • the browser's address/search bar has the focus (browser on the MAIN screen), or
///  • a YouTube video plays in FULLSCREEN on the main screen (not in a normal window).
/// Found via UI Automation and window titles – no browser extension. Runs on its own thread (UIA calls into other
/// processes must never block the island's UI thread).
/// </summary>
public sealed class MakeRoomService : ObservableObject
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "helium", "arc", "chromium", "thorium",
        "zen", "floorp", "librewolf", "waterfox", "yandex"
    };

    private bool _addressBar, _youTube, _browserFront;

    /// <summary>
    /// A browser window is in front (main screen). A fullscreen browser is never a "game": right after leaving a
    /// fullscreen video the game check is still a moment behind and briefly showed the CPU/RAM bar.
    /// </summary>
    public bool BrowserInFront { get => _browserFront; private set => Set(ref _browserFront, value); }
    private Thread _thread;
    private volatile bool _stop;

    /// <summary>The address / search bar of a browser on the main screen has the keyboard focus.</summary>
    public bool AddressBar { get => _addressBar; private set { if (Set(ref _addressBar, value)) Raise(nameof(Active)); } }
    /// <summary>A YouTube video fills the main screen (browser fullscreen).</summary>
    public bool YouTubeFullscreen { get => _youTube; private set { if (Set(ref _youTube, value)) Raise(nameof(Active)); } }
    public bool Active => AddressBar || YouTubeFullscreen;

    /// <summary>The monitor the island is on (set by the island; empty = the main screen).</summary>
    public static System.Drawing.Rectangle IslandBounds { get; set; }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "PeakPill make-room" };
        _thread.Start();
    }

    public void Stop() => _stop = true;

#if DEBUG
    /// <summary>Visual test only: pretend the address bar is focused (the detection thread is stopped first).</summary>
    public void ForceForTest(bool bar) { _stop = true; _forced = true; AddressBar = bar; }
#endif
    private volatile bool _forced;
#if DEBUG
#endif

    /// <summary>
    /// A web page in the browser wants a verification code (the focused field is a code field). Raised once per page
    /// (window + title) every few minutes, on the UI thread, with the browser window.
    /// </summary>
    public event Action<IntPtr, string> CodeWanted;
    private readonly Dictionary<string, DateTime> _codeSeen = new();

    private void Loop()
    {
        while (!_stop)
        {
            bool bar = false, yt = false, front = false;
            int wait = 400;
            try
            {
                var h = Native.GetForegroundWindow();
                if (h != IntPtr.Zero && IsBrowser(h, out var pid))
                {
                    var title = TitleOf(h);
                    var focused = Focused(pid);
                    if (OnPrimary(h, out bool fills))
                    {
                        front = true;
                        wait = 200; // a browser is in front: look closely
                        yt = fills && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase);
                        if (!yt) bar = focused != null && IsAddressBar(focused);
                    }
                    if (focused != null && !bar && CodeWanted != null && IsCodeField(focused, title))
                    {
                        var key = $"{h}|{title}";
                        if (!_codeSeen.TryGetValue(key, out var at) || DateTime.Now - at > TimeSpan.FromMinutes(3))
                        {
                            _codeSeen[key] = DateTime.Now;
                            Ui.Run(() => CodeWanted?.Invoke(h, title));
                        }
                    }
                }
            }
            catch { /* UIA can throw while windows come and go */ }
            Ui.Run(() => { if (_forced) return; BrowserInFront = front; AddressBar = bar; YouTubeFullscreen = yt; });
            Thread.Sleep(wait);
        }
    }

    /// <summary>The focused text field inside this browser (null = something else has the focus).</summary>
    private static AutomationElement Focused(int browserPid)
    {
        var f = AutomationElement.FocusedElement;
        if (f == null) return null;
        var c = f.Current;
        if (c.ProcessId != browserPid || c.ControlType != ControlType.Edit && c.ControlType != ControlType.ComboBox) return null;
        return f;
    }

    // ---------- verification codes ----------

    // the field itself clearly asks for a code that comes by mail / SMS
    private static readonly System.Text.RegularExpressions.Regex StrongWords = new(
        @"bestätigungscode|verifizierungscode|verification code|security code|sicherheitscode|einmal.?code|einmalpass|one[- ]time|\botp\b|\b2fa\b|code eingeben|enter (the |your )?code|gib den code|anmeldecode|login code|sign-in code|6-digit|6-stellig",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    // only "Code" in the field – counts when the page is about signing in / verifying
    private static readonly System.Text.RegularExpressions.Regex CodeWords = new(
        @"\bcode\b|\bcodes\b|bestätigung|verifizier|verification|verify",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LoginPage = new(
        @"anmeld|sign.?in|log.?in|konto|account|bestätig|verif|sicherheit|security|2-step|two-step|zwei.?schritt|authenti",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    // codes that are not sent by mail: card checks, postcodes, vouchers, QR …
    private static readonly System.Text.RegularExpressions.Regex NotMailCodes = new(
        @"cvc|cvv|karte|card|kredit|post|zip|plz|gutschein|rabatt|promo|coupon|voucher|discount|qr|country|länder|vorwahl|area",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MailSites = new(
        @"gmail|posteingang|inbox|outlook|gmx|web\.de|t-online|proton|yahoo mail|icloud mail",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A field that wants a code: its name/label says so ("Code eingeben", "Bestätigungscode", "verification code"),
    /// or – for the rows of single-digit boxes – a narrow empty field on a page whose title is about verifying.
    /// </summary>
    private static bool IsCodeField(AutomationElement f, string title)
    {
        var c = f.Current;
        if (c.ControlType != ControlType.Edit || c.IsPassword) return false;
        if (MailSites.IsMatch(title)) return false; // already in the mails
        var label = $"{c.Name} {c.AutomationId} {c.HelpText}";
        if (NotMailCodes.IsMatch(label)) return false;
        if (StrongWords.IsMatch(label)) return true;
        if (CodeWords.IsMatch(label) && LoginPage.IsMatch(title)) return true;
        bool narrow = c.BoundingRectangle.Width is > 0 and < 80;
        return narrow && string.IsNullOrWhiteSpace(c.Name) && (StrongWords.IsMatch(title) || CodeWords.IsMatch(title) && LoginPage.IsMatch(title)) && !NotMailCodes.IsMatch(title);
    }

    /// <summary>
    /// Brings the mails up in this browser: an open mail tab is selected (Gmail, Outlook, GMX …), otherwise the
    /// mail address opens as a new tab. Runs off the UI thread.
    /// </summary>
    public static void OpenMail(IntPtr browser, string url) => Task.Run(() =>
    {
        try
        {
            if (browser != IntPtr.Zero && AutomationElement.FromHandle(browser) is { } win)
            {
                var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";
                var tabs = win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                foreach (AutomationElement t in tabs)
                {
                    var name = t.Current.Name ?? "";
                    if (!MailSites.IsMatch(name) && !(host.Length > 0 && name.Contains(host.Split('.')[0], StringComparison.OrdinalIgnoreCase))) continue;
                    if (t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) { ((SelectionItemPattern)p).Select(); return; }
                    if (t.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return; }
                }
            }
        }
        catch { /* tabs not readable – open the address instead */ }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    });

    private static bool IsAddressBar(AutomationElement f)
    {
        var c = f.Current;
        if (c.ClassName == "OmniboxViewViews" || c.AutomationId == "urlbar-input") return true;
        var name = c.Name ?? "";
        return name.Contains("Adress", StringComparison.OrdinalIgnoreCase) || name.Contains("Address", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Suchleiste", StringComparison.OrdinalIgnoreCase) || name.Contains("Search or enter", StringComparison.OrdinalIgnoreCase);
    }

    // the window in front rarely changes: remember the last answer instead of opening the process 2–5 times a second
    private static IntPtr _lastWindow;
    private static int _lastPid;
    private static bool _lastIsBrowser;

    private static bool IsBrowser(IntPtr h, out int pid)
    {
        Native.GetWindowThreadProcessId(h, out var p);
        pid = (int)p;
        if (h == _lastWindow && pid == _lastPid) return _lastIsBrowser;
        bool browser;
        try
        {
            using var proc = Process.GetProcessById(pid);
            browser = Browsers.Contains(proc.ProcessName);
        }
        catch { browser = false; }
        (_lastWindow, _lastPid, _lastIsBrowser) = (h, pid, browser);
        return browser;
    }

    /// <summary>On the main screen? <paramref name="fills"/> = covers it completely without a title bar (fullscreen).</summary>
    private static bool OnPrimary(IntPtr h, out bool fills)
    {
        fills = false;
        var mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        // the screen the island sits on (it can be moved to another monitor since 2026-11-16)
        var ib = IslandBounds;
        bool islandScreen = ib.IsEmpty
            ? (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0
            : mi.rcMonitor.Left == ib.Left && mi.rcMonitor.Top == ib.Top && mi.rcMonitor.Right == ib.Right && mi.rcMonitor.Bottom == ib.Bottom;
        if (!islandScreen) return false;
        if (Native.GetWindowRect(h, out var r))
        {
            var m = mi.rcMonitor;
            bool covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
            bool framed = (Native.GetWindowLongPtr(h, -16).ToInt64() & 0x00C00000) == 0x00C00000; // WS_CAPTION
            fills = covers && !framed;
        }
        return true;
    }

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
}
