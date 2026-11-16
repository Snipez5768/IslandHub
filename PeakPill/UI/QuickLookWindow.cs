using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PeakPill.Services;
using Path = System.Windows.Shapes.Path;
using Line = System.Windows.Shapes.Line;

namespace PeakPill.UI;

/// <summary>
/// Quick-Look style big view of a clipboard picture: the screen dims and the picture opens almost up to the
/// monitor edges. Esc, a click or the × closes it; a small glass bar below offers copy / Live Text / save / edit.
/// The pen switches the same window into an edit mode: crop frame with aspect presets, rotate, mirror and
/// resize (W × H, proportions kept while the chain is on). "Fertig" hands the result back as a new picture.
/// </summary>
public sealed class QuickLookWindow : Window
{
    private static QuickLookWindow _open;
    /// <summary>Render test only.</summary>
    public static QuickLookWindow Current => _open;
    public void CloseNow() { _closing = true; Close(); }
    private readonly ClipItem _item;
    private readonly Action<BitmapSource> _edited;
    private readonly Border _frame;
    private readonly ImageBrush _brush;
    private readonly BitmapSource _display;
    private readonly ScaleTransform _scale = new(0.9, 0.9);
    private readonly Border _barHost;
    private readonly StackPanel _viewBar;
    private readonly double _maxW, _maxH, _dpi;
    private bool _closing;

    public static void Show(ClipItem item, Action<ClipItem> copy, Action<ClipItem> liveText, Action<ClipItem> save, Action<BitmapSource> edited = null)
    {
        if (item?.ImageFile == null || !File.Exists(item.ImageFile)) return;
        _open?.Close();
        _open = new QuickLookWindow(item, copy, liveText, save, edited);
        _open.Show();
        _open.Activate();
    }

    private QuickLookWindow(ClipItem item, Action<ClipItem> copy, Action<ClipItem> liveText, Action<ClipItem> save, Action<BitmapSource> edited)
    {
        _item = item;
        _edited = edited;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Vorschau";
        Background = new SolidColorBrush(Color.FromArgb(0x9E, 0, 0, 0));
        FontFamily = (FontFamily)Application.Current.TryFindResource("TextFont") ?? new FontFamily("Segoe UI Variable Text");

        // the primary monitor's work area (the island lives there)
        var wa = SystemParameters.WorkArea;
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;

        const double margin = 40, barSpace = 64;
        _maxW = wa.Width - margin * 2;
        _maxH = wa.Height - margin - barSpace - 12;
        _dpi = App.Island != null ? VisualTreeHelper.GetDpi(App.Island).DpiScaleX : 1;
        var (w, h) = Fit(item.ImageWidth, item.ImageHeight, _maxH);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(item.ImageFile);
        // enough pixels for the big view in any orientation
        bmp.DecodePixelWidth = (int)Math.Min(item.ImageWidth > 0 ? item.ImageWidth : 4000, Math.Max(_maxW, _maxH) * Math.Max(1, _dpi));
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.EndInit();
        bmp.Freeze();
        _display = bmp;

        _brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        _frame = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(12),
            Background = _brush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            Cursor = Cursors.Arrow,
            ToolTip = $"{item.ImageWidth}×{item.ImageHeight}"
        };
        RenderOptions.SetBitmapScalingMode(_frame, BitmapScalingMode.HighQuality);

