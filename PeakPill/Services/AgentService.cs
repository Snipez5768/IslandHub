using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

public enum AgentState { Running, Waiting, Done }

/// <summary>
/// Claude Code asks for a permission (C3, 2026-11-18): the island shows "Erlauben" / "Ablehnen"; the hook's answer
/// waits for the click. No click in time → "{}" and Claude Code asks in its own window as always.
/// </summary>
public sealed class PermissionAsk
{
    public AgentSession Session { get; set; }
    public string Tool { get; init; }
    public JsonElement Input { get; init; }
    /// <summary>Claude Code's own suggestion for "always allow" (permission_suggestions) – null when it sent none.</summary>
    public JsonElement? Suggestions { get; init; }
    public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Open => !Answer.Task.IsCompleted;
    /// <summary>A question of Claude's (AskUserQuestion) with answers to pick, instead of allow / deny (9, 2026-11-18).</summary>
    public bool IsQuestion => Tool == "AskUserQuestion";
    public string Question => IsQuestion && FirstQuestion is { } q ? AgentService.StrOf(q, "question") : null;
    public List<string> Options => IsQuestion && FirstQuestion is { } q && q.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
        ? o.EnumerateArray().Select(x => AgentService.StrOf(x, "label")).Where(x => !string.IsNullOrEmpty(x)).ToList() : new();
    /// <summary>"Immer" (10): Claude's suggestion, or for a command the command itself.</summary>
    public bool CanAlways => !IsQuestion && (Suggestions is { ValueKind: JsonValueKind.Array } s && s.GetArrayLength() > 0 || Tool is "Bash" && AgentService.StrOf(Input, "command") is { Length: > 0 });

    private JsonElement? FirstQuestion => Input.ValueKind == JsonValueKind.Object && Input.TryGetProperty("questions", out var qs)
        && qs.ValueKind == JsonValueKind.Array && qs.GetArrayLength() == 1 && qs[0].ValueKind == JsonValueKind.Object
        && !(qs[0].TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True) ? qs[0] : null;

    /// <summary>Only one single-choice question with 2–4 answers fits the island; everything else stays in Claude.</summary>
    public bool Answerable => !IsQuestion || Question != null && Options.Count is >= 2 and <= 4;

    private static string Decision(object decision) =>
        JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision } });

    internal void Allow() => Answer.TrySetResult(Decision(new { behavior = "allow" }));
    internal void Deny() => Answer.TrySetResult(Decision(new { behavior = "deny", message = "The user denied this in PeakPill." }));
    internal void Pass() => Answer.TrySetResult("{}");

    internal void AllowAlways()
    {
        JsonNode rules;
        if (Suggestions is { ValueKind: JsonValueKind.Array } s && s.GetArrayLength() > 0) rules = JsonNode.Parse(s.GetRawText());
        else rules = new JsonArray(new JsonObject
        {
            ["type"] = "addRules", ["behavior"] = "allow", ["destination"] = "localSettings",
            ["rules"] = new JsonArray(new JsonObject { ["toolName"] = Tool, ["ruleContent"] = AgentService.StrOf(Input, "command") }),
        });
        var decision = new JsonObject { ["behavior"] = "allow", ["updatedPermissions"] = rules };
        Answer.TrySetResult(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision } }.ToJsonString());
    }

    /// <summary>The picked answer goes back as the tool's input (AskUserQuestion reads "answers": question → label).</summary>
    internal void AnswerWith(string label)
    {
        var input = JsonNode.Parse(Input.GetRawText()) as JsonObject ?? new JsonObject();
        input["answers"] = new JsonObject { [Question] = label };
        var decision = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input };
        Answer.TrySetResult(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision } }.ToJsonString());
    }
}

/// <summary>One Claude Code session as the island shows it.</summary>
public sealed class AgentSession : ObservableObject
{
    private string _task = "", _step = "", _elapsed = "0:00", _project = "";
    private int _done, _total;
    private AgentState _state;

