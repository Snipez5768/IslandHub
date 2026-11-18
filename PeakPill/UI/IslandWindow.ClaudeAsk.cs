using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// The user's picks from the chat mockups (2026-11-18):
/// C1 – Claude asks you something: the island knocks (wobbles, the star hops) and says "Claude wartet auf dich".
/// C3 – Claude asks for a permission: the island opens with "Erlauben" / "Ablehnen", the click goes back to Claude.
/// A4 – a new title: the island widens, the cover flips over and the words come in one by one.
/// </summary>
public partial class IslandWindow
{
    // ---------- C1: Claude waits for your answer ----------

    private void OnAgentNeedsYou(AgentSession s)
    {
        UpdatePill(true); // Claude takes the island with its sentence
        if (S.Animations == AnimationLevel.Aus) return;
        After(80, () =>
        {
            Wobble();
            // the star knocks twice while the island rings
            After(380, () => Pop(LeadIcon));
            After(720, () => Pop(LeadIcon));
        });
    }

    // ---------- C3: a permission, answered right in the island ----------

    private void OnPermissionAsked(PermissionAsk a)
    {
        var (title, detail) = AgentService.Describe(a);
        var s = a.Session;
        var peek = new PeekInfo
        {
            Key = "agentask",
            Important = true,
            Icon = "asterisk",
            GlyphBrush = AgentBrush,
            Source = string.IsNullOrEmpty(s?.Project) ? "Claude Code" : $"Claude Code · {s.Project}",
            SourceIcon = "asterisk",
            SourceBrush = AgentBrush,
            Title = title,
            Text = detail,
            TimeoutMs = 0,
        };
        _askOf[peek] = a;
        if (a.IsQuestion) Wobble(); // a question: it knocks first (C1), then the answers come
        ShowPeek(peek); // (in the queue like any alert – ShowAskCard shows it in the mockup's look)
    }

    private readonly Dictionary<PeekInfo, PermissionAsk> _askOf = new();
    private bool _askWired;

