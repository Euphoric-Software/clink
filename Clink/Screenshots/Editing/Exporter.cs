using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>
/// Turns a <see cref="DocumentState"/> into the picture that is saved, copied and uploaded: (1) the raster composite cut to the
/// crop (exact CPU pixels); (2) the vector band drawn by WPF into <c>RenderTargetBitmap</c>s at 96 dpi (Pbgra32; grayscale text,
/// ideal formatting: <see cref="VectorRenderer.Configure"/>) with a translate by minus the target's origin, so one document pixel is
/// one output pixel; (3) a premultiplied "over" of each render onto (1) (<see cref="Blend.OverPremultiplied"/>), in z-order.
/// Shapes (pen, line, arrow, rectangle) are rendered together, in tiles when the crop is over 8192 px on a side (a three-4K-monitor
/// span is 11520 px wide), which keeps the render target allocations bounded; a tiled render is byte-identical to a single one for
/// shapes. A text note is rendered on a target of its own (its box plus margin, on a 32-pixel lattice of document coordinates),
/// because WPF's text rasterizer is not translation invariant in the last bit (one level in a few glyph pixels at a tile edge or
/// on another origin): rendered alone the same way in every case, the text of a tiled export equals the text of an untiled one.
/// The vector render needs the UI thread (WPF drawing objects); the rest does not (<see cref="ExportAsync"/>).
/// </summary>
internal static class Exporter
{
    /// <summary>A crop with a side over this many pixels is rendered in tiles. A test lowers it (and <see cref="TileSize"/>) to exercise the tiling on a small image.</summary>
    internal static int TileThreshold = 8192;
    /// <summary>The side of a tile, in pixels.</summary>
    internal static int TileSize = 4096;

    private const int Lattice = 32, TextMargin = 32, ShapeMargin = 8;

    /// <summary>One WPF render of the vector band, ready to be blended over the composite at (X, Y) (premultiplied BGRA, <c>Width * Height * 4</c> bytes).</summary>
    internal sealed record VectorLayer(byte[] Pixels, int Width, int Height, int X, int Y);

    /// <summary>Test seam: called with the phase ("compose", "render", "blend") and the managed thread id it runs on. Null in the app.</summary>
    internal static Action<string, int>? Probe;

    private static void Mark(string phase) => Probe?.Invoke(phase, Environment.CurrentManagedThreadId);

    /// <summary>
    /// The finished picture (a new image, never the document's own buffers), all on the calling thread. <see cref="ExportAsync"/> gives
    /// the same bytes with only the WPF drawing on the calling (UI) thread.
    /// </summary>
    public static BgraImage Export(DocumentState doc, RasterComposer? composer = null)
    {
        composer ??= RasterOps.NewComposer();
        var crop = CropOf(doc);
        var result = ComposeCrop(doc, composer, crop);
        BlendLayers(result, RenderLayers(doc, crop));
        return result;
    }

    /// <summary>
    /// The same picture as <see cref="Export"/> (byte for byte), but the raster composite and the blend run on worker threads: only
    /// the vector render (WPF drawing objects and <c>RenderTargetBitmap</c>, which need the UI thread, and are cheap next to the
    /// raster work) runs on the calling thread, while the composite is computed. Call it from the UI thread.
    /// </summary>
    public static async Task<BgraImage> ExportAsync(DocumentState doc, RasterComposer? composer = null)
    {
        composer ??= RasterOps.NewComposer();
        var crop = CropOf(doc);
        var compose = Task.Run(() => ComposeCrop(doc, composer, crop));
        List<VectorLayer> layers;
        try { layers = RenderLayers(doc, crop); }
        catch
        {
            try { await compose.ConfigureAwait(false); } catch { }   // never leave the composer in use behind the caller
            throw;
        }
        var result = await compose;
        if (layers.Count == 0) return result;
        await Task.Run(() => BlendLayers(result, layers));
        return result;
    }

    private static RectI CropOf(DocumentState doc)
    {
        var crop = doc.Crop.Intersect(doc.Base.Bounds);
        if (crop.IsEmpty) throw new InvalidOperationException("The crop is empty");
        return crop;
    }

    /// <summary>The raster composite cut to <paramref name="crop"/> (the part of the export that needs no WPF).</summary>
    private static BgraImage ComposeCrop(DocumentState doc, RasterComposer composer, RectI crop)
    {
        Mark("compose");
        var composite = composer.Compose(doc);
        if (!composite.Bounds.Contains(crop)) throw new InvalidOperationException("The composite does not cover the crop");
        return composite.Crop(crop);
    }

    /// <summary>Renders the vector band, in z-order: a run of shapes, a text note, another run... (UI thread).</summary>
    private static List<VectorLayer> RenderLayers(DocumentState doc, RectI crop)
    {
        var layers = new List<VectorLayer>();
        if (doc.Vector.IsEmpty) return layers;
        Mark("render");
        var run = new List<EditorObject>();
        foreach (var o in doc.Vector)
        {
            if (o is TextNote note)
            {
                RenderShapes(run, crop, layers);
                run.Clear();
                RenderText(note, crop, layers);
            }
            else run.Add(o);
        }
        RenderShapes(run, crop, layers);
        return layers;
    }

    private static void BlendLayers(BgraImage result, List<VectorLayer> layers)
    {
        if (layers.Count == 0) return;
        Mark("blend");
        foreach (var l in layers) Blend.OverPremultiplied(result, l.Pixels, l.Width, l.Height, l.X, l.Y);
    }
    // ------------------------------------------------------------------ shapes