    public string Id { get; init; }
    public string Project { get => _project; set => Set(ref _project, value); }
    /// <summary>What the user asked for (first line of the prompt).</summary>
    public string Task { get => _task; set => Set(ref _task, value); }
    /// <summary>What Claude is doing right now ("Bearbeitet IslandWindow.xaml.cs").</summary>
    public string Step { get => _step; set => Set(ref _step, value); }
    public AgentState State { get => _state; set { if (Set(ref _state, value)) { Raise(nameof(IsRunning)); Raise(nameof(IsWaiting)); Raise(nameof(IsDone)); } } }
    public bool IsRunning => State == AgentState.Running;
    public bool IsWaiting => State == AgentState.Waiting;
    public bool IsDone => State == AgentState.Done;
    public int Done { get => _done; set { if (Set(ref _done, value)) Changed(); } }
    public int Total { get => _total; set { if (Set(ref _total, value)) Changed(); } }
    public bool HasPlan => Total > 0;
    public double Progress => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
    public string Counter => Total > 0 ? $"{Math.Min(Done, Total)} / {Total}" : "";
    public string Elapsed { get => _elapsed; set => Set(ref _elapsed, value); }

    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public DateTime LastEvent { get; set; } = DateTime.Now;
    /// <summary>The plan's step names, so "advance" can show the next one. Saved with the session: after an PeakPill
    /// restart the short updates ("next step") still have a plan to move along.</summary>
    public List<string> PlanItems { get; set; } = new();
    public int PlanIndex { get; set; } = -1;
    /// <summary>A permission request the island is showing right now (never saved).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public PermissionAsk Ask { get; set; }
    /// <summary>Files changed in this task (11) – what the "done" moment tells.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public HashSet<string> ChangedFiles { get; } = new();
    /// <summary>Claude Code's own record of the session – the tokens are counted from it (12).</summary>
    public string Transcript { get; set; }
    private long _tokens;
    [System.Text.Json.Serialization.JsonIgnore] public long Tokens { get => _tokens; set { if (Set(ref _tokens, value)) { Raise(nameof(TokensText)); Raise(nameof(SideText)); } } }
    [System.Text.Json.Serialization.JsonIgnore] public double ContextPercent { get; set; }
    public string TokensText => Tokens <= 0 ? "" : Tokens >= 1_000_000 ? L.T($"{Tokens / 1e6:0.0} Mio Tokens") : L.T($"{Tokens / 1000.0:0}k Tokens");
    /// <summary>The right side of a row in the Agent tab: "2 / 5 · 1,2 Mio Tokens".</summary>
    public string SideText => string.Join(" · ", new[] { Counter, TokensText }.Where(x => x.Length > 0));
    [System.Text.Json.Serialization.JsonIgnore] internal long TranscriptPos;
    [System.Text.Json.Serialization.JsonIgnore] internal DateTime TokensAt;
    [System.Text.Json.Serialization.JsonIgnore] internal readonly HashSet<string> SeenMessages = new();

    private void Changed()
    {
        Raise(nameof(HasPlan));
        Raise(nameof(Progress));
        Raise(nameof(Counter));
        Raise(nameof(SideText));
    }
}

/// <summary>
/// Receives Claude Code's hook events (HTTP POST on 127.0.0.1:47823) and keeps one <see cref="AgentSession"/>
/// per session: task, current step, plan progress, elapsed time, running / waiting for you / done.
/// </summary>
public sealed class AgentService : ObservableObject
{
    public const int Port = 47823;
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private TcpListener _listener;

    public ObservableCollection<AgentSession> Sessions { get; } = new();
    public event Action<AgentSession> Finished, NeedsYou;
    /// <summary>C3: a permission to answer in the island; Gone: answered elsewhere or no answer in time.</summary>
    public event Action<PermissionAsk> PermissionAsked;
    public event Action<AgentSession> PermissionGone;
    private const int PermissionWaitMs = 45000;
    /// <summary>Questions and plan approvals need more than yes / no – those stay "Claude wartet auf dich" (C1).</summary>
    private static bool NotAskable(string tool) => tool is null or "ExitPlanMode";

