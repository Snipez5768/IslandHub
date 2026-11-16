using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IslandHub.Services;

namespace IslandHub.UI;

/// <summary>Answering a message right in the island (user wish 2026-11-16) – the card part.</summary>
public partial class IslandWindow
{
    /// <summary>The message field opens in the card; the card stays while you type.</summary>
    private void OpenReplyBox()
    {
        if (_peek?.Reply == null) return;
        _peekTimer.Stop();
        StopCardCountdown();
        PeekProgress.Visibility = Visibility.Collapsed;
        PeekReply.Visibility = Visibility.Visible;
        PeekReplyBox.Tag = S.ReplyAutoSend ? $"Antwort an {_peek.Title} …" : $"Antwort an {_peek.Title} (du sendest selbst) …";
        PeekPanel.Measure(new Size(PeekW - 20, double.PositiveInfinity));
        double h = Math.Clamp(PeekPanel.DesiredSize.Height + 2, 54, 170);
        AnimateSize(PeekW, h + NotchExtra, Math.Min(28, (h + 2) / 2), true);
        _sticky = true;
        Activate();
        Dispatcher.BeginInvoke(new Action(() => { PeekReplyBox.Focus(); Keyboard.Focus(PeekReplyBox); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PeekReplyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; SendReply(); }
        else if (e.Key == Key.Escape) { e.Handled = true; PeekReplyBox.Text = ""; _sticky = false; EndPeek(); }
    }

    private void PeekReplySend_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SendReply();
    }

    private void SendReply()
    {
        var text = PeekReplyBox.Text.Trim();
        var reply = _peek?.Reply;
        if (text.Length == 0 || reply == null) return;
        PeekReplyBox.Text = "";
        _sticky = false;
        EndPeek();
        reply(text);
        ShowHint(S.ReplyAutoSend ? "send" : "reply", S.ReplyAutoSend ? "Gesendet" : "Eingefügt – Enter drückst du", (Brush)FindResource("Blue"), check: S.ReplyAutoSend);
    }
}