    private static void RenderShapes(List<EditorObject> shapes, RectI crop, List<VectorLayer> layers)
    {
        if (shapes.Count == 0) return;
        bool tiled = crop.Width > TileThreshold || crop.Height > TileThreshold;
        int tile = Math.Max(1, TileSize);
        int stepX = tiled ? tile : crop.Width, stepY = tiled ? tile : crop.Height;
        // The box of each object in output coordinates: a tile that none touches is not rendered at all.
        var boxes = shapes.Select(o => HitTest.Bounds(o, VectorRenderer.TextBounds)).Where(b => !b.IsEmpty)
            .Select(b => new Rect(b.X - crop.X - ShapeMargin, b.Y - crop.Y - ShapeMargin, b.Width + 2 * ShapeMargin, b.Height + 2 * ShapeMargin)).ToList();
        if (boxes.Count == 0) return;
        // Nothing is drawn outside the union of the boxes (they carry the stroke and arrow-head room): the render target never needs to be bigger.
        var all = boxes[0];
        foreach (var b in boxes) all.Union(b);
        var union = RectI.FromLtrb((int)Math.Floor(all.Left), (int)Math.Floor(all.Top), (int)Math.Ceiling(all.Right), (int)Math.Ceiling(all.Bottom));
        var cropLocal = new RectI(0, 0, crop.Width, crop.Height);
        for (int ty = 0; ty < crop.Height; ty += stepY)
        {
            for (int tx = 0; tx < crop.Width; tx += stepX)
            {
                int tw = Math.Min(stepX, crop.Width - tx), th = Math.Min(stepY, crop.Height - ty);
                var tileRect = new Rect(tx, ty, tw, th);
                if (!boxes.Any(b => b.IntersectsWith(tileRect))) continue;
                var part = new RectI(tx, ty, tw, th).Intersect(union);   // the part of the tile the shapes can reach
                if (part.IsEmpty) continue;
                // that part plus a margin that is thrown away, never beyond the crop (its edge is the edge of an untiled render, too)
                var area = part.Inflate(ShapeMargin).Intersect(cropLocal);
                var pixels = Render(shapes, crop.X + area.X, crop.Y + area.Y, area.Width, area.Height, new RectI(part.X - area.X, part.Y - area.Y, part.Width, part.Height));
                layers.Add(new VectorLayer(pixels, part.Width, part.Height, part.X, part.Y));
            }
        }
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// The document area a text note is rendered on: its box plus room for a backdrop box (6 px beyond the text), an outline (2 px) and
    /// the anti-aliasing, on the 32-pixel lattice of document coordinates, cut to the crop expanded outward to the lattice (the origin
    /// stays on the lattice, so the glyphs rasterize as in an uncropped export; a huge note does not make a huge render target).
    /// Empty when the note is outside the crop.
    /// </summary>
    internal static RectI TextArea(Rect box, RectI crop)
    {
        int x0 = (int)Math.Floor((box.Left - TextMargin) / Lattice) * Lattice, y0 = (int)Math.Floor((box.Top - TextMargin) / Lattice) * Lattice;
        int x1 = (int)Math.Ceiling((box.Right + TextMargin) / Lattice) * Lattice, y1 = (int)Math.Ceiling((box.Bottom + TextMargin) / Lattice) * Lattice;
        var docArea = RectI.FromLtrb(x0, y0, x1, y1);
        if (docArea.IsEmpty || docArea.Intersect(crop).IsEmpty) return RectI.Empty;
        var aligned = RectI.FromLtrb(FloorLattice(crop.X), FloorLattice(crop.Y), CeilLattice(crop.X + crop.Width), CeilLattice(crop.Y + crop.Height));
        return docArea.Intersect(aligned);
    }

    private static int FloorLattice(int v) => (int)Math.Floor(v / (double)Lattice) * Lattice;
    private static int CeilLattice(int v) => (int)Math.Ceiling(v / (double)Lattice) * Lattice;

    private static void RenderText(TextNote note, RectI crop, List<VectorLayer> layers)
    {
        var docArea = TextArea(VectorRenderer.TextBounds(note), crop);
        if (docArea.IsEmpty) return;
        var pixels = Render(new[] { note }, docArea.X, docArea.Y, docArea.Width, docArea.Height, new RectI(0, 0, docArea.Width, docArea.Height));
        layers.Add(new VectorLayer(pixels, docArea.Width, docArea.Height, docArea.X - crop.X, docArea.Y - crop.Y));
    }

    // ------------------------------------------------------------------ one render target

    /// <summary>
    /// Draws <paramref name="objects"/> into a <paramref name="w"/> x <paramref name="h"/> target whose top-left is the document point
    /// (<paramref name="originX"/>, <paramref name="originY"/>) and returns the premultiplied bytes of <paramref name="take"/> (target coordinates).
    /// </summary>
    private static byte[] Render(IEnumerable<EditorObject> objects, int originX, int originY, int w, int h, RectI take)
    {
        var visual = new DrawingVisual();
        VectorRenderer.Configure(visual);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-originX, -originY));
            foreach (var o in objects) VectorRenderer.Draw(dc, o);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var pixels = new byte[take.Width * take.Height * 4];
        if (take.X == 0 && take.Y == 0 && take.Width == w && take.Height == h) rtb.CopyPixels(pixels, take.Width * 4, 0);
        else rtb.CopyPixels(new Int32Rect(take.X, take.Y, take.Width, take.Height), pixels, take.Width * 4, 0);
        return pixels;
    }
}
