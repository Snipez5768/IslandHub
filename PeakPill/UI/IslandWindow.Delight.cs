using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Small moments added 2026-11-17 (user: "setz alle Animationsideen um"):
/// the KI aura around the closed island while an answer is written, a hop of the tab symbol on a page switch,
/// cards that drop into the island and settle, and a mute moment that stays on the microphone.
/// (The thinking eyes live in IslandWindow.Eyes.cs.)
/// </summary>
public partial class IslandWindow
{
    // ---------- KI aura: a calm band of colour turns around the closed island while the KI writes ----------

    private bool _auraOn, _auraWired;

    private void UpdateAura()
    {
        if (AiAura == null || H?.Ai == null) return;
        if (!_auraWired)
        {
            _auraWired = true;
            H.Ai.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AiService.Busy)) Ui.Run(UpdateAura); };
        }
        // only around the closed island (pill, hint, card) – the open KI page has its own dots and stop button
        bool want = H.Ai.Busy && S.Animations != AnimationLevel.Aus && _state != State.Expanded && !_tucked;
        // the same few pixels of rim whatever size the island has (pill or card)
        double w = Math.Max(40, _targetW), h = Math.Max(20, _targetH);
        AiAuraScale.ScaleX = (w + 7) / w;
        AiAuraScale.ScaleY = (h + 7) / h;
        if (want == _auraOn) return;
        _auraOn = want;
        if (want)
        {
            AiAura.Visibility = Visibility.Visible;
            if (S.Animations == AnimationLevel.An)
                AiAuraSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3.6)) { RepeatBehavior = RepeatBehavior.Forever });
            var breathe = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.8) };
            breathe.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(0)));
            breathe.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromPercent(0.5), new SineEase { EasingMode = EasingMode.EaseInOut }));
            breathe.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }));
            var fadeIn = new DoubleAnimation(0, 0.55, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fadeIn.Completed += (_, _) => { if (_auraOn) AiAura.BeginAnimation(OpacityProperty, breathe); };
            AiAura.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fadeOut.Completed += (_, _) =>
            {
                if (_auraOn) return;
                AiAura.Visibility = Visibility.Collapsed;
                AiAuraSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            };
            AiAura.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    // ---------- tab switch: the new tab's symbol hops once ----------

    private static void HopTab(FrameworkElement tab)
    {
        if (tab == null || App.Settings.Animations == AnimationLevel.Aus) return;
        var tt = tab.RenderTransform as TranslateTransform;
        if (tt == null) tab.RenderTransform = tt = new TranslateTransform();
        var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), FillBehavior = FillBehavior.Stop };
        k.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        k.KeyFrames.Add(new EasingDoubleKeyFrame(-3.5, KeyTime.FromPercent(0.32), new CubicEase { EasingMode = EasingMode.EaseOut }));
        k.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
        tt.BeginAnimation(TranslateTransform.YProperty, k);
    }

    // ---------- cards: symbol, text and buttons drop in from above and settle ----------

    private void DropInPeek()
    {
        if (S.Animations == AnimationLevel.Aus) return;
        int i = 0;
        foreach (FrameworkElement e in new FrameworkElement[] { PeekLead, PeekBody, PeekActionsRight })
        {
            if (e.Visibility != Visibility.Visible) continue;
            var tt = new TranslateTransform(0, -14);
            e.RenderTransform = tt;
            var begin = TimeSpan.FromMilliseconds(70 + i * 45);
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(460))
            {
                BeginTime = begin,
                EasingFunction = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut },
            });
            e.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                Duration = begin + TimeSpan.FromMilliseconds(220),
                FillBehavior = FillBehavior.Stop,
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)),
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(begin + TimeSpan.FromMilliseconds(220)), new CubicEase { EasingMode = EasingMode.EaseOut }),
                },
            });
            i++;
        }
    }

    // ---------- hung up (user wish 2026-11-17): the phone turns red and tips down, shoots into the island with a
    //            bounce, then "Anruf beendet" ----------

    private DateTime _hungUpUntil;
    /// <summary>The phone is turning red – the island's regular refresh must not paint it green again.</summary>
    private bool _phoneTipping;

    /// <summary>A few seconds after hanging up Windows still reports Discord on the microphone – the call is over anyway.</summary>
    private bool JustHungUp => DateTime.Now < _hungUpUntil && H.Privacy.Apps.All(a => a.IsDiscord);

    /// <summary>The bubble beside the island that shows the call (null: the call fills the island itself).</summary>
    private Border CallBubble() =>
        new[] { Bubble, Bubble2 }.FirstOrDefault(b => b.Visibility == Visibility.Visible && b.Tag is Act.Live);

    private DateTime _hangUpMomentAt = DateTime.MinValue;
    private bool _wasInVoice, _wasDiscordMic;

    /// <summary>
    /// Notices the end of a Discord call – from the island's button or hung up in Discord itself (user wish
    /// 2026-11-17). Called before the pill is rebuilt; true = the hang-up moment took over (it rebuilds the pill).
    /// </summary>
    private bool WatchCallEnd()
    {
        bool connected = H.Discord.Connected;
        bool inVoice = connected && H.Discord.InVoice;
        bool mic = H.Privacy.Apps.Any(a => a.IsDiscord && a.Mic);
        // with the Discord connection the channel says it at once; without it the microphone (a little later)
        bool ended = connected ? _wasInVoice && !inVoice : _wasDiscordMic && !mic;
        _wasInVoice = inVoice;
        _wasDiscordMic = mic;
        if (!ended || DateTime.Now - _hangUpMomentAt < TimeSpan.FromSeconds(4)) return false;
        _hangUpMomentAt = DateTime.Now;
        _ = HangUpMoment();
        return true;
    }

    /// <summary>
    /// The phone in the call bubble turns red, flies left into the island – quick, with a little bounce – and lands
    /// exactly where the symbol of "Anruf beendet" sits; the words slide in beside it.
    /// </summary>
    private async Task HangUpMoment()
    {
        var danger = (Brush)FindResource("Danger");
        var bubble = CallBubble();
        var icon = bubble == null ? null : bubble == Bubble ? BubbleIcon : Bubble2Icon;
        if (S.Animations == AnimationLevel.Aus || icon == null || _state is State.Expanded or State.Peek)
        {
            _hungUpUntil = DateTime.Now.AddSeconds(6);
            UpdatePill(true);
            ShowHint("phone.down", L.T("Anruf beendet"), danger, key: "live");
            return;
        }

        // 1) red – the receiver goes down
        _phoneTipping = true;
        var brush = new SolidColorBrush((icon.Foreground as SolidColorBrush)?.Color ?? Colors.White);
        icon.Foreground = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(((SolidColorBrush)danger).Color, TimeSpan.FromMilliseconds(150)));
        icon.Kind = "phone.down";
        var swell = new ScaleTransform();
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        icon.RenderTransform = swell;
        var grow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220) };
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.22, KeyTime.FromPercent(0.5), new SineEase()));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromPercent(1), new SineEase()));
        swell.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        swell.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        await Task.Delay(240);

        // 2) the phone leaves its bubble; the empty bubble is swallowed, the island turns into "Anruf beendet"
        Point from;
        try { from = icon.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), Root); }
        catch { from = new Point(Root.ActualWidth / 2, 24); }
        var fly = new SfIcon { Kind = "phone.down", Size = 15, Width = 16, Height = 16, Foreground = danger };
        FlyLayer.Children.Add(fly);
        Canvas.SetLeft(fly, from.X - 8);
        Canvas.SetTop(fly, from.Y - 8);
        icon.Opacity = 0;
        _hungUpUntil = DateTime.Now.AddSeconds(6);
        UpdatePill(true);
        ShowHint("phone.down", L.T("Anruf beendet"), danger, key: "live");
        HintIcon.BeginAnimation(OpacityProperty, null);
        HintIcon.Opacity = 0;
        HintText.Opacity = 0;

        // 3) the flight: two springs follow the (still moving) place of the hint symbol – fast, slightly overshooting
        // a small bounce that stays inside the island (0.52 swung the phone out past its left edge)
        var sx = new Spring(from.X, 0.3, 0.72, 0.15);
        var sy = new Spring(from.Y, 0.3, 0.72, 0.15);
        var start = DateTime.Now;
        var last = start;
        var landed = new TaskCompletionSource<bool>();
        bool textIn = false;
        EventHandler step = null;
        step = (_, _) =>
        {
            var now = DateTime.Now;
            double dt = Math.Min(0.05, (now - last).TotalSeconds);
            last = now;
            try
            {
                var to = HintIcon.TranslatePoint(new Point(HintIcon.ActualWidth / 2, HintIcon.ActualHeight / 2), Root);
                sx.Target = to.X;
                sy.Target = to.Y;
            }
            catch { }
            sx.Step(dt);
            sy.Step(dt);
            Canvas.SetLeft(fly, sx.Value - 8);
            Canvas.SetTop(fly, sy.Value - 8);
            double ms = (now - start).TotalMilliseconds;
            if (!textIn && ms > 160)
            {
                // the words follow the phone in
                textIn = true;
                var tt = new TranslateTransform(-10, 0);
                HintText.RenderTransform = new TransformGroup { Children = { HintTextScale, tt } };
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                HintText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            }
            if ((sx.Settled && sy.Settled && ms > 200) || ms > 1100) landed.TrySetResult(true);
        };
        CompositionTarget.Rendering += step;
        await landed.Task;
        CompositionTarget.Rendering -= step;

        // 4) landed: the real symbol takes over
        FlyLayer.Children.Remove(fly);
        HintIcon.Opacity = 1;
        if (!textIn) HintText.Opacity = 1;
        After(900, () =>
        {
            _phoneTipping = false;
            icon.Opacity = 1;
            icon.RenderTransform = null;
            HintText.RenderTransform = HintTextScale;
            UpdatePill();
        });
    }

    // ---------- muted: the moment stays on the microphone ----------

    /// <summary>
    /// Muted (user wish 2026-11-17): the mic symbol pops and shakes its head, its bubble glows red – but the island
    /// itself never washes red, above all not while it is open.
    /// </summary>
    private void MuteMoment()
    {
        if (S.Animations == AnimationLevel.Aus) return;
        Swell(Act.Live, 1.1);
        Swell(Act.MicMuted, 1.1);
        var red = ((SolidColorBrush)FindResource("Danger")).Color;
        foreach (var (b, icon) in new[] { (Bubble, BubbleIcon), (Bubble2, Bubble2Icon) })
        {
            if (b.Visibility != Visibility.Visible || b.Tag is not (Act.Live or Act.MicMuted)) continue;
            PopShake(icon);
            if (S.Animations == AnimationLevel.An && b.Effect is System.Windows.Media.Effects.DropShadowEffect fx) RedGlow(fx, red);
        }
        if (_state == State.Hint) PopShake(HintIcon);
        // open on the Live page: only the mute button answers
        if (_state == State.Expanded && _tab == "Live") PopShake(LiveSingleMuteBtn);
    }

    /// <summary>Pops in and shakes its head once ("no sound").</summary>
    private static void PopShake(FrameworkElement e)
    {
        if (e == null) return;
        var sc = new ScaleTransform();
        var rt = new RotateTransform();
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = new TransformGroup { Children = { sc, rt } };
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        var shake = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(180), Duration = TimeSpan.FromMilliseconds(460), FillBehavior = FillBehavior.Stop };
        foreach (var (a, ms) in new[] { (0.0, 0), (-13.0, 80), (10.0, 180), (-6.0, 280), (3.0, 370), (0.0, 460) })
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(a, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase()));
        rt.BeginAnimation(RotateTransform.AngleProperty, shake);
    }

    private static void RedGlow(System.Windows.Media.Effects.DropShadowEffect fx, Color c)
    {
        var d = TimeSpan.FromMilliseconds(1100);
        fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, new ColorAnimation(c, c, d) { FillBehavior = FillBehavior.Stop });
        fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(0.9, 0.35, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop });
    }
}
