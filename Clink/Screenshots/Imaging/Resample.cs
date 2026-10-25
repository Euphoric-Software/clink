using System.Diagnostics;

namespace Clink.Screenshots.Imaging;

/// <summary>Scaling and compositing of images (straight alpha).</summary>
internal static class Resample
{
    /// <summary>
    /// Scales <paramref name="srcRect"/> of <paramref name="src"/> into <paramref name="dstRect"/> of <paramref name="dst"/>
    /// (bilinear, pixel centres aligned; the part of <paramref name="dstRect"/> outside <paramref name="dst"/> is not computed).
    /// Equal sizes copy. When an axis shrinks by more than 2 x, the source is first reduced by a box filter (an integer factor per
    /// axis) so that no pixel is skipped. Sampling clamps to the source rectangle. Interpolation is alpha-weighted, so a transparent
    /// pixel's colour does not bleed into its neighbours.
    /// </summary>
    public static void Bilinear(BgraImage src, RectI srcRect, BgraImage dst, RectI dstRect)
    {
        var sr = srcRect.Intersect(src.Bounds);
        if (sr.IsEmpty || dstRect.IsEmpty) return;
        var clip = dstRect.Intersect(dst.Bounds);
        if (clip.IsEmpty) return;
        long t0 = Stopwatch.GetTimestamp();

        if (srcRect.Width == dstRect.Width && srcRect.Height == dstRect.Height)
        {
            dst.Blit(src, srcRect, dstRect.X, dstRect.Y);
            return;
        }

        double rx = (double)sr.Width / dstRect.Width, ry = (double)sr.Height / dstRect.Height;
        if (rx > 2 || ry > 2)
        {
            var reduced = BoxReduce(src, sr, rx > 2 ? (int)rx : 1, ry > 2 ? (int)ry : 1);
            Bilinear(reduced, reduced.Bounds, dst, dstRect);
            return;
        }

        // Map by the unclipped rectangles; clamp to what the source really has.
        double sx0 = srcRect.X, sy0 = srcRect.Y;
        double kx = (double)srcRect.Width / dstRect.Width, ky = (double)srcRect.Height / dstRect.Height;
        int n = clip.Width;
        var xi0 = new int[n];
        var xi1 = new int[n];
        var fx = new float[n];
        for (int i = 0; i < n; i++)
        {
            double sx = sx0 + (clip.X + i - dstRect.X + 0.5) * kx - 0.5;
            sx = Math.Clamp(sx, sr.X, sr.Right - 1);
            int a = (int)Math.Floor(sx);
            xi0[i] = a; xi1[i] = Math.Min(a + 1, sr.Right - 1); fx[i] = (float)(sx - a);
        }
        var sp = src.Pixels;
        var dp = dst.Pixels;
        int ss = src.Stride, ds = dst.Stride;
        ImagingUtil.ForRows(clip.Y, clip.Bottom, clip.Area, y =>
        {
            double sy = sy0 + (y - dstRect.Y + 0.5) * ky - 0.5;
            sy = Math.Clamp(sy, sr.Y, sr.Bottom - 1);
            int ya = (int)Math.Floor(sy), yb = Math.Min(ya + 1, sr.Bottom - 1);
            float fy = (float)(sy - ya);
            int ra = ya * ss, rb = yb * ss;
            int di = y * ds + clip.X * 4;
            for (int i = 0; i < n; i++, di += 4)
            {
                int p00 = ra + xi0[i] * 4, p10 = ra + xi1[i] * 4, p01 = rb + xi0[i] * 4, p11 = rb + xi1[i] * 4;
                float wx = fx[i];
                float w00 = (1 - wx) * (1 - fy), w10 = wx * (1 - fy), w01 = (1 - wx) * fy, w11 = wx * fy;
                if (sp[p00 + 3] == 255 && sp[p10 + 3] == 255 && sp[p01 + 3] == 255 && sp[p11 + 3] == 255)
                {
                    for (int c = 0; c < 3; c++)
                        dp[di + c] = ImagingUtil.ClampByte(sp[p00 + c] * w00 + sp[p10 + c] * w10 + sp[p01 + c] * w01 + sp[p11 + c] * w11);
                    dp[di + 3] = 255;
                }
                else
                {
                    float a00 = sp[p00 + 3] * w00, a10 = sp[p10 + 3] * w10, a01 = sp[p01 + 3] * w01, a11 = sp[p11 + 3] * w11;
                    float asum = a00 + a10 + a01 + a11;
                    if (asum <= 0) { dp[di] = dp[di + 1] = dp[di + 2] = dp[di + 3] = 0; continue; }
                    for (int c = 0; c < 3; c++)
                        dp[di + c] = ImagingUtil.ClampByte((sp[p00 + c] * a00 + sp[p10 + c] * a10 + sp[p01 + c] * a01 + sp[p11 + c] * a11) / asum);
                    dp[di + 3] = ImagingUtil.ClampByte(asum);
                }
            }
        });
        ImagingUtil.LogTime("bilinear", $"{sr.Width}x{sr.Height} -> {dstRect.Width}x{dstRect.Height}", t0);
    }

