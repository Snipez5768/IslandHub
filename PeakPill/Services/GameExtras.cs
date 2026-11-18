using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>
/// 38 (2026-11-18): the frames per second of the game in front, read from RivaTuner Statistics Server (RTSS – part of
/// MSI Afterburner) when it runs. Windows itself only tells it with admin rights; without RTSS there is simply no number.
/// </summary>
public static class RtssFps
{
    public static int? For(uint pid)
    {
        try
        {
            using var mm = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
            using var v = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (v.ReadUInt32(0) != 0x52545353) return null;                    // 'RTSS'
            uint entrySize = v.ReadUInt32(8), arrOffset = v.ReadUInt32(12), arrSize = v.ReadUInt32(16);
            for (uint i = 0; i < arrSize; i++)
            {
                long e = arrOffset + i * entrySize;
                if (v.ReadUInt32(e) != pid) continue;
                uint t0 = v.ReadUInt32(e + 268), t1 = v.ReadUInt32(e + 272), frames = v.ReadUInt32(e + 276);
                if (t1 <= t0 || frames == 0) return null;
                return (int)Math.Round(1000.0 * frames / (t1 - t0));
            }
        }
        catch { }
        return null;
    }
}

/// <summary>A game of the Steam wishlist that is on sale right now.</summary>
public sealed record WishlistSale(string AppId, string Name, int Percent, string Price, string Was);

/// <summary>
/// 37 (2026-11-18): once a day the Steam wishlist of the account signed in to the Steam app on this PC is looked
/// through; a game on sale is told once. The wishlist must be public in Steam.
/// </summary>
public static class SteamWishlist
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The account last signed in to Steam here (SteamID64) – null without Steam.</summary>
    public static string LocalSteamId()
    {
        try
        {
            var path = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (path == null) return null;
            var vdf = File.ReadAllText(Path.Combine(path, "config", "loginusers.vdf"));
            string best = null;
            foreach (Match m in Regex.Matches(vdf, "\"(7656\\d{13})\"\\s*\\{(?<body>[^}]*)\\}"))
            {
                best ??= m.Groups[1].Value;
                if (Regex.IsMatch(m.Groups["body"].Value, "\"MostRecent\"\\s*\"1\"")) return m.Groups[1].Value;
            }
            return best;
        }
        catch { return null; }
    }

    public static async Task<List<WishlistSale>> SalesAsync(string steamId)
    {
        var sales = new List<WishlistSale>();
        using var wl = JsonDocument.Parse(await Http.GetStringAsync($"https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid={steamId}"));
        var ids = wl.RootElement.GetProperty("response").TryGetProperty("items", out var items)
            ? items.EnumerateArray().Select(x => x.GetProperty("appid").GetInt64()).Take(200).ToList() : new List<long>();
        string cc = L.English ? "US" : "DE";
        for (int i = 0; i < ids.Count; i += 50)
        {
            var input = new JsonObject
            {
                ["ids"] = new JsonArray(ids.Skip(i).Take(50).Select(id => (JsonNode)new JsonObject { ["appid"] = id }).ToArray()),
                ["context"] = new JsonObject { ["language"] = L.English ? "english" : "german", ["country_code"] = cc },
                ["data_request"] = new JsonObject { ["include_basic_info"] = true },
            };
            var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input.ToJsonString());
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.GetProperty("response").TryGetProperty("store_items", out var list)) continue;
            foreach (var it in list.EnumerateArray())
            {
                if (!it.TryGetProperty("best_purchase_option", out var o)) continue;
                int pct = o.TryGetProperty("discount_pct", out var d) && d.TryGetInt32(out var p) ? p : 0;
                if (pct <= 0) continue;
                string Str(JsonElement e, string k) => e.TryGetProperty(k, out var x) ? x.ToString() : "";
                sales.Add(new WishlistSale(Str(it, "appid"), Str(it, "name"), pct, Str(o, "formatted_final_price"), Str(o, "formatted_original_price")));
            }
        }
        return sales;
    }
}
