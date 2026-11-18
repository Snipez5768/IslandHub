using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

public enum AgentState { Running, Waiting, Done }

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

    private void Changed()
    {
        Raise(nameof(HasPlan));
        Raise(nameof(Progress));
        Raise(nameof(Counter));
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

    /// <summary>Minimal HTTP: read the JSON body, answer "{}" (never influences Claude's decisions).</summary>
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
                var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                await s.WriteAsync(resp);
                if (headerEnd < 0) return;
                var body = Encoding.UTF8.GetString(data.ToArray(), headerEnd + 4, Math.Min(contentLength, data.Count - headerEnd - 4));
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement.Clone();
                Ui.Run(() => Handle(root));
            }
            catch { /* a broken request is just ignored */ }
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal void Handle(JsonElement e)
    {
        var ev = Str(e, "hook_event_name");
        var id = Str(e, "session_id");
        if (ev == null || id == null) return;

        var s = Sessions.FirstOrDefault(x => x.Id == id);
        if (ev == "SessionEnd")
        {
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
                break;

            case "PreToolUse":
                if (s.State != AgentState.Running && s.FinishedAt != null) { s.StartedAt = DateTime.Now; s.FinishedAt = null; }
                s.State = AgentState.Running;
                if (string.IsNullOrEmpty(s.Task)) s.Task = "Claude Code"; // started before PeakPill knew it – the prompt is unknown
                OnTool(s, Str(e, "tool_name") ?? "", e.TryGetProperty("tool_input", out var ti) ? ti : default);
                break;

            case "Notification":
                // permission prompt / question → waiting for you; the idle reminder after a finished turn is not news
                var kind = Str(e, "notification_type") ?? "";
                var msg = Str(e, "message") ?? "";
                if (s.IsDone && (kind.Contains("idle") || msg.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase))) break;
                s.State = AgentState.Waiting;
                s.Step = L.T("Wartet auf dich");
                NeedsYou?.Invoke(s);
                break;

            case "Stop":
                s.State = AgentState.Done;
                s.FinishedAt = DateTime.Now;
                if (s.Total > 0) s.Done = s.Total;
                s.Step = L.T("Fertig");
                UpdateElapsed(s);
                Finished?.Invoke(s);
                break;
        }
        RaiseAll();
        Save();
    }

    private static void OnTool(AgentSession s, string tool, JsonElement input)
    {
        string file = Path.GetFileName(Str(input, "file_path") ?? Str(input, "notebook_path") ?? "");
        switch (tool)
        {
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit":
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
