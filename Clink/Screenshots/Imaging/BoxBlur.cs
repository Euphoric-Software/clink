namespace Clink.Screenshots.Imaging;

/// <summary>
/// The float planes of <see cref="BoxBlur"/> (one image-sized plane is 4 bytes per pixel: 33 MB at 4K, and a blur uses 2 or 3),
/// kept for reuse because a drag re-blurs many times a second and the planes live on the large object heap. Static and
/// thread-safe. Keeps at most <see cref="MaxKept"/> planes and <see cref="MaxKeptBytes"/> bytes in all; small planes (under
/// <see cref="MinPooled"/> floats) are not pooled.
/// <para>The editor calls <see cref="Clear"/> when it closes (before its <c>GC.Collect</c> with LOH compaction) so the memory
/// the planes hold (up to the cap) goes back to the system; the next blur simply allocates again. <see cref="Clear"/> also starts
/// a new generation: a plane rented before it and returned after it (a blur still running on a worker) is dropped, not kept.</para>
/// </summary>
internal static class PlanePool
{
    public const int MaxKept = 6;
    /// <summary>The most bytes the kept planes may hold together (a few 4K planes; larger returns drop planes until under it).</summary>
    public const long MaxKeptBytes = 160L << 20;
    public const int MinPooled = 1 << 16;
    private const int Granule = 1 << 18;   // lengths are rounded up to 1 MB so that slightly different regions reuse a plane

    private static readonly List<float[]> free = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<float[], object> rented = new();   // plane -> boxed generation
    private static int generation;   // guarded by free

    /// <summary>A plane of at least <paramref name="length"/> floats (contents undefined).</summary>
    public static float[] Rent(int length)
    {
        if (length < MinPooled) return new float[length];
        lock (free)
        {
            int best = -1;
            for (int i = 0; i < free.Count; i++)
                if (free[i].Length >= length && (best < 0 || free[i].Length < free[best].Length)) best = i;
            if (best >= 0)
            {
                var a = free[best];
                free.RemoveAt(best);
                rented.AddOrUpdate(a, generation);
                return a;
            }
            long rounded = ((long)length + Granule - 1) / Granule * Granule;
            var plane = new float[(int)Math.Min(rounded, Array.MaxLength)];
            rented.AddOrUpdate(plane, generation);
            return plane;
        }
    }

    /// <summary>
    /// Gives a plane back. Beyond <see cref="MaxKept"/> the smallest kept one is dropped; while the kept planes hold more than
    /// <see cref="MaxKeptBytes"/> the largest is dropped. A plane rented before the last <see cref="Clear"/> is dropped.
    /// </summary>
    public static void Return(float[]? plane)
    {
        if (plane == null || plane.Length < MinPooled) return;
        lock (free)
        {
            if (rented.TryGetValue(plane, out var gen))
            {
                rented.Remove(plane);
                if ((int)gen != generation) return;   // rented before a Clear
            }
            if (free.Contains(plane)) return;
            free.Add(plane);
            if (free.Count > MaxKept)
            {
                int smallest = 0;
                for (int i = 1; i < free.Count; i++) if (free[i].Length < free[smallest].Length) smallest = i;
                free.RemoveAt(smallest);
            }
            long bytes = 0;
            foreach (var p in free) bytes += (long)p.Length * sizeof(float);
            while (bytes > MaxKeptBytes && free.Count > 0)
            {
                int largest = 0;
                for (int i = 1; i < free.Count; i++) if (free[i].Length > free[largest].Length) largest = i;
                bytes -= (long)free[largest].Length * sizeof(float);
                free.RemoveAt(largest);
            }
        }
    }

    /// <summary>Drops every kept plane (the editor calls this on close).</summary>
    public static void Clear()
    {
        lock (free)
        {
            free.Clear();
            generation++;
        }
    }

