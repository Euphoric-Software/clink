using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Services;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>Slice S4: the composer v2 (dirty tracking, bounded memory, the queue, the adjustment stages), the raster ops, the raster tools of the session and canvas, inserted images, the export.</summary>
internal static class ScreenshotRasterTests
{
    // ------------------------------------------------------------------ helpers

    private static BgraImage Noise(int w, int h, int seed, bool alpha = false)
    {
        var rnd = new Random(seed);
        var img = new BgraImage(w, h);
        for (int i = 0; i < img.Pixels.Length; i += 4)
        {
            img.Pixels[i] = (byte)rnd.Next(256); img.Pixels[i + 1] = (byte)rnd.Next(256); img.Pixels[i + 2] = (byte)rnd.Next(256);
            img.Pixels[i + 3] = alpha ? (byte)rnd.Next(256) : (byte)255;
        }
        return img;
    }

    private static BgraImage Solid(int w, int h, byte b, byte g, byte r)
    {
        var img = new BgraImage(w, h);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = b; img.Pixels[i + 1] = g; img.Pixels[i + 2] = r; img.Pixels[i + 3] = 255; }
        return img;
    }

    /// <summary>A smooth picture (so that an erased area has something to continue).</summary>
    private static BgraImage Ramp(int w, int h)
    {
        var img = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, (byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)((x + y) * 255 / Math.Max(1, w + h - 2)));
        return img;
    }

    private static DocumentState Doc(int w = 300, int h = 200) => DocumentState.Create(Ramp(w, h));

    private static int MaxDelta(BgraImage a, BgraImage b)
    {
        Check.Equal((a.Width, a.Height), (b.Width, b.Height), "same size");
        int max = 0;
        for (int i = 0; i < a.Pixels.Length; i++) max = Math.Max(max, Math.Abs(a.Pixels[i] - b.Pixels[i]));
        return max;
    }

    private static string FirstDifference(BgraImage a, BgraImage b)
    {
        for (int i = 0; i < a.Pixels.Length; i += 4)
            if (a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2] || a.Pixels[i + 3] != b.Pixels[i + 3])
                return $"{i / 4 % a.Width},{i / 4 / a.Width}";
        return "none";
    }

    private static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(3);
        }
        return cond();
    }

    private static void Pump(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(2);
        }
    }

    private static DocumentState WithObject(DocumentState d, EditorObject o) => d.Add(o, out _);

    // ------------------------------------------------------------------ composer: basics (adapted from S3)

    /// <summary>A test op: every pixel of the rectangle becomes the average of its (2r+1)-square neighbourhood in what is below, plus the op's strength in red. A local op with a read margin.</summary>
    private sealed class AverageOp : IRasterOp
    {
        private readonly BlurRegion _b;
        public AverageOp(BlurRegion b) { _b = b; }
        public RectI Affects => _b.R;
        public int ReadMargin => _b.Radius;
        public void Apply(BgraImage src, BgraImage dst, RectI area)
        {
            int r = _b.Radius;
            for (int y = area.Y; y < area.Bottom; y++)
                for (int x = area.X; x < area.Right; x++)
                {
                    int sb = 0, sg = 0, sr = 0, n = 0;
                    for (int dy = -r; dy <= r; dy++)
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, src.Width - 1), yy = Math.Clamp(y + dy, 0, src.Height - 1);
                            var p = src.GetPixel(xx, yy);
                            sb += p.B; sg += p.G; sr += p.R; n++;
                        }
                    dst.SetPixel(x, y, (byte)(sb / n), (byte)(sg / n), (byte)Math.Min(255, sr / n + _b.Block));
                }
        }
    }

    private static RasterComposer AverageComposer() => new() { OpFor = (o, _) => o is BlurRegion b ? new AverageOp(b) : null };

    [UnitTest]
    public static void With_no_ops_and_no_adjustment_the_composite_is_the_base_itself()
    {
        var doc = Doc();
        var composer = new RasterComposer();
        Check.True(ReferenceEquals(composer.Compose(doc), doc.Base), "no copy at all");
        var withVector = doc.Add(new RectOutline(0, new Rect(1, 1, 3, 3), Colors.Red, 1), out _);
        Check.True(ReferenceEquals(composer.Compose(withVector), doc.Base), "the vector band is not the composer's");
        var raster = doc.Add(new BlurRegion(0, new RectI(2, 2, 10, 10), 1, false, 0), out _);
        Check.True(ReferenceEquals(composer.Compose(raster), doc.Base), "a raster object nothing can draw: skipped");
        var tried = AverageComposer().Compose(raster);
        Check.False(ReferenceEquals(tried, doc.Base), "with an op it is a new image");
        Check.Equal(0, composer.HeldBuffers, "nothing held when nothing draws");
    }

    /// <summary>The design's order: the unsharp mask of the base (amount Sharpness / 100), then the brightness / contrast LUT over it.</summary>
    private static BgraImage SharpenThenLut(BgraImage baseImg, AdjustmentValues v)
    {
        var sharp = baseImg;
        if (v.Sharpness > 0)
        {
            sharp = new BgraImage(baseImg.Width, baseImg.Height);
            Adjustments.Unsharp(baseImg, sharp, sharp.Bounds, v.Sharpness / 100.0);
        }
        if (v.Brightness == 0 && v.Contrast == 0) return sharp.Clone();
        var o = new BgraImage(baseImg.Width, baseImg.Height);
        Adjustments.Apply(sharp, o, o.Bounds, Adjustments.BuildLut(v.Brightness, v.Contrast));
        return o;
    }

    [UnitTest]
    public static void The_adjustment_stage_is_the_unsharp_mask_then_the_lut_and_the_identity_is_free()
    {
        var doc = DocumentState.Create(Noise(40, 30, 3));
        var composer = new RasterComposer();
        Check.True(ReferenceEquals(composer.Compose(doc), doc.Base), "the identity never copies");

        var v = new AdjustmentValues(20, -30, 0);
        var lut = new BgraImage(40, 30);
        Adjustments.Apply(doc.Base, lut, lut.Bounds, Adjustments.BuildLut(20, -30));
        var adjusted = composer.Compose(doc with { Adjust = v });
        Check.Equal(lut.Pixels, adjusted.Pixels, "brightness and contrast through the library's lookup table");
        Check.True(ReferenceEquals(adjusted, composer.Compose(doc with { Adjust = v with { } })), "cached per (picture, values)");
        Check.Equal(doc.Base.Pixels, Noise(40, 30, 3).Pixels, "the base was not touched");

        var sharp = new AdjustmentValues(20, -30, 150);
        var sharpened = new BgraImage(40, 30);
        Adjustments.Unsharp(doc.Base, sharpened, sharpened.Bounds, 1.5);
        var expected = new BgraImage(40, 30);
        Adjustments.Apply(sharpened, expected, expected.Bounds, Adjustments.BuildLut(20, -30));
        Check.Equal(expected.Pixels, composer.Compose(doc with { Adjust = sharp }).Pixels, "sharpness 150 = amount 1.5 on the BASE, then the LUT over it");
        Check.Equal(expected.Pixels, Adjuster.Full(sharp, doc.Base).Pixels, "Adjuster.Full is the same order (the export)");
        Check.Equal(SharpenThenLut(doc.Base, sharp).Pixels, expected.Pixels);
        // another brightness over the same sharpness: the cached sharpened base, one LUT pass
        var brighter = new AdjustmentValues(40, -30, 150);
        Check.Equal(SharpenThenLut(doc.Base, brighter).Pixels, composer.Compose(doc with { Adjust = brighter }).Pixels, "the LUT over the cached sharpened base");
        Check.Equal(doc.Base.Pixels, composer.Compose(doc with { Adjust = AdjustmentValues.Identity }).Pixels, "back to the identity: the base again");
        Check.True(ReferenceEquals(composer.Compose(doc with { Adjust = AdjustmentValues.Identity }), doc.Base));
    }

    [UnitTest]
    public static void The_below_cache_recomputes_only_the_ops_from_the_first_changed_one()
    {
        var doc = DocumentState.Create(Noise(40, 30, 9));
        for (int i = 0; i < 4; i++) doc = doc.Add(new BlurRegion(0, new RectI(i * 5, i * 3, 12, 10), 1, false, 0), out _);
        var composer = AverageComposer();
        composer.Compose(doc);
        Check.Equal(4, composer.LastRecomputed, "everything the first time");
        composer.Compose(doc);
        Check.Equal(0, composer.LastRecomputed, "nothing changed: nothing computed");
        var top = (BlurRegion)doc.Raster[3];
        var edited = doc.Replace(top with { Block = 30 });
        composer.Compose(edited);
        Check.Equal(1, composer.LastRecomputed, "only the top op: the three below are cached");
        var bottom = (BlurRegion)doc.Raster[0];
        composer.Compose(edited.Replace(bottom with { Block = 5 }));
        // the change reaches the ops above it through their read margin: (0,0,12,10) -> op 1 -> op 2; op 3 (from x = 15) lies out of reach
        Check.Equal(3, composer.LastRecomputed, "a change at the bottom recomputes the ones above it that it can reach");
        composer.Clear();
        composer.Compose(edited);
        Check.Equal(4, composer.LastRecomputed);
    }

    // ------------------------------------------------------------------ composer: incremental == from scratch

    private static readonly Color[] s_colors = { Colors.Yellow, Colors.Cyan, Color.FromRgb(255, 64, 129), Colors.LimeGreen };

    private static EditorObject Gen(Random rnd, int w, int h, bool blur)
    {
        RectI R() => new(rnd.Next(-4, w - 6), rnd.Next(-4, h - 6), rnd.Next(3, 28), rnd.Next(3, 20));
        switch (rnd.Next(blur ? 9 : 8))
        {
            case 0: return new HighlightRect(0, R(), s_colors[rnd.Next(s_colors.Length)]);
            case 1:
            {
                var pts = Enumerable.Range(0, rnd.Next(2, 6)).Select(_ => new Point(rnd.Next(-3, w + 3), rnd.Next(-3, h + 3))).ToImmutableArray();
                return new HighlightStroke(0, pts, rnd.Next(3, 13), s_colors[rnd.Next(s_colors.Length)]);
            }
            case 2: return new SpotlightBox(0, R(), 0.2 + rnd.NextDouble() * 0.7, rnd.Next(0, 6), rnd.Next(2) == 0, rnd.Next(5, 40));
            case 3: return new MagnifyBox(0, R(), R(), Colors.White, rnd.Next(0, 4), rnd.Next(2) == 0);
            case 4: return new SmartEraseRect(0, R());
            case 5: return new BlurRegion(0, R(), 0, true, rnd.Next(2, 9));
            case 6: return new InsertedImage(0, Noise(rnd.Next(2, 8), rnd.Next(2, 8), rnd.Next(1000), alpha: true), R(), 0.3 + rnd.NextDouble() * 0.7);
            case 7: return new SmartEraseRect(0, new RectI(rnd.Next(0, w - 10), rnd.Next(0, h - 10), rnd.Next(2, 9), rnd.Next(2, 9)));
            default: return new BlurRegion(0, R(), rnd.Next(1, 4), false, 0);
        }
    }

    private static DocumentState InsertAt(DocumentState d, int index, EditorObject o) =>
        d with { Raster = d.Raster.Insert(index, o with { Id = d.NextId }), NextId = d.NextId + 1 };

    private static void RandomEdits(int seed, bool withBlur, int tolerance, int steps)
    {
        var rnd = new Random(seed);
        const int W = 64, H = 48;
        var doc = DocumentState.Create(Noise(W, H, seed));
        var inc = RasterOps.NewComposer();
        for (int i = rnd.Next(2, 7); i > 0; i--) doc = doc.Add(Gen(rnd, W, H, withBlur), out _);
        var prev = inc.Compose(doc).Clone();
        inc.TakeDirty();
        for (int step = 0; step < steps; step++)
        {
            string what = $"seed {seed} step {step}";
            int pick = rnd.Next(doc.Raster.Count);
            switch (rnd.Next(8))
            {
                case 0:   // replace by another object (a move / resize / restyle changes a field; a new kind changes the op)
                    doc = doc.Replace(Gen(rnd, W, H, withBlur) with { Id = doc.Raster[pick].Id });
                    break;
                case 1:   // delete
                    if (doc.Raster.Count > 1) doc = doc.Remove(doc.Raster[pick].Id);
                    break;
                case 2:   // insert at any index
                    doc = InsertAt(doc, rnd.Next(doc.Raster.Count + 1), Gen(rnd, W, H, withBlur));
                    break;
                case 3:   // reorder
                    doc = doc.Reorder(doc.Raster[pick].Id, rnd.Next(-3, 4));
                    break;
                case 4:   // add on top
                    doc = doc.Add(Gen(rnd, W, H, withBlur), out _);
                    break;
                case 5:   // an edit of the same object that keeps its kind: shift its rectangle by a little
                    doc = doc.Replace(ObjectOps.Translate(doc.Raster[pick], rnd.Next(-5, 6), rnd.Next(-5, 6)));
                    break;
                case 6:   // nothing
                    break;
                default:  // sometimes the adjustments change under everything
                    doc = rnd.Next(4) == 0 ? doc with { Adjust = new AdjustmentValues(rnd.Next(-30, 30), rnd.Next(-30, 30), 0) } : doc.Replace(Gen(rnd, W, H, withBlur) with { Id = doc.Raster[pick].Id });
                    break;
            }
            var result = inc.Compose(doc);
            var dirty = inc.TakeDirty();
            var fresh = RasterOps.NewComposer().Compose(doc);
            int delta = MaxDelta(fresh, result);
            Check.True(delta <= tolerance, $"{what}: incremental differs from a from-scratch composite by {delta} (first at {FirstDifference(fresh, result)})");
            // nothing outside the reported dirty region changed
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (dirty.Contains(x, y)) continue;
                    int i = (y * W + x) * 4;
                    if (prev.Pixels[i] != result.Pixels[i] || prev.Pixels[i + 1] != result.Pixels[i + 1] || prev.Pixels[i + 2] != result.Pixels[i + 2] || prev.Pixels[i + 3] != result.Pixels[i + 3])
                        throw new TestFailure($"{what}: pixel {x},{y} changed outside the dirty region {dirty}");
                }
            prev = result.Clone();
        }
    }

    [UnitTest]
    public static void An_incremental_composite_equals_a_from_scratch_one_for_random_edits_with_non_local_ops()
    {
        // magnify (dest depends on source), smart erase (rect on its ring), pixelate (block grid), spotlight (whole picture), inserted images with opacity, highlights
        foreach (int seed in Enumerable.Range(1, 14)) RandomEdits(seed, withBlur: false, tolerance: 0, steps: 45);
    }

    [UnitTest]
    public static void An_incremental_composite_with_blur_differs_from_a_from_scratch_one_by_one_level_at_most()
    {
        // BoxBlur's running sums start elsewhere in a dirty rectangle: +-1 (documented); the ops above a blur see that +-1
        foreach (int seed in Enumerable.Range(100, 8)) RandomEdits(seed, withBlur: true, tolerance: 3, steps: 35);
    }

    [UnitTest]
    public static void A_local_test_op_with_a_margin_recomposes_exactly_for_random_edits()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            var rnd = new Random(seed);
            const int W = 48, H = 32;
            var doc = DocumentState.Create(Noise(W, H, seed));
            var incremental = AverageComposer();
            BlurRegion RandomOp() => new(0, new RectI(rnd.Next(0, W - 8), rnd.Next(0, H - 8), rnd.Next(3, 16), rnd.Next(3, 12)), rnd.Next(0, 3), false, rnd.Next(0, 40));
            for (int i = 0; i < 4; i++) doc = doc.Add(RandomOp(), out _);
            incremental.Compose(doc);
            for (int step = 0; step < 40; step++)
            {
                int pick = rnd.Next(doc.Raster.Count);
                switch (rnd.Next(4))
                {
                    case 0: doc = doc.Replace(((BlurRegion)doc.Raster[pick]) with { R = new RectI(rnd.Next(0, W - 8), rnd.Next(0, H - 8), rnd.Next(3, 16), rnd.Next(3, 12)) }); break;
                    case 1: doc = doc.Replace(((BlurRegion)doc.Raster[pick]) with { Radius = rnd.Next(0, 3), Block = rnd.Next(0, 40) }); break;
                    case 2: if (doc.Raster.Count > 1) doc = doc.Remove(doc.Raster[pick].Id); break;
                    default: doc = InsertAt(doc, rnd.Next(doc.Raster.Count + 1), RandomOp()); break;
                }
                Check.Equal(AverageComposer().Compose(doc).Pixels, incremental.Compose(doc).Pixels, $"seed {seed} step {step}");
            }
        }
    }

    // ------------------------------------------------------------------ composer: Apply does not depend on the area

    private static void CheckAreaIndependence(string name, IRasterOp op, BgraImage src, int tolerance = 0)
    {
        var bounds = src.Bounds;
        var full = op.Affects.Intersect(bounds);
        Check.False(full.IsEmpty, name + ": the footprint is on the picture");
        var whole = src.Clone();
        op.Apply(src, whole, full);
        // two other areas inside the footprint
        var a1 = new RectI(full.X + full.Width / 4, full.Y + full.Height / 4, Math.Max(1, full.Width / 2), Math.Max(1, full.Height / 2));
        var a2 = new RectI(full.X + 1, full.Y, Math.Max(1, full.Width - 2), Math.Max(1, Math.Min(3, full.Height)));
        foreach (var area in new[] { a1, a2 })
        {
            var part = src.Clone();   // the pixels below, in the area, as the composer restores them
            op.Apply(src, part, area);
            for (int y = area.Y; y < area.Bottom; y++)
                for (int x = area.X; x < area.Right; x++)
                {
                    var p = part.GetPixel(x, y);
                    var q = whole.GetPixel(x, y);
                    int d = Math.Max(Math.Max(Math.Abs(p.B - q.B), Math.Abs(p.G - q.G)), Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.A - q.A)));
                    if (d > tolerance) throw new TestFailure($"{name}: pixel {x},{y} differs by {d} when applied over {area} instead of {full}");
                }
            // and nothing outside the area was written
            for (int i = 0; i < src.Pixels.Length; i += 4)
            {
                int x = i / 4 % src.Width, y = i / 4 / src.Width;
                if (area.Contains(x, y)) continue;
                if (part.Pixels[i] != src.Pixels[i] || part.Pixels[i + 1] != src.Pixels[i + 1] || part.Pixels[i + 2] != src.Pixels[i + 2]) throw new TestFailure($"{name}: wrote {x},{y} outside {area}");
            }
        }
    }

    [UnitTest]
    public static void Apply_does_not_depend_on_the_area_it_is_given()
    {
        var src = Noise(80, 60, 5, alpha: true);
        var ops = new RasterOps();
        var ctx = new OpContext(src.Bounds, 1);
        var pts = ImmutableArray.Create(new Point(5, 5), new Point(40, 30), new Point(70, 12));
        var small = Noise(7, 5, 3, alpha: true);
        CheckAreaIndependence("highlight rect", ops.Create(new HighlightRect(1, new RectI(10, 8, 40, 30), Colors.Yellow), ctx)!, src);
        CheckAreaIndependence("highlight stroke", ops.Create(new HighlightStroke(1, pts, 9, Colors.Cyan), ctx)!, src);
        CheckAreaIndependence("pixelate", ops.Create(new BlurRegion(1, new RectI(7, 5, 47, 33), 0, true, 8), ctx)!, src);
        CheckAreaIndependence("smart erase", ops.Create(new SmartEraseRect(1, new RectI(12, 9, 30, 21)), ctx)!, src);
        CheckAreaIndependence("magnify", ops.Create(new MagnifyBox(1, new RectI(4, 4, 20, 14), new RectI(30, 20, 44, 30), Colors.White, 3, false), ctx)!, src);
        CheckAreaIndependence("magnify smooth", ops.Create(new MagnifyBox(1, new RectI(4, 4, 20, 14), new RectI(30, 20, 40, 28), Colors.Red, 2, true), ctx)!, src);
        CheckAreaIndependence("inserted image", ops.Create(new InsertedImage(1, small, new RectI(15, 10, 35, 25), 0.6), ctx)!, src);
        CheckAreaIndependence("spotlight fixed", ops.Create(new SpotlightBox(1, new RectI(20, 15, 30, 20), 0.5, 3, false, 10), ctx)!, src);
        CheckAreaIndependence("spotlight gradient", ops.Create(new SpotlightBox(2, new RectI(20, 15, 30, 20), 0.5, 4, true, 25), ctx with { InputStamp = 2 })!, src);
        // a blur's running sums start elsewhere in a smaller area: one level
        CheckAreaIndependence("blur", ops.Create(new BlurRegion(1, new RectI(10, 8, 50, 40), 3, false, 0), ctx)!, src, tolerance: 1);
    }

    // ------------------------------------------------------------------ ops: same pixels as the library, footprints

    private static BgraImage Through(EditorObject o, BgraImage src) => RasterOps.NewComposer().Compose(DocumentState.Create(src).Add(o, out _)).Clone();

    [UnitTest]
    public static void Each_raster_record_gives_the_pixels_of_the_library_call()
    {
        var src = Noise(70, 50, 11, alpha: true);
        var r = new RectI(8, 6, 40, 30);

        var blur = src.Clone(); BoxBlur.Blur(src, blur, src.Bounds, r, 4);
        Check.Equal(blur.Pixels, Through(new BlurRegion(0, r, 4, false, 12), src).Pixels, "blur");

        var pix = src.Clone(); Pixelate.Apply(src, pix, src.Bounds, r, 9);
        Check.Equal(pix.Pixels, Through(new BlurRegion(0, r, 4, true, 9), src).Pixels, "pixelate");

        var hl = src.Clone(); Highlight.Rect(src, hl, src.Bounds, r, Colors.Yellow);
        Check.Equal(hl.Pixels, Through(new HighlightRect(0, r, Colors.Yellow), src).Pixels, "highlight rect");

        var pts = ImmutableArray.Create(new Point(5, 5), new Point(50, 30), new Point(30, 44));
        var mask = StrokeMask.Rasterize(pts, 11, src.Bounds);
        var hs = src.Clone(); Highlight.Multiply(src, hs, src.Bounds, mask, Colors.LimeGreen);
        Check.Equal(hs.Pixels, Through(new HighlightStroke(0, pts, 11, Colors.LimeGreen), src).Pixels, "highlight stroke (the StrokeMask through Highlight.Multiply)");

        var levels = Spotlight.Build(src, 5, true);
        var sp = src.Clone(); Spotlight.Blend(src, sp, src.Bounds, levels, r, 0.6, 20);
        Check.Equal(sp.Pixels, Through(new SpotlightBox(0, r, 0.6, 5, true, 20), src).Pixels, "spotlight (gradient)");

        var mg = src.Clone(); Magnifier.Render(src, mg, src.Bounds, new RectI(3, 3, 16, 10), new RectI(30, 25, 32, 20), true, Colors.White, 3);
        Check.Equal(mg.Pixels, Through(new MagnifyBox(0, new RectI(3, 3, 16, 10), new RectI(30, 25, 32, 20), Colors.White, 3, true), src).Pixels, "magnify");

        var er = src.Clone(); SmartErase.Fill(src, er, src.Bounds, r, 3, 12);
        Check.Equal(er.Pixels, Through(new SmartEraseRect(0, r), src).Pixels, "smart erase");

        var tiny = Noise(9, 7, 2, alpha: true);
        var ins = src.Clone(); Resample.Over(tiny, ins, ins.Bounds, new RectI(12, 9, 27, 21), 0.55);
        Check.Equal(ins.Pixels, Through(new InsertedImage(0, tiny, new RectI(12, 9, 27, 21), 0.55), src).Pixels, "inserted image: Resample.Over with the opacity");
    }

    [UnitTest]
    public static void AffectedBy_and_ReadRegion_follow_each_ops_dependencies()
    {
        var bounds = new RectI(0, 0, 200, 150);
        var ops = new RasterOps();
        var ctx = new OpContext(bounds, 1);

        // magnify: a change under the SOURCE dirties the DEST; a change elsewhere only itself
        var mag = ops.Create(new MagnifyBox(1, new RectI(10, 10, 20, 20), new RectI(100, 60, 40, 40), Colors.White, 3, false), ctx)!;
        var inSource = new RectI(15, 15, 2, 2);
        Check.True(mag.AffectedBy(inSource).Contains(new RectI(100, 60, 40, 40)), "source -> the whole dest");
        Check.True(mag.AffectedBy(inSource).Contains(inSource), "and the change itself");
        var far = new RectI(150, 120, 3, 3);
        Check.Equal(far, mag.AffectedBy(far), "a change far from the source dirties only itself");
        Check.True(mag.ReadRegion(new RectI(105, 65, 2, 2)).Contains(new RectI(10, 10, 20, 20)), "a dest pixel reads the source");
        Check.Equal(new RectI(5, 5, 2, 2), mag.ReadRegion(new RectI(5, 5, 2, 2)), "elsewhere only itself");

        // smart erase: the ring changes -> the whole rectangle
        var rect = new RectI(60, 40, 30, 20);
        var erase = ops.Create(new SmartEraseRect(1, rect), ctx)!;
        var ringPixel = new RectI(59, 45, 1, 1);   // just left of the rectangle: inside the 3-px ring
        Check.True(erase.AffectedBy(ringPixel).Contains(rect), "a ring pixel dirties the whole rectangle");
        Check.Equal(new RectI(10, 10, 2, 2), erase.AffectedBy(new RectI(10, 10, 2, 2)), "outside the ring: nothing more");
        Check.True(erase.ReadRegion(new RectI(70, 50, 1, 1)).Contains(rect.Inflate(3)), "a pixel of the rectangle needs the ring");

        // pixelate: snaps to the block grid anchored at the rectangle's corner
        var pr = new RectI(5, 7, 60, 40);
        var pix = ops.Create(new BlurRegion(1, pr, 0, true, 12), ctx)!;
        var snapped = pix.AffectedBy(new RectI(5 + 13, 7 + 5, 1, 1));
        Check.True(snapped.Contains(new RectI(5 + 12, 7, 12, 12)), "the block that holds the pixel");
        Check.Equal(new RectI(5 + 12, 7, 12, 12), snapped, "exactly that block (the pixel is inside it)");
        var edge = pix.AffectedBy(new RectI(5 + 11, 7 + 11, 2, 2));
        Check.Equal(new RectI(5, 7, 24, 24), edge, "a change on a block corner: the four blocks");
        var last = pix.AffectedBy(new RectI(5 + 58, 7 + 38, 1, 1));
        Check.Equal(new RectI(5 + 48, 7 + 36, 12, 4), last, "the last, cut, block stays inside the rectangle");

        // spotlight: the whole picture
        var spot = ops.Create(new SpotlightBox(1, new RectI(20, 20, 40, 30), 0.5, 4, false, 10), ctx)!;
        Check.Equal(bounds, spot.AffectedBy(new RectI(1, 1, 1, 1)).Intersect(bounds), "any change dirties everything");
        Check.True(spot.AffectedBy(RectI.Empty).IsEmpty, "no change, nothing");

        // local ops (blur margin, highlight): the default
        var blur = ops.Create(new BlurRegion(1, new RectI(50, 50, 40, 40), 5, false, 0), ctx)!;
        int margin = BoxBlur.ReadMargin(5);
        var changed = new RectI(70, 70, 2, 2);
        Check.Equal(changed.Union(changed.Inflate(margin).Intersect(new RectI(50, 50, 40, 40))), blur.AffectedBy(changed), "input plus margin, inside the footprint");
        Check.True(blur.AffectedBy(changed).Contains(changed));
        Check.Equal(changed.Inflate(margin).Intersect(new RectI(50, 50, 40, 40)), blur.ReadRegion(changed).Intersect(new RectI(50, 50, 40, 40)).Intersect(changed.Inflate(margin)), "reads the margin around it");
        var hl = ops.Create(new HighlightRect(1, new RectI(50, 50, 40, 40), Colors.Yellow), ctx)!;
        Check.Equal(new RectI(60, 60, 3, 3), hl.AffectedBy(new RectI(60, 60, 3, 3)), "a per-pixel op: just the pixels");
    }

    [UnitTest]
    public static void Spotlight_levels_are_cached_per_object_and_only_a_change_of_their_input_or_settings_rebuilds_them()
    {
        var composer = RasterOps.NewComposer(out var ops);
        var doc = DocumentState.Create(Noise(120, 90, 4));
        doc = doc.Add(new HighlightRect(0, new RectI(5, 5, 30, 20), Colors.Yellow), out _);
        doc = doc.Add(new SpotlightBox(0, new RectI(30, 20, 40, 30), 0.5, 4, true, 30), out var spot);
        composer.Compose(doc);
        Check.Equal(1, ops.SpotCache.Builds, "built once");
        var s = (SpotlightBox)spot;
        // moving, resizing and dimming the box only re-blend
        for (int i = 0; i < 6; i++)
        {
            doc = doc.Replace(s with { Box = new RectI(30 + i * 3, 20 + i, 40 + i, 30), Dim = 0.3 + i * 0.05 });
            var moved = composer.Compose(doc);
            var fresh = RasterOps.NewComposer().Compose(doc);
            Check.Equal(fresh.Pixels, moved.Pixels, $"move {i}");
        }
        Check.Equal(1, ops.SpotCache.Builds, "six moves: no new levels");
        // a different radius or gradient flag builds them again
        doc = doc.Replace(s with { Radius = 6 });
        composer.Compose(doc);
        Check.Equal(2, ops.SpotCache.Builds, "radius");
        doc = doc.Replace(s with { Radius = 6, Gradient = false });
        composer.Compose(doc);
        Check.Equal(3, ops.SpotCache.Builds, "gradient flag");
        // a change of the pixels under it builds them again
        doc = doc.Replace(doc.Raster[0] with { }); // equal: nothing
        composer.Compose(doc);
        Check.Equal(3, ops.SpotCache.Builds, "an equal object changes nothing");
        doc = doc.Replace(((HighlightRect)doc.Raster[0]) with { Color = Colors.Cyan });
        var after = composer.Compose(doc);
        Check.Equal(4, ops.SpotCache.Builds, "the pixels below changed");
        Check.Equal(RasterOps.NewComposer().Compose(doc).Pixels, after.Pixels);
        Check.True(ops.SpotCache.Count <= SpotlightCache.MaxEntries);
    }

    // ------------------------------------------------------------------ composer: bounded memory

    [UnitTest]
    public static void The_composer_holds_a_fixed_number_of_pictures_whatever_the_number_of_objects()
    {
        var rnd = new Random(77);
        const int W = 200, H = 150;
        var doc = DocumentState.Create(Noise(W, H, 5));
        for (int i = 0; i < 60; i++) doc = doc.Add(Gen(rnd, W, H, blur: true), out _);
        Check.Equal(60, doc.Raster.Count);
        var composer = RasterOps.NewComposer(out var ops);
        composer.Compose(doc);
        long one = (long)W * H * 4;
        Check.True(composer.HeldBuffers <= 5, $"after 60 objects: {composer.HeldBuffers} buffers");
        // editing the middle, the bottom and the top (several ops redone through the scratch pictures)
        foreach (int index in new[] { 30, 0, 59, 12 })
        {
            doc = doc.Replace(ObjectOps.Translate(doc.Raster[index], 4, 3));
            composer.Compose(doc);
            Check.True(composer.HeldBuffers <= 5, $"after an edit of object {index}: {composer.HeldBuffers} buffers");
        }
        // the honest figure: the pictures, plus the spotlight levels and highlight masks, plus the inserted images of the list
        var m = composer.Memory;
        Check.True(m.Pictures <= 5 * one, $"{m.Pictures} bytes of pictures for {one}-byte pictures");
        Check.Equal(ops.SpotCache.Bytes + ops.MaskBytes, m.Caches, "the op caches are counted");
        Check.True(ops.SpotCache.Count <= SpotlightCache.MaxEntries && m.PeakCaches >= ops.SpotCache.Bytes, "the level sets are bounded and the peak of a rebuild is recorded");
        long inserted = doc.Raster.OfType<InsertedImage>().Select(i => i.Image).Distinct().Sum(i => i.Pixels.LongLength);
        Check.Equal(inserted, m.Inserted, "the inserted images are counted");
        Check.Equal(m.Pictures + m.Caches + m.Inserted, composer.HeldBytes, "HeldBytes is all of it");
        Log.Info($"composer memory with 60 objects at {W}x{H}: {m}");
        // with a brightness change and a sharpness the sharpened base and the LUT result are two more
        var adjusted = doc with { Adjust = new AdjustmentValues(10, 10, 80) };
        composer.DeferSharpness = true;
        composer.Compose(adjusted);
        composer.PrepareSharpened();
        composer.Compose(adjusted);
        Check.True(composer.HeldBuffers <= 7, $"with adjustments: {composer.HeldBuffers} buffers");
        Check.True(composer.Memory.Pictures <= 7 * one);
        // when nothing draws any more the working pictures (composite, below cache, scratch) and the level sets are let go
        composer.Compose(DocumentState.Create(doc.Base));
        Check.Equal(0, composer.HeldBuffers, "no objects, no adjustment: nothing held");
        Check.Equal(0, ops.SpotCache.Count, "no spotlight in the list: no levels");
        Check.Equal(0, ops.MaskCount, "no stroke in the list: no masks");
        // the same number as with 3 objects
        var small = DocumentState.Create(doc.Base);
        for (int i = 0; i < 3; i++) small = small.Add(doc.Raster[i] with { }, out _);
        var c3 = RasterOps.NewComposer();
        c3.Compose(small);
        c3.Compose(small.Replace(ObjectOps.Translate(small.Raster[0], 2, 2)));
        Check.True(c3.HeldBuffers <= 5 && c3.HeldBuffers >= 1);
        composer.Clear();
        Check.Equal(0, composer.HeldBuffers, "Clear drops everything");
    }

    // ------------------------------------------------------------------ the compose queue

    private sealed class SlowOp : IRasterOp
    {
        private readonly HighlightRect _h;
        private readonly ManualResetEventSlim? _gate, _started;
        public SlowOp(HighlightRect h, ManualResetEventSlim? gate, ManualResetEventSlim? started) { _h = h; _gate = gate; _started = started; }
        public RectI Affects => _h.R;
        public int ReadMargin => 0;
        public void Apply(BgraImage src, BgraImage dst, RectI area)
        {
            _started?.Set();
            _gate?.Wait(10_000);
            for (int y = area.Y; y < area.Bottom; y++)
                for (int x = area.X; x < area.Right; x++)
                    dst.SetPixel(x, y, 0, 0, _h.Color.R);
        }
    }

    [UnitTest]
    public static void A_stale_result_is_never_delivered_and_only_one_compose_is_in_flight()
    {
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var composer = new RasterComposer { OpFor = (o, _) => o is HighlightRect h ? new SlowOp(h, gate, started) : null };
        var q = new ComposeQueue(composer, a => a());
        var delivered = new List<(long Version, BgraImage Image)>();
        q.Completed += r => { lock (delivered) delivered.Add((r.Version, r.Image.Clone())); };
        var d0 = DocumentState.Create(Solid(30, 20, 9, 9, 9));
        DocumentState State(byte red) => d0.Add(new HighlightRect(0, new RectI(2, 2, 10, 8), Color.FromRgb(red, 0, 0)), out _);

        q.Request(State(10));
        Check.True(started.Wait(5000), "the first compose started");
        q.Request(State(20));
        q.Request(State(30));
        q.Request(State(40));
        Check.True(q.Busy);
        gate.Set();
        Check.True(q.WaitIdle(5000), "idle at the end");
        Check.Equal(2, q.Started, "the first (stale) one and the newest; the two in between never ran");
        Check.Equal(1, q.Dropped, "the stale result was dropped");
        Check.Equal(1, delivered.Count, "and only the newest was delivered");
        Check.Equal(4L, delivered[0].Version);
        Check.Equal(40, (int)delivered[0].Image.GetPixel(3, 3).R, "its pixels are those of the newest state");
        Check.Equal(1, q.MaxInFlight, "never two composes at once");
        q.Shutdown();
    }

    [UnitTest]
    public static void Many_requests_from_many_threads_still_run_one_compose_at_a_time_and_end_on_the_newest()
    {
        var composer = RasterOps.NewComposer();
        var q = new ComposeQueue(composer, a => a());
        var results = new List<long>();
        q.Completed += r => { lock (results) results.Add(r.Version); };
        var baseImg = Noise(160, 120, 6);
        var states = Enumerable.Range(0, 40).Select(i => DocumentState.Create(baseImg).Add(new HighlightRect(0, new RectI(i, i, 30, 20), Colors.Yellow), out _)).ToArray();
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            var rnd = new Random(1 + t);   // one per thread: Random is not thread-safe
            for (int i = 0; i < 10; i++) { q.Request(states[t * 10 + i]); Thread.Sleep(rnd.Next(0, 3)); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Check.True(q.WaitIdle(10_000));
        Check.Equal(1, q.MaxInFlight);
        Check.True(results.Count >= 1);
        Check.Equal(q.LatestVersion, results[^1], "the last delivery is the newest request");
        Check.True(results.SequenceEqual(results.OrderBy(v => v)), "versions only go forward");
        q.Shutdown();
    }

    [UnitTest]
    public static void A_spotlight_radius_drag_is_debounced_and_a_box_move_is_not()
    {
        var composer = RasterOps.NewComposer(out var ops);
        var q = new ComposeQueue(composer, a => a()) { LevelsDebounceMs = 1000 };   // far longer than the test's own sleeps: no timing margin to lose
        int delivered = 0;
        q.Completed += _ => Interlocked.Increment(ref delivered);
        var baseImg = Noise(120, 90, 8);
        var d = DocumentState.Create(baseImg).Add(new SpotlightBox(0, new RectI(30, 20, 40, 30), 0.5, 4, true, 30), out var obj);
        var sp = (SpotlightBox)obj;
        q.Request(d);
        Check.True(q.WaitIdle(5000));
        int builds = ops.SpotCache.Builds, started = q.Started;
        Check.Equal(1, builds);

        // a box move composes at once and only re-blends
        q.Request(d.Replace(sp with { Box = new RectI(35, 22, 40, 30) }));
        Check.True(WaitFor(() => q.Started == started + 1, 150), "no delay for a move");
        Check.True(q.WaitIdle(5000));
        Check.Equal(builds, ops.SpotCache.Builds, "a move builds no levels");

        // a radius drag: several changes; nothing is composed (no level build) until the last change has rested
        started = q.Started;
        var sw = Stopwatch.StartNew();
        for (int r = 5; r <= 9; r++)
        {
            q.Request(d.Replace(sp with { Radius = r }));
            Thread.Sleep(20);
        }
        Thread.Sleep(80);
        Check.Equal(started, q.Started, $"no compose while the slider moves (after {sw.ElapsedMilliseconds} ms)");
        Check.Equal(builds, ops.SpotCache.Builds);
        Check.True(q.WaitIdle(5000));
        Check.Equal(started + 1, q.Started, "one compose after the last change");
        Check.Equal(builds + 1, ops.SpotCache.Builds, "one build, for the last radius");
        Check.Equal(RasterOps.NewComposer().Compose(d.Replace(sp with { Radius = 9 })).Pixels, composer.Compose(d.Replace(sp with { Radius = 9 })).Pixels);
        q.Shutdown();
    }

    [UnitTest]
    public static void Shutdown_waits_for_the_compose_under_way_and_nothing_is_delivered_after_it()
    {
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var composer = new RasterComposer { OpFor = (o, _) => o is HighlightRect h ? new SlowOp(h, gate, started) : null };
        var q = new ComposeQueue(composer, a => a());
        int delivered = 0;
        q.Completed += _ => delivered++;
        var d = DocumentState.Create(Solid(30, 20, 9, 9, 9)).Add(new HighlightRect(0, new RectI(2, 2, 10, 8), Colors.Red), out _);
        q.Request(d);
        Check.True(started.Wait(5000));
        var release = new Thread(() => { Thread.Sleep(100); gate.Set(); });
        var sw = Stopwatch.StartNew();   // before the release thread starts: its 100 ms sleep is inside the measured time
        release.Start();
        q.Shutdown();   // returns only when the compose has finished: it is not cut off half-way
        Check.True(sw.ElapsedMilliseconds >= 80, "waited for the compose");
        release.Join();
        Check.Equal(0, delivered, "nothing reaches the UI after the shutdown");
        q.Request(d.Replace(((HighlightRect)d.Raster[0]) with { R = new RectI(5, 5, 4, 4) }));
        Thread.Sleep(30);
        Check.Equal(1, q.Started, "no new work after the shutdown");
    }

    // ------------------------------------------------------------------ the two-stage adjuster

    [UnitTest]
    public static void The_lut_shows_at_once_and_the_sharpened_base_follows_the_debounce_with_the_exact_library_result()
    {
        var baseImg = Noise(80, 60, 12);
        var v = new AdjustmentValues(15, 25, 120);
        var lut = new BgraImage(80, 60);
        Adjustments.Apply(baseImg, lut, lut.Bounds, Adjustments.BuildLut(15, 25));
        var full = SharpenThenLut(baseImg, v);

        // synchronously, without the queue
        var composer = new RasterComposer { DeferSharpness = true };
        var doc = DocumentState.Create(baseImg) with { Adjust = v };
        Check.Equal(lut.Pixels, composer.Compose(doc).Pixels, "stage 1 pending: the lookup table of the base only");
        Check.NotNull(composer.PendingSharpen);
        Check.True(composer.PrepareSharpened());
        Check.Equal(full.Pixels, composer.Compose(doc).Pixels, "settled: the LUT over the unsharp mask of the base");
        Check.Null(composer.PendingSharpen);
        Check.Equal(Adjuster.Full(v, baseImg).Pixels, full.Pixels, "the export's order");

        // through the queue
        var c2 = new RasterComposer { DeferSharpness = true };
        var q = new ComposeQueue(c2, a => a()) { SharpenDebounceMs = 60 };
        var shown = new List<byte[]>();
        q.Completed += r => { lock (shown) shown.Add((byte[])r.Image.Pixels.Clone()); };
        q.Request(doc);
        Check.True(WaitFor(() => { lock (shown) return shown.Count >= 1; }, 3000));
        lock (shown) Check.Equal(lut.Pixels, shown[0], "the first thing shown is the LUT result");
        Check.True(q.WaitIdle(5000));
        lock (shown)
        {
            Check.Equal(2, shown.Count, "then the sharpened picture");
            Check.Equal(full.Pixels, shown[1], "equal to the LUT of Adjustments.Unsharp of the base");
        }
        Check.Equal(1, q.SharpenJobs);
        q.Shutdown();
    }

    [UnitTest]
    public static void A_slider_change_during_the_debounce_cancels_the_pending_unsharp_mask()
    {
        var baseImg = Noise(80, 60, 13);
        BgraImage Expect(int sharp) => SharpenThenLut(baseImg, new AdjustmentValues(10, 0, sharp));
        var composer = new RasterComposer { DeferSharpness = true };
        var q = new ComposeQueue(composer, a => a()) { SharpenDebounceMs = 1000 };
        var shown = new List<byte[]>();
        q.Completed += r => { lock (shown) shown.Add((byte[])r.Image.Pixels.Clone()); };
        var s100 = DocumentState.Create(baseImg) with { Adjust = new AdjustmentValues(10, 0, 100) };
        var s150 = s100 with { Adjust = new AdjustmentValues(10, 0, 150) };
        q.Request(s100);
        Check.True(WaitFor(() => { lock (shown) return shown.Count >= 1; }, 3000));
        Thread.Sleep(30);          // inside the debounce
        q.Request(s150);           // the slider moved on
        Check.True(q.WaitIdle(8000));
        Check.Equal(1, q.SharpenJobs, "one unsharp mask ran: the one for the last value");
        lock (shown)
        {
            var old = Expect(100).Pixels;
            Check.False(shown.Any(s => s.SequenceEqual(old)), "the sharpened picture for the old value never appeared");
            Check.Equal(Expect(150).Pixels, shown[^1], "the last picture is the one for the last value");
        }
        q.Shutdown();
    }

    [UnitTest]
    public static void A_brightness_drag_over_a_sharpened_picture_keeps_the_sharpening_and_never_sharpens_again()
    {
        var baseImg = Noise(90, 70, 14);
        int sharpens = 0;
        var composer = RasterOps.NewComposer();
        composer.DeferSharpness = true;
        composer.SharpenBase = (s, b) => { sharpens++; return Adjuster.Sharpen(s, b); };
        var doc = DocumentState.Create(baseImg).Add(new HighlightRect(0, new RectI(10, 10, 30, 20), Colors.Yellow), out _);
        var settled = doc with { Adjust = new AdjustmentValues(0, 0, 100) };
        composer.Compose(settled);
        Check.True(composer.PrepareSharpened());
        composer.Compose(settled);
        Check.Equal(1, sharpens);
        // the brightness / contrast slider moves: each frame is the LUT over the cached sharpened base, at once
        for (int b = 5; b <= 40; b += 5)
        {
            var frame = doc with { Adjust = new AdjustmentValues(b, b / 2, 100) };
            var shown = composer.Compose(frame);
            Check.Null(composer.PendingSharpen, $"brightness {b}: nothing pending");
            Check.Equal(RasterOps.NewComposer().Compose(frame).Pixels, shown.Pixels, $"brightness {b}: the sharpening is kept (equals the full quality composite)");
        }
        Check.Equal(1, sharpens, "never sharpened again: the sharpened base depends on (picture, sharpness) only");
    }

    [UnitTest]
    public static void The_settled_preview_equals_the_export_for_blur_spotlight_magnify_and_sharpness()
    {
        var baseImg = Ramp(160, 120);
        for (int i = 0; i < baseImg.Pixels.Length; i += 37) baseImg.Pixels[i] ^= 0x5A;   // some detail for the unsharp mask
        var doc = DocumentState.Create(baseImg);
        doc = doc.Add(new BlurRegion(0, new RectI(10, 10, 50, 40), 3, false, 0), out _);
        doc = doc.Add(new MagnifyBox(0, new RectI(70, 10, 20, 15), new RectI(100, 60, 40, 30), Colors.White, 2, true), out _);
        doc = doc.Add(new SpotlightBox(0, new RectI(40, 30, 60, 50), 0.5, 4, true, 40), out var spot);
        var composer = RasterOps.NewComposer();
        composer.DeferSharpness = true;   // the live preview
        composer.Compose(doc);
        // the user moves the spotlight and drags the sharpness / brightness sliders
        doc = doc.Replace(((SpotlightBox)spot) with { Box = new RectI(45, 35, 60, 50) });
        composer.Compose(doc);
        doc = doc with { Adjust = new AdjustmentValues(12, -8, 90) };
        composer.Compose(doc);
        Check.NotNull(composer.PendingSharpen, "the sharpened base comes later");
        Check.True(composer.PrepareSharpened());
        var preview = composer.Compose(doc).Clone();
        var exported = Exporter.Export(doc);
        int delta = MaxDelta(exported, preview);
        Check.True(delta <= 1, $"the settled preview differs from the export by {delta} (first at {FirstDifference(exported, preview)})");
    }

    [UnitTest]
    public static void A_failing_sharpen_is_tried_once_and_the_unsharpened_picture_stays()
    {
        var baseImg = Noise(60, 40, 15);
        int calls = 0;
        var composer = new RasterComposer { DeferSharpness = true };
        composer.SharpenBase = (_, _) => { Interlocked.Increment(ref calls); throw new InvalidOperationException("no memory for the sharpened base"); };
        var q = new ComposeQueue(composer, a => a()) { SharpenDebounceMs = 10 };
        var shown = new List<byte[]>();
        q.Completed += r => { lock (shown) shown.Add((byte[])r.Image.Pixels.Clone()); };
        var doc = DocumentState.Create(baseImg) with { Adjust = new AdjustmentValues(10, 0, 80) };
        q.Request(doc);
        Check.True(q.WaitIdle(5000), "idle: the failure does not re-arm the sharpening");
        Thread.Sleep(150);   // ten debounces
        Check.Equal(1, calls, "tried once");
        Check.Equal(1, q.SharpenJobs);
        Check.Equal(1, q.SharpenFailures);
        Check.Null(composer.PendingSharpen, "nothing pending any more");
        lock (shown) Check.Equal(SharpenThenLut(baseImg, new AdjustmentValues(10, 0, 0)).Pixels, shown[^1], "the LUT of the unsharpened base stays");
        // the same values again (an undo, a slider back): still not tried again; another sharpness is
        composer.Compose(doc with { Adjust = new AdjustmentValues(30, 0, 80) });
        Check.Null(composer.PendingSharpen, "the failed (picture, sharpness) is remembered");
        composer.Compose(doc with { Adjust = new AdjustmentValues(30, 0, 90) });
        Check.NotNull(composer.PendingSharpen, "another sharpness is tried");
        q.Shutdown();
    }

    /// <summary>A highlight rectangle (the library's Highlight.Rect) whose Apply throws while <c>fail</c> says so.</summary>
    private sealed class FlakyOp : IRasterOp
    {
        private readonly HighlightRect _h;
        private readonly Func<bool> _fail;
        public FlakyOp(HighlightRect h, Func<bool> fail) { _h = h; _fail = fail; }
        public RectI Affects => _h.R;
        public int ReadMargin => 0;
        public void Apply(BgraImage src, BgraImage dst, RectI area)
        {
            if (_fail()) throw new InvalidOperationException("flaky op");
            Highlight.Rect(src, dst, area, _h.R, _h.Color);
        }
    }

    [UnitTest]
    public static void A_failed_compose_drops_the_caches_and_the_next_compose_equals_a_from_scratch_one()
    {
        int failures = 0;
        var composer = new RasterComposer { OpFor = (o, _) => o is HighlightRect h ? new FlakyOp(h, () => h.Color == Colors.Red && failures-- > 0) : null };
        var doc = DocumentState.Create(Noise(60, 40, 16));
        doc = doc.Add(new HighlightRect(0, new RectI(5, 5, 30, 20), Colors.Yellow), out _);
        doc = doc.Add(new HighlightRect(0, new RectI(20, 10, 30, 20), Colors.Cyan), out var top);
        composer.Compose(doc);
        var edited = doc.Replace(((HighlightRect)top) with { Color = Colors.Red });
        failures = 1;
        Check.Throws<InvalidOperationException>(() => composer.Compose(edited), "the op throws");
        Check.Equal(0, composer.HeldBuffers, "nothing half-updated is kept");
        var again = composer.Compose(edited);
        Check.Equal(RasterOps.NewComposer().Compose(edited).Pixels, again.Pixels, "the same state again: composed, from scratch (not the stale composite)");
        Check.Equal(composer.Compose(doc).Pixels, RasterOps.NewComposer().Compose(doc).Pixels, "and on from there");
    }

    [UnitTest]
    public static void A_failed_compose_is_retried_once_and_an_identical_request_afterwards_recomposes()
    {
        int failures = 2;
        var composer = new RasterComposer { OpFor = (o, _) => o is HighlightRect h ? new FlakyOp(h, () => failures-- > 0) : null };
        var q = new ComposeQueue(composer, a => a()) { Synchronous = true };
        var notices = new List<string>();
        q.Failed += notices.Add;
        int recovered = 0;
        q.Recovered += () => recovered++;
        var delivered = new List<byte[]>();
        q.Completed += r => delivered.Add((byte[])r.Image.Pixels.Clone());
        var doc = DocumentState.Create(Noise(50, 30, 17)).Add(new HighlightRect(0, new RectI(5, 5, 20, 10), Colors.Yellow), out _);
        q.Request(doc);
        Check.Equal(2, q.Started, "the failure was retried once, from scratch");
        Check.Equal(2, q.Failures);
        Check.Equal(0, delivered.Count);
        Check.Equal(new[] { ComposeQueue.RetryingNotice, ComposeQueue.GaveUpNotice }, notices.ToArray(), "the status line is told");
        Check.False(q.Busy, "no endless retries");
        q.Request(doc);   // the very same state: not ignored
        Check.Equal(3, q.Started, "an identical request after the failures recomposes");
        Check.Equal(1, delivered.Count);
        Check.Equal(1, recovered, "the status line is told that it works again (the \"Preview failed\" text goes)");
        Check.Equal(RasterOps.NewComposer().Compose(doc).Pixels, delivered[0], "and shows the right picture");
        q.Request(doc);
        Check.Equal(3, q.Started, "after a success the same state is again nothing to do");
        Check.Equal(1, recovered, "Recovered is raised once per failure, not on every success");
        q.Shutdown();
    }

    [UnitTest]
    public static void A_failure_of_a_stale_compose_does_not_use_up_the_retry_of_the_current_one()
    {
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        int calls = 0;
        // the first two applications fail: the stale compose's, and the first one of the newest state's
        var composer = new RasterComposer
        {
            OpFor = (o, _) => o is HighlightRect h ? new FlakyOp(h, () =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n == 1) { started.Set(); gate.Wait(10_000); }
                return n <= 2;
            }) : null,
        };
        var q = new ComposeQueue(composer, a => a());
        var notices = new List<string>();
        q.Failed += notices.Add;
        int delivered = 0;
        q.Completed += _ => Interlocked.Increment(ref delivered);
        var d0 = DocumentState.Create(Solid(30, 20, 9, 9, 9));
        q.Request(d0.Add(new HighlightRect(0, new RectI(2, 2, 10, 8), Colors.Red), out _));
        Check.True(started.Wait(5000));
        q.Request(d0.Add(new HighlightRect(0, new RectI(3, 3, 10, 8), Colors.Blue), out _));   // newer: the running one is stale
        gate.Set();
        Check.True(q.WaitIdle(5000));
        Check.Equal(3, q.Started, "stale (failed), newest (failed), newest again: its retry was not used up by the stale failure");
        Check.Equal(2, q.Failures);
        Check.Equal(1, delivered, "the retry of the newest state was delivered");
        Check.Equal(new[] { ComposeQueue.RetryingNotice }, notices.ToArray(), "no give-up message");
        q.Shutdown();
    }

    [UnitTest]
    public static void A_compose_that_keeps_failing_logs_the_first_failure_and_then_at_most_one_line_per_interval()
    {
        bool fail = true;
        var composer = new RasterComposer { OpFor = (o, _) => o is HighlightRect h ? new FlakyOp(h, () => fail) : null };
        var q = new ComposeQueue(composer, a => a()) { Synchronous = true };
        var d0 = DocumentState.Create(Solid(30, 20, 9, 9, 9));
        for (int i = 0; i < 40; i++) q.Request(d0.Add(new HighlightRect(0, new RectI(i % 10, 2, 10, 8), Colors.Red), out _));
        Check.True(q.Failures >= 40, $"{q.Failures} failures");
        Check.Equal(1, q.ErrorsLogged, "one line (with the stack trace) for the whole streak");
        Check.Equal(q.Failures - 1, q.ErrorsSuppressed, "the others are counted, not written");
        q.ErrorLogIntervalMs = 0;   // the interval has passed
        q.Request(d0.Add(new HighlightRect(0, new RectI(1, 1, 9, 8), Colors.Red), out _));
        Check.True(q.ErrorsLogged >= 2, "then another line once the interval has passed (with the count of the ones left out)");
        fail = false;
        q.Request(d0.Add(new HighlightRect(0, new RectI(2, 1, 9, 8), Colors.Red), out _));   // a success ends the streak
        fail = true;
        q.ErrorLogIntervalMs = 5000;
        int before = q.ErrorsLogged;
        q.Request(d0.Add(new HighlightRect(0, new RectI(3, 1, 9, 8), Colors.Red), out _));
        Check.Equal(before + 1, q.ErrorsLogged, "a new streak starts with a full line again");
        q.Shutdown();
    }

    [UnitTest]
    public static void A_failed_sharpen_job_still_recomposes_a_picture_that_shows_stale_spotlight_levels_once_the_gesture_is_over()
    {
        var baseImg = Noise(300, 200, 21);
        var composer = RasterOps.NewComposer(out var ops);
        composer.DeferSharpness = true;
        composer.SharpenBase = (_, _) => { Thread.Sleep(400); throw new InvalidOperationException("no memory for the sharpened base"); };
        var q = new ComposeQueue(composer, a => a()) { SharpenDebounceMs = 20 };
        var shown = new List<byte[]>();
        q.Completed += r => { lock (shown) shown.Add((byte[])r.Image.Pixels.Clone()); };
        var d0 = DocumentState.Create(baseImg).Add(new HighlightRect(0, new RectI(10, 10, 120, 80), Colors.Yellow), out var hl);
        d0 = d0.Add(new SpotlightBox(0, new RectI(150, 90, 100, 80), 0.5, 12, true, 40), out _);
        q.Request(d0);
        Check.True(q.WaitIdle(5000));
        // a gesture (the highlight moves under the spotlight) while a sharpness is set: levels of the older pixels are re-blended
        var moved = d0.Replace(((HighlightRect)hl) with { R = new RectI(120, 60, 120, 80) }) with { Adjust = new AdjustmentValues(0, 0, 80) };
        q.Request(moved, interactive: true);
        Check.True(WaitFor(() => { lock (shown) return shown.Count >= 2; }, 3000));
        Check.True(ops.SpotCache.StaleReuses >= 1, "the precondition: the interactive compose re-blended stale levels");
        Check.True(WaitFor(() => q.SharpenJobs == 1, 3000), "the sharpen job is running (its compose fails)");
        q.Request(moved, interactive: false);   // the gesture ends while the sharpen job runs: nothing can start now
        Check.True(q.WaitIdle(8000));
        Check.Equal(1, q.SharpenFailures);
        var fresh = RasterOps.NewComposer();
        fresh.DeferSharpness = true;
        lock (shown) Check.Equal(fresh.Compose(moved).Pixels, shown[^1], "the last picture shown has fresh levels");
        q.Shutdown();
    }

    [UnitTest]
    public static void The_highlight_mask_cache_is_bounded_and_a_long_freehand_drag_does_not_pile_up_masks()
    {
        var composer = RasterOps.NewComposer(out var ops);
        var doc = DocumentState.Create(Noise(1200, 800, 18));
        // a long freehand drag: every preview frame is a new stroke (one more point); only the shown one's mask stays
        var pts = new List<Point> { new(10, 10) };
        for (int i = 1; i <= 150; i++)
        {
            pts.Add(new Point(10 + i * 7.5, 10 + (i * 37) % 760));
            composer.Compose(doc.Add(new HighlightStroke(0, pts.ToImmutableArray(), 18, Colors.Yellow), out _));
            Check.True(ops.MaskCount <= 1, $"frame {i}: {ops.MaskCount} masks kept");
            Check.True(ops.MaskBytes <= RasterOps.DefaultMaskCacheBytes, $"frame {i}: {ops.MaskBytes} bytes of masks");
        }
        // many big strokes in the list: the cache stays within its byte budget, and the picture is still right
        ops.MaskCacheBytes = 2L << 20;
        var many = doc;
        for (int k = 0; k < 10; k++)
            many = many.Add(new HighlightStroke(0, ImmutableArray.Create(new Point(5 + k, 5), new Point(1190 - k, 790), new Point(5, 790 - k)), 12, s_colors[k % s_colors.Length]), out _);
        var shown = composer.Compose(many);
        Check.True(ops.MaskBytes <= ops.MaskCacheBytes, $"{ops.MaskBytes} bytes of masks for a {ops.MaskCacheBytes}-byte budget");
        Check.Equal(RasterOps.NewComposer().Compose(many).Pixels, shown.Pixels);
    }

    [UnitTest]
    public static void Stroke_ops_hold_no_mask_so_the_cap_bounds_the_real_bytes_at_2000_square()
    {
        const int N = 2000;
        var baseImg = Noise(N, N, 31);
        var doc = DocumentState.Create(baseImg);
        for (int k = 0; k < 6; k++)
            doc = doc.Add(new HighlightStroke(0, ImmutableArray.Create(new Point(k * 3, 0), new Point(N, N - k * 3)), 10, s_colors[k % s_colors.Length]), out _);
        // the footprint for layout is known without a mask, and is the mask's own bounds
        foreach (var o in doc.Raster.OfType<HighlightStroke>())
            Check.Equal(StrokeMask.Rasterize(o.Pts, o.Width, baseImg.Bounds).Bounds, StrokeMask.BoundsOf(o.Pts, o.Width, baseImg.Bounds), "BoundsOf = the mask's bounds");
        long one = StrokeMask.BoundsOf(((HighlightStroke)doc.Raster[0]).Pts, 10, baseImg.Bounds).Width * (long)StrokeMask.BoundsOf(((HighlightStroke)doc.Raster[0]).Pts, 10, baseImg.Bounds).Height;
        Check.True(one > 3_000_000, $"a large diagonal mask: {one} bytes");

        long cap = one + one / 4;   // one mask fits, two do not
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var composer = RasterOps.NewComposer(out var ops);
        ops.MaskCacheBytes = cap;
        composer.Compose(doc);
        Check.True(ops.MaskBytes <= cap, $"{ops.MaskBytes} bytes of masks for a cap of {cap}");
        // brightness changes over the strokes (the ops stay in the composer; nothing may keep their masks)
        for (int b = 10; b <= 50; b += 10)
        {
            composer.Compose(doc with { Adjust = new AdjustmentValues(b, 0, 0) });
            Check.True(ops.MaskBytes <= cap, $"after brightness {b}: {ops.MaskBytes} bytes of masks");
        }
        var held = composer.Memory;
        long managed = GC.GetTotalMemory(true) - before;
        // everything held = the composer's pictures + the cached masks (<= cap); the 6 masks together are 6 x `one` and must not be reachable
        long limit = held.Pictures + cap + (2L << 20);
        Check.True(managed <= limit, $"{managed / 1048576.0:0.0} MB held, limit {limit / 1048576.0:0.0} MB (pictures {held.Pictures / 1048576.0:0.0} MB, cap {cap / 1048576.0:0.0} MB); six masks would add {5 * one / 1048576.0:0.0} MB");
        Check.Equal(ops.MaskBytes, held.Caches, "the composer reports exactly the cached masks");

        // an evicted mask is rasterized again when its op is applied: the picture is the same as a composer with no cap at all
        var edited = doc.Replace(((HighlightStroke)doc.Raster[0]) with { Color = Colors.Red });
        var again = composer.Compose(edited);
        Check.True(ops.MaskBytes <= cap);
        Check.Equal(RasterOps.NewComposer().Compose(edited).Pixels, again.Pixels, "re-rasterized masks give the same picture");
        GC.KeepAlive(baseImg);
    }

    [UnitTest]
    public static void An_image_that_finishes_inserting_during_a_drag_waits_for_the_gesture_and_both_are_kept()
    {
        foreach (bool cancel in new[] { false, true })
        {
            var s = new EditorSession(Doc(300, 200));
            s.Clipboard = new FakeClipboard { Image = Noise(60, 40, 7, alpha: true) };
            var rect = s.Add(new HighlightRect(0, new RectI(20, 20, 50, 30), Colors.Yellow));
            int depth = s.UndoDepth;
            // a drag in progress: it keeps the document it started from (as the canvas's gestures do)
            var baseState = s.Committed;
            s.SetPreview(baseState.Replace(ObjectOps.Translate(rect, 40, 25)));
            var (ok, err) = Await(s.PasteImageAsync(new Point(150, 100)));
            Check.True(ok, err);
            Check.Equal(1, s.PendingInserts, "the picture waits for the gesture");
            Check.True(s.HasPreview, "the drag is not disturbed");
            Check.Equal(depth, s.UndoDepth);
            Check.Equal(1, s.State.Raster.Count, "nothing inserted yet");
            if (cancel) s.CancelPreview(); else Check.True(s.CommitPreview());
            Check.Equal(0, s.PendingInserts);
            var raster = s.Committed.Raster;
            Check.Equal(2, raster.Count, "the dragged object and the picture");
            Check.Equal(2, raster.Select(o => o.Id).Distinct().Count(), "unique ids");
            var ins = raster.OfType<InsertedImage>().Single();
            Check.True(ins.Id != rect.Id && ins.Id >= baseState.NextId, "a fresh id from the document it lands in");
            var hl = raster.OfType<HighlightRect>().Single();
            Check.Equal(cancel ? new RectI(20, 20, 50, 30) : new RectI(60, 45, 50, 30), hl.R, cancel ? "the cancelled drag changed nothing" : "the drag was kept");
            Check.Equal(cancel ? depth + 1 : depth + 2, s.UndoDepth, "one undo step each");
            Check.Equal(ins.Id, s.SelectedId, "the picture is selected");
            Check.True(s.Undo());
            Check.Equal(1, s.Committed.Raster.Count, "undo takes the picture away");
            if (!cancel) Check.Equal(new RectI(60, 45, 50, 30), s.Committed.Raster.OfType<HighlightRect>().Single().R, "and leaves the drag");
            s.Release();
        }
    }

    /// <summary>A clipboard that hands back a bitmap still to be converted (the real one's bitmap format).</summary>
    private sealed class BitmapClipboard : IImageClipboard
    {
        public BitmapSource? Bitmap;
        public BgraImage? GetImage(out string? error) => throw new InvalidOperationException("the session must use Read");
        public (BgraImage? Image, BitmapSource? Bitmap) Read(out string? error) { error = null; return (null, Bitmap); }
    }

    [UnitTest]
    public static void A_pasted_clipboard_bitmap_is_converted_scanned_and_reduced_after_the_clipboard_read()
    {
        var s = new EditorSession(Doc(300, 200));
        var px = new byte[600 * 400 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 120; px[i + 2] = 230; px[i + 3] = 0; }   // a CF_DIB: no alpha at all
        var bmp = BitmapSource.Create(600, 400, 96, 96, PixelFormats.Bgra32, null, px, 600 * 4);
        bmp.Freeze();
        s.Clipboard = new BitmapClipboard { Bitmap = bmp };
        var (ok, err) = Await(s.PasteImageAsync(new Point(150, 100)));
        Check.True(ok, err);
        var ins = (InsertedImage)s.Selected!;
        Check.Equal((300, 200), (ins.Image.Width, ins.Image.Height), "reduced to the picture's size");
        Check.False(ins.Image.HasTransparency(), "made opaque, as before");
        Check.Equal((byte)230, ins.Image.GetPixel(10, 10).R);
        s.Release();
    }

    [UnitTest]
    public static void The_memory_text_counts_the_base_the_canvas_bitmap_and_every_inserted_image_of_the_undo_stack()
    {
        var s = new EditorSession(Doc(300, 200));
        var a = Noise(40, 30, 1);
        var b = Noise(50, 20, 2);
        s.InsertImage(a, new Point(100, 100));
        s.InsertImage(b, new Point(150, 100));
        s.Duplicate();               // shares b: counted once
        s.Undo(); s.Undo(); s.Undo(); // all three undone: their images are still reachable (redo)
        s.Delete();                  // nothing selected after the undos: no-op
        var m = s.Memory();
        long baseBytes = s.Committed.Base.Pixels.LongLength;
        Check.Equal(baseBytes, m.Bases, "the base picture, once (every state shares it)");
        Check.Equal(300L * 200 * 4, m.Bitmap, "the WriteableBitmap");
        Check.Equal(a.Pixels.LongLength + b.Pixels.LongLength, m.Inserted, "each distinct inserted image once, undone states included");
        Check.True(m.Total >= m.Bases + m.Bitmap + m.Inserted);
        string text = s.MemoryText();
        static string Mb(long v) => (v / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Check.True(text.StartsWith(Mb(m.Total) + " MB held"), text);
        Check.True(text.Contains("base picture(s) " + Mb(m.Bases)) && text.Contains("canvas bitmap " + Mb(m.Bitmap)) && text.Contains("inserted images (all undo states) " + Mb(m.Inserted)), text);
        // a crop or an adjustment shares the base; a replaced base would be counted too
        s.Redo(); s.Redo();
        Check.Equal(a.Pixels.LongLength + b.Pixels.LongLength, s.Memory().Inserted);
        s.Release();
    }
    [UnitTest]
    public static void Below_a_spotlight_a_gesture_rebuilds_its_levels_at_most_every_120_ms_and_its_end_rebuilds_them()
    {
        var composer = RasterOps.NewComposer(out var ops);
        var doc = DocumentState.Create(Noise(400, 300, 19));
        doc = doc.Add(new HighlightRect(0, new RectI(20, 20, 60, 40), Colors.Yellow), out var hlObj);
        doc = doc.Add(new SpotlightBox(0, new RectI(100, 80, 150, 100), 0.5, 6, true, 60), out _);
        composer.Compose(doc);
        int builds0 = ops.SpotCache.Builds;
        Check.Equal(1, builds0);
        var hl = (HighlightRect)hlObj;

        // dragging the highlight under the spotlight: every frame changes the pixels below it
        composer.Interactive = true;
        const int frames = 40;
        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= frames; i++)
        {
            doc = doc.Replace(hl with { R = hl.R.Offset(i * 3, i) });
            composer.Compose(doc);
            Thread.Sleep(4);
        }
        double ms = sw.Elapsed.TotalMilliseconds;
        int built = ops.SpotCache.Builds - builds0;
        int bound = (int)Math.Ceiling(ms / SpotlightCache.RebuildDebounceMs) + 1;
        Check.True(built <= bound, $"{built} level builds for {frames} frames in {ms:0} ms (at most {bound})");
        Check.True(built < frames, "not one per frame");
        Check.True(ops.SpotCache.StaleReuses > 0, "re-blended with the previous levels meanwhile");
        // one or two more frames at once: right after a build the next frame is always a re-blend with stale levels
        for (int i = 1; i <= 2 && !composer.HasStaleOutput; i++)
        {
            doc = doc.Replace(hl with { R = hl.R.Offset(frames * 3 + i, frames) });
            composer.Compose(doc);
        }
        Check.True(composer.HasStaleOutput, "the composite holds stale levels at the end of the drag");

        // the gesture ends on the same state: the levels are rebuilt at once and the picture is exact
        composer.Interactive = false;
        int before = ops.SpotCache.Builds;
        var end = composer.Compose(doc);
        Check.Equal(before + 1, ops.SpotCache.Builds, "rebuilt at the end of the gesture");
        Check.False(composer.HasStaleOutput);
        Check.Equal(RasterOps.NewComposer().Compose(doc).Pixels, end.Pixels, "equal to a from-scratch composite");

        // through the queue: the same state, first during the gesture, then after it, is composed again
        var c2 = RasterOps.NewComposer(out var ops2);
        var q = new ComposeQueue(c2, a => a()) { Synchronous = true };
        byte[]? last = null;
        q.Completed += r => last = (byte[])r.Image.Pixels.Clone();
        var d0 = doc.Replace(hl);
        q.Request(d0);
        var moved = d0.Replace(hl with { R = hl.R.Offset(40, 20) });
        q.Request(moved, interactive: true);
        Check.Equal(1, ops2.SpotCache.Builds, "during the gesture: re-blended with the previous levels");
        int started = q.Started;
        q.Request(moved, interactive: false);   // the mouse is released: the same document
        Check.Equal(started + 1, q.Started, "composed again although the state is the same");
        Check.Equal(2, ops2.SpotCache.Builds, "with fresh levels");
        Check.Equal(RasterOps.NewComposer().Compose(moved).Pixels, last!, "the picture shown is exact");
        q.Shutdown();
    }

    // ------------------------------------------------------------------ session and canvas

    private static EditorCanvas CanvasOf(EditorSession session, double w = 1000, double h = 800)
    {
        session.ComposeSynchronously = true;
        var canvas = new EditorCanvas();
        canvas.Measure(new Size(w, h));
        canvas.Arrange(new Rect(0, 0, w, h));
        canvas.Attach(session);
        return canvas;
    }

    private static void Drag(EditorCanvas c, EditorTool tool, Point from, Point to, ModifierKeys mods = ModifierKeys.None)
    {
        c.BeginRasterRect(tool, from);
        c.DragTo(c.DocToView(to), mods);
        c.EndGesture(c.DocToView(to));
    }

    [UnitTest]
    public static void Dragging_with_a_raster_tool_makes_one_object_in_one_undo_step_with_the_tool_options()
    {
        var expected = new Dictionary<EditorTool, Type>
        {
            [EditorTool.Blur] = typeof(BlurRegion), [EditorTool.Pixelate] = typeof(BlurRegion), [EditorTool.Highlight] = typeof(HighlightRect),
            [EditorTool.Spotlight] = typeof(SpotlightBox), [EditorTool.Magnify] = typeof(MagnifyBox), [EditorTool.Erase] = typeof(SmartEraseRect),
        };
        foreach (var (tool, type) in expected)
        {
            var s = new EditorSession(Doc());
            var c = CanvasOf(s);
            s.SetTool(tool);
            int depth = s.UndoDepth;
            c.BeginRasterRect(tool, new Point(40, 30));
            c.DragTo(c.DocToView(new Point(120, 90)));
            Check.True(s.HasPreview, tool + ": a preview while dragging");
            Check.Equal(depth, s.UndoDepth, tool + ": not an undo step yet");
            Check.Equal(0, s.Committed.Raster.Count);
            c.EndGesture(c.DocToView(new Point(120, 90)));
            Check.Equal(depth + 1, s.UndoDepth, tool + ": one undo step");
            Check.Equal(1, s.Committed.Raster.Count);
            var o = s.Committed.Raster[0];
            Check.Equal(type, o.GetType(), tool.ToString());
            Check.Equal(o.Id, s.SelectedId, tool + ": selected");
            switch (o)
            {
                case BlurRegion b:
                    Check.Equal(new RectI(40, 30, 80, 60), b.R);
                    Check.Equal((tool == EditorTool.Pixelate, 8, 12), (b.Pixelate, b.Radius, b.Block), "defaults: blur 8, block 12");
                    break;
                case HighlightRect h: Check.Equal((new RectI(40, 30, 80, 60), Color.FromRgb(0xFF, 0xEB, 0x3B)), (h.R, h.Color)); break;
                case SpotlightBox sp:
                    Check.Equal(new RectI(40, 30, 80, 60), sp.Box);
                    Check.Equal((0.55, 12, false, 70), (sp.Dim, sp.Radius, sp.Gradient, sp.FalloffPx), "dim 0.55, radius 12, falloff 35% of the shorter side (200)");
                    break;
                case MagnifyBox m:
                    Check.Equal(new RectI(40, 30, 80, 60), m.Source);
                    Check.Equal((160, 120), (m.Dest.Width, m.Dest.Height), "2x");
                    Check.True(!m.Dest.Intersects(m.Source), "the copy appears beside the source");
                    Check.True(s.Committed.Base.Bounds.Contains(m.Dest), "inside the picture");
                    Check.Equal((Colors.White, 3.0, false), (m.Outline, m.OutlineWidth, m.Smooth), "white 3 px outline, crisp");
                    break;
                case SmartEraseRect e: Check.Equal(new RectI(40, 30, 80, 60), e.R); break;
            }
            Check.True(s.Undo());
            Check.Equal(0, s.Committed.Raster.Count, tool + ": undo removes it");
            c.Detach();
        }
    }

    [UnitTest]
    public static void A_click_without_a_drag_makes_no_raster_object_and_a_freehand_highlight_is_a_stroke()
    {
        var s = new EditorSession(Doc());
        var c = CanvasOf(s);
        s.SetTool(EditorTool.Blur);
        c.BeginRasterRect(EditorTool.Blur, new Point(40, 30));
        c.EndGesture(c.DocToView(new Point(40, 30)));
        Check.Equal(0, s.Committed.Raster.Count);
        Check.False(s.IsDirty);

        s.SetTool(EditorTool.Highlight);
        c.BeginHighlightStroke(new Point(20, 20));
        foreach (var p in new[] { new Point(40, 25), new Point(70, 40), new Point(100, 38) }) c.DragTo(c.DocToView(p));
        c.EndGesture(c.DocToView(new Point(100, 38)));
        Check.Equal(1, s.Committed.Raster.Count);
        var stroke = (HighlightStroke)s.Committed.Raster[0];
        Check.Equal((18.0, Color.FromRgb(0xFF, 0xEB, 0x3B)), (stroke.Width, stroke.Color), "freehand width 18, #FFEB3B");
        Check.True(stroke.Pts.Length >= 2);
        Check.Equal(2, s.UndoDepth);

        // freehand is the option's toggle only; with a rectangle, Shift means a square as with every rectangle tool
        s.SetHighlightFreehand(true);
        Check.True(s.Options.HighlightFreehand);
        s.SetHighlightFreehand(false);
        Drag(c, EditorTool.Highlight, new Point(150, 20), new Point(190, 90), ModifierKeys.Shift);
        var square = (HighlightRect)s.Committed.Raster[^1];
        Check.Equal(square.R.Width, square.R.Height, $"Shift keeps the highlight square ({square.R})");
        c.Detach();
    }

    [UnitTest]
    public static void Raster_objects_move_and_resize_by_their_handles_delete_duplicate_and_reorder_inside_the_raster_band()
    {
        var s = new EditorSession(Doc());
        var c = CanvasOf(s);
        s.SetTool(EditorTool.Blur);
        Drag(c, EditorTool.Blur, new Point(40, 30), new Point(120, 90));
        s.SetTool(EditorTool.Select);
        var blur = s.Selected!;
        int depth = s.UndoDepth;

        // resize with the bottom-right handle
        c.BeginSelect(new Point(120, 90), 1);
        c.DragTo(c.DocToView(new Point(150, 110)));
        c.EndGesture(c.DocToView(new Point(150, 110)));
        Check.Equal(new RectI(40, 30, 110, 80), ((BlurRegion)s.Selected!).R);
        Check.Equal(depth + 1, s.UndoDepth, "one undo step");
        // move by dragging the inside
        c.BeginSelect(new Point(80, 60), 1);
        c.DragTo(c.DocToView(new Point(90, 70)));
        c.EndGesture(c.DocToView(new Point(90, 70)));
        Check.Equal(new RectI(50, 40, 110, 80), ((BlurRegion)s.Selected!).R);
        Check.Equal(depth + 2, s.UndoDepth);

        // add more raster objects and a vector one; reorder / duplicate / delete stay in the band
        s.Add(new HighlightRect(0, new RectI(10, 10, 20, 20), Colors.Yellow));
        s.Add(new RectOutline(0, new Rect(5, 5, 50, 50), Colors.Red, 2));
        var hl2 = s.Add(new HighlightRect(0, new RectI(60, 10, 20, 20), Colors.Cyan));
        Check.Equal(3, s.Committed.Raster.Count);
        Check.Equal(1, s.Committed.Vector.Count);
        Check.True(s.Reorder(int.MinValue));
        Check.Equal(hl2.Id, s.Committed.Raster[0].Id, "to the back of the raster band");
        Check.Equal(1, s.Committed.Vector.Count);
        Check.True(s.Duplicate());
        Check.Equal(4, s.Committed.Raster.Count, "the copy is in the raster band");
        Check.Equal(1, s.Committed.Vector.Count);
        Check.True(s.Delete());
        Check.Equal(3, s.Committed.Raster.Count);
        s.Select(blur.Id);
        Check.True(s.Reorder(5));
        Check.Equal(blur.Id, s.Committed.Raster[^1].Id, "to the front of the raster band, never past a vector object");
        c.Detach();
    }

    [UnitTest]
    public static void The_magnifier_keeps_the_proportions_of_its_source_and_its_source_frame_can_move()
    {
        var s = new EditorSession(Doc());
        var c = CanvasOf(s);
        Drag(c, EditorTool.Magnify, new Point(30, 20), new Point(90, 60));   // 60 x 40 source
        s.SetTool(EditorTool.Select);
        var m = (MagnifyBox)s.Selected!;
        Check.Equal((120, 80), (m.Dest.Width, m.Dest.Height));

        foreach (var handle in new[] { Handle.BottomRight, Handle.Right, Handle.Bottom, Handle.TopLeft, Handle.Left, Handle.Top })
        {
            var cur = (MagnifyBox)s.Selected!;
            var at = HitTest.HandlesOf(cur).First(h => h.Handle == handle).At;
            c.BeginSelect(at, 1);
            c.DragTo(c.DocToView(new Point(at.X + 14, at.Y + 9)));
            c.EndGesture(c.DocToView(new Point(at.X + 14, at.Y + 9)));
            var after = (MagnifyBox)s.Selected!;
            double ratio = after.Dest.Width / (double)after.Dest.Height;
            Check.Near(1.5, ratio, 0.05, $"{handle}: the copy keeps 3:2 ({after.Dest.Width}x{after.Dest.Height})");
            Check.True(after.Dest.Width != cur.Dest.Width, $"{handle}: it changed size");
            Check.Equal(m.Source, after.Source, "the source is not touched by resizing the copy");
        }

        // dragging inside the dashed source frame moves the source
        var before = (MagnifyBox)s.Selected!;
        int depth = s.UndoDepth;
        c.BeginSelect(new Point(40, 30), 1);
        c.DragTo(c.DocToView(new Point(50, 36)));
        c.EndGesture(c.DocToView(new Point(50, 36)));
        var moved = (MagnifyBox)s.Selected!;
        Check.Equal(new RectI(before.Source.X + 10, before.Source.Y + 6, before.Source.Width, before.Source.Height), moved.Source);
        Check.Equal(before.Dest, moved.Dest, "the copy stays");
        Check.Equal(depth + 1, s.UndoDepth);

        // the zoom option re-sizes the copy of the selected magnifier
        s.SetMagnifyZoom(3);
        var z = (MagnifyBox)s.Selected!;
        Check.Equal((moved.Source.Width * 3, moved.Source.Height * 3), (z.Dest.Width, z.Dest.Height));
        c.Detach();
    }

    [UnitTest]
    public static void Magnify_placement_goes_beside_the_source_inside_the_picture()
    {
        var bounds = new RectI(0, 0, 300, 200);
        Check.Equal(new RectI(92, 20, 120, 80), MagnifyLayout.PlaceDest(new RectI(20, 20, 60, 40), 2, bounds), "right of the source, 12 px away, level with its top");
        var source = new RectI(230, 20, 60, 40);
        var below = MagnifyLayout.PlaceDest(source, 2, bounds);
        Check.True(bounds.Contains(below) && !below.Intersects(source), $"no room on the right: below it, {below}");
        Check.Equal(72, below.Y);
        var big = MagnifyLayout.PlaceDest(new RectI(10, 10, 280, 180), 4, bounds);
        Check.True(bounds.Contains(big), "never bigger than the picture");
        Check.Near(280 / 180.0, big.Width / (double)big.Height, 0.02, "and the proportions are kept");
        var zoomed = MagnifyLayout.WithZoom(new RectI(92, 20, 120, 80), new RectI(20, 20, 60, 40), 3, bounds);
        Check.Equal((180, 120), (zoomed.Width, zoomed.Height));
        Check.True(bounds.Contains(zoomed));
    }

    [UnitTest]
    public static void Raster_options_are_clamped_remembered_in_the_settings_and_restyle_the_selected_object()
    {
        var o = new ScreenshotEditorOptions { BlurRadius = 999, PixelateBlock = 0, HighlightColor = "yellow", HighlightWidth = double.NaN, SpotDim = 5, SpotRadius = -3, SpotFalloffPercent = 1, MagnifyZoom = 100, MagnifyOutlineColor = "#12", MagnifyOutlineWidth = 99, ImageOpacity = 0 };
        o.Normalize();
        Check.Equal((40, 2, ScreenshotEditorOptions.DefaultHighlightColor, 18.0), (o.BlurRadius, o.PixelateBlock, o.HighlightColor, o.HighlightWidth));
        Check.Equal((0.95, 0, 5, 8.0), (o.SpotDim, o.SpotRadius, o.SpotFalloffPercent, o.MagnifyZoom));
        Check.Equal((ScreenshotEditorOptions.DefaultOutlineColor, 12.0, 0.1), (o.MagnifyOutlineColor, o.MagnifyOutlineWidth, o.ImageOpacity));
        var d = new ScreenshotEditorOptions();
        Check.Equal((8, 12, 0.55, 12, false, 35, 2.0, true, "#FFFFFF", 18.0, 1.0), (d.BlurRadius, d.PixelateBlock, d.SpotDim, d.SpotRadius, d.SpotGradient, d.SpotFalloffPercent, d.MagnifyZoom, d.MagnifyOutline, d.MagnifyOutlineColor, d.HighlightWidth, d.ImageOpacity), "the documented defaults");

        ScreenshotEditorOptions? persisted = null;
        var s = new EditorSession(Doc(), new ScreenshotEditorOptions(), p => persisted = p);
        s.SetBlurRadius(500);
        Check.Equal(40, s.Options.BlurRadius, "clamped");
        s.SetPixelateBlock(20); s.SetHighlightColor(Colors.LimeGreen); s.SetHighlightWidth(30); s.SetHighlightFreehand(true);
        s.SetSpotDim(0.7); s.SetSpotRadius(20); s.SetSpotGradient(true); s.SetSpotFalloff(50);
        s.SetMagnifyZoom(3); s.SetMagnifyOutline(false); s.SetMagnifyOutlineColor(Colors.Red); s.SetMagnifySmooth(true); s.SetImageOpacity(0.5);
        Check.Null(persisted, "written when the editor closes");
        s.FlushOptions();
        Check.Equal((40, 20, 30.0, true), (persisted!.BlurRadius, persisted.PixelateBlock, persisted.HighlightWidth, persisted.HighlightFreehand));
        Check.Equal((0.7, 20, true, 50, 3.0, false, "#FF0000", true, 0.5), (persisted.SpotDim, persisted.SpotRadius, persisted.SpotGradient, persisted.SpotFalloffPercent, persisted.MagnifyZoom, persisted.MagnifyOutline, persisted.MagnifyOutlineColor, persisted.MagnifySmooth, persisted.ImageOpacity));

        // through the settings file
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = new Settings();
        settings.ScreenshotEditor = persisted;
        var back = Settings.FromJson(settings.ToJson());
        Check.Equal((40, 20, "#32CD32", 3.0, 50), (back.ScreenshotEditor.BlurRadius, back.ScreenshotEditor.PixelateBlock, back.ScreenshotEditor.HighlightColor, back.ScreenshotEditor.MagnifyZoom, back.ScreenshotEditor.SpotFalloffPercent));
        var hostile = Settings.FromJson("""{ "ScreenshotEditor": { "BlurRadius": 9000, "SpotDim": -4, "MagnifyZoom": 0 } }""");
        Check.Equal((40, 0.0, 1.25), (hostile.ScreenshotEditor.BlurRadius, hostile.ScreenshotEditor.SpotDim, hostile.ScreenshotEditor.MagnifyZoom), "clamped on load");

        // the options restyle the selected object, a slider drag is a preview and the release is one undo step
        var s2 = new EditorSession(Doc());
        var blur = s2.Add(new BlurRegion(0, new RectI(10, 10, 50, 40), 8, false, 12));
        int depth = s2.UndoDepth;
        s2.SetBlurRadius(20, final: false);
        Check.Equal(20, ((BlurRegion)s2.State.Raster[0]).Radius, "shown at once");
        Check.Equal(8, ((BlurRegion)s2.Committed.Raster[0]).Radius, "but a preview");
        s2.SetBlurRadius(22, final: false);
        s2.CommitPreview();
        Check.Equal(depth + 1, s2.UndoDepth);
        Check.Equal(22, ((BlurRegion)s2.Selected!).Radius);
        var sp = s2.Add(new SpotlightBox(0, new RectI(5, 5, 50, 40), 0.55, 12, false, 70));
        s2.SetSpotGradient(true);
        s2.SetSpotFalloff(10);
        var spot = (SpotlightBox)s2.Selected!;
        Check.Equal((true, 20), (spot.Gradient, spot.FalloffPx), "10% of the shorter side (200)");
        var hl = s2.Add(new HighlightRect(0, new RectI(5, 5, 10, 10), Colors.Yellow));
        s2.SetHighlightColor(Colors.Cyan);
        Check.Equal(Colors.Cyan, ((HighlightRect)s2.Selected!).Color);
        _ = blur; _ = sp; _ = hl;
    }

    [UnitTest]
    public static void Adjustments_are_a_preview_while_dragging_one_undo_step_at_the_release_and_reset()
    {
        var s = new EditorSession(Doc());
        int depth = s.UndoDepth;
        s.SetAdjust(new AdjustmentValues(10, 0, 0), final: false);
        s.SetAdjust(new AdjustmentValues(40, 0, 0), final: false);
        Check.Equal(40, s.State.Adjust.Brightness);
        Check.Equal(0, s.Committed.Adjust.Brightness, "only a preview");
        Check.Equal(depth, s.UndoDepth);
        s.CommitPreview();
        Check.Equal(depth + 1, s.UndoDepth, "one step per slider release");
        Check.Equal(40, s.Committed.Adjust.Brightness);
        s.SetAdjust(new AdjustmentValues(400, -400, 900), final: true);
        Check.Equal(new AdjustmentValues(100, -100, 200), s.Committed.Adjust, "clamped to the slider ranges");
        s.SetAdjust(s.Committed.Adjust, final: true);
        Check.Equal(depth + 2, s.UndoDepth, "the same values again: no step");
        Check.True(s.ResetAdjust());
        Check.True(s.Committed.Adjust.IsIdentity);
        s.Undo();
        Check.Equal(100, s.Committed.Adjust.Brightness, "reset is undoable");

        // a reset of a picture that is not adjusted is nothing: no undo step, not dirty
        var fresh = new EditorSession(Doc());
        int d0 = fresh.UndoDepth;
        Check.False(fresh.ResetAdjust(), "nothing to reset");
        Check.Equal(d0, fresh.UndoDepth, "no undo step");
        Check.False(fresh.IsDirty, "not dirty");
    }

    // ------------------------------------------------------------------ inserted images

    private sealed class FakeClipboard : IImageClipboard
    {
        public BgraImage? Image;
        public string? Error;
        public int Reads;
        public BgraImage? GetImage(out string? error) { Reads++; error = Image == null ? Error : null; return Image; }
    }

    [UnitTest]
    public static void An_inserted_image_is_fitted_to_at_most_half_the_picture_centred_and_keeps_its_proportions()
    {
        // smaller than half: its own size, centred on the point
        Check.Equal(new RectI(130, 90, 40, 20), InsertPlacement.Fit(40, 20, 300, 200, new Point(150, 100)));
        // bigger: scaled to fit 50% in both directions, proportions kept
        var big = InsertPlacement.Fit(1000, 400, 300, 200, new Point(150, 100));
        Check.Equal((150, 60), (big.Width, big.Height), "half the width (the limiting side)");
        Check.Equal(new RectI(75, 70, 150, 60), big);
        var tall = InsertPlacement.Fit(200, 800, 300, 200, new Point(150, 100));
        Check.Equal((25, 100), (tall.Width, tall.Height));
        // near an edge it is kept inside the picture
        var edge = InsertPlacement.Fit(100, 100, 300, 200, new Point(299, 199));
        Check.True(new RectI(0, 0, 300, 200).Contains(edge), edge.ToString());
        var corner = InsertPlacement.Fit(100, 100, 300, 200, new Point(-50, -50));
        Check.Equal(new RectI(0, 0, 100, 100), corner);
        Check.Equal((1, 1), (InsertPlacement.Fit(1, 1, 300, 200, new Point(0, 0)).Width, InsertPlacement.Fit(1, 1, 300, 200, new Point(0, 0)).Height));

        // resizing an inserted image keeps its proportions (Shift or not)
        var img = Noise(80, 40, 1, alpha: true);
        var obj = new InsertedImage(1, img, new RectI(10, 10, 80, 40), 1);
        var resized = (InsertedImage)ObjectOps.Resize(obj, Handle.BottomRight, new Point(130, 100), keepAspect: false);
        Check.Near(2.0, resized.Dest.Width / (double)resized.Dest.Height, 0.05, $"2:1 kept ({resized.Dest})");
    }

    [UnitTest]
    public static void Inserting_and_pasting_pictures_goes_through_the_session_with_the_limits()
    {
        var s = new EditorSession(Doc(300, 200));
        var fake = new FakeClipboard { Image = Noise(1000, 400, 2, alpha: true) };
        s.Clipboard = fake;
        string? note = null;
        s.Notice += m => note = m;
        int depth = s.UndoDepth;
        var (ok1, err) = Await(s.PasteImageAsync(new Point(150, 100)));
        Check.True(ok1, err);
        Check.Equal(1, fake.Reads);
        Check.Equal(depth + 1, s.UndoDepth, "one undo step");
        var ins = (InsertedImage)s.Selected!;
        Check.Equal(new RectI(75, 70, 150, 60), ins.Dest);
        Check.Equal((300, 120), (ins.Image.Width, ins.Image.Height), "stored reduced to the picture's size (1000 x 400 -> 300 x 120)");
        Check.Equal(1.0, ins.Opacity);
        Check.Equal(EditorTool.Select, s.Tool);
        Check.Equal(1, s.Committed.Raster.Count, "in the raster band");

        s.SetImageOpacity(0.4);
        Check.Equal(0.4, ((InsertedImage)s.Selected!).Opacity);
        Check.True(Await(s.PasteImageAsync(new Point(10, 10))).Ok);
        Check.Equal(0.4, ((InsertedImage)s.Selected!).Opacity, "the next one starts at the last opacity");

        fake.Image = null;
        fake.Error = "There is no picture on the clipboard";
        int before = s.UndoDepth;
        (ok1, err) = Await(s.PasteImageAsync(new Point(0, 0)));
        Check.False(ok1);
        Check.Equal("There is no picture on the clipboard", err);
        Check.Equal(err, note, "the user is told");
        Check.Equal(before, s.UndoDepth);

        // the size limits (16384 px on a side, 100 megapixels)
        Check.True(ImageInput.WithinLimits(16384, 6103));
        Check.False(ImageInput.WithinLimits(16385, 1));
        Check.False(ImageInput.WithinLimits(10001, 10000));
        Check.True(ImageInput.WithinLimits(10000, 10000));
        Check.False(ImageInput.WithinLimits(0, 5));

        using var dir = new TempDir();
        string ok = dir.File("ok.png");
        File.WriteAllBytes(ok, PngFile.Encode(Noise(20, 10, 3).ToBitmapSource()));
        (ok1, err) = Await(s.InsertImageFileAsync(ok, new Point(100, 100)));
        Check.True(ok1, err);
        var fromFile = (InsertedImage)s.Selected!;
        Check.Equal((20, 10), (fromFile.Image.Width, fromFile.Image.Height));
        Check.Equal(Noise(20, 10, 3).Pixels, fromFile.Image.Pixels, "decoded as Bgra32, exactly");

        string large = dir.File("large.png");   // bigger than the 300 x 200 picture: decoded and reduced on a worker
        File.WriteAllBytes(large, PngFile.Encode(Noise(900, 450, 4).ToBitmapSource()));
        Check.True(Await(s.InsertImageFileAsync(large, new Point(100, 100))).Ok);
        var reduced = (InsertedImage)s.Selected!;
        Check.Equal((300, 150), (reduced.Image.Width, reduced.Image.Height), "stored at most at the picture's size, proportions kept");

        string wide = dir.File("wide.png");   // 16385 x 1: one pixel over the side limit, tiny on disk
        File.WriteAllBytes(wide, PngFile.Encode(new BgraImage(16385, 1).ForceOpaque().ToBitmapSource()));
        int count = s.Committed.Raster.Count;
        (ok1, err) = Await(s.InsertImageFileAsync(wide, new Point(100, 100)));
        Check.False(ok1);
        Check.Equal(ImageInput.TooLarge, err);
        Check.Equal(count, s.Committed.Raster.Count, "nothing was inserted");
        Check.Equal(ImageInput.TooLarge, note, "the refusal toast / status line");
        string junk = dir.File("junk.png");
        File.WriteAllText(junk, "this is not a picture");
        (ok1, err) = Await(s.InsertImageFileAsync(junk, new Point(1, 1)));
        Check.False(ok1);
        Check.Equal(ImageInput.Unreadable, err);
        Check.False(Await(s.InsertImageFileAsync(dir.File("missing.png"), new Point(1, 1))).Ok);
    }

    /// <summary>Runs the dispatcher until <paramref name="t"/> is done (the session finishes an insert on the UI thread).</summary>
    private static T Await<T>(Task<T> t)
    {
        Pump(() => t.IsCompleted, 20_000);
        Check.True(t.IsCompleted, "the task finished");
        return t.Result;
    }

    [UnitTest]
    public static void An_inserted_picture_bigger_than_the_screenshot_is_stored_at_most_at_its_size()
    {
        var s = new EditorSession(Doc(300, 200));
        var photo = new BgraImage(8000, 6000).ForceOpaque();   // a 48 MP photo: 192 MB decoded
        s.Clipboard = new FakeClipboard { Image = photo };
        var (ok, err) = Await(s.PasteImageAsync(new Point(150, 100)));
        Check.True(ok, err);
        var ins = (InsertedImage)s.Selected!;
        Check.False(ReferenceEquals(photo, ins.Image), "not the full decoded picture");
        Check.True(ins.Image.Width <= 300 && ins.Image.Height <= 200, $"stored at {ins.Image.Width} x {ins.Image.Height}");
        Check.Equal((267, 200), (ins.Image.Width, ins.Image.Height), "4:3 kept");
        var composer = RasterOps.NewComposer();
        composer.Compose(s.Committed);
        Check.Equal(ins.Image.Pixels.LongLength, composer.Memory.Inserted, "the memory diagnostic counts the inserted picture");
        // a picture that fits is stored as it is (no copy)
        var small = Noise(40, 30, 5);
        Check.True(ReferenceEquals(small, ImageInput.FitWithin(small, 300, 200)));
        s.Release();
    }

    [UnitTest]
    public static void Picture_sizes_are_read_from_the_png_jpeg_and_bmp_headers_before_any_decoding()
    {
        static byte[] Encode(BitmapEncoder enc, BgraImage img)
        {
            enc.Frames.Add(BitmapFrame.Create(img.ToBitmapSource()));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        Check.Equal(((int, int)?)(20, 10), ImageInput.PeekSize(new MemoryStream(PngFile.Encode(Noise(20, 10, 3).ToBitmapSource()))), "png");
        Check.Equal(((int, int)?)(13, 7), ImageInput.PeekSize(new MemoryStream(Encode(new BmpBitmapEncoder(), Noise(13, 7, 1)))), "bmp");
        Check.Equal(((int, int)?)(21, 9), ImageInput.PeekSize(new MemoryStream(Encode(new JpegBitmapEncoder(), Noise(21, 9, 2)))), "jpeg");
        Check.Null(ImageInput.PeekSize(new MemoryStream(Encode(new GifBitmapEncoder(), Noise(5, 5, 2)))), "another format: WIC reads it lazily");

        // made-up headers of 20000 x 20000 with no pixels behind them: refused from the header
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
        jpeg.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x4E, 0x20, 0x4E, 0x20, 0x03, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
        Check.Equal(((int, int)?)(20000, 20000), ImageInput.PeekSize(new MemoryStream(jpeg.ToArray())));
        Check.Null(ImageInput.FromStream(new MemoryStream(jpeg.ToArray()), out var err));
        Check.Equal(ImageInput.TooLarge, err, "jpeg");
        var bmp = new byte[54];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(20000).CopyTo(bmp, 18);
        BitConverter.GetBytes(-20000).CopyTo(bmp, 22);   // top-down
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)32).CopyTo(bmp, 28);
        Check.Equal(((int, int)?)(20000, 20000), ImageInput.PeekSize(new MemoryStream(bmp)));
        Check.Null(ImageInput.FromStream(new MemoryStream(bmp), out err));
        Check.Equal(ImageInput.TooLarge, err, "bmp");
        // the clipboard's CF_DIB starts at the info header
        Check.Equal(((int, int)?)(20000, 20000), ImageInput.BmpInfoSize(bmp.AsSpan(14)));
    }

    [UnitTest]
    public static void A_clipboard_bitmap_with_no_alpha_at_all_pastes_opaque()
    {
        var px = new byte[5 * 3 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 120; px[i + 2] = 230; px[i + 3] = 0; }   // a CF_DIB: the fourth byte is 0
        var dib = BitmapSource.Create(5, 3, 96, 96, PixelFormats.Bgra32, null, px, 20);
        var img = ImageInput.FromBitmap(dib, out var err);
        Check.NotNull(img, err);
        Check.False(img!.HasTransparency(), "made opaque");
        Check.Equal(((byte)10, (byte)120, (byte)230, (byte)255), img.GetPixel(2, 1), "the colours as they are");
        // a bitmap with real transparency keeps it
        px[3] = 128;
        var withAlpha = ImageInput.FromBitmap(BitmapSource.Create(5, 3, 96, 96, PixelFormats.Bgra32, null, px, 20), out _)!;
        Check.Equal((byte)128, withAlpha.GetPixel(0, 0).A);
        Check.Equal((byte)0, withAlpha.GetPixel(1, 0).A);
    }

    [UnitTest]
    public static void The_picker_model_reads_the_history_and_the_files_on_a_worker_never_on_the_calling_thread()
    {
        int caller = Environment.CurrentManagedThreadId;
        var calls = new System.Collections.Concurrent.ConcurrentQueue<(string What, int Thread)>();
        var entries = Enumerable.Range(0, 5).Select(i => new ScreenshotEntry { Path = $@"C:\shots\s{i}.png" }).ToList();
        var model = new InsertPickerModel(
            () => { calls.Enqueue(("history", Environment.CurrentManagedThreadId)); return (entries, new[] { @"C:\shots\s1.png" }); },
            p => { calls.Enqueue(("file " + p, Environment.CurrentManagedThreadId)); return !p.EndsWith("s3.png"); },
            p => { calls.Enqueue(("thumbnail " + p, Environment.CurrentManagedThreadId)); return null; });
        Check.Equal(0, calls.Count, "making the model reads nothing");
        var load = model.LoadAsync();
        Check.True(load.Wait(5000));
        Check.Equal(new[] { @"C:\shots\s0.png", @"C:\shots\s2.png", @"C:\shots\s4.png" }, load.Result.Select(e => e.Path).ToArray(), "the open one and the missing one left out");
        Check.True(model.ThumbnailAsync(load.Result[0].Path).Wait(5000));
        Check.True(calls.Any(c => c.What == "history") && calls.Any(c => c.What.StartsWith("file")) && calls.Any(c => c.What.StartsWith("thumbnail")));
        Check.True(calls.All(c => c.Thread != caller), "every read ran on a worker: " + string.Join(", ", calls.Select(c => $"{c.What}@{c.Thread}")));
        // and a real thumbnail is decoded small and frozen, and an over-limit header is not decoded at all
        using var dir = new TempDir();
        string png = dir.File("t.png");
        File.WriteAllBytes(png, PngFile.Encode(Noise(400, 200, 6).ToBitmapSource()));
        var thumb = InsertPickerModel.DecodeThumbnail(png);
        Check.NotNull(thumb);
        Check.Equal(InsertPickerModel.ThumbnailDecodeWidth, thumb!.PixelWidth);
        Check.True(thumb.IsFrozen);
        string wide = dir.File("wide.png");
        File.WriteAllBytes(wide, PngFile.Encode(new BgraImage(16385, 1).ForceOpaque().ToBitmapSource()));
        Check.Null(InsertPickerModel.DecodeThumbnail(wide), "refused from its header");
    }

    [UnitTest]
    public static void The_picker_lists_recent_screenshots_newest_first_without_the_open_one_and_without_missing_files()
    {
        var entries = Enumerable.Range(0, 40).Select(i => new ScreenshotEntry { Path = $@"C:\shots\s{i:00}.png", Created = new DateTime(2026, 1, 1).AddHours(-i), Width = 10, Height = 10 }).ToList();
        var list = InsertPickerModel.Candidates(entries, new[] { @"C:\shots\S01.png", @"C:\shots\..\shots\s03.png" }, p => !p.EndsWith("s02.png"));
        Check.Equal(24, list.Count, "at most 24");
        Check.Equal(@"C:\shots\s00.png", list[0].Path, "newest first (the order of the history)");
        Check.False(list.Any(e => e.Path.EndsWith("s01.png")), "the file being edited (case-insensitively) is left out");
        Check.False(list.Any(e => e.Path.EndsWith("s03.png")), "also by its normalised path");
        Check.False(list.Any(e => e.Path.EndsWith("s02.png")), "a file that is gone is left out");
        Check.Equal(@"C:\shots\s04.png", list[1].Path, "the next after the skipped ones");
    }

    [UnitTest]
    public static void Fifty_raster_objects_warn_once_and_the_warning_comes_back_after_dropping_below()
    {
        var s = new EditorSession(Doc());
        var notes = new List<string>();
        s.Notice += notes.Add;
        for (int i = 0; i < 49; i++) s.Add(new HighlightRect(0, new RectI(i, i % 20, 5, 5), Colors.Yellow));
        Check.Equal(0, notes.Count);
        s.Add(new HighlightRect(0, new RectI(1, 1, 5, 5), Colors.Yellow));
        Check.Equal(1, notes.Count, "at 50");
        Check.Contains(notes[0], "50");
        s.Add(new HighlightRect(0, new RectI(2, 2, 5, 5), Colors.Yellow));
        Check.Equal(1, notes.Count, "not again at 51");
        s.Undo(); s.Undo();   // 49 again
        s.Add(new HighlightRect(0, new RectI(3, 3, 5, 5), Colors.Yellow));
        Check.Equal(2, notes.Count, "below 50 and up to 50 again: warned again");
        s.Add(new HighlightRect(0, new RectI(4, 4, 5, 5), Colors.Yellow));
        Check.Equal(2, notes.Count, "51: quiet");
    }

    [UnitTest]
    public static void Closing_the_session_stops_the_queue_and_gives_back_the_blur_planes()
    {
        var s = new EditorSession(Doc(120, 90));
        var c = CanvasOf(s);
        s.Add(new SpotlightBox(0, new RectI(10, 10, 40, 30), 0.5, 4, true, 20));
        PlanePool.Return(PlanePool.Rent(PlanePool.MinPooled * 2));
        Check.True(PlanePool.KeptBytes > 0, "a plane is kept");
        c.Detach();
        s.Release();
        Check.Equal(0L, PlanePool.KeptBytes, "PlanePool.Clear() on close");
        Check.Equal(0, s.Composer.HeldBuffers);
        Check.Equal(0, s.Ops.SpotCache.Count);
    }

    // ------------------------------------------------------------------ the canvas shows the composite

    [UnitTest]
    public static void The_canvas_shows_the_composite_and_only_rewrites_the_changed_part_of_the_bitmap()
    {
        var s = new EditorSession(DocumentState.Create(Solid(100, 60, 200, 200, 200)));
        var c = CanvasOf(s);
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), c.ShownPixel(10, 10)!.Value, "the picture");
        var hl = s.Add(new HighlightRect(0, new RectI(5, 5, 30, 20), Color.FromRgb(255, 255, 0)));
        // 200 x (255,255,0)/255 per channel: blue 0, green 200, red 200
        Check.Equal(((byte)0, (byte)200, (byte)200, (byte)255), c.ShownPixel(10, 10)!.Value, "highlighted");
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), c.ShownPixel(50, 40)!.Value, "elsewhere untouched");
        s.Undo();
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), c.ShownPixel(10, 10)!.Value, "undone");
        s.Redo();
        s.SetPreview(s.Committed.Replace(ObjectOps.Translate(hl, 50, 30)));   // a drag in progress
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), c.ShownPixel(10, 10)!.Value, "moved away");
        Check.Equal(((byte)0, (byte)200, (byte)200, (byte)255), c.ShownPixel(60, 40)!.Value, "and shown at the new place");
        s.CancelPreview();
        Check.Equal(((byte)0, (byte)200, (byte)200, (byte)255), c.ShownPixel(10, 10)!.Value, "cancelled: back");

        s.SetAdjust(new AdjustmentValues(-50, 0, 0));
        var dark = c.ShownPixel(50, 40)!.Value;
        Check.True(dark.B < 100, $"brightness -50 darkens the picture: {dark}");
        c.Detach();
        s.Release();
    }

    [UnitTest]
    public static void With_the_background_queue_the_composite_arrives_on_the_dispatcher()
    {
        var s = new EditorSession(DocumentState.Create(Solid(100, 60, 200, 200, 200)));
        var canvas = new EditorCanvas();
        canvas.Measure(new Size(800, 600));
        canvas.Arrange(new Rect(0, 0, 800, 600));
        canvas.Attach(s);   // not synchronous: a worker composes, the UI thread receives
        s.Add(new HighlightRect(0, new RectI(5, 5, 30, 20), Color.FromRgb(255, 255, 0)));
        Pump(() => canvas.ShownPixel(10, 10)!.Value.B == 0, 5000);
        Check.Equal(((byte)0, (byte)200, (byte)200, (byte)255), canvas.ShownPixel(10, 10)!.Value, "the highlight appeared");
        Check.True(canvas.ComposedCount >= 1);
        canvas.Detach();
        s.Release();
    }

    [UnitTest]
    public static void A_reopened_document_with_effects_shows_nothing_until_its_first_composite_never_the_raw_base()
    {
        // a reopened document: a highlight (it could be a blur over a password) is in its raster band
        var doc = DocumentState.Create(Solid(100, 60, 200, 200, 200)).Add(new HighlightRect(0, new RectI(5, 5, 30, 20), Color.FromRgb(255, 255, 0)), out _);
        var s = new EditorSession(doc);
        var canvas = new EditorCanvas();
        canvas.Measure(new Size(800, 600));
        canvas.Arrange(new Rect(0, 0, 800, 600));
        canvas.Attach(s);   // the background queue: the composite comes through the dispatcher, which has not run yet
        Check.True(canvas.FirstCompositePending, "waiting for the first composite");
        Check.Equal(((byte)0, (byte)0, (byte)0, (byte)0), canvas.ShownPixel(10, 10)!.Value, "nothing shown under the effect, not the raw base");
        Check.Equal(((byte)0, (byte)0, (byte)0, (byte)0), canvas.ShownPixel(80, 50)!.Value, "nothing at all yet");
        Pump(() => !canvas.FirstCompositePending, 5000);
        Check.False(canvas.FirstCompositePending);
        Check.Equal(((byte)0, (byte)200, (byte)200, (byte)255), canvas.ShownPixel(10, 10)!.Value, "then the composite");
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), canvas.ShownPixel(80, 50)!.Value);
        canvas.Detach();
        s.Release();

        // adjustments alone count too; a document without effects shows its base at once
        var adjusted = new EditorSession(DocumentState.Create(Solid(40, 30, 200, 200, 200)) with { Adjust = new AdjustmentValues(-50, 0, 0) });
        var c2 = new EditorCanvas();
        c2.Attach(adjusted);
        Check.True(c2.FirstCompositePending);
        Check.Equal((byte)0, c2.ShownPixel(5, 5)!.Value.A);
        c2.Detach();
        adjusted.Release();
        var plain = new EditorSession(DocumentState.Create(Solid(40, 30, 200, 200, 200)));
        var c3 = new EditorCanvas();
        c3.Attach(plain);
        Check.False(c3.FirstCompositePending);
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), c3.ShownPixel(5, 5)!.Value, "the base itself");
        c3.Detach();
        plain.Release();
    }

    // ------------------------------------------------------------------ export

    [UnitTest]
    public static void The_export_draws_the_raster_band_under_the_vector_band_and_never_the_editing_aids()
    {
        var baseImg = Ramp(120, 80);
        var doc = DocumentState.Create(baseImg);
        doc = doc.Add(new HighlightRect(0, new RectI(10, 10, 60, 40), Colors.Yellow), out _);
        doc = doc.Add(new RectOutline(0, new Rect(20, 20, 30, 20), Colors.Blue, 4), out _);   // a vector object over the highlight
        var img = Exporter.Export(doc);
        var onLine = img.GetPixel(20, 30);   // on the left edge of the outline: pure blue, the highlight is under it
        Check.Equal(((byte)255, (byte)0, (byte)0, (byte)255), onLine, "the vector object is on top, whole");
        var inside = img.GetPixel(60, 15);   // highlighted, no vector there
        var plain = baseImg.GetPixel(60, 15);
        Check.True(inside.B < plain.B || plain.B == 0, "the highlight is multiplied in");
        Check.Equal(0, (int)inside.B, "yellow has no blue");

        // the magnifier: the dashed source frame is an editor overlay, not pixels
        var src = new RectI(8, 8, 20, 14);
        var mdoc = DocumentState.Create(baseImg).Add(new MagnifyBox(0, src, new RectI(70, 40, 40, 28), Colors.White, 3, false), out _);
        var exported = Exporter.Export(mdoc);
        for (int y = src.Y - 1; y <= src.Bottom; y++)
            for (int x = src.X - 1; x <= src.Right; x++)
                Check.Equal(baseImg.GetPixel(x, y), exported.GetPixel(x, y), $"the source area is untouched at {x},{y}");
        Check.Equal(Magnifier.OuterRing.R, exported.GetPixel(70, 40).R, "the copy and its outline are drawn");
        // the same through the live document of a session (selection and adorners never reach the export)
        var s = new EditorSession(DocumentState.Create(baseImg));
        var m = s.Add(new MagnifyBox(0, src, new RectI(70, 40, 40, 28), Colors.White, 3, false));
        Check.Equal(m.Id, s.SelectedId);
        Check.Equal(exported.Pixels, s.Export().Pixels, "selected or not, the same pixels");
        s.Release();
    }

    [UnitTest]
    public static void Export_uses_its_own_composer_at_full_quality_while_the_display_composer_defers_sharpness()
    {
        var s = new EditorSession(DocumentState.Create(Noise(60, 40, 4)));
        s.ComposeSynchronously = false;
        s.SetAdjust(new AdjustmentValues(0, 0, 100));
        var exported = s.Export();
        var expected = new BgraImage(60, 40);
        Adjustments.Unsharp(s.Committed.Base, expected, expected.Bounds, 1.0);
        Check.Equal(expected.Pixels, exported.Pixels, "the saved picture has the sharpening");
        Check.True(s.Composer.DeferSharpness);
        s.Release();
    }

    // ------------------------------------------------------------------ performance (generous bounds, the actual numbers go to the log)

    [UnitTest]
    public static void Perf_4K_the_composer_opens_re_blends_a_moving_spotlight_and_applies_the_lut_within_budget()
    {
        var img = new BgraImage(3840, 2160);
        var rnd = new Random(5);
        // a picture with some structure rather than pure noise
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int i = (y * img.Width + x) * 4;
                img.Pixels[i] = (byte)(x * 255 / img.Width); img.Pixels[i + 1] = (byte)(y * 255 / img.Height); img.Pixels[i + 2] = (byte)(((x / 40 + y / 40) & 1) * 120 + rnd.Next(30)); img.Pixels[i + 3] = 255;
            }
        var doc = DocumentState.Create(img);
        doc = doc.Add(new BlurRegion(0, new RectI(200, 200, 800, 500), 8, false, 12), out _);
        doc = doc.Add(new HighlightRect(0, new RectI(1200, 300, 900, 200), Colors.Yellow), out _);
        doc = doc.Add(new SmartEraseRect(0, new RectI(2400, 800, 500, 300)), out _);
        doc = doc.Add(new SpotlightBox(0, new RectI(1000, 600, 1200, 800), 0.55, 12, true, 756), out var spot);
        var s = (SpotlightBox)spot;
        var composer = RasterOps.NewComposer();
        var sw = Stopwatch.StartNew();
        composer.Compose(doc);
        double first = sw.Elapsed.TotalMilliseconds;
        var times = new List<double>();
        for (int i = 1; i <= 8; i++)
        {
            doc = doc.Replace(s with { Box = new RectI(1000 + i * 25, 600 + i * 10, 1200, 800) });
            sw.Restart();
            composer.Compose(doc);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var lutDoc = DocumentState.Create(img) with { Adjust = new AdjustmentValues(20, 10, 0) };
        var lutComposer = new RasterComposer();
        sw.Restart();
        lutComposer.Compose(lutDoc);
        double lut = sw.Elapsed.TotalMilliseconds;
        var sharpDoc = lutDoc with { Adjust = new AdjustmentValues(20, 10, 100) };
        var sharpComposer = new RasterComposer();
        sw.Restart();
        sharpComposer.Compose(sharpDoc);
        double lutAndUnsharp = sw.Elapsed.TotalMilliseconds;
        // a brightness drag over the sharpened picture: the LUT over the cached sharpened base
        var lutOverSharp = new List<double>();
        for (int b = 22; b <= 30; b += 2)
        {
            sw.Restart();
            sharpComposer.Compose(lutDoc with { Adjust = new AdjustmentValues(b, 10, 100) });
            lutOverSharp.Add(sw.Elapsed.TotalMilliseconds);
        }
        // a highlight that moves under the spotlight (the spotlight's levels are rebuilt)
        var hl = (HighlightRect)doc.Raster[1];
        sw.Restart();
        composer.Compose(doc.Replace(hl with { R = hl.R.Offset(30, 0) }));
        double below = sw.Elapsed.TotalMilliseconds;
        // the same during a gesture: re-blended with the previous levels (rebuilt at most every 120 ms)
        composer.Interactive = true;
        var dragged = new List<double>();
        for (int i = 2; i <= 6; i++)
        {
            sw.Restart();
            composer.Compose(doc.Replace(hl with { R = hl.R.Offset(30 * i, 0) }));
            dragged.Add(sw.Elapsed.TotalMilliseconds);
        }
        composer.Interactive = false;
        sw.Restart();
        composer.Compose(doc.Replace(hl with { R = hl.R.Offset(180, 0) }));
        double gestureEnd = sw.Elapsed.TotalMilliseconds;
        var mem = composer.Memory;
        static long Mb(long b) => b / 1048576;
        string line = $"perf 4K composer: first compose (blur, highlight, erase, gradient spotlight) {first:0} ms; spotlight move re-blend {times.Average():0} ms avg (best {times.Min():0}, worst {times.Max():0}); " +
                 $"highlight moved under the spotlight (levels rebuilt) {below:0} ms; the same during a drag {dragged.Average():0} ms avg (worst {dragged.Max():0}), at the release {gestureEnd:0} ms; " +
                 $"brightness/contrast LUT {lut:0} ms; LUT + unsharp {lutAndUnsharp:0} ms; LUT over the cached sharpened base {lutOverSharp.Average():0} ms avg (worst {lutOverSharp.Max():0}); " +
                 $"held {mem.PictureCount} pictures {Mb(mem.Pictures)} MB + levels/masks {Mb(mem.Caches)} MB (peak {Mb(mem.PeakCaches)} MB) + inserted {Mb(mem.Inserted)} MB = {Mb(mem.Total)} MB, blur planes kept {Mb(PlanePool.KeptBytes)} MB; " +
                 $"the sharpened-picture composer {Mb(sharpComposer.HeldBytes)} MB; cores {Environment.ProcessorCount}";
        Log.Info(line);
        if (Environment.GetEnvironmentVariable("CLINK_PERF_LOG") is { Length: > 0 } perfFile) File.AppendAllText(perfFile, line + "\n");
        Check.True(first < 5000, $"first compose {first:0} ms");
        Check.True(times.Max() < 2000, $"spotlight move {times.Max():0} ms");
        Check.True(lut < 1000, $"LUT {lut:0} ms");
        Check.True(lutOverSharp.Max() < 1000, $"LUT over the sharpened base {lutOverSharp.Max():0} ms");
        Check.True(composer.HeldBuffers <= 5);
        Check.True(mem.Total == composer.HeldBytes && mem.Caches > 0, "the levels are counted");
        composer.Clear();
        sharpComposer.Clear();
        PlanePool.Clear();
    }
}
