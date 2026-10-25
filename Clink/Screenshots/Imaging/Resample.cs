namespace Clink.Screenshots.Imaging;

/// <summary>
/// Scaling and compositing of images (straight alpha).
/// <para>Uniform op form: <c>Bilinear(src, dst, clip, srcRect, dstRect)</c> and <c>Over(src, dst, clip, dest, opacity)</c> read
/// <c>src</c>, compute as if unclipped and write only <c>clip ∩ dstRect</c> / <c>clip ∩ dest</c> (clipped to <c>dst</c>), nothing
/// else. <c>dst</c> must not alias <c>src</c> (throws). Their footprint is the destination rectangle; within it a pixel depends on
/// the source pixels around its mapped position only, so a dirty sub-rectangle of the destination can be redrawn on its own.</para>
/// </summary>
internal static class Resample
{
    /// <summary>
    /// Scales <paramref name="srcRect"/> of <paramref name="src"/> into <paramref name="dstRect"/> of <paramref name="dst"/>
    /// (bilinear, pixel centres aligned), writing only <c>dstRect ∩ clip ∩ dst</c>. Equal sizes copy (when the source rectangle lies
    /// inside the image). When an axis shrinks by more than 2 x, the source is first reduced by a box filter (an integer factor per
    /// axis) so that no pixel is skipped; both paths map the destination onto the UNCLIPPED <paramref name="srcRect"/> the same way
    /// (a reduced pixel stands at the centre of its block). Sampling clamps to the part of the source rectangle inside the image.
    /// Interpolation is alpha-weighted, so a transparent pixel's colour does not bleed into its neighbours.
    /// </summary>
    public static void Bilinear(BgraImage src, BgraImage dst, RectI clip, RectI srcRect, RectI dstRect)
    {
        ImagingUtil.RequireNoAlias(src, dst);
        var sr = srcRect.Intersect(src.Bounds);
        if (sr.IsEmpty || dstRect.IsEmpty) return;
        var vis = dstRect.Intersect(dst.Bounds).Intersect(clip);
        if (vis.IsEmpty) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        if (srcRect.Width == dstRect.Width && srcRect.Height == dstRect.Height && src.Bounds.Contains(srcRect))
        {
            dst.Blit(src, new RectI(srcRect.X + vis.X - dstRect.X, srcRect.Y + vis.Y - dstRect.Y, vis.Width, vis.Height), vis.X, vis.Y);
            return;
        }

        double kx = (double)srcRect.Width / dstRect.Width, ky = (double)srcRect.Height / dstRect.Height;
        int fx = kx > 2 ? (int)kx : 1, fy = ky > 2 ? (int)ky : 1;
        // Per destination column / row: the two sample cells (blocks of fx / fy source pixels from sr's corner) and the weight.
        Axis(vis.X, vis.Width, dstRect.X, srcRect.X, kx, sr.X, sr.Width, fx, out var xi0, out var xi1, out var wx);
        Axis(vis.Y, vis.Height, dstRect.Y, srcRect.Y, ky, sr.Y, sr.Height, fy, out var yi0, out var yi1, out var wy);

        BgraImage sample;
        int offX, offY;   // sample column = cell + offX
        if (fx > 1 || fy > 1)
        {
            // Reduce only the blocks the destination needs; the grid stays anchored at sr's corner.
            int bx0 = xi0.Min(), bx1 = xi1.Max(), by0 = yi0.Min(), by1 = yi1.Max();
            var part = new RectI(sr.X + bx0 * fx, sr.Y + by0 * fy, (bx1 - bx0 + 1) * fx, (by1 - by0 + 1) * fy).Intersect(sr);
            sample = BoxReduce(src, part, fx, fy);
            offX = -bx0; offY = -by0;
        }
        else
        {
            sample = src;
            offX = sr.X; offY = sr.Y;
        }
        for (int i = 0; i < xi0.Length; i++) { xi0[i] = (xi0[i] + offX) * 4; xi1[i] = (xi1[i] + offX) * 4; }

        var sp = sample.Pixels;
        var dp = dst.Pixels;
        int ss = sample.Stride, ds = dst.Stride, n = vis.Width;
        ImagingUtil.ForRows(vis.Y, vis.Bottom, vis.Area, y =>
        {
            int r = y - vis.Y;
            float fyw = wy[r];
            int ra = (yi0[r] + offY) * ss, rb = (yi1[r] + offY) * ss;
            int di = y * ds + vis.X * 4;
            for (int i = 0; i < n; i++, di += 4)
            {
                int p00 = ra + xi0[i], p10 = ra + xi1[i], p01 = rb + xi0[i], p11 = rb + xi1[i];
                float fxw = wx[i];
                float w00 = (1 - fxw) * (1 - fyw), w10 = fxw * (1 - fyw), w01 = (1 - fxw) * fyw, w11 = fxw * fyw;
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
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("bilinear", $"{srcRect.Width}x{srcRect.Height} -> {dstRect.Width}x{dstRect.Height} ({vis.Width}x{vis.Height} drawn)", ms);
    }

    /// <summary>
    /// One axis of <see cref="Bilinear"/>: for the destination positions [v0, v0 + count), the sample position in source pixel space
    /// <c>s = srcStart + (v - dstStart + 0.5) * k - 0.5</c> (the same mapping with or without the box reduction), expressed in cells
    /// of <paramref name="f"/> source pixels starting at <paramref name="s0"/> (the clipped source range [s0, s0 + len)). A cell stands
    /// at the centre of its pixels (the last cell may be shorter); positions before the first / after the last cell clamp to it.
    /// </summary>
    private static void Axis(int v0, int count, int dstStart, int srcStart, double k, int s0, int len, int f, out int[] i0, out int[] i1, out float[] w)
    {
        i0 = new int[count]; i1 = new int[count]; w = new float[count];
        int cells = (len + f - 1) / f, last = cells - 1;
        double c0 = s0 + (Math.Min(f, len) - 1) / 2.0;
        double cLast = s0 + last * f + (len - last * f - 1) / 2.0;
        for (int j = 0; j < count; j++)
        {
            double s = srcStart + (v0 + j - dstStart + 0.5) * k - 0.5;
            if (last == 0 || s <= c0) { i0[j] = i1[j] = 0; w[j] = 0; continue; }
            if (s >= cLast) { i0[j] = i1[j] = last; w[j] = 0; continue; }
            int b = Math.Clamp((int)Math.Floor((s - c0) / f), 0, last - 1);
            double cb = c0 + (double)b * f, cn = b + 1 == last ? cLast : cb + f;
            i0[j] = b; i1[j] = b + 1; w[j] = (float)Math.Clamp((s - cb) / (cn - cb), 0, 1);
        }
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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("box-reduce", $"{r.Width}x{r.Height} by {kx}x{ky}", ms);
        return o;
    }

    /// <summary>
    /// Draws <paramref name="src"/> scaled into <paramref name="dest"/> (a rectangle in <paramref name="dst"/>'s pixels) over
    /// <paramref name="dst"/>, only where <paramref name="clip"/> and <paramref name="dst"/> overlap it: straight-alpha source-over,
    /// with the source's alpha multiplied by <paramref name="opacity"/> (0..1). Opacity 0 changes nothing; an opaque source at
    /// opacity 1 replaces.
    /// </summary>
    // Note for the S3/S4 integration: the editor's Blend.OverStraight implements the same straight-alpha source-over; the two may be
    // consolidated into one (this one, or a shared helper both call) when the composer lands.
    public static void Over(BgraImage src, BgraImage dst, RectI clip, RectI dest, double opacity)
    {
        ImagingUtil.RequireNoAlias(src, dst);
        opacity = Math.Clamp(opacity, 0, 1);
        var vis = dest.Intersect(dst.Bounds).Intersect(clip);
        if (vis.IsEmpty || src.Width == 0 || src.Height == 0 || opacity <= 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // Scale into a temporary of the visible part only (the destination can be mostly off the image).
        var tmp = new BgraImage(vis.Width, vis.Height);
        Bilinear(src, tmp, tmp.Bounds, src.Bounds, dest.Offset(-vis.X, -vis.Y));
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
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("over", $"{src.Width}x{src.Height} -> {dest} opacity {opacity:0.##}", ms);
    }
}
