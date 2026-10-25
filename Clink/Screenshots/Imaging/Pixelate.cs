using System.Diagnostics;

namespace Clink.Screenshots.Imaging;

/// <summary>Mosaic: every block of a grid becomes its mean colour.</summary>
internal static class Pixelate
{
    /// <summary>
    /// Replaces <paramref name="r"/> (clipped to the image) by block means. The grid is anchored at the top-left of <paramref name="r"/>
    /// as given (not as clipped, so a rectangle dragged past the image edge keeps its blocks); blocks cut by the edge of the
    /// rectangle or of the image average the pixels they have. In place; every channel including alpha is averaged.
    /// </summary>
    public static void Apply(BgraImage img, RectI r, int block)
    {
        if (block <= 1) return;
        var c = r.Intersect(img.Bounds);
        if (c.IsEmpty) return;
        long t0 = Stopwatch.GetTimestamp();
        int firstBx = (c.X - r.X) / block, lastBx = (c.Right - 1 - r.X) / block;
        int firstBy = (c.Y - r.Y) / block, lastBy = (c.Bottom - 1 - r.Y) / block;
        var p = img.Pixels;
        int stride = img.Stride;
        ImagingUtil.ForRows(firstBy, lastBy + 1, (long)c.Width * c.Height / Math.Max(1, lastBy - firstBy + 1) * (lastBy - firstBy + 1), by =>
        {
            int y0 = Math.Max(c.Y, r.Y + by * block), y1 = Math.Min(c.Bottom, r.Y + (by + 1) * block);
            for (int bx = firstBx; bx <= lastBx; bx++)
            {
                int x0 = Math.Max(c.X, r.X + bx * block), x1 = Math.Min(c.Right, r.X + (bx + 1) * block);
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = y * stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4) { sb += p[i]; sg += p[i + 1]; sr += p[i + 2]; sa += p[i + 3]; }
                }
                long n = (long)(x1 - x0) * (y1 - y0), h = n / 2;
                byte mb = (byte)((sb + h) / n), mg = (byte)((sg + h) / n), mr = (byte)((sr + h) / n), ma = (byte)((sa + h) / n);
                for (int y = y0; y < y1; y++)
                {
                    int i = y * stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4) { p[i] = mb; p[i + 1] = mg; p[i + 2] = mr; p[i + 3] = ma; }
                }
            }
        });
        ImagingUtil.LogTime("pixelate", $"{c.Width}x{c.Height} block {block}", t0);
    }
}
