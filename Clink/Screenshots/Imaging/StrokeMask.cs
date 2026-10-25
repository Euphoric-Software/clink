using System.Diagnostics;
using System.Windows;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// A CPU rasterizer for freehand strokes: a union of round-capped segments ("capsules"), as an 8-bit coverage mask. Not a raster
/// op (it reads no image); the mask feeds <see cref="Highlight.Multiply(BgraImage, BgraImage, RectI, AlphaMask, System.Windows.Media.Color)"/>.
/// </summary>
internal static class StrokeMask
{
    /// <summary>
    /// The coverage of the polyline <paramref name="pts"/> drawn with a round pen of <paramref name="width"/> (image pixels),
    /// limited to <paramref name="clip"/>. A pixel is sampled at its centre: full coverage up to a distance of width/2 - 0.5 from the
    /// line, falling to 0 over the next pixel (a 1-px antialiasing ramp). The coverage of the segments is combined with MAX, so a
    /// stroke crossing itself does not get darker there. One point is a dot; non-finite points are skipped. The mask's bounds are the
    /// stroke's bounding box (inflated by half the width plus the ramp) clipped to <paramref name="clip"/>.
    /// </summary>
    public static AlphaMask Rasterize(IReadOnlyList<Point> pts, double width, RectI clip)
    {
        if (pts.Count == 0 || !(width > 0) || clip.IsEmpty) return AlphaMask.None;
        long t0 = Stopwatch.GetTimestamp();
        double hw = width / 2;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var good = new List<Point>(pts.Count);
        foreach (var p in pts)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            good.Add(p);
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        if (good.Count == 0) return AlphaMask.None;
        var bounds = RectI.FromLtrb(ToInt(Math.Floor(minX - hw - 1)), ToInt(Math.Floor(minY - hw - 1)),
                                    ToInt(Math.Ceiling(maxX + hw + 1)), ToInt(Math.Ceiling(maxY + hw + 1))).Intersect(clip);
        if (bounds.IsEmpty) return AlphaMask.None;
        int bw = bounds.Width;
        var cov = new byte[bw * bounds.Height];
        int segments = Math.Max(1, good.Count - 1);
        for (int s = 0; s < segments; s++)
        {
            var a = good[s];
            var b = good.Count == 1 ? a : good[s + 1];
            double abx = b.X - a.X, aby = b.Y - a.Y, len2 = abx * abx + aby * aby;
            int x0 = Math.Max(bounds.X, ToInt(Math.Floor(Math.Min(a.X, b.X) - hw - 1)));
            int x1 = Math.Min(bounds.Right, ToInt(Math.Ceiling(Math.Max(a.X, b.X) + hw + 1)));
            int y0 = Math.Max(bounds.Y, ToInt(Math.Floor(Math.Min(a.Y, b.Y) - hw - 1)));
            int y1 = Math.Min(bounds.Bottom, ToInt(Math.Ceiling(Math.Max(a.Y, b.Y) + hw + 1)));
            for (int y = y0; y < y1; y++)
            {
                double cy = y + 0.5;
                int row = (y - bounds.Y) * bw - bounds.X;
                for (int x = x0; x < x1; x++)
                {
                    double cx = x + 0.5;
                    double t = len2 > 0 ? ((cx - a.X) * abx + (cy - a.Y) * aby) / len2 : 0;
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    double dx = cx - (a.X + t * abx), dy = cy - (a.Y + t * aby);
                    double c = hw + 0.5 - Math.Sqrt(dx * dx + dy * dy);
                    if (c <= 0) continue;
                    byte v = c >= 1 ? (byte)255 : (byte)(c * 255 + 0.5);
                    if (v > cov[row + x]) cov[row + x] = v;
                }
            }
        }
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("stroke-mask", $"{good.Count} pts width {width:0.#} bounds {bounds}", ms);
        return new AlphaMask(bounds, cov);
    }

    /// <summary>The bounds <see cref="Rasterize"/> gives its mask (empty when it makes none), without rasterizing: the stroke's footprint for layout.</summary>
    public static RectI BoundsOf(IReadOnlyList<Point> pts, double width, RectI clip)
    {
        if (pts.Count == 0 || !(width > 0) || clip.IsEmpty) return RectI.Empty;
        double hw = width / 2;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var p in pts)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            any = true;
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        if (!any) return RectI.Empty;
        return RectI.FromLtrb(ToInt(Math.Floor(minX - hw - 1)), ToInt(Math.Floor(minY - hw - 1)),
                              ToInt(Math.Ceiling(maxX + hw + 1)), ToInt(Math.Ceiling(maxY + hw + 1))).Intersect(clip);
    }

    private static int ToInt(double v) => v <= -1e9 ? -1_000_000_000 : v >= 1e9 ? 1_000_000_000 : (int)v;
}
