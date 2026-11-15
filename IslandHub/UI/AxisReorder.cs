using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandHub.UI;

/// <summary>
/// Reordering by dragging, locked to ONE axis (user wish 2026-11-14): the item only moves left/right (game tiles) or
/// up/down (tab list in Settings). Near a slot it snaps in with a small animation; pulled further than the snap
/// distance it follows the pointer again. The other items slide aside. A press without moving stays a normal click.
/// </summary>
public sealed class AxisReorder
{
    private readonly ItemsControl _list;
    private readonly bool _horizontal;
    private readonly Action<int, int> _moved;
    private const double StartDistance = 5, Snap = 9;

    private FrameworkElement _item;
    private int _from = -1, _to = -1;
    private Point _down;
    private bool _dragging, _snapped;
    private List<FrameworkElement> _items;
    private double[] _pos;
    private double _pitch;

    /// <summary>True right after a drag ended – the click that follows must not start anything.</summary>
    public bool JustDragged { get; private set; }

    public AxisReorder(ItemsControl list, bool horizontal, Action<int, int> moved)
    {
        _list = list;
        _horizontal = horizontal;
        _moved = moved;
        list.PreviewMouseLeftButtonDown += OnDown;
        list.PreviewMouseMove += OnMove;
        list.PreviewMouseLeftButtonUp += OnUp;
        list.LostMouseCapture += (_, _) => { if (_dragging) Finish(); };
    }

    private double Axis(Point p) => _horizontal ? p.X : p.Y;

    private List<FrameworkElement> Containers() =>
        Enumerable.Range(0, _list.Items.Count).Select(i => _list.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement).Where(c => c != null).ToList();

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        JustDragged = false;
        if (e.OriginalSource is DependencyObject d && IsInTextBox(d)) return; // renaming a tile
        var items = Containers();
        _item = items.FirstOrDefault(c => c.IsMouseOver);
        if (_item == null) return;
        _items = items;
        _from = items.IndexOf(_item);
        _down = e.GetPosition(_list);
    }

    private static bool IsInTextBox(DependencyObject d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d)) if (d is TextBox) return true;
        return false;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_item == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_list);
        double delta = Axis(p) - Axis(_down);
        if (!_dragging)
        {
            if (Math.Abs(delta) < StartDistance) return;
            BeginDrag();
        }
        // where the item would be if it simply followed the pointer (only along the axis)
        double raw = _pos[_from] + delta;
        int to = Math.Clamp((int)Math.Round((raw - _pos[0]) / _pitch), 0, _items.Count - 1);
        if (to != _to) { _to = to; ShiftOthers(); }
        double slot = _pos[0] + _to * _pitch;
        double offset = raw - _pos[_from];
        if (Math.Abs(raw - slot) <= Snap)
        {
            // close to a slot: it snaps in (once), and stays there while the pointer is within reach
            if (!_snapped) { _snapped = true; Animate(_item, slot - _pos[_from], 110); }
        }
        else
        {
            if (_snapped) { _snapped = false; StopAnimation(_item); }
            Shift(_item).X = _horizontal ? offset : 0;
            Shift(_item).Y = _horizontal ? 0 : offset;
        }
        e.Handled = true;
    }

    private void BeginDrag()
    {
        _dragging = true;
        _snapped = false;
        _to = _from;
        _pos = _items.Select(c => Axis(c.TranslatePoint(new Point(0, 0), _list))).ToArray();
        _pitch = _items.Count > 1 ? Math.Abs(_pos[1] - _pos[0]) : (_horizontal ? _item.ActualWidth : _item.ActualHeight);
        if (_pitch < 1) _pitch = _horizontal ? _item.ActualWidth : _item.ActualHeight;
        Panel.SetZIndex(_item, 100);
        Lift(_item, true);
        _list.CaptureMouse();
    }

    /// <summary>
    /// Picked up: the item pops up a little (scale with a small overshoot + a soft shadow) and stays lifted while
    /// dragged; on release it sinks back into place (user wish 2026-11-14).
    /// </summary>
    private void Lift(FrameworkElement c, bool up)
    {
        if (c.Tag is not ScaleTransform sc)
        {
            sc = new ScaleTransform(1, 1);
            c.Tag = sc;
        }
        c.RenderTransformOrigin = new Point(0.5, 0.5);
        if (c.RenderTransform is not TransformGroup)
        {
            var g = new TransformGroup();
            g.Children.Add(sc);
            g.Children.Add(new TranslateTransform());
            c.RenderTransform = g;
        }
        var ease = up ? (IEasingFunction)new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
        var a = new DoubleAnimation(up ? 1.08 : 1, TimeSpan.FromMilliseconds(up ? 260 : 180)) { EasingFunction = ease };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        if (up)
            c.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        else
            c.Effect = null;
    }

    /// <summary>The items between the old and the new place slide one step aside.</summary>
    private void ShiftOthers()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i] == _item) continue;
            double by = 0;
            if (_to > _from && i > _from && i <= _to) by = -_pitch;
            else if (_to < _from && i >= _to && i < _from) by = _pitch;
            Animate(_items[i], by, 140);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) { _item = null; return; }
        e.Handled = true;
        Finish();
    }

    private void Finish()
    {
        if (!_dragging) return;
        _dragging = false;
        JustDragged = true;
        _list.ReleaseMouseCapture();
        var item = _item;
        int from = _from, to = _to;
        var items = _items;
        // settle into the slot, then really reorder
        Lift(item, false); // sinks back into place while it settles
        Animate(item, (to - from) * _pitch, 160, () =>
        {
            foreach (var c in items) { StopAnimation(c); c.RenderTransform = Transform.Identity; Panel.SetZIndex(c, 0); c.Opacity = 1; c.Effect = null; c.Tag = null; }
            if (from != to) _moved(from, to);
        });
        _item = null;
    }

    /// <summary>The move part of the item's transform (inside the lift group while picked up).</summary>
    private TranslateTransform Shift(FrameworkElement c)
    {
        if (c.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        if (c.RenderTransform is TransformGroup g && g.Children.OfType<TranslateTransform>().FirstOrDefault() is { } inGroup) return inGroup;
        t = new TranslateTransform();
        c.RenderTransform = t;
        return t;
    }

    private void Animate(FrameworkElement c, double to, int ms, Action done = null)
    {
        var t = Shift(c);
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        // must be hooked up BEFORE the animation starts, otherwise it never fires
        if (done != null) a.Completed += (_, _) => done();
        t.BeginAnimation(_horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty, a);
    }

    private void StopAnimation(FrameworkElement c)
    {
        var t = Shift(c);
        var prop = _horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        double now = (double)t.GetValue(prop);
        t.BeginAnimation(prop, null);
        t.SetValue(prop, now);
    }
}
