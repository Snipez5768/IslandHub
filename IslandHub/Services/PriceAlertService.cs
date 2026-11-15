using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using IslandHub.Core;

namespace IslandHub.Services;

public enum AlertKind { Shop, Crypto, Stock, Steam, EpicFree, TwitchLive }

/// <summary>below: price ≤ target · above: price ≥ target · move: |day change| ≥ target % · sale: any discount / price drop · live: channel online.</summary>
public enum AlertCondition { Below, Above, Move, Sale, Live }

public sealed class PriceAlert : ObservableObject
{
    private double? _price, _change;
    private string _status = "Wird geprüft …", _currency = "€";
    private bool _triggered, _error;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AlertKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>URL (shop), CoinGecko id, ticker, Steam app id or Twitch channel.</summary>
    public string Query { get; set; } = "";
    public AlertCondition Condition { get; set; }
    public double? Target { get; set; }
    public string Url { get; set; }
    public DateTime? LastChecked { get; set; }
    /// <summary>Epic: the free games seen last time.</summary>
    public string LastSeen { get; set; }
    /// <summary>Price when the alarm was set (for "any price drop").</summary>
    public double? StartPrice { get; set; }
    /// <summary>Already reported – fires again only after the condition was false once.</summary>
    public bool Triggered { get => _triggered; set => Set(ref _triggered, value); }

    public double? Price { get => _price; set { if (Set(ref _price, value)) Changed(); } }
    public double? Change { get => _change; set { if (Set(ref _change, value)) Changed(); } }
    public string Currency { get => _currency; set { if (Set(ref _currency, value)) Changed(); } }
    [JsonIgnore] public string Status { get => _status; set => Set(ref _status, value); }
    [JsonIgnore] public bool Error { get => _error; set => Set(ref _error, value); }

    [JsonIgnore] public string PriceText => Kind switch
    {
        AlertKind.TwitchLive => Price == 1 ? "LIVE" : "offline",
        AlertKind.EpicFree => string.IsNullOrEmpty(LastSeen) ? "–" : LastSeen,
        _ => Price is { } p ? Fmt(p, Currency) : "–"
    };
    [JsonIgnore] public string ChangeText => Kind is AlertKind.EpicFree or AlertKind.TwitchLive ? "" : Change is { } c ? (c >= 0 ? "▲ " : "▼ ") + Math.Abs(c).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " %" : "";
    [JsonIgnore] public bool ChangeUp => Change >= 0;
    [JsonIgnore] public string RuleText => Condition switch
    {
        AlertCondition.Below => $"Ziel unter {Fmt(Target ?? 0, Currency)}",
        AlertCondition.Above => $"Alarm über {Fmt(Target ?? 0, Currency)}",
        AlertCondition.Move => $"Alarm bei ±{Target ?? 5:0.#} % an einem Tag",
        AlertCondition.Sale => Kind == AlertKind.Shop ? "Alarm bei jedem Preisfall" : "Alarm bei jedem Sale",
        AlertCondition.Live => Kind == AlertKind.EpicFree ? "Alarm bei neuen Gratis-Spielen" : "Alarm, wenn live",
        _ => ""
    };
    /// <summary>0…1 – how close the price is to the target (for the little bar).</summary>
    [JsonIgnore] public double Closeness
    {
        get
        {
            if (Price is not { } p || Target is not { } t || t <= 0) return 0;
            return Condition switch
            {
                AlertCondition.Below => Math.Clamp(t / p, 0, 1),
                AlertCondition.Above => Math.Clamp(p / t, 0, 1),
                _ => 0
            };
        }
    }
    [JsonIgnore] public bool HasBar => Condition is AlertCondition.Below or AlertCondition.Above && Price != null;
    [JsonIgnore] public string IconKind => Kind switch
    {
        AlertKind.Crypto => "bitcoin", AlertKind.Stock => "chart", AlertKind.Steam => "gamecontroller",
        AlertKind.EpicFree => "gift", AlertKind.TwitchLive => "dot.radiowaves", _ => "cart"
    };

