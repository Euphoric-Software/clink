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

    /// <summary>Deterministic pseudo-random opaque pixels.</summary>
    private static BgraImage Noise(int w, int h, int seed)
    {
        var img = new BgraImage(w, h);
        uint s = (uint)seed * 2654435761u + 12345u;
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            s = s * 1664525u + 1013904223u;
            img.Pixels[i] = (byte)(s >> 24);
        }
        img.ForceOpaque();
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
        BoxBlur.Blur(img, img.Bounds, 3.0, dst);
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
        BoxBlur.Blur(img, img.Bounds, 3.5, whole);

        var region = new RectI(20, 15, 25, 20);
        var dst = img.Clone();
        BoxBlur.Blur(img, region, 3.5, dst);
        Check.True(OutsideUnchanged(img, dst, region), "nothing written outside the region");
        Check.Equal(0, MaxDiff(whole, dst, region), "region equals the same pixels of the whole-image blur (the margin is read)");
        Check.True(MaxDiff(img, dst, region) > 10, "the region really was blurred");

        var inPlace = img.Clone();
        BoxBlur.Blur(inPlace, region, 3.5);
        Check.True(Same(dst, inPlace), "in-place overload gives the same result");

        // a region at the image edge uses edge clamping
        var edge = img.Clone();
        BoxBlur.Blur(img, new RectI(0, 0, 10, 10), 3.5, edge);
        Check.Equal(0, MaxDiff(whole, edge, new RectI(0, 0, 10, 10)), "corner region equals whole-image blur");
        BoxBlur.Blur(img, new RectI(-20, -20, 5, 5), 3.5, edge);   // outside: no-op, no throw
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

    // ------------------------------------------------------------------ smart erase

    private static BgraImage LinearImage(int w, int h) =>
        Make(w, h, (x, y) => (20 + 2 * x + y, 200 - x - 2 * y, 100 + x - y));

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

    [UnitTest]
    public static void Spotlight_inside_is_the_source_and_far_outside_is_the_dimmed_blur()
    {
        var img = Noise(120, 90, 5);
        var box = new RectI(40, 30, 30, 20);
        // radius 4 is a full-resolution level: comparable with a direct blur
        var levels = Spotlight.Build(img, 4, gradient: false);
        var dst = new BgraImage(120, 90);
        Spotlight.Blend(levels, box, 0.5, 30, dst);
        Check.Equal(0, MaxDiff(img, dst, box), "inside identical to the source");
        Check.Equal((byte)255, dst.GetPixel(box.X, box.Y).A);

        var blur = new BgraImage(120, 90);
        BoxBlur.Blur(img, img.Bounds, 4, blur);
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
        Spotlight.Blend(fl, new RectI(20, 10, 20, 20), 0.5, 10, o);
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
        Check.True(ReferenceEquals(levels.Levels[0], img), "level 0 is the image");
        Check.True(levels.Half[4] && !levels.Half[0], "the strong levels are half resolution");
        var box = new RectI(0, 0, 10, 60);   // a strip on the left: the distance is the x distance
        var dst = new BgraImage(260, 60);
        Spotlight.Blend(levels, box, 0, 200, dst);   // dim 0 isolates the blur
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
        Spotlight.Blend(sl, new RectI(70, 50, 20, 20), 0.4, 30, o);
        var direct = new BgraImage(160, 120);
        BoxBlur.Blur(smooth, smooth.Bounds, 4, direct);
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
            foreach (var lv in levels.Levels) foreach (byte b in lv.Pixels) h = h * 31 + b;
            return h;
        }
        long before = Hash();
        var a = new BgraImage(100, 80);
        var b = new BgraImage(100, 80);
        Spotlight.Blend(levels, new RectI(10, 10, 20, 20), 0.5, 25, a);
        Spotlight.Blend(levels, new RectI(60, 40, 30, 30), 0.5, 25, b);
        Check.Equal(before, Hash(), "blending changes no level");
        Check.False(Same(a, b), "different boxes, different pictures");
        Check.Equal(0, MaxDiff(img, a, new RectI(10, 10, 20, 20)), "box 1 untouched in picture 1");
        Check.Equal(0, MaxDiff(img, b, new RectI(60, 40, 30, 30)), "box 2 untouched in picture 2");

        // blending twice with the same box is the same picture; a rebuild gives the same levels
        var a2 = new BgraImage(100, 80);
        Spotlight.Blend(levels, new RectI(10, 10, 20, 20), 0.5, 25, a2);
        Check.True(Same(a, a2), "deterministic");
        var again = Spotlight.Build(img, 10, gradient: true);
        for (int i = 0; i < 5; i++) Check.True(Same(levels.Levels[i], again.Levels[i]), $"level {i} rebuilt identically");

        Check.Throws<ArgumentException>(() => Spotlight.Blend(levels, new RectI(0, 0, 5, 5), 0.5, 10, new BgraImage(10, 10)));
        // an empty box: everything is outside
        var all = new BgraImage(100, 80);
        Spotlight.Blend(levels, RectI.Empty, 0.5, 25, all);
        Check.True(all.GetPixel(50, 40).G <= levels.Levels[4].GetPixel(25, 20).G, "dimmed everywhere");
    }

    // ------------------------------------------------------------------ magnifier

    [UnitTest]
    public static void Magnifier_nearest_2x_maps_pixels_exactly()
    {
        var below = Make(16, 16, (x, y) => (x * 10, y * 10, x + y));
        var dst = Solid(40, 40, 1, 2, 3);
        var before = dst.Clone();
        var dest = new RectI(10, 12, 16, 12);
        Magnifier.Render(below, new RectI(3, 4, 8, 6), dest, smooth: false, Colors.White, 0, dst);
        for (int dy = 0; dy < dest.Height; dy++)
            for (int dx = 0; dx < dest.Width; dx++)
                Check.Equal(below.GetPixel(3 + dx / 2, 4 + dy / 2), dst.GetPixel(dest.X + dx, dest.Y + dy), $"at {dx},{dy}");
        Check.True(OutsideUnchanged(before, dst, dest), "nothing outside dest");

        // dest partly outside dst: only the visible part, no throw
        Magnifier.Render(below, new RectI(0, 0, 8, 8), new RectI(30, 30, 16, 16), false, Colors.White, 0, dst);
        Check.Equal(below.GetPixel(0, 0), dst.GetPixel(30, 30));
        Check.Equal(below.GetPixel(4, 4), dst.GetPixel(38, 38));
    }

    [UnitTest]
    public static void Magnifier_draws_the_outline_inside_dest_and_smooth_interpolates()
    {
        var below = Make(32, 32, (x, y) => (x * 8, y * 8, 100));
        var dst = Solid(60, 60, 0, 0, 0);
        var dest = new RectI(10, 10, 30, 30);
        Magnifier.Render(below, new RectI(0, 0, 15, 15), dest, false, Colors.White, 3, dst);
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
        Magnifier.Render(flat, new RectI(2, 2, 10, 10), new RectI(5, 5, 25, 25), smooth: true, Colors.White, 0, sm);
        Check.Equal((90, 120, 30), (sm.GetPixel(17, 17).B, sm.GetPixel(17, 17).G, sm.GetPixel(17, 17).R), "smooth of a flat area is flat");
        var ramp = Make(10, 1, (x, y) => (x * 25, 0, 0));
        var rd = Solid(40, 4, 0, 0, 0);
        Magnifier.Render(ramp, new RectI(0, 0, 10, 1), new RectI(0, 0, 40, 4), true, Colors.White, 0, rd);
        for (int x = 1; x < 40; x++) Check.True(rd.GetPixel(x, 1).B >= rd.GetPixel(x - 1, 1).B, $"smooth ramp is monotone at {x}");
        Check.True(rd.GetPixel(10, 1).B != rd.GetPixel(11, 1).B || rd.GetPixel(11, 1).B != rd.GetPixel(12, 1).B, "interpolated, not stepped");
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
        Adjustments.Apply(img, dst, lut);
        Check.Equal(((byte)245, (byte)235, (byte)225, (byte)77), dst.GetPixel(0, 0));
        Adjustments.Apply(img, img, lut);   // in place
        Check.Equal(dst.GetPixel(1, 0), img.GetPixel(1, 0));
        Check.Throws<ArgumentException>(() => Adjustments.Apply(img, new BgraImage(3, 1), lut));
    }

    [UnitTest]
    public static void Adjustments_unsharp_keeps_flat_areas_and_raises_edge_contrast()
    {
        var flat = Solid(30, 20, 40, 120, 200);
        var o = new BgraImage(30, 20);
        Adjustments.Unsharp(flat, o, 1.5);
        Check.True(Same(flat, o), "constant image stays constant");

        var edge = Make(40, 10, (x, y) => x < 20 ? (80, 80, 80) : (160, 160, 160));
        var sharp = new BgraImage(40, 10);
        Adjustments.Unsharp(edge, sharp, 1.0);
        Check.True(sharp.GetPixel(19, 5).B < 80, $"dark side undershoots: {sharp.GetPixel(19, 5).B}");
        Check.True(sharp.GetPixel(20, 5).B > 160, $"bright side overshoots: {sharp.GetPixel(20, 5).B}");
        Check.Equal((byte)80, sharp.GetPixel(2, 5).B, "far from the edge unchanged");
        Check.True(sharp.GetPixel(20, 5).B - sharp.GetPixel(19, 5).B > 80, "edge contrast increased");

        var zero = new BgraImage(40, 10);
        Adjustments.Unsharp(edge, zero, 0);
        Check.True(Same(edge, zero), "amount 0 copies");
    }

    // ------------------------------------------------------------------ resample

    [UnitTest]
    public static void Resample_identity_copies_and_downscales_average()
    {
        var src = Noise(20, 12, 4);
        var dst = new BgraImage(30, 20);
        Resample.Bilinear(src, src.Bounds, dst, new RectI(5, 3, 20, 12));
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 20; x++) Check.Equal(src.GetPixel(x, y), dst.GetPixel(5 + x, 3 + y), $"copy at {x},{y}");
        Check.Equal((byte)0, dst.GetPixel(0, 0).A, "outside dstRect untouched");

        var checker = Make(16, 16, (x, y) => (x + y) % 2 == 0 ? (0, 0, 0) : (255, 255, 255));
        var half = new BgraImage(8, 8);
        Resample.Bilinear(checker, checker.Bounds, half, half.Bounds);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Check.True(Math.Abs(half.GetPixel(x, y).G - 127.5) <= 1, $"2x checkerboard at {x},{y}: {half.GetPixel(x, y).G}");

        var big = Make(32, 32, (x, y) => (x + y) % 2 == 0 ? (0, 0, 0) : (255, 255, 255));
        var quarter = new BgraImage(8, 8);
        Resample.Bilinear(big, big.Bounds, quarter, quarter.Bounds);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Check.True(Math.Abs(quarter.GetPixel(x, y).G - 127.5) <= 1, $"4x checkerboard (box prefilter) at {x},{y}: {quarter.GetPixel(x, y).G}");

        var ramp = Make(2, 1, (x, y) => (x * 255, x * 255, x * 255));
        var up = new BgraImage(8, 1);
        Resample.Bilinear(ramp, ramp.Bounds, up, up.Bounds);
        for (int x = 1; x < 8; x++) Check.True(up.GetPixel(x, 0).B >= up.GetPixel(x - 1, 0).B, $"upscaled ramp monotone at {x}");
        Check.Equal((byte)0, up.GetPixel(0, 0).B);
        Check.Equal((byte)255, up.GetPixel(7, 0).B);
    }

    [UnitTest]
    public static void Resample_transparent_pixels_do_not_bleed_colour()
    {
        var src = new BgraImage(2, 1);
        src.SetPixel(0, 0, 255, 0, 0, 255);      // opaque blue
        src.SetPixel(1, 0, 0, 255, 0, 0);        // transparent green
        var dst = new BgraImage(4, 1);
        Resample.Bilinear(src, src.Bounds, dst, dst.Bounds);
        var p = dst.GetPixel(1, 0);              // between the two: half transparent, still blue
        Check.True(p.A > 0 && p.A < 255, $"partly transparent: {p.A}");
        Check.True(p.B > 240 && p.G < 15, $"colour is the opaque pixel's: {p.B},{p.G}");
    }

    [UnitTest]
    public static void Resample_over_respects_opacity_and_alpha()
    {
        var dst = Solid(10, 10, 0, 0, 0);
        var white = Solid(4, 4, 255, 255, 255);
        var before = dst.Clone();
        Resample.Over(dst, white, new RectI(2, 2, 4, 4), 0);
        Check.True(Same(before, dst), "opacity 0 changes nothing");
        Resample.Over(dst, white, new RectI(2, 2, 4, 4), 1);
        Check.Equal(((byte)255, (byte)255, (byte)255, (byte)255), dst.GetPixel(3, 3), "opacity 1 replaces");
        Check.Equal((byte)0, dst.GetPixel(1, 1).B, "outside dest");

        var half = Solid(10, 10, 0, 0, 0);
        Resample.Over(half, white, new RectI(0, 0, 4, 4), 0.5);
        Check.True(Math.Abs(half.GetPixel(1, 1).B - 128) <= 1, $"opacity 0.5: {half.GetPixel(1, 1).B}");
        Check.Equal((byte)255, half.GetPixel(1, 1).A);

        var clear = new BgraImage(4, 4);   // fully transparent source
        var d2 = Solid(10, 10, 9, 9, 9);
        Resample.Over(d2, clear, new RectI(0, 0, 4, 4), 1);
        Check.Equal(((byte)9, (byte)9, (byte)9, (byte)255), d2.GetPixel(1, 1), "transparent source leaves the destination");

        var scaled = Solid(10, 10, 0, 0, 0);
        Resample.Over(scaled, white, new RectI(-4, -4, 12, 12), 1);   // scaled 3x, partly outside
        Check.Equal((byte)255, scaled.GetPixel(0, 0).B);
        Check.Equal((byte)255, scaled.GetPixel(7, 7).B);
        Check.Equal((byte)0, scaled.GetPixel(8, 8).B);

        var onClear = new BgraImage(4, 4);
        Resample.Over(onClear, Solid(2, 2, 10, 20, 30, 128), new RectI(0, 0, 2, 2), 1);
        Check.Equal((byte)128, onClear.GetPixel(0, 0).A, "over transparent: the source alpha");
        Check.Equal(((byte)10, (byte)20, (byte)30), (onClear.GetPixel(0, 0).B, onClear.GetPixel(0, 0).G, onClear.GetPixel(0, 0).R));
    }

    // ------------------------------------------------------------------ performance smoke (log only, generous bounds)

    [UnitTest]
    public static void Perf_4k_spotlight_levels_blend_and_blur()
    {
        var img = Make(3840, 2160, (x, y) => (x & 255, y & 255, (x + y) & 255));
        var sw = Stopwatch.StartNew();
        var levels = Spotlight.Build(img, 40, gradient: true);
        long buildMs = sw.ElapsedMilliseconds;
        var dst = new BgraImage(3840, 2160);
        sw.Restart();
        Spotlight.Blend(levels, new RectI(1200, 600, 1400, 800), 0.55, 756, dst);
        long blendMs = sw.ElapsedMilliseconds;
        sw.Restart();
        Spotlight.Blend(levels, new RectI(1300, 650, 1400, 800), 0.55, 756, dst);
        long blend2Ms = sw.ElapsedMilliseconds;

        var blurred = new BgraImage(3840, 2160);
        sw.Restart();
        BoxBlur.Blur(img, img.Bounds, 20, blurred);
        long blurMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var u = new BgraImage(3840, 2160);
        Adjustments.Unsharp(img, u, 1.0);
        long unsharpMs = sw.ElapsedMilliseconds;
        sw.Restart();
        Adjustments.Apply(img, u, Adjustments.BuildLut(20, 20));
        long lutMs = sw.ElapsedMilliseconds;
        Log.Info($"perf 4K: gradient levels build {buildMs} ms, blend {blendMs} ms (second {blend2Ms} ms), blur sigma 20 {blurMs} ms, unsharp {unsharpMs} ms, lut {lutMs} ms, cores {Environment.ProcessorCount}");
        Check.True(buildMs < 3000, $"levels build {buildMs} ms");
        Check.True(blendMs < 3000, $"blend {blendMs} ms");
        Check.True(blurMs < 3000, $"blur {blurMs} ms");
        Check.True(unsharpMs < 3000, $"unsharp {unsharpMs} ms");
    }
}
