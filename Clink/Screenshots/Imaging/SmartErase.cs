using System.Diagnostics;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// Removes what is inside a rectangle by continuing the surroundings into it: a flat colour when the border is uniform, otherwise a
/// smooth blend of the edge profiles (a Coons patch when all four sides have pixels). Specification: docs/screenshots-design.md A3.
/// <para>Deviation from the text of the spec, to make the linear-gradient property exact: the ring is a few pixels thick, so a
/// side's profile sits (rows + 1) / 2 pixels outside the rectangle, not on its edge. The blend parameters u and v are measured from
/// the profiles' real positions, and the corner values are the medians of the ring's corner blocks (a block is symmetric around
/// the position the corner is taken at, so for a linear image the median is exact, and a 1-px line through a corner block does not
/// move it).</para>
/// </summary>
internal static class SmartErase
{
    public static void Fill(BgraImage img, RectI r, int ring = 3, int tolerance = 12)
    {
        var rc = r.Intersect(img.Bounds);
        if (rc.IsEmpty) return;
        long t0 = Stopwatch.GetTimestamp();
        ring = Math.Max(1, ring);
        int W = img.Width, H = img.Height;
        int x0 = rc.X, y0 = rc.Y, x1 = rc.Right, y1 = rc.Bottom, w = rc.Width, h = rc.Height;
        var p = img.Pixels;
        int stride = img.Stride;

        // Ring rows / columns actually inside the image, per side. A side is present when it has at least one.
        int kt = Math.Min(ring, y0), kb = Math.Min(ring, H - y1), kl = Math.Min(ring, x0), kr = Math.Min(ring, W - x1);
        bool hasT = kt > 0, hasB = kb > 0, hasL = kl > 0, hasR = kr > 0;

        // Histograms of the ring (r inflated, minus r, clipped), or of the rectangle's own border when there is no ring.
        var hist = new int[3, 256];
        int count = 0;
        if (!hasT && !hasB && !hasL && !hasR)
        {
            for (int y = y0; y < y1; y++)
            {
                bool edgeRow = y == y0 || y == y1 - 1;
                for (int x = x0; x < x1; x++)
                {
                    if (!edgeRow && x != x0 && x != x1 - 1) { x = x1 - 2; continue; }
                    int i = y * stride + x * 4;
                    hist[0, p[i]]++; hist[1, p[i + 1]]++; hist[2, p[i + 2]]++; count++;
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
                    int i = y * stride + x * 4;
                    hist[0, p[i]]++; hist[1, p[i + 1]]++; hist[2, p[i + 2]]++; count++;
                }
            }
        }

        var med = new byte[3];
        int spread = 0;
        for (int c = 0; c < 3; c++)
        {
            int p10 = Nth(hist, c, (int)(0.10 * (count - 1)));
            int p90 = Nth(hist, c, (int)Math.Ceiling(0.90 * (count - 1)));
            med[c] = (byte)Nth(hist, c, count / 2);
            spread = Math.Max(spread, p90 - p10);
        }
        bool flat = (!hasT && !hasB && !hasL && !hasR) || spread <= tolerance;
        if (flat)
        {
            for (int y = y0; y < y1; y++)
            {
                int i = y * stride + x0 * 4;
                for (int x = x0; x < x1; x++, i += 4) { p[i] = med[0]; p[i + 1] = med[1]; p[i + 2] = med[2]; p[i + 3] = 255; }
            }
            ImagingUtil.LogTime("smart-erase", $"{w}x{h} flat", t0);
            return;
        }

        // Edge profiles (3 channels interleaved), filtered. Positions are pixel indices.
        double[]? T = hasT ? Filter(Profile(p, stride, x0, y0 - kt, w, kt, true), w) : null;
        double[]? B = hasB ? Filter(Profile(p, stride, x0, y1, w, kb, true), w) : null;
        double[]? L = hasL ? Filter(Profile(p, stride, x0 - kl, y0, kl, h, false), h) : null;
        double[]? R = hasR ? Filter(Profile(p, stride, x1, y0, kr, h, false), h) : null;
        double yT = y0 - (kt + 1) / 2.0, yB = (y1 - 1) + (kb + 1) / 2.0;
        double xL = x0 - (kl + 1) / 2.0, xR = (x1 - 1) + (kr + 1) / 2.0;

        double[]? c00 = null, c10 = null, c01 = null, c11 = null;
        bool four = hasT && hasB && hasL && hasR;
        if (four)
        {
            c00 = BlockMedian(p, stride, x0 - kl, y0 - kt, kl, kt);
            c10 = BlockMedian(p, stride, x1, y0 - kt, kr, kt);
            c01 = BlockMedian(p, stride, x0 - kl, y1, kl, kb);
            c11 = BlockMedian(p, stride, x1, y1, kr, kb);
        }

        ImagingUtil.ForRows(y0, y1, (long)w * h, y =>
        {
            int yi = y - y0;
            double v = hasT && hasB ? (y - yT) / (yB - yT) : 0;
            int i = y * stride + x0 * 4;
            for (int xi = 0; xi < w; xi++, i += 4)
            {
                int x = x0 + xi;
                double u = hasL && hasR ? (x - xL) / (xR - xL) : 0;
                for (int c = 0; c < 3; c++)
                {
                    double val;
                    if (four)
                    {
                        val = (1 - v) * T![xi * 3 + c] + v * B![xi * 3 + c] + (1 - u) * L![yi * 3 + c] + u * R![yi * 3 + c]
                              - ((1 - u) * (1 - v) * c00![c] + u * (1 - v) * c10![c] + (1 - u) * v * c01![c] + u * v * c11![c]);
                    }
                    else if (hasT && hasB) val = T![xi * 3 + c] + v * (B![xi * 3 + c] - T[xi * 3 + c]);
                    else if (hasL && hasR) val = L![yi * 3 + c] + u * (R![yi * 3 + c] - L[yi * 3 + c]);
                    else if ((hasT || hasB) && (hasL || hasR))
                    {
                        double dv = hasT ? y - yT : yB - y, du = hasL ? x - xL : xR - x;
                        double wv = 1 / (dv * dv), wu = 1 / (du * du);
                        double vv = hasT ? T![xi * 3 + c] : B![xi * 3 + c], uv = hasL ? L![yi * 3 + c] : R![yi * 3 + c];
                        val = (wv * vv + wu * uv) / (wv + wu);
                    }
                    else if (hasT) val = T![xi * 3 + c];
                    else if (hasB) val = B![xi * 3 + c];
                    else if (hasL) val = L![yi * 3 + c];
                    else val = R![yi * 3 + c];
                    p[i + c] = ImagingUtil.ClampByte(val);
                }
                p[i + 3] = 255;
            }
        });
        ImagingUtil.LogTime("smart-erase", $"{w}x{h} sides {(hasT ? "T" : "")}{(hasB ? "B" : "")}{(hasL ? "L" : "")}{(hasR ? "R" : "")}", t0);
    }

