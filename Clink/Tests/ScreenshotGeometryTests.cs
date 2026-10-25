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
}
