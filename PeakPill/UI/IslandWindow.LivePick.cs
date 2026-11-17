using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PeakPill.Core;
using PeakPill.Services;
using SD = System.Drawing;

namespace PeakPill.UI;

/// <summary>
/// "Streamen" in a Discord call (user wish 2026-11-17): with one screen it goes live with it straight away; with
/// several the Live page grows and shows a small picture of each screen – the chosen one is streamed. The island
/// opens Discord's own picker in the background, picks that screen and presses "Live gehen".
/// </summary>
public partial class IslandWindow
{
    private const double LivePickH = 178;
    private bool _livePick;
    private LiveApp _pickApp;

    private void LiveStartStream_Click(object sender, RoutedEventArgs e)
    {
        var app = (sender as FrameworkElement)?.Tag as LiveApp ?? H.Privacy.Apps.FirstOrDefault(a => a.IsDiscord);
        if (app == null) return;
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length <= 1) { _ = StreamScreen(app, 1); return; }
        ShowLivePick(app, screens);
    }

    private void ShowLivePick(LiveApp app, System.Windows.Forms.Screen[] screens)
    {
        _pickApp = app;
        _livePick = true;
        LivePickScreens.Children.Clear();
        // laid out like the desk (left to right); the number stays Windows' / Discord's own
        var order = screens.Select((s, i) => (s, n: i + 1)).OrderBy(x => x.s.Bounds.X).ThenBy(x => x.s.Bounds.Y).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            var tile = ScreenTile(order[i].s, order[i].n);
            LivePickScreens.Children.Add(tile);
            if (S.Animations != AnimationLevel.Aus) TileAppear(tile, i);
        }
        UpdateLiveUi();
        if (_state == State.Expanded && _tab == "Live") AnimateSize(ExpW, TabHeight("Live") + NotchExtra, ExpRadius, true);
    }

    private FrameworkElement ScreenTile(System.Windows.Forms.Screen s, int number)
    {
        ImageSource pic = null;
        try
        {
            using var full = ScreenshotService.Capture(s.Bounds);
            int w = 216, h = Math.Max(1, (int)Math.Round(w * (double)s.Bounds.Height / Math.Max(1, s.Bounds.Width)));
            using var small = new SD.Bitmap(full, w, h);
            pic = ScreenshotService.ToBitmapSource(small);
        }
        catch { }

        var img = new Border
        {
            Height = 58,
            CornerRadius = new CornerRadius(6),
            Background = pic != null ? new ImageBrush(pic) { Stretch = Stretch.UniformToFill } : (Brush)FindResource("Card"),
        };
        var name = new TextBlock { Text = $"Bildschirm {number}", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 5, 0, 0) };
        var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? s.Bounds;
        var where = s.Primary ? "Hauptbildschirm" : s.Bounds.X < primary.X ? "links" : s.Bounds.X > primary.X ? "rechts" : s.Bounds.Y < primary.Y ? "oben" : "unten";
        var note = new TextBlock { Text = where, FontSize = 9.5, Margin = new Thickness(2, 0, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        var tile = new Border
        {
            Width = 112,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = $"Bildschirm {number} in Discord streamen",
            Child = new StackPanel { Children = { img, name, note } },
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        tile.MouseEnter += (_, _) => tile.BorderBrush = (Brush)FindResource("Blue");
        tile.MouseLeave += (_, _) => tile.BorderBrush = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var app = _pickApp;
            EndLivePick();
            if (app != null) _ = StreamScreen(app, number);
        };
        return tile;
    }

    /// <summary>The screens come in one after another: a little bigger and transparent → in place.</summary>
    private static void TileAppear(FrameworkElement tile, int i)
    {
        var sc = new ScaleTransform(0.88, 0.88);
        tile.RenderTransform = sc;
        tile.Opacity = 0;
        var begin = TimeSpan.FromMilliseconds(60 + i * 60);
        var grow = new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(360)) { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        tile.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin });
    }

    private void LivePickCancel_Click(object sender, RoutedEventArgs e)
    {
        EndLivePick();
        UpdateLiveUi();
        if (_state == State.Expanded && _tab == "Live") AnimateSize(ExpW, TabHeight("Live") + NotchExtra, ExpRadius, true);
    }

    private void EndLivePick()
    {
        _livePick = false;
        _pickApp = null;
        LivePick.Visibility = Visibility.Collapsed;
    }

    /// <summary>Called at the end of UpdateLiveUi: while choosing, the screens take the place of the call.</summary>
    private void ApplyLivePick()
    {
        if (_livePick && _pickApp != null && !H.Privacy.Apps.Contains(_pickApp)) EndLivePick(); // the call ended meanwhile
        LivePick.Visibility = _livePick ? Visibility.Visible : Visibility.Collapsed;
        if (_livePick) LiveSingle.Visibility = LiveListScroll.Visibility = Visibility.Collapsed;
    }

    /// <summary>Discord: open its picker, choose screen <paramref name="number"/>, "Live gehen" – then back to where you were.</summary>
    private async Task StreamScreen(LiveApp app, int number)
    {
        var before = Native.GetForegroundWindow();
        Collapse();
        ShowHint("display", "Stream wird gestartet …", (Brush)FindResource("Blue"), key: "live");
        await Task.Delay(120);
        bool front = H.Privacy.Open(app);
        await Task.Delay(700);
        var r = await H.Privacy.GoLiveAsync(app, number, LiveLog);
        var screens = System.Windows.Forms.Screen.AllScreens;
        LiveLog($"golive: screen {number} of {screens.Length} (primary = {Array.FindIndex(screens, s => s.Primary) + 1}) front={front} → {r}");
        switch (r)
        {
            case PrivacyService.GoLiveResult.Live:
                for (int i = 0; i < 16; i++)
                {
                    await Task.Delay(250);
                    if (!H.Privacy.Apps.Any(a => a.IsDiscord && a.Screen)) continue;
                    // live: back to the window you came from (Discord stays in the background)
                    if (before != IntPtr.Zero) PrivacyService.Focus(before);
                    return;
                }
                LiveLog("golive: pressed, but no stream seen after 4 s");
                StreamPicked();
                return;
            case PrivacyService.GoLiveResult.PickerOpen:
                StreamPicked();
                return;
            case PrivacyService.GoLiveResult.AlreadyLive:
                // Discord streams already (the island had not noticed): offer to end it instead
                if (before != IntPtr.Zero) PrivacyService.Focus(before);
                ShowPeek(new PeekInfo
                {
                    Icon = "display",
                    Key = "live",
                    GlyphBrush = (Brush)FindResource("Blue"),
                    Title = "Du streamst schon",
                    Text = "Discord überträgt bereits. Erst beenden, dann einen anderen Bildschirm wählen.",
                    TimeoutMs = 7000,
                }.Action("Beenden", () => _ = StopSharing(app), true));
                return;
        }
        // no share button (Discord minimised to the tray, other layout …): the user's keybind, into Discord
        if (DiscordStreamKey(out var g))
        {
            await PasteHelper.SendComboHeldAsync(g);
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(250);
                if (H.Privacy.Apps.Any(a => a.IsDiscord && a.Screen)) { LiveLog("stream: keybind started it"); return; }
            }
        }
        ShowPeek(new PeekInfo
        {
            Icon = "display",
            Key = "live",
            GlyphBrush = (Brush)FindResource("Blue"),
            Title = "Discord ist vorn",
            Text = "Klick unten links auf das Bildschirm-Symbol, um zu streamen.",
            TimeoutMs = 7000,
        });
    }
}
