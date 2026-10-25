namespace Clink.Screenshots.Imaging;

/// <summary>
/// Mosaic: every block of a grid becomes its mean colour.
/// <para>Uniform op form: <c>Apply(src, dst, clip, region, block)</c> reads <c>src</c>, writes only <c>clip ∩ region</c> (clipped to
/// the image) and nothing else; <c>dst</c> may be <c>src</c>. NON-LOCAL within its footprint: a written pixel depends on its whole
/// block, which can reach outside the clip (the mean is always taken over the full block from <c>src</c>), so the composer
/// invalidates the whole region, not a dirty sub-rectangle, when the pixels under it change.</para>
/// <para>Alpha: an opaque block is the plain mean of its pixels. A block with transparency is averaged premultiplied (colour weighted
/// by alpha, alpha the plain mean), so transparent black does not darken it; a fully transparent block stays transparent black.</para>
/// </summary>
internal static class Pixelate
{
    /// <summary>Pixelates <paramref name="region"/> of the image in place: <c>Apply(img, img, region, region, block)</c>.</summary>
    public static void Apply(BgraImage img, RectI region, int block) => Apply(img, img, region, region, block);

    /// <summary>
    /// Writes <paramref name="region"/> (clipped to the image and to <paramref name="clip"/>) of <paramref name="src"/> as block
    /// means into <paramref name="dst"/> (the size of <paramref name="src"/>, or <paramref name="src"/> itself). The grid is anchored
    /// at the top-left of <paramref name="region"/> as given (not as clipped, so a rectangle dragged past the image edge keeps its
    /// blocks); blocks cut by the edge of the region or of the image average the pixels they have. A block of 1 or less copies.
    /// In place with a clip that cuts blocks, a later call over the rest of such a block averages the pixels as they are then
    /// (partly pixelated already); the composer runs it src to dst, where that cannot happen.
    /// </summary>
    public static void Apply(BgraImage src, BgraImage dst, RectI clip, RectI region, int block)
    {
        ImagingUtil.RequireSameSize(src, dst);
        var c = region.Intersect(src.Bounds);      // the pixels the blocks average
        var w = c.Intersect(clip);                 // the pixels written
        if (w.IsEmpty) return;
        if (block <= 1)
        {
            if (!ReferenceEquals(src.Pixels, dst.Pixels)) dst.Blit(src, w, w.X, w.Y);
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int firstBx = (w.X - region.X) / block, lastBx = (w.Right - 1 - region.X) / block;
        int firstBy = (w.Y - region.Y) / block, lastBy = (w.Bottom - 1 - region.Y) / block;
        var sp = src.Pixels;
        var dp = dst.Pixels;
        int stride = src.Stride;
        // Blocks are disjoint and each reads only its own pixels before writing them: safe in place and in parallel.
        ImagingUtil.ForRows(firstBy, lastBy + 1, w.Area, by =>
        {
            int y0 = Math.Max(c.Y, region.Y + by * block), y1 = Math.Min(c.Bottom, region.Y + (by + 1) * block);
            int wy0 = Math.Max(y0, w.Y), wy1 = Math.Min(y1, w.Bottom);
            for (int bx = firstBx; bx <= lastBx; bx++)
            {
                int x0 = Math.Max(c.X, region.X + bx * block), x1 = Math.Min(c.Right, region.X + (bx + 1) * block);
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = y * stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        int a = sp[i + 3];
                        sb += sp[i] * a; sg += sp[i + 1] * a; sr += sp[i + 2] * a; sa += a;
                    }
                }
                long n = (long)(x1 - x0) * (y1 - y0), h = n / 2;
                byte mb, mg, mr, ma;
                if (sa == 255 * n)
                {
                    // Opaque: the sums are 255 x the plain sums; the plain mean, rounded.
                    mb = (byte)((sb / 255 + h) / n); mg = (byte)((sg / 255 + h) / n); mr = (byte)((sr / 255 + h) / n); ma = 255;
                }
                else if (sa == 0) mb = mg = mr = ma = 0;
                else
                {
                    long ha = sa / 2;
                    mb = (byte)((sb + ha) / sa); mg = (byte)((sg + ha) / sa); mr = (byte)((sr + ha) / sa); ma = (byte)((sa + h) / n);
                }
                int wx0 = Math.Max(x0, w.X), wx1 = Math.Min(x1, w.Right);
                for (int y = wy0; y < wy1; y++)
                {
                    int i = y * stride + wx0 * 4;
                    for (int x = wx0; x < wx1; x++, i += 4) { dp[i] = mb; dp[i + 1] = mg; dp[i + 2] = mr; dp[i + 3] = ma; }
                }
            }
        });
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("pixelate", $"{w.Width}x{w.Height} block {block}", ms);
    }
}
