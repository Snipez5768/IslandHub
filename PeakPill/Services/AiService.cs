using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>One line of the KI chat.</summary>
public sealed class AiMessage : ObservableObject
{
    private string _text = "";
    private bool _streaming, _failed;

    public bool IsUser { get; init; }
    public string Text { get => _text; set => Set(ref _text, value ?? ""); }
    /// <summary>The answer is still arriving.</summary>
    public bool IsStreaming { get => _streaming; set => Set(ref _streaming, value); }
    /// <summary>No answer came (no key, no network, declined …): <see cref="Text"/> then says why, and the turn is not sent again.</summary>
    public bool Failed { get => _failed; set => Set(ref _failed, value); }
    /// <summary>Who answered ("Claude · Opus 5.5").</summary>
    public string By { get; set; }
    /// <summary>Pictures sent with a question (2026-11-17): PNG files in the data folder (ai-images).</summary>
    public List<string> Images { get; set; } = new();

    private string _steps = "";
    /// <summary>What the model did on the island while answering ("Liest die Zwischenablage" …), one per line.</summary>
    public string Steps { get => _steps; set => Set(ref _steps, value ?? ""); }

    // a question to the user inside the answer ("… an Lenas iPhone senden?" – Senden / Abbrechen)
    private string _question;
    private TaskCompletionSource<bool> _answer;
    public string Question { get => _question; private set => Set(ref _question, value); }
    public string QuestionYes { get; private set; }

    public void Ask(string question, string yes, TaskCompletionSource<bool> answer)
    {
        _answer?.TrySetResult(false);
        _answer = answer;
        QuestionYes = yes;
        Question = question;
    }

    public void Reply(bool yes)
    {
        var a = _answer;
        _answer = null;
        Question = null;
        a?.TrySetResult(yes);
    }
}

/// <summary>
/// The KI page (2026-11-17): a small chat with Claude, ChatGPT or Gemini on the user's own API key. Keys are
/// stored encrypted for this Windows user; the chat itself stays on this PC (ai-chat.json) and goes only to the
/// service that answers.
/// </summary>
public sealed class AiService : ObservableObject, IAiHost
{
    /// <summary>The same for the whole chat (it is part of what the answers build on).</summary>
    public static string SystemPrompt => L.English
        ? "You are the AI assistant in PeakPill, a small island at the top of the Windows screen. " +
          "Answer in the language of the question, friendly, clear and rather brief: the window is narrow. " +
          "Short paragraphs, Markdown sparingly (bold, lists and code blocks work). " +
          "You can act on the user's island with your functions (clipboard, LocalSend to their phone or PCs, timer, reminders, music, its pages). " +
          "Use them when the user asks for such an action; check island_status first when you need device names or the current time. " +
          "Sending with LocalSend asks the user to confirm – just call it."
        : "Du bist der KI-Assistent in PeakPill, einer kleinen Insel oben am Windows-Bildschirm. " +
          "Antworte in der Sprache der Frage (meist Deutsch), freundlich, klar und eher knapp: das Fenster ist schmal. " +
          "Kurze Absätze, Markdown sparsam (fett, Listen, Code-Blöcke gehen). " +
          "Du kannst mit deinen Funktionen die Insel des Nutzers bedienen (Zwischenablage, LocalSend an Handy oder PCs, Timer, Erinnerungen, Musik, ihre Seiten). " +
          "Nutze sie, wenn der Nutzer so etwas möchte; frag zuerst island_status ab, wenn du Gerätenamen oder die aktuelle Zeit brauchst. " +
          "Senden mit LocalSend fragt den Nutzer selbst nach einer Bestätigung – ruf es einfach auf.";

    private const string FileName = "ai-chat.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static AppSettings S => App.Settings;

    public ObservableCollection<AiMessage> Messages { get; } = new();
    public event Action<AiMessage> Answered;

    private CancellationTokenSource _cts;
    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    // ---------- services, keys, models ----------

