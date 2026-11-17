using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Speaking into the KI chat (user wish 2026-11-17). The mic button records; while it records the field shows a red
/// dot, the running time and level bars that move with the voice. Mic again = the words land in the field (to check
/// before sending), Enter = written down and sent at once, Esc = thrown away. Gemini or ChatGPT write it down; with
/// only a Claude key Windows' own voice typing (Win+H) types into the field instead.
/// </summary>
public partial class IslandWindow
{
    private const int DictBars = 34;
    private Dictation _dict;
    private DispatcherTimer _dictTimer;
    private CancellationTokenSource _dictCts;
    private int _dictUi; // counts UI changes – a late "hide the message" must not hide a newer state

    private static readonly SolidColorBrush DictRed = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush DictBar = Frozen(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush DictGrey = Frozen(Color.FromRgb(0x8E, 0x8E, 0x93));

    private enum DictState { Idle, Recording, Writing, Message }

    private void AiMic_Click(object sender, RoutedEventArgs e)
    {
        if (_dictCts != null) { _dictCts.Cancel(); return; }   // writing it down: the button stops that
        if (_dict != null) { _ = DictationFinish(false); return; }

        if (Dictation.Writer() == null)
        {
            // Claude cannot hear audio: Windows' voice typing writes into the field
            Activate();
            AiInput.Focus();
            Dictation.WindowsVoiceTyping();
            return;
        }

        var d = new Dictation();
        try { d.Start(); }
        catch (Exception ex)
        {
            d.Dispose();
            App.Log(ex);
            DictationMessage(L.T("Kein Mikrofon gefunden – oder Windows erlaubt es nicht."));
            return;
        }
        d.Ended += () => Dispatcher.BeginInvoke(() => { if (_dict == d) _ = DictationFinish(false); });
        _dict = d;
        DictationUi(DictState.Recording);
        if (_dictTimer == null)
        {
            _dictTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
            _dictTimer.Tick += (_, _) => DictationTick();
        }
        _dictTimer.Start();
        AiInput.Focus(); // Enter and Esc still arrive
    }

    /// <summary>Enter / Esc while recording; true = handled.</summary>
    private bool DictationKey(KeyEventArgs e)
    {
        if (_dict == null && _dictCts == null) return false;
        if (e.Key == Key.Enter) { e.Handled = true; if (_dict != null) _ = DictationFinish(true); return true; }
        if (e.Key == Key.Escape) { e.Handled = true; DictationCancel(); return true; }
        e.Handled = true; // no typing into the hidden field meanwhile
        return true;
    }

    /// <summary>Throws the recording (or the writing) away.</summary>
    private void DictationCancel()
    {
        _dictCts?.Cancel();
        var d = _dict;
        _dict = null;
        _dictTimer?.Stop();
        d?.Dispose();
        DictationUi(DictState.Idle);
    }

    private void DictationTick()
    {
        if (_dict == null) { _dictTimer.Stop(); return; }
        // the island closed or moved to another page: nothing keeps listening in the background
        if (_state != State.Expanded || _tab != "Ai") { DictationCancel(); return; }
        var len = _dict.Length;
        AiRecTime.Text = $"{(int)len.TotalMinutes}:{len.Seconds:00}";
        // a soft curve: quiet speech still moves the bars, shouting does not leave them all at the top
        double lvl = Math.Clamp(Math.Sqrt(_dict.TakePeak()) * 1.25, 0, 1);
        if (AiRecBars.Children.Count >= DictBars) AiRecBars.Children.RemoveAt(AiRecBars.Children.Count - 1);
        AiRecBars.Children.Insert(0, new Rectangle
        {
            Width = 2.5, Height = 2 + lvl * 16, RadiusX = 1.25, RadiusY = 1.25, Margin = new Thickness(1.2, 0, 1.2, 0),
            Fill = DictBar, VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private async Task DictationFinish(bool send)
    {
        var d = _dict;
        if (d == null) return;
        _dict = null;
        _dictTimer?.Stop();
        var wav = await d.StopAsync();
        d.Dispose();
        if (wav == null) { DictationMessage(L.T("Zu kurz, um etwas zu verstehen.")); return; }
        var who = Dictation.Writer();
        if (who == null) { DictationUi(DictState.Idle); return; }

        DictationUi(DictState.Writing);
        AiRecText.Text = L.T($"{AiService.Name(who.Value)} schreibt mit …");
        using var cts = _dictCts = new CancellationTokenSource();
        try
        {
            var text = await Dictation.TranscribeAsync(wav, who.Value, cts.Token);
            _dictCts = null;
            if (text.Length == 0) { DictationMessage(L.T("Nichts verstanden.")); return; }
            var before = AiInput.Text.TrimEnd();
            DictationUi(DictState.Idle);
            AiInput.Text = before.Length > 0 ? before + " " + text : text;
            AiInput.CaretIndex = AiInput.Text.Length;
            if (send) AiSend();
            else AiInput.Focus();
        }
        catch (OperationCanceledException) { _dictCts = null; DictationUi(DictState.Idle); }
        catch (DictationException ex) { _dictCts = null; DictationMessage(ex.Message); }
        catch (Exception ex) { _dictCts = null; App.Log(ex); DictationMessage(L.T("Das hat nicht geklappt.")); }
    }

    /// <summary>A short line in the field (too short, nothing heard, wrong key …) that goes away by itself.</summary>
    private async void DictationMessage(string text)
    {
        DictationUi(DictState.Message);
        AiRecText.Text = text;
        AiRecText.ToolTip = text;
        int mine = _dictUi;
        await Task.Delay(3800);
        if (mine == _dictUi) DictationUi(DictState.Idle);
    }

    private void DictationUi(DictState s)
    {
        _dictUi++;
        bool rec = s == DictState.Recording, idle = s == DictState.Idle;
        AiRec.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        AiInput.Opacity = idle ? 1 : 0;
        AiInput.IsReadOnly = !idle;
        AiRecDot.Visibility = AiRecTime.Visibility = AiRecBars.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        AiRecText.Visibility = rec ? Visibility.Collapsed : Visibility.Visible;
        AiRecText.Foreground = s == DictState.Message ? DictGrey : (Brush)new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xF5));
        if (!rec) AiRecText.ToolTip = null;
        if (rec) { AiRecBars.Children.Clear(); AiRecTime.Text = "0:00"; }

        // the mic button turns into a red stop while recording, a grey one while writing
        Ext.SetIcon(AiMicBtn, rec || s == DictState.Writing ? "stop" : "mic");
        AiMicBtn.Foreground = rec ? DictRed : DictGrey;
        AiMicBtn.ToolTip = rec ? L.T("Fertig – Text ins Feld (Enter schickt sofort ab, Esc verwirft)")
                         : s == DictState.Writing ? L.T("Abbrechen") : L.T("Sprechen statt tippen");

        AiRecDot.BeginAnimation(OpacityProperty, null);
        if (rec && S.Animations != AnimationLevel.Aus)
            AiRecDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        if (idle) UpdateAiSend();
        else AiSendBtn.Opacity = rec ? 1 : 0.35;
    }
}
