using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// The helpers (user's picks from the chat mockups, 2026-11-18):
/// 40a drop a picture on the island → JPG · PNG · smaller · PDF (or into the Ablage);
/// 41a/b a copied link → "QR" under the island, and a QR button on every link in the clipboard list;
/// 42 / 46 Ctrl+Alt+Y calculator, Ctrl+Alt+Space quick search – one line with the best hit, ↓ opens the list;
/// 43 / 44 / 45 after copying: a price or unit converted, "translate?", an address → map, a number → call;
/// 47 a file just saved on the desktop or in Documents – drag it straight out of the island.
/// </summary>
public partial class IslandWindow
{
    private bool _helpersWired;

    private void WireHelpers()
    {
        if (_helpersWired) return;
        _helpersWired = true;
        H.Clipboard.Captured += (item, byUser) => { if (byUser) Ui.Run(() => _ = OfferHelper(item)); };
        HelperBubble.MouseLeftButtonUp += (_, e) => { e.Handled = true; var a = _helperAct; HideHelper(); a?.Invoke(); };
        QueryBox.TextChanged += (_, _) => RefreshQuery();
        QueryBox.PreviewKeyDown += QueryKey;
        Deactivated += (_, _) => { if (_peek?.Key == "query") EndPeek(); };
        WatchSaved();
    }

    // =====================================================================
    //  43 / 44 / 45 / 41a · the small pill after copying
    // =====================================================================

    private Action _helperAct;
    private int _helperGen;

    private async Task OfferHelper(ClipItem item)
    {
        if (!S.CopyHelpers || _state != State.Collapsed || _tucked || _roomAway || !Island.IsVisible) return;
        var (kind, value) = CopyHelpers.Detect(item);
        switch (kind)
        {
            case HelperKind.Link:
                ShowHelper("qr", L.T("QR-Code"), L.T("Zeigen"), () => ShowQr(value));
                break;
            case HelperKind.Phone:
                ShowHelper("phone", value, L.T("Anrufen"), () => Launch("tel:" + Regex.Replace(value, @"[^\d+]", "")));
                break;
            case HelperKind.Address:
                ShowHelper("globe", value, L.T("Karte"), () => Launch("https://www.openstreetmap.org/search?query=" + Uri.EscapeDataString(value)));
                break;
            case HelperKind.Unit:
                if (CopyHelpers.ConvertUnit(value) is { } unit) ShowHelper("arrow.left.right", "≈ " + unit, L.T("Kopieren"), () => CopyQuiet(unit));
                break;
            case HelperKind.Money:
                if (await CopyHelpers.ConvertMoneyAsync(value) is { } money)
                    ShowHelper("arrow.left.right", money, L.T("Kopieren"), () => CopyQuiet(money.TrimStart('≈', ' ')));
                break;
            case HelperKind.Foreign:
                if (AiService.HasKey(S.AiProvider))
                    ShowHelper("globe", L.T("Übersetzen?"), L.T("Ja"), () => AskAi((L.English
                        ? "Translate the following text into English. Reply with the translation only.\n\n"
                        : "Übersetze den folgenden Text ins Deutsche. Antworte nur mit der Übersetzung.\n\n") + value));
                break;
        }
    }

    /// <summary>The second pill drops out of the island, stays 5 s (longer under the pointer), and goes again.</summary>
    private void ShowHelper(string icon, string text, string action, Action act)
    {
        int me = ++_helperGen;
        _helperAct = act;
        HelperIcon.Kind = icon;
        HelperText.Text = text;
        HelperAction.Text = action;
        try
        {
            var c = Island.TranslatePoint(new Point(Island.ActualWidth / 2, Island.ActualHeight), Root);
            HelperBubble.Margin = new Thickness(0, c.Y + 6, 0, 0);
            HelperShift.X = c.X - Root.ActualWidth / 2;
        }
        catch { }
        HelperBubble.Visibility = Visibility.Visible;
        HelperBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        if (S.Animations != AnimationLevel.Aus)
            HelperShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
        void Later() { if (me != _helperGen) return; if (HelperBubble.IsMouseOver) { After(1000, Later); return; } HideHelper(); }
        After(5000, Later);
    }

