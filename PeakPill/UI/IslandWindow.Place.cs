using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PeakPill.Core;
using PeakPill.Services;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

/// <summary>
/// Where the island sits and how big it is (user wish 2026-11-16):
/// • a move mode – drag the island along the top edge, onto another monitor too; it snaps to the middle;
/// • it grows with the resolution as if every screen were 1080 px tall, times the user's own size.
/// Everything inside keeps its own (unscaled) units – one scale on the root does the rest.
/// </summary>
public partial class IslandWindow
{
    private const double BaseW = 560, BaseH = 360;
    private double _zoom = 1;
    private readonly ScaleTransform _zoomT = new(1, 1);
    private bool _moveMode, _dragging;
    private double _grabDx;

    /// <summary>Screen pixels per island unit right now (Windows scaling × the island's own zoom).</summary>
    private double PxX => VisualTreeHelper.GetDpi(this).DpiScaleX * _zoom;
    private double PxY => VisualTreeHelper.GetDpi(this).DpiScaleY * _zoom;

    private static WF.Screen IslandScreen() =>
        (S.FollowActiveScreen && _followScreen != null ? WF.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _followScreen) : null)
        ?? WF.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == S.IslandScreen) ?? WF.Screen.PrimaryScreen;

    private void ApplyZoom()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scr = IslandScreen();
        // 1080 physical pixels tall = 1; Windows' own scaling is taken out, so the island looks the same on every screen
        double res = S.ScaleWithScreen ? scr.Bounds.Height / 1080.0 / dpi.DpiScaleY : 1;
        double z = Math.Clamp(res * Math.Clamp(S.IslandSize, 0.7, 1.4), 0.5, 2.5);
        if (Root.LayoutTransform != _zoomT) Root.LayoutTransform = _zoomT;
        _zoom = z;
        _zoomT.ScaleX = _zoomT.ScaleY = z;
        Width = BaseW * z;
        Height = BaseH * z;
    }

    /// <summary>Top edge of the chosen screen, the island's centre at its saved place (in physical pixels – mixed DPI safe).</summary>
    private void Reposition()
    {
        ApplyZoom();
        var dpi = VisualTreeHelper.GetDpi(this);
        var b = IslandScreen().Bounds;
        double wPx = Width * dpi.DpiScaleX;
        double margin = 200 * dpi.DpiScaleX * _zoom; // the widest island stays on its screen
        double cx = Math.Clamp(b.Left + b.Width / 2.0 + S.IslandX * b.Width, b.Left + margin, b.Right - margin);
        var h = new WindowInteropHelper(this).Handle;
        MakeRoomService.IslandBounds = b;
        if (h == IntPtr.Zero)
        {
            Left = (cx - wPx / 2) / dpi.DpiScaleX;
            Top = b.Top / dpi.DpiScaleY;
            return;
        }
        Native.SetWindowPos(h, IntPtr.Zero, (int)Math.Round(cx - wPx / 2), b.Top, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    // ---------- move mode ----------

    public bool IsMoving => _moveMode;

    public void EnterMoveMode()
    {
        if (_moveMode) return;
        if (_tucked) Untuck();
        Collapse();
        _moveMode = true;
        IslandRim.Stroke = (Brush)FindResource("Blue");
        IslandRim.StrokeThickness = 1.5;
        _hintHoldMs = int.MaxValue; // stays until "Fertig"
        ShowHint("arrow.left.right", "Zum Verschieben ziehen", (Brush)FindResource("Blue"), key: "move",
                 actions: new List<(string, string, Brush, Action)> { ("check", "Fertig", (Brush)FindResource("Green"), ExitMoveMode) });
    }

    public void ExitMoveMode()
    {
        if (!_moveMode) return;
        _moveMode = false;
        _dragging = false;
        Island.ReleaseMouseCapture();
        IslandRim.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "IslandBorder");
        IslandRim.StrokeThickness = 0.5;
        App.SaveSettings();
        _hintGen++;
        EndHint();
        ShowHint("check", "Position gespeichert", (Brush)FindResource("Green"), check: true);
    }

    // ---------- a mode that stays until "Fertig" (arranging widgets) ----------

    private Action _modeDone;

    /// <summary>A blue notice with "Fertig" that stays in the island until the mode ends.</summary>
    public void ShowModeHint(string icon, string text, Action done)
    {
        _modeDone = done;
        if (_tucked) Untuck();
        Collapse();
        _hintHoldMs = int.MaxValue;
        ShowHint(icon, text, (Brush)FindResource("Blue"), key: "mode",
                 actions: new List<(string, string, Brush, Action)> { ("check", "Fertig", (Brush)FindResource("Green"), () => { var d = _modeDone; _modeDone = null; d?.Invoke(); }) });
    }

    public void EndModeHint()
    {
        _modeDone = null;
        if (_state == State.Hint && HintText.Text == "Widgets anordnen") { _hintGen++; EndHint(); }
        ShowHint("check", "Widgets fixiert", (Brush)FindResource("Green"), check: true);
    }

    // ---------- downloads (2026-11-16): open the app, hide, "hängt" ----------

    private void DownloadRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInside(d, sender as DependencyObject) && FindParentButton(d) != null) return;
        Dl?.OpenSource();
        Collapse();
    }

    private void DownloadDismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dl?.Dismiss();
        UpdatePill(true);
        UpdateLiveUi();
        if (!AnyDl && !H.Privacy.InUse && _state == State.Expanded && _tab == "Live") SelectTab("Music", false);
    }

    private static System.Windows.Controls.Button FindParentButton(DependencyObject d)
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is System.Windows.Controls.Button b) return b;
        return null;
    }

    /// <summary>A browser download stopped growing: ask – hide it, or open the browser to look.</summary>
    private void OnDownloadStuck(string name, string app)
    {
        EyesStartle(); // something went wrong – the eyes are startled
        string where = string.IsNullOrEmpty(app) ? L.T("im Browser") : L.T($"in {app}");
        _hintHoldMs = 8000;
        ShowHint("download", $"{name} hängt", (Brush)FindResource("Orange"), key: "dlstuck",
                 actions: new List<(string, string, Brush, Action)>
                 {
                     ("xmark", "Ausblenden", (Brush)FindResource("Danger"), () => { H.Downloads.Dismiss(); UpdatePill(true); UpdateLiveUi(); }),
                     ("open", $"Öffnen ({where})", (Brush)FindResource("Blue"), () => BrowserDownloadService.OpenBrowser(app))
                 });
    }

    // ---------- "Heute": weather + appointments (2026-11-16) ----------

    private void UpdateTodayUi()
    {
        // today's match of the followed team sits on top of the calendar
        var m = H.Feeds.Match;
        TodayMatch.Visibility = m != null ? Visibility.Visible : Visibility.Collapsed;
        if (m != null)
        {
            TodayMatchText.Text = $"{m.Home}  {m.Score}  {m.Away}";
            TodayMatchState.Text = m.State;
            TodayMatchState.Foreground = m.Live ? (Brush)FindResource("Green") : (Brush)FindResource("SubText");
        }
        BuildCalendar(m != null ? 2 : 3);
    }

    /// <summary>
    /// A calendar widget like iOS (user wish 2026-11-16 – days only, no appointments): the month, the weekday
    /// letters, this week and the next ones. Today is a red circle, the days already gone this week are dimmed.
    /// </summary>
    private void BuildCalendar(int weeks)
    {
        var de = L.Culture;
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var last = monday.AddDays(weeks * 7 - 1);
        string Month(DateTime d) => d.ToString("MMMM", de);
        CalMonth.Text = monday.Month == last.Month ? $"{Month(today)} {today.Year}" : L.T($"{Month(monday)} · {Month(last)}");
        CalGrid.Children.Clear();
        var sub = (Brush)FindResource("SubText");
        foreach (var letter in L.English ? new[] { "M", "T", "W", "T", "F", "S", "S" } : new[] { "M", "D", "M", "D", "F", "S", "S" })
            CalGrid.Children.Add(new TextBlock { Text = letter, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });
        for (int i = 0; i < weeks * 7; i++)
        {
            var d = monday.AddDays(i);
            var cell = new Grid { Height = 17 };
            if (d == today) cell.Children.Add(new System.Windows.Shapes.Ellipse { Width = 17, Height = 17, Fill = (Brush)FindResource("Danger") });
            cell.Children.Add(new TextBlock
            {
                // the first day of a new month shows its short name, so the change is easy to see
                Text = d.Day == 1 && d != today ? d.ToString("MMM", de).TrimEnd('.') : d.Day.ToString(),
                FontSize = d.Day == 1 && d != today ? 9 : 10.5,
                FontWeight = d == today ? FontWeights.Bold : FontWeights.Normal,
                Foreground = d < today ? sub : Brushes.White,
                Opacity = d < today ? 0.6 : 1,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            CalGrid.Children.Add(cell);
        }
    }

    /// <summary>A goal, a stream that went live, a parcel that moved: a ticker; with "Öffnen" when there is a page.</summary>
    private void OnFeedMoment(FeedMoment m)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(m.Color));
        brush.Freeze();
        var p = new PeekInfo
        {
            Key = "feed:" + m.Kind + m.Title, Icon = m.Icon, GlyphBrush = brush, Ticker = true,
            Source = m.Kind switch { "sport" => L.T("Fußball"), "twitch" => "Twitch", "youtube" => "YouTube", "parcel" => "DHL", _ => "PeakPill" },
            SourceIcon = m.Icon, SourceBrush = brush, Title = m.Title, Text = m.Text, TimeoutMs = 10000
        };
        if (m.Link != null)
            p.Button("open", L.T("Öffnen"), () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(m.Link) { UseShellExecute = true }); } catch { } }, brush);
        if (m.Kind == "sport" && m.Title == "Tor!" && S.Animations != AnimationLevel.Aus) Ripples(((SolidColorBrush)brush).Color);
        ShowPeek(p);
    }

    /// <summary>Back to the middle of the main screen.</summary>
    public void ResetPosition()
    {
        S.IslandScreen = null;
        S.IslandX = 0;
        App.SaveSettings();
        Reposition();
    }

    private void InitMoveMode()
    {
        Island.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_moveMode) return;
            // the "Fertig" button must still work
            if (e.OriginalSource is DependencyObject d && IsInside(d, HintActions)) return;
            Native.GetCursorPos(out var p);
            var c = Island.PointToScreen(new Point(Island.ActualWidth / 2, 0));
            _grabDx = p.X - c.X;
            _dragging = Island.CaptureMouse();
            e.Handled = true;
        };
        Island.PreviewMouseMove += (_, e) =>
        {
            if (!_dragging) return;
            Native.GetCursorPos(out var p);
            var scr = WF.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
            var b = scr.Bounds;
            double cx = p.X - _grabDx, mid = b.Left + b.Width / 2.0;
            // snaps to the middle (like Apple's): within 16 px it sits exactly centred
            bool snap = Math.Abs(cx - mid) < 16 * VisualTreeHelper.GetDpi(this).DpiScaleX;
            double x = snap ? 0 : (cx - mid) / b.Width;
            bool screenChanged = S.IslandScreen != scr.DeviceName && !(S.IslandScreen == null && scr.Primary);
            S.IslandScreen = scr.Primary ? null : scr.DeviceName;
            if (Math.Abs(S.IslandX - x) < 1e-6 && !screenChanged) return;
            if (snap && S.IslandX != 0) Pop(HintIconCircle);
            S.IslandX = x;
            Reposition();
            e.Handled = true;
        };
        Island.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            Island.ReleaseMouseCapture();
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) => { if (_moveMode && e.Key is Key.Escape or Key.Enter) { ExitMoveMode(); e.Handled = true; } };
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }
}
