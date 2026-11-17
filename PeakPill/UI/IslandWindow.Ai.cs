using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// The KI page (user wish 2026-11-17): a chat with Claude, ChatGPT or Gemini on the user's own API key.
/// Questions are blue bubbles on the right, answers grey ones on the left (with light Markdown), the page grows
/// with the chat up to <see cref="AiMaxH"/>, and an answer that arrives while the island is closed comes as a card.
/// </summary>
public partial class IslandWindow
{
    private const double AiMaxH = 340;
    private readonly Dictionary<AiMessage, FrameworkElement> _aiRows = new();
    private readonly HashSet<AiMessage> _aiDirty = new();
    private DispatcherTimer _aiRenderTimer;
    private bool _aiFollow = true, _aiBuilt;

    private static readonly FontFamily AiMono = new("Cascadia Mono, Consolas");
    private static readonly SolidColorBrush AiBubble = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush AiCodeBg = Frozen(Color.FromArgb(0x59, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush AiInlineCodeBg = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush AiFailBg = Frozen(Color.FromArgb(0x24, 0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush AiText = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF7));

    private void WireAi()
    {
        AiInput.AcceptsReturn = true;
        H.Ai.Messages.CollectionChanged += (_, e) => Ui.Run(() => OnAiMessages(e));
        H.Ai.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AiService.Busy)) Ui.Run(UpdateAiSend); };
        H.Ai.Answered += m => Ui.Run(() => OnAiAnswered(m));
        _aiRenderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(45) };
        _aiRenderTimer.Tick += (_, _) => { _aiRenderTimer.Stop(); RenderDirtyAi(); };
        foreach (var m in H.Ai.Messages) Watch(m);
        UpdateAiUi();
    }

    private void Watch(AiMessage m)
    {
        m.PropertyChanged -= AiMessage_Changed;
        m.PropertyChanged += AiMessage_Changed;
    }

    private void AiMessage_Changed(object sender, PropertyChangedEventArgs e) => Ui.Run(() =>
    {
        _aiDirty.Add((AiMessage)sender);
        if (!_aiRenderTimer.IsEnabled) _aiRenderTimer.Start();
    });

    // ---------- the list ----------

    private void OnAiMessages(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null && _aiBuilt)
        {
            foreach (AiMessage m in e.NewItems)
            {
                Watch(m);
                var row = BuildAiRow(m);
                _aiRows[m] = row;
                AiList.Children.Add(row);
                if (S.Animations != AnimationLevel.Aus) AiAppear(row, m.IsUser);
            }
            _aiFollow = true;
        }
        else RebuildAi();
        UpdateAiUi();
        AfterAiLayout();
    }

    private void RebuildAi()
    {
        AiList.Children.Clear();
        _aiRows.Clear();
        foreach (var m in H.Ai.Messages)
        {
            Watch(m);
            var row = BuildAiRow(m);
            _aiRows[m] = row;
            AiList.Children.Add(row);
        }
        _aiBuilt = true;
    }

    private void RenderDirtyAi()
    {
        foreach (var m in _aiDirty)
        {
            if (!_aiRows.TryGetValue(m, out var old)) continue;
            int i = AiList.Children.IndexOf(old);
            if (i < 0) continue;
            var row = BuildAiRow(m);
            _aiRows[m] = row;
            // WPF does not let a child be replaced in place: out first, then the new one at the same spot
            AiList.Children.RemoveAt(i);
            AiList.Children.Insert(i, row);
        }
        _aiDirty.Clear();
        AfterAiLayout();
    }

    /// <summary>Grow the page with the chat and keep the newest line in view (unless the user scrolled up).</summary>
    private void AfterAiLayout()
    {
        UpdateAiHeight();
        if (_aiFollow) Dispatcher.BeginInvoke(new Action(() => AiScroll.ScrollToEnd()), DispatcherPriority.Loaded);
    }

    private void AiScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // only a scroll by the user decides; growing content keeps following
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _aiFollow = AiScroll.VerticalOffset >= AiScroll.ScrollableHeight - 6;
    }

    /// <summary>New line: slides up a little and fades in, questions from the right.</summary>
    private static void AiAppear(FrameworkElement row, bool fromRight)
    {
        var tt = new TranslateTransform(fromRight ? 10 : 0, 8);
        row.RenderTransform = tt;
        row.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(260);
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private FrameworkElement BuildAiRow(AiMessage m)
    {
        if (m.IsUser)
        {
            // the pictures sent with the question sit above its bubble (2026-11-17)
            var q = new StackPanel { Margin = new Thickness(48, 5, 0, 3), HorizontalAlignment = HorizontalAlignment.Right };
            if (m.Images.Count > 0)
            {
                var pics = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, m.Text.Length > 0 ? 4 : 0) };
                foreach (var path in m.Images) if (AiThumb(path, 150, 96) is { } thumb) pics.Children.Add(thumb);
                q.Children.Add(pics);
            }
            if (m.Text.Length > 0)
                q.Children.Add(new Border
                {
                    Background = (Brush)FindResource("Accent"),
                    CornerRadius = new CornerRadius(14, 14, 4, 14),
                    Padding = new Thickness(11, 6, 11, 7),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Brushes.White },
                });
            return q;
        }

        var row = new StackPanel { Margin = new Thickness(0, 3, 30, 3), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent };
        FrameworkElement content = m.IsStreaming && m.Text.Length == 0 ? TypingDots()
            : m.Failed ? AiFailure(m)
            : AiRich(m.Text, AiText);
        row.Children.Add(new Border
        {
            Background = m.Failed ? AiFailBg : AiBubble,
            CornerRadius = new CornerRadius(14, 14, 14, 4),
            Padding = new Thickness(11, 7, 11, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content,
        });
        if (!m.IsStreaming && !m.Failed && !string.IsNullOrEmpty(m.By))
        {
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 0, 0), Height = 18 };
            var who = new TextBlock { Text = m.By, FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center };
            who.SetResourceReference(TextBlock.ForegroundProperty, "FaintText");
            meta.Children.Add(who);
            var copy = new Button { Style = (Style)FindResource("SmallIconButton"), Width = 18, Height = 18, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Antwort kopieren", Opacity = 0 };
            Ext.SetIcon(copy, "copy");
            Ext.SetIconSize(copy, 11);
            copy.Click += (_, _) => AiCopy(copy, m.Text);
            meta.Children.Add(copy);
            row.MouseEnter += (_, _) => copy.Opacity = 1;
            row.MouseLeave += (_, _) => copy.Opacity = 0;
            row.Children.Add(meta);
        }
        return row;
    }

    // ---------- pictures for the chat: pasted (Ctrl+V), dropped, chosen, or from a screenshot ----------

    private readonly List<string> _aiPics = new();

    /// <summary>A rounded picture (read without locking the file).</summary>
    private static FrameworkElement AiThumb(string path, double maxW, double maxH)
    {
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = (int)(maxW * 2);
            bi.EndInit();
            bi.Freeze();
            double scale = Math.Min(maxW / bi.PixelWidth, maxH / bi.PixelHeight);
            return new Border
            {
                Width = Math.Max(24, bi.PixelWidth * scale),
                Height = Math.Max(24, bi.PixelHeight * scale),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(4, 0, 0, 0),
                Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill },
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.5),
            };
        }
        catch { return null; }
    }

    /// <summary>Adds a picture to the next question (it shows above the field, × removes it).</summary>
    public void AiAttach(System.Windows.Media.Imaging.BitmapSource img)
    {
        if (img == null || _aiPics.Count >= 4) return;
        try { _aiPics.Add(AiService.StoreImage(img)); } catch { return; }
        ShowAiPics();
    }

    private void AiAttachFile(string path)
    {
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            AiAttach(bi);
        }
        catch { }
    }

    private void ShowAiPics()
    {
        AiPics.Children.Clear();
        foreach (var path in _aiPics.ToList())
        {
            if (AiThumb(path, 64, 44) is not FrameworkElement thumb) continue;
            var g = new Grid { Margin = new Thickness(0, 0, 6, 0) };
            thumb.Margin = new Thickness(0);
            g.Children.Add(thumb);
            var x = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand, ToolTip = L.T("Entfernen"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1.5),
                Child = new SfIcon { Kind = "xmark", Size = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var p = path;
            x.MouseLeftButtonUp += (_, e) => { e.Handled = true; _aiPics.Remove(p); ShowAiPics(); };
            g.Children.Add(x);
            AiPics.Children.Add(g);
            if (S.Animations != AnimationLevel.Aus && path == _aiPics[^1]) Pop(g);
        }
        AiPicsRow.Visibility = _aiPics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAiSend();
        UpdateAiHeight();
    }

    private void AiPicture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Bilder") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif", Multiselect = true };
        _sticky = true;
        if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames.Take(4)) AiAttachFile(f);
        AiInput.Focus();
    }

    private void TabAi_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files.Where(f => ShellIcon.IsImage(f)).Take(4)) AiAttachFile(f);
        else if (e.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bmp) AiAttach(bmp);
        e.Handled = true;
    }

    /// <summary>From the screenshot: open the KI page with the picture attached, the field ready for a prompt.</summary>
    public void AskAiAbout(System.Windows.Media.Imaging.BitmapSource shot)
    {
        _aiPics.Clear();
        AiAttach(shot);
        Expand("Ai", true);
        Dispatcher.BeginInvoke(new Action(() => { AiInput.Focus(); AiInput.CaretIndex = AiInput.Text.Length; }), DispatcherPriority.Input);
    }

    private void AiCopy(Button b, string text)
    {
        try { Clipboard.SetText(text); } catch { return; }
        Ext.SetIcon(b, "check");
        After(1200, () => Ext.SetIcon(b, "copy"));
    }

    /// <summary>Three dots that breathe one after another until the first word arrives.</summary>
    private static FrameworkElement TypingDots()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Height = 16, Margin = new Thickness(1, 0, 1, 0) };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.White, Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35 };
            if (App.Settings.Animations != AnimationLevel.Aus)
            {
                var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 160), Duration = TimeSpan.FromMilliseconds(1100) };
                a.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                a.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                dot.BeginAnimation(OpacityProperty, a);
                var tt = new TranslateTransform();
                dot.RenderTransform = tt;
                var up = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = a.BeginTime, Duration = a.Duration };
                up.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                up.KeyFrames.Add(new EasingDoubleKeyFrame(-2.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                up.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                tt.BeginAnimation(TranslateTransform.YProperty, up);
            }
            p.Children.Add(dot);
        }
        return p;
    }

    private FrameworkElement AiFailure(AiMessage m)
    {
        var p = new StackPanel();
        var line = new DockPanel();
        var icon = new SfIcon { Kind = "info", Size = 13, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(SfIcon.ForegroundProperty, "Danger");
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        line.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = AiText });
        p.Children.Add(line);
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 4, 0, 0) };
        bool last = H.Ai.Messages.Count > 0 && H.Ai.Messages[^1] == m;
        if (m.Text.Contains("Einstellungen")) acts.Children.Add(AiLink(L.T("Einstellungen öffnen"), () => { Collapse(); SettingsWindow.ShowSingle("Ai"); }));
        else if (last && m.Text != "Abgebrochen.") acts.Children.Add(AiLink(L.T("Nochmal"), () => _ = H.Ai.RetryAsync()));
        if (acts.Children.Count > 0) p.Children.Add(acts);
        return p;
    }

    private TextBlock AiLink(string text, Action act)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 12, 0), Background = Brushes.Transparent };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        t.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        return t;
    }

    // ---------- light Markdown: paragraphs, **bold**, *italic*, `code`, ``` blocks, lists, headings, links ----------

    private static readonly Regex MdInline = new(
        @"\*\*(?<b>.+?)\*\*|`(?<c>[^`]+)`|\[(?<lt>[^\]]+)\]\((?<lu>https?://[^)\s]+)\)|(?<![\w*])\*(?!\s)(?<i>[^*]+?)(?<!\s)\*(?![\w*])|(?<u>https?://[^\s)]+)",
        RegexOptions.Compiled);
    private static readonly Regex MdHeading = new(@"^#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex MdBullet = new(@"^(\s*)([-*•+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);

    private FrameworkElement AiMarkdown(string text, Brush fg)
    {
        var panel = new StackPanel();
        var para = new List<string>();
        var code = new StringBuilder();
        bool inCode = false;

        void Add(FrameworkElement el)
        {
            if (panel.Children.Count > 0) el.Margin = new Thickness(el.Margin.Left, el.Margin.Top + 5, el.Margin.Right, el.Margin.Bottom);
            panel.Children.Add(el);
        }
        void FlushPara()
        {
            if (para.Count == 0) return;
            var tb = MdText(fg);
            for (int i = 0; i < para.Count; i++)
            {
                if (i > 0) tb.Inlines.Add(new LineBreak());
                MdInlines(tb.Inlines, para[i]);
            }
            Add(tb);
            para.Clear();
        }

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                if (inCode) { Add(MdCode(code.ToString().TrimEnd('\n'))); code.Clear(); inCode = false; }
                else { FlushPara(); inCode = true; }
                continue;
            }
            if (inCode) { code.Append(raw).Append('\n'); continue; }
            var t = raw.Trim();
            if (t.Length == 0) { FlushPara(); continue; }
            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                FlushPara();
                Add(new Border { Height = 1, Background = AiBubble, Margin = new Thickness(0, 2, 0, 2) });
                continue;
            }
            var h = MdHeading.Match(t);
            if (h.Success)
            {
                FlushPara();
                var tb = MdText(fg);
                tb.FontWeight = FontWeights.SemiBold;
                tb.FontSize = 13;
                MdInlines(tb.Inlines, h.Groups[1].Value);
                Add(tb);
                continue;
            }
            var b = MdBullet.Match(raw);
            if (b.Success)
            {
                FlushPara();
                var marker = b.Groups[2].Value is "-" or "*" or "+" or "•" ? "•" : b.Groups[2].Value;
                var g = new Grid { Margin = new Thickness(Math.Min(b.Groups[1].Value.Length, 8) * 5, 0, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var mk = MdText(fg);
                mk.Text = marker;
                mk.Margin = new Thickness(2, 0, 7, 0);
                if (marker == "•") mk.Foreground = (Brush)FindResource("SubText");
                var body = MdText(fg);
                MdInlines(body.Inlines, b.Groups[3].Value);
                Grid.SetColumn(body, 1);
                g.Children.Add(mk);
                g.Children.Add(body);
                // list items sit closer together than paragraphs
                if (panel.Children.Count > 0 && panel.Children[^1] is Grid) { g.Margin = new Thickness(g.Margin.Left, 2, 0, 0); panel.Children.Add(g); }
                else Add(g);
                continue;
            }
            if (t.StartsWith('|'))
            {
                // tables: kept as monospace lines (the island is too narrow for a real grid)
                if (Regex.IsMatch(t, @"^\|[\s:|-]+\|$")) continue;
                FlushPara();
                var tb = MdText(fg);
                tb.FontFamily = AiMono;
                tb.FontSize = 11;
                tb.Text = string.Join("  ", t.Trim('|').Split('|').Select(c => c.Trim()));
                if (panel.Children.Count > 0 && panel.Children[^1] is TextBlock prev && prev.FontFamily == AiMono) { tb.Margin = new Thickness(0, 1, 0, 0); panel.Children.Add(tb); }
                else Add(tb);
                continue;
            }
            para.Add(t.StartsWith("> ") ? t[2..] : t);
        }
        if (inCode) Add(MdCode(code.ToString().TrimEnd('\n'))); // still arriving
        FlushPara();
        return panel;
    }

    private static TextBlock MdText(Brush fg) => new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = fg, LineHeight = 17 };

    // ---------- answers you can select (user wish 2026-11-17: copy parts of an answer) ----------

    private const double AiBubbleMax = 318;

    /// <summary>
    /// The answer as a read-only rich text: select with the mouse, Ctrl+C or right-click › Copy. Same light Markdown
    /// as before (paragraphs, bold, italic, `code`, code blocks, lists, headings, links).
    /// </summary>
    private FrameworkElement AiRich(string text, Brush fg)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5, Foreground = fg, LineHeight = 17 };
        var para = new List<string>();
        var code = new StringBuilder();
        bool inCode = false;
        var plain = new StringBuilder(); // for the bubble's width

        Paragraph P(double top = 5) => new() { Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : top, 0, 0) };
        void FlushPara()
        {
            if (para.Count == 0) return;
            var p = P();
            for (int i = 0; i < para.Count; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                MdInlines(p.Inlines, para[i]);
                plain.AppendLine(para[i]);
            }
            doc.Blocks.Add(p);
            para.Clear();
        }
        void CodeBlock(string c)
        {
            var p = P(6);
            p.Background = AiCodeBg;
            p.Padding = new Thickness(8, 6, 8, 6);
            p.FontFamily = AiMono;
            p.FontSize = 11;
            p.LineHeight = 15;
            p.Inlines.Add(new Run(c));
            doc.Blocks.Add(p);
            foreach (var line in c.Split('\n')) plain.AppendLine(line + "   ");
        }

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                if (inCode) { CodeBlock(code.ToString().TrimEnd('\n')); code.Clear(); inCode = false; }
                else { FlushPara(); inCode = true; }
                continue;
            }
            if (inCode) { code.Append(raw).Append('\n'); continue; }
            var t = raw.Trim();
            if (t.Length == 0) { FlushPara(); continue; }
            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$")) { FlushPara(); continue; }
            var h = MdHeading.Match(t);
            if (h.Success)
            {
                FlushPara();
                var p = P(7);
                p.FontWeight = FontWeights.SemiBold;
                p.FontSize = 13;
                MdInlines(p.Inlines, h.Groups[1].Value);
                doc.Blocks.Add(p);
                plain.AppendLine(h.Groups[1].Value);
                continue;
            }
            var b = MdBullet.Match(raw);
            if (b.Success)
            {
                FlushPara();
                bool follows = doc.Blocks.LastBlock is Paragraph { Tag: "li" };
                var p = P(follows ? 2 : 5);
                p.Tag = "li";
                double indent = 14 + Math.Min(b.Groups[1].Value.Length, 8) * 5;
                p.Margin = new Thickness(indent, p.Margin.Top, 0, 0);
                p.TextIndent = -12; // the marker hangs left of the text
                var marker = b.Groups[2].Value is "-" or "*" or "+" or "•" ? "•" : b.Groups[2].Value;
                var mk = new Run(marker + "  ");
                if (marker == "•") mk.Foreground = (Brush)FindResource("SubText");
                p.Inlines.Add(mk);
                MdInlines(p.Inlines, b.Groups[3].Value);
                doc.Blocks.Add(p);
                plain.AppendLine("    " + b.Groups[3].Value);
                continue;
            }
            if (t.StartsWith('|'))
            {
                if (Regex.IsMatch(t, @"^\|[\s:|-]+\|$")) continue;
                FlushPara();
                var row = string.Join("  ", t.Trim('|').Split('|').Select(c => c.Trim()));
                var p = P(doc.Blocks.LastBlock is Paragraph { Tag: "tr" } ? 1 : 5);
                p.Tag = "tr";
                p.FontFamily = AiMono;
                p.FontSize = 11;
                p.Inlines.Add(new Run(row));
                doc.Blocks.Add(p);
                plain.AppendLine(row);
                continue;
            }
            para.Add(t.StartsWith("> ") ? t[2..] : t);
        }
        if (inCode) CodeBlock(code.ToString().TrimEnd('\n')); // still arriving
        FlushPara();

        var box = new RichTextBox
        {
            Document = doc,
            IsReadOnly = true,
            IsDocumentEnabled = true, // links stay clickable
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = fg,
            CaretBrush = Brushes.Transparent,
            SelectionBrush = (Brush)FindResource("Accent"),
            SelectionOpacity = 0.45,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null,
            Cursor = Cursors.IBeam,
            Width = BubbleWidth(plain.ToString()),
        };
        // the chat scrolls, not the answer
        box.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            AiScroll.ScrollToVerticalOffset(AiScroll.VerticalOffset - e.Delta / 3.0);
            _aiFollow = AiScroll.VerticalOffset >= AiScroll.ScrollableHeight - 6;
        };
        return box;
    }

    /// <summary>As wide as the longest line needs (a short answer keeps a short bubble), at most the chat's width.</summary>
    private double BubbleWidth(string plain)
    {
        double widest = 0;
        var face = new Typeface("Segoe UI");
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var line in plain.Split('\n'))
        {
            var clean = Regex.Replace(line, @"\*\*|`|\[([^\]]+)\]\([^)]+\)", "$1").TrimEnd();
            if (clean.Length == 0) continue;
            var ft = new FormattedText(clean, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12.5, Brushes.White, ppd);
            widest = Math.Max(widest, ft.WidthIncludingTrailingWhitespace);
            if (widest >= AiBubbleMax) break;
        }
        return Math.Clamp(Math.Ceiling(widest) + 10, 40, AiBubbleMax);
    }

    private void MdInlines(InlineCollection into, string s)
    {
        int at = 0;
        foreach (Match m in MdInline.Matches(s))
        {
            if (m.Index > at) into.Add(new Run(s[at..m.Index]));
            if (m.Groups["b"].Success) into.Add(new Run(m.Groups["b"].Value) { FontWeight = FontWeights.SemiBold });
            else if (m.Groups["i"].Success) into.Add(new Run(m.Groups["i"].Value) { FontStyle = FontStyles.Italic });
            else if (m.Groups["c"].Success) into.Add(new Run(" " + m.Groups["c"].Value + " ") { FontFamily = AiMono, FontSize = 11.5, Background = AiInlineCodeBg });
            else
            {
                var url = m.Groups["lu"].Success ? m.Groups["lu"].Value : m.Groups["u"].Value;
                var link = new Hyperlink(new Run(m.Groups["lt"].Success ? m.Groups["lt"].Value : url)) { Cursor = Cursors.Hand, TextDecorations = null };
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
                into.Add(link);
            }
            at = m.Index + m.Length;
        }
        if (at < s.Length) into.Add(new Run(s[at..]));
    }

    private FrameworkElement MdCode(string code)
    {
        var g = new Grid();
        var box = new Border
        {
            Background = AiCodeBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 26, 7),
            Child = new TextBlock { Text = code, FontFamily = AiMono, FontSize = 11, Foreground = AiText, TextWrapping = TextWrapping.Wrap },
        };
        g.Children.Add(box);
        var copy = new Button { Style = (Style)FindResource("SmallIconButton"), Width = 20, Height = 20, ToolTip = "Code kopieren", Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 3, 0) };
        Ext.SetIcon(copy, "copy");
        Ext.SetIconSize(copy, 11);
        copy.Click += (_, _) => AiCopy(copy, code);
        g.Children.Add(copy);
        g.MouseEnter += (_, _) => copy.Opacity = 1;
        g.MouseLeave += (_, _) => copy.Opacity = 0;
        return g;
    }

    // ---------- page state, input, height ----------

    private void UpdateAiUi()
    {
        if (AiList == null || H?.Ai == null) return;
        if (!_aiBuilt) RebuildAi();
        var p = S.AiProvider;
        bool key = AiService.HasKey(p), any = H.Ai.Messages.Count > 0;
        AiEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        AiEmptyTitle.Text = key ? L.T($"Frag {AiService.Name(p)} etwas") : L.T("KI-Chat einrichten");
        AiEmptyText.Text = key ? L.T($"{AiService.Label(p)} · Enter schickt ab") : L.T("Mit deinem eigenen Schlüssel für Claude, ChatGPT oder Gemini");
        AiSetupBtn.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        AiInput.Tag = L.T($"Frag {AiService.Name(p)} …");
        AiNewBtn.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateAiSend();
        UpdateAiHeight();
    }

    private void UpdateAiSend()
    {
        if (AiSendIcon == null) return;
        bool busy = H.Ai.Busy;
        AiSendIcon.Kind = busy ? "stop" : "arrow.up";
        AiSendIcon.Size = busy ? 11 : 13;
        AiSendBtn.ToolTip = busy ? L.T("Antwort stoppen (Esc)") : L.T("Senden (Enter)");
        AiSendBtn.Opacity = busy || AiInput.Text.Trim().Length > 0 || _aiPics.Count > 0 ? 1 : 0.35;
        AiSendBtn.Background = busy ? (Brush)FindResource("SubText") : (Brush)FindResource("Accent");
    }

    private double AiHeight()
    {
        if (AiList == null) return ExpH;
        const double Chrome = 7 + 24 + 6 + 10;
        double w = ExpW - 24;
        AiInputBox.Measure(new Size(w, double.PositiveInfinity));
        double h = Chrome + AiInputBox.DesiredSize.Height;
        if (AiPicsRow.Visibility == Visibility.Visible)
        {
            AiPicsRow.Measure(new Size(w, double.PositiveInfinity));
            h += AiPicsRow.DesiredSize.Height;
        }
        if (H.Ai.Messages.Count == 0)
        {
            AiEmpty.Measure(new Size(w, double.PositiveInfinity));
            h += AiEmpty.DesiredSize.Height + 14;
        }
        else
        {
            AiList.Measure(new Size(w - 6, double.PositiveInfinity));
            h += AiList.DesiredSize.Height + 2;
        }
        return Math.Clamp(Math.Ceiling(h), ExpH, AiMaxH);
    }

    private void UpdateAiHeight()
    {
        if (_state != State.Expanded || _tab != "Ai") return;
        double h = TabHeight("Ai") + NotchExtra;
        if (Math.Abs(h - _targetH) > 0.5) AnimateSize(ExpW, h, ExpRadius, false);
    }

    private void AiInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAiSend();
        UpdateAiHeight(); // a second or third line makes the field (and the page) taller
    }

    private void AiInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; AiSend(); }
        // a picture in the clipboard (screenshot, copied photo): Ctrl+V adds it to the question
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage()) { e.Handled = true; AiAttach(Clipboard.GetImage()); }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(ShellIcon.IsImage)) { e.Handled = true; foreach (var f in Clipboard.GetFileDropList().Cast<string>().Where(ShellIcon.IsImage).Take(4)) AiAttachFile(f); }
        else if (e.Key == Key.Escape && H.Ai.Busy) { e.Handled = true; H.Ai.Stop(); }
        else if (e.Key == Key.Up && AiInput.Text.Length == 0 && H.Ai.LastQuestion.Length > 0)
        {
            e.Handled = true;
            AiInput.Text = H.Ai.LastQuestion;
            AiInput.CaretIndex = AiInput.Text.Length;
        }
    }

    private void AiSend_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; AiSend(); }

    private void AiSend()
    {
        if (H.Ai.Busy) { H.Ai.Stop(); return; }
        var text = AiInput.Text.Trim();
        if (text.Length == 0 && _aiPics.Count == 0) return;
        var pics = _aiPics.ToList();
        _aiPics.Clear();
        ShowAiPics();
        AiInput.Clear();
        if (S.Animations != AnimationLevel.Aus)
        {
            var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.78, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }));
            AiSendScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            AiSendScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        _ = H.Ai.SendAsync(text, pics);
        AiInput.Focus();
    }

    private void AiNew_Click(object sender, RoutedEventArgs e)
    {
        H.Ai.NewChat();
        AiInput.Focus();
    }

    private void AiSetup_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        SettingsWindow.ShowSingle("Ai");
    }

    /// <summary>The answer came while the island was closed or on another page: a card with its beginning.</summary>
    private void OnAiAnswered(AiMessage m)
    {
        if (m.Failed || (_state == State.Expanded && _tab == "Ai")) return;
        Note("Ai", 300);
        var first = Regex.Replace(m.Text, @"[*`#>_]", "").Replace('\n', ' ').Trim();
        if (first.Length > 110) first = first[..110].TrimEnd() + " …";
        var name = AiService.Name(S.AiProvider);
        ShowPeek(new PeekInfo
        {
            Icon = "bubble",
            Key = "ai",
            Source = m.By,
            Title = L.T($"{name} hat geantwortet"),
            Text = first,
            TimeoutMs = 7000,
        }.Action(L.T("Lesen"), () => Expand("Ai", true), true));
    }
}
