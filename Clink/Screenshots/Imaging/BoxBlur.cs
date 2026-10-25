using System.Buffers;
using System.Diagnostics;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// Gaussian blur approximated by three box blurs (Kovesi's radii for the sigma), each a separable running sum: O(pixels) whatever
/// the radius. Edges clamp. Each channel (alpha too, unless the area is opaque) is blurred on its own as straight values.
/// </summary>
internal static class BoxBlur
{
    /// <summary>The radii of the three box passes whose combined variance best matches <paramref name="sigma"/> (all 0 for sigma &lt;= 0).</summary>
    public static int[] RadiiForSigma(double sigma)
    {
        var radii = new int[3];
        if (!(sigma > 0)) return radii;
        if (sigma > 1000) sigma = 1000;
        const int n = 3;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        if (wl < 1) wl = 1;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        for (int i = 0; i < n; i++) radii[i] = ((i < m ? wl : wu) - 1) / 2;
        return radii;
    }

    /// <summary>Blurs the image in place (<paramref name="region"/> only; it reads up to 3 radii around it).</summary>
    public static void Blur(BgraImage img, RectI region, double sigma) => Blur(img, region, sigma, img);

    /// <summary>
    /// Blurs <paramref name="region"/> (clipped to the image) of <paramref name="src"/> into <paramref name="dst"/>, which has the size
    /// of <paramref name="src"/> (or is <paramref name="src"/> itself). Reads <paramref name="src"/> inflated by the sum of the three
    /// radii (clipped to the image), writes only <paramref name="region"/>.
    /// </summary>
    public static void Blur(BgraImage src, RectI region, double sigma, BgraImage dst)
    {
        if (src.Width != dst.Width || src.Height != dst.Height) throw new ArgumentException("dst must have the size of src", nameof(dst));
        region = region.Intersect(src.Bounds);
        if (region.IsEmpty) return;
        long t0 = Stopwatch.GetTimestamp();
        var radii = RadiiForSigma(sigma);
        int margin = radii[0] + radii[1] + radii[2];
        if (margin == 0)
        {
            if (!ReferenceEquals(src, dst)) dst.Blit(src, region, region.X, region.Y);
            return;
        }
        var e = region.Inflate(margin).Intersect(src.Bounds);
        int ew = e.Width, eh = e.Height;
        int len = ew * eh;
        long pixels = len;

        bool opaque = true;
        for (int y = e.Y; y < e.Bottom && opaque; y++)
        {
            int i = y * src.Stride + e.X * 4 + 3;
            for (int x = 0; x < ew; x++, i += 4) if (src.Pixels[i] != 255) { opaque = false; break; }
        }

        var a = ArrayPool<float>.Shared.Rent(len);
        var b = ArrayPool<float>.Shared.Rent(len);
        try
        {
            for (int c = 0; c < (opaque ? 3 : 4); c++)
            {
                var sp = src.Pixels;
                int stride = src.Stride;
                var plane = a;
                ImagingUtil.ForRows(0, eh, pixels, row =>
                {
                    int si = (e.Y + row) * stride + e.X * 4 + c, di = row * ew;
                    for (int x = 0; x < ew; x++, si += 4) plane[di + x] = sp[si];
                });
                var cur = a;
                var other = b;
                foreach (int r in radii) if (r > 0) { HPass(cur, other, ew, eh, r, pixels); (cur, other) = (other, cur); }
                foreach (int r in radii) if (r > 0) { VPass(cur, other, ew, eh, r, pixels); (cur, other) = (other, cur); }
                var dp = dst.Pixels;
                int dstride = dst.Stride;
                var result = cur;
                ImagingUtil.ForRows(region.Y, region.Bottom, (long)region.Width * region.Height, y =>
                {
                    int di = y * dstride + region.X * 4 + c, ri = (y - e.Y) * ew + (region.X - e.X);
                    for (int x = 0; x < region.Width; x++, di += 4, ri++)
                    {
                        int v = (int)(result[ri] + 0.5f);
                        dp[di] = v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
                    }
                });
            }
            if (opaque)
            {
                var dp = dst.Pixels;
                for (int y = region.Y; y < region.Bottom; y++)
                {
                    int di = y * dst.Stride + region.X * 4 + 3;
                    for (int x = 0; x < region.Width; x++, di += 4) dp[di] = 255;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(a);
            ArrayPool<float>.Shared.Return(b);
        }
        ImagingUtil.LogTime("blur", $"{region.Width}x{region.Height} sigma {sigma:0.##} radii {radii[0]},{radii[1]},{radii[2]}", t0);
    }

    private static void HPass(float[] a, float[] b, int w, int h, int r, long pixels)
    {
        double d = 2 * r + 1;
        ImagingUtil.ForRows(0, h, pixels, y =>
        {
            int off = y * w;
            double sum = (r + 1) * (double)a[off];
            for (int j = 1; j <= r; j++) sum += a[off + Math.Min(j, w - 1)];
            for (int i = 0; i < w; i++)
            {
                b[off + i] = (float)(sum / d);
                sum += a[off + Math.Min(i + 1 + r, w - 1)] - a[off + Math.Max(i - r, 0)];
            }
        });
    }

    private static void VPass(float[] a, float[] b, int w, int h, int r, long pixels)
    {
        double d = 2 * r + 1;
        const int strip = 64;
        int strips = (w + strip - 1) / strip;
        Action<int> body = s =>
        {
            int x0 = s * strip, x1 = Math.Min(w, x0 + strip), sw = x1 - x0;
            var sum = new double[sw];
            for (int x = 0; x < sw; x++) sum[x] = (r + 1) * (double)a[x0 + x];
            for (int j = 1; j <= r; j++)
            {
                int row = Math.Min(j, h - 1) * w + x0;
                for (int x = 0; x < sw; x++) sum[x] += a[row + x];
            }
            for (int y = 0; y < h; y++)
            {
                int o = y * w + x0, add = Math.Min(y + 1 + r, h - 1) * w + x0, rem = Math.Max(y - r, 0) * w + x0;
                for (int x = 0; x < sw; x++)
                {
                    b[o + x] = (float)(sum[x] / d);
                    sum[x] += a[add + x] - a[rem + x];
                }
            }
        };
        if (pixels >= ImagingUtil.ParallelPixels && strips > 1) Parallel.For(0, strips, body);
        else for (int s = 0; s < strips; s++) body(s);
    }
}