    private void HideHelper()
    {
        _helperGen++;
        _helperAct = null;
        var fade = new DoubleAnimation(HelperBubble.Opacity, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { if (HelperBubble.Opacity < 0.01) HelperBubble.Visibility = Visibility.Collapsed; };
        HelperBubble.BeginAnimation(OpacityProperty, fade);
    }

    private void CopyQuiet(string text)
    {
        try { Clipboard.SetText(text); ShowHint("check", L.T($"{text} kopiert"), (Brush)FindResource("Green"), check: true); } catch { }
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    /// <summary>The helper button of a clipboard row (41b / 45).</summary>
    private void ClipHelper_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is not ClipItem i) return;
        var (kind, value) = CopyHelpers.Detect(i);
        switch (kind)
        {
            case HelperKind.Link: Collapse(); After(300, () => ShowQr(value)); break;
            case HelperKind.Phone: Launch("tel:" + Regex.Replace(value, @"[^\d+]", "")); break;
            case HelperKind.Address: Launch("https://www.openstreetmap.org/search?query=" + Uri.EscapeDataString(value)); break;
        }
    }

    // =====================================================================
    //  41 · QR code
    // =====================================================================

    private void ShowQr(string url)
    {
        if (string.IsNullOrEmpty(url) || QrImage(url) is not { } img) return;
        string host = url;
        try { host = new Uri(url).Host.Replace("www.", ""); } catch { }
        var p = new PeekInfo { Key = "moment:qr", Title = L.T("Mit dem Handy scannen"), Meta = host, Picture = img, TimeoutMs = 25000 };
        p.Rows.Add(("link", (Brush)FindResource("Blue"), url.Length > 46 ? url[..45] + "…" : url, null));
        ShowPeek(p);
    }

