using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// A raster in BGRA, 8 bits per channel, row-major with no padding (<see cref="Stride"/> = Width * 4).
/// <para><b>Alpha convention: straight (non-premultiplied).</b> B, G and R are the colour as it is, whatever A says; a capture is
/// fully opaque (A = 255 everywhere, <see cref="ForceOpaque"/>), and transparent pixels exist only where nothing was drawn (a gap
/// between monitors in <see cref="Compose"/>, the padding of <see cref="Crop"/>). WPF's own drawing surfaces (RenderTargetBitmap)
/// are premultiplied Pbgra32: go through <see cref="FromPbgra(BitmapSource)"/> / <see cref="ToPbgra"/>, never copy their bytes in as they are.</para>
/// The pixel type of the whole screenshot subsystem: the GDI source fills one per monitor, the overlay shows it, the PNG writer and
/// the clipboard take it. Pure managed code apart from <see cref="ToBitmapSource"/> / <see cref="From"/>. The API is the one
/// the later editor slices build on.
/// </summary>
internal sealed class BgraImage
{
    /// <summary>Largest image: the buffer is one managed array, so width * height * 4 has to fit in an int.</summary>
    public const long MaxPixels = int.MaxValue / 4;

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    /// <summary>A transparent black image.</summary>
    public BgraImage(int width, int height) : this(width, height, null) { }

