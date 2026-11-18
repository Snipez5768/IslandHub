using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// The ring stack (user's pick 2026-11-18, replaces the circles beside the island; those stay as a setting):
/// everything besides the main thing sits on the right INSIDE the island as small overlapping rings – no numbers, the
/// ring shows how far (timer, Claude, a download); a call / the muted mic always sits here on the right too.
/// Under the pointer the stack fans out and the island grows a little; a click on a ring opens its page.
/// </summary>
public partial class IslandWindow
{
    private readonly Dictionary<Act, Grid> _ringEls = new();
    private bool _stackOpen, _stackWired;
    private const double StackOverlap = -7, StackOpenGap = 5;
    private readonly HashSet<Grid> _ringEntering = new();

    private void UpdateRingStack(List<Act> acts)
    {
        if (!_stackWired)
        {
            _stackWired = true;
            RingStack.MouseEnter += (_, _) => FanStack(true);
            RingStack.MouseLeave += (_, _) => FanStack(false);
        }
        if (acts == null || acts.Count == 0)
        {
            if (RingStack.Visibility != Visibility.Collapsed) { RingStack.Children.Clear(); _ringEls.Clear(); _ringEntering.Clear(); RingStack.Visibility = Visibility.Collapsed; }
            return;
        }
        // keep the elements (their spin and pop must not restart every second) – only add, drop and re-order
        foreach (var gone in _ringEls.Keys.Where(a => !acts.Contains(a)).ToList()) { RingStack.Children.Remove(_ringEls[gone]); _ringEntering.Remove(_ringEls[gone]); _ringEls.Remove(gone); }
        for (int i = 0; i < acts.Count; i++)
        {
            var a = acts[i];
            bool fresh = !_ringEls.TryGetValue(a, out var el);
            if (fresh)
            {
                el = NewRing(a);
                _ringEls[a] = el;
            }
            if (RingStack.Children.IndexOf(el) != i)
            {
                RingStack.Children.Remove(el);
                RingStack.Children.Insert(i, el);
            }
            // the last one lies on top (user wish 2026-11-18) – one that is still coming out lies underneath until it lands
            if (!_ringEntering.Contains(el)) Panel.SetZIndex(el, i);
            el.Margin = new Thickness(i == 0 ? 0 : _stackOpen ? StackOpenGap : StackOverlap, 0, 0, 0);
            if (fresh && S.Animations != AnimationLevel.Aus && _state == State.Collapsed) _ringEntering.Add(el);
            FillRing(el, a);
            if (fresh && _ringEntering.Contains(el)) RingEntry(el, i);
        }
        RingStack.Visibility = Visibility.Visible;
    }

