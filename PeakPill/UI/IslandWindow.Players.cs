using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Players besides Spotify (user wish 2026-11-16: Apple Music, SoundCloud, YouTube Music). Play, skip, cover and
/// position come from Windows for all of them. On top: the player's own volume (the app's mixer slider – for a
/// browser that is the whole browser), the service named instead of just the browser, and one click to its window /
/// tab. Queue and likes need the services' own logins (Apple and YouTube Music have no public way for that on Windows).
/// </summary>
public partial class IslandWindow
{
    private static readonly (string Service, string TitleMark)[] WebPlayers =
        { ("YouTube Music", "YouTube Music"), ("SoundCloud", "SoundCloud"), ("Apple Music", "Apple Music"), ("Deezer", "Deezer"), ("YouTube", "YouTube") };

    private static readonly Dictionary<string, string> BrowserProcess = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Google Chrome"] = "chrome", ["Chrome"] = "chrome", ["Microsoft Edge"] = "msedge", ["Firefox"] = "firefox",
        ["Brave"] = "brave", ["Opera"] = "opera", ["Vivaldi"] = "vivaldi"
    };

    private string _serviceFor, _service;

    /// <summary>Which music service plays in the browser – read from the browser windows' titles (the playing tab is usually in front).</summary>
    private string PlayerService()
    {
        var app = H.Media.AppName ?? "";
        string key = app + "|" + H.Media.Title;
        if (key == _serviceFor) return _service;
        _serviceFor = key;
        _service = null;
        if (!BrowserProcess.TryGetValue(app, out var exe)) return _service = app == "App" ? "Apple Music" : null;
        foreach (var title in WindowTitles(exe))
        {
            var hit = WebPlayers.FirstOrDefault(w => title.Contains(w.TitleMark, StringComparison.OrdinalIgnoreCase));
            if (hit.Service != null) return _service = hit.Service;
        }
        return null;
    }

    /// <summary>The process name whose mixer slider is the player's volume.</summary>
    private static string PlayerProcess(string app) => app switch
    {
        "Spotify" => "Spotify",
        "Apple Music" => "AppleMusic",
        "VLC" => "vlc",
        _ => BrowserProcess.TryGetValue(app ?? "", out var exe) ? exe : null
    };

    /// <summary>A click on "Artist · Service": that player comes to the front (its tab, when it plays in a browser).</summary>
    private void MusicSource_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var app = H.Media.AppName ?? "";
        var service = PlayerService();
        var exe = PlayerProcess(app);
        Task.Run(() =>
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(exe ?? app))
                    using (p)
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        if (service != null && BrowserProcess.ContainsValue(exe ?? "")) SelectTab(p.MainWindowHandle, service);
                        PrivacyService.Focus(p.MainWindowHandle);
                        return;
                    }
            }
            catch { }
        });
        Collapse();
    }

    /// <summary>Selects the browser tab whose name contains <paramref name="mark"/> (UI Automation, off the UI thread).</summary>
    private static void SelectTab(IntPtr browser, string mark)
    {
        try
        {
            var win = AutomationElement.FromHandle(browser);
            var tabs = win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            foreach (AutomationElement t in tabs)
            {
                if (!(t.Current.Name ?? "").Contains(mark, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) { ((SelectionItemPattern)p).Select(); return; }
                if (t.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return; }
            }
        }
        catch { }
    }

    private static List<string> WindowTitles(string exe)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(exe)) using (p) pids.Add((uint)p.Id);
        var titles = new List<string>();
        if (pids.Count == 0) return titles;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(512);
            if (GetWindowText(h, sb, sb.Capacity) > 0) titles.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
}
