using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandHub.UI;

/// <summary>
/// iOS-style scroll wheel ("picker"). Mouse wheel, drag or click to choose a number.
/// With <see cref="Loop"/> the values wrap endlessly (… 58, 59, 0, 1 …).
/// </summary>
public sealed class WheelPicker : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(WheelPicker),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(WheelPicker), new FrameworkPropertyMetadata(0));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(WheelPicker), new FrameworkPropertyMetadata(59));
    public static readonly DependencyProperty LoopProperty = DependencyProperty.Register(nameof(Loop), typeof(bool), typeof(WheelPicker), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(WheelPicker),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool Loop { get => (bool)GetValue(LoopProperty); set => SetValue(LoopProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    /// <summary>"00" instead of "0" – a time of day (Wecker) reads 07 : 05.</summary>
    public static readonly DependencyProperty TwoDigitsProperty = DependencyProperty.Register(nameof(TwoDigits), typeof(bool), typeof(WheelPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool TwoDigits { get => (bool)GetValue(TwoDigitsProperty); set => SetValue(TwoDigitsProperty, value); }

    public event Action<int> ValueCommitted;

    private double _pos, _target;
    private bool _dragging, _animating, _committing;
    private Point _dragStart;
    private double _dragStartPos;
    private int _wheelAcc;
    private static readonly Typeface Face = new(new FontFamily("SF Pro Display, Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("SF Pro Display, Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public WheelPicker()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        ClipToBounds = true;
    }

    private int Count => Maximum - Minimum + 1;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var w = (WheelPicker)d;
        if (w._committing) return;
        w._pos = w._target = (int)e.NewValue;
        w.InvalidateVisual();
    }

    private int Normalize(int v) => Loop ? ((v - Minimum) % Count + Count) % Count + Minimum : Math.Clamp(v, Minimum, Maximum);

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 48 : Math.Min(48, available.Width), ItemHeight * 3);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double cy = ActualHeight / 2, h = ItemHeight;
        int first = (int)Math.Floor(_pos) - 3, last = (int)Math.Ceiling(_pos) + 3;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int k = first; k <= last; k++)
        {
            if (!Loop && (k < Minimum || k > Maximum)) continue;
            double dist = k - _pos;
            if (Math.Abs(dist) > 2.4) continue;
            double y = cy + dist * h * (1 - Math.Abs(dist) * 0.06); // slight cylinder compression
            double a = Math.Max(0, 1 - Math.Abs(dist) * 0.42);
            double size = 17 * (1 - Math.Min(Math.Abs(dist), 2) * 0.12);
            bool center = Math.Abs(dist) < 0.5;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * (center ? 1 : a * 0.7)), 255, 255, 255));
            var ft = new FormattedText(Normalize(k).ToString(TwoDigits ? "00" : "0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                center ? FaceBold : Face, size, brush, dpi);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, y - ft.Height / 2));
        }
    }

    private void AnimateTo(double target)
    {
        if (!Loop) target = Math.Clamp(target, Minimum, Maximum);
        _target = target;
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object sender, EventArgs e)
    {
        double diff = _target - _pos;
        if (Math.Abs(diff) < 0.003)
        {
            _pos = _target;
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            Commit();
        }
        else _pos += diff * 0.28;
        InvalidateVisual();
    }

    private void Commit()
    {
        int v = Normalize((int)Math.Round(_pos));
        if (Loop)
        {
            // keep the position small so it never drifts far after endless scrolling
            _pos = _target = v + (_pos - Math.Round(_pos));
        }
        _committing = true;
        Value = v;
        _committing = false;
        ValueCommitted?.Invoke(v);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        _wheelAcc += e.Delta;
        int steps = 0;
        while (_wheelAcc >= 120) { steps--; _wheelAcc -= 120; }
        while (_wheelAcc <= -120) { steps++; _wheelAcc += 120; }
        if (steps != 0) AnimateTo(Math.Round(_target) + steps);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartPos = _pos;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _pos = _dragStartPos - (p.Y - _dragStart.Y) / ItemHeight;
        if (!Loop) _pos = Math.Clamp(_pos, Minimum - 0.4, Maximum + 0.4);
        _target = _pos;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - _dragStart.Y) < 3)
            AnimateTo(Math.Round(_pos + (p.Y - ActualHeight / 2) / ItemHeight)); // click a neighbour to select it
        else
            AnimateTo(Math.Round(_pos));
        e.Handled = true;
    }
}
