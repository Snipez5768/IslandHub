using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using IslandHub.Core;

namespace IslandHub.UI;

// Settings redesign (approved mockup 2026-11-14): a clickable island preview, a small picture next to every
// option and a page with all animations to play. Everything here only draws – it never touches the real island,
// except the "Auf der Insel ansehen" links that ask the island to show a moment once.

internal static class Pv
{
    public static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static LinearGradientBrush Grad(params (string Hex, double Offset)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (h, o) in stops) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(h), o));
        g.Freeze();
        return g;
    }

    public static readonly Brush Wall = Grad(("#FF1D3557", 0), ("#FF4A3466", 0.5), ("#FF8A5A3C", 1));
    public static readonly Brush MiniWall = Grad(("#FF223049", 0), ("#FF3B2C49", 1));
    public static readonly Brush CoverA = Grad(("#FFFF9F0A", 0), ("#FFFF375F", 1));
    public static readonly Brush CoverB = Grad(("#FF64D2FF", 0), ("#FF30D158", 1));
    public static readonly Brush Shot = Grad(("#FF2A4A7A", 0), ("#FF6A3A6A", 1));
    public static readonly Brush Sub = B("#FF98989D");
    public static readonly Brush Dim = B("#FF636366");
    public static readonly Brush Tile = B("#FF3A3A3C");
    public static readonly Brush Glass = B("#FF2C2C2E");

    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static SfIcon Icon(string kind, double size, Brush fg = null) =>
        new() { Kind = kind, Size = size, Foreground = fg ?? Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock T(string text, double size = 12, Brush fg = null, bool bold = false) =>
        new() { Text = text, FontSize = size, Foreground = fg ?? Brushes.White, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };

    public static Border Box(double w, double h, Brush bg, double r) =>
        new() { Width = w, Height = h, Background = bg, CornerRadius = new CornerRadius(r) };

    public static T2 At<T2>(this T2 e, double l, double t, HorizontalAlignment ha = HorizontalAlignment.Left, VerticalAlignment va = VerticalAlignment.Top) where T2 : FrameworkElement
    {
        e.Margin = new Thickness(l, t, 0, 0);
        e.HorizontalAlignment = ha;
        e.VerticalAlignment = va;
        return e;
    }

    public static StackPanel Row(double gap, params UIElement[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is FrameworkElement f && i > 0) f.Margin = new Thickness(gap, f.Margin.Top, 0, 0);
            if (items[i] is FrameworkElement v) v.VerticalAlignment = VerticalAlignment.Center;
            s.Children.Add(items[i]);
        }
        return s;
    }

    public static Grid Stack(params UIElement[] items)
    {
        var g = new Grid();
        foreach (var i in items) g.Children.Add(i);
        return g;
    }
}

