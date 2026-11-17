using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PeakPill.Core;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

/// <summary>
/// A see-through, click-through layer over one screen for things that fly across it (2026-11-17): a copied text from
/// the pointer into the island, a screenshot into the island, a finished download down to the taskbar. The island's
/// own window is only 560 × 360, so these need their own. It is shown only while something flies.
/// </summary>
public sealed class FlightWindow : Window
{
    private static FlightWindow _one;
    private readonly Canvas _canvas = new();
    private int _flying;
    private WF.Screen _screen;

    private FlightWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED));
        };
    }

    /// <summary>The layer over <paramref name="screen"/>, ready to draw on (DIP coordinates of that screen).</summary>
    private static FlightWindow Over(WF.Screen screen)
    {
        _one ??= new FlightWindow();
        var w = _one;
        if (w._screen?.DeviceName != screen.DeviceName || !w.IsVisible)
        {
            w._screen = screen;
            if (!w.IsVisible) { w.Left = screen.Bounds.Left; w.Top = screen.Bounds.Top; w.Width = 100; w.Height = 100; w.Show(); }
            var h = new WindowInteropHelper(w).Handle;
            var b = screen.Bounds;
            Native.SetWindowPos(h, new IntPtr(-1), b.Left, b.Top, b.Width, b.Height, 0x0010); // HWND_TOPMOST, NOACTIVATE
        }
        return w;
    }

    private Point ToLocal(Point screenPx)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point((screenPx.X - _screen.Bounds.Left) / dpi.DpiScaleX, (screenPx.Y - _screen.Bounds.Top) / dpi.DpiScaleY);
    }

    /// <summary>
    /// Flies <paramref name="el"/> (centred) from one screen point to another (physical pixels), shrinking to
    /// <paramref name="endScale"/>. <paramref name="arc"/> bends the path (x eases out, y eases in = a throw).
    /// Returns when it has landed; the element is removed then.
    /// </summary>
    public static Task Fly(FrameworkElement el, Point fromPx, Point toPx, double endScale, int ms, bool arc = true, double endOpacity = 0.0, int fadeMs = 120)
    {
        var tcs = new TaskCompletionSource<bool>();
        var screen = WF.Screen.FromPoint(new System.Drawing.Point((int)toPx.X, (int)toPx.Y));
        var w = Over(screen);
        w._flying++;
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ew = el.DesiredSize.Width, eh = el.DesiredSize.Height;
        var a = w.ToLocal(fromPx);
        var b = w.ToLocal(toPx);
        var move = new TranslateTransform(a.X - ew / 2, a.Y - eh / 2);
        var scale = new ScaleTransform(1, 1, ew / 2, eh / 2);
        el.RenderTransform = new TransformGroup { Children = { scale, move } };
        w._canvas.Children.Add(el);

        var dur = TimeSpan.FromMilliseconds(ms);
        IEasingFunction ex = arc ? new CubicEase { EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseInOut };
        IEasingFunction ey = arc ? new SineEase { EasingMode = EasingMode.EaseIn } : new CubicEase { EasingMode = EasingMode.EaseInOut };
        var ax = new DoubleAnimation(a.X - ew / 2, b.X - ew / 2, dur) { EasingFunction = ex };
        var ay = new DoubleAnimation(a.Y - eh / 2, b.Y - eh / 2, dur) { EasingFunction = ey };
        var sc = new DoubleAnimation(1, endScale, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var fade = new DoubleAnimation(1, endOpacity, TimeSpan.FromMilliseconds(fadeMs)) { BeginTime = dur - TimeSpan.FromMilliseconds(Math.Min(fadeMs, ms)) };
        fade.Completed += (_, _) =>
        {
            w._canvas.Children.Remove(el);
            if (--w._flying <= 0) { w._flying = 0; w.Hide(); }
            tcs.TrySetResult(true);
        };
        move.BeginAnimation(TranslateTransform.XProperty, ax);
        move.BeginAnimation(TranslateTransform.YProperty, ay);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
        el.BeginAnimation(OpacityProperty, fade);
        return tcs.Task;
    }
}
