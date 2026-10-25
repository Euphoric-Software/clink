namespace Clink.Screenshots.Imaging;

/// <summary>
/// Removes what is inside a rectangle by continuing the surroundings into it: a flat colour when the border is uniform, otherwise a
/// smooth blend of the edge profiles (a Coons patch when all four sides have pixels). Specification: docs/screenshots-design.md A3.
/// <para>Uniform op form: <c>Fill(src, dst, clip, r, ring, tolerance)</c> reads <c>src</c> (the ring around <c>r</c>), computes the
/// fill of the whole rectangle as if unclipped and writes only <c>clip ∩ r</c> (clipped to the image); <c>dst</c> may be <c>src</c>.
/// NON-LOCAL: every pixel of the rectangle depends on the whole ring, so the composer invalidates the whole rectangle (and recomposes
/// it when anything under its ring changes), never a dirty sub-rectangle of it.</para>
/// <para><b>Median convention.</b> A side's profile value at a position is the MEDIAN of the ring pixels across the ring's depth there
/// (the mean of the two middle values for an even depth), not their mean, so a 1-px line parallel to the side inside the ring does
/// not shift the profile. The profiles run past the rectangle over the ring's corner pixels (T and B over
/// [x0 - kl, x1 + kr), L and R over [y0 - kt, y1 + kb)), are filtered there (median 5, then box 3) and then cut back to the
/// rectangle, so the filter windows at the rectangle's own ends are full and only shrink where the image edge cuts the ring. The
/// corner values are the medians of the ring's corner blocks.</para>
/// <para>Deviation from the text of the spec, to make the linear-gradient property exact: the ring is a few pixels thick, so a
/// side's profile sits (rows + 1) / 2 pixels outside the rectangle, not on its edge. The blend parameters u and v are measured from
/// the profiles' real positions (a block is symmetric around the position the corner is taken at, so for a linear image the median
/// is exact, and a 1-px line through a corner block does not move it).</para>
/// <para>Alpha: the fill is always opaque. An opaque ring is used as it is (the fast path). A ring with transparency is weighted by
/// alpha: the uniformity test and the flat colour use alpha-weighted percentiles, the profiles and corners are taken premultiplied
/// with their alpha alongside, and the blend is un-premultiplied at the end, so transparent black (crop padding, a gap between
/// monitors) does not pull the fill towards black.</para>
/// </summary>
internal static class SmartErase
{
    /// <summary>In place: <c>Fill(img, img, r, r, ring, tolerance)</c> (the spec's signature).</summary>
    public static void Fill(BgraImage img, RectI r, int ring = 3, int tolerance = 12) => Fill(img, img, r, r, ring, tolerance);

