using System.Windows.Media;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// The marker pen: multiplies the pixels by a colour, so black text stays black and white paper takes the colour.
/// <para>Uniform op form: <c>Multiply(src, dst, clip, mask, hl)</c> / <c>Rect(src, dst, clip, r, hl)</c> read <c>src</c> and write
/// only <c>clip ∩ footprint</c> (the mask bounds / the rectangle, clipped to the image), every pixel there (a pixel of zero coverage
/// is copied). LOCAL and per pixel: <c>dst</c> may be <c>src</c>, and a dirty sub-rectangle can be recomposed on its own.</para>
/// </summary>
internal static class Highlight
{
    /// <summary>In place: <c>Multiply(img, img, mask.Bounds, mask, hl)</c>.</summary>
    public static void Multiply(BgraImage img, AlphaMask mask, Color hl) => Multiply(img, img, mask.Bounds, mask, hl);

    /// <summary>
    /// Per colour channel <c>out = src * lerp(255, hl, m) / 255</c> with m = mask coverage 0..1. Alpha is copied. Writes
    /// <paramref name="mask"/>'s bounds ∩ <paramref name="clip"/> ∩ the image into <paramref name="dst"/> (the size of
    /// <paramref name="src"/>, or <paramref name="src"/> itself).
    /// </summary>
    public static void Multiply(BgraImage src, BgraImage dst, RectI clip, AlphaMask mask, Color hl)
    {
        ImagingUtil.RequireSameSize(src, dst);
        var r = mask.Bounds.Intersect(src.Bounds).Intersect(clip);
        if (r.IsEmpty) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var sp = src.Pixels;
        var dp = dst.Pixels;
        bool copy = !ReferenceEquals(sp, dp);
        int stride = src.Stride, mw = mask.Bounds.Width;
        ImagingUtil.ForRows(r.Y, r.Bottom, r.Area, y =>
        {
            int i = y * stride + r.X * 4, mi = (y - mask.Bounds.Y) * mw + (r.X - mask.Bounds.X);
            for (int x = 0; x < r.Width; x++, i += 4, mi++)
            {
                int m = mask.Coverage[mi];
                if (m == 0)
                {
                    if (copy) { dp[i] = sp[i]; dp[i + 1] = sp[i + 1]; dp[i + 2] = sp[i + 2]; dp[i + 3] = sp[i + 3]; }
                    continue;
                }
                MultiplyPixel(sp, dp, i, hl, m);
            }
        });
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("highlight", $"mask {r.Width}x{r.Height}", ms);
    }

    /// <summary>In place: <c>Rect(img, img, r, r, hl)</c>.</summary>
    public static void Rect(BgraImage img, RectI r, Color hl) => Rect(img, img, r, r, hl);

    /// <summary>The same as <see cref="Multiply(BgraImage, BgraImage, RectI, AlphaMask, Color)"/> with full coverage over <paramref name="r"/>.</summary>
    public static void Rect(BgraImage src, BgraImage dst, RectI clip, RectI r, Color hl)
    {
        ImagingUtil.RequireSameSize(src, dst);
        r = r.Intersect(src.Bounds).Intersect(clip);
        if (r.IsEmpty) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var sp = src.Pixels;
        var dp = dst.Pixels;
        int stride = src.Stride;
        ImagingUtil.ForRows(r.Y, r.Bottom, r.Area, y =>
        {
            int i = y * stride + r.X * 4;
            for (int x = 0; x < r.Width; x++, i += 4) MultiplyPixel(sp, dp, i, hl, 255);
        });
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("highlight-rect", $"{r.Width}x{r.Height}", ms);
    }

    private static void MultiplyPixel(byte[] sp, byte[] dp, int i, Color hl, int m)
    {
        // factor = lerp(255, hl, m / 255) scaled by 255: 255 * 255 - m * (255 - hl); out = src * factor / (255 * 255), rounded.
        dp[i] = Mul(sp[i], hl.B, m);
        dp[i + 1] = Mul(sp[i + 1], hl.G, m);
        dp[i + 2] = Mul(sp[i + 2], hl.R, m);
        dp[i + 3] = sp[i + 3];
    }

    private static byte Mul(byte src, int hl, int m)
    {
        int factor = 65025 - m * (255 - hl);
        return (byte)((src * factor + 32512) / 65025);
    }
}
