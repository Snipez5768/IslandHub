using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandHub.Core;

namespace IslandHub.UI;

/// <summary>
/// Two little eyes in the closed island (user wish 2026-11-16, Settings › Erscheinungsbild): they come out when the
/// pointer comes near the island, sit left of the song cover (the island grows a little for them), look at the
/// pointer, blink now and then, and go away a few seconds after the pointer left. Cheap on purpose: the pointer is read
/// 15 times a second (numbers only); the island redraws only when the pointer really moved while they are out.
/// </summary>
public partial class IslandWindow
{
    private readonly DispatcherTimer _eyeTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly DispatcherTimer _blinkTimer = new();
    private readonly Random _eyeRnd = new();
    private Native.POINT _eyeLast;
    private bool _eyesShown, _eyesWired;
    private DateTime _eyesUntil;
    private static readonly TimeSpan EyesLinger = TimeSpan.FromSeconds(2.5);

    /// <summary>Called by the pill and when the setting changes – the pointer watch runs only while eyes are wanted.</summary>
    private void UpdateEyes(bool _ = false)
    {
        if (!_eyesWired)
        {
            _eyesWired = true;
            _eyeTimer.Tick += (_, _) => EyeTick();
            _blinkTimer.Tick += (_, _) => Blink();
        }
        bool wanted = S.IslandEyes && !_tucked;
        if (wanted && !_eyeTimer.IsEnabled) _eyeTimer.Start();
        if (!wanted) { _eyeTimer.Stop(); if (_eyesShown) HideEyes(); }
    }

    private void EyeTick()
    {
        Native.GetCursorPos(out var p);
        bool moved = p.X != _eyeLast.X || p.Y != _eyeLast.Y;
        _eyeLast = p;
        bool can = _state == State.Collapsed && Island.IsVisible && !_roomAway;
        if (can && moved && PointerNear(p)) _eyesUntil = DateTime.Now + EyesLinger;
        bool want = can && DateTime.Now < _eyesUntil;
        if (want && !_eyesShown) ShowEyes();
        else if (!want && _eyesShown) HideEyes();
        if (_eyesShown && moved) LookAt(p);
    }

    /// <summary>Within ~150 px beside and ~120 px below the island (in island units, so it scales with it).</summary>
    private bool PointerNear(Native.POINT p)
    {
        try
        {
            var tl = Island.PointToScreen(new Point(0, 0));
            double w = Island.ActualWidth * PxX, h = Island.ActualHeight * PxY;
            double mx = 150 * PxX, my = 120 * PxY;
            return p.X >= tl.X - mx && p.X <= tl.X + w + mx && p.Y >= tl.Y - 10 && p.Y <= tl.Y + h + my;
        }
        catch { return false; }
    }

    private void ShowEyes()
    {
        _eyesShown = true;
        if (_eyesUntil < DateTime.Now + EyesLinger) _eyesUntil = DateTime.Now + EyesLinger; // they always stay a moment
        Eyes.Visibility = Visibility.Visible;
        UpdatePill(true); // the island grows a little for them (on its spring)
        LookAt(_eyeLast);
        if (S.Animations != AnimationLevel.Aus)
        {
            // they open: pop up from small, a short moment after the island started to widen
            var pop = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = TimeSpan.FromMilliseconds(90), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            EyesBlink.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);
            Eyes.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(90) });
        }
        _blinkTimer.Interval = TimeSpan.FromSeconds(2 + _eyeRnd.NextDouble() * 3);
        _blinkTimer.Start();
    }

    private void HideEyes()
    {
        _eyesShown = false;
        _blinkTimer.Stop();
        if (S.Animations == AnimationLevel.Aus || !Eyes.IsVisible) { Eyes.Visibility = Visibility.Collapsed; UpdatePill(true); return; }
        // they close, then the island takes its room back
        var close = new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        close.Completed += (_, _) =>
        {
            if (_eyesShown) return; // came back meanwhile
            Eyes.Visibility = Visibility.Collapsed;
            EyesBlink.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            Eyes.BeginAnimation(OpacityProperty, null);
            UpdatePill(true);
        };
        EyesBlink.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, close);
        Eyes.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(60) });
    }

    private void LookAt(Native.POINT p)
    {
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

    private void LookAtPointer() { Native.GetCursorPos(out var p); LookAt(p); }

    private void Blink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(3 + _eyeRnd.NextDouble() * 4);
        if (!_eyesShown || S.Animations == AnimationLevel.Aus) return;
        var close = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(180), FillBehavior = FillBehavior.Stop };
        close.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        close.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromPercent(0.45), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        close.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        EyesBlink.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, close);
    }
}
