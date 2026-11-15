using System.Runtime.CompilerServices;
using System.Windows;

using System.Windows.Media.Animation;

namespace IslandHub.UI;

/// <summary>
/// Endless decoration (spinners, pulses, equalizers). The island is a see-through window, so every animated frame
/// redraws and copies the whole window – measured 2026-11-15: three always-turning rings alone kept the render thread
/// at ~23 % of a core. Calm keeps such loops cheap: they run only while their element is on screen, and all at the same
/// lower frame rate, so they share their frames. One-shot motion (springs, opening, closing) is not touched.
/// </summary>
public static class Calm
{
    public const int Fps = 30;

    private static readonly ConditionalWeakTable<UIElement, Dictionary<(IAnimatable Target, DependencyProperty Prop), AnimationTimeline>> Loops = new();

    /// <summary>Runs <paramref name="anim"/> on <paramref name="target"/> whenever <paramref name="host"/> is visible.</summary>
    public static void Loop(UIElement host, IAnimatable target, DependencyProperty prop, AnimationTimeline anim)
    {
        Timeline.SetDesiredFrameRate(anim, Fps);
        var map = Loops.GetValue(host, h => { h.IsVisibleChanged += OnVisibleChanged; return new(); });
        map[(target, prop)] = anim;
        target.BeginAnimation(prop, host.IsVisible ? anim : null);
    }

    public static void Stop(UIElement host, IAnimatable target, DependencyProperty prop)
    {
        if (Loops.TryGetValue(host, out var map)) map.Remove((target, prop));
        target.BeginAnimation(prop, null);
    }

    public static bool Running(UIElement host, IAnimatable target, DependencyProperty prop)
        => Loops.TryGetValue(host, out var map) && map.ContainsKey((target, prop));

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement host || !Loops.TryGetValue(host, out var map)) return;
        bool visible = (bool)e.NewValue;
        foreach (var ((target, prop), anim) in map) target.BeginAnimation(prop, visible ? anim : null);
    }
}

