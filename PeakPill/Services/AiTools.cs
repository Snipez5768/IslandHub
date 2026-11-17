using System.Text.Json;
using System.Text.Json.Nodes;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>One island function the KI may call (name, what it does, its parameters as JSON Schema).</summary>
public sealed record AiTool(string Name, string Description, JsonObject Parameters, Func<JsonObject, Task<string>> Run);

/// <summary>What the tools need from the island window: ask the user, open a page.</summary>
public interface IAiHost
{
    /// <summary>A card in the chat with the question and two buttons – true = yes.</summary>
    Task<bool> ConfirmAsync(string question, string yes);
    void OpenPage(string tab);
}

/// <summary>
/// The island's functions for the KI chat (user wish 2026-11-17: "die KI soll meine Insel nutzen können").
/// Everything that leaves the PC (LocalSend) asks first in the chat; the rest only acts inside the island.
/// Every result is a small JSON text the model reads.
/// </summary>
public static class AiTools
{
    private static Hub H => App.Hub;

    private static JsonObject Schema(params (string Name, string Type, string Description, string[] Enum, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (name, type, desc, en, required) in props)
        {
            var o = new JsonObject { ["type"] = type, ["description"] = desc };
            if (en != null) o["enum"] = new JsonArray(en.Select(e => (JsonNode)e).ToArray());
            p[name] = o;
            if (required) req.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);
    private static string Str(JsonObject a, string key) => a?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a?[key]?.ToString();
    private static double? Num(JsonObject a, string key) => a?[key] is JsonValue v && (v.TryGetValue<double>(out var d) || double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) ? d : null;

    public static List<AiTool> All(IAiHost host) => new()
    {
        new("island_status",
            "Current state of the user's PeakPill island on Windows: local date and time, what music is playing, the timer, how many clipboard entries exist, and which LocalSend devices (phones, PCs) are nearby. Call this first when you need any of that.",
            Schema(), _ => Ui0(() => Json(new
            {
                now = DateTime.Now.ToString("yyyy-MM-dd HH:mm, dddd", System.Globalization.CultureInfo.InvariantCulture),
                music = H.Media.HasMedia ? new { title = H.Media.Title, artist = H.Media.Artist, playing = H.Media.IsPlaying } : null,
                timer = H.Timer.Active ? new { running = H.Timer.Running, remaining = H.Timer.RemainingText } : null,
                clipboard_entries = H.Clipboard.Items.Count,
                localsend_devices = H.LocalSend.Devices.Select(d => new { name = d.Alias, type = d.DeviceType }).ToList(),
            }))),

        new("get_clipboard",
            "Read the user's most recent clipboard entries (newest first). Texts and links come with their text; pictures and files only with their kind and name.",
            Schema(("count", "integer", "How many entries, 1 to 10 (default 5)", null, false)),
            a => Ui0(() =>
            {
                int n = Math.Clamp((int)(Num(a, "count") ?? 5), 1, 10);
                return Json(H.Clipboard.Items.Take(n).Select((i, k) => new
                {
                    index = k,
                    kind = i.Kind.ToString().ToLowerInvariant(),
                    text = i.Kind is ClipKind.Text or ClipKind.Link ? Cut(i.Text, 1500) : null,
                    name = i.Kind == ClipKind.Files ? string.Join(", ", i.Files?.Select(Path.GetFileName) ?? Enumerable.Empty<string>()) : i.IsImage ? "picture" : null,
                }).ToList());
            })),

        new("copy_to_clipboard",
            "Put a text into the user's Windows clipboard so they can paste it anywhere.",
            Schema(("text", "string", "The text to copy", null, true)),
            a => Ui0(() =>
            {
                var t = Str(a, "text") ?? "";
                System.Windows.Clipboard.SetText(t);
                return Json(new { ok = true, characters = t.Length });
            })),

        new("localsend_send",
            "Send something with LocalSend to a phone or PC nearby (see island_status for device names). The user confirms in the chat before anything is sent. what = text (send the given text), clipboard (a clipboard entry by index from get_clipboard), screenshot (the newest screenshot), file (a file path on this PC).",
            Schema(("device", "string", "Device name as listed in island_status", null, true),
                   ("what", "string", "What to send", new[] { "text", "clipboard", "screenshot", "file" }, true),
                   ("text", "string", "The text, when what = text", null, false),
                   ("clipboard_index", "integer", "Index from get_clipboard, when what = clipboard", null, false),
                   ("path", "string", "Full file path, when what = file", null, false)),
            a => SendViaLocalSend(host, a)),

        new("start_timer",
            "Start the island's timer.",
            Schema(("minutes", "number", "Duration in minutes (may have decimals)", null, true)),
            a => Ui0(() =>
            {
                var m = Num(a, "minutes") ?? 0;
                if (m <= 0 || m > 24 * 60) return Json(new { ok = false, error = "minutes must be between 0 and 1440" });
                H.Timer.Reset();
                H.Timer.SetDurationSeconds((int)Math.Round(m * 60));
                H.Timer.Start();
                return Json(new { ok = true, running_for = H.Timer.RemainingText });
            })),

        new("add_reminder",
            "Add a reminder to the island. It rings at the given local time.",
            Schema(("text", "string", "What to remind of", null, true),
                   ("due", "string", "Local date and time, ISO format like 2026-11-17T18:30", null, true)),
            a => Ui0(() =>
            {
                if (!DateTime.TryParse(Str(a, "due"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var due))
                    return Json(new { ok = false, error = "due must be like 2026-11-17T18:30" });
                if (due < DateTime.Now.AddMinutes(-1)) return Json(new { ok = false, error = "that time has passed" });
                var r = H.Reminders.Add(Str(a, "text") ?? "", due, RepeatKind.Einmal);
                return Json(new { ok = true, due = due.ToString("yyyy-MM-dd HH:mm") });
            })),

        new("media",
            "Control the music that is playing (Spotify, browser, any player).",
            Schema(("action", "string", "What to do", new[] { "play_pause", "next", "previous" }, true)),
            a => Ui0(() =>
            {
                switch (Str(a, "action"))
                {
                    case "next": H.Media.Next(); break;
                    case "previous": H.Media.Previous(); break;
                    default: H.Media.PlayPause(); break;
                }
                return Json(new { ok = true });
            })),

        new("open_page",
            "Open a page of the island for the user.",
            Schema(("page", "string", "Which page", new[] { "Music", "Clip", "Send", "Timer", "Today", "Remind", "Controls", "Notify", "Agent", "Games", "Shelf" }, true)),
            a => Ui0(() => { host.OpenPage(Str(a, "page")); return Json(new { ok = true }); })),
    };

    /// <summary>Runs on the UI thread (the services are WPF objects).</summary>
    private static Task<string> Ui0(Func<string> f)
    {
        var d = System.Windows.Application.Current.Dispatcher;
        try { return Task.FromResult(d.CheckAccess() ? f() : d.Invoke(f)); }
        catch (Exception ex) { return Task.FromResult(Json(new { ok = false, error = ex.Message })); }
    }

    private static string Cut(string s, int n) => s == null ? null : s.Length > n ? s[..n] + "…" : s;
    private static string Q(string s) => L.English ? "“" + s + "”" : "„" + s + "“";

    private static async Task<string> SendViaLocalSend(IAiHost host, JsonObject a)
    {
        var name = Str(a, "device") ?? "";
        var d = H.LocalSend.Devices.FirstOrDefault(x => x.Alias.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? H.LocalSend.Devices.FirstOrDefault(x => x.Alias.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (d == null) return Json(new { ok = false, error = "no such device nearby", devices = H.LocalSend.Devices.Select(x => x.Alias).ToList() });

        string what = Str(a, "what"), label;
        Func<Task<bool>> send;
        switch (what)
        {
            case "text":
                var text = Str(a, "text") ?? "";
                if (text.Length == 0) return Json(new { ok = false, error = "text is empty" });
                label = Q(Cut(text, 60));
                send = () => H.LocalSend.SendTextAsync(d, text);
                break;
            case "clipboard":
                int idx = (int)(Num(a, "clipboard_index") ?? 0);
                var item = H.Clipboard.Items.ElementAtOrDefault(idx);
                if (item == null) return Json(new { ok = false, error = "no clipboard entry with that index" });
                if (item.IsImage && item.ImageFile != null) { label = L.T("das Bild aus der Zwischenablage"); send = () => H.LocalSend.SendAsync(d, new[] { item.ImageFile }); }
                else if (item.Kind == ClipKind.Files && item.Files is { Count: > 0 }) { label = string.Join(", ", item.Files.Select(Path.GetFileName)); send = () => H.LocalSend.SendAsync(d, item.Files); }
                else { label = Q(Cut(item.Text, 60)); send = () => H.LocalSend.SendTextAsync(d, item.Text ?? ""); }
                break;
            case "screenshot":
                var shot = H.Clipboard.Items.FirstOrDefault(i => i.IsImage && i.ImageFile != null && File.Exists(i.ImageFile));
                if (shot == null) return Json(new { ok = false, error = "no screenshot or picture in the clipboard" });
                label = L.T("den letzten Screenshot");
                send = () => H.LocalSend.SendAsync(d, new[] { shot.ImageFile });
                break;
            case "file":
                var path = Str(a, "path");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Json(new { ok = false, error = "file not found" });
                label = Path.GetFileName(path);
                send = () => H.LocalSend.SendAsync(d, new[] { path });
                break;
            default:
                return Json(new { ok = false, error = "what must be text, clipboard, screenshot or file" });
        }

        // nothing leaves the PC without the user's yes
        if (!await host.ConfirmAsync(L.T($"{label} an {d.Alias} senden?"), L.T("Senden")))
            return Json(new { ok = false, error = "the user declined" });
        bool ok = await send();
        return Json(new { ok, status = H.LocalSend.Status });
    }
}
