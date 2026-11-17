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
        SendMe.Text = ls.ServerUp ? L.T($"Sichtbar als „{LocalSendService.Alias}“") : L.T("Nur senden – Empfangen ist aus");
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T($"An {d.Alias} senden") };
        _sticky = true;
        if (dlg.ShowDialog(this) == true) _ = H.LocalSend.SendAsync(d, dlg.FileNames);
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
