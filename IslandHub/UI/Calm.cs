using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IslandHub.UI;

/// <summary>
/// Endless decoration (spinners, pulses, equalizers). The island is a see-through window, so every animated frame
/// redraws and copies the whole window – measured 2026-11-15: three always-turning rings alone kept the render thread
/// at ~23 % of a core. Calm keeps such loops cheap: they run only while their element is on screen, and all at the same
/// lower frame rate, so they share their frames. One-shot motion (springs, opening, closing) is not touched.
///
/// Stopping and hiding take effect only once the moment is over (2026-11-16): the side bubble is refilled every half
/// second – it hides its ring and stops its star, then shows / starts them again in the same pass. Stopping at once
/// restarted every spinner from zero twice a second (the Claude ring "tried to load and kept resetting").
/// </summary>
public static class Calm
{
    public const int Fps = 30;

    private sealed class Entry
    {
        public AnimationTimeline Anim;
        public bool Running;      // applied to the target right now
        public int StopGen;       // a pending stop runs only if nothing asked for the loop again meanwhile
        public bool StopPending;
    }

    private static readonly ConditionalWeakTable<UIElement, Dictionary<(IAnimatable Target, DependencyProperty Prop), Entry>> Loops = new();

    /// <summary>Runs <paramref name="anim"/> on <paramref name="target"/> whenever <paramref name="host"/> is visible.
    /// Asked again while it runs (or while a stop is pending), it simply keeps running – no restart.</summary>
    public static void Loop(UIElement host, IAnimatable target, DependencyProperty prop, AnimationTimeline anim)
    {
        var map = Loops.GetValue(host, h => { h.IsVisibleChanged += OnVisibleChanged; return new(); });
        if (map.TryGetValue((target, prop), out var e))
        {
            e.StopPending = false;
            e.StopGen++;
            if (e.Running) return; // already turning – leave it
        }
        else map[(target, prop)] = e = new Entry();
        Timeline.SetDesiredFrameRate(anim, Fps);
        e.Anim = anim;
        if (host.IsVisible) { target.BeginAnimation(prop, anim); e.Running = true; }
    }

    /// <summary>Stops the loop – once this moment is over (a Loop call in the same pass cancels it).</summary>
    public static void Stop(UIElement host, IAnimatable target, DependencyProperty prop)
    {
        if (!Loops.TryGetValue(host, out var map) || !map.TryGetValue((target, prop), out var e)) { target.BeginAnimation(prop, null); return; }
        if (e.StopPending) return;
        e.StopPending = true;
        int gen = ++e.StopGen;
        host.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!e.StopPending || e.StopGen != gen) return;
            map.Remove((target, prop));
            target.BeginAnimation(prop, null);
        }), DispatcherPriority.ContextIdle);
    }

    /// <summary>The loop is wanted (running or waiting to be shown) and no stop is pending.</summary>
    public static bool Running(UIElement host, IAnimatable target, DependencyProperty prop)
        => Loops.TryGetValue(host, out var map) && map.TryGetValue((target, prop), out var e) && !e.StopPending;

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs ev)
    {
        if (sender is not UIElement host || !Loops.TryGetValue(host, out var map)) return;
        if ((bool)ev.NewValue)
        {
            foreach (var (key, e) in map)
                if (!e.Running) { key.Target.BeginAnimation(key.Prop, e.Anim); e.Running = true; }
            return;
        }
        // hidden: pause once the moment is over – hidden and shown again in the same pass changes nothing
        host.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (host.IsVisible) return;
            foreach (var (key, e) in map)
                if (e.Running) { key.Target.BeginAnimation(key.Prop, null); e.Running = false; }
        }), DispatcherPriority.ContextIdle);
    }
}
