using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PeakPill.Core;

namespace PeakPill.UI;

/// <summary>
/// Search over every setting (user wish 2026-11-17). Typing in the sidebar replaces the page list with matching rows;
/// a click opens the page, unfolds what the row sits in, scrolls to it and lights it up for a moment.
/// The index is built from the window itself (titles, explanations, "So geht's" steps) – nothing to keep in sync.
/// </summary>
public partial class SettingsWindow
{
    private sealed record SearchHit(string Page, string PageName, string Title, string Text, FrameworkElement Target);

    private List<SearchHit> _searchIndex;

    private (string Tag, string Name, StackPanel Panel)[] Pages() => Nav.Children.OfType<RadioButton>()
        .Select(rb => ((string)rb.Tag, rb.Content as string ?? "", FindName("Page" + rb.Tag) as StackPanel))
        .Where(p => p.Item3 != null).ToArray();

    /// <summary>What a TextBlock shows – the translation swaps its Run, not its Text property.</summary>
    private static string Shown(TextBlock tb)
    {
        static string Of(Inline i) => i switch
        {
            Run r => r.Text,
            Span s => string.Concat(s.Inlines.Select(Of)),
            LineBreak => " ",
            _ => "",
        };
        return tb.Inlines.Count > 0 ? string.Concat(tb.Inlines.Select(Of)) : tb.Text;
    }

    private List<SearchHit> BuildSearchIndex()
    {
        var rowStyles = new[] { TryFindResource("Row"), TryFindResource("LastRow") }.Where(s => s != null).ToList();
        var desc = TryFindResource("Desc");
        var header = TryFindResource("GroupHeader");
        var hits = new Dictionary<FrameworkElement, (string Page, string PageName, string Title, List<string> Texts)>();
        void Add(FrameworkElement target, string page, string pageName, string text, bool canTitle)
        {
            if (!hits.TryGetValue(target, out var h)) h = (page, pageName, null, new List<string>());
            h.Texts.Add(text);
            if (h.Title == null && canTitle) h.Title = text;
            hits[target] = h;
        }
        foreach (var (tag, name, panel) in Pages())
        {
            FrameworkElement lastRow = null;
            void Walk(DependencyObject d, FrameworkElement row, bool inDetails)
            {
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                {
                    var r = row;
                    bool details = inDetails;
                    // a row (Row / LastRow style) – but the steps folded under an app row ("…Details") count for that row
                    if (c is Border b && rowStyles.Contains(b.Style) && !inDetails) r = lastRow = b;
                    if (c is FrameworkElement { Name: { } n } && n.EndsWith("Details") && lastRow != null) { r = lastRow; details = true; }
                    if (c is TextBlock tb)
                    {
                        var text = Shown(tb)?.Trim();
                        bool isDesc = tb.Style == desc || tb.Tag is "Guide";
                        if (!string.IsNullOrEmpty(text) && text != "⋮⋮")
                        {
                            FrameworkElement target = r ?? (tb.Style == header || tb.Style == desc ? tb : null); // not the preview's demo texts
                            if (target != null)
                            {
                                Add(target, tag, name, text, r == null || !isDesc && !details);
                                // the German original as well – a German word finds the row in the English window too
                                if (L.English && !string.IsNullOrEmpty(tb.Text) && tb.Text != text) Add(target, tag, name, tb.Text, false);
                            }
                        }
                    }
                    else if (c is ContentControl { Content: string s } && r != null && s.Length > 1)
                        Add(r, tag, name, s, false); // buttons and choices in a row ("Verbinden", "English") help to find it too
                    Walk(c, r, details);
                }
            }
            Walk(panel, null, false);
        }
        return hits.Where(h => h.Value.Texts.Count > 0)
            .Select(h => new SearchHit(h.Value.Page, h.Value.PageName, h.Value.Title ?? h.Value.Texts[0], string.Join(" ", h.Value.Texts), h.Key))
            .Where(h => h.Title != h.PageName) // the page title itself is the nav entry
            .ToList();
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SettingsSearch.Text.Trim();
        SettingsSearchClear.Visibility = q.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool searching = q.Length > 0;
        Nav.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        SearchResultsScroll.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Children.Clear();
        if (!searching) return;

        _searchIndex ??= BuildSearchIndex();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = _searchIndex
            .Select(h => (Hit: h, Score: Score(h, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(14)
            .ToList();

        if (found.Count == 0)
        {
            SearchResults.Children.Add(new TextBlock
            {
                Text = L.T("Nichts gefunden"), FontSize = 12, Margin = new Thickness(8, 6, 0, 0),
                Foreground = (Brush)FindResource("SubText"),
            });
            return;
        }
        foreach (var (hit, _) in found) SearchResults.Children.Add(ResultRow(hit));
    }

    /// <summary>Every word must appear; in the title it counts more, at its start even more.</summary>
    private static int Score(SearchHit h, string[] words)
    {
        int score = 0;
        foreach (var w in words)
        {
            int inTitle = h.Title.IndexOf(w, StringComparison.CurrentCultureIgnoreCase);
            if (inTitle == 0) score += 6;
            else if (inTitle > 0) score += 4;
            else if (h.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase)) score += 1;
            else if (h.PageName.Contains(w, StringComparison.CurrentCultureIgnoreCase)) score += 1;
            else return 0;
        }
        return score;
    }

    private FrameworkElement ResultRow(SearchHit hit)
    {
        var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = hit.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = hit.PageName, FontSize = 10.5, Foreground = (Brush)FindResource("SubText") });
        b.Child = sp;
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => GoTo(hit);
        return b;
    }

