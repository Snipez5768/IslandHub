using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using IslandHub.Core;

namespace IslandHub.Services;

/// <summary>
/// OBS Studio via its built-in obs-websocket (v5, ws://127.0.0.1:4455): recording / streaming state and time,
/// start / stop, scene switching. Connects by itself while OBS runs; the password (if OBS asks for one) is
/// stored encrypted in the settings.
/// </summary>
public sealed class ObsService : ObservableObject
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private ClientWebSocket _ws;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private bool _connected, _recording, _streaming, _busy;
    private DateTime _recStart, _streamStart, _nextTry;
    private string _scene, _status = "OBS läuft nicht", _recTime = "", _streamTime = "";

    public ObservableCollection<string> Scenes { get; } = new();
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public bool Recording { get => _recording; private set { if (Set(ref _recording, value)) Raise(nameof(Active)); } }
    public bool Streaming { get => _streaming; private set { if (Set(ref _streaming, value)) Raise(nameof(Active)); } }
    public bool Active => Recording || Streaming;
    public string Scene { get => _scene; private set => Set(ref _scene, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string RecordTime { get => _recTime; private set => Set(ref _recTime, value); }
    public string StreamTime { get => _streamTime; private set => Set(ref _streamTime, value); }

    public void Start()
    {
        _t.Tick += async (_, _) => await Tick();
        _t.Start();
    }

    private async Task Tick()
    {
        if (Recording) RecordTime = Fmt(DateTime.Now - _recStart);
        if (Streaming) StreamTime = Fmt(DateTime.Now - _streamStart);
        if (Connected || _busy || DateTime.Now < _nextTry) return;
        if (Process.GetProcessesByName("obs64").Length == 0 && Process.GetProcessesByName("obs").Length == 0)
        {
            Status = "OBS läuft nicht";
            return;
        }
        _busy = true;
        _nextTry = DateTime.Now.AddSeconds(10);
        try { await Connect(); }
        catch (Exception ex) { Status = ex.Message; Close(); }
        finally { _busy = false; }
    }

    private static string Fmt(TimeSpan d) => d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    private async Task Connect()
    {
        _ws = new ClientWebSocket();
        using (var cts = new CancellationTokenSource(3000))
        {
            try { await _ws.ConnectAsync(new Uri("ws://127.0.0.1:4455"), cts.Token); }
            catch { throw new Exception("OBS-WebSocket ist aus (OBS → Werkzeuge → WebSocket-Servereinstellungen)"); }
        }
        var hello = await Receive() ?? throw new Exception("OBS antwortet nicht");
        var d = hello["d"];
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 4 | 64 }; // Scenes | Outputs
        if (d?["authentication"] is JsonNode auth)
        {
            var pw = DiscordService.Unprotect(App.Settings.ObsPassword);
            if (string.IsNullOrEmpty(pw)) throw new Exception("OBS will ein Passwort – trag es in den Einstellungen ein");
            string salt = auth["salt"]!.GetValue<string>(), challenge = auth["challenge"]!.GetValue<string>();
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pw + salt)));
            identify["authentication"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }
        await SendRaw(new JsonObject { ["op"] = 1, ["d"] = identify });
        var ok = await Receive();
        if (ok?["op"]?.GetValue<int>() != 2) throw new Exception("OBS-Passwort falsch");

        Connected = true;
        Status = "Verbunden";
        _ = ReadLoop();
        await RefreshAll();
    }

    private async Task RefreshAll()
    {
        var rec = await Request("GetRecordStatus");
        var str = await Request("GetStreamStatus");
        ApplyOutput(rec, true);
        ApplyOutput(str, false);
        var sl = await Request("GetSceneList");
        Ui.Run(() =>
        {
            Scenes.Clear();
            if (sl?["scenes"] is JsonArray arr)
                foreach (var s in arr.Reverse()) // OBS lists the bottom scene first
                    if (s?["sceneName"]?.GetValue<string>() is { } n) Scenes.Add(n);
            Scene = sl?["currentProgramSceneName"]?.GetValue<string>();
        });
    }

    private void ApplyOutput(JsonNode r, bool record)
    {
        if (r == null) return;
        bool active = r["outputActive"]?.GetValue<bool>() ?? false;
        double ms = r["outputDuration"]?.GetValue<double>() ?? 0;
        Ui.Run(() =>
        {
            if (record) { _recStart = DateTime.Now.AddMilliseconds(-ms); Recording = active; RecordTime = active ? Fmt(DateTime.Now - _recStart) : ""; }
            else { _streamStart = DateTime.Now.AddMilliseconds(-ms); Streaming = active; StreamTime = active ? Fmt(DateTime.Now - _streamStart) : ""; }
        });
    }

    private async Task ReadLoop()
    {
        try
        {
            while (_ws is { State: WebSocketState.Open })
            {
                var m = await Receive();
                if (m == null) break;
                int op = m["op"]?.GetValue<int>() ?? -1;
                var d = m["d"];
                if (op == 7)
                {
                    var id = d?["requestId"]?.GetValue<string>();
                    TaskCompletionSource<JsonNode> tcs = null;
                    if (id != null) lock (_pending) _pending.TryGetValue(id, out tcs);
                    tcs?.TrySetResult(d?["responseData"] ?? new JsonObject());
                }
                else if (op == 5)
                {
                    var data = d?["eventData"];
                    switch (d?["eventType"]?.GetValue<string>())
                    {
                        case "RecordStateChanged": OnState(data, true); break;
                        case "StreamStateChanged": OnState(data, false); break;
                        case "CurrentProgramSceneChanged": Ui.Run(() => Scene = data?["sceneName"]?.GetValue<string>()); break;
                        case "SceneListChanged": _ = RefreshAll(); break;
                    }
                }
            }
        }
        catch { }
        Ui.Run(() => { Close(); Status = "OBS geschlossen"; });
    }

    private void OnState(JsonNode data, bool record)
    {
        var state = data?["outputState"]?.GetValue<string>() ?? "";
        bool active = data?["outputActive"]?.GetValue<bool>() ?? false;
        if (state.EndsWith("STARTING") || state.EndsWith("STOPPING")) return;
        Ui.Run(() =>
        {
            if (record) { if (active && !Recording) _recStart = DateTime.Now; Recording = active; if (!active) RecordTime = ""; }
            else { if (active && !Streaming) _streamStart = DateTime.Now; Streaming = active; if (!active) StreamTime = ""; }
        });
    }

    private void Close()
    {
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        Connected = false;
        Recording = Streaming = false;
        RecordTime = StreamTime = "";
        Ui.Run(() => Scenes.Clear());
    }

    // ---------- actions ----------

    public Task ToggleRecord() => Request("ToggleRecord");
    public Task ToggleStream() => Request("ToggleStream");
    public Task SetScene(string name) => Request("SetCurrentProgramScene", new JsonObject { ["sceneName"] = name });

    /// <summary>Settings changed (password) → reconnect.</summary>
    public void Reconnect()
    {
        Close();
        _nextTry = DateTime.MinValue;
    }

    // ---------- protocol ----------

    private async Task<JsonNode> Request(string type, JsonObject data = null)
    {
        if (_ws is not { State: WebSocketState.Open }) return null;
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data != null) d["requestData"] = data;
        await SendRaw(new JsonObject { ["op"] = 6, ["d"] = d });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(4000));
        lock (_pending) _pending.Remove(id);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    private async Task SendRaw(JsonNode msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _send.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _send.Release(); }
    }

    private async Task<JsonNode> Receive()
    {
        var buf = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }
}