    public static readonly AiProvider[] All = { AiProvider.Claude, AiProvider.ChatGpt, AiProvider.Gemini };

    public static string Name(AiProvider p) => p switch { AiProvider.ChatGpt => "ChatGPT", AiProvider.Gemini => "Gemini", _ => "Claude" };

    public static string DefaultModel(AiProvider p) => p switch
    {
        AiProvider.ChatGpt => "gpt-5",
        AiProvider.Gemini => "gemini-2.5-flash",
        _ => "claude-opus-5-5",
    };

    /// <summary>Where the user gets a key.</summary>
    public static string KeyUrl(AiProvider p) => p switch
    {
        AiProvider.ChatGpt => "https://platform.openai.com/api-keys",
        AiProvider.Gemini => "https://aistudio.google.com/apikey",
        _ => "https://console.anthropic.com/settings/keys",
    };

    public static string KeyHint(AiProvider p) => p switch
    {
        AiProvider.ChatGpt => L.T("beginnt mit sk-"),
        AiProvider.Gemini => L.T("beginnt mit AIza"),
        _ => L.T("beginnt mit sk-ant-"),
    };

    public static string GetKey(AiProvider p) =>
        S.AiKeysProtected.TryGetValue(p.ToString(), out var c) ? DiscordService.Unprotect(c) : "";

