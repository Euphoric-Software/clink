namespace Clink.Screenshots.Imaging;

/// <summary>
/// "Over" compositing of one <see cref="BgraImage"/> (straight alpha) or one premultiplied pixel buffer onto another, in integer
/// arithmetic with round-to-nearest, so a result is the same on every machine (the exporter's exact-pixel tests rely on that).
/// Pure; rows are independent, so the work runs in parallel above a size.
/// </summary>
internal static class Blend
{
    private const int ParallelPixels = 1 << 18;

    /// <summary>
    /// Draws premultiplied BGRA (<paramref name="pbgra"/>, <paramref name="w"/> x <paramref name="h"/>, no padding: what a
    /// Pbgra32 <c>RenderTargetBitmap</c> holds) over <paramref name="dst"/> with its top-left at (dstX, dstY); parts outside the
    /// destination are ignored. The destination stays straight alpha; over an opaque pixel the result is exactly
    /// <c>src + dst * (255 - srcAlpha) / 255</c> per channel.
    /// </summary>
    public static void OverPremultiplied(BgraImage dst, byte[] pbgra, int w, int h, int dstX, int dstY)
    {
        if (pbgra.Length < w * h * 4) throw new ArgumentException("buffer too small", nameof(pbgra));
        var area = new RectI(dstX, dstY, Math.Max(0, w), Math.Max(0, h)).Intersect(dst.Bounds);
        if (area.IsEmpty) return;
        void Row(int y)
        {
            int si = ((y - dstY) * w + (area.X - dstX)) * 4;
            int di = y * dst.Stride + area.X * 4;
            var d = dst.Pixels;
            for (int x = 0; x < area.Width; x++, si += 4, di += 4)
            {
                int sa = pbgra[si + 3];
                if (sa == 0) continue;
                int da = d[di + 3];
                if (sa == 255)
                {
                    d[di] = pbgra[si]; d[di + 1] = pbgra[si + 1]; d[di + 2] = pbgra[si + 2]; d[di + 3] = 255;
                    continue;
                }
                int inv = 255 - sa;
                if (da == 255)
                {
                    for (int c = 0; c < 3; c++) d[di + c] = (byte)Math.Min(255, pbgra[si + c] + (d[di + c] * inv + 127) / 255);
                    continue;
                }
                // general case: the destination is translucent too. Work premultiplied, then un-premultiply.
                int outA = sa + (da * inv + 127) / 255;
                for (int c = 0; c < 3; c++)
                {
                    int dp = (d[di + c] * da + 127) / 255;
                    int op = pbgra[si + c] + (dp * inv + 127) / 255;
                    d[di + c] = outA == 0 ? (byte)0 : (byte)Math.Min(255, (op * 255 + outA / 2) / outA);
                }
                d[di + 3] = (byte)Math.Min(255, outA);
            }
        }
        if ((long)area.Width * area.Height >= ParallelPixels) Parallel.For(area.Y, area.Bottom, Row);
        else for (int y = area.Y; y < area.Bottom; y++) Row(y);
    }

    /// <summary>
    /// Draws <paramref name="src"/> (straight alpha) over <paramref name="dst"/> (straight alpha) with its top-left at (dstX, dstY),
    /// scaled by <paramref name="opacity"/> (0..1). Parts outside the destination are ignored.
    /// </summary>
    public static void OverStraight(BgraImage dst, BgraImage src, int dstX, int dstY, double opacity = 1.0)
    {
        int op = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        if (op == 0) return;
        var area = new RectI(dstX, dstY, src.Width, src.Height).Intersect(dst.Bounds);
        if (area.IsEmpty) return;
        void Row(int y)
        {
            int si = ((y - dstY) * src.Width + (area.X - dstX)) * 4;
            int di = y * dst.Stride + area.X * 4;
            var d = dst.Pixels;
            var s = src.Pixels;
            for (int x = 0; x < area.Width; x++, si += 4, di += 4)
            {
                int sa = op == 255 ? s[si + 3] : (s[si + 3] * op + 127) / 255;
                if (sa == 0) continue;
                int da = d[di + 3];
                if (sa == 255)
                {
                    d[di] = s[si]; d[di + 1] = s[si + 1]; d[di + 2] = s[si + 2]; d[di + 3] = 255;
                    continue;
                }
                if (da == 255)
                {
                    for (int c = 0; c < 3; c++) d[di + c] = (byte)((s[si + c] * sa + d[di + c] * (255 - sa) + 127) / 255);
                    continue;
                }
                int outA = sa + (da * (255 - sa) + 127) / 255;
                for (int c = 0; c < 3; c++)
                {
                    int num = s[si + c] * sa * 255 + d[di + c] * da * (255 - sa);   // alpha-weighted, x 255 x 255
                    d[di + c] = outA == 0 ? (byte)0 : (byte)Math.Min(255, (int)(((long)num + (long)outA * 255 / 2) / ((long)outA * 255)));
                }
                d[di + 3] = (byte)Math.Min(255, outA);
            }
        }
        if ((long)area.Width * area.Height >= ParallelPixels) Parallel.For(area.Y, area.Bottom, Row);
        else for (int y = area.Y; y < area.Bottom; y++) Row(y);
    }
}
