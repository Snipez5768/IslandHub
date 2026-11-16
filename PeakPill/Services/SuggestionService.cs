using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>"There is a feature for what you are looking at" – e.g. a price alarm for the Amazon product page that is open.</summary>
public sealed class Suggestion
{
    public string Key { get; set; }
    public string Title { get; set; }
    public string Text { get; set; }
    /// <summary>What to hand to <see cref="PriceAlertService.AddAsync"/> when activated.</summary>
    public string Input { get; set; }
    public string Rule { get; set; } = "";
    public string IconKind { get; set; } = "sparkles";
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>
/// Watches which page is open in the browser in front (read from its address bar – no extension needed) and offers
/// a matching feature once: shown briefly in the island and kept in the "Vorschläge" list.
/// </summary>
public sealed class SuggestionService : ObservableObject
{
    private static readonly string[] Browsers = { "chrome", "msedge", "brave", "opera", "vivaldi", "firefox" };
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(2) };
    private HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _lastHwnd;
    private string _lastTitle;
    private bool _busy;

    public ObservableCollection<Suggestion> Items { get; } = new();
    public int Count => Items.Count;
    public event Action<Suggestion> Suggested;

    public void Start()
    {
        _seen = Storage.Load<HashSet<string>>("suggestions_seen.json") ?? new(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Storage.Load<List<Suggestion>>("suggestions.json") ?? new()) Items.Add(s);
        _t.Tick += async (_, _) => await Tick();
        _t.Start();
    }

    private async Task Tick()
    {
        if (_busy) return;
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return;
        var title = WindowTitle(h);
        if (h == _lastHwnd && title == _lastTitle) return; // nothing changed – no address bar reading
        _lastHwnd = h;
        _lastTitle = title;
        Native.GetWindowThreadProcessId(h, out var pid);
        string proc;
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName.ToLowerInvariant(); } catch { return; }
        if (!Browsers.Contains(proc)) return;

        _busy = true;
        try
        {
            var url = await Task.Run(() => AddressOf(h));
            if (string.IsNullOrEmpty(url)) return;
            var s = Match(url, CleanTitle(title));
            if (s == null || _seen.Contains(s.Key) || Items.Any(i => i.Key == s.Key)) return;
            _seen.Add(s.Key);
            Items.Insert(0, s);
            while (Items.Count > 20) Items.RemoveAt(Items.Count - 1);
            Save();
            Raise(nameof(Count));
            Suggested?.Invoke(s);
        }
        catch { }
        finally { _busy = false; }
    }

    /// <summary>Which pages have a feature, and what it is.</summary>
    private static Suggestion Match(string url, string title)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant().Replace("www.", "");
        var path = u.AbsolutePath;

        var asin = Regex.Match(path, @"/(?:dp|gp/product)/([A-Z0-9]{10})");
        if (host.StartsWith("amazon.") && asin.Success)
        {
            var link = $"https://www.{host}/dp/{asin.Groups[1].Value}";
            return new Suggestion { Key = "amazon:" + asin.Groups[1].Value, Title = "Amazon-Produkt erkannt", Text = $"Preisalarm für „{Short(title)}“?", Input = link, IconKind = "cart" };
        }
        var app = Regex.Match(path, @"^/app/(\d+)");
        if (host == "store.steampowered.com" && app.Success)
            return new Suggestion { Key = "steam:" + app.Groups[1].Value, Title = "Steam-Spiel erkannt", Text = $"Bescheid sagen, wenn „{Short(title.Replace(" on Steam", "").Replace(" bei Steam", ""))}“ im Sale ist?", Input = $"https://store.steampowered.com/app/{app.Groups[1].Value}", IconKind = "gamecontroller" };
        if (host == "twitch.tv")
        {
            var ch = path.Trim('/').Split('/')[0].ToLowerInvariant();
            if (ch.Length > 2 && !new[] { "directory", "videos", "settings", "subscriptions", "inventory", "drops", "wallet", "search", "downloads", "jobs", "p", "turbo" }.Contains(ch))
                return new Suggestion { Key = "twitch:" + ch, Title = $"Twitch: {ch}", Text = "Bescheid sagen, wenn der Kanal live geht?", Input = "twitch:" + ch, IconKind = "dot.radiowaves" };
        }
        var coin = Regex.Match(path, @"/(?:coins|munze|currencies)/([a-z0-9\-]+)");
        if ((host.EndsWith("coingecko.com") || host.EndsWith("coinmarketcap.com")) && coin.Success)
            return new Suggestion { Key = "crypto:" + coin.Groups[1].Value, Title = "Kryptowährung erkannt", Text = $"Kursalarm für {Short(coin.Groups[1].Value)} (±5 % am Tag)?", Input = "krypto:" + coin.Groups[1].Value, Rule = "5%", IconKind = "bitcoin" };
        var quote = Regex.Match(path, @"/quote/([A-Za-z0-9.\-]+)");
        if (host.EndsWith("finance.yahoo.com") && quote.Success)
            return new Suggestion { Key = "stock:" + quote.Groups[1].Value.ToUpperInvariant(), Title = "Aktie erkannt", Text = $"Kursalarm für {quote.Groups[1].Value.ToUpperInvariant()} (±5 % am Tag)?", Input = "aktie:" + quote.Groups[1].Value, Rule = "5%", IconKind = "chart" };
        return null;
    }

    public void Remove(Suggestion s)
    {
        Items.Remove(s);
        Save();
        Raise(nameof(Count));
    }

    private void Save()
    {
        Storage.Save("suggestions.json", Items.ToList());
        Storage.Save("suggestions_seen.json", _seen);
    }

    private static string Short(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > 40 ? s[..38].TrimEnd() + "…" : s;
    }

    private static string CleanTitle(string t) =>
        Regex.Replace(t ?? "", @"\s+[-–—]\s+(Google Chrome|Microsoft​? Edge|Brave|Opera|Vivaldi|Mozilla Firefox|Firefox).*$", "").Replace("Amazon.de: ", "").Trim();

    private static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---------- the browser's address bar via UI Automation (works without any extension) ----------

    private static readonly Dictionary<IntPtr, AutomationElement> BarCache = new();

    private static string AddressOf(IntPtr hwnd)
    {
        try
        {
            if (!BarCache.TryGetValue(hwnd, out var bar) || !IsAlive(bar))
            {
                var root = AutomationElement.FromHandle(hwnd);
                bar = root.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)));
                if (bar == null) return null;
                if (BarCache.Count > 20) BarCache.Clear();
                BarCache[hwnd] = bar;
            }
            return bar.TryGetCurrentPattern(ValuePattern.Pattern, out var p) ? ((ValuePattern)p).Current.Value : null;
        }
        catch { return null; }
    }

    private static bool IsAlive(AutomationElement e)
    {
        try { _ = e.Current.ControlType; return true; } catch { return false; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
}
