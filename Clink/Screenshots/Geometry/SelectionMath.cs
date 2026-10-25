using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Geometry;

/// <summary>A rectangle in device-independent pixels (what WPF lays out in), relative to one window.</summary>
internal readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>The part of a selection that lies on one monitor: <c>Global</c> in virtual-screen pixels, <c>Local</c> relative to that monitor's top-left.</summary>
internal readonly record struct MonitorPart(int Index, RectI Global, RectI Local);

/// <summary>
/// The coordinate arithmetic of the region overlay, pure so it is tested as it is. Everything is in physical pixels of the virtual
/// screen (negative to the left of / above the primary monitor) and only <see cref="ToDip"/> / <see cref="PxToDip"/> leave that
/// world, at the window edge. A rectangle covers the pixels <c>[X, X+Width)</c>.
/// </summary>
internal static class SelectionMath
{
    /// <summary>A drag smaller than this (either side) is a click: it clears the selection instead of making one.</summary>
    public const int MinDragSize = 3;

    /// <summary>The smallest rectangle that holds every monitor.</summary>
    public static RectI VirtualBounds(IEnumerable<RectI> monitors)
    {
        var bounds = RectI.Empty;
        foreach (var m in monitors) bounds = bounds.Union(m);
        return bounds;
    }

    /// <summary>The rectangle between two corner points (pixel edges), whichever way round; see <see cref="Band"/> for a drag.</summary>
    public static RectI Normalize(int x1, int y1, int x2, int y2) => RectI.FromCorners(x1, y1, x2, y2);

    /// <summary>
    /// The rubber band of a drag from the pixel under the pointer at the press to the pixel under it now, whichever way it goes:
    /// both corner pixels are inside (<see cref="RectI.FromCornerPixels"/>), so corner to corner of a 1920 x 1080 monitor is 1920 x 1080.
    /// </summary>
    public static RectI Band(int anchorX, int anchorY, int x, int y) => RectI.FromCornerPixels(anchorX, anchorY, x, y);

    /// <summary>The part of a rectangle inside the virtual screen (empty when none).</summary>
    public static RectI Clamp(RectI r, RectI virtualBounds) => r.Intersect(virtualBounds);

    /// <summary>
    /// The far corner PIXEL of a square drag (Shift): the side is the longer of the two drags, in the direction of each, and shrinks
    /// when the square would leave <paramref name="bounds"/> so it stays square (the corner pixel stays inside; the band from the
    /// anchor to it is then side + 1 pixels across, see <see cref="Band"/>).
    /// </summary>
    public static (int X, int Y) SquareCorner(int anchorX, int anchorY, int x, int y, RectI bounds)
    {
        int dx = x - anchorX, dy = y - anchorY;
        int sx = dx < 0 ? -1 : 1, sy = dy < 0 ? -1 : 1;
        int room = Math.Min(sx > 0 ? bounds.Right - 1 - anchorX : anchorX - bounds.X, sy > 0 ? bounds.Bottom - 1 - anchorY : anchorY - bounds.Y);
        int side = Math.Max(0, Math.Min(Math.Max(Math.Abs(dx), Math.Abs(dy)), room));
        return (anchorX + sx * side, anchorY + sy * side);
    }

