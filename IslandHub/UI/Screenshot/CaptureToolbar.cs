using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using IslandHub.Core;
using IslandHub.Services;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace IslandHub.UI;

/// <summary>
/// Floating Liquid-Glass capture bar (like macOS ⇧⌘5). Its glass shows a real blurred copy of the screen behind it.
/// Non-activating, so the overlay underneath keeps keyboard focus (Esc / Enter).
/// </summary>
public sealed class CaptureToolbar : Window
{
    private readonly CaptureSession _session;
    private readonly WF.Screen _screen;
    private readonly SD.Bitmap _shot;
    private readonly Image _backdrop = new() { Stretch = Stretch.None };
    private readonly Border _shell;
    private readonly TextBlock _hint = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
    private readonly Button _timer;
    private readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public event Action Cancelled;
    public event Action RepeatLast;

    private static readonly (CaptureMode Mode, string Icon, string Tip, string Hint)[] Modes =
    {
        (CaptureMode.Area, "selection", "Bereich", "Bereich aufziehen"),
        (CaptureMode.Window, "window", "Fenster", "Fenster anklicken"),
        (CaptureMode.Screen, "display", "Ganzer Bildschirm", "Bildschirm anklicken"),
        (CaptureMode.Freeform, "lasso", "Freihand", "Form umfahren"),
        (CaptureMode.Gif, "record", "Aufnahme als GIF", "Bereich für GIF ziehen"),
        (CaptureMode.Picker, "eyedropper", "Farbpipette", "Farbe anklicken"),
    };

    public CaptureToolbar(CaptureSession session, WF.Screen screen, SD.Bitmap shot)
    {
        _session = session;
        _screen = screen;
        _shot = shot;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        SetResourceReference(FontFamilyProperty, "TextFont");
        Foreground = Brushes.White;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var (mode, icon, tip, hint) in Modes)
        {
            var rb = new RadioButton { GroupName = "CaptureMode", ToolTip = tip, IsChecked = mode == session.Mode, Width = 42, Height = 34 };
            rb.SetResourceReference(StyleProperty, "TabButton");
            Ext.SetIcon(rb, icon);
            var m = mode; var h = hint;
            rb.Checked += (_, _) => { _session.Mode = m; _hint.Text = h; _session.Notify(); };
            if (mode == CaptureMode.Gif) rb.Foreground = (Brush)Application.Current.FindResource("Danger");
            row.Children.Add(rb);
        }
        row.Children.Add(Separator());

        _timer = new Button { Margin = new Thickness(2, 0, 2, 0), ToolTip = "Selbstauslöser" };
        _timer.SetResourceReference(StyleProperty, "ChipButton");
        Ext.SetIcon(_timer, "timer");
        Ext.SetIconSize(_timer, 13);
        _timer.Click += (_, _) =>
        {
            _session.DelaySeconds = _session.DelaySeconds switch { 0 => 3, 3 => 5, 5 => 10, _ => 0 };
            UpdateTimerText();
        };
        UpdateTimerText();
        row.Children.Add(_timer);

        var repeat = new Button { ToolTip = "Letzten Bereich erneut aufnehmen", Width = 32, Height = 32 };
        repeat.SetResourceReference(StyleProperty, "IconButton");
        Ext.SetIcon(repeat, "refresh");
        Ext.SetHover(repeat, (Brush)Application.Current.FindResource("Teal"));
        repeat.IsEnabled = !string.IsNullOrEmpty(App.Settings.LastRegion);
        repeat.Click += (_, _) => RepeatLast?.Invoke();
        row.Children.Add(repeat);

        row.Children.Add(Separator());
        _hint.Text = Modes.First(x => x.Mode == session.Mode).Hint;
        _hint.Foreground = (Brush)Application.Current.FindResource("SubText");
        row.Children.Add(_hint);

        var close = new Button { ToolTip = "Abbrechen (Esc)", Width = 32, Height = 32 };
        close.SetResourceReference(StyleProperty, "IconButton");
        Ext.SetIcon(close, "xmark");
        Ext.SetHover(close, (Brush)Application.Current.FindResource("Danger"));
        close.Click += (_, _) => Cancelled?.Invoke();
        row.Children.Add(close);

        // glass: blurred screen copy + dark tint + specular rim
        var glass = new Grid();
        var blurHost = new Grid { ClipToBounds = true };
        _backdrop.Effect = new BlurEffect { Radius = 28, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
        _backdrop.HorizontalAlignment = HorizontalAlignment.Left;
        _backdrop.VerticalAlignment = VerticalAlignment.Top;
        blurHost.Children.Add(_backdrop);
        glass.Children.Add(blurHost);
        glass.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x1C, 0x1C, 0x1E)) });
        var sheen = new Border { Margin = new Thickness(1) };
        sheen.SetResourceReference(Border.BackgroundProperty, "GlassSheen");
        glass.Children.Add(sheen);
        var rim = new Border { BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(22) };
        rim.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
        glass.Children.Add(rim);
        glass.Children.Add(row);

        _shell = new Border { CornerRadius = new CornerRadius(22), Child = glass, Margin = new Thickness(18) };
        _shell.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0.45 };
        glass.SizeChanged += (_, _) => glass.Clip = new RectangleGeometry(new Rect(0, 0, glass.ActualWidth, glass.ActualHeight), 22, 22);
        Content = _shell;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | 0x08000000 /* NOACTIVATE */ | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) => PlaceAndPaint();
        _keepOnTop.Tick += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        };
        _keepOnTop.Start();
        Closed += (_, _) => _keepOnTop.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Cancelled?.Invoke(); };
    }

    private void UpdateTimerText() =>
        _timer.Content = _session.DelaySeconds == 0 ? "Aus" : $"{_session.DelaySeconds} s";

    private Border Separator() => new()
    {
        Width = 0.8, Margin = new Thickness(6, 7, 6, 7),
        Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))
    };

    private void PlaceAndPaint()
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(h, out var wr);
        int w = wr.Right - wr.Left, ht = wr.Bottom - wr.Top;
        var b = _screen.Bounds;
        int x = b.Left + (b.Width - w) / 2, y = b.Bottom - ht - 70;
        Native.SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        // paint the glass with a blurred copy of exactly what is behind the bar
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int pad = (int)(18 * dpi.DpiScaleX);
            var crop = new SD.Rectangle(x - b.Left + pad - 40, y - b.Top + pad - 40, w - 2 * pad + 80, ht - 2 * pad + 80);
            crop.Intersect(new SD.Rectangle(0, 0, _shot.Width, _shot.Height));
            using var part = _shot.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var src = ScreenshotService.ToBitmapSource(part);
            _backdrop.Source = src;
            _backdrop.Width = crop.Width / dpi.DpiScaleX;
            _backdrop.Height = crop.Height / dpi.DpiScaleY;
            _backdrop.Margin = new Thickness(-40 / dpi.DpiScaleX, -40 / dpi.DpiScaleY, 0, 0);
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