    /// <summary>The bytes the kept planes hold.</summary>
    public static long KeptBytes
    {
        get { lock (free) return free.Sum(p => (long)p.Length * sizeof(float)); }
    }
}

/// <summary>
/// Gaussian blur approximated by three box blurs (Kovesi's radii for the sigma), each a separable running sum: O(pixels) whatever
/// the radius. Edges clamp at the image edge.
/// <para>Uniform op form (like every raster op of this folder): <c>Blur(src, dst, clip, region, sigma)</c> reads
/// <paramref name="src"/>, computes the blur of the region as if unclipped, writes only <c>clip ∩ region</c> (clipped to the image)
/// into <c>dst</c> and touches nothing else. LOCAL: a pixel depends on <c>src</c> within the sum of the three radii
/// (<see cref="ReadMargin"/>), so a dirty sub-rectangle can be recomposed on its own. <c>dst</c> may be <c>src</c> only for a
/// single-shot call that covers the whole region (<c>clip</c> contains <c>region ∩ image</c>; every plane is read before it is
/// written); an in-place call with a smaller clip throws (see <see cref="ImagingUtil"/>).</para>
/// <para>Alpha: an opaque read area blurs B, G and R and writes alpha 255 (the fast path). Otherwise the blur runs on premultiplied
/// colour (c * a) and alpha, and the result is un-premultiplied, so transparent black does not darken its neighbours.</para>
/// </summary>
internal static class BoxBlur
{
    /// <summary>The radii of the three box passes whose combined variance best matches <paramref name="sigma"/> (all 0 for sigma &lt;= 0).</summary>
    public static int[] RadiiForSigma(double sigma)
    {
        var radii = new int[3];
        if (!(sigma > 0)) return radii;
        if (sigma > 1000) sigma = 1000;
        const int n = 3;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        if (wl < 1) wl = 1;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        for (int i = 0; i < n; i++) radii[i] = ((i < m ? wl : wu) - 1) / 2;
        return radii;
    }

    /// <summary>How far around a pixel the blur reads (the sum of the three radii).</summary>
    public static int ReadMargin(double sigma) => RadiiForSigma(sigma).Sum();

    /// <summary>Blurs <paramref name="region"/> of the image in place: <c>Blur(img, img, region, region, sigma)</c>.</summary>
    public static void Blur(BgraImage img, RectI region, double sigma) => Blur(img, img, region, region, sigma);

    /// <summary>
    /// Writes into <paramref name="dst"/> (the size of <paramref name="src"/>, or <paramref name="src"/> itself) the blur of
    /// <paramref name="region"/> of <paramref name="src"/>, only where <paramref name="clip"/> and the region overlap (clipped to the
    /// image). Reads <paramref name="src"/> up to <see cref="ReadMargin"/> around that. Sigma 0 (or a sigma too small for a box)
    /// copies the pixels.
    /// </summary>
    public static void Blur(BgraImage src, BgraImage dst, RectI clip, RectI region, double sigma)
    {
        ImagingUtil.RequireSameSize(src, dst);
        var whole = region.Intersect(src.Bounds);
        var w = whole.Intersect(clip);
        if (w.IsEmpty) return;
        ImagingUtil.RequireSinglePass(src, dst, clip, whole, nameof(Blur));
        BlurInto(src, w, sigma, dst, w.X, w.Y, IsOpaque(src, whole.Inflate(ReadMargin(sigma)).Intersect(src.Bounds)));
    }

