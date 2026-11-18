using System.Windows;
using System.Windows.Controls;
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
        peek.Action(L.T("Ablehnen"), () => H.Agents.Decide(a, false));
        peek.Action(L.T("Erlauben"), () => H.Agents.Decide(a, true), true);
        ShowPeek(peek); // (in the queue like any alert – ShowAskCard shows it in the mockup's look)
    }

    private bool _askWired;

    /// <summary>C3 as in the mockup: the island grows, the sentence on top, "Erlauben" and "Ablehnen" side by side.</summary>
    private void ShowAskCard()
    {
        if (!_askWired)
        {
            _askWired = true;
            ((TextBlock)AskAllow.Child).Text = L.T("Erlauben");
            ((TextBlock)AskDeny.Child).Text = L.T("Ablehnen");
            foreach (var (b, allow) in new[] { (AskAllow, true), (AskDeny, false) })
            {
                var idle = b.Background;
                var hover = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
                b.MouseEnter += (_, _) => b.Background = hover;
                b.MouseLeave += (_, _) => b.Background = idle;
                b.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var p = _peek;
                    b.Background = idle;
                    EndPeek();
                    if (p?.Key == "agentask" && p.Actions.Count == 2) p.Actions[allow ? 1 : 0].Act?.Invoke();
                };
            }
        }
        AskText.Text = _peek.Title;
        AskDetail.Text = _peek.Text;
        AskDetail.Visibility = string.IsNullOrEmpty(_peek.Text) ? Visibility.Collapsed : Visibility.Visible;
        _peekTimer.Stop();
        ShowPanel(AskPanel);
        AskPanel.Measure(new Size(AskW - 28, double.PositiveInfinity));
        double h = Math.Max(84, AskPanel.DesiredSize.Height);
        AnimateSize(AskW, h + NotchExtra, 22, true);
        ShowSideBubbles();
        // the buttons come a moment after the island has grown (as in the mockup)
        if (S.Animations != AnimationLevel.Aus)
        {
            var row = (FrameworkElement)AskAllow.Parent;
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440))));
            row.BeginAnimation(OpacityProperty, fade);
        }
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
