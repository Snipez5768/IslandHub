using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// LocalSend in the island (user wish 2026-11-17): the LocalSend page shows the phones and PCs nearby – drop files
/// on one (or click it and choose files) to send them. When someone sends something, the island asks first
/// ("Annehmen" / "Ablehnen"); received files land in Downloads\PeakPill and in the Ablage.
/// </summary>
public partial class IslandWindow
{
    private void WireSend()
    {
        var ls = H.LocalSend;
        ls.Ask = AskReceive;
        ls.Received += files => Ui.Run(() => OnReceived(files));
        ls.Notice += (msg, ok) => Ui.Run(() => ShowHint(ok ? "check" : "xmark", msg, (Brush)FindResource(ok ? "Green" : "Danger"), key: "send"));
        ls.PropertyChanged += (_, _) => Ui.Run(UpdateSendUi);
        ls.Devices.CollectionChanged += (_, _) => Ui.Run(UpdateSendUi);
        UpdateSendUi();
    }

    private void UpdateSendUi()
    {
        if (SendMe == null || H?.LocalSend == null) return;
        var ls = H.LocalSend;
        UpdateSendHint();
        SendEmpty.Visibility = ls.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool status = ls.Moving || !string.IsNullOrEmpty(ls.Status);
        SendStatusRow.Visibility = status ? Visibility.Visible : Visibility.Collapsed;
        SendStatus.Text = ls.Status;
        SendBar.Visibility = ls.Moving ? Visibility.Visible : Visibility.Collapsed;
        if (ls.Moving) SendBarFill.Width = Math.Max(3, (ExpW - 28) * ls.Progress);
        // devices found or gone: the page grows or shrinks with them
        if (_state == State.Expanded && _tab == "Send" && Math.Abs(TabHeight("Send") + NotchExtra - _targetH) > 0.5)
            AnimateSize(ExpW, TabHeight("Send") + NotchExtra, ExpRadius, true);
    }

