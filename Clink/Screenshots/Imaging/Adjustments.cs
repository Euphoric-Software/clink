using System.Diagnostics;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// Brightness, contrast and sharpness of the base image. Order when combined: sharpen first (<see cref="Unsharp"/>), then the
/// brightness / contrast lookup table (<see cref="Apply"/>).
/// </summary>
internal static class Adjustments
{
    /// <summary>
    /// The 256-entry table for brightness and contrast (both -100..100; 0 / 0 is the identity). Contrast factor
    /// <c>f = 259 (c + 255) / (255 (259 - c))</c> with c = contrast * 2.55, applied around mid-grey; brightness adds <c>b * 2.55</c>.
    /// </summary>
    public static byte[] BuildLut(int brightness, int contrast)
    {
        double c = Math.Clamp(contrast, -100, 100) * 2.55;
        double f = 259 * (c + 255) / (255 * (259 - c));
        double b = Math.Clamp(brightness, -100, 100) * 2.55;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = ImagingUtil.ClampByte(f * (i - 128) + 128 + b);
        return lut;
    }

    /// <summary>Maps B, G and R of every pixel through <paramref name="lut"/> (alpha unchanged). <paramref name="dst"/> may be <paramref name="src"/>.</summary>
    public static void Apply(BgraImage src, BgraImage dst, byte[] lut)
    {
        if (src.Width != dst.Width || src.Height != dst.Height) throw new ArgumentException("dst must have the size of src", nameof(dst));
        if (lut.Length != 256) throw new ArgumentException("a lookup table has 256 entries", nameof(lut));
        long t0 = Stopwatch.GetTimestamp();
        var sp = src.Pixels;
        var dp = dst.Pixels;
        int w = src.Width, stride = src.Stride;
        ImagingUtil.ForRows(0, src.Height, (long)w * src.Height, y =>
        {
            int i = y * stride;
            for (int x = 0; x < w; x++, i += 4)
            {
                dp[i] = lut[sp[i]]; dp[i + 1] = lut[sp[i + 1]]; dp[i + 2] = lut[sp[i + 2]]; dp[i + 3] = sp[i + 3];
            }
        });
        ImagingUtil.LogTime("lut", $"{w}x{src.Height}", t0);
    }

    /// <summary>
    /// Unsharp mask: <c>out = src + amount * (src - blur(src))</c>, clamped; <paramref name="amount"/> 1 = 100 %. Alpha unchanged.
    /// <paramref name="dst"/> may be <paramref name="src"/>.
    /// </summary>
    public static void Unsharp(BgraImage src, BgraImage dst, double amount, double sigma = 1.5)
    {
        if (src.Width != dst.Width || src.Height != dst.Height) throw new ArgumentException("dst must have the size of src", nameof(dst));
        if (src.Width == 0 || src.Height == 0) return;
        if (amount == 0 || !(sigma > 0))
        {
            if (!ReferenceEquals(src, dst)) dst.Blit(src, src.Bounds, 0, 0);
            return;
        }
        long t0 = Stopwatch.GetTimestamp();
        var blurred = new BgraImage(src.Width, src.Height);
        BoxBlur.Blur(src, src.Bounds, sigma, blurred);
        var sp = src.Pixels;
        var bp = blurred.Pixels;
        var dp = dst.Pixels;
        int w = src.Width, stride = src.Stride;
        ImagingUtil.ForRows(0, src.Height, (long)w * src.Height, y =>
        {
            int i = y * stride;
            for (int x = 0; x < w; x++, i += 4)
            {
                for (int c = 0; c < 3; c++) dp[i + c] = ImagingUtil.ClampByte(sp[i + c] + amount * (sp[i + c] - bp[i + c]));
                dp[i + 3] = sp[i + 3];
            }
        });
        ImagingUtil.LogTime("unsharp", $"{w}x{src.Height} amount {amount:0.##}", t0);
    }
}
