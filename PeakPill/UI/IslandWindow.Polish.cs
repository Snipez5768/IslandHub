using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PeakPill.Core;
using PeakPill.Services;
using Shape = System.Windows.Shapes.Shape;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

/// <summary>Polish round 2026-11-15 (user's order): open/close, page switch, bubble birth, bell, new song, mute, tuck – plus the missing moments.</summary>
public partial class IslandWindow
{
    // ---------- the cover flies (Apple's matched geometry): pill cover ⇄ big cover on the Musik page ----------

    private readonly Spring _flyX = new(0), _flyY = new(0), _flyS = new(20);
    private FrameworkElement _flyTo, _flyHide;
    private bool _flying;
    private double _flyR0 = 5, _flyR1 = 12, _flyS0 = 20, _flyS1 = 78;

    /// <summary>Starts the flight from <paramref name="from"/> to <paramref name="to"/>; both real covers stay hidden meanwhile.</summary>
    private void StartCoverFlight(FrameworkElement from, FrameworkElement to)
    {
        if (H.Media.Cover == null) return;
        bool small = from == LeadCover;
        StartFlight(from, to, H.Media.Cover, small ? 5 : 12, small ? 12 : 5);
    }

    /// <summary>
    /// Any element flies between two places in the island (matched geometry): a picture of it is carried over,
    /// the real ones stay hidden meanwhile. Corners blend from <paramref name="r0"/> to <paramref name="r1"/>.
    /// </summary>
    private void StartFlight(FrameworkElement from, FrameworkElement to, ImageSource picture, double r0, double r1)
    {
        if (S.Animations == AnimationLevel.Aus || picture == null || !from.IsVisible) return;
        try
        {
            bool reversing = _flying; // opened and closed again mid-flight: turn around from where the cover is now
            if (reversing)
            {
                if (_flyTo != null) _flyTo.Opacity = 1;
                if (_flyHide != null) _flyHide.Opacity = 1;
            }
            IslandContent.UpdateLayout();
            if (!reversing)
            {
                var p = from.TranslatePoint(new Point(0, 0), IslandContent);
                double size = from.ActualWidth;
                if (size < 4) return;
                _flyX.Snap(p.X); _flyY.Snap(p.Y); _flyS.Snap(size);
            }
            CoverFlyerImg.ImageSource = picture;
            _flyR0 = r0; _flyR1 = r1;
            _flyS0 = Math.Max(1, _flyS.Value);
            _flyS1 = Math.Max(1, to.ActualWidth);
            _flyTo = to;
            _flyHide = from;
            from.Opacity = 0;
            to.Opacity = 0;
            _flying = true;
            PlaceFlyer();
            CoverFlyer.Visibility = Visibility.Visible;
            StartMotion();
        }
        catch { EndCoverFlight(); }
    }

    /// <summary>One frame: the goal is read live – the page is still scaling and the island still growing.</summary>
    private void StepCoverFlight(double dt)
    {
        if (!_flying) return;
        if (_flyTo == null || !_flyTo.IsVisible) { EndCoverFlight(); return; }
        try
        {
            // the page is laid out at its final width and centred, while the island still grows or shrinks around it:
            // aim at the cover's distance from the left edge of the FINISHED island, measured from today's left edge –
            // so the cover hugs the edge and never leaves the island
            var p = _flyTo.TranslatePoint(new Point(0, 0), IslandContent);
            double inset = p.X + (_targetW - IslandContent.ActualWidth) / 2;
            _flyX.Target = inset; _flyY.Target = p.Y; _flyS.Target = _flyTo.ActualWidth;
        }
        catch { }
        _flyX.Step(dt); _flyY.Step(dt); _flyS.Step(dt);
        PlaceFlyer();
        if (_flyX.Settled && _flyY.Settled && _flyS.Settled) EndCoverFlight();
    }

    private void PlaceFlyer()
    {
        double s = Math.Max(4, _flyS.Value);
        CoverFlyer.Width = CoverFlyer.Height = s;
        CoverFlyer.Margin = new Thickness(_flyX.Value, _flyY.Value, 0, 0);
        // the corners blend with the size (e.g. 5 px on the small cover, 12 px on the big one)
        double t = Math.Abs(_flyS1 - _flyS0) < 0.5 ? 1 : Math.Clamp((s - _flyS0) / (_flyS1 - _flyS0), 0, 1);
        double r = _flyR0 + (_flyR1 - _flyR0) * t;
        CoverFlyer.CornerRadius = new CornerRadius(r * s / Math.Max(1, _flyS0 + (_flyS1 - _flyS0) * t));
    }

    /// <summary>A picture of an element as it looks right now (through a brush – rendering it directly would carry its offset).</summary>
    private static BitmapSource Snapshot(FrameworkElement e)
    {
        try
        {
            if (e.ActualWidth < 1 || e.ActualHeight < 1) return null;
            var dpi = VisualTreeHelper.GetDpi(e);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * dpi.DpiScaleX * 2), (int)Math.Ceiling(e.ActualHeight * dpi.DpiScaleY * 2),
                                             dpi.PixelsPerInchX * 2, dpi.PixelsPerInchY * 2, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }

    private void EndCoverFlight()
    {
        _flying = false;
        CoverFlyer.Visibility = Visibility.Collapsed;
        if (_flyTo != null) _flyTo.Opacity = 1;
        if (_flyHide != null) _flyHide.Opacity = 1;
        _flyTo = _flyHide = null;
    }

    // ---------- tucked away: show where the island went ----------

    /// <summary>
    /// The island has just been hidden: the strip grows out of where it sat and glows softly for a moment, then fades
    /// (unless the pointer is near). The very first time it glows twice and stays a little longer.
    /// </summary>
    private DateTime _tuckIntroUntil;

