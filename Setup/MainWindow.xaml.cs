using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PeakPillSetup;

public partial class MainWindow : Window
{
    private readonly bool _uninstall;
    private readonly DispatcherTimer _blink = new();
    private readonly Random _rnd = new();
    private bool _busy, _removed;

    public MainWindow(bool uninstall)
    {
        InitializeComponent();
        _uninstall = uninstall;
        if (uninstall) ShowRemove();
        else ShowWelcome(Installer.InstalledVersion, Installer.HadIslandHub, Installer.RuntimeMissing, Installer.AutostartOn);
        _blink.Interval = TimeSpan.FromSeconds(2.5);
        _blink.Tick += (_, _) => Blink();
        _blink.Start();
    }

    // ------------------------------------------------------------------ pages

    private void Page(UIElement page)
    {
        foreach (var p in new UIElement[] { PageWelcome, PageProgress, PageDone, PageRemove })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        if (page is FrameworkElement fe)
        {
            fe.Opacity = 0;
            fe.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }
    }

    private void ShowWelcome(string installed, bool hadOld, bool runtimeMissing, bool autostartOn)
    {
        Heading.Text = "PeakPill";
        if (installed != null)
        {
            Sub.Text = installed == Installer.Version ? $"Version {installed} ist schon installiert." : $"Version {installed} ist installiert – jetzt kommt {Installer.Version}.";
            InstallBtn.Content = installed == Installer.Version ? "Neu installieren" : "Aktualisieren";
            OptAutostart.IsChecked = autostartOn;
        }
        else if (hadOld)
        {
            Sub.Text = "Früher hieß PeakPill „IslandHub“ – deine Einstellungen werden übernommen.";
            OptAutostart.IsChecked = autostartOn;
        }
        else Sub.Text = "Eine Dynamic Island für Windows";
        var note = runtimeMissing ? ".NET 8 von Microsoft wird mit installiert – Windows fragt einmal nach Erlaubnis.\n" : "";
        WelcomeNote.Text = note + $"Version {Installer.Version} · nur für dich, ohne Administrator-Rechte";
        Page(PageWelcome);
    }

    private void ShowRemove()
    {
        Heading.Text = "PeakPill entfernen?";
        Sub.Text = "Die Insel, ihr Autostart und die Verknüpfungen werden entfernt.";
        Page(PageRemove);
    }

    private void ShowDone(string heading, string sub, string note, bool launchOption, string button = "Fertig")
    {
        Heading.Text = heading;
        Sub.Text = sub;
        DoneNote.Text = note;
        DoneNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        LaunchCard.Visibility = launchOption ? Visibility.Visible : Visibility.Collapsed;
        DoneBtn.Content = button;
        Page(PageDone);
    }

    private void SetProgress(double part, string text)
    {
        Status.Text = text;
        double w = Math.Max(0, Math.Min(1, part)) * ((FrameworkElement)Bar.Parent).ActualWidth;
        Bar.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ------------------------------------------------------------------ actions

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        CloseBtn.IsEnabled = false;
        Heading.Text = "Wird installiert";
        Sub.Text = "Das dauert nur einen Moment.";
        Page(PageProgress);
        await Task.Delay(60); // the bar has its width now
        var opts = new Installer.Options { Autostart = OptAutostart.IsChecked == true, DesktopLink = OptDesktop.IsChecked == true };
        try
        {
            await Installer.InstallAsync(opts, new Progress<(double Part, string Text)>(p => SetProgress(p.Part, p.Text)));
            await Task.Delay(350);
            ShowDone("PeakPill ist bereit", "Die Insel erscheint oben in der Mitte deines Bildschirms.",
                     "Du findest PeakPill im Startmenü. Entfernen kannst du es unter Einstellungen › Apps.", true);
        }
        catch (Exception ex)
        {
            ShowDone("Das hat nicht geklappt", ex.Message, "", false, "Schließen");
        }
        finally
        {
            _busy = false;
            CloseBtn.IsEnabled = true;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchCard.Visibility == Visibility.Visible && OptLaunch.IsChecked == true) Installer.Launch();
        Close();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        CloseBtn.IsEnabled = false;
        Heading.Text = "Wird entfernt";
        Sub.Text = "Einen Moment …";
        Page(PageProgress);
        await Task.Delay(60);
        SetProgress(0.5, "PeakPill wird entfernt …");
        bool keep = OptKeep.IsChecked == true;
        await Task.Run(() => Installer.Uninstall(keep));
        SetProgress(1, "Fertig");
        await Task.Delay(300);
        _busy = false;
        _removed = true;
        CloseBtn.IsEnabled = true;
        ShowDone("PeakPill wurde entfernt", "Danke fürs Ausprobieren!", keep ? "Deine Einstellungen sind noch da – für den Fall, dass du wiederkommst." : "", false, "Schließen");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_uninstall && _removed) Installer.DeleteSelfLater();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void Blink()
    {
        _blink.Interval = TimeSpan.FromSeconds(3 + _rnd.NextDouble() * 3);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(190) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.12, KeyTime.FromPercent(0.4), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        LogoBlink.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ------------------------------------------------------------------ pictures of every page (layout check)

    public async Task ShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        _blink.Stop();
        async Task Shot(string name)
        {
            await Task.Delay(400);
            UpdateLayout();
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            rtb.Render(bg);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        ShowWelcome(null, false, false, false); await Shot("1_welcome");
        ShowWelcome(null, true, true, true); await Shot("2_welcome_old_runtime");
        ShowWelcome("1.2.0", false, false, true); await Shot("3_update");
        Heading.Text = "Wird installiert"; Sub.Text = "Das dauert nur einen Moment."; Page(PageProgress); await Task.Delay(100);
        SetProgress(0.62, "PeakPill wird kopiert …"); await Shot("4_progress");
        ShowDone("PeakPill ist bereit", "Die Insel erscheint oben in der Mitte deines Bildschirms.",
                 "Du findest PeakPill im Startmenü. Entfernen kannst du es unter Einstellungen › Apps.", true); await Shot("5_done");
        ShowRemove(); await Shot("6_remove");
        ShowDone("PeakPill wurde entfernt", "Danke fürs Ausprobieren!", "Deine Einstellungen sind noch da – für den Fall, dass du wiederkommst.", false, "Schließen"); await Shot("7_removed");
        ShowDone("Das hat nicht geklappt", "Die Erlaubnis für .NET wurde nicht gegeben. PeakPill braucht .NET 8 – starte den Installer einfach noch einmal.", "", false, "Schließen"); await Shot("8_error");
    }
}
