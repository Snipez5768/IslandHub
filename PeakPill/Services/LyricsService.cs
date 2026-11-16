using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PeakPill.Services;

/// <summary>One line of a song text; <see cref="At"/> = when it is sung (null = the text has no times).</summary>
public sealed record LyricLine(TimeSpan? At, string Text);

/// <summary>
/// Song texts (user wish 2026-11-16) from LRCLIB – a free, open lyrics database, no login, works for every player.
/// Timed lyrics ("[01:23.45] …") let the island follow the song; otherwise the plain text is shown.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Dictionary<string, List<LyricLine>> Cache = new();
    private static readonly Regex Stamp = new(@"^\[(\d+):(\d+(?:[.,]\d+)?)\]\s*(.*)$", RegexOptions.Compiled);

    static LyricsService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("PeakPill (Windows island)");

    /// <summary>The lines (empty = none found).</summary>
    public static async Task<List<LyricLine>> GetAsync(string title, string artist, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return new();
        string key = $"{artist}|{title}".ToLowerInvariant();
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var lines = new List<LyricLine>();
        try
        {
            string q = $"track_name={Uri.EscapeDataString(Clean(title))}" + (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist.Split(',')[0].Trim())}");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://lrclib.net/api/search?" + q));
            // the best match: timed lyrics first, then the closest length
            JsonElement? best = null;
            double bestScore = double.MaxValue;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                bool synced = e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String;
                bool plain = e.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String;
                if (!synced && !plain) continue;
                double len = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                double score = (synced ? 0 : 1000) + (duration > TimeSpan.Zero && len > 0 ? Math.Abs(len - duration.TotalSeconds) : 50);
                if (score < bestScore) { bestScore = score; best = e; }
            }
            if (best is { } b)
            {
                if (b.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    foreach (var raw in s.GetString().Split('\n'))
                    {
                        var m = Stamp.Match(raw.Trim());
                        if (!m.Success) continue;
                        var at = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
                        lines.Add(new LyricLine(at, m.Groups[3].Value.Trim()));
                    }
                }
                else if (b.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String)
                    lines.AddRange(p.GetString().Split('\n').Select(l => new LyricLine(null, l.Trim())));
            }
        }
        catch { /* offline or not found – the page says so */ }
        Cache[key] = lines;
        return lines;
    }

    /// <summary>"Song (Remastered 2011)" / "Song - Live" → "Song" – the database knows the plain title.</summary>
    private static string Clean(string title) => Regex.Replace(title, @"\s*[\(\[].*?[\)\]]|\s+-\s+(Remaster|Live|Radio|Single|Edit|Version|Mono|Stereo).*$", "", RegexOptions.IgnoreCase).Trim();

    /// <summary>The index of the line sung at <paramref name="pos"/> (-1 before the first).</summary>
    public static int LineAt(List<LyricLine> lines, TimeSpan pos)
    {
        int idx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].At is not { } at) return -1;
            if (at <= pos + TimeSpan.FromMilliseconds(250)) idx = i; else break;
        }
        return idx;
    }
}
