using System.Diagnostics;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// The blurred versions of an image a spotlight blends between. They depend on the image and the radius only, never on the box, so
/// moving or resizing a spotlight re-blends but does not rebuild them. <see cref="Source"/> is held by reference (the unblurred
/// pixels inside the box and level 0 of a gradient): do not change it while the levels are in use.
/// </summary>
internal sealed class SpotlightLevels
{
    public SpotlightLevels(BgraImage source, int radius, bool gradient, BgraImage[] levels, bool[] half)
    {
        Source = source; Radius = radius; Gradient = gradient; Levels = levels; Half = half;
    }

    public BgraImage Source { get; }
    public int Radius { get; }
    public bool Gradient { get; }
    /// <summary>Fixed: one level, the image blurred with sigma = Radius. Gradient: 5 levels, 0 = the image itself, k = sigma Radius * k / 4.</summary>
    public BgraImage[] Levels { get; }
    /// <summary>Level k is stored at half resolution (and sampled bilinearly).</summary>
    public bool[] Half { get; }
}

/// <summary>The spotlight: the box stays as it is, the rest is blurred and dimmed.</summary>
internal static class Spotlight
{
    /// <summary>Levels with a sigma from here on are computed (and kept) at half resolution.</summary>
    private const double HalfResSigma = 6;

    public static SpotlightLevels Build(BgraImage below, int radius, bool gradient)
    {
        long t0 = Stopwatch.GetTimestamp();
        radius = Math.Max(0, radius);
        int w = below.Width, h = below.Height;
        if (w == 0 || h == 0 || radius == 0)
        {
            var same = gradient ? new[] { below, below, below, below, below } : new[] { below };
            return new SpotlightLevels(below, radius, gradient, same, new bool[same.Length]);
        }
        int count = gradient ? 5 : 1;
        var levels = new BgraImage[count];
        var half = new bool[count];
        BgraImage prev = below;           // previous level at full resolution
        BgraImage? halfPrev = null;       // previous level at half resolution, once the chain went there
        double sPrev = 0;
        int first = gradient ? 1 : 0;
        if (gradient) levels[0] = below;
        for (int k = first; k < count; k++)
        {
            double s = gradient ? radius * k / 4.0 : radius;
            double inc = Math.Sqrt(Math.Max(0, s * s - sPrev * sPrev));
            if (s >= HalfResSigma)
            {
                halfPrev ??= Downsample(prev);
                var next = new BgraImage(halfPrev.Width, halfPrev.Height);
                BoxBlur.Blur(halfPrev, halfPrev.Bounds, inc / 2, next);
                halfPrev = next;
                levels[k] = next;
                half[k] = true;
            }
            else
            {
                var next = new BgraImage(w, h);
                BoxBlur.Blur(prev, prev.Bounds, inc, next);
                prev = next;
                levels[k] = next;
            }
            sPrev = s;
        }
        ImagingUtil.LogTime("spotlight-levels", $"{w}x{h} radius {radius} {(gradient ? "gradient" : "fixed")}", t0);
        return new SpotlightLevels(below, radius, gradient, levels, half);
    }