    private static BitmapSource QrImage(string text)
    {
        try
        {
            var hints = new Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 0, [ZXing.EncodeHintType.CHARACTER_SET] = "UTF-8" };
            var m = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 0, 0, hints);
            int n = m.Width, scale = Math.Max(2, 132 / n), size = n * scale;
            var px = new byte[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = m[x / scale, y / scale] ? (byte)0 : (byte)255;
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, px, size);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // =====================================================================
    //  40a · drop a picture: convert it
    // =====================================================================

    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".jxr", ".ico" };

    /// <summary>One picture dropped on the island: offer the formats. False = not a picture (the Ablage takes it).</summary>
    private bool OfferConvert(string[] files)
    {
        if (files is not { Length: 1 } || !File.Exists(files[0]) || !Pictures.Contains(Path.GetExtension(files[0]))) return false;
        var file = files[0];
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var p = new PeekInfo
        {
            Key = "convert", Important = true, Icon = "photo", GlyphBrush = (Brush)FindResource("Blue"),
            Source = L.T("Umwandeln"), SourceIcon = "arrow.left.right", SourceBrush = (Brush)FindResource("Blue"),
            Title = Path.GetFileName(file), Text = SizeText(new FileInfo(file).Length), TimeoutMs = 14000,
        };
        if (ext is not ".jpg" and not ".jpeg") p.Action("JPG", () => _ = ConvertPicture(file, "jpg"));
        if (ext != ".png") p.Action("PNG", () => _ = ConvertPicture(file, "png"));
        p.Action(L.T("Kleiner"), () => _ = ConvertPicture(file, "small"));
        p.Action("PDF", () => _ = ConvertPicture(file, "pdf"));
        p.Action(L.T("Ablage"), () => H.Shelf.Add(new[] { file }), true);
        if (_state == State.Expanded) { Collapse(); After(320, () => ShowPeek(p)); } else ShowPeek(p);
        return true;
    }

    private static string SizeText(long b) =>
        b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    private async Task ConvertPicture(string file, string to)
    {
        ShowHint("arrow.left.right", L.T("Wandelt um …"), (Brush)FindResource("Blue"), key: "convert");
        string result = null;
        try { result = await Task.Run(() => ConvertFile(file, to)); } catch (Exception ex) { App.Log(ex); }
        if (result == null) { ShowHint("xmark", L.T("Ging nicht"), (Brush)FindResource("Danger"), key: "convert"); return; }
        H.Shelf.Add(new[] { result });
        ShowHint("check", Path.GetFileName(result), (Brush)FindResource("Green"), check: true, key: "convert");
    }

    private static string ConvertFile(string file, string to)
    {
        var dec = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];
        if (to == "small")
        {
            double s = Math.Min(1, 1600.0 / Math.Max(src.PixelWidth, src.PixelHeight));
            if (s < 1) src = new TransformedBitmap(src, new ScaleTransform(s, s));
        }
        if (to != "png" && src.Format != PixelFormats.Bgr24 && src.Format != PixelFormats.Bgr32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0); // JPEG has no transparency
        string name = Path.GetFileNameWithoutExtension(file) + (to == "small" ? "-klein" : "");
        string ext = to switch { "png" => ".png", "pdf" => ".pdf", _ => ".jpg" };
        string dir = Path.GetDirectoryName(file);
        string target = Unique(dir, name, ext);
        try { File.WriteAllBytes(target, Encode(src, to)); }
        catch (UnauthorizedAccessException)
        {
            dir = App.Settings.ScreenshotFolder;
            Directory.CreateDirectory(dir);
            target = Unique(dir, name, ext);
            File.WriteAllBytes(target, Encode(src, to));
        }
        return target;
    }

    private static byte[] Encode(BitmapSource src, string to)
    {
        BitmapEncoder enc = to == "png" ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = to == "small" ? 80 : 92 };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return to == "pdf" ? Pdf(ms.ToArray(), src.PixelWidth, src.PixelHeight) : ms.ToArray();
    }

    private static string Unique(string dir, string name, string ext)
    {
        string path = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }

    /// <summary>A one-page PDF with the JPEG on it, the page as big as the picture.</summary>
    private static byte[] Pdf(byte[] jpeg, int w, int h)
    {
        double pw = w * 0.75, ph = h * 0.75;
        var inv = CultureInfo.InvariantCulture;
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.4\n");
        offsets.Add(ms.Position); Write("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        offsets.Add(ms.Position); Write("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        offsets.Add(ms.Position); Write(string.Format(inv, "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 {0:0.##} {1:0.##}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >> endobj\n", pw, ph));
        offsets.Add(ms.Position); Write($"4 0 obj << /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        ms.Write(jpeg, 0, jpeg.Length); Write("\nendstream endobj\n");
        string content = string.Format(inv, "q {0:0.##} 0 0 {1:0.##} 0 0 cm /Im0 Do Q", pw, ph);
        offsets.Add(ms.Position); Write($"5 0 obj << /Length {content.Length} >>\nstream\n{content}\nendstream endobj\n");
        long xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:0000000000} 00000 n \n");
        Write($"trailer << /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // =====================================================================
    //  42 / 46 · calculator and quick search
    // =====================================================================

    private sealed record Hit(string Icon, string Text, string Kind, Action Run);
    private List<Hit> _hits = new();
    private int _hitIdx;
    private bool _queryOpen;
    private const double QueryW = 320;
    private static List<(string Name, string Path)> _apps;

    private void OpenQuery(string start)
    {
        WireHelpers();
        if (_state == State.Peek && _peek?.Key == "query") { QueryBox.Focus(); return; }
        if (_state == State.Expanded) Collapse();
        _queryStart = start;
        ShowPeek(new PeekInfo { Key = "query", Important = true, TimeoutMs = 0 });
    }

    private string _queryStart = "";
    private bool _noFocus; // films: never take the focus from the user

    private void ShowQueryCard()
    {
        _queryOpen = false;
        QueryList.Visibility = Visibility.Collapsed;
        QueryBox.Text = _queryStart;
        QueryBox.CaretIndex = QueryBox.Text.Length;
        _peekTimer.Stop();
        ShowPanel(QueryPanel);
        SizeQuery();
        ShowSideBubbles();
        if (!_noFocus)
        {
            Activate(); // typing goes into the island
            Dispatcher.BeginInvoke(new Action(() => { QueryBox.Focus(); Keyboard.Focus(QueryBox); }), System.Windows.Threading.DispatcherPriority.Input);
        }
        RefreshQuery();
        if (_apps == null) Task.Run(LoadApps);
    }

    private void SizeQuery()
    {
        QueryPanel.Measure(new Size(QueryW - 24, double.PositiveInfinity));
        double h = Math.Max(38, QueryPanel.DesiredSize.Height);
        AnimateSize(QueryW, h + NotchExtra, Math.Min(22, (h + NotchExtra) / 2), true);
    }

    private void QueryKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; EndPeek(); break;
            case Key.Down or Key.Tab:
                e.Handled = true;
                if (!_queryOpen) { _queryOpen = true; QueryList.Visibility = Visibility.Visible; }
                else if (_hits.Count > 0) _hitIdx = (_hitIdx + 1) % _hits.Count;
                ShowHits();
                break;
            case Key.Up:
                e.Handled = true;
                if (_hits.Count > 0) _hitIdx = (_hitIdx - 1 + _hits.Count) % _hits.Count;
                ShowHits();
                break;
            case Key.Enter:
                e.Handled = true;
                if (_hits.Count == 0) return;
                var hit = _hits[Math.Clamp(_hitIdx, 0, _hits.Count - 1)];
                EndPeek();
                hit.Run();
                break;
        }
    }

    private void RefreshQuery()
    {
        var q = QueryBox.Text ?? "";
        QueryIcon.Kind = q.StartsWith('=') ? "plus" : "search";
        _hits = Find(q.Trim());
        _hitIdx = 0;
        ShowHits();
    }

    private void ShowHits()
    {
        var best = _hits.Count > 0 ? _hits[Math.Clamp(_hitIdx, 0, _hits.Count - 1)] : null;
        QueryBest.Text = best == null ? "" : best.Kind == "calc" ? best.Text : best.Text + "  ↵";
        QueryBest.Foreground = best?.Kind == "calc" ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB2));
        QueryBest.FontWeight = best?.Kind == "calc" ? FontWeights.SemiBold : FontWeights.Normal;
        QueryList.Children.Clear();
        if (_queryOpen)
        {
            int i = 0;
            foreach (var h in _hits.Take(6))
            {
                bool sel = i == _hitIdx;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Background = sel ? new SolidColorBrush(Color.FromArgb(0x55, 0x0A, 0x84, 0xFF)) : Brushes.Transparent, Cursor = Cursors.Hand };
                var kind = new TextBlock { Text = KindName(h.Kind), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xB2)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
                DockPanel.SetDock(kind, Dock.Right);
                row.Children.Add(kind);
                row.Children.Add(new SfIcon { Kind = h.Icon, Size = 12, Foreground = Brushes.White, Margin = new Thickness(6, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = h.Text, FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var run = h;
                row.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; EndPeek(); run.Run(); };
                QueryList.Children.Add(new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = row });
                i++;
            }
        }
        if (_state == State.Peek && _peek?.Key == "query") SizeQuery();
    }

    private static string KindName(string k) => k switch
    {
        "app" => L.T("App"), "file" => L.T("Datei"), "folder" => L.T("Ordner"), "calc" => L.T("Ergebnis"), _ => L.T("Befehl")
    };

    private List<Hit> Find(string q)
    {
        var hits = new List<Hit>();
        if (q.Length == 0) return hits;
        // a sum: "=12*7+3" or just "12*7" – the result, Enter copies it
        var expr = q.TrimStart('=').Trim();
        if (expr.Length > 0 && Regex.IsMatch(expr, @"^[\d\s.,+\-*/×÷:()%^]+$") && Regex.IsMatch(expr, @"\d") && Calc(expr) is { } v)
        {
            string r = v.ToString("0.##########", L.English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("de-DE"));
            hits.Add(new Hit("plus", "= " + r, "calc", () => CopyQuiet(r)));
        }
        if (q.StartsWith('=')) return hits;
        var low = q.ToLowerInvariant();
        // island commands
        if (Regex.Match(low, @"^(?:timer\s*)?(\d{1,3})\s*(?:min|minuten|minutes|m)?$") is { Success: true } tm && (low.StartsWith("timer") || low.Contains("min")))
        {
            int min = int.Parse(tm.Groups[1].Value);
            hits.Add(new Hit("timer", L.T($"Timer {min} min"), "cmd", () => { H.Timer.Reset(); H.Timer.SetDurationSeconds(min * 60); H.Timer.Start(); UpdatePill(true); }));
        }
        if (low.StartsWith("notiz ") || low.StartsWith("note "))
        {
            var text = q[(q.IndexOf(' ') + 1)..].Trim();
            if (text.Length > 0) hits.Add(new Hit("square.pencil", L.T($"Notiz: {text}"), "cmd", () => { H.Notes.Add(text); ShowHint("check", L.T("Notiz gespeichert"), (Brush)FindResource("Green"), check: true); }));
        }
        foreach (var (name, tab, icon) in new[]
        {
            (L.T("Musik"), "Music", "music"), (L.T("Zwischenablage"), "Clip", "clipboard"), (L.T("Ablage"), "Shelf", "folder"), ("KI", "Ai", "sparkles"),
            ("Claude", "Agent", "asterisk"), (L.T("Mitteilungen"), "Notify", "bell"), (L.T("Timer"), L.T("Timer"), "timer"), (L.T("Fokus"), "focus", "moon"),
            (L.T("Heute"), "Today", "calendar"), (L.T("Erinnerungen"), "Remind", "checklist"), (L.T("Steuerung"), "Controls", "sliders"),
            (L.T("Notizen"), "Notes", "square.pencil"), (L.T("Einstellungen"), "settings", "gear"),
        })
        {
            if (!name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) continue;
            var t = tab;
            hits.Add(new Hit(icon, name, "cmd", () =>
            {
                if (t == "settings") SettingsWindow.ShowSingle();
                else if (t == "focus") { Expand(L.T("Timer"), true); SetFocusMode(true); }
                else Expand(t, true);
            }));
        }
        // apps from the start menu, then recent and nearby files
        if (_apps != null)
            foreach (var a in _apps.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(a => a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(a => a.Name.Length).Take(5))
            {
                var path = a.Path;
                hits.Add(new Hit("window", a.Name, "app", () => Launch(path)));
            }
        if (q.Length >= 2)
            foreach (var f in FindFiles(q).Take(5))
            {
                var path = f;
                bool dir = Directory.Exists(path);
                hits.Add(new Hit(dir ? "folder" : "doc", Path.GetFileName(path), dir ? "folder" : "file", () => Launch(path)));
            }
        return hits;
    }

    /// <summary>+ − × ÷ ^ % and brackets; comma or dot as the decimal mark. Null if it is not a sum.</summary>
    private static double? Calc(string expr)
    {
        try
        {
            var e = expr.Replace("×", "*").Replace("÷", "/").Replace(":", "/").Replace(" ", "");
            e = Regex.Replace(e, @"(\d),(\d)", "$1.$2");
            e = Regex.Replace(e, @"(\d+(?:\.\d+)?)%", "($1/100)");
            int pos = 0;
            double v = Sum(e, ref pos);
            return pos == e.Length && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;
        }
        catch { return null; }
    }

    private static double Sum(string e, ref int p)
    {
        double v = Product(e, ref p);
        while (p < e.Length && e[p] is '+' or '-') { char o = e[p++]; double r = Product(e, ref p); v = o == '+' ? v + r : v - r; }
        return v;
    }

    private static double Product(string e, ref int p)
    {
        double v = Power(e, ref p);
        while (p < e.Length && e[p] is '*' or '/') { char o = e[p++]; double r = Power(e, ref p); v = o == '*' ? v * r : v / r; }
        return v;
    }

    private static double Power(string e, ref int p)
    {
        double v = Atom(e, ref p);
        if (p < e.Length && e[p] == '^') { p++; v = Math.Pow(v, Power(e, ref p)); }
        return v;
    }

    private static double Atom(string e, ref int p)
    {
        if (p < e.Length && e[p] == '-') { p++; return -Atom(e, ref p); }
        if (p < e.Length && e[p] == '(') { p++; double v = Sum(e, ref p); if (p < e.Length && e[p] == ')') p++; return v; }
        int s = p;
        while (p < e.Length && (char.IsDigit(e[p]) || e[p] == '.')) p++;
        if (s == p) throw new FormatException();
        return double.Parse(e[s..p], CultureInfo.InvariantCulture);
    }

    private static void LoadApps()
    {
        var list = new List<(string, string)>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (Regex.IsMatch(name, "uninstall|deinstall|readme|hilfe|help", RegexOptions.IgnoreCase)) continue;
                    if (!list.Any(x => x.Item1 == name)) list.Add((name, lnk));
                }
            }
            catch { }
        }
        _apps = list;
    }

    private static readonly string[] FileRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Recent),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };

    /// <summary>Recently used files (their shortcuts) and what lies on the desktop, in Documents and Downloads.</summary>
    private static IEnumerable<string> FindFiles(string q)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in FileRoots)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(root).ToList(); } catch { continue; }
            foreach (var path in entries)
            {
                var name = Path.GetFileName(path);
                bool recent = root == FileRoots[0];
                if (recent) name = Path.GetFileNameWithoutExtension(name); // "bericht.pdf.lnk" → "bericht.pdf"
                if (!name.Contains(q, StringComparison.OrdinalIgnoreCase) || name.StartsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(name)) yield return path;
            }
        }
    }

    // =====================================================================
    //  47 · just saved
    // =====================================================================

    private readonly List<FileSystemWatcher> _savedWatch = new();
    private readonly Dictionary<string, DateTime> _savedSeen = new();
    private string _savedFile;

    private void WatchSaved()
    {
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            try
            {
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                w.Created += (_, e) => OnSaved(e.FullPath);
                w.Renamed += (_, e) => OnSaved(e.FullPath);
                w.EnableRaisingEvents = true;
                _savedWatch.Add(w);
            }
            catch { }
        }
        HintPanel.PreviewMouseMove += SavedDrag;
    }

    private void OnSaved(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name.StartsWith("~$") || name.StartsWith(".") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        // after the file is written (and not the same file twice within a minute)
        Ui.Run(() => After(900, () =>
        {
            if (!S.SavedFiles || !File.Exists(path) || _savedSeen.TryGetValue(path, out var t) && DateTime.Now - t < TimeSpan.FromMinutes(1)) return;
            _savedSeen[path] = DateTime.Now;
            if (_state != State.Collapsed || _tucked) return;
            _savedFile = path;
            _hintHoldMs = 5000;
            ShowHint("doc", name, Brushes.White, key: "saved");
            HintPanel.Cursor = Cursors.Hand;
            HintPanel.ToolTip = L.T("Gespeichert – herausziehen, um sie weiterzugeben");
            After(5500, () => { if (_savedFile == path) { _savedFile = null; HintPanel.ClearValue(CursorProperty); HintPanel.ToolTip = null; } });
        }));
    }

    /// <summary>The notice of a just saved file can be dragged out like a file from the Ablage.</summary>
    private void SavedDrag(object sender, MouseEventArgs e)
    {
        if (_savedFile == null || e.LeftButton != MouseButtonState.Pressed || !File.Exists(_savedFile)) return;
        var file = _savedFile;
        _savedFile = null;
        try { DragDrop.DoDragDrop(HintPanel, new DataObject(DataFormats.FileDrop, new[] { file }), DragDropEffects.Copy | DragDropEffects.Link); } catch { }
    }
}
