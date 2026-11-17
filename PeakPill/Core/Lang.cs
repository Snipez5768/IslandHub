using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace PeakPill.Core;

/// <summary>
/// The island's language (user wish 2026-11-17): English by default, German switchable in Settings and in the
/// installer. The texts stay German in the code; <see cref="T"/> looks the English one up in
/// <see cref="LangEn.Pairs"/>. Texts with live values ("3 Kopien von eben dazu?") are matched as templates with
/// {0}, {1} … holes. Static texts from XAML are translated when their element loads (<see cref="HookXaml"/>).
/// </summary>
public static class L
{
    public static bool English { get; private set; } = true;

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US"), De = CultureInfo.GetCultureInfo("de-DE");
    /// <summary>For dates, weekdays and numbers shown in the island.</summary>
    public static CultureInfo Culture => English ? En : De;

    private static Dictionary<string, string> _exact = new();
    private static List<(Regex Re, string En)> _patterns = new();
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly Regex Hole = new(@"\{(\d+)\}", RegexOptions.Compiled);

    /// <summary>"en" or "de" (anything else counts as English).</summary>
    public static void Init(string language)
    {
        English = !string.Equals(language, "de", StringComparison.OrdinalIgnoreCase);
        _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<(Regex, string, int)>();
        foreach (var (de, en) in LangEn.Pairs.Concat(LangEnSettings.Pairs))
        {
            if (!Hole.IsMatch(de)) { _exact[de] = en; continue; }
            // a template: the fixed parts must match exactly, the holes take anything
            var parts = Hole.Split(de);
            var sb = new StringBuilder("^");
            int literal = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 0) { sb.Append(Regex.Escape(parts[i])); literal += parts[i].Count(char.IsLetter); }
                else sb.Append("(?<h").Append(parts[i]).Append(">.+?)");
            }
            sb.Append('$');
            if (literal == 0) continue; // "{0} · {1}" alone would match everything
            patterns.Add((new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), en, literal));
        }
        _patterns = patterns.OrderByDescending(p => p.Item3).Select(p => (p.Item1, p.Item2)).ToList();
        Cache.Clear();
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    /// <summary>The text in the island's language (German texts in, English out when English is on).</summary>
    public static string T(string s)
    {
        if (!English || string.IsNullOrEmpty(s)) return s;
        if (_exact.TryGetValue(s, out var en)) return en;
        if (Cache.TryGetValue(s, out en)) return en;
        en = s;
        bool letters = false;
        foreach (var c in s) if (char.IsLetter(c)) { letters = true; break; }
        if (letters)
        {
            foreach (var (re, template) in _patterns)
            {
                var m = re.Match(s);
                if (!m.Success) continue;
                en = Hole.Replace(template, h => { var g = m.Groups["h" + h.Groups[1].Value]; return g.Success ? T(g.Value) : h.Value; });
                break;
            }
        }
        if (Cache.Count < 4000) Cache[s] = en;
        return en;
    }

    // ---------- XAML: a window translates its whole element tree (also hidden pages) right after it is built ----------
    // (WPF raises Loaded only on elements with their own handler – a class handler alone never fires, so the tree is
    //  walked instead. Lists that create their rows later are walked again whenever they make new rows.)

    public static void HookXaml() { }

    private static readonly DependencyProperty WatchedProperty =
        DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(L), new PropertyMetadata(false));

    /// <summary>Translates every static text below <paramref name="root"/> (logical and visual tree).</summary>
    public static void TranslateTree(DependencyObject root)
    {
        if (!English || root == null) return;
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (!seen.Add(d)) continue;
            if (d is FrameworkElement fe) Translate(fe);
            if (d is ItemsControl ic && !(bool)ic.GetValue(WatchedProperty))
            {
                // rows made later (Live list, clipboard …): translate them as they appear
                ic.SetValue(WatchedProperty, true);
                ic.ItemContainerGenerator.StatusChanged += (_, _) =>
                {
                    if (ic.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                        ic.Dispatcher.BeginInvoke(new Action(() => TranslateTree(ic)), System.Windows.Threading.DispatcherPriority.Loaded);
                };
            }
            foreach (var child in LogicalTreeHelper.GetChildren(d)) if (child is DependencyObject c) stack.Push(c);
            if (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            // tooltips and context menus are separate trees
            if (d is FrameworkElement f2)
            {
                if (f2.ToolTip is DependencyObject tt) stack.Push(tt);
                if (f2.ContextMenu != null) stack.Push(f2.ContextMenu);
            }
        }
    }

    private static bool Bound(DependencyObject o, DependencyProperty p) => BindingOperations.IsDataBound(o, p);

    private static void Translate(FrameworkElement e)
    {
        if (!English || e == null) return;
        switch (e)
        {
            case TextBlock tb when !Bound(tb, TextBlock.TextProperty):
                if (tb.Inlines.Count == 1 && tb.Inlines.FirstInline is Run only && !Bound(only, Run.TextProperty)) { var t = T(only.Text); if (t != only.Text) only.Text = t; }
                else foreach (var inline in tb.Inlines.ToList()) TranslateInline(inline);
                break;
            case TextBox box when box.Tag is string ph && !Bound(box, FrameworkElement.TagProperty):
                box.Tag = T(ph); // the placeholder
                break;
            case HeaderedItemsControl hi when hi.Header is string h && !Bound(hi, HeaderedItemsControl.HeaderProperty):
                hi.Header = T(h);
                break;
            case HeaderedContentControl hc when hc.Header is string h2 && !Bound(hc, HeaderedContentControl.HeaderProperty):
                hc.Header = T(h2);
                break;
            case ContentControl cc when cc.Content is string c && !Bound(cc, ContentControl.ContentProperty):
                cc.Content = T(c);
                break;
        }
        if (e is Window w && !string.IsNullOrEmpty(w.Title)) w.Title = T(w.Title);
        if (e.ToolTip is string tip && !Bound(e, FrameworkElement.ToolTipProperty)) e.ToolTip = T(tip);
        // Settings' option tiles show their label from Tag ("Leise", "Gruppen" …). Elsewhere Tag is a key (tabs, OBS
        // scenes, the aspect ratio …) and must never change – so only these groups.
        if (e is RadioButton { GroupName: "Fs" or "Shape" or "TabStyle" or "ClipLayout" } rb && rb.Tag is string tag) rb.Tag = T(tag);
    }

    private static void TranslateInline(Inline inline)
    {
        switch (inline)
        {
            case Run r when !Bound(r, Run.TextProperty):
                var t = T(r.Text);
                if (t != r.Text) r.Text = t;
                break;
            case Span sp:
                foreach (var i in sp.Inlines.ToList()) TranslateInline(i);
                break;
        }
    }
}