    /// <summary>
    /// The blur of <paramref name="region"/> (inside <paramref name="src"/>) written to <paramref name="dst"/> at
    /// (<paramref name="dstX"/>, <paramref name="dstY"/>), which must fit. <paramref name="dst"/> may be <paramref name="src"/>
    /// at the same position.
    /// </summary>
    internal static void BlurInto(BgraImage src, RectI region, double sigma, BgraImage dst, int dstX, int dstY, bool? opaqueArea = null)
    {
        if (!src.Bounds.Contains(region) || !dst.Bounds.Contains(new RectI(dstX, dstY, region.Width, region.Height)))
            throw new ArgumentOutOfRangeException(nameof(region), "the region and its destination must lie inside the images");
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var radii = RadiiForSigma(sigma);
        int margin = radii[0] + radii[1] + radii[2];
        if (margin == 0)
        {
            if (!ReferenceEquals(src.Pixels, dst.Pixels) || dstX != region.X || dstY != region.Y) dst.Blit(src, region, dstX, dstY);
            return;
        }
        var e = region.Inflate(margin).Intersect(src.Bounds);
        int ew = e.Width, eh = e.Height;
        int len = ew * eh;
        long pixels = len;
        var sp = src.Pixels;
        int stride = src.Stride;

        // The caller decides from the unclipped region so that a clipped run takes the same path as the whole one.
        bool opaque = opaqueArea ?? IsOpaque(src, e);

        var p0 = PlanePool.Rent(len);
        var p1 = PlanePool.Rent(len);
        float[]? p2 = opaque ? null : PlanePool.Rent(len);
        try
        {
            var dp = dst.Pixels;
            int dstride = dst.Stride;
            int ox = dstX - region.X, oy = dstY - region.Y;   // dst position = src position + (ox, oy)
            if (opaque)
            {
                for (int c = 0; c < 3; c++)
                {
                    LoadPlane(sp, stride, e, c, false, p0, pixels);
                    var result = BlurPlane(p0, p1, ew, eh, radii, pixels);
                    ImagingUtil.ForRows(region.Y, region.Bottom, region.Area, y =>
                    {
                        int di = (y + oy) * dstride + (region.X + ox) * 4 + c, ri = (y - e.Y) * ew + (region.X - e.X);
                        for (int x = 0; x < region.Width; x++, di += 4, ri++)
                        {
                            int v = (int)(result[ri] + 0.5f);
                            dp[di] = v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
                        }
                    });
                }
                for (int y = region.Y; y < region.Bottom; y++)
                {
                    int di = (y + oy) * dstride + (region.X + ox) * 4 + 3;
                    for (int x = 0; x < region.Width; x++, di += 4) dp[di] = 255;
                }
            }
            else
            {
                // Alpha first (kept in its own plane), then each colour premultiplied, divided by the blurred alpha; alpha is
                // written last, so an in-place blur still reads the original alpha for every colour channel.
                LoadPlane(sp, stride, e, 3, false, p0, pixels);
                var alpha = BlurPlane(p0, p1, ew, eh, radii, pixels);
                var w0 = ReferenceEquals(alpha, p0) ? p1 : p0;
                var w1 = p2!;
                for (int c = 0; c < 3; c++)
                {
                    LoadPlane(sp, stride, e, c, true, w0, pixels);
                    var result = BlurPlane(w0, w1, ew, eh, radii, pixels);
                    ImagingUtil.ForRows(region.Y, region.Bottom, region.Area, y =>
                    {
                        int di = (y + oy) * dstride + (region.X + ox) * 4 + c, ri = (y - e.Y) * ew + (region.X - e.X);
                        for (int x = 0; x < region.Width; x++, di += 4, ri++)
                        {
                            float a = alpha[ri];
                            int v = a < 0.5f ? 0 : (int)(result[ri] / a + 0.5f);
                            dp[di] = v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
                        }
                    });
                }
                ImagingUtil.ForRows(region.Y, region.Bottom, region.Area, y =>
                {
                    int di = (y + oy) * dstride + (region.X + ox) * 4 + 3, ri = (y - e.Y) * ew + (region.X - e.X);
                    for (int x = 0; x < region.Width; x++, di += 4, ri++)
                    {
                        int v = (int)(alpha[ri] + 0.5f);
                        dp[di] = v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
                    }
                });
            }
        }
        finally
        {
            PlanePool.Return(p0);
            PlanePool.Return(p1);
            PlanePool.Return(p2);
        }
        if (ImagingUtil.ShouldReport(t0, out double ms))
            ImagingUtil.Report("blur", $"{region.Width}x{region.Height} sigma {sigma:0.##} radii {radii[0]},{radii[1]},{radii[2]}{(opaque ? "" : " premultiplied")}", ms);
    }

