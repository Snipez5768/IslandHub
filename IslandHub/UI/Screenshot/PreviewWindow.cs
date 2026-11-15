using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandHub.Core;
using IslandHub.Services;
using WF = System.Windows.Forms;

namespace IslandHub.UI;

/// <summary>
/// macOS-style floating thumbnail after a screenshot: slides in bottom-right, drag it into any app,
/// click to edit (incl. Live Text), hover for quick actions. Disappears after a few seconds.
/// </summary>
public sealed class PreviewWindow : Window
{
    private static PreviewWindow _current;
    private readonly ClipItem _item;
    private readonly DispatcherTimer _life = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly StackPanel _actions;
    private readonly TranslateTransform _slide = new(260, 0);
    private Point _down;
    private bool _pressed, _closing;

    public static void CloseCurrent() => _current?.Dismiss(false);

    public static void Show(ClipItem item, WF.Screen screen)
    {
        CloseCurrent();
        _current = new PreviewWindow(item, screen);
        _current.Show();
    }

    private PreviewWindow(ClipItem item, WF.Screen screen)
    {
        _item = item;
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

        var full = LoadImage(item.ImageFile);
        double maxW = 210, maxH = 150;
        double k = Math.Min(maxW / Math.Max(1, full.PixelWidth), maxH / Math.Max(1, full.PixelHeight));
        k = Math.Min(k, 1);
        double tw = Math.Max(90, full.PixelWidth * k), th = Math.Max(60, full.PixelHeight * k);

        var img = new Border
        {
            Width = tw, Height = th, CornerRadius = new CornerRadius(12),
            Background = new ImageBrush(full) { Stretch = Stretch.UniformToFill },
            Cursor = Cursors.Hand,
            ToolTip = "Klicken zum Bearbeiten · in eine App ziehen"
        };
        var rim = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(0.8), IsHitTestVisible = false };
        rim.SetResourceReference(Border.BorderBrushProperty, "GlassRim");

