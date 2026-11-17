using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PeakPill.UI;

/// <summary>
/// Text whose digits roll like a counter (user wish 2026-11-17: "Zahlen rollen" – timer, percent, volume).
/// Only the characters that changed move: a bigger digit comes up from below, a smaller one drops in from above
/// (a countdown rolls downwards). Anything that is not "same length, digits changed" simply swaps.
/// Font, colour and size come from the control (inherited by the inner text), so it drops in for a TextBlock.
/// </summary>
public sealed class RollingText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RollingText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((RollingText)d).OnText((string)e.OldValue, (string)e.NewValue)));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value ?? ""); }

    /// <summary>false = no rolling (reduced animations, or the caller wants a plain swap).</summary>
    public bool Roll { get; set; } = true;

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };

    public RollingText()
    {
        Focusable = false;
        IsTabStop = false;
        AddVisualChild(_row);
        AddLogicalChild(_row);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _row;
    protected override Size MeasureOverride(Size c) { _row.Measure(c); return _row.DesiredSize; }
    protected override Size ArrangeOverride(Size s) { _row.Arrange(new Rect(s)); return s; }

    private void OnText(string old, string now)
    {
        old ??= ""; now ??= "";
        bool roll = Roll && IsLoaded && IsVisible && App.Settings.Animations != Core.AnimationLevel.Aus
                    && old.Length == now.Length && old.Length > 0 && old != now && Cells() == old.Length;
        if (!roll) { Build(now); return; }
        for (int i = 0; i < now.Length; i++)
        {
            if (old[i] == now[i]) continue;
            var cell = (Grid)_row.Children[i];
            if (!char.IsDigit(old[i]) || !char.IsDigit(now[i])) { ((TextBlock)cell.Children[^1]).Text = now[i].ToString(); continue; }
            // up for a bigger digit, down for a smaller (9 → 0 counts as going up, 0 → 9 as going down)
            int a = old[i] - '0', b = now[i] - '0';
            bool up = b > a ? !(a == 0 && b == 9) : (a == 9 && b == 0);
            RollCell(cell, now[i], up, i);
        }
    }

    private int Cells() => _row.Children.Count;

    private void Build(string text)
    {
        _row.Children.Clear();
        foreach (var ch in text) _row.Children.Add(NewCell(ch));
    }

    private static Grid NewCell(char ch)
    {
        var g = new Grid { ClipToBounds = true };
        g.Children.Add(new TextBlock { Text = ch.ToString() });
        return g;
    }

    private void RollCell(Grid cell, char ch, bool up, int index)
    {
        // the leaving digit and the coming one share the cell for the move
        while (cell.Children.Count > 1) cell.Children.RemoveAt(0);
        var outgoing = (TextBlock)cell.Children[0];
        var incoming = new TextBlock { Text = ch.ToString() };
        cell.Children.Add(incoming);
        double h = Math.Max(10, FontSize * 1.25);
        var dur = TimeSpan.FromMilliseconds(320);
        var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, (_row.Children.Count - 1 - index)) * 25); // the last digit first, like a real counter

        var tOut = new TranslateTransform();
        outgoing.RenderTransform = tOut;
        var aOut = new DoubleAnimation(0, up ? -h : h, TimeSpan.FromMilliseconds(220)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        aOut.Completed += (_, _) => cell.Children.Remove(outgoing);
        tOut.BeginAnimation(TranslateTransform.YProperty, aOut);
        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { BeginTime = delay });

        var tIn = new TranslateTransform(0, up ? h : -h);
        incoming.RenderTransform = tIn;
        tIn.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up ? h : -h, 0, dur) { BeginTime = delay, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }
}
