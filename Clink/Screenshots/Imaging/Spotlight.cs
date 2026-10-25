using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// The blurred versions of an image a spotlight blends between. They depend on the image and the radius only, never on the box, so
/// moving or resizing a spotlight re-blends but does not rebuild them. Only the BLURRED levels are kept, as copies owned by this
/// object: the unblurred image (level 0 of a gradient, the inside of the box) is the <c>src</c> the caller passes to
/// <see cref="Spotlight.Blend"/>, which must be the image the levels were built from (same size, same pixels).
/// <para>Memory model: an image of <see cref="Spotlight.AllHalfPixels"/> (4 MP) or more stores every blurred level at half resolution
/// (a quarter of the bytes; sampled bilinearly), whatever its sigma: a 4K gradient holds 4 x 8.3 MB = 33 MB, a fixed spotlight
/// 8.3 MB. Below 4 MP only the levels with sigma &gt;= 6 are half resolution, the others full.</para>
/// </summary>
internal sealed class SpotlightLevels
{
    internal SpotlightLevels(int width, int height, int radius, bool gradient, BgraImage?[] levels, bool[] half, bool opaque)
    {
        Width = width; Height = height; Radius = radius; Gradient = gradient; Levels = levels; Half = half; Opaque = opaque;
    }

    /// <summary>The size of the image the levels were built from.</summary>
    public int Width { get; }
    public int Height { get; }
    public int Radius { get; }
    public bool Gradient { get; }
    /// <summary>
    /// Fixed: one level, the image blurred with sigma = Radius. Gradient: 5 levels, k = sigma Radius * k / 4. A null level is the
    /// unblurred image (the caller's src): level 0 of a gradient always, every level when the radius is 0.
    /// </summary>
    public BgraImage?[] Levels { get; }
    /// <summary>Level k is stored at half resolution (ceil(W / 2) x ceil(H / 2), sampled bilinearly).</summary>
    public bool[] Half { get; }
    /// <summary>The source was fully opaque when the levels were built (so is every level): the blend skips alpha.</summary>
    public bool Opaque { get; }

    /// <summary>The bytes the levels hold.</summary>
    public long Bytes => Levels.Sum(l => l == null ? 0L : l.Pixels.LongLength);
}

/// <summary>
/// The spotlight: the box stays as it is, the rest is blurred and dimmed.
/// <para>Uniform op form: <c>Blend(src, dst, clip, levels, box, dim, falloffPx)</c> reads <c>src</c> (and the levels), writes every
/// pixel of <c>clip</c> (clipped to the image) and nothing else. <c>dst</c> must not alias <c>src</c> or a level buffer (throws).
/// NON-LOCAL: the footprint is the whole image (every pixel depends on the box), so a change of the box or the dim re-blends the
/// whole image; the levels depend on the pixels below only, so the composer rebuilds them only when those change.</para>
/// </summary>
internal static class Spotlight
{
    /// <summary>Below <see cref="AllHalfPixels"/>, levels with a sigma from here on are computed (and kept) at half resolution.</summary>
    public const double HalfResSigma = 6;

    /// <summary>From this many pixels on, every blurred level is kept at half resolution.</summary>
    public const long AllHalfPixels = 4_000_000;

    /// <summary>Distances are clamped here so that their squares never overflow (a box or a falloff beyond a billion pixels).</summary>
    private const long MaxDistance = 1 << 30;

