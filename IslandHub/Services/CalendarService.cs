using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using IslandHub.Core;

namespace IslandHub.Services;

/// <summary>One appointment as the island shows it.</summary>
public sealed class CalendarEvent
{
    public string Title { get; init; } = "";
    public string Location { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool AllDay { get; init; }
    /// <summary>Colour of the calendar it comes from (one per link).</summary>
    public System.Windows.Media.Brush Color { get; init; }
    public string When => AllDay ? "ganztags" : Start.Date == DateTime.Today ? Start.ToString("HH:mm") : Start.ToString("ddd HH:mm", CultureInfo.GetCultureInfo("de-DE"));
    public string Until => AllDay ? "" : End.ToString("HH:mm");
}

/// <summary>
/// The calendar (user wish 2026-11-16) without any login: Google ("Privatadresse im iCal-Format") and Outlook
/// ("Kalender veröffentlichen → ICS") give a secret link – IslandHub reads it every 15 minutes. Simple repeats
/// (daily / weekly / monthly / yearly, with interval, count, until, weekdays and exceptions) are expanded.
/// </summary>
public sealed class CalendarService : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string[] Palette = { "#FF0A84FF", "#FFFF9F0A", "#FF30D158", "#FFBF5AF2", "#FFFF375F" };
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _told = new();
    private List<CalendarEvent> _all = new();
    private string _status = "";

    /// <summary>Today's and tomorrow's appointments still to come (or running), soonest first.</summary>
    public ObservableCollection<CalendarEvent> Upcoming { get; } = new();
    public bool HasLinks => App.Settings.CalendarUrls.Any(u => !string.IsNullOrWhiteSpace(u));
    public string Status { get => _status; private set => Set(ref _status, value); }
    public CalendarEvent Next => Upcoming.FirstOrDefault(e => !e.AllDay && e.Start > DateTime.Now);

    /// <summary>An appointment starts in 10 minutes (once per appointment).</summary>
    public event Action<CalendarEvent> Soon;

    public void Start()
    {
        _t.Tick += async (_, _) => await RefreshAsync();
        _t.Start();
        _soon.Tick += (_, _) => Tick();
        _soon.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var links = App.Settings.CalendarUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (links.Count == 0) { _all = new(); Status = "Füge in den Einstellungen einen Kalender-Link hinzu."; Publish(); return; }
        var list = new List<CalendarEvent>();
        int ok = 0;
        for (int i = 0; i < links.Count; i++)
        {
            try
            {
                var url = links[i].Trim();
                if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
                var text = await Http.GetStringAsync(url);
                var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Palette[i % Palette.Length]));
                brush.Freeze();
                list.AddRange(Parse(text, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(8), brush));
                ok++;
            }
            catch (Exception ex) { App.Log(ex); }
        }
        _all = list.OrderBy(e => e.Start).ToList();
        Status = ok == 0 ? "Kalender gerade nicht erreichbar." : "";
        Publish();
    }

    private void Publish()
    {
        var now = DateTime.Now;
        var keep = _all.Where(e => e.End > now && e.Start < DateTime.Today.AddDays(2)).Take(6).ToList();
        Upcoming.Clear();
        foreach (var e in keep) Upcoming.Add(e);
        Raise(nameof(HasLinks));
        Raise(nameof(Next));
    }

    private void Tick()
    {
        if (_all.Count == 0) return;
        var now = DateTime.Now;
        foreach (var e in _all)
        {
            if (e.AllDay) continue;
            var left = e.Start - now;
            string key = $"{e.Title}|{e.Start:O}";
            if (left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(10) && _told.Add(key)) Soon?.Invoke(e);
        }
        // the list drops what has ended
        if (Upcoming.Any(e => e.End <= now)) Publish();
    }

#if DEBUG
    /// <summary>Test only: show these appointments (nothing is fetched or saved).</summary>
    public void Demo(IEnumerable<CalendarEvent> list) { _t.Stop(); _all = list.OrderBy(e => e.Start).ToList(); Status = ""; Publish(); }
