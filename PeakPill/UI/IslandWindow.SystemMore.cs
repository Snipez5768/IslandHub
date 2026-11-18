using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// The system block in the island (user's picks 2026-11-18):
/// 23a a device battery at 20 % becomes a red ring (Act.Device), at 10 % a notice · 23b/24b/29 a status view in the
/// control center (network, device batteries, laptop battery) · 24a moments when Wi-Fi or VPN change · 25 a waiting
/// Windows update with "Heute 22:00" · 26 USB sticks with "Auswerfen" · 27 the printer as a ring, done / paper out ·
/// 28 temperatures in the game mode (Act/UpdatePill).
/// </summary>
public partial class IslandWindow
{
    private readonly HashSet<string> _ejecting = new();
    private DateTime? _restartAt;
    private DispatcherTimer _restartTimer;
    private bool _statusOpen;

    private void WireSystem()
    {
        var sys = H.Sys;
        StatusBtn.ToolTip = L.T("Netzwerk und Akkus");
        StatusTitle.Text = L.T("Netzwerk und Akkus");
        StatusBack.Text = L.T("Zurück");
        StatusBack.MouseEnter += (_, _) => StatusBack.Foreground = Brushes.White;
        StatusBack.MouseLeave += (_, _) => StatusBack.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        StatusBack.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowStatus(false); };

