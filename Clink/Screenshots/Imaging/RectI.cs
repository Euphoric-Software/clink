namespace Clink.Screenshots.Imaging;

/// <summary>
/// An integer rectangle in physical pixels: <c>X</c>/<c>Y</c> is the top-left pixel, the rectangle covers the pixels
/// <c>[X, X + Width)</c> x <c>[Y, Y + Height)</c>. A negative width or height never exists: the constructor throws on one and the
/// factories normalise. A rectangle with no area is <see cref="IsEmpty"/>; the factories and the set operations return exactly
/// <see cref="Empty"/> for one, but <c>new RectI(3, 3, 0, 0)</c> is constructible too, so test <see cref="IsEmpty"/>, never
/// <c>== Empty</c>. Used in virtual-screen coordinates (which can be negative) and in image coordinates.
/// </summary>
internal readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public static readonly RectI Empty = new(0, 0, 0, 0);

    public int Width { get; } = Width >= 0 ? Width : throw new ArgumentOutOfRangeException(nameof(Width), Width, "a rectangle has no negative width");
    public int Height { get; } = Height >= 0 ? Height : throw new ArgumentOutOfRangeException(nameof(Height), Height, "a rectangle has no negative height");

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    /// <summary>
    /// The rectangle between two corner POINTS (pixel edges) in any order. No pixel is added: from (10,10) to (20,30) is 10 x 20, and
    /// equal points give <see cref="Empty"/>. For a drag between two pixels the pointer was on, use <see cref="FromCornerPixels"/>.
    /// </summary>
    public static RectI FromCorners(int x1, int y1, int x2, int y2) =>
        FromLtrb(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    /// <summary>
    /// The rectangle that holds both corner PIXELS, in any order (a drag from the pixel under the pointer at the press to the one
    /// under it now): (0,0) to (1919,1079) is 1920 x 1080, and a drag left / up gives the same size as right / down. Never empty.
    /// </summary>
    public static RectI FromCornerPixels(int x1, int y1, int x2, int y2) =>
        FromLtrb(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2) + 1, Math.Max(y1, y2) + 1);

    public static RectI FromLtrb(int left, int top, int right, int bottom) =>
        right <= left || bottom <= top ? Empty : new RectI(left, top, right - left, bottom - top);

    /// <summary>True when the pixel (x, y) is inside.</summary>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool Contains(RectI other) => !other.IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>The overlap of two rectangles; <see cref="Empty"/> when they do not overlap.</summary>
    public RectI Intersect(RectI other) => FromLtrb(Math.Max(X, other.X), Math.Max(Y, other.Y), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));

    public bool Intersects(RectI other) => !Intersect(other).IsEmpty;

    /// <summary>The smallest rectangle that holds both; an empty one adds nothing.</summary>
    public RectI Union(RectI other)
    {
        if (IsEmpty) return other.IsEmpty ? Empty : other;
        if (other.IsEmpty) return this;
        return FromLtrb(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public RectI Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>Grown by <paramref name="amount"/> on every side (shrunk when negative; an over-shrunk rectangle is empty).</summary>
    public RectI Inflate(int amount) => Inflate(amount, amount);

    /// <summary>Grown by <paramref name="dx"/> on the left and right and by <paramref name="dy"/> on the top and bottom (shrunk when negative; an over-shrunk rectangle is <see cref="Empty"/>).</summary>
    public RectI Inflate(int dx, int dy) => FromLtrb(X - dx, Y - dy, Right + dx, Bottom + dy);

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
