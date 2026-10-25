using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

internal enum TextBackdrop { None, Box, Outline }

/// <summary>Brightness / contrast -100..100, sharpness 0..200 (%; the unsharp amount is Sharpness / 100): what the (S4) adjustment stage of <see cref="RasterComposer"/> applies to the base. 0 / 0 / 0 (no change) is the identity.</summary>
internal sealed record AdjustmentValues(int Brightness = 0, int Contrast = 0, int Sharpness = 0)
{
    public static readonly AdjustmentValues Identity = new();
    public bool IsIdentity => Brightness == 0 && Contrast == 0 && Sharpness == 0;
}

/// <summary>One thing on the picture. Immutable; an edit makes a new record (<c>with</c>) and a new <see cref="DocumentState"/>.</summary>
internal abstract record EditorObject(int Id);

// ---- vector band: drawn by WPF, always above the whole raster band
internal sealed record PenStroke(int Id, ImmutableArray<Point> Pts, Color Color, double Width) : EditorObject(Id);
internal sealed record LineShape(int Id, Point A, Point B, Color Color, double Width, bool ArrowAtEnd) : EditorObject(Id);
internal sealed record FreehandArrow(int Id, ImmutableArray<Point> Pts, Color Color, double Width) : EditorObject(Id);
internal sealed record RectOutline(int Id, Rect R, Color Color, double Width) : EditorObject(Id);
internal sealed record TextNote(int Id, Point Origin, string Text, double Size, Color Fill, bool Bold, TextBackdrop Backdrop, Color BackdropColor) : EditorObject(Id);

// ---- raster band: CPU BGRA, each op samples the composite of everything below it in the raster band (data only in S3; the ops are S4's)
internal sealed record BlurRegion(int Id, RectI R, int Radius, bool Pixelate, int Block) : EditorObject(Id);
internal sealed record HighlightRect(int Id, RectI R, Color Color) : EditorObject(Id);
internal sealed record HighlightStroke(int Id, ImmutableArray<Point> Pts, double Width, Color Color) : EditorObject(Id);
internal sealed record SpotlightBox(int Id, RectI Box, double Dim, int Radius, bool Gradient, int FalloffPx) : EditorObject(Id);
internal sealed record MagnifyBox(int Id, RectI Source, RectI Dest, Color Outline, double OutlineWidth, bool Smooth) : EditorObject(Id);
internal sealed record SmartEraseRect(int Id, RectI R) : EditorObject(Id);
internal sealed record InsertedImage(int Id, BgraImage Image, RectI Dest, double Opacity) : EditorObject(Id);

/// <summary>
/// The whole editable document as an immutable value: the base picture (shared by reference between states), the two object bands in
/// z-order (first = bottom), the crop and the adjustments. Every edit returns a new state that shares what did not change, so the undo
/// stack is just a list of these. Vector objects always draw above the whole raster band (the "two bands" decision, docs A3).
/// </summary>
internal sealed record DocumentState(
    BgraImage Base,
    ImmutableList<EditorObject> Raster,
    ImmutableList<EditorObject> Vector,
    RectI Crop,
    AdjustmentValues Adjust,
    int NextId = 1)
{
    /// <summary>A fresh document over <paramref name="baseImage"/>: no objects, the whole picture as the crop.</summary>
    public static DocumentState Create(BgraImage baseImage) =>
        new(baseImage, ImmutableList<EditorObject>.Empty, ImmutableList<EditorObject>.Empty, baseImage.Bounds, AdjustmentValues.Identity);

    public static bool IsRaster(EditorObject o) => o is BlurRegion or HighlightRect or HighlightStroke or SpotlightBox or MagnifyBox or SmartEraseRect or InsertedImage;

    public bool HasObjects => !Raster.IsEmpty || !Vector.IsEmpty;

    public EditorObject? Find(int id) => Vector.FirstOrDefault(o => o.Id == id) ?? Raster.FirstOrDefault(o => o.Id == id);

    /// <summary>Adds an object on top of its band under a fresh id (the id in <paramref name="proto"/> is ignored); returns the new state and the object as stored.</summary>
    public DocumentState Add(EditorObject proto, out EditorObject added)
    {
        added = proto with { Id = NextId };
        var s = this with { NextId = NextId + 1 };
        return IsRaster(added) ? s with { Raster = Raster.Add(added) } : s with { Vector = Vector.Add(added) };
    }

    /// <summary>Replaces the object that has <paramref name="replacement"/>'s id (in its band, same z position); unchanged when there is none.</summary>
    public DocumentState Replace(EditorObject replacement)
    {
        int i = Vector.FindIndex(o => o.Id == replacement.Id);
        if (i >= 0) return replacement.Equals(Vector[i]) ? this : this with { Vector = Vector.SetItem(i, replacement) };
        i = Raster.FindIndex(o => o.Id == replacement.Id);
        if (i < 0 || replacement.Equals(Raster[i])) return this;
        return this with { Raster = Raster.SetItem(i, replacement) };
    }

    public DocumentState Remove(int id)
    {
        int i = Vector.FindIndex(o => o.Id == id);
        if (i >= 0) return this with { Vector = Vector.RemoveAt(i) };
        i = Raster.FindIndex(o => o.Id == id);
        return i >= 0 ? this with { Raster = Raster.RemoveAt(i) } : this;
    }

    /// <summary>Moves an object <paramref name="delta"/> places up (+) or down (-) inside its own band, clamped; <c>int.MaxValue</c> / <c>int.MinValue</c> = to the front / back.</summary>
    public DocumentState Reorder(int id, int delta)
    {
        int i = Vector.FindIndex(o => o.Id == id);
        if (i >= 0)
        {
            int to = (int)Math.Clamp((long)i + delta, 0, Vector.Count - 1);
            return to == i ? this : this with { Vector = Vector.RemoveAt(i).Insert(to, Vector[i]) };
        }
        i = Raster.FindIndex(o => o.Id == id);
        if (i < 0) return this;
        int rt = (int)Math.Clamp((long)i + delta, 0, Raster.Count - 1);
        return rt == i ? this : this with { Raster = Raster.RemoveAt(i).Insert(rt, Raster[i]) };
    }

    /// <summary>The same document with a different crop, clipped to the picture (an empty result keeps the old crop).</summary>
    public DocumentState WithCrop(RectI crop)
    {
        var c = crop.Intersect(Base.Bounds);
        return c.IsEmpty || c == Crop ? this : this with { Crop = c };
    }
}