    private void TuckIntro()
    {
        if (S.Animations == AnimationLevel.Aus || _captureHidden) return;
        bool first = !S.TuckHintShown;
        if (first) { S.TuckHintShown = true; App.SaveSettings(); }
        double hold = first ? 2.4 : 1.0;
        _tuckIntroUntil = DateTime.Now.AddSeconds(hold + 0.5); // the near-check waits until the intro is over
        _stripShown = false;                                  // … and then shows the strip again only near the pointer
        TuckStrip.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(hold + 0.5),
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold))),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + 0.5)), new SineEase())
            }
        });
        // it widens out of the island's middle …
        TuckBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
        // … and glows (twice the first time)
        var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(first ? 1.8 : 0.9) };
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        if (first)
        {
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.2), new SineEase()));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.15, KeyTime.FromPercent(0.5), new SineEase()));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.7), new SineEase()));
        }
        else glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.7, KeyTime.FromPercent(0.35), new SineEase()));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new SineEase()));
        TuckGlow.BeginAnimation(DropShadowEffect.OpacityProperty, glow);
    }

    // ---------- mic muted / on again: the slash moves (Apple's silent switch) ----------

    /// <summary>
    /// "Stumm": the plain mic shows and the slash draws itself across it. "An" (after muting): the slash starts drawn
    /// and pulls back off the mic. Any other hint: no slash.
    /// </summary>
    private void SlashMoment(string icon, string key, Brush color)
    {
        HintSlash.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        HintSlash.BeginAnimation(OpacityProperty, null);
        bool mute = icon == "mic.slash", unmute = icon == "mic" && key == "mic";
        if (!(mute || unmute) || S.Animations == AnimationLevel.Aus) { HintSlash.Opacity = 0; return; }
        HintIcon.Kind = "mic";
        HintSlash.Stroke = mute ? color : Brushes.White;
        HintSlash.Opacity = 1;
        var ease = new CubicEase { EasingMode = mute ? EasingMode.EaseOut : EasingMode.EaseIn };
        // dash offset 9 = nothing drawn, 0 = the whole slash; −9 = drawn off the far end
        var draw = mute
            ? new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = ease }
            : new DoubleAnimation(0, -9, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(160), EasingFunction = ease };
        HintSlash.StrokeDashOffset = mute ? 9 : 0;
        HintSlash.BeginAnimation(Shape.StrokeDashOffsetProperty, draw);
        if (unmute) HintSlash.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)) { BeginTime = TimeSpan.FromMilliseconds(400) });
    }

    // ---------- new song: the bars take the new cover colour softly ----------

    private SolidColorBrush _barBrush;

    /// <summary>All bars share one brush; a new colour blends in over 0.45 s instead of jumping.</summary>
    private void BarColor(Brush wanted)
    {
        var c = (wanted as SolidColorBrush)?.Color ?? Colors.White;
        if (_barBrush == null)
        {
            _barBrush = new SolidColorBrush(c);
            foreach (var bar in new[] { EqBar1, EqBar2, EqBar3, EqBar4, EqBar5, MEqBar1, MEqBar2, MEqBar3, MEqBar4 }) bar.Fill = _barBrush;
            _barBrushGoal = c;
            return;
        }
        if (_barBrushGoal == c) return;
        _barBrushGoal = c;
        if (S.Animations == AnimationLevel.Aus) { _barBrush.BeginAnimation(SolidColorBrush.ColorProperty, null); _barBrush.Color = c; return; }
        _barBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(450)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private Color? _barBrushGoal;

    // ---------- system moments (polish 2026-11-15): volume, brightness, headphones, charger ----------

    private string _levelKey;
    private int _hintHoldMs;

    private void InitDeviceMoments()
    {
        H.Devices.Volume += (v, muted) => ShowLevel("vol", muted || v < 0.005 ? "speaker.slash" : "speaker", muted ? 0 : v);
        H.Devices.Brightness += v => ShowLevel("sun", "sun", v);
        H.Devices.Headphones += OnHeadphones;
        H.Devices.Charger += OnCharger;
        H.Devices.BatteryLow += OnBatteryLow;
        H.Devices.Start();
    }

    /// <summary>
    /// Volume / brightness: the pill widens to symbol · bar · number. While the key is held the bar just follows
    /// (no new pop), and the notice stays until 1.5 s after the last change. The open island has its own slider.
    /// </summary>
    public void ShowLevel(string key, string icon, double value)
    {
        if (!S.LevelInIsland || _captureHidden || _state == State.Peek || _state == State.Expanded) return;
        if (_tucked) return;
        value = Math.Clamp(value, 0, 1);
        string pct = $"{Math.Round(value * 100)} %";
        bool running = _state == State.Hint && _levelKey == key && HintLevel.Visibility == Visibility.Visible;
        if (running)
        {
            HintIcon.Kind = icon;
            HintText.Text = pct;
            HintRoll.Text = pct; // rolls digit by digit (2026-11-17)
            int gen = ++_hintGen;
            _ = RunHint(gen, false);
        }
        else
        {
            ShowHint(icon, pct, Brushes.White);
            if (_state != State.Hint) return;
            HintLevel.Visibility = Visibility.Visible;
            HintText.MinWidth = 38; // "100 %" – the bar does not jump when the number gets a digit
            HintText.TextAlignment = TextAlignment.Right;
            // the number is drawn by HintRoll from now on; HintText keeps only the room
            HintRoll.Roll = false;
            HintRoll.Text = pct;
            HintRoll.Roll = true;
            HintRoll.Visibility = Visibility.Visible;
            HintText.BeginAnimation(OpacityProperty, null);
            HintText.Opacity = 0;
            AnimateSize(250, PillH + NotchExtra, PillH / 2, true);
            HintLevelScale.ScaleX = value;
        }
        _levelKey = key;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HintLevelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
    }

    /// <summary>A small ring on the right of a hint (battery of the headphones, the laptop battery).</summary>
    private void HintRing(double value, Brush color)
    {
        var ring = new Ring { Width = 20, Height = 20, Thickness = 2.4, Value = 0, Stroke = color, Track = Tint(color, 0.22), VerticalAlignment = VerticalAlignment.Center };
        HintActions.Children.Clear();
        HintActions.Children.Add(ring);
        HintActions.Margin = new Thickness(10, 0, 0, 0);
        HintActions.Visibility = Visibility.Visible;
        // the ring fills up to the level once the island has opened
        ring.BeginAnimation(Ring.ValueProperty, new DoubleAnimation(0, value * 100, TimeSpan.FromMilliseconds(650))
            { BeginTime = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        HintText.Measure(new Size(double.PositiveInfinity, 40));
        double w = Math.Clamp(10 + 22 + 12 + HintText.DesiredSize.Width + 10 + 20 + 14, S.Shape == IslandShape.Notch ? 200 : IdleW, 330);
        AnimateSize(w, PillH + NotchExtra, PillH / 2, true);
    }

    private void OnHeadphones(string name, double? battery)
    {
        if (!S.DeviceInIsland) return;
        _hintHoldMs = 2600;
        ShowHint("headphones", name, Brushes.White);
        if (_state != State.Hint) return;
        if (battery is { } b) HintRing(b, b <= 0.2 ? (Brush)FindResource("Danger") : (Brush)FindResource("Green"));
    }

    private void OnCharger(bool plugged, double level)
    {
        if (!S.PowerInIsland) return;
        _hintHoldMs = 2200;
        var green = (Brush)FindResource("Green");
        ShowHint(plugged ? "bolt" : "battery", plugged ? $"Lädt · {Math.Round(level * 100)} %" : $"{Math.Round(level * 100)} %", plugged ? green : Brushes.White);
        if (_state != State.Hint) return;
        if (plugged) LiquidBolt(level, green); // the bolt fills like a liquid up to the level
        HintRing(level, plugged ? green : level <= 0.2 ? (Brush)FindResource("Danger") : Brushes.White);
    }

    private bool _lowYawned;

    private void OnBatteryLow(double level)
    {
        if (!S.PowerInIsland) return;
        // the eyes yawn first (tired like the battery), then the warning comes
        if (!_lowYawned && _state == State.Collapsed && EyesYawn()) { _lowYawned = true; After(1400, () => { OnBatteryLow(level); _lowYawned = false; }); return; }
        _hintHoldMs = 3000;
        var red = (Brush)FindResource("Danger");
        ShowHint("battery", $"Akku {Math.Round(level * 100)} %", red);
        if (_state != State.Hint) return;
        HintRing(level, red);
    }

    // ---------- page switch with a direction (Apple: content follows the order of the pages) ----------

    private readonly TranslateTransform _pageShift = new(), _ghostShift = new();

    private BitmapSource SnapPage()
    {
        try
        {
            if (Pages.ActualWidth < 1 || Pages.ActualHeight < 1) return null;
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(Pages.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Pages.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            // through a brush: rendering the element itself would carry its offset inside the island
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(Pages), null, new Rect(0, 0, Pages.ActualWidth, Pages.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }

    /// <summary>
    /// "Karussell mit Feder" (user's pick 2026-11-17, replaces the blurred short slide that stuttered): the old page and
    /// the new one move together by the whole width like swiping on the iPhone, and the new one springs a little past
    /// its place. No blur – WPF draws it without the graphics card. Reduced animations: a short soft slide instead.
    /// </summary>
    private void PageSlide(int dir, BitmapSource old)
    {
        bool full = S.Animations == AnimationLevel.An;
        double w = Pages.ActualWidth;
        double way = full ? w + 24 : 14;
        var t = TimeSpan.FromMilliseconds(full ? 420 : 220);
        IEasingFunction ease = full ? new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };

        // only during the move: nothing shows beside the page area (the pages themselves may reach into the margins)
        ExpandedPanel.Clip = new RectangleGeometry(new Rect(-1, -40, ExpandedPanel.ActualWidth + 2, ExpandedPanel.ActualHeight + 80));

        PageGhost.Source = old;
        PageGhost.Width = w;
        PageGhost.Effect = null;
        PageGhost.RenderTransform = _ghostShift;
        PageGhost.Visibility = Visibility.Visible;
        PageGhost.BeginAnimation(OpacityProperty, full ? null : new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)));
        PageGhost.Opacity = 1;
        var outAnim = new DoubleAnimation(0, -dir * way, t) { EasingFunction = ease };
        outAnim.Completed += (_, _) =>
        {
            PageGhost.Visibility = Visibility.Collapsed;
            PageGhost.Source = null;
            ExpandedPanel.Clip = null;
        };

        Pages.Effect = null;
        Pages.RenderTransform = _pageShift;
        // start values right away – the animations only begin with the next tick, until then these are drawn
        _ghostShift.BeginAnimation(TranslateTransform.XProperty, null);
        _pageShift.BeginAnimation(TranslateTransform.XProperty, null);
        _ghostShift.X = 0;
        _pageShift.X = dir * way;
        if (full)
        {
            // the slide rides the island's own spring (2026-11-17) – a taller page and the slide are one move
            _slideDir = dir;
            _slideWay = way;
            _slide.Snap(1);
            _slide.Target = 0;
            _sliding = true;
            StartMotion();
        }
        else
        {
            _ghostShift.BeginAnimation(TranslateTransform.XProperty, outAnim);
            _pageShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * way, 0, t) { EasingFunction = ease });
        }
        Pages.BeginAnimation(OpacityProperty, full ? null : new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
        Pages.Opacity = 1;
        // the picture of the old page must be there in the very next frame – else the island flashes empty once
        ExpandedPanel.UpdateLayout();
    }

    // ---------- the bar under the active tab glides to the new one, stretching on the way ----------

    private bool _indicatorAnimate;
    private TranslateTransform _indicatorShift => TabIndicatorShift;

    /// <summary>After the layout (names in the quiet bar come and go): under the middle of the checked tab.</summary>
    private void PlaceTabIndicator()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            bool animate = _indicatorAnimate;
            _indicatorAnimate = false;
            var rb = _tabBtns?.Values.FirstOrDefault(r => r.IsChecked == true && r.IsVisible);
            if (rb == null || !TabHeaderBar.IsVisible) { TabIndicator.Opacity = 0; return; }
            double x;
            try { x = rb.TranslatePoint(new Point(rb.ActualWidth / 2, 0), TabHeaderBar).X - TabIndicator.Width / 2; }
            catch { return; }
            TabIndicator.Opacity = 1;
            if (!animate || S.Animations == AnimationLevel.Aus || Math.Abs(_indicatorShift.X - x) < 0.5)
            {
                _indicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
                _indicatorShift.X = x;
                return;
            }
            var t = TimeSpan.FromMilliseconds(420);
            _indicatorShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, t) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } });
            // stretched in the middle of the way, like a drop pulled along
            var stretch = new DoubleAnimationUsingKeyFrames { Duration = t };
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.9, KeyTime.FromPercent(0.4), new SineEase { EasingMode = EasingMode.EaseOut }));
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }));
            TabIndicatorScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

