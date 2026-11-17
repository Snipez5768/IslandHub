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
        HintRing(level, plugged ? green : level <= 0.2 ? (Brush)FindResource("Danger") : Brushes.White);
    }

    private void OnBatteryLow(double level)
    {
        if (!S.PowerInIsland) return;
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

    /// <summary>The old page leaves against the direction, the new one comes in from it – a short way, soft and slightly blurred.</summary>
    private void PageSlide(int dir, BitmapSource old)
    {
        bool full = S.Animations == AnimationLevel.An;
        double way = full ? 26 : 12;
        var outT = TimeSpan.FromMilliseconds(200);
        var inT = TimeSpan.FromMilliseconds(380);
        var easeOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        PageGhost.Source = old;
        PageGhost.Width = Pages.ActualWidth;
        PageGhost.RenderTransform = _ghostShift;
        PageGhost.Visibility = Visibility.Visible;
        var ghostFade = new DoubleAnimation(1, 0, outT) { EasingFunction = easeIn };
        ghostFade.Completed += (_, _) => { PageGhost.Visibility = Visibility.Collapsed; PageGhost.Source = null; PageGhost.Effect = null; };
        PageGhost.BeginAnimation(OpacityProperty, ghostFade);
        _ghostShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -dir * way, outT) { EasingFunction = easeIn });

        Pages.RenderTransform = _pageShift;
        _pageShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * way, 0, inT) { EasingFunction = easeOut });
        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { FillBehavior = FillBehavior.Stop, EasingFunction = easeOut });
        Pages.Opacity = 1;
        if (!full) return;
        var gb = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        PageGhost.Effect = gb;
        gb.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, 6, outT));
        var pb = new BlurEffect { Radius = 6, RenderingBias = RenderingBias.Performance };
        Pages.Effect = pb;
        var sharpen = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = easeOut };
        sharpen.Completed += (_, _) => { if (Pages.Effect == pb) Pages.Effect = null; };
        pb.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
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