/// <summary>The small picture on the left of a settings row: shows what the option does.</summary>
public sealed class HintPic : Border
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(HintPic),
        new PropertyMetadata(null, (d, _) => ((HintPic)d).Build()));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public HintPic()
    {
        Width = 64; Height = 40;
        CornerRadius = new CornerRadius(7);
        Background = Pv.Tile;
        ClipToBounds = true;
        Margin = new Thickness(0, 0, 12, 0);
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        DockPanel.SetDock(this, Dock.Left);
    }

    private static Border Pill(double w = 30, double h = 9, double top = 6) =>
        new() { Width = w, Height = h, CornerRadius = new CornerRadius(h / 2), Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, top, 0, 0) };

    private void Build()
    {
        UIElement c;
        var white = Brushes.White;
        switch (Kind)
        {
            case "hover": c = Pv.Stack(Pill(), Pv.Icon("cursor", 14).At(34, 15)); break;
            case "delay": c = Pv.Stack(Pill(), Pv.Icon("clock", 14, Pv.Sub).At(25, 18)); break;
            case "smart": c = Pv.Row(3, Pv.Icon("music", 13, Pv.Res("Pink")), Pv.T("→", 11, Pv.Sub), Pv.T("Musik", 11)); break;
            case "trackpeek":
            {
                var p = Pill(48, 12, 6);
                p.Child = Pv.Row(3, Pv.Box(6, 6, Pv.CoverA, 2), Pv.Box(22, 3, white, 1.5));
                c = p;
                break;
            }
            case "autostart": c = Pv.Row(4, Pv.Icon("window", 15, Pv.B("#FF64D2FF")), Pv.T("→", 11, Pv.Sub), Pv.Box(20, 7, Brushes.Black, 3.5)); break;
            case "sound": c = Pv.Icon("bell.ring", 16, Pv.Res("Orange")); break;
            case "repeat": c = Pv.Icon("refresh", 16, Pv.Sub); break;
            case "stack":
            {
                var cv = new Canvas();
                var shades = new[] { "#FF636366", "#FF8E8E93", "#FFC7C7CC" };
                for (int i = 0; i < 3; i++)
                {
                    var b = Pv.Box(22, 14, Pv.B(shades[i]), 3);
                    b.Child = new TextBlock { Text = (3 - i).ToString(), FontSize = 9, Foreground = Pv.B("#FF1C1C1E"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    Canvas.SetLeft(b, 14 + i * 7); Canvas.SetTop(b, 17 - i * 5);
                    cv.Children.Add(b);
                }
                c = cv;
                break;
            }
            case "click": c = Pv.Icon("cursor", 16, Pv.Sub); break;
            case "confirm":
            {
                var p = Pill(46, 12, 6);
                p.Child = Pv.Row(2, Pv.Icon("check", 8, Pv.Res("Green")), new TextBlock { Text = "Kopiert", FontSize = 7, Foreground = white });
                c = p;
                break;
            }
            case "history": c = Pv.Icon("clock", 16, Pv.Sub); break;
            case "limit": c = Pv.Icon("checklist", 16, Pv.Sub); break;
            case "clear": c = Pv.Icon("trash", 16, Pv.Res("Danger")); break;
            case "folder": c = Pv.Icon("folder", 16, Pv.B("#FF64D2FF")); break;
            case "shotpreview":
            {
                var p = new Border { Width = 40, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
                p.Child = new Border { CornerRadius = new CornerRadius(4), Background = Pv.Shot, Margin = new Thickness(5) };
                c = p;
                break;
            }
            case "autosave": c = Pv.Icon("save", 16, Pv.Sub); break;
            case "glow":
            {
                var p = Pill(34, 12, 0);
                p.VerticalAlignment = VerticalAlignment.Center;
                p.Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x46, 0x6E), BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.9 };
                c = p;
                break;
            }
            case "tabhide":
            {
                var dots = new UIElement[4];
                for (int i = 0; i < 4; i++) dots[i] = new Ellipse { Width = 5, Height = 5, Fill = i == 1 ? white : Pv.Dim };
                c = Pv.Row(4, dots);
                break;
            }
            case "order":
            {
                var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                for (int i = 0; i < 3; i++) s.Children.Add(new Border { Width = 30, Height = 4, CornerRadius = new CornerRadius(2), Background = i == 1 ? white : Pv.Dim, Margin = new Thickness(i == 1 ? 6 : 0, 0, 0, 3) });
                c = s;
                break;
            }
            case "anim": c = Pv.Icon("sparkles", 18, Pv.Res("Pink")); break;
            case "micblue": c = Pv.Icon("mic", 16, Pv.Res("Blue")); break;
            case "privacy": c = Pv.Icon("shield", 16, Pv.Res("Green")); break;
            case "data": c = Pv.Icon("folder", 16, Pv.Sub); break;
            case "power": c = Pv.Icon("power", 16, Pv.Res("Danger")); break;
            case "keys": c = Pv.Icon("keyboard", 16, Pv.Sub); break;
            case "sun": c = Pv.Row(3, Pv.Icon("sun", 14, Pv.B("#FFFFD60A")), Pv.T("12°", 11)); break;
            case "globe": c = Pv.Icon("globe", 16, Pv.B("#FF64D2FF")); break;
            case "widgets": c = Pv.Icon("widgets", 16, Pv.Res("Green")); break;
            case "code":
            {
                var field = new Border { CornerRadius = new CornerRadius(4), Background = Pv.B("#FF1C1C1E"), Padding = new Thickness(5, 2, 5, 2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                field.Child = new TextBlock { Text = "4 8 1 9", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = white };
                c = Pv.Row(3, field, Pv.T("→", 10, Pv.Sub), Pv.Icon("envelope", 12, Pv.Res("Blue")));
                break;
            }
            case "mail": c = Pv.Icon("envelope", 17, Pv.Res("Blue")); break;
            case "level":
            {
                var p = Pill(52, 12, 6);
                var track = Pv.Box(26, 3, Pv.B("#55FFFFFF"), 1.5);
                track.Child = new Border { Width = 16, Height = 3, CornerRadius = new CornerRadius(1.5), Background = white, HorizontalAlignment = HorizontalAlignment.Left };
                p.Child = Pv.Row(3, Pv.Icon("speaker", 8, white), track);
                c = p;
                break;
            }
            case "headphones": c = Pv.Row(4, Pv.Icon("headphones", 15, white), new Ring { Width = 12, Height = 12, Thickness = 2, Value = 80, Stroke = Pv.Res("Green"), Track = Pv.B("#3330D158") }); break;
            case "charge": c = Pv.Row(4, Pv.Icon("bolt", 15, Pv.Res("Green")), new Ring { Width = 12, Height = 12, Thickness = 2, Value = 64, Stroke = Pv.Res("Green"), Track = Pv.B("#3330D158") }); break;
            case "update": c = Pv.Icon("download", 16, Pv.Res("Blue")); break;
            case "fs.bar" or "fs.hide" or "fs.norm":
            {
                Width = 100; Height = 56; Margin = new Thickness(0); Background = Pv.Wall; CornerRadius = new CornerRadius(6);
                if (Kind == "fs.bar")
                {
                    var bar = new Border { Width = 62, Height = 9, CornerRadius = new CornerRadius(0, 0, 4, 4), Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
                    bar.Child = Pv.Row(4, Pv.Box(10, 2, Pv.Res("Green"), 1), Pv.Box(10, 2, Pv.Res("Orange"), 1), Pv.Box(10, 2, Pv.B("#FF64D2FF"), 1));
                    c = bar;
                }
                else if (Kind == "fs.hide")
                    c = Pv.Stack(Pv.Icon("eye", 16, Pv.B("#99FFFFFF")), new Line { X1 = 41, Y1 = 19, X2 = 59, Y2 = 37, Stroke = Pv.B("#99FFFFFF"), StrokeThickness = 1.6, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round });
                else c = Pill(32, 9, 5);
                break;
            }
            default: c = null; break;
        }
        Child = c;
    }
}

/// <summary>Option tile for the island style: a little wallpaper with the island on it in that style.</summary>
internal static class StylePic
{
    public static Border Make(Brush bg, Brush rim) =>
        new()
        {
            Width = 92, Height = 54, CornerRadius = new CornerRadius(6), Background = Pv.Wall,
            Child = new Border { Width = 52, Height = 16, CornerRadius = new CornerRadius(8), Background = bg, BorderBrush = rim, BorderThickness = new Thickness(0.5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) }
        };
}

/// <summary>
/// The island on a wallpaper inside the settings – in the chosen form, style, tab bar and accent. Click through the
/// pages with the chips (or the tab symbols in the preview). Only a look, nothing in it works.
/// </summary>
public sealed class IslandPreview : Border
{
    private readonly Border _isl;
    private readonly Grid _body;
    private string _view = "music";
    private double _maxR = 26;
    private static AppSettings S => App.Settings;

    public event Action<string> ViewChanged;

    public static readonly (string Key, string Icon, string Name)[] Tabs =
    {
        ("music", "music", "Musik"), ("clip", "clipboard", "Zwischenablage"), ("live", "dot.radiowaves", "Live"), ("agent", "sparkles", "Agent"),
        ("games", "gamecontroller", "Spiele"), ("notify", "bell.badge", "Mitteilungen"), ("timer", "timer", "Timer")
    };

    public static readonly (string Key, string Name)[] Views =
    {
        ("closed", "Geschlossen"), ("banner", "Mitteilung kommt"), ("music", "Musik"), ("clip", "Zwischenablage"), ("live", "Live"),
        ("agent", "Agent"), ("games", "Spiele"), ("notify", "Mitteilungen"), ("timer", "Timer")
    };

    public IslandPreview()
    {
        Height = 214;
        CornerRadius = new CornerRadius(8);
        Background = Pv.Wall;
        ClipToBounds = true;
        _body = new Grid();
        _isl = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(0.5), Child = _body, Width = 330, Height = 122 };
        _isl.SetResourceReference(BackgroundProperty, "IslandBackground");
        _isl.SetResourceReference(BorderBrushProperty, "IslandBorder");
        _isl.SizeChanged += (_, _) => UpdateShape();
        Child = new Grid { Children = { _isl } };
        Loaded += (_, _) => Refresh(true);
    }

    public string View
    {
        get => _view;
        set { if (_view == value) return; _view = value; Refresh(); ViewChanged?.Invoke(value); }
    }

    private void UpdateShape()
    {
        double w = _isl.ActualWidth, h = _isl.ActualHeight, r = Math.Min(h / 2, _maxR);
        bool notch = S.Shape == IslandShape.Notch;
        _isl.CornerRadius = notch ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);
        _body.Clip = new RectangleGeometry(new Rect(0, 0, w, h), notch ? 0 : r, notch ? 0 : r);
    }

    private static (double W, double H) SizeOf(string v) => v switch
    {
        "closed" => (150, 28), "banner" => (300, 70), "clip" => (330, 184), "live" or "agent" => (330, 116),
        "games" => (330, 112), "notify" => (330, 140), "timer" => (330, 124), _ => (330, 122)
    };

    public void Refresh(bool instant = false)
    {
        var (w, h) = SizeOf(_view);
        _maxR = _view == "closed" ? 14 : _view == "banner" ? 24 : 26;
        _isl.Margin = new Thickness(0, S.Shape == IslandShape.Notch ? 0 : 8, 0, 0);
        if (instant)
        {
            _isl.BeginAnimation(WidthProperty, null); _isl.BeginAnimation(HeightProperty, null);
            _isl.Width = w; _isl.Height = h;
        }
        else
        {
            var ease = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut };
            _isl.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(480)) { EasingFunction = ease });
            _isl.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(480)) { EasingFunction = ease });
        }
        UpdateShape();
        // the new content appears softly while the shape changes
        var content = Build(_view);
        if (instant) { _body.Children.Clear(); _body.Children.Add(content); return; }
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fadeOut.Completed += (_, _) =>
        {
            _body.Children.Clear();
            _body.Children.Add(content);
            _body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(60) });
        };
        _body.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ---------- content ----------

    private UIElement Build(string v)
    {
        if (v == "closed") return Closed();
        if (v == "banner") return Banner();
        var dock = new DockPanel { LastChildFill = true };
        var bar = TabBar(v);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        var body = v switch
        {
            "clip" => Clip(), "live" => Live(), "agent" => Agent(), "games" => Games(), "notify" => Notify(), "timer" => Timer(), _ => Music()
        };
        if (body is FrameworkElement f) f.Margin = new Thickness(16, 4, 16, 0);
        dock.Children.Add(body);
        return dock;
    }

    // only made-up content in the settings (user wish 2026-11-15): no song, game or contact of the user's own
    private static Brush CoverBrush() => Pv.CoverA;

    private static UIElement Bars(double h)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Height = h, VerticalAlignment = VerticalAlignment.Center };
        double[] f = { 0.55, 1, 0.7, 0.85, 0.45 };
        for (int i = 0; i < f.Length; i++)
        {
            var sc = new ScaleTransform(1, f[i]);
            var bar = new Rectangle { Width = 3, Height = h, RadiusX = 1, RadiusY = 1, Fill = Brushes.White, Margin = new Thickness(0, 0, i < f.Length - 1 ? 2 : 0, 0), RenderTransformOrigin = new Point(0.5, 1), RenderTransform = sc };
            s.Children.Add(bar);
            Calm.Loop(bar, sc, ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(380 + i * 70)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(-i * 120), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        return s;
    }

    private UIElement Closed()
    {
        var g = new Grid { Margin = new Thickness(5, 0, 12, 0) };
        g.Children.Add(Pv.Box(18, 18, CoverBrush(), 5).At(0, 0, HorizontalAlignment.Left, VerticalAlignment.Center));
        var b = Bars(13);
        ((FrameworkElement)b).HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(b);
        return g;
    }

    private UIElement Banner()
    {
        var avatar = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = Pv.Res("Green"), Child = Pv.T("A", 16, Pv.B("#FF04342C"), true) };
        ((TextBlock)avatar.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)avatar.Child).VerticalAlignment = VerticalAlignment.Center;
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Pv.T("WhatsApp · jetzt", 11, Pv.Sub));
        text.Children.Add(Pv.T("Anna", 13, null, true));
        text.Children.Add(Pv.T("Kommst du heute noch vorbei?", 12, Pv.B("#FFC7C7CC")));
        var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(avatar);
        s.Children.Add(text);
        return s;
    }

    private FrameworkElement TabIcon(string key, string icon, string active, double size)
    {
        var i = Pv.Icon(icon, size, key == active ? Brushes.White : Pv.Dim);
        var hit = new Border { Background = Brushes.Transparent, Child = i, Cursor = Cursors.Hand, Padding = new Thickness(1) };
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; View = key; };
        return hit;
    }

    private UIElement TabBar(string v)
    {
        var active = Tabs.First(t => t.Key == v);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 4), Height = 18 };
        if (S.TabBarAutoHide)
        {
            foreach (var t in Tabs)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = t.Key == v ? Brushes.White : Pv.B("#FF48484A"), Margin = new Thickness(2.5, 0, 2.5, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
                var key = t.Key;
                dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; View = key; };
                row.Children.Add(dot);
            }
            return row;
        }
        switch (S.TabBar)
        {
            case TabBarStyle.Titel:
            {
                var d = new DockPanel { Margin = new Thickness(16, 8, 16, 2), Height = 20 };
                var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var t in Tabs) { var e = TabIcon(t.Key, t.Icon, v, 11); e.Margin = new Thickness(4, 0, 0, 0); icons.Children.Add(e); }
                DockPanel.SetDock(icons, Dock.Right);
                d.Children.Add(icons);
                var title = Pv.T(active.Name, 14, null, true);
                title.VerticalAlignment = VerticalAlignment.Center;
                d.Children.Add(title);
                return d;
            }
            case TabBarStyle.Gruppen:
            {
                string[][] groups = { new[] { "music", "live", "games" }, new[] { "clip", "agent", "timer" }, new[] { "notify" } };
                for (int gi = 0; gi < groups.Length; gi++)
                {
                    if (gi > 0) row.Children.Add(new Rectangle { Width = 1, Height = 12, Fill = Pv.Tile, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                    foreach (var k in groups[gi])
                    {
                        var t = Tabs.First(x => x.Key == k);
                        var col = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
                        col.Children.Add(TabIcon(t.Key, t.Icon, v, 12));
                        col.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = k == v ? Brushes.White : Brushes.Transparent, Margin = new Thickness(0, 1, 0, 0) });
                        row.Children.Add(col);
                    }
                }
                return row;
            }
            default:
            {
                foreach (var t in Tabs)
                {
                    if (t.Key == v)
                    {
                        var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 5, 0) };
                        s.Children.Add(Pv.Icon(t.Icon, 12));
                        var n = Pv.T(t.Name, 11);
                        n.Margin = new Thickness(4, 0, 0, 0);
                        n.VerticalAlignment = VerticalAlignment.Center;
                        s.Children.Add(n);
                        row.Children.Add(s);
                    }
                    else { var e = TabIcon(t.Key, t.Icon, v, 12); e.Margin = new Thickness(5, 0, 5, 0); row.Children.Add(e); }
                }
                row.Children.Add(new Rectangle { Width = 1, Height = 12, Fill = Pv.Tile, Margin = new Thickness(5, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(Pv.Icon("gear", 12, Pv.Dim));
                return row;
            }
        }
    }

    private static UIElement Music()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(Pv.Box(62, 62, CoverBrush(), 10).At(0, 0, HorizontalAlignment.Left, VerticalAlignment.Center));
        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(info, 1);
        info.Children.Add(Pv.T("Nachtlicht", 13, null, true));
        info.Children.Add(Pv.T("Lena Ost · Spotify", 11, Pv.Sub));
        var track = new Grid { Height = 3, Margin = new Thickness(0, 8, 0, 7) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Pv.Tile });
        var fill = new Border { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 80 };
        fill.SetResourceReference(BackgroundProperty, "Accent");
        track.Children.Add(fill);
        info.Children.Add(track);
        info.Children.Add(Pv.Row(18, Pv.Icon("backward.fill", 13), Pv.Icon("pause.fill", 15), Pv.Icon("forward.fill", 13)));
        g.Children.Add(info);
        return g;
    }

    private static UIElement Clip()
    {
        var s = new StackPanel();
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new[] { "#FF4A6FA5", "#FF8A5A3C", "#FF5A7A4A", "#FF6A3A6A", "#FF3A4A5A" })
            strip.Children.Add(new Border { Width = 50, Height = 34, CornerRadius = new CornerRadius(6), Background = Pv.B(c), Margin = new Thickness(0, 0, 6, 0) });
        s.Children.Add(strip);
        var day = Pv.T("Heute", 11, Pv.Sub);
        day.Margin = new Thickness(0, 8, 0, 2);
        s.Children.Add(day);
        foreach (var (a, b, sel) in new[] { ("Treffen um 18 Uhr am Bahnhof", "Text · eben", true), ("Bericht_final.pdf", "Datei · 10:42", false) })
        {
            var r = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var mark = new Border { Width = 3, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
            if (sel) mark.SetResourceReference(BackgroundProperty, "Accent");
            r.Children.Add(mark);
            var t = new StackPanel();
            t.Children.Add(Pv.T(a, 12));
            t.Children.Add(Pv.T(b, 11, Pv.Sub));
            r.Children.Add(t);
            s.Children.Add(r);
        }
        return s;
    }

    private static Border Round(string icon, Brush bg) =>
        new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = bg, Margin = new Thickness(6, 0, 0, 0), Child = Pv.Icon(icon, 14) };

    private static UIElement Live()
    {
        var d = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(Round("mic", Pv.Glass));
        btns.Children.Add(Round("open", Pv.Glass));
        btns.Children.Add(Round("phone", Pv.Res("Danger")));
        DockPanel.SetDock(btns, Dock.Right);
        d.Children.Add(btns);
        var ic = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Pv.B("#FF5865F2"), Child = Pv.Icon("mic", 20) };
        DockPanel.SetDock(ic, Dock.Left);
        d.Children.Add(ic);
        var t = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Pv.T("Discord · Sprachkanal", 11, Pv.Sub));
        t.Children.Add(Pv.T("Im Anruf", 13, null, true));
        t.Children.Add(Pv.T("12:04", 11, Pv.Res("Green")));
        d.Children.Add(t);
        return d;
    }

    private static UIElement Agent()
    {
        var d = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var open = new Border { CornerRadius = new CornerRadius(12), Background = Pv.B("#FF48484A"), Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center, Child = Pv.T("Öffnen", 12) };
        DockPanel.SetDock(open, Dock.Right);
        d.Children.Add(open);
        var ring = Pv.Stack(new Ring { Width = 46, Height = 46, Value = 60, Thickness = 3, Stroke = Pv.Res("Orange") }, Pv.Icon("asterisk", 18, Pv.Res("Orange")));
        DockPanel.SetDock(ring, Dock.Left);
        d.Children.Add(ring);
        var t = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Pv.T("Claude Code · dynamic island", 11, Pv.Sub));
        t.Children.Add(Pv.T("Baut die Einstellungen", 13, null, true));
        t.Children.Add(Pv.T("Schritt 3 von 5", 11, Pv.Sub));
        d.Children.Add(t);
        return d;
    }

    private static UIElement Games()
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var sample = new[] { ("#FF3A6EA5", "Truck Sim"), ("#FFA5503A", "Racer"), ("#FF4A8A5A", "Farm"), ("#FF6A4A8A", "Space") };
        for (int i = 0; i < sample.Length; i++)
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 10, 0), Width = 50 };
            col.Children.Add(new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(10), Background = Pv.B(sample[i].Item1) });
            var name = Pv.T(sample[i].Item2, 11, Pv.B("#FFC7C7CC"));
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(name);
            s.Children.Add(col);
        }
        return s;
    }

    private static UIElement Notify()
    {
        var s = new StackPanel();
        var f = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var right = Pv.T("Alle gelesen", 11, Pv.Sub);
        DockPanel.SetDock(right, Dock.Right);
        f.Children.Add(right);
        f.Children.Add(Pv.Row(12, Pv.T("Alle", 11), Pv.T("Ungelesen", 11, Pv.Sub)));
        ((FrameworkElement)f.Children[1]).HorizontalAlignment = HorizontalAlignment.Left;
        s.Children.Add(f);
        foreach (var (a, b, c, unread) in new[] { ("WhatsApp · Anna", "Kommst du heute noch vorbei?", "#FF30D158", true), ("Steam", "Download fertig · 12,4 GB", "#FF3A6EA5", false) })
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            r.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = unread ? Pv.Res("Blue") : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center });
            r.Children.Add(new Ellipse { Width = 26, Height = 26, Fill = Pv.B(c), Margin = new Thickness(8, 0, 8, 0) });
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(Pv.T(a, 12, null, unread));
            t.Children.Add(Pv.T(b, 11, Pv.Sub));
            r.Children.Add(t);
            s.Children.Add(r);
        }
        return s;
    }

    private static UIElement Timer()
    {
        var d = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        right.Children.Add(new Border { CornerRadius = new CornerRadius(15), Background = Pv.Res("Green"), Padding = new Thickness(22, 6, 22, 6), Child = Pv.T("Start", 13, Pv.B("#FF04342C"), true) });
        var quick = Pv.T("5 · 10 · 25 Min", 11, Pv.Sub);
        quick.HorizontalAlignment = HorizontalAlignment.Center;
        quick.Margin = new Thickness(0, 6, 0, 0);
        right.Children.Add(quick);
        DockPanel.SetDock(right, Dock.Right);
        d.Children.Add(right);
        var wheels = new Grid();
        wheels.Children.Add(new Border { Height = 26, CornerRadius = new CornerRadius(7), Background = Pv.Glass, VerticalAlignment = VerticalAlignment.Center });
        var cols = new UniformGrid { Columns = 3 };
        foreach (var (a, b, c, u) in new[] { ("", "0", "1", "h"), ("24", "25", "26", "min"), ("59", "00", "01", "s") })
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var top = Pv.T(a, 12, Pv.B("#FF48484A")); top.HorizontalAlignment = HorizontalAlignment.Center; top.Height = 22;
            var mid = new TextBlock { FontSize = 17, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Height = 26 };
            mid.Inlines.Add(new System.Windows.Documents.Run(b));
            mid.Inlines.Add(new System.Windows.Documents.Run(" " + u) { FontSize = 11, Foreground = Pv.Sub });
            var bot = Pv.T(c, 12, Pv.B("#FF48484A")); bot.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(top); col.Children.Add(mid); col.Children.Add(bot);
            cols.Children.Add(col);
        }
        wheels.Children.Add(cols);
        d.Children.Add(wheels);
        return d;
    }
}

