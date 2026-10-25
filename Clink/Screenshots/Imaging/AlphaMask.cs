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

/// <summary>
/// Shared helpers of the pixel operations: row parallelism for large images and timing log lines.
/// <para><b>The raster op contract</b> (what the editor's composer codes against): every op is
/// <c>Op(BgraImage src, BgraImage dst, RectI clip, ...params)</c>. It reads <c>src</c>, computes as if unclipped, writes only
/// <c>clip ∩ footprint</c> (clipped to the image) and never touches another pixel of <c>dst</c>; inside the footprint every pixel of
/// the clip is written (an identity pixel is copied). Running it once per dirty sub-rectangle gives the same bytes as one unclipped
/// run (BoxBlur and the unsharp mask: up to the float rounding of running sums that start elsewhere, byte-identical in the tests).
/// In place (<c>dst == src</c>) is allowed for BoxBlur, Pixelate, Highlight, SmartErase and Adjustments; Spotlight.Blend,
/// Magnifier.Render, Resample.Bilinear and Resample.Over throw on it. LOCAL ops (a pixel depends on src near it): BoxBlur
/// (<see cref="BoxBlur.ReadMargin"/>), Highlight, Adjustments (LUT per pixel, unsharp within the blur margin), Resample. NON-LOCAL
/// ops, whose whole footprint the composer invalidates rather than a dirty sub-rectangle: Pixelate (whole blocks), SmartErase (the
/// whole rectangle depends on the ring), Magnifier (dest depends on a source far from it), Spotlight (the whole image).</para>
/// </summary>
internal static class ImagingUtil
{
    /// <summary>Operations over at least this many pixels run their rows in parallel.</summary>
    public const long ParallelPixels = 1_000_000;

    /// <summary>An operation that took at least this long is written to the log (at Info), rate-limited per operation.</summary>
    public const double LogThresholdMs = 300;

    /// <summary>At most one log line per operation name in this interval; the ones in between are counted and reported with the next.</summary>
    public static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, (long Last, int Suppressed)> logState = new(StringComparer.Ordinal);

    public static void ForRows(int y0, int y1, long pixels, Action<int> body)
    {
        if (pixels >= ParallelPixels && y1 - y0 > 1) Parallel.For(y0, y1, body);
        else for (int y = y0; y < y1; y++) body(y);
    }

    /// <summary>
    /// The milliseconds since <paramref name="startTimestamp"/>, and whether the caller should format a timing line and hand it to
    /// <see cref="Report"/>. The pattern keeps the fast path free of string allocation:
    /// <code>if (ImagingUtil.ShouldReport(t0, out double ms)) ImagingUtil.Report("blur", $"{w}x{h} ...", ms);</code>
    /// Debug builds: always (every operation goes to the debugger output). Release builds, where Debug.WriteLine does not exist:
    /// only an operation of <see cref="LogThresholdMs"/> or more.
    /// </summary>
    public static bool ShouldReport(long startTimestamp, out double ms)
    {
        ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
#if DEBUG
        return true;
#else
        return ms >= LogThresholdMs;
#endif
    }

    /// <summary>
    /// The project's Log has no Debug level (Info, Warn, Error only): every line goes to the debugger output, and a slow one
    /// (<see cref="LogThresholdMs"/> or more) to the log at Info as well, at most one per <paramref name="op"/> per
    /// <see cref="LogInterval"/> (the next line that gets through says how many were suppressed).
    /// </summary>
    public static void Report(string op, string detail, double ms)
    {
        string line = $"imaging: {op} {detail} {ms:0.0} ms";
        Debug.WriteLine(line);
        if (ms < LogThresholdMs) return;
        if (!TakeLogSlot(op, Stopwatch.GetTimestamp(), out int suppressed)) return;
        Log.Info(suppressed > 0 ? $"{line} ({suppressed} more over {LogThresholdMs:0} ms suppressed)" : line);
    }

    /// <summary>
    /// The rate limiter of <see cref="Report"/>: true when <paramref name="op"/> may log at <paramref name="now"/> (a Stopwatch
    /// timestamp), with the number of lines suppressed since its last one; false (and one more suppressed) within the interval.
    /// </summary>
    internal static bool TakeLogSlot(string op, long now, out int suppressed)
    {
        lock (logState)
        {
            if (logState.TryGetValue(op, out var s) && Stopwatch.GetElapsedTime(s.Last, now) < LogInterval)
            {
                logState[op] = (s.Last, s.Suppressed + 1);
                suppressed = 0;
                return false;
            }
            suppressed = s.Suppressed;
            logState[op] = (now, 0);
            return true;
        }
    }

    public static byte ClampByte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    /// <summary>Throws unless the two images have the same size (the ops whose src and dst share one coordinate system).</summary>
    public static void RequireSameSize(BgraImage src, BgraImage dst)
    {
        if (src.Width != dst.Width || src.Height != dst.Height)
            throw new ArgumentException($"dst ({dst.Width} x {dst.Height}) must have the size of src ({src.Width} x {src.Height})", nameof(dst));
    }

    /// <summary>Throws when <paramref name="dst"/> shares its pixels with <paramref name="src"/> (the ops that cannot run in place).</summary>
    public static void RequireNoAlias(BgraImage src, BgraImage dst)
    {
        if (ReferenceEquals(src.Pixels, dst.Pixels)) throw new ArgumentException("dst must not alias src", nameof(dst));
    }
}
