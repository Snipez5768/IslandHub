using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using PeakPill.Core;
using PeakPill.Services;
using IOPath = System.IO.Path;
using Path = System.Windows.Shapes.Path;

namespace PeakPill.UI;

/// <summary>
/// Screenshot editor: markup (pen, marker, arrow, shapes, text, pixelate, crop, undo) and Live Text –
/// recognised text is highlighted on the image and can be selected with the mouse and copied.
/// </summary>
public sealed class EditorWindow : Window
{
    private enum Tool { Select, Pen, Marker, Arrow, Rect, Ellipse, Text, Pixelate, Crop }

    private readonly ClipItem _item;
    private BitmapSource _base;
    private int _pw, _ph;
    private readonly Grid _surface = new() { ClipToBounds = true };
    private readonly Image _img = new() { Stretch = Stretch.Fill };
    private readonly Canvas _ink = new() { Background = Brushes.Transparent };
    private readonly Canvas _live = new() { Background = Brushes.Transparent, Visibility = Visibility.Collapsed, Cursor = Cursors.IBeam };
    private readonly Canvas _temp = new() { IsHitTestVisible = false };
    private readonly Viewbox _view = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Button _copyText, _qrBtn, _liveBtn;
    private readonly Stack<(Action Undo, Action Redo)> _undo = new(), _redo = new();

    private Tool _tool = Tool.Select;
    private Color _color = Color.FromRgb(0xFF, 0x45, 0x3A);
    private double _size = 2;
    private Point _start;
    private Shape _drawing;
    private bool _down, _modified, _deleted;

    private List<OcrWord> _words;
    private readonly List<Border> _wordBoxes = new();
    private int _selA = -1, _selB = -1;
    private string _qr;

    public static void Open(ClipItem item, bool liveText)
    {
        if (item?.ImageFile == null || !File.Exists(item.ImageFile)) return;
        var w = new EditorWindow(item);
        if (liveText) w.ContentRendered += async (_, _) => await w.SetLiveText(true);
        w.Show();
        w.Activate();
    }

