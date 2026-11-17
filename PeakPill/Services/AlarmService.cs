using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>
/// The alarm clock on the Timer page (user wish 2026-11-15: "Timer | Wecker" switch, the same wheels set a time of
/// day). One alarm; it rings once at the next occurrence of that time and then turns itself off (snooze moves it).
/// Saved in the settings, so it survives a restart of PeakPill.
/// </summary>
public sealed class AlarmService : ObservableObject
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _at;

    /// <summary>When it rings next (null = no alarm set).</summary>
    public DateTime? At { get => _at; private set { if (Set(ref _at, value)) { Raise(nameof(IsSet)); Raise(nameof(UntilText)); } } }
    public bool IsSet => At != null;
    public event Action Rang;

    public void Start()
    {
        var saved = App.Settings.AlarmAt;
        // missed while the PC was off for more than half an hour: forget it (an old alarm must not ring at noon)
        if (saved is { } s && DateTime.Now - s > TimeSpan.FromMinutes(30)) saved = null;
        At = saved;
        _t.Tick += (_, _) => Tick();
        _t.Start();
    }

    private void Tick()
    {
        if (At is not { } at) return;
        Raise(nameof(UntilText));
        if (DateTime.Now < at) return;
        At = null;
        Save();
        Rang?.Invoke();
    }

    /// <summary>Rings at the next hh:mm (today if still ahead, else tomorrow).</summary>
    public void SetTime(int hour, int minute)
    {
        var now = DateTime.Now;
        var at = now.Date.AddHours(hour).AddMinutes(minute);
        if (at <= now) at = at.AddDays(1);
        At = at;
        Save();
    }

    public void Snooze(int minutes = 5)
    {
        At = DateTime.Now.AddMinutes(minutes);
        Save();
    }

    public void Clear()
    {
        At = null;
        Save();
    }

    private void Save()
    {
        App.Settings.AlarmAt = At;
        App.SaveSettings();
    }

    /// <summary>"klingelt in 6 Std 45 Min", "klingelt in 12 Min", "klingelt gleich".</summary>
    public string UntilText
    {
        get
        {
            if (At is not { } at) return "";
            var d = at - DateTime.Now;
            if (d.TotalMinutes < 1) return L.T("klingelt gleich");
            int h = (int)d.TotalHours, m = (int)Math.Ceiling(d.TotalMinutes - h * 60);
            if (m == 60) { h++; m = 0; }
            return h > 0 ? (m > 0 ? L.T($"klingelt in {h} Std {m} Min") : L.T($"klingelt in {h} Std")) : L.T($"klingelt in {m} Min");
        }
    }
}
