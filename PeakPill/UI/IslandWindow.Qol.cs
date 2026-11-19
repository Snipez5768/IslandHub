using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;
using Path = System.Windows.Shapes.Path;

namespace PeakPill.UI;

/// <summary>
/// The round of 2026-11-18 (user: "alles umsetzen außer 12, 13"): the island steps back while you type, the timer
/// set by dragging, "Als Notiz", bubbles that answer the pointer, the rim run when Claude is done, the timer's last
/// minute, pause / play, the lead swapping softly, the island rolling up when tucked, and room for "Rückgängig".
/// </summary>
public partial class IslandWindow
{
    private void InitQol()
    {
        WireTypingFade();
        WireTimerDrag();
        foreach (var b in new[] { Bubble, Bubble2 })
        {
            var bb = b;
            bb.MouseEnter += (_, _) => BubbleHover(bb, true);
            bb.MouseLeave += (_, _) => BubbleHover(bb, false);
        }
        // a click into the open page keeps the tab bar on screen for a moment (it faded while you deleted something)
        Pages.PreviewMouseDown += (_, _) => { _headerPinUntil = DateTime.Now.AddSeconds(3); UpdateHeaderFade(); };
        H.Agents.Finished += _ => Ui.Run(() => After(250, () => RimRun((Brush)FindResource("Green"))));
        // the clock in the quiet island: once a second; only the digits that change roll
        _clockTimer.Tick += (_, _) => { if (IdleClock.IsVisible) IdleClock.Text = ClockText(); };
        _clockTimer.Start();
        H.Timer.Completed += () => Ui.Run(() => { if (_focusRun == null && DateTime.Now > _focusEndedAt.AddSeconds(2)) RimRun((Brush)FindResource("Orange"), full: true); }); // (not after focus: its card comes)
    }

    // ---------- the clock (mockup B, 2026-11-18): in the middle of the quiet island, switchable ----------

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private static string ClockText() => DateTime.Now.ToString(S.ShowSeconds ? "HH:mm:ss" : "HH:mm");

    // ---------- 15: while you type in another window the island steps back (half see-through) ----------

    private IntPtr _keyHook;
    private Native.LowLevelKeyboardProc _keyProc;
    private long _lastKeyTicks;
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _typingFaded;