    public bool Any => Sessions.Count > 0;
    public bool AnyRunning => Sessions.Any(s => s.IsRunning);
    public bool AnyWaiting => Sessions.Any(s => s.IsWaiting);
    /// <summary>The session the island shows: waiting for you first, then running, then the latest.</summary>
    public AgentSession Primary =>
        Sessions.FirstOrDefault(s => s.IsWaiting) ?? Sessions.FirstOrDefault(s => s.IsRunning) ?? Sessions.OrderByDescending(s => s.LastEvent).FirstOrDefault();

    /// <summary>false = render test: the user's sessions are neither shown nor overwritten.</summary>
    public bool Persist { get; set; } = true;

    /// <summary>Sessions survive an PeakPill restart (otherwise a running Claude only comes back with its next step, without its task).</summary>
    private void Save()
    {
        if (!Persist) return;
        try { Storage.Save("agents.json", Sessions.Where(s => s.Id != "demo").ToList()); } catch { }
    }

    private void Load()
    {
        try
        {
            foreach (var s in Storage.Load<List<AgentSession>>("agents.json") ?? new())
                if (s.Id != null && DateTime.Now - s.LastEvent < TimeSpan.FromMinutes(45) && !(s.IsDone && s.FinishedAt is { } f && DateTime.Now - f > TimeSpan.FromMinutes(20)))
                {
                    UpdateElapsed(s);
                    Sessions.Add(s);
                }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Render test: start from an empty list and never save.</summary>
    public void ForTest()
    {
        Persist = false;
        Sessions.Clear();
        RaiseAll();
    }

    /// <summary>Render test: no real Claude sessions report to this island (the running ones would show up in the film).</summary>
    public void StopListening()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Sessions.Clear();
        RaiseAll();
    }

    public void Start()
    {
        Load();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _ = AcceptLoop();
        }
        catch (Exception ex) { App.Log(ex); } // port taken – the island simply shows no agents
        _t.Tick += (_, _) => Tick();
        _t.Start();
    }

