using System.Diagnostics;

namespace Clink.Screenshots.Imaging;

/// <summary>
/// An 8-bit coverage mask over <see cref="Bounds"/> (image coordinates): <c>Coverage[(y - Bounds.Y) * Bounds.Width + (x - Bounds.X)]</c>,
/// 0 = not covered, 255 = fully covered. Outside <see cref="Bounds"/> the coverage is 0.
/// </summary>
internal sealed record AlphaMask(RectI Bounds, byte[] Coverage)
{
    public static readonly AlphaMask None = new(RectI.Empty, Array.Empty<byte>());

    /// <summary>The coverage at image pixel (x, y); 0 outside <see cref="Bounds"/>.</summary>
    public byte Get(int x, int y) =>
        Bounds.Contains(x, y) ? Coverage[(y - Bounds.Y) * Bounds.Width + (x - Bounds.X)] : (byte)0;

    /// <summary>A mask of <paramref name="bounds"/> with every pixel at <paramref name="value"/>.</summary>
    public static AlphaMask Filled(RectI bounds, byte value = 255)
    {
        if (bounds.IsEmpty) return None;
        var data = new byte[bounds.Width * bounds.Height];
        if (value != 0) Array.Fill(data, value);
        return new AlphaMask(bounds, data);
    }
}

/// <summary>Shared helpers of the pixel operations: row parallelism for large images and timing log lines.</summary>
internal static class ImagingUtil
{
    /// <summary>Operations over at least this many pixels run their rows in parallel.</summary>
    public const long ParallelPixels = 1_000_000;

    public static void ForRows(int y0, int y1, long pixels, Action<int> body)
    {
        if (pixels >= ParallelPixels && y1 - y0 > 1) Parallel.For(y0, y1, body);
        else for (int y = y0; y < y1; y++) body(y);
    }

    /// <summary>
    /// Logs how long an operation took. The project's Log has no Debug level (Info, Warn, Error only), so a fast operation
    /// (under 20 ms) goes to the debugger output only and a slow one to the log at Info.
    /// </summary>
    public static void LogTime(string op, string detail, long startTimestamp)
    {
        double ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        string line = $"imaging: {op} {detail} {ms:0.0} ms";
        if (ms >= 20) Log.Info(line);
        else Debug.WriteLine(line);
    }

    public static byte ClampByte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
}
