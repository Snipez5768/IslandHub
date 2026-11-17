using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.UI;

/// <summary>
/// Two little eyes at the island's left edge (user's picture 2026-11-16, Settings › Erscheinungsbild). When the pointer
/// comes near, the island grows to the LEFT (the right edge and the song cover stay where they are) and the eyes wake up:
/// they pop out of the new room still shut, open their lids, blink once and look at the pointer. Six seconds after the
/// pointer left they fall asleep – lids droop, then close – and only then shrink away and give the room back.
/// Everything on springs inside the island's own motion frame. Cheap: the pointer is read 15 times a second (numbers
/// only); nothing is redrawn while it rests.
/// </summary>
public partial class IslandWindow
{
    private const double EyesRoom = 30;                     // what the island grows to the left (eyes + a 9 px gap to the cover)
    private static readonly TimeSpan EyesLinger = TimeSpan.FromSeconds(6);

    // wake-up and sleep timing (seconds after ShowEyes / HideEyes)
    private const double PopDelay = 0.11, OpenDelay = 0.26, WakeBlink = 0.95, DroopHold = 0.42;
    private const double ShutSliver = 0.14;                 // closed eyes still show a thin sliver (‿) – the shape is there
    private const double BlinkLength = 0.19;

    private readonly DispatcherTimer _eyeTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly DispatcherTimer _blinkTimer = new();
    private readonly Random _eyeRnd = new();
    // the room: the island's own spring · the eyes: bubbly pop (like a bubble's symbol) · the lids: their own soft spring
    private readonly Spring _eyesExt = new(0, Spring.IslandResponse, Spring.IslandDamping, 0.05);
    private readonly Spring _eyesPop = new(0, 0.40, 0.55, 0.004);
    private readonly Spring _eyesOpen = new(0, 0.34, 0.62, 0.004);
    private Native.POINT _eyeLast;
    private bool _eyesShown, _eyesWired, _pillBaseSet, _wakeBlinked, _wakeLooked;
    private DateTime _eyesUntil, _eyesSince, _blinkAt = DateTime.MinValue;
    private Thickness _pillBase;
    private double _lidShown = -1;

