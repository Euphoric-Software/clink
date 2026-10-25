using System.Windows;

namespace Clink.Screenshots.Geometry;

/// <summary>Pure polyline smoothing for freehand strokes.</summary>
internal static class StrokeSmoothing
{
    /// <summary>
    /// Chaikin corner cutting of an open polyline: every segment is replaced by the points at 1/4 and 3/4 along it, the first and
    /// last points of the stroke are kept as they are (so an arrow still ends where the hand ended). Fewer than 3 points: unchanged.
    /// </summary>
    public static List<Point> Chaikin(IReadOnlyList<Point> pts, int iterations)
    {
        var cur = pts.ToList();
        for (int it = 0; it < iterations && cur.Count >= 3; it++)
        {
            var next = new List<Point>(cur.Count * 2) { cur[0] };
            for (int i = 0; i < cur.Count - 1; i++)
            {
                var a = cur[i];
                var b = cur[i + 1];
                var q = new Point(0.75 * a.X + 0.25 * b.X, 0.75 * a.Y + 0.25 * b.Y);
                var r = new Point(0.25 * a.X + 0.75 * b.X, 0.25 * a.Y + 0.75 * b.Y);
                if (i > 0) next.Add(q);
                if (i < cur.Count - 2) next.Add(r);
            }
            next.Add(cur[^1]);
            cur = next;
        }
        return cur;
    }

    /// <summary>The sum of the absolute turning angles along the polyline, in radians (a straight line turns 0).</summary>
    public static double TotalTurning(IReadOnlyList<Point> pts)
    {
        double sum = 0;
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            var a = pts[i] - pts[i - 1];
            var b = pts[i + 1] - pts[i];
            if (a.LengthSquared < 1e-18 || b.LengthSquared < 1e-18) continue;
            double ang = Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
            sum += Math.Abs(ang);
        }
        return sum;
    }

    /// <summary>The sum of the squared turning angles (a bending energy): corner cutting splits a sharp turn into smaller ones, so this falls as a stroke is smoothed even though the plain sum of the angles does not.</summary>
    public static double BendingEnergy(IReadOnlyList<Point> pts)
    {
        double sum = 0;
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            var a = pts[i] - pts[i - 1];
            var b = pts[i + 1] - pts[i];
            if (a.LengthSquared < 1e-18 || b.LengthSquared < 1e-18) continue;
            double ang = Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
            sum += ang * ang;
        }
        return sum;
    }

    /// <summary>The sharpest single turn along the polyline, in radians.</summary>
    public static double MaxTurning(IReadOnlyList<Point> pts)
    {
        double max = 0;
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            var a = pts[i] - pts[i - 1];
            var b = pts[i + 1] - pts[i];
            if (a.LengthSquared < 1e-18 || b.LengthSquared < 1e-18) continue;
            max = Math.Max(max, Math.Abs(Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y)));
        }
        return max;
    }

    /// <summary>Drops points closer than <paramref name="minDistance"/> to the previous kept one (the last point is always kept).</summary>
    public static List<Point> Decimate(IReadOnlyList<Point> pts, double minDistance)
    {
        var result = new List<Point>();
        foreach (var p in pts)
            if (result.Count == 0 || (p - result[^1]).Length >= minDistance) result.Add(p);
        if (pts.Count > 1 && result[^1] != pts[^1]) result.Add(pts[^1]);
        return result;
    }
}