    /// <summary>
    /// Writes into <paramref name="dst"/> (the size of the levels' source; every pixel is written) the image with everything outside
    /// <paramref name="box"/> blurred and darkened: <paramref name="dim"/> 0 = not darkened, 1 = black. Inside the box: the source as it
    /// is; across a 2-px feather at the box edge the effect fades in. Fixed levels: the outside is the one blurred level. Gradient:
    /// with d the distance to the box, t = clamp(d / falloffPx, 0, 1), f = 4t, the outside is the interpolation between levels
    /// floor(f) and ceil(f). Alpha is blended like the colour but never darkened.
    /// </summary>
    public static void Blend(SpotlightLevels levels, RectI box, double dim, int falloffPx, BgraImage dst)
    {
        var src = levels.Source;
        if (dst.Width != src.Width || dst.Height != src.Height) throw new ArgumentException("dst must have the size of the levels' source", nameof(dst));
        int W = src.Width, H = src.Height;
        if (W == 0 || H == 0) return;
        long t0 = Stopwatch.GetTimestamp();
        double keep = 1 - Math.Clamp(dim, 0, 1);
        bool hasBox = !box.IsEmpty;
        int bx0 = box.X, bx1 = box.Right - 1, by0 = box.Y, by1 = box.Bottom - 1;
        const double Far = 1e9;

        var dxs = new int[W];
        for (int x = 0; x < W; x++) dxs[x] = hasBox ? Math.Max(Math.Max(bx0 - x, 0), x - bx1) : 0;
        // Horizontal sampling tables of the half-resolution levels.
        var hx0 = new int[W];
        var hx1 = new int[W];
        var hfx = new float[W];
        int firstHalf = Array.IndexOf(levels.Half, true);
        if (firstHalf >= 0)
        {
            var lv = levels.Levels[firstHalf];
            for (int x = 0; x < W; x++)
            {
                double s = Math.Clamp((x + 0.5) / 2 - 0.5, 0, lv.Width - 1);
                int a = (int)s;
                hx0[x] = a; hx1[x] = Math.Min(a + 1, lv.Width - 1); hfx[x] = (float)(s - a);
            }
        }

        var sp = src.Pixels;
        var dp = dst.Pixels;
        int stride = src.Stride;
        int nLevels = levels.Levels.Length;
        ImagingUtil.ForRows(0, H, (long)W * H, y =>
        {
            int dy = hasBox ? Math.Max(Math.Max(by0 - y, 0), y - by1) : 0;
            double hs = Math.Clamp((y + 0.5) / 2 - 0.5, 0, double.MaxValue);
            int row = y * stride;
            Span<float> va = stackalloc float[4];
            Span<float> vb = stackalloc float[4];
            for (int x = 0; x < W; x++)
            {
                int i = row + x * 4;
                double d = hasBox ? Math.Sqrt((double)dxs[x] * dxs[x] + (double)dy * dy) : Far;
                if (d == 0)
                {
                    dp[i] = sp[i]; dp[i + 1] = sp[i + 1]; dp[i + 2] = sp[i + 2]; dp[i + 3] = sp[i + 3];
                    continue;
                }
                if (levels.Gradient)
                {
                    double t = falloffPx > 0 ? Math.Min(1, d / falloffPx) : 1;
                    double f = t * (nLevels - 1);
                    int l0 = (int)f, l1 = Math.Min(l0 + 1, nLevels - 1);
                    float frac = (float)(f - l0);
                    Fetch(levels, l0, x, y, hs, hx0, hx1, hfx, stride, va);
                    if (frac > 0 && l1 != l0)
                    {
                        Fetch(levels, l1, x, y, hs, hx0, hx1, hfx, stride, vb);
                        for (int c = 0; c < 4; c++) va[c] += (vb[c] - va[c]) * frac;
                    }
                }
                else Fetch(levels, 0, x, y, hs, hx0, hx1, hfx, stride, va);
                float feather = d >= 2 ? 1f : (float)(d / 2);
                for (int c = 0; c < 4; c++)
                {
                    float outside = c < 3 ? va[c] * (float)keep : va[c];
                    float v = sp[i + c] + (outside - sp[i + c]) * feather;
                    dp[i + c] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
                }
            }
        });
        ImagingUtil.LogTime("spotlight-blend", $"{W}x{H} box {box}", t0);
    }

    private static void Fetch(SpotlightLevels lv, int k, int x, int y, double hs, int[] hx0, int[] hx1, float[] hfx, int fullStride, Span<float> o)
    {
        var img = lv.Levels[k];
        var p = img.Pixels;
        if (!lv.Half[k])
        {
            int i = y * fullStride + x * 4;
            o[0] = p[i]; o[1] = p[i + 1]; o[2] = p[i + 2]; o[3] = p[i + 3];
            return;
        }
        double sy = Math.Min(hs, img.Height - 1);
        int ya = (int)sy, yb = Math.Min(ya + 1, img.Height - 1);
        float fy = (float)(sy - ya), fx = hfx[x];
        int s = img.Stride;
        int p00 = ya * s + hx0[x] * 4, p10 = ya * s + hx1[x] * 4, p01 = yb * s + hx0[x] * 4, p11 = yb * s + hx1[x] * 4;
        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        for (int c = 0; c < 4; c++) o[c] = p[p00 + c] * w00 + p[p10 + c] * w10 + p[p01 + c] * w01 + p[p11 + c] * w11;
    }

    /// <summary>The image at half size (ceil), each pixel the mean of the 2 x 2 block it covers (clamped at the edge).</summary>
    internal static BgraImage Downsample(BgraImage src)
    {
        int w = Math.Max(1, (src.Width + 1) / 2), h = Math.Max(1, (src.Height + 1) / 2);
        var o = new BgraImage(w, h);
        var sp = src.Pixels;
        var op = o.Pixels;
        int sw = src.Width, sh = src.Height, ss = src.Stride;
        ImagingUtil.ForRows(0, h, (long)sw * sh, y =>
        {
            int ya = Math.Min(2 * y, sh - 1), yb = Math.Min(2 * y + 1, sh - 1);
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Min(2 * x, sw - 1) * 4, xb = Math.Min(2 * x + 1, sw - 1) * 4;
                int oi = (y * w + x) * 4;
                for (int c = 0; c < 4; c++)
                    op[oi + c] = (byte)((sp[ya * ss + xa + c] + sp[ya * ss + xb + c] + sp[yb * ss + xa + c] + sp[yb * ss + xb + c] + 2) >> 2);
            }
        });
        return o;
    }
}
