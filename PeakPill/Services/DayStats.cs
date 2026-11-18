using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>One day in numbers (focus minutes, finished to-dos, Claude's finished tasks).</summary>
public sealed class DayEntry
{
    public double FocusMinutes { get; set; }
    public int TodosDone { get; set; }
    public int ClaudeDone { get; set; }
    public bool MorningShown { get; set; }
    public bool EveningShown { get; set; }
}

/// <summary>
/// The workday in numbers (2026-11-18): focus time per day for the week bars, and what the evening moment tells.
/// Stays on this PC (daystats.json), the last 60 days.
/// </summary>
public sealed class DayStatsService : ObservableObject
{
    public Dictionary<string, DayEntry> Days { get; private set; } = new();

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    public DayEntry Today => Get(DateTime.Today);

    public DayEntry Get(DateTime d)
    {
        if (!Days.TryGetValue(Key(d), out var e)) Days[Key(d)] = e = new DayEntry();
        return e;
    }

    public void Load()
    {
        Days = Storage.Load<Dictionary<string, DayEntry>>("daystats.json");
        foreach (var old in Days.Keys.Where(k => DateTime.TryParse(k, out var d) && DateTime.Today - d > TimeSpan.FromDays(60)).ToList())
            Days.Remove(old);
    }

    public void Save()
    {
        try { Storage.Save("daystats.json", Days); } catch { }
        Raise(nameof(Days));
    }

    public void AddFocus(double minutes) { if (minutes < 0.5) return; Today.FocusMinutes += minutes; Save(); }
    public void TodoDone() { Today.TodosDone++; Save(); }
    public void ClaudeDone() { Today.ClaudeDone++; Save(); }

    /// <summary>Monday … Sunday of this week: focus minutes.</summary>
    public double[] FocusWeek()
    {
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        return Enumerable.Range(0, 7).Select(i => Days.TryGetValue(Key(monday.AddDays(i)), out var e) ? e.FocusMinutes : 0).ToArray();
    }
}

/// <summary>
/// Screen time (2026-11-18): which app was in front, and for how long – counted only while someone is at the PC
/// (input in the last 2 minutes, or a video / game in full screen). Stays on this PC (usage.json), the last 14 days.
/// Also counts the minutes without a break for the eye break.
/// </summary>
public sealed class UsageService : ObservableObject
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(5) };
    private Dictionary<string, Dictionary<string, double>> _days = new();
    private readonly Dictionary<int, string> _names = new();
    private DateTime _saved = DateTime.Now;

    /// <summary>Seconds at the PC without a break of 2+ minutes (for the eye break).</summary>
    public double ActiveStreak { get; private set; }
    public event Action Tick;

    public void Start()
    {
        _days = Storage.Load<Dictionary<string, Dictionary<string, double>>>("usage.json");
        foreach (var old in _days.Keys.Where(k => DateTime.TryParse(k, out var d) && DateTime.Today - d > TimeSpan.FromDays(14)).ToList())
            _days.Remove(old);
        _t.Tick += (_, _) => Step();
        _t.Start();
    }

    /// <summary>The eye break was shown (or skipped) – the count starts again.</summary>
    public void ResetStreak() => ActiveStreak = 0;

    private void Step()
    {
        double idle = IdleSeconds();
        bool fullscreen = App.Hub?.Game?.IsFullscreen == true;
        bool here = idle < 120 || fullscreen;
        if (idle >= 120) ActiveStreak = 0; else ActiveStreak += 5;
        if (here && App.Settings.ScreenTime)
        {
            var name = ForegroundName();
            if (name != null)
            {
                var key = DateTime.Today.ToString("yyyy-MM-dd");
                if (!_days.TryGetValue(key, out var day)) _days[key] = day = new();
                day[name] = day.GetValueOrDefault(name) + 5;
                if (DateTime.Now - _saved > TimeSpan.FromMinutes(1)) { _saved = DateTime.Now; try { Storage.Save("usage.json", _days); } catch { } }
            }
        }
        Tick?.Invoke();
    }

    /// <summary>Today's apps, longest first (name, seconds).</summary>
    public List<(string Name, double Seconds)> Today()
    {
        var key = DateTime.Today.ToString("yyyy-MM-dd");
        return _days.TryGetValue(key, out var day) ? day.OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList() : new();
    }

    public double TodayTotal => Today().Sum(p => p.Seconds);

#if DEBUG
    /// <summary>Films: a made-up day (no real app names).</summary>
    public void DemoSeed()
    {
        _days[DateTime.Today.ToString("yyyy-MM-dd")] = new() { ["Code-Editor"] = 7500, ["Browser"] = 4800, ["Spiele"] = 3120, ["Chat"] = 1860, ["Musik"] = 600 };
    }
#endif

    /// <summary>The app in front by its friendly name ("Google Chrome", "Visual Studio Code") – PeakPill itself and the
    /// desktop don't count.</summary>
    private string ForegroundName()
    {
        try
        {
            var w = GetForegroundWindow();
            if (w == IntPtr.Zero) return null;
            GetWindowThreadProcessId(w, out var pid);
            if (pid == Environment.ProcessId) return null;
            if (_names.TryGetValue((int)pid, out var known)) return known;
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName;
            if (name is "explorer" or "ShellExperienceHost" or "SearchHost" or "StartMenuExperienceHost" or "LockApp") return Cache(pid, null);
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) name = desc.Trim();
            }
            catch { }
            return Cache(pid, name);
        }
        catch { return null; }
    }

    private string Cache(uint pid, string name)
    {
        if (_names.Count > 300) _names.Clear();
        _names[(int)pid] = name;
        return name;
    }

    private static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (unchecked((uint)Environment.TickCount) - info.dwTime) / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
