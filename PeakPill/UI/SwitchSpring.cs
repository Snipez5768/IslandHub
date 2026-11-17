using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PeakPill.UI;

/// <summary>
/// The on/off switches spring over instead of jumping (2026-11-17). The template still moves the knob by its trigger
/// (left/right); this plays the way in between: the knob starts where it was and springs to its new place, slightly
/// stretched while it travels. Only on a real change – opening a window doesn't animate every switch.
/// </summary>
public static class SwitchSpring
{
    public static void Attach(FrameworkElement root)
    {
        root.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) => Play(e.OriginalSource as ToggleButton, true)));
        root.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, e) => Play(e.OriginalSource as ToggleButton, false)));
    }

    private static void Play(ToggleButton tb, bool on)
    {
        if (tb == null || !tb.IsLoaded || !tb.IsVisible || App.Settings.Animations == Core.AnimationLevel.Aus) return;
        if (tb.Template?.FindName("knob", tb) is not Border knob) return;
        double travel = Math.Max(0, tb.ActualWidth - knob.ActualWidth - 4);
        if (travel < 1) return;
        TranslateTransform move;
        if (knob.RenderTransform is TransformGroup g && g.Children.OfType<TranslateTransform>().FirstOrDefault() is { } t) move = t;
        else
        {
            move = new TranslateTransform();
            var group = new TransformGroup();
            if (knob.RenderTransform is Transform old && old != Transform.Identity) group.Children.Add(old);
            group.Children.Add(move);
            knob.RenderTransform = group;
        }
        // the trigger has already put the knob on its new side: start from the old one
        var a = new DoubleAnimation(on ? -travel : travel, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut },
        };
        move.BeginAnimation(TranslateTransform.XProperty, a);
    }
}
