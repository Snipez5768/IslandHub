using System.Windows;
using System.Windows.Media;
using PeakPill.Core;

namespace PeakPill.UI;

/// <summary>
/// 55 the island at the bottom (user's pick 2026-11-18): it sits above the taskbar and opens upwards. The whole window
/// is mirrored top ↔ bottom, and what is inside the island (and the little pills and bubbles around it) is mirrored
/// back – so every size spring, card, bubble and animation simply runs upwards, and nothing reads upside down.
/// </summary>
public partial class IslandWindow
{
    private readonly ScaleTransform _flip = new(1, 1);

    private void ApplyBottom()
    {
        bool b = S.IslandBottom;
        _flip.ScaleY = b ? -1 : 1;
        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        Root.RenderTransform = _flip;
        foreach (var el in new[] { IslandContent, Bubble.Child as FrameworkElement, Bubble2.Child as FrameworkElement,
                                   LyricBubble.Child as FrameworkElement, HelperBubble.Child as FrameworkElement })
        {
            if (el == null) continue;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = b ? new ScaleTransform(1, -1) : Transform.Identity;
        }
        Reposition();
    }

    /// <summary>The element on screen in physical pixels – top-left and bottom-right, whichever way it is mirrored.</summary>
    private Rect ScreenRect(FrameworkElement e)
    {
        var a = e.PointToScreen(new Point(0, 0));
        var c = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new Rect(new Point(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y)), new Point(Math.Max(a.X, c.X), Math.Max(a.Y, c.Y)));
    }
}