        // ---- glass bar below the picture
        _viewBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _viewBar.Children.Add(Btn("copy", "Kopieren", "Blue", () => { copy?.Invoke(_item); Fade(); }));
        _viewBar.Children.Add(Btn("text.viewfinder", "Text", "Teal", () => { Fade(); liveText?.Invoke(_item); }));
        _viewBar.Children.Add(Btn("save", "Sichern", "Green", () => { Fade(); save?.Invoke(_item); }));
        if (edited != null)
        {
            var pen = Btn("pencil", null, "Yellow", EnterEdit);
            pen.ToolTip = "Bearbeiten: Format, Drehen, Spiegeln, Größe";
            _viewBar.Children.Add(pen);
        }
        _viewBar.Children.Add(Btn("xmark", null, "Danger", Fade));
        _barHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1E)),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Child = _viewBar,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black }
        };
        _barHost.MouseLeftButtonUp += (_, e) => e.Handled = true; // clicks between the buttons don't close

        _chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        _chips.MouseLeftButtonUp += (_, e) => e.Handled = true;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(_chips);
        stack.Children.Add(_frame);
        stack.Children.Add(_barHost);
        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(stack);
        Content = root;

        // Esc, a click on the picture or next to it closes (in edit mode Esc only leaves the edit mode)
        MouseLeftButtonUp += (_, e) => { if (!e.Handled && !_editing) Fade(); };
        PreviewKeyDown += (_, e) =>
        {
            if (_editing)
            {
                if (e.Key == Key.Escape) { e.Handled = true; LeaveEdit(); }
                else if (e.Key == Key.Enter) { e.Handled = true; FinishEdit(); }
                return;
            }
            if (e.Key is Key.Escape or Key.Space) { e.Handled = true; Fade(); }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; copy?.Invoke(_item); Fade(); }
        };
        Deactivated += (_, _) => { if (!_editing) Fade(); };
        Closed += (_, _) => { if (_open == this) _open = null; };

        // open: fade the dim layer in, the picture springs up
        Opacity = 0;
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            var spring = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
        };
    }

    /// <summary>Picture size in device-independent pixels, fitted into the screen (small pictures at most 2×).</summary>
    private (double w, double h) Fit(int pw, int ph, double maxH)
    {
        double iw = Math.Max(1, pw) / _dpi, ih = Math.Max(1, ph) / _dpi;
        double k = Math.Min(2.0, Math.Min(_maxW / iw, maxH / ih));
        return (iw * k, ih * k);
    }

    private static Brush R(string key) => (Brush)Application.Current.TryFindResource(key);

    private Button Btn(string icon, string text, string hover, Action act, string style = null)
    {
        var b = new Button { Content = text, Height = 32, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(4, 0, 4, 0), FontSize = 12.5 };
        b.SetResourceReference(StyleProperty, style ?? (text == null ? "GlassIcon" : "PillButton"));
        if (text == null) { b.Width = 32; b.Content = null; b.Padding = new Thickness(0); }
        if (icon != null) Ext.SetIcon(b, icon);
        Ext.SetIconSize(b, text == null ? 17 : 14);
        if (hover != null && R(hover) is { } hb) Ext.SetHover(b, hb);
        b.Click += (_, e) => { e.Handled = true; act(); };
        return b;
    }

    private void Fade()
    {
        if (_closing) return;
        _closing = true;
        var d = TimeSpan.FromMilliseconds(150);
        var shrink = new DoubleAnimation(0.94, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    // =====================================================================================================
    // Edit mode: format (crop with aspect presets), rotate, mirror, resize
    // =====================================================================================================

    private const double ChipSpace = 44, MinCrop = 24;
    private readonly StackPanel _chips;
    private StackPanel _editBar;
    private Canvas _overlay;
    private Path _dim;
    private Border _cropBox;
    private readonly Line[] _grid = new Line[4];
    private readonly Path[] _corners = new Path[4];
    private TextBox _wBox, _hBox;
    private Button _lockBtn;
    private bool _editing, _lock = true, _sizeCustom, _updatingSize;
    private Matrix _m = Matrix.Identity;        // rotation / mirroring, exact quarter turns only
    private Rect _crop = new(0, 0, 1, 1);        // normalized to the turned picture
    private string _aspectKey = "Frei";
    private int _outW, _outH;
    private enum Grip { None, Move, L, T, R, B, TL, TR, BL, BR }
    private Grip _grip;
    private Point _dragStart;
    private Rect _dragRect;

    private bool Swapped => _m.M11 == 0;
    private int TurnedW => Swapped ? _item.ImageHeight : _item.ImageWidth;
    private int TurnedH => Swapped ? _item.ImageWidth : _item.ImageHeight;

    private static readonly (string key, double ratio)[] Aspects =
        { ("Frei", 0), ("Original", -1), ("1:1", 1), ("4:3", 4.0 / 3), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16) };

    private double? AspectRatio => _aspectKey switch
    {
        "Frei" => null,
        "Original" => (double)TurnedW / Math.Max(1, TurnedH),
        _ => Aspects.First(a => a.key == _aspectKey).ratio
    };

    /// <summary>Render test only: opens the edit mode with a preset, optionally turned once.</summary>
    public void EditForTest(string aspect, bool rotate)
    {
        EnterEdit();
        if (rotate) Turn(true);
        SetAspect(aspect);
    }

    /// <summary>Render test only: the picture "Fertig" would produce right now.</summary>
    public BitmapSource ResultForTest() => Render();

    private void EnterEdit()
    {
        if (_editing || _item.ImageWidth <= 0 || _item.ImageHeight <= 0) return;
        _editing = true;
        _m = Matrix.Identity;
        _crop = new Rect(0, 0, 1, 1);
        _aspectKey = "Frei";
        _sizeCustom = false;
        _lock = true;
        _editBar ??= BuildEditBar();
        BuildChips();
        BuildOverlay();
        _chips.Visibility = Visibility.Visible;
        _barHost.Child = _editBar;
        _frame.ToolTip = null;
        _frame.Cursor = null;
        UpdateLock();
        Relayout();
        CropChanged();
        foreach (var fe in new FrameworkElement[] { _chips, _overlay, _editBar })
            fe.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void LeaveEdit()
    {
        if (!_editing) return;
        _editing = false;
        _m = Matrix.Identity;
        _chips.Visibility = Visibility.Collapsed;
        _frame.Child = null;
        _barHost.Child = _viewBar;
        _frame.ToolTip = $"{_item.ImageWidth}×{_item.ImageHeight}";
        _frame.Cursor = Cursors.Arrow;
        _brush.ImageSource = _display;
        var (w, h) = Fit(_item.ImageWidth, _item.ImageHeight, _maxH);
        _frame.Width = w; _frame.Height = h;
        Activate();
    }

    private StackPanel BuildEditBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Button Tip(Button b, string tip) { b.ToolTip = tip; return b; }
        bar.Children.Add(Tip(Btn("rotate.left", null, "Blue", () => Turn(false)), "Nach links drehen"));
        bar.Children.Add(Tip(Btn("rotate.right", null, "Blue", () => Turn(true)), "Nach rechts drehen"));
        bar.Children.Add(Tip(Btn("flip.h", null, "Purple", () => Mirror(true)), "Horizontal spiegeln"));
        bar.Children.Add(Tip(Btn("flip.v", null, "Purple", () => Mirror(false)), "Vertikal spiegeln"));
        bar.Children.Add(Sep());

        TextBox Box(string tip)
        {
            var t = new TextBox { Width = 62, Height = 30, Margin = new Thickness(4, 0, 4, 0), TextAlignment = TextAlignment.Center, Padding = new Thickness(4, 0, 4, 0), ToolTip = tip, MaxLength = 5 };
            t.SetResourceReference(StyleProperty, "DarkTextBox");
            t.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            DataObject.AddPastingHandler(t, (_, e) =>
            {
                if (e.DataObject.GetData(DataFormats.UnicodeText) is not string s || !s.All(char.IsDigit)) e.CancelCommand();
            });
            t.GotKeyboardFocus += (_, _) => t.Dispatcher.BeginInvoke(new Action(t.SelectAll));
            t.TextChanged += (_, _) => SizeTyped(t);
            return t;
        }
        _wBox = Box("Breite in Pixeln");
        _hBox = Box("Höhe in Pixeln");
        _lockBtn = Btn("link", null, null, () => { _lock = !_lock; UpdateLock(); if (_lock) SizeTyped(_wBox); });
        _lockBtn.Margin = new Thickness(0);
        bar.Children.Add(_wBox);
        bar.Children.Add(_lockBtn);
        bar.Children.Add(_hBox);
        bar.Children.Add(new TextBlock { Text = "px", Foreground = R("SubText"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) });
        bar.Children.Add(Sep());
        bar.Children.Add(Btn(null, "Abbrechen", null, LeaveEdit));
        bar.Children.Add(Btn("check", "Fertig", null, FinishEdit, "AccentButton"));
        return bar;

        static Border Sep() => new() { Width = 0.5, Height = 20, Margin = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
    }

    private void UpdateLock()
    {
        Ext.SetIcon(_lockBtn, _lock ? "link" : "link.off");
        _lockBtn.Foreground = _lock ? new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00)) : R("SubText");
        _lockBtn.ToolTip = _lock ? "Seitenverhältnis beibehalten (an)" : "Seitenverhältnis beibehalten (aus) – Bild kann gestaucht werden";
    }

    /// <summary>Plain chips like in the mockup: the chosen one is white with dark text.</summary>
    private void BuildChips()
    {
        _chips.Children.Clear();
        foreach (var (key, _) in Aspects)
        {
            var b = new Border
            {
                Height = 26,
                Padding = new Thickness(11, 0, 11, 0),
                Margin = new Thickness(3, 0, 3, 0),
                CornerRadius = new CornerRadius(13),
                Cursor = Cursors.Hand,
                Tag = key,
                Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center }
            };
            b.MouseEnter += (_, _) => PaintChips();
            b.MouseLeave += (_, _) => PaintChips();
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetAspect(key); };
            _chips.Children.Add(b);
        }
        PaintChips();
    }

    private void PaintChips()
    {
        foreach (Border b in _chips.Children)
        {
            bool sel = (string)b.Tag == _aspectKey;
            b.Background = sel ? Brushes.White : b.IsMouseOver ? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            ((TextBlock)b.Child).Foreground = sel ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)) : new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
        }
    }

    private void SetAspect(string key)
    {
        _aspectKey = key;
        PaintChips();
        if (AspectRatio is double a)
        {
            // the biggest frame of that format, centred in the picture
            double pr = (double)TurnedW / TurnedH;
            double w = Math.Min(1, a / pr), h = Math.Min(1, pr / a);
            _crop = new Rect((1 - w) / 2, (1 - h) / 2, w, h);
        }
        else if (key == "Frei") { /* keep the frame, it just becomes free */ }
        CropChanged();
    }

    // ---- rotate / mirror: the picture turns, the frame turns with it

    private void Turn(bool right)
    {
        var c = _crop;
        _m = Matrix.Multiply(_m, right ? new Matrix(0, 1, -1, 0, 0, 0) : new Matrix(0, -1, 1, 0, 0, 0));
        _crop = right ? new Rect(1 - c.Y - c.Height, c.X, c.Height, c.Width) : new Rect(c.Y, 1 - c.X - c.Width, c.Height, c.Width);
        // a typed size turns too
        if (_sizeCustom) (_outW, _outH) = (_outH, _outW);
        Relayout();
        if (AspectRatio != null && _aspectKey != "Frei") SetAspect(_aspectKey); else CropChanged();
    }

    private void Mirror(bool horizontal)
    {
        var c = _crop;
        _m = Matrix.Multiply(_m, horizontal ? new Matrix(-1, 0, 0, 1, 0, 0) : new Matrix(1, 0, 0, -1, 0, 0));
        _crop = horizontal ? new Rect(1 - c.X - c.Width, c.Y, c.Width, c.Height) : new Rect(c.X, 1 - c.Y - c.Height, c.Width, c.Height);
        Relayout();
    }

    /// <summary>Shows the turned picture at its new size and redraws the frame.</summary>
    private void Relayout()
    {
        var (w, h) = Fit(TurnedW, TurnedH, _maxH - ChipSpace);
        _frame.Width = w; _frame.Height = h;
        if (_m.IsIdentity) _brush.ImageSource = _display;
        else
        {
            var tb = new TransformedBitmap(_display, new MatrixTransform(_m));
            tb.Freeze();
            _brush.ImageSource = tb;
        }
        _overlay.Width = w; _overlay.Height = h;
        DrawOverlay();
    }

    // ---- crop frame

    private void BuildOverlay()
    {
        _overlay = new Canvas { Background = Brushes.Transparent, ClipToBounds = false };
        _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), IsHitTestVisible = false };
        _cropBox = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        _overlay.Children.Add(_dim);
        _overlay.Children.Add(_cropBox);
        for (int i = 0; i < 4; i++)
        {
            _grid[i] = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), StrokeThickness = 0.5, IsHitTestVisible = false };
            _overlay.Children.Add(_grid[i]);
        }
        for (int i = 0; i < 4; i++)
        {
            _corners[i] = new Path { Stroke = Brushes.White, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
            _overlay.Children.Add(_corners[i]);
        }
        _overlay.MouseLeftButtonDown += OverlayDown;
        _overlay.MouseMove += OverlayMove;
        _overlay.MouseLeftButtonUp += OverlayUp;
        _overlay.LostMouseCapture += (_, _) => _grip = Grip.None;
        _frame.Child = _overlay;
    }

    private Rect CropPx => new(_crop.X * _overlay.Width, _crop.Y * _overlay.Height, _crop.Width * _overlay.Width, _crop.Height * _overlay.Height);

    private void DrawOverlay()
    {
        if (_overlay == null || double.IsNaN(_overlay.Width)) return;
        double W = _overlay.Width, H = _overlay.Height;
        var r = CropPx;
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, W, H), 12, 12), new RectangleGeometry(r));
        Canvas.SetLeft(_cropBox, r.X); Canvas.SetTop(_cropBox, r.Y);
        _cropBox.Width = r.Width; _cropBox.Height = r.Height;
        for (int i = 0; i < 2; i++)
        {
            double x = r.X + r.Width * (i + 1) / 3, y = r.Y + r.Height * (i + 1) / 3;
            (_grid[i].X1, _grid[i].Y1, _grid[i].X2, _grid[i].Y2) = (x, r.Y, x, r.Bottom);
            (_grid[i + 2].X1, _grid[i + 2].Y1, _grid[i + 2].X2, _grid[i + 2].Y2) = (r.X, y, r.Right, y);
        }
        double L = Math.Min(18, Math.Min(r.Width, r.Height) / 2), o = 1.5;
        Geometry Corner(double x, double y, double dx, double dy) =>
            Geometry.Parse(FormattableString.Invariant($"M{x + dx * L},{y} L{x},{y} L{x},{y + dy * L}"));
        _corners[0].Data = Corner(r.X - o, r.Y - o, 1, 1);
        _corners[1].Data = Corner(r.Right + o, r.Y - o, -1, 1);
        _corners[2].Data = Corner(r.X - o, r.Bottom + o, 1, -1);
        _corners[3].Data = Corner(r.Right + o, r.Bottom + o, -1, -1);
    }

    private Grip HitGrip(Point p)
    {
        var r = CropPx;
        const double c = 16, e = 9;
        bool nl = Math.Abs(p.X - r.X) < c, nr = Math.Abs(p.X - r.Right) < c, nt = Math.Abs(p.Y - r.Y) < c, nb = Math.Abs(p.Y - r.Bottom) < c;
        if (nl && nt) return Grip.TL;
        if (nr && nt) return Grip.TR;
        if (nl && nb) return Grip.BL;
        if (nr && nb) return Grip.BR;
        bool inX = p.X > r.X && p.X < r.Right, inY = p.Y > r.Y && p.Y < r.Bottom;
        if (inY && Math.Abs(p.X - r.X) < e) return Grip.L;
        if (inY && Math.Abs(p.X - r.Right) < e) return Grip.R;
        if (inX && Math.Abs(p.Y - r.Y) < e) return Grip.T;
        if (inX && Math.Abs(p.Y - r.Bottom) < e) return Grip.B;
        return inX && inY ? Grip.Move : Grip.None;
    }

    private static Cursor CursorOf(Grip g) => g switch
    {
        Grip.TL or Grip.BR => Cursors.SizeNWSE,
        Grip.TR or Grip.BL => Cursors.SizeNESW,
        Grip.L or Grip.R => Cursors.SizeWE,
        Grip.T or Grip.B => Cursors.SizeNS,
        Grip.Move => Cursors.SizeAll,
        _ => Cursors.Arrow
    };

    private void OverlayDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var p = e.GetPosition(_overlay);
        _grip = HitGrip(p);
        if (_grip == Grip.None) return;
        _dragStart = p;
        _dragRect = CropPx;
        _overlay.CaptureMouse();
    }

    private void OverlayUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _grip = Grip.None;
        _overlay.ReleaseMouseCapture();
    }

    private void OverlayMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        if (_grip == Grip.None) { _overlay.Cursor = CursorOf(HitGrip(p)); return; }
        double W = _overlay.Width, H = _overlay.Height;
        var r = _dragRect;
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        double? a = AspectRatio;
        Rect n;

        if (_grip == Grip.Move)
        {
            double x = Math.Clamp(r.X + dx, 0, W - r.Width), y = Math.Clamp(r.Y + dy, 0, H - r.Height);
            n = new Rect(x, y, r.Width, r.Height);
        }
        else if (_grip is Grip.TL or Grip.TR or Grip.BL or Grip.BR)
        {
            // the opposite corner stays where it is
            int sx = _grip is Grip.TR or Grip.BR ? 1 : -1, sy = _grip is Grip.BL or Grip.BR ? 1 : -1;
            double ax = sx > 0 ? r.X : r.Right, ay = sy > 0 ? r.Y : r.Bottom;
            double maxW = sx > 0 ? W - ax : ax, maxH = sy > 0 ? H - ay : ay;
            double w = (p.X - ax) * sx, h = (p.Y - ay) * sy;
            if (a is double k)
            {
                w = Math.Max(w, h * k);
                w = Math.Min(w, Math.Min(maxW, maxH * k));
                w = Math.Max(w, MinCrop * Math.Max(1, k));
                h = w / k;
            }
            else
            {
                w = Math.Clamp(w, MinCrop, maxW);
                h = Math.Clamp(h, MinCrop, maxH);
            }
            n = new Rect(sx > 0 ? ax : ax - w, sy > 0 ? ay : ay - h, w, h);
        }
        else
        {
            bool horiz = _grip is Grip.L or Grip.R;
            if (horiz)
            {
                int s = _grip == Grip.R ? 1 : -1;
                double ax = s > 0 ? r.X : r.Right, maxW = s > 0 ? W - ax : ax;
                double w = Math.Clamp((p.X - ax) * s, MinCrop, maxW), h = r.Height, cy = r.Y + r.Height / 2;
                if (a is double k)
                {
                    double maxH = 2 * Math.Min(cy, H - cy);
                    h = w / k;
                    if (h > maxH) { h = maxH; w = h * k; }
                }
                n = new Rect(s > 0 ? ax : ax - w, a != null ? cy - h / 2 : r.Y, w, h);
            }
            else
            {
                int s = _grip == Grip.B ? 1 : -1;
                double ay = s > 0 ? r.Y : r.Bottom, maxH = s > 0 ? H - ay : ay;
                double h = Math.Clamp((p.Y - ay) * s, MinCrop, maxH), w = r.Width, cx = r.X + r.Width / 2;
                if (a is double k)
                {
                    double maxW = 2 * Math.Min(cx, W - cx);
                    w = h * k;
                    if (w > maxW) { w = maxW; h = w / k; }
                }
                n = new Rect(a != null ? cx - w / 2 : r.X, s > 0 ? ay : ay - h, w, h);
            }
        }

        n.Intersect(new Rect(0, 0, W, H));
        if (n.IsEmpty) return;
        _crop = new Rect(n.X / W, n.Y / H, n.Width / W, n.Height / H);
        CropChanged();
    }

    // ---- size

    private (int w, int h) CropPixels()
    {
        var r = PixelRect(TurnedW, TurnedH);
        return (r.Width, r.Height);
    }

    private Int32Rect PixelRect(int tw, int th)
    {
        int x = (int)Math.Round(_crop.X * tw), y = (int)Math.Round(_crop.Y * th);
        int w = (int)Math.Round(_crop.Width * tw), h = (int)Math.Round(_crop.Height * th);
        x = Math.Clamp(x, 0, tw - 1); y = Math.Clamp(y, 0, th - 1);
        w = Math.Clamp(w, 1, tw - x); h = Math.Clamp(h, 1, th - y);
        return new Int32Rect(x, y, w, h);
    }

    private void CropChanged()
    {
        DrawOverlay();
        var (cw, ch) = CropPixels();
        if (!_sizeCustom) (_outW, _outH) = (cw, ch);
        else if (_lock) _outH = Math.Max(1, (int)Math.Round(_outW * (double)ch / cw));
        ShowSize();
    }

    private void ShowSize()
    {
        _updatingSize = true;
        if (_wBox.Text != _outW.ToString()) _wBox.Text = _outW.ToString();
        if (_hBox.Text != _outH.ToString()) _hBox.Text = _outH.ToString();
        _updatingSize = false;
    }

    private void SizeTyped(TextBox box)
    {
        if (_updatingSize || !_editing) return;
        if (!int.TryParse(box.Text, out int v) || v < 1 || v > 20000) return;
        var (cw, ch) = CropPixels();
        if (box == _wBox)
        {
            _outW = v;
            if (_lock) _outH = Math.Max(1, (int)Math.Round(v * (double)ch / cw));
        }
        else
        {
            _outH = v;
            if (_lock) _outW = Math.Max(1, (int)Math.Round(v * (double)cw / ch));
        }
        _sizeCustom = _outW != cw || _outH != ch;
        _updatingSize = true;
        if (box == _wBox) { if (_hBox.Text != _outH.ToString()) _hBox.Text = _outH.ToString(); }
        else if (_wBox.Text != _outW.ToString()) _wBox.Text = _outW.ToString();
        _updatingSize = false;
    }

    // ---- result

    private void FinishEdit()
    {
        if (!_editing) return;
        try
        {
            var result = Render();
            if (result == null) return;
            _edited?.Invoke(result);
            Fade();
        }
        catch (Exception ex) { App.Log(ex); LeaveEdit(); }
    }

    /// <summary>Full resolution: turn/mirror → crop → resize (high quality), as a frozen picture.</summary>
    private BitmapSource Render()
    {
        var full = new BitmapImage();
        full.BeginInit();
        full.UriSource = new Uri(_item.ImageFile);
        full.CacheOption = BitmapCacheOption.OnLoad;
        full.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        full.EndInit();
        full.Freeze();

        BitmapSource src = full;
        if (!_m.IsIdentity) { var tb = new TransformedBitmap(full, new MatrixTransform(_m)); tb.Freeze(); src = tb; }
        var r = PixelRect(src.PixelWidth, src.PixelHeight);
        if (r.X != 0 || r.Y != 0 || r.Width != src.PixelWidth || r.Height != src.PixelHeight)
        {
            var cb = new CroppedBitmap(src, r);
            cb.Freeze();
            src = cb;
        }
        int ow = Math.Max(1, _outW), oh = Math.Max(1, _outH);
        if ((long)ow * oh > 120_000_000) return null;
        if (ow == src.PixelWidth && oh == src.PixelHeight)
        {
            // pixels stay 1:1 – only make it a plain 96-dpi picture
            var copy = BitmapFrame.Create(src);
            copy.Freeze();
            return copy;
        }
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, ow, oh));
        var rtb = new RenderTargetBitmap(ow, oh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