    private EditorWindow(ClipItem item)
    {
        _item = item;
        Title = L.T("Bildschirmfoto");
        Icon = IconFactory.CreateWindowIcon();
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Foreground = Brushes.White;
        SetResourceReference(FontFamilyProperty, "TextFont");
        FontSize = 13;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 700; MinHeight = 440;

        LoadBase(LoadBitmap(item.ImageFile));
        _surface.Children.Add(_img);
        _surface.Children.Add(_ink);
        _surface.Children.Add(_temp);
        _surface.Children.Add(_live);
        _view.Child = _surface;

        // ---------- top bar ----------
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5) };
        foreach (var (t, icon, tip, hover) in new[]
        {
            (Tool.Select, "cursor", L.T("Auswählen"), "Blue"),
            (Tool.Pen, "pencil", L.T("Stift"), "Yellow"),
            (Tool.Marker, "highlighter", L.T("Textmarker"), "Yellow"),
            (Tool.Arrow, "arrow", L.T("Pfeil"), L.T("Orange")),
            (Tool.Rect, "rect", L.T("Rechteck"), L.T("Orange")),
            (Tool.Ellipse, "ellipse", L.T("Ellipse"), L.T("Orange")),
            (Tool.Text, "text", "Text", "Blue"),
            (Tool.Pixelate, "pixelate", L.T("Verpixeln"), "Purple"),
            (Tool.Crop, "crop", L.T("Zuschneiden"), "Green"),
        })
        {
            var rb = new RadioButton { GroupName = "EditTool", ToolTip = tip, IsChecked = t == Tool.Select };
            rb.SetResourceReference(StyleProperty, "TabButton");
            Ext.SetIcon(rb, icon);
            var tt = t;
            rb.Checked += (_, _) => { _tool = tt; if (_live.Visibility == Visibility.Visible && tt != Tool.Select) _ = SetLiveText(false); UpdateCursor(); };
            tools.Children.Add(rb);
        }
        tools.Children.Add(Sep());
        foreach (var hex in new[] { "#FF453A", "#FFD60A", "#30D158", "#0A84FF", "#FFFFFF", "#000000" })
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            tools.Children.Add(ColorDot(c));
        }
        tools.Children.Add(Sep());
        foreach (var (s, d) in new[] { (1.0, 5.0), (2.0, 8.0), (3.5, 11.0) })
            tools.Children.Add(SizeDot(s, d));
        tools.Children.Add(Sep());
        tools.Children.Add(IconBtn("undo", L.T("Rückgängig (Strg+Z)"), "Blue", Undo));
        tools.Children.Add(IconBtn("redo", L.T("Wiederholen (Strg+Y)"), "Blue", Redo));
        tools.Children.Add(Sep());
        _liveBtn = IconBtn("text.viewfinder", L.T("Text erkennen (Live Text)"), "Teal", async () => await SetLiveText(_live.Visibility != Visibility.Visible));
        tools.Children.Add(_liveBtn);
        _qrBtn = IconBtn("qr", L.T("QR-Code"), "Green", OpenQr);
        _qrBtn.Visibility = Visibility.Collapsed;
        tools.Children.Add(_qrBtn);

        var topGlass = GlassCapsule(tools);
        topGlass.HorizontalAlignment = HorizontalAlignment.Center;
        topGlass.Margin = new Thickness(12, 10, 12, 6);

        // ---------- bottom bar ----------
        _copyText = Pill("copy", "Text kopieren", true, CopySelectedText);
        _copyText.Visibility = Visibility.Collapsed;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_copyText);
        right.Children.Add(Gap());
        // the picture as it is now (with drawings) to the KI chat – a prompt of your own goes with it (2026-11-17)
        right.Children.Add(Pill("bubble", L.T("KI fragen"), false, () => { var pic = RenderComposite(); Close(); App.Island.AskAiAbout(pic); }));
        right.Children.Add(Gap());
        right.Children.Add(GlassIconBtn("copy", L.T("Bild in die Zwischenablage"), "Blue", () => { Commit(); Flash(L.T("Bild kopiert")); }));
        right.Children.Add(GlassIconBtn("save", L.T("Auf dem PC sichern"), "Green", SaveToFolder));
        right.Children.Add(GlassIconBtn("download", L.T("Sichern unter …"), "Green", SaveAs));
        right.Children.Add(GlassIconBtn("pin", L.T("Oben anheften"), L.T("Orange"), () => PinWindow.Show(RenderComposite())));
        right.Children.Add(GlassIconBtn("trash", L.T("Löschen"), "Danger", Delete));
        right.Children.Add(Gap());
        right.Children.Add(Pill("check", L.T("Fertig"), true, () => Close()));

        var bottom = new DockPanel { Margin = new Thickness(16, 6, 12, 12) };
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        _status.Foreground = (Brush)FindResource("SubText");
        bottom.Children.Add(_status);

        var stage = new Border { Margin = new Thickness(16, 4, 16, 4), CornerRadius = new CornerRadius(10), Child = _view, ClipToBounds = true };
        var root = new DockPanel();
        DockPanel.SetDock(topGlass, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(topGlass);
        root.Children.Add(bottom);
        root.Children.Add(stage);
        Content = root;

        var area = SystemParameters.WorkArea;
        Width = Math.Clamp(_pw + 80, MinWidth, area.Width * 0.85);
        Height = Math.Clamp(_ph + 160, MinHeight, area.Height * 0.85);
        UpdateStatus();

        _ink.MouseLeftButtonDown += Ink_Down;
        _ink.MouseMove += Ink_Move;
        _ink.MouseLeftButtonUp += Ink_Up;
        _live.MouseLeftButtonDown += Live_Down;
        _live.MouseMove += Live_Move;
        _live.MouseLeftButtonUp += (_, _) => { _down = false; _live.ReleaseMouseCapture(); };
        PreviewKeyDown += OnKey;
        Closing += (_, _) => { if (_modified && !_deleted) Commit(); };
        SourceInitialized += (_, _) =>
        {
            Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
            if (Native.TryApplyBackdrop(this, 2))
            {
                Background = Brushes.Transparent;
                stage.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
            }
        };
        Loaded += (_, _) => _ = DetectQrAsync();
    }

    // =====================================================================
    //  Image
    // =====================================================================

    private static BitmapSource LoadBitmap(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private void LoadBase(BitmapSource b)
    {
        _base = b;
        _pw = b.PixelWidth;
        _ph = b.PixelHeight;
        _img.Source = b;
        foreach (var fe in new FrameworkElement[] { _surface, _img, _ink, _live, _temp })
        {
            fe.Width = _pw;
            fe.Height = _ph;
        }
    }

    private double BaseStroke => Math.Max(2, Math.Round(Math.Max(_pw, _ph) / 500.0));

    private BitmapSource RenderComposite(bool includeInk = true)
    {
        var liveVis = _live.Visibility;
        _live.Visibility = Visibility.Collapsed;
        _temp.Children.Clear();
        if (!includeInk) _ink.Visibility = Visibility.Hidden;
        _surface.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(_surface) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, _pw, _ph), Stretch = Stretch.Fill };
            dc.DrawRectangle(vb, null, new Rect(0, 0, _pw, _ph));
        }
        var rtb = new RenderTargetBitmap(_pw, _ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        _ink.Visibility = Visibility.Visible;
        _live.Visibility = liveVis;
        return rtb;
    }

    private void Commit()
    {
        try { App.Hub.Clipboard.UpdateImage(_item, RenderComposite()); _modified = false; }
        catch (Exception ex) { App.Log(ex); }
    }

    // =====================================================================
    //  Markup tools
    // =====================================================================

    private void UpdateCursor() => _ink.Cursor = _tool switch
    {
        Tool.Select => Cursors.Arrow,
        Tool.Text => Cursors.IBeam,
        _ => Cursors.Cross
    };

    private Brush Stroke(bool marker = false)
    {
        var c = marker ? Color.FromArgb(0x70, _color.R, _color.G, _color.B) : _color;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void Ink_Down(object sender, MouseButtonEventArgs e)
    {
        if (_tool == Tool.Select) return;
        var p = e.GetPosition(_surface);
        _start = p;
        _down = true;
        _ink.CaptureMouse();
        double w = BaseStroke * _size;
        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Marker:
                var pl = new Polyline
                {
                    Stroke = Stroke(_tool == Tool.Marker),
                    StrokeThickness = _tool == Tool.Marker ? w * 4 : w,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                pl.Points.Add(p);
                _drawing = pl;
                AddInk(pl);
                break;
            case Tool.Arrow:
                _drawing = new Path { Stroke = Stroke(), Fill = Stroke(), StrokeThickness = w, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                AddInk(_drawing);
                break;
            case Tool.Rect:
                _drawing = new Rectangle { Stroke = Stroke(), StrokeThickness = w, RadiusX = w * 1.5, RadiusY = w * 1.5 };
                Place(_drawing, new Rect(p, p));
                AddInk(_drawing);
                break;
            case Tool.Ellipse:
                _drawing = new Ellipse { Stroke = Stroke(), StrokeThickness = w };
                Place(_drawing, new Rect(p, p));
                AddInk(_drawing);
                break;
            case Tool.Pixelate:
            case Tool.Crop:
                _drawing = new Rectangle
                {
                    Stroke = Brushes.White, StrokeThickness = Math.Max(1, BaseStroke / 1.5), StrokeDashArray = new DoubleCollection { 4, 3 },
                    Fill = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255))
                };
                Place(_drawing, new Rect(p, p));
                _temp.Children.Add(_drawing);
                break;
            case Tool.Text:
                _down = false;
                _ink.ReleaseMouseCapture();
                PlaceTextBox(p);
                break;
        }
    }

    private void Ink_Move(object sender, MouseEventArgs e)
    {
        if (!_down || _drawing == null) return;
        var p = e.GetPosition(_surface);
        switch (_drawing)
        {
            case Polyline pl: pl.Points.Add(p); break;
            case Path path: path.Data = ArrowGeometry(_start, p, BaseStroke * _size); break;
            default: Place(_drawing, new Rect(_start, p)); break;
        }
    }

    private void Ink_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_down) return;
        _down = false;
        _ink.ReleaseMouseCapture();
        var p = e.GetPosition(_surface);
        var rect = new Rect(_start, p);
        var shape = _drawing;
        _drawing = null;
        if (_tool is Tool.Pixelate or Tool.Crop)
        {
            _temp.Children.Clear();
            var r = Int(rect);
            if (r.Width < 4 || r.Height < 4) return;
            if (_tool == Tool.Pixelate) Pixelate(r); else Crop(r);
        }
    }

    private void Place(UIElement el, Rect r)
    {
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        if (el is FrameworkElement fe) { fe.Width = Math.Max(1, r.Width); fe.Height = Math.Max(1, r.Height); }
    }

    private static Geometry ArrowGeometry(Point a, Point b, double w)
    {
        var v = b - a;
        double len = v.Length;
        if (len < 1) return Geometry.Empty;
        v /= len;
        var n = new Vector(-v.Y, v.X);
        double head = Math.Max(w * 4, 12);
        var baseP = b - v * head;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false, false);
            c.LineTo(baseP, true, true);
            c.BeginFigure(b, true, true);
            c.LineTo(baseP + n * head * 0.55, true, true);
            c.LineTo(baseP - n * head * 0.55, true, true);
        }
        g.Freeze();
        return g;
    }

    private void AddInk(UIElement el)
    {
        _ink.Children.Add(el);
        _modified = true;
        Push(() => _ink.Children.Remove(el), () => { if (!_ink.Children.Contains(el)) _ink.Children.Add(el); });
    }

    private void PlaceTextBox(Point p)
    {
        double fs = Math.Max(16, BaseStroke * _size * 9);
        var tb = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Stroke(), CaretBrush = Stroke(),
            FontSize = fs, FontWeight = FontWeights.Bold, MinWidth = fs * 2, AcceptsReturn = false
        };
        tb.SetResourceReference(FontFamilyProperty, "DisplayFont");
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y - fs * 0.7);
        _ink.Children.Add(tb);
        tb.Focus();
        void Finish()
        {
            if (!_ink.Children.Contains(tb)) return;
            _ink.Children.Remove(tb);
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            var text = new TextBlock { Text = tb.Text, Foreground = tb.Foreground, FontSize = fs, FontWeight = FontWeights.Bold };
            text.SetResourceReference(FontFamilyProperty, "DisplayFont");
            text.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.5 };
            Canvas.SetLeft(text, Canvas.GetLeft(tb));
            Canvas.SetTop(text, Canvas.GetTop(tb));
            AddInk(text);
        }
        tb.LostKeyboardFocus += (_, _) => Finish();
        tb.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) { e.Handled = true; Keyboard.ClearFocus(); Focus(); } };
    }

    private static Int32Rect Int(Rect r) => new((int)Math.Max(0, r.X), (int)Math.Max(0, r.Y), (int)r.Width, (int)r.Height);

    private Int32Rect Clamp(Int32Rect r)
    {
        int x = Math.Clamp(r.X, 0, _pw - 1), y = Math.Clamp(r.Y, 0, _ph - 1);
        return new Int32Rect(x, y, Math.Clamp(r.Width, 1, _pw - x), Math.Clamp(r.Height, 1, _ph - y));
    }

    private void Pixelate(Int32Rect r)
    {
        r = Clamp(r);
        var before = _base;
        int block = Math.Max(6, Math.Max(r.Width, r.Height) / 22);
        var crop = new CroppedBitmap(_base, r);
        var small = new TransformedBitmap(crop, new ScaleTransform(1.0 / block, 1.0 / block));
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(_base, new Rect(0, 0, _pw, _ph));
            dc.DrawImage(small, new Rect(r.X, r.Y, r.Width, r.Height));
        }
        var rtb = new RenderTargetBitmap(_pw, _ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        LoadBase(rtb);
        _modified = true;
        Push(() => LoadBase(before), () => LoadBase(rtb));
    }

    private void Crop(Int32Rect r)
    {
        r = Clamp(r);
        var beforeBase = _base;
        var beforeInk = _ink.Children.Cast<UIElement>().ToList();
        var composite = RenderComposite();
        var cropped = new CroppedBitmap(composite, r);
        cropped.Freeze();
        void Apply() { _ink.Children.Clear(); LoadBase(cropped); ResetLiveText(); UpdateStatus(); }
        Apply();
        _modified = true;
        Push(() =>
        {
            LoadBase(beforeBase);
            _ink.Children.Clear();
            foreach (var el in beforeInk) _ink.Children.Add(el);
            ResetLiveText();
            UpdateStatus();
        }, Apply);
    }

    private void Push(Action undo, Action redo)
    {
        _undo.Push((undo, redo));
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var a = _undo.Pop();
        a.Undo();
        _redo.Push(a);
        _modified = true;
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var a = _redo.Pop();
        a.Redo();
        _undo.Push(a);
        _modified = true;
    }

    // =====================================================================
    //  Live Text
    // =====================================================================

    private void ResetLiveText()
    {
        _words = null;
        _wordBoxes.Clear();
        _live.Children.Clear();
        _live.Visibility = Visibility.Collapsed;
        _copyText.Visibility = Visibility.Collapsed;
        _selA = _selB = -1;
    }

    public async Task SetLiveText(bool on)
    {
        if (!on)
        {
            _live.Visibility = Visibility.Collapsed;
            _copyText.Visibility = Visibility.Collapsed;
            _liveBtn.Foreground = (Brush)FindResource("SubText");
            UpdateStatus();
            return;
        }
        _liveBtn.Foreground = (Brush)FindResource("Teal");
        if (_words == null)
        {
            _status.Text = L.T("Text wird erkannt …");
            try
            {
                var tmp = IOPath.Combine(IOPath.GetTempPath(), $"peakpill_ocr_{Guid.NewGuid():N}.png");
                ScreenshotService.SavePng(RenderComposite(false), tmp);
                _words = await OcrService.RecognizeWordsAsync(tmp);
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex)
            {
                App.Log(ex);
                _status.Text = ex.Message;
                return;
            }
            BuildWordBoxes();
        }
        if (_words.Count == 0)
        {
            _status.Text = L.T("Kein Text gefunden");
            return;
        }
        _live.Visibility = Visibility.Visible;
        _live.Opacity = 0;
        _live.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        _status.Text = L.T("Text markieren und kopieren · Strg+C");
        _copyText.Content = L.T("Alles kopieren");
        _copyText.Visibility = Visibility.Visible;
    }

    private void BuildWordBoxes()
    {
        _live.Children.Clear();
        _wordBoxes.Clear();
        _live.Children.Add(new Rectangle { Width = _pw, Height = _ph, Fill = new SolidColorBrush(Color.FromArgb(0x52, 0, 0, 0)), IsHitTestVisible = false });
        double pad = Math.Max(1.5, _ph / 600.0);
        foreach (var w in _words)
        {
            var b = new Border
            {
                Width = w.Box.Width + pad * 2, Height = w.Box.Height + pad * 2,
                CornerRadius = new CornerRadius(Math.Max(2, w.Box.Height / 5)),
                Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 255, 255, 255)),
                BorderThickness = new Thickness(Math.Max(0.6, pad / 2)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(b, w.Box.X - pad);
            Canvas.SetTop(b, w.Box.Y - pad);
            _live.Children.Add(b);
            _wordBoxes.Add(b);
        }
    }

    private int NearestWord(Point p)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _words.Count; i++)
        {
            var r = _words[i].Box;
            double dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
            double dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
            double d = dx * dx + dy * dy * 4;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void Live_Down(object sender, MouseButtonEventArgs e)
    {
        if (_words == null || _words.Count == 0) return;
        var p = e.GetPosition(_surface);
        if (e.ClickCount == 2)
        {
            // double click selects the whole line
            int i = NearestWord(p);
            int line = _words[i].Line;
            _selA = _words.FindIndex(w => w.Line == line);
            _selB = _words.FindLastIndex(w => w.Line == line);
            PaintSelection();
            return;
        }
        _selA = _selB = NearestWord(p);
        _down = true;
        _live.CaptureMouse();
        PaintSelection();
    }

    private void Live_Move(object sender, MouseEventArgs e)
    {
        if (!_down || _words == null) return;
        int i = NearestWord(e.GetPosition(_surface));
        if (i != _selB) { _selB = i; PaintSelection(); }
    }

    private void PaintSelection()
    {
        int a = Math.Min(_selA, _selB), b = Math.Max(_selA, _selB);
        var on = new SolidColorBrush(Color.FromArgb(0x8C, 0x0A, 0x84, 0xFF));
        var off = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        for (int i = 0; i < _wordBoxes.Count; i++)
            _wordBoxes[i].Background = a >= 0 && i >= a && i <= b ? on : off;
        bool any = a >= 0;
        _copyText.Content = any ? "Text kopieren" : L.T("Alles kopieren");
        _status.Text = any ? L.T($"{b - a + 1} Wörter markiert · Strg+C") : L.T("Text markieren und kopieren · Strg+C");
    }

    private void CopySelectedText()
    {
        if (_words == null || _words.Count == 0) return;
        int a = Math.Min(_selA, _selB), b = Math.Max(_selA, _selB);
        var words = a < 0 ? _words : _words.Skip(a).Take(b - a + 1);
        var text = OcrService.JoinWords(words);
        if (string.IsNullOrWhiteSpace(text)) return;
        App.Hub.Clipboard.AddText(text, L.T("Texterkennung"));
        Flash(L.T("Text kopiert"));
    }

    private async Task DetectQrAsync()
    {
        var src = _base;
        _qr = await Task.Run(() => ScreenshotService.DecodeQr(src));
        if (_qr == null) return;
        _qrBtn.Visibility = Visibility.Visible;
        _qrBtn.ToolTip = L.T("QR-Code: ") + _qr;
    }

    private void OpenQr()
    {
        if (_qr == null) return;
        if (Uri.IsWellFormedUriString(_qr, UriKind.Absolute))
        {
            try { Process.Start(new ProcessStartInfo(_qr) { UseShellExecute = true }); } catch { }
        }
        else
        {
            App.Hub.Clipboard.AddText(_qr, L.T("QR-Code"));
            Flash(L.T("QR-Inhalt kopiert"));
        }
    }

    // =====================================================================
    //  Actions
    // =====================================================================

    private void SaveToFolder()
    {
        try
        {
            Commit();
            var dest = ScreenshotService.SaveCopy(_item.ImageFile, App.Settings.ScreenshotFolder);
            Flash(L.T("Gesichert in ") + IOPath.GetFileName(IOPath.GetDirectoryName(dest)));
        }
        catch (Exception ex) { App.Log(ex); _status.Text = ex.Message; }
    }

    private void SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG-Bild|*.png",
            FileName = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png",
            InitialDirectory = Directory.Exists(App.Settings.ScreenshotFolder) ? App.Settings.ScreenshotFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dlg.ShowDialog(this) != true) return;
        ScreenshotService.SavePng(RenderComposite(), dlg.FileName);
        Flash(L.T("Gesichert"));
    }

    private void Delete()
    {
        _deleted = true;
        App.Hub.Clipboard.Remove(_item);
        Close();
    }

    private void Flash(string text)
    {
        _status.Text = "✓  " + text;
        _status.Foreground = (Brush)FindResource("Green");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); _status.Foreground = (Brush)FindResource("SubText"); UpdateStatus(); };
        t.Start();
    }

    private void UpdateStatus()
    {
        if (_live.Visibility == Visibility.Visible) return;
        _status.Text = $"{_pw} × {_ph}";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Redo(); else Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C)
        {
            if (_live.Visibility == Visibility.Visible) CopySelectedText();
            else { Commit(); Flash(L.T("Bild kopiert")); }
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S) { SaveToFolder(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_live.Visibility == Visibility.Visible && _selA >= 0) { _selA = _selB = -1; PaintSelection(); }
            else Close();
            e.Handled = true;
        }
    }

    /// <summary>Developer aid for the render test.</summary>
    public void SaveSnapshot(string path)
    {
        UpdateLayout();
        var el = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        rtb.Render(dv);
        ScreenshotService.SavePng(rtb, path);
    }

    // =====================================================================
    //  UI helpers
    // =====================================================================

    private static Border Sep() => new()
    {
        Width = 0.8, Margin = new Thickness(6, 7, 6, 7),
        Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))
    };

    private static FrameworkElement Gap() => new Border { Width = 10 };

    private Border GlassCapsule(UIElement child)
    {
        var g = new Grid();
        var fill = new Border { CornerRadius = new CornerRadius(22) };
        fill.SetResourceReference(Border.BackgroundProperty, "GlassFill");
        var sheen = new Border { CornerRadius = new CornerRadius(22), Margin = new Thickness(1) };
        sheen.SetResourceReference(Border.BackgroundProperty, "GlassSheen");
        var rim = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(0.8) };
        rim.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
        g.Children.Add(fill);
        g.Children.Add(sheen);
        g.Children.Add(rim);
        g.Children.Add(child);
        return new Border { Child = g };
    }

    private Button IconBtn(string icon, string tip, string hover, Action act)
    {
        var b = new Button { ToolTip = tip, Width = 32, Height = 32 };
        b.SetResourceReference(StyleProperty, "IconButton");
        Ext.SetIcon(b, icon);
        Ext.SetHover(b, (Brush)FindResource(hover));
        b.Click += (_, _) => act();
        return b;
    }

    private Button GlassIconBtn(string icon, string tip, string hover, Action act)
    {
        var b = new Button { ToolTip = tip, Margin = new Thickness(3, 0, 3, 0), Width = 34, Height = 34 };
        b.SetResourceReference(StyleProperty, "GlassIcon");
        Ext.SetIcon(b, icon);
        Ext.SetHover(b, (Brush)FindResource(hover));
        b.Click += (_, _) => act();
        return b;
    }

    private Button Pill(string icon, string text, bool accent, Action act)
    {
        var b = new Button { Content = text, Margin = new Thickness(3, 0, 3, 0) };
        b.SetResourceReference(StyleProperty, accent ? "AccentButton" : "PillButton");
        Ext.SetIcon(b, icon);
        b.Click += (_, _) => act();
        return b;
    }

    private RadioButton ColorDot(Color c)
    {
        var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Color.FromArgb(0x59, 255, 255, 255)), StrokeThickness = 0.8 };
        var ring = new Ellipse { Width = 22, Height = 22, StrokeThickness = 2, Stroke = Brushes.Transparent };
        var g = new Grid { Width = 26, Height = 30, Background = Brushes.Transparent };
        g.Children.Add(ring);
        g.Children.Add(dot);
        var rb = new RadioButton
        {
            GroupName = "InkColor", Content = g, Cursor = Cursors.Hand, Focusable = false,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='RadioButton'><ContentPresenter/></ControlTemplate>")
        };
        rb.Checked += (_, _) => { _color = c; ring.Stroke = Brushes.White; };
        rb.Unchecked += (_, _) => ring.Stroke = Brushes.Transparent;
        if (c == _color) { rb.IsChecked = true; ring.Stroke = Brushes.White; }
        return rb;
    }

    private RadioButton SizeDot(double size, double d)
    {
        var dot = new Ellipse { Width = d, Height = d, Fill = Brushes.White };
        var bg = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Transparent };
        var g = new Grid { Width = 28, Height = 30, Background = Brushes.Transparent };
        g.Children.Add(bg);
        g.Children.Add(dot);
        var rb = new RadioButton
        {
            GroupName = "InkSize", Content = g, Cursor = Cursors.Hand, Focusable = false, ToolTip = L.T("Strichstärke"),
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='RadioButton'><ContentPresenter/></ControlTemplate>")
        };
        rb.Checked += (_, _) => { _size = size; bg.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)); };
        rb.Unchecked += (_, _) => bg.Background = Brushes.Transparent;
        if (Math.Abs(size - _size) < 0.01) { rb.IsChecked = true; bg.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)); }
        return rb;
    }
}