    /// <summary>
    /// Fills <paramref name="r"/> (clipped to the image) from the ring of <paramref name="ring"/> pixels around it in
    /// <paramref name="src"/>, writing only where <paramref name="clip"/> overlaps it into <paramref name="dst"/> (the size of
    /// <paramref name="src"/>, or <paramref name="src"/> itself).
    /// </summary>
    public static void Fill(BgraImage src, BgraImage dst, RectI clip, RectI r, int ring = 3, int tolerance = 12)
    {
        ImagingUtil.RequireSameSize(src, dst);
        var rc = r.Intersect(src.Bounds);
        var wr = rc.Intersect(clip);
        if (wr.IsEmpty) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ring = Math.Max(1, ring);
        int W = src.Width, H = src.Height;
        int x0 = rc.X, y0 = rc.Y, x1 = rc.Right, y1 = rc.Bottom, w = rc.Width, h = rc.Height;
        var p = src.Pixels;
        var dp = dst.Pixels;
        int stride = src.Stride;

        // Ring rows / columns actually inside the image, per side. A side is present when it has at least one.
        int kt = Math.Min(ring, y0), kb = Math.Min(ring, H - y1), kl = Math.Min(ring, x0), kr = Math.Min(ring, W - x1);
        bool hasT = kt > 0, hasB = kb > 0, hasL = kl > 0, hasR = kr > 0;
        bool none = !hasT && !hasB && !hasL && !hasR;

        // The samples: the ring (r inflated, minus r, clipped), or the rectangle's own 1-px border when there is no ring.
        var samples = new List<int>();
        if (none)
        {
            for (int y = y0; y < y1; y++)
            {
                bool edgeRow = y == y0 || y == y1 - 1;
                for (int x = x0; x < x1; x++)
                {
                    if (!edgeRow && x != x0 && x != x1 - 1) { x = x1 - 2; continue; }
                    samples.Add(y * stride + x * 4);
                }
            }
        }
        else
        {
            for (int y = y0 - kt; y < y1 + kb; y++)
            {
                bool inRows = y >= y0 && y < y1;
                for (int x = x0 - kl; x < x1 + kr; x++)
                {
                    if (inRows && x >= x0 && x < x1) { x = x1 - 1; continue; }
                    samples.Add(y * stride + x * 4);
                }
            }
        }
        bool opaque = true;
        foreach (int i in samples) if (p[i + 3] != 255) { opaque = false; break; }

        // Histograms of the samples (alpha-weighted when not opaque).
        var hist = new long[3, 256];
        long count = 0;
        foreach (int i in samples)
        {
            int wgt = opaque ? 1 : p[i + 3];
            if (wgt == 0) continue;
            hist[0, p[i]] += wgt; hist[1, p[i + 1]] += wgt; hist[2, p[i + 2]] += wgt; count += wgt;
        }
        var med = new byte[3];
        int spread = 0;
        if (count > 0)
            for (int c = 0; c < 3; c++)
            {
                int p10 = Nth(hist, c, (long)(0.10 * (count - 1)));
                int p90 = Nth(hist, c, (long)Math.Ceiling(0.90 * (count - 1)));
                med[c] = (byte)Nth(hist, c, count / 2);
                spread = Math.Max(spread, p90 - p10);
            }
        bool flat = none || count == 0 || spread <= tolerance;
        if (flat)
        {
            for (int y = wr.Y; y < wr.Bottom; y++)
            {
                int i = y * stride + wr.X * 4;
                for (int x = wr.X; x < wr.Right; x++, i += 4) { dp[i] = med[0]; dp[i + 1] = med[1]; dp[i + 2] = med[2]; dp[i + 3] = 255; }
            }
            if (ImagingUtil.ShouldReport(t0, out double fms)) ImagingUtil.Report("smart-erase", $"{w}x{h} flat", fms);
            return;
        }

        // Edge profiles over the extended spans (nc channels interleaved: B G R, plus A when premultiplied), filtered, indexed by
        // column - (x0 - kl) for T / B and row - (y0 - kt) for L / R.
        int nc = opaque ? 3 : 4;
        int nx = w + kl + kr, ny = h + kt + kb;
        double[]? T = hasT ? Filter(Profile(p, stride, x0 - kl, y0 - kt, nx, kt, true, opaque), nx, nc) : null;
        double[]? B = hasB ? Filter(Profile(p, stride, x0 - kl, y1, nx, kb, true, opaque), nx, nc) : null;
        double[]? L = hasL ? Filter(Profile(p, stride, x0 - kl, y0 - kt, kl, ny, false, opaque), ny, nc) : null;
        double[]? R = hasR ? Filter(Profile(p, stride, x1, y0 - kt, kr, ny, false, opaque), ny, nc) : null;
        double yT = y0 - (kt + 1) / 2.0, yB = (y1 - 1) + (kb + 1) / 2.0;
        double xL = x0 - (kl + 1) / 2.0, xR = (x1 - 1) + (kr + 1) / 2.0;

        double[]? c00 = null, c10 = null, c01 = null, c11 = null;
        bool four = hasT && hasB && hasL && hasR;
        if (four)
        {
            c00 = BlockMedian(p, stride, x0 - kl, y0 - kt, kl, kt, opaque);
            c10 = BlockMedian(p, stride, x1, y0 - kt, kr, kt, opaque);
            c01 = BlockMedian(p, stride, x0 - kl, y1, kl, kb, opaque);
            c11 = BlockMedian(p, stride, x1, y1, kr, kb, opaque);
        }

        ImagingUtil.ForRows(wr.Y, wr.Bottom, wr.Area, y =>
        {
            int yi = (y - y0 + kt) * nc;
            double v = hasT && hasB ? (y - yT) / (yB - yT) : 0;
            Span<double> val = stackalloc double[4];
            int i = y * stride + wr.X * 4;
            for (int x = wr.X; x < wr.Right; x++, i += 4)
            {
                int xi = (x - x0 + kl) * nc;
                double u = hasL && hasR ? (x - xL) / (xR - xL) : 0;
                for (int c = 0; c < nc; c++)
                {
                    double vc;
                    if (four)
                    {
                        vc = (1 - v) * T![xi + c] + v * B![xi + c] + (1 - u) * L![yi + c] + u * R![yi + c]
                             - ((1 - u) * (1 - v) * c00![c] + u * (1 - v) * c10![c] + (1 - u) * v * c01![c] + u * v * c11![c]);
                    }
                    else if (hasT && hasB) vc = T![xi + c] + v * (B![xi + c] - T[xi + c]);
                    else if (hasL && hasR) vc = L![yi + c] + u * (R![yi + c] - L[yi + c]);
                    else if ((hasT || hasB) && (hasL || hasR))
                    {
                        double dv = hasT ? y - yT : yB - y, du = hasL ? x - xL : xR - x;
                        double wv = 1 / (dv * dv), wu = 1 / (du * du);
                        double vv = hasT ? T![xi + c] : B![xi + c], uv = hasL ? L![yi + c] : R![yi + c];
                        vc = (wv * vv + wu * uv) / (wv + wu);
                    }
                    else if (hasT) vc = T![xi + c];
                    else if (hasB) vc = B![xi + c];
                    else if (hasL) vc = L![yi + c];
                    else vc = R![yi + c];
                    val[c] = vc;
                }
                if (opaque)
                {
                    dp[i] = ImagingUtil.ClampByte(val[0]); dp[i + 1] = ImagingUtil.ClampByte(val[1]); dp[i + 2] = ImagingUtil.ClampByte(val[2]);
                }
                else
                {
                    double a = val[3];
                    for (int c = 0; c < 3; c++) dp[i + c] = a > 1e-6 ? ImagingUtil.ClampByte(val[c] * 255 / a) : (byte)0;
                }
                dp[i + 3] = 255;
            }
        });
        if (ImagingUtil.ShouldReport(t0, out double ms))
            ImagingUtil.Report("smart-erase", $"{w}x{h} sides {(hasT ? "T" : "")}{(hasB ? "B" : "")}{(hasL ? "L" : "")}{(hasR ? "R" : "")}{(opaque ? "" : " premultiplied")}", ms);
    }