    private void SettingsSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsSearch.Text.Length > 0) { SettingsSearch.Clear(); e.Handled = true; }
        else if (e.Key == Key.Enter && SearchResults.Children.OfType<Border>().FirstOrDefault() is { } first)
        {
            // Enter takes the best match
            first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
            e.Handled = true;
        }
    }

    private void SettingsSearchClear_Click(object sender, RoutedEventArgs e)
    {
        SettingsSearch.Clear();
        SettingsSearch.Focus();
    }

    private void GoTo(SearchHit hit)
    {
        ShowPage(hit.Page);
        // folded parts the row sits in: "Einrichten" panels (…Details) and the tab list open up
        for (var p = LogicalTreeHelper.GetParent(hit.Target) as FrameworkElement; p != null; p = LogicalTreeHelper.GetParent(p) as FrameworkElement)
        {
            if (p.Visibility != Visibility.Collapsed || p.Name.StartsWith("Page")) continue;
            if (FindToggleFor(p.Name) is { } toggle) { toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
            else if (p == TabOrderPanel) TabOrderRow_Click(null, null);
            if (p.Visibility == Visibility.Collapsed) p.Visibility = Visibility.Visible;
        }
        // an app row (Discord, Spotify, OBS): its setup opens too
        foreach (var t in new[] { DiscordExpand, SpotifyExpand, ClaudeExpand, ObsExpand })
            if (IsInside(t, hit.Target) && t.IsChecked != true) { t.IsChecked = true; t.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // a target that stays hidden (e.g. the Minimal-Modus list while it is off): its nearest visible parent
            FrameworkElement t = hit.Target;
            while (t != null && !t.IsVisible) t = LogicalTreeHelper.GetParent(t) as FrameworkElement;
            if (t == null) return;
            var top = t.TransformToAncestor(PageScroll).Transform(new Point(0, 0)).Y;
            PageScroll.ScrollToVerticalOffset(Math.Max(0, PageScroll.VerticalOffset + top - 60));
            Flash(t);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (var p = child; p != null; p = LogicalTreeHelper.GetParent(p))
            if (p == parent) return true;
        return false;
    }

    private ToggleButton FindToggleFor(string panelName) =>
        string.IsNullOrEmpty(panelName) ? null
        : new[] { DiscordExpand, SpotifyExpand, ClaudeExpand, ObsExpand }.FirstOrDefault(t => (string)t.Tag == panelName);

    /// <summary>The found row lights up in the accent colour and fades back.</summary>
    private void Flash(FrameworkElement t)
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(S.Accent); } catch { accent = Colors.DodgerBlue; }
        var glow = new Border
        {
            CornerRadius = new CornerRadius(8), IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)),
        };
        var layer = AdornerLayer.GetAdornerLayer(t);
        if (layer == null) return;
        var ad = new FlashAdorner(t, glow);
        layer.Add(ad);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400)) { BeginTime = TimeSpan.FromMilliseconds(500) };
        fade.Completed += (_, _) => layer.Remove(ad);
        glow.BeginAnimation(OpacityProperty, fade);
    }

    private sealed class FlashAdorner : Adorner
    {
        private readonly Border _child;
        public FlashAdorner(UIElement target, Border child) : base(target) { _child = child; AddVisualChild(child); IsHitTestVisible = false; }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _child;
        protected override Size ArrangeOverride(Size finalSize) { _child.Arrange(new Rect(AdornedElement.RenderSize)); return finalSize; }
    }
}
