using System.Text.Json;
using System.Text.Json.Nodes;

namespace PeakPill.Services;

/// <summary>
/// Sets up Claude Code so the island sees what it works on (user wish 2026-11-17): five "http" hooks in the user's
/// own Claude Code settings (%USERPROFILE%\.claude\settings.json) post every step to <see cref="AgentService"/>.
/// Everything else in that file stays as it is; before the first change a copy is kept next to it.
/// </summary>
public static class ClaudeHooks
{
    public static readonly string[] Events = { "UserPromptSubmit", "PreToolUse", "Notification", "Stop", "SessionEnd" };
    public static string Url => $"http://127.0.0.1:{AgentService.Port}/hook";
    public static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public enum State { NoClaude, Missing, Partly, Ready, Broken }

    /// <summary>Is Claude Code there at all, and are our hooks in its settings?</summary>
    public static State Check()
    {
        var dir = Path.GetDirectoryName(SettingsFile);
        if (!Directory.Exists(dir)) return State.NoClaude;
        if (!File.Exists(SettingsFile)) return State.Missing;
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(SettingsFile), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch { return State.Broken; }
        if (root == null) return State.Broken;
        int n = Events.Count(ev => HasOurs(root["hooks"]?[ev] as JsonArray));
        return n == Events.Length ? State.Ready : n == 0 ? State.Missing : State.Partly;
    }

    private static bool HasOurs(JsonArray matchers) =>
        matchers != null && matchers.OfType<JsonObject>().Any(m => (m["hooks"] as JsonArray)?.OfType<JsonObject>()
            .Any(h => (h["url"]?.ToString() ?? "").Contains($"127.0.0.1:{AgentService.Port}")) == true);

    /// <summary>Adds the hooks that are missing. Returns null, or what went wrong in plain words.</summary>
    public static string Install() => Change(root =>
    {
        var hooks = root["hooks"] as JsonObject;
        if (hooks == null) root["hooks"] = hooks = new JsonObject();
        foreach (var ev in Events)
        {
            var list = hooks[ev] as JsonArray;
            if (list == null) hooks[ev] = list = new JsonArray();
            if (HasOurs(list)) continue;
            list.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = Url, ["timeout"] = 1 }),
            });
        }
    });

    /// <summary>Takes only PeakPill's hooks out again; other hooks stay.</summary>
    public static string Remove() => Change(root =>
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var ev in Events)
        {
            if (hooks[ev] is not JsonArray list) continue;
            foreach (var m in list.OfType<JsonObject>().ToList())
            {
                if (m["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.OfType<JsonObject>().Where(h => (h["url"]?.ToString() ?? "").Contains($"127.0.0.1:{AgentService.Port}")).ToList()) hs.Remove(h);
                if (hs.Count == 0) list.Remove(m);
            }
            if (list.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    });

    private static string Change(Action<JsonObject> edit)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
            JsonObject root = new();
            if (File.Exists(SettingsFile))
            {
                var text = File.ReadAllText(SettingsFile);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? throw new JsonException(); }
                    catch { return L.T("Die Claude-Code-Einstellungen lassen sich nicht lesen – bitte von Hand prüfen."); }
                }
                var backup = SettingsFile + ".peakpill-backup";
                if (!File.Exists(backup)) File.Copy(SettingsFile, backup);
            }
            edit(root);
            File.WriteAllText(SettingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return ex.Message;
        }
    }

    /// <summary>The block to paste by hand, for the guide.</summary>
    public static string Snippet()
    {
        var hooks = new JsonObject();
        foreach (var ev in Events)
            hooks[ev] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = Url, ["timeout"] = 1 }) });
        return new JsonObject { ["hooks"] = hooks }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