    /// <summary>The value with sorted (weighted) index <paramref name="idx"/> of channel c in the histogram.</summary>
    private static int Nth(long[,] hist, int c, long idx)
    {
        long cum = 0;
        for (int v = 0; v < 256; v++)
        {
            cum += hist[c, v];
            if (cum > idx) return v;
        }
        return 255;
    }

    /// <summary>Channel c of the pixel at byte index i: the colour as it is (opaque), or premultiplied (c * a / 255) with c = 3 the alpha.</summary>
    private static double Sample(byte[] p, int i, int c, bool opaque) =>
        opaque ? p[i + c] : c == 3 ? p[i + 3] : p[i + c] * p[i + 3] / 255.0;

    /// <summary>The median of the first <paramref name="n"/> values (the mean of the two middle ones for an even count); sorts them.</summary>
    private static double Median(double[] v, int n)
    {
        Array.Sort(v, 0, n);
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
    }

    /// <summary>
    /// A ring strip of <paramref name="cols"/> x <paramref name="rows"/> pixels at (sx, sy) as a profile: with
    /// <paramref name="alongX"/> (top / bottom side) one value per column, the median over its rows; otherwise (left / right side)
    /// one per row, the median over its columns. Result: length * nc doubles (B G R, and A when not opaque, interleaved).
    /// </summary>
    private static double[] Profile(byte[] p, int stride, int sx, int sy, int cols, int rows, bool alongX, bool opaque)
    {
        int nc = opaque ? 3 : 4;
        int len = alongX ? cols : rows, depth = alongX ? rows : cols;
        var o = new double[len * nc];
        var v = new double[depth];
        for (int k = 0; k < len; k++)
            for (int c = 0; c < nc; c++)
            {
                for (int d = 0; d < depth; d++)
                {
                    int x = alongX ? sx + k : sx + d, y = alongX ? sy + d : sy + k;
                    v[d] = Sample(p, y * stride + x * 4, c, opaque);
                }
                o[k * nc + c] = Median(v, depth);
            }
        return o;
    }

    /// <summary>
    /// A median of width 5 then a box of width 3, per channel. The windows are cut symmetrically at the ends of the (extended)
    /// profile, so a linear profile stays exactly linear.
    /// </summary>
    private static double[] Filter(double[] profile, int n, int nc)
    {
        var med = new double[profile.Length];
        var win = new double[5];
        for (int c = 0; c < nc; c++)
            for (int i = 0; i < n; i++)
            {
                int rad = Math.Min(2, Math.Min(i, n - 1 - i));
                int m = 0;
                for (int j = i - rad; j <= i + rad; j++) win[m++] = profile[j * nc + c];
                Array.Sort(win, 0, m);
                med[i * nc + c] = win[m / 2];
            }
        var o = new double[profile.Length];
        for (int c = 0; c < nc; c++)
            for (int i = 0; i < n; i++)
            {
                int rad = Math.Min(1, Math.Min(i, n - 1 - i));
                double s = 0;
                for (int j = i - rad; j <= i + rad; j++) s += med[j * nc + c];
                o[i * nc + c] = s / (2 * rad + 1);
            }
        return o;
    }

    /// <summary>Per channel median of a block (the mean of the two middle values for an even count).</summary>
    private static double[] BlockMedian(byte[] p, int stride, int sx, int sy, int cols, int rows, bool opaque)
    {
        int nc = opaque ? 3 : 4;
        var o = new double[nc];
        var v = new double[cols * rows];
        for (int c = 0; c < nc; c++)
        {
            int m = 0;
            for (int y = sy; y < sy + rows; y++)
                for (int x = sx; x < sx + cols; x++) v[m++] = Sample(p, y * stride + x * 4, c, opaque);
            o[c] = Median(v, m);
        }
        return o;
    }
}
