using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Screenshots.Sources;

namespace Clink.Tests;

/// <summary>The pure parts of the screenshot capture: rectangles, the pixel type, the selection arithmetic and the overlay's interaction model.</summary>
internal static class ScreenshotGeometryTests
{
    // A two-monitor layout with a negative origin: a 1920x1080 primary at 0,0 and a 2560x1440 monitor to its left and higher up.
    private static readonly RectI Primary = new(0, 0, 1920, 1080);
    private static readonly RectI Left = new(-2560, -360, 2560, 1440);

    /// <summary>An image whose pixel at (x, y) is (B = x, G = y, R = x + y, A = 255) modulo 256: every pixel tells where it came from.</summary>
    private static BgraImage Gradient(int w, int h, int offsetX = 0, int offsetY = 0)
    {
        var img = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, (byte)(x + offsetX), (byte)(y + offsetY), (byte)(x + y + offsetX + offsetY));
        return img;
    }

    // ------------------------------------------------------------------ RectI

    [UnitTest]
    public static void RectI_corners_intersection_union_and_inflate()
    {
        Check.Equal(new RectI(10, 20, 30, 40), RectI.FromCorners(40, 60, 10, 20), "any drag direction");
        Check.Equal(RectI.Empty, RectI.FromCorners(5, 5, 5, 5), "equal points: exactly Empty, as every factory gives");
        Check.True(RectI.FromCorners(5, 5, 5, 5).IsEmpty);
        Check.Equal(RectI.Empty, RectI.FromCorners(5, 5, 9, 5), "no height");
        Check.Equal(new RectI(5, 5, 5, 5), new RectI(0, 0, 10, 10).Intersect(new RectI(5, 5, 10, 10)));
        Check.True(new RectI(0, 0, 10, 10).Intersect(new RectI(10, 0, 5, 5)).IsEmpty, "touching edges do not overlap (the right edge is exclusive)");
        Check.Equal(new RectI(-5, 0, 20, 10), new RectI(-5, 0, 5, 5).Union(new RectI(0, 5, 15, 5)));
        Check.Equal(new RectI(1, 1, 2, 2), new RectI(0, 0, 10, 10).Intersect(new RectI(1, 1, 2, 2)));
        Check.Equal(new RectI(0, 0, 4, 4), new RectI(1, 1, 2, 2).Inflate(1));
        Check.True(new RectI(1, 1, 2, 2).Inflate(-1).IsEmpty, "over-shrunk is empty");
        Check.True(new RectI(0, 0, 10, 10).Contains(9, 9));
        Check.False(new RectI(0, 0, 10, 10).Contains(10, 9));
        Check.Equal(new RectI(2, 2, 3, 3), RectI.Empty.Union(new RectI(2, 2, 3, 3)), "an empty rectangle adds nothing");
    }

    [UnitTest]
    public static void RectI_corner_pixels_are_both_inside_and_a_negative_size_cannot_be_made()
    {
        Check.Equal(new RectI(0, 0, 1920, 1080), RectI.FromCornerPixels(0, 0, 1919, 1079), "corner pixel to corner pixel");
        Check.Equal(RectI.FromCornerPixels(0, 0, 1919, 1079), RectI.FromCornerPixels(1919, 1079, 0, 0), "either way round");
        Check.Equal(RectI.FromCornerPixels(10, 10, 20, 30), RectI.FromCornerPixels(20, 10, 10, 30));
        Check.Equal(new RectI(7, 7, 1, 1), RectI.FromCornerPixels(7, 7, 7, 7), "one pixel, never empty");
        Check.Equal(new RectI(-12, -3, 3, 4), RectI.FromCornerPixels(-10, 0, -12, -3), "negative coordinates");

        Check.Throws<ArgumentOutOfRangeException>(() => _ = new RectI(0, 0, -5, 3));
        Check.Throws<ArgumentOutOfRangeException>(() => _ = new RectI(0, 0, 5, -1));
        Check.True(new RectI(3, 3, 0, 0).IsEmpty, "zero size is allowed, and empty");
        Check.True(default(RectI).IsEmpty);

        Check.Equal(new RectI(-2, 4, 14, 2), new RectI(0, 3, 10, 4).Inflate(2, -1), "each axis on its own");
        Check.Equal(RectI.Empty, new RectI(0, 0, 10, 4).Inflate(0, -2), "over-shrunk on one axis");
    }

    // ------------------------------------------------------------------ BgraImage

    [UnitTest]
    public static void BgraImage_crop_copies_the_pixels_of_the_rectangle_and_pads_what_is_outside_with_transparency()
    {
        var img = Gradient(8, 6);
        var crop = img.Crop(new RectI(2, 1, 3, 2));
        Check.Equal(3, crop.Width);
        Check.Equal(2, crop.Height);
        Check.Equal(img.GetPixel(2, 1), crop.GetPixel(0, 0));
        Check.Equal(img.GetPixel(4, 2), crop.GetPixel(2, 1));
        crop.SetPixel(0, 0, 9, 9, 9);
        Check.NotEqual(9, (int)img.GetPixel(2, 1).B, "a crop is a copy");

        // partly outside: the part inside lands at the right place, the rest is transparent black
        var edge = img.Crop(new RectI(6, 4, 4, 4));
        Check.Equal(4, edge.Width);
        Check.Equal(img.GetPixel(7, 5), edge.GetPixel(1, 1));
        Check.Equal(((byte)0, (byte)0, (byte)0, (byte)0), edge.GetPixel(3, 3));
        var before = img.Crop(new RectI(-2, -1, 4, 3));
        Check.Equal(img.GetPixel(0, 0), before.GetPixel(2, 1));
        Check.Equal(0, (int)before.GetPixel(0, 0).A);
        Check.Equal(0, img.Crop(RectI.Empty).Width);
    }

    [UnitTest]
    public static void BgraImage_compose_places_each_part_in_virtual_coordinates_and_leaves_gaps_transparent()
    {
        // two "monitors" side by side with a 2 px gap; the area spans both and the gap
        var a = Gradient(10, 6);                     // at 0,0
        var b = Gradient(10, 6, 100, 0);             // at 12,0 (pixels continue the gradient so they can be told apart)
        var area = new RectI(7, 1, 10, 3);           // x 7..16: 3 px of a, 2 px of gap, 5 px of b
        var composed = BgraImage.Compose(area, new[] { (new RectI(0, 0, 10, 6), a), (new RectI(12, 0, 10, 6), b) });
        Check.Equal(10, composed.Width);
        Check.Equal(3, composed.Height);
        Check.Equal(a.GetPixel(7, 1), composed.GetPixel(0, 0));
        Check.Equal(a.GetPixel(9, 3), composed.GetPixel(2, 2));
        Check.Equal(0, (int)composed.GetPixel(3, 0).A, "the gap");
        Check.Equal(0, (int)composed.GetPixel(4, 2).A, "the gap");
        Check.Equal(b.GetPixel(0, 1), composed.GetPixel(5, 0));
        Check.Equal(b.GetPixel(4, 3), composed.GetPixel(9, 2));
        Check.Equal(0, BgraImage.Compose(RectI.Empty, new[] { (Primary, new BgraImage(1, 1)) }).Width);

        // negative origin: a part placed at -5,-5 is cut at its edge
        var neg = BgraImage.Compose(new RectI(-3, -3, 6, 6), new[] { (new RectI(-5, -5, 5, 5), Gradient(5, 5)) });
        Check.Equal(Gradient(5, 5).GetPixel(2, 2), neg.GetPixel(0, 0));
        Check.Equal(0, (int)neg.GetPixel(3, 3).A, "outside the part");
    }

    [UnitTest]
    public static void BgraImage_alpha_is_forced_opaque_and_detected()
    {
        var img = new BgraImage(4, 3);
        Check.True(img.HasTransparency(), "GDI leaves alpha at 0");
        img.SetPixel(1, 1, 10, 20, 30, 0);
        Check.True(ReferenceEquals(img, img.ForceOpaque()), "returns the same image");
        Check.False(img.HasTransparency());
        Check.Equal(((byte)10, (byte)20, (byte)30, (byte)255), img.GetPixel(1, 1), "only alpha changes");
    }

    [UnitTest]
    public static void BgraImage_LooksBlack_samples_the_rectangle_and_ignores_empty_ones()
    {
        var black = new BgraImage(200, 100).ForceOpaque();
        Check.True(black.LooksBlack(black.Bounds));
        Check.True(black.LooksBlack(new RectI(10, 10, 50, 50)));
        black.SetPixel(100, 50, 2, 2, 2);
        Check.True(black.LooksBlack(black.Bounds, threshold: 2), "dark noise at the threshold is still black");
        black.SetPixel(0, 0, 0, 0, 3);
        Check.False(black.LooksBlack(black.Bounds), "the sampled grid includes the top-left pixel");
        Check.True(black.LooksBlack(new RectI(50, 10, 20, 20)), "a rectangle that avoids the lit pixel");
        Check.False(new BgraImage(10, 10).Clone().LooksBlack(RectI.Empty), "nothing to say about an empty rectangle");
        Check.False(black.LooksBlack(new RectI(500, 500, 10, 10)), "outside the image");

        var white = Gradient(64, 64);
        white.SetPixel(0, 0, 255, 255, 255);
        Check.False(white.LooksBlack(white.Bounds));
    }

    [UnitTest]
    public static void BgraImage_converts_to_a_WPF_bitmap_and_back_unchanged()
    {
        var img = Gradient(13, 7).ForceOpaque();
        var bmp = img.ToBitmapSource();
        Check.True(bmp.IsFrozen, "usable from a worker thread");
        Check.Equal(13, bmp.PixelWidth);
        Check.Equal(96.0, bmp.DpiX, "96 dpi: one pixel, one DIP");
        var back = BgraImage.From(bmp);
        Check.Equal(img.Pixels, back.Pixels);
        Check.Throws<ArgumentException>(() => new BgraImage(2, 2, new byte[3]));

        var first = img.ToBitmapSource();
        var second = img.ToBitmapSource();
        Check.False(ReferenceEquals(first, second), "always a fresh copy");
        Check.True(first.IsFrozen && second.IsFrozen, "both frozen");
        Check.Equal(BgraImage.From(first).Pixels, BgraImage.From(second).Pixels);
        img.Row(1)[0] = 77;   // a write through a span shows in the next bitmap, never a stale one
        Check.Equal(77, (int)BgraImage.From(img.ToBitmapSource()).Pixels[img.Stride]);
        Check.Equal(img.Pixels, BgraImage.From(first).Pixels.Select((b, i) => i == img.Stride ? (byte)77 : b).ToArray(), "the earlier bitmap is a copy and kept its pixels");
    }

    [UnitTest]
    public static void BgraImage_rows_spans_and_unpadded_crops()
    {
        var img = Gradient(8, 6);
        Check.Equal(32, img.Row(2).Length);
        Check.Equal(img.GetPixel(3, 2).G, img.Row(2)[3 * 4 + 1]);
        img.Row(5)[0] = 200;
        Check.Equal(200, (int)img.GetPixel(0, 5).B, "a row is the image's own memory");
        Check.Throws<ArgumentOutOfRangeException>(() => img.Row(6));
        Check.Throws<ArgumentOutOfRangeException>(() => img.Row(-1));
        Check.Equal(8 * 6 * 4, img.AsSpan().Length);

        var clipped = img.Crop(new RectI(6, 4, 4, 4), pad: false);
        Check.Equal((2, 2), (clipped.Width, clipped.Height), "cut to the image: only real pixels");
        Check.Equal(img.GetPixel(7, 5), clipped.GetPixel(1, 1));
        Check.Equal((4, 4), (img.Crop(new RectI(6, 4, 4, 4)).Width, img.Crop(new RectI(6, 4, 4, 4)).Height), "padded by default");
        Check.Equal(0, img.Crop(new RectI(50, 50, 4, 4), pad: false).Width, "outside: nothing");
    }

    [UnitTest]
    public static void BgraImage_premultiplied_bytes_convert_to_straight_alpha_and_back()
    {
        var img = new BgraImage(4, 1);
        img.SetPixel(0, 0, 200, 100, 50, 255);   // opaque: unchanged both ways
        img.SetPixel(1, 0, 200, 100, 50, 128);   // half transparent
        img.SetPixel(2, 0, 255, 255, 255, 1);    // almost invisible
        img.SetPixel(3, 0, 9, 9, 9, 0);          // invisible: its colour means nothing
        var p = img.ToPbgra();
        Check.Equal(new byte[] { 200, 100, 50, 255 }, p[0..4]);
        Check.Equal(new byte[] { 100, 50, 25, 128 }, p[4..8], "colour times alpha");
        Check.Equal(new byte[] { 0, 0, 0, 0 }, p[12..16]);
        Check.Equal(200, (int)img.GetPixel(1, 0).B, "ToPbgra leaves the image alone");

        var back = BgraImage.FromPbgra(4, 1, p);
        Check.Equal(img.GetPixel(0, 0), back.GetPixel(0, 0));
        var half = back.GetPixel(1, 0);
        Check.True(Math.Abs(half.B - 200) <= 2 && Math.Abs(half.G - 100) <= 2 && Math.Abs(half.R - 50) <= 2 && half.A == 128, $"about the colour again: {half}");
        Check.Equal(((byte)0, (byte)0, (byte)0, (byte)0), back.GetPixel(3, 0), "A = 0 is transparent black");

        // through a WPF Pbgra32 bitmap, as RenderTargetBitmap hands it over
        var pb = System.Windows.Media.Imaging.BitmapSource.Create(4, 1, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, img.ToPbgra(), 16);
        var fromWpf = BgraImage.FromPbgra(pb);
        Check.Equal(img.GetPixel(0, 0), fromWpf.GetPixel(0, 0));
        Check.Equal(128, (int)fromWpf.GetPixel(1, 0).A);
        Check.True(Math.Abs(fromWpf.GetPixel(1, 0).B - 200) <= 2);
    }

    [UnitTest]
    public static void FrozenDesktop_composes_a_rectangle_across_monitors_and_finds_black_captures()
    {
        FrozenMonitor Fake(RectI r, BgraImage img) =>
            new(new Clink.Native.MonitorInfo(0, "\\\\.\\DISPLAY" + r.X, "", r.X, r.Y, r.Width, r.Height, false, false, 80, 0, ""), img, "test");
        var imgA = Gradient(64, 48);
        var imgB = new BgraImage(32, 32, Gradient(32, 32, 50, 50).Pixels);
        var desk = new FrozenDesktop(new[] { Fake(new RectI(0, 0, 64, 48), imgA), Fake(new RectI(-32, -10, 32, 32), imgB) }, DateTime.Now);
        Check.Equal(new RectI(-32, -10, 96, 58), desk.VirtualBounds);
        Check.Equal(imgA.GetPixel(0, 0), desk.Compose(new RectI(-1, 0, 4, 2)).GetPixel(1, 0), "the primary's top-left");
        Check.Equal(imgB.GetPixel(31, 10), desk.Compose(new RectI(-1, 0, 4, 2)).GetPixel(0, 0), "across the edge: the left monitor's last column");
        Check.Equal(0, (int)desk.Compose(new RectI(-1, 30, 2, 2)).GetPixel(0, 1).A, "below the left monitor (a gap)");
        Check.True(desk.MonitorAt(-5, 0) != null && desk.MonitorAt(-5, 0)!.Bounds.X == -32);
        Check.Null(desk.MonitorAt(-5, 40));

        var black = new BgraImage(40, 40).ForceOpaque();
        var blackDesk = new FrozenDesktop(new[] { Fake(new RectI(0, 0, 40, 40), black), Fake(new RectI(40, 0, 32, 32), imgB) }, DateTime.Now);
        Check.True(blackDesk.LooksBlack(new RectI(0, 0, 40, 40)));
        Check.False(blackDesk.LooksBlack(new RectI(0, 0, 72, 32)), "one monitor in the rectangle is not black");
        Check.False(blackDesk.LooksBlack(new RectI(500, 500, 5, 5)), "a rectangle on no monitor");

        // a dark crop of a lit monitor (a black terminal) is not a black monitor
        var terminal = new BgraImage(40, 40).ForceOpaque();
        terminal.SetPixel(39, 39, 200, 200, 200);
        var termDesk = new FrozenDesktop(new[] { Fake(new RectI(0, 0, 40, 40), terminal) }, DateTime.Now);
        Check.True(termDesk.LooksBlack(new RectI(0, 0, 20, 20)), "the crop is black");
        Check.False(termDesk.WholeMonitorsLookBlack(new RectI(0, 0, 20, 20)), "the monitor is not");
        Check.True(blackDesk.WholeMonitorsLookBlack(new RectI(5, 5, 10, 10)), "a monitor black as a whole");
        Check.False(blackDesk.WholeMonitorsLookBlack(new RectI(30, 0, 20, 20)), "spanning a lit monitor too");
        Check.False(blackDesk.WholeMonitorsLookBlack(new RectI(500, 500, 5, 5)), "on no monitor");
    }

    // ------------------------------------------------------------------ SelectionMath

    [UnitTest]
    public static void SelectionMath_normalises_a_drag_in_any_direction_and_clamps_it_to_the_virtual_screen()
    {
        var virt = SelectionMath.VirtualBounds(new[] { Primary, Left });
        Check.Equal(new RectI(-2560, -360, 4480, 1440), virt);
        Check.Equal(new RectI(100, 200, 300, 400), SelectionMath.Normalize(400, 600, 100, 200));
        Check.Equal(new RectI(100, 200, 300, 400), SelectionMath.Normalize(100, 600, 400, 200));
        // dragged past every edge of a layout with a negative origin
        Check.Equal(virt, SelectionMath.Clamp(new RectI(-5000, -2000, 10000, 5000), virt));
        Check.Equal(new RectI(-2560, -360, 100, 100), SelectionMath.Clamp(new RectI(-2600, -400, 140, 140), virt));
        Check.True(SelectionMath.Clamp(new RectI(3000, 0, 50, 50), virt).IsEmpty, "entirely outside");
        Check.True(SelectionMath.VirtualBounds(Array.Empty<RectI>()).IsEmpty);
    }

    [UnitTest]
    public static void SelectionMath_splits_a_selection_at_the_monitor_edges()
    {
        var monitors = new[] { Primary, Left };   // primary first, as Monitors.Enumerate orders them
        var cross = new RectI(-100, 0, 300, 50);  // 100 px on the left monitor, 200 on the primary
        var parts = SelectionMath.SplitByMonitor(cross, monitors);
        Check.Equal(2, parts.Count);
        Check.Equal(new MonitorPart(0, new RectI(0, 0, 200, 50), new RectI(0, 0, 200, 50)), parts[0]);
        Check.Equal(new MonitorPart(1, new RectI(-100, 0, 100, 50), new RectI(2460, 360, 100, 50)), parts[1], "local = relative to that monitor's top-left");
        Check.Equal(1, SelectionMath.SplitByMonitor(new RectI(10, 10, 20, 20), monitors).Count);
        Check.Equal(0, SelectionMath.SplitByMonitor(RectI.Empty, monitors).Count);
        Check.Equal(0, SelectionMath.SplitByMonitor(new RectI(5000, 5000, 10, 10), monitors).Count);

        // a gap between two monitors belongs to neither
        var gapped = new[] { new RectI(0, 0, 100, 100), new RectI(150, 0, 100, 100) };
        var span = SelectionMath.SplitByMonitor(new RectI(90, 10, 70, 10), gapped);
        Check.Equal(new[] { 10, 10 }, span.Select(p => p.Global.Width).ToArray());
    }

    [UnitTest]
    public static void SelectionMath_finds_the_monitor_under_or_nearest_to_a_point()
    {
        var monitors = new[] { Primary, Left };
        Check.Equal(0, SelectionMath.MonitorIndexAt(0, 0, monitors));
        Check.Equal(1, SelectionMath.MonitorIndexAt(-1, 0, monitors));
        Check.Equal(0, SelectionMath.MonitorIndexAt(1919, 1079, monitors));
        Check.Equal(0, SelectionMath.MonitorIndexAt(2500, 500, monitors), "right of the primary: nearest");
        Check.Equal(1, SelectionMath.MonitorIndexAt(-3000, -400, monitors));
        Check.Equal(-1, SelectionMath.MonitorIndexAt(0, 0, Array.Empty<RectI>()));
    }

    [UnitTest]
    public static void SelectionMath_square_drags_stay_square_inside_the_screen()
    {
        var bounds = new RectI(0, 0, 100, 100);
        Check.Equal((70, 70), SelectionMath.SquareCorner(50, 50, 70, 60, bounds), "the longer drag wins");
        Check.Equal((30, 30), SelectionMath.SquareCorner(50, 50, 40, 30, bounds), "up and left");
        Check.Equal((30, 70), SelectionMath.SquareCorner(50, 50, 40, 70, bounds), "each axis keeps its own direction");
        Check.Equal((99, 99), SelectionMath.SquareCorner(80, 80, 99, 90, bounds), "shrinks to stay inside: the last pixel is 99");
        Check.Equal((0, 0), SelectionMath.SquareCorner(20, 20, 0, 5, bounds), "and at the top-left edge");
        Check.Equal((50, 50), SelectionMath.SquareCorner(50, 50, 50, 50, bounds));
        var (qx, qy) = SelectionMath.SquareCorner(80, 80, 99, 90, bounds);
        var square = SelectionMath.Band(80, 80, qx, qy);
        Check.Equal(new RectI(80, 80, 20, 20), square, "the band to the corner pixel is square and ends at the edge");
        Check.True(bounds.Contains(square));
    }

    [UnitTest]
    public static void SelectionMath_the_band_holds_both_corner_pixels_whichever_way_it_is_dragged()
    {
        Check.Equal(new RectI(0, 0, 1920, 1080), SelectionMath.Band(0, 0, 1919, 1079), "corner to corner of a 1920 x 1080 monitor");
        Check.Equal(new RectI(0, 0, 1920, 1080), SelectionMath.Band(1919, 1079, 0, 0), "dragged up and left: the same size");
        Check.Equal(SelectionMath.Band(100, 100, 300, 200), SelectionMath.Band(300, 100, 100, 200), "right-to-left equals left-to-right");
        Check.Equal(SelectionMath.Band(100, 100, 300, 200), SelectionMath.Band(100, 200, 300, 100), "bottom-to-top equals top-to-bottom");
        Check.Equal(new RectI(-2560, -360, 2560, 1440), SelectionMath.Band(-2560, -360, -1, 1079), "a whole monitor with a negative origin");
    }

    [UnitTest]
    public static void SelectionMath_finds_the_monitor_of_a_rectangle_the_game_rule_and_an_unchanged_layout()
    {
        var monitors = new[] { Primary, Left };
        Check.Equal(0, SelectionMath.MonitorIndexOf(new RectI(-100, 0, 300, 50), monitors), "most of it is on the primary");
        Check.Equal(1, SelectionMath.MonitorIndexOf(new RectI(-300, 0, 400, 50), monitors), "most of it is on the left one");
        Check.Equal(0, SelectionMath.MonitorIndexOf(new RectI(-100, 0, 200, 50), monitors), "equal parts: the first");
        Check.Equal(0, SelectionMath.MonitorIndexOf(new RectI(3000, 100, 10, 10), monitors), "on none: the nearest");
        Check.Equal(-1, SelectionMath.MonitorIndexOf(new RectI(0, 0, 5, 5), Array.Empty<RectI>()));

        Check.Equal("Overwatch", SelectionMath.GameFor("Overwatch", Primary, new RectI(1900, 10, 50, 50)), "overlaps the recorded monitor");
        Check.Null(SelectionMath.GameFor("Overwatch", Primary, new RectI(-500, 10, 50, 50)), "on another monitor");
        Check.Null(SelectionMath.GameFor("Overwatch", Primary, new RectI(1920, 0, 50, 50)), "touching its edge is not on it");
        Check.Null(SelectionMath.GameFor("Overwatch", null, new RectI(10, 10, 50, 50)), "no recorded monitor");
        Check.Null(SelectionMath.GameFor(" ", Primary, new RectI(10, 10, 50, 50)), "no game");
        Check.Null(SelectionMath.GameFor(null, Primary, new RectI(10, 10, 50, 50)));

        Check.True(SelectionMath.SameLayout(new[] { Primary, Left }, new[] { Left, Primary }), "order does not matter");
        Check.False(SelectionMath.SameLayout(new[] { Primary, Left }, new[] { Primary }), "a monitor went away");
        Check.False(SelectionMath.SameLayout(new[] { Primary }, new[] { new RectI(0, 0, 1280, 720) }), "a resolution change");
        Check.False(SelectionMath.SameLayout(new[] { Primary }, Array.Empty<RectI>()));
    }

    [UnitTest]
    public static void SelectionMath_pixels_and_DIPs_round_trip_exactly_at_every_common_scale()
    {
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            foreach (int origin in new[] { 0, 1920, -2560, -1366 })
            {
                for (int px = origin - 5; px < origin + 4000; px += 7)
                {
                    double dip = SelectionMath.PxToDip(px, origin, scale);
                    Check.Equal(px, SelectionMath.DipToPx(dip, origin, scale), $"scale {scale}, origin {origin}, pixel {px}");
                }
            }
            var rect = SelectionMath.ToDip(new RectI(-2460, 360, 300, 150), Left, scale);
            Check.Near(100 / scale, rect.X, 1e-9, "x relative to the monitor");
            Check.Near(720 / scale, rect.Y, 1e-9);
            Check.Near(300 / scale, rect.Width, 1e-9);
            Check.Equal(new RectI(-2460, 360, 300, 150), SelectionMath.DipToPx(rect, Left, scale), $"and back, scale {scale}");
            foreach (var r in new[] { new RectI(0, 0, 1, 1), new RectI(17, 33, 101, 7), new RectI(1919, 1079, 1, 1) })
                Check.Equal(r, SelectionMath.DipToPx(SelectionMath.ToDip(r, Primary, scale), Primary, scale), $"{r} at scale {scale}");
        }
        Check.True(SelectionMath.DipToPx(new DipRect(10, 10, 0.1, 0.1), Primary, 1.0).IsEmpty, "rounds to nothing");
        Check.Equal("1280 x 720", SelectionMath.SizeLabel(new RectI(5, 5, 1280, 720)));
    }

    [UnitTest]
    public static void SelectionMath_places_the_label_and_the_loupe_inside_the_area()
    {
        var sel = new DipRect(100, 100, 200, 100);
        var (x, y) = SelectionMath.PlaceLabel(sel, 80, 20, 1000, 800);
        Check.Equal((100.0, 76.0), (x, y));   // above the top-left corner, 4 DIP gap
        var (ix, iy) = SelectionMath.PlaceLabel(new DipRect(100, 5, 200, 100), 80, 20, 1000, 800);
        Check.Equal((100.0, 9.0), (ix, iy));   // no room above: inside
        var (rx, _) = SelectionMath.PlaceLabel(new DipRect(990, 100, 5, 5), 80, 20, 1000, 800);
        Check.Equal(920.0, rx, "kept inside on the right");

        var (lx, ly) = SelectionMath.PlaceLoupe(100, 100, 136, 150, 1920, 1080);
        Check.Equal((124.0, 124.0), (lx, ly));
        var (fx, fy) = SelectionMath.PlaceLoupe(1900, 1070, 136, 150, 1920, 1080);
        Check.Equal((1900.0 - 24 - 136, 1070.0 - 24 - 150), (fx, fy), "flipped to the other side at the corner");
        var (cx, cy) = SelectionMath.PlaceLoupe(50, 50, 136, 150, 100, 100);
        Check.Equal((0.0, 0.0), (cx, cy), "an area smaller than the box");
    }

    // ------------------------------------------------------------------ RegionSelection

    private static RegionSelection Region(out List<RectI> committed, out int[] cancelled)
    {
        var sel = new RegionSelection(new[] { Primary, Left });
        var done = new List<RectI>();
        var count = new int[1];
        sel.Committed += r => done.Add(r);
        sel.Cancelled += () => count[0]++;
        committed = done;
        cancelled = count;
        return sel;
    }

    [UnitTest]
    public static void RegionSelection_a_drag_commits_on_release_in_any_direction()
    {
        var sel = Region(out var committed, out var cancelled);
        sel.BeginDrag(300, 400);
        sel.MoveCursor(100, 100);
        Check.True(sel.Dragging);
        Check.Equal(new RectI(100, 100, 201, 301), sel.Selection, "the band follows the pointer and holds both corner pixels");
        Check.True(sel.EndDrag(100, 100));
        Check.Equal(new[] { new RectI(100, 100, 201, 301) }, committed.ToArray());
        Check.Equal(PickOutcome.Drag, sel.Outcome);
        Check.True(sel.Finished);
        Check.Equal(0, cancelled[0]);
        sel.MoveCursor(5, 5);   // finished: nothing more is accepted
        sel.Cancel();
        Check.Equal(1, committed.Count);
        Check.Equal(0, cancelled[0]);
    }

    [UnitTest]
    public static void RegionSelection_a_cross_monitor_drag_is_one_rectangle_in_virtual_pixels()
    {
        var sel = Region(out var committed, out _);
        sel.BeginDrag(1800, 300);
        sel.MoveCursor(-2000, 900);
        sel.EndDrag(-2000, 900);
        Check.Equal(new RectI(-2000, 300, 3801, 601), committed.Single());
        Check.Equal(2, SelectionMath.SplitByMonitor(committed.Single(), sel.Monitors).Count);
    }

    [UnitTest]
    public static void RegionSelection_a_click_or_a_tiny_drag_clears_and_commits_nothing()
    {
        var sel = Region(out var committed, out _);
        sel.BeginDrag(50, 50);
        Check.False(sel.EndDrag(50, 50), "a click (1 x 1 px) makes nothing");
        sel.BeginDrag(50, 50);
        Check.False(sel.EndDrag(51, 52), "2 x 3 px is under the minimum");
        Check.Equal(0, committed.Count);
        Check.True(sel.Selection.IsEmpty);
        Check.False(sel.Finished);
        sel.BeginDrag(50, 50);
        Check.True(sel.EndDrag(52, 52), "3 x 3 is the smallest drag");
        Check.Equal(new RectI(50, 50, 3, 3), committed.Single());
    }

    [UnitTest]
    public static void RegionSelection_corner_to_corner_is_the_whole_monitor_in_every_direction()
    {
        foreach (var (ax, ay, bx, by) in new[] { (0, 0, 1919, 1079), (1919, 1079, 0, 0), (1919, 0, 0, 1079), (0, 1079, 1919, 0) })
        {
            var sel = Region(out var committed, out _);
            sel.BeginDrag(ax, ay);
            sel.MoveCursor(bx, by);
            sel.EndDrag(bx, by);
            Check.Equal(Primary, committed.Single(), $"({ax},{ay}) to ({bx},{by})");
        }
        var left = Region(out var c1, out _);
        left.BeginDrag(300, 200); left.EndDrag(100, 100);
        var right = Region(out var c2, out _);
        right.BeginDrag(100, 100); right.EndDrag(300, 200);
        Check.Equal(c2.Single(), c1.Single(), "a drag left / up is as big as one right / down");
    }

    [UnitTest]
    public static void RegionSelection_a_throwing_Changed_subscriber_still_ends_the_capture()
    {
        var cancel = Region(out _, out var cancelled);
        cancel.Changed += () => throw new InvalidOperationException("a window failed to redraw");
        Check.Throws<InvalidOperationException>(() => cancel.Cancel());
        Check.Equal(1, cancelled[0], "Cancelled was raised anyway");
        Check.True(cancel.Finished);

        var commit = Region(out var committed, out _);
        commit.BeginDrag(10, 10);
        commit.Changed += () => throw new InvalidOperationException("redraw");
        Check.Throws<InvalidOperationException>(() => commit.EndDrag(100, 100));
        Check.Equal(new[] { new RectI(10, 10, 91, 91) }, committed.ToArray(), "Committed was raised anyway");

        var monitor = Region(out var whole, out _);
        monitor.Changed += () => throw new InvalidOperationException("redraw");
        Check.Throws<InvalidOperationException>(() => monitor.CommitMonitorAt(5, 5));
        Check.Equal(new[] { Primary }, whole.ToArray());
        Check.Equal(PickOutcome.WholeMonitor, monitor.Outcome);
    }

    [UnitTest]
    public static void RegionSelection_the_band_is_clamped_to_the_screen_and_Shift_keeps_it_square()
    {
        var sel = Region(out var committed, out _);
        // Real pointer positions: GetCursorPos never reports a pixel outside the monitors, so the last pixel is 1919, 1079.
        sel.BeginDrag(1900, 1000);
        sel.MoveCursor(1919, 1079);
        Check.Equal(new RectI(1900, 1000, 20, 80), sel.Selection, "up to and including the primary's right and bottom edge pixels");
        sel.MoveCursor(1919, 1079, square: true);
        Check.Equal(new RectI(1900, 1000, 20, 20), sel.Selection, "square, shrunk to fit the room on the right");
        sel.MoveCursor(1700, 900, square: true);
        Check.Equal(new RectI(1700, 800, 201, 201), sel.Selection, "the longer drag (200) sets the side, both corner pixels inside");
        sel.EndDrag(1700, 900, square: true);
        Check.Equal(new RectI(1700, 800, 201, 201), committed.Single());

        // the clamp itself: an arrow key or a square drag aiming past the virtual screen (its top-left is -2560, -360)
        var edge = Region(out _, out _);
        edge.BeginDrag(-2550, -350);
        edge.MoveCursor(-2560, -360, square: true);
        Check.Equal(new RectI(-2560, -360, 11, 11), edge.Selection, "the virtual screen's corner pixel is the last one");
    }

    [UnitTest]
    public static void RegionSelection_right_click_clears_the_band_first_and_cancels_when_there_is_none()
    {
        var sel = Region(out var committed, out var cancelled);
        sel.BeginDrag(10, 10);
        sel.MoveCursor(100, 100);
        Check.False(sel.RightClick(), "clears the band being made");
        Check.False(sel.Dragging);
        Check.True(sel.Selection.IsEmpty);
        Check.Equal(0, cancelled[0]);
        Check.True(sel.RightClick(), "nothing to clear: cancel");
        Check.Equal(1, cancelled[0]);
        Check.True(sel.Finished);
        Check.Equal(0, committed.Count);
        Check.True(sel.Result.IsEmpty);
    }

    [UnitTest]
    public static void RegionSelection_Enter_takes_the_whole_monitor_under_the_pointer_and_Esc_cancels()
    {
        var sel = Region(out var committed, out _);
        sel.MoveCursor(-100, 100);
        sel.Confirm();
        Check.Equal(Left, committed.Single(), "the left monitor, whole");
        Check.Equal(Left, sel.Result);

        var second = Region(out var committed2, out var cancelled2);
        second.MoveCursor(500, 500);
        second.BeginDrag(500, 500);
        second.MoveCursor(700, 650);
        second.Confirm();
        Check.Equal(new RectI(500, 500, 201, 151), committed2.Single(), "Enter with a band commits the band");
        Check.Equal(PickOutcome.Keyboard, second.Outcome);
        Check.Equal(PickOutcome.WholeMonitor, sel.Outcome);

        var third = Region(out var committed3, out var cancelled3);
        third.Cancel();
        Check.Equal(1, cancelled3[0]);
        Check.Equal(0, committed3.Count);
        third.Confirm();
        Check.Equal(0, committed3.Count, "finished is final");
    }

    // ------------------------------------------------------------------ editor geometry: arrows, smoothing, hit testing, toolbar placement

    /// <summary>A hand-drawn stroke: a point every 3 px along <paramref name="dir"/> with random sideways jitter (deterministic by seed).</summary>
    private static List<System.Windows.Point> HandStroke(System.Windows.Point from, System.Windows.Vector dir, double length, int seed, double jitter)
    {
        var rnd = new Random(seed);
        var perp = new System.Windows.Vector(-dir.Y, dir.X);
        var pts = new List<System.Windows.Point>();
        for (double d = 0; d <= length; d += 3)
            pts.Add(from + dir * d + perp * ((rnd.NextDouble() - 0.5) * 2 * jitter));
        return pts;
    }

    private static double Degrees(System.Windows.Vector a, System.Windows.Vector b) => Math.Abs(System.Windows.Vector.AngleBetween(a, b));

    [UnitTest]
    public static void A_freehand_arrow_head_points_the_way_the_hand_was_going_despite_jitter()
    {
        foreach (var (name, dir) in new[]
        {
            ("east", new System.Windows.Vector(1, 0)),
            ("north-east", new System.Windows.Vector(Math.Sqrt(0.5), -Math.Sqrt(0.5))),
            ("south-west", new System.Windows.Vector(-Math.Sqrt(0.5), Math.Sqrt(0.5))),
            ("north", new System.Windows.Vector(0, -1)),
        })
        {
            for (int seed = 1; seed <= 25; seed++)
            {
                var pts = HandStroke(new System.Windows.Point(100, 100), dir, 240, seed, 1.5);
                var got = ArrowGeometry.FreehandDirection(pts, 4);
                Check.True(got.HasValue, $"{name} seed {seed}: a head");
                double off = Degrees(dir, got!.Value);
                Check.True(off <= 5, $"{name} seed {seed}: {off:0.0} degrees off the truth");
                Check.Near(1.0, got.Value.Length, 1e-9, "a unit vector");
            }
        }
    }

    [UnitTest]
    public static void A_freehand_arrow_follows_the_final_leg_of_an_L_shaped_stroke()
    {
        var pts = new List<System.Windows.Point>();
        for (int x = 0; x <= 100; x += 3) pts.Add(new System.Windows.Point(x, 0));
        for (int y = -3; y >= -60; y -= 3) pts.Add(new System.Windows.Point(100, y));
        var dir = ArrowGeometry.FreehandDirection(pts, 4)!.Value;
        Check.True(Degrees(new System.Windows.Vector(0, -1), dir) <= 5, "north, not the diagonal from start to end");
    }

    [UnitTest]
    public static void A_short_freehand_stroke_uses_first_to_last_and_a_dot_has_no_head()
    {
        var shortStroke = new[] { new System.Windows.Point(0, 0), new System.Windows.Point(3, 1), new System.Windows.Point(6, 2) };   // 6.3 px, shorter than the 18 px head
        var d = ArrowGeometry.FreehandDirection(shortStroke, 4)!.Value;
        var expected = new System.Windows.Vector(6, 2) / new System.Windows.Vector(6, 2).Length;
        Check.Near(expected.X, d.X, 1e-9);
        Check.Near(expected.Y, d.Y, 1e-9);

        Check.Null(ArrowGeometry.FreehandDirection(new[] { new System.Windows.Point(5, 5), new System.Windows.Point(6, 5.5) }, 4), "1.1 px: a dot");
        Check.Null(ArrowGeometry.FreehandDirection(new[] { new System.Windows.Point(5, 5) }, 4), "one point");
        Check.Null(ArrowGeometry.FreehandDirection(Array.Empty<System.Windows.Point>(), 4));
        var dot = ArrowGeometry.Freehand(new[] { new System.Windows.Point(5, 5), new System.Windows.Point(6, 5.5) }, 4);
        Check.False(dot.HasHead);
        Check.True(dot.Shaft.Length > 0, "the dot is still drawn");
        Check.False(ArrowGeometry.Straight(new System.Windows.Point(0, 0), new System.Windows.Point(1, 0), 4, arrow: true).HasHead, "a straight arrow under 2 px has no head either");
    }

    [UnitTest]
    public static void The_arrow_head_is_max_10_or_4_and_a_half_widths_long_with_a_25_degree_half_angle()
    {
        Check.Equal(10.0, ArrowGeometry.HeadLength(1));
        Check.Equal(10.0, ArrowGeometry.HeadLength(2));
        Check.Near(13.5, ArrowGeometry.HeadLength(3));
        Check.Near(45.0, ArrowGeometry.HeadLength(10));
        var (tip, left, right) = ArrowGeometry.Head(new System.Windows.Point(100, 50), new System.Windows.Vector(1, 0), 4);
        double half = 18 * Math.Tan(25 * Math.PI / 180);
        Check.Equal(new System.Windows.Point(100, 50), tip);
        Check.Near(82, left.X, 1e-9);
        Check.Near(82, right.X, 1e-9);
        Check.Near(Math.Abs(left.Y - 50), half, 1e-9);
        Check.Near(Math.Abs(right.Y - 50), half, 1e-9);
        Check.Near(50.0, Degrees(left - tip, right - tip), 1e-6, "25 degrees either side of the shaft");
    }

    [UnitTest]
    public static void The_arrow_shaft_stops_inside_the_head_so_it_does_not_poke_through()
    {
        var straight = ArrowGeometry.Straight(new System.Windows.Point(0, 0), new System.Windows.Point(100, 0), 4, arrow: true);
        Check.True(straight.HasHead);
        Check.Near(100 - 0.8 * 18, straight.Shaft[^1].X, 1e-9);
        Check.Equal(new System.Windows.Point(100, 0), straight.Tip);
        var plain = ArrowGeometry.Straight(new System.Windows.Point(0, 0), new System.Windows.Point(100, 0), 4, arrow: false);
        Check.False(plain.HasHead);
        Check.Equal(new System.Windows.Point(100, 0), plain.Shaft[^1], "a plain line goes all the way");

        var pts = new List<System.Windows.Point>();
        for (int x = 0; x <= 200; x += 4) pts.Add(new System.Windows.Point(x, 0));
        var free = ArrowGeometry.Freehand(pts, 4);
        Check.True(free.HasHead);
        Check.Near(200 - 0.8 * 18, free.Shaft[^1].X, 1e-6, "cut at L - 0.8 * head length");
        Check.Equal(new System.Windows.Point(200, 0), free.Tip, "the head is at the end of the stroke");
        Check.Near(0.0, free.Shaft[0].X, 1e-9, "and the shaft starts where the stroke did");
    }

    [UnitTest]
    public static void Resampling_walks_the_polyline_at_even_arc_length_and_keeps_the_end()
    {
        var res = ArrowGeometry.Resample(new[] { new System.Windows.Point(0, 0), new System.Windows.Point(10, 0), new System.Windows.Point(10, 10) }, 2);
        Check.Equal(11, res.Count, "0, 2, ..., 20 along the path");
        Check.Equal(new System.Windows.Point(10, 2), res[6]);
        Check.Equal(new System.Windows.Point(10, 10), res[^1]);
        var odd = ArrowGeometry.Resample(new[] { new System.Windows.Point(0, 0), new System.Windows.Point(5, 0) }, 2);
        Check.Equal(new System.Windows.Point(5, 0), odd[^1], "the last point is always there");
        Check.Equal(4, odd.Count);
        Check.Equal(7.0, ArrowGeometry.Length(new[] { new System.Windows.Point(0, 0), new System.Windows.Point(3, 4), new System.Windows.Point(3, 6) }));
    }

    [UnitTest]
    public static void Chaikin_smoothing_keeps_the_ends_and_reduces_the_total_turning()
    {
        var zigzag = new List<System.Windows.Point>();
        for (int i = 0; i < 12; i++) zigzag.Add(new System.Windows.Point(i * 10, i % 2 == 0 ? 0 : 6));
        var smooth = StrokeSmoothing.Chaikin(zigzag, 2);
        Check.Equal(zigzag[0], smooth[0], "start kept");
        Check.Equal(zigzag[^1], smooth[^1], "end kept");
        Check.True(smooth.Count > zigzag.Count);
        // Corner cutting keeps the sum of the turning angles (each corner is split into smaller turns of the same sign) but takes the
        // sharpness out: the largest turn and the bending energy (the sum of the squared angles) fall.
        Check.Near(StrokeSmoothing.TotalTurning(zigzag), StrokeSmoothing.TotalTurning(smooth), 1e-6, "the sum of the angles is kept");
        Check.True(StrokeSmoothing.MaxTurning(smooth) < 0.6 * StrokeSmoothing.MaxTurning(zigzag), "the sharpest corner is rounded");
        Check.True(StrokeSmoothing.BendingEnergy(smooth) < 0.5 * StrokeSmoothing.BendingEnergy(zigzag), "less bending");

        // a hand-drawn line: the jitter turns the path back and forth; smoothing takes the sharpness of that away
        var shaky = HandStroke(new System.Windows.Point(0, 0), new System.Windows.Vector(1, 0), 300, 7, 1.5);
        var calm = StrokeSmoothing.Chaikin(shaky, 2);
        Check.Equal(shaky[0], calm[0]);
        Check.Equal(shaky[^1], calm[^1]);
        Check.True(StrokeSmoothing.BendingEnergy(calm) < 0.5 * StrokeSmoothing.BendingEnergy(shaky), $"bending {StrokeSmoothing.BendingEnergy(shaky):0.0} -> {StrokeSmoothing.BendingEnergy(calm):0.0}");
        Check.True(StrokeSmoothing.BendingEnergy(StrokeSmoothing.Chaikin(shaky, 3)) <= StrokeSmoothing.BendingEnergy(calm) + 1e-9, "more iterations, no more bending");
        var line = new[] { new System.Windows.Point(0, 0), new System.Windows.Point(10, 10), new System.Windows.Point(20, 20) };
        foreach (var p in StrokeSmoothing.Chaikin(line, 2)) Check.Near(p.X, p.Y, 1e-9, "a straight line stays on its line");
        Check.Equal(2, StrokeSmoothing.Chaikin(new[] { new System.Windows.Point(0, 0), new System.Windows.Point(5, 5) }, 2).Count, "two points: nothing to cut");
        Check.Equal(0, StrokeSmoothing.Chaikin(Array.Empty<System.Windows.Point>(), 2).Count);

        var dense = new[] { new System.Windows.Point(0, 0), new System.Windows.Point(0.2, 0), new System.Windows.Point(0.4, 0), new System.Windows.Point(5, 0), new System.Windows.Point(5.1, 0) };
        var thin = StrokeSmoothing.Decimate(dense, 1);
        Check.Equal(new[] { new System.Windows.Point(0, 0), new System.Windows.Point(5, 0), new System.Windows.Point(5.1, 0) }, thin.ToArray(), "close points dropped, the last kept");
    }

    // ---- hit testing

    private static readonly Func<Clink.Screenshots.Editing.TextNote, System.Windows.Rect> NoText = _ => System.Windows.Rect.Empty;

    private static Clink.Screenshots.Editing.DocumentState EmptyDoc() => Clink.Screenshots.Editing.DocumentState.Create(new BgraImage(200, 200));

    private static System.Windows.Media.Color Red => System.Windows.Media.Colors.Red;

    [UnitTest]
    public static void The_pick_distance_is_a_fixed_number_of_screen_DIPs_so_it_shrinks_when_zoomed_in()
    {
        var doc = EmptyDoc().Add(new Clink.Screenshots.Editing.LineShape(0, new System.Windows.Point(0, 50), new System.Windows.Point(100, 50), Red, 4, false), out var line);
        // scale = DIPs per picture pixel. At 1 the tolerance is 4/2 + 6 = 8 px.
        Check.Near(8.0, HitTest.Tolerance(4, 1));
        Check.True(HitTest.HitObject(line, new System.Windows.Point(50, 57), 1, NoText), "7 px away at 100%");
        Check.False(HitTest.HitObject(line, new System.Windows.Point(50, 59), 1, NoText), "9 px away at 100%");
        // zoomed in 4x: 6 DIP is only 1.5 picture pixels
        Check.Near(3.5, HitTest.Tolerance(4, 4));
        Check.False(HitTest.HitObject(line, new System.Windows.Point(50, 57), 4, NoText), "the same 7 px is far away at 400%");
        Check.True(HitTest.HitObject(line, new System.Windows.Point(50, 53), 4, NoText), "3 px at 400%");
        // zoomed out to 25%: 6 DIP is 24 picture pixels
        Check.True(HitTest.HitObject(line, new System.Windows.Point(50, 75), 0.25, NoText), "25 px at 25%");
        Check.False(HitTest.HitObject(line, new System.Windows.Point(120, 50), 1, NoText), "past the end");
        Check.Near(5.0, HitTest.DistanceToSegment(new System.Windows.Point(103, 54), new System.Windows.Point(0, 50), new System.Windows.Point(100, 50)), 1e-9, "distance to the end point");
        Check.Near(3.0, HitTest.DistanceToPolyline(new System.Windows.Point(5, 3), new[] { new System.Windows.Point(0, 0), new System.Windows.Point(10, 0) }));
    }

    [UnitTest]
    public static void The_topmost_object_wins_and_the_vector_band_is_above_the_raster_band()
    {
        var doc = EmptyDoc();
        doc = doc.Add(new Clink.Screenshots.Editing.BlurRegion(0, new RectI(10, 10, 100, 100), 4, false, 8), out var blur);   // raster band
        doc = doc.Add(new Clink.Screenshots.Editing.LineShape(0, new System.Windows.Point(0, 50), new System.Windows.Point(100, 50), Red, 4, false), out var first);
        doc = doc.Add(new Clink.Screenshots.Editing.RectOutline(0, new System.Windows.Rect(20, 30, 60, 40), Red, 4), out var second);
        var p = new System.Windows.Point(50, 50);   // on the line; inside the rectangle (not on its border), inside the blur box
        Check.Equal(first.Id, HitTest.Topmost(doc, p, 1, NoText)!.Id, "a rectangle outline is hit on its border only: the line under its interior is reachable");
        var onBorder = new System.Windows.Point(20, 50);
        Check.Equal(second.Id, HitTest.Topmost(doc, onBorder, 1, NoText)!.Id, "where both are hit, the one added later is on top");
        doc = doc.Reorder(second.Id, int.MinValue);
        Check.Equal(first.Id, HitTest.Topmost(doc, onBorder, 1, NoText)!.Id, "sent to the back: the line is on top now");
        Check.Equal(blur.Id, HitTest.Topmost(doc, new System.Windows.Point(100, 100), 1, NoText)!.Id, "only the raster object is there");
        Check.Null(HitTest.Topmost(doc, new System.Windows.Point(190, 190), 1, NoText));
    }

    [UnitTest]
    public static void Handles_of_the_selected_object_win_over_whatever_lies_on_top()
    {
        var doc = EmptyDoc();
        doc = doc.Add(new Clink.Screenshots.Editing.RectOutline(0, new System.Windows.Rect(20, 20, 40, 40), Red, 2), out var rect);
        doc = doc.Add(new Clink.Screenshots.Editing.LineShape(0, new System.Windows.Point(40, 60), new System.Windows.Point(100, 60), Red, 6, false), out var line);
        var corner = new System.Windows.Point(60, 60);   // the rectangle's bottom-right handle, with the line running across it
        Check.Equal(line.Id, HitTest.Hit(doc, null, corner, 1, NoText).Object!.Id, "nothing selected: the object on top");
        var hit = HitTest.Hit(doc, rect, corner, 1, NoText);
        Check.Equal(Handle.BottomRight, hit.Handle);
        Check.Equal(rect.Id, hit.Object!.Id);
        // the reach is in DIPs too
        Check.Equal(Handle.BottomRight, HitTest.Hit(doc, rect, new System.Windows.Point(66, 60), 1, NoText).Handle, "6 px at 100% (7 DIP reach)");
        Check.Equal(Handle.None, HitTest.Hit(doc, rect, new System.Windows.Point(66, 60), 4, NoText).Handle, "6 px is 24 DIP at 400%");
        // a line offers its two ends, a pen stroke none
        Check.Equal(2, HitTest.HandlesOf(line).Count);
        Check.Equal(Handle.End, HitTest.Hit(doc, line, new System.Windows.Point(99, 60), 1, NoText).Handle);
        Check.Equal(8, HitTest.HandlesOf(rect).Count);
        var pen = new Clink.Screenshots.Editing.PenStroke(1, System.Collections.Immutable.ImmutableArray.Create(new System.Windows.Point(0, 0), new System.Windows.Point(9, 9)), Red, 3);
        Check.Equal(0, HitTest.HandlesOf(pen).Count, "freehand: move only");
    }

    [UnitTest]
    public static void Resizing_a_rectangle_keeps_the_opposite_side_flips_past_it_and_can_keep_the_proportions()
    {
        var r = new System.Windows.Rect(20, 20, 40, 20);
        Check.Equal(new System.Windows.Rect(20, 20, 60, 30), HitTest.ResizeRect(r, Handle.BottomRight, new System.Windows.Point(80, 50), false));
        Check.Equal(new System.Windows.Rect(10, 20, 50, 20), HitTest.ResizeRect(r, Handle.Left, new System.Windows.Point(10, 999), false), "an edge handle moves one side only");
        Check.Equal(new System.Windows.Rect(60, 20, 20, 20), HitTest.ResizeRect(r, Handle.Left, new System.Windows.Point(80, 5), false), "dragged past the right side: flipped");
        var kept = HitTest.ResizeRect(r, Handle.BottomRight, new System.Windows.Point(100, 45), keepAspect: true);
        Check.Near(2.0, kept.Width / kept.Height, 1e-9, "2:1 stays 2:1");
        Check.Near(20, kept.X); Check.Near(20, kept.Y);
        Check.True(HitTest.ResizeRect(r, Handle.BottomRight, new System.Windows.Point(20, 20), false, minSize: 5).Width >= 5, "never collapses");
        var fromTopLeft = HitTest.ResizeRect(r, Handle.TopLeft, new System.Windows.Point(0, 0), keepAspect: true);
        Check.Near(60, fromTopLeft.Right); Check.Near(40, fromTopLeft.Bottom);
    }

    [UnitTest]
    public static void The_toolbar_goes_below_the_selection_else_above_else_inside_and_stays_in_the_work_area()
    {
        var work = new RectI(0, 0, 1920, 1040);
        var bar = (Width: 500, Height: 48);
        Check.Equal((100, 508), ToolbarPlacement.Place(new RectI(100, 200, 300, 300), bar, work), "below, left-aligned, 8 px gap");
        Check.Equal((100, 444), ToolbarPlacement.Place(new RectI(100, 500, 300, 520), bar, work), "below does not fit: above, top - 8 - 48");
        Check.Equal((0, 974), ToolbarPlacement.Place(new RectI(0, 20, 1920, 1010), bar, work), "neither fits: inside, at the bottom");
        // clamped sideways: the selection is near the right edge, the toolbar is wider than what is left
        Check.Equal((1420, 508), ToolbarPlacement.Place(new RectI(1800, 200, 100, 300), bar, work), "right edge");
        // a work area with a negative origin (a monitor to the left of the primary)
        var left = new RectI(-1920, -200, 1920, 1080);
        Check.Equal((-1920, 308), ToolbarPlacement.Place(new RectI(-1950, 100, 200, 200), bar, left), "left edge of a negative work area");
        // a toolbar wider than the work area sits at its left edge
        Check.Equal(0, ToolbarPlacement.Place(new RectI(100, 100, 100, 100), (3000, 48), work).X);
        // and vertically never above the top of the work area
        Check.Equal(0, ToolbarPlacement.Place(new RectI(0, 0, 1920, 1040), (500, 2000), work).Y);
    }
}
