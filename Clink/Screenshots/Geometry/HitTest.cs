using System.Windows;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Geometry;

/// <summary>A selection handle: the 8 around a rectangle, or the 2 ends of a line / arrow.</summary>
internal enum Handle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Start, End }

/// <summary>What is under the pointer: a handle of the selected object, an object, or nothing.</summary>
internal sealed record HitResult(Handle Handle, EditorObject? Object)
{
    public static readonly HitResult Nothing = new(Handle.None, null);
}

/// <summary>
/// Pure hit testing in document pixels. The pick distance is a fixed number of screen DIPs, so it is divided by the view scale
/// (DIPs per document pixel): the same few pixels of slack whatever the zoom. Order: handles of the selected object, then the
/// vector band top-down, then the raster band top-down.
/// </summary>
internal static class HitTest
{
    /// <summary>How far from a line (beyond half its width) a click still hits it, in DIPs.</summary>
    public const double SlopDip = 6;
    /// <summary>How far from a handle's centre a click still grabs it, in DIPs.</summary>
    public const double HandleHitDip = 7;

    /// <summary>Document pixels of tolerance for a stroke of <paramref name="width"/>: half its width plus <see cref="SlopDip"/> / scale.</summary>
    public static double Tolerance(double width, double viewScale) => width / 2 + SlopDip / Math.Max(viewScale, 1e-6);

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-18) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    public static double DistanceToPolyline(Point p, IReadOnlyList<Point> pts)
    {
        if (pts.Count == 0) return double.PositiveInfinity;
        if (pts.Count == 1) return (p - pts[0]).Length;
        double best = double.PositiveInfinity;
        for (int i = 1; i < pts.Count; i++) best = Math.Min(best, DistanceToSegment(p, pts[i - 1], pts[i]));
        return best;
    }

    /// <summary>The distance from <paramref name="p"/> to the border of the rectangle (0 on it, positive inside and outside).</summary>
    public static double DistanceToBorder(Point p, Rect r)
    {
        var c = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft, r.TopLeft };
        return DistanceToPolyline(p, c);
    }

    /// <summary>The box that holds the object, in document pixels. <paramref name="textBounds"/> measures a text note (WPF's FormattedText: the caller's).</summary>
    public static Rect Bounds(EditorObject o, Func<TextNote, Rect> textBounds)
    {
        switch (o)
        {
            case PenStroke s: return Inflated(PointsBox(s.Pts), s.Width / 2);
            case FreehandArrow a:
            {
                var box = Inflated(PointsBox(a.Pts), Math.Max(a.Width / 2, ArrowGeometry.HeadLength(a.Width) * 0.5));
                if (!box.IsEmpty && ArrowGeometry.Freehand(a.Pts, a.Width) is { HasHead: true } head)
                {
                    // a very short flick: the head reaches further than the inflation above
                    box.Union(head.Tip);
                    box.Union(head.BaseLeft);
                    box.Union(head.BaseRight);
                }
                return box;
            }
            case LineShape l:
            {
                var box = Inflated(Rect.Union(new Rect(l.A, l.B), new Rect(l.B, l.B)), l.ArrowAtEnd ? Math.Max(l.Width / 2, ArrowGeometry.HeadLength(l.Width) * 0.5) : l.Width / 2);
                if (l.ArrowAtEnd && ArrowGeometry.Straight(l.A, l.B, l.Width, true) is { HasHead: true } shape)
                {
                    // an arrow shorter than half its head: the head reaches further back than the inflation above
                    box.Union(shape.Tip);
                    box.Union(shape.BaseLeft);
                    box.Union(shape.BaseRight);
                }
                return box;
            }
            case RectOutline r: return Inflated(r.R, r.Width / 2);
            case TextNote t: return textBounds(t);
            case BlurRegion b: return ToRect(b.R);
            case HighlightRect h: return ToRect(h.R);
            case HighlightStroke hs: return Inflated(PointsBox(hs.Pts), hs.Width / 2);
            case SpotlightBox sp: return ToRect(sp.Box);
            case MagnifyBox m: return ToRect(m.Dest);
            case SmartEraseRect e: return ToRect(e.R);
            case InsertedImage i: return ToRect(i.Dest);
            default: return Rect.Empty;
        }
    }

    private static Rect ToRect(RectI r) => new(r.X, r.Y, r.Width, r.Height);

    private static Rect Inflated(Rect r, double by)
    {
        if (r.IsEmpty) return r;
        r.Inflate(by, by);
        return r;
    }

    private static Rect PointsBox(IReadOnlyList<Point> pts)
    {
        if (pts.Count == 0) return Rect.Empty;
        double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y), x1 = pts.Max(p => p.X), y1 = pts.Max(p => p.Y);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>True when <paramref name="p"/> is on the object: close to the line for strokes / lines / arrows, close to the border for a rectangle outline, inside the box for text and raster objects.</summary>
    public static bool HitObject(EditorObject o, Point p, double viewScale, Func<TextNote, Rect> textBounds)
    {
        switch (o)
        {
            case PenStroke s: return DistanceToPolyline(p, s.Pts) <= Tolerance(s.Width, viewScale);
            case FreehandArrow a:
                return DistanceToPolyline(p, a.Pts) <= Tolerance(a.Width, viewScale)
                    || (a.Pts.Length > 0 && (p - a.Pts[^1]).Length <= ArrowGeometry.HeadLength(a.Width) * 0.6);
            case LineShape l:
                return DistanceToSegment(p, l.A, l.B) <= Tolerance(l.Width, viewScale)
                    || (l.ArrowAtEnd && (p - l.B).Length <= ArrowGeometry.HeadLength(l.Width) * 0.6);
            case RectOutline r: return DistanceToBorder(p, r.R) <= Tolerance(r.Width, viewScale);
            case HighlightStroke hs: return DistanceToPolyline(p, hs.Pts) <= Tolerance(hs.Width, viewScale);
            default:
                var b = Bounds(o, textBounds);
                if (b.IsEmpty) return false;
                b.Inflate(SlopDip / Math.Max(viewScale, 1e-6), SlopDip / Math.Max(viewScale, 1e-6));
                return b.Contains(p);
        }
    }

    /// <summary>The topmost object under <paramref name="p"/>: the vector band from the top, then the raster band from the top. Null when none.</summary>
    public static EditorObject? Topmost(DocumentState doc, Point p, double viewScale, Func<TextNote, Rect> textBounds)
    {
        for (int i = doc.Vector.Count - 1; i >= 0; i--)
            if (HitObject(doc.Vector[i], p, viewScale, textBounds)) return doc.Vector[i];
        for (int i = doc.Raster.Count - 1; i >= 0; i--)
            if (HitObject(doc.Raster[i], p, viewScale, textBounds)) return doc.Raster[i];
        return null;
    }

    // ------------------------------------------------------------------ handles

    /// <summary>The handles an object offers: both ends of a line / arrow, the 8 around a rectangle-like object, none for freehand strokes and text (move only).</summary>
    public static IReadOnlyList<(Handle Handle, Point At)> HandlesOf(EditorObject o)
    {
        switch (o)
        {
            case LineShape l: return new[] { (Handle.Start, l.A), (Handle.End, l.B) };
            case RectOutline r: return RectHandles(r.R);
            case BlurRegion b: return RectHandles(ToRect(b.R));
            case HighlightRect h: return RectHandles(ToRect(h.R));
            case SpotlightBox sp: return RectHandles(ToRect(sp.Box));
            case MagnifyBox m: return RectHandles(ToRect(m.Dest));
            case SmartEraseRect e: return RectHandles(ToRect(e.R));
            case InsertedImage i: return RectHandles(ToRect(i.Dest));
            default: return Array.Empty<(Handle, Point)>();
        }
    }

    public static IReadOnlyList<(Handle Handle, Point At)> RectHandles(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return new[]
        {
            (Handle.TopLeft, new Point(r.Left, r.Top)), (Handle.Top, new Point(cx, r.Top)), (Handle.TopRight, new Point(r.Right, r.Top)),
            (Handle.Right, new Point(r.Right, cy)), (Handle.BottomRight, new Point(r.Right, r.Bottom)), (Handle.Bottom, new Point(cx, r.Bottom)),
            (Handle.BottomLeft, new Point(r.Left, r.Bottom)), (Handle.Left, new Point(r.Left, cy)),
        };
    }

    /// <summary>The handle of <paramref name="handles"/> within reach of <paramref name="p"/>, the nearest when two are; <see cref="Handle.None"/> when none is.</summary>
    public static Handle HitHandle(IReadOnlyList<(Handle Handle, Point At)> handles, Point p, double viewScale)
    {
        double reach = HandleHitDip / Math.Max(viewScale, 1e-6);
        var best = Handle.None;
        double bestD = double.PositiveInfinity;
        foreach (var (h, at) in handles)
        {
            double d = (p - at).Length;
            if (d <= reach && d < bestD) { best = h; bestD = d; }
        }
        return best;
    }

    /// <summary>
    /// What is under <paramref name="p"/>: first a handle of <paramref name="selected"/>, which wins over every object (also over one
    /// that lies on top of it), then <see cref="Topmost"/>.
    /// </summary>
    public static HitResult Hit(DocumentState doc, EditorObject? selected, Point p, double viewScale, Func<TextNote, Rect> textBounds)
    {
        if (selected != null)
        {
            var h = HitHandle(HandlesOf(selected), p, viewScale);
            if (h != Handle.None) return new HitResult(h, selected);
        }
        var top = Topmost(doc, p, viewScale, textBounds);
        return top == null ? HitResult.Nothing : new HitResult(Handle.None, top);
    }

    /// <summary>
    /// The rectangle after dragging <paramref name="handle"/> to <paramref name="to"/>: the opposite edge or corner stays, the
    /// result is normalised (a drag past the opposite side flips) and at least <paramref name="minSize"/> on a side. With
    /// <paramref name="keepAspect"/> a corner drag keeps the original proportions.
    /// </summary>
    public static Rect ResizeRect(Rect orig, Handle handle, Point to, bool keepAspect, double minSize = 1)
    {
        double l = orig.Left, t = orig.Top, r = orig.Right, b = orig.Bottom;
        bool moveL = handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft, moveR = handle is Handle.TopRight or Handle.Right or Handle.BottomRight;
        bool moveT = handle is Handle.TopLeft or Handle.Top or Handle.TopRight, moveB = handle is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;
        if (keepAspect && (moveL || moveR) && (moveT || moveB) && orig.Width > 0 && orig.Height > 0)
        {
            double ax = moveL ? r : l, ay = moveT ? b : t;   // the corner that stays
            double w = Math.Abs(to.X - ax), h = Math.Abs(to.Y - ay);
            double aspect = orig.Width / orig.Height;
            if (w / aspect > h) h = w / aspect; else w = h * aspect;
            double sx = to.X < ax ? -1 : 1, sy = to.Y < ay ? -1 : 1;
            to = new Point(ax + sx * w, ay + sy * h);
        }
        if (moveL) l = to.X; if (moveR) r = to.X;
        if (moveT) t = to.Y; if (moveB) b = to.Y;
        double x0 = Math.Min(l, r), x1 = Math.Max(l, r), y0 = Math.Min(t, b), y1 = Math.Max(t, b);
        if (x1 - x0 < minSize) { if (moveL && !moveR) x0 = x1 - minSize; else x1 = x0 + minSize; }
        if (y1 - y0 < minSize) { if (moveT && !moveB) y0 = y1 - minSize; else y1 = y0 + minSize; }
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
