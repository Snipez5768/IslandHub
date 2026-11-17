using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>
/// Speaking instead of typing in the KI chat (user wish 2026-11-17: "im KI-Chat das Mikro als Eingabe benutzen").
/// Records the default microphone, keeps it as 16 kHz mono WAV (small, enough for speech) and lets Gemini or
/// ChatGPT write it down. Claude cannot hear audio – without a Gemini or ChatGPT key, Windows' own voice
/// typing (Win+H) takes over.
/// </summary>
/// <summary>A reason the user can read (wrong key, offline …).</summary>
public sealed class DictationException : Exception { public DictationException(string m) : base(m) { } }

public sealed class Dictation : IDisposable
{
    public const int Rate = 16000;
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(3);

    private WasapiCapture _cap;
    private readonly MemoryStream _pcm = new();
    private readonly object _lock = new();
    private float _peak;
    private long _acc;     // resampling: input frames are summed until one output sample is due
    private double _sum;
    private int _n;
    private TaskCompletionSource<bool> _stopped;

    public DateTime Started { get; private set; }
    public TimeSpan Length => TimeSpan.FromSeconds(_pcm.Length / 2.0 / Rate);
    /// <summary>Raised (on a capture thread) when the 3 minutes are full or the microphone went away.</summary>
    public event Action Ended;

    /// <summary>Starts recording; throws when there is no microphone (or Windows blocks it).</summary>
    public void Start()
    {
        _cap = new WasapiCapture(); // the default recording device
        _cap.ShareMode = AudioClientShareMode.Shared;
        _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cap.DataAvailable += OnData;
        _cap.RecordingStopped += (_, _) => { _stopped.TrySetResult(true); Ended?.Invoke(); };
        Started = DateTime.Now;
        _cap.StartRecording();
    }

    /// <summary>The loudest moment since the last call, 0 … 1 (for the level bars).</summary>
    public float TakePeak() { lock (_lock) { var p = _peak; _peak = 0; return p; } }

