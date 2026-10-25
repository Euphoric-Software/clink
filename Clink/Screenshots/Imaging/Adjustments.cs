namespace Clink.Screenshots.Imaging;

/// <summary>
/// Brightness, contrast and sharpness of the base image. Order when combined: sharpen first (<see cref="Unsharp"/>), then the
/// brightness / contrast lookup table (<see cref="Apply"/>).
/// <para>The editor's <c>AdjustmentValues(Brightness, Contrast, Sharpness)</c> map as: Brightness and Contrast -100..100 (default
/// 0 = no change) straight into <see cref="BuildLut"/>; <b>Sharpness 0..200 (%), default 0 = no change, <see cref="Unsharp"/>
/// amount = Sharpness / 100</b> (sigma 1.5).</para>
/// <para>Uniform op form: <c>Apply(src, dst, clip, lut)</c> and <c>Unsharp(src, dst, clip, amount, sigma)</c> read <c>src</c> and
/// write only <c>clip</c> (clipped to the image), every pixel of it; <c>dst</c> may be <c>src</c>. The footprint is the whole
/// image. LOCAL: the LUT is per pixel, the unsharp mask reads <see cref="BoxBlur.ReadMargin"/> of sigma around a pixel, so a
/// dirty sub-rectangle can be recomposed on its own.</para>
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

    /// <summary>
    /// Maps B, G and R of every pixel of <paramref name="clip"/> (clipped to the image) through <paramref name="lut"/> into
    /// <paramref name="dst"/> (the size of <paramref name="src"/>, or <paramref name="src"/> itself); alpha is copied.
    /// </summary>
    public static void Apply(BgraImage src, BgraImage dst, RectI clip, byte[] lut)
    {
        ImagingUtil.RequireSameSize(src, dst);
        if (lut.Length != 256) throw new ArgumentException("a lookup table has 256 entries", nameof(lut));
        var r = clip.Intersect(src.Bounds);
        if (r.IsEmpty) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var sp = src.Pixels;
        var dp = dst.Pixels;
        int stride = src.Stride;
        ImagingUtil.ForRows(r.Y, r.Bottom, r.Area, y =>
        {
            int i = y * stride + r.X * 4;
            for (int x = 0; x < r.Width; x++, i += 4)
            {
                dp[i] = lut[sp[i]]; dp[i + 1] = lut[sp[i + 1]]; dp[i + 2] = lut[sp[i + 2]]; dp[i + 3] = sp[i + 3];
            }
        });
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("lut", $"{r.Width}x{r.Height}", ms);
    }

    /// <summary>
    /// Unsharp mask over <paramref name="clip"/> (clipped to the image): <c>out = src + amount * (src - blur(src))</c>, clamped to
    /// 0..255; <paramref name="amount"/> 1 = 100 % (Sharpness / 100). Alpha is copied. <paramref name="dst"/> is the size of
    /// <paramref name="src"/> or <paramref name="src"/> itself (the blur of the clip is taken into a temporary first). Amount 0
    /// copies.
    /// </summary>
    public static void Unsharp(BgraImage src, BgraImage dst, RectI clip, double amount, double sigma = 1.5)
    {
        ImagingUtil.RequireSameSize(src, dst);
        var r = clip.Intersect(src.Bounds);
        if (r.IsEmpty) return;
        if (amount == 0 || !(sigma > 0))
        {
            if (!ReferenceEquals(src.Pixels, dst.Pixels)) dst.Blit(src, r, r.X, r.Y);
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var blurred = new BgraImage(r.Width, r.Height);
        BoxBlur.BlurInto(src, r, sigma, blurred, 0, 0);
        var sp = src.Pixels;
        var bp = blurred.Pixels;
        var dp = dst.Pixels;
        int stride = src.Stride, bs = blurred.Stride;
        float a = (float)amount;
        ImagingUtil.ForRows(r.Y, r.Bottom, r.Area, y =>
        {
            int i = y * stride + r.X * 4, bi = (y - r.Y) * bs;
            for (int x = 0; x < r.Width; x++, i += 4, bi += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    float v = sp[i + c] + a * (sp[i + c] - bp[bi + c]);
                    dp[i + c] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
                }
                dp[i + 3] = sp[i + 3];
            }
        });
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("unsharp", $"{r.Width}x{r.Height} amount {amount:0.##}", ms);
    }
}
