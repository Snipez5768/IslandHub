using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using IslandHub.Core;

namespace IslandHub.UI;

public sealed record WidgetDef(string Kind, string Title, string Glyph, string TemplateKey, Func<object> Content);

public static class WidgetRegistry
{
    public static readonly WidgetDef[] All =
    {
        new("clock",   "Uhr",         "", "W_Clock",   () => App.Hub.Clock),
        new("world",   "Weltzeit",    "", "W_World",   () => App.Hub.Clock),
        new("weather", "Wetter",      "", "W_Weather", () => App.Hub.Weather),
        new("stats",   "PC-Werte",    "", "W_Stats",   () => App.Hub.Stats),
        new("battery", "Akku",        "", "W_Battery", () => App.Hub.Stats),
        new("timer",   "Fokus-Timer", "", "W_Timer",   () => App.Hub.Timer),
        new("todo",    "To-do",       "", "W_Todo",    () => App.Hub.Todos),
    };

    public static WidgetDef Get(string kind) => All.FirstOrDefault(w => w.Kind == kind);

    /// <summary>Builds an iOS "small" widget (150 × 150, continuous corners). The pin button only appears on hover.</summary>
    public static FrameworkElement BuildCard(WidgetDef d, bool desktop, Action pinAction)
    {
        // inside the flat island the cards use compact templates ("C_…"), on the desktop the full ones ("W_…")
        var card = new Border
        {
            Width = desktop ? 158 : 128,
            Height = desktop ? 158 : 81,
            CornerRadius = new CornerRadius(desktop ? 24 : 18),
            Padding = new Thickness(desktop ? 14 : 10),
            Margin = desktop ? new Thickness(12) : new Thickness(0, 0, 8, 0),
            ToolTip = d.Title
        };
        // Liquid Glass card: translucent fill + specular rim
        card.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
        card.BorderThickness = new Thickness(0.8);
        if (desktop)
        {
            card.Background = new LinearGradientBrush(Color.FromArgb(0xE6, 0x2C, 0x2C, 0x30), Color.FromArgb(0xEB, 0x16, 0x16, 0x1A), 90);
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Direction = 270 };
        }
        else card.SetResourceReference(Border.BackgroundProperty, "GlassFill");

        var grid = new Grid();
        var content = new ContentControl
        {
            Content = d.Content(),
            ContentTemplate = (DataTemplate)Application.Current.FindResource(desktop ? d.TemplateKey : "C_" + d.TemplateKey[2..]),
            Foreground = Brushes.White
        };
        content.SetResourceReference(TextElement.FontFamilyProperty, "TextFont");
        grid.Children.Add(content);

        var pin = new Button
        {
            ToolTip = desktop ? "Vom Desktop lösen" : "An den Desktop heften",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, -8, 0),
            Opacity = 0
        };
        pin.SetResourceReference(FrameworkElement.StyleProperty, "SmallIconButton");
        Ext.SetIcon(pin, desktop ? "xmark" : "pin");
        Ext.SetHover(pin, (Brush)Application.Current.FindResource(desktop ? "Danger" : "Orange"));
        pin.Click += (_, _) => pinAction?.Invoke();
        grid.Children.Add(pin);
        card.MouseEnter += (_, _) => pin.Opacity = 1;
        card.MouseLeave += (_, _) => pin.Opacity = 0;

        card.Child = grid;
        return card;
    }
}

/// <summary>A widget pinned to the desktop: borderless, stays behind normal windows, draggable.</summary>
public sealed class DesktopWidgetWindow : Window
{
    private static readonly List<DesktopWidgetWindow> Open = new();
    public string Kind { get; }
    private readonly System.Windows.Shapes.Rectangle _outline;
    private bool _dragging;
    private Point _grab;

    // ---------- arranging (user wish 2026-11-16): fixed by default, a mode to move them, a grid they stick to ----------

    /// <summary>One grid step = a widget (158) plus a 14 px gap – side by side they line up like on the iPhone.</summary>
    private const double Cell = 172, Edge = 16, CardInset = 12, CardSize = 158;

    public static bool Arranging { get; private set; }

