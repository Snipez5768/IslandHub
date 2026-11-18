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
            if (RingStack.Visibility != Visibility.Collapsed) { RingStack.Children.Clear(); _ringEls.Clear(); RingStack.Visibility = Visibility.Collapsed; }
            return;
        }
        // keep the elements (their spin and pop must not restart every second) – only add, drop and re-order
        foreach (var gone in _ringEls.Keys.Where(a => !acts.Contains(a)).ToList()) { RingStack.Children.Remove(_ringEls[gone]); _ringEls.Remove(gone); }
        for (int i = 0; i < acts.Count; i++)
        {
            var a = acts[i];
            if (!_ringEls.TryGetValue(a, out var el))
            {
                el = NewRing(a);
                _ringEls[a] = el;
                if (S.Animations != AnimationLevel.Aus && RingStack.IsVisible) Pop(el);
            }
            if (RingStack.Children.IndexOf(el) != i)
            {
                RingStack.Children.Remove(el);
                RingStack.Children.Insert(i, el);
            }
            Panel.SetZIndex(el, acts.Count - i); // the first lies on top
            el.Margin = new Thickness(i == 0 ? 0 : _stackOpen ? StackOpenGap : StackOverlap, 0, 0, 0);
            FillRing(el, a);
        }
        RingStack.Visibility = Visibility.Visible;
    }

    private Grid NewRing(Act a)
    {
        var g = new Grid { Width = 20, Height = 20, Cursor = Cursors.Hand, Tag = a, Background = Brushes.Transparent };
        // a black disc behind, so overlapping rings stay apart
        g.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.Black });
        var ring = new Ring { Width = 17, Height = 17, Thickness = 2, Value = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
        g.Children.Add(ring);
        g.Children.Add(new SfIcon { Size = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
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
        var ring = (Ring)g.Children[1];
        var icon = (SfIcon)g.Children[2];
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
        if (!ReferenceEquals(ring.Stroke, color)) { ring.Stroke = color; ring.Track = Tint(color, 0.25); }
        ring.Value = value;
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