    /// <summary>
    /// The monitor a rectangle belongs to: the one that holds the largest part of it (the first of equal ones), else the one nearest
    /// to its centre; -1 without monitors. (Where an editor inside the overlay would sit.)
    /// </summary>
    public static int MonitorIndexOf(RectI r, IReadOnlyList<RectI> monitors)
    {
        int best = -1;
        long bestArea = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            long area = r.Intersect(monitors[i]).Area;
            if (area > bestArea) { bestArea = area; best = i; }
        }
        return best >= 0 ? best : MonitorIndexAt(r.X + r.Width / 2, r.Y + r.Height / 2, monitors);
    }

    /// <summary>
    /// The detected game's name for a screenshot of <paramref name="rect"/>: the game, when the picture overlaps the monitor that is
    /// being recorded (where the game is); null without a game, without a recorded monitor or when the picture is elsewhere.
    /// </summary>
    public static string? GameFor(string? game, RectI? recordedMonitor, RectI rect)
    {
        if (string.IsNullOrWhiteSpace(game) || recordedMonitor is not { } mon) return null;
        return mon.Intersects(rect) ? game : null;
    }

    /// <summary>The same monitor rectangles, in any order (a display change that moved or resized nothing).</summary>
    public static bool SameLayout(IEnumerable<RectI> a, IEnumerable<RectI> b)
    {
        var x = a.OrderBy(r => r.X).ThenBy(r => r.Y).ThenBy(r => r.Width).ThenBy(r => r.Height).ToList();
        var y = b.OrderBy(r => r.X).ThenBy(r => r.Y).ThenBy(r => r.Width).ThenBy(r => r.Height).ToList();
        return x.SequenceEqual(y);
    }

    /// <summary>The index of the monitor holding the point, else the nearest one; -1 without monitors.</summary>
    public static int MonitorIndexAt(int x, int y, IReadOnlyList<RectI> monitors)
    {
        int best = -1;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (m.Contains(x, y)) return i;
            long dx = x < m.X ? m.X - x : x >= m.Right ? x - (m.Right - 1) : 0;
            long dy = y < m.Y ? m.Y - y : y >= m.Bottom ? y - (m.Bottom - 1) : 0;
            long d = dx * dx + dy * dy;
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>The selection cut at the monitors' edges: one part per monitor it touches (in monitor order). A selection across a gap has no part for the gap.</summary>
    public static IReadOnlyList<MonitorPart> SplitByMonitor(RectI selection, IReadOnlyList<RectI> monitors)
    {
        var parts = new List<MonitorPart>();
        if (selection.IsEmpty) return parts;
        for (int i = 0; i < monitors.Count; i++)
        {
            var overlap = selection.Intersect(monitors[i]);
            if (!overlap.IsEmpty) parts.Add(new MonitorPart(i, overlap, overlap.Offset(-monitors[i].X, -monitors[i].Y)));
        }
        return parts;
    }

    // ------------------------------------------------------------------ the window edge

    /// <summary>A pixel position as DIPs in a window that starts at <paramref name="origin"/> and runs at <paramref name="scale"/> pixels per DIP.</summary>
    public static double PxToDip(int px, int origin, double scale) => (px - origin) / scale;

    /// <summary>The pixel for a position in DIPs (rounded to the nearest pixel): the inverse of <see cref="PxToDip"/>.</summary>
    public static int DipToPx(double dip, int origin, double scale) => origin + (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);

    /// <summary>A virtual-screen rectangle as DIPs in the window of <paramref name="monitor"/>.</summary>
    public static DipRect ToDip(RectI r, RectI monitor, double scale) =>
        new(PxToDip(r.X, monitor.X, scale), PxToDip(r.Y, monitor.Y, scale), r.Width / scale, r.Height / scale);

    /// <summary>
    /// The inverse of <see cref="ToDip"/>: a rectangle in DIPs of the window of <paramref name="monitor"/> as virtual-screen pixels.
    /// Each edge is rounded to the nearest pixel edge (so <c>ToDip</c> and back gives the same rectangle at every scale); a
    /// rectangle that rounds to nothing is <see cref="RectI.Empty"/>.
    /// </summary>
    public static RectI DipToPx(DipRect r, RectI monitor, double scale) =>
        RectI.FromLtrb(DipToPx(r.X, monitor.X, scale), DipToPx(r.Y, monitor.Y, scale), DipToPx(r.Right, monitor.X, scale), DipToPx(r.Bottom, monitor.Y, scale));

    // ------------------------------------------------------------------ text and boxes

    /// <summary>"1280 x 720": the size label, in the physical pixels the PNG will have.</summary>
    public static string SizeLabel(RectI r) => $"{r.Width} x {r.Height}";

    /// <summary>
    /// Where the size label goes: above the selection's top-left corner, and inside the selection (at its top-left) when there is no
    /// room above; always kept inside the area.
    /// </summary>
    public static (double X, double Y) PlaceLabel(DipRect selection, double labelWidth, double labelHeight, double areaWidth, double areaHeight, double gap = 4)
    {
        double y = selection.Y - labelHeight - gap;
        if (y < 0) y = selection.Y + gap;
        double x = selection.X;
        x = Math.Max(0, Math.Min(x, areaWidth - labelWidth));
        y = Math.Max(0, Math.Min(y, areaHeight - labelHeight));
        return (x, y);
    }

    /// <summary>The loupe's top-left: to the lower right of the pointer, flipped to the other side at the area's right / bottom edge.</summary>
    public static (double X, double Y) PlaceLoupe(double pointerX, double pointerY, double width, double height, double areaWidth, double areaHeight, double offset = 24)
    {
        double x = pointerX + offset, y = pointerY + offset;
        if (x + width > areaWidth) x = pointerX - offset - width;
        if (y + height > areaHeight) y = pointerY - offset - height;
        x = Math.Max(0, Math.Min(x, Math.Max(0, areaWidth - width)));
        y = Math.Max(0, Math.Min(y, Math.Max(0, areaHeight - height)));
        return (x, y);
    }
}