/// <summary>
/// "Animationen" page: every animation as a small card that plays a copy of it (click), plus – where it is safe –
/// "Auf der Insel ansehen", which lets the real island show the moment once.
/// </summary>
public static class AnimGallery
{
    private sealed record Def(string Key, string Title, string Desc, string Level, bool Fx, bool Demo, Action<Stage> Build);

    /// <summary>always = runs at every level, an = only with "An", small = An + Reduziert (Fx: light / rings only with An).</summary>
    private static readonly Def[] All =
    {
        new("spring", "Öffnen und schließen", "Feder mit leichtem Nachschwingen", "always", false, true, Spring),
        new("page", "Seite wechseln", "Inhalt wird weich und wieder scharf", "always", false, false, Page),
        new("birth", "Blase tropft heraus", "Erst klein in der Insel, dann als Tropfen", "an", false, false, Birth),
        new("lift", "Ziehen", "Kachel hebt sich und sinkt zurück", "small", false, false, Lift),
        new("bell", "Mitteilung", "Glocke schwingt, dann kommt der Banner", "small", false, true, Bell),
        new("copy", "Kopiert", "Insel gibt kurz nach, Haken gleitet rein", "small", false, true, Copy),
        new("song", "Neuer Song", "Altes Cover gleitet raus, neues rein", "small", false, true, Song),
        new("mute", "Mikro stumm", "Dunkelrot, Blase zuckt kurz", "small", false, true, Mute),
        new("shot", "Bildschirmfoto", "Insel zeigt das Bild und nimmt es auf", "small", false, true, Shot),
        new("dl", "Download fertig", "Licht schießt in den Ring", "small", true, true, s => Done(s, "download", Pv.Res("Blue"), 80, Colors.White)),
        new("claude", "Claude fertig", "Grünes Licht, Ring schließt sich", "small", true, true, s => Done(s, "asterisk", Pv.Res("Orange"), 60, Color.FromRgb(0x30, 0xD1, 0x58))),
        new("timer", "Timer abgelaufen", "Zwei Ringe und ein Wackeln", "small", true, true, Timer),
    };