    private static readonly Brush AskIdle = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A)), AskHover = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3C));

    /// <summary>A button of the card: grey, lighter under the pointer; a click closes the card first, then acts.</summary>
    private Border AskButton(Border b, string text, Action<PermissionAsk> act)
    {
        b ??= new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand,
                           Child = new TextBlock { Foreground = Brushes.White, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } };
        b.Background = AskIdle;
        ((TextBlock)b.Child).Text = text;
        b.MouseEnter += (_, _) => b.Background = AskHover;
        b.MouseLeave += (_, _) => b.Background = AskIdle;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var p = _peek;
            b.Background = AskIdle;
            EndPeek();
            if (p != null && _askOf.Remove(p, out var ask)) act(ask);
        };
        return b;
    }

    /// <summary>C3 as in the mockup: the island grows, the sentence on top, "Erlauben" ("Immer") "Ablehnen" side by side –
    /// or, for Claude's question (9), its answers as buttons.</summary>
    private void ShowAskCard()
    {
        if (!_askWired)
        {
            _askWired = true;
            AskButton(AskAllow, L.T("Erlauben"), a => H.Agents.Decide(a, true));
            AskButton(AskAlways, L.T("Immer"), a => H.Agents.Always(a));
            AskButton(AskDeny, L.T("Ablehnen"), a => H.Agents.Decide(a, false));
            AskOwn.Text = L.T("Eigene Antwort in Claude");
            AskOwn.MouseEnter += (_, _) => AskOwn.Foreground = Brushes.White;
            AskOwn.MouseLeave += (_, _) => AskOwn.Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB2));
            AskOwn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var p = _peek;
                EndPeek();
                if (p != null && _askOf.Remove(p, out var ask)) { H.Agents.HandBack(ask); AgentService.OpenHost(ask.Session); }
            };
        }
        _askOf.TryGetValue(_peek, out var a);
        bool question = a?.IsQuestion == true;
        AskText.Text = _peek.Title;
        AskDetail.Text = _peek.Text;
        AskDetail.Visibility = string.IsNullOrEmpty(_peek.Text) ? Visibility.Collapsed : Visibility.Visible;
        AskButtons.Visibility = question ? Visibility.Collapsed : Visibility.Visible;
        AskOptions.Visibility = AskOwn.Visibility = question ? Visibility.Visible : Visibility.Collapsed;
        // "Immer" only when Claude Code can remember it (10)
        bool always = a?.CanAlways == true;
        AskAlways.Visibility = always ? Visibility.Visible : Visibility.Collapsed;
        AskAlwaysCol.Width = always ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AskAlwaysGap.Width = new GridLength(always ? 6 : 0);
        AskOptions.Children.Clear();
        if (question)
            foreach (var option in a.Options)
            {
                var label = option;
                AskOptions.Children.Add(AskButton(null, label, x => H.Agents.Answer(x, label)));
            }
        _peekTimer.Stop();
        ShowPanel(AskPanel);
        AskPanel.Measure(new Size(AskW - 28, double.PositiveInfinity));
        double h = Math.Max(84, AskPanel.DesiredSize.Height);
        AnimateSize(AskW, h + NotchExtra, 22, true);
        ShowSideBubbles();
        // the buttons come a moment after the island has grown (as in the mockup) – the answers one by one
        if (S.Animations == AnimationLevel.Aus) return;
        if (question)
        {
            int i = 0;
            foreach (FrameworkElement b in AskOptions.Children) RowIn(b, 220 + i++ * 90);
            return;
        }
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440))));
        AskButtons.BeginAnimation(OpacityProperty, fade);
    }


    private const double AskW = 300;

    // ---------- A4: a new title, word by word ----------

    private int _titleRun;
    private double _titleW;
    private const double TitleMaxW = 220;

    /// <summary>The covers flip over (instead of sliding) when the title follows right after.</summary>
    private bool TitleMomentComing => S.TrackPeek && H.Media.IsPlaying && S.Animations != AnimationLevel.Aus;

    /// <summary>The room between the lead and the right side: wide enough for the title while it shows.</summary>
    private double TitleRoom(List<Act> acts)
    {
        bool musicLeads = acts.Count > 0 && acts[0] == Act.Music;
        if (TitleWords.Visibility == Visibility.Visible && !musicLeads)
        {
            _titleRun++; // something else took the island – the title gives way at once
            TitleWords.Visibility = Visibility.Collapsed;
            TitleWords.Children.Clear();
        }
        return TitleWords.Visibility == Visibility.Visible ? Math.Max(112, _titleW + 30) : 112;
    }

    private void TitleMoment()
    {
        if (_lastLead != Act.Music || string.IsNullOrEmpty(H.Media.Title)) return;
        int me = ++_titleRun;
        TitleWords.Children.Clear();
        var grey = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB2));
        var words = H.Media.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => (w, false)).ToList();
        if (!string.IsNullOrEmpty(H.Media.Artist))
        {
            words.Add(("·", true));
            words.AddRange(H.Media.Artist.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => (w, true)));
        }
        double used = 0;
        foreach (var (word, artist) in words)
        {
            var tb = Word(word, artist ? grey : Brushes.White);
            tb.Measure(new Size(double.PositiveInfinity, 40));
            if (used + tb.DesiredSize.Width > TitleMaxW)
            {
                var more = Word("…", grey);
                more.Measure(new Size(double.PositiveInfinity, 40));
                TitleWords.Children.Add(more);
                used += more.DesiredSize.Width;
                break;
            }
            TitleWords.Children.Add(tb);
            used += tb.DesiredSize.Width;
        }
        _titleW = Math.Min(used, TitleMaxW + 14);
        TitleWords.Visibility = Visibility.Visible;
        UpdatePill(true); // the island widens

        int n = TitleWords.Children.Count;
        for (int i = 0; i < n; i++)
        {
            var tb = (TextBlock)TitleWords.Children[i];
            After(200 + i * 140, () =>
            {
                if (me != _titleRun) return;
                var g = (TransformGroup)tb.RenderTransform;
                var d = TimeSpan.FromMilliseconds(320);
                var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
                tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                g.Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, d) { EasingFunction = spring });
                g.Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, d) { EasingFunction = spring });
                g.Children[1].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, d) { EasingFunction = spring });
            });
        }
        // a moment to read it, then the words fade and the island becomes narrow again
        After(200 + n * 140 + 2400, () =>
        {
            if (me != _titleRun) return;
            foreach (UIElement w in TitleWords.Children)
                w.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
            After(240, () =>
            {
                if (me != _titleRun) return;
                TitleWords.Visibility = Visibility.Collapsed;
                TitleWords.Children.Clear();
                UpdatePill(true);
            });
        });
    }

    private static TextBlock Word(string text, Brush color) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = color,
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new TransformGroup { Children = { new ScaleTransform(0.8, 0.8), new TranslateTransform(0, 6) } },
    };

    /// <summary>The cover flips over like a card: the old picture turns away, the new one turns in.</summary>
    private void CoverFlip(ImageSource old)
    {
        _trackPeekNotBefore = DateTime.Now.AddMilliseconds(380);
        var fresh = PillCover.ImageSource;
        PillCover.ImageSource = old;
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LeadCoverScale.ScaleY = 1;
        var away = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        away.Completed += (_, _) =>
        {
            PillCover.ImageSource = fresh;
            var back = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            back.Completed += (_, _) => { LeadCoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); LeadCoverScale.ScaleX = 1; };
            LeadCoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, back);
        };
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, away);
    }
}