    private void SendRefresh_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in H.LocalSend.Devices.ToList()) H.LocalSend.Devices.Remove(d);
        _ = H.LocalSend.Announce();
    }

    private void SendFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LocalSendService.SaveDir);
        try { Process.Start("explorer.exe", $"\"{LocalSendService.SaveDir}\""); } catch { }
    }

    private void SendDevice_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LocalSendDevice d) return;
        e.Handled = true;
        // something chosen above (a copy, a text, files): that goes; otherwise pick files now
        if (SendStaged(d)) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T($"An {d.Alias} senden") };
        _sticky = true;
        if (dlg.ShowDialog(this) == true) _ = H.LocalSend.SendAsync(d, dlg.FileNames);
    }

    // ---------- what to send: from the clipboard, a text of your own, files (user wish 2026-11-17) ----------

    private string _sendSrc, _sendSrcBefore;
    private ClipItem _sendClip;
    private readonly List<string> _sendFiles = new();

    private void SendSrc_Checked(object sender, RoutedEventArgs e)
    {
        _sendSrcBefore = _sendSrc;
        _sendSrc = sender == SendSrcClip ? "clip" : sender == SendSrcText ? "text" : "files";
        ShowSendPick();
        if (_sendSrc == "files") PickSendFiles();
        if (_sendSrc == "text") Dispatcher.BeginInvoke(new Action(() => SendText.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A second click on the chosen source closes it again.</summary>
    private void SendSrc_Click(object sender, RoutedEventArgs e)
    {
        var src = sender == SendSrcClip ? "clip" : sender == SendSrcText ? "text" : "files";
        if (_sendSrcBefore == src && _sendSrc == src)
        {
            ((RadioButton)sender).IsChecked = false;
            _sendSrc = null;
            ShowSendPick();
        }
        _sendSrcBefore = _sendSrc;
    }

    private void PickSendFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        _sticky = true;
        if (dlg.ShowDialog(this) == true) { _sendFiles.Clear(); _sendFiles.AddRange(dlg.FileNames); }
        ShowSendPick();
    }

    private void SendText_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSendHint();
        UpdateSendUi();
    }

    private void ShowSendPick()
    {
        SendPick.Visibility = _sendSrc == null ? Visibility.Collapsed : Visibility.Visible;
        SendClipScroll.Visibility = _sendSrc is "clip" or "files" ? Visibility.Visible : Visibility.Collapsed;
        SendTextBox.Visibility = _sendSrc == "text" ? Visibility.Visible : Visibility.Collapsed;
        SendClipList.Children.Clear();
        if (_sendSrc == "clip")
            foreach (var item in H.Clipboard.Items.Take(10)) SendClipList.Children.Add(SendCard(item));
        else if (_sendSrc == "files")
            foreach (var f in _sendFiles) SendClipList.Children.Add(FileChip(f));
        UpdateSendHint();
        UpdateSendUi();
    }

    /// <summary>A recent copy as a small card – a picture shows itself, a text its first lines. Click = chosen.</summary>
    private FrameworkElement SendCard(ClipItem item)
    {
        FrameworkElement inner;
        if (item.IsImage && item.ImageFile != null && File.Exists(item.ImageFile))
            inner = new Border { CornerRadius = new CornerRadius(7), Background = new ImageBrush(ShellIcon.Get(item.ImageFile)) { Stretch = Stretch.UniformToFill } };
        else
        {
            var t = new TextBlock { Text = item.Preview, FontSize = 10, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 5, 7, 5), Foreground = Brushes.White };
            if (item.Kind == ClipKind.Files) t.Text = string.Join("\n", item.Files?.Select(Path.GetFileName) ?? Enumerable.Empty<string>());
            inner = t;
        }
        var card = new Border
        {
            Width = 84, Height = 54, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(2),
            BorderBrush = item == _sendClip ? (Brush)FindResource("Accent") : Brushes.Transparent,
            Cursor = Cursors.Hand, Child = inner, ToolTip = item.Kind == ClipKind.Image ? null : item.Preview,
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _sendClip = _sendClip == item ? null : item;
            foreach (Border b in SendClipList.Children.OfType<Border>()) b.BorderBrush = Brushes.Transparent;
            if (_sendClip != null) card.BorderBrush = (Brush)FindResource("Accent");
            if (_sendClip != null && S.Animations != AnimationLevel.Aus) Pop(card);
            UpdateSendHint();
        };
        return card;
    }

    private static FrameworkElement FileChip(string path) => new Border
    {
        Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(9, 4, 9, 4), CornerRadius = new CornerRadius(9), Height = 26,
        Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
        Child = new TextBlock { Text = Path.GetFileName(path), FontSize = 10.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis },
    };

    /// <summary>The line on top says what a tap on a device would send.</summary>
    private void UpdateSendHint()
    {
        if (SendMe == null) return;
        string what = _sendSrc switch
        {
            "clip" when _sendClip != null => _sendClip.IsImage ? L.T("das Bild") : _sendClip.Kind == ClipKind.Files ? L.T("die Dateien") : Quote(_sendClip.Preview),
            "text" when SendText.Text.Trim().Length > 0 => Quote(SendText.Text.Trim()),
            "files" when _sendFiles.Count > 0 => _sendFiles.Count == 1 ? Path.GetFileName(_sendFiles[0]) : L.T($"{_sendFiles.Count} Dateien"),
            _ => null,
        };
        SendMe.Text = what != null ? L.T($"Gerät antippen – sendet {what}")
            : _sendSrc == "clip" ? L.T("Wähl eine Kopie, dann ein Gerät")
            : H.LocalSend.ServerUp ? L.T($"Sichtbar als „{LocalSendService.Alias}“") : L.T("Nur senden – Empfangen ist aus");
        SendMe.Foreground = what != null ? (Brush)FindResource("Accent") : (Brush)FindResource("SubText");

        static string Quote(string s)
        {
            s = (s ?? "").Replace('\n', ' ');
            if (s.Length > 24) s = s[..24] + "…";
            return L.English ? "“" + s + "”" : "„" + s + "“";
        }
    }

    /// <summary>Sends what is chosen above to <paramref name="d"/>; false = nothing chosen.</summary>
    private bool SendStaged(LocalSendDevice d)
    {
        switch (_sendSrc)
        {
            case "clip" when _sendClip != null:
                var item = _sendClip;
                if (item.IsImage && item.ImageFile != null) _ = H.LocalSend.SendAsync(d, new[] { item.ImageFile });
                else if (item.Kind == ClipKind.Files && item.Files is { Count: > 0 }) _ = H.LocalSend.SendAsync(d, item.Files);
                else _ = H.LocalSend.SendTextAsync(d, item.Text ?? "");
                return true;
            case "text" when SendText.Text.Trim().Length > 0:
                var text = SendText.Text.Trim();
                _ = SendTextThenClear(d, text);
                return true;
            case "files" when _sendFiles.Count > 0:
                _ = H.LocalSend.SendAsync(d, _sendFiles.ToList());
                return true;
        }
        return false;
    }

    private async Task SendTextThenClear(LocalSendDevice d, string text)
    {
        if (await H.LocalSend.SendTextAsync(d, text) && SendText.Text.Trim() == text) SendText.Clear();
    }

    /// <summary>As tall as what is open (devices, the copies, the text field).</summary>
    private double SendHeight()
    {
        if (TabSend == null) return ExpH;
        const double Chrome = 7 + 24 + 6 + 10;
        TabSend.Measure(new Size(ExpW - 24, double.PositiveInfinity));
        return Math.Clamp(Math.Ceiling(Chrome + TabSend.DesiredSize.Height + 4), ExpH, 330);
    }

    private void SendDevice_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x0A, 0x84, 0xFF));
    }

    private void SendDevice_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.Background = Brushes.Transparent;
    }

    private void SendDevice_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.Background = Brushes.Transparent;
        if ((sender as FrameworkElement)?.Tag is not LocalSendDevice d) return;
        e.Handled = true;
        var files = DroppedFiles(e);
        if (files.Count > 0) _ = H.LocalSend.SendAsync(d, files);
    }

    /// <summary>Dropped on the page, not on a device: with one device nearby it goes there.</summary>
    private void Send_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedFiles(e);
        if (files.Count == 0) return;
        if (H.LocalSend.Devices.Count == 1) _ = H.LocalSend.SendAsync(H.LocalSend.Devices[0], files);
        else ShowHint("share", L.T(H.LocalSend.Devices.Count == 0 ? "Kein Gerät in der Nähe" : "Auf ein Gerät ziehen"), null, key: "send");
    }

    private static List<string> DroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(File.Exists).ToList() : new List<string>();

    /// <summary>Someone wants to send: a card with "Annehmen" / "Ablehnen" (nothing comes in without a yes).</summary>
    private Task<bool> AskReceive(LocalSendRequest r)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Run(() =>
        {
            long size = r.Files.Sum(f => f.Size);
            // the name as the title, what it is underneath – a long sentence did not fit the card
            string what = r.Files.Count == 1 ? L.T($"möchte „{r.Files[0].Name}“ senden · {Size(size)}") : L.T($"möchte {r.Files.Count} Dateien senden · {Size(size)}");
            var peek = new PeekInfo
            {
                Icon = r.DeviceType == "mobile" ? "iphone" : "display",
                Key = "localsend",
                Important = true,
                Source = "LocalSend",
                SourceIcon = "share",
                Title = r.From,
                Text = what,
                TimeoutMs = 55000,
            };
            peek.Action(L.T("Ablehnen"), () => tcs.TrySetResult(false));
            peek.Action(L.T("Annehmen"), () => tcs.TrySetResult(true), true);
            ShowPeek(peek);
            After(56000, () => tcs.TrySetResult(false));
        });
        return tcs.Task;
    }

    private static string Size(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    private void OnReceived(IReadOnlyList<string> files)
    {
        try { H.Shelf.Add(files); } catch { }
        var first = files[0];
        var peek = new PeekInfo
        {
            Icon = "download",
            Key = "localsend",
            Source = "LocalSend",
            SourceIcon = "share",
            GlyphBrush = (Brush)FindResource("Green"),
            Title = files.Count == 1 ? Path.GetFileName(first) : L.T($"{files.Count} Dateien empfangen"),
            Text = files.Count == 1 ? L.T("Empfangen · in der Ablage") : L.T("In Downloads\\PeakPill und in der Ablage"),
            TimeoutMs = 6000,
        };
        if (ShellIcon.IsImage(first)) { peek.Image = ShellIcon.Get(first); }
        peek.Action(L.T("Zeigen"), () => { try { Process.Start("explorer.exe", $"/select,\"{first}\""); } catch { } });
        ShowPeek(peek);
    }
}
