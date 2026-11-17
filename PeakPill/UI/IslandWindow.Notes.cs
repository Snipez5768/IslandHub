using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Quick notes (user wish 2026-11-17): Strg+Alt+N opens the field anywhere, Enter keeps the note, Shift+Enter makes
/// a new line. Each note: click = edit, and on hover pin, copy, send with LocalSend, delete.
/// </summary>
public partial class IslandWindow
{
    private Note _editingNote;

    private void WireNotes()
    {
        H.Notes.Items.CollectionChanged += (_, _) => Ui.Run(() => { if (_tab == "Notes") UpdateNotesUi(); });
    }

    private readonly HashSet<string> _notesShown = new();

    private void UpdateNotesUi()
    {
        NotesList.Children.Clear();
        foreach (var n in H.Notes.Items)
        {
            var card = NoteCard(n);
            NotesList.Children.Add(card);
            // a note that is new since the last look grows in and pushes the others down
            if (_notesShown.Count > 0 && !_notesShown.Contains(n.Id) && S.Animations != AnimationLevel.Aus) GrowIn(card);
        }
        _notesShown.Clear();
        foreach (var n in H.Notes.Items) _notesShown.Add(n.Id);
        if (_notesShown.Count == 0) _notesShown.Add(""); // "looked once" – the first note after an empty list grows in too
        bool empty = H.Notes.Items.Count == 0;
        NotesEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        var key = S.Hotkeys.GetValueOrDefault(nameof(HotkeyAction.QuickNote));
        NotesEmptyText.Text = string.IsNullOrWhiteSpace(key)
            ? L.T("Schreib oben los – Enter speichert")
            : L.T($"Schreib oben los – überall mit {HotkeyGesture.Display(key)}");
        NoteInput.Tag = _editingNote != null ? L.T("Notiz bearbeiten … (Enter speichert, Esc bricht ab)") : L.T("Notiz schreiben … (Enter speichert)");
        // the page grows with the notes (up to three cards), like LocalSend's
        if (_state == State.Expanded && _tab == "Notes" && Math.Abs(NotesHeight() + NotchExtra - _targetH) > 0.5)
            AnimateSize(ExpW, NotesHeight() + NotchExtra, ExpRadius, true);
    }

    private double NotesHeight() => H.Notes.Items.Count == 0 ? ExpH : Math.Min(300, ExpH + 44 * Math.Min(H.Notes.Items.Count, 3));

    private void NoteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            SaveNote();
        }
        else if (e.Key == Key.Escape && _editingNote != null)
        {
            e.Handled = true;
            _editingNote = null;
            NoteInput.Clear();
            UpdateNotesUi();
        }
    }

    private void NoteSave_Click(object sender, RoutedEventArgs e) => SaveNote();

    private void SaveNote()
    {
        var text = NoteInput.Text;
        if (_editingNote != null) H.Notes.Update(_editingNote, text);
        else if (H.Notes.Add(text) == null) return;
        _editingNote = null;
        NoteInput.Clear();
        UpdateNotesUi();
        NotesScroll.ScrollToTop();
        NoteInput.Focus();
    }

    private FrameworkElement NoteCard(Note n)
    {
        var sub = (Brush)FindResource("SubText");
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 6, 7), Margin = new Thickness(0, 0, 0, 5),
            Background = new SolidColorBrush(Color.FromArgb(n == _editingNote ? (byte)0x30 : (byte)0x14, 255, 255, 255)),
            BorderBrush = n.Pinned ? (Brush)FindResource("Accent") : Brushes.Transparent, BorderThickness = new Thickness(n.Pinned ? 0.8 : 0),
            Cursor = Cursors.Hand, ToolTip = L.T("Klicken zum Bearbeiten"),
        };
        var dock = new DockPanel();

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden, Margin = new Thickness(6, -2, 0, 0) };
        DockPanel.SetDock(tools, Dock.Right);
        Button Tool(string icon, string tip, Action act, Brush color = null)
        {
            var b = new Button { Width = 22, Height = 22, ToolTip = L.T(tip), Foreground = color ?? sub };
            b.SetResourceReference(StyleProperty, "SmallIconButton");
            Ext.SetIcon(b, icon);
            Ext.SetIconSize(b, 12);
            b.Click += (_, e) => { e.Handled = true; act(); };
            return b;
        }
        tools.Children.Add(Tool("pin", n.Pinned ? "Lösen" : "Anheften", () => H.Notes.TogglePin(n), n.Pinned ? (Brush)FindResource("Accent") : null));
        Button copy = null;
        copy = Tool("copy", "Kopieren", () =>
        {
            try { System.Windows.Clipboard.SetText(n.Text); } catch { return; }
            Ext.SetIcon(copy, "check");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            t.Tick += (_, _) => { t.Stop(); Ext.SetIcon(copy, "copy"); };
            t.Start();
        });
        tools.Children.Add(copy);
        tools.Children.Add(Tool("share", "Mit LocalSend senden", () => SendNote(n)));
        var del = Tool("trash", "Löschen", () => DeleteNote(n, card)); // folds away, "Rückgängig" for 5 s
        Ext.SetHover(del, (Brush)FindResource("Danger"));
        tools.Children.Add(del);
        dock.Children.Add(tools);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = n.Preview, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (n.Pinned) meta.Children.Add(new SfIcon { Kind = "pin", Size = 9, Foreground = sub, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        meta.Children.Add(new TextBlock { Text = n.When, FontSize = 10, Foreground = sub });
        body.Children.Add(meta);
        dock.Children.Add(body);
        card.Child = dock;

        card.MouseEnter += (_, _) => tools.Visibility = Visibility.Visible;
        card.MouseLeave += (_, _) => tools.Visibility = Visibility.Hidden;
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _editingNote = n;
            NoteInput.Text = n.Text;
            NoteInput.Focus();
            NoteInput.CaretIndex = NoteInput.Text.Length;
            UpdateNotesUi();
        };
        return card;
    }

    /// <summary>To the LocalSend page with the note as the text to send – then a click on the device.</summary>
    private void SendNote(Note n)
    {
        Expand("Send", true);
        SendText.Text = n.Text;
        if (SendSrcText.IsChecked == true) ShowSendPick();
        else SendSrcText.IsChecked = true;
    }
}