    private Grid NewRing(Act a)
    {
        var g = new Grid { Width = 20, Height = 20, Cursor = Cursors.Hand, Tag = a, Background = Brushes.Transparent };
        // no disc behind any more: where a ring isn't filled yet the one below shows through, its filled part lies over
        // it (user wish 2026-11-18)
        var ring = new Ring { Width = 17, Height = 17, Thickness = 2, Value = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
        g.Children.Add(ring);
        g.Children.Add(new SfIcon { Size = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) });
        g.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            BubbleClicked(new Border { Tag = g.Tag }); // same as the circles: a call mutes, the rest opens its page
        };
        g.MouseEnter += (_, _) => g.ToolTip = BubbleTip(g.Tag as Act?);
        return g;
    }

    private void FillRing(Grid g, Act a)
    {
        var ring = (Ring)g.Children[0];
        var icon = (SfIcon)g.Children[1];
        Brush color;
        double value;
        string kind;
        bool spin = false;
        switch (a)
        {
            case Act.Timer:
                color = (Brush)FindResource("Orange"); value = H.Timer.RemainingPercent; kind = "timer";
                if (_rimLastMinute) color = (Brush)FindResource("Danger");
                break;
            case Act.Agent:
            {
                var ag = H.Agents.Primary;
                bool done = !H.Agents.AnyRunning && !H.Agents.AnyWaiting;
                if (done) { color = (Brush)FindResource("Green"); value = 100; kind = "check"; }
                else if (H.Agents.AnyWaiting) { color = AgentBrush; value = 100; kind = "asterisk"; }
                else if (ag?.HasPlan == true) { color = AgentBrush; value = Math.Max(8, ag.Progress * 100); kind = "asterisk"; }
                else { color = AgentBrush; value = 28; kind = "asterisk"; spin = true; }
                break;
            }
            case Act.Steam:
            {
                color = SteamBrush; kind = "download";
                bool total = Dl is not BrowserDownloadService b || b.HasTotal;
                value = total ? Math.Max(6, Dl.Progress * 100) : 28;
                spin = !total;
                break;
            }
            case Act.Live:
            {
                var mic = H.Privacy.Apps.FirstOrDefault(x => x.Mic);
                bool muted = mic?.SelfMuted == true;
                color = muted ? (Brush)FindResource("Danger") : (Brush)FindResource("Green");
                value = 100;
                kind = muted ? "mic.slash" : mic != null ? "mic" : H.Privacy.AnyScreen ? "display" : "camera";
                break;
            }
            case Act.MicMuted: color = (Brush)FindResource("Danger"); value = 100; kind = "mic.slash"; break;
            case Act.Obs: color = H.Obs.Recording ? (Brush)FindResource("Danger") : (Brush)FindResource("Green"); value = 100; kind = "record"; break;
            case Act.Recording: color = (Brush)FindResource("Danger"); value = 100; kind = "record"; break;
            case Act.Alarm: color = (Brush)FindResource("Orange"); value = 100; kind = "bell"; break;
            default: color = Brushes.White; value = 100; kind = "dot.radiowaves"; break;
        }
        if (!ReferenceEquals(ring.Stroke, color)) { ring.Stroke = color; ring.Track = Tint(color, 0.22); }
        ring.Tag = value;                                   // (while it comes out, the fill runs there by itself)
        if (!_ringEntering.Contains(g)) ring.Value = value;
        icon.Kind = kind;
        icon.Foreground = color;
        // without a known end it turns (Claude without a plan, a download of unknown size)
        bool spinning = ring.RenderTransform is RotateTransform rt && rt.HasAnimatedProperties;
        if (spin && !spinning && S.Animations != AnimationLevel.Aus)
        {
            var r = new RotateTransform();
            ring.RenderTransform = r;
            r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else if (!spin && spinning) ring.RenderTransform = Transform.Identity;
    }

    /// <summary>
    /// A new ring comes out (user's pick E4, 2026-11-18): it slides out from behind the ring before it (the first one
    /// grows out of a point) as an empty circle, lands on top, its icon pops up – and then the ring fills to where it is.
    /// </summary>
    private void RingEntry(Grid g, int index)
    {
        var ring = (Ring)g.Children[0];
        var icon = (SfIcon)g.Children[1];
        var iconScale = (ScaleTransform)icon.RenderTransform;
        ring.Value = 0;
        Panel.SetZIndex(g, -1);                                         // behind the stack while it slides out
        var slide = new TranslateTransform();
        var grow = new ScaleTransform(1, 1);
        g.RenderTransformOrigin = new Point(0.5, 0.5);
        g.RenderTransform = new TransformGroup { Children = { grow, slide } };
        var ms = (double v) => TimeSpan.FromMilliseconds(v);
        var outEase = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        if (index > 0)
        {
            double behind = 20 + (_stackOpen ? StackOpenGap : StackOverlap);
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-behind, 0, ms(440)) { EasingFunction = outEase, FillBehavior = FillBehavior.Stop });
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, ms(440)) { EasingFunction = outEase, FillBehavior = FillBehavior.Stop });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, ms(440)) { EasingFunction = outEase, FillBehavior = FillBehavior.Stop });
        }
        else
        {
            var pop = new DoubleAnimation(0.2, 1, ms(420)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ms(180)) { FillBehavior = FillBehavior.Stop });
        // the icon pops up once the circle is almost in its place
        iconScale.ScaleX = iconScale.ScaleY = 0;
        var iconPop = new DoubleAnimation(0, 1, ms(300)) { BeginTime = ms(300), EasingFunction = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut } };
        iconPop.Completed += (_, _) =>
        {
            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            iconScale.ScaleX = iconScale.ScaleY = 1;
        };
        iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconPop);
        iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconPop);
        // landed: on top like the last one should be
        After(420, () => { if (_ringEntering.Contains(g)) Panel.SetZIndex(g, RingStack.Children.IndexOf(g)); });
        // then the ring fills to where it is
        double target = ring.Tag is double t ? t : 100;
        var fill = new DoubleAnimation(0, target, ms(target >= 99 ? 520 : Math.Max(260, target * 6))) { BeginTime = ms(560), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        fill.Completed += (_, _) =>
        {
            _ringEntering.Remove(g);
            ring.Value = ring.Tag is double now ? now : target;          // where it is now (a timer moved on meanwhile)
            g.RenderTransform = Transform.Identity;
        };
        ring.BeginAnimation(Ring.ValueProperty, fill);
    }

    /// <summary>Under the pointer the rings move apart and the island grows by that much; away, they slide together.</summary>
    private void FanStack(bool open)
    {
        if (_stackOpen == open || _state != State.Collapsed) return;
        _stackOpen = open;
        int n = RingStack.Children.Count;
        if (n < 2) return;
        var d = TimeSpan.FromMilliseconds(open ? 300 : 240);
        IEasingFunction ease = open ? new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 1; i < n; i++)
        {
            var el = (FrameworkElement)RingStack.Children[i];
            var to = new Thickness(open ? StackOpenGap : StackOverlap, 0, 0, 0);
            var anim = new ThicknessAnimation(to, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            anim.Completed += (_, _) => el.Margin = to;
            el.BeginAnimation(MarginProperty, anim);
            el.Margin = to;
        }
        double delta = (n - 1) * (StackOpenGap - StackOverlap) * (open ? 1 : -1);
        AnimateSize(_targetW + delta, _targetH, PillH / 2.0, true);
    }
}
