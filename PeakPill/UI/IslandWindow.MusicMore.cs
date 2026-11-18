using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Music, the second round (user's picks from the chat mockups, 2026-11-18):
/// 15a swipe the closed island – it stretches like a rubber band, let go: next / previous title;
/// 16b the line sung now appears small under the closed island and fades again;
/// 17a a mixer in the control center – every app that makes sound with its own slider;
/// 19  a sleep timer – a small moon counts down, at the end the music fades out and stops;
/// 20  a video in the browser – the time left and a thin line along the bottom of the island;
/// 21b 15 seconds back and forth as quiet extra buttons.
/// </summary>
public partial class IslandWindow
{
    private readonly DispatcherTimer _musicTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly Brush SleepPurple = Frozen(Color.FromRgb(0xBF, 0x5A, 0xF2));

    private void WireMusicMore()
    {
        Back15Btn.ToolTip = L.T("15 Sekunden zurück");
        Fwd15Btn.ToolTip = L.T("15 Sekunden vor");
        SleepBtn.ToolTip = L.T("Schlaftimer");
        MixerBtn.ToolTip = L.T("Lautstärke pro App");
        OutputBtn.ToolTip = L.T("Nächstes Ausgabegerät");
        MixerTitle.Text = L.T("Lautstärke pro App");
        MixerBack.Text = L.T("Zurück");
        MixerBack.MouseEnter += (_, _) => MixerBack.Foreground = Brushes.White;
        MixerBack.MouseLeave += (_, _) => MixerBack.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        MixerBack.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowMixer(false); };

        Island.PreviewMouseLeftButtonDown += SwipeDown;
        Island.PreviewMouseMove += SwipeMove;
        Island.PreviewMouseLeftButtonUp += SwipeUp;

        _musicTick.Tick += (_, _) => MusicTick();
        _musicTick.Start();
        H.Media.TrackChanged += () => Ui.Run(OnTrackForMore);
    }

    private bool MusicLeads => _state == State.Collapsed && _lastLead == Act.Music && H.Media.HasMedia && Island.IsVisible && !_tucked && !EyeBreakOn;

    private void MusicTick()
    {
        bool lead = MusicLeads;
        LyricLineTick(lead);
        VideoTick(lead);
        SleepTick();
    }

    private void OnTrackForMore()
    {
        _lineFor = null;
        _lineLyrics = new();
        _lineIdx = int.MinValue;
        HideLyric();
        if (_sleepSongEnd && _sleepFading) SleepFinish(); // the song ended while it faded out
    }

    // =====================================================================
    //  15a · swipe with a rubber band
    // =====================================================================

    private readonly ScaleTransform _swipeStretch = new();
    private readonly TranslateTransform _swipeShift = new();
    private bool _swipeArmed, _swiping, _swipeReady;
    private double _swipeStartX, _swipeDx;
    private const double SwipeNeed = 40;

    private void SwipeDown(object sender, MouseButtonEventArgs e)
    {
        if (_moveMode || _state != State.Collapsed || _lastLead != Act.Music || !H.Media.HasMedia) return;
        Native.GetCursorPos(out var p);
        _swipeStartX = p.X;
        _swipeArmed = true;
        _swiping = false;
        _swipeDx = 0;
    }

    private void SwipeMove(object sender, MouseEventArgs e)
    {
        if (!_swipeArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _swipeArmed = false; if (_swiping) SwipeRelease(false); return; }
        Native.GetCursorPos(out var p);
        double dx = (p.X - _swipeStartX) / Math.Max(0.1, PxX);
        if (!_swiping)
        {
            if (Math.Abs(dx) < 8) return;
            _swiping = Island.CaptureMouse();
            if (!_swiping) return;
            _hoverTimer.Stop();
            if (!_swipeReady && Island.RenderTransform is TransformGroup g) { g.Children.Add(_swipeStretch); g.Children.Add(_swipeShift); _swipeReady = true; }
        }
        ApplySwipe(dx);
        e.Handled = true;
    }

    private void ApplySwipe(double dx)
    {
        if (!_swipeReady && Island.RenderTransform is TransformGroup g0) { g0.Children.Add(_swipeStretch); g0.Children.Add(_swipeShift); _swipeReady = true; }
        _swipeDx = dx;
        // the further you pull, the harder it gets – like a rubber band
        double pull = Math.Sign(dx) * 34 * (1 - Math.Exp(-Math.Abs(dx) / 70));
        _swipeStretch.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _swipeShift.BeginAnimation(TranslateTransform.XProperty, null);
        _swipeStretch.ScaleX = 1 + Math.Abs(pull) / Math.Max(100, Island.ActualWidth);
        _swipeShift.X = pull / 2;
        bool right = dx > 0;
        SwipeArrow.Kind = right ? "chevron.right" : "chevron.left";
        SwipeArrow.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        SwipeArrow.Margin = right ? new Thickness(0, Island.ActualHeight / 2 - 6.5, 10, 0) : new Thickness(10, Island.ActualHeight / 2 - 6.5, 0, 0);
        SwipeArrow.BeginAnimation(OpacityProperty, null);
        SwipeArrow.Opacity = Math.Min(1, Math.Abs(dx) / SwipeNeed);
    }

    private void SwipeUp(object sender, MouseButtonEventArgs e)
    {
        if (!_swipeArmed) return;
        _swipeArmed = false;
        if (!_swiping) return; // just a click – the island opens as always
        e.Handled = true;
        SwipeRelease(Math.Abs(_swipeDx) >= SwipeNeed);
    }

    private void SwipeRelease(bool go)
    {
        _swiping = false;
        Island.ReleaseMouseCapture();
        if (go) { if (_swipeDx > 0) H.Media.Next(); else H.Media.Previous(); }
        // it snaps back with a little bounce
        var d = TimeSpan.FromMilliseconds(520);
        var ease = new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut };
        double sx = _swipeStretch.ScaleX, x = _swipeShift.X;
        _swipeStretch.ScaleX = 1;
        _swipeShift.X = 0;
        if (S.Animations != AnimationLevel.Aus)
        {
            _swipeStretch.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, 1, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            _swipeShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, 0, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
        double from = SwipeArrow.Opacity;
        SwipeArrow.Opacity = 0;
        SwipeArrow.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(go ? 420 : 200)) { FillBehavior = FillBehavior.Stop });
    }

    // =====================================================================
    //  16b · the line sung now, small under the island
    // =====================================================================

    private List<LyricLine> _lineLyrics = new();
    private string _lineFor;
    private int _lineIdx = int.MinValue;
    private bool _lyricShown;

    private void LyricLineTick(bool lead)
    {
        if (!S.LyricLine || !lead || !H.Media.IsPlaying || IsVideoNow || string.IsNullOrEmpty(H.Media.Title)) { HideLyric(); return; }
        string key = H.Media.Artist + "|" + H.Media.Title;
        if (key != _lineFor) { _ = LoadLineLyricsAsync(key); return; }
        if (_lineLyrics.Count == 0) return;
        int idx = LyricsService.LineAt(_lineLyrics, H.Media.Position);
        if (idx == _lineIdx) return;
        _lineIdx = idx;
        if (idx >= 0 && idx < _lineLyrics.Count) ShowLyric(_lineLyrics[idx].Text);
    }

    private async Task LoadLineLyricsAsync(string key)
    {
        _lineFor = key;
        _lineLyrics = new();
        _lineIdx = int.MinValue;
        var m = H.Media;
        List<LyricLine> lines;
        try { lines = await LyricsService.GetAsync(m.Title, m.Artist, m.Duration); } catch { return; }
        if (key != _lineFor) return;
        _lineLyrics = lines.Where(l => l.At != null && l.Text.Length > 0).ToList(); // only timed text can follow the song
    }

    private void ShowLyric(string text)
    {
        LyricText.Text = text;
        try
        {
            var c = Island.TranslatePoint(new Point(Island.ActualWidth / 2, Island.ActualHeight), Root);
            LyricBubble.Margin = new Thickness(0, c.Y + 6, 0, 0);
            LyricShift.X = c.X - Root.ActualWidth / 2;
        }
        catch { }
        _lyricShown = true;
        if (S.Animations == AnimationLevel.Aus) { LyricBubble.BeginAnimation(OpacityProperty, null); LyricBubble.Opacity = 1; return; }
        // drops in from the island, stays a moment, fades
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(3900) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(LyricBubble.Opacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3300))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3900)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        LyricBubble.BeginAnimation(OpacityProperty, fade);
        LyricShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
    }

    private void HideLyric()
    {
        if (!_lyricShown) return;
        _lyricShown = false;
        _lineIdx = int.MinValue;
        double from = LyricBubble.Opacity;
        LyricBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(180)));
    }

    // =====================================================================
    //  20 · video progress
    // =====================================================================

    private static readonly HashSet<string> VideoServices = new() { "YouTube", "Netflix", "Prime Video", "Disney+" };

    private bool IsVideoNow => H.Media.HasMedia && H.Media.CanSeek && PlayerService() is { } s && VideoServices.Contains(s);

    /// <summary>"−12:40": what is left of the video.</summary>
    private static string VideoLeft() => "−" + Fmt(H.Media.Duration - H.Media.Position);

    private void VideoTick(bool lead)
    {
        bool on = lead && IsVideoNow;
        VideoLine.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        double frac = Math.Clamp(H.Media.Position.TotalSeconds / Math.Max(1, H.Media.Duration.TotalSeconds), 0, 1);
        VideoLine.Width = Math.Max(2, (Island.ActualWidth - 32) * frac);
        VideoLine.Margin = new Thickness(16, Island.ActualHeight - 4, 0, 0);
        if (TrailText.Visibility == Visibility.Visible) TrailText.Text = VideoLeft();
    }

    // =====================================================================
    //  21b · 15 seconds back and forth
    // =====================================================================

    private void Back15_Click(object sender, RoutedEventArgs e) => Jump(-15, Back15Btn);
    private void Fwd15_Click(object sender, RoutedEventArgs e) => Jump(15, Fwd15Btn);

    private void Jump(int seconds, FrameworkElement button)
    {
        if (!H.Media.CanSeek) return;
        var to = H.Media.Position + TimeSpan.FromSeconds(seconds);
        if (to < TimeSpan.Zero) to = TimeSpan.Zero;
        if (to > H.Media.Duration - TimeSpan.FromSeconds(1)) to = H.Media.Duration - TimeSpan.FromSeconds(1);
        H.Media.Seek(to);
        UpdateMusicUi();
        if (S.Animations == AnimationLevel.Aus) return;
        // the arrow turns once in its direction
        var spin = new RotateTransform();
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, seconds < 0 ? -360 : 360, TimeSpan.FromMilliseconds(420))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // =====================================================================
    //  19 · sleep timer
    // =====================================================================

    private DateTime? _sleepAt;
    private bool _sleepSongEnd, _sleepFading;
    private DateTime _fadeStart;
    private double _fadeSeconds, _fadeFrom;
    private AppVolume _sleepVol;

    private bool SleepActive => _sleepAt != null || _sleepSongEnd;

    private void Sleep_Click(object sender, RoutedEventArgs e)
    {
        bool open = SleepChips.Visibility != Visibility.Visible;
        SleepChips.Children.Clear();
        if (open)
        {
            foreach (var (label, minutes) in new[] { ("15", 15), ("30", 30), ("45", 45), ("60 min", 60), (L.T("Songende"), -1), (L.T("Aus"), 0) })
            {
                if (minutes == 0 && !SleepActive) continue;
                bool active = minutes == -1 ? _sleepSongEnd : false;
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand,
                    Background = active ? SleepPurple : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2C)),
                    Child = new TextBlock { Text = label, FontSize = 10.5, Foreground = Brushes.White },
                };
                int m = minutes;
                chip.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; SetSleep(m); };
                SleepChips.Children.Add(chip);
                if (S.Animations != AnimationLevel.Aus) RowIn(chip, SleepChips.Children.Count * 40);
            }
        }
        SleepChips.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>minutes &gt; 0: in that many minutes · -1: at the end of the song · 0: off.</summary>
    private void SetSleep(int minutes)
    {
        SleepChips.Visibility = Visibility.Collapsed;
        if (_sleepFading) SleepRestoreVolume();
        _sleepFading = false;
        _sleepAt = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : null;
        _sleepSongEnd = minutes == -1;
        if (SleepActive) SleepBtn.Foreground = SleepPurple; else SleepBtn.ClearValue(ForegroundProperty);
        if (minutes != 0) ShowHint("moon", minutes > 0 ? L.T($"Musik stoppt in {minutes} min") : L.T("Musik stoppt nach diesem Song"), SleepPurple, key: "sleep");
        UpdatePill(true);
    }

    /// <summary>For the closed island: minutes left, or a note for "end of the song".</summary>
    private string SleepLeftText() =>
        _sleepSongEnd ? "♪" : _sleepAt is { } at ? $"{Math.Max(0, (int)Math.Ceiling((at - DateTime.Now).TotalMinutes))}" : "";

    private void SleepTick()
    {
        if (!SleepActive) return;
        if (SleepBadge.Visibility == Visibility.Visible) SleepBadgeText.Text = SleepLeftText();
        var now = DateTime.Now;
        if (!_sleepFading)
        {
            TimeSpan left = _sleepAt is { } at ? at - now
                          : H.Media.CanSeek ? H.Media.Duration - H.Media.Position : TimeSpan.MaxValue;
            bool fadeNow = _sleepAt != null ? left <= TimeSpan.FromSeconds(30) : H.Media.IsPlaying && left <= TimeSpan.FromSeconds(20);
            if (!fadeNow) return;
            _sleepFading = true;
            _fadeStart = now;
            _fadeSeconds = Math.Max(2, Math.Min(30, left.TotalSeconds));
            _sleepVol = new AppVolume();
            var proc = PlayerProcess(H.Media.AppName);
            if (proc == null || !_sleepVol.Attach(proc)) _sleepVol = null;
            _fadeFrom = _sleepVol?.Volume ?? 1;
            return;
        }
        double frac = (now - _fadeStart).TotalSeconds / _fadeSeconds;
        if (_sleepVol != null) try { _sleepVol.Volume = _fadeFrom * Math.Max(0, 1 - frac); } catch { }
        if (frac >= 1) SleepFinish();
    }

    /// <summary>Faded out: pause, then the volume goes back to where it was (for the next time you press play).</summary>
    private void SleepFinish()
    {
        if (H.Media.IsPlaying) H.Media.PlayPause();
        _sleepFading = false;
        _sleepAt = null;
        _sleepSongEnd = false;
        SleepBtn.ClearValue(ForegroundProperty);
        After(1500, SleepRestoreVolume);
        UpdatePill(true);
    }

    private void SleepRestoreVolume()
    {
        var v = _sleepVol;
        _sleepVol = null;
        if (v == null) return;
        try { v.Volume = _fadeFrom; v.Release(); } catch { }
    }

    // =====================================================================
    //  17a · the mixer in the control center
    // =====================================================================

    private readonly List<AudioSessionControl> _mixerSessions = new();

    private void Mixer_Click(object sender, RoutedEventArgs e) => ShowMixer(true);

    private void ShowMixer(bool on)
    {
        TabControls.Children[0].Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TabControls.Children[1].Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        MixerView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) BuildMixer(); else _mixerSessions.Clear();
        if (S.Animations != AnimationLevel.Aus)
            TabControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void BuildMixer()
    {
        MixerRows.Children.Clear();
        _mixerSessions.Clear();
        var apps = new List<(string Name, List<AudioSessionControl> Sessions, bool Active)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var ss = mgr.Sessions;
            var byPid = new Dictionary<uint, List<AudioSessionControl>>();
            for (int i = 0; i < ss.Count; i++)
            {
                var s = ss[i];
                if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                uint pid = s.GetProcessID;
                if (pid == 0 || pid == Environment.ProcessId) continue;
                if (!byPid.TryGetValue(pid, out var list)) byPid[pid] = list = new();
                list.Add(s);
            }
            foreach (var (pid, list) in byPid)
            {
                string name;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    name = p.ProcessName;
                    try { var d = p.MainModule?.FileVersionInfo.FileDescription; if (!string.IsNullOrWhiteSpace(d) && d.Length <= 30) name = d.Trim(); } catch { }
                }
                catch { continue; }
                bool active = list.Any(s => s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive);
                // one app with several processes (browsers): one row
                int same = apps.FindIndex(a => a.Name == name);
                if (same >= 0) { apps[same].Sessions.AddRange(list); if (active) apps[same] = (name, apps[same].Sessions, true); }
                else apps.Add((name, list, active));
            }
        }
        catch { }
        if (apps.Count == 0)
        {
            var none = new TextBlock { Text = L.T("Gerade macht keine App Ton"), FontSize = 11 };
            none.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            MixerRows.Children.Add(none);
            return;
        }
        int i2 = 0;
        foreach (var (name, sessions, _) in apps.OrderByDescending(a => a.Active).Take(3))
        {
            _mixerSessions.AddRange(sessions);
            double vol = 1;
            try { vol = sessions[0].SimpleAudioVolume.Volume; } catch { }
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Brushes.White, Width = 86, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = vol, VerticalAlignment = VerticalAlignment.Center };
            slider.SetResourceReference(StyleProperty, "ThinSlider");
            var mine = sessions;
            slider.ValueChanged += (_, ev) =>
            {
                foreach (var s in mine) try { s.SimpleAudioVolume.Volume = (float)ev.NewValue; } catch { }
            };
            row.Children.Add(slider);
            MixerRows.Children.Add(row);
            if (S.Animations != AnimationLevel.Aus) RowIn(row, 60 + i2 * 90);
            i2++;
        }
    }
}

