using System.IO;
using System.Windows.Media.Imaging;

namespace Clink.Screenshots;

/// <summary>PNG encoding and the crash-safe write of a screenshot file (WPF's PngBitmapEncoder; no System.Drawing).</summary>
internal static class PngFile
{
    /// <summary>The PNG bytes of a bitmap. Safe on a worker thread when the bitmap is frozen (<see cref="BgraImage.ToBitmapSource"/>'s are).</summary>
    public static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the bytes to <c>&lt;path&gt;.&lt;guid&gt;.partial</c> (a name of its own, so two writers aiming at the same name never
    /// share a temporary file) and then moves that to <paramref name="path"/>: a file under its real name is always complete.
    /// <paramref name="path"/> must not exist (the name is picked by <see cref="ScreenshotNaming.UniquePath"/>): when it does by now,
    /// the move throws an IOException and nothing is overwritten. A failed write leaves no ".partial" behind.
    /// </summary>
    public static void WriteAtomic(string path, byte[] png)
    {
        string partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(png, 0, png.Length);
                fs.Flush(true);
            }
            File.Move(partial, path, overwrite: false);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    /// <summary>The largest picture <see cref="Decode"/> reads: 16384 px on a side and 100 megapixels (an untrusted file must not make the app allocate gigabytes).</summary>
    public const int MaxSide = 16384;
    public const long MaxPixels = 100_000_000;

    /// <summary>
    /// Reads a PNG file into a <see cref="BgraImage"/> (straight alpha). Null when the file is not a PNG, cannot be decoded, or is
    /// larger than <see cref="MaxSide"/> / <see cref="MaxPixels"/>; the size is checked from the header before any pixel is decoded.
    /// Safe on a worker thread.
    /// </summary>
    public static Clink.Screenshots.Imaging.BgraImage? Decode(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Decode(fs);
    }

    /// <summary>
    /// <see cref="Decode(string)"/> on one open stream, so the header that was checked and the pixels that are decoded are the same
    /// bytes: the header is read, the stream rewound, and WIC decodes lazily from it (<see cref="BitmapCacheOption.None"/>); the
    /// frame's size is compared with the header's before a single pixel is copied. A file swapped between two opens cannot get a
    /// large picture past the limits this way. The stream has to stay open until this returns (the pixels are read from it).
    /// </summary>
    internal static Clink.Screenshots.Imaging.BgraImage? Decode(Stream fs)
    {
        long start = fs.Position;
        var size = ReadSize(fs);
        if (size is not { } s || s.Width > MaxSide || s.Height > MaxSide || (long)s.Width * s.Height > MaxPixels) return null;
        fs.Position = start;
        var decoder = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != s.Width || frame.PixelHeight != s.Height) return null;
        return Clink.Screenshots.Imaging.BgraImage.From(frame);
    }

    /// <summary>The pixel size of a PNG file from its header, or null when it cannot be read. Does not decode the pixels.</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadSize(fs);
        }
        catch { return null; }
    }

    /// <summary>The pixel size from the PNG header at the stream's current position (24 bytes are read), or null.</summary>
    internal static (int Width, int Height)? ReadSize(Stream fs)
    {
        try
        {
            Span<byte> head = stackalloc byte[24];
            int got = 0;
            while (got < 24)
            {
                int n = fs.Read(head[got..]);
                if (n <= 0) break;
                got += n;
            }
            if (got < 24) return null;
            // 8-byte signature, then the IHDR chunk: length(4) "IHDR"(4) width(4) height(4), big-endian.
            ReadOnlySpan<byte> signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!head[..8].SequenceEqual(signature) || head[12] != (byte)'I' || head[13] != (byte)'H' || head[14] != (byte)'D' || head[15] != (byte)'R') return null;
            int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.Slice(16, 4));
            int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.Slice(20, 4));
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch { return null; }
    }
}
