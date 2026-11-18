using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// The workday (user's picks from the chat mockups, 2026-11-18):
/// 1a focus – the timer as a focus session; messages are collected quietly (a small counter) and fan out at the end;
/// 3  this week's focus minutes as bars on the timer's focus side;
/// 4  screen time – which app was in front today, on the "Heute" page;
/// 5a eye break – after 20 minutes at the PC the island's eyes look out of the window for 20 seconds;
/// 6  good morning – the first time at the PC in the morning: weather and open to-dos;
/// 7  evening – at the set time: the day in numbers.
/// </summary>
public partial class IslandWindow
{
    private sealed class FocusRun
    {
        public DateTime Start;
        public int Seconds;
        public readonly List<IslandNotification> Held = new();
    }

    private FocusRun _focusRun;
    private DateTime _focusEndedAt;
    private bool _focusMode, _usageView, _momentWired;

    private static Brush OrangeB => (Brush)Application.Current.FindResource("Orange");
    private static readonly Brush EyeBlue = Frozen(Color.FromRgb(0x64, 0xD2, 0xFF));

    private void WireWorkday()
    {
        // "Fokus" next to the quick picks: the focus side of the timer
        var f = new TextBlock { Text = L.T("Fokus"), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(12, 0, 0, 0), Background = Brushes.Transparent };
        f.SetResourceReference(TextBlock.ForegroundProperty, "Orange");
        f.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetFocusMode(true); };
        TimerPresets.Children.Add(f);
        FocusBack.Text = L.T("Timer");
        FocusBack.MouseEnter += (_, _) => FocusBack.Foreground = Brushes.White;
        FocusBack.MouseLeave += (_, _) => FocusBack.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        FocusBack.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetFocusMode(false); };

        // the "Heute" page: calendar ↔ screen time
        TodaySwitch.MouseEnter += (_, _) => TodaySwitch.Foreground = Brushes.White;
        TodaySwitch.MouseLeave += (_, _) => TodaySwitch.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        TodaySwitch.MouseLeftButtonUp += (_, e) => { e.Handled = true; _usageView = !_usageView; RefreshTodaySide(true); };
        RefreshTodaySide(false);

        // what the evening tells: finished to-dos, Claude's finished tasks
        foreach (var t in H.Todos.Items) WatchTodo(t);
        H.Todos.Items.CollectionChanged += (_, e) => { if (e.NewItems != null) foreach (TodoItem t in e.NewItems) WatchTodo(t); };
        H.Agents.Finished += _ => H.Day.ClaudeDone();

        // eye break, morning and evening look every 5 s (with the screen time)
        H.Usage.Tick += () => Ui.Run(WorkdayTick);
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) Ui.Run(() => After(1500, () => MorningCheck(force: false)));
        };
    }

    private void WatchTodo(TodoItem t)
    {
        t.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TodoItem.Done) && t.Done) H.Day.TodoDone(); };
    }

    // =====================================================================
    //  1a · focus
    // =====================================================================

    private void SetFocusMode(bool on)
    {
        if (_focusMode == on) return;
        _focusMode = on;
        if (on && HourWheel.Value == 0 && MinuteWheel.Value < 5) { MinuteWheel.Value = 25; SecondWheel.Value = 0; }
        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        UpdateTimerUi();
    }

    /// <summary>Called at the end of UpdateTimerUi: the focus side shows "Fokus starten" and the week bars.</summary>
    private void ApplyFocusMode()
    {
        bool f = _focusMode && !_alarmMode;
        FocusWeek.Visibility = f ? Visibility.Visible : Visibility.Collapsed;
        if (!_alarmMode) TimerPresets.Visibility = f ? Visibility.Collapsed : Visibility.Visible;
        if (!f) return;
        TimerStartBtn.Content = L.T("Fokus starten");
        TimerStartBtn.Background = Tint(OrangeB, 0.24);
        TimerStartBtn.Foreground = OrangeB;
        BuildFocusWeek();
    }

    /// <summary>Monday … Sunday: the focus minutes as bars that grow up when the page opens (3).</summary>
    private void BuildFocusWeek()
    {
        var mins = H.Day.FocusWeek();
        double max = Math.Max(60, mins.Max());
        int today = ((int)DateTime.Today.DayOfWeek + 6) % 7;
        var days = Culture().DateTimeFormat.AbbreviatedDayNames; // Sunday first
        FocusBars.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            double v = mins[i];
            var bar = new Border
            {
                Width = 9, Height = Math.Max(2, 22 * v / max), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom,
                Background = v > 0 ? OrangeB : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(1, 1),
                ToolTip = v > 0 ? Hm(v * 60) : null,
            };
            var label = new TextBlock { Text = days[(i + 1) % 7].TrimEnd('.'), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center };
            if (i == today) label.Foreground = Brushes.White; else label.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            var col = new StackPanel();
            col.Children.Add(new Grid { Height = 22, Children = { bar } });
            col.Children.Add(label);
            FocusBars.Children.Add(col);
            if (S.Animations != AnimationLevel.Aus)
            {
                var grow = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
                    { BeginTime = TimeSpan.FromMilliseconds(60 + i * 45), EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
                ((ScaleTransform)bar.RenderTransform).ScaleY = 0;
                ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }
        double week = mins.Sum();
        FocusWeekText.Text = week > 0 ? L.T($"{Hm(week * 60)} diese Woche") : L.T("Noch kein Fokus diese Woche");
    }

    private void StartFocus()
    {
        int seconds = HourWheel.Value * 3600 + MinuteWheel.Value * 60 + SecondWheel.Value;
        if (seconds <= 0) { MinuteWheel.Value = 25; seconds = 25 * 60; }
        H.Timer.Reset();
        H.Timer.SetDurationSeconds(seconds);
        H.Timer.Start();
        _focusRun = new FocusRun { Start = DateTime.Now, Seconds = seconds };
        UpdateTimerUi();
        UpdatePill(true);
        // the island closes – the focus begins
        if (_state == State.Expanded) After(350, () => { if (_state == State.Expanded) Collapse(); });
    }

    /// <summary>The closed island during focus: the moon, "Fokus", the counter of waiting messages.</summary>
    private void FocusLead()
    {
        LeadText.Text = L.T("Fokus");
        LeadText.Foreground = Brushes.White;
        LeadText.Visibility = Visibility.Visible;
        int n = _focusRun?.Held.Count ?? 0;
        if (n == 0) return;
        FocusBadgeText.Text = n.ToString();
        FocusBadge.Visibility = Visibility.Visible;
    }

    private void HoldForFocus(IslandNotification n)
    {
        _focusRun.Held.Add(n);
        UpdateNotifyUi();
        UpdatePill(true);
        if (S.Animations == AnimationLevel.Aus) return;
        // one more: the counter swells once – no bell, no card
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromPercent(0.35), new CubicEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }));
        FocusBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        FocusBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>The focus ends (time is up, or cancelled): its minutes count, the collected messages fan out (1a).</summary>
    private void FocusFinished(bool completed)
    {
        var run = _focusRun;
        if (run == null) return;
        _focusRun = null;
        _focusEndedAt = DateTime.Now;
        double minutes = completed ? run.Seconds / 60.0 : run.Seconds * H.Timer.ProgressPercent / 100 / 60;
        H.Day.AddFocus(minutes);
        if (completed) H.Timer.Reset();
        UpdateTimerUi();
        UpdatePill(true);
        if (!completed && run.Held.Count == 0) return;

        int n = run.Held.Count;
        var p = new PeekInfo
        {
            Key = "moment:focus",
            Important = true,
            Title = n == 0 ? L.T("Fokus geschafft") : n == 1 ? L.T("1 Mitteilung während Fokus") : L.T($"{n} Mitteilungen während Fokus"),
            Meta = Hm(minutes * 60),
            TimeoutMs = n == 0 ? 6000 : 0,
        };
        var blue = (Brush)FindResource("Blue");
        foreach (var m in Enumerable.Reverse(run.Held).Take(3))
            p.Rows.Add(("bell", blue, m.AppName, string.IsNullOrEmpty(m.Title) ? m.Text : m.Title));
        if (n > 3) p.Rows.Add(("plus", (Brush)FindResource("SubText"), n - 3 == 1 ? L.T("1 weitere") : L.T($"{n - 3} weitere"), null));
        if (n == 0) p.Rows.Add(("check", (Brush)FindResource("Green"), L.T("Nichts verpasst"), L.T($"{Hm(minutes * 60)} am Stück")));
        if (n > 0) p.Action(L.T("Ansehen"), () => Expand("Notify", true), true);
        ShowPeek(p);
    }

    // =====================================================================
    //  the moment card (focus done, good morning, evening)
    // =====================================================================

    private const double MomentW = 290;

    private void ShowMomentCard()
    {
        if (!_momentWired)
        {
            _momentWired = true;
            var idle = MomentButton.Background;
            var hover = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
            MomentButton.MouseEnter += (_, _) => MomentButton.Background = hover;
            MomentButton.MouseLeave += (_, _) => MomentButton.Background = idle;
            MomentButton.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var shown = _peek;
                MomentButton.Background = idle;
                EndPeek();
                if (shown?.Actions.Count > 0) shown.Actions[0].Act?.Invoke();
            };
        }
        MomentTitle.Text = _peek.Title;
        MomentMeta.Text = _peek.Meta ?? "";
        MomentRows.Children.Clear();
        int i = 0;
        foreach (var (glyph, color, text, sub) in _peek.Rows)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 1), RenderTransformOrigin = new Point(0, 0.5) };
            row.Children.Add(new SfIcon { Kind = glyph, Size = 11, Foreground = color, Width = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            var tb = new TextBlock { FontSize = 11.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            tb.Inlines.Add(new System.Windows.Documents.Run(text ?? ""));
            if (!string.IsNullOrEmpty(sub)) tb.Inlines.Add(new System.Windows.Documents.Run(" · " + sub) { Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB2)) });
            row.Children.Add(tb);
            MomentRows.Children.Add(row);
            if (S.Animations != AnimationLevel.Aus) RowIn(row, 200 + i * 130);
            i++;
        }
        MomentPic.Source = _peek.Picture;
        MomentPicBox.Visibility = _peek.Picture != null ? Visibility.Visible : Visibility.Collapsed;
        if (_peek.Picture != null && S.Animations != AnimationLevel.Aus) RowIn(MomentPicBox, 200 + i * 130);
        bool button = _peek.Actions.Count > 0;
        MomentButton.Visibility = button ? Visibility.Visible : Visibility.Collapsed;
        if (button)
        {
            MomentButtonText.Text = _peek.Actions[0].Label;
            if (S.Animations != AnimationLevel.Aus) RowIn(MomentButton, 200 + i * 130);
        }
        _peekTimer.Stop();
        ShowPanel(MomentPanel);
        MomentPanel.Measure(new Size(MomentW - 28, double.PositiveInfinity));
        double h = Math.Max(60, MomentPanel.DesiredSize.Height);
        AnimateSize(MomentW, h + NotchExtra, 22, true);
        ShowSideBubbles();
        if (_peek.TimeoutMs > 0)
        {
            _peekTimer.Interval = TimeSpan.FromMilliseconds(_peek.TimeoutMs);
            _peekTimer.Start();
        }
    }

    /// <summary>A row drops in from a little above and settles – one after the other, like cards dealt out.</summary>
    private static void RowIn(FrameworkElement el, int delayMs)
    {
        var move = new TranslateTransform(0, -8);
        var scale = new ScaleTransform(0.92, 0.92);
        el.RenderTransform = new TransformGroup { Children = { scale, move } };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var d = TimeSpan.FromMilliseconds(420);
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(begin + TimeSpan.FromMilliseconds(220))));
        el.BeginAnimation(OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, d) { BeginTime = begin, EasingFunction = spring });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, d) { BeginTime = begin, EasingFunction = spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, d) { BeginTime = begin, EasingFunction = spring });
    }

    // =====================================================================
    //  4 · screen time on the "Heute" page
    // =====================================================================

    private static readonly Brush[] UsageColors =
    {
        Frozen(Color.FromRgb(0x0A, 0x84, 0xFF)), Frozen(Color.FromRgb(0x64, 0xD2, 0xFF)),
        Frozen(Color.FromRgb(0xBF, 0x5A, 0xF2)), Frozen(Color.FromRgb(0x30, 0xD1, 0x58)),
    };

    /// <summary>Calendar or screen time on the right side of "Heute" (called when the page is built, and by the switch).</summary>
    private void RefreshTodaySide(bool animate)
    {
        TodaySwitch.Text = _usageView ? L.T("Kalender") : L.T("Bildschirmzeit");
        TodayCalendar.Visibility = _usageView ? Visibility.Collapsed : Visibility.Visible;
        UsageView.Visibility = _usageView ? Visibility.Visible : Visibility.Collapsed;
        if (!_usageView) return;
        UsageRows.Children.Clear();
        if (!S.ScreenTime)
        {
            UsageHead.Text = L.T("Bildschirmzeit ist aus");
            UsageRows.Children.Add(new TextBlock { Text = L.T("In den Einstellungen einschalten"), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB2)) });
            return;
        }
        var apps = H.Usage.Today();
        double total = apps.Sum(a => a.Seconds);
        UsageHead.Text = L.T($"Heute am PC · {Hm(total)}");
        if (apps.Count == 0) return;
        double max = apps[0].Seconds;
        int i = 0;
        foreach (var (name, secs) in apps.Take(4))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var time = new TextBlock { Text = Hm(secs), FontSize = 10, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            time.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            row.Children.Add(new TextBlock { Text = name, FontSize = 10.5, Foreground = Brushes.White, Width = 70, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var bar = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Width = Math.Max(3, 70 * secs / max), Background = UsageColors[i % UsageColors.Length],
                RenderTransform = new ScaleTransform(1, 1),
            };
            row.Children.Add(bar);
            UsageRows.Children.Add(row);
            if (animate && S.Animations != AnimationLevel.Aus)
                ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { BeginTime = TimeSpan.FromMilliseconds(i * 60), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            i++;
        }
    }

    // =====================================================================
    //  5a · eye break · 6 good morning · 7 evening
    // =====================================================================

    private const int EyeBreakAfter = 20 * 60, EyeBreakSeconds = 20;
    private DateTime _eyeBreakUntil;
    private DispatcherTimer _eyeBreakTimer;

    private bool EyeBreakOn => DateTime.Now < _eyeBreakUntil;

    /// <summary>A quiet moment may show: the island is closed, visible, nothing else is shown, no game, no call.</summary>
    private bool CanMoment(bool forced = false) =>
        _state == State.Collapsed && Island.IsVisible && !_tucked && !_roomAway && _peeks.Count == 0
        && (forced || !H.Game.IsFullscreen && !(H.Privacy.InUse && !JustHungUp));

    private void WorkdayTick()
    {
        if (S.EyeBreak && !EyeBreakOn && H.Usage.ActiveStreak >= EyeBreakAfter && CanMoment()) StartEyeBreak();
        MorningCheck(force: false);
        EveningCheck();
    }

    private void StartEyeBreak()
    {
        H.Usage.ResetStreak();
        _eyeBreakUntil = DateTime.Now.AddSeconds(EyeBreakSeconds);
        if (_eyeBreakTimer == null)
        {
            _eyeBreakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _eyeBreakTimer.Tick += (_, _) =>
            {
                if (EyeBreakOn && _state == State.Collapsed) { UpdatePill(); return; }
                _eyeBreakTimer.Stop();
                _eyeBreakUntil = DateTime.MinValue;
                UpdateEyes();
                UpdatePill(true);
                After(250, Blink); // back: a blink
            };
        }
        _eyeBreakTimer.Start();
        UpdateEyes();       // the eyes come even when they are switched off – this moment is theirs
        _eyesUntil = _eyeBreakUntil;
        UpdatePill(true);
    }

    /// <summary>
    /// The eye break takes the closed island (called by UpdatePill after the lead was built): the sentence, the seconds
    /// left in light blue – the eyes beside it look up and away, out of the window (EyeTick).
    /// </summary>
    private bool PillOverlay()
    {
        if (!EyeBreakOn || _state != State.Collapsed) return false;
        LeadCover.Visibility = LeadIcon.Visibility = Equalizer.Visibility = IdleClock.Visibility = FocusBadge.Visibility = TrailLive.Visibility = Visibility.Collapsed;
        TitleWords.Visibility = Visibility.Collapsed;
        LeadText.Text = L.T("Schau kurz aus dem Fenster");
        LeadText.Foreground = Brushes.White;
        LeadText.Visibility = Visibility.Visible;
        int left = (int)Math.Ceiling((_eyeBreakUntil - DateTime.Now).TotalSeconds);
        ShowTrail(Math.Max(0, left).ToString(), EyeBlue);
        return true;
    }

    private void MorningCheck(bool force)
    {
        if (!S.MorningMoment || H.Day.Today.MorningShown) return;
        var now = DateTime.Now;
        if (!force && (now.Hour < 5 || now.Hour >= 12 || H.Usage.ActiveStreak < 5)) return;
        if (!CanMoment(forced: force)) return;
        H.Day.Today.MorningShown = true;
        H.Day.Save();
        var p = new PeekInfo
        {
            Key = "moment:morning",
            Title = L.T("Guten Morgen"),
            Meta = now.ToString(L.English ? "ddd, MMM d" : "ddd, d. MMM", Culture()),
            TimeoutMs = 7000,
        };
        if (H.Weather.HasData) p.Rows.Add(("sun", OrangeB, $"{H.Weather.Icon} {H.Weather.TempText}", H.Weather.HighLow));
        int open = H.Todos.Items.Count(t => !t.Done);
        p.Rows.Add(("checklist", (Brush)FindResource("Green"), open == 0 ? L.T("Keine Aufgaben offen") : open == 1 ? L.T("1 Aufgabe offen") : L.T($"{open} Aufgaben offen"), null));
        var due = H.Reminders.Items.Where(r => r.Due.Date == DateTime.Today && r.Due > now).OrderBy(r => r.Due).FirstOrDefault();
        if (due != null) p.Rows.Add(("bell", OrangeB, due.Due.ToString("HH:mm"), due.Text));
        ShowPeek(p);
    }

    private void EveningCheck(bool force = false)
    {
        if (!S.EveningMoment || H.Day.Today.EveningShown) return;
        var now = DateTime.Now;
        var at = DateTime.Today.AddMinutes(S.EveningMinutes);
        if (!force && (now < at || now > at.AddHours(3) || H.Usage.ActiveStreak < 5)) return;
        if (!CanMoment(forced: force)) return;
        H.Day.Today.EveningShown = true;
        H.Day.Save();
        var day = H.Day.Today;
        var p = new PeekInfo { Key = "moment:evening", Title = L.T("Feierabend"), Meta = now.ToString("HH:mm"), TimeoutMs = 20000 };
        p.Rows.Add(("moon", OrangeB, day.FocusMinutes >= 1 ? L.T($"{Hm(day.FocusMinutes * 60)} Fokus") : L.T("Heute kein Fokus"), null));
        p.Rows.Add(("check", (Brush)FindResource("Green"), day.TodosDone == 1 ? L.T("1 Aufgabe erledigt") : L.T($"{day.TodosDone} Aufgaben erledigt"), null));
        if (day.ClaudeDone > 0) p.Rows.Add(("asterisk", AgentBrush, day.ClaudeDone == 1 ? L.T("Claude: 1 Aufgabe fertig") : L.T($"Claude: {day.ClaudeDone} Aufgaben fertig"), null));
        if (S.ScreenTime && H.Usage.TodayTotal > 60) p.Rows.Add(("display", (Brush)FindResource("Blue"), L.T($"{Hm(H.Usage.TodayTotal)} am PC"), null));
        p.Action(L.T("Bis morgen"), () => { }, true);
        ShowPeek(p);
    }

    // ---------- small helpers ----------

    private static CultureInfo Culture() => CultureInfo.GetCultureInfo(L.English ? "en-US" : "de-DE");

    /// <summary>"5 h 12" / "42 min" / "40 s" from seconds.</summary>
    private static string Hm(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes:00}";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min";
        return $"{(int)t.TotalSeconds} s";
    }
}