    public static SpotlightLevels Build(BgraImage src, int radius, bool gradient)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        radius = Math.Max(0, radius);
        int w = src.Width, h = src.Height;
        int count = gradient ? 5 : 1;
        bool opaque = !src.HasTransparency();
        if (w == 0 || h == 0 || radius == 0) return new SpotlightLevels(w, h, radius, gradient, new BgraImage?[count], new bool[count], opaque);
        bool allHalf = (long)w * h >= AllHalfPixels;
        var levels = new BgraImage?[count];
        var half = new bool[count];
        BgraImage prev = src;             // previous level at full resolution
        BgraImage? halfPrev = null;       // previous level at half resolution, once the chain went there
        double sPrev = 0;
        for (int k = gradient ? 1 : 0; k < count; k++)
        {
            double s = gradient ? radius * k / 4.0 : radius;
            double inc = Math.Sqrt(Math.Max(0, s * s - sPrev * sPrev));
            if (allHalf || s >= HalfResSigma)
            {
                halfPrev ??= Downsample(prev);
                var next = new BgraImage(halfPrev.Width, halfPrev.Height);
                BoxBlur.Blur(halfPrev, next, halfPrev.Bounds, halfPrev.Bounds, inc / 2);
                halfPrev = next;
                levels[k] = next;
                half[k] = true;
            }
            else
            {
                var next = new BgraImage(w, h);
                BoxBlur.Blur(prev, next, prev.Bounds, prev.Bounds, inc);
                prev = next;
                levels[k] = next;
            }
            sPrev = s;
        }
        if (ImagingUtil.ShouldReport(t0, out double ms))
            ImagingUtil.Report("spotlight-levels", $"{w}x{h} radius {radius} {(gradient ? "gradient" : "fixed")}{(allHalf ? " all-half" : "")}", ms);
        return new SpotlightLevels(w, h, radius, gradient, levels, half, opaque);
    }

    /// <summary>
    /// Writes into <paramref name="dst"/> (the size of <paramref name="src"/>; only the pixels of <paramref name="clip"/>) the image
    /// with everything outside <paramref name="box"/> blurred and darkened: <paramref name="dim"/> 0 = not darkened, 1 = black.
    /// Inside the box: <paramref name="src"/> as it is; across a 2-px feather at the box edge the effect fades in. Fixed levels: the
    /// outside is the one blurred level. Gradient: with d the distance to the box, t = clamp(d / falloffPx, 0, 1), f = 4t, the outside
    /// is the interpolation between levels floor(f) and ceil(f) (falloff 0 or less: the strongest level everywhere). Alpha is
    /// blended like the colour but never darkened. <paramref name="src"/> must be the image <paramref name="levels"/> were built from.
    /// </summary>
    public static void Blend(BgraImage src, BgraImage dst, RectI clip, SpotlightLevels levels, RectI box, double dim, int falloffPx)
    {
        if (src.Width != levels.Width || src.Height != levels.Height)
            throw new ArgumentException($"src ({src.Width} x {src.Height}) must be the image the levels were built from ({levels.Width} x {levels.Height})", nameof(src));
        ImagingUtil.RequireSameSize(src, dst);
        ImagingUtil.RequireNoAlias(src, dst);
        foreach (var lv in levels.Levels)
            if (lv != null && ReferenceEquals(lv.Pixels, dst.Pixels)) throw new ArgumentException("dst must not be a level buffer", nameof(dst));
        var c = clip.Intersect(src.Bounds);
        if (c.IsEmpty) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        int n = levels.Levels.Length;
        int cw = c.Width;
        bool hasBox = !box.IsEmpty;
        long bx0 = box.X, bx1 = (long)box.Right - 1, by0 = box.Y, by1 = (long)box.Bottom - 1;
        long falloff = Math.Min(MaxDistance, Math.Max(0, falloffPx));
        float keepF = (float)(1 - Math.Clamp(dim, 0, 1));

        // Per clip column: the squared horizontal distance to the box.
        var dx2 = new long[cw];
        long minDx2 = long.MaxValue, maxDx2 = 0;
        for (int i = 0; i < cw; i++)
        {
            long x = c.X + i;
            long dx = hasBox ? Math.Min(MaxDistance, Math.Max(Math.Max(bx0 - x, 0), x - bx1)) : 0;
            dx2[i] = dx * dx;
            minDx2 = Math.Min(minDx2, dx2[i]);
            maxDx2 = Math.Max(maxDx2, dx2[i]);
        }
        // Inside columns (dx = 0) of the clip, in clip-column indices; only rows with dy = 0 have inside pixels.
        int inA = hasBox ? (int)Math.Clamp(bx0 - c.X, 0, cw) : 0, inB = hasBox ? (int)Math.Clamp(bx1 + 1 - c.X, 0, cw) : 0;

        // Horizontal sampling of the half-resolution levels (all have the same size): element offsets into a vertical-lerp row that
        // starts at half column hj0.
        int firstHalf = Array.IndexOf(levels.Half, true);
        int[]? hx0 = null, hx1 = null;
        float[]? hfx = null;
        int hj0 = 0, hj1 = -1;
        if (firstHalf >= 0)
        {
            var lv = levels.Levels[firstHalf]!;
            hx0 = new int[cw]; hx1 = new int[cw]; hfx = new float[cw];
            int ha0 = int.MaxValue, ha1 = -1;
            var a0 = new int[cw];
            var a1 = new int[cw];
            for (int i = 0; i < cw; i++)
            {
                double s = Math.Clamp((c.X + i + 0.5) / 2 - 0.5, 0, lv.Width - 1);
                int a = (int)s;
                a0[i] = a; a1[i] = Math.Min(a + 1, lv.Width - 1); hfx[i] = (float)(s - a);
                ha0 = Math.Min(ha0, a0[i]); ha1 = Math.Max(ha1, a1[i]);
            }
            hj0 = ha0; hj1 = ha1;
            for (int i = 0; i < cw; i++) { hx0[i] = (a0[i] - hj0) * 4; hx1[i] = (a1[i] - hj0) * 4; }
        }

        var job = new BlendJob
        {
            Src = src, Dst = dst, Levels = levels, N = n, Gradient = levels.Gradient, C = c,
            Dx2 = dx2, MinDx2 = minDx2, MaxDx2 = maxDx2, InA = inA, InB = inB, HasBox = hasBox, By0 = by0, By1 = by1,
            F2 = falloff * falloff, Scale = falloff > 0 ? (n - 1) / (float)falloff : 0,
            Keep = Vector128.Create(keepF, keepF, keepF, 1f), AlphaOr = levels.Opaque ? 0xFF000000u : 0u,
            Hx0 = hx0, Hx1 = hx1, Hfx = hfx, Hj0 = hj0, HalfSpan = hj1 - hj0 + 1,
        };
        int rows = c.Height;
        if ((long)cw * rows >= ImagingUtil.ParallelPixels && rows > 1)
        {
            int chunks = Math.Min(rows, Environment.ProcessorCount * 4);
            Parallel.For(0, chunks, k => BlendRows(job, c.Y + (int)((long)rows * k / chunks), c.Y + (int)((long)rows * (k + 1) / chunks)));
        }
        else BlendRows(job, c.Y, c.Bottom);
        if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("spotlight-blend", $"{c.Width}x{c.Height} of {src.Width}x{src.Height} box {box}", ms);
    }

    /// <summary>What every row of one blend shares.</summary>
    private sealed class BlendJob
    {
        public required BgraImage Src, Dst;
        public required SpotlightLevels Levels;
        public required int N;
        public required bool Gradient;
        public required RectI C;
        public required long[] Dx2;
        public required long MinDx2, MaxDx2;
        public required int InA, InB;
        public required bool HasBox;
        public required long By0, By1;
        public required long F2;
        public required float Scale;
        public required Vector128<float> Keep;
        public required uint AlphaOr;
        public int[]? Hx0, Hx1;
        public float[]? Hfx;
        public int Hj0, HalfSpan;
    }

    /// <summary>
    /// Rows [y0, y1) of a blend. Per row: the vertical distance once, the levels the row needs (from its least and greatest distance)
    /// each built ONCE into a full-width float row (half-resolution levels: vertical lerp, then horizontal through the tables), the
    /// inside span copied, and per outside pixel one or two contiguous level loads and a lerp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlendRows(BlendJob j, int y0, int y1)
    {
        if (y0 >= y1) return;
        var c = j.C;
        int cw = c.Width, n = j.N;
        var sp = j.Src.Pixels;
        var dp = j.Dst.Pixels;
        int stride = j.Src.Stride;
        var levels = j.Levels.Levels;
        var half = j.Levels.Half;
        var rowBufs = new float[n][];
        for (int k = 0; k < n; k++) rowBufs[k] = ArrayPool<float>.Shared.Rent(cw * 4);
        float[]? tmp = j.HalfSpan > 0 ? ArrayPool<float>.Shared.Rent(j.HalfSpan * 4) : null;
        try
        {
            for (int y = y0; y < y1; y++)
            {
                int rowOff = y * stride + c.X * 4;
                long dy = j.HasBox ? Math.Min(MaxDistance, Math.Max(Math.Max(j.By0 - y, 0), y - j.By1)) : MaxDistance;
                long dy2 = dy * dy;
                bool boxRow = j.HasBox && dy == 0;
                int inA = boxRow ? j.InA : 0, inB = boxRow ? j.InB : 0;
                if (inB > inA) Buffer.BlockCopy(sp, rowOff + inA * 4, dp, rowOff + inA * 4, (inB - inA) * 4);
                if (inA == 0 && inB == cw) continue;   // the whole clipped row is inside the box

                // The levels this row needs.
                int lmin, lmax;
                if (!j.Gradient) lmin = lmax = 0;
                else
                {
                    long dmin2 = dy2 + j.MinDx2, dmax2 = dy2 + j.MaxDx2;
                    lmin = dmin2 >= j.F2 ? n - 1 : Math.Min(n - 1, (int)(MathF.Sqrt(dmin2) * j.Scale));
                    lmax = dmax2 >= j.F2 ? n - 1 : Math.Min(n - 1, (int)(MathF.Sqrt(dmax2) * j.Scale) + 1);
                }
                for (int k = lmin; k <= lmax; k++)
                {
                    var lv = levels[k];
                    if (lv == null) FullRow(sp, rowOff, cw, rowBufs[k]);
                    else if (!half[k]) FullRow(lv.Pixels, rowOff, cw, rowBufs[k]);
                    else HalfRow(lv, y, j.Hx0!, j.Hx1!, j.Hfx!, j.Hj0, j.HalfSpan, cw, tmp!, rowBufs[k]);
                }

                Outside(rowBufs, n, j.Gradient, dy2, j.Dx2, 0, inA, j.F2, j.Scale, j.Keep, sp, dp, rowOff, j.AlphaOr);
                Outside(rowBufs, n, j.Gradient, dy2, j.Dx2, Math.Max(inA, inB), cw, j.F2, j.Scale, j.Keep, sp, dp, rowOff, j.AlphaOr);
            }
        }
        finally
        {
            foreach (var b in rowBufs) ArrayPool<float>.Shared.Return(b);
            if (tmp != null) ArrayPool<float>.Shared.Return(tmp);
        }
    }

    /// <summary>The outside pixels [xa, xb) (clip columns) of one row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Outside(float[][] rows, int n, bool gradient, long dy2, long[] dx2, int xa, int xb, long f2, float scale,
        Vector128<float> keep, byte[] sp, byte[] dp, int rowOff, uint alphaOr)
    {
        if (xa >= xb) return;
        ref float top = ref MemoryMarshal.GetArrayDataReference(rows[n - 1]);
        ref byte spRef = ref MemoryMarshal.GetArrayDataReference(sp);
        ref byte dpRef = ref MemoryMarshal.GetArrayDataReference(dp);
        if ((uint)rowOff + (uint)xb * 4 > (uint)dp.Length || (uint)rowOff + (uint)xb * 4 > (uint)sp.Length || xb > dx2.Length)
            throw new IndexOutOfRangeException();
        for (int x = xa; x < xb; x++)
        {
            long d2 = dx2[x] + dy2;
            nuint e = (nuint)(x * 4);
            Vector128<float> o;
            if (!gradient || d2 >= f2) o = Vector128.LoadUnsafe(ref top, e);
            else
            {
                float f = MathF.Sqrt(d2) * scale;
                int l0 = (int)f;
                if (l0 >= n - 1) o = Vector128.LoadUnsafe(ref top, e);
                else
                {
                    o = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(rows[l0]), e);
                    float fr = f - l0;
                    if (fr > 0)
                    {
                        var o1 = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(rows[l0 + 1]), e);
                        o += (o1 - o) * fr;
                    }
                }
            }
            o *= keep;
            int bi = rowOff + x * 4;
            if (d2 < 4)
            {
                float fe = MathF.Sqrt(d2) * 0.5f;
                var s = LoadBytes(ref Unsafe.Add(ref spRef, bi));
                o = s + (o - s) * fe;
            }
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dpRef, bi), Pack(o) | alphaOr);
        }
    }

    /// <summary>Bytes [off, off + cw * 4) of a full-resolution row as floats.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FullRow(byte[] p, int off, int cw, float[] row)
    {
        if ((uint)off + (uint)cw * 4 > (uint)p.Length || row.Length < cw * 4) throw new IndexOutOfRangeException();
        ref byte src = ref MemoryMarshal.GetArrayDataReference(p);
        ref float dst = ref MemoryMarshal.GetArrayDataReference(row);
        for (int x = 0; x < cw; x++) LoadBytes(ref Unsafe.Add(ref src, off + x * 4)).StoreUnsafe(ref dst, (nuint)(x * 4));
    }

    /// <summary>Row y of a half-resolution level at full width: the vertical lerp of its two rows, then the horizontal lerp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void HalfRow(BgraImage lv, int y, int[] hx0, int[] hx1, float[] hfx, int hj0, int span, int cw, float[] tmp, float[] row)
    {
        int hh = lv.Height;
        double hs = Math.Clamp((y + 0.5) / 2 - 0.5, 0, hh - 1);
        int ya = (int)hs, yb = Math.Min(ya + 1, hh - 1);
        float fy = (float)(hs - ya);
        var p = lv.Pixels;
        int ra = ya * lv.Stride + hj0 * 4, rb = yb * lv.Stride + hj0 * 4;
        if ((uint)ra + (uint)span * 4 > (uint)p.Length || (uint)rb + (uint)span * 4 > (uint)p.Length || tmp.Length < span * 4 || row.Length < cw * 4
            || hx0.Length < cw || hx1.Length < cw || hfx.Length < cw)
            throw new IndexOutOfRangeException();
        ref byte pr = ref MemoryMarshal.GetArrayDataReference(p);
        ref float t = ref MemoryMarshal.GetArrayDataReference(tmp);
        ref float o = ref MemoryMarshal.GetArrayDataReference(row);
        for (int i = 0; i < span; i++)
        {
            var a = LoadBytes(ref Unsafe.Add(ref pr, ra + i * 4));
            var b = LoadBytes(ref Unsafe.Add(ref pr, rb + i * 4));
            (a + (b - a) * fy).StoreUnsafe(ref t, (nuint)(i * 4));
        }
        for (int x = 0; x < cw; x++)
        {
            var a = Vector128.LoadUnsafe(ref t, (nuint)hx0[x]);
            var b = Vector128.LoadUnsafe(ref t, (nuint)hx1[x]);
            (a + (b - a) * hfx[x]).StoreUnsafe(ref o, (nuint)(x * 4));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> LoadBytes(ref byte p)
    {
        var b = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref p)).AsByte();
        return Vector128.ConvertToSingle(Vector128.WidenLower(Vector128.WidenLower(b)).AsInt32());
    }

    /// <summary>Four floats rounded (half up) and clamped to 0..255, packed as BGRA bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(Vector128<float> v)
    {
        v = Vector128.Min(Vector128.Max(v, Vector128<float>.Zero), Vector128.Create(255f)) + Vector128.Create(0.5f);
        var i = Vector128.ConvertToInt32(v).AsUInt32();
        var s = Vector128.Narrow(i, i);
        return Vector128.Narrow(s, s).AsUInt32().ToScalar();
    }

    /// <summary>The image at half size (ceil), each pixel the mean of the 2 x 2 block it covers (clamped at the edge).</summary>
    internal static BgraImage Downsample(BgraImage src)
    {
        int w = Math.Max(1, (src.Width + 1) / 2), h = Math.Max(1, (src.Height + 1) / 2);
        var o = new BgraImage(w, h);
        var sp = src.Pixels;
        var op = o.Pixels;
        int sw = src.Width, sh = src.Height, ss = src.Stride;
        ImagingUtil.ForRows(0, h, (long)sw * sh, y =>
        {
            int ya = Math.Min(2 * y, sh - 1), yb = Math.Min(2 * y + 1, sh - 1);
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Min(2 * x, sw - 1) * 4, xb = Math.Min(2 * x + 1, sw - 1) * 4;
                int oi = (y * w + x) * 4;
                for (int c = 0; c < 4; c++)
                    op[oi + c] = (byte)((sp[ya * ss + xa + c] + sp[ya * ss + xb + c] + sp[yb * ss + xa + c] + sp[yb * ss + xb + c] + 2) >> 2);
            }
        });
        return o;
    }
}