    private void WireTypingFade()
    {
        _keyProc = (code, w, l) =>
        {
            // key down only (0x100 WM_KEYDOWN, 0x104 WM_SYSKEYDOWN) – the time only, nothing of what was typed
            if (code >= 0 && (w == (IntPtr)0x100 || w == (IntPtr)0x104))
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(l);
                bool modifier = k.vkCode is >= 0xA0 and <= 0xA5 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
                bool chord = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0;
                if (!modifier && !chord) Interlocked.Exchange(ref _lastKeyTicks, DateTime.Now.Ticks);
            }
            return Native.CallNextHookEx(_keyHook, code, w, l);
        };
        _typingTimer.Tick += (_, _) => TypingTick();
        App.SettingsChanged += ApplyTypingHook;
        ApplyTypingHook();
    }

    private void ApplyTypingHook()
    {
        bool want = S.FadeWhileTyping;
        if (want && _keyHook == IntPtr.Zero)
        {
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, IntPtr.Zero, 0);
            _typingTimer.Start();
        }
        else if (!want && _keyHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
            _typingTimer.Stop();
            SetTypingFade(false);
        }
    }

    private void TypingTick()
    {
        var last = new DateTime(Interlocked.Read(ref _lastKeyTicks));
        bool typing = DateTime.Now - last < TimeSpan.FromSeconds(1.4);
        bool fade = typing && _state == State.Collapsed && !_tucked && !IsActive && !IsCursorOverIsland(40);
        SetTypingFade(fade);
    }

    private void SetTypingFade(bool on)
    {
        if (on == _typingFaded) return;
        _typingFaded = on;
        if (S.Animations == AnimationLevel.Aus) { Root.BeginAnimation(OpacityProperty, null); Root.Opacity = on ? 0.3 : 1; return; }
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0.3 : 1, TimeSpan.FromMilliseconds(on ? 380 : 160)));
    }

    /// <summary>Something wants attention (opens, a card, a notice): fully there at once.</summary>
    private void TypingFadeOff()
    {
        if (!_typingFaded) return;
        _typingFaded = false;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 1;
    }

    // ---------- 14: the timer is set by dragging down on its page ----------

    private bool _timerDragging;
    private double _timerDragY;
    private int _timerDragStart;

    private void WireTimerDrag()
    {
        TimerSetup.Background ??= Brushes.Transparent; // the empty parts can be grabbed too
        TimerSetup.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // the wheels and buttons keep their own handling
            for (var d = e.OriginalSource as DependencyObject; d != null && d != TimerSetup; d = ParentOf(d))
                if (d is WheelPicker or ButtonBase) return;
            _timerDragging = true;
            _timerDragY = e.GetPosition(TimerSetup).Y;
            _timerDragStart = HourWheel.Value * 60 + MinuteWheel.Value;
            TimerSetup.CaptureMouse();
            TimerSetup.Cursor = Cursors.SizeNS;
            e.Handled = true;
        };
        TimerSetup.PreviewMouseMove += (_, e) =>
        {
            if (!_timerDragging) return;
            // down = more: one minute every 6 px, past an hour five at a time
            double dy = e.GetPosition(TimerSetup).Y - _timerDragY;
            int steps = (int)Math.Round(dy / 6);
            int m = _timerDragStart + steps;
            if (m > 60) m = 60 + (m - 60) * 5;
            m = Math.Clamp(m, 0, 23 * 60 + 59);
            if (HourWheel.Value != m / 60) HourWheel.Value = m / 60;
            if (MinuteWheel.Value != m % 60) MinuteWheel.Value = m % 60;
            if (SecondWheel.Value != 0) SecondWheel.Value = 0;
        };
        TimerSetup.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_timerDragging) return;
            _timerDragging = false;
            TimerSetup.ReleaseMouseCapture();
            TimerSetup.Cursor = null;
        };
    }

    // ---------- 17: a clipboard entry becomes a note ----------

    private void ClipToNote_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is not ClipItem i) return;
        var text = i.Kind == ClipKind.Files && i.Files is { Count: > 0 } ? string.Join("\n", i.Files) : i.Text;
        if (H.Notes.Add(text) == null) return;
        if (sender is FrameworkElement fe) Pop(fe);
        ShowUndo(L.T("Als Notiz gespeichert"), () => { var n = H.Notes.Items.FirstOrDefault(x => x.Text == text?.Trim()); if (n != null) H.Notes.Remove(n); });
    }

    // ---------- 10: bubbles grow a little under the pointer and say what they are ----------

    private void BubbleHover(Border b, bool on)
    {
        if (!_bubbleScale.TryGetValue(b, out var sc) || S.Animations == AnimationLevel.Aus) return;
        var a = new DoubleAnimation(on ? 1.12 : 1, TimeSpan.FromMilliseconds(on ? 260 : 200))
            { EasingFunction = on ? new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut } };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        if (on) b.ToolTip = BubbleTip(b.Tag as Act?);
    }

    private string BubbleTip(Act? a) => a switch
    {
        Act.Timer => $"{L.T("Timer")} · {H.Timer.RemainingText}",
        Act.Agent when H.Agents.Primary is { } ag => $"Claude · {ag.Project}\n{ag.Task}\n{ag.Step}",
        Act.Live => string.Join("\n", H.Privacy.Apps.Select(x => $"{x.Name}: {x.Parts} · {x.Elapsed}")),
        Act.Steam => string.Join(" · ", new[] { Dl.Name, Dl.Percent, Dl.Eta }.Where(x => !string.IsNullOrEmpty(x))),
        Act.Obs => H.Obs.Recording ? $"REC {H.Obs.RecordTime}" : $"LIVE {H.Obs.StreamTime}",
        Act.Alarm => $"{L.T("Wecker")} · {H.Alarm.At:HH:mm}",
        Act.Music => $"{H.Media.Title} – {H.Media.Artist}",
        Act.MicMuted => L.T("Mikro stumm – klicken zum Einschalten"),
        Act.Device when H.Sys.LowDevice is { } d => $"{d.Name} · {d.Level * 100:0} %",
        Act.Print => L.T($"Druckt · {H.Sys.PrintText}"),
        _ => null,
    };

    // ---------- 8 / 9: a light runs once around the rim (Claude done green; the timer's end closes the ring) ----------

    private Path _runRim;

    private void RimRun(Brush color, bool full = false)
    {
        if (S.Animations == AnimationLevel.Aus || _state != State.Collapsed || !Island.IsVisible) return;
        if (_runRim == null)
        {
            _runRim = new Path
            {
                StrokeThickness = 2, StrokeDashCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            Panel.SetZIndex(_runRim, 22);
            IslandContent.Children.Add(_runRim);
        }
        double w = double.IsNaN(Island.Width) ? Island.ActualWidth : Island.Width;
        double h = double.IsNaN(Island.Height) ? Island.ActualHeight : Island.Height;
        var geo = SmoothCorners.Make(w, h, Island.CornerRadius.TopLeft, inset: 1.1);
        double len = Length(geo), t = _runRim.StrokeThickness;
        if (len < 1) return;
        _runRim.Data = geo;
        _runRim.Margin = IslandRim.Margin;
        _runRim.Stroke = color;
        _runRim.Visibility = Visibility.Visible;
        double period = len / t;
        var d = TimeSpan.FromMilliseconds(full ? 700 : 950);
        if (full)
        {
            // the timer's end: the ring closes all the way round, then fades
            _runRim.StrokeDashArray = new DoubleCollection { period, 0.001 };
            _runRim.StrokeDashOffset = 0;
            _runRim.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(1400),
                KeyFrames = { new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)), new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.2)),
                              new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.6)), new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)) },
            });
            return;
        }
        // a short comet once around, from the top centre
        double comet = period * 0.22;
        _runRim.StrokeDashArray = new DoubleCollection { comet, period - comet };
        _runRim.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(comet, comet - period, d) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        _runRim.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            Duration = d,
            KeyFrames = { new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)), new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.12)),
                          new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.8)), new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)) },
        });
    }

    /// <summary>Claude finished: the asterisk spins and lands as the check.</summary>
    private void SpinToCheck()
    {
        if (S.Animations == AnimationLevel.Aus) return;
        var rot = new RotateTransform();
        var sc = new ScaleTransform();
        LeadIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        LeadIcon.RenderTransform = new TransformGroup { Children = { sc, rot } };
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-200, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.4, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromPercent(0.55), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase()));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // ---------- 6 / 7: pause / play and a softly changing lead ----------

    private bool _wasPlaying;
    private Act? _lastLead;
    private string _lastAgentIcon;

    private bool _pausedLook;

    /// <summary>P1: the cover a little smaller with a dark veil and the pause sign – or back to normal.</summary>
    private void SetPausedLook(bool paused, bool anim)
    {
        if (paused == _pausedLook) return;
        _pausedLook = paused;
        double scale = paused ? 0.86 : 1;
        var d = TimeSpan.FromMilliseconds(paused ? 420 : 320);
        IEasingFunction ease = paused ? new CubicEase { EasingMode = EasingMode.EaseOut } : new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        double from = LeadCoverScale.ScaleX;
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LeadCoverScale.ScaleX = LeadCoverScale.ScaleY = scale;
        CoverPause.BeginAnimation(OpacityProperty, null);
        CoverPause.Opacity = paused ? 1 : 0;
        if (!anim) return;
        var a = new DoubleAnimation(from, scale, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        CoverPause.BeginAnimation(OpacityProperty, new DoubleAnimation(paused ? 0 : 1, paused ? 1 : 0, d) { FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Called by UpdatePill once it knows what leads and whether music plays.</summary>
    private void PillMoments(Act? lead, bool playing)
    {
        bool anim = S.Animations != AnimationLevel.Aus && _state == State.Collapsed;
        // paused – "zur Ruhe kommen" (user's pick P1, 2026-11-18): the bars sink to small dots and stay, the cover tips
        // back a little (smaller, darker) and a fine pause sign lies on it; playing again: the bars pop up, the cover returns
        if (playing != _wasPlaying && lead == Act.Music && anim)
        {
            if (!playing)
                foreach (var s in new[] { Eq1, Eq2, Eq3, Eq4, Eq5 })
                    s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s.ScaleY, 0.2, TimeSpan.FromMilliseconds(380))
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            else Pop(Equalizer);
        }
        _wasPlaying = playing;
        bool swapping = _songPending || Math.Abs(LeadCoverShift.X) > 0.01;
        if (!swapping)
        {
            if (LeadCover.Opacity < 0.99 && !LeadCover.HasAnimatedProperties) LeadCover.Opacity = 1; // (the earlier dimming)
            SetPausedLook(lead == Act.Music && !playing, anim);
        }
        // a different thing leads the island now (e.g. the paused cover gives way to the timer): it slides in softly
        if (anim && lead != null && _lastLead != null && lead != _lastLead)
        {
            var tt = new TranslateTransform(10, 0);
            Lead.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
            Lead.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
            Trail.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(60), FillBehavior = FillBehavior.Stop });
        }
        _lastLead = lead;
        // Claude: the asterisk turns into the check
        string agentIcon = lead == Act.Agent && LeadIcon.IsVisible ? LeadIcon.Kind : null;
        if (agentIcon == "check" && _lastAgentIcon == "asterisk" && anim) SpinToCheck();
        _lastAgentIcon = agentIcon;
    }

    // ---------- 11: tucking away rolls the island up into the top edge; it unrolls when it comes back ----------

    private void RollUp()
    {
        if (S.Animations == AnimationLevel.Aus) return;
        _roomShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -18, TimeSpan.FromMilliseconds(240))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop });
    }

    private void RollDown()
    {
        if (S.Animations == AnimationLevel.Aus) return;
        _roomShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-26, 0, TimeSpan.FromMilliseconds(460))
            { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    // ---------- 1: room for "Rückgängig" – the island grows by the bar instead of covering the list ----------

    private bool _undoRoom;
    private const double UndoRoomH = 36;

    private void SetUndoRoom(bool on)
    {
        if (on == _undoRoom) return;
        _undoRoom = on;
        if (_state == State.Expanded) AnimateSize(ExpW, TabHeight(_tab) + NotchExtra, ExpRadius, true);
    }
}
