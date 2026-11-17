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

    /// <summary>Green → red and turned down like a receiver put back (0.3 s).</summary>
    private Task TipPhone(FrameworkElement icon)
    {
        if (S.Animations == AnimationLevel.Aus || icon is not SfIcon sf) return Task.CompletedTask;
        _phoneTipping = true;
        After(3000, () => _phoneTipping = false); // safety: never stuck red
        var from = (sf.Foreground as SolidColorBrush)?.Color ?? Colors.White;
        var brush = new SolidColorBrush(from);
        sf.Foreground = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(((SolidColorBrush)FindResource("Danger")).Color, TimeSpan.FromMilliseconds(180)));
        var rt = new RotateTransform();
        var sc = new ScaleTransform();
        sf.RenderTransformOrigin = new Point(0.5, 0.5);
        sf.RenderTransform = new TransformGroup { Children = { sc, rt } };
        rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 135, TimeSpan.FromMilliseconds(280)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
        var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(280), FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromPercent(0.4), new SineEase()));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase()));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        return Task.Delay(320);
    }

    private void HungUpMoment(Border bubble)
    {
        _hungUpUntil = DateTime.Now.AddSeconds(6);
        bool anim = S.Animations != AnimationLevel.Aus;
        var m = bubble == null ? null : bubble == Bubble ? _bm1 : _bm2;
        if (m != null && anim)
        {
            // quick and springy into the island (the usual way out is calmer); it stays visible while it slides under
            // the island's edge instead of fading on the spot
            var (r, d, fr) = (m.Gap.Response, m.Gap.Damping, m.Fade.Response);
            m.Gap.Response = 0.34;
            m.Gap.Damping = 0.5;
            m.Fade.Response = 0.9;
            After(1000, () => { m.Gap.Response = r; m.Gap.Damping = d; m.Fade.Response = fr; });
        }
        UpdatePill(true); // the call leaves the list – its bubble is swallowed by the island
        if (bubble != null && (bubble == Bubble ? BubbleIcon : Bubble2Icon) is SfIcon phone)
        {
            // the rebuild painted the phone green again – it leaves red and tipped
            phone.Kind = "phone";
            phone.Foreground = (Brush)FindResource("Danger");
        }
        After(m != null && anim ? 150 : 0, () =>
        {
            if (anim) Squash(); // the island takes the hit
            ShowHint("phone.down", "Anruf beendet", (Brush)FindResource("Danger"), key: "live");
            if (anim) Pop(HintIcon);
        });
        // the bubble's symbol stands upright again for the next time
        After(1200, () => { _phoneTipping = false; foreach (var i in new FrameworkElement[] { BubbleIcon, Bubble2Icon }) i.RenderTransform = null; UpdatePill(); });
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
