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
    /// Whether a card running at <paramref name="fps"/> gives every frame of a <paramref name="wantedFps"/> clip without a hitch: the rate is
    /// the wanted one or close to a whole multiple of it (59.94 and 119.88 reach 60; 90 does not, nor 240 for 144: the frames would land
    /// unevenly on the clip's grid).
    /// </summary>
    public static bool Reaches(double fps, double wantedFps)
    {
        if (wantedFps <= 0 || fps < wantedFps - 0.5) return false;
        double k = Math.Round(fps / wantedFps);
        return k >= 1 && Math.Abs(fps / k - wantedFps) < 0.5;
    }

    /// <summary>
    /// The mode to capture: first the frame rate (the modes that reach <paramref name="wantedFps"/>, see <see cref="Reaches"/>; when none
    /// does, the best rate below it; when there is none below either, every mode); among them the largest size inside
    /// <paramref name="maxWidth"/> x <paramref name="maxHeight"/> (the smallest when none fits); at that size the rate closest to the wanted one
    /// (a reaching one first); then the cheapest layout (NV12, YUY2, P010, RGB32, MJPG); then not HDR; then the higher rate. Null when the card
    /// offers no usable mode.
    /// </summary>
    public static VideoMode? Pick(IReadOnlyList<VideoMode> modes, uint maxWidth, uint maxHeight, double wantedFps)
    {
        var usable = modes.Where(m => m.Width > 0 && m.Height > 0 && m.Fps > 0 && m.Format != VideoFormat.Other).ToList();
        if (usable.Count == 0) return null;
        long Area(VideoMode m) => (long)m.Width * m.Height;
        // Frame rate first: the modes that reach the wanted rate; else the best rate below it; else (only odd rates above it) all of them.
        var candidates = usable.Where(m => Reaches(m.Fps, wantedFps)).ToList();
        if (candidates.Count == 0)
        {
            var below = usable.Where(m => m.Fps < wantedFps - 0.5).ToList();
            if (below.Count > 0) { double best = below.Max(m => m.Fps); candidates = below.Where(m => m.Fps >= best - 0.5).ToList(); }
            else candidates = usable;
        }
        // Then the largest size inside the preset (the smallest of those when none fits).
        var fitting = candidates.Where(m => m.Width <= maxWidth && m.Height <= maxHeight).ToList();
        long size;
        if (fitting.Count > 0) size = fitting.Max(Area);
        else { fitting = candidates; size = candidates.Min(Area); }
        // Then a reaching rate, the one closest to the wanted rate (59.94 counts as 60: the lowest multiple, or the highest below); then layout.
        double Distance(VideoMode m) { double d = Math.Abs(m.Fps - wantedFps); return d < 0.5 ? 0 : d; }
        return fitting.Where(m => Area(m) == size)
            .OrderBy(m => Reaches(m.Fps, wantedFps) ? 0 : 1).ThenBy(Distance).ThenBy(m => Rank(m.Format)).ThenBy(m => m.Hdr).ThenByDescending(m => m.Fps)
            .First();
    }

    /// <summary>
    /// The size a card's frames are recorded at: the mode's size, scaled down (keeping its shape) to fit inside <paramref name="maxWidth"/> x
    /// <paramref name="maxHeight"/> when it is larger (a card with no mode inside the preset), rounded to even numbers as the encoder needs.
    /// </summary>
    public static (uint Width, uint Height) OutputSize(uint width, uint height, uint maxWidth, uint maxHeight)
    {
        double w = Math.Max(2, width), h = Math.Max(2, height);
        double k = Math.Min(1, Math.Min(maxWidth / w, maxHeight / h));
        return ((uint)Math.Max(2, Math.Round(w * k / 2) * 2), (uint)Math.Max(2, Math.Round(h * k / 2) * 2));
    }

    /// <summary>The encode rate for a card: the wanted rate, or the card's own rate (rounded, so 29.97 is 30) when that is lower; never below 15.</summary>
    public static uint EncodeFps(double cardFps, uint wantedFps) => (uint)Math.Clamp(Math.Min(Math.Round(cardFps), wantedFps), 15, Math.Max(15, wantedFps));

    /// <summary>
    /// Whether a card frame stamped <paramref name="frameTime"/> goes into a clip with frame period <paramref name="period"/>, the card's own
    /// period being <paramref name="cardPeriod"/> (100 ns units). The frames taken follow a grid of due times one clip period apart
    /// (<paramref name="nextDue"/>, <see cref="long.MinValue"/> before the first frame): a frame is taken when it is no more than half a card
    /// period before the time due, and the grid moves on by one period. So a card at k times the clip's rate gives every k-th frame, and a
    /// frame that arrives a little early or late still counts. A frame a whole period or more behind the grid (the first one, a gap without
    /// signal) starts the grid again at itself, rather than taking every frame until the grid catches up.
    /// </summary>
    public static bool TakeFrame(long frameTime, long period, long cardPeriod, ref long nextDue)
    {
        if (nextDue != long.MinValue && frameTime < nextDue - cardPeriod / 2) return false;
        nextDue = nextDue == long.MinValue || frameTime - nextDue >= period ? frameTime + period : nextDue + period;
        return true;
    }
}
