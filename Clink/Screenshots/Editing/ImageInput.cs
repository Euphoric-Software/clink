using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>Where Ctrl+V gets a picture (a made-up one in tests). UI thread.</summary>
internal interface IImageClipboard
{
    /// <summary>The picture on the clipboard, or null with the reason in <paramref name="error"/> (nothing there, too large, busy).</summary>
    BgraImage? GetImage(out string? error);

    /// <summary>
    /// Like <see cref="GetImage"/>, but a clipboard bitmap may come back still to be converted (<c>Bitmap</c>, frozen: it can go to a
    /// worker, where <see cref="ImageInput.FromBitmap"/> does the pixel conversion, the transparency scan and the reduction): only
    /// the reading of the clipboard itself must be on the UI thread. Exactly one of the two is set when there is a picture.
    /// </summary>
    (BgraImage? Image, BitmapSource? Bitmap) Read(out string? error)
    {
        var image = GetImage(out error);
        return (image, null);
    }
}

/// <summary>
/// Pictures that come from outside (files, the clipboard) are untrusted input decoded by WIC: refused above 16384 px on a side or
/// 100 megapixels, checked from the header before any pixel is decoded (PNG, JPEG and BMP by <see cref="PeekSize"/> before WIC is
/// even asked; other formats from WIC's lazily read frame), converted to straight-alpha Bgra32. Thread-safe (the files are read on a
/// worker); the clipboard is the UI thread's.
/// </summary>
internal static class ImageInput
{
    public const int MaxSide = PngFile.MaxSide;
    public const long MaxPixels = PngFile.MaxPixels;

    public const string TooLarge = "That picture is too large (the limit is 16384 pixels on a side and 100 megapixels)";
    public const string Unreadable = "That file could not be read as a picture";

    public static bool WithinLimits(int w, int h) => w > 0 && h > 0 && w <= MaxSide && h <= MaxSide && (long)w * h <= MaxPixels;

    /// <summary>A picture file (PNG, JPG, BMP, GIF, TIFF...). Null with the reason in <paramref name="error"/> when it cannot be read or is over the limits.</summary>
    public static BgraImage? LoadFile(string path, out string? error)
    {
        error = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return FromStream(fs, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or COMException)
        {
            error = Unreadable;
            return null;
        }
    }

    /// <summary>A picture in a stream (the clipboard's "PNG" format); see <see cref="LoadFile"/>.</summary>
    public static BgraImage? FromStream(Stream stream, out string? error)
    {
        error = null;
        try
        {
            if (stream.CanSeek)
            {
                long at = stream.Position;
                var size = PeekSize(stream);
                stream.Position = at;
                if (size is { } s && !WithinLimits(s.Width, s.Height)) { error = s.Width > 0 && s.Height > 0 ? TooLarge : Unreadable; return null; }
            }
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) { error = Unreadable; return null; }
            var frame = decoder.Frames[0];
            if (!WithinLimits(frame.PixelWidth, frame.PixelHeight)) { error = frame.PixelWidth > 0 && frame.PixelHeight > 0 ? TooLarge : Unreadable; return null; }
            return BgraImage.From(frame);   // FormatConvertedBitmap(Bgra32) when the format is another
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or COMException)
        {
            error = Unreadable;
            return null;
        }
    }

    /// <summary>
    /// A decoded bitmap (the clipboard's image); see <see cref="LoadFile"/>. A 32-bit bitmap whose alpha is 0 everywhere (a CF_DIB
    /// that never had an alpha channel: most programs leave the fourth byte 0) is made opaque, not pasted invisible.
    /// </summary>
    public static BgraImage? FromBitmap(BitmapSource source, out string? error)
    {
        error = null;
        if (!WithinLimits(source.PixelWidth, source.PixelHeight)) { error = source.PixelWidth > 0 && source.PixelHeight > 0 ? TooLarge : Unreadable; return null; }
        try
        {
            var image = BgraImage.From(source);
            if (AllTransparent(image)) image.ForceOpaque();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
        {
            error = Unreadable;
            return null;
        }
    }

    /// <summary>True when every alpha byte is 0 (false for an empty image).</summary>
    internal static bool AllTransparent(BgraImage image)
    {
        var p = image.Pixels;
        if (p.Length == 0) return false;
        for (int i = 3; i < p.Length; i += 4) if (p[i] != 0) return false;
        return true;
    }

    // ------------------------------------------------------------------ sizes from the headers

    /// <summary>
    /// The pixel size of a PNG, JPEG or BMP from its first bytes (the stream is read from its current position and left wherever the
    /// reading stopped); null for another format or a header that cannot be read (WIC then reads it lazily). Reads at most 1 MB.
    /// </summary>
    public static (int Width, int Height)? PeekSize(Stream s)
    {
        try
        {
            Span<byte> head = stackalloc byte[26];
            int got = ReadUpTo(s, head);
            if (got >= 24 && head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G'
                && head[12] == (byte)'I' && head[13] == (byte)'H' && head[14] == (byte)'D' && head[15] == (byte)'R')
                return (BinaryPrimitives.ReadInt32BigEndian(head.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(head.Slice(20, 4)));
            if (got >= 26 && head[0] == (byte)'B' && head[1] == (byte)'M') return BmpInfoSize(head[14..]);
            if (got >= 4 && head[0] == 0xFF && head[1] == 0xD8) return JpegSize(s, head[2..got]);
            return null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException) { return null; }
    }

    /// <summary>The size in a BITMAPINFOHEADER / BITMAPCOREHEADER (the bytes from the header's size field on; a CF_DIB starts there); the height may be negative (top-down).</summary>
    public static (int Width, int Height)? BmpInfoSize(ReadOnlySpan<byte> info)
    {
        if (info.Length < 8) return null;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(info);
        if (headerSize == 12) return (BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(4, 2)), BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(6, 2)));
        if (headerSize < 40 || info.Length < 12) return null;
        int w = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(4, 4)), h = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(8, 4));
        return (w, h == int.MinValue ? int.MaxValue : Math.Abs(h));
    }

    private static int ReadUpTo(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>The frame size from the first SOFn marker; <paramref name="already"/> are the bytes after the SOI that were read already.</summary>
    private static (int Width, int Height)? JpegSize(Stream s, ReadOnlySpan<byte> already)
    {
        var pending = new Queue<byte>(already.ToArray());
        long budget = 1 << 20;
        int Next()
        {
            if (pending.Count > 0) return pending.Dequeue();
            if (--budget < 0) return -1;
            return s.ReadByte();
        }
        for (int segments = 0; segments < 1024; segments++)
        {
            int b = Next();
            if (b != 0xFF) return null;
            int marker;
            do marker = Next(); while (marker == 0xFF);   // fill bytes
            if (marker < 0) return null;
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;   // no length
            if (marker == 0xD9 || marker == 0xDA) return null;                    // end, or the scan before any frame header
            int hi = Next(), lo = Next();
            if (hi < 0 || lo < 0) return null;
            int length = (hi << 8) | lo;
            if (length < 2) return null;
            bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (sof)
            {
                if (length < 7) return null;
                int precision = Next(), h1 = Next(), h0 = Next(), w1 = Next(), w0 = Next();
                if (precision < 0 || w0 < 0) return null;
                int h = (h1 << 8) | h0, w = (w1 << 8) | w0;
                return h == 0 ? null : (w, h);   // a height of 0 comes later (DNL): WIC decides
            }
            for (int i = 0; i < length - 2; i++) if (Next() < 0) return null;
        }
        return null;
    }

    // ------------------------------------------------------------------ fitting an inserted picture

    /// <summary>
    /// <paramref name="image"/> reduced once so that it is at most <paramref name="maxW"/> x <paramref name="maxH"/> (the picture it is
    /// inserted into), proportions kept: a box prefilter for the whole factor and then bilinear (<see cref="Resample.Bilinear"/> does
    /// both). The image itself when it fits already. Pure; worker thread (an 8000 x 6000 photo is 190 MB until it is reduced).
    /// </summary>
    public static BgraImage FitWithin(BgraImage image, int maxW, int maxH)
    {
        maxW = Math.Max(1, maxW); maxH = Math.Max(1, maxH);
        if (image.Width <= maxW && image.Height <= maxH) return image;
        double scale = Math.Min((double)maxW / image.Width, (double)maxH / image.Height);
        int w = Math.Clamp((int)Math.Round(image.Width * scale), 1, maxW), h = Math.Clamp((int)Math.Round(image.Height * scale), 1, maxH);
        var dst = new BgraImage(w, h);
        Resample.Bilinear(image, dst, dst.Bounds, image.Bounds, dst.Bounds);
        return dst;
    }
}

