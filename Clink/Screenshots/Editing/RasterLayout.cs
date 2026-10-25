using System.Windows;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>Pure placement rules of the magnifier: where the enlarged copy appears, how it follows the zoom, and how it resizes (always the proportions of its source).</summary>
internal static class MagnifyLayout
{
    /// <summary>The gap between the source and the enlarged copy.</summary>
    public const int Gap = 12;
    public const int MinSide = 8;

    private static (int W, int H) SizeFor(RectI source, double zoom, RectI bounds)
    {
        double w = Math.Max(1, source.Width * zoom), h = Math.Max(1, source.Height * zoom);
        // never bigger than the picture
        double fit = Math.Min(1.0, Math.Min(bounds.Width / w, bounds.Height / h));
        if (fit < 1) { w *= fit; h *= fit; }
        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    /// <summary>
    /// The enlarged copy of <paramref name="source"/> at <paramref name="zoom"/>: to its right when it fits inside the picture there,
    /// else below, left, above (sliding along the side to fit); when it fits nowhere, kept inside the picture on top of the source.
    /// </summary>
    public static RectI PlaceDest(RectI source, double zoom, RectI bounds)
    {
        var (w, h) = SizeFor(source, zoom, bounds);
        int maxX = Math.Max(0, bounds.Width - w), maxY = Math.Max(0, bounds.Height - h);
        var candidates = new[]
        {
            new RectI(source.Right + Gap, Math.Clamp(source.Y, 0, maxY), w, h),
            new RectI(Math.Clamp(source.X, 0, maxX), source.Bottom + Gap, w, h),
            new RectI(source.X - Gap - w, Math.Clamp(source.Y, 0, maxY), w, h),
            new RectI(Math.Clamp(source.X, 0, maxX), source.Y - Gap - h, w, h),
        };
        foreach (var c in candidates)
            if (bounds.Contains(c)) return c;
        return new RectI(Math.Clamp(source.X, 0, maxX), Math.Clamp(source.Y, 0, maxY), w, h);
    }

    /// <summary>The copy at another zoom: the same centre when it fits, kept inside the picture.</summary>
    public static RectI WithZoom(RectI dest, RectI source, double zoom, RectI bounds)
    {
        var (w, h) = SizeFor(source, zoom, bounds);
        int cx = dest.X + dest.Width / 2, cy = dest.Y + dest.Height / 2;
        int x = Math.Clamp(cx - w / 2, 0, Math.Max(0, bounds.Width - w)), y = Math.Clamp(cy - h / 2, 0, Math.Max(0, bounds.Height - h));
        return new RectI(x, y, w, h);
    }

    /// <summary>The copy after dragging <paramref name="handle"/> to <paramref name="to"/>: the opposite side or corner stays, the proportions are those of <paramref name="source"/>.</summary>
    public static RectI ResizeDest(RectI source, RectI dest, Handle handle, Point to)
    {
        double aspect = source.Width > 0 && source.Height > 0 ? source.Width / (double)source.Height : dest.Width / (double)Math.Max(1, dest.Height);
        var orig = new Rect(dest.X, dest.Y, dest.Width, dest.Height);
        var r1 = HitTest.ResizeRect(orig, handle, to, keepAspect: true, MinSide);
        bool horizEdge = handle is Handle.Top or Handle.Bottom, vertEdge = handle is Handle.Left or Handle.Right;
        double w = r1.Width, h = r1.Height;
        if (horizEdge) w = h * aspect; else h = w / aspect;
        if (w < MinSide) { w = MinSide; h = w / aspect; }
        if (h < MinSide) { h = MinSide; w = h * aspect; }
        double x = handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft ? orig.Right - w : orig.Left;
        double y = handle is Handle.TopLeft or Handle.Top or Handle.TopRight ? orig.Bottom - h : orig.Top;
        if (vertEdge) y = orig.Top + (orig.Height - h) / 2;
        if (horizEdge) x = orig.Left + (orig.Width - w) / 2;
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
        return new RectI(ix, iy, Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    /// <summary>The source rectangle moved by (dx, dy), kept inside the picture.</summary>
    public static RectI MoveSource(RectI source, int dx, int dy, RectI bounds)
    {
        int x = Math.Clamp(source.X + dx, 0, Math.Max(0, bounds.Width - source.Width)), y = Math.Clamp(source.Y + dy, 0, Math.Max(0, bounds.Height - source.Height));
        return new RectI(x, y, source.Width, source.Height);
    }
}

/// <summary>Where an inserted picture goes.</summary>
internal static class InsertPlacement
{
    /// <summary>
    /// The destination of an image of <paramref name="imgW"/> x <paramref name="imgH"/> on a picture of <paramref name="picW"/> x
    /// <paramref name="picH"/>: its own size, or smaller so that it takes at most half the picture's width and half its height
    /// (never enlarged), proportions kept, centred on <paramref name="centre"/> and kept inside the picture.
    /// </summary>
    public static RectI Fit(int imgW, int imgH, int picW, int picH, Point centre)
    {
        imgW = Math.Max(1, imgW); imgH = Math.Max(1, imgH);
        double scale = Math.Min(1.0, Math.Min(0.5 * picW / imgW, 0.5 * picH / imgH));
        if (!(scale > 0)) scale = 1;
        int w = Math.Max(1, (int)Math.Round(imgW * scale)), h = Math.Max(1, (int)Math.Round(imgH * scale));
        w = Math.Min(w, Math.Max(1, picW)); h = Math.Min(h, Math.Max(1, picH));
        int x = (int)Math.Round(centre.X - w / 2.0), y = (int)Math.Round(centre.Y - h / 2.0);
        x = Math.Clamp(x, 0, Math.Max(0, picW - w));
        y = Math.Clamp(y, 0, Math.Max(0, picH - h));
        return new RectI(x, y, w, h);
    }
}
