using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using PeakPill.Core;

namespace PeakPill.Services;

public enum HelperKind { None, Link, Phone, Address, Money, Unit, Foreign }

/// <summary>
/// What a copied text is (2026-11-18, helpers 41a / 43 / 44 / 45): a link (→ QR code), a phone number (→ call on the
/// phone), an address (→ map), a price or a unit (→ converted), or text in another language (→ translate).
/// </summary>
public static class CopyHelpers
{
    private static readonly Regex Phone = new(@"^\s*(\+\d{1,3}[\s\-/]?)?(\(?\d{2,5}\)?[\s\-/]?)?\d[\d\s\-/]{4,14}\d\s*$", RegexOptions.Compiled);
    private static readonly Regex Address = new(
        @"\b[A-ZÄÖÜ][\wäöüß.\-]*(?:\s[\wäöüß.\-]+){0,3}?(?:straße|strasse|str\.|weg|gasse|platz|allee|ring|damm|ufer|chaussee|steig|pfad|street|st\.|road|rd\.|avenue|ave\.|lane|drive)\s*\d+\s?[a-zA-Z]?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Money = new(
        @"^\s*(?<c1>[$€£¥]|USD|EUR|GBP|CHF|JPY)?\s*(?<n>\d{1,3}(?:[.,\s]\d{3})*(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?)\s*(?<c2>[$€£¥]|USD|EUR|GBP|CHF|JPY)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Unit = new(
        @"^\s*(?<n>-?\d+(?:[.,]\d+)?)\s*(?<u>miles?|mi|inch(?:es)?|in|""|ft|feet|foot|lbs?|pounds?|oz|ounces?|°\s?F|gal(?:lons?)?|mph|yards?|yd)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> German = new(StringComparer.OrdinalIgnoreCase)
        { "der", "die", "das", "und", "ist", "nicht", "ein", "eine", "ich", "du", "wir", "mit", "für", "auf", "zu", "den", "dem", "sie", "es", "auch", "noch", "wie" };
    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "is", "are", "you", "of", "to", "in", "that", "it", "with", "for", "this", "on", "be", "was", "have", "not" };

    public static (HelperKind Kind, string Value) Detect(ClipItem item)
    {
        if (item == null) return (HelperKind.None, null);
        if (item.Kind == ClipKind.Link) return (HelperKind.Link, item.Text?.Trim());
        if (item.Kind != ClipKind.Text || string.IsNullOrWhiteSpace(item.Text)) return (HelperKind.None, null);
        var t = item.Text.Trim();
        if (t.Length <= 30 && Phone.IsMatch(t) && t.Count(char.IsDigit) is >= 7 and <= 15 && !Regex.IsMatch(t, @"^\d+$")) return (HelperKind.Phone, t);
        if (t.Length <= 40 && Money.Match(t) is { Success: true } m && (m.Groups["c1"].Success ^ m.Groups["c2"].Success)) return (HelperKind.Money, t);
        if (t.Length <= 30 && Unit.IsMatch(t)) return (HelperKind.Unit, t);
        if (t.Length <= 140 && Address.Match(t) is { Success: true }) return (HelperKind.Address, t.Replace("\r", " ").Replace("\n", ", "));
        if (IsForeign(t)) return (HelperKind.Foreign, t);
        return (HelperKind.None, null);
    }

    /// <summary>Prose in another language than the island's (a few words at least, no code, no list of numbers).</summary>
    private static bool IsForeign(string t)
    {
        if (t.Length < 25 || t.Length > 4000 || t.Contains("://") || t.Count(c => c is '{' or '}' or ';' or '<' or '>') > 2) return false;
        var words = Regex.Matches(t, @"\p{L}+").Select(m => m.Value).ToList();
        if (words.Count < 5 || words.Sum(w => w.Length) < t.Length * 0.55) return false;
        int de = words.Count(German.Contains), en = words.Count(English.Contains);
        return L.English ? en == 0 && de >= 2 || en == 0 && words.Count >= 8 : de == 0;
    }

    // ---------- units (no internet needed) ----------

    /// <summary>"5 miles" → "8,05 km" (in the island's number format).</summary>
    public static string ConvertUnit(string text)
    {
        var m = Unit.Match(text);
        if (!m.Success || !TryNumber(m.Groups["n"].Value, out double v)) return null;
        var u = m.Groups["u"].Value.ToLowerInvariant().Replace(" ", "");
        (double val, string unit) r = u switch
        {
            "mi" or "mile" or "miles" => (v * 1.609344, "km"),
            "in" or "inch" or "inches" or "\"" => (v * 2.54, "cm"),
            "ft" or "feet" or "foot" => (v * 0.3048, "m"),
            "yd" or "yard" or "yards" => (v * 0.9144, "m"),
            "lb" or "lbs" or "pound" or "pounds" => (v * 0.45359237, "kg"),
            "oz" or "ounce" or "ounces" => (v * 28.349523, "g"),
            "°f" => ((v - 32) * 5 / 9, "°C"),
            "gal" or "gallon" or "gallons" => (v * 3.785411784, "l"),
            "mph" => (v * 1.609344, "km/h"),
            _ => (double.NaN, null),
        };
        return r.unit == null ? null : $"{r.val.ToString(r.val >= 100 ? "0" : "0.##", Culture)} {r.unit}";
    }

    // ---------- money (rates of the European Central Bank, once a day) ----------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly Dictionary<string, (DateTime Day, double Rate)> Rates = new();

    /// <summary>"49,99 $" → "≈ 46,10 €" – into euro, or from euro into dollar.</summary>
    public static async Task<string> ConvertMoneyAsync(string text)
    {
        var m = Money.Match(text);
        if (!m.Success || !TryNumber(m.Groups["n"].Value, out double v)) return null;
        string from = Code(m.Groups["c1"].Success ? m.Groups["c1"].Value : m.Groups["c2"].Value);
        if (from == null) return null;
        string to = from == "EUR" ? "USD" : "EUR";
        double? rate = await RateAsync(from, to);
        if (rate is not { } r) return null;
        return $"≈ {(v * r).ToString("N2", Culture)} {(to == "EUR" ? "€" : "$")}";
    }

    private static async Task<double?> RateAsync(string from, string to)
    {
        string key = from + to;
        if (Rates.TryGetValue(key, out var c) && c.Day == DateTime.Today) return c.Rate;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.frankfurter.app/latest?from={from}&to={to}"));
            double rate = doc.RootElement.GetProperty("rates").GetProperty(to).GetDouble();
            Rates[key] = (DateTime.Today, rate);
            return rate;
        }
        catch { return null; }
    }

    private static string Code(string c) => c.ToUpperInvariant() switch
    {
        "$" or "USD" => "USD", "€" or "EUR" => "EUR", "£" or "GBP" => "GBP", "¥" or "JPY" => "JPY", "CHF" => "CHF", _ => null
    };

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(L.English ? "en-US" : "de-DE");

    /// <summary>"1.234,56" and "1,234.56" and "49,99" – the last separator with 1–2 digits after it is the decimal one.</summary>
    private static bool TryNumber(string s, out double v)
    {
        s = s.Replace(" ", "");
        int lastSep = Math.Max(s.LastIndexOf(','), s.LastIndexOf('.'));
        if (lastSep >= 0 && s.Length - lastSep - 1 is 1 or 2)
            s = s[..lastSep].Replace(",", "").Replace(".", "") + "." + s[(lastSep + 1)..];
        else s = s.Replace(",", "").Replace(".", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
