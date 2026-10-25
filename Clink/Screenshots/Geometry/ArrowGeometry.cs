using System.Windows;

namespace Clink.Screenshots.Geometry;

/// <summary>An arrow ready to draw: the shaft polyline (cut short so it does not poke through the head) and the head triangle, or no head for a dot.</summary>
internal sealed record ArrowShape(Point[] Shaft, bool HasHead, Point Tip, Point BaseLeft, Point BaseRight);

/// <summary>
/// Pure arrow-head geometry (document pixels). Straight arrows and freehand arrows share <see cref="Head"/>; a freehand arrow's
/// direction is the principal axis of the end of the stroke (<see cref="FreehandDirection"/>), which ignores hand jitter.
/// </summary>
internal static class ArrowGeometry
{
    public const double HeadHalfAngleDegrees = 25;
    private const double ShaftOverlap = 0.8;   // the shaft stops this fraction of a head length short of the tip

    /// <summary>Head length for a stroke width: max(10, 4.5 * width).</summary>
    public static double HeadLength(double width) => Math.Max(10, 4.5 * width);

    /// <summary>The head triangle for an arrow whose tip is at <paramref name="tip"/> and that points along the unit vector <paramref name="dir"/>.</summary>
    public static (Point Tip, Point BaseLeft, Point BaseRight) Head(Point tip, Vector dir, double width)
    {
        double len = HeadLength(width);
        double half = len * Math.Tan(HeadHalfAngleDegrees * Math.PI / 180);
        var back = new Point(tip.X - dir.X * len, tip.Y - dir.Y * len);
        var perp = new Vector(-dir.Y, dir.X);
        return (tip, new Point(back.X + perp.X * half, back.Y + perp.Y * half), new Point(back.X - perp.X * half, back.Y - perp.Y * half));
    }

    public static double Length(IReadOnlyList<Point> pts)
    {
        double sum = 0;
        for (int i = 1; i < pts.Count; i++) sum += (pts[i] - pts[i - 1]).Length;
        return sum;
    }

    /// <summary>The polyline sampled every <paramref name="step"/> of arc length from its first point; the last point is always included.</summary>
    public static List<Point> Resample(IReadOnlyList<Point> pts, double step)
    {
        var result = new List<Point>();
        if (pts.Count == 0) return result;
        result.Add(pts[0]);
        double carry = 0;   // arc length walked since the last sample
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var seg = pts[i] - a;
            double segLen = seg.Length;
            if (segLen <= 0) continue;
            double at = step - carry;   // distance into this segment of the next sample
            while (at <= segLen)
            {
                result.Add(a + seg * (at / segLen));
                at += step;
            }
            carry = segLen - (at - step);
        }
        if (pts.Count > 1 && (result[^1] - pts[^1]).Length > 1e-9) result.Add(pts[^1]);
        return result;
    }

    /// <summary>
    /// The direction a freehand arrow's head points: resample by arc length (2 px), take the last
    /// <c>D = clamp(3 * headLength, 12, 40% of the stroke)</c> of it, and use the principal axis (largest eigenvector of the 2x2
    /// covariance) of those points, oriented from the window's first point to its last. A stroke shorter than the head length
    /// uses first-to-last; a stroke shorter than 2 px has no head (null).
    /// </summary>
    public static Vector? FreehandDirection(IReadOnlyList<Point> pts, double width)
    {
        double total = Length(pts);
        if (pts.Count < 2 || total < 2) return null;
        double headLen = HeadLength(width);
        var overall = Direction(pts[0], pts[^1]);
        if (total < headLen) return overall ?? Direction(pts[0], FarthestFrom(pts, pts[0]));

        var res = Resample(pts, 2);
        double window = Math.Min(Math.Max(3 * headLen, 12), 0.4 * total);
        window = Math.Max(window, Math.Min(12, total));
        double from = total - window;
        var inWindow = new List<Point>();
        double walked = 0;
        for (int i = 0; i < res.Count; i++)
        {
            if (i > 0) walked += (res[i] - res[i - 1]).Length;
            if (walked >= from - 1e-9) inWindow.Add(res[i]);
        }
        if (inWindow.Count < 2) return overall;

        double mx = inWindow.Average(p => p.X), my = inWindow.Average(p => p.Y);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in inWindow) { double dx = p.X - mx, dy = p.Y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        if (sxx + syy < 1e-9) return overall;
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);   // angle of the largest eigenvector
        var axis = new Vector(Math.Cos(theta), Math.Sin(theta));
        var travel = inWindow[^1] - inWindow[0];
        if (Vector.Multiply(axis, travel) < 0) axis = -axis;
        return axis;
    }

    private static Point FarthestFrom(IReadOnlyList<Point> pts, Point p) => pts.OrderByDescending(q => (q - p).LengthSquared).First();

    /// <summary>Unit vector from a to b; null when they coincide.</summary>
    public static Vector? Direction(Point a, Point b)
    {
        var v = b - a;
        double len = v.Length;
        return len < 1e-9 ? null : v / len;
    }

    /// <summary>The polyline cut after <paramref name="length"/> of arc length (all of it when it is shorter; just the first point for 0 or less).</summary>
    public static List<Point> CutAtLength(IReadOnlyList<Point> pts, double length)
    {
        var result = new List<Point>();
        if (pts.Count == 0) return result;
        result.Add(pts[0]);
        if (length <= 0) return result;
        double walked = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double seg = (pts[i] - pts[i - 1]).Length;
            if (walked + seg >= length)
            {
                double t = seg <= 0 ? 0 : (length - walked) / seg;
                result.Add(pts[i - 1] + (pts[i] - pts[i - 1]) * t);
                return result;
            }
            walked += seg;
            result.Add(pts[i]);
        }
        return result;
    }

    /// <summary>The freehand arrow for a stroke: Chaikin-smoothed shaft (2 iterations) cut at <c>L - 0.8 * headLength</c>, and the head at the stroke's end.</summary>
    public static ArrowShape Freehand(IReadOnlyList<Point> pts, double width)
    {
        if (pts.Count == 0) return new ArrowShape(Array.Empty<Point>(), false, default, default, default);
        var smooth = StrokeSmoothing.Chaikin(pts, 2);
        var dir = FreehandDirection(pts, width);
        if (dir is not { } d) return new ArrowShape(smooth.ToArray(), false, pts[^1], pts[^1], pts[^1]);
        double headLen = HeadLength(width);
        var shaft = CutAtLength(smooth, Length(smooth) - ShaftOverlap * headLen);
        var (tip, left, right) = Head(pts[^1], d, width);
        return new ArrowShape(shaft.ToArray(), true, tip, left, right);
    }

    /// <summary>A straight line / arrow from a to b; with <paramref name="arrow"/> the shaft stops short of the head. Under 2 px long: no head.</summary>
    public static ArrowShape Straight(Point a, Point b, double width, bool arrow)
    {
        var dir = Direction(a, b);
        if (!arrow || dir is not { } d || (b - a).Length < 2) return new ArrowShape(new[] { a, b }, false, b, b, b);
        double len = (b - a).Length;
        var end = a + d * Math.Max(0, len - ShaftOverlap * HeadLength(width));
        var (tip, left, right) = Head(b, d, width);
        return new ArrowShape(new[] { a, end }, true, tip, left, right);
    }
}