    private static AppSettings S => App.Settings;
    private static readonly List<(Border Card, Def Def, Stage Stage, TextBlock Note)> Cards = new();

    private static bool Disabled(Def d) => (S.Animations == AnimationLevel.Aus && d.Level != "always") || (S.Animations != AnimationLevel.An && d.Level == "an");

    private static string NoteOf(Def d)
    {
        if (S.Animations == AnimationLevel.An) return "";
        if (d.Level == "always") return "Läuft immer";
        if (S.Animations == AnimationLevel.Aus) return "Aus: passiert ohne Bewegung";
        if (d.Level == "an") return "Blase erscheint direkt, ohne Tropfen";
        return d.Fx ? "Reduziert: ohne Licht und Ringe" : "Auch bei Reduziert";
    }

    public static void Build(Panel moves, Panel moments)
    {
        Cards.Clear();
        foreach (var d in All)
        {
            var stage = new Stage();
            var title = Pv.T(d.Title, 12.5, null, true);
            var desc = new TextBlock { Text = d.Desc, FontSize = 11.5, Foreground = Pv.Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            var play = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Pv.B("#FF48484A"), Child = Pv.Icon("play.fill", 10), VerticalAlignment = VerticalAlignment.Center };
            var head = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
            DockPanel.SetDock(play, Dock.Right);
            head.Children.Add(play);
            var words = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            words.Children.Add(title);
            words.Children.Add(desc);
            head.Children.Add(words);
            var note = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            var body = new StackPanel();
            body.Children.Add(stage.Root);
            body.Children.Add(head);
            body.Children.Add(note);
            if (d.Demo)
            {
                var link = new TextBlock { Text = "Auf der Insel ansehen", FontSize = 11.5, Margin = new Thickness(0, 5, 0, 0), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
                link.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                var key = d.Key;
                link.MouseLeftButtonUp += (_, e) => { e.Handled = true; App.Island?.DemoMoment(key); };
                body.Children.Add(link);
            }
            var card = new Border { Background = Pv.Glass, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Child = body, BorderBrush = Pv.B("#12FFFFFF"), BorderThickness = new Thickness(0.5) };
            var def = d;
            card.MouseLeftButtonUp += (_, _) => Play(def, stage);
            (d.Key is "spring" or "page" or "birth" or "lift" ? moves : moments).Children.Add(card);
            Cards.Add((card, d, stage, note));
        }
        Refresh();
    }

    /// <summary>After the level changed: dim what does not run, write what changes, show the still pictures.</summary>
    public static void Refresh()
    {
        foreach (var (card, d, stage, note) in Cards)
        {
            card.Opacity = Disabled(d) ? 0.5 : 1;
            note.Text = NoteOf(d);
            note.Foreground = Disabled(d) ? Pv.Res("Orange") : Pv.Sub;
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            stage.Reset(false, false);
            d.Build(stage);
        }
    }

    private static void Play(Def d, Stage stage)
    {
        if (Disabled(d)) return;
        stage.Reset(true, S.Animations == AnimationLevel.An);
        d.Build(stage);
    }

    public static void PlayAll()
    {
        int i = 0;
        foreach (var (_, d, stage, _) in Cards)
        {
            if (Disabled(d)) continue;
            var (dd, st) = (d, stage);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1 + i++ * 450) };
            t.Tick += (_, _) => { t.Stop(); Play(dd, st); };
            t.Start();
        }
    }

    // ================= the stage: a small wallpaper with a zero-width canvas in its middle =================

    public sealed class Stage
    {
        public readonly Border Root;
        public readonly Canvas O;
        public readonly TranslateTransform Shift = new();
        public bool Play, Fx;

        public Stage()
        {
            O = new Canvas { Width = 0, Height = 96, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, RenderTransform = Shift };
            Root = new Border { Height = 96, CornerRadius = new CornerRadius(7), Background = Pv.MiniWall, ClipToBounds = true, Child = new Grid { Children = { O } } };
        }

        public void Reset(bool play, bool fx)
        {
            Play = play; Fx = fx;
            Shift.BeginAnimation(TranslateTransform.XProperty, null);
            Shift.X = 0;
            O.Children.Clear();
        }

        /// <summary>Starts an animation only when playing – the still picture shows the start state.</summary>
        public void A(IAnimatable target, DependencyProperty p, AnimationTimeline a)
        {
            if (Play) target.BeginAnimation(p, a);
        }

        public T Add<T>(T e, double left, double top) where T : UIElement
        {
            Canvas.SetLeft(e, left); Canvas.SetTop(e, top);
            O.Children.Add(e);
            return e;
        }
    }

    /// <summary>The mini island: keeps its middle (or its left edge) while its size animates, rounds itself.</summary>
    private sealed class Mini : Border
    {
        public double CenterX;
        public double? LeftX;
        public double MaxR = 11;
        public readonly Grid Inner = new();

        public Mini(double w, double h, double cx)
        {
            Width = w; Height = h; CenterX = cx;
            Background = Brushes.Black;
            Child = Inner;
            SizeChanged += (_, _) => Place();
            Loaded += (_, _) => Place();
        }

        public void Place()
        {
            double w = double.IsNaN(ActualWidth) || ActualWidth == 0 ? Width : ActualWidth, h = ActualHeight == 0 ? Height : ActualHeight;
            Canvas.SetLeft(this, LeftX ?? CenterX - w / 2);
            double r = Math.Min(h / 2, MaxR);
            CornerRadius = new CornerRadius(r);
            Inner.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        }
    }

    // ================= little helpers =================

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));
    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction InOut = new SineEase { EasingMode = EasingMode.EaseInOut };
    private static readonly IEasingFunction Over = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };

    /// <summary>Key frames at absolute times (ms); each frame eases into its value.</summary>
    private static DoubleAnimationUsingKeyFrames K(params (double V, int Ms, IEasingFunction E)[] f)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        foreach (var (v, ms, e) in f) a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), e));
        return a;
    }

    private static DoubleAnimation D(double from, double to, int ms, int delay = 0, IEasingFunction e = null) =>
        new(from, to, Ms(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = e };

    private static Mini Pill(Stage s, double cx = 0, bool bars = true)
    {
        var p = new Mini(96, 22, cx);
        s.Add(p, cx - 48, 8);
        p.Inner.Children.Add(Pv.Box(12, 12, Pv.CoverA, 3).At(5, 5));
        if (bars) p.Inner.Children.Add(MiniBars().At(72, 6));
        return p;
    }

    private static StackPanel MiniBars()
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Height = 10 };
        double[] f = { 1, 0.6, 0.85, 0.5 };
        for (int i = 0; i < 4; i++)
        {
            var sc = new ScaleTransform(1, f[i]);
            var bar = new Rectangle { Width = 2, Height = 10, RadiusX = 1, RadiusY = 1, Fill = Brushes.White, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom, RenderTransformOrigin = new Point(0.5, 1), RenderTransform = sc };
            s.Children.Add(bar);
            Calm.Loop(bar, sc, ScaleTransform.ScaleYProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(330 + i * 60)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = InOut });
        }
        return s;
    }

    private static Border Bubble(Stage s, double cx, UIElement content)
    {
        var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brushes.Black, Child = content, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        return s.Add(b, cx - 11, 8);
    }

    private static void Size(Stage s, Mini p, params (double W, double H, int Ms, IEasingFunction E)[] f)
    {
        s.A(p, FrameworkElement.WidthProperty, K(f.Select(x => (x.W, x.Ms, x.E)).ToArray()));
        s.A(p, FrameworkElement.HeightProperty, K(f.Select(x => (x.H, x.Ms, x.E)).ToArray()));
    }

    private static void Fade(Stage s, UIElement e, params (double V, int Ms)[] f) =>
        s.A(e, UIElement.OpacityProperty, K(f.Select(x => (x.V, x.Ms, (IEasingFunction)InOut)).ToArray()));

    // ================= the animations =================

    private static void Spring(Stage s)
    {
        var p = Pill(s);
        p.MaxR = 24;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 }.At(14, 12);
        content.Children.Add(Pv.Box(40, 40, Pv.CoverA, 8));
        var t = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 140 };
        t.Children.Add(Pv.T("Nachtlicht", 11, null, true));
        t.Children.Add(Pv.T("Lena Ost", 11, Pv.Sub));
        t.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Pv.Tile, Margin = new Thickness(0, 5, 0, 0), Child = new Border { Width = 56, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.White, CornerRadius = new CornerRadius(1.5) } });
        content.Children.Add(t);
        p.Inner.Children.Add(content);
        var over = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        Size(s, p, (96, 22, 0, null), (222, 66, 520, over), (222, 66, 1250, null), (96, 22, 1750, new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }));
        foreach (UIElement e in p.Inner.Children)
            if (e != content) Fade(s, e, (1, 0), (0, 120), (0, 1500), (1, 1750));
        Fade(s, content, (0, 0), (0, 300), (1, 500), (1, 1150), (0, 1280));
    }

    private static void Page(Stage s)
    {
        var p = new Mini(210, 64, 0) { MaxR = 22 };
        s.Add(p, -105, 8);
        var c1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        c1.Children.Add(Pv.Box(38, 38, Pv.CoverA, 8));
        var t = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Pv.T("Nachtlicht", 11, null, true));
        t.Children.Add(Pv.T("Lena Ost", 11, Pv.Sub));
        c1.Children.Add(t);
        var c2 = new DockPanel { Margin = new Thickness(16, 0, 16, 0), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.9, 0.9) };
        var start = new Border { CornerRadius = new CornerRadius(12), Background = Pv.Res("Green"), Padding = new Thickness(14, 4, 14, 4), VerticalAlignment = VerticalAlignment.Center, Child = Pv.T("Start", 11, Pv.B("#FF04342C"), true) };
        DockPanel.SetDock(start, Dock.Right);
        c2.Children.Add(start);
        var time = Pv.T("25:00", 20, null, true);
        time.VerticalAlignment = VerticalAlignment.Center;
        c2.Children.Add(time);
        p.Inner.Children.Add(c1);
        p.Inner.Children.Add(c2);
        var blur1 = new BlurEffect { Radius = 0 };
        var blur2 = new BlurEffect { Radius = 6 };
        c1.Effect = blur1;
        c2.Effect = blur2;
        s.A(c1, UIElement.OpacityProperty, D(1, 0, 300, 250));
        s.A(blur1, BlurEffect.RadiusProperty, D(0, 6, 300, 250));
        var sc1 = (ScaleTransform)c1.RenderTransform;
        s.A(sc1, ScaleTransform.ScaleXProperty, D(1, 0.92, 300, 250));
        s.A(sc1, ScaleTransform.ScaleYProperty, D(1, 0.92, 300, 250));
        s.A(c2, UIElement.OpacityProperty, D(0, 1, 420, 380));
        s.A(blur2, BlurEffect.RadiusProperty, D(6, 0, 420, 380, Out));
        var sc2 = (ScaleTransform)c2.RenderTransform;
        s.A(sc2, ScaleTransform.ScaleXProperty, D(0.9, 1, 480, 380, Over));
        s.A(sc2, ScaleTransform.ScaleYProperty, D(0.9, 1, 480, 380, Over));
    }

    private static void Birth(Stage s)
    {
        // the pill grows on its right side while the symbol shows small inside, then a drop with a neck comes out;
        // "Mitte halten": the whole group slides left so it stays centred
        var p = Pill(s);
        p.LeftX = -48;
        var star = Pv.Icon("asterisk", 13, Pv.Res("Orange"));
        var inside = new Border { Child = star, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.3, 0.3) }.At(91, 4);
        p.Inner.Children.Add(inside);
        var neck = new Border { Width = 0, Height = 12, CornerRadius = new CornerRadius(6), Background = Brushes.Black };
        s.Add(neck, 44, 13);
        var drop = Bubble(s, 51, Pv.Icon("asterisk", 13, Pv.Res("Orange")));
        drop.Opacity = 0;
        Canvas.SetLeft(drop, 40);
        var sc = (ScaleTransform)drop.RenderTransform;
        s.A(p, FrameworkElement.WidthProperty, K((96, 0, null), (110, 320, Out), (96, 800, InOut)));
        var isc = (ScaleTransform)inside.RenderTransform;
        s.A(isc, ScaleTransform.ScaleXProperty, K((0.3, 0, null), (0.75, 280, Over)));
        s.A(isc, ScaleTransform.ScaleYProperty, K((0.3, 0, null), (0.75, 280, Over)));
        Fade(s, inside, (0, 0), (1, 270), (1, 340), (0, 390));
        Fade(s, drop, (0, 0), (0, 340), (1, 370));
        s.A(drop, Canvas.LeftProperty, K((40, 0, null), (40, 360, null), (60, 720, Out), (54, 1150, InOut)));
        s.A(sc, ScaleTransform.ScaleXProperty, K((0.8, 0, null), (0.8, 360, null), (1.05, 720, Out), (1, 1150, InOut)));
        s.A(sc, ScaleTransform.ScaleYProperty, K((0.8, 0, null), (0.8, 360, null), (1.05, 720, Out), (1, 1150, InOut)));
        s.A(neck, FrameworkElement.WidthProperty, K((0, 0, null), (0, 370, null), (16, 630, Out), (12, 720, null), (0, 760, null)));
        s.A(neck, FrameworkElement.HeightProperty, K((12, 0, null), (12, 370, null), (9, 630, null), (5, 720, null), (4, 760, null)));
        s.A(neck, Canvas.TopProperty, K((13, 0, null), (13, 370, null), (14.5, 630, null), (16.5, 720, null), (17, 760, null)));
        s.A(s.Shift, TranslateTransform.XProperty, K((0, 0, null), (0, 340, null), (-14, 1150, Out)));
    }

    private static void Lift(Stage s)
    {
        var p = new Mini(200, 52, 0) { MaxR = 18 };
        s.Add(p, -100, 8);
        var tiles = new[] { "#FFFF9F0A", "#FF64D2FF", "#FFBF5AF2", "#FF30D158" }
            .Select((c, i) => new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Pv.B(c), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } } }.At(16 + i * 44, 10))
            .ToArray();
        // the lifted tile sits above the island's clip, so it is drawn on the canvas instead of inside
        foreach (var (t, i) in tiles.Select((t, i) => (t, i)))
        {
            if (i == 1) { t.Margin = new Thickness(0); s.Add(t, -100 + 16 + 44, 18); Panel.SetZIndex(t, 5); }
            else p.Inner.Children.Add(t);
        }
        var lifted = tiles[1];
        var g = (TransformGroup)lifted.RenderTransform;
        var sc = (ScaleTransform)g.Children[0];
        var tr = (TranslateTransform)g.Children[1];
        var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 0, ShadowDepth = 0, Opacity = 0.7 };
        lifted.Effect = shadow;
        s.A(sc, ScaleTransform.ScaleXProperty, K((1, 0, null), (1.12, 250, Out), (1.12, 1050, null), (1, 1350, Over)));
        s.A(sc, ScaleTransform.ScaleYProperty, K((1, 0, null), (1.12, 250, Out), (1.12, 1050, null), (1, 1350, Over)));
        s.A(shadow, DropShadowEffect.BlurRadiusProperty, K((0, 0, null), (14, 250, Out), (14, 1050, null), (0, 1350, null)));
        s.A(shadow, DropShadowEffect.ShadowDepthProperty, K((0, 0, null), (5, 250, Out), (5, 1050, null), (0, 1350, null)));
        s.A(tr, TranslateTransform.XProperty, K((0, 0, null), (0, 300, null), (44, 1000, InOut), (44, 1350, null)));
        var other = (TranslateTransform)((TransformGroup)tiles[2].RenderTransform).Children[1];
        s.A(other, TranslateTransform.XProperty, K((0, 0, null), (0, 560, null), (-44, 860, Over)));
    }

    private static void Bell(Stage s)
    {
        var p = Pill(s);
        p.MaxR = 20;
        var bars = p.Inner.Children[1];
        var cover = p.Inner.Children[0];
        var turn = new RotateTransform();
        var pop = new ScaleTransform(0.5, 0.5);
        var bell = new Border { Child = Pv.Icon("bell", 13), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.15), RenderTransform = new TransformGroup { Children = { pop, turn } } }.At(73, 4);
        p.Inner.Children.Add(bell);
        var avatar = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Pv.Res("Green"), Child = Pv.T("A", 13, Pv.B("#FF04342C"), true) };
        ((TextBlock)avatar.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)avatar.Child).VerticalAlignment = VerticalAlignment.Center;
        var words = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 150 };
        words.Children.Add(Pv.T("WhatsApp · jetzt", 10.5, Pv.Sub));
        words.Children.Add(Pv.T("Anna", 11, null, true));
        words.Children.Add(Pv.T("Kommst du heute vorbei?", 11, Pv.B("#FFC7C7CC")));
        var banner = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 }.At(10, 9);
        banner.Children.Add(avatar);
        banner.Children.Add(words);
        p.Inner.Children.Add(banner);
        Fade(s, bars, (1, 0), (0, 150));
        Fade(s, bell, (0, 0), (0, 70), (1, 210), (1, 830), (0, 950));
        s.A(pop, ScaleTransform.ScaleXProperty, K((0.5, 0, null), (0.5, 70, null), (1, 260, Over)));
        s.A(pop, ScaleTransform.ScaleYProperty, K((0.5, 0, null), (0.5, 70, null), (1, 260, Over)));
        s.A(turn, RotateTransform.AngleProperty, K((0, 0, null), (0, 260, null), (-18, 330, InOut), (16, 420, InOut), (-12, 510, InOut), (8, 600, InOut), (-4, 680, InOut), (0, 760, InOut)));
        Fade(s, cover, (1, 0), (1, 830), (0, 950));
        Size(s, p, (96, 22, 0, null), (96, 22, 860, null), (214, 56, 1380, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }));
        Fade(s, banner, (0, 0), (0, 1050), (1, 1300));
    }

    private static void Copy(Stage s)
    {
        var p = Pill(s);
        p.RenderTransformOrigin = new Point(0.5, 0.5);
        var sc = new ScaleTransform();
        p.RenderTransform = sc;
        s.A(sc, ScaleTransform.ScaleXProperty, K((1, 0, null), (1.06, 120, InOut), (0.98, 260, InOut), (1, 420, Over)));
        s.A(sc, ScaleTransform.ScaleYProperty, K((1, 0, null), (0.84, 120, InOut), (1.04, 260, InOut), (1, 420, Over)));
        Fade(s, p.Inner.Children[1], (1, 0), (0, 140));
        var shift = new TranslateTransform(12, 0);
        var word = Pv.Row(3, Pv.Icon("check", 11, Pv.Res("Green")), Pv.T("Kopiert", 11));
        word.HorizontalAlignment = HorizontalAlignment.Left;
        word.Margin = new Thickness(30, 0, 0, 0);
        word.Opacity = 0;
        word.RenderTransform = shift;
        p.Inner.Children.Add(word);
        Fade(s, word, (0, 0), (0, 150), (1, 380));
        s.A(shift, TranslateTransform.XProperty, K((12, 0, null), (12, 150, null), (0, 470, Over)));
    }

    private static void Song(Stage s)
    {
        // like the island: the old cover slides out to the left, the new one comes in from the right, a short tint
        var p = Pill(s);
        var old = (Border)p.Inner.Children[0];
        var oldShift = new TranslateTransform();
        old.RenderTransform = oldShift;
        var nw = Pv.Box(12, 12, Pv.CoverB, 3).At(5, 5);
        var newShift = new TranslateTransform(22, 0);
        nw.RenderTransform = newShift;
        nw.Opacity = 0;
        p.Inner.Children.Add(nw);
        var tint = new Border { Background = Pv.B("#FF30D1A0"), Opacity = 0 };
        p.Inner.Children.Insert(0, tint);
        var title = Pv.T("Neuer Titel", 10.5).At(24, 3.5);
        title.Opacity = 0;
        p.Inner.Children.Add(title);
        s.A(oldShift, TranslateTransform.XProperty, K((0, 0, null), (-22, 340, new CubicEase { EasingMode = EasingMode.EaseIn })));
        Fade(s, old, (1, 0), (0, 320));
        s.A(newShift, TranslateTransform.XProperty, K((22, 0, null), (22, 90, null), (0, 520, Over)));
        Fade(s, nw, (0, 0), (0, 90), (1, 400));
        Fade(s, tint, (0, 0), (0.32, 300), (0.32, 700), (0, 1100));
        Fade(s, p.Inner.Children.OfType<StackPanel>().First(), (1, 0), (0, 200), (0, 950), (1, 1150));
        Fade(s, title, (0, 0), (0, 250), (1, 450), (1, 950), (0, 1150));
    }

    private static void Mute(Stage s)
    {
        var p = Pill(s, -14);
        var micOn = Pv.Icon("mic", 12);
        var micOff = Pv.Icon("mic.slash", 12, Pv.Res("Danger"));
        micOff.Opacity = 0;
        var offScale = new ScaleTransform(0.4, 0.4);
        micOff.RenderTransformOrigin = new Point(0.5, 0.5);
        micOff.RenderTransform = offScale;
        var b = Bubble(s, 51, Pv.Stack(micOn, micOff));
        var sc = (ScaleTransform)b.RenderTransform;
        var bg = new SolidColorBrush(Colors.Black);
        b.Background = bg;
        s.A(sc, ScaleTransform.ScaleXProperty, K((1, 0, null), (1.22, 180, InOut), (1, 520, Over)));
        s.A(sc, ScaleTransform.ScaleYProperty, K((1, 0, null), (1.22, 180, InOut), (1, 520, Over)));
        if (s.Play) bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(0x3A, 0x0B, 0x0B), Ms(300)));
        Fade(s, micOn, (1, 0), (0, 150));
        Fade(s, micOff, (0, 0), (0, 120), (1, 300));
        s.A(offScale, ScaleTransform.ScaleXProperty, K((0.4, 0, null), (0.4, 120, null), (1, 500, Over)));
        s.A(offScale, ScaleTransform.ScaleYProperty, K((0.4, 0, null), (0.4, 120, null), (1, 500, Over)));
        if (s.Fx)
        {
            var tint = new Border { Background = Pv.B("#FF4A0E0B"), Opacity = 0 };
            p.Inner.Children.Insert(0, tint);
            Fade(s, tint, (0, 0), (0.9, 250), (0.9, 600), (0, 1100));
        }
    }

    private static void Shot(Stage s)
    {
        var p = Pill(s);
        p.MaxR = 20;
        var img = new Border { CornerRadius = new CornerRadius(8), Background = Pv.Shot, Margin = new Thickness(10), Opacity = 0, RenderTransformOrigin = new Point(1, 0), RenderTransform = new ScaleTransform(1.08, 1.08) };
        var lines = new StackPanel { Margin = new Thickness(10, 12, 0, 0) };
        lines.Children.Add(new Border { Width = 70, Height = 4, CornerRadius = new CornerRadius(2), Background = Pv.B("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) });
        lines.Children.Add(new Border { Width = 46, Height = 4, CornerRadius = new CornerRadius(2), Background = Pv.B("#66FFFFFF"), HorizontalAlignment = HorizontalAlignment.Left });
        img.Child = lines;
        p.Inner.Children.Add(img);
        Size(s, p, (96, 22, 0, null), (160, 84, 520, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }), (160, 84, 1240, null), (96, 22, 1800, InOut));
        Fade(s, p.Inner.Children[0], (1, 0), (0, 120), (0, 1700), (1, 1850));
        Fade(s, p.Inner.Children[1], (1, 0), (0, 120), (0, 1700), (1, 1850));
        Fade(s, img, (0, 0), (0, 280), (1, 600), (1, 1200), (0, 1640));
        var sc = (ScaleTransform)img.RenderTransform;
        s.A(sc, ScaleTransform.ScaleXProperty, K((1.08, 0, null), (1.08, 280, null), (1, 600, Out), (1, 1200, null), (0.15, 1640, InOut)));
        s.A(sc, ScaleTransform.ScaleYProperty, K((1.08, 0, null), (1.08, 280, null), (1, 600, Out), (1, 1200, null), (0.15, 1640, InOut)));
    }

    private static void Done(Stage s, string icon, Brush color, double from, Color light)
    {
        var p = Pill(s, -14);
        var ring = new Ring { Width = 20, Height = 20, Thickness = 2, Value = from, Stroke = color };
        var sym = Pv.Icon(icon, 10, color);
        var check = Pv.Icon("check", 11, Pv.Res("Green"));
        check.Opacity = 0;
        var cs = new ScaleTransform(0.3, 0.3);
        check.RenderTransformOrigin = new Point(0.5, 0.5);
        check.RenderTransform = cs;
        var b = Bubble(s, 51, Pv.Stack(ring, sym, check));
        int t = 0;
        if (s.Fx)
        {
            var band = new Border { Width = 30, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform(-30, 0) };
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xA0, light.R, light.G, light.B), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 1));
            band.Background = g;
            p.Inner.Children.Add(band);
            s.A((TranslateTransform)band.RenderTransform, TranslateTransform.XProperty, D(-30, 130, 460, 0, new SineEase { EasingMode = EasingMode.EaseIn }));
            t = 430;
        }
        s.A(ring, Ring.ValueProperty, D(from, 100, 380, t, Out));
        if (s.Play) After(t + 200, () => ring.Stroke = Pv.Res("Green"));
        Fade(s, sym, (1, 0), (1, t + 180), (0, t + 300));
        Fade(s, check, (0, 0), (0, t + 260), (1, t + 420));
        s.A(cs, ScaleTransform.ScaleXProperty, K((0.3, 0, null), (0.3, t + 260, null), (1, t + 640, Over)));
        s.A(cs, ScaleTransform.ScaleYProperty, K((0.3, 0, null), (0.3, t + 260, null), (1, t + 640, Over)));
        if (s.Fx)
        {
            var glow = new DropShadowEffect { Color = Color.FromRgb(0x30, 0xD1, 0x58), ShadowDepth = 0, BlurRadius = 0, Opacity = 0.8 };
            b.Effect = glow;
            s.A(glow, DropShadowEffect.BlurRadiusProperty, K((0, 0, null), (0, t + 250, null), (16, t + 500, Out), (0, t + 1100, InOut)));
        }
    }

    private static void Timer(Stage s)
    {
        var p = Pill(s, 0, false);
        var zero = Pv.T("0:00", 11, Pv.Res("Orange"), true);
        zero.HorizontalAlignment = HorizontalAlignment.Right;
        zero.VerticalAlignment = VerticalAlignment.Center;
        zero.Margin = new Thickness(0, 0, 9, 0);
        p.Inner.Children.Add(zero);
        int t = 0;
        if (s.Fx)
        {
            for (int i = 0; i < 2; i++)
            {
                var sc = new ScaleTransform();
                var r = new Border { Width = 100, Height = 26, CornerRadius = new CornerRadius(13), BorderBrush = Pv.Res("Orange"), BorderThickness = new Thickness(2), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = sc };
                s.Add(r, -50, 6);
                s.A(r, UIElement.OpacityProperty, D(0.9, 0, 750, i * 230));
                s.A(sc, ScaleTransform.ScaleXProperty, D(1, 1.3, 750, i * 230, Out));
                s.A(sc, ScaleTransform.ScaleYProperty, D(1, 1.9, 750, i * 230, Out));
            }
            t = 350;
        }
        var shake = new TranslateTransform();
        p.RenderTransform = shake;
        s.A(shake, TranslateTransform.XProperty, K((0, 0, null), (0, t, null), (-3, t + 70, null), (3, t + 150, null), (-2, t + 230, null), (1, t + 300, null), (0, t + 380, null)));
    }

    private static void After(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }
}
