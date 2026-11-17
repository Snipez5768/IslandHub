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
}

/// <summary>
/// The KI page (2026-11-17): a small chat with Claude, ChatGPT or Gemini on the user's own API key. Keys are
/// stored encrypted for this Windows user; the chat itself stays on this PC (ai-chat.json) and goes only to the
/// service that answers.
/// </summary>
public sealed class AiService : ObservableObject
{
    /// <summary>The same for the whole chat (it is part of what the answers build on).</summary>
    public static string SystemPrompt => L.English
        ? "You are the AI assistant in PeakPill, a small island at the top of the Windows screen. " +
          "Answer in the language of the question, friendly, clear and rather brief: the window is narrow. " +
          "Short paragraphs, Markdown sparingly (bold, lists and code blocks work)."
        : "Du bist der KI-Assistent in PeakPill, einer kleinen Insel oben am Windows-Bildschirm. " +
          "Antworte in der Sprache der Frage (meist Deutsch), freundlich, klar und eher knapp: das Fenster ist schmal. " +
          "Kurze Absätze, Markdown sparsam (fett, Listen, Code-Blöcke gehen).";

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
    }

    public void Load()
    {
        try
        {
            foreach (var m in Storage.Load<List<Saved>>(FileName))
                if (!string.IsNullOrEmpty(m.Text)) Messages.Add(new AiMessage { IsUser = m.IsUser, Text = m.Text, Failed = m.Failed, By = m.By });
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Storage.Save(FileName, Messages.Where(m => !m.IsStreaming).TakeLast(60)
                .Select(m => new Saved { IsUser = m.IsUser, Text = m.Text, Failed = m.Failed, By = m.By }).ToList());
        }
        catch { }
    }

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
        Messages.RemoveAt(Messages.Count - 1);
        Messages.RemoveAt(Messages.Count - 1);
        await SendAsync(question);
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

    /// <summary>The user's last question (↑ in the empty field brings it back).</summary>
    public string LastQuestion => Messages.LastOrDefault(m => m.IsUser)?.Text ?? "";

    public async Task SendAsync(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || Busy) return;
        var provider = S.AiProvider;
        var key = GetKey(provider);
        var model = GetModel(provider);
        var history = History();
        Messages.Add(new AiMessage { IsUser = true, Text = text });
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
            switch (provider)
            {
                case AiProvider.ChatGpt: await StreamOpenAi(history, key, model, Add, ct); break;
                case AiProvider.Gemini: await StreamGemini(history, key, model, Add, ct); break;
                default: await StreamClaude(history, key, model, Add, ct); break;
            }
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

    private static async Task StreamClaude(List<AiMessage> history, string key, string model, Action<string> add, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = key };
        bool fallback = WithFallback.Contains(model);
        var p = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 64000,
            System = SystemPrompt,
            CacheControl = new BetaCacheControlEphemeral(),
            Messages = history.Select(m => m.IsUser
                ? (BetaMessageParam)new BetaUserMessageParam { Content = m.Text }
                : new BetaAssistantMessageParam { Content = m.Text }).ToList(),
            Betas = fallback ? ["server-side-fallback-2026-07-01"] : null,
            Fallbacks = fallback ? new Default() : null,
        };
        bool refused = false;
        await foreach (var ev in client.Beta.Messages.CreateStreaming(p, ct))
        {
            if (ev.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var t)) add(t.Text);
            else if (ev.TryPickDelta(out var md) && md.Delta.StopReason == BetaStopReason.Refusal) refused = true;
        }
        if (refused) throw new AiException(L.T("Claude hat diese Anfrage abgelehnt. Formulier sie anders oder versuch ein anderes Modell."));
    }

    public static async Task<List<string>> ClaudeModels(string key, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = key };
        var page = await client.Models.List(cancellationToken: ct);
        return page.Items.Select(m => m.ID).ToList();
    }

    // ---------- ChatGPT (OpenAI Chat Completions, streamed) ----------

    private static async Task StreamOpenAi(List<AiMessage> history, string key, string model, Action<string> add, CancellationToken ct)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt } };
        foreach (var m in history) msgs.Add(new JsonObject { ["role"] = m.IsUser ? "user" : "assistant", ["content"] = m.Text });
        var body = new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = msgs };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        await ReadSse(req, "ChatGPT", ct, json =>
        {
            var ch = json["choices"] as JsonArray;
            if (ch is { Count: > 0 }) add(ch[0]?["delta"]?["content"]?.GetValue<string>());
        });
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

    private static async Task StreamGemini(List<AiMessage> history, string key, string model, Action<string> add, CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var m in history)
            contents.Add(new JsonObject { ["role"] = m.IsUser ? "user" : "model", ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Text } } });
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = SystemPrompt } } },
            ["contents"] = contents,
        };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", key);
        await ReadSse(req, "Gemini", ct, json =>
        {
            var parts = json["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            if (parts != null) foreach (var part in parts) add(part?["text"]?.GetValue<string>());
            if (json["promptFeedback"]?["blockReason"] != null) throw new AiException(L.T("Gemini hat diese Anfrage abgelehnt."));
        });
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
