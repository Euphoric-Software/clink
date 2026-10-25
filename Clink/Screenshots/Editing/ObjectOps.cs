using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>Pure edits of single objects (move, resize, recolour). Each returns a new record; the object it is given is never changed.</summary>
internal static class ObjectOps
{
    public static EditorObject Translate(EditorObject o, double dx, double dy)
    {
        Point Mv(Point p) => new(p.X + dx, p.Y + dy);
        ImmutableArray<Point> Mvs(ImmutableArray<Point> pts) => pts.Select(Mv).ToImmutableArray();
        RectI MvI(RectI r) => r.Offset((int)Math.Round(dx), (int)Math.Round(dy));
        return o switch
        {
            PenStroke s => s with { Pts = Mvs(s.Pts) },
            LineShape l => l with { A = Mv(l.A), B = Mv(l.B) },
            FreehandArrow a => a with { Pts = Mvs(a.Pts) },
            RectOutline r => r with { R = new Rect(r.R.X + dx, r.R.Y + dy, r.R.Width, r.R.Height) },
            TextNote t => t with { Origin = Mv(t.Origin) },
            BlurRegion b => b with { R = MvI(b.R) },
            HighlightRect h => h with { R = MvI(h.R) },
            HighlightStroke hs => hs with { Pts = Mvs(hs.Pts) },
            SpotlightBox sp => sp with { Box = MvI(sp.Box) },
            MagnifyBox m => m with { Dest = MvI(m.Dest) },
            SmartEraseRect e => e with { R = MvI(e.R) },
            InsertedImage i => i with { Dest = MvI(i.Dest) },
            _ => o,
        };
    }

    /// <summary>The object with <paramref name="handle"/> dragged to <paramref name="to"/>; objects that have no such handle come back unchanged.</summary>
    public static EditorObject Resize(EditorObject o, Handle handle, Point to, bool keepAspect)
    {
        static RectI ToI(Rect r)
        {
            int x0 = (int)Math.Round(r.Left), y0 = (int)Math.Round(r.Top), x1 = (int)Math.Round(r.Right), y1 = (int)Math.Round(r.Bottom);
            return new RectI(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        }
        static Rect FromI(RectI r) => new(r.X, r.Y, r.Width, r.Height);
        switch (o)
        {
            case LineShape l when handle == Handle.Start: return l with { A = to };
            case LineShape l when handle == Handle.End: return l with { B = to };
            case RectOutline r: return r with { R = HitTest.ResizeRect(r.R, handle, to, keepAspect) };
            case BlurRegion b: return b with { R = ToI(HitTest.ResizeRect(FromI(b.R), handle, to, keepAspect)) };
            case HighlightRect h: return h with { R = ToI(HitTest.ResizeRect(FromI(h.R), handle, to, keepAspect)) };
            case SpotlightBox sp: return sp with { Box = ToI(HitTest.ResizeRect(FromI(sp.Box), handle, to, keepAspect)) };
            case MagnifyBox m: return m with { Dest = MagnifyLayout.ResizeDest(m.Source, m.Dest, handle, to) };   // always the proportions of the source
            case SmartEraseRect e: return e with { R = ToI(HitTest.ResizeRect(FromI(e.R), handle, to, keepAspect)) };
            case InsertedImage i: return i with { Dest = ToI(HitTest.ResizeRect(FromI(i.Dest), handle, to, true)) };
            default: return o;
        }
    }

    /// <summary>The object in <paramref name="color"/> (text: the text colour); null when the object has no colour to change.</summary>
    public static EditorObject? WithColor(EditorObject o, Color color) => o switch
    {
        PenStroke s => s with { Color = color },
        LineShape l => l with { Color = color },
        FreehandArrow a => a with { Color = color },
        RectOutline r => r with { Color = color },
        TextNote t => t with { Fill = color },
        _ => null,
    };

    /// <summary>The object with the line width <paramref name="width"/>; null for an object that has none (text).</summary>
    public static EditorObject? WithWidth(EditorObject o, double width) => o switch
    {
        PenStroke s => s with { Width = width },
        LineShape l => l with { Width = width },
        FreehandArrow a => a with { Width = width },
        RectOutline r => r with { Width = width },
        _ => null,
    };
}