    private void Changed()
    {
        Raise(nameof(PriceText)); Raise(nameof(ChangeText)); Raise(nameof(ChangeUp)); Raise(nameof(Closeness)); Raise(nameof(HasBar)); Raise(nameof(RuleText));
    }
    public void RaiseRule() => Raise(nameof(RuleText));

    public static string Fmt(double v, string cur)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        string num = v >= 1000 ? v.ToString("#,##0", de) : v >= 1 ? v.ToString("#,##0.00", de) : v.ToString("0.0000", de);
        return cur is "$" or "USD" ? num + " $" : num + " " + (cur == "EUR" ? "€" : cur);
    }
}

/// <summary>
/// Price alarms: shop pages (fetched in the background every 3 h), crypto (CoinGecko), stocks (Yahoo, a few minutes
/// delayed), Steam prices and sales, Epic's free games, Twitch channels going live.
/// </summary>
public sealed class PriceAlertService : ObservableObject
{
    private static readonly HttpClient Http = CreateClient();
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _busy;

    public ObservableCollection<PriceAlert> Items { get; } = new();
    public event Action<PriceAlert, string> Fired;

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        // a normal browser identity – shops serve their regular page; nothing about the user is sent
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE,de;q=0.9");
        return h;
    }

    public void Load()
    {
        foreach (var a in Storage.Load<List<PriceAlert>>("price_alerts.json") ?? new()) Items.Add(a);
    }

    /// <summary>False for tests: nothing is written to the user's alarm list.</summary>
    public bool Persist { get; set; } = true;

    public void Save() { if (Persist) Storage.Save("price_alerts.json", Items.ToList()); }

    public void Start()
    {
        _t.Tick += async (_, _) => await CheckDue();
        _t.Start();
        _ = CheckDue(force: true);
    }

    private static TimeSpan IntervalOf(AlertKind k) => k switch
    {
        AlertKind.Crypto => TimeSpan.FromMinutes(2),
        AlertKind.Stock => TimeSpan.FromMinutes(5),
        AlertKind.TwitchLive => TimeSpan.FromMinutes(2),
        AlertKind.EpicFree => TimeSpan.FromHours(6),
        _ => TimeSpan.FromHours(3) // shops, Steam
    };

    private async Task CheckDue(bool force = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            foreach (var a in Items.ToList())
            {
                if (!force && a.LastChecked is { } lc && DateTime.Now - lc < IntervalOf(a.Kind)) continue;
                await Check(a);
            }
            Save();
        }
        finally { _busy = false; }
    }

    public async Task Check(PriceAlert a)
    {
        try
        {
            switch (a.Kind)
            {
                case AlertKind.Crypto: await CheckCrypto(a); break;
                case AlertKind.Stock: await CheckStock(a); break;
                case AlertKind.Steam: await CheckSteam(a); break;
                case AlertKind.EpicFree: await CheckEpic(a); break;
                case AlertKind.TwitchLive: await CheckTwitch(a); break;
                default: await CheckShop(a); break;
            }
            a.Error = false;
            a.LastChecked = DateTime.Now;
            a.Status = "geprüft " + DateTime.Now.ToString("HH:mm");
            Evaluate(a);
        }
        catch (Exception ex)
        {
            a.Error = true;
            a.LastChecked = DateTime.Now;
            a.Status = "konnte nicht geprüft werden";
            App.Log(new Exception($"Preisalarm {a.Name}: {ex.Message}"));
        }
    }

    private void Evaluate(PriceAlert a)
    {
        bool hit = false;
        string msg = "";
        var p = a.Price;
        switch (a.Condition)
        {
            case AlertCondition.Below when p is { } v && a.Target is { } t:
                hit = v <= t; msg = $"jetzt {a.PriceText}"; break;
            case AlertCondition.Above when p is { } v && a.Target is { } t:
                hit = v >= t; msg = $"jetzt {a.PriceText}"; break;
            case AlertCondition.Move when a.Change is { } c:
                hit = Math.Abs(c) >= (a.Target ?? 5); msg = $"{a.ChangeText} heute · {a.PriceText}"; break;
            case AlertCondition.Sale when a.Kind == AlertKind.Shop && p is { } v && a.StartPrice is { } sp:
                hit = v < sp - 0.009; msg = $"jetzt {a.PriceText} (vorher {PriceAlert.Fmt(sp, a.Currency)})"; break;
            case AlertCondition.Sale:
                hit = a.Change is < 0; msg = $"im Sale: {a.PriceText} ({a.ChangeText.Replace("▼ ", "−")})"; break;
            case AlertCondition.Live when a.Kind == AlertKind.EpicFree:
                hit = a.Change == 1; msg = $"Neu gratis: {a.LastSeen}"; break;
            case AlertCondition.Live:
                hit = p == 1; msg = "ist jetzt live"; break;
        }
        if (hit && !a.Triggered) { a.Triggered = true; Fired?.Invoke(a, msg); }
        else if (!hit && a.Triggered) a.Triggered = false; // re-arm once the condition is false again
    }

    // ---------- adding: one text field understands links, tickers, coins and game names ----------

    /// <summary>
    /// Creates an alarm from what was typed: a link (shop / Steam / Twitch), "BTC", "AAPL", "epic", a game name.
    /// <paramref name="rule"/>: "&lt;150", "&gt;100000", "5%" / "±5%", or empty for the sensible default.
    /// </summary>
    public async Task<(PriceAlert Alert, string Error)> AddAsync(string input, string rule)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return (null, "Gib einen Link, ein Kürzel oder einen Spielnamen ein");
        PriceAlert a;
        try { a = await Resolve(input); }
        catch (Exception ex) { App.Log(ex); a = null; }
        if (a == null) return (null, "Nicht gefunden – prüfe die Eingabe");
        ApplyRule(a, rule);
        await Check(a);
        if (a.Kind == AlertKind.Shop && a.Price is { } p0) a.StartPrice = p0;
        if (a.Condition == AlertCondition.Below && a.Target == null && a.Price is { } p) a.Target = Math.Floor(p * 0.9);
        a.RaiseRule();
        Items.Insert(0, a);
        Save();
        return (a, null);
    }

    private static void ApplyRule(PriceAlert a, string rule)
    {
        rule = (rule ?? "").Trim().Replace("€", "").Replace("$", "").Replace(" ", "");
        double Num(string s) => double.Parse(s.Replace(".", "").Replace(',', '.'), CultureInfo.InvariantCulture);
        if (a.Kind is AlertKind.EpicFree or AlertKind.TwitchLive) { a.Condition = AlertCondition.Live; return; }
        if (rule.StartsWith("<") && rule.Length > 1) { a.Condition = AlertCondition.Below; a.Target = Num(rule[1..]); return; }
        if (rule.StartsWith(">") && rule.Length > 1) { a.Condition = AlertCondition.Above; a.Target = Num(rule[1..]); return; }
        if (rule.EndsWith("%")) { a.Condition = AlertCondition.Move; a.Target = Math.Abs(Num(rule.TrimStart('±', '+', '-').TrimEnd('%'))); return; }
        if (rule.Length > 0 && char.IsDigit(rule[0])) { a.Condition = AlertCondition.Below; a.Target = Num(rule); return; }
        a.Condition = a.Kind switch
        {
            AlertKind.Crypto or AlertKind.Stock => AlertCondition.Move,
            AlertKind.Steam => AlertCondition.Sale,
            _ => AlertCondition.Sale
        };
        if (a.Condition == AlertCondition.Move) a.Target = 5;
    }

    private async Task<PriceAlert> Resolve(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
        {
            var host = u.Host.ToLowerInvariant();
            var steam = Regex.Match(u.AbsolutePath, @"/app/(\d+)");
            if (host.EndsWith("steampowered.com") && steam.Success) return await SteamById(steam.Groups[1].Value);
            if (host.EndsWith("twitch.tv"))
            {
                var ch = u.AbsolutePath.Trim('/').Split('/')[0];
                return ch.Length > 0 ? new PriceAlert { Kind = AlertKind.TwitchLive, Query = ch.ToLowerInvariant(), Name = ch, Url = $"https://www.twitch.tv/{ch}", Currency = "" } : null;
            }
            return new PriceAlert { Kind = AlertKind.Shop, Query = input, Url = input, Name = host.Replace("www.", "") };
        }
        var lower = input.ToLowerInvariant();
        if (lower is "epic" or "epic gratis" or "epic games") return new PriceAlert { Kind = AlertKind.EpicFree, Query = "epic", Name = "Epic Games · Gratis-Spiele", Url = "https://store.epicgames.com/de/free-games", Currency = "" };
        if (lower.StartsWith("twitch:")) { var ch = input[7..].Trim(); return new PriceAlert { Kind = AlertKind.TwitchLive, Query = ch.ToLowerInvariant(), Name = ch, Url = $"https://www.twitch.tv/{ch}", Currency = "" }; }
        if (lower.StartsWith("aktie:")) return await StockBySymbol(input[6..].Trim());
        if (lower.StartsWith("krypto:")) return await CryptoBySearch(input[7..].Trim(), exactOnly: false);
        if (lower.StartsWith("steam:")) return await SteamBySearch(input[6..].Trim());

        // short code: a coin with exactly this symbol first (BTC, ETH, SOL), then a stock ticker (AAPL, TSLA)
        if (Regex.IsMatch(input, @"^[A-Za-z0-9.\-]{1,10}$") && !input.Contains(' '))
        {
            var c = await CryptoBySearch(input, exactOnly: true);
            if (c != null) return c;
            var s = await StockBySymbol(input);
            if (s != null) return s;
        }
        return await CryptoBySearch(input, exactOnly: true) ?? await SteamBySearch(input);
    }

    // ---------- crypto: CoinGecko (free, no account) ----------

    private static async Task<PriceAlert> CryptoBySearch(string q, bool exactOnly)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.coingecko.com/api/v3/search?query=" + Uri.EscapeDataString(q)));
        foreach (var c in doc.RootElement.GetProperty("coins").EnumerateArray())
        {
            var sym = c.GetProperty("symbol").GetString() ?? "";
            var name = c.GetProperty("name").GetString() ?? "";
            if (exactOnly && !sym.Equals(q, StringComparison.OrdinalIgnoreCase) && !name.Equals(q, StringComparison.OrdinalIgnoreCase)) continue;
            // only real, ranked coins for a bare symbol – "AAPL" must be the share, not some token named after it
            if (exactOnly && (!c.TryGetProperty("market_cap_rank", out var rank) || rank.ValueKind != JsonValueKind.Number || rank.GetInt32() > 400)) continue;
            var id = c.GetProperty("id").GetString();
            return new PriceAlert { Kind = AlertKind.Crypto, Query = id, Name = name, Currency = "€", Url = $"https://www.coingecko.com/de/munze/{id}" };
        }
        return null;
    }

    private static async Task CheckCrypto(PriceAlert a)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.coingecko.com/api/v3/simple/price?ids={Uri.EscapeDataString(a.Query)}&vs_currencies=eur&include_24hr_change=true"));
        var c = doc.RootElement.GetProperty(a.Query);
        a.Currency = "€";
        a.Price = c.GetProperty("eur").GetDouble();
        if (c.TryGetProperty("eur_24h_change", out var ch) && ch.ValueKind == JsonValueKind.Number) a.Change = ch.GetDouble();
    }

    // ---------- stocks: Yahoo Finance chart data (free, a few minutes delayed) ----------

    private static async Task<PriceAlert> StockBySymbol(string symbol)
    {
        var a = new PriceAlert { Kind = AlertKind.Stock, Query = symbol.ToUpperInvariant() };
        try
        {
            await CheckStock(a);
            if (a.Price == null) return null;
            a.Url = $"https://finance.yahoo.com/quote/{a.Query}";
            return a;
        }
        catch { return null; }
    }

    private static async Task CheckStock(PriceAlert a)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(a.Query)}?interval=1d&range=5d"));
        var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
        double price = meta.GetProperty("regularMarketPrice").GetDouble();
        double prev = meta.TryGetProperty("chartPreviousClose", out var pc) ? pc.GetDouble() : meta.GetProperty("previousClose").GetDouble();
        a.Currency = meta.TryGetProperty("currency", out var cu) && cu.GetString() == "USD" ? "$" : cu.GetString() == "EUR" ? "€" : cu.GetString();
        var name = meta.TryGetProperty("shortName", out var sn) ? sn.GetString() : meta.TryGetProperty("longName", out var ln) ? ln.GetString() : null;
        if (string.IsNullOrEmpty(a.Name)) a.Name = name != null ? $"{name} ({a.Query})" : a.Query;
        a.Price = price;
        a.Change = prev > 0 ? (price - prev) / prev * 100 : null;
    }

    // ---------- Steam store (price + discount) ----------

    private static async Task<PriceAlert> SteamBySearch(string term)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&cc=de&l=german"));
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return null;
        return await SteamById(items[0].GetProperty("id").GetInt32().ToString());
    }

    private static async Task<PriceAlert> SteamById(string appId)
    {
        var a = new PriceAlert { Kind = AlertKind.Steam, Query = appId, Url = $"https://store.steampowered.com/app/{appId}" };
        await CheckSteam(a);
        return a;
    }

    private static async Task CheckSteam(PriceAlert a)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={a.Query}&cc=de&l=german"));
        var d = doc.RootElement.GetProperty(a.Query).GetProperty("data");
        if (string.IsNullOrEmpty(a.Name) || a.Name == a.Query) a.Name = d.GetProperty("name").GetString() + " · Steam";
        a.Currency = "€";
        if (d.TryGetProperty("price_overview", out var po))
        {
            a.Price = po.GetProperty("final").GetInt32() / 100.0;
            int disc = po.GetProperty("discount_percent").GetInt32();
            a.Change = disc > 0 ? -disc : null; // only a sale shows a percentage
        }
        else { a.Price = 0; a.Change = null; } // free to play or not yet released
    }

    // ---------- Epic: this week's free games ----------

    private static async Task CheckEpic(PriceAlert a)
    {
        // two addresses for the same data – some networks / DNS filters only resolve one of them
        string json = null;
        foreach (var host in new[] { "store-site-backend-static-ipv4.ak.epicgames.com", "store-site-backend-static.epicgames.com" })
        {
            try { json = await Http.GetStringAsync($"https://{host}/freeGamesPromotions?locale=de&country=DE&allowCountries=DE"); break; }
            catch (HttpRequestException) { }
        }
        if (json == null) throw new Exception("Epic-Server nicht erreichbar");
        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        foreach (var g in doc.RootElement.GetProperty("data").GetProperty("Catalog").GetProperty("searchStore").GetProperty("elements").EnumerateArray())
        {
            if (!g.TryGetProperty("promotions", out var pr) || pr.ValueKind != JsonValueKind.Object) continue;
            var now = pr.GetProperty("promotionalOffers");
            if (now.GetArrayLength() == 0) continue;
            bool free = now[0].GetProperty("promotionalOffers").EnumerateArray()
                .Any(o => o.GetProperty("discountSetting").GetProperty("discountPercentage").GetInt32() == 0);
            if (free) names.Add(g.GetProperty("title").GetString());
        }
        var key = string.Join(", ", names);
        // fires when the list of free games changes (the first check only remembers it)
        a.Change = a.LastSeen != null && key.Length > 0 && key != a.LastSeen ? 1 : 0;
        if (a.Change == 1) a.Triggered = false;
        a.LastSeen = key;
        a.Price = names.Count;
    }

    // ---------- Twitch: live? (public channel page) ----------

    private static async Task CheckTwitch(PriceAlert a)
    {
        var html = await Http.GetStringAsync($"https://www.twitch.tv/{Uri.EscapeDataString(a.Query)}");
        a.Price = html.Contains("\"isLiveBroadcast\":true", StringComparison.Ordinal) ? 1 : 0;
        a.Currency = "";
    }

    // ---------- shops: read the price from the page (structured data first) ----------

    private static async Task CheckShop(PriceAlert a)
    {
        var html = await Http.GetStringAsync(a.Url ?? a.Query);
        if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) && !html.Contains("\"price\"", StringComparison.Ordinal) && !html.Contains("a-price", StringComparison.Ordinal))
            throw new Exception("Shop zeigt eine Captcha-Seite");

        var pt = Regex.Match(html, @"id=""productTitle""[^>]*>\s*([^<]{3,300}?)\s*</span>");
        var title = pt.Success ? pt.Groups[1].Value
            : Meta(html, "og:title") ?? Regex.Match(html, @"<title[^>]*>([^<]{3,200})</title>", RegexOptions.IgnoreCase).Groups[1].Value;
        title = WebUtility.HtmlDecode(title).Trim();
        if (title.Length > 0 && (string.IsNullOrEmpty(a.Name) || a.Name.Contains('.')))
        {
            var host = new Uri(a.Url ?? a.Query).Host.Replace("www.", "");
            var shop = host.Split('.')[0];
            shop = char.ToUpper(shop[0]) + shop[1..];
            title = Regex.Replace(title, @"\s*[:|\-–]\s*(Amazon\.de|Amazon).*$", "", RegexOptions.IgnoreCase);
            if (title.Length > 48) title = title[..46].TrimEnd() + "…";
            a.Name = $"{title} · {shop}";
        }

        double? price = null;
        // 1) Amazon's visible price
        var amz = Regex.Match(html, @"class=""a-offscreen"">\s*([0-9.,]+)\s*(?:&nbsp;)?\s*€");
        if (amz.Success) price = ParsePrice(amz.Groups[1].Value);
        // 2) structured data (most shops)
        price ??= ParsePrice(Meta(html, "product:price:amount") ?? Meta(html, "og:price:amount"));
        if (price == null)
        {
            var ip = Regex.Match(html, @"itemprop=""price""[^>]*content=""([0-9.,]+)""", RegexOptions.IgnoreCase);
            if (ip.Success) price = ParsePrice(ip.Groups[1].Value);
        }
        if (price == null)
        {
            var ld = Regex.Match(html, @"""price""\s*:\s*""?([0-9]+[.,]?[0-9]*)""?");
            if (ld.Success) price = ParsePrice(ld.Groups[1].Value);
        }
        if (price == null) throw new Exception("Kein Preis auf der Seite gefunden");
        a.Currency = "€";
        a.Price = price;
    }

    private static string Meta(string html, string prop)
    {
        var m = Regex.Match(html, $@"<meta[^>]+(?:property|name)=""{Regex.Escape(prop)}""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(html, $@"<meta[^>]+content=""([^""]+)""[^>]+(?:property|name)=""{Regex.Escape(prop)}""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double? ParsePrice(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        // "1.299,00" (de) / "1,299.00" (en) / "189.99"
        if (s.Contains(',') && s.Contains('.')) s = s.LastIndexOf(',') > s.LastIndexOf('.') ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (s.Contains(',')) s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    public void Remove(PriceAlert a)
    {
        Items.Remove(a);
        Save();
    }
}
