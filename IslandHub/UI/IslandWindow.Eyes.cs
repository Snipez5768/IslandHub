using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandHub.Core;

namespace IslandHub.UI;

/// <summary>
/// Two little eyes at the island's left edge (user's picture 2026-11-16, Settings › Erscheinungsbild). When the pointer
/// comes near, the island grows to the LEFT (the right edge and the song cover stay where they are) and the eyes pop out
/// like a side bubble is born; they look at the pointer and blink. Six seconds after the pointer left they shrink away
/// first, then the island takes its room back. Both on springs, inside the island's own motion frame.
/// Cheap: the pointer is read 15 times a second (numbers only); nothing is redrawn while it rests.
/// </summary>
public partial class IslandWindow
{
    private const double EyesRoom = 30;                     // what the island grows to the left (eyes + a 9 px gap to the cover)
    private static readonly TimeSpan EyesLinger = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _eyeTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly DispatcherTimer _blinkTimer = new();
    private readonly Random _eyeRnd = new();
    // the room: the island's own spring (a little overshoot, like the island) · the eyes: bubbly pop (like a bubble's symbol)
    private readonly Spring _eyesExt = new(0, Spring.IslandResponse, Spring.IslandDamping, 0.05);
    private readonly Spring _eyesPop = new(0, 0.40, 0.55, 0.004);
    private Native.POINT _eyeLast;
    private bool _eyesShown, _eyesWired, _pillBaseSet;
    private DateTime _eyesUntil, _popAt;
    private Thickness _pillBase;

    private double EyesExt => Math.Max(0, _eyesExt.Value);
    private bool EyesSettled => _eyesExt.Settled && _eyesPop.Settled;

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
        if (S.Animations == AnimationLevel.Aus) { _eyesExt.Snap(EyesRoom); _eyesPop.Snap(1); }
        _eyesExt.Target = EyesRoom;
        _popAt = DateTime.Now.AddMilliseconds(110); // the room opens first, then they pop out of it
        LookAt(_eyeLast);
        _blinkTimer.Interval = TimeSpan.FromSeconds(2.5 + _eyeRnd.NextDouble() * 3);
        _blinkTimer.Start();
        StartMotion();
    }

    private void HideEyes()
    {
        _eyesShown = false;
        _blinkTimer.Stop();
        if (S.Animations == AnimationLevel.Aus) { _eyesPop.Snap(0); _eyesExt.Snap(0); }
        StartMotion();
    }

    /// <summary>One frame of the eyes (from the island's motion frame).</summary>
    private void StepEyes(double dt)
    {
        if (_eyesShown) { if (DateTime.Now >= _popAt) _eyesPop.Target = 1; }
        else
        {
            _eyesPop.Target = 0;
            if (_eyesPop.Value < 0.35) _eyesExt.Target = 0; // the eyes are almost gone – now the island closes the room
        }
        _eyesExt.Step(dt);
        _eyesPop.Step(dt);
        double pop = Math.Max(0, _eyesPop.Value);
        bool any = _eyesShown || pop > 0.02 || EyesExt > 0.3;
        Eyes.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EyesPop.ScaleX = EyesPop.ScaleY = Math.Max(0.01, pop);
        Eyes.Opacity = Math.Clamp(pop * 1.6, 0, 1);
        ApplyPillPad();
    }

    /// <summary>The pill's content moves right by the room the eyes take – so the song cover stays exactly where it was.</summary>
    private void PadPill()
    {
        _pillBase = PillPanel.Margin;
        _pillBaseSet = true;
        ApplyPillPad();
    }

    private void ApplyPillPad()
    {
        if (!_pillBaseSet) { _pillBase = PillPanel.Margin; _pillBaseSet = true; }
        var want = new Thickness(_pillBase.Left + EyesExt, _pillBase.Top, _pillBase.Right, _pillBase.Bottom);
        if (PillPanel.Margin != want) PillPanel.Margin = want;
    }

    private void LookAt(Native.POINT p)
    {
        try
        {
            var c = Eyes.PointToScreen(new Point(Eyes.ActualWidth / 2, Eyes.ActualHeight / 2));
            double dx = p.X - c.X, dy = p.Y - c.Y, d = Math.Sqrt(dx * dx + dy * dy);
            // the pupils move at most 1.3 px sideways and 2 px up/down (the eye is taller than wide); close by they move less
            double reach = Math.Min(1, d / 60);
            double ox = d < 0.5 ? 0 : dx / d * 1.3 * reach, oy = d < 0.5 ? 0 : dy / d * 2.0 * reach;
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
