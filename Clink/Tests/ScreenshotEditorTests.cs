using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Services;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>The screenshot editor without a window: the document model, undo, the raster composer, the exporter, blending, the session and the options.</summary>
internal static class ScreenshotEditorTests
{
    private static BgraImage Solid(int w, int h, byte b, byte g, byte r)
    {
        var img = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, b, g, r, 255);
        return img;
    }

    private static DocumentState Doc(int w = 60, int h = 40) => DocumentState.Create(Solid(w, h, 200, 200, 200));

    private static Color Red => Colors.Red;

    // ------------------------------------------------------------------ DocumentState

    [UnitTest]
    public static void A_state_is_a_value_an_edit_makes_a_new_one_and_the_old_one_is_unchanged()
    {
        var empty = Doc();
        var withRect = empty.Add(new RectOutline(0, new Rect(1, 1, 5, 5), Red, 2), out var rect);
        Check.Equal(0, empty.Vector.Count, "the old state has no objects");
        Check.Equal(1, withRect.Vector.Count);
        Check.True(ReferenceEquals(empty.Base, withRect.Base), "the base picture is shared, never copied");
        Check.Equal(1, rect.Id);
        Check.Equal(2, withRect.NextId);

        var moved = withRect.Replace(ObjectOps.Translate(rect, 10, 0));
        Check.Equal(new Rect(1, 1, 5, 5), ((RectOutline)withRect.Find(1)!).R, "the earlier state still has the old rectangle");
        Check.Equal(new Rect(11, 1, 5, 5), ((RectOutline)moved.Find(1)!).R);

        var removed = moved.Remove(1);
        Check.Equal(1, moved.Vector.Count);
        Check.Equal(0, removed.Vector.Count);
        Check.Null(removed.Find(1));
        Check.True(ReferenceEquals(removed.Remove(99), removed), "removing what is not there changes nothing");
    }

    [UnitTest]
    public static void Reordering_moves_an_object_inside_its_own_band_only()
    {
        var d = Doc();
        d = d.Add(new BlurRegion(0, new RectI(0, 0, 5, 5), 4, false, 8), out var blur1);                  // raster 1
        d = d.Add(new RectOutline(0, new Rect(1, 1, 5, 5), Red, 2), out var v1);                          // vector 1
        d = d.Add(new HighlightRect(0, new RectI(2, 2, 5, 5), Colors.Yellow), out var raster2);           // raster 2
        d = d.Add(new LineShape(0, new Point(0, 0), new Point(5, 5), Red, 2, false), out var v2);         // vector 2
        d = d.Add(new SpotlightBox(0, new RectI(3, 3, 5, 5), 0.5, 4, false, 10), out var raster3);        // raster 3
        Check.Equal(new[] { blur1.Id, raster2.Id, raster3.Id }, d.Raster.Select(o => o.Id).ToArray());
        Check.Equal(new[] { v1.Id, v2.Id }, d.Vector.Select(o => o.Id).ToArray());

        var up = d.Reorder(v1.Id, 1);
        Check.Equal(new[] { v2.Id, v1.Id }, up.Vector.Select(o => o.Id).ToArray());
        Check.Equal(d.Raster, up.Raster, "the raster band is untouched");
        Check.True(ReferenceEquals(up.Reorder(v1.Id, 1), up), "already on top: nothing changes");
        Check.Equal(new[] { v1.Id, v2.Id }, up.Reorder(v1.Id, int.MinValue).Vector.Select(o => o.Id).ToArray(), "to the back");
        // a raster object moves among raster objects, never past a vector one
        var r = d.Reorder(blur1.Id, 5);
        Check.Equal(new[] { raster2.Id, raster3.Id, blur1.Id }, r.Raster.Select(o => o.Id).ToArray());
        Check.Equal(d.Vector, r.Vector);
        Check.True(DocumentState.IsRaster(blur1) && !DocumentState.IsRaster(v1));
    }

    [UnitTest]
    public static void The_crop_is_clipped_to_the_picture_and_an_empty_one_is_refused()
    {
        var d = Doc(60, 40);
        Check.Equal(new RectI(0, 0, 60, 40), d.Crop);
        Check.Equal(new RectI(10, 5, 30, 20), d.WithCrop(new RectI(10, 5, 30, 20)).Crop);
        Check.Equal(new RectI(50, 30, 10, 10), d.WithCrop(new RectI(50, 30, 100, 100)).Crop, "cut to the picture");
        Check.True(ReferenceEquals(d, d.WithCrop(new RectI(500, 500, 10, 10))), "outside: ignored");
        Check.True(ReferenceEquals(d, d.WithCrop(d.Crop)), "the same crop is the same state");
        Check.True(AdjustmentValues.Identity.IsIdentity);
        Check.False(new AdjustmentValues(10, 0, 0).IsIdentity);
    }

    [UnitTest]
    public static void The_default_adjustments_are_the_identity_with_sharpness_0()
    {
        var a = new AdjustmentValues();
        Check.Equal(0, a.Sharpness);
        Check.True(a.IsIdentity);
        Check.False(new AdjustmentValues(0, 0, 50).IsIdentity);
        Check.True(Doc().Adjust.IsIdentity);
    }

    [UnitTest]
    public static void Replacing_an_object_with_an_equal_one_changes_nothing()
    {
        var d = Doc().Add(new RectOutline(0, new Rect(1, 1, 5, 5), Red, 2), out var rect);
        var ro = (RectOutline)rect;
        Check.True(ReferenceEquals(d, d.Replace(ro with { Width = 2 })), "an equal record is the same state");
        Check.False(ReferenceEquals(d, d.Replace(ro with { Width = 3 })));
        var r = Doc().Add(new HighlightRect(0, new RectI(1, 1, 4, 4), Colors.Yellow), out var hl);
        Check.True(ReferenceEquals(r, r.Replace(hl with { })), "raster band too");

        var s = new EditorSession(d);
        s.Select(rect.Id);
        s.SetWidth(2);   // the width it already has
        Check.Equal(1, s.UndoDepth, "no undo step");
        Check.False(s.IsDirty, "not dirty");
        s.SetWidth(6);
        Check.Equal(2, s.UndoDepth);
        Check.True(s.IsDirty);
    }

    // ------------------------------------------------------------------ UndoStack

    [UnitTest]
    public static void Undo_goes_back_and_forward_through_the_pushed_states()
    {
        var u = new UndoStack<string>("a");
        Check.Equal("a", u.Current);
        Check.False(u.CanUndo || u.CanRedo);
        Check.True(u.Push("b"));
        Check.True(u.Push("c"));
        Check.Equal("c", u.Current);
        Check.True(u.Undo());
        Check.Equal("b", u.Current);
        Check.True(u.CanRedo);
        Check.True(u.Redo());
        Check.Equal("c", u.Current);
        Check.False(u.Redo(), "nothing to redo");
        Check.True(u.Undo() && u.Undo());
        Check.False(u.Undo(), "at the start");
        Check.Equal("a", u.Current);

        // a push after undo drops what could be redone
        u.Redo();
        Check.True(u.Push("x"));
        Check.False(u.CanRedo);
        Check.Equal(new[] { "a", "b", "x" }.Length, u.Count);
        var same = u.Current;
        Check.False(u.Push(same), "the current state again is not a new step");
    }

    [UnitTest]
    public static void The_undo_stack_keeps_the_newest_200_states()
    {
        var u = new UndoStack<string>("s0");
        for (int i = 1; i <= 250; i++) u.Push("s" + i);
        Check.Equal(200, u.Count);
        Check.Equal("s250", u.Current);
        int steps = 0;
        while (u.Undo()) steps++;
        Check.Equal(199, steps);
        Check.Equal("s51", u.Current, "the oldest 51 states are gone");
        var small = new UndoStack<string>("a", 3);
        small.Push("b"); small.Push("c"); small.Push("d");
        Check.Equal(3, small.Count);
        small.Undo(); small.Undo();
        Check.Equal("b", small.Current);
        Check.Throws<ArgumentOutOfRangeException>(() => new UndoStack<string>("a", 0));
    }

    // ------------------------------------------------------------------ EditorSession

    [UnitTest]
    public static void A_drag_in_progress_is_only_a_preview_until_it_ends()
    {
        var s = new EditorSession(Doc());
        var added = s.Committed.Add(new RectOutline(0, new Rect(1, 1, 5, 5), Red, 2), out var rect);
        s.SetPreview(added);
        Check.Equal(1, s.State.Vector.Count, "the preview is what is shown");
        Check.Equal(0, s.Committed.Vector.Count, "but not the document");
        Check.False(s.CanUndo, "and not an undo step");
        Check.False(s.IsDirty);
        s.SetPreview(s.Committed.Add(new RectOutline(0, new Rect(1, 1, 9, 9), Red, 2), out _));   // the drag goes on
        Check.Equal(1, s.UndoDepth);
        Check.True(s.CommitPreview(rect.Id));
        Check.Equal(2, s.UndoDepth, "one step for the whole gesture");
        Check.Equal(1, s.Committed.Vector.Count);
        Check.Equal(rect.Id, s.SelectedId, "the new object is selected");
        Check.True(s.IsDirty);

        s.SetPreview(s.Committed.Replace(ObjectOps.Translate(rect, 5, 5)));
        s.CancelPreview();
        Check.Equal(new Rect(1, 1, 9, 9), ((RectOutline)s.State.Vector[0]).R, "a cancelled drag leaves the document alone");
        Check.Equal(2, s.UndoDepth);
        Check.False(s.CommitPreview(), "no preview: nothing to commit");
    }

    [UnitTest]
    public static void Undo_and_redo_restore_documents_and_the_dirty_flag_follows_the_saved_state()
    {
        var s = new EditorSession(Doc());
        int changes = 0;
        s.StateChanged += () => changes++;
        var rect = s.Add(new RectOutline(0, new Rect(1, 1, 5, 5), Red, 2));
        var line = s.Add(new LineShape(0, new Point(0, 0), new Point(9, 9), Red, 2, true));
        Check.Equal(line.Id, s.SelectedId);
        Check.True(s.IsDirty);
        Check.True(s.Undo());
        Check.Equal(1, s.State.Vector.Count);
        Check.Null(s.SelectedId, "the selected object is gone: nothing selected");
        Check.True(s.Redo());
        Check.Equal(2, s.State.Vector.Count);
        s.MarkSaved();
        Check.False(s.IsDirty);
        s.Select(line.Id);
        Check.True(s.Delete());
        Check.True(s.IsDirty);
        s.Undo();
        Check.False(s.IsDirty, "undone back to the saved state");
        Check.True(changes > 4);
        Check.True(s.Undo() && s.Undo());
        Check.False(s.Undo());
        Check.Equal(0, s.State.Vector.Count);
        Check.Equal(1, rect.Id);
    }

    [UnitTest]
    public static void Delete_duplicate_nudge_and_reorder_act_on_the_selection_and_each_is_one_undo_step()
    {
        var s = new EditorSession(Doc());
        Check.False(s.Delete() || s.Duplicate() || s.Nudge(1, 0) || s.Reorder(1), "nothing selected, nothing happens");
        var a = s.Add(new RectOutline(0, new Rect(10, 10, 5, 5), Red, 2));
        int depth = s.UndoDepth;
        Check.True(s.Nudge(1, 0));
        Check.True(s.Nudge(0, 10));
        Check.Equal(depth + 2, s.UndoDepth);
        Check.Equal(new Rect(11, 20, 5, 5), ((RectOutline)s.Selected!).R);
        Check.True(s.Duplicate());
        Check.Equal(2, s.State.Vector.Count);
        Check.NotEqual(a.Id, s.SelectedId, "the copy is selected");
        Check.Equal(new Rect(23, 32, 5, 5), ((RectOutline)s.Selected!).R, "12 px down and right");
        Check.True(s.Reorder(int.MinValue));
        Check.Equal(s.SelectedId, s.State.Vector[0].Id, "to the back");
        Check.True(s.Delete());
        Check.Equal(1, s.State.Vector.Count);
        Check.Null(s.SelectedId);
    }

    [UnitTest]
    public static void Colour_width_and_text_options_restyle_the_selection_and_are_remembered()
    {
        ScreenshotEditorOptions? persisted = null;
        var s = new EditorSession(Doc(), new ScreenshotEditorOptions(), o => persisted = o);
        var line = s.Add(new LineShape(0, new Point(0, 0), new Point(20, 0), Red, 4, false));
        s.SetColor(Colors.Blue);
        Check.Equal(Colors.Blue, ((LineShape)s.Selected!).Color);
        Check.Equal("#0000FF", s.Options.Color);
        s.SetWidth(10, final: false);   // a slider being dragged
        Check.Equal(10.0, ((LineShape)s.State.Vector[0]).Width, "shown at once");
        Check.Equal(4.0, ((LineShape)s.Committed.Vector[0]).Width, "but only a preview");
        int depth = s.UndoDepth;
        s.SetWidth(12, final: false);
        s.CommitPreview();
        Check.Equal(depth + 1, s.UndoDepth, "the release is one undo step");
        Check.Equal(12.0, ((LineShape)s.Selected!).Width);
        Check.Null(persisted, "written when the editor closes, not on every change");
        s.FlushOptions();
        Check.Equal("#0000FF", persisted!.Color);
        Check.Equal(12.0, persisted.Width);
        persisted = null;
        s.FlushOptions();
        Check.Null(persisted, "and only when something changed");

        var note = s.Add(new TextNote(0, new Point(5, 5), "hi", 20, Colors.White, false, TextBackdrop.None, Colors.Black));
        s.SetFontSize(40);
        s.SetBold(true);
        s.SetBackdrop(TextBackdrop.Box);
        var t = (TextNote)s.Selected!;
        Check.Equal((40.0, true, TextBackdrop.Box), (t.Size, t.Bold, t.Backdrop));
        s.SetColor(Colors.Yellow);
        Check.Equal(Colors.Yellow, ((TextNote)s.Selected!).Fill);
        Check.Equal("#FFFF00", s.Options.TextColor, "text has its own colour option");
        Check.Equal("#0000FF", s.Options.Color, "and does not disturb the line colour");
        Check.Equal(note.Id, s.SelectedId);
    }

    [UnitTest]
    public static void Changing_the_tool_drops_a_gesture_and_the_export_is_cached_per_document()
    {
        var s = new EditorSession(Doc());
        s.SetPreview(s.Committed.Add(new RectOutline(0, new Rect(1, 1, 5, 5), Red, 2), out _));
        s.SetTool(EditorTool.Pen);
        Check.False(s.HasPreview);
        Check.Equal(EditorTool.Pen, s.Tool);
        var first = s.Export();
        Check.True(ReferenceEquals(first, s.Export()), "exported once per document");
        s.Add(new RectOutline(0, new Rect(10, 10, 10, 10), Red, 2));
        var second = s.Export();
        Check.False(ReferenceEquals(first, second));
        Check.NotEqual(first.Pixels, second.Pixels);
        s.Release();
        Check.False(ReferenceEquals(second, s.Export()), "released: exported again");
    }

    // ------------------------------------------------------------------ Blend

    [UnitTest]
    public static void Premultiplied_pixels_blend_over_opaque_and_transparent_destinations()
    {
        var dst = Solid(2, 1, 200, 100, 0);
        // a half-covering 50 / 0 / 0 premultiplied pixel and a fully covering one
        Blend.OverPremultiplied(dst, new byte[] { 50, 0, 0, 128, 7, 8, 9, 255 }, 2, 1, 0, 0);
        Check.Equal(((byte)150, (byte)50, (byte)0, (byte)255), dst.GetPixel(0, 0), "src + dst * (255 - a) / 255, rounded");
        Check.Equal(((byte)7, (byte)8, (byte)9, (byte)255), dst.GetPixel(1, 0), "opaque replaces");

        var clear = new BgraImage(1, 1);   // transparent black
        Blend.OverPremultiplied(clear, new byte[] { 50, 0, 0, 128 }, 1, 1, 0, 0);
        var px = clear.GetPixel(0, 0);
        Check.Equal(128, (int)px.A);
        Check.True(Math.Abs(px.B - 100) <= 1 && px.G == 0 && px.R == 0, $"straight colour comes back: {px}");

        var untouched = Solid(1, 1, 1, 2, 3);
        Blend.OverPremultiplied(untouched, new byte[] { 99, 99, 99, 0 }, 1, 1, 0, 0);
        Check.Equal(((byte)1, (byte)2, (byte)3, (byte)255), untouched.GetPixel(0, 0), "alpha 0 changes nothing");

        // placement and clipping
        var big = Solid(4, 4, 0, 0, 0);
        Blend.OverPremultiplied(big, new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 }, 2, 2, 3, 3);
        Check.Equal(((byte)255, (byte)255, (byte)255, (byte)255), big.GetPixel(3, 3));
        Check.Equal(((byte)0, (byte)0, (byte)0, (byte)255), big.GetPixel(2, 3), "left of the placement");
        Blend.OverPremultiplied(big, new byte[] { 1, 1, 1, 255 }, 1, 1, -5, -5);   // wholly outside: ignored
        Blend.OverPremultiplied(big, new byte[] { 1, 1, 1, 255 }, 1, 1, 4, 0);
    }

    [UnitTest]
    public static void Straight_alpha_images_blend_over_each_other_with_opacity()
    {
        var dst = Solid(1, 1, 200, 0, 0);
        var src = new BgraImage(1, 1);
        src.SetPixel(0, 0, 100, 0, 0, 128);
        Blend.OverStraight(dst, src, 0, 0);
        Check.Equal(((byte)150, (byte)0, (byte)0, (byte)255), dst.GetPixel(0, 0));

        var opaque = new BgraImage(1, 1);
        opaque.SetPixel(0, 0, 0, 0, 100, 255);
        var d2 = Solid(1, 1, 0, 0, 0);
        Blend.OverStraight(d2, opaque, 0, 0, opacity: 0.5);
        Check.True(Math.Abs(d2.GetPixel(0, 0).R - 50) <= 1, $"half opacity: about half the red, {d2.GetPixel(0, 0)}");
        var d3 = Solid(1, 1, 5, 5, 5);
        Blend.OverStraight(d3, opaque, 0, 0, opacity: 0);
        Check.Equal(((byte)5, (byte)5, (byte)5, (byte)255), d3.GetPixel(0, 0), "opacity 0 draws nothing");
    }

    // ------------------------------------------------------------------ Exporter

    private static int CountDifferent(BgraImage a, BgraImage b)
    {
        int n = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
            if (a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2] || a.Pixels[i + 3] != b.Pixels[i + 3]) n++;
        return n;
    }

    /// <summary>"x,y" of the first few pixels that differ, for a failure message.</summary>
    private static string WhereDifferent(BgraImage a, BgraImage b)
    {
        var where = new List<string>();
        for (int i = 0; i < a.Pixels.Length && where.Count < 8; i += 4)
            if (a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2] || a.Pixels[i + 3] != b.Pixels[i + 3])
                where.Add($"{i / 4 % a.Width},{i / 4 / a.Width}");
        return string.Join(" ", where);
    }

    [UnitTest]
    public static void A_rectangle_outline_lands_on_exact_pixels_at_96_dpi()
    {
        // (10,10)-(30,30), 2 px wide, centred on its edges: the pixels 9..10 and 29..30 on each side are the line; the render target
        // is 96 dpi and unscaled, so there is no anti-aliasing and no shift.
        var doc = Doc().Add(new RectOutline(0, new Rect(10, 10, 20, 20), Red, 2), out _);
        var img = Exporter.Export(doc);
        Check.Equal((60, 40), (img.Width, img.Height));
        int red = 0;
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 60; x++)
            {
                bool outer = x >= 9 && x < 31 && y >= 9 && y < 31, inner = x >= 11 && x < 29 && y >= 11 && y < 29;
                var expected = outer && !inner ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)200, (byte)200, (byte)200, (byte)255);
                Check.Equal(expected, img.GetPixel(x, y), $"pixel {x},{y}");
                if (outer && !inner) red++;
            }
        }
        Check.Equal(22 * 22 - 18 * 18, red);
    }

    [UnitTest]
    public static void The_export_honours_the_crop_and_leaves_the_document_and_its_picture_alone()
    {
        var doc = Doc().Add(new RectOutline(0, new Rect(10, 10, 20, 20), Red, 2), out _);
        var baseCopy = (byte[])doc.Base.Pixels.Clone();
        var cropped = Exporter.Export(doc.WithCrop(new RectI(5, 5, 40, 30)));
        Check.Equal((40, 30), (cropped.Width, cropped.Height));
        Check.Equal(((byte)0, (byte)0, (byte)255, (byte)255), cropped.GetPixel(4, 4), "source pixel 9,9 is the line's outer corner");
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), cropped.GetPixel(3, 4), "source 8,9 is outside it");
        Check.Equal(((byte)0, (byte)0, (byte)255, (byte)255), cropped.GetPixel(25, 25), "source 30,30");
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), cropped.GetPixel(26, 25), "source 31,30");
        Check.Equal(baseCopy, doc.Base.Pixels, "the base picture is never drawn on");

        // an annotation partly outside the crop is clipped; one wholly outside leaves no trace
        var outside = Doc().Add(new RectOutline(0, new Rect(-10, -10, 15, 15), Red, 2), out _).WithCrop(new RectI(20, 20, 20, 20));
        Check.Equal(0, CountDifferent(Exporter.Export(outside), Solid(20, 20, 200, 200, 200)));
    }

    [UnitTest]
    public static void Without_objects_the_export_is_a_copy_of_the_crop_and_never_the_documents_own_buffer()
    {
        var doc = Doc();
        var img = Exporter.Export(doc);
        Check.False(ReferenceEquals(img, doc.Base));
        Check.False(ReferenceEquals(img.Pixels, doc.Base.Pixels));
        Check.Equal(doc.Base.Pixels, img.Pixels);
        img.SetPixel(0, 0, 1, 2, 3);
        Check.Equal(((byte)200, (byte)200, (byte)200, (byte)255), doc.Base.GetPixel(0, 0));
    }

    [UnitTest]
    public static void Rendering_in_tiles_gives_the_same_bytes_as_rendering_at_once()
    {
        var doc = Doc(130, 90);
        doc = doc.Add(new RectOutline(0, new Rect(10.5, 12, 100, 60), Colors.Red, 3), out _);
        doc = doc.Add(new LineShape(0, new Point(5, 80), new Point(120, 8), Colors.Blue, 4, true), out _);
        doc = doc.Add(new PenStroke(0, ImmutableArray.Create(new Point(3, 3), new Point(40, 30), new Point(70, 10), new Point(120, 70)), Colors.Green, 5), out _);
        doc = doc.Add(new FreehandArrow(0, ImmutableArray.Create(new Point(20, 60), new Point(50, 45), new Point(80, 55), new Point(115, 40)), Colors.Orange, 4), out _);
        doc = doc.Add(new TextNote(0, new Point(14, 30), "Tiles\nand text", 22, Colors.White, true, TextBackdrop.Box, Color.FromArgb(200, 0, 0, 0)), out _);
        doc = doc.Add(new TextNote(0, new Point(60, 68), "outline", 18, Colors.White, false, TextBackdrop.Outline, Colors.Black), out _);
        var whole = Exporter.Export(doc);
        Check.True(CountDifferent(whole, Doc(130, 90).Base) > 500, "something was drawn");

        int threshold = Exporter.TileThreshold, size = Exporter.TileSize;
        try
        {
            Exporter.TileThreshold = 0;
            foreach (int tile in new[] { 16, 37, 64 })
            {
                Exporter.TileSize = tile;
                var tiled = Exporter.Export(doc);
                Check.Equal(0, CountDifferent(whole, tiled), $"{tile}-px tiles differ from the single render at {WhereDifferent(whole, tiled)}");
                Check.Equal(whole.Pixels, tiled.Pixels);
            }
            // with a crop and tiles together
            var cropped = doc.WithCrop(new RectI(7, 9, 101, 63));
            Exporter.TileSize = 25;
            var tiledCrop = Exporter.Export(cropped);
            Exporter.TileThreshold = threshold;
            Check.Equal(Exporter.Export(cropped).Pixels, tiledCrop.Pixels, "a crop rendered in tiles");
        }
        finally
        {
            Exporter.TileThreshold = threshold;
            Exporter.TileSize = size;
        }
    }

    [UnitTest]
    public static void Translucent_and_text_objects_are_blended_with_the_picture_under_them()
    {
        var doc = Doc().Add(new RectOutline(0, new Rect(10, 10, 20, 20), Color.FromArgb(128, 255, 0, 0), 4), out _);
        var p = Exporter.Export(doc).GetPixel(10, 10);   // inside the line: half red over grey 200
        Check.True(p.R >= 225 && p.R <= 230 && p.G >= 98 && p.G <= 102 && p.B >= 98 && p.B <= 102 && p.A == 255, $"half-transparent red over grey: {p}");
        var text = Doc().Add(new TextNote(0, new Point(2, 2), "WWW", 30, Colors.Black, true, TextBackdrop.None, Colors.Black), out _);
        var img = Exporter.Export(text);
        int dark = 0;
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++) if (img.GetPixel(x, y).B < 80) dark++;
        Check.True(dark > 100, $"black text over grey: {dark} dark pixels");
        Check.True(img.GetPixel(59, 39).B == 200, "the far corner is untouched");
        var measured = VectorRenderer.TextBounds((TextNote)text.Vector[0]);
        Check.True(measured.Width > 30 && measured.Height > 20 && measured.X == 2 && measured.Y == 2, $"measured {measured}");
    }

    // ------------------------------------------------------------------ RasterComposer

    /// <summary>A test op: every pixel of the rectangle becomes the average of its (2r+1)-square neighbourhood in what is below, plus the op's rank in the red channel.</summary>
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

    private static RasterComposer TestComposer() => new() { OpFor = o => o is BlurRegion b ? new AverageOp(b) : null };

    private static BgraImage Noise(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var img = new BgraImage(w, h);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = (byte)rnd.Next(256); img.Pixels[i + 1] = (byte)rnd.Next(256); img.Pixels[i + 2] = (byte)rnd.Next(256); img.Pixels[i + 3] = 255; }
        return img;
    }

    [UnitTest]
    public static void With_no_ops_and_no_adjustment_the_composite_is_the_base_itself()
    {
        var doc = Doc();
        var composer = new RasterComposer();
        Check.True(ReferenceEquals(composer.Compose(doc), doc.Base), "no copy at all");
        var withVector = doc.Add(new RectOutline(0, new Rect(1, 1, 3, 3), Red, 1), out _);
        Check.True(ReferenceEquals(composer.Compose(withVector), doc.Base), "the vector band is not the composer's");
        var raster = doc.Add(new BlurRegion(0, new RectI(2, 2, 10, 10), 1, false, 0), out _);
        Check.True(ReferenceEquals(composer.Compose(raster), doc.Base), "a raster object nothing can draw yet: skipped");
        var tried = TestComposer().Compose(raster);
        Check.False(ReferenceEquals(tried, doc.Base), "with an op it is a new image");
        Check.Equal(doc.Base.Pixels, Solid(60, 40, 200, 200, 200).Pixels, "and the base was not touched");
    }

    [UnitTest]
    public static void The_adjustment_stage_is_a_seam_and_changes_the_base_when_it_is_not_the_identity()
    {
        var composer = new RasterComposer { Adjuster = (v, img) => { var c = img.Clone(); for (int i = 0; i < c.Pixels.Length; i += 4) c.Pixels[i] = (byte)v.Brightness; return c; } };
        var doc = Doc();
        Check.True(ReferenceEquals(composer.Compose(doc), doc.Base), "the identity never reaches the adjuster");
        var bright = doc with { Adjust = new AdjustmentValues(77, 0, 0) };
        var adjusted = composer.Compose(bright);
        Check.Equal(77, (int)adjusted.GetPixel(3, 3).B);
        Check.True(ReferenceEquals(adjusted, composer.Compose(bright)), "cached per (picture, values)");
        Check.Equal(0, (int)composer.Compose(bright with { Adjust = new AdjustmentValues(0, 5, 0) }).GetPixel(3, 3).B + 0);
    }

    [UnitTest]
    public static void An_incremental_composite_with_a_dirty_rectangle_equals_a_from_scratch_one_for_random_edits()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            var rnd = new Random(seed);
            const int W = 48, H = 32;
            var doc = DocumentState.Create(Noise(W, H, seed));
            var incremental = TestComposer();
            BlurRegion RandomOp() => new(0, new RectI(rnd.Next(0, W - 8), rnd.Next(0, H - 8), rnd.Next(3, 16), rnd.Next(3, 12)), rnd.Next(0, 3), false, rnd.Next(0, 40));
            for (int i = 0; i < 4; i++) doc = doc.Add(RandomOp(), out _);
            incremental.Compose(doc, null);
            for (int step = 0; step < 40; step++)
            {
                RectI dirty = RectI.Empty;
                int pick = rnd.Next(doc.Raster.Count);
                switch (rnd.Next(4))
                {
                    case 0:   // move
                    {
                        var old = (BlurRegion)doc.Raster[pick];
                        var moved = old with { R = new RectI(rnd.Next(0, W - 8), rnd.Next(0, H - 8), old.R.Width, old.R.Height) };
                        dirty = old.R.Union(moved.R);
                        doc = doc.Replace(moved);
                        break;
                    }
                    case 1:   // change the radius or the strength
                    {
                        var old = (BlurRegion)doc.Raster[pick];
                        doc = doc.Replace(old with { Radius = rnd.Next(0, 3), Block = rnd.Next(0, 40) });
                        dirty = old.R;
                        break;
                    }
                    case 2:   // delete one
                        if (doc.Raster.Count > 1) { dirty = ((BlurRegion)doc.Raster[pick]).R; doc = doc.Remove(doc.Raster[pick].Id); }
                        break;
                    default:   // add one on top
                        doc = doc.Add(RandomOp(), out var added);
                        dirty = ((BlurRegion)added).R;
                        break;
                }
                var fresh = TestComposer().Compose(doc);
                var inc = incremental.Compose(doc, dirty.IsEmpty ? null : dirty);
                Check.Equal(fresh.Pixels, inc.Pixels, $"seed {seed} step {step}");
            }
        }
    }

    [UnitTest]
    public static void The_below_cache_recomputes_only_the_ops_from_the_first_changed_one()
    {
        var doc = DocumentState.Create(Noise(40, 30, 9));
        for (int i = 0; i < 4; i++) doc = doc.Add(new BlurRegion(0, new RectI(i * 5, i * 3, 12, 10), 1, false, 0), out _);
        var composer = TestComposer();
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
        Check.Equal(4, composer.LastRecomputed, "a change at the bottom recomputes the ones above");
        composer.Clear();
        composer.Compose(edited);
        Check.Equal(4, composer.LastRecomputed);
    }

    // ------------------------------------------------------------------ options

    [UnitTest]
    public static void Editor_options_are_clamped_and_unknown_values_go_back_to_their_defaults()
    {
        var o = new ScreenshotEditorOptions { Width = 999, FontSize = 1, Color = "red", TextColor = "#12", TextBackdrop = "Glow", BackdropColor = "#80FFFFFF" };
        o.Normalize();
        Check.Equal(ScreenshotEditorOptions.MaxWidth, o.Width);
        Check.Equal(ScreenshotEditorOptions.MinFontSize, o.FontSize);
        Check.Equal(ScreenshotEditorOptions.DefaultColor, o.Color, "a word is not a colour");
        Check.Equal(ScreenshotEditorOptions.DefaultTextColor, o.TextColor);
        Check.Equal("outline", o.TextBackdrop);
        Check.Equal("#80FFFFFF", o.BackdropColor, "a colour with alpha is kept");
        Check.Equal(Color.FromArgb(0x80, 255, 255, 255), o.BackdropColorValue);

        var low = new ScreenshotEditorOptions { Width = -3, FontSize = 9999, TextBackdrop = " BOX " };
        low.Normalize();
        Check.Equal(ScreenshotEditorOptions.MinWidth, low.Width);
        Check.Equal(ScreenshotEditorOptions.MaxFontSize, low.FontSize);
        Check.Equal("box", low.TextBackdrop);
        var nan = new ScreenshotEditorOptions { Width = double.NaN, FontSize = double.PositiveInfinity, Color = null! };
        nan.Normalize();
        Check.Equal((4.0, 28.0, ScreenshotEditorOptions.DefaultColor), (nan.Width, nan.FontSize, nan.Color));
        Check.Equal(Colors.White, new ScreenshotEditorOptions().TextColorValue);
        Check.Equal("#E53935", ScreenshotEditorOptions.ToHex(new ScreenshotEditorOptions().ColorValue));
    }

    [UnitTest]
    public static void Editor_options_are_part_of_the_settings_file_and_are_normalized_on_load()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        Check.Equal(4.0, Settings.FromJson("{}").ScreenshotEditor.Width, "default options without the key");
        Check.Equal(4.0, Settings.FromJson("""{ "ScreenshotEditor": null }""").ScreenshotEditor.Width, "a null block");
        var s = Settings.FromJson("""{ "ScreenshotEditor": { "Width": 500, "Color": "#112233", "TextBackdrop": "box", "FutureKey": 7 } }""");
        Check.Equal(40.0, s.ScreenshotEditor.Width, "clamped");
        Check.Equal("#112233", s.ScreenshotEditor.Color);
        Check.Equal("box", s.ScreenshotEditor.TextBackdrop);

        s.ScreenshotEditor.FontSize = 33;
        File.WriteAllText(dir.File("settings.json"), s.ToJson());
        var back = Settings.Load();
        Check.Equal(33.0, back.ScreenshotEditor.FontSize);
        Check.Equal("#112233", back.ScreenshotEditor.Color);
        Check.True(back.ScreenshotEditor.Unknown != null && back.ScreenshotEditor.Unknown.ContainsKey("FutureKey"), "a key a newer version wrote is kept");
        Check.Contains(back.ToJson(), "FutureKey");
    }

    // ------------------------------------------------------------------ the canvas (without a window)

    private static EditorCanvas Canvas(EditorSession session, double w = 1000, double h = 800)
    {
        var canvas = new EditorCanvas();
        canvas.Measure(new Size(w, h));
        canvas.Arrange(new Rect(0, 0, w, h));
        canvas.Attach(session);
        return canvas;
    }

    [UnitTest]
    public static void The_canvas_keys_pick_tools_nudge_delete_and_undo()
    {
        var session = new EditorSession(Doc(200, 100));
        var canvas = Canvas(session);
        var none = System.Windows.Input.ModifierKeys.None;
        var shift = System.Windows.Input.ModifierKeys.Shift;
        var ctrl = System.Windows.Input.ModifierKeys.Control;
        foreach (var (key, mods, tool) in new[]
        {
            (System.Windows.Input.Key.P, none, EditorTool.Pen), (System.Windows.Input.Key.A, none, EditorTool.Arrow), (System.Windows.Input.Key.A, shift, EditorTool.StraightArrow),
            (System.Windows.Input.Key.L, none, EditorTool.Line), (System.Windows.Input.Key.R, none, EditorTool.Rectangle), (System.Windows.Input.Key.T, none, EditorTool.Text),
            (System.Windows.Input.Key.C, none, EditorTool.Crop), (System.Windows.Input.Key.V, none, EditorTool.Select),
        })
        {
            Check.True(canvas.HandleKey(key, mods), key.ToString());
            Check.Equal(tool, session.Tool, key.ToString());
        }
        Check.False(canvas.HandleKey(System.Windows.Input.Key.Escape, none), "Esc with nothing to drop: the window closes");

        var rect = session.Add(new RectOutline(0, new Rect(10, 10, 20, 20), Red, 2));
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Right, none));
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Down, shift));
        Check.Equal(new Rect(11, 20, 20, 20), ((RectOutline)session.Selected!).R, "1 px, then 10 with Shift");
        Check.True(canvas.HandleKey(System.Windows.Input.Key.D, ctrl), "duplicate");
        Check.Equal(2, session.State.Vector.Count);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Delete, none));
        Check.Equal(1, session.State.Vector.Count);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Z, ctrl), "undo the delete");
        Check.Equal(2, session.State.Vector.Count);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Z, ctrl | shift), "Ctrl+Shift+Z redoes");
        Check.Equal(1, session.State.Vector.Count);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Z, ctrl));
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Y, ctrl), "Ctrl+Y redoes too");
        Check.Equal(1, session.State.Vector.Count);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Z, ctrl));
        Check.Equal(2, session.State.Vector.Count);
        session.Select(rect.Id);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Escape, none), "Esc deselects first");
        Check.Null(session.SelectedId);
        session.SetTool(EditorTool.Pen);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Escape, none), "then goes back to Select");
        Check.Equal(EditorTool.Select, session.Tool);
        Check.False(canvas.HandleKey(System.Windows.Input.Key.P, System.Windows.Input.ModifierKeys.Alt), "Alt combinations are the system's");
        canvas.Detach();
    }

    [UnitTest]
    public static void Zoom_fits_at_most_100_percent_and_100_percent_is_one_screen_pixel_per_picture_pixel()
    {
        var small = new EditorSession(Doc(400, 300));
        var c1 = Canvas(small);
        c1.ZoomToFit();
        Check.Equal(1.0, c1.Zoom, "a picture that fits is shown at 100%");
        var big = new EditorSession(DocumentState.Create(new BgraImage(4000, 3000)));
        var c2 = Canvas(big);
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(c2).DpiScaleX;
        c2.ZoomToFit();
        Check.Near(Math.Min((1000 * dpi - 32) / 4000, (800 * dpi - 32) / 3000), c2.Zoom, 1e-9, "fit with a 16 px margin");
        Check.True(c2.Zoom < 1);
        c2.ZoomActual();
        Check.Equal(1.0, c2.Zoom, "100%");
        Check.Near(1.0, c2.ScaleDip * dpi, 1e-9, "physical pixels per picture pixel, whatever the DPI scale");
        c2.ZoomBy(1.15, new Point(500, 400));
        Check.Near(1.15, c2.Zoom, 1e-9);
        c2.ZoomTo(1000, new Point(0, 0));
        Check.True(c2.Zoom <= 32, "clamped");
        c2.ZoomTo(0.0001, new Point(0, 0));
        Check.True(c2.Zoom >= 0.02);
        int changes = 0;
        c2.ViewChanged += () => changes++;
        c2.PanBy(5, 5);
        c2.ZoomToFit();
        Check.True(changes >= 2);
        c1.Detach();
        c2.Detach();
    }

    [UnitTest]
    public static void A_drag_on_an_uncropped_picture_starts_a_new_crop_instead_of_moving_nothing()
    {
        var session = new EditorSession(Doc(200, 100));
        session.SetTool(EditorTool.Crop);
        var canvas = Canvas(session);
        Check.Equal(session.Committed.Base.Bounds, session.Committed.Crop, "the whole picture is the crop");
        canvas.BeginCrop(new Point(50, 20));
        Check.True(canvas.HasGesture);
        var end = canvas.DocToView(new Point(120, 70));
        canvas.DragTo(end);
        Check.Equal(new RectI(50, 20, 70, 50), session.State.Crop, "a new rectangle follows the drag");
        canvas.EndGesture(end);
        Check.Equal(new RectI(50, 20, 70, 50), session.Committed.Crop);
        Check.True(session.IsDirty);

        // inside an existing crop a drag still moves it
        canvas.BeginCrop(new Point(85, 45));
        var to = canvas.DocToView(new Point(95, 55));
        canvas.DragTo(to);
        Check.Equal(new RectI(60, 30, 70, 50), session.State.Crop, "moved by (10, 10)");
        canvas.EndGesture(to);
        canvas.Detach();
    }

    [UnitTest]
    public static void Esc_during_a_drag_and_a_pan_started_in_a_drag_both_drop_the_preview()
    {
        var session = new EditorSession(Doc(200, 100));
        session.SetTool(EditorTool.Crop);
        var canvas = Canvas(session);
        canvas.BeginCrop(new Point(50, 20));
        canvas.DragTo(canvas.DocToView(new Point(120, 70)));
        Check.True(session.HasPreview);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None), "Esc is used up by the drag");
        Check.False(canvas.HasGesture);
        Check.False(session.HasPreview, "the preview is gone");
        Check.Equal(session.Committed.Base.Bounds, session.State.Crop);
        Check.False(session.IsDirty);
        Check.Equal(EditorTool.Crop, session.Tool, "the tool is untouched");

        canvas.BeginCrop(new Point(50, 20));
        canvas.DragTo(canvas.DocToView(new Point(120, 70)));
        Check.True(session.HasPreview);
        canvas.BeginPan(new Point(10, 10));   // the middle button went down during the left drag
        Check.False(session.HasPreview, "no phantom preview is left");
        canvas.DragTo(new Point(30, 30));
        canvas.EndGesture(new Point(30, 30));
        Check.False(session.IsDirty);
        canvas.Detach();
    }

    [UnitTest]
    public static void Choosing_the_crop_tool_deselects_so_keys_cannot_act_on_a_hidden_selection()
    {
        var session = new EditorSession(Doc(200, 100));
        var canvas = Canvas(session);
        session.Add(new RectOutline(0, new Rect(10, 10, 20, 20), Red, 2));
        Check.NotNull(session.SelectedId);
        session.SetTool(EditorTool.Crop);
        Check.Null(session.SelectedId);
        Check.False(canvas.HandleKey(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None));
        Check.Equal(1, session.State.Vector.Count, "the rectangle is still there");
        canvas.Detach();
    }

    [UnitTest]
    public static void The_window_fits_the_work_area_on_common_screens_and_never_throws()
    {
        // (physical screen size, work area height without the taskbar, scale)
        foreach (var (sw, sh, workH, dpi) in new[] { (1920, 1080, 1008, 1.5), (1366, 768, 728, 1.0), (2560, 1440, 1368, 1.75), (2560, 1600, 1528, 2.0), (1280, 720, 672, 1.0), (1920, 1080, 1040, 2.0) })
        {
            foreach (var (iw, ih) in new[] { (10, 8), (800, 600), (1920, 1080), (7000, 3000) })
            {
                var (x, y, w, h) = ScreenshotEditorWindow.Place(iw, ih, dpi, 100, 50, sw, workH);
                string what = $"{sw}x{sh}@{dpi} picture {iw}x{ih}";
                Check.True(w > 0 && h > 0, what);
                Check.True(x >= 100 && y >= 50 && x + w <= 100 + sw && y + h <= 50 + workH, what + ": inside the work area");
                Check.True(w <= sw * 0.9 + 1 && h <= workH * 0.9 + 1, what + ": within 90%");
                Check.True(Math.Abs(x - (100 + (sw - w) / 2)) <= 1 && Math.Abs(y - (50 + (workH - h) / 2)) <= 1, what + ": centred");
            }
        }
        var (_, _, bw, bh) = ScreenshotEditorWindow.Place(10, 8, 1.0, 0, 0, 3000, 2000);
        Check.Equal((1000, 700), (bw, bh), "a small picture: the base size");
    }

    [UnitTest]
    public static void The_minimum_size_never_exceeds_the_window_on_a_small_work_area_at_200_percent()
    {
        // 1920 x 1080 at 200%: work area 1920 x 1040 physical = 960 x 520 DIPs; the XAML minimum (860 x 520) is cut to 90% of that
        var (minW, minH) = ScreenshotEditorWindow.MinSizeFor(860, 520, 2.0, 1920, 1040);
        Check.Near(860, minW, 1e-9, "the width fits");
        Check.Near(468, minH, 1e-9, "the height is cut to 90% of the work area");
        var (x, y, w, h) = ScreenshotEditorWindow.Place(10, 8, 2.0, 0, 0, 1920, 1040);
        Check.True(minW * 2.0 <= w + 1e-9 && minH * 2.0 <= h + 1e-9, $"the minimum {minW}x{minH} DIP fits the window {w}x{h} px at {x},{y}");
        var (bigW, bigH) = ScreenshotEditorWindow.MinSizeFor(860, 520, 1.0, 3000, 2000);
        Check.Equal((860.0, 520.0), (bigW, bigH), "a big screen keeps the XAML minimum");
    }

    [UnitTest]
    public static void A_freehand_arrow_shorter_than_its_head_has_the_whole_head_in_the_bounds_and_in_the_export()
    {
        foreach (double width in new[] { 10.0, 40.0 })
        {
            var arrow = new FreehandArrow(0, ImmutableArray.Create(new Point(70, 80), new Point(75, 80)), Colors.Red, width);
            var b = HitTest.Bounds(arrow, VectorRenderer.TextBounds);
            var shape = ArrowGeometry.Freehand(arrow.Pts, arrow.Width);
            Check.True(shape.HasHead, "width " + width);
            foreach (var p in new[] { shape.Tip, shape.BaseLeft, shape.BaseRight })
                Check.True(b.Contains(p), $"width {width}: the head corner {p} is inside {b}");

            // a crop around it must show exactly what the uncropped picture shows there (nothing of the head is cut off by the render area)
            var doc = Doc(160, 160).Add(arrow, out _);
            var full = Exporter.Export(doc);
            var crop = new RectI(30, 30, 100, 100);
            var part = Exporter.Export(doc.WithCrop(crop));
            for (int y = 0; y < crop.Height; y++)
                for (int x = 0; x < crop.Width; x++)
                    if (full.GetPixel(crop.X + x, crop.Y + y) != part.GetPixel(x, y)) throw new Exception($"width {width}: pixel {x},{y} of the crop differs");
            Check.True(CountDifferent(full, Doc(160, 160).Base) > 20, "the arrow was drawn");
        }
    }

    [UnitTest]
    public static void A_text_note_is_rendered_on_the_crop_aligned_area_only()
    {
        var crop = new RectI(0, 0, 60, 40);
        var huge = new Rect(10, 10, 15000, 5000);   // a 5000-px font
        Check.Equal(new RectI(0, 0, 64, 64), Exporter.TextArea(huge, crop), "cut to the crop expanded to the 32-px grid");
        Check.Equal(new RectI(32, 32, 32, 32), Exporter.TextArea(huge, new RectI(40, 40, 20, 20)), "the origin stays on the grid");
        Check.Equal(new RectI(0, 0, 96, 32), Exporter.TextArea(new Rect(40, 4, 10, 10), new RectI(30, 0, 50, 10)), "a small note: its own grid area, cut to the aligned crop");
        Check.True(Exporter.TextArea(new Rect(500, 500, 10, 10), crop).IsEmpty, "a note outside the crop");
        var real = (TextNote)Doc().Add(new TextNote(0, new Point(10, 10), "W", 5000, Colors.White, false, TextBackdrop.None, Colors.Black), out _).Vector[0];
        var area = Exporter.TextArea(VectorRenderer.TextBounds(real), crop);
        Check.True(area.Width <= 64 && area.Height <= 64 && area.X % 32 == 0 && area.Y % 32 == 0, "a real huge note: " + area);
    }

    [UnitTest]
    public static void Keys_during_a_drag_are_swallowed_and_the_drag_commits_the_expected_state()
    {
        var none = System.Windows.Input.ModifierKeys.None;
        var ctrl = System.Windows.Input.ModifierKeys.Control;
        var session = new EditorSession(Doc(200, 100));
        session.Add(new RectOutline(0, new Rect(10, 10, 20, 20), Red, 2));
        session.SetTool(EditorTool.Crop);
        var canvas = Canvas(session);
        canvas.BeginCrop(new Point(50, 20));
        var end = canvas.DocToView(new Point(120, 70));
        canvas.DragTo(end);
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Z, ctrl), "Ctrl+Z is swallowed");
        Check.True(canvas.HandleKey(System.Windows.Input.Key.Delete, none), "Delete is swallowed");
        Check.True(canvas.HandleKey(System.Windows.Input.Key.P, none), "a tool letter is swallowed");
        Check.True(canvas.HasGesture, "the drag goes on");
        Check.Equal(EditorTool.Crop, session.Tool, "the tool did not change");
        Check.Equal(1, session.State.Vector.Count, "the rectangle was not undone");
        canvas.EndGesture(end);
        Check.Equal(new RectI(50, 20, 70, 50), session.Committed.Crop, "the drag commits the crop");
        Check.Equal(1, session.Committed.Vector.Count);
        canvas.Detach();
    }

    [UnitTest]
    public static void A_second_editor_is_refused_and_the_first_one_stays()
    {
        var host = new EditorWindowHost { ShowWindows = false };   // made, not shown: no focus, no key sent
        var request = new EditorRequest
        {
            Document = Doc(),
            Title = "t",
            IsNewCapture = true,
            Options = new ScreenshotEditorOptions(),
            SaveAsync = (_, _, _) => Task.FromResult(true),
        };
        var first = host.EditAsync(request);
        Check.True(host.IsOpen);
        Check.False(first.IsCompleted, "open until it is closed");
        var second = host.EditAsync(request);
        Check.True(second.IsCompleted && !second.Result, "refused at once");
        Check.True(host.IsOpen, "still the first window");
        host.Window!.CloseApproved();
        Check.True(first.IsCompleted && first.Result, "the first completes when it closes");
        Check.False(host.IsOpen);
    }

    [UnitTest]
    public static void A_straight_arrow_shorter_than_half_its_head_still_has_its_head_in_the_bounds()
    {
        var arrow = new LineShape(1, new Point(10, 10), new Point(12, 10), Red, 2, true);
        var b = HitTest.Bounds(arrow, VectorRenderer.TextBounds);
        var shape = ArrowGeometry.Straight(arrow.A, arrow.B, arrow.Width, true);
        Check.True(shape.HasHead);
        foreach (var p in new[] { shape.Tip, shape.BaseLeft, shape.BaseRight })
            Check.True(b.Contains(p), "the head corner " + p + " is inside " + b);
    }
}