/// <summary>The system clipboard: the "PNG" format when there is one (it keeps transparency), else the bitmap. Retries while another program holds the clipboard open.</summary>
internal sealed class SystemImageClipboard : IImageClipboard
{
    public BgraImage? GetImage(out string? error)
    {
        var (image, bitmap) = Read(out error);
        return bitmap != null ? ImageInput.FromBitmap(bitmap, out error) : image;
    }

    /// <summary>
    /// UI thread. What cannot leave it: the clipboard reads (<c>GetData("PNG")</c>, <c>GetImage()</c>, which also makes the bitmap
    /// from the DIB) and, for the "PNG" format, its decoding (the stream is only valid here). The bitmap format is returned frozen
    /// and unconverted for the worker; the header size check comes first.
    /// </summary>
    public (BgraImage? Image, BitmapSource? Bitmap) Read(out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is Stream png)
                {
                    using (png)
                    {
                        var image = ImageInput.FromStream(png, out error);
                        if (image != null || error == ImageInput.TooLarge) return (image, null);
                        error = null;   // not a readable PNG: the bitmap format may still be
                    }
                }
                if (Clipboard.ContainsImage())
                {
                    // the size from the DIB header first: an over-limit bitmap is refused before WPF converts all of it
                    if (DibSize() is { } size && size.Width > 0 && size.Height > 0 && !ImageInput.WithinLimits(size.Width, size.Height)) { error = ImageInput.TooLarge; return (null, null); }
                    if (Clipboard.GetImage() is { } bitmap)
                    {
                        if (!ImageInput.WithinLimits(bitmap.PixelWidth, bitmap.PixelHeight)) { error = bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 ? ImageInput.TooLarge : ImageInput.Unreadable; return (null, null); }
                        if (bitmap.CanFreeze) bitmap.Freeze();
                        return (null, bitmap);
                    }
                }
                error = "There is no picture on the clipboard";
                return (null, null);
            }
            catch (COMException)
            {
                Thread.Sleep(60);   // CLIPBRD_E_CANT_OPEN: another program has the clipboard open
            }
        }
        error = "The clipboard is busy: try again";
        return (null, null);
    }

    private static (int Width, int Height)? DibSize()
    {
        try
        {
            if (!Clipboard.ContainsData(DataFormats.Dib) || Clipboard.GetData(DataFormats.Dib) is not Stream dib) return null;
            using (dib)
            {
                Span<byte> head = stackalloc byte[16];
                int got = dib.Read(head);
                return ImageInput.BmpInfoSize(head[..got]);
            }
        }
        catch (Exception ex) when (ex is not COMException) { return null; }
    }
}