    /// <summary>An image over <paramref name="pixels"/> (not copied); null allocates a transparent black buffer.</summary>
    public BgraImage(int width, int height, byte[]? pixels)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width), "negative size");
        if ((long)width * height > MaxPixels) throw new ArgumentOutOfRangeException(nameof(width), $"{width} x {height} is too large");
        Width = width;
        Height = height;
        int length = width * height * 4;
        if (pixels != null && pixels.Length != length) throw new ArgumentException($"{pixels.Length} bytes for {width} x {height} (need {length})", nameof(pixels));
        Pixels = pixels ?? new byte[length];
    }

    public RectI Bounds => new(0, 0, Width, Height);

    public BgraImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>The pixel at (x, y) as (B, G, R, A). Outside the image: throws.</summary>
    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) is outside {Width} x {Height}");
        int i = y * Stride + x * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a = 255)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) is outside {Width} x {Height}");
        int i = y * Stride + x * 4;
        Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = a;
    }

    /// <summary>The bytes of row <paramref name="y"/> (Width * 4, BGRA). Outside the image: throws.</summary>
    public Span<byte> Row(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y), $"row {y} is outside {Width} x {Height}");
        return Pixels.AsSpan(y * Stride, Stride);
    }

    /// <summary>Every pixel, row after row (BGRA).</summary>
    public Span<byte> AsSpan() => Pixels.AsSpan();

    /// <summary>
    /// The pixels of <paramref name="r"/> as an image of its own. With <paramref name="pad"/> (the default) the part of
    /// <paramref name="r"/> outside this image is transparent black, so the result always has the size of <paramref name="r"/>;
    /// without it <paramref name="r"/> is first cut to this image, so the result has only real pixels (and may be smaller). An empty
    /// rectangle (or, without padding, one outside the image) gives a 0 x 0 image.
    /// </summary>
    public BgraImage Crop(RectI r, bool pad = true)
    {
        if (!pad) r = r.Intersect(Bounds);
        if (r.IsEmpty) return new BgraImage(0, 0);
        var result = new BgraImage(r.Width, r.Height);
        result.Blit(this, r, 0, 0);
        return result;
    }

    /// <summary>Copies the pixels of <paramref name="srcRect"/> (clipped to <paramref name="src"/> and to this image) to (dstX, dstY), replacing what is there. Alpha is copied as it is.</summary>
    public void Blit(BgraImage src, RectI srcRect, int dstX, int dstY)
    {
        // Clip the source rectangle to the source, then both to this image; keep the offset between the two consistent.
        var s = srcRect.Intersect(src.Bounds);
        if (s.IsEmpty) return;
        int dx = dstX + (s.X - srcRect.X), dy = dstY + (s.Y - srcRect.Y);
        var d = new RectI(dx, dy, s.Width, s.Height).Intersect(Bounds);
        if (d.IsEmpty) return;
        int sx = s.X + (d.X - dx), sy = s.Y + (d.Y - dy);
        int bytes = d.Width * 4;
        for (int row = 0; row < d.Height; row++)
            Buffer.BlockCopy(src.Pixels, (sy + row) * src.Stride + sx * 4, Pixels, (d.Y + row) * Stride + d.X * 4, bytes);
    }

    /// <summary>
    /// An image of <paramref name="area"/> (a rectangle in any coordinate system) made of the parts: each is an image and where its
    /// top-left pixel sits in that coordinate system. Later parts are drawn over earlier ones; whatever no part covers (a gap
    /// between monitors of an irregular layout) stays transparent.
    /// </summary>
    public static BgraImage Compose(RectI area, IEnumerable<(RectI Placement, BgraImage Image)> parts)
    {
        if (area.IsEmpty) return new BgraImage(0, 0);
        var result = new BgraImage(area.Width, area.Height);
        foreach (var (placement, image) in parts)
        {
            var overlap = placement.Intersect(area);
            if (overlap.IsEmpty) continue;
            var local = new RectI(overlap.X - placement.X, overlap.Y - placement.Y, overlap.Width, overlap.Height);
            result.Blit(image, local, overlap.X - area.X, overlap.Y - area.Y);
        }
        return result;
    }

    /// <summary>
    /// Sets every alpha to 255 (GDI leaves it 0, which would make a transparent PNG). Returns this image.
    /// </summary>
    public BgraImage ForceOpaque()
    {
        var p = Pixels;
        for (int i = 3; i < p.Length; i += 4) p[i] = 255;
        return this;
    }

    /// <summary>True when any pixel is not fully opaque.</summary>
    public bool HasTransparency()
    {
        var p = Pixels;
        for (int i = 3; i < p.Length; i += 4) if (p[i] != 255) return true;
        return false;
    }

    /// <summary>
    /// The capture came out black: every sampled pixel of <paramref name="r"/> (clipped to the image) has all colour channels at
    /// most <paramref name="threshold"/>. Samples a 64 x 64 grid across the rectangle, so it costs the same for any size. An empty
    /// rectangle (or one outside the image) is not black: there is nothing to say about it.
    /// </summary>
    public bool LooksBlack(RectI r, int threshold = 2)
    {
        var c = r.Intersect(Bounds);
        if (c.IsEmpty) return false;
        const int grid = 64;
        int stepsX = Math.Min(grid, c.Width), stepsY = Math.Min(grid, c.Height);
        for (int gy = 0; gy < stepsY; gy++)
        {
            int y = c.Y + (int)((long)gy * c.Height / stepsY);
            for (int gx = 0; gx < stepsX; gx++)
            {
                int x = c.X + (int)((long)gx * c.Width / stepsX);
                int i = y * Stride + x * 4;
                if (Pixels[i] > threshold || Pixels[i + 1] > threshold || Pixels[i + 2] > threshold) return false;
            }
        }
        return true;
    }

    /// <summary>A new frozen WPF bitmap of this image (Bgra32, a copy of the pixels). <paramref name="dpi"/> 96 makes one image pixel one DIP.</summary>
    public BitmapSource ToBitmapSource(double dpi = 96)
    {
        if (Width == 0 || Height == 0) throw new InvalidOperationException("an empty image has no bitmap");
        var bmp = BitmapSource.Create(Width, Height, dpi, dpi, PixelFormats.Bgra32, null, Pixels, Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>The pixels of a WPF bitmap, converted to Bgra32 when it is in another format.</summary>
    public static BgraImage From(BitmapSource source)
    {
        var src = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var image = new BgraImage(w, h);
        if (w > 0 && h > 0) src.CopyPixels(Int32Rect.Empty, image.Pixels, image.Stride, 0);
        return image;
    }

    /// <summary>
    /// The pixels of a premultiplied WPF bitmap (Pbgra32: what RenderTargetBitmap draws into), un-premultiplied into this class's
    /// straight alpha. A bitmap in another format is converted to Pbgra32 first.
    /// </summary>
    public static BgraImage FromPbgra(BitmapSource source)
    {
        var src = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var bytes = new byte[w * h * 4];
        if (w > 0 && h > 0) src.CopyPixels(Int32Rect.Empty, bytes, w * 4, 0);
        return FromPbgra(w, h, bytes);
    }

    /// <summary>Premultiplied BGRA bytes (row-major, no padding) as a straight-alpha image; the array is reused. A = 0 gives transparent black.</summary>
    public static BgraImage FromPbgra(int width, int height, byte[] pbgra)
    {
        var image = new BgraImage(width, height, pbgra);
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            if (a == 255) continue;
            if (a == 0) { p[i] = p[i + 1] = p[i + 2] = 0; continue; }
            for (int c = 0; c < 3; c++) p[i + c] = (byte)Math.Min(255, (p[i + c] * 255 + a / 2) / a);
        }
        return image;
    }

    /// <summary>This image as premultiplied BGRA bytes (a new array; what a Pbgra32 WPF bitmap holds). An opaque image's bytes are unchanged.</summary>
    public byte[] ToPbgra()
    {
        var p = (byte[])Pixels.Clone();
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) p[i + c] = (byte)((p[i + c] * a + 127) / 255);
        }
        return p;
    }
}