    private double EyesExt => Math.Max(0, _eyesExt.Value);
    private double EyesAge => (DateTime.Now - _eyesSince).TotalSeconds;
    private bool Blinking => (DateTime.Now - _blinkAt).TotalSeconds < BlinkLength;
    // still choreographing: waking up (until the wake blink is over) or falling asleep (until the lids are down)
    private bool EyesBusy => _eyesShown ? EyesAge < WakeBlink + BlinkLength : _eyesPop.Value > 0.01 && EyesAge < DroopHold + 0.05;
    private bool EyesSettled => _eyesExt.Settled && _eyesPop.Settled && _eyesOpen.Settled && !Blinking && !EyesBusy;

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
        // the KI writes an answer: the eyes wake up by themselves and look up, thinking (2026-11-17)
        bool thinking = H.Ai.Busy && S.Animations != AnimationLevel.Aus;
        bool want = can && (DateTime.Now < _eyesUntil || thinking);
        if (want && !_eyesShown) ShowEyes();
        else if (!want && _eyesShown) HideEyes();
        if (!_eyesShown || EyesAge <= OpenDelay + 0.12) return; // the lids are still opening – they look up first
        if (thinking) Ponder();
        else if (moved || _pondering) { _pondering = false; LookAt(p); } // awake: follow the pointer
    }

    private bool _pondering;
    private int _ponderSide = 1;
    private DateTime _ponderAt;

    /// <summary>Up to one side, a pause, up to the other – like someone thinking.</summary>
    private void Ponder()
    {
        if (_pondering && DateTime.Now - _ponderAt < TimeSpan.FromMilliseconds(1500)) return;
        _pondering = true;
        _ponderAt = DateTime.Now;
        _ponderSide = -_ponderSide;
        Glance(1.1 * _ponderSide, -1.9, 650);
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
        bool fresh = _eyesPop.Value < 0.3; // gone (or almost) → the full wake-up; still there → they just open again
        _eyesShown = true;
        _eyesSince = fresh ? DateTime.Now : DateTime.Now.AddSeconds(-WakeBlink - BlinkLength); // no second wake blink
        _wakeBlinked = _wakeLooked = !fresh;
        if (!fresh) LookAt(_eyeLast);
        if (_eyesUntil < DateTime.Now + EyesLinger) _eyesUntil = DateTime.Now + EyesLinger; // they always stay a moment
        if (fresh)
        {
            _eyesOpen.Snap(ShutSliver);
            Glance(0, 1.6, 0); // asleep, looking down
        }
        _eyesExt.Target = EyesRoom;
        if (S.Animations == AnimationLevel.Aus) { _eyesExt.Snap(EyesRoom); _eyesPop.Snap(1); _eyesOpen.Snap(1); _wakeBlinked = true; LookAt(_eyeLast); }
        _blinkTimer.Interval = TimeSpan.FromSeconds(3 + _eyeRnd.NextDouble() * 3);
        _blinkTimer.Start();
        StartMotion();
    }

    private void HideEyes()
    {
        _eyesShown = false;
        _eyesSince = DateTime.Now;
        _blinkTimer.Stop();
        Glance(0, 1.2, 320); // sleepy: the gaze sinks while the lids droop
        if (S.Animations == AnimationLevel.Aus) { _eyesOpen.Snap(0); _eyesPop.Snap(0); _eyesExt.Snap(0); }
        StartMotion();
    }

    /// <summary>One frame of the eyes (from the island's motion frame).</summary>
    private void StepEyes(double dt)
    {
        double age = EyesAge;
        if (_eyesShown)
        {
            // wake up: the room opens · the shut eyes pop out of it · the lids open · one blink · awake
            if (age >= PopDelay) _eyesPop.Target = 1;
            if (age >= OpenDelay) { _eyesOpen.Response = 0.34; _eyesOpen.Damping = 0.62; _eyesOpen.Target = 1; }
            if (!_wakeLooked && age >= OpenDelay + 0.12) { _wakeLooked = true; LookAtPointer(); } // eyes open – find the pointer
            if (!_wakeBlinked && age >= WakeBlink) { _wakeBlinked = true; _blinkAt = DateTime.Now; }
        }
        else
        {
            // fall asleep: the lids droop half-way and rest there a moment, then close; only shut eyes shrink away,
            // and the island takes its room back once they are almost gone
            if (age < DroopHold) { _eyesOpen.Response = 0.55; _eyesOpen.Damping = 1; _eyesOpen.Target = 0.5; }
            else { _eyesOpen.Response = 0.20; _eyesOpen.Damping = 0.9; _eyesOpen.Target = ShutSliver; }
            if (age >= DroopHold && _eyesOpen.Value < ShutSliver + 0.12) _eyesPop.Target = 0;
            if (_eyesPop.Value < 0.35 && _eyesPop.Target == 0) _eyesExt.Target = 0;
        }
        _eyesExt.Step(dt);
        _eyesPop.Step(dt);
        _eyesOpen.Step(dt);

        double pop = Math.Max(0, _eyesPop.Value);
        bool any = _eyesShown || pop > 0.02 || EyesExt > 0.3;
        Eyes.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        // a little stretch as they pop: a touch wider than tall at first, settling round
        EyesPop.ScaleX = Math.Max(0.01, pop * (1 + 0.10 * (1 - Math.Clamp(pop, 0, 1))));
        EyesPop.ScaleY = Math.Max(0.01, pop);
        Eyes.Opacity = Math.Clamp(pop * 1.8, 0, 1);
        SetLids(Math.Min(Math.Clamp(_eyesOpen.Value, 0, 1.2), BlinkLid()));
        ApplyPillPad();
    }

    /// <summary>How far a blink leaves the lids open right now (1 = no blink): quick down, a breath shut, softer up.</summary>
    private double BlinkLid()
    {
        double t = (DateTime.Now - _blinkAt).TotalSeconds;
        if (t < 0 || t >= BlinkLength) return 1.2;
        const double down = 0.06, shut = 0.085, low = ShutSliver; // a thin line stays – vanishing completely looked like a flicker
        if (t < down) { double u = t / down; return 1 - (1 - low) * u * u; }
        if (t < shut) return low;
        double v = (t - shut) / (BlinkLength - shut);
        return low + (1 - low) * (1 - (1 - v) * (1 - v));
    }

    /// <summary>The upper lid: a wide curved shape that slides down over each oval (cut out of it, so it matches every
    /// island theme). <paramref name="open"/> 1 = wide open, 0 = shut.</summary>
    private void SetLids(double open)
    {
        open = Math.Clamp(open, 0, 1);
        if (Math.Abs(open - _lidShown) < 0.004) return;
        _lidShown = open;
        if (open >= 0.999) { EyeBallL.Clip = EyeBallR.Clip = null; return; }
        // a straight lid (the classic sleepy half-moon) – a curved one left little "ears" at the oval's sides
        const double w = 6.5, h = 10.5;
        double bottom = (1 - open) * h;                   // the lid's lower edge: the eye's top … its bottom
        var ball = new EllipseGeometry(new Point(w / 2, h / 2), w / 2, h / 2);
        var lid = new RectangleGeometry(new Rect(-2, -20, w + 4, bottom + 20));
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, ball, lid);
        clip.Freeze();
        EyeBallL.Clip = EyeBallR.Clip = clip;
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
            Glance(ox, oy, 140);
        }
        catch { }
    }

    /// <summary>Moves both pupils – glided over <paramref name="ms"/> (the pointer is read 15× a second; this keeps the
    /// gaze smooth in between), 0 = at once.</summary>
    private void Glance(double x, double y, int ms)
    {
        foreach (var (t, prop, v) in new[] { (EyeL, TranslateTransform.XProperty, x), (EyeR, TranslateTransform.XProperty, x),
                                             (EyeL, TranslateTransform.YProperty, y), (EyeR, TranslateTransform.YProperty, y) })
        {
            if (ms <= 0 || S.Animations == AnimationLevel.Aus) { t.BeginAnimation(prop, null); t.SetValue(prop, v); continue; }
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            t.BeginAnimation(prop, a, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void LookAtPointer() { Native.GetCursorPos(out var p); LookAt(p); }

    private void Blink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(3 + _eyeRnd.NextDouble() * 4);
        if (!_eyesShown || S.Animations == AnimationLevel.Aus || EyesAge < WakeBlink + BlinkLength) return;
        _blinkAt = DateTime.Now;
        StartMotion();
    }
}