    /// <summary>
    /// Averages blocks of <paramref name="kx"/> x <paramref name="ky"/> pixels of <paramref name="r"/> into a new image (the blocks
    /// at the right / bottom edge average what they have). Alpha-weighted like <see cref="Bilinear"/>.
    /// </summary>
    public static BgraImage BoxReduce(BgraImage src, RectI r, int kx, int ky)
    {
        r = r.Intersect(src.Bounds);
        kx = Math.Max(1, kx); ky = Math.Max(1, ky);
        int ow = (r.Width + kx - 1) / kx, oh = (r.Height + ky - 1) / ky;
        var o = new BgraImage(ow, oh);
        if (ow == 0 || oh == 0) return o;
        long t0 = Stopwatch.GetTimestamp();
        var sp = src.Pixels;
        var op = o.Pixels;
        ImagingUtil.ForRows(0, oh, r.Area, oy =>
        {
            int y0 = r.Y + oy * ky, y1 = Math.Min(r.Bottom, y0 + ky);
            for (int ox = 0; ox < ow; ox++)
            {
                int x0 = r.X + ox * kx, x1 = Math.Min(r.Right, x0 + kx);
                double sb = 0, sg = 0, sr = 0, sa = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = y * src.Stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        double a = sp[i + 3];
                        sb += sp[i] * a; sg += sp[i + 1] * a; sr += sp[i + 2] * a; sa += a;
                    }
                }
                int n = (x1 - x0) * (y1 - y0), oi = (oy * ow + ox) * 4;
                if (sa <= 0) continue;
                op[oi] = ImagingUtil.ClampByte(sb / sa);
                op[oi + 1] = ImagingUtil.ClampByte(sg / sa);
                op[oi + 2] = ImagingUtil.ClampByte(sr / sa);
                op[oi + 3] = ImagingUtil.ClampByte(sa / n);
            }
        });
        ImagingUtil.LogTime("box-reduce", $"{r.Width}x{r.Height} by {kx}x{ky}", t0);
        return o;
    }

    /// <summary>
    /// Draws <paramref name="src"/> scaled into <paramref name="dest"/> (a rectangle in <paramref name="dst"/>'s pixels, clipped to it)
    /// over <paramref name="dst"/>, straight-alpha source-over, with the source's alpha multiplied by <paramref name="opacity"/>
    /// (0..1). Opacity 0 changes nothing; an opaque source at opacity 1 replaces.
    /// </summary>
    public static void Over(BgraImage dst, BgraImage src, RectI dest, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        var vis = dest.Intersect(dst.Bounds);
        if (vis.IsEmpty || src.Width == 0 || src.Height == 0 || opacity <= 0) return;
        long t0 = Stopwatch.GetTimestamp();
        // Scale into a temporary of the visible part only (the destination can be mostly off the image).
        var tmp = new BgraImage(vis.Width, vis.Height);
        Bilinear(src, src.Bounds, tmp, dest.Offset(-vis.X, -vis.Y));
        var tp = tmp.Pixels;
        var dp = dst.Pixels;
        int op = (int)Math.Round(opacity * 256);   // 0..256
        ImagingUtil.ForRows(0, vis.Height, vis.Area, ty =>
        {
            int ti = ty * tmp.Stride, di = (vis.Y + ty) * dst.Stride + vis.X * 4;
            for (int x = 0; x < vis.Width; x++, ti += 4, di += 4)
            {
                int aS = op >= 256 ? tp[ti + 3] : (tp[ti + 3] * op + 128) >> 8;
                if (aS == 0) continue;
                if (aS == 255)
                {
                    dp[di] = tp[ti]; dp[di + 1] = tp[ti + 1]; dp[di + 2] = tp[ti + 2]; dp[di + 3] = 255;
                    continue;
                }
                int aD = dp[di + 3];
                int aDw = aD * (255 - aS);                       // destination weight, scaled by 255
                int outA255 = aS * 255 + aDw;                    // out alpha, scaled by 255
                if (outA255 == 0) continue;
                for (int c = 0; c < 3; c++)
                    dp[di + c] = (byte)((tp[ti + c] * aS * 255 + dp[di + c] * aDw + outA255 / 2) / outA255);
                dp[di + 3] = (byte)((outA255 + 127) / 255);
            }
        });
        ImagingUtil.LogTime("over", $"{src.Width}x{src.Height} -> {dest} opacity {opacity:0.##}", t0);
    }
}