        _actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8), Opacity = 0 };
        _actions.Children.Add(Act("text.viewfinder", "Text erkennen", "Teal", () => { Dismiss(true); EditorWindow.Open(_item, true); }));
        _actions.Children.Add(Act("pencil", "Bearbeiten", "Yellow", () => { Dismiss(true); EditorWindow.Open(_item, false); }));
        _actions.Children.Add(Act("save", "Auf dem PC sichern", "Green", () => { App.Island.SaveImageItem(_item); Dismiss(true); }));
        _actions.Children.Add(Act("pin", "Oben anheften", "Orange", () => { PinWindow.Show(LoadImage(_item.ImageFile)); Dismiss(true); }));
        _actions.Children.Add(Act("trash", "Löschen", "Danger", () => { App.Hub.Clipboard.Remove(_item); Dismiss(true); }));

        var grid = new Grid();
        grid.Children.Add(img);
        grid.Children.Add(rim);
        grid.Children.Add(_actions);

        var qr = ScreenshotService.DecodeQr(full);
        if (qr != null)
        {
            var chip = new Button { Content = Uri.IsWellFormedUriString(qr, UriKind.Absolute) ? "QR öffnen" : "QR kopieren", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), ToolTip = qr };
            chip.SetResourceReference(StyleProperty, "AccentButton");
            Ext.SetIcon(chip, "qr");
            Ext.SetIconSize(chip, 12);
            chip.Padding = new Thickness(8, 3, 9, 3);
            chip.FontSize = 11;
            chip.Click += (_, _) =>
            {
                if (Uri.IsWellFormedUriString(qr, UriKind.Absolute)) { try { Process.Start(new ProcessStartInfo(qr) { UseShellExecute = true }); } catch { } }
                else App.Hub.Clipboard.AddText(qr, "QR-Code");
                Dismiss(true);
            };
            grid.Children.Add(chip);
        }

        var shell = new Border { Child = grid, Margin = new Thickness(20), RenderTransform = _slide };
        shell.Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.5 };
        Content = shell;

        img.MouseLeftButtonDown += (_, e) => { _down = e.GetPosition(this); _pressed = true; img.CaptureMouse(); };
        img.MouseMove += (_, e) =>
        {
            if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _down.X) < 6 && Math.Abs(p.Y - _down.Y) < 6) return;
            _pressed = false;
            img.ReleaseMouseCapture();
            DragOut();
        };
        img.MouseLeftButtonUp += (_, _) =>
        {
            img.ReleaseMouseCapture();
            if (!_pressed) return;
            _pressed = false;
            Dismiss(true);
            EditorWindow.Open(_item, false);
        };
        MouseEnter += (_, _) => { _life.Stop(); _actions.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))); };
        MouseLeave += (_, _) => { _life.Start(); _actions.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))); };
        _life.Tick += (_, _) => Dismiss(false);

        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.GetWindowRect(h, out var r);
            var wa = screen.WorkingArea;
            Native.SetWindowPos(h, new IntPtr(-1), wa.Right - (r.Right - r.Left) - 6, wa.Bottom - (r.Bottom - r.Top) - 6, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(260, 0, TimeSpan.FromMilliseconds(480))
            { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 7, EasingMode = EasingMode.EaseOut } });
            _life.Start();
        };
    }

    private static BitmapSource LoadImage(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private Button Act(string icon, string tip, string hoverKey, Action act)
    {
        var b = new Button { ToolTip = tip, Margin = new Thickness(2, 0, 2, 0), Width = 30, Height = 30 };
        b.SetResourceReference(StyleProperty, "GlassIcon");
        b.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1E));
        Ext.SetIcon(b, icon);
        Ext.SetIconSize(b, 14);
        Ext.SetHover(b, (Brush)Application.Current.FindResource(hoverKey));
        b.Click += (_, _) => act();
        return b;
    }

    private void DragOut()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "IslandHub");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"Screenshot_{_item.Time:yyyy-MM-dd_HH-mm-ss}.png");
            if (!File.Exists(file)) File.Copy(_item.ImageFile, file, true);
            var data = new DataObject(DataFormats.FileDrop, new[] { file });
            data.SetImage(LoadImage(_item.ImageFile));
            _life.Stop();
            Opacity = 0.6;
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (Exception ex) { App.Log(ex); }
        Dismiss(true);
    }

    private void Dismiss(bool fast)
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();
        if (_current == this) _current = null;
        var a = new DoubleAnimation(300, TimeSpan.FromMilliseconds(fast ? 160 : 320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        a.Completed += (_, _) => Close();
        _slide.BeginAnimation(TranslateTransform.XProperty, a);
    }
}

/// <summary>A screenshot pinned on top of everything (drag to move, mouse wheel to zoom, double-click to close).</summary>
public sealed class PinWindow : Window
{
    private readonly ScaleTransform _zoom = new(1, 1);

    public static void Show(BitmapSource img) => new PinWindow(img).Show();

    private PinWindow(BitmapSource img)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var area = SystemParameters.WorkArea;
        double k = Math.Min(1, Math.Min(area.Width * 0.6 / img.PixelWidth, area.Height * 0.6 / img.PixelHeight));
        var picture = new Border
        {
            Width = img.PixelWidth * k, Height = img.PixelHeight * k, CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(img), LayoutTransform = _zoom, ToolTip = "Ziehen · Mausrad = Zoom · Doppelklick = schließen"
        };
        var close = new Button { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), Opacity = 0 };
        close.SetResourceReference(StyleProperty, "GlassIcon");
        close.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E));
        close.Width = close.Height = 26;
        Ext.SetIcon(close, "xmark");
        Ext.SetIconSize(close, 12);
        Ext.SetHover(close, (Brush)Application.Current.FindResource("Danger"));
        close.Click += (_, _) => Close();

        var grid = new Grid { Margin = new Thickness(16) };
        grid.Children.Add(picture);
        grid.Children.Add(close);
        grid.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = 0.5 };
        Content = grid;

        MouseEnter += (_, _) => close.Opacity = 1;
        MouseLeave += (_, _) => close.Opacity = 0;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            try { DragMove(); } catch { }
        };
        MouseWheel += (_, e) =>
        {
            double z = Math.Clamp(_zoom.ScaleX * (e.Delta > 0 ? 1.1 : 0.9), 0.25, 3);
            _zoom.ScaleX = _zoom.ScaleY = z;
        };
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
    }
}
