using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandHub.Core;

namespace IslandHub.UI;

/// <summary>
/// Two little eyes on the left of the resting island that look at the pointer (user wish 2026-11-16, Settings ›
/// Erscheinungsbild). Cheap on purpose: the pointer is read 15 times a second only while they are shown, and the
/// island redraws only when the pointer really moved; now and then they blink (one short one-shot animation).
/// </summary>
public partial class IslandWindow
{
    private readonly DispatcherTimer _eyeTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly DispatcherTimer _blinkTimer = new();
    private readonly Random _eyeRnd = new();
    private Native.POINT _eyeLast;
    private bool _eyesOn, _eyesWired;

    /// <summary>Called by the pill: <paramref name="idle"/> = nothing else sits on the left right now.</summary>
    private void UpdateEyes(bool idle)
    {
        if (!_eyesWired)
        {
            _eyesWired = true;
            _eyeTimer.Tick += (_, _) => LookAtPointer();
            _blinkTimer.Tick += (_, _) => Blink();
        }
        bool on = S.IslandEyes && idle && !_tucked;
        Eyes.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on == _eyesOn) return;
        _eyesOn = on;
        if (on)
        {
            _eyeLast = default;
            _eyeTimer.Start();
            _blinkTimer.Interval = TimeSpan.FromSeconds(2 + _eyeRnd.NextDouble() * 3);
            _blinkTimer.Start();
        }
        else { _eyeTimer.Stop(); _blinkTimer.Stop(); }
    }

    private void LookAtPointer()
    {
        if (!Eyes.IsVisible) return;
        Native.GetCursorPos(out var p);
        if (p.X == _eyeLast.X && p.Y == _eyeLast.Y) return; // the pointer rests – nothing to redraw
        _eyeLast = p;
        try
        {
            var c = Eyes.PointToScreen(new Point(Eyes.ActualWidth / 2, Eyes.ActualHeight / 2));
            double dx = p.X - c.X, dy = p.Y - c.Y, d = Math.Sqrt(dx * dx + dy * dy);
            // the pupils move at most 1.8 px sideways and 2.6 px up/down (the eye is taller than wide); close by they move less
            double reach = Math.Min(1, d / 60);
            double ox = d < 0.5 ? 0 : dx / d * 1.8 * reach, oy = d < 0.5 ? 0 : dy / d * 2.6 * reach;
            EyeL.X = EyeR.X = ox;
            EyeL.Y = EyeR.Y = oy;
        }
        catch { }
    }

    private void Blink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(3 + _eyeRnd.NextDouble() * 4);
        if (!Eyes.IsVisible || S.Animations == AnimationLevel.Aus) return;
        var close = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(180), FillBehavior = FillBehavior.Stop };
        close.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        close.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromPercent(0.45), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        close.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        EyesBlink.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, close);
    }
}
