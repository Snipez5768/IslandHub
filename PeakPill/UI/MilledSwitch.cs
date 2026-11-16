using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace PeakPill.UI;

/// <summary>
/// A two-way switch that looks milled into the island (approved mockup 2026-11-15, Timer | Wecker): a groove darker
/// than the island with its shadow under the top edge and a fine light line where the lower edge catches the light,
/// and a raised grey slider that springs to the chosen side. The chosen word is white, the other one sits dark in the groove.
/// </summary>
public sealed class MilledSwitch : Grid
{
    private readonly TextBlock _a, _b;
    private readonly Border _thumb;
    private readonly TranslateTransform _shift = new();
    private readonly StackPanel _words;
    private bool _second;

    public event Action<bool> Changed;

    /// <summary>true = the second word (e.g. "Wecker") is chosen.</summary>
    public bool Second
    {
        get => _second;
        set { if (_second == value) return; _second = value; Place(true); }
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static LinearGradientBrush V(params (string Hex, double Offset)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (h, o) in stops) g.GradientStops.Add(new GradientStop(C(h), o));
        g.Freeze();
        return g;
    }

    public MilledSwitch(string first, string second, double fontSize = 12.5)
    {
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        double h = Math.Round(fontSize * 1.75);
        Height = h;
        double r = h / 2;

        // the groove: deepest at the top, a touch lighter towards the bottom
        var groove = new Border { CornerRadius = new CornerRadius(r), Background = V(("#FF020203", 0), ("#FF08080A", 0.45), ("#FF141416", 1)) };
        // shadow the upper edge throws into the cut
        var topShade = new Border { CornerRadius = new CornerRadius(r), IsHitTestVisible = false,
                                    Background = V(("#F2000000", 0), ("#80000000", 0.18), ("#00000000", 0.5)) };
        // the lower edge catching light (inside) …
        var lowerLip = new Border { CornerRadius = new CornerRadius(r), IsHitTestVisible = false, BorderThickness = new Thickness(0, 0, 0, 1),
                                    BorderBrush = V(("#00FFFFFF", 0), ("#00FFFFFF", 0.6), ("#22FFFFFF", 1)) };
        // … and the island's surface right below the cut (a hair of light outside)
        var bevel = new Border { CornerRadius = new CornerRadius(r + 0.5), Margin = new Thickness(-0.5, -0.5, -0.5, -1), IsHitTestVisible = false,
                                 BorderThickness = new Thickness(0.5, 0.5, 0.5, 1), BorderBrush = V(("#E6000000", 0), ("#00000000", 0.5), ("#14FFFFFF", 1)) };

        // the raised slider
        _thumb = new Border
        {
            Margin = new Thickness(2), CornerRadius = new CornerRadius(r - 2), HorizontalAlignment = HorizontalAlignment.Left,
            Background = V(("#FF5A5A5F", 0), ("#FF434347", 0.55), ("#FF38383B", 1)),
            BorderThickness = new Thickness(0, 0.8, 0, 0), BorderBrush = new SolidColorBrush(C("#4DFFFFFF")),
            RenderTransform = _shift,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.9 }
        };

        _a = Word(first, fontSize);
        _b = Word(second, fontSize);
        _words = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        _words.Children.Add(_a);
        _words.Children.Add(_b);

        Children.Add(bevel);
        Children.Add(groove);
        Children.Add(topShade);
        Children.Add(lowerLip);
        Children.Add(_thumb);
        Children.Add(_words);

        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _second = !_second;
            Place(true);
            Changed?.Invoke(_second);
        };
        // keep the press from reaching the island (it would close or open things)
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        Loaded += (_, _) => Place(false);
        SizeChanged += (_, _) => Place(false);
    }

    private static TextBlock Word(string text, double size) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Padding = new Thickness(size * 0.8, 0, size * 0.8, 0),
        VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(C("#FF6E6E73"))
    };

    private void Place(bool animate)
    {
        if (!IsLoaded) return;
        var on = _second ? _b : _a;
        var off = _second ? _a : _b;
        double x = on.TranslatePoint(new Point(0, 0), this).X - 2;
        double w = on.ActualWidth;
        if (w <= 0) return;
        var d = TimeSpan.FromMilliseconds(450);
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        if (animate)
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, d) { EasingFunction = ease });
            _thumb.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        }
        else
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _thumb.BeginAnimation(WidthProperty, null);
            _shift.X = x;
            _thumb.Width = w;
        }
        var white = Brushes.White;
        on.Foreground = white;
        off.Foreground = new SolidColorBrush(C("#FF6E6E73"));
    }
}