    private async Task AcceptLoop()
    {
        while (_listener != null)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    /// <summary>Minimal HTTP: read the JSON body, answer "{}" – only a permission request gets the user's click from the island.</summary>
    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = 3000;
                var s = c.GetStream();
                var buf = new byte[64 * 1024];
                var data = new List<byte>();
                int headerEnd = -1, contentLength = 0;
                while (true)
                {
                    int n = await s.ReadAsync(buf);
                    if (n <= 0) break;
                    data.AddRange(buf.AsSpan(0, n).ToArray());
                    if (headerEnd < 0)
                    {
                        var text = Encoding.ASCII.GetString(data.ToArray());
                        headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0)
                        {
                            foreach (var line in text[..headerEnd].Split("\r\n"))
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    int.TryParse(line[15..].Trim(), out contentLength);
                        }
                    }
                    if (headerEnd >= 0 && data.Count >= headerEnd + 4 + contentLength) break;
                    if (data.Count > 8 * 1024 * 1024) break;
                }
                string answer = "{}";
                try
                {
                    if (headerEnd >= 0)
                    {
                        var body = Encoding.UTF8.GetString(data.ToArray(), headerEnd + 4, Math.Min(contentLength, data.Count - headerEnd - 4));
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement.Clone();
                        PermissionAsk ask = null;
                        if (Str(root, "hook_event_name") == "PermissionRequest" && !NotAskable(Str(root, "tool_name")))
                            ask = new PermissionAsk
                            {
                                Tool = Str(root, "tool_name"), Input = root.TryGetProperty("tool_input", out var ti) ? ti : default,
                                Suggestions = root.TryGetProperty("permission_suggestions", out var sg) ? sg : null,
                            };
                        if (ask is { Answerable: false }) ask = null; // several questions, many answers: Claude asks itself (C1)
                        Ui.Run(() => Handle(root, ask));
                        if (ask != null)
                        {
                            // the answer waits for the click in the island – no click in time: Claude Code asks itself
                            var first = await Task.WhenAny(ask.Answer.Task, Task.Delay(PermissionWaitMs));
                            if (first != ask.Answer.Task) { ask.Pass(); Ui.Run(() => AskEnded(ask, true)); }
                            answer = await ask.Answer.Task;
                        }
                    }
                }
                catch (Exception ex) { answer = "{}"; App.Log(ex); }
                var bytes = Encoding.UTF8.GetBytes(answer);
                await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                await s.WriteAsync(bytes);
            }
            catch { /* a broken request is just ignored */ }
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static string StrOf(JsonElement e, string name) => Str(e, name);

    internal void Handle(JsonElement e, PermissionAsk ask = null)
    {
        var ev = Str(e, "hook_event_name");
        var id = Str(e, "session_id");
        if (ev == null || id == null) { ask?.Pass(); return; }

        var s = Sessions.FirstOrDefault(x => x.Id == id);
        // Claude went on (answered in its own window) – the question in the island is over
        if (s?.Ask is { } open && ev is not ("Notification" or "PermissionRequest")) { open.Pass(); AskEnded(open, false); }
        if (ev == "SessionEnd")
        {
            ask?.Pass();
            if (s != null) Sessions.Remove(s);
            RaiseAll();
            Save();
            return;
        }
        if (s == null)
        {
            s = new AgentSession { Id = id };
            Sessions.Add(s);
        }
        var quiet = DateTime.Now - s.LastEvent;
        s.LastEvent = DateTime.Now;
        var transcript = Str(e, "transcript_path");
        if (!string.IsNullOrEmpty(transcript) && transcript != s.Transcript) { s.Transcript = transcript; s.TranscriptPos = 0; s.Tokens = 0; s.SeenMessages.Clear(); }
        var cwd = Str(e, "cwd");
        if (!string.IsNullOrEmpty(cwd)) s.Project = Path.GetFileName(cwd.TrimEnd('\\', '/'));

        switch (ev)
        {
            case "UserPromptSubmit":
                // a message sent while Claude is still working joins the running task – it is no new task,
                // and the plan with its "2 / 5" stays (unless the session went silent long ago and missed its Stop)
                if (s.IsRunning && s.HasPlan && quiet < TimeSpan.FromMinutes(10)) break;
                s.Task = FirstLine(Str(e, "prompt"));
                s.Step = L.T("Denkt nach …");
                s.State = AgentState.Running;
                s.StartedAt = DateTime.Now;
                s.FinishedAt = null;
                s.Done = s.Total = 0;
                s.PlanItems.Clear();
                s.PlanIndex = -1;
                s.ChangedFiles.Clear();
                break;

            case "PreToolUse":
                if (s.State != AgentState.Running && s.FinishedAt != null) { s.StartedAt = DateTime.Now; s.FinishedAt = null; }
                s.State = AgentState.Running;
                if (string.IsNullOrEmpty(s.Task)) s.Task = "Claude Code"; // started before PeakPill knew it – the prompt is unknown
                OnTool(s, Str(e, "tool_name") ?? "", e.TryGetProperty("tool_input", out var ti) ? ti : default);
                if (DateTime.Now - s.TokensAt > TimeSpan.FromSeconds(20)) CountTokens(s);
                break;

            case "Notification":
                // permission prompt / question → waiting for you; the idle reminder after a finished turn is not news
                var kind = Str(e, "notification_type") ?? "";
                var msg = Str(e, "message") ?? "";
                if (s.IsDone && (kind.Contains("idle") || msg.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase))) break;
                s.State = AgentState.Waiting;
                s.Step = L.T("Wartet auf dich");
                if (s.Ask == null) NeedsYou?.Invoke(s); // (the island is already asking with its buttons)
                break;

            case "PermissionRequest":
                s.State = AgentState.Waiting;
                s.Step = L.T("Wartet auf dich");
                if (ask != null)
                {
                    s.Ask?.Pass();
                    ask.Session = s;
                    s.Ask = ask;
                    PermissionAsked?.Invoke(ask);  // C3: the island opens with "Erlauben" / "Ablehnen"
                }
                else NeedsYou?.Invoke(s);          // a question: C1 "Claude wartet auf dich"
                break;

            case "Stop":
                s.State = AgentState.Done;
                s.FinishedAt = DateTime.Now;
                if (s.Total > 0) s.Done = s.Total;
                s.Step = L.T("Fertig");
                UpdateElapsed(s);
                CountTokens(s);
                Finished?.Invoke(s);
                break;
        }
        RaiseAll();
        Save();
    }

    /// <summary>The click in the island: allow or deny – Claude goes on right away.</summary>
    public void Decide(PermissionAsk ask, bool allow) =>
        Settle(ask, a => { if (allow) a.Allow(); else a.Deny(); }, allow ? L.T("Erlaubt – macht weiter") : L.T("Abgelehnt"));

    /// <summary>"Immer" (10): allowed, and Claude Code remembers it.</summary>
    public void Always(PermissionAsk ask) => Settle(ask, a => a.AllowAlways(), L.T("Erlaubt – macht weiter"));

    /// <summary>An answer to Claude's question (9).</summary>
    public void Answer(PermissionAsk ask, string label) => Settle(ask, a => a.AnswerWith(label), L.T("Antwort gegeben"));

    /// <summary>"Eigene Antwort": the island steps back, Claude asks in its own window.</summary>
    public void HandBack(PermissionAsk ask)
    {
        if (!ask.Open) return;
        ask.Pass();
        if (ask.Session?.Ask == ask) ask.Session.Ask = null;
        RaiseAll();
    }

    private void Settle(PermissionAsk ask, Action<PermissionAsk> how, string step)
    {
        if (!ask.Open) return;
        how(ask);
        var s = ask.Session;
        if (s == null) return;
        if (s.Ask == ask) s.Ask = null;
        s.State = AgentState.Running;
        s.Step = step;
        RaiseAll();
    }

    /// <summary>Answered in Claude's own window, or no click in time (then Claude asks there: C1 reminds of it).</summary>
    private void AskEnded(PermissionAsk ask, bool timedOut)
    {
        var s = ask.Session;
        if (s == null || s.Ask != ask) return;
        s.Ask = null;
        PermissionGone?.Invoke(s);
        if (timedOut && s.IsWaiting) NeedsYou?.Invoke(s);
        RaiseAll();
    }

    /// <summary>What Claude wants, for the card: a title and the detail underneath.</summary>
    public static (string Title, string Detail) Describe(PermissionAsk a)
    {
        string S(string n) => Str(a.Input, n);
        string Short(string t, int n = 70) { t = FirstLine(t ?? ""); return t.Length > n ? t[..(n - 1)] + "…" : t; }
        return a.Tool switch
        {
            "AskUserQuestion" => (a.Question ?? L.T("Claude hat eine Frage"), ""),
            "Bash" or "PowerShell" => (L.T("Claude möchte einen Befehl ausführen"), Short(S("command"))),
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => (L.T("Claude möchte eine Datei ändern"), Path.GetFileName(S("file_path") ?? S("notebook_path") ?? "")),
            "Read" => (L.T("Claude möchte eine Datei lesen"), Short(S("file_path"))),
            "Glob" or "Grep" or "LS" => (L.T("Claude möchte einen Ordner durchsuchen"), Short(S("path") ?? S("pattern"))),
            "WebFetch" => (L.T("Claude möchte eine Webseite öffnen"), Short(S("url"))),
            "WebSearch" => (L.T("Claude möchte im Internet suchen"), Short(S("query"))),
            _ => (L.T($"Claude möchte {a.Tool} benutzen"), ""),
        };
    }

    private static void OnTool(AgentSession s, string tool, JsonElement input)
    {
        string file = Path.GetFileName(Str(input, "file_path") ?? Str(input, "notebook_path") ?? "");
        switch (tool)
        {
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit":
                if (file.Length > 0) s.ChangedFiles.Add(file);
                s.Step = L.T($"Bearbeitet {file}"); break;
            case "Read":
                s.Step = L.T($"Liest {file}"); break;
            case "Bash" or "PowerShell":
                s.Step = FirstLine(Str(input, "description")) is { Length: > 0 } d ? d : L.T("Führt einen Befehl aus"); break;
            case "Grep" or "Glob":
                s.Step = L.T("Durchsucht den Code"); break;
            case "WebSearch" or "WebFetch":
                s.Step = L.T("Sucht im Web"); break;
            case "Agent" or "Task":
                s.Step = L.T("Startet einen Helfer"); break;
            case "AskUserQuestion":
                s.Step = L.T("Hat eine Frage an dich"); break;
            case "TodoWrite":
                ReadPlan(s, input, "todos"); break;
            case "mcp__ccd_turn__update_plan":
                ReadPlan(s, input, "items"); break;
            case "mcp__ccd_turn__wrap_up":
                s.Step = L.T("Fasst zusammen"); break;
            default:
                s.Step = tool.StartsWith("mcp__", StringComparison.Ordinal) ? L.T("Nutzt ein Werkzeug") : tool; break;
        }
    }

    /// <summary>Progress from Claude's task list: completed / total, the step in progress as the current step.</summary>
    private static void ReadPlan(AgentSession s, JsonElement input, string listName)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        if (input.TryGetProperty(listName, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            s.PlanItems.Clear();
            int done = 0, current = -1, i = 0;
            foreach (var it in list.EnumerateArray())
            {
                var text = Str(it, "content") ?? Str(it, "activeForm") ?? "";
                var status = Str(it, "status") ?? "";
                s.PlanItems.Add(text);
                if (status == "completed") done++;
                if (status == "in_progress" && current < 0) current = i;
                i++;
            }
            s.Total = s.PlanItems.Count;
            s.Done = done;
            s.PlanIndex = current;
            if (current >= 0) s.Step = s.PlanItems[current];
            return;
        }
        // short updates: {"advance":true} or {"done":[2],"start":4}
        if (s.Total == 0) return;
        if (input.TryGetProperty("advance", out var adv) && adv.ValueKind == JsonValueKind.True)
        {
            s.Done = Math.Min(s.Total, s.Done + 1);
            s.PlanIndex = Math.Min(s.PlanItems.Count - 1, s.PlanIndex + 1);
        }
        if (input.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.Array)
            s.Done = Math.Min(s.Total, s.Done + d.GetArrayLength());
        // Total can be known while the step names are not (e.g. a plan set before PeakPill started) – then there is nothing to clamp to
        if (input.TryGetProperty("start", out var st) && st.TryGetInt32(out var n) && s.PlanItems.Count > 0)
            s.PlanIndex = Math.Clamp(n - 1, 0, s.PlanItems.Count - 1);
        if (s.PlanIndex >= 0 && s.PlanIndex < s.PlanItems.Count) s.Step = s.PlanItems[s.PlanIndex];
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 90 ? line[..87] + "…" : line;
    }

    private static void UpdateElapsed(AgentSession s)
    {
        var d = (s.FinishedAt ?? DateTime.Now) - s.StartedAt;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        s.Elapsed = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
    }

    private void Tick()
    {
        bool changed = false;
        foreach (var s in Sessions.ToList())
        {
            if (s.IsRunning) UpdateElapsed(s);
            // finished long ago, or silent for very long (crashed / closed without SessionEnd) → gone
            if (s.IsDone && s.FinishedAt is { } f && DateTime.Now - f > TimeSpan.FromMinutes(20) ||
                DateTime.Now - s.LastEvent > TimeSpan.FromMinutes(45))
            {
                Sessions.Remove(s);
                changed = true;
            }
        }
        if (changed || AnyRunning) RaiseAll();
        if (changed) Save();
    }

    private void RaiseAll()
    {
        Raise(nameof(Any));
        Raise(nameof(AnyRunning));
        Raise(nameof(AnyWaiting));
        Raise(nameof(Primary));
    }

    /// <summary>Brings the Claude app (desktop app or the terminal it runs in) to the front.</summary>
    /// <summary>
    /// Counts the session's tokens from Claude Code's own record (12, 2026-11-18): every answer carries its usage –
    /// input, output and the cached parts. Only the new lines are read each time; one answer can span several lines.
    /// </summary>
    private void CountTokens(AgentSession s)
    {
        var path = s.Transcript;
        s.TokensAt = DateTime.Now;
        if (string.IsNullOrEmpty(path)) return;
        long from = s.TranscriptPos;
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) return;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= from) return;
                fs.Seek(from, SeekOrigin.Begin);
                var bytes = new byte[fs.Length - from];
                int read = 0;
                while (read < bytes.Length) { int n = fs.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                int end = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                if (end < 0) return; // no whole line yet
                var text = Encoding.UTF8.GetString(bytes, 0, end + 1);
                long added = 0;
                double context = -1;
                var seen = new List<string>();
                foreach (var line in text.Split('\n'))
                {
                    if (!line.Contains("\"usage\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (!doc.RootElement.TryGetProperty("message", out var m) || !m.TryGetProperty("usage", out var u)) continue;
                        var id = Str(m, "id");
                        if (id != null && (s.SeenMessages.Contains(id) || seen.Contains(id))) continue;
                        if (id != null) seen.Add(id);
                        long N(string k) => u.TryGetProperty(k, out var v) && v.TryGetInt64(out var x) ? x : 0;
                        long input = N("input_tokens"), cacheNew = N("cache_creation_input_tokens"), cacheRead = N("cache_read_input_tokens");
                        added += input + N("output_tokens") + cacheNew + cacheRead;
                        context = input + cacheNew + cacheRead;
                    }
                    catch { }
                }
                long pos = from + end + 1;
                Ui.Run(() =>
                {
                    if (s.TranscriptPos != from) return; // counted meanwhile
                    s.TranscriptPos = pos;
                    foreach (var id in seen) s.SeenMessages.Add(id);
                    s.Tokens += added;
                    if (context >= 0) s.ContextPercent = Math.Min(100, 100 * context / (context > 200_000 ? 1_000_000 : 200_000));
                });
            }
            catch { }
        });
    }

    /// <summary>
    /// The window of this one session (8b): a terminal, VS Code or Claude window whose title names the project or the
    /// task – otherwise any Claude Code window, as before.
    /// </summary>
    public static bool OpenHost(AgentSession s)
    {
        if (s == null) return OpenHost();
        var marks = new[] { s.Project, s.Task?.Split(' ').FirstOrDefault(w => w.Length > 4) }.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude", "WindowsTerminal", "Code", "cmd", "powershell", "pwsh", "conhost", "OpenConsole" };
        IntPtr hit = IntPtr.Zero;
        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.Length == 0 || !marks.Any(m => title.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
                GetWindowThreadProcessId(h, out var pid);
                try { using var p = Process.GetProcessById((int)pid); if (!hosts.Contains(p.ProcessName)) return true; } catch { return true; }
                hit = h;
                return false;
            }, IntPtr.Zero);
        }
        catch { }
        return hit != IntPtr.Zero ? PrivacyService.Focus(hit) : OpenHost();
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static bool OpenHost()
    {
        foreach (var name in new[] { "claude", "Claude", "WindowsTerminal", "Code" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return PrivacyService.Focus(p.MainWindowHandle);
                }
            }
        }
        return false;
    }

    /// <summary>Render test only.</summary>
    public void Demo(string task, string step, int done, int total, int seconds, AgentState state, string id = "demo", string project = "dynamic island")
    {
        var s = Sessions.FirstOrDefault(x => x.Id == id) ?? new AgentSession { Id = id };
        if (!Sessions.Contains(s)) Sessions.Add(s);
        s.Project = project; s.Task = task; s.Step = step; s.Total = total; s.Done = done; s.State = state;
        s.StartedAt = DateTime.Now.AddSeconds(-seconds); s.FinishedAt = state == AgentState.Done ? DateTime.Now : null;
        UpdateElapsed(s);
        RaiseAll();
    }

    public void EndDemo()
    {
        foreach (var s in Sessions.Where(x => x.Id.StartsWith("demo")).ToList()) Sessions.Remove(s);
        RaiseAll();
    }
}
