using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Clink.Screenshots.Imaging;

namespace Clink.Tests;

/// <summary>The pure pixel library of the screenshot editor: blur, pixelate, highlight, stroke masks, smart erase, spotlight, magnifier, adjustments, resampling.</summary>
internal static class ScreenshotImagingTests
{
    // ------------------------------------------------------------------ helpers

    private static BgraImage Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var img = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) img.SetPixel(x, y, b, g, r, a);
        return img;
    }

    /// <summary>Deterministic pseudo-random pixels, opaque unless <paramref name="randomAlpha"/>.</summary>
    private static BgraImage Noise(int w, int h, int seed, bool randomAlpha = false)
    {
        var img = new BgraImage(w, h);
        uint s = (uint)seed * 2654435761u + 12345u;
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            s = s * 1664525u + 1013904223u;
            img.Pixels[i] = (byte)(s >> 24);
        }
        if (!randomAlpha) img.ForceOpaque();
        return img;
    }

    /// <summary>Opaque image with channel c = f(x, y) per channel.</summary>
    private static BgraImage Make(int w, int h, Func<int, int, (int B, int G, int R)> f)
    {
        var img = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (b, g, r) = f(x, y);
                img.SetPixel(x, y, (byte)b, (byte)g, (byte)r);
            }
        return img;
    }

    private static bool Same(BgraImage a, BgraImage b) => a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    /// <summary>Largest per-channel (B, G, R) difference over the rectangle.</summary>
    private static int MaxDiff(BgraImage a, BgraImage b, RectI r)
    {
        int max = 0;
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                max = Math.Max(max, Math.Max(Math.Abs(p.B - q.B), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.R - q.R))));
            }
        return max;
    }

    /// <summary>Largest per-channel difference over the rectangle, alpha included.</summary>
    private static int MaxDiff4(BgraImage a, BgraImage b, RectI r)
    {
        int max = 0;
        r = r.Intersect(a.Bounds);
        for (int y = r.Y; y < r.Bottom; y++)
        {
            int i = y * a.Stride + r.X * 4;
            for (int k = 0; k < r.Width * 4; k++) max = Math.Max(max, Math.Abs(a.Pixels[i + k] - b.Pixels[i + k]));
        }
        return max;
    }

    private static bool OutsideUnchanged(BgraImage before, BgraImage after, RectI inside)
    {
        for (int y = 0; y < before.Height; y++)
            for (int x = 0; x < before.Width; x++)
            {
                if (inside.Contains(x, y)) continue;
                if (before.GetPixel(x, y) != after.GetPixel(x, y)) return false;
            }
        return true;
    }

    private static long Sum(BgraImage img, int channel)
    {
        long s = 0;
        for (int i = channel; i < img.Pixels.Length; i += 4) s += img.Pixels[i];
        return s;
    }

    /// <summary>
    /// The uniform op contract: running <paramref name="op"/> with each clip gives, inside the clip, exactly what the unclipped run
    /// gives there, and leaves every other pixel of dst as it was.
    /// </summary>
    private static void ClipContract(string name, BgraImage src, Action<BgraImage, BgraImage, RectI> op, params RectI[] clips)
    {
        var full = Noise(src.Width, src.Height, 99, randomAlpha: true);
        var pattern = full.Clone();
        op(src, full, src.Bounds);
        foreach (var clip in clips)
        {
            var part = pattern.Clone();
            op(src, part, clip);
            var c = clip.Intersect(src.Bounds);
            Check.Equal(0, c.IsEmpty ? 0 : MaxDiff4(full, part, c), $"{name}, clip {clip}: inside the clip equals the unclipped result");
            Check.True(OutsideUnchanged(pattern, part, c), $"{name}, clip {clip}: nothing written outside the clip");
        }
    }

    private static readonly RectI[] SomeClips =
    {
        new(0, 0, 7, 5), new(10, 8, 13, 9), new(-5, 20, 30, 4), new(30, -3, 50, 70), new(3, 3, 1, 1), new(200, 200, 5, 5),
    };

    // ------------------------------------------------------------------ blur

    [UnitTest]
    public static void Blur_radii_follow_sigma()
    {
        Check.True(BoxBlur.RadiiForSigma(0).All(r => r == 0), "sigma 0: no blur");
        Check.True(BoxBlur.RadiiForSigma(0.3).All(r => r == 0), "a tiny sigma rounds to no blur");
        foreach (double sigma in new[] { 2.0, 5.0, 12.0, 40.0 })
        {
            var radii = BoxBlur.RadiiForSigma(sigma);
            double variance = radii.Sum(r => ((2.0 * r + 1) * (2 * r + 1) - 1) / 12.0);
            Check.True(Math.Abs(Math.Sqrt(variance) - sigma) / sigma < 0.12, $"sigma {sigma}: radii {string.Join(",", radii)} give {Math.Sqrt(variance):0.00}");
        }
        Check.Equal(BoxBlur.RadiiForSigma(5).Sum(), BoxBlur.ReadMargin(5));
    }

    [UnitTest]
    public static void Blur_constant_image_is_unchanged()
    {
        foreach (byte alpha in new byte[] { 255, 128 })
        {
            var img = Solid(37, 23, 10, 200, 77, alpha);
            var copy = img.Clone();
            BoxBlur.Blur(img, img.Bounds, 4.5);
            Check.True(Same(img, copy), $"constant image, alpha {alpha}");
        }
    }

    [UnitTest]
    public static void Blur_impulse_response_is_symmetric_and_keeps_the_sum()
    {
        var img = new BgraImage(65, 65).ForceOpaque();
        for (int y = 30; y <= 34; y++)
            for (int x = 30; x <= 34; x++) img.SetPixel(x, y, 255, 255, 255);   // 5x5 block centred on (32, 32)
        var dst = img.Clone();
        BoxBlur.Blur(img, dst, img.Bounds, img.Bounds, 3.0);
        long total = Sum(dst, 0);
        Check.True(Math.Abs(total - 25 * 255) <= 25 * 255 / 50, $"sum {total} vs {25 * 255}");
        Check.True(dst.GetPixel(32, 32).B > dst.GetPixel(32, 38).B, "peak in the middle");
        for (int y = 0; y < 65; y++)
            for (int x = 0; x < 65; x++)
            {
                int v = dst.GetPixel(x, y).B;
                Check.True(Math.Abs(v - dst.GetPixel(64 - x, y).B) <= 1, $"mirror x at {x},{y}");
                Check.True(Math.Abs(v - dst.GetPixel(x, 64 - y).B) <= 1, $"mirror y at {x},{y}");
                Check.True(Math.Abs(v - dst.GetPixel(y, x).B) <= 1, $"transpose at {x},{y}");
            }
        Check.Equal((byte)255, dst.GetPixel(0, 0).A, "alpha stays opaque");
    }

    [UnitTest]
    public static void Blur_writes_only_the_region_and_matches_the_whole_image_blur_there()
    {
        var img = Noise(70, 50, 1);
        var whole = img.Clone();
        BoxBlur.Blur(img, whole, img.Bounds, img.Bounds, 3.5);

        var region = new RectI(20, 15, 25, 20);
        var dst = img.Clone();
        BoxBlur.Blur(img, dst, region, region, 3.5);
        Check.True(OutsideUnchanged(img, dst, region), "nothing written outside the region");
        Check.Equal(0, MaxDiff(whole, dst, region), "region equals the same pixels of the whole-image blur (the margin is read)");
        Check.True(MaxDiff(img, dst, region) > 10, "the region really was blurred");

        var inPlace = img.Clone();
        BoxBlur.Blur(inPlace, region, 3.5);
        Check.True(Same(dst, inPlace), "in-place overload gives the same result");

        // a region at the image edge uses edge clamping
        var edge = img.Clone();
        BoxBlur.Blur(img, edge, img.Bounds, new RectI(0, 0, 10, 10), 3.5);
        Check.Equal(0, MaxDiff(whole, edge, new RectI(0, 0, 10, 10)), "corner region equals whole-image blur");
        BoxBlur.Blur(img, edge, img.Bounds, new RectI(-20, -20, 5, 5), 3.5);   // outside: no-op, no throw
        Check.Throws<ArgumentException>(() => BoxBlur.Blur(img, new BgraImage(5, 5), img.Bounds, region, 2));
    }

    [UnitTest]
    public static void Blur_sigma_0_is_the_identity_and_copies_only_the_region()
    {
        var src = Noise(40, 30, 21, randomAlpha: true);
        var inPlace = src.Clone();
        BoxBlur.Blur(inPlace, inPlace.Bounds, 0);
        Check.True(Same(src, inPlace), "sigma 0 in place: unchanged");

        var dst = Solid(40, 30, 1, 2, 3, 4);
        var before = dst.Clone();
        var region = new RectI(5, 6, 12, 9);
        BoxBlur.Blur(src, dst, dst.Bounds, region, 0);
        Check.Equal(0, MaxDiff4(src, dst, region), "the region is a copy of src");
        Check.True(OutsideUnchanged(before, dst, region), "only the region is copied");

        var clipped = before.Clone();
        BoxBlur.Blur(src, clipped, new RectI(0, 0, 10, 10), region, 0.2);   // too small for a box: a copy too
        Check.Equal(0, MaxDiff4(src, clipped, new RectI(5, 6, 5, 4)));
        Check.True(OutsideUnchanged(before, clipped, new RectI(5, 6, 5, 4)), "only clip ∩ region");
    }

    [UnitTest]
    public static void Blur_respects_the_clip_contract()
    {
        var region = new RectI(4, 3, 40, 30);
        ClipContract("blur opaque", Noise(50, 40, 2), (s, d, c) => BoxBlur.Blur(s, d, c, region, 3.2), SomeClips);
        ClipContract("blur with alpha", Noise(50, 40, 3, randomAlpha: true), (s, d, c) => BoxBlur.Blur(s, d, c, region, 2.5), SomeClips);
    }

    [UnitTest]
    public static void Blur_with_transparency_is_premultiplied_so_transparent_black_does_not_darken()
    {
        // left half opaque red, right half transparent black
        var img = new BgraImage(40, 10);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 20; x++) img.SetPixel(x, y, 0, 0, 255, 255);
        var dst = new BgraImage(40, 10);
        BoxBlur.Blur(img, dst, img.Bounds, img.Bounds, 3);
        bool sawPartial = false;
        for (int x = 0; x < 40; x++)
        {
            var p = dst.GetPixel(x, 5);
            if (p.A == 0) continue;
            if (p.A < 255) sawPartial = true;
            Check.True(p.R >= 254 && p.B == 0 && p.G == 0, $"x {x}: straight colour stays red ({p.B},{p.G},{p.R},{p.A})");
        }
        Check.True(sawPartial, "the edge is partly transparent");
        Check.True(dst.GetPixel(19, 5).A < 255 && dst.GetPixel(20, 5).A > 0, "alpha is blurred");
        Check.Equal(((byte)0, (byte)0, (byte)0, (byte)0), dst.GetPixel(39, 5), "far from the edge: transparent black");
    }

    [UnitTest]
    public static void PlanePool_reuses_planes_and_clear_releases_them()
    {
        PlanePool.Clear();
        Check.Equal(0L, PlanePool.KeptBytes);
        var a = PlanePool.Rent(1 << 20);
        Check.True(a.Length >= 1 << 20);
        PlanePool.Return(a);
        Check.True(PlanePool.KeptBytes >= 4L << 20, "kept after return");
        Check.True(ReferenceEquals(a, PlanePool.Rent(900_000)), "a smaller request reuses the kept plane");
        PlanePool.Return(a);
        var small = PlanePool.Rent(100);
        PlanePool.Return(small);
        Check.True(PlanePool.KeptBytes < (5L << 20), "small planes are not pooled");
        for (int i = 0; i < PlanePool.MaxKept + 3; i++) PlanePool.Return(new float[PlanePool.MinPooled + i]);
        Check.True(PlanePool.KeptBytes <= (long)PlanePool.MaxKept * a.Length * 4, "at most MaxKept planes");
        PlanePool.Clear();
        Check.Equal(0L, PlanePool.KeptBytes, "Clear drops everything");
        var img = Noise(300, 300, 4);
        BoxBlur.Blur(img, img.Bounds, 3);   // works again after a clear
    }

    // ------------------------------------------------------------------ pixelate

    [UnitTest]
    public static void Pixelate_2x2_blocks_become_their_means()
    {
        var img = new BgraImage(4, 4);
        byte[,] v = { { 0, 10, 100, 200 }, { 20, 30, 100, 200 }, { 1, 2, 3, 4 }, { 5, 6, 7, 8 } };
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) img.SetPixel(x, y, v[y, x], (byte)(255 - v[y, x]), v[y, x]);
        Pixelate.Apply(img, img.Bounds, 2);
        Check.Equal((byte)15, img.GetPixel(0, 0).B, "mean of 0,10,20,30");
        Check.Equal((byte)15, img.GetPixel(1, 1).B);
        Check.Equal((byte)240, img.GetPixel(1, 0).G, "green is 255 - 15");
        Check.Equal((byte)150, img.GetPixel(2, 0).B, "mean of 100,200,100,200");
        Check.Equal((byte)150, img.GetPixel(3, 1).B);
        Check.Equal((byte)4, img.GetPixel(0, 2).B, "mean of 1,2,5,6 = 3.5, rounded");
        Check.Equal((byte)6, img.GetPixel(3, 3).B, "mean of 3,4,7,8 = 5.5, rounded");
        Check.Equal((byte)255, img.GetPixel(0, 0).A);
    }

    [UnitTest]
    public static void Pixelate_partial_blocks_at_the_edges_and_the_anchor_at_the_rectangle()
    {
        var img = Make(6, 6, (x, y) => (x * 10 + y, 0, 0));
        var before = img.Clone();
        var r = new RectI(1, 1, 5, 3);   // blocks of 2 anchored at (1,1): x 1-2, 3-4, 5; y 1-2, 3
        Pixelate.Apply(img, r, 2);
        Check.True(OutsideUnchanged(before, img, r), "nothing outside the rectangle");
        // block x 1-2, y 1-2: values x*10+y for (1,1)=11 (2,1)=21 (1,2)=12 (2,2)=22 -> mean 16.5 -> 17
        Check.Equal((byte)17, img.GetPixel(1, 1).B);
        Check.Equal((byte)17, img.GetPixel(2, 2).B);
        // the last column (x = 5) is a block of width 1: pixels (5,1), (5,2) -> 51, 52 -> 51.5 -> 52
        Check.Equal((byte)52, img.GetPixel(5, 2).B);
        // the last row (y = 3) is a block of height 1: (1,3), (2,3) -> 13, 23 -> 18
        Check.Equal((byte)18, img.GetPixel(1, 3).B);
        Check.Equal((byte)18, img.GetPixel(2, 3).B);
        // the corner block is one pixel: unchanged
        Check.Equal(before.GetPixel(5, 3).B, img.GetPixel(5, 3).B);

        // a rectangle dragged past the image keeps its grid; only the visible part is touched
        var img2 = Make(6, 6, (x, y) => (x * 10 + y, 0, 0));
        Pixelate.Apply(img2, new RectI(-1, -1, 4, 4), 2);   // blocks x -1..0, 1..2; the first holds the pixels x = 0 only
        Check.Equal(before.GetPixel(0, 0).B, img2.GetPixel(0, 0).B, "block (-1..0, -1..0) is the single pixel (0,0)");
        Check.Equal(img2.GetPixel(1, 1).B, img2.GetPixel(2, 2).B, "block x 1-2, y 1-2 is flat");
    }

    [UnitTest]
    public static void Pixelate_clip_writes_the_full_block_mean_and_nothing_else()
    {
        var region = new RectI(-3, 2, 45, 33);
        ClipContract("pixelate", Noise(50, 40, 5), (s, d, c) => Pixelate.Apply(s, d, c, region, 6), SomeClips);
        ClipContract("pixelate alpha", Noise(50, 40, 6, randomAlpha: true), (s, d, c) => Pixelate.Apply(s, d, c, region, 4), SomeClips);

        // a clip that cuts a block: the written part has the mean of the whole block, not of the clipped part
        var img = Make(8, 8, (x, y) => (x * 20, 0, 0));
        var dst = img.Clone();
        Pixelate.Apply(img, dst, new RectI(0, 0, 2, 8), img.Bounds, 4);
        Check.Equal((byte)30, dst.GetPixel(0, 0).B, "mean of 0, 20, 40, 60 over the block");
        Check.Equal(img.GetPixel(2, 0).B, dst.GetPixel(2, 0).B, "outside the clip untouched");
    }

    [UnitTest]
    public static void Pixelate_with_transparency_is_premultiplied()
    {
        var img = new BgraImage(2, 2);
        img.SetPixel(0, 0, 0, 0, 255, 255);
        img.SetPixel(1, 0, 0, 0, 255, 255);   // two opaque red, two transparent black
        Pixelate.Apply(img, img.Bounds, 2);
        Check.Equal(((byte)0, (byte)0, (byte)255, (byte)128), img.GetPixel(1, 1), "the colour of the opaque pixels, half the alpha");
        var clear = new BgraImage(2, 2);
        Pixelate.Apply(clear, clear.Bounds, 2);
        Check.Equal(((byte)0, (byte)0, (byte)0, (byte)0), clear.GetPixel(0, 0), "a transparent block stays transparent black");
    }

    // ------------------------------------------------------------------ stroke mask

    [UnitTest]
    public static void StrokeMask_has_full_coverage_on_the_centreline_and_none_beyond_the_pen()
    {
        var clip = new RectI(0, 0, 100, 60);
        var mask = StrokeMask.Rasterize(new[] { new Point(10, 30.5), new Point(90, 30.5) }, 8, clip);
        for (int x = 10; x < 90; x++) Check.Equal((byte)255, mask.Get(x, 30), $"centreline at {x}");
        Check.Equal((byte)255, mask.Get(30, 27), "up to width/2 - 0.5 from the line is full");
        Check.True(mask.Get(30, 26) > 0 && mask.Get(30, 26) < 255, "the ramp pixel");
        Check.Equal((byte)0, mask.Get(30, 25), "beyond width/2 + 1");
        Check.Equal((byte)0, mask.Get(30, 36));
        Check.True(mask.Get(8, 30) > 0, "round cap");
        Check.Equal((byte)0, mask.Get(4, 30), "beyond the cap");
        Check.True(clip.Contains(mask.Bounds), "bounds inside the clip");

        var clipped = StrokeMask.Rasterize(new[] { new Point(-50, 10), new Point(50, 10) }, 6, new RectI(0, 0, 20, 20));
        Check.True(new RectI(0, 0, 20, 20).Contains(clipped.Bounds), "clipped to the clip rectangle");
        Check.Equal((byte)255, clipped.Get(5, 10));
        Check.True(StrokeMask.Rasterize(Array.Empty<Point>(), 6, clip).Bounds.IsEmpty, "no points: empty");
    }

    [UnitTest]
    public static void StrokeMask_overlap_takes_the_maximum_and_one_point_is_a_dot()
    {
        var clip = new RectI(0, 0, 60, 60);
        // two strokes crossing at (30,30): the segment mask of a doubled-back path equals the single pass
        var single = StrokeMask.Rasterize(new[] { new Point(10, 30.5), new Point(50, 30.5) }, 5, clip);
        var doubled = StrokeMask.Rasterize(new[] { new Point(10, 30.5), new Point(50, 30.5), new Point(10, 30.5), new Point(50, 30.5) }, 5, clip);
        Check.True(single.Coverage.AsSpan().SequenceEqual(doubled.Coverage), "going over the same line twice does not change the coverage");
        var cross = StrokeMask.Rasterize(new[] { new Point(10, 30.5), new Point(50, 30.5), new Point(30.5, 10), new Point(30.5, 50) }, 5, clip);
        Check.Equal((byte)255, cross.Get(30, 30), "crossing point is 255, not a sum");
        Check.True(cross.Coverage.All(c => c <= 255));

        var dot = StrokeMask.Rasterize(new[] { new Point(20.5, 20.5) }, 9, clip);
        Check.Equal((byte)255, dot.Get(20, 20));
        Check.Equal((byte)255, dot.Get(23, 20), "radius 4.5: 3 px away is full");
        Check.Equal((byte)0, dot.Get(26, 20));
        Check.Equal(dot.Get(17, 20), dot.Get(23, 20), "round: symmetric");
        Check.Equal(dot.Get(20, 17), dot.Get(23, 20), "round: both axes");
        Check.True(dot.Get(24, 24) < 255, "corner of the box is outside the circle");
    }

    [UnitTest]
    public static void StrokeMask_capsule_area_is_within_3_percent()
    {
        var clip = new RectI(-50, -50, 400, 300);
        foreach (var (a, b, width) in new[]
        {
            (new Point(20.3, 30.7), new Point(120.9, 70.2), 9.0), (new Point(10, 10), new Point(200, 10.5), 3.0),
            (new Point(50.5, 50.5), new Point(50.5, 50.5), 15.0), (new Point(5, 100), new Point(150, 20), 24.0),
        })
        {
            var mask = StrokeMask.Rasterize(new[] { a, b }, width, clip);
            double area = mask.Coverage.Sum(c => (double)c) / 255;
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            double expected = len * width + Math.PI * width * width / 4;
            Check.True(Math.Abs(area - expected) / expected < 0.03, $"capsule {a} -> {b} width {width}: area {area:0.0} vs {expected:0.0}");
        }
    }

    // ------------------------------------------------------------------ highlight

    [UnitTest]
    public static void Highlight_multiplies_black_stays_white_takes_the_colour()
    {
        var hl = Color.FromRgb(0xFF, 0xEB, 0x3B);
        var img = new BgraImage(3, 1);
        img.SetPixel(0, 0, 0, 0, 0);
        img.SetPixel(1, 0, 255, 255, 255);
        img.SetPixel(2, 0, 100, 100, 100, 200);
        Highlight.Rect(img, img.Bounds, hl);
        Check.Equal((0, 0, 0), (img.GetPixel(0, 0).B, img.GetPixel(0, 0).G, img.GetPixel(0, 0).R), "black stays black");
        Check.Equal((0x3B, 0xEB, 0xFF), (img.GetPixel(1, 0).B, img.GetPixel(1, 0).G, img.GetPixel(1, 0).R), "white becomes the colour");
        Check.Equal((byte)200, img.GetPixel(2, 0).A, "alpha untouched");
        Check.True(img.GetPixel(2, 0).B < 100 && img.GetPixel(2, 0).R == 100, "grey: blue channel darkened, red kept (hl.R = 255)");

        var img2 = new BgraImage(2, 1);
        img2.SetPixel(0, 0, 255, 255, 255);
        img2.SetPixel(1, 0, 255, 255, 255);
        var mask = new AlphaMask(new RectI(0, 0, 2, 1), new byte[] { 0, 128 });
        Highlight.Multiply(img2, mask, hl);
        Check.Equal((255, 255, 255), (img2.GetPixel(0, 0).B, img2.GetPixel(0, 0).G, img2.GetPixel(0, 0).R), "m = 0: unchanged");
        int b = img2.GetPixel(1, 0).B;
        Check.True(b > 0x3B && b < 255 && Math.Abs(b - (255 + 0x3B) / 2) <= 2, $"m = 0.5: halfway, got {b}");
    }

    [UnitTest]
    public static void Highlight_stroke_mask_clips_to_the_image_and_does_not_double_darken()
    {
        var img = Solid(40, 20, 255, 255, 255);
        var pts = new[] { new Point(5, 10.5), new Point(35, 10.5), new Point(5, 10.5) };
        var mask = StrokeMask.Rasterize(pts, 6, new RectI(-10, -10, 100, 100));
        Highlight.Multiply(img, mask, Color.FromRgb(255, 255, 0));
        Check.Equal((0, 255, 255), (img.GetPixel(20, 10).B, img.GetPixel(20, 10).G, img.GetPixel(20, 10).R), "yellow (B 0, G 255, R 255) once, not twice");
        Check.Equal((255, 255, 255), (img.GetPixel(20, 0).B, img.GetPixel(20, 0).G, img.GetPixel(20, 0).R), "far from the pen");
        Highlight.Rect(img, new RectI(-5, -5, 10, 10), Color.FromRgb(255, 0, 0));   // partly outside: no throw
        Check.Equal((byte)0, img.GetPixel(0, 0).G);
        Check.Equal((byte)255, img.GetPixel(6, 6).G, "outside the rectangle");
    }

    [UnitTest]
    public static void Highlight_src_to_dst_writes_the_footprint_inside_the_clip()
    {
        var hl = Color.FromRgb(255, 200, 0);
        var mask = StrokeMask.Rasterize(new[] { new Point(3, 3), new Point(45, 30) }, 7, new RectI(0, 0, 50, 40));
        ClipContract("highlight mask", Noise(50, 40, 7, randomAlpha: true), (s, d, c) => Highlight.Multiply(s, d, c, mask, hl), SomeClips);
        ClipContract("highlight rect", Noise(50, 40, 8), (s, d, c) => Highlight.Rect(s, d, c, new RectI(5, 5, 30, 20), hl), SomeClips);
        // zero coverage inside the mask bounds is a copy of src, not left as it was in dst
        var src = Solid(4, 1, 10, 20, 30);
        var dst = Solid(4, 1, 99, 99, 99);
        Highlight.Multiply(src, dst, dst.Bounds, new AlphaMask(new RectI(0, 0, 3, 1), new byte[] { 0, 255, 0 }), hl);
        Check.Equal(src.GetPixel(0, 0), dst.GetPixel(0, 0), "m = 0 copies src");
        Check.Equal(((byte)99, (byte)99, (byte)99, (byte)255), dst.GetPixel(3, 0), "outside the mask bounds untouched");
    }

    // ------------------------------------------------------------------ smart erase

    private static BgraImage LinearImage(int w, int h) =>
        Make(w, h, (x, y) => (20 + 2 * x + y, 200 - x - 2 * y, 100 + x - y));

    private static void Paint(BgraImage img, RectI r, byte b, byte g, byte rr, byte a = 255)
    {
        r = r.Intersect(img.Bounds);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++) img.SetPixel(x, y, b, g, rr, a);
    }

    [UnitTest]
    public static void SmartErase_uniform_ring_fills_the_exact_colour()
    {
        var img = Solid(60, 40, 50, 100, 150);
        var r = new RectI(20, 10, 20, 15);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++) img.SetPixel(x, y, 255, 0, 255, 17);
        // a little noise in the ring within the tolerance
        img.SetPixel(19, 12, 52, 98, 151);
        img.SetPixel(40, 20, 49, 101, 149);
        SmartErase.Fill(img, r);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                Check.Equal((50, 100, 150, 255), (img.GetPixel(x, y).B, img.GetPixel(x, y).G, img.GetPixel(x, y).R, img.GetPixel(x, y).A), $"at {x},{y}");
    }

    [UnitTest]
    public static void SmartErase_reproduces_a_linear_gradient_from_four_sides()
    {
        var truth = LinearImage(80, 60);
        var img = truth.Clone();
        var r = new RectI(25, 15, 30, 25);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++) img.SetPixel(x, y, 255, 0, 255);
        SmartErase.Fill(img, r);
        Check.True(MaxDiff(truth, img, r) <= 1, $"max error {MaxDiff(truth, img, r)}");
        Check.True(OutsideUnchanged(truth, img, r), "nothing outside the rectangle changed");

        // a thin and a one-pixel rectangle as well
        foreach (var thin in new[] { new RectI(30, 10, 1, 30), new RectI(10, 30, 50, 1), new RectI(40, 30, 1, 1) })
        {
            var t = truth.Clone();
            for (int y = thin.Y; y < thin.Bottom; y++)
                for (int x = thin.X; x < thin.Right; x++) t.SetPixel(x, y, 0, 0, 0);
            SmartErase.Fill(t, thin);
            Check.True(MaxDiff(truth, t, thin) <= 1, $"{thin}: max error {MaxDiff(truth, t, thin)}");
        }
    }

    [UnitTest]
    public static void SmartErase_rectangle_on_the_image_edge_uses_the_sides_it_has()
    {
        // horizontal gradient, rectangle touching the top edge: left, right and bottom exist, top does not
        var truth = Make(90, 50, (x, y) => (10 + 3 * x, 100, 50));
        var img = truth.Clone();
        var r = new RectI(30, 0, 30, 20);
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++) img.SetPixel(x, y, 0, 255, 0);
        SmartErase.Fill(img, r);
        Check.True(MaxDiff(truth, img, r) <= 1, $"top edge: max error {MaxDiff(truth, img, r)}");

        // vertical gradient, rectangle on the left edge: top, bottom and right exist
        var truth2 = Make(50, 90, (x, y) => (10, 3 * y + 5, 50));
        var img2 = truth2.Clone();
        var r2 = new RectI(0, 30, 20, 30);
        for (int y = r2.Y; y < r2.Bottom; y++)
            for (int x = r2.X; x < r2.Right; x++) img2.SetPixel(x, y, 0, 255, 0);
        SmartErase.Fill(img2, r2);
        Check.True(MaxDiff(truth2, img2, r2) <= 1, $"left edge: max error {MaxDiff(truth2, img2, r2)}");

        // one side only: full height, touching the left, top and bottom edges
        var truth3 = Make(60, 70, (x, y) => (10 + 3 * y, 20, 30));
        var img3 = truth3.Clone();
        var r3 = new RectI(0, 0, 25, 70);
        for (int y = 0; y < 70; y++)
            for (int x = 0; x < 25; x++) img3.SetPixel(x, y, 0, 255, 0);
        SmartErase.Fill(img3, r3);
        Check.True(MaxDiff(truth3, img3, r3) <= 1, $"one side: max error {MaxDiff(truth3, img3, r3)}");

        // two adjacent sides: the corner of the image; values stay between the neighbours' range and are opaque
        var img4 = LinearImage(60, 50);
        var before4 = img4.Clone();
        var r4 = new RectI(0, 0, 20, 15);
        SmartErase.Fill(img4, r4);
        Check.True(OutsideUnchanged(before4, img4, r4));
        Check.Equal((byte)255, img4.GetPixel(0, 0).A);
        Check.True(MaxDiff(before4, img4, r4) <= 40, $"adjacent sides, plausible fill: {MaxDiff(before4, img4, r4)}");

        // two opposite sides: a vertical band across the whole height
        var truth5 = Make(80, 40, (x, y) => (10 + 2 * x, 90, 40));
        var img5 = truth5.Clone();
        var r5 = new RectI(30, 0, 20, 40);
        for (int y = 0; y < 40; y++)
            for (int x = 30; x < 50; x++) img5.SetPixel(x, y, 0, 255, 0);
        SmartErase.Fill(img5, r5);
        Check.True(MaxDiff(truth5, img5, r5) <= 1, $"opposite sides: max error {MaxDiff(truth5, img5, r5)}");

        // a rectangle one pixel below the top edge: a top ring of one row
        var truth6 = LinearImage(80, 60);
        var img6 = truth6.Clone();
        var r6 = new RectI(25, 1, 30, 20);
        Paint(img6, r6, 0, 255, 0);
        SmartErase.Fill(img6, r6);
        Check.True(MaxDiff(truth6, img6, r6) <= 1, $"y0 = 1: max error {MaxDiff(truth6, img6, r6)}");
    }

    [UnitTest]
    public static void SmartErase_two_adjacent_sides_weight_by_inverse_squared_distance()
    {
        // rectangle in the bottom-right corner: only the top and the left side exist. Above the rectangle's rows the image is A
        // (corner block included), left of it B.
        var a = (B: 200, G: 50, R: 120);
        var bb = (B: 40, G: 180, R: 60);
        var img = Make(60, 50, (x, y) => y < 30 ? a : bb);
        var r = new RectI(35, 30, 25, 20);
        Paint(img, r, 0, 0, 0);
        SmartErase.Fill(img, r);
        double yT = r.Y - 2, xL = r.X - 2;   // a ring of 3: the profiles sit 2 px outside
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                var p = img.GetPixel(x, y);
                double wv = 1 / ((y - yT) * (y - yT)), wu = 1 / ((x - xL) * (x - xL));
                var expected = (B: (wv * a.B + wu * bb.B) / (wv + wu), G: (wv * a.G + wu * bb.G) / (wv + wu), R: (wv * a.R + wu * bb.R) / (wv + wu));
                if (y > r.Y)
                    Check.Equal((ImagingUtil.ClampByte(expected.B), ImagingUtil.ClampByte(expected.G), ImagingUtil.ClampByte(expected.R), (byte)255), (p.B, p.G, p.R, p.A), $"at {x},{y}");
                else   // the first row's left profile value also sees the corner block (A) through the filter
                    Check.True(p.B >= bb.B && p.B <= a.B && p.G >= a.G && p.G <= bb.G, $"first row at {x}: between the sides");
            }
    }

    [UnitTest]
    public static void SmartErase_a_thin_dark_line_through_the_ring_hardly_shows()
    {
        var truth = LinearImage(90, 70);
        var img = truth.Clone();
        var r = new RectI(30, 20, 30, 30);
        for (int x = 0; x < 90; x++) img.SetPixel(x, 35, 0, 0, 0);    // a horizontal line through the left and right rings
        for (int y = 0; y < 70; y++) img.SetPixel(45, y, 0, 0, 0);    // a vertical line through the top and bottom rings
        SmartErase.Fill(img, r);
        int dev = MaxDiff(truth, img, r);
        Check.True(dev <= 6, $"deviation {dev}");
    }

    [UnitTest]
    public static void SmartErase_a_line_on_the_rectangle_edge_or_along_the_ring_hardly_shows()
    {
        var truth = LinearImage(90, 70);
        var r = new RectI(30, 20, 30, 30);
        var cases = new (string Name, Action<BgraImage> Draw)[]
        {
            ("vertical line at r.X", im => { for (int y = 0; y < 70; y++) im.SetPixel(r.X, y, 0, 0, 0); }),
            ("vertical line at r.Right - 1", im => { for (int y = 0; y < 70; y++) im.SetPixel(r.Right - 1, y, 0, 0, 0); }),
            ("horizontal line at r.Y", im => { for (int x = 0; x < 90; x++) im.SetPixel(x, r.Y, 0, 0, 0); }),
            ("horizontal line at r.Bottom - 1", im => { for (int x = 0; x < 90; x++) im.SetPixel(x, r.Bottom - 1, 255, 255, 255); }),
            ("border line inside the top ring", im => { for (int x = 0; x < 90; x++) im.SetPixel(x, r.Y - 2, 0, 0, 0); }),
            ("border line inside the left ring", im => { for (int y = 0; y < 70; y++) im.SetPixel(r.X - 2, y, 255, 255, 255); }),
        };
        foreach (var (name, draw) in cases)
        {
            var img = truth.Clone();
            draw(img);
            SmartErase.Fill(img, r);
            int dev = MaxDiff(truth, img, r);
            Check.True(dev <= 6, $"{name}: deviation {dev}");
        }
    }

    [UnitTest]
    public static void SmartErase_transparent_ring_pixels_do_not_pull_the_fill_to_black()
    {
        // flat: the left part of the ring is transparent black, the rest one colour
        var img = Solid(60, 40, 50, 100, 150);
        Paint(img, new RectI(0, 0, 20, 40), 0, 0, 0, 0);
        var r = new RectI(20, 10, 20, 15);
        Paint(img, r, 255, 0, 255);
        SmartErase.Fill(img, r);
        Check.Equal(((byte)50, (byte)100, (byte)150, (byte)255), img.GetPixel(25, 15), "flat fill from the opaque ring pixels only");

        // a gradient with a transparent left side: the Coons patch falls back to the other sides
        var truth = LinearImage(80, 60);
        var g = truth.Clone();
        var r2 = new RectI(25, 15, 30, 25);
        Paint(g, new RectI(0, 0, 25, 60), 0, 0, 0, 0);
        Paint(g, r2, 255, 0, 255);
        SmartErase.Fill(g, r2);
        Check.True(MaxDiff(truth, g, r2) <= 2, $"transparent left side: max error {MaxDiff(truth, g, r2)}");
        Check.Equal((byte)255, g.GetPixel(30, 20).A, "the fill is opaque");
    }

    [UnitTest]
    public static void SmartErase_is_deterministic_and_stays_inside_the_rectangle()
    {
        var src = Noise(70, 50, 7);
        var rects = new[]
        {
            new RectI(20, 10, 25, 20), new RectI(0, 0, 15, 15), new RectI(55, 35, 30, 30), new RectI(0, 20, 70, 10), new RectI(0, 0, 70, 50), new RectI(-5, -5, 20, 20),
        };
        foreach (var r in rects)
        {
            var a = src.Clone();
            var b = src.Clone();
            SmartErase.Fill(a, r);
            SmartErase.Fill(b, r);
            Check.True(Same(a, b), $"{r}: two runs identical");
            Check.True(OutsideUnchanged(src, a, r.Intersect(src.Bounds)), $"{r}: nothing outside written");
        }
        var empty = src.Clone();
        SmartErase.Fill(empty, new RectI(200, 200, 10, 10));
        SmartErase.Fill(empty, RectI.Empty);
        Check.True(Same(src, empty), "outside the image / empty: no-op");
    }

    [UnitTest]
    public static void SmartErase_respects_the_clip_contract()
    {
        var lin = LinearImage(50, 40);
        ClipContract("smart erase gradient", lin, (s, d, c) => SmartErase.Fill(s, d, c, new RectI(8, 6, 30, 25)), SomeClips);
        ClipContract("smart erase noise", Noise(50, 40, 9), (s, d, c) => SmartErase.Fill(s, d, c, new RectI(-4, 10, 30, 40)), SomeClips);
        ClipContract("smart erase alpha", Noise(50, 40, 10, randomAlpha: true), (s, d, c) => SmartErase.Fill(s, d, c, new RectI(8, 6, 30, 25)), SomeClips);
    }

    [UnitTest]
    public static void SmartErase_whole_image_fills_the_median_of_its_own_border()
    {
        var img = Noise(30, 20, 3);
        for (int x = 0; x < 30; x++) { img.SetPixel(x, 0, 200, 100, 50); img.SetPixel(x, 19, 200, 100, 50); }
        for (int y = 0; y < 20; y++) { img.SetPixel(0, y, 200, 100, 50); img.SetPixel(29, y, 200, 100, 50); }
        SmartErase.Fill(img, img.Bounds);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 30; x++)
                Check.Equal((200, 100, 50, 255), (img.GetPixel(x, y).B, img.GetPixel(x, y).G, img.GetPixel(x, y).R, img.GetPixel(x, y).A), $"at {x},{y}");
    }

    // ------------------------------------------------------------------ spotlight

    /// <summary>
    /// The spec's spotlight, pixel by pixel in doubles: the reference the optimized blend is checked against. Half-resolution
    /// levels are sampled bilinearly at ((x + 0.5) / 2 - 0.5, (y + 0.5) / 2 - 0.5), clamped; a null level is the source.
    /// </summary>
    private static BgraImage ReferenceBlend(BgraImage src, SpotlightLevels lv, RectI box, double dim, int falloff, RectI area)
    {
        var o = new BgraImage(src.Width, src.Height);
        double keep = 1 - Math.Clamp(dim, 0, 1);
        int n = lv.Levels.Length;
        var a = area.Intersect(src.Bounds);
        var sv = new double[4];
        for (int y = a.Y; y < a.Bottom; y++)
            for (int x = a.X; x < a.Right; x++)
            {
                var s = src.GetPixel(x, y);
                sv[0] = s.B; sv[1] = s.G; sv[2] = s.R; sv[3] = s.A;
                double d;
                if (box.IsEmpty) d = double.PositiveInfinity;
                else
                {
                    double dx = Math.Max(Math.Max((double)box.X - x, 0), x - (box.Right - 1.0));
                    double dy = Math.Max(Math.Max((double)box.Y - y, 0), y - (box.Bottom - 1.0));
                    d = Math.Sqrt(dx * dx + dy * dy);
                }
                if (d == 0) { o.SetPixel(x, y, s.B, s.G, s.R, s.A); continue; }
                double[] v;
                if (!lv.Gradient) v = RefSample(src, lv, 0, x, y);
                else
                {
                    double t = falloff > 0 ? Math.Min(1, d / falloff) : 1;
                    double f = t * (n - 1);
                    int l0 = Math.Min((int)Math.Floor(f), n - 1), l1 = Math.Min(l0 + 1, n - 1);
                    double fr = f - l0;
                    v = RefSample(src, lv, l0, x, y);
                    if (fr > 0 && l1 != l0)
                    {
                        var v1 = RefSample(src, lv, l1, x, y);
                        for (int c = 0; c < 4; c++) v[c] += (v1[c] - v[c]) * fr;
                    }
                }
                double feather = d >= 2 ? 1 : d / 2;
                var r = new byte[4];
                for (int c = 0; c < 4; c++)
                {
                    double outside = c < 3 ? v[c] * keep : v[c];
                    r[c] = ImagingUtil.ClampByte(sv[c] + (outside - sv[c]) * feather);
                }
                o.SetPixel(x, y, r[0], r[1], r[2], r[3]);
            }
        return o;
    }

    private static double[] RefSample(BgraImage src, SpotlightLevels lv, int k, int x, int y)
    {
        var img = lv.Levels[k] ?? src;
        if (!lv.Half[k])
        {
            var p = img.GetPixel(x, y);
            return new double[] { p.B, p.G, p.R, p.A };
        }
        double sx = Math.Clamp((x + 0.5) / 2 - 0.5, 0, img.Width - 1), sy = Math.Clamp((y + 0.5) / 2 - 0.5, 0, img.Height - 1);
        int xa = (int)Math.Floor(sx), ya = (int)Math.Floor(sy), xb = Math.Min(xa + 1, img.Width - 1), yb = Math.Min(ya + 1, img.Height - 1);
        double fx = sx - xa, fy = sy - ya;
        var p00 = img.GetPixel(xa, ya); var p10 = img.GetPixel(xb, ya); var p01 = img.GetPixel(xa, yb); var p11 = img.GetPixel(xb, yb);
        double L(byte q00, byte q10, byte q01, byte q11) => q00 * (1 - fx) * (1 - fy) + q10 * fx * (1 - fy) + q01 * (1 - fx) * fy + q11 * fx * fy;
        return new[] { L(p00.B, p10.B, p01.B, p11.B), L(p00.G, p10.G, p01.G, p11.G), L(p00.R, p10.R, p01.R, p11.R), L(p00.A, p10.A, p01.A, p11.A) };
    }

    [UnitTest]
    public static void Spotlight_optimized_blend_equals_the_reference_within_1()
    {
        var rng = new Random(20261024);
        int[] widths = { 1, 2, 37, 64, 101, 160 };
        int[] heights = { 1, 23, 48, 77 };
        int[] radii = { 0, 2, 5, 9, 16 };
        int[] falloffs = { 0, 1, 3, 17, 60, 500 };
        double[] dims = { 0, 0.55, 1 };
        for (int t = 0; t < 48; t++)
        {
            int w = widths[t % widths.Length], h = heights[(t / 2) % heights.Length];
            if (t < 6) { w = widths[t]; h = 23; }
            bool gradient = t % 3 != 0;
            int radius = radii[t % radii.Length];
            var img = Noise(w, h, 100 + t, randomAlpha: t % 4 == 1);
            var lv = Spotlight.Build(img, radius, gradient);
            RectI box = (t % 8) switch
            {
                0 => RectI.Empty,
                1 => new RectI(-10, -5, w / 2 + 10, h / 2 + 5),                // partly outside (top-left)
                2 => new RectI(w + 5, h + 3, 10, 10),                          // fully outside
                3 => new RectI(-5, -5, w + 10, h + 10),                        // covers the image
                4 => new RectI(w / 3, h / 3, 1, 1),                            // one pixel
                _ => new RectI(rng.Next(-20, w + 5), rng.Next(-20, h + 5), rng.Next(1, w + 20), rng.Next(1, h + 20)),
            };
            int falloff = falloffs[(t / 3) % falloffs.Length];
            double dim = dims[t % dims.Length];
            var dst = new BgraImage(w, h);
            Spotlight.Blend(img, dst, dst.Bounds, lv, box, dim, falloff);
            var reference = ReferenceBlend(img, lv, box, dim, falloff, img.Bounds);
            int diff = MaxDiff4(reference, dst, img.Bounds);
            Check.True(diff <= 1, $"case {t}: {w}x{h} radius {radius} {(gradient ? "gradient" : "fixed")} box {box} falloff {falloff} dim {dim}: max difference {diff}");
            if (!box.IsEmpty) Check.Equal(0, MaxDiff4(img, dst, box.Intersect(img.Bounds)), $"case {t}: inside the box is the source");
        }
    }

    [UnitTest]
    public static void Spotlight_large_images_keep_every_level_at_half_resolution()
    {
        var big = Noise(2000, 2000, 31);
        var lv = Spotlight.Build(big, 3, gradient: true);   // sigmas 0.75..3: full resolution below 4 MP
        Check.True(lv.Levels[0] == null && lv.Half.Skip(1).All(hh => hh), "4 MP: every blurred level at half resolution");
        Check.Equal(1000, lv.Levels[1]!.Width);
        Check.Equal(4L * 1000 * 1000 * 4, lv.Bytes, "4 half levels of 1000 x 1000");
        var box = new RectI(900, 950, 300, 120);
        var clip = new RectI(820, 900, 180, 120);
        var dst = new BgraImage(2000, 2000);
        Spotlight.Blend(big, dst, clip, lv, box, 0.4, 150);
        Check.True(MaxDiff4(ReferenceBlend(big, lv, box, 0.4, 150, clip), dst, clip) <= 1, "matches the reference");

        var small = Spotlight.Build(Noise(200, 100, 32), 3, gradient: true);
        Check.True(small.Half.All(hh => !hh), "below 4 MP small sigmas stay full resolution");
        var fixedBig = Spotlight.Build(big, 2, gradient: false);
        Check.True(fixedBig.Half[0], "a fixed level of a 4 MP image is half resolution");
    }

    [UnitTest]
    public static void Spotlight_clipped_blend_equals_the_full_blend_and_writes_nothing_else()
    {
        foreach (bool gradient in new[] { false, true })
        {
            var img = Noise(50, 40, 40);
            var lv = Spotlight.Build(img, 8, gradient);
            ClipContract($"spotlight {(gradient ? "gradient" : "fixed")}", img, (s, d, c) => Spotlight.Blend(s, d, c, lv, new RectI(12, 9, 20, 14), 0.55, 20), SomeClips);
        }
    }

    [UnitTest]
    public static void Spotlight_blend_rejects_aliasing_and_a_foreign_source()
    {
        var img = Noise(40, 30, 41);
        var lv = Spotlight.Build(img, 8, gradient: true);
        var box = new RectI(5, 5, 10, 10);
        Check.Throws<ArgumentException>(() => Spotlight.Blend(img, img, img.Bounds, lv, box, 0.5, 10), "dst = src");
        Check.Throws<ArgumentException>(() => Spotlight.Blend(img, lv.Levels[1]!, img.Bounds, lv, box, 0.5, 10), "dst = a level buffer (or the wrong size)");
        var full = Spotlight.Build(Noise(40, 30, 42), 2, gradient: false);   // a full-resolution level, the size of the image
        Check.False(full.Half[0]);
        Check.Throws<ArgumentException>(() => Spotlight.Blend(img, full.Levels[0]!, img.Bounds, full, box, 0.5, 10), "dst = a full-resolution level buffer");
        Check.Throws<ArgumentException>(() => Spotlight.Blend(Noise(41, 30, 1), new BgraImage(41, 30), RectI.Empty, lv, box, 0.5, 10), "src is not the levels' image");
        Check.Throws<ArgumentException>(() => Spotlight.Blend(img, new BgraImage(10, 10), img.Bounds, lv, box, 0.5, 10), "dst of another size");
    }

    [UnitTest]
    public static void Spotlight_inside_is_the_source_and_far_outside_is_the_dimmed_blur()
    {
        var img = Noise(120, 90, 5);
        var box = new RectI(40, 30, 30, 20);
        // radius 4 is a full-resolution level: comparable with a direct blur
        var levels = Spotlight.Build(img, 4, gradient: false);
        var dst = new BgraImage(120, 90);
        Spotlight.Blend(img, dst, dst.Bounds, levels, box, 0.5, 30);
        Check.Equal(0, MaxDiff(img, dst, box), "inside identical to the source");
        Check.Equal((byte)255, dst.GetPixel(box.X, box.Y).A);

        var blur = new BgraImage(120, 90);
        BoxBlur.Blur(img, blur, img.Bounds, img.Bounds, 4);
        int worst = 0;
        foreach (var (x, y) in new[] { (2, 2), (110, 80), (5, 60), (100, 10), (20, 70) })
        {
            var p = dst.GetPixel(x, y);
            var q = blur.GetPixel(x, y);
            double m = Math.Max(Math.Abs(p.B - q.B / 2.0), Math.Max(Math.Abs(p.G - q.G / 2.0), Math.Abs(p.R - q.R / 2.0)));
            worst = Math.Max(worst, (int)Math.Ceiling(m));
        }
        Check.True(worst <= 1, $"far outside equals blur * (1 - dim): worst {worst}");

        // the feather: a constant image makes the arithmetic visible
        var flat = Solid(60, 40, 200, 200, 200);
        var fl = Spotlight.Build(flat, 8, gradient: false);
        var o = new BgraImage(60, 40);
        Spotlight.Blend(flat, o, o.Bounds, fl, new RectI(20, 10, 20, 20), 0.5, 10);
        Check.Equal((byte)200, o.GetPixel(20, 10).B, "box edge pixel inside");
        Check.True(Math.Abs(o.GetPixel(19, 20).B - 150) <= 1, $"1 px outside: halfway, got {o.GetPixel(19, 20).B}");
        Check.True(Math.Abs(o.GetPixel(18, 20).B - 100) <= 1, $"2 px outside: full effect, got {o.GetPixel(18, 20).B}");
        Check.True(Math.Abs(o.GetPixel(2, 2).B - 100) <= 1, "far: 200 * 0.5");
        Check.Equal((byte)255, o.GetPixel(2, 2).A, "alpha is not dimmed");
    }

    [UnitTest]
    public static void Spotlight_gradient_blurs_more_with_distance_and_ends_at_the_top_level()
    {
        var img = Noise(260, 60, 9);
        var levels = Spotlight.Build(img, 12, gradient: true);
        Check.Equal(5, levels.Levels.Length);
        Check.True(levels.Levels[0] == null, "level 0 is the caller's image, not kept");
        Check.True(levels.Levels.Skip(1).All(l => l != null && !ReferenceEquals(l.Pixels, img.Pixels)), "the blurred levels are owned copies");
        Check.True(levels.Half[4] && !levels.Half[0], "the strong levels are half resolution");
        Check.True(levels.Opaque, "built from an opaque image");
        var box = new RectI(0, 0, 10, 60);   // a strip on the left: the distance is the x distance
        var dst = new BgraImage(260, 60);
        Spotlight.Blend(img, dst, dst.Bounds, levels, box, 0, 200);   // dim 0 isolates the blur
        double Sharp(int x0, int x1)
        {
            double s = 0; int n = 0;
            for (int y = 0; y < 60; y++)
                for (int x = x0; x < x1; x++) { s += Math.Abs(dst.GetPixel(x + 1, y).G - dst.GetPixel(x, y).G); n++; }
            return s / n;
        }
        double prev = double.MaxValue;
        var sharpness = new List<double>();
        for (int x = 10; x + 40 <= 250; x += 40)
        {
            double s = Sharp(x, x + 39);
            sharpness.Add(s);
            Check.True(s <= prev + 1.0, $"band at {x}: {s:0.0} after {prev:0.0}");
            prev = s;
        }
        Check.True(sharpness[0] > 3 * sharpness[^1], $"clearly sharper near the box ({sharpness[0]:0.0}) than far ({sharpness[^1]:0.0})");

        // far outside equals the radius-12 level (the last level), to within the box-blur approximation
        var smooth = Make(160, 120, (x, y) => (2 * x + y, 200 - x, 3 * y / 2 + 10));
        var sl = Spotlight.Build(smooth, 4, gradient: true);
        var o = new BgraImage(160, 120);
        Spotlight.Blend(smooth, o, o.Bounds, sl, new RectI(70, 50, 20, 20), 0.4, 30);
        var direct = new BgraImage(160, 120);
        BoxBlur.Blur(smooth, direct, smooth.Bounds, smooth.Bounds, 4);
        foreach (var (x, y) in new[] { (25, 25), (130, 90), (25, 100) })
        {
            var p = o.GetPixel(x, y);
            var q = direct.GetPixel(x, y);
            Check.True(Math.Abs(p.B - q.B * 0.6) <= 2 && Math.Abs(p.G - q.G * 0.6) <= 2 && Math.Abs(p.R - q.R * 0.6) <= 2, $"far outside at {x},{y}: {p.B},{p.G},{p.R} vs {q.B * 0.6:0},{q.G * 0.6:0},{q.R * 0.6:0}");
        }
    }

    [UnitTest]
    public static void Spotlight_levels_do_not_depend_on_the_box()
    {
        var img = Noise(100, 80, 11);
        var levels = Spotlight.Build(img, 10, gradient: true);
        long Hash()
        {
            long h = 17;
            foreach (var lv in levels.Levels) if (lv != null) foreach (byte b in lv.Pixels) h = h * 31 + b;
            return h;
        }
        long before = Hash();
        var a = new BgraImage(100, 80);
        var b = new BgraImage(100, 80);
        Spotlight.Blend(img, a, a.Bounds, levels, new RectI(10, 10, 20, 20), 0.5, 25);
        Spotlight.Blend(img, b, b.Bounds, levels, new RectI(60, 40, 30, 30), 0.5, 25);
        Check.Equal(before, Hash(), "blending changes no level");
        Check.False(Same(a, b), "different boxes, different pictures");
        Check.Equal(0, MaxDiff(img, a, new RectI(10, 10, 20, 20)), "box 1 untouched in picture 1");
        Check.Equal(0, MaxDiff(img, b, new RectI(60, 40, 30, 30)), "box 2 untouched in picture 2");

        // blending twice with the same box is the same picture; a rebuild gives the same levels
        var a2 = new BgraImage(100, 80);
        Spotlight.Blend(img, a2, a2.Bounds, levels, new RectI(10, 10, 20, 20), 0.5, 25);
        Check.True(Same(a, a2), "deterministic");
        var again = Spotlight.Build(img, 10, gradient: true);
        for (int i = 1; i < 5; i++) Check.True(Same(levels.Levels[i]!, again.Levels[i]!), $"level {i} rebuilt identically");

        // changing the caller's image after the build does not change the levels (they are copies)
        var copy = img.Clone();
        var lc = Spotlight.Build(copy, 10, gradient: true);
        long h0 = 17;
        foreach (var lv in lc.Levels) if (lv != null) foreach (byte x in lv.Pixels) h0 = h0 * 31 + x;
        Array.Clear(copy.Pixels);
        long h1 = 17;
        foreach (var lv in lc.Levels) if (lv != null) foreach (byte x in lv.Pixels) h1 = h1 * 31 + x;
        Check.Equal(h0, h1, "levels are not views of the source");

        // an empty box: everything is outside
        var all = new BgraImage(100, 80);
        Spotlight.Blend(img, all, all.Bounds, levels, RectI.Empty, 0.5, 25);
        Check.True(all.GetPixel(50, 40).G <= levels.Levels[4]!.GetPixel(25, 20).G, "dimmed everywhere");
    }

    // ------------------------------------------------------------------ magnifier

    [UnitTest]
    public static void Magnifier_nearest_2x_maps_pixels_exactly()
    {
        var below = Make(16, 16, (x, y) => (x * 10, y * 10, x + y));
        var dst = Solid(40, 40, 1, 2, 3);
        var before = dst.Clone();
        var dest = new RectI(10, 12, 16, 12);
        Magnifier.Render(below, dst, dst.Bounds, new RectI(3, 4, 8, 6), dest, smooth: false, Colors.White, 0);
        for (int dy = 0; dy < dest.Height; dy++)
            for (int dx = 0; dx < dest.Width; dx++)
                Check.Equal(below.GetPixel(3 + dx / 2, 4 + dy / 2), dst.GetPixel(dest.X + dx, dest.Y + dy), $"at {dx},{dy}");
        Check.True(OutsideUnchanged(before, dst, dest), "nothing outside dest");

        // dest partly outside dst: only the visible part, no throw
        Magnifier.Render(below, dst, dst.Bounds, new RectI(0, 0, 8, 8), new RectI(30, 30, 16, 16), false, Colors.White, 0);
        Check.Equal(below.GetPixel(0, 0), dst.GetPixel(30, 30));
        Check.Equal(below.GetPixel(4, 4), dst.GetPixel(38, 38));
        Check.Throws<ArgumentException>(() => Magnifier.Render(below, below, below.Bounds, new RectI(0, 0, 4, 4), new RectI(8, 8, 8, 8), false, Colors.White, 0), "dst = src");
    }

    [UnitTest]
    public static void Magnifier_source_partly_outside_repeats_the_edge_pixels()
    {
        var below = Make(16, 16, (x, y) => (x * 10, y * 10, x + y));
        var dst = Solid(40, 40, 1, 2, 3);
        var dest = new RectI(4, 4, 16, 16);
        Magnifier.Render(below, dst, dst.Bounds, new RectI(-4, -4, 8, 8), dest, false, Colors.White, 0);
        for (int dy = 0; dy < 16; dy++)
            for (int dx = 0; dx < 16; dx++)
            {
                int sx = Math.Clamp(-4 + dx / 2, 0, 15), sy = Math.Clamp(-4 + dy / 2, 0, 15);
                Check.Equal(below.GetPixel(sx, sy), dst.GetPixel(dest.X + dx, dest.Y + dy), $"at {dx},{dy}");
            }
        var sm = Solid(40, 40, 1, 2, 3);
        Magnifier.Render(below, sm, sm.Bounds, new RectI(12, 12, 8, 8), dest, true, Colors.White, 0);   // past the right / bottom edge
        Check.Equal(below.GetPixel(15, 15), sm.GetPixel(dest.Right - 1, dest.Bottom - 1), "smooth: clamped to the last pixel");
    }

    [UnitTest]
    public static void Magnifier_draws_the_outline_inside_dest_and_smooth_interpolates()
    {
        var below = Make(32, 32, (x, y) => (x * 8, y * 8, 100));
        var dst = Solid(60, 60, 0, 0, 0);
        var dest = new RectI(10, 10, 30, 30);
        Magnifier.Render(below, dst, dst.Bounds, new RectI(0, 0, 15, 15), dest, false, Colors.White, 3);
        Check.Equal(((byte)0x20, (byte)0x20, (byte)0x20), (dst.GetPixel(10, 10).B, dst.GetPixel(10, 10).G, dst.GetPixel(10, 10).R), "outermost pixel: dark ring");
        Check.Equal(((byte)0x20, (byte)0x20, (byte)0x20), (dst.GetPixel(25, 39).B, dst.GetPixel(25, 39).G, dst.GetPixel(25, 39).R), "bottom edge dark");
        foreach (int k in new[] { 1, 2, 3 })
        {
            Check.Equal((byte)255, dst.GetPixel(10 + k, 25).B, $"left outline pixel {k}");
            Check.Equal((byte)255, dst.GetPixel(25, 10 + k).G, $"top outline pixel {k}");
            Check.Equal((byte)255, dst.GetPixel(39 - k, 25).R, $"right outline pixel {k}");
        }
        Check.Equal(below.GetPixel(2, 7), dst.GetPixel(14, 25 - 0), "inside the frame: the magnified pixel (x 14 -> 2, y 25 -> 7)");
        Check.Equal((byte)0, dst.GetPixel(9, 10).B, "the frame does not reach outside dest");
        Check.Equal((byte)0, dst.GetPixel(40, 25).B);

        var flat = Solid(20, 20, 90, 120, 30);
        var sm = Solid(40, 40, 0, 0, 0);
        Magnifier.Render(flat, sm, sm.Bounds, new RectI(2, 2, 10, 10), new RectI(5, 5, 25, 25), smooth: true, Colors.White, 0);
        Check.Equal((90, 120, 30), (sm.GetPixel(17, 17).B, sm.GetPixel(17, 17).G, sm.GetPixel(17, 17).R), "smooth of a flat area is flat");
        var ramp = Make(10, 1, (x, y) => (x * 25, 0, 0));
        var rd = Solid(40, 4, 0, 0, 0);
        Magnifier.Render(ramp, rd, rd.Bounds, new RectI(0, 0, 10, 1), new RectI(0, 0, 40, 4), true, Colors.White, 0);
        for (int x = 1; x < 40; x++) Check.True(rd.GetPixel(x, 1).B >= rd.GetPixel(x - 1, 1).B, $"smooth ramp is monotone at {x}");
        Check.True(rd.GetPixel(10, 1).B != rd.GetPixel(11, 1).B || rd.GetPixel(11, 1).B != rd.GetPixel(12, 1).B, "interpolated, not stepped");
    }

    [UnitTest]
    public static void Magnifier_respects_the_clip_contract()
    {
        var below = Noise(30, 30, 50);
        var outline = Color.FromArgb(160, 255, 255, 255);
        ClipContract("magnifier", new BgraImage(50, 40), (s, d, c) => Magnifier.Render(below, d, c, new RectI(5, 5, 12, 9), new RectI(8, 6, 36, 27), false, outline, 3), SomeClips);
        ClipContract("magnifier smooth", new BgraImage(50, 40), (s, d, c) => Magnifier.Render(below, d, c, new RectI(-3, 20, 15, 15), new RectI(-10, 2, 45, 45), true, outline, 2), SomeClips);
    }

    // ------------------------------------------------------------------ adjustments

    [UnitTest]
    public static void Adjustments_lut_identity_monotone_and_directional()
    {
        var id = Adjustments.BuildLut(0, 0);
        for (int i = 0; i < 256; i++) Check.Equal((byte)i, id[i], $"identity at {i}");
        foreach (var (b, c) in new[] { (0, 100), (0, -100), (50, 30), (-80, 60), (100, 100), (-100, -100) })
        {
            var lut = Adjustments.BuildLut(b, c);
            for (int i = 1; i < 256; i++) Check.True(lut[i] >= lut[i - 1], $"monotone ({b},{c}) at {i}");
        }
        var bright = Adjustments.BuildLut(40, 0);
        Check.True(bright[100] > 100 && bright[0] > 0, "brightness lifts");
        Check.Equal((byte)(100 + (int)Math.Round(40 * 2.55)), bright[100], "offset b * 2.55");
        var contrast = Adjustments.BuildLut(0, 50);
        Check.True(contrast[200] > 200 && contrast[50] < 50, "contrast spreads around mid-grey");
        Check.True(Math.Abs(contrast[128] - 128) <= 1, "mid-grey stays");
        Check.True(Adjustments.BuildLut(0, -100)[255] < 255 - 100, "minimum contrast pulls the white in a long way");
    }

    [UnitTest]
    public static void Adjustments_apply_maps_colour_channels_but_not_alpha()
    {
        var img = new BgraImage(2, 1);
        img.SetPixel(0, 0, 10, 20, 30, 77);
        img.SetPixel(1, 0, 200, 210, 220, 255);
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)(255 - i);
        var dst = new BgraImage(2, 1);
        Adjustments.Apply(img, dst, dst.Bounds, lut);
        Check.Equal(((byte)245, (byte)235, (byte)225, (byte)77), dst.GetPixel(0, 0));
        Adjustments.Apply(img, img, img.Bounds, lut);   // in place
        Check.Equal(dst.GetPixel(1, 0), img.GetPixel(1, 0));
        Check.Throws<ArgumentException>(() => Adjustments.Apply(img, new BgraImage(3, 1), img.Bounds, lut));
        ClipContract("lut", Noise(50, 40, 60, randomAlpha: true), (s, d, c) => Adjustments.Apply(s, d, c, lut), SomeClips);
    }

    [UnitTest]
    public static void Adjustments_unsharp_keeps_flat_areas_and_raises_edge_contrast()
    {
        var flat = Solid(30, 20, 40, 120, 200);
        var o = new BgraImage(30, 20);
        Adjustments.Unsharp(flat, o, o.Bounds, 1.5);
        Check.True(Same(flat, o), "constant image stays constant");

        var edge = Make(40, 10, (x, y) => x < 20 ? (80, 80, 80) : (160, 160, 160));
        var sharp = new BgraImage(40, 10);
        Adjustments.Unsharp(edge, sharp, sharp.Bounds, 1.0);
        Check.True(sharp.GetPixel(19, 5).B < 80, $"dark side undershoots: {sharp.GetPixel(19, 5).B}");
        Check.True(sharp.GetPixel(20, 5).B > 160, $"bright side overshoots: {sharp.GetPixel(20, 5).B}");
        Check.Equal((byte)80, sharp.GetPixel(2, 5).B, "far from the edge unchanged");
        Check.True(sharp.GetPixel(20, 5).B - sharp.GetPixel(19, 5).B > 80, "edge contrast increased");

        var zero = new BgraImage(40, 10);
        Adjustments.Unsharp(edge, zero, zero.Bounds, 0);
        Check.True(Same(edge, zero), "amount 0 copies");
        ClipContract("unsharp", Noise(50, 40, 61), (s, d, c) => Adjustments.Unsharp(s, d, c, 1.3), SomeClips);
        var inPlace = edge.Clone();
        Adjustments.Unsharp(inPlace, inPlace, inPlace.Bounds, 1.0);
        Check.True(Same(sharp, inPlace), "in place gives the same result");
    }

    [UnitTest]
    public static void Adjustments_unsharp_clamps_a_black_white_edge_at_amount_2()
    {
        var edge = Make(40, 10, (x, y) => x < 20 ? (0, 0, 0) : (255, 255, 255));
        var o = new BgraImage(40, 10);
        Adjustments.Unsharp(edge, o, o.Bounds, 2.0);   // Sharpness 200 %
        for (int x = 0; x < 40; x++)
        {
            var p = o.GetPixel(x, 5);
            byte expected = x < 20 ? (byte)0 : (byte)255;
            Check.Equal((expected, expected, expected, (byte)255), (p.B, p.G, p.R, p.A), $"x {x}: clamped, not wrapped");
        }
    }

    // ------------------------------------------------------------------ resample

    [UnitTest]
    public static void Resample_identity_copies_and_downscales_average()
    {
        var src = Noise(20, 12, 4);
        var dst = new BgraImage(30, 20);
        Resample.Bilinear(src, dst, dst.Bounds, src.Bounds, new RectI(5, 3, 20, 12));
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 20; x++) Check.Equal(src.GetPixel(x, y), dst.GetPixel(5 + x, 3 + y), $"copy at {x},{y}");
        Check.Equal((byte)0, dst.GetPixel(0, 0).A, "outside dstRect untouched");

        var checker = Make(16, 16, (x, y) => (x + y) % 2 == 0 ? (0, 0, 0) : (255, 255, 255));
        var half = new BgraImage(8, 8);
        Resample.Bilinear(checker, half, half.Bounds, checker.Bounds, half.Bounds);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Check.True(Math.Abs(half.GetPixel(x, y).G - 127.5) <= 1, $"2x checkerboard at {x},{y}: {half.GetPixel(x, y).G}");

        var big = Make(32, 32, (x, y) => (x + y) % 2 == 0 ? (0, 0, 0) : (255, 255, 255));
        var quarter = new BgraImage(8, 8);
        Resample.Bilinear(big, quarter, quarter.Bounds, big.Bounds, quarter.Bounds);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Check.True(Math.Abs(quarter.GetPixel(x, y).G - 127.5) <= 1, $"4x checkerboard (box prefilter) at {x},{y}: {quarter.GetPixel(x, y).G}");

        var ramp = Make(2, 1, (x, y) => (x * 255, x * 255, x * 255));
        var up = new BgraImage(8, 1);
        Resample.Bilinear(ramp, up, up.Bounds, ramp.Bounds, up.Bounds);
        for (int x = 1; x < 8; x++) Check.True(up.GetPixel(x, 0).B >= up.GetPixel(x - 1, 0).B, $"upscaled ramp monotone at {x}");
        Check.Equal((byte)0, up.GetPixel(0, 0).B);
        Check.Equal((byte)255, up.GetPixel(7, 0).B);
        Check.Throws<ArgumentException>(() => Resample.Bilinear(src, src, src.Bounds, src.Bounds, new RectI(0, 0, 5, 5)), "dst = src");
    }

    [UnitTest]
    public static void Resample_non_integer_shrink_keeps_the_geometry()
    {
        // 3.5x: a box reduction by 3 (the last block of each axis is partial) then a bilinear 1.17x; a linear image must come out
        // as the linear function at each destination pixel's mapped centre
        var src = Make(70, 14, (x, y) => (2 * x + 10, 4 * y + 10, 100));
        var dst = new BgraImage(20, 4);
        Resample.Bilinear(src, dst, dst.Bounds, src.Bounds, dst.Bounds);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 20; x++)
            {
                double sx = (x + 0.5) * 3.5 - 0.5, sy = (y + 0.5) * 3.5 - 0.5;
                var p = dst.GetPixel(x, y);
                Check.True(Math.Abs(p.B - (2 * sx + 10)) <= 1, $"x {x}: B {p.B} vs {2 * sx + 10:0.00}");
                Check.True(Math.Abs(p.G - (4 * sy + 10)) <= 1, $"y {y}: G {p.G} vs {4 * sy + 10:0.00}");
            }
    }

    [UnitTest]
    public static void Resample_source_partly_outside_maps_both_paths_by_the_unclipped_rectangle()
    {
        var src = Make(30, 20, (x, y) => (3 * x + 20, 2 * y + 30, 50));
        var srcRect = new RectI(-10, 0, 40, 20);   // the left 10 columns are outside the image
        foreach (var (dw, dh) in new[] { (20, 10), (10, 5), (30, 15) })   // 2x (direct), 4x (box reduction), 1.33x
        {
            var dst = new BgraImage(dw, dh);
            Resample.Bilinear(src, dst, dst.Bounds, srcRect, dst.Bounds);
            double kx = 40.0 / dw, ky = 20.0 / dh;
            int fx = kx > 2 ? (int)kx : 1;
            double first = (fx - 1) / 2.0, last = 29 - (fx - 1) / 2.0;   // the outermost sample centres the image has
            for (int x = 0; x < dw; x++)
            {
                double sx = -10 + (x + 0.5) * kx - 0.5;
                if (sx < first || sx > last) continue;
                var p = dst.GetPixel(x, dh / 2);
                Check.True(Math.Abs(p.B - (3 * sx + 20)) <= 1, $"{dw}x{dh} x {x}: B {p.B} vs {3 * sx + 20:0.00}");
            }
            // the part mapped outside clamps to the first sample the image has (column 0, or the mean of the first block)
            Check.True(Math.Abs(dst.GetPixel(0, 0).B - (3 * first + 20)) <= 1, $"{dw}x{dh}: the part mapped outside clamps to the left edge ({dst.GetPixel(0, 0).B})");
            var clipped = new BgraImage(dw, dh);
            var clip = new RectI(dw / 3, 1, dw / 2, dh / 2);
            Resample.Bilinear(src, clipped, clip, srcRect, clipped.Bounds);
            Check.Equal(0, MaxDiff4(dst, clipped, clip), $"{dw}x{dh}: clipped equals unclipped");
        }
    }

    [UnitTest]
    public static void Resample_transparent_pixels_do_not_bleed_colour()
    {
        var src = new BgraImage(2, 1);
        src.SetPixel(0, 0, 255, 0, 0, 255);      // opaque blue
        src.SetPixel(1, 0, 0, 255, 0, 0);        // transparent green
        var dst = new BgraImage(4, 1);
        Resample.Bilinear(src, dst, dst.Bounds, src.Bounds, dst.Bounds);
        var p = dst.GetPixel(1, 0);              // between the two: half transparent, still blue
        Check.True(p.A > 0 && p.A < 255, $"partly transparent: {p.A}");
        Check.True(p.B > 240 && p.G < 15, $"colour is the opaque pixel's: {p.B},{p.G}");
    }

    [UnitTest]
    public static void Resample_respects_the_clip_contract()
    {
        var pic = Noise(23, 17, 70, randomAlpha: true);
        ClipContract("bilinear up", new BgraImage(50, 40), (s, d, c) => Resample.Bilinear(pic, d, c, pic.Bounds, new RectI(-4, 3, 60, 33)), SomeClips);
        ClipContract("bilinear down", new BgraImage(50, 40), (s, d, c) => Resample.Bilinear(Noise(200, 150, 71), d, c, new RectI(10, -20, 180, 160), new RectI(2, 2, 45, 35)), SomeClips);
        ClipContract("over", new BgraImage(50, 40), (s, d, c) => Resample.Over(pic, d, c, new RectI(3, -5, 40, 50), 0.7), SomeClips);
    }

    [UnitTest]
    public static void Resample_over_respects_opacity_and_alpha()
    {
        var dst = Solid(10, 10, 0, 0, 0);
        var white = Solid(4, 4, 255, 255, 255);
        var before = dst.Clone();
        Resample.Over(white, dst, dst.Bounds, new RectI(2, 2, 4, 4), 0);
        Check.True(Same(before, dst), "opacity 0 changes nothing");
        Resample.Over(white, dst, dst.Bounds, new RectI(2, 2, 4, 4), 1);
        Check.Equal(((byte)255, (byte)255, (byte)255, (byte)255), dst.GetPixel(3, 3), "opacity 1 replaces");
        Check.Equal((byte)0, dst.GetPixel(1, 1).B, "outside dest");

        var half = Solid(10, 10, 0, 0, 0);
        Resample.Over(white, half, half.Bounds, new RectI(0, 0, 4, 4), 0.5);
        Check.True(Math.Abs(half.GetPixel(1, 1).B - 128) <= 1, $"opacity 0.5: {half.GetPixel(1, 1).B}");
        Check.Equal((byte)255, half.GetPixel(1, 1).A);

        var clear = new BgraImage(4, 4);   // fully transparent source
        var d2 = Solid(10, 10, 9, 9, 9);
        Resample.Over(clear, d2, d2.Bounds, new RectI(0, 0, 4, 4), 1);
        Check.Equal(((byte)9, (byte)9, (byte)9, (byte)255), d2.GetPixel(1, 1), "transparent source leaves the destination");

        var scaled = Solid(10, 10, 0, 0, 0);
        Resample.Over(white, scaled, scaled.Bounds, new RectI(-4, -4, 12, 12), 1);   // scaled 3x, partly outside
        Check.Equal((byte)255, scaled.GetPixel(0, 0).B);
        Check.Equal((byte)255, scaled.GetPixel(7, 7).B);
        Check.Equal((byte)0, scaled.GetPixel(8, 8).B);

        var onClear = new BgraImage(4, 4);
        Resample.Over(Solid(2, 2, 10, 20, 30, 128), onClear, onClear.Bounds, new RectI(0, 0, 2, 2), 1);
        Check.Equal((byte)128, onClear.GetPixel(0, 0).A, "over transparent: the source alpha");
        Check.Equal(((byte)10, (byte)20, (byte)30), (onClear.GetPixel(0, 0).B, onClear.GetPixel(0, 0).G, onClear.GetPixel(0, 0).R));
        Check.Throws<ArgumentException>(() => Resample.Over(dst, dst, dst.Bounds, new RectI(0, 0, 4, 4), 1), "dst = src");
    }

    // ------------------------------------------------------------------ logging

    [UnitTest]
    public static void Imaging_log_lines_are_rate_limited_per_operation()
    {
        string op = "test-op-" + Guid.NewGuid().ToString("N");
        long t = Stopwatch.GetTimestamp(), sec = Stopwatch.Frequency;
        Check.True(ImagingUtil.TakeLogSlot(op, t, out int s0) && s0 == 0, "the first line goes through");
        Check.False(ImagingUtil.TakeLogSlot(op, t + sec, out _), "1 s later: suppressed");
        Check.False(ImagingUtil.TakeLogSlot(op, t + 9 * sec, out _), "9 s later: suppressed");
        Check.True(ImagingUtil.TakeLogSlot("other-" + op, t + sec, out _), "another operation has its own slot");
        Check.True(ImagingUtil.TakeLogSlot(op, t + 11 * sec, out int s1), "after 10 s: through again");
        Check.Equal(2, s1, "with the count of the suppressed lines");
        Check.False(ImagingUtil.TakeLogSlot(op, t + 12 * sec, out _));
    }

    // ------------------------------------------------------------------ performance smoke (logs the times; bounds x3 in Debug)

    [UnitTest]
    public static void Perf_4k_spotlight_levels_blend_and_blur()
    {
#if DEBUG
        const int Slack = 3;
#else
        const int Slack = 1;
#endif
        var img = new BgraImage(3840, 2160);
        var px = img.Pixels;
        for (int y = 0; y < 2160; y++)
            for (int x = 0; x < 3840; x++)
            {
                int i = (y * 3840 + x) * 4;
                px[i] = (byte)x; px[i + 1] = (byte)y; px[i + 2] = (byte)(x + y); px[i + 3] = 255;
            }
        var sw = Stopwatch.StartNew();
        var levels = Spotlight.Build(img, 40, gradient: true);
        long buildMs = sw.ElapsedMilliseconds;
        var dst = new BgraImage(3840, 2160);
        sw.Restart();
        Spotlight.Blend(img, dst, dst.Bounds, levels, new RectI(1200, 600, 1400, 800), 0.55, 756);
        double blendMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        Spotlight.Blend(img, dst, dst.Bounds, levels, new RectI(1300, 650, 1400, 800), 0.55, 756);
        double blend2Ms = sw.Elapsed.TotalMilliseconds;
        // best of a few more, so one scheduling hiccup does not decide the Release bound
        double blendBest = blend2Ms;
        for (int k = 0; k < 4; k++)
        {
            sw.Restart();
            Spotlight.Blend(img, dst, dst.Bounds, levels, new RectI(1250 + 20 * k, 640, 1400, 800), 0.55, 756);
            blendBest = Math.Min(blendBest, sw.Elapsed.TotalMilliseconds);
        }

        var blurred = new BgraImage(3840, 2160);
        sw.Restart();
        BoxBlur.Blur(img, blurred, img.Bounds, img.Bounds, 20);
        long blurMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var u = new BgraImage(3840, 2160);
        Adjustments.Unsharp(img, u, u.Bounds, 1.0);
        long unsharpMs = sw.ElapsedMilliseconds;
        sw.Restart();
        Adjustments.Apply(img, u, u.Bounds, Adjustments.BuildLut(20, 20));
        long lutMs = sw.ElapsedMilliseconds;

        var noisy = Noise(1000, 800, 77);
        sw.Restart();
        SmartErase.Fill(noisy, new RectI(100, 100, 800, 600));
        long eraseMs = sw.ElapsedMilliseconds;

        Log.Info($"perf 4K: gradient levels build {buildMs} ms, blend {blendMs:0.0} ms (second {blend2Ms:0.0} ms, best of 5 {blendBest:0.0} ms), blur sigma 20 {blurMs} ms, unsharp {unsharpMs} ms, lut {lutMs} ms, smart erase 800x600 {eraseMs} ms, cores {Environment.ProcessorCount}");
        Check.True(buildMs + blendMs < 1500 * Slack, $"levels build + blend {buildMs} + {blendMs:0} ms");
        Check.True(blurMs < 1000 * Slack, $"blur {blurMs} ms");
        Check.True(unsharpMs < 1000 * Slack, $"unsharp {unsharpMs} ms");
        Check.True(lutMs < 300 * Slack, $"lut {lutMs} ms");
        Check.True(eraseMs < 200 * Slack, $"smart erase 800x600 {eraseMs} ms");
#if !DEBUG
        Check.True(blendBest < 100, $"Release blend {blendBest:0.0} ms (budget 15-25 ms; generous bound for a loaded machine)");
#endif
    }
}