/// <summary>18b (user's pick 2026-11-18): one button that steps to the next sound output; the island shows the symbol
/// turning from the old device into the new one, with an arrow to go on to the next.</summary>
public partial class IslandWindow
{
    private void Output_Click(object sender, RoutedEventArgs e) => SwitchOutput();

    private void SwitchOutput()
    {
        H.Devices.Quiet(TimeSpan.FromSeconds(3)); // our own moment – not the "headphones connected" notice as well
        var step = AudioOutputs.SwitchNext();
        if (step is not { } s)
        {
            ShowHint("speaker", L.T("Nur ein Ausgabegerät"), (Brush)FindResource("SubText"), key: "output");
            return;
        }
        if (_state == State.Expanded) { Collapse(); After(320, () => OutputMoment(s.From, s.To)); }
        else OutputMoment(s.From, s.To);
    }

    private void OutputMoment(AudioOutput from, AudioOutput to)
    {
        _hintHoldMs = 2600;
        ShowHint(from.Icon, to.Name, Brushes.White, key: "output",
                 actions: new List<(string, string, Brush, Action)> { ("chevron.right", L.T("Nächstes Gerät"), (Brush)FindResource("Blue"), SwitchOutput) });
        if (from.Icon == to.Icon || S.Animations == AnimationLevel.Aus) { HintIcon.Kind = to.Icon; return; }
        // the old symbol turns away, the new one turns in
        After(240, () =>
        {
            var away = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            away.Completed += (_, _) =>
            {
                HintIcon.Kind = to.Icon;
                HintIconScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } });
            };
            HintIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, away);
        });
    }
}
