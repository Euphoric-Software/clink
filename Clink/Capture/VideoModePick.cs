namespace Clink.Capture;

/// <summary>The pixel layouts a capture card offers that Clink can use (the others are left out of the pick).</summary>
internal enum VideoFormat { Other, Nv12, Yuy2, P010, Rgb32, Mjpg }

/// <summary>One video mode a capture card offers: size, frame rate (59.94 stays 59.94), pixel layout, and whether the card says it is HDR (PQ).</summary>
internal readonly record struct VideoMode(uint Width, uint Height, double Fps, VideoFormat Format, bool Hdr = false)
{
    public override string ToString() => $"{Width}x{Height} @ {Fps:0.##} fps {Format.ToString().ToUpperInvariant()}{(Hdr ? " HDR" : "")}";
}

/// <summary>Which mode of a capture card is used. Pure: unit-tested without a card.</summary>
internal static class VideoModePick
{
    /// <summary>The pixel layout named by a Media Foundation subtype GUID ({FOURCC-0000-0010-8000-00AA00389B71}; RGB32 is the D3D format number 22).</summary>
    public static VideoFormat FormatOf(Guid subtype)
    {
        if (!subtype.ToString("D").EndsWith("-0000-0010-8000-00aa00389b71", StringComparison.Ordinal)) return VideoFormat.Other;
        uint d1 = BitConverter.ToUInt32(subtype.ToByteArray(), 0);
        return d1 switch
        {
            22 => VideoFormat.Rgb32,          // D3DFMT_X8R8G8B8 (MFVideoFormat_RGB32)
            0x3231564E => VideoFormat.Nv12,   // 'NV12'
            0x32595559 => VideoFormat.Yuy2,   // 'YUY2'
            0x30313050 => VideoFormat.P010,   // 'P010'
            0x47504A4D => VideoFormat.Mjpg,   // 'MJPG'
            _ => VideoFormat.Other,
        };
    }

    /// <summary>The largest size the Resolution preset (native | 1440p | 1080p | 720p) allows a card mode to have; unknown text = native (no limit).</summary>
    public static (uint Width, uint Height) LimitOf(string? resolutionPreset) => resolutionPreset?.ToLowerInvariant() switch
    {
        "1440p" => (2560, 1440),
        "1080p" => (1920, 1080),
        "720p" => (1280, 720),
        _ => (uint.MaxValue, uint.MaxValue),
    };

    private static int Rank(VideoFormat f) => f switch
    {
        VideoFormat.Nv12 => 0,
        VideoFormat.Yuy2 => 1,
        VideoFormat.P010 => 2,
        VideoFormat.Rgb32 => 3,
        VideoFormat.Mjpg => 4,
        _ => 9,
    };

    /// <summary>
    /// The mode to capture: first the frame rate (the modes that reach <paramref name="wantedFps"/>, 59.94 counting as 60, or the card's best
    /// rate when that is lower); among them the largest size inside <paramref name="maxWidth"/> x <paramref name="maxHeight"/> (the smallest
    /// when none fits); at that size the lowest rate at or above the wanted one, else the highest below; then the cheapest layout (NV12, YUY2, P010, RGB32, MJPG); then not HDR; then the higher rate. Null when the card offers no usable mode.
    /// </summary>
    public static VideoMode? Pick(IReadOnlyList<VideoMode> modes, uint maxWidth, uint maxHeight, double wantedFps)
    {
        var usable = modes.Where(m => m.Width > 0 && m.Height > 0 && m.Fps > 0 && m.Format != VideoFormat.Other).ToList();
        if (usable.Count == 0) return null;
        long Area(VideoMode m) => (long)m.Width * m.Height;
        // Frame rate first: only the modes that reach the wanted rate, or the best rate the card has when that is lower.
        double target = Math.Min(wantedFps, usable.Max(m => m.Fps));
        var reaching = usable.Where(m => m.Fps >= target - 0.5).ToList();
        // Then the largest size inside the preset (the smallest of those when none fits).
        var fitting = reaching.Where(m => m.Width <= maxWidth && m.Height <= maxHeight).ToList();
        long size;
        if (fitting.Count > 0) size = fitting.Max(Area);
        else { fitting = reaching; size = reaching.Min(Area); }
        // Then the lowest rate at or above the wanted one (59.94 counts as 60), else the highest below it; then layout.
        bool Above(VideoMode m) => m.Fps >= wantedFps - 0.5;
        double Distance(VideoMode m) { double d = Math.Abs(m.Fps - wantedFps); return d < 0.5 ? 0 : d; }
        return fitting.Where(m => Area(m) == size)
            .OrderBy(m => Above(m) ? 0 : 1).ThenBy(Distance).ThenBy(m => Rank(m.Format)).ThenBy(m => m.Hdr).ThenByDescending(m => m.Fps)
            .First();
    }
}
