using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Clink.Screenshots.Geometry;

namespace Clink.Screenshots.Editing;

/// <summary>
/// Draws the vector band's objects with WPF in document pixels. The editor canvas gives each object a <see cref="DrawingVisual"/>
/// (<see cref="CreateVisual"/>) and the exporter draws them all into one render target through <see cref="Draw"/>, so what is on
/// screen at 100% and what is saved come from the same code. UI thread (the brushes are frozen; formatted text is thread-affine).
/// </summary>
internal static class VectorRenderer
{
    private static readonly Typeface s_regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface s_bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>The visual of one object, with the text options the export uses.</summary>
    public static DrawingVisual CreateVisual(EditorObject o)
    {
        var v = new DrawingVisual();
        Configure(v);
        using (var dc = v.RenderOpen()) Draw(dc, o);
        return v;
    }

    /// <summary>Grayscale text (ClearType on a transparent target fringes) with ideal formatting: the same on screen and in the export.</summary>
    public static void Configure(DrawingVisual v)
    {
        TextOptions.SetTextRenderingMode(v, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(v, TextFormattingMode.Ideal);
    }

    private static Brush BrushOf(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen RoundPen(Color c, double width)
    {
        var p = new Pen(BrushOf(c), Math.Max(0.1, width)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    public static void Draw(DrawingContext dc, EditorObject o)
    {
        switch (o)
        {
            case PenStroke s: DrawPolyline(dc, s.Pts.Length > 2 ? StrokeSmoothing.Chaikin(s.Pts, 2) : s.Pts.ToList(), s.Color, s.Width); break;
            case LineShape l: DrawLine(dc, l); break;
            case FreehandArrow a: DrawArrow(dc, ArrowGeometry.Freehand(a.Pts, a.Width), a.Color, a.Width); break;
            case RectOutline r:
                var pen = new Pen(BrushOf(r.Color), Math.Max(0.1, r.Width)) { LineJoin = PenLineJoin.Miter };
                pen.Freeze();
                dc.DrawRectangle(null, pen, r.R);
                break;
            case TextNote t: DrawText(dc, t); break;
        }
    }

    private static void DrawLine(DrawingContext dc, LineShape l)
    {
        var shape = ArrowGeometry.Straight(l.A, l.B, l.Width, l.ArrowAtEnd);
        if (l.ArrowAtEnd) DrawArrow(dc, shape, l.Color, l.Width);
        else DrawPolyline(dc, shape.Shaft, l.Color, l.Width);
    }

    private static void DrawPolyline(DrawingContext dc, IReadOnlyList<Point> pts, Color color, double width)
    {
        if (pts.Count == 0) return;
        if (pts.Count == 1 || (pts.Count == 2 && pts[0] == pts[1]))
        {
            dc.DrawEllipse(BrushOf(color), null, pts[0], width / 2, width / 2);   // a dot
            return;
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, RoundPen(color, width), g);
    }

    private static void DrawArrow(DrawingContext dc, ArrowShape shape, Color color, double width)
    {
        if (shape.Shaft.Length > 0 && !(shape.HasHead && shape.Shaft.Length == 1)) DrawPolyline(dc, shape.Shaft, color, width);
        if (!shape.HasHead) return;
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(shape.Tip, true, true);
            ctx.LineTo(shape.BaseLeft, true, false);
            ctx.LineTo(shape.BaseRight, true, false);
        }
        head.Freeze();
        var brush = BrushOf(color);
        dc.DrawGeometry(brush, new Pen(brush, 1) { LineJoin = PenLineJoin.Round }, head);
    }

    // ------------------------------------------------------------------ text

    public static FormattedText Format(TextNote t, Brush? brush = null)
    {
        string text = string.IsNullOrEmpty(t.Text) ? " " : t.Text;
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, t.Bold ? s_bold : s_regular,
            Math.Max(1, t.Size), brush ?? BrushOf(t.Fill), 1.0);
    }

    /// <summary>The box of a text note in document pixels (its origin is the top-left).</summary>
    public static Rect TextBounds(TextNote t)
    {
        var ft = Format(t);
        return new Rect(t.Origin.X, t.Origin.Y, Math.Max(1, ft.WidthIncludingTrailingWhitespace), Math.Max(1, ft.Height));
    }

    private static void DrawText(DrawingContext dc, TextNote t)
    {
        if (string.IsNullOrEmpty(t.Text)) return;
        var ft = Format(t);
        var box = new Rect(t.Origin.X, t.Origin.Y, Math.Max(1, ft.WidthIncludingTrailingWhitespace), Math.Max(1, ft.Height));
        switch (t.Backdrop)
        {
            case TextBackdrop.Box:
                var pad = new Rect(box.X - 6, box.Y - 3, box.Width + 12, box.Height + 6);
                dc.DrawRoundedRectangle(BrushOf(t.BackdropColor), null, pad, 4, 4);
                break;
            case TextBackdrop.Outline:
                var outline = new Pen(BrushOf(t.BackdropColor), 4) { LineJoin = PenLineJoin.Round };
                outline.Freeze();
                dc.DrawGeometry(null, outline, ft.BuildGeometry(t.Origin));
                break;
        }
        dc.DrawText(ft, t.Origin);
    }
}