#if DEBUG
    /// <summary>Film strips of one scene: <c>--polish-test &lt;dir&gt; &lt;scene&gt;</c>. Nothing of the user's is saved.</summary>
    public async Task PolishTestAsync(string dir, string scene)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        H.Agents.ForTest();
        if (_tucked) Untuck();
        var anim = S.Animations;
        S.Animations = AnimationLevel.An;
        bool peek = S.TrackPeek;
        S.TrackPeek = false;
        var frames = new List<BitmapSource>();
        var xs = new List<string>(); // island centre per frame – horizontal sloshing shows here
        double fh = 200;
        void Frame()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(fh * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, fh));
            rtb.Render(bg); rtb.Render(Root); rtb.Freeze(); frames.Add(rtb);
            try { var c = Island.TranslatePoint(new Point(Island.ActualWidth / 2, 0), Root); var l = Island.TranslatePoint(new Point(0, 0), Root); var cv = LeadCover.TranslatePoint(new Point(0, 0), Root); xs.Add($"{frames.Count}: centre {c.X:0.0}  w {Island.ActualWidth:0}  left {l.X:0.0}  right {l.X + Island.ActualWidth:0.0}  cover {cv.X:0.0}"); } catch { }
        }
        async Task Film(int count, int ms) { for (int i = 0; i < count; i++) { Frame(); await Task.Delay(ms); } }
        void Save(string name)
        {
            if (frames.Count == 0) return;
            // two columns keep the strip readable
            int cols = frames.Count > 8 ? 2 : 1, rows = (frames.Count + cols - 1) / cols;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                for (int i = 0; i < frames.Count; i++)
                {
                    int c = i / rows, r = i % rows;
                    dc.DrawImage(frames[i], new Rect(c * ActualWidth, r * fh, ActualWidth, fh));
                    var ft = new FormattedText((i + 1).ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, 1);
                    dc.DrawText(ft, new Point(c * ActualWidth + 6, r * fh + 4));
                }
            var dpi = VisualTreeHelper.GetDpi(this);
            var all = new RenderTargetBitmap((int)(cols * ActualWidth * dpi.DpiScaleX), (int)(rows * fh * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            all.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(all));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            frames.Clear();
        }
        BitmapSource Pic(Color a, Color b)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, 64, 64));
            var r = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            r.Render(dv); r.Freeze();
            return r;
        }
        var orange = Pic(Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F));
        H.Media.Fake("Nachtlicht", "Lena Ost", orange, false);
        Collapse();
        await Task.Delay(1500);

        try
        {
            switch (scene)
            {
                case "open":
                    Expand("Music", true);
                    await Film(14, 45);
                    Save("1_open");
                    await Task.Delay(1200);
                    Collapse();
                    await Film(12, 45);
                    Save("2_close");
                    break;
                case "bubble":
                    fh = 70;
                    H.Agents.Demo("Test", "Bearbeitet Datei", 2, 5, 60, AgentState.Running);
                    await Film(16, 50);
                    Save("1_birth");
                    break;
                case "level":
                    fh = 60;
                    for (int i = 0; i < 14; i++)
                    {
                        ShowLevel("vol", "speaker", 0.30 + i * 0.03);
                        Frame();
                        await Task.Delay(60);
                    }
                    await Film(4, 400);
                    Save("1_volume");
                    await Task.Delay(1500);
                    OnHeadphones("AirPods Pro", 0.8);
                    await Film(10, 120);
                    Save("2_headphones");
                    await Task.Delay(2500);
                    OnCharger(true, 0.64);
                    await Film(10, 120);
                    Save("3_charger");
                    break;
                case "ticker":
                    fh = 130;
                    OnNotificationArrived(new IslandNotification { Id = 0, AppName = "WhatsApp", Title = "Anna", Text = "Kommst du heute noch vorbei? Ich hab Kuchen gebacken.", Time = DateTime.Now });
                    await Film(16, 90);
                    Save("1_bell_ticker");
                    OpenTicker();
                    await Film(12, 45);
                    Save("2_card");
                    break;
                case "song":
                    fh = 60;
                    var blue = Pic(Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58));
                    H.Media.Fake("Morgenrot", "Kai Wendt", blue, true);
                    await Film(16, 50);
                    Save("1_song");
                    break;
                case "mute":
                    fh = 60;
                    ShowHint("mic.slash", "Stumm", (Brush)FindResource("Danger"), key: "mic");
                    MuteMoment();
                    await Film(10, 60);
                    Save("1_muted");
                    await Task.Delay(1800);
                    ShowHint("mic", "An", null, key: "mic");
                    await Film(10, 60);
                    Save("2_on");
                    break;
                case "tuck":
                    bool hintShown = S.TuckHintShown;
                    S.TuckHintShown = false; // film the first-time version (the setting is not saved in tests)
                    fh = 140;
                    Expand("Music", true);
                    await Task.Delay(1200);
                    Tuck();
                    await Film(8, 40);
                    await Film(10, 150);
                    Save("1_tuck_first");
                    fh = 50;
                    Untuck();
                    Collapse();
                    await Task.Delay(1500);
                    Tuck();
                    await Film(10, 120);
                    Save("2_tuck_again");
                    Untuck();
                    S.TuckHintShown = hintShown;
                    break;
                case "place":
                {
                    var log = new System.Text.StringBuilder();
                    double size = S.IslandSize, ix = S.IslandX; string scr = S.IslandScreen;
                    void Note(string what) { Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var r); log.AppendLine($"{what}: zoom={_zoom:0.00} window={r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}"); }
                    Note("start");
                    S.IslandSize = 1.3; Reposition(); await Task.Delay(400); Note("size 130 %");
                    fh = 200 * 1.3;
                    Expand("Music", true);
                    await Task.Delay(900);
                    Frame(); Save("1_size130");
                    Collapse();
                    S.IslandSize = 0.8; Reposition(); await Task.Delay(400); Note("size 80 %");
                    fh = 60;
                    Frame(); Save("2_size80");
                    S.IslandSize = 1.0; Reposition(); await Task.Delay(300);
                    EnterMoveMode();
                    await Task.Delay(700);
                    Frame(); Save("3_move_mode");
                    S.IslandX = -0.25; Reposition(); await Task.Delay(300); Note("moved left 25 %");
                    var other = WF.Screen.AllScreens.FirstOrDefault(s => !s.Primary);
                    if (other != null) { S.IslandScreen = other.DeviceName; S.IslandX = 0; Reposition(); await Task.Delay(500); Note($"other screen {other.DeviceName}"); }
                    ExitMoveMode();
                    S.IslandSize = size; S.IslandX = ix; S.IslandScreen = scr; Reposition();
                    File.WriteAllText(Path.Combine(dir, "place.txt"), log.ToString());
                    break;
                }
                case "reply":
                    // the card with "Antworten" and the open message field – nothing is sent
                    fh = 170;
                    OnNotificationArrived(new IslandNotification { Id = 0, AppName = "WhatsApp", Title = "Anna", Text = "Kommst du heute noch vorbei?", Time = DateTime.Now });
                    await Task.Delay(2200);
                    OpenTicker();
                    await Task.Delay(900);
                    Frame(); Save("1_card");
                    OpenReplyBox();
                    PeekReplyBox.Text = "Ja, so gegen 7!";
                    await Task.Delay(700);
                    Frame(); Save("2_reply_box");
                    _sticky = false;
                    EndPeek();
                    break;
                case "eyes":
                    // music plays: the eyes come out left of the cover (the island widens), go again; then pause → no bars
                    fh = 60;
                    _sticky = false;
                    Collapse();
                    H.Media.Fake("Nachtlicht", "Lena Ost", orange, false);
                    await Task.Delay(1500);
                    Frame();
                    var zoom = new List<BitmapSource>();
                    async Task Zoom(int count, int ms)
                    {
                        // 4× around the island's left end: the lids, the blink and the gaze are a few pixels only
                        for (int i = 0; i < count; i++)
                        {
                            var dpi = VisualTreeHelper.GetDpi(this);
                            var full = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(fh * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                            var bgv = new DrawingVisual();
                            using (var dc = bgv.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, fh));
                            full.Render(bgv); full.Render(Root);
                            var l = Island.TranslatePoint(new Point(0, 0), Root);
                            int x0 = (int)Math.Max(0, (l.X - 8) * dpi.DpiScaleX), y0 = (int)Math.Max(0, (l.Y - 6) * dpi.DpiScaleY);
                            int cw = (int)Math.Min(90 * dpi.DpiScaleX, full.PixelWidth - x0), ch = (int)Math.Min(46 * dpi.DpiScaleY, full.PixelHeight - y0);
                            var crop = new CroppedBitmap(full, new Int32Rect(x0, y0, cw, ch));
                            var big = new TransformedBitmap(crop, new ScaleTransform(4 / dpi.DpiScaleX, 4 / dpi.DpiScaleY));
                            big.Freeze(); zoom.Add(big);
                            await Task.Delay(ms);
                        }
                    }
                    void SaveZoom(string name)
                    {
                        if (zoom.Count == 0) return;
                        int cols = 4, rows = (zoom.Count + cols - 1) / cols;
                        double zw = zoom[0].PixelWidth, zh = zoom[0].PixelHeight;
                        var dv = new DrawingVisual();
                        using (var dc = dv.RenderOpen())
                            for (int i = 0; i < zoom.Count; i++)
                            {
                                int c = i % cols, r = i / cols;
                                dc.DrawImage(zoom[i], new Rect(c * zw, r * zh, zw, zh));
                                dc.DrawText(new FormattedText((i + 1).ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.White, 1), new Point(c * zw + 6, r * zh + 4));
                            }
                        var all = new RenderTargetBitmap((int)(cols * zw), (int)(rows * zh), 96, 96, PixelFormats.Pbgra32);
                        all.Render(dv);
                        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(all));
                        using var fs = File.Create(Path.Combine(dir, name + ".png")); enc.Save(fs);
                        zoom.Clear();
                    }
                    // the real pointer must not open the island or move the eyes meanwhile
                    Root.IsHitTestVisible = false;
                    _eyeTimer.Stop();
                    if (_state != State.Collapsed) { Collapse(); await Task.Delay(900); }
                    _eyeLast = new Native.POINT { X = 0, Y = 2000 }; // the pointer sits low and left
                    ShowEyes();
                    await Zoom(28, 40);
                    SaveZoom("0_wake_zoom");
                    _blinkAt = DateTime.Now; StartMotion();
                    await Zoom(8, 25);
                    SaveZoom("0_blink_zoom");
                    _eyesUntil = DateTime.Now;
                    HideEyes();
                    await Zoom(28, 40);
                    SaveZoom("0_sleep_zoom");
                    await Task.Delay(600);
                    ShowEyes();
                    await Film(12, 45);
                    Glance(-1.3, 1.5, 0); Frame();
                    _eyesUntil = DateTime.Now; // as if the pointer had left a while ago
                    HideEyes();
                    await Film(14, 45);
                    Save("1_eyes_music");
                    Root.IsHitTestVisible = true;
                    File.WriteAllLines(Path.Combine(dir, "edges.txt"), xs);
                    H.Media.FakePlaying(false);
                    await Task.Delay(900);
                    Frame();
                    H.Media.FakePlaying(true);
                    await Task.Delay(900);
                    Frame();
                    Save("2_pause_play");
                    break;
                case "lyrics":
                    fh = 320;
                    H.Media.Fake("Bohemian Rhapsody", "Queen", orange, true);
                    Expand("Music", true);
                    await Task.Delay(900);
                    MusicLyrics_Click(null, null);
                    for (int i = 0; i < 30 && LyricsHint.Text.Contains("gesucht"); i++) await Task.Delay(300);
                    if (_state != State.Expanded) { Expand("Music", true); await Task.Delay(300); if (!_lyricsOpen) MusicLyrics_Click(null, null); }
                    await Task.Delay(1000);
                    Frame(); Save("1_lyrics");
                    File.WriteAllText(Path.Combine(dir, "lyrics.txt"), $"{LyricsHint.Text}\n{_lyrics.Count} Zeilen\nSpotify verbunden: {H.Spotify.Connected}, neu verbinden nötig: {H.Spotify.NeedsReconnect}");
                    MusicLyrics_Click(null, null);
                    break;
                case "agentring":
                {
                    // Claude works (no plan): the ring turns – the bubble is refilled again and again, the angle must keep going
                    var log = new System.Text.StringBuilder();
                    H.Agents.Demo("Test", "Bearbeitet Datei", 0, 0, 30, AgentState.Running);
                    await Task.Delay(1500);
                    var (ring, icon) = Bubble.Tag is Act.Agent ? (BubbleRing, BubbleIcon) : (Bubble2Ring, Bubble2Icon);
                    for (int i = 0; i < 16; i++)
                    {
                        UpdatePill(true); // what the half-second refresh does
                        await Task.Delay(120);
                        double ra = (ring.RenderTransform as RotateTransform)?.Angle ?? -1, ia = (icon.RenderTransform as RotateTransform)?.Angle ?? -1;
                        log.AppendLine($"{i,2}: Ring {ra,6:0.0}°  Stern {ia,6:0.0}°");
                    }
                    File.WriteAllText(Path.Combine(dir, "angles.txt"), log.ToString());
                    H.Agents.EndDemo();
                    break;
                }
                case "clipconfirm":
                {
                    // only pictures in the clipboard history (test copy – nothing is saved), then "Alles löschen"
                    fh = 320;
                    H.Clipboard.Items.Clear();
                    for (int i = 0; i < 3; i++)
                    {
                        var pic = Pic(Color.FromRgb((byte)(60 + i * 60), 120, 200), Color.FromRgb(240, (byte)(80 + i * 40), 90));
                        var f = Path.Combine(dir, $"pic{i}.png");
                        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(pic));
                        using (var fs = File.Create(f)) enc.Save(fs);
                        H.Clipboard.Items.Add(new ClipItem { Kind = ClipKind.Image, ImageFile = f, ImageWidth = 64, ImageHeight = 64, Source = "Test" });
                    }
                    Expand("Clip", true);
                    await Task.Delay(1200);
                    Frame();
                    ShowClipConfirm(3, 0);
                    await Task.Delay(900);
                    Frame();
                    Save("1_clip_confirm");
                    ClipConfirmCard.Measure(new Size(ExpW, double.PositiveInfinity));
                    File.WriteAllText(Path.Combine(dir, "heights.txt"), $"Insel {Island.ActualHeight:0} px, Karte {ClipConfirmCard.DesiredSize.Height:0} px");
                    HideClipConfirm();
                    break;
                }
                case "today":
                    Expand("Today", true);
                    await Task.Delay(1200);
                    fh = 160;
                    Frame(); Save("1_today");
                    break;
                case "reverse":
                    // open, change your mind after 0.12 s, change it again after 0.15 s – nothing may jump
                    fh = 150;
                    Expand("Music", true);
                    await Film(3, 40);
                    Collapse();
                    await Film(4, 40);
                    Expand("Music", true);
                    await Film(11, 40);
                    Save("1_reverse");
                    File.WriteAllLines(Path.Combine(dir, "centre.txt"), xs);
                    break;
                case "wsizes":
                {
                    var panel = new System.Windows.Controls.WrapPanel { Width = 1100, Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)) };
                    foreach (var kind in new[] { "weather", "clock", "stats", "todo" })
                        foreach (var sz in new[] { "S", "M", "L" })
                            panel.Children.Add(WidgetRegistry.BuildCard(WidgetRegistry.Get(kind), true, null, sz));
                    panel.Measure(new Size(1100, double.PositiveInfinity));
                    panel.Arrange(new Rect(panel.DesiredSize));
                    panel.UpdateLayout();
                    await Task.Delay(300);
                    var rtb = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(panel);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(dir, "sizes.png"))) enc.Save(fs);
                    break;
                }
                case "offer":
                {
                    // the stack is switched on after copying: the island offers the copies from just before.
                    // The real clipboard is NOT touched (the stack is on only inside the service, nothing is promised).
                    fh = 200;
                    var qf = typeof(PeakPill.Services.ClipboardService).GetField("_queueMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var fakes = new[] { "Rechnung Nr. 4711", "Musterstraße 12, Berlin", "demo@example.com" }
                        .Select((t, i) => new ClipItem { Kind = ClipKind.Text, Text = t, Time = DateTime.Now.AddMinutes(-3 + i), Source = "Demo" }).ToList();
                    foreach (var f in fakes) H.Clipboard.Items.Insert(0, f);
                    qf.SetValue(H.Clipboard, true);
                    OfferRecentCopies();
                    await Film(14, 120);
                    Save("1_offer");
                    File.WriteAllText(Path.Combine(dir, "order.txt"), string.Join(" | ", H.Clipboard.RecentCopies(TimeSpan.FromMinutes(10)).Select(i => i.Text)));
                    qf.SetValue(H.Clipboard, false);
                    foreach (var f in fakes) H.Clipboard.Items.Remove(f);
                    break;
                }
                case "ai":
                {
                    // the KI page with a made-up chat (demo texts only, nothing is sent, the user's chat comes back afterwards)
                    fh = 380;
                    var keep = H.Ai.Messages.ToList();
                    var keys = new Dictionary<string, string>(S.AiKeysProtected);
                    var prov = S.AiProvider;
                    S.AiProvider = AiProvider.Claude;
                    S.AiKeysProtected.Clear();
                    H.Ai.Messages.Clear();
                    Expand("Ai", false);
                    _sticky = true; // stays open without taking the keyboard from the user
                    await Task.Delay(900);
                    Frame(); Save("0_setup");
                    AiService.SetKey(AiProvider.Claude, "demo");
                    UpdateAiUi();
                    await Task.Delay(500);
                    Frame(); Save("1_empty");
                    AiInput.Text = "Wie lange muss ein Ei kochen?";
                    await Task.Delay(200);
                    Frame();
                    AiInput.Clear();
                    var t = H.Ai.DemoAsync("Wie lange muss ein Ei kochen?",
                        "Kommt drauf an, wie du es magst:\n\n- **Weich:** 4–5 Minuten\n- **Wachsweich:** 6–7 Minuten\n- **Hart:** 9–10 Minuten\n\nTipp: Danach kurz in kaltes Wasser legen, dann lässt es sich leichter schälen.",
                        "Claude · Opus 5.5", 45);
                    await Film(15, 150);
                    await t;
                    await Task.Delay(500);
                    Frame(); Save("2_chat");
                    await H.Ai.DemoAsync("Wie dreh ich einen Text in Python um?",
                        "Mit Slicing geht das in einer Zeile:\n\n```python\ntext = \"PeakPill\"\nprint(text[::-1])\n```\n\nDas `[::-1]` läuft rückwärts durch den Text. Mehr dazu: [Python-Doku](https://docs.python.org/3/)",
                        "Claude · Opus 5.5", 20);
                    await Task.Delay(700);
                    Frame(); Save("3_markdown");
                    await H.Ai.DemoAsync("Und auf Englisch?", "Keine Verbindung. Bist du online?", "Claude · Opus 5.5", 0, fail: true);
                    await Task.Delay(700);
                    Frame(); Save("4_error");
                    H.Ai.Messages.Clear();
                    Collapse();
                    await Task.Delay(900);
                    fh = 160;
                    var t2 = H.Ai.DemoAsync("Ein Wort für schnell?", "Zügig, flink oder rasch – je nachdem, wie es klingen soll.", "Claude · Opus 5.5", 30);
                    await t2;
                    await Film(8, 140);
                    Save("5_peek");
                    H.Ai.Messages.Clear();
                    foreach (var m in keep) H.Ai.Messages.Add(m);
                    S.AiKeysProtected.Clear();
                    foreach (var (k, v) in keys) S.AiKeysProtected[k] = v;
                    S.AiProvider = prov;
                    break;
                }
                case "aitools":
                {
                    // the KI acting on the island: steps above the answer, the confirmation card (demo texts only, nothing is sent)
                    fh = 380;
                    var keep = H.Ai.Messages.ToList();
                    H.Ai.Messages.Clear();
                    Expand("Ai", false);
                    _sticky = true;
                    await Task.Delay(900);
                    H.Ai.Messages.Add(new AiMessage { IsUser = true, Text = "Schick die Einkaufsliste an Demo-Handy" });
                    var a = new AiMessage { IsStreaming = true, By = "Gemini · Demo" };
                    H.Ai.Messages.Add(a);
                    await Task.Delay(300);
                    a.Steps = L.T("Schaut auf die Insel") + "\n" + L.T("Sendet mit LocalSend");
                    var answer = new TaskCompletionSource<bool>();
                    a.Ask(L.T("„Milch, Eier, Brot“ an Demo-Handy senden?"), L.T("Senden"), answer);
                    await Task.Delay(700);
                    Frame(); Save("0_confirm");
                    a.Reply(true);
                    await answer.Task;
                    a.Text = "Erledigt – die Liste ist auf **Demo-Handy**.";
                    a.IsStreaming = false;
                    await Task.Delay(700);
                    Frame(); Save("1_done");
                    H.Ai.Messages.Clear();
                    foreach (var m in keep) H.Ai.Messages.Add(m);
                    break;
                }
                case "dictate":
                {
                    // speaking into the KI chat: the recording look (made-up levels), "writing it down", a message;
                    // then 1.5 s of the real microphone only to check the capture (kept in memory, thrown away)
                    fh = 260;
                    var keep = H.Ai.Messages.ToList();
                    H.Ai.Messages.Clear();
                    Expand("Ai", false);
                    _sticky = true;
                    await Task.Delay(900);
                    DictationUi(DictState.Recording);
                    var rnd = new Random(7);
                    for (int i = 0; i < DictBars; i++)
                    {
                        double lvl = Math.Abs(Math.Sin(i * 0.45)) * (0.4 + rnd.NextDouble() * 0.6);
                        AiRecBars.Children.Insert(0, new System.Windows.Shapes.Rectangle { Width = 2.5, Height = 2 + lvl * 16, RadiusX = 1.25, RadiusY = 1.25, Margin = new Thickness(1.2, 0, 1.2, 0), Fill = DictBar, VerticalAlignment = VerticalAlignment.Center });
                    }
                    AiRecTime.Text = "0:07";
                    await Task.Delay(400);
                    Frame(); Save("0_recording");
                    DictationUi(DictState.Writing);
                    AiRecText.Text = L.T($"{AiService.Name(AiProvider.Gemini)} schreibt mit …");
                    await Task.Delay(300);
                    Frame(); Save("1_writing");
                    DictationMessage(L.T("Nichts verstanden."));
                    await Task.Delay(300);
                    Frame(); Save("2_message");
                    DictationUi(DictState.Idle);
                    await Task.Delay(300);
                    Frame(); Save("3_idle");
                    var info = new System.Text.StringBuilder();
                    using (var d = new Dictation())
                    {
                        try
                        {
                            d.Start();
                            float peak = 0;
                            for (int i = 0; i < 15; i++) { await Task.Delay(100); peak = Math.Max(peak, d.TakePeak()); }
                            var wav = await d.StopAsync();
                            info.AppendLine($"capture ok: {wav?.Length ?? 0} bytes, {d.Length.TotalSeconds:0.00} s, peak {peak:0.000}");
                        }
                        catch (Exception ex) { info.AppendLine("capture failed: " + ex.Message); }
                    }
                    // optional: a made-up spoken sentence (Windows' text-to-speech) written down by the user's own key
                    var test = Environment.GetEnvironmentVariable("PP_TEST_WAV");
                    if (test != null && File.Exists(test) && Dictation.Writer() is { } who)
                    {
                        try { info.AppendLine($"{who}: " + await Dictation.TranscribeAsync(File.ReadAllBytes(test), who, CancellationToken.None)); }
                        catch (Exception ex) { info.AppendLine($"{who} failed: " + ex.Message); }
                    }
                    File.WriteAllText(Path.Combine(dir, "result.txt"), info.ToString());
                    H.Ai.Messages.Clear();
                    foreach (var m in keep) H.Ai.Messages.Add(m);
                    break;
                }
                case "aireal":
                {
                    // one real, tiny request with the user's own key: the model must call start_timer (no personal data leaves the PC)
                    fh = 380;
                    var keep = H.Ai.Messages.ToList();
                    H.Ai.Messages.Clear();
                    Expand("Ai", false);
                    _sticky = true;
                    await Task.Delay(900);
                    var send = H.Ai.SendAsync("Start a 1 minute timer. Reply with one word.");
                    for (int i = 0; i < 60 && !send.IsCompleted; i++) await Task.Delay(500);
                    await Task.Delay(800);
                    Frame(); Save("0_answer");
                    var last = H.Ai.Messages.LastOrDefault();
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"),
                        $"steps={last?.Steps?.Replace("\n", " | ")}\ntext={last?.Text}\nfailed={last?.Failed}\ntimer={H.Timer.Active} {H.Timer.RemainingText}");
                    H.Timer.Reset();
                    H.Ai.Messages.Clear();
                    foreach (var m in keep) H.Ai.Messages.Add(m);
                    break;
                }
                case "delight":
                {
                    // 2026-11-17: mute without the red island, Discord hang-up, KI aura + thinking eyes, tab hop, cards drop in
                    Root.IsHitTestVisible = false; // the real mouse must not open the island mid-test
                    var discord = System.Diagnostics.Process.GetProcessesByName("Discord").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(f => f != null)
                                  ?? @"C:\Windows\System32\notepad.exe";
                    fh = 150;
                    ShowHint("mic.slash", "Stumm", (Brush)FindResource("Danger"), key: "mic");
                    MuteMoment();
                    await Film(8, 90);
                    Save("1_mute_pill");
                    await Task.Delay(2500);

                    H.Privacy.Demo((discord, true, false, false, 3));
                    await Task.Delay(500);
                    UpdateLiveUi();
                    Expand("Live", false);
                    _sticky = true;
                    await Task.Delay(900);
                    Frame(); Save("2_live_discord");
                    MuteMoment();
                    await Film(8, 90);
                    Save("3_mute_open");
                    H.Privacy.EndDemo();
                    Collapse();
                    await Task.Delay(1200);

                    var eyes = S.IslandEyes;
                    S.IslandEyes = true;
                    UpdateEyes();
                    var keep = H.Ai.Messages.ToList();
                    var think = H.Ai.DemoAsync("Demo", new string('x', 1) + " " + string.Join(" ", Enumerable.Repeat("Wort", 40)), "Demo", 70);
                    await Film(14, 160);
                    Save("4_aura_eyes");
                    await think;
                    await Task.Delay(1500);
                    S.IslandEyes = eyes;
                    UpdateEyes();
                    H.Ai.Messages.Clear();
                    foreach (var m in keep) H.Ai.Messages.Add(m);
                    _peeks.Clear();
                    EndPeek();
                    await Task.Delay(1200);

                    fh = 200;
                    Expand("Music", false);
                    _sticky = true;
                    await Task.Delay(900);
                    SelectTab("Timer", false);
                    await Film(7, 55);
                    Save("5_tab_hop");
                    Collapse();
                    await Task.Delay(1200);

                    fh = 150;
                    ShowPeek(new PeekInfo { Icon = "bell", Title = "Lena", Text = "Kommst du heute noch vorbei?", Source = "Nachrichten · jetzt", TimeoutMs = 4000 });
                    await Film(9, 55);
                    Save("6_card_drop");
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "hangup":
                {
                    // the hang-up moment with a made-up call next to music – nothing is really hung up
                    Root.IsHitTestVisible = false;
                    var discord = System.Diagnostics.Process.GetProcessesByName("Discord").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(f => f != null)
                                  ?? @"C:\Windows\System32\notepad.exe";
                    H.Privacy.Demo((discord, true, false, false, 3));
                    await Task.Delay(1800);
                    fh = 110;
                    var bubble = CallBubble();
                    File.WriteAllText(Path.Combine(dir, "bubble.txt"), bubble?.Name ?? "no call bubble");
                    Frame();
                    var moment = HangUpMoment();
                    for (int i = 0; i < 22; i++) { Frame(); await Task.Delay(45); }
                    await moment;
                    Save("1_hangup");
                    await Task.Delay(2500);
                    H.Privacy.EndDemo();
                    _hungUpUntil = DateTime.MinValue;
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "bubblehover":
                {
                    // a bubble beside the island opens its own page (the real mouse is not used: the hover end is called directly)
                    Root.IsHitTestVisible = false;
                    var discord = System.Diagnostics.Process.GetProcessesByName("Discord").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(f => f != null)
                                  ?? @"C:\Windows\System32\notepad.exe";
                    H.Privacy.Demo((discord, true, false, false, 3));
                    await Task.Delay(1500);
                    fh = 170;
                    var lines = new List<string>();
                    foreach (var b in new[] { Bubble, Bubble2 }) lines.Add($"{b.Name}: visible={b.Visibility} tag={b.Tag} → {BubbleTab(b) ?? "(usual)"}");
                    File.WriteAllLines(Path.Combine(dir, "bubbles.txt"), lines);
                    Frame();
                    var live = new[] { Bubble, Bubble2 }.FirstOrDefault(b => b.Visibility == Visibility.Visible && b.Tag is Act.Live);
                    if (live != null) { Expand(BubbleTab(live), false); _sticky = true; }
                    await Film(7, 90);
                    Save("1_hover_live");
                    Collapse();
                    H.Privacy.EndDemo();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "livepick":
                {
                    // the screen choice in the island – only shown, no tile is clicked (that would drive the real Discord)
                    Root.IsHitTestVisible = false;
                    var discord = System.Diagnostics.Process.GetProcessesByName("Discord").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(f => f != null)
                                  ?? @"C:\Windows\System32\notepad.exe";
                    H.Privacy.Demo((discord, true, false, false, 3));
                    await Task.Delay(500);
                    UpdateLiveUi();
                    Expand("Live", false);
                    _sticky = true;
                    await Task.Delay(900);
                    fh = 240;
                    var app = H.Privacy.Apps.FirstOrDefault(a => a.IsDiscord) ?? H.Privacy.Apps.FirstOrDefault();
                    var screens = System.Windows.Forms.Screen.AllScreens;
                    File.WriteAllText(Path.Combine(dir, "screens.txt"), string.Join("\n", screens.Select((s, i) => $"{i + 1}: {s.Bounds} primary={s.Primary}")));
                    ShowLivePick(app, screens);
                    await Film(9, 70);
                    Save("1_pick");
                    LivePickCancel_Click(null, null);
                    await Film(6, 70);
                    Save("2_cancel");
                    H.Privacy.EndDemo();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "localsend":
                {
                    // the LocalSend page with made-up devices, then a made-up incoming request (declined in the test)
                    Root.IsHitTestVisible = false;
                    fh = 200;
                    var real = H.LocalSend.Devices.ToList();
                    H.LocalSend.Devices.Clear();
                    Expand("Send", false);
                    _sticky = true;
                    await Task.Delay(900);
                    Frame(); Save("0_empty");
                    H.LocalSend.Devices.Add(new LocalSendDevice { Fingerprint = "a", Alias = "Lenas iPhone", DeviceType = "mobile", DeviceModel = "iPhone", Ip = "192.168.0.20" });
                    H.LocalSend.Devices.Add(new LocalSendDevice { Fingerprint = "b", Alias = "Pixel 9", DeviceType = "mobile", DeviceModel = "Google", Ip = "192.168.0.21" });
                    H.LocalSend.Devices.Add(new LocalSendDevice { Fingerprint = "c", Alias = "Laptop", DeviceType = "desktop", DeviceModel = "Windows", Ip = "192.168.0.22" });
                    await Task.Delay(600);
                    Frame(); Save("1_devices");
                    // the sources: recent copies (one picked), then a text of your own
                    SendSrcClip.IsChecked = true;
                    await Task.Delay(500);
                    _sendClip = H.Clipboard.Items.FirstOrDefault(i => i.IsImage) ?? H.Clipboard.Items.FirstOrDefault();
                    ShowSendPick();
                    await Task.Delay(700);
                    fh = 300;
                    Frame(); Save("1b_clipboard");
                    SendSrcText.IsChecked = true;
                    SendText.Text = "Treffen um 18 Uhr am Bahnhof";
                    await Task.Delay(700);
                    Frame(); Save("1c_text");
                    SendText.Clear();
                    SendSrcText.IsChecked = false;
                    _sendSrc = null;
                    _sendClip = null;
                    ShowSendPick();
                    fh = 200;
                    Collapse();
                    await Task.Delay(1000);
                    var ask = AskReceive(new LocalSendRequest("Lenas iPhone", "mobile", new List<(string, long)> { ("Urlaub.jpg", 2_400_000), ("Notiz.pdf", 180_000) }));
                    await Film(8, 80);
                    Save("2_incoming");
                    EndPeek();
                    H.LocalSend.Devices.Clear();
                    foreach (var d in real) H.LocalSend.Devices.Add(d);
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "aipics":
                {
                    // KI chat with pictures and selectable answers – a made-up picture and answer, nothing is sent
                    Root.IsHitTestVisible = false;
                    fh = 400;
                    var keep = H.Ai.Messages.ToList();
                    var keys = new Dictionary<string, string>(S.AiKeysProtected);
                    H.Ai.Messages.Clear();
                    AiService.SetKey(S.AiProvider, "demo");
                    var demo = Pic(Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2));
                    AskAiAbout(demo);
                    _sticky = true;
                    AiInput.Text = "Was ist das für eine Farbe?";
                    await Task.Delay(900);
                    Frame(); Save("1_attached");
                    var path = _aiPics.FirstOrDefault();
                    _aiPics.Clear();
                    ShowAiPics();
                    AiInput.Clear();
                    H.Ai.Messages.Add(new AiMessage { IsUser = true, Text = "Was ist das für eine Farbe?", Images = path != null ? new List<string> { path } : new() });
                    H.Ai.Messages.Add(new AiMessage { By = "Gemini · gemini-2.5-flash", Text = "Ein **Verlauf** von Blau nach Lila:\n\n- oben links: `#0A84FF`\n- unten rechts: `#BF5AF2`\n\nDu kannst diesen Text markieren und mit Strg+C kopieren." });
                    await Task.Delay(800);
                    Frame(); Save("2_answer");
                    H.Ai.Messages.Clear();
                    foreach (var m in keep) H.Ai.Messages.Add(m);
                    S.AiKeysProtected.Clear();
                    foreach (var (k, v) in keys) S.AiKeysProtected[k] = v;
                    Collapse();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "langshots":
                {
                    // every Settings page and every island page, to spot texts that are still German
                    Root.IsHitTestVisible = false;
                    var w = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
                    w.Show();
                    foreach (var page in new[] { "General", "Look", "Anim", "Keys", "Clip", "Shot", "Widgets", "Ai", "About" })
                    {
                        w.ShowPage(page);
                        await Task.Delay(700);
                        var el = (FrameworkElement)w.Content;
                        var dpi2 = VisualTreeHelper.GetDpi(w);
                        // the whole page, not just the visible part
                        var scroll = (System.Windows.Controls.ScrollViewer)w.FindName("PageScroll");
                        var content = (FrameworkElement)scroll.Content;
                        content.Measure(new Size(scroll.ActualWidth, double.PositiveInfinity));
                        double hgt = Math.Max(el.ActualHeight, content.DesiredSize.Height + 40);
                        var rtb2 = new RenderTargetBitmap((int)(scroll.ActualWidth * dpi2.DpiScaleX), (int)(hgt * dpi2.DpiScaleY), dpi2.PixelsPerInchX, dpi2.PixelsPerInchY, PixelFormats.Pbgra32);
                        content.Arrange(new Rect(0, 0, scroll.ActualWidth, hgt));
                        rtb2.Render(content);
                        var enc2 = new PngBitmapEncoder();
                        enc2.Frames.Add(BitmapFrame.Create(rtb2));
                        using (var fs = File.Create(Path.Combine(dir, $"settings_{page}.png"))) enc2.Save(fs);
                        scroll.InvalidateMeasure();
                    }
                    w.Close();
                    fh = 340;
                    foreach (var tab in new[] { "Music", "Clip", "Agent", "Ai", "Games", "Notify", "Timer", "Today", "Remind", "Controls" })
                    {
                        Expand(tab, false);
                        _sticky = true;
                        await Task.Delay(900);
                        Frame();
                    }
                    Save("island_tabs");
                    Collapse();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "qol":
                {
                    // the round of 2026-11-18, one after another (demo content only)
                    fh = 70;
                    var log = new List<string>();
                    Root.IsHitTestVisible = false;
                    _sticky = false;
                    Collapse();
                    H.Agents.EndDemo();
                    H.Timer.Reset();
                    var cover = Pic(Color.FromRgb(0xFF, 0x7A, 0x3D), Color.FromRgb(0xE0, 0x3A, 0x5A));
                    H.Media.Fake("Nachtlicht", "Lena Ost", cover, false);
                    UpdatePill(true);
                    await Task.Delay(900);
                    // 6: pause – the bars fall, the cover dims
                    H.Media.FakePlaying(false);
                    UpdatePill(true);
                    await Film(8, 50);
                    log.Add($"paused: cover opacity={LeadCover.Opacity:0.00} eq={Equalizer.Visibility}");
                    // 7: a timer starts – it takes the island from the paused cover, sliding in
                    H.Timer.Reset(); H.Timer.SetDurationSeconds(50); H.Timer.Start();
                    UpdatePill(true);
                    await Film(8, 50);
                    // 9: the last minute – red, breathing
                    await Task.Delay(400);
                    await Film(6, 150);
                    log.Add($"last minute: rim stroke={_timerRim.Stroke} visible={_timerRim.Visibility}");
                    Save("pause_lead_lastminute");
                    // 9: the end – the ring closes
                    RimRun((Brush)FindResource("Orange"), full: true);
                    await Film(8, 90);
                    H.Timer.Reset();
                    UpdatePill(true);
                    await Task.Delay(500);
                    // 8: Claude done – the asterisk spins into the check, a green light runs round
                    H.Agents.Demo("Demo-Aufgabe", "Liest", 2, 3, 30, AgentState.Running, "demo-qol");
                    UpdatePill(true);
                    await Task.Delay(700);
                    H.Agents.Demo("Demo-Aufgabe", "Fertig", 3, 3, 40, AgentState.Done, "demo-qol");
                    UpdatePill(true);
                    RimRun((Brush)FindResource("Green"));
                    await Film(12, 80);
                    Save("ring_agent");
                    H.Agents.EndDemo();
                    // 10: a bubble under the pointer – music playing + timer bubble
                    H.Media.FakePlaying(true);
                    H.Timer.Reset(); H.Timer.SetDurationSeconds(300); H.Timer.Start();
                    UpdatePill(true);
                    await Task.Delay(1200);
                    BubbleHover(Bubble, true);
                    await Film(5, 60);
                    log.Add($"bubble tip: {Bubble.ToolTip}");
                    BubbleHover(Bubble, false);
                    H.Timer.Reset();
                    UpdatePill(true);
                    // 15: typing elsewhere
                    Interlocked.Exchange(ref _lastKeyTicks, DateTime.Now.Ticks);
                    bool active = IsActive;
                    TypingTick();
                    await Task.Delay(500);
                    log.Add($"typing: faded={_typingFaded} opacity={Root.Opacity:0.00} (island active={active})");
                    Frame();
                    TypingFadeOff();
                    Save("bubble_typing");
                    // 1: deleting in the open island – the island grows by the bar
                    fh = 260;
                    Expand("Clip", false);
                    _sticky = true;
                    await Task.Delay(900);
                    double before = Island.ActualHeight;
                    H.Clipboard.AddText("Demo: Notiz für später", "Demo");
                    await Task.Delay(500);
                    var item = H.Clipboard.Items.FirstOrDefault(i => i.Text?.StartsWith("Demo: Notiz") == true);
                    if (item != null) DeleteClip(item);
                    await Film(10, 60);
                    log.Add($"undo room: height before={before:0} with bar={Island.ActualHeight:0}");
                    DoUndo();
                    if (item != null) H.Clipboard.Remove(item);
                    Save("undo_room");
                    // 11: tucking away rolls up
                    Collapse();
                    await Task.Delay(700);
                    fh = 70;
                    Tuck();
                    await Film(8, 40);
                    Untuck();
                    await Film(10, 50);
                    Save("tuck");
                    Collapse();
                    File.WriteAllLines(Path.Combine(dir, "qol.txt"), log);
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "pausedrule":
                {
                    // paused music + timer → the timer in the island (no bubble); playing music + timer → timer bubble
                    fh = 60;
                    _sticky = false;
                    Collapse();
                    H.Agents.EndDemo();
                    var cover = Pic(Color.FromRgb(0xFF, 0x7A, 0x3D), Color.FromRgb(0xE0, 0x3A, 0x5A));
                    var log = new List<string>();
                    string State0() => $"playing={H.Media.IsPlaying} lead={(LeadCover.IsVisible ? "cover" : LeadIcon.IsVisible ? LeadIcon.Kind : "-")} trail={(TrailText.IsVisible ? TrailText.Text : "-")} bubble={Bubble.Visibility} timerRim={_timerRim.Visibility}";
                    H.Media.Fake("Nachtlicht", "Lena Ost", cover, false); H.Media.FakePlaying(false);
                    H.Timer.Reset(); H.Timer.SetDurationSeconds(300); H.Timer.Start();
                    UpdatePill(true);
                    await Task.Delay(1200);
                    log.Add("paused + timer: " + State0());
                    {
                        var cell = VisualTreeHelper.GetChildrenCount(TrailText) > 0 ? VisualTreeHelper.GetChild(TrailText, 0) as System.Windows.Controls.StackPanel : null;
                        var tb = cell?.Children.Count > 0 ? ((System.Windows.Controls.Grid)cell.Children[0]).Children[0] as System.Windows.Controls.TextBlock : null;
                        log.Add($"  rolling: w={TrailText.ActualWidth:0.0} h={TrailText.ActualHeight:0.0} cells={cell?.Children.Count} fg={tb?.Foreground} font={tb?.FontSize} tbW={tb?.ActualWidth:0.0} opacity={TrailText.Opacity}");
                    }
                    Frame();
                    H.Media.Fake("Nachtlicht", "Lena Ost", cover, true);
                    UpdatePill(true);
                    await Task.Delay(1200);
                    log.Add("playing + timer: " + State0());
                    Frame();
                    H.Timer.Reset();
                    H.Media.Fake("Nachtlicht", "Lena Ost", cover, false); H.Media.FakePlaying(false);
                    H.Agents.Demo("Demo-Aufgabe", "Liest", 1, 3, 20, AgentState.Running, "demo-paused");
                    UpdatePill(true);
                    await Task.Delay(1200);
                    log.Add("paused + agent: " + State0());
                    Frame();
                    H.Agents.EndDemo();
                    UpdatePill(true);
                    await Task.Delay(1200);
                    log.Add("paused alone: " + State0());
                    Frame();
                    Save("pausedrule");
                    File.WriteAllLines(Path.Combine(dir, "pausedrule.txt"), log);
                    break;
                }
                case "coverclip":
                {
                    // the left end of the closed island while music plays, 4× – with and without eyes
                    fh = 60;
                    _sticky = false;
                    Collapse();
                    H.Media.Fake("Nachtlicht", "Lena Ost", Pic(Color.FromRgb(0xFF, 0x7A, 0x3D), Color.FromRgb(0xE0, 0x3A, 0x5A)), true);
                    await Task.Delay(1200);
                    var shots = new List<BitmapSource>();
                    var info = new List<string>();
                    async Task Zoomed(int n, int ms)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            var dpi = VisualTreeHelper.GetDpi(this);
                            var full = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(fh * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                            var bgv = new DrawingVisual();
                            using (var dc = bgv.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, fh));
                            full.Render(bgv); full.Render(Root);
                            var l = Island.TranslatePoint(new Point(0, 0), Root);
                            int x0 = (int)Math.Max(0, (l.X - 6) * dpi.DpiScaleX), y0 = (int)Math.Max(0, (l.Y - 4) * dpi.DpiScaleY);
                            int cw = (int)Math.Min(110 * dpi.DpiScaleX, full.PixelWidth - x0), ch = (int)Math.Min(42 * dpi.DpiScaleY, full.PixelHeight - y0);
                            var big = new TransformedBitmap(new CroppedBitmap(full, new Int32Rect(x0, y0, cw, ch)), new ScaleTransform(4 / dpi.DpiScaleX, 4 / dpi.DpiScaleY));
                            big.Freeze(); shots.Add(big);
                            try
                            {
                                var c = LeadCover.TranslatePoint(new Point(0, 0), Island);
                                var eb = Eyes.IsVisible ? Eyes.TransformToAncestor(Island).TransformBounds(new Rect(0, 0, Eyes.ActualWidth, Eyes.ActualHeight)) : Rect.Empty;
                                info.Add($"cover left={c.X:0.0} eyes right={(eb.IsEmpty ? 0 : eb.Right):0.0} gap={(eb.IsEmpty ? 99 : c.X - eb.Right):0.0} ext={EyesExt:0.0}");
                            }
                            catch { }
                            await Task.Delay(ms);
                        }
                    }
                    await Zoomed(2, 120);
                    // a strong bass kick, made up: the cover must stay inside its place
                    for (int i = 0; i < 30; i++) CoverBeat(0.1);
                    CoverBeat(0.95);
                    var b = LeadCover.TransformToAncestor(Island).TransformBounds(new Rect(0, 0, LeadCover.ActualWidth, LeadCover.ActualHeight));
                    info.Add($"kick: scale={LeadCoverScale.ScaleX:0.000} bounds L={b.Left:0.00} T={b.Top:0.00} R={b.Right:0.00} B={b.Bottom:0.00} (rest: L=6 T=6 R=26 B=26)");
                    await Zoomed(1, 60);
                    shots.Clear();
                    info.Clear();
                    // waking up and falling asleep, frame by frame: the eyes must never reach the cover
                    _eyesUntil = DateTime.Now.AddSeconds(30);
                    await Zoomed(15, 35);
                    await Task.Delay(800);
                    _eyesUntil = DateTime.MinValue;
                    await Zoomed(18, 45);
                    int cols = 3, rows = (shots.Count + cols - 1) / cols;
                    double zw = shots[0].PixelWidth, zh = shots[0].PixelHeight;
                    var dv2 = new DrawingVisual();
                    using (var dc = dv2.RenderOpen())
                        for (int i = 0; i < shots.Count; i++) dc.DrawImage(shots[i], new Rect(i % cols * zw, i / cols * zh, zw, zh));
                    var all = new RenderTargetBitmap((int)(cols * zw), (int)(rows * zh), 96, 96, PixelFormats.Pbgra32);
                    all.Render(dv2);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(all));
                    using (var fs = File.Create(Path.Combine(dir, "coverclip.png"))) enc.Save(fs);
                    File.WriteAllLines(Path.Combine(dir, "coverclip.txt"), info);
                    break;
                }
                case "forget":
                {
                    // closed ≥ 1 min → what runs wins (KI first); closed 20 s → the last page as before
                    var log = new List<string> { $"running now: ai={H.Ai.Busy} agent={H.Agents.AnyRunning || H.Agents.AnyWaiting} call={H.Privacy.InUse} music={H.Media.IsPlaying} timer={H.Timer.Running}" };
                    H.Agents.Demo("Demo-Aufgabe", "Liest", 1, 3, 20, AgentState.Running, "demo-forget");
                    _moments.Clear();
                    S.LastTab = "Remind";
                    _closedTab = "Remind";
                    _closedAt = DateTime.Now.AddSeconds(-90);
                    log.Add($"closed 90 s, agent runs → {ContextTab()} (expected Agent)");
                    _closedAt = DateTime.Now.AddSeconds(-20);
                    log.Add($"closed 20 s, agent runs → {ContextTab()} (expected Remind; hidden={IsTabHidden("Remind")} minimal={MinimalAway("Remind")} smart={S.SmartTab})");
                    H.Agents.EndDemo();
                    _closedAt = DateTime.Now.AddSeconds(-90);
                    log.Add($"closed 90 s, no agent → {ContextTab()} (music={H.Media.IsPlaying}: expected Music if it plays, else Remind)");
                    File.WriteAllLines(Path.Combine(dir, "forget.txt"), log);
                    break;
                }
                case "flair":
                {
                    // the animation round of 2026-11-17, one after another (demo content only)
                    var log = new List<string>();
                    Root.IsHitTestVisible = false;
                    _sticky = false;
                    Collapse();
                    await Task.Delay(600);
                    fh = 70;
                    // timer: rim + rolling digits
                    H.Timer.Reset();
                    H.Timer.SetDurationSeconds(125);
                    H.Timer.Start();
                    await Task.Delay(900);
                    log.Add($"timer rim visible={_timerRim.Visibility} lead={_timerLead}");
                    for (int i = 0; i < 14; i++) { Frame(); await Task.Delay(90); }
                    Save("timer");
                    // reminder wobble
                    Wobble();
                    await Film(12, 80);
                    Save("wobble");
                    H.Timer.Reset();
                    UpdatePill(true);
                    await Task.Delay(500);
                    // magnet + gulp
                    MagnetBulge(20);
                    await Film(6, 40);
                    IslandGulp();
                    await Film(8, 45);
                    Save("magnet_gulp");
                    // charging bolt
                    OnCharger(true, 0.62);
                    await Film(12, 90);
                    Save("bolt");
                    await Task.Delay(2500);
                    // the page slide on the spring: Timer → Today, then a taller page (Clip)
                    fh = 260;
                    Expand(L.T("Timer"), false);
                    _sticky = true;
                    await Task.Delay(900);
                    _tabBtns["Today"].IsChecked = true;
                    await Film(12, 45);
                    _tabBtns["Clip"].IsChecked = true;
                    await Film(12, 45);
                    Save("slide");
                    // a new clipboard entry grows in; deleting it folds away and offers "Rückgängig"
                    var demo = H.Clipboard.AddText("Demo: Einkaufsliste Milch, Brot", "Demo");
                    await Film(8, 45);
                    var item = H.Clipboard.Items.FirstOrDefault(i => i.Text?.StartsWith("Demo: Einkaufsliste") == true);
                    if (item != null) DeleteClip(item);
                    await Film(10, 60);
                    log.Add($"undo bar visible={_undoBar?.Visibility} after delete: in list={H.Clipboard.Items.Contains(item)}");
                    DoUndo();
                    await Task.Delay(400);
                    log.Add($"after undo: in list={H.Clipboard.Items.Contains(item)}");
                    if (item != null) H.Clipboard.Remove(item);
                    Save("list_undo");
                    // keyboard: → walks to the next tab
                    var before = _tab;
                    Window_PreviewKeyDown(this, new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, System.Windows.Input.Key.Right) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    log.Add($"keyboard → : {before} → {_tab}");
                    Collapse();
                    // copy flight (separate window – only checked for errors here)
                    try { FlyCopy("Demo-Text zum Kopieren"); log.Add("fly copy ok"); } catch (Exception ex) { log.Add("fly copy: " + ex.Message); }
                    await Task.Delay(900);
                    // settings: carousel page switch + a switch springing
                    var w = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
                    w.Show();
                    await Task.Delay(600);
                    var shots = new List<BitmapSource>();
                    void ShotW()
                    {
                        var el = (FrameworkElement)w.Content;
                        var d2 = VisualTreeHelper.GetDpi(w);
                        var r = new RenderTargetBitmap((int)(el.ActualWidth * d2.DpiScaleX), (int)(el.ActualHeight * d2.DpiScaleY), d2.PixelsPerInchX, d2.PixelsPerInchY, PixelFormats.Pbgra32);
                        r.Render(el); r.Freeze(); shots.Add(r);
                    }
                    w.ShowPage("Anim");
                    for (int i = 0; i < 4; i++) { await Task.Delay(70); ShotW(); }
                    var enc = new PngBitmapEncoder();
                    foreach (var s in shots) enc.Frames.Add(BitmapFrame.Create(s));
                    for (int i = 0; i < shots.Count; i++)
                    {
                        var e1 = new PngBitmapEncoder();
                        e1.Frames.Add(BitmapFrame.Create(shots[i]));
                        using var fs = File.Create(Path.Combine(dir, $"settings_slide_{i}.png"));
                        e1.Save(fs);
                    }
                    w.Close();
                    File.WriteAllLines(Path.Combine(dir, "flair.txt"), log);
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "eyesopen":
                {
                    // the eyes are awake, then the island opens: no eye in any frame, the width only grows (no shrink after)
                    fh = 200;
                    bool eyesBefore = S.IslandEyes;
                    S.IslandEyes = true;
                    UpdateEyes();
                    _sticky = false;
                    Collapse();
                    H.Media.Fake("Nachtlicht", "Lena Ost", Pic(Color.FromRgb(0xFF, 0x7A, 0x3D), Color.FromRgb(0xE0, 0x3A, 0x5A)), true);
                    await Task.Delay(800);
                    _eyesUntil = DateTime.Now.AddSeconds(30);
                    await Task.Delay(1800);
                    Frame();
                    var log = new List<string> { $"before: eyes visible={Eyes.IsVisible} ext={EyesExt:0.0} w={Island.ActualWidth:0.0}" };
                    Root.IsHitTestVisible = false;
                    Expand("Music", false);
                    _sticky = true;
                    for (int i = 0; i < 16; i++)
                    {
                        await Task.Delay(35);
                        Frame();
                        log.Add($"{i}: eyes visible={Eyes.IsVisible} ext={EyesExt:0.0} w={Island.ActualWidth:0.0}");
                    }
                    await Task.Delay(600);
                    log.Add($"end: w={Island.ActualWidth:0.0}");
                    File.WriteAllLines(Path.Combine(dir, "widths.txt"), log);
                    Save("eyesopen");
                    Collapse();
                    S.IslandEyes = eyesBefore;
                    UpdateEyes();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "carousel":
                {
                    // "Karussell mit Feder": Timer → Today (forward), then back to Timer; the bar under the tab glides along
                    fh = 200;
                    Root.IsHitTestVisible = false;
                    Expand(L.T("Timer"), false);
                    _sticky = true;
                    await Task.Delay(900);
                    Frame();
                    _tabBtns["Today"].IsChecked = true;
                    await Film(12, 40);
                    await Task.Delay(400);
                    Frame();
                    Save("forward");
                    _tabBtns["Timer"].IsChecked = true;
                    await Film(12, 40);
                    Save("back");
                    Collapse();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "detach":
                {
                    // a made-up WhatsApp message: banner, then "the user went into WhatsApp" → the card drops away
                    fh = 160;
                    var lines = new List<string>();
                    var wa = new IslandNotification { Id = -77, AppId = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", AppName = "WhatsApp", Title = "Mia", Text = "Bist du schon unterwegs?", Time = DateTime.Now };
                    foreach (var keys in new[] { new[] { "whatsapp", "whatsappdesktop" }, new[] { "discord" }, new[] { "chrome", "googlechrome" } })
                        lines.Add($"{string.Join("/", keys)} → {FromApp(wa, keys)}");
                    var dc = new IslandNotification { AppId = "com.squirrel.Discord.Discord", AppName = "Discord" };
                    lines.Add($"discord app vs Discord notif → {FromApp(dc, new[] { "discord" })}");
                    File.WriteAllLines(Path.Combine(dir, "match.txt"), lines);
                    Collapse();
                    await Task.Delay(400);
                    ShowNotificationBanner(wa);
                    await Film(6, 120);
                    DetachPeek();
                    await Film(10, 60);
                    Save("detach");
                    break;
                }
                case "claudeset":
                {
                    // the Claude Code row opened (status read from the real settings file, nothing written) + the test run
                    var w = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
                    w.Show();
                    w.ShowPage("General");
                    await Task.Delay(500);
                    var tg = (System.Windows.Controls.Primitives.ToggleButton)w.FindName("ClaudeExpand");
                    tg.IsChecked = true;
                    tg.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await Task.Delay(400);
                    var row = (FrameworkElement)w.FindName("ClaudeDetails");
                    row.BringIntoView();
                    await Task.Delay(400);
                    var el = (FrameworkElement)w.Content;
                    var dpi2 = VisualTreeHelper.GetDpi(w);
                    var rtb2 = new RenderTargetBitmap((int)(el.ActualWidth * dpi2.DpiScaleX), (int)(el.ActualHeight * dpi2.DpiScaleY), dpi2.PixelsPerInchX, dpi2.PixelsPerInchY, PixelFormats.Pbgra32);
                    rtb2.Render(el);
                    var enc2 = new PngBitmapEncoder();
                    enc2.Frames.Add(BitmapFrame.Create(rtb2));
                    using (var fs = File.Create(Path.Combine(dir, "settings_claude.png"))) enc2.Save(fs);
                    File.WriteAllText(Path.Combine(dir, "status.txt"), $"{ClaudeHooks.Check()} · {((System.Windows.Controls.TextBlock)w.FindName("ClaudeStatus")).Text}");
                    w.Close();
                    break;
                }
                case "trio":
                {
                    // quick notes (demo text only), the settings search, and a made-up browser download end to end
                    Root.IsHitTestVisible = false;
                    fh = 240;
                    var demo = new[] { H.Notes.Add("Milch, Eier und Brot kaufen"), H.Notes.Add("Idee: Wetter-Widget in Blau ausprobieren\nmit runden Ecken") };
                    H.Notes.TogglePin(demo[1]);
                    Expand("Notes", false);
                    _sticky = true;
                    await Task.Delay(900);
                    Frame();
                    NoteInput.Text = "Neue Notiz …";
                    await Task.Delay(200);
                    Frame();
                    NoteInput.Clear();
                    foreach (var n in demo) H.Notes.Remove(n);
                    Save("notes");

                    // settings search: "spotify", then Enter = the best match, opened and lit up
                    var w = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
                    w.Show();
                    await Task.Delay(600);
                    var box = (System.Windows.Controls.TextBox)w.FindName("SettingsSearch");
                    void Shot(string name)
                    {
                        var el = (FrameworkElement)w.Content;
                        var dpi2 = VisualTreeHelper.GetDpi(w);
                        var rtb2 = new RenderTargetBitmap((int)(el.ActualWidth * dpi2.DpiScaleX), (int)(el.ActualHeight * dpi2.DpiScaleY), dpi2.PixelsPerInchX, dpi2.PixelsPerInchY, PixelFormats.Pbgra32);
                        rtb2.Render(el);
                        var enc2 = new PngBitmapEncoder();
                        enc2.Frames.Add(BitmapFrame.Create(rtb2));
                        using var fs = File.Create(Path.Combine(dir, name + ".png"));
                        enc2.Save(fs);
                    }
                    var lines = new List<string>();
                    foreach (var q in new[] { "spotify", "minimal", "kürzel", "redirect", "xyzzy" })
                    {
                        box.Text = q;
                        await Task.Delay(250);
                        var res = (System.Windows.Controls.StackPanel)w.FindName("SearchResults");
                        lines.Add($"{q}: " + string.Join(" | ", res.Children.OfType<System.Windows.Controls.Border>()
                            .Select(b => string.Join(" / ", ((System.Windows.Controls.StackPanel)b.Child).Children.OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text)))));
                    }
                    box.Text = "spotify";
                    await Task.Delay(300);
                    Shot("search_list");
                    var firstHit = ((System.Windows.Controls.StackPanel)w.FindName("SearchResults")).Children.OfType<System.Windows.Controls.Border>().First();
                    firstHit.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    await Task.Delay(700);
                    Shot("search_goto");
                    box.Text = "minimal";
                    await Task.Delay(300);
                    var hit2 = ((System.Windows.Controls.StackPanel)w.FindName("SearchResults")).Children.OfType<System.Windows.Controls.Border>().First();
                    hit2.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    await Task.Delay(700);
                    Shot("search_goto2");
                    w.Close();
                    File.WriteAllLines(Path.Combine(dir, "search.txt"), lines);

                    // a browser download: a growing ".crdownload" in the Downloads folder, then renamed = finished
                    Collapse();
                    await Task.Delay(500);
                    var dlDir = BrowserDownloadService.DownloadsFolder();
                    var tmp = Path.Combine(dlDir, "PeakPill-Testdownload.zip.crdownload");
                    var fin = Path.Combine(dlDir, "PeakPill-Testdownload.zip");
                    var dlLog = new List<string>();
                    try
                    {
                        var chunk = new byte[3 * 1024 * 1024];
                        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                            for (int i = 0; i < 6; i++)
                            {
                                f.Write(chunk); f.Flush(true);
                                await Task.Delay(900);
                                dlLog.Add($"{i}: downloading={H.Downloads.Downloading} name={H.Downloads.Name} pct={H.Downloads.Percent} speed={H.Downloads.Speed}");
                                if (i == 3) { Expand("Live", false); _sticky = true; await Task.Delay(500); Frame(); Collapse(); await Task.Delay(400); Frame(); }
                            }
                        File.Move(tmp, fin);
                        for (int i = 0; i < 8; i++) { await Task.Delay(500); Frame(); }
                        dlLog.Add($"after: downloading={H.Downloads.Downloading}");
                    }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                        try { File.Delete(fin); } catch { }
                    }
                    File.WriteAllLines(Path.Combine(dir, "download.txt"), dlLog);
                    Save("download");
                    Collapse();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "minimal":
                {
                    // Minimal-Modus on: the settings section (open) + which tabs the island shows; "So geht's" folded and one opened
                    Root.IsHitTestVisible = false;
                    static IEnumerable<T> FindKids<T>(DependencyObject d) where T : DependencyObject
                    {
                        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                        {
                            if (c is T t) yield return t;
                            foreach (var k in FindKids<T>(c)) yield return k;
                        }
                    }
                    bool keepMin = S.MinimalMode;
                    S.MinimalMode = true;
                    var w = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
                    w.Show();
                    foreach (var page in new[] { "General", "Look", "Keys" })
                    {
                        w.ShowPage(page);
                        await Task.Delay(700);
                        if (page == "General")
                            foreach (var link in FindKids<System.Windows.Controls.TextBlock>((DependencyObject)w.Content).Where(t => t.Cursor == System.Windows.Input.Cursors.Hand && t.Text.Contains("▾")).Take(1).ToList())
                                link.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                        await Task.Delay(200);
                        var el = (FrameworkElement)w.Content;
                        var dpi2 = VisualTreeHelper.GetDpi(w);
                        var scroll = (System.Windows.Controls.ScrollViewer)w.FindName("PageScroll");
                        var content = (FrameworkElement)scroll.Content;
                        content.Measure(new Size(scroll.ActualWidth, double.PositiveInfinity));
                        double hgt = Math.Max(el.ActualHeight, content.DesiredSize.Height + 40);
                        var rtb2 = new RenderTargetBitmap((int)(scroll.ActualWidth * dpi2.DpiScaleX), (int)(hgt * dpi2.DpiScaleY), dpi2.PixelsPerInchX, dpi2.PixelsPerInchY, PixelFormats.Pbgra32);
                        content.Arrange(new Rect(0, 0, scroll.ActualWidth, hgt));
                        rtb2.Render(content);
                        var enc2 = new PngBitmapEncoder();
                        enc2.Frames.Add(BitmapFrame.Create(rtb2));
                        using (var fs = File.Create(Path.Combine(dir, $"settings_{page}.png"))) enc2.Save(fs);
                        scroll.InvalidateMeasure();
                    }
                    w.Close();
                    App.NotifySettingsChanged();
                    fh = 120;
                    Expand("Timer", false);
                    _sticky = true;
                    await Task.Delay(900);
                    Frame();
                    Save("island_minimal");
                    File.WriteAllLines(Path.Combine(dir, "tabs.txt"), MinimalCandidates.Select(t =>
                        $"{t}: content={HasContent(t)} visible={_tabBtns[t].Visibility == Visibility.Visible}"));
                    Collapse();
                    S.MinimalMode = keepMin;
                    App.NotifySettingsChanged();
                    Root.IsHitTestVisible = true;
                    break;
                }
                case "aisettings":
                {
                    var w = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
                    w.Show();
                    w.ShowPage("Ai");
                    await Task.Delay(800);
                    var dpi = VisualTreeHelper.GetDpi(w);
                    var el = (FrameworkElement)w.Content;
                    var rtb = new RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    rtb.Render(el);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(dir, "settings_ai.png"))) enc.Save(fs);
                    w.Close();
                    break;
                }
                case "aikeys":
                {
                    // the real services with a made-up, invalid key: each must answer "key wrong" in plain words
                    var lines = new List<string>();
                    var keys = new Dictionary<string, string>(S.AiKeysProtected);
                    var prov = S.AiProvider;
                    var keep = H.Ai.Messages.ToList();
                    foreach (var p in AiService.All)
                    {
                        var fake = p == AiProvider.Gemini ? "AIzaInvalidTestKey000000000000000000000" : p == AiProvider.ChatGpt ? "sk-invalid-test-key" : "sk-ant-invalid-test-key";
                        try { var ms = await AiService.ListModels(p, fake, CancellationToken.None); lines.Add($"{p} list: {ms.Count} models (?)"); }
                        catch (Exception ex) { lines.Add($"{p} list: {AiService.Explain(ex)}  [{ex.GetType().Name}]"); }
                        S.AiProvider = p;
                        AiService.SetKey(p, fake);
                        H.Ai.Messages.Clear();
                        await H.Ai.SendAsync("Hallo");
                        lines.Add($"{p} chat: failed={H.Ai.Messages[^1].Failed} · {H.Ai.Messages[^1].Text}");
                    }
                    File.WriteAllLines(Path.Combine(dir, "keys.txt"), lines);
                    H.Ai.Messages.Clear();
                    foreach (var m in keep) H.Ai.Messages.Add(m);
                    S.AiKeysProtected.Clear();
                    foreach (var (k, v) in keys) S.AiKeysProtected[k] = v;
                    S.AiProvider = prov;
                    break;
                }
                case "listen":
                {
                    // a known app starts listening while a Discord call runs – the wave must still come
                    fh = 120;
                    var claude = System.Diagnostics.Process.GetProcessesByName("Claude").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(f => f != null);
                    H.Privacy.Demo((claude ?? @"C:\Windows\System32\notepad.exe", true, false, false, 3));
                    _knownLive ??= Storage.Load<Dictionary<string, DateTime>>("privacy_known.json") ?? new();
                    File.WriteAllText(Path.Combine(dir, "app.txt"), $"{H.Privacy.Apps[0].Name} bekannt: {_knownLive.ContainsKey("m:" + H.Privacy.Apps[0].Name)}");
                    await Task.Delay(400);
                    H.Privacy.DemoStarted('m');
                    await Film(12, 110);
                    Save("1_listen");
                    H.Privacy.EndDemo();
                    break;
                }
                case "queue2":
                {
                    // another process pastes (reads the clipboard) – the queue must move once per paste
                    var log = new System.Text.StringBuilder();
                    bool qm = H.Clipboard.QueueMode;
                    H.Clipboard.QueueMode = true;
                    H.Clipboard.Notice += n => log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} Insel: {n}");
                    foreach (var t in new[] { "Eins", "Zwei", "Drei" }) { H.Clipboard.AddText(t, "Test"); await Task.Delay(900); }
                    log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} gestapelt: {H.Clipboard.QueueCount}");
                    File.WriteAllText(Path.Combine(dir, "ready.txt"), "ready");
                    for (int i = 0; i < 24 && H.Clipboard.QueueCount > 0; i++) await Task.Delay(500);
                    log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} Ende: Stapel={H.Clipboard.QueueCount}");
                    H.Clipboard.QueueMode = qm;
                    File.WriteAllText(Path.Combine(dir, "queue2.txt"), log.ToString());
                    break;
                }
                case "dl":
                    H.Downloads.Demo("Projekt-Dateien.zip", 0.42, "12,4 MB/s");
                    await Task.Delay(800);
                    Expand("Live", true);
                    await Task.Delay(1200);
                    fh = 160;
                    Frame(); Save("1_download_row");
                    Collapse();
                    await Task.Delay(900);
                    OnDownloadStuck("Projekt-Dateien.zip", "Chrome");
                    await Task.Delay(800);
                    fh = 70;
                    Frame(); Save("2_stuck");
                    H.Downloads.EndDemo();
                    break;
                case "page":
                    Expand("Music", true);
                    await Task.Delay(1500);
                    SelectTab("Timer", false);
                    await Film(10, 40);
                    Save("1_forward");
                    await Task.Delay(1000);
                    SelectTab("Music", false);
                    await Film(10, 40);
                    Save("2_back");
                    break;
            }
        }
        finally
        {
            S.TrackPeek = peek;
            S.Animations = anim;
            App.Quit();
        }
    }
#endif
}

