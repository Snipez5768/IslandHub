using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// "Rückgängig" after deleting (user wish 2026-11-17): a small bar at the bottom of the open island for five seconds.
/// Notes, clipboard entries and notifications fold away first, then the bar offers to bring them back.
/// </summary>
public partial class IslandWindow
{
    private Border _undoBar;
    private TextBlock _undoText;
    private Action _undo, _undoCommit;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private void ShowUndo(string text, Action undo, Action commit = null)
    {
        CommitUndo(); // an earlier one is final now
        if (_undoBar == null)
        {
            _undoText = new TextBlock { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var btn = new Button { Content = L.T("Rückgängig"), Padding = new Thickness(10, 2, 10, 2), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand };
            btn.SetResourceReference(StyleProperty, "PillButton");
            btn.Click += (_, e) => { e.Handled = true; DoUndo(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new SfIcon { Kind = "trash", Size = 11, Foreground = (Brush)FindResource("SubText"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_undoText);
            row.Children.Add(btn);
            _undoBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x34, 0x34, 0x37)), CornerRadius = new CornerRadius(15), Padding = new Thickness(12, 3, 3, 3),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2),
                Child = row, Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform(),
            };
            Grid.SetRow(_undoBar, 1);
            Panel.SetZIndex(_undoBar, 40);
            ExpandedPanel.Children.Add(_undoBar);
            _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); HideUndo(); CommitUndo(); };
        }
        _undo = undo;
        _undoCommit = commit;
        _undoText.Text = text;
        _undoBar.Visibility = Visibility.Visible;
        SetUndoRoom(true); // the island grows by the bar – it covers nothing
        _headerPinUntil = DateTime.Now.AddSeconds(5); // and the tab bar stays while you act on the page
        UpdateHeaderFade();
        if (S.Animations != AnimationLevel.Aus)
        {
            ((TranslateTransform)_undoBar.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
            _undoBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        else _undoBar.Opacity = 1;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void DoUndo()
    {
        var u = _undo;
        _undo = null;
        _undoCommit = null;
        _undoTimer.Stop();
        HideUndo();
        u?.Invoke();
    }

    private void CommitUndo()
    {
        var c = _undoCommit;
        _undoCommit = null;
        _undo = null;
        c?.Invoke();
    }

    private void HideUndo()
    {
        if (_undoBar == null || _undoBar.Visibility != Visibility.Visible) return;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => { if (_undo == null) { _undoBar.Visibility = Visibility.Collapsed; SetUndoRoom(false); } };
        _undoBar.BeginAnimation(OpacityProperty, a);
    }

    // ---------- the deletions ----------

    private void DeleteClip(ClipItem item, object sender = null)
    {
        var c = ContainerOf(ClipList, sender) ?? ClipList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        ShrinkOut(c, () =>
        {
            int at = H.Clipboard.RemoveKeepFile(item);
            ShowUndo(L.T("Eintrag gelöscht"), () => { H.Clipboard.Restore(item, at); RefreshClipView(); }, () => H.Clipboard.ForgetFile(item));
        }, S.ClipLayout == ClipLayout.Streifen);
    }

    private void DismissNotification(IslandNotification n, object sender = null)
    {
        var c = ContainerOf(NotifList, sender) ?? NotifList.ItemContainerGenerator.ContainerFromItem(n) as FrameworkElement;
        ShrinkOut(c, () =>
        {
            int at = H.Notifications.Items.IndexOf(n);
            H.Notifications.Dismiss(n);
            UpdateNotifyUi();
            ShowUndo(L.T("Mitteilung entfernt"), () => { H.Notifications.Restore(n, at); UpdateNotifyUi(); });
        });
    }

    private void DeleteNote(Note n, FrameworkElement card)
    {
        if (_editingNote == n) { _editingNote = null; NoteInput.Clear(); }
        ShrinkOut(card, () =>
        {
            int at = H.Notes.Items.IndexOf(n);
            H.Notes.Remove(n);
            ShowUndo(L.T("Notiz gelöscht"), () => H.Notes.Restore(n, at));
        });
    }
}