        sys.WifiChanged += name => Ui.Run(() =>
        {
            if (name == null) { ShowHint("wifi", L.T("WLAN getrennt"), (Brush)FindResource("SubText"), key: "net"); return; }
            ShowHint("wifi", name.Length > 0 ? L.T($"Verbunden · {name}") : L.T("WLAN verbunden"), (Brush)FindResource("Blue"), key: "net");
            WaveHintIcon(); // the waves light up one after the other
        });
        sys.VpnChanged += on => Ui.Run(() =>
            ShowHint("shield", on ? L.T("VPN an") : L.T("VPN aus"), on ? (Brush)FindResource("Green") : (Brush)FindResource("SubText"), check: on, key: "net"));
        sys.DriveAdded += d => Ui.Run(() => OnDriveAdded(d));
        sys.DriveRemoved += root => Ui.Run(() =>
        {
            _peeks.RemoveAll(p => p.Key == "usb:" + root);
            if (_peek?.Key == "usb:" + root) EndPeek();
            if (_ejecting.Remove(root)) ShowHint("check", L.T("Kann raus"), (Brush)FindResource("Green"), check: true, key: "usb");
        });
        sys.PrintDone += () => Ui.Run(() => ShowHint("doc", L.T("Gedruckt"), (Brush)FindResource("Green"), check: true, key: "print"));
        sys.PrintProblem += text => Ui.Run(() =>
        {
            Wobble(); // you have to do something (user wish: wherever you must act)
            ShowPeek(new PeekInfo { Key = "print", Important = true, Icon = "doc", GlyphBrush = (Brush)FindResource("Danger"), Title = text, Text = L.T("Drucker"), TimeoutMs = 12000 });
        });
        sys.DeviceCritical += d => Ui.Run(() =>
            ShowHint(d.Icon, L.T($"{d.Name} · {d.Level * 100:0} % – bald laden"), (Brush)FindResource("Danger"), key: "devbat"));
        sys.UpdateWaiting += () => Ui.Run(OfferRestart);
        sys.PropertyChanged += (_, e) => Ui.Run(() =>
        {
            if (e.PropertyName is nameof(SystemWatch.Devices) or nameof(SystemWatch.Printing) or nameof(SystemWatch.GpuTemp)) UpdatePill();
            if (_statusOpen && e.PropertyName is nameof(SystemWatch.Down) or nameof(SystemWatch.Devices) or nameof(SystemWatch.Wifi)) BuildStatus();
        });
    }

    // ---------- 24a ----------

    private void WaveHintIcon()
    {
        if (S.Animations == AnimationLevel.Aus) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1200) };
        foreach (var (t, v) in new[] { (0.0, 0.25), (0.2, 1.0), (0.4, 0.35), (0.6, 1.0), (0.8, 0.5), (1.0, 1.0) })
            a.KeyFrames.Add(new LinearDoubleKeyFrame(v, KeyTime.FromPercent(t)));
        HintIcon.BeginAnimation(OpacityProperty, a);
    }

    // ---------- 26 ----------

    private void OnDriveAdded(RemovableDrive d)
    {
        var p = new PeekInfo
        {
            Key = "usb:" + d.Root, Icon = "folder", GlyphBrush = (Brush)FindResource("Blue"),
            Source = "USB", SourceIcon = "folder", SourceBrush = (Brush)FindResource("Blue"),
            Title = d.Label, Text = L.T($"{SizeText(d.Free)} frei"), TimeoutMs = 9000,
            Click = () => Launch(d.Root),
        };
        p.Action(L.T("Öffnen"), () => Launch(d.Root));
        p.Action(L.T("Auswerfen"), () =>
        {
            _ejecting.Add(d.Root);
            ShowHint("folder", L.T("Wird ausgeworfen …"), (Brush)FindResource("SubText"), key: "usb");
            Task.Run(() => SystemWatch.Eject(d.Root));
            After(8000, () => { if (_ejecting.Remove(d.Root)) ShowHint("xmark", L.T("Noch in Benutzung"), (Brush)FindResource("Orange"), key: "usb"); });
        }, true);
        ShowPeek(p);
    }

    // ---------- 25 ----------

    private void OfferRestart()
    {
        var p = new PeekInfo
        {
            Key = "update", Important = true, Icon = "refresh", GlyphBrush = (Brush)FindResource("Orange"),
            Source = "Windows Update", SourceIcon = "refresh", SourceBrush = (Brush)FindResource("Orange"),
            Title = L.T("Update wartet auf Neustart"), TimeoutMs = 0,
        };
        var tonight = DateTime.Today.AddHours(22);
        if (DateTime.Now > tonight.AddMinutes(-10)) tonight = DateTime.Now.AddMinutes(30);
        p.Action(L.T("Später"), () => { });
        p.Action(L.T("Jetzt"), ConfirmRestartNow);
        p.Action(L.T($"Heute {tonight:HH:mm}"), () => PlanRestart(tonight), true);
        ShowPeek(p);
    }

    private void ConfirmRestartNow()
    {
        var p = new PeekInfo
        {
            Key = "update", Important = true, Icon = "refresh", GlyphBrush = (Brush)FindResource("Orange"),
            Title = L.T("Jetzt neu starten?"), Text = L.T("Speichere vorher deine Arbeit."), TimeoutMs = 0,
        };
        p.Action(L.T("Abbrechen"), () => { });
        p.Action(L.T("Neu starten"), () => SystemWatch.RestartAt(DateTime.Now.AddSeconds(5)), true);
        ShowPeek(p);
    }

    /// <summary>Windows' own planned restart; 10 minutes before, the island counts down with "Abbrechen".</summary>
    private void PlanRestart(DateTime at)
    {
        SystemWatch.RestartAt(at);
        _restartAt = at;
        ShowHint("check", L.T($"Neustart um {at:HH:mm}"), (Brush)FindResource("Green"), check: true, key: "update");
        _restartTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _restartTimer.Tick -= RestartTick;
        _restartTimer.Tick += RestartTick;
        _restartTimer.Start();
    }

    private bool _restartWarned;

    private void RestartTick(object sender, EventArgs e)
    {
        if (_restartAt is not { } at) { _restartTimer.Stop(); return; }
        if (_restartWarned || at - DateTime.Now > TimeSpan.FromMinutes(10)) return;
        _restartWarned = true;
        var p = new PeekInfo
        {
            Key = "update", Important = true, Icon = "refresh", GlyphBrush = (Brush)FindResource("Orange"),
            Title = L.T($"Neustart um {at:HH:mm}"), Text = L.T("In 10 Minuten startet der PC neu."), TimeoutMs = 0,
        };
        p.Action(L.T("Abbrechen"), () => { SystemWatch.CancelRestart(); _restartAt = null; ShowHint("xmark", L.T("Neustart abgebrochen"), (Brush)FindResource("SubText"), key: "update"); });
        p.Action("OK", () => { }, true);
        Wobble();
        ShowPeek(p);
    }

    // ---------- 23b / 24b / 29 ----------

    private void Status_Click(object sender, RoutedEventArgs e) => ShowStatus(true);

    private void ShowStatus(bool on)
    {
        _statusOpen = on;
        if (on && MixerView.Visibility == Visibility.Visible) MixerView.Visibility = Visibility.Collapsed;
        TabControls.Children[0].Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TabControls.Children[1].Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) BuildStatus(animate: true);
        if (S.Animations != AnimationLevel.Aus)
            TabControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static string Rate(double bps) =>
        bps >= 1 << 20 ? $"{bps / (1 << 20):0.0} MB/s" : bps >= 1024 ? $"{bps / 1024:0} KB/s" : $"{bps:0} B/s";

    private void BuildStatus(bool animate = false)
    {
        var sys = H.Sys;
        var grey = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB2));
        StatusRows.Children.Clear();
        DockPanel Row(string icon, Brush color, string text, string right, Brush rightColor = null)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var r = new TextBlock { Text = right ?? "", FontSize = 10.5, Foreground = rightColor ?? grey, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(r, Dock.Right);
            row.Children.Add(r);
            row.Children.Add(new SfIcon { Kind = icon, Size = 12, Foreground = color, Width = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            StatusRows.Children.Add(row);
            return row;
        }
        string net = sys.Wifi is { } w ? (w.Length > 0 ? w : L.T("WLAN")) : sys.Online ? L.T("Kabel") : L.T("Offline");
        Row("wifi", (Brush)FindResource("Blue"), $"↓ {Rate(sys.Down)}   ↑ {Rate(sys.Up)}", net + (sys.Vpn ? " · VPN" : ""), sys.Vpn ? (Brush)FindResource("Green") : null);
        foreach (var d in sys.Devices.Take(sys.HasBattery ? 1 : 2))
            Row(d.Icon, d.Level <= 0.2 ? (Brush)FindResource("Danger") : (Brush)FindResource("Green"), d.Name, $"{d.Level * 100:0} %", d.Level <= 0.2 ? (Brush)FindResource("Danger") : null);
        if (sys.HasBattery)
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            Row("battery", (Brush)FindResource("Green"), $"{ps.BatteryLifePercent * 100:0} %", sys.Forecast ?? "");
        }
        if (sys.Devices.Count == 0 && !sys.HasBattery) Row("battery", grey, L.T("Keine Geräte mit Akku"), "");
        if (!animate || S.Animations == AnimationLevel.Aus) return;
        int i = 0;
        foreach (FrameworkElement r in StatusRows.Children) RowIn(r, 60 + i++ * 80);
    }
}
