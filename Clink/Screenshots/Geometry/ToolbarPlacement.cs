using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Geometry;

/// <summary>Where a floating toolbar goes next to a selection (the overlay editor of a later slice). Pure; all in physical pixels.</summary>
internal static class ToolbarPlacement
{
    public const int Gap = 8;

    /// <summary>
    /// Below the selection when it fits in the work area, else above it, else inside it at the bottom (a selection that fills the
    /// screen); left-aligned with the selection and clamped into the work area (a toolbar wider than the work area sits at its left
    /// edge; one taller than it at its top).
    /// </summary>
    public static (int X, int Y) Place(RectI selection, (int Width, int Height) toolbar, RectI workArea, int gap = Gap)
    {
        int x = Math.Min(selection.X, workArea.Right - toolbar.Width);
        x = Math.Max(x, workArea.X);
        int y;
        if (selection.Bottom + gap + toolbar.Height <= workArea.Bottom) y = selection.Bottom + gap;
        else if (selection.Y - gap - toolbar.Height >= workArea.Y) y = selection.Y - gap - toolbar.Height;
        else y = selection.Bottom - gap - toolbar.Height;   // inside, at the bottom
        y = Math.Min(y, workArea.Bottom - toolbar.Height);
        y = Math.Max(y, workArea.Y);
        return (x, y);
    }
}
