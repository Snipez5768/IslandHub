using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Claude &amp; KI, the second round (user's picks from the chat mockups, 2026-11-18):
/// 11  Claude is done – how many files it changed, a click shows which;
/// 13a select text anywhere, Ctrl+Alt+A: translate, rephrase or explain it – the answer is written in the KI page;
/// 14  Ctrl+Alt+B: draw a box around something on the screen – the KI explains what it is.
/// (8b one row per session, 9 Claude's questions, 10 "Immer", 12 tokens: AgentService and IslandWindow.ClaudeAsk.cs.)
/// </summary>
public partial class IslandWindow
{
    // =====================================================================
    //  11 · what Claude changed
    // =====================================================================

    private void DoneSummary(AgentSession s)
    {
        var files = s.ChangedFiles.ToList();
        if (files.Count == 0 || s.FinishedAt is { } f && (f - s.StartedAt).TotalSeconds < 15) return;
        // after the green check has run (OnAgentFinished): a quiet line, no wobble – nothing to do
        After(1500, () =>
        {
            if (_state != State.Collapsed) return;
            _hintHoldMs = 3500;
            ShowHint("check", files.Count == 1 ? L.T("Fertig · 1 Datei geändert") : L.T($"Fertig · {files.Count} Dateien geändert"),
                     (Brush)FindResource("Green"), key: "agentdone",
                     actions: new List<(string, string, Brush, Action)> { ("doc", L.T("Welche?"), (Brush)FindResource("Blue"), () => ShowChangedFiles(s, files)) });
        });
    }

    private void ShowChangedFiles(AgentSession s, List<string> files)
    {
        var p = new PeekInfo
        {
            Key = "moment:changed",
            Title = files.Count == 1 ? L.T("1 Datei geändert") : L.T($"{files.Count} Dateien geändert"),
            Meta = s.Project,
            TimeoutMs = 9000,
        };
        var grey = (Brush)FindResource("SubText");
        foreach (var file in files.Take(4)) p.Rows.Add(("doc", AgentBrush, file, null));
        if (files.Count > 4) p.Rows.Add(("plus", grey, L.T($"{files.Count - 4} weitere"), null));
        p.Action(L.T("Claude öffnen"), () => AgentService.OpenHost(s), true);
        ShowPeek(p);
    }

    // =====================================================================
    //  13a · selected text → the KI
    // =====================================================================

    private async void AskAboutSelection()
    {
        var text = await GrabSelectionAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowHint("text", L.T("Erst Text markieren"), (Brush)FindResource("SubText"), key: "aisel");
            return;
        }
        text = text.Trim();
        string shown = text.Replace("\r", " ").Replace("\n", " ");
        if (shown.Length > 70) shown = shown[..69] + "…";
        var p = new PeekInfo
        {
            Key = "aisel", Important = true, Icon = "sparkles", GlyphBrush = (Brush)FindResource("Blue"),
            Source = L.T("Markierter Text"), SourceIcon = "text", SourceBrush = (Brush)FindResource("Blue"),
            Title = shown, TimeoutMs = 15000,
        };
        bool en = L.English;
        p.Action(L.T("Übersetzen"), () => AskAi((en
            ? "Translate the following text. If it is in English, into German; otherwise into English. Reply with the translation only.\n\n"
            : "Übersetze den folgenden Text. Ist er auf Deutsch, ins Englische, sonst ins Deutsche. Antworte nur mit der Übersetzung.\n\n") + text));
        p.Action(L.T("Umformulieren"), () => AskAi((en
            ? "Rewrite the following text so it reads clearer and smoother, in the same language. Reply with the new text only.\n\n"
            : "Formuliere den folgenden Text klarer und flüssiger, in derselben Sprache. Antworte nur mit dem neuen Text.\n\n") + text));
        p.Action(L.T("Erklären"), () => AskAi((en
            ? "Explain briefly and simply what the following text means.\n\n"
            : "Erkläre kurz und einfach, was der folgende Text bedeutet.\n\n") + text), true);
        ShowPeek(p);
    }

    /// <summary>The KI page opens and the answer is written there (copy with its button, ask further below).</summary>
    private void AskAi(string prompt, IList<string> images = null)
    {
        Expand("Ai", true);
        _ = H.Ai.SendAsync(prompt, images);
    }

    /// <summary>
    /// The selected text of the app in front: Ctrl+C is pressed for it once the hotkey's keys are up, the text is read
    /// and the clipboard gets back what it held before. The clipboard list does not record this copy.
    /// </summary>
    private async Task<string> GrabSelectionAsync()
    {
        // Ctrl / Alt / Shift of the hotkey still down: our Ctrl+C would be Ctrl+Alt+C
        for (int i = 0; i < 60 && (Down(0x11) || Down(0x12) || Down(0x10)); i++) await Task.Delay(25);
        IDataObject saved = null;
        try
        {
            var src = Clipboard.GetDataObject();
            if (src != null)
            {
                var copy = new DataObject();
                foreach (var fmt in src.GetFormats(false))
                    try { var d = src.GetData(fmt); if (d != null) copy.SetData(fmt, d); } catch { }
                saved = copy;
            }
        }
        catch { }
        uint before = GetClipboardSequenceNumber();
        H.Clipboard.Quiet(1600);
        keybd_event(0x11, 0, 0, IntPtr.Zero);
        keybd_event(0x43, 0, 0, IntPtr.Zero);
        keybd_event(0x43, 0, 2, IntPtr.Zero);
        keybd_event(0x11, 0, 2, IntPtr.Zero);
        for (int i = 0; i < 25 && GetClipboardSequenceNumber() == before; i++) await Task.Delay(40);
        string text = null;
        if (GetClipboardSequenceNumber() != before)
        {
            await Task.Delay(40);
            for (int i = 0; i < 5 && text == null; i++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { await Task.Delay(50); }
            }
            // back to what the clipboard held before
            H.Clipboard.Quiet(900);
            for (int i = 0; i < 5; i++)
            {
                try { if (saved != null) Clipboard.SetDataObject(saved, true); else Clipboard.Clear(); break; } catch { await Task.Delay(50); }
            }
        }
        return text;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    // =====================================================================
    //  14 · explain a part of the screen
    // =====================================================================

    /// <summary>Called by the screenshot flow with the picture of the box: "schaut sich das an …", then the KI page.</summary>
    public void ExplainShot(string file)
    {
        if (string.IsNullOrEmpty(file)) return;
        _hintHoldMs = 6000;
        ShowHint("sparkles", L.T("Schaut sich das an …"), (Brush)FindResource("Blue"), key: "aishot");
        int before = H.Ai.Messages.Count;
        _ = H.Ai.SendAsync(L.English ? "Explain briefly what can be seen in this picture." : "Erkläre kurz, was auf diesem Bild zu sehen ist.", new[] { file });
        // the KI page opens once the answer begins (or right away when it fails)
        var started = DateTime.Now;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        t.Tick += (_, _) =>
        {
            var last = H.Ai.Messages.Count > before ? H.Ai.Messages[^1] : null;
            bool ready = last == null && !H.Ai.Busy || last is { IsUser: false } m && (m.Text.Length > 0 || m.Failed) || !H.Ai.Busy;
            if (!ready && DateTime.Now - started < TimeSpan.FromSeconds(30)) return;
            t.Stop();
            Expand("Ai", true);
        };
        t.Start();
    }
}
