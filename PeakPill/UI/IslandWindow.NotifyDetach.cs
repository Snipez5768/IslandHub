using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// User wish 2026-11-17: a notification is shown, you go into its app (Windows toast, taskbar, "Öffnen") – the card
/// lets go of the island and drops away, and the app's messages count as read (no badge, no banner any more).
/// </summary>
public partial class IslandWindow
{
    /// <summary>The notification the banner shows right now.</summary>
    private IslandNotification _peekNotif;

    private void WireNotifyDetach()
    {
        H.Foreground.Changed += h => Ui.Run(() => OnForegroundForNotifications(h));
    }

    private void OnForegroundForNotifications(IntPtr h)
    {
        if (h == IntPtr.Zero || H.Foreground.IsOwn(h)) return;
        var unread = H.Notifications.Items.Where(n => n.Unread).ToList();
        bool banner = _state == State.Peek && _peek is { Key: "notif" } && _peekNotif != null;
        if (unread.Count == 0 && !banner) return;
        var app = AppKeys(h);
        if (app.Length == 0) return;

        bool any = false;
        foreach (var n in unread.Where(n => FromApp(n, app))) { n.Unread = false; any = true; }
        if (any) UpdateNotifyUi();
        if (banner && FromApp(_peekNotif, app)) DetachPeek();
    }

    /// <summary>Names the foreground app goes by: its process name and its description ("WhatsApp", "Discord").</summary>
    private static string[] AppKeys(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out var pid);
        string proc = null;
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
        return new[] { Norm(proc), Norm(ForegroundTracker.AppNameOf(h)) }
            .Where(k => k.Length >= 3 && k is not ("applicationframehost" or "explorer" or "peakpill")).Distinct().ToArray();
    }

    private static bool FromApp(IslandNotification n, string[] keys)
    {
        var name = Norm(n.AppName);
        var hay = name + "|" + Norm(n.AppId);
        return keys.Any(k => hay.Contains(k) || name.Length >= 3 && k.Contains(name));
    }

    private static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The card comes loose from the island and falls away while the island shrinks back to the pill.</summary>
    private void DetachPeek()
    {
        _peekNotif = null;
        if (S.Animations == AnimationLevel.Aus || Island.ActualWidth < 1) { EndPeek(); return; }
        BitmapSource shot = null;
        Point at = default;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(Island.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Island.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(Island), null, new Rect(0, 0, Island.ActualWidth, Island.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            shot = rtb;
            at = Island.TranslatePoint(new Point(0, 0), Root);
        }
        catch { }
        EndPeek();
        if (shot == null) return;

        var img = new Image { Source = shot, Width = Island.ActualWidth, Height = Island.ActualHeight, RenderTransformOrigin = new Point(0.5, 0) };
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        img.RenderTransform = new TransformGroup { Children = { scale, move } };
        Canvas.SetLeft(img, at.X);
        Canvas.SetTop(img, at.Y);
        FlyLayer.Children.Add(img);

        // a short tug upwards (still stuck), then it lets go and drops, getting smaller and fading
        var drop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromPercent(0.18), new SineEase { EasingMode = EasingMode.EaseOut }));
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(46, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseIn }));
        move.BeginAnimation(TranslateTransform.YProperty, drop);
        var shrink = new DoubleAnimation(1, 0.88, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = TimeSpan.FromMilliseconds(140) };
        fade.Completed += (_, _) => FlyLayer.Children.Remove(img);
        img.BeginAnimation(OpacityProperty, fade);
    }
}