    /// <summary>True when every pixel of <paramref name="area"/> (inside the image) has alpha 255.</summary>
    internal static bool IsOpaque(BgraImage src, RectI area)
    {
        var sp = src.Pixels;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            int i = y * src.Stride + area.X * 4 + 3;
            for (int x = 0; x < area.Width; x++, i += 4) if (sp[i] != 255) return false;
        }
        return true;
    }

    /// <summary>Channel <paramref name="c"/> of the area <paramref name="e"/> into a plane; <paramref name="premultiply"/> stores c * a.</summary>
    private static void LoadPlane(byte[] sp, int stride, RectI e, int c, bool premultiply, float[] plane, long pixels)
    {
        int ew = e.Width;
        ImagingUtil.ForRows(0, e.Height, pixels, row =>
        {
            int si = (e.Y + row) * stride + e.X * 4, di = row * ew;
            if (premultiply) for (int x = 0; x < ew; x++, si += 4) plane[di + x] = sp[si + c] * sp[si + 3];
            else for (int x = 0; x < ew; x++, si += 4) plane[di + x] = sp[si + c];
        });
    }

    /// <summary>The three horizontal then the three vertical passes; returns the plane that holds the result (a or b).</summary>
    private static float[] BlurPlane(float[] a, float[] b, int w, int h, int[] radii, long pixels)
    {
        var cur = a;
        var other = b;
        foreach (int r in radii) if (r > 0) { HPass(cur, other, w, h, r, pixels); (cur, other) = (other, cur); }
        foreach (int r in radii) if (r > 0) { VPass(cur, other, w, h, r, pixels); (cur, other) = (other, cur); }
        return cur;
    }

    private static void HPass(float[] a, float[] b, int w, int h, int r, long pixels)
    {
        double d = 2 * r + 1;
        ImagingUtil.ForRows(0, h, pixels, y =>
        {
            int off = y * w;
            double sum = (r + 1) * (double)a[off];
            for (int j = 1; j <= r; j++) sum += a[off + Math.Min(j, w - 1)];
            for (int i = 0; i < w; i++)
            {
                b[off + i] = (float)(sum / d);
                sum += (double)a[off + Math.Min(i + 1 + r, w - 1)] - a[off + Math.Max(i - r, 0)];
            }
        });
    }

    private static void VPass(float[] a, float[] b, int w, int h, int r, long pixels)
    {
        double d = 2 * r + 1;
        const int strip = 64;
        int strips = (w + strip - 1) / strip;
        Action<int> body = s =>
        {
            int x0 = s * strip, x1 = Math.Min(w, x0 + strip), sw = x1 - x0;
            Span<double> sum = stackalloc double[strip];
            for (int x = 0; x < sw; x++) sum[x] = (r + 1) * (double)a[x0 + x];
            for (int j = 1; j <= r; j++)
            {
                int row = Math.Min(j, h - 1) * w + x0;
                for (int x = 0; x < sw; x++) sum[x] += a[row + x];
            }
            for (int y = 0; y < h; y++)
            {
                int o = y * w + x0, add = Math.Min(y + 1 + r, h - 1) * w + x0, rem = Math.Max(y - r, 0) * w + x0;
                for (int x = 0; x < sw; x++)
                {
                    b[o + x] = (float)(sum[x] / d);
                    sum[x] += (double)a[add + x] - a[rem + x];
                }
            }
        };
        if (pixels >= ImagingUtil.ParallelPixels && strips > 1) Parallel.For(0, strips, body);
        else for (int s = 0; s < strips; s++) body(s);
    }
}
