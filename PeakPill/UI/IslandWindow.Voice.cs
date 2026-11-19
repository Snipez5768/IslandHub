using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using PeakPill.Core;

namespace PeakPill.UI;

/// <summary>
/// 57 voice commands (user's pick 2026-11-18): hold the hotkey, speak, let go – the same commands as the quick search
/// (timer, note, music, open an app …). Windows' own speech recognition, offline, no key needed; it needs the
/// language's speech pack in Windows.
/// </summary>
public partial class IslandWindow
{
    private SpeechRecognitionEngine _voice;
    private readonly StringBuilder _heard = new();
    private TaskCompletionSource<bool> _voiceDone;

    private async void VoiceCommand()
    {
        if (_voice != null) return;
        var culture = SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture)
            .OrderByDescending(c => c.TwoLetterISOLanguageName == (L.English ? "en" : "de")).FirstOrDefault();
        if (culture == null)
        {
            ShowHint("mic.slash", L.T("Windows-Spracherkennung fehlt (Sprachpaket)"), (Brush)FindResource("Danger"), key: "voice");
            return;
        }
        try
        {
            _heard.Clear();
            _voice = new SpeechRecognitionEngine(culture);
            _voice.LoadGrammar(new DictationGrammar());
            _voice.SetInputToDefaultAudioDevice();
            _voice.SpeechRecognized += (_, e) => _heard.Append(e.Result.Text).Append(' ');
            _voiceDone = new TaskCompletionSource<bool>();
            _voice.RecognizeCompleted += (_, _) => _voiceDone.TrySetResult(true);
            _voice.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _voice?.Dispose();
            _voice = null;
            ShowHint("mic.slash", L.T("Kein Mikrofon"), (Brush)FindResource("Danger"), key: "voice");
            return;
        }
        _hintHoldMs = 20000;
        ShowHint("mic", L.T("Hört zu …"), (Brush)FindResource("Danger"), key: "voice");
        // while the key is held (at most 15 s)
        int vk = VoiceKey();
        var until = DateTime.Now.AddSeconds(15);
        while (DateTime.Now < until && (GetAsyncKeyState(vk) & 0x8000) != 0) await Task.Delay(60);
        try { _voice.RecognizeAsyncStop(); } catch { }
        await Task.WhenAny(_voiceDone.Task, Task.Delay(2500));
        _voice.Dispose();
        _voice = null;
        RunSpoken(_heard.ToString().Trim());
    }

    /// <summary>The key of the voice hotkey ("Ctrl+Alt+G" → G) – held, it keeps listening.</summary>
    private int VoiceKey()
    {
        var combo = S.Hotkeys.GetValueOrDefault(nameof(Core.HotkeyAction.VoiceCommand)) ?? "Ctrl+Alt+G";
        var last = combo.Split('+').Last().Trim();
        return Enum.TryParse<Key>(last, true, out var k) ? KeyInterop.VirtualKeyFromKey(k) : 0x47;
    }

    private void RunSpoken(string said)
    {
        if (said.Length == 0) { ShowHint("mic.slash", L.T("Nichts gehört"), (Brush)FindResource("SubText"), key: "voice"); return; }
        var q = Normalize(said);
        // a few things that are said, not typed
        Action direct = null;
        string label = null;
        if (Regex.IsMatch(q, @"^(nächste[rsn]? (titel|lied|song)|weiter|skip|next( song| track)?)$")) { direct = H.Media.Next; label = L.T("Nächster Titel"); }
        else if (Regex.IsMatch(q, @"^(vorherige[rsn]? (titel|lied|song)|zurück|previous( song| track)?)$")) { direct = H.Media.Previous; label = L.T("Vorheriger Titel"); }
        else if (Regex.IsMatch(q, @"^(pause|stopp?|musik (aus|stopp?)|abspielen|play|musik an|weiter abspielen)$")) { direct = H.Media.PlayPause; label = L.T("Wiedergabe / Pause"); }
        if (direct != null)
        {
            direct();
            ShowHint("check", label, (Brush)FindResource("Green"), check: true, key: "voice");
            return;
        }
        q = Regex.Replace(q, @"^(öffne|starte|open|start|zeig mir|zeige)\s+", "");
        var hits = Find(q);
        if (hits.Count == 0) { ShowHint("xmark", L.T($"Nicht verstanden: „{said}“"), (Brush)FindResource("SubText"), key: "voice"); return; }
        var hit = hits[0];
        ShowHint("check", hit.Text, (Brush)FindResource("Green"), check: true, key: "voice");
        After(500, hit.Run);
    }

    private static readonly (string Word, string Digit)[] Numbers =
    {
        ("fünfundvierzig", "45"), ("fünfundzwanzig", "25"), ("dreißig", "30"), ("vierzig", "40"), ("fünfzig", "50"), ("sechzig", "60"), ("neunzig", "90"),
        ("zwanzig", "20"), ("fünfzehn", "15"), ("zwölf", "12"), ("elf", "11"), ("zehn", "10"), ("neun", "9"), ("acht", "8"), ("sieben", "7"), ("sechs", "6"),
        ("fünf", "5"), ("vier", "4"), ("drei", "3"), ("zwei", "2"), ("eine", "1"), ("einen", "1"), ("eins", "1"), ("ein", "1"),
        ("forty-five", "45"), ("thirty", "30"), ("twenty", "20"), ("fifteen", "15"), ("twelve", "12"), ("ten", "10"), ("nine", "9"), ("eight", "8"),
        ("seven", "7"), ("six", "6"), ("five", "5"), ("four", "4"), ("three", "3"), ("two", "2"), ("one", "1"),
    };

    /// <summary>"Timer fünf Minuten." → "timer 5 min"</summary>
    private static string Normalize(string s)
    {
        s = s.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');
        foreach (var (w, d) in Numbers) s = Regex.Replace(s, $@"\b{w}\b", d);
        s = Regex.Replace(s, @"\b(minuten|minute|minutes|minute)\b", "min");
        s = Regex.Replace(s, @"^(stell(e)? (einen |den )?|starte (einen |den )?|set (a )?)", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
