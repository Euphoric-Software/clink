using System.Diagnostics;
using System.Windows.Media;

namespace Clink.Screenshots.Imaging;

/// <summary>The marker pen: multiplies the pixels by a colour, so black text stays black and white paper takes the colour.</summary>
internal static class Highlight
{
    /// <summary>
    /// Per colour channel <c>out = src * lerp(255, hl, m) / 255</c> with m = mask coverage 0..1. Alpha is untouched. The mask is
    /// clipped to the image. In place.
    /// </summary>
    public static void Multiply(BgraImage img, AlphaMask mask, Color hl)
    {
        var r = mask.Bounds.Intersect(img.Bounds);
        if (r.IsEmpty) return;
        long t0 = Stopwatch.GetTimestamp();
        var p = img.Pixels;
        int stride = img.Stride, mw = mask.Bounds.Width;
        ImagingUtil.ForRows(r.Y, r.Bottom, r.Area, y =>
        {
            int i = y * stride + r.X * 4, mi = (y - mask.Bounds.Y) * mw + (r.X - mask.Bounds.X);
            for (int x = 0; x < r.Width; x++, i += 4, mi++)
            {
                int m = mask.Coverage[mi];
                if (m == 0) continue;
                MultiplyPixel(p, i, hl, m);
            }
        });
        ImagingUtil.LogTime("highlight", $"mask {r.Width}x{r.Height}", t0);
    }

    /// <summary>The same as <see cref="Multiply"/> with full coverage over <paramref name="r"/> (clipped to the image).</summary>
    public static void Rect(BgraImage img, RectI r, Color hl)
    {
        r = r.Intersect(img.Bounds);
        if (r.IsEmpty) return;
        long t0 = Stopwatch.GetTimestamp();
        var p = img.Pixels;
        int stride = img.Stride;
        ImagingUtil.ForRows(r.Y, r.Bottom, r.Area, y =>
        {
            int i = y * stride + r.X * 4;
            for (int x = 0; x < r.Width; x++, i += 4) MultiplyPixel(p, i, hl, 255);
        });
        ImagingUtil.LogTime("highlight-rect", $"{r.Width}x{r.Height}", t0);
    }

    private static void MultiplyPixel(byte[] p, int i, Color hl, int m)
    {
        // factor = lerp(255, hl, m / 255) scaled by 255: 255 * 255 - m * (255 - hl); out = src * factor / (255 * 255), rounded.
        p[i] = Mul(p[i], hl.B, m);
        p[i + 1] = Mul(p[i + 1], hl.G, m);
        p[i + 2] = Mul(p[i + 2], hl.R, m);
    }

    private static byte Mul(byte src, int hl, int m)
    {
        int factor = 65025 - m * (255 - hl);
        return (byte)((src * factor + 32512) / 65025);
    }
}