    private void OnData(object sender, WaveInEventArgs e)
    {
        var f = _cap.WaveFormat;
        int bytes = f.BitsPerSample / 8, ch = f.Channels, frame = bytes * ch;
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
        float peak = 0;
        lock (_lock)
        {
            for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
            {
                double v = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = i + c * bytes;
                    v += bytes switch
                    {
                        4 when isFloat => BitConverter.ToSingle(e.Buffer, o),
                        4 => BitConverter.ToInt32(e.Buffer, o) / 2147483648.0,
                        3 => ((e.Buffer[o] << 8 | e.Buffer[o + 1] << 16 | e.Buffer[o + 2] << 24) >> 8) / 8388608.0,
                        2 => BitConverter.ToInt16(e.Buffer, o) / 32768.0,
                        _ => 0,
                    };
                }
                v /= ch;
                if (Math.Abs(v) > peak) peak = (float)Math.Abs(v);
                // average every input frame that falls into one 16 kHz sample (a plain box filter is fine for speech)
                _sum += v; _n++;
                _acc += Rate;
                while (_acc >= f.SampleRate)
                {
                    _acc -= f.SampleRate;
                    var s = (short)Math.Clamp(_sum / Math.Max(1, _n) * 32767, -32768, 32767);
                    _pcm.WriteByte((byte)s); _pcm.WriteByte((byte)(s >> 8));
                    _sum = 0; _n = 0;
                }
            }
            if (peak > _peak) _peak = peak;
        }
        if (Length >= MaxLength) _cap.StopRecording();
    }

    /// <summary>Stops and returns the recording as a WAV file in memory (null when it was too short to hold a word).</summary>
    public async Task<byte[]> StopAsync()
    {
        if (_cap == null) return null;
        try { _cap.StopRecording(); } catch { }
        await Task.WhenAny(_stopped.Task, Task.Delay(1500));
        lock (_lock)
        {
            if (_pcm.Length < Rate * 2 * 0.4) return null; // under 0.4 s
            return Wav(_pcm.ToArray());
        }
    }

    public void Dispose()
    {
        try { _cap?.StopRecording(); } catch { }
        _cap?.Dispose();
        _cap = null;
    }

    private static byte[] Wav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    // ---------- writing it down ----------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Which provider writes it down: the chosen one if it can hear, else any other key that can (null = none).</summary>
    public static AiProvider? Writer()
    {
        if (S.AiProvider is AiProvider.Gemini or AiProvider.ChatGpt && AiService.HasKey(S.AiProvider)) return S.AiProvider;
        if (AiService.HasKey(AiProvider.Gemini)) return AiProvider.Gemini;
        if (AiService.HasKey(AiProvider.ChatGpt)) return AiProvider.ChatGpt;
        return null;
    }

    private static AppSettings S => App.Settings;

    /// <summary>The spoken words as text (empty when nothing was said). Throws <see cref="DictationException"/> with a readable reason.</summary>
    public static async Task<string> TranscribeAsync(byte[] wav, AiProvider p, CancellationToken ct)
    {
        var key = AiService.GetKey(p);
        try
        {
            return p == AiProvider.ChatGpt ? await OpenAi(wav, key, "gpt-4o-mini-transcribe", ct) : await Gemini(wav, key, ct);
        }
        catch (DictationException) when (p == AiProvider.ChatGpt)
        {
            return await OpenAi(wav, key, "whisper-1", ct); // older accounts / projects without the newer model
        }
        catch (HttpRequestException) { throw new DictationException(L.T("Keine Verbindung. Bist du online?")); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new DictationException(L.T("Keine Verbindung. Bist du online?")); }
    }

    private static async Task<string> Gemini(byte[] wav, string key, CancellationToken ct)
    {
        var model = AiService.GetModel(AiProvider.Gemini);
        var body = new JsonObject
        {
            ["contents"] = new JsonArray { new JsonObject { ["role"] = "user", ["parts"] = new JsonArray
            {
                new JsonObject { ["text"] = "Transcribe this recording word for word, in the language that is spoken, with normal punctuation. " +
                                            "Reply with the transcript only – no quotes, no notes. If nobody speaks, reply with nothing." },
                new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) } },
            } } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", key);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new DictationException(Fail("Gemini", (int)res.StatusCode, text));
        var parts = JsonNode.Parse(text)?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
        return string.Concat(parts?.Where(x => x?["thought"]?.GetValue<bool>() != true).Select(x => x?["text"]?.GetValue<string>() ?? "") ?? Enumerable.Empty<string>()).Trim();
    }

    private static async Task<string> OpenAi(byte[] wav, string key, string model, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent(model), "model");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new DictationException(Fail("ChatGPT", (int)res.StatusCode, text));
        return (JsonNode.Parse(text)?["text"]?.GetValue<string>() ?? "").Trim();
    }

    private static string Fail(string who, int code, string body)
    {
        if (code is 401 or 403 || body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase))
            return L.T($"Der {who}-Schlüssel stimmt nicht. Prüf ihn in den Einstellungen unter „KI“.");
        if (code == 429) return L.T($"{who}: zu viele Anfragen oder kein Guthaben mehr.");
        string msg = "";
        try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? ""; } catch { }
        if (msg.Length > 120) msg = msg[..120] + " …";
        return msg.Length > 0 ? $"{who}: {msg}" : L.T($"{who} konnte die Aufnahme nicht lesen.");
    }

    // ---------- without a key that can hear: Windows' voice typing ----------

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    /// <summary>Win+H – Windows types what you say into the focused field.</summary>
    public static void WindowsVoiceTyping()
    {
        keybd_event(0x5B, 0, 0, IntPtr.Zero); keybd_event(0x48, 0, 0, IntPtr.Zero);
        keybd_event(0x48, 0, 2, IntPtr.Zero); keybd_event(0x5B, 0, 2, IntPtr.Zero);
    }
}
