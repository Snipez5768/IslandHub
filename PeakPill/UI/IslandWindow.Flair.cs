using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

/// <summary>
/// The animation round of 2026-11-17 (user: "setz alle Animationsvorschläge um"): timer rim, liquid charging bolt, cover
/// beat, flights (copy, screenshot, download), the read card sucked up, magnet and gulp, the ringing reminder, the
/// slide on the island's own spring, lists that grow and shrink, the timer flying home, the hopping counter.
/// </summary>
public partial class IslandWindow
{
    private RotateTransform _islandTilt;
    private Path _timerRim;
    private bool _timerLead;

    private void InitFlair()
    {
        if (Island.RenderTransform is TransformGroup g) { _islandTilt = new RotateTransform(); g.Children.Insert(1, _islandTilt); }
        _timerRim = new Path
        {
            StrokeThickness = 1.8, StrokeDashCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
        };
        _timerRim.SetResourceReference(Shape.StrokeProperty, "Orange");
        Panel.SetZIndex(_timerRim, 21);
        IslandContent.Children.Add(_timerRim);
        H.Timer.PropertyChanged += (_, _) => Ui.Run(UpdateTimerRim);
        H.Clipboard.Items.CollectionChanged += (_, e) => GrowNew(ClipList, e);
        H.Notifications.Items.CollectionChanged += (_, e) => GrowNew(NotifList, e);
        SwitchSpring.Attach(this);
        WireFollowScreen();
        // the KI's answer failed while the eyes watched it think: startled
        H.Ai.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiService.Busy) && !H.Ai.Busy && H.Ai.Messages.LastOrDefault()?.Failed == true) Ui.Run(() => EyesStartle());
        };
    }

    // ---------- timer: the progress runs once around the pill as a line (the time stays inside) ----------

    private void UpdateTimerRim()
    {
        if (_timerRim == null) return;
        bool on = _timerLead && _state == State.Collapsed && H.Timer.Active && Island.IsVisible;
        if (!on) { if (_timerRim.Visibility != Visibility.Collapsed) _timerRim.Visibility = Visibility.Collapsed; return; }
        double w = double.IsNaN(Island.Width) ? Island.ActualWidth : Island.Width;
        double h = double.IsNaN(Island.Height) ? Island.ActualHeight : Island.Height;
        double r = Island.CornerRadius.TopLeft;
        const double inset = 1.1;
        var geo = SmoothCorners.Make(w, h, r, inset: inset);
        double len = Length(geo);
        if (len < 1) return;
        _timerRim.Data = geo;
        _timerRim.Margin = IslandRim.Margin;
        double t = _timerRim.StrokeThickness;
        // the figure starts at the top-left where the rounding ends – the line starts at the top centre instead
        double wi = w - 2 * inset, hi = h - 2 * inset, half = Math.Min(wi, hi) / 2, ri = Math.Min(Math.Max(0, r - inset), half);
        double p = Math.Min(1.6 * ri, half);
        double start = Math.Max(0, wi / 2 - p);
        double prog = Math.Clamp(H.Timer.ProgressPercent / 100, 0.002, 1);
        double dash = prog * len / t, period = len / t;
        _timerRim.StrokeDashArray = new DoubleCollection { dash, Math.Max(0.001, period - dash) };
        _timerRim.StrokeDashOffset = -start / t;
        _timerRim.Visibility = Visibility.Visible;
    }

    private static double Length(Geometry g)
    {
        double sum = 0;
        foreach (var f in g.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute).Figures)
        {
            var last = f.StartPoint;
            foreach (var s in f.Segments)
            {
                IEnumerable<Point> pts = s switch { PolyLineSegment pl => pl.Points, LineSegment l => new[] { l.Point }, _ => Array.Empty<Point>() };
                foreach (var pt in pts) { sum += (pt - last).Length; last = pt; }
            }
            if (f.IsClosed) sum += (f.StartPoint - last).Length;
        }
        return sum;
    }

    // ---------- charging: the bolt fills up like a liquid to the battery level ----------

    private SfIcon _boltFill;
    private GradientStop _boltTop, _boltEdge;

    private void LiquidBolt(double level, Brush color)
    {
        if (S.Animations == AnimationLevel.Aus || HintIcon.Parent is not Grid grid) return;
        if (_boltFill == null)
        {
            _boltTop = new GradientStop(Colors.White, 0);
            _boltEdge = new GradientStop(Colors.Transparent, 0.001);
            _boltFill = new SfIcon
            {
                Kind = "bolt", Size = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                OpacityMask = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0), GradientStops = { _boltTop, _boltEdge } },
            };
            grid.Children.Add(_boltFill);
        }
        _boltFill.Foreground = color;
        _boltFill.Visibility = Visibility.Visible;
        HintIcon.BeginAnimation(OpacityProperty, null);
        HintIcon.Opacity = 0.3; // the empty bolt stays as an outline of what fills
        double to = Math.Clamp(level, 0.08, 1);
        var dur = TimeSpan.FromMilliseconds(900);
        var slosh = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        _boltTop.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(0, to, dur) { BeginTime = TimeSpan.FromMilliseconds(160), EasingFunction = slosh });
        _boltEdge.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(0.001, Math.Min(1, to + 0.001), dur) { BeginTime = TimeSpan.FromMilliseconds(160), EasingFunction = slosh });
    }

    private void LiquidBoltOff()
    {
        if (_boltFill == null) return;
        _boltFill.Visibility = Visibility.Collapsed;
    }

    // ---------- music: the small cover thumps with the bass (inwards, see below) ----------

    private ScaleTransform _coverPulse;
    private double _bassAvg, _coverBeat;

    private void CoverBeat(double bass)
    {
        if (S.Animations != AnimationLevel.An || LeadCover.Visibility != Visibility.Visible || _songPending || _state != State.Collapsed) return;
        // the cover's own scale (the song swap uses it too – only while no swap moves the cover)
        if (Math.Abs(LeadCoverShift.X) > 0.01 || LeadCoverScale.HasAnimatedProperties) return;
        _coverPulse = LeadCoverScale;
        _bassAvg = _bassAvg * 0.92 + bass * 0.08;
        double kick = Math.Max(0, bass - _bassAvg * 1.08);
        _coverBeat = Math.Max(_coverBeat * 0.8, Math.Min(1, kick * 3.2));
        // the beat pushes the cover IN and it springs back – never bigger than its place (growing outwards cut it at the
        // island's round left end, user report 2026-11-17)
        _coverPulse.ScaleX = _coverPulse.ScaleY = 1 - 0.07 * _coverBeat;
    }

    private void CoverBeatStop()
    {
        _coverBeat = 0;
        if (_coverPulse != null) _coverPulse.ScaleX = _coverPulse.ScaleY = 1;
    }

    // ---------- flights across the screen ----------

    private Point IslandCentrePx()
    {
        try { return Island.PointToScreen(new Point(Island.ActualWidth / 2, Island.ActualHeight / 2)); }
        catch { return new Point(0, 0); }
    }

    private bool OnIslandScreen(Point px) =>
        WF.Screen.FromPoint(new System.Drawing.Point((int)px.X, (int)px.Y)).DeviceName == IslandScreen().DeviceName;

    /// <summary>Ctrl+C: the copied words fly as a small card from the pointer into the island.</summary>
    private void FlyCopy(string text)
    {
        if (S.Animations != AnimationLevel.An || _state != State.Collapsed || _tucked || string.IsNullOrWhiteSpace(text)) return;
        Native.GetCursorPos(out var c);
        var from = new Point(c.X + 16, c.Y + 12);
        if (!OnIslandScreen(from)) return;
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (line.Length > 42) line = line[..41] + "…";
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 5, 9, 5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.6), MaxWidth = 220,
            Child = new TextBlock { Text = line, Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        _ = FlightWindow.Fly(card, from, IslandCentrePx(), 0.2, 560);
        After(520, IslandGulp);
    }

    /// <summary>The screenshot shrinks and flies into the island (no flash – user's choice).</summary>
    public async Task FlyShot(BitmapSource img, System.Drawing.Rectangle region)
    {
        if (S.Animations != AnimationLevel.An || _tucked || img == null || region.Width < 4) return;
        var centre = new Point(region.X + region.Width / 2.0, region.Y + region.Height / 2.0);
        if (!OnIslandScreen(centre)) return;
        var scr = WF.Screen.FromPoint(new System.Drawing.Point((int)centre.X, (int)centre.Y));
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double wDip = region.Width / dpi, hDip = region.Height / dpi;
        var pic = new Border
        {
            Width = wDip, Height = hDip, CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        double end = Math.Min(1, 34 / Math.Max(wDip, hDip));
        var fly = FlightWindow.Fly(pic, centre, IslandCentrePx(), end, 600, fadeMs: 140);
        After(560, IslandGulp);
        await fly;
    }

    /// <summary>A finished download: its icon drops out of the island down towards the taskbar.</summary>
    private void DropToTaskbar(string path)
    {
        if (S.Animations != AnimationLevel.An || _tucked) return;
        var from = IslandCentrePx();
        var b = IslandScreen().Bounds;
        var to = new Point(from.X, b.Bottom - 24 * VisualTreeHelper.GetDpi(this).DpiScaleY);
        var icon = new Image { Source = ShellIcon.Get(path), Width = 34, Height = 34 };
        _ = FlightWindow.Fly(icon, new Point(from.X, from.Y + 20), to, 0.55, 760, arc: true, endOpacity: 0, fadeMs: 200);
    }

    // ---------- the island swallows something / opens its mouth ----------

    private ScaleTransform IslandSquash => Island.RenderTransform is TransformGroup g ? g.Children.OfType<ScaleTransform>().FirstOrDefault() : null;

    private void IslandGulp()
    {
        if (S.Animations == AnimationLevel.Aus || IslandSquash is not { } st) return;
        var x = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        var y = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        foreach (var (kx, ky, at) in new[] { (1.0, 1.0, 0.0), (1.06, 0.88, 0.3), (0.98, 1.05, 0.62), (1.0, 1.0, 1.0) })
        {
            x.KeyFrames.Add(new EasingDoubleKeyFrame(kx, KeyTime.FromPercent(at), new SineEase()));
            y.KeyFrames.Add(new EasingDoubleKeyFrame(ky, KeyTime.FromPercent(at), new SineEase()));
        }
        st.BeginAnimation(ScaleTransform.ScaleXProperty, x);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, y);
    }

    /// <summary>A file is dragged onto the closed island: it bulges towards the pointer before it opens.</summary>
    private void MagnetBulge(double pointerX)
    {
        if (S.Animations == AnimationLevel.Aus || IslandSquash is not { } st) return;
        st.CenterX = Math.Clamp(pointerX, 0, Island.ActualWidth) - Island.ActualWidth / 2;
        var k = TimeSpan.FromMilliseconds(260);
        var x = new DoubleAnimationUsingKeyFrames { Duration = k };
        x.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromPercent(0.4), new CubicEase { EasingMode = EasingMode.EaseOut }));
        x.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase()));
        var y = new DoubleAnimationUsingKeyFrames { Duration = k };
        y.KeyFrames.Add(new EasingDoubleKeyFrame(1.28, KeyTime.FromPercent(0.4), new CubicEase { EasingMode = EasingMode.EaseOut }));
        y.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase()));
        x.Completed += (_, _) => st.CenterX = 0;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, x);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, y);
    }

    // ---------- a reminder rings: the island wobbles like an alarm clock ----------

    private void Wobble()
    {
        if (S.Animations == AnimationLevel.Aus || _islandTilt == null) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100) };
        double[] angles = { 0, -4.5, 4.5, -4, 4, -3, 3, -1.5, 1.5, 0 };
        for (int i = 0; i < angles.Length; i++)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(angles[i], KeyTime.FromPercent(i / (double)(angles.Length - 1)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        _islandTilt.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    // ---------- "Gelesen": the card is sucked up into the island ----------

    private void SuckPeekUp()
    {
        if (S.Animations == AnimationLevel.Aus || Island.ActualWidth < 1) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(Island.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Island.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(Island), null, new Rect(0, 0, Island.ActualWidth, Island.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            var at = Island.TranslatePoint(new Point(0, 0), Root);
            var img = new Image { Source = rtb, Width = Island.ActualWidth, Height = Island.ActualHeight, RenderTransformOrigin = new Point(0.5, 0) };
            var sc = new ScaleTransform(1, 1);
            var mv = new TranslateTransform();
            img.RenderTransform = new TransformGroup { Children = { sc, mv } };
            Canvas.SetLeft(img, at.X);
            Canvas.SetTop(img, at.Y);
            FlyLayer.Children.Add(img);
            var d = TimeSpan.FromMilliseconds(260);
            var inEase = new CubicEase { EasingMode = EasingMode.EaseIn };
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.15, d) { EasingFunction = inEase });
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.55, d) { EasingFunction = inEase });
            mv.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -6, d) { EasingFunction = inEase });
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(60) };
            fade.Completed += (_, _) => FlyLayer.Children.Remove(img);
            img.BeginAnimation(OpacityProperty, fade);
        }
        catch { }
    }

    // ---------- the page slide on the island's own spring (one move with the height change) ----------

    private readonly Spring _slide = new(0, Spring.IslandResponse, Spring.IslandDamping, 0.002);
    private double _slideWay;
    private int _slideDir;
    private bool _sliding;

    private void StepSlide(double dt)
    {
        if (!_sliding) return;
        _slide.Step(dt);
        double v = _slide.Value;
        _pageShift.X = _slideDir * _slideWay * v;
        _ghostShift.X = -_slideDir * _slideWay * (1 - v);
        if (!_slide.Settled) return;
        _sliding = false;
        _pageShift.X = 0;
        PageGhost.Visibility = Visibility.Collapsed;
        PageGhost.Source = null;
        ExpandedPanel.Clip = null;
    }

    // ---------- lists: new entries grow in and push the others, deleted ones shrink away ----------

    private void GrowNew(ItemsControl list, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null || S.Animations == AnimationLevel.Aus) return;
        if (_state != State.Expanded || !list.IsVisible) return;
        var items = e.NewItems.Cast<object>().ToList();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var it in items)
                if (list.ItemContainerGenerator.ContainerFromItem(it) is FrameworkElement c) GrowIn(c, list == ClipList && S.ClipLayout == ClipLayout.Streifen);
        }), DispatcherPriority.Loaded);
    }

    private static void GrowIn(FrameworkElement c, bool sideways = false)
    {
        var st = new ScaleTransform(sideways ? 0 : 1, sideways ? 1 : 0);
        c.LayoutTransform = st;
        var d = TimeSpan.FromMilliseconds(320);
        var a = new DoubleAnimation(0, 1, d) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
        a.Completed += (_, _) => { if (ReferenceEquals(c.LayoutTransform, st)) c.LayoutTransform = Transform.Identity; };
        st.BeginAnimation(sideways ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty, a);
        c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(60) });
    }

    /// <summary>The row folds away (the ones below move up), then <paramref name="done"/> removes it for real.</summary>
    private void ShrinkOut(FrameworkElement c, Action done, bool sideways = false)
    {
        if (c == null || S.Animations == AnimationLevel.Aus) { done(); return; }
        c.IsHitTestVisible = false;
        var st = new ScaleTransform(1, 1);
        c.LayoutTransform = st;
        var d = TimeSpan.FromMilliseconds(220);
        var a = new DoubleAnimation(1, 0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        bool fired = false;
        a.Completed += (_, _) => { if (fired) return; fired = true; done(); c.LayoutTransform = Transform.Identity; c.IsHitTestVisible = true; c.BeginAnimation(OpacityProperty, null); };
        st.BeginAnimation(sideways ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty, a);
        c.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)));
    }

    private static FrameworkElement ContainerOf(ItemsControl list, object sender) =>
        sender is DependencyObject d ? ItemsControl.ContainerFromElement(list, d) as FrameworkElement : null;

    // ---------- closing: the timer's digits fly into the pill, the download bar into its bubble ----------

    private void FlyHomeOnCollapse()
    {
        if (S.Animations != AnimationLevel.An || _state != State.Expanded) return;
        if (_tab == "Timer" && H.Timer.Active && TimerRunning.IsVisible && !_alarmMode)
            FlyHome(TimerBigText, () => TrailText.IsVisible ? TrailText : Bubble.IsVisible ? Bubble : null);
        else if (_tab == "Live" && AnyDl && SteamRow.IsVisible)
            FlyHome(SteamRow, () => Bubble.IsVisible ? Bubble : TrailText.IsVisible ? TrailText : null);
    }

    private void FlyHome(FrameworkElement src, Func<FrameworkElement> target)
    {
        BitmapSource shot;
        Point from;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(src.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(src.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(src), null, new Rect(0, 0, src.ActualWidth, src.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            shot = rtb;
            from = src.TranslatePoint(new Point(src.ActualWidth / 2, src.ActualHeight / 2), Root);
        }
        catch { return; }
        double w0 = src.ActualWidth, h0 = src.ActualHeight;
        var img = new Image { Source = shot, Width = w0, Height = h0, RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false };
        var sc = new ScaleTransform();
        img.RenderTransform = sc;
        FlyLayer.Children.Add(img);
        Canvas.SetLeft(img, from.X - w0 / 2);
        Canvas.SetTop(img, from.Y - h0 / 2);

        var sx = new Spring(from.X, 0.42, 0.78, 0.2);
        var sy = new Spring(from.Y, 0.42, 0.78, 0.2);
        var ss = new Spring(1, 0.42, 0.85, 0.005);
        FrameworkElement hidden = null;
        var start = DateTime.Now;
        var last = start;
        EventHandler step = null;
        step = (_, _) =>
        {
            var now = DateTime.Now;
            double dt = Math.Min(0.05, (now - last).TotalSeconds);
            last = now;
            var t = target();
            if (t != null && t.ActualHeight > 0)
            {
                if (hidden != t) { if (hidden != null) hidden.Opacity = 1; hidden = t; t.Opacity = 0; }
                try
                {
                    var c = t.TranslatePoint(new Point(t.ActualWidth / 2, t.ActualHeight / 2), Root);
                    sx.Target = c.X; sy.Target = c.Y;
                    ss.Target = Math.Clamp(t.ActualHeight / h0, 0.15, 1);
                }
                catch { }
            }
            sx.Step(dt); sy.Step(dt); ss.Step(dt);
            Canvas.SetLeft(img, sx.Value - w0 / 2);
            Canvas.SetTop(img, sy.Value - h0 / 2);
            sc.ScaleX = sc.ScaleY = Math.Max(0.05, ss.Value);
            double ms = (now - start).TotalMilliseconds;
            if ((sx.Settled && sy.Settled && ms > 250) || ms > 1200)
            {
                CompositionTarget.Rendering -= step;
                FlyLayer.Children.Remove(img);
                if (hidden != null) hidden.Opacity = 1;
            }
        };
        CompositionTarget.Rendering += step;
    }

    // ---------- the counter on a tab hops when it goes up ----------

    private int _lastUnreadShown;

    private void BadgeHop(int unread)
    {
        if (unread > _lastUnreadShown && _tabBtns != null && _tabBtns.TryGetValue("Notify", out var b) && b.IsVisible) HopTab(b);
        _lastUnreadShown = unread;
    }

    // ---------- several screens: the island follows the window you work in (Settings, off by default) ----------

    private static string _followScreen;

    private void WireFollowScreen()
    {
        H.Foreground.Changed += h => Ui.Run(() =>
        {
            if (!S.FollowActiveScreen || h == IntPtr.Zero || H.Foreground.IsOwn(h) || _state != State.Collapsed || _moveMode) return;
            var scr = WF.Screen.FromHandle(h);
            if (scr.DeviceName == IslandScreen().DeviceName) return;
            _followScreen = scr.DeviceName;
            Reposition();
        });
    }
}