    /// <summary>Turns the arrange mode on or off (from the settings, the tray, a widget's menu, or the island's "Fertig").</summary>
    public static void SetArranging(bool on)
    {
        if (Arranging == on) return;
        Arranging = on;
        foreach (var w in Open) w._outline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) App.Island.ShowModeHint("widgets", "Widgets anordnen", () => SetArranging(false));
        else { App.Island.EndModeHint(); SaveAll(); }
    }

    /// <summary>The card's top-left goes to the nearest free grid cell of the screen it is on (animated on release).</summary>
    private void SnapToGrid(bool animate)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var centre = new System.Drawing.Point((int)((Left + ActualWidth / 2) * dpi.DpiScaleX), (int)((Top + ActualHeight / 2) * dpi.DpiScaleY));
        var wa = System.Windows.Forms.Screen.FromPoint(centre).WorkingArea;
        double ax = wa.Left / dpi.DpiScaleX + Edge, ay = wa.Top / dpi.DpiScaleY + Edge;
        int maxCol = Math.Max(0, (int)((wa.Width / dpi.DpiScaleX - 2 * Edge - CardSize) / Cell));
        int maxRow = Math.Max(0, (int)((wa.Height / dpi.DpiScaleY - 2 * Edge - CardSize) / Cell));
        int col = Math.Clamp((int)Math.Round((Left + CardInset - ax) / Cell), 0, maxCol);
        int row = Math.Clamp((int)Math.Round((Top + CardInset - ay) / Cell), 0, maxRow);
        // a taken cell: the nearest free one (rings around it)
        bool Taken(int c, int r) => Open.Any(o => o != this && Math.Abs(o.Left + CardInset - (ax + c * Cell)) < 2 && Math.Abs(o.Top + CardInset - (ay + r * Cell)) < 2);
        if (Taken(col, row))
        {
            var best = (c: col, r: row, d: double.MaxValue);
            for (int c = 0; c <= maxCol; c++)
                for (int r = 0; r <= maxRow; r++)
                {
                    if (Taken(c, r)) continue;
                    double d = Math.Pow(c - col, 2) + Math.Pow(r - row, 2);
                    if (d < best.d) best = (c, r, d);
                }
            (col, row) = (best.c, best.r);
        }
        double left = ax + col * Cell - CardInset, top = ay + row * Cell - CardInset;
        if (!animate) { Left = left; Top = top; SaveAll(); return; }
        var ease = new System.Windows.Media.Animation.BackEase { Amplitude = 0.3, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);
        var ax2 = new System.Windows.Media.Animation.DoubleAnimation(Left, left, dur) { EasingFunction = ease };
        var ay2 = new System.Windows.Media.Animation.DoubleAnimation(Top, top, dur) { EasingFunction = ease };
        ax2.Completed += (_, _) => { BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null); Left = left; Top = top; SaveAll(); };
        BeginAnimation(LeftProperty, ax2);
        BeginAnimation(TopProperty, ay2);
    }

    private DesktopWidgetWindow(WidgetDef def, double left, double top)
    {
        Kind = def.Kind;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Foreground = Brushes.White;
        SetResourceReference(FontFamilyProperty, "TextFont");
        FontSize = 13;
        Title = "IslandHub – " + def.Title;
        // the card plus a dashed outline that only shows while arranging (2026-11-16)
        var root = new Grid();
        root.Children.Add(WidgetRegistry.BuildCard(def, true, Unpin));
        _outline = new System.Windows.Shapes.Rectangle
        {
            Margin = new Thickness(8), RadiusX = 27, RadiusY = 27, StrokeThickness = 1.6, IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 4, 3 }, Visibility = Arranging ? Visibility.Visible : Visibility.Collapsed
        };
        _outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Blue");
        root.Children.Add(_outline);
        Content = root;

        // fixed unless "Widgets anordnen" is on – then they follow the pointer and stick to the grid on release
        MouseLeftButtonDown += (_, e) =>
        {
            if (!Arranging || e.OriginalSource is TextBox) return;
            Native.GetCursorPos(out var p);
            var dpi = VisualTreeHelper.GetDpi(this);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            _grab = new Point(p.X / dpi.DpiScaleX - Left, p.Y / dpi.DpiScaleY - Top);
            _dragging = CaptureMouse();
            e.Handled = true;
        };
        MouseMove += (_, _) =>
        {
            if (!_dragging) return;
            Native.GetCursorPos(out var p);
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = p.X / dpi.DpiScaleX - _grab.X;
            Top = p.Y / dpi.DpiScaleY - _grab.Y;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            SnapToGrid(true);
            e.Handled = true;
        };
        var menu = new ContextMenu();
        var arrange = new MenuItem();
        arrange.Click += (_, _) => SetArranging(!Arranging);
        var unpin = new MenuItem { Header = "Vom Desktop lösen" };
        unpin.Click += (_, _) => Unpin();
        menu.Items.Add(arrange);
        menu.Items.Add(unpin);
        menu.Opened += (_, _) => arrange.Header = Arranging ? "Fertig (Widgets fixieren)" : "Widgets anordnen";
        ContextMenu = menu;
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
        Activated += (_, _) => SendToBack();
        Loaded += (_, _) => SendToBack();
    }

    private void SendToBack()
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(h, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void Unpin()
    {
        Open.Remove(this);
        Close();
        SaveAll();
    }

    public static bool IsPinned(string kind) => Open.Any(w => w.Kind == kind);

    public static void Unpin(string kind)
    {
        foreach (var w in Open.Where(w => w.Kind == kind).ToList()) w.Unpin();
    }

    public static void Pin(string kind)
    {
        var def = WidgetRegistry.Get(kind);
        if (def == null) return;
        var existing = Open.FirstOrDefault(w => w.Kind == kind);
        if (existing != null) { existing.Activate(); return; }
        var area = SystemParameters.WorkArea;
        double left = area.Right - 260 - Open.Count * 24, top = area.Top + 120 + Open.Count * 24;
        var w = new DesktopWidgetWindow(def, left, top);
        Open.Add(w);
        w.Show();
        w.SnapToGrid(false); // a new widget takes the nearest free cell
    }

    public static void RestoreAll()
    {
        var area = SystemParameters.VirtualScreenLeft;
        foreach (var s in App.Settings.DesktopWidgets.ToList())
        {
            var def = WidgetRegistry.Get(s.Kind);
            if (def == null || Open.Any(o => o.Kind == s.Kind)) continue;
            double left = Math.Clamp(s.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100);
            double top = Math.Clamp(s.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100);
            var w = new DesktopWidgetWindow(def, left, top);
            Open.Add(w);
            w.Show();
        }
        if (Open.Any(o => o.Kind == "stats")) App.Hub.Stats.WantGpu = true;
    }

    private static void SaveAll()
    {
        App.Settings.DesktopWidgets = Open.Select(o => new DesktopWidgetState { Kind = o.Kind, Left = o.Left, Top = o.Top }).ToList();
        App.SaveSettings();
    }
}