    public static void SetKey(AiProvider p, string key)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) S.AiKeysProtected.Remove(p.ToString());
        else S.AiKeysProtected[p.ToString()] = DiscordService.Protect(key);
    }

    public static bool HasKey(AiProvider p) => GetKey(p).Length > 0;

    public static string GetModel(AiProvider p) =>
        S.AiModels.TryGetValue(p.ToString(), out var m) && !string.IsNullOrWhiteSpace(m) ? m : DefaultModel(p);

    public static void SetModel(AiProvider p, string model)
    {
        if (string.IsNullOrWhiteSpace(model)) S.AiModels.Remove(p.ToString());
        else S.AiModels[p.ToString()] = model.Trim();
    }

    /// <summary>"Claude · Opus 5.5", "ChatGPT · gpt-5".</summary>
    public static string Label(AiProvider p) => L.T($"{Name(p)} · {PrettyModel(GetModel(p))}");

    public static string PrettyModel(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (!id.StartsWith("claude-")) return id;
        // claude-opus-5-5 → Opus 5.5, claude-haiku-4-5 → Haiku 4.5
        var parts = id["claude-".Length..].Split('-');
        if (parts.Length < 2 || !char.IsDigit(parts[1][0])) return id;
        var name = char.ToUpper(parts[0][0]) + parts[0][1..];
        var ver = string.Join(".", parts.Skip(1).TakeWhile(x => x.Length <= 2 && x.All(char.IsDigit)));
        return $"{name} {ver}";
    }

    // ---------- the chat ----------

    private sealed class Saved
    {
        public bool IsUser { get; set; }
        public string Text { get; set; }
        public bool Failed { get; set; }
        public string By { get; set; }
        public List<string> Images { get; set; }
    }

    public void Load()
    {
        try
        {
            foreach (var m in Storage.Load<List<Saved>>(FileName))
                if (!string.IsNullOrEmpty(m.Text) || m.Images is { Count: > 0 })
                    Messages.Add(new AiMessage { IsUser = m.IsUser, Text = m.Text ?? "", Failed = m.Failed, By = m.By, Images = m.Images?.Where(File.Exists).ToList() ?? new() });
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var keep = Messages.Where(m => !m.IsStreaming).TakeLast(60).ToList();
            Storage.Save(FileName, keep.Select(m => new Saved { IsUser = m.IsUser, Text = m.Text, Failed = m.Failed, By = m.By, Images = m.Images.Count > 0 ? m.Images : null }).ToList());
            // pictures no message refers to any more go
            var used = keep.SelectMany(m => m.Images).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(ImageDir))
                foreach (var f in Directory.GetFiles(ImageDir, "*.png"))
                    if (!used.Contains(f) && File.GetCreationTime(f) < DateTime.Now.AddMinutes(-30)) try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // ---------- pictures (screenshot, pasted photo) ----------

    public static string ImageDir => Storage.PathOf("ai-images");

    /// <summary>Keeps a picture for the chat: at most 1800 px on the long side, as PNG. Returns its file.</summary>
    public static string StoreImage(System.Windows.Media.Imaging.BitmapSource src)
    {
        Directory.CreateDirectory(ImageDir);
        double scale = Math.Min(1, 1800.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        System.Windows.Media.Imaging.BitmapSource bmp = src;
        if (scale < 1) bmp = new System.Windows.Media.Imaging.TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        var path = Path.Combine(ImageDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png");
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    private static string Base64(string path) => Convert.ToBase64String(File.ReadAllBytes(path));

    /// <summary>What a question with only a picture asks.</summary>
    private static string PictureOnly => L.English ? "What’s in this picture?" : "Was ist auf dem Bild?";

    public void NewChat()
    {
        Stop();
        Messages.Clear();
        Save();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>The last answer failed: ask the same question again.</summary>
    public async Task RetryAsync()
    {
        if (Busy || Messages.Count < 2 || !Messages[^1].Failed || !Messages[^2].IsUser) return;
        var question = Messages[^2].Text;
        var pics = Messages[^2].Images;
        Messages.RemoveAt(Messages.Count - 1);
        Messages.RemoveAt(Messages.Count - 1);
        await SendAsync(question, pics);
    }

#if DEBUG
    /// <summary>Film test: a made-up exchange word by word – nothing is sent and nothing saved.</summary>
    public async Task DemoAsync(string question, string answer, string by, int msPerWord, bool fail = false)
    {
        Messages.Add(new AiMessage { IsUser = true, Text = question });
        var a = new AiMessage { IsStreaming = true, By = by };
        Messages.Add(a);
        Busy = true;
        await Task.Delay(900);
        if (fail) { a.Failed = true; a.Text = answer; }
        else foreach (var w in System.Text.RegularExpressions.Regex.Split(answer, @"(?<=\s)")) { a.Text += w; await Task.Delay(msPerWord); }
        a.IsStreaming = false;
        Busy = false;
        Answered?.Invoke(a);
    }
#endif

    // ---------- the island for the model (IAiHost) ----------

    private AiMessage _answering;
    /// <summary>Set by the island window: opens one of its pages.</summary>
    public Action<string> PageOpener;

    public void OpenPage(string tab) => Ui.Run(() => PageOpener?.Invoke(tab));

    /// <summary>A card in the answer with the question and two buttons (nothing leaves the PC without a yes).</summary>
    public Task<bool> ConfirmAsync(string question, string yes)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = _answering;
        if (m == null) { tcs.SetResult(false); return tcs.Task; }
        Ui.Run(() => m.Ask(question, yes, tcs));
        return tcs.Task;
    }

    private static string StepText(string tool) => tool switch
    {
        "island_status" => L.T("Schaut auf die Insel"),
        "get_clipboard" => L.T("Liest die Zwischenablage"),
        "copy_to_clipboard" => L.T("Kopiert in die Zwischenablage"),
        "localsend_send" => L.T("Sendet mit LocalSend"),
        "start_timer" => L.T("Stellt den Timer"),
        "add_reminder" => L.T("Legt eine Erinnerung an"),
        "media" => L.T("Steuert die Musik"),
        "open_page" => L.T("Öffnet eine Seite"),
        "add_note" => L.T("Schreibt eine Notiz"),
        "get_notes" => L.T("Liest deine Notizen"),
        _ => tool,
    };

    /// <summary>The user's last question (↑ in the empty field brings it back).</summary>
    public string LastQuestion => Messages.LastOrDefault(m => m.IsUser)?.Text ?? "";

    public async Task SendAsync(string text, IList<string> images = null)
    {
        text = text?.Trim() ?? "";
        var pics = images?.Where(File.Exists).ToList() ?? new List<string>();
        if ((text.Length == 0 && pics.Count == 0) || Busy) return;
        var provider = S.AiProvider;
        var key = GetKey(provider);
        var model = GetModel(provider);
        var history = History();
        Messages.Add(new AiMessage { IsUser = true, Text = text, Images = pics });
        history.Add(Messages[^1]);
        var answer = new AiMessage { IsStreaming = true, By = Label(provider) };
        Messages.Add(answer);

        Busy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            if (key.Length == 0) throw new AiException(L.T($"Noch kein API-Schlüssel für {Name(provider)}. Trag ihn in den Einstellungen unter „KI“ ein."));
            void Add(string piece) { if (!string.IsNullOrEmpty(piece)) answer.Text += piece; }
            // the island's functions (2026-11-17): the model may call them; each call shows as a small line
            _answering = answer;
            var turn = new Turn { Add = Add, Tools = AiTools.All(this) };
            turn.Run = async (name, args) =>
            {
                var tool = turn.Tools.FirstOrDefault(t => t.Name == name);
                Ui.Run(() => answer.Steps = (answer.Steps.Length > 0 ? answer.Steps + "\n" : "") + StepText(name));
                if (tool == null) return "{\"ok\":false,\"error\":\"unknown function\"}";
                try { return await tool.Run(args); }
                catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
            };
            switch (provider)
            {
                case AiProvider.ChatGpt: await StreamOpenAi(history, key, model, turn, ct); break;
                case AiProvider.Gemini: await StreamGemini(history, key, model, turn, ct); break;
                default: await StreamClaude(history, key, model, turn, ct); break;
            }
            if (answer.Text.Trim().Length == 0 && answer.Steps.Length > 0) answer.Text = L.T("Erledigt.");
            if (answer.Text.Trim().Length == 0) throw new AiException(L.T("Keine Antwort bekommen. Versuch es noch einmal."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (answer.Text.Trim().Length == 0) { answer.Failed = true; answer.Text = L.T("Abgebrochen."); }
            else answer.Text = answer.Text.TrimEnd() + " …";
        }
        catch (AiException ex) { answer.Failed = true; answer.Text = ex.Message; }
        catch (AnthropicUnauthorizedException) { answer.Failed = true; answer.Text = L.T("Der Claude-Schlüssel stimmt nicht. Prüf ihn in den Einstellungen unter „KI“."); }
        catch (AnthropicNotFoundException) { answer.Failed = true; answer.Text = L.T($"Das Modell „{model}“ gibt es nicht (mehr). Wähl in den Einstellungen ein anderes."); }
        catch (AnthropicRateLimitException) { answer.Failed = true; answer.Text = L.T("Zu viele Anfragen gerade. Warte einen Moment."); }
        catch (AnthropicOverloadedException) { answer.Failed = true; answer.Text = L.T("Claude ist gerade überlastet. Versuch es gleich noch einmal."); }
        catch (AnthropicApiException ex) { answer.Failed = true; answer.Text = "Claude: " + Short(ex.Message); }
        catch (AnthropicIOException) { answer.Failed = true; answer.Text = L.T("Keine Verbindung. Bist du online?"); }
        catch (HttpRequestException) { answer.Failed = true; answer.Text = L.T("Keine Verbindung. Bist du online?"); }
        catch (TaskCanceledException) { answer.Failed = true; answer.Text = L.T("Die Antwort hat zu lange gedauert."); }
        catch (Exception ex) { answer.Failed = true; answer.Text = L.T("Das hat nicht geklappt: ") + Short(ex.Message); }
        finally
        {
            answer.IsStreaming = false;
            Busy = false;
            _cts?.Dispose();
            _cts = null;
            Save();
            Answered?.Invoke(answer);
        }
    }

    /// <summary>Earlier turns that got an answer, in order – a question whose answer failed is left out with it.</summary>
    private List<AiMessage> History()
    {
        var list = new List<AiMessage>();
        for (int i = 0; i + 1 < Messages.Count; i++)
        {
            var q = Messages[i];
            var a = Messages[i + 1];
            if (q.IsUser && !a.IsUser && !a.Failed && a.Text.Length > 0) { list.Add(q); list.Add(a); i++; }
        }
        return list;
    }

    private static string Short(string s)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] + " …" : s;
    }

    private sealed class AiException : Exception { public AiException(string m) : base(m) { } }

    // ---------- Claude (official Anthropic SDK) ----------

    /// <summary>Models where a declined request is answered by another model in the same call (server-side fallback).</summary>
    private static readonly string[] WithFallback = { "claude-fable-5-2", "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5" };

    /// <summary>One answer in progress: where the words go and which island functions the model may call.</summary>
    private sealed class Turn
    {
        public Action<string> Add;
        public List<AiTool> Tools = new();
        public Func<string, JsonObject, Task<string>> Run;
        public const int MaxRounds = 8;
    }

    private static Dictionary<string, JsonElement> ToElements(JsonObject o) =>
        o.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    private static async Task StreamClaude(List<AiMessage> history, string key, string model, Turn turn, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = key };
        bool fallback = WithFallback.Contains(model);
        var messages = history.Select(m => m.IsUser
            ? (BetaMessageParam)new BetaUserMessageParam { Content = ClaudeContent(m) }
            : new BetaAssistantMessageParam { Content = m.Text }).ToList();
        var tools = turn.Tools.Select(t => (BetaToolUnion)new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new()
            {
                Properties = ToElements(t.Parameters["properties"]!.AsObject()),
                Required = t.Parameters["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToList(),
            },
        }).ToList();

        for (int round = 0; round < Turn.MaxRounds; round++)
        {
            var p = new MessageCreateParams
            {
                Model = model,
                MaxTokens = 64000,
                System = SystemPrompt,
                CacheControl = new BetaCacheControlEphemeral(),
                Messages = messages.ToList(),
                Tools = tools.Count > 0 ? tools : null,
                Betas = fallback ? ["server-side-fallback-2026-07-01"] : null,
                Fallbacks = fallback ? new Default() : null,
            };
            // the blocks of this reply, gathered from the stream (thinking must go back unchanged with tool results)
            var blocks = new SortedDictionary<long, (string Kind, StringBuilder Text, string Id, string Name, string Signature)>();
            bool refused = false;
            await foreach (var ev in client.Beta.Messages.CreateStreaming(p, ct))
            {
                if (ev.TryPickContentBlockStart(out var s))
                {
                    var cb = s.ContentBlock;
                    if (cb.TryPickBetaToolUse(out var tu)) blocks[s.Index] = ("tool", new StringBuilder(), tu.ID, tu.Name, null);
                    else if (cb.TryPickBetaThinking(out var th)) blocks[s.Index] = ("thinking", new StringBuilder(th.Thinking ?? ""), null, null, th.Signature);
                    else if (cb.TryPickBetaRedactedThinking(out var rd)) blocks[s.Index] = ("redacted", new StringBuilder(rd.Data ?? ""), null, null, null);
                    else if (cb.TryPickBetaText(out var tx)) { blocks[s.Index] = ("text", new StringBuilder(tx.Text ?? ""), null, null, null); turn.Add(tx.Text); }
                }
                else if (ev.TryPickContentBlockDelta(out var d))
                {
                    if (!blocks.TryGetValue(d.Index, out var b)) continue;
                    if (d.Delta.TryPickText(out var t)) { b.Text.Append(t.Text); turn.Add(t.Text); }
                    else if (d.Delta.TryPickInputJson(out var j)) b.Text.Append(j.PartialJson);
                    else if (d.Delta.TryPickThinking(out var th2)) b.Text.Append(th2.Thinking);
                    else if (d.Delta.TryPickSignature(out var sg)) blocks[d.Index] = (b.Kind, b.Text, b.Id, b.Name, sg.Signature);
                }
                else if (ev.TryPickDelta(out var md) && md.Delta.StopReason == BetaStopReason.Refusal) refused = true;
            }
            if (refused) throw new AiException(L.T("Claude hat diese Anfrage abgelehnt. Formulier sie anders oder versuch ein anderes Modell."));
            var calls = blocks.Values.Where(b => b.Kind == "tool").ToList();
            if (calls.Count == 0) return;

            // echo the reply, then one result per tool call in a single user message
            var echo = new List<BetaContentBlockParam>();
            foreach (var b in blocks.Values)
            {
                switch (b.Kind)
                {
                    case "thinking": echo.Add(new BetaThinkingBlockParam { Thinking = b.Text.ToString(), Signature = b.Signature ?? "" }); break;
                    case "redacted": echo.Add(new BetaRedactedThinkingBlockParam { Data = b.Text.ToString() }); break;
                    case "text": if (b.Text.Length > 0) echo.Add(new BetaTextBlockParam { Text = b.Text.ToString() }); break;
                    case "tool": echo.Add(new BetaToolUseBlockParam { ID = b.Id, Name = b.Name, Input = ToElements(ParseArgs(b.Text.ToString())) }); break;
                }
            }
            messages.Add(new BetaAssistantMessageParam { Content = echo });
            var results = new List<BetaContentBlockParam>();
            foreach (var c in calls)
                results.Add(new BetaToolResultBlockParam { ToolUseID = c.Id, Content = await turn.Run(c.Name, ParseArgs(c.Text.ToString())) });
            messages.Add(new BetaUserMessageParam { Content = results });
            if (turn.Add != null) turn.Add("\n\n"); // the words after the tools start a new paragraph
        }
    }

    private static JsonObject ParseArgs(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
    /// <summary>A question as Claude reads it: its pictures first, then the words.</summary>
    private static List<BetaContentBlockParam> ClaudeContent(AiMessage m)
    {
        var blocks = new List<BetaContentBlockParam>();
        foreach (var img in m.Images.Where(File.Exists))
            blocks.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Base64(img), MediaType = MediaType.ImagePng } });
        blocks.Add(new BetaTextBlockParam { Text = m.Text.Length > 0 ? m.Text : PictureOnly });
        return blocks;
    }

    public static async Task<List<string>> ClaudeModels(string key, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = key };
        var page = await client.Models.List(cancellationToken: ct);
        return page.Items.Select(m => m.ID).ToList();
    }

    // ---------- ChatGPT (OpenAI Chat Completions, streamed) ----------

    private static async Task StreamOpenAi(List<AiMessage> history, string key, string model, Turn turn, CancellationToken ct)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt } };
        foreach (var m in history)
        {
            if (!m.IsUser || m.Images.Count == 0) { msgs.Add(new JsonObject { ["role"] = m.IsUser ? "user" : "assistant", ["content"] = m.Text }); continue; }
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Text.Length > 0 ? m.Text : PictureOnly } };
            foreach (var img in m.Images.Where(File.Exists))
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Base64(img) } });
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
        }
        var tools = new JsonArray(turn.Tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
        }).ToArray());

        for (int round = 0; round < Turn.MaxRounds; round++)
        {
            var body = new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = msgs.DeepClone() };
            if (tools.Count > 0) body["tools"] = tools.DeepClone();
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var text = new StringBuilder();
            var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
            await ReadSse(req, "ChatGPT", ct, json =>
            {
                var delta = (json["choices"] as JsonArray)?[0]?["delta"];
                if (delta == null) return;
                if (delta["content"] is JsonValue c && c.TryGetValue<string>(out var piece)) { text.Append(piece); turn.Add(piece); }
                if (delta["tool_calls"] is JsonArray tcs)
                    foreach (var tc in tcs)
                    {
                        int i = tc?["index"]?.GetValue<int>() ?? 0;
                        if (!calls.TryGetValue(i, out var call)) call = (null, null, new StringBuilder());
                        var id = tc?["id"]?.GetValue<string>() ?? call.Id;
                        var name = tc?["function"]?["name"]?.GetValue<string>() ?? call.Name;
                        call.Args.Append(tc?["function"]?["arguments"]?.GetValue<string>() ?? "");
                        calls[i] = (id, name, call.Args);
                    }
            });
            if (calls.Count == 0) return;
            msgs.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text.Length > 0 ? text.ToString() : null,
                ["tool_calls"] = new JsonArray(calls.Values.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToString() },
                }).ToArray()),
            });
            foreach (var c in calls.Values)
                msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = await turn.Run(c.Name, ParseArgs(c.Args.ToString())) });
            if (text.Length > 0) turn.Add("\n\n");
        }
    }
    public static async Task<List<string>> OpenAiModels(string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var json = await GetJson(req, "ChatGPT", ct);
        string[] skip = { "audio", "realtime", "tts", "transcribe", "image", "embedding", "whisper", "dall", "moderation", "instruct", "search", "codex" };
        return (json["data"] as JsonArray ?? new()).Select(x => x?["id"]?.GetValue<string>() ?? "")
            .Where(id => (id.StartsWith("gpt-") || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1]))) && !skip.Any(id.Contains))
            .OrderByDescending(id => id, StringComparer.Ordinal).ToList();
    }

    // ---------- Gemini (Google AI Studio, streamed) ----------

    private static async Task StreamGemini(List<AiMessage> history, string key, string model, Turn turn, CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var m in history)
        {
            var parts = new JsonArray();
            if (m.IsUser)
                foreach (var img in m.Images.Where(File.Exists))
                    parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = Base64(img) } });
            parts.Add(new JsonObject { ["text"] = m.Text.Length > 0 ? m.Text : PictureOnly });
            contents.Add(new JsonObject { ["role"] = m.IsUser ? "user" : "model", ["parts"] = parts });
        }
        var decls = new JsonArray(turn.Tools.Select(t =>
        {
            var d = new JsonObject { ["name"] = t.Name, ["description"] = t.Description };
            // a function without parameters gets no schema at all (an empty object schema is rejected)
            if (t.Parameters["properties"] is JsonObject { Count: > 0 }) d["parameters"] = GeminiSchema(t.Parameters);
            return (JsonNode)d;
        }).ToArray());
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";

        for (int round = 0; round < Turn.MaxRounds; round++)
        {
            var body = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = SystemPrompt } } },
                ["contents"] = contents.DeepClone(),
            };
            if (decls.Count > 0) body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = decls.DeepClone() } };
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("x-goog-api-key", key);
            // every part of the reply goes back as it came (newer models attach a "thoughtSignature" to function calls)
            var modelParts = new JsonArray();
            var calls = new List<JsonObject>();
            await ReadSse(req, "Gemini", ct, json =>
            {
                if (json["promptFeedback"]?["blockReason"] != null) throw new AiException(L.T("Gemini hat diese Anfrage abgelehnt."));
                if (json["candidates"]?[0]?["content"]?["parts"] is not JsonArray parts) return;
                foreach (var part in parts)
                {
                    if (part is not JsonObject po) continue;
                    modelParts.Add(po.DeepClone());
                    if (po["functionCall"] is JsonObject fc) calls.Add(fc);
                    else if (po["text"] is JsonValue tv && po["thought"]?.GetValue<bool>() != true) turn.Add(tv.GetValue<string>());
                }
            });
            if (calls.Count == 0) return;
            contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });
            var answers = new JsonArray();
            foreach (var fc in calls)
            {
                var name = fc["name"]?.GetValue<string>() ?? "";
                var result = await turn.Run(name, fc["args"] as JsonObject ?? new JsonObject());
                var resp = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = JsonNode.Parse(result) } };
                if (fc["id"] != null) resp["id"] = fc["id"]!.DeepClone();
                answers.Add(new JsonObject { ["functionResponse"] = resp });
            }
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = answers });
        }
    }

    /// <summary>Gemini reads OpenAPI-style schemas: no "additionalProperties", an empty "required" left out.</summary>
    private static JsonNode GeminiSchema(JsonObject schema)
    {
        var s = schema.DeepClone().AsObject();
        if (s["required"] is JsonArray { Count: 0 }) s.Remove("required");
        if (s["properties"] is JsonObject { Count: 0 }) { s.Remove("properties"); }
        return s;
    }
    public static async Task<List<string>> GeminiModels(string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200");
        req.Headers.Add("x-goog-api-key", key);
        var json = await GetJson(req, "Gemini", ct);
        return (json["models"] as JsonArray ?? new())
            .Where(m => (m?["supportedGenerationMethods"] as JsonArray)?.Any(x => x?.GetValue<string>() == "generateContent") == true)
            .Select(m => (m?["name"]?.GetValue<string>() ?? "").Replace("models/", ""))
            .Where(id => id.StartsWith("gemini-") && !id.Contains("embedding") && !id.Contains("tts") && !id.Contains("image"))
            .OrderByDescending(id => id, StringComparer.Ordinal).ToList();
    }

    // ---------- shared HTTP helpers ----------

    /// <summary>Checks the key and lists the models it may use (Settings › KI › Prüfen).</summary>
    public static Task<List<string>> ListModels(AiProvider p, string key, CancellationToken ct) => p switch
    {
        AiProvider.ChatGpt => OpenAiModels(key, ct),
        AiProvider.Gemini => GeminiModels(key, ct),
        _ => ClaudeModels(key, ct),
    };

    private static async Task ReadSse(HttpRequestMessage req, string who, CancellationToken ct, Action<JsonNode> onEvent)
    {
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new AiException(await ErrorText(res, who, ct));
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;
            JsonNode json;
            try { json = JsonNode.Parse(data); } catch (JsonException) { continue; }
            if (json?["error"] != null) throw new AiException($"{who}: {Short(json["error"]?["message"]?.GetValue<string>())}");
            if (json != null) onEvent(json);
        }
    }

    private static async Task<JsonNode> GetJson(HttpRequestMessage req, string who, CancellationToken ct)
    {
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new AiException(await ErrorText(res, who, ct));
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    private static async Task<string> ErrorText(HttpResponseMessage res, string who, CancellationToken ct)
    {
        int code = (int)res.StatusCode;
        if (code is 401 or 403) return L.T($"Der {who}-Schlüssel stimmt nicht. Prüf ihn in den Einstellungen unter „KI“.");
        if (code == 429) return L.T($"{who}: zu viele Anfragen oder kein Guthaben mehr.");
        if (code == 404) return L.T($"{who}: dieses Modell gibt es nicht. Wähl in den Einstellungen ein anderes.");
        string msg = "";
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            var json = JsonNode.Parse(body);
            msg = json?["error"]?["message"]?.GetValue<string>() ?? (json is JsonArray a ? a[0]?["error"]?["message"]?.GetValue<string>() : null) ?? "";
        }
        catch { }
        // Gemini says "API key not valid" with a plain 400
        if (msg.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) || msg.Contains("API_KEY_INVALID"))
            return L.T($"Der {who}-Schlüssel stimmt nicht. Prüf ihn in den Einstellungen unter „KI“.");
        return $"{who}: {(msg.Length > 0 ? Short(msg) : "Fehler " + code)}";
    }

    /// <summary>Same wording as in the chat for the check in Settings.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        AiException a => a.Message,
        AnthropicUnauthorizedException => L.T("Der Claude-Schlüssel stimmt nicht."),
        AnthropicApiException a => "Claude: " + Short(a.Message),
        AnthropicIOException or HttpRequestException => L.T("Keine Verbindung. Bist du online?"),
        _ => Short(ex.Message),
    };
}
