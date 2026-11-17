using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PeakPill.Core;

namespace PeakPill.UI;

/// <summary>
/// Settings pages change like the island's (2026-11-17, "Karussell mit Feder"): the old page slides away under the
/// sidebar while the new one pushes in from the side of its place in the list and springs a little.
/// </summary>
public partial class SettingsWindow
{
    private Image _pageGhost;
    private readonly TranslateTransform _pageIn = new(), _ghostOut = new();
    private BitmapSource _leaving;
    private string _shownPage = "Look"; // the page the window opens with

    /// <summary>Before the pages switch: a picture of what is on screen now.</summary>
    private void SnapLeavingPage()
    {
        _leaving = null;
        if (!IsLoaded || S.Animations == AnimationLevel.Aus || PageScroll.ActualWidth < 1) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(PageScroll.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(PageScroll.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(PageScroll), null, new Rect(0, 0, PageScroll.ActualWidth, PageScroll.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            _leaving = rtb;
        }
        catch { }
    }

    private void SlideToPage(string tag)
    {
        var from = _shownPage;
        _shownPage = tag;
        if (_leaving == null || from == null || from == tag) return;
        var tags = Nav.Children.OfType<RadioButton>().Select(r => (string)r.Tag).ToList();
        int dir = Math.Sign(tags.IndexOf(tag) - tags.IndexOf(from));
        if (dir == 0) return;
        if (_pageGhost == null)
        {
            _pageGhost = new Image { IsHitTestVisible = false, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransform = _ghostOut };
            Grid.SetColumn(_pageGhost, 1);
            Panel.SetZIndex(_pageGhost, 1);
            Panel.SetZIndex(Sidebar, 5); // the leaving page slides under the sidebar
            RootGrid.Children.Add(_pageGhost);
            PageScroll.RenderTransform = _pageIn;
        }
        double w = PageScroll.ActualWidth;
        _pageGhost.Source = _leaving;
        _pageGhost.Visibility = Visibility.Visible;
        _leaving = null;
        var d = TimeSpan.FromMilliseconds(440);
        var ease = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut };
        var outA = new DoubleAnimation(0, -dir * w * 0.6, d) { EasingFunction = ease };
        outA.Completed += (_, _) => { _pageGhost.Visibility = Visibility.Collapsed; _pageGhost.Source = null; };
        _ghostOut.BeginAnimation(TranslateTransform.XProperty, outA);
        _pageGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300)));
        _pageIn.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * w, 0, d) { EasingFunction = ease });
    }
}
