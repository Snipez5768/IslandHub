using System.Windows;
using System.Windows.Media;

namespace PeakPill.UI;

/// <summary>
/// Apple's "continuous corners": the rounding starts earlier and blends into the straight edge instead of a plain
/// quarter circle (Figma corner smoothing, iOS uses 60 %). The shape always lies inside the circular one of the same
/// radius. A capsule (radius = half the height) has no room to blend and stays a capsule.
/// </summary>
public static class SmoothCorners
{
    public const double Ios = 0.6;

    /// <param name="inset">moves the shape inward on all sides (for a rim stroke that must stay inside the clip)</param>
    public static Geometry Make(double w, double h, double r, double smoothing = Ios, double inset = 0)
    {
        w -= 2 * inset; h -= 2 * inset; r = Math.Max(0, r - inset);
        if (w <= 0 || h <= 0) return Geometry.Empty;
        double half = Math.Min(w, h) / 2;
        r = Math.Min(r, half);
        if (r < 0.5) return new RectangleGeometry(new Rect(inset, inset, w, h));

        // how far from the corner the curve starts; the smoothing shrinks where there is no room for it
        double p = Math.Min((1 + smoothing) * r, half);
        double xi = Math.Clamp(p / r - 1, 0, smoothing);
        double arcDeg = 90 * (1 - xi);
        double arcLen = Math.Sin(Rad(arcDeg / 2)) * r * Math.Sqrt(2);
        double alpha = (90 - arcDeg) / 2;
        double p34 = r * Math.Tan(Rad(alpha / 2));
        double beta = 45 * xi;
        double c = p34 * Math.Cos(Rad(beta));
        double d = c * Math.Tan(Rad(beta));
        double b = Math.Max(0, (p - arcLen - c - d) / 3);
        double a = 2 * b;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double L = inset, T = inset, R = inset + w, B = inset + h;
            var arc = new Size(r, r);
            ctx.BeginFigure(new Point(L + p, T), true, true);
            // top-right
            ctx.LineTo(new Point(R - p, T), true, true);
            ctx.BezierTo(new Point(R - p + a, T), new Point(R - p + a + b, T), new Point(R - p + a + b + c, T + d), true, true);
            ctx.ArcTo(new Point(R - d, T + p - a - b - c), arc, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(new Point(R, T + p - a - b), new Point(R, T + p - a), new Point(R, T + p), true, true);
            // bottom-right
            ctx.LineTo(new Point(R, B - p), true, true);
            ctx.BezierTo(new Point(R, B - p + a), new Point(R, B - p + a + b), new Point(R - d, B - p + a + b + c), true, true);
            ctx.ArcTo(new Point(R - p + a + b + c, B - d), arc, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(new Point(R - p + a + b, B), new Point(R - p + a, B), new Point(R - p, B), true, true);
            // bottom-left
            ctx.LineTo(new Point(L + p, B), true, true);
            ctx.BezierTo(new Point(L + p - a, B), new Point(L + p - a - b, B), new Point(L + p - a - b - c, B - d), true, true);
            ctx.ArcTo(new Point(L + d, B - p + a + b + c), arc, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(new Point(L, B - p + a + b), new Point(L, B - p + a), new Point(L, B - p), true, true);
            // top-left
            ctx.LineTo(new Point(L, T + p), true, true);
            ctx.BezierTo(new Point(L, T + p - a), new Point(L, T + p - a - b), new Point(L + d, T + p - a - b - c), true, true);
            ctx.ArcTo(new Point(L + p - a - b - c, T + d), arc, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(new Point(L + p - a - b, T), new Point(L + p - a, T), new Point(L + p, T), true, true);
        }
        g.Freeze();
        return g;
    }

    private static double Rad(double deg) => deg * Math.PI / 180;
}