#endif

    // ---------- iCalendar (RFC 5545), the parts calendars really use ----------

    public static List<CalendarEvent> Parse(string ics, DateTime from, DateTime to, System.Windows.Media.Brush color)
    {
        var lines = Unfold(ics);
        var result = new List<CalendarEvent>();
        Dictionary<string, (string Params, string Value)> ev = null;
        var exdates = new List<DateTime>();
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { ev = new(StringComparer.OrdinalIgnoreCase); exdates.Clear(); continue; }
            if (line == "END:VEVENT" && ev != null)
            {
                try { Expand(ev, exdates, from, to, color, result); } catch { }
                ev = null;
                continue;
            }
            if (ev == null) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            var value = line[(colon + 1)..];
            int semi = head.IndexOf(';');
            var name = semi < 0 ? head : head[..semi];
            var pars = semi < 0 ? "" : head[(semi + 1)..];
            if (name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var v in value.Split(',')) if (ParseDate(pars, v, out var d, out _)) exdates.Add(d);
                continue;
            }
            ev[name] = (pars, value);
        }
        return result;
    }

    private static void Expand(Dictionary<string, (string Params, string Value)> ev, List<DateTime> ex, DateTime from, DateTime to,
                               System.Windows.Media.Brush color, List<CalendarEvent> result)
    {
        if (!ev.TryGetValue("DTSTART", out var ds) || !ParseDate(ds.Params, ds.Value, out var start, out bool allDay)) return;
        if (ev.TryGetValue("STATUS", out var st) && st.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
        DateTime end;
        if (ev.TryGetValue("DTEND", out var de) && ParseDate(de.Params, de.Value, out var e2, out _)) end = e2;
        else if (ev.TryGetValue("DURATION", out var du) && TryDuration(du.Value, out var span)) end = start + span;
        else end = allDay ? start.AddDays(1) : start.AddHours(1);
        var length = end - start;
        string title = Unescape(ev.TryGetValue("SUMMARY", out var s) ? s.Value : "Termin");
        string loc = Unescape(ev.TryGetValue("LOCATION", out var l) ? l.Value : "");

        void Add(DateTime at)
        {
            if (ex.Any(x => Math.Abs((x - at).TotalMinutes) < 1)) return;
            if (at + length <= from || at >= to) return;
            result.Add(new CalendarEvent { Title = title, Location = loc, Start = at, End = at + length, AllDay = allDay, Color = color });
        }

        if (!ev.TryGetValue("RRULE", out var rr)) { Add(start); return; }
        var rule = rr.Value.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        string freq = rule.GetValueOrDefault("FREQ", "");
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL", "1"), out var iv) ? Math.Max(1, iv) : 1;
        int count = int.TryParse(rule.GetValueOrDefault("COUNT", "0"), out var c) ? c : 0;
        DateTime until = DateTime.MaxValue;
        if (rule.TryGetValue("UNTIL", out var u) && ParseDate("", u, out var ud, out bool udDay)) until = udDay ? ud.AddDays(1).AddTicks(-1) : ud; // a date-only end includes that day
        var days = rule.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(x => Regex.Match(x, "(MO|TU|WE|TH|FR|SA|SU)$").Value).Where(x => x.Length > 0).Select(Day).ToList()
            : new List<DayOfWeek>();
        int n = 0;
        var cur = start;
        for (int guard = 0; guard < 5000 && cur < to && cur <= until; guard++)
        {
            if (freq == "WEEKLY" && days.Count > 0)
            {
                // every listed weekday of this week
                var weekStart = cur.Date.AddDays(-(((int)cur.DayOfWeek + 6) % 7));
                foreach (var d in days.OrderBy(d => ((int)d + 6) % 7))
                {
                    var at = weekStart.AddDays(((int)d + 6) % 7) + start.TimeOfDay;
                    if (at < start || at > until) continue;
                    if (count > 0 && n >= count) return;
                    n++;
                    Add(at);
                }
                cur = cur.AddDays(7 * interval);
                continue;
            }
            if (count > 0 && n >= count) return;
            n++;
            Add(cur);
            cur = freq switch
            {
                "DAILY" => cur.AddDays(interval),
                "WEEKLY" => cur.AddDays(7 * interval),
                "MONTHLY" => cur.AddMonths(interval),
                "YEARLY" => cur.AddYears(interval),
                _ => DateTime.MaxValue
            };
        }
    }

    private static DayOfWeek Day(string d) => d switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday
    };

    /// <summary>"20261116T090000Z" (UTC), "20261116T090000" with TZID or floating, "20261116" (all day) → local time.</summary>
    private static bool ParseDate(string pars, string v, out DateTime local, out bool allDay)
    {
        local = default;
        v = v.Trim();
        allDay = v.Length == 8 || pars.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !v.Contains('T');
        if (allDay)
            return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out local);
        bool utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        if (utc) { local = DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(); return true; }
        var tz = Regex.Match(pars, "TZID=\"?([^;\"]+)\"?").Groups[1].Value;
        if (tz.Length > 0 && FindZone(tz) is { } zone)
        {
            try { local = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local); return true; } catch { }
        }
        local = t; // floating time = local
        return true;
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        try { if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var w)) return TimeZoneInfo.FindSystemTimeZoneById(w); } catch { }
        return null;
    }

    private static bool TryDuration(string v, out TimeSpan span)
    {
        span = default;
        var m = Regex.Match(v, @"^P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return false;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        span = new TimeSpan(G(1) * 7 + G(2), G(3), G(4), G(5));
        return true;
    }

    private static List<string> Unfold(string ics)
    {
        var res = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && res.Count > 0) res[^1] += raw[1..];
            else res.Add(raw.TrimEnd('\r'));
        }
        return res;
    }

    private static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}