    /// <summary>The value with sorted index <paramref name="idx"/> of channel c in the histogram.</summary>
    private static int Nth(int[,] hist, int c, int idx)
    {
        int cum = 0;
        for (int v = 0; v < 256; v++)
        {
            cum += hist[c, v];
            if (cum > idx) return v;
        }
        return 255;
    }

    /// <summary>
    /// The mean over a ring strip of <paramref name="cols"/> x <paramref name="rows"/> pixels at (sx, sy): with
    /// <paramref name="alongX"/> (top / bottom side) one value per column, the mean over its rows; otherwise (left / right side) one
    /// per row, the mean over its columns. Result: length * 3 doubles (B G R interleaved).
    /// </summary>
    private static double[] Profile(byte[] p, int stride, int sx, int sy, int cols, int rows, bool alongX)
    {
        int len = alongX ? cols : rows, depth = alongX ? rows : cols;
        var o = new double[len * 3];
        for (int k = 0; k < len; k++)
            for (int c = 0; c < 3; c++)
            {
                long sum = 0;
                for (int d = 0; d < depth; d++)
                {
                    int x = alongX ? sx + k : sx + d, y = alongX ? sy + d : sy + k;
                    sum += p[y * stride + x * 4 + c];
                }
                o[k * 3 + c] = (double)sum / depth;
            }
        return o;
    }

    /// <summary>
    /// A median of width 5 then a box of width 3, per channel. The windows are cut symmetrically at the ends (so a linear profile
    /// stays exactly linear; the end values are only smoothed as far as their neighbours allow).
    /// </summary>
    private static double[] Filter(double[] profile, int n)
    {
        var med = new double[profile.Length];
        var win = new double[5];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < n; i++)
            {
                int rad = Math.Min(2, Math.Min(i, n - 1 - i));
                int m = 0;
                for (int j = i - rad; j <= i + rad; j++) win[m++] = profile[j * 3 + c];
                Array.Sort(win, 0, m);
                med[i * 3 + c] = win[m / 2];
            }
        var o = new double[profile.Length];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < n; i++)
            {
                int rad = Math.Min(1, Math.Min(i, n - 1 - i));
                double s = 0;
                for (int j = i - rad; j <= i + rad; j++) s += med[j * 3 + c];
                o[i * 3 + c] = s / (2 * rad + 1);
            }
        return o;
    }

    /// <summary>Per channel median of a block (the mean of the two middle values for an even count).</summary>
    private static double[] BlockMedian(byte[] p, int stride, int sx, int sy, int cols, int rows)
    {
        var o = new double[3];
        var v = new int[cols * rows];
        for (int c = 0; c < 3; c++)
        {
            int m = 0;
            for (int y = sy; y < sy + rows; y++)
                for (int x = sx; x < sx + cols; x++) v[m++] = p[y * stride + x * 4 + c];
            Array.Sort(v);
            o[c] = m % 2 == 1 ? v[m / 2] : (v[m / 2 - 1] + v[m / 2]) / 2.0;
        }
        return o;
    }
}
