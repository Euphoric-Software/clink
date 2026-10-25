using System.Windows.Media;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// The magnifier: a scaled copy of a part of the image, framed by an outline.
/// <para>Uniform op form: <c>Render(src, dst, clip, source, dest, smooth, outline, outlineWidth)</c> reads <c>src</c>, writes only
/// <c>clip ∩ dest</c> (clipped to <c>dst</c>) and nothing else. <c>dst</c> must not alias <c>src</c> (throws): the composer renders
/// from the below-composite into another buffer. NON-LOCAL: the pixels written (dest) are far from the pixels read (source), so a
/// change under the source rectangle invalidates the whole dest rectangle.</para>
/// </summary>
internal static class Magnifier
{
    /// <summary>The colour of the thin ring just outside the outline.</summary>
    public static readonly Color OuterRing = Color.FromRgb(0x20, 0x20, 0x20);

    /// <summary>
    /// Draws <paramref name="source"/> (a rectangle of <paramref name="src"/>) scaled into <paramref name="dest"/> of
    /// <paramref name="dst"/> (any size; only the part of dest inside <paramref name="clip"/> and <paramref name="dst"/> is drawn, as
    /// if the whole were). Nearest neighbour by default (crisp pixels), bilinear when <paramref name="smooth"/>. Samples are clamped
    /// to <paramref name="src"/>, so a source rectangle partly outside the image repeats the edge pixels. Then the frame: the
    /// outermost pixel row / column of <paramref name="dest"/> is a 1-px dark ring (<see cref="OuterRing"/>) and the next
    /// <paramref name="outlineWidth"/> pixels (rounded; 0 = no frame at all) are <paramref name="outline"/> (its alpha applies over
    /// the magnified pixels). The frame lies inside <paramref name="dest"/>, so it never reaches outside the rectangle.
    /// </summary>
    public static void Render(BgraImage src, BgraImage dst, RectI clip, RectI source, RectI dest, bool smooth, Color outline, double outlineWidth)
    {
        ImagingUtil.RequireNoAlias(src, dst);
        var vis = dest.Intersect(dst.Bounds).Intersect(clip);
        if (vis.IsEmpty || source.IsEmpty || src.Width == 0 || src.Height == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double kx = (double)source.Width / dest.Width, ky = (double)source.Height / dest.Height;
        int bw = src.Width, bh = src.Height;
        var sp = src.Pixels;
        var dp = dst.Pixels;
        int ss = src.Stride, ds = dst.Stride;
        ImagingUtil.ForRows(vis.Y, vis.Bottom, vis.Area, y =>
        {
            double fy = source.Y + (y - dest.Y + 0.5) * ky;
            int di = y * ds + vis.X * 4;
            if (!smooth)
            {
                int sy = Math.Clamp((int)Math.Floor(fy), 0, bh - 1);
                for (int x = vis.X; x < vis.Right; x++, di += 4)
                {
                    int sx = Math.Clamp((int)Math.Floor(source.X + (x - dest.X + 0.5) * kx), 0, bw - 1);
                    int si = sy * ss + sx * 4;
                    dp[di] = sp[si]; dp[di + 1] = sp[si + 1]; dp[di + 2] = sp[si + 2]; dp[di + 3] = sp[si + 3];
                }
                return;
            }
            double cy = Math.Clamp(fy - 0.5, 0, bh - 1);
            int ya = (int)cy, yb = Math.Min(ya + 1, bh - 1);
            float wy = (float)(cy - ya);
            for (int x = vis.X; x < vis.Right; x++, di += 4)
            {
                double cx = Math.Clamp(source.X + (x - dest.X + 0.5) * kx - 0.5, 0, bw - 1);
                int xa = (int)cx, xb = Math.Min(xa + 1, bw - 1);
                float wx = (float)(cx - xa);
                int p00 = ya * ss + xa * 4, p10 = ya * ss + xb * 4, p01 = yb * ss + xa * 4, p11 = yb * ss + xb * 4;
                for (int c = 0; c < 4; c++)
                {
                    float top = sp[p00 + c] + (sp[p10 + c] - sp[p00 + c]) * wx;
                    float bot = sp[p01 + c] + (sp[p11 + c] - sp[p01 + c]) * wx;
                    dp[di + c] = ImagingUtil.ClampByte(top + (bot - top) * wy);
                }
            }
        });

        int ow = (int)Math.Round(Math.Max(0, outlineWidth));
        if (ow > 0)
        {
            // Ring k (0 = outermost) is the dest rectangle shrunk by k: ring 0 dark, rings 1..ow the outline colour.
            for (int k = 0; k <= ow; k++)
            {
                var ring = dest.Inflate(-k);
                if (ring.IsEmpty) break;
                var color = k == 0 ? OuterRing : outline;
                FillRect(dst, new RectI(ring.X, ring.Y, ring.Width, 1), color, vis);
                if (ring.Height > 1) FillRect(dst, new RectI(ring.X, ring.Bottom - 1, ring.Width, 1), color, vis);
                if (ring.Height > 2)
                {
                    FillRect(dst, new RectI(ring.X, ring.Y + 1, 1, ring.Height - 2), color, vis);
                    if (ring.Width > 1) FillRect(dst, new RectI(ring.Right - 1, ring.Y + 1, 1, ring.Height - 2), color, vis);
                }
            }
        }
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("magnify", $"{source} -> {dest} {(smooth ? "smooth" : "nearest")}", ms);
    }

    private static void FillRect(BgraImage dst, RectI r, Color c, RectI limit)
    {
        r = r.Intersect(limit);
        if (r.IsEmpty) return;
        var p = dst.Pixels;
        int a = c.A;
        for (int y = r.Y; y < r.Bottom; y++)
        {
            int i = y * dst.Stride + r.X * 4;
            for (int x = r.X; x < r.Right; x++, i += 4)
            {
                if (a == 255) { p[i] = c.B; p[i + 1] = c.G; p[i + 2] = c.R; p[i + 3] = 255; continue; }
                p[i] = (byte)((c.B * a + p[i] * (255 - a) + 127) / 255);
                p[i + 1] = (byte)((c.G * a + p[i + 1] * (255 - a) + 127) / 255);
                p[i + 2] = (byte)((c.R * a + p[i + 2] * (255 - a) + 127) / 255);
            }
        }
    }
}
