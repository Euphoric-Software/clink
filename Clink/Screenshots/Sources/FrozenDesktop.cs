using Clink.Native;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Sources;

/// <summary>One monitor's frozen pixels (physical size, opaque) and which source produced them.</summary>
internal sealed record FrozenMonitor(MonitorInfo Monitor, BgraImage Image, string SourceName)
{
    /// <summary>The monitor's rectangle in virtual-screen pixels.</summary>
    public RectI Bounds => new(Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height);

    /// <summary><see cref="BgraImage.LooksBlack"/> for a rectangle in this monitor's own pixels (0,0 = its top-left).</summary>
    public bool LooksBlack(RectI localRect) => Image.LooksBlack(localRect);
}

/// <summary>
/// Every monitor, copied at one moment and kept as pixels: the overlay shows these, and the crop is cut from them, so what
/// the PNG holds is exactly what was on screen when the key was pressed. Coordinates are physical pixels of the virtual screen
/// (negative to the left of / above the primary monitor).
/// </summary>
internal sealed class FrozenDesktop
{
    public IReadOnlyList<FrozenMonitor> Monitors { get; }
    public DateTime CapturedAt { get; }
    public RectI VirtualBounds { get; }

    public FrozenDesktop(IReadOnlyList<FrozenMonitor> monitors, DateTime capturedAt)
    {
        Monitors = monitors;
        CapturedAt = capturedAt;
        var bounds = RectI.Empty;
        foreach (var m in monitors) bounds = bounds.Union(m.Bounds);
        VirtualBounds = bounds;
    }

    /// <summary>Captures every monitor with the first source that handles it. Any thread; no window of ours may exist yet. Throws when a monitor cannot be copied or no source handles it.</summary>
    public static FrozenDesktop CaptureAll(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<IScreenshotSource> sources)
    {
        if (monitors.Count == 0) throw new ScreenshotCaptureException("No monitor was found");
        var frozen = new List<FrozenMonitor>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var source = sources.FirstOrDefault(s => s.Handles(monitor))
                ?? throw new ScreenshotCaptureException($"No screenshot source handles {monitor.ShortName}");
            frozen.Add(source.Capture(monitor));
        }
        return new FrozenDesktop(frozen, DateTime.Now);
    }

    /// <summary>The monitor that holds the pixel (x, y), or null when it is in a gap.</summary>
    public FrozenMonitor? MonitorAt(int x, int y) => Monitors.FirstOrDefault(m => m.Bounds.Contains(x, y));

    /// <summary>The pixels of a rectangle of the virtual screen, composed from the monitors it spans; gaps are transparent.</summary>
    public BgraImage Compose(RectI virtualRect) =>
        BgraImage.Compose(virtualRect, Monitors.Select(m => (m.Bounds, m.Image)));

    /// <summary>The capture of this rectangle came out black on every monitor it touches (an exclusive-fullscreen game): see <see cref="BgraImage.LooksBlack"/>.</summary>
    public bool LooksBlack(RectI virtualRect)
    {
        bool any = false;
        foreach (var m in Monitors)
        {
            var overlap = m.Bounds.Intersect(virtualRect);
            if (overlap.IsEmpty) continue;
            any = true;
            if (!m.LooksBlack(overlap.Offset(-m.Bounds.X, -m.Bounds.Y))) return false;
        }
        return any;
    }

    /// <summary>
    /// Every monitor the rectangle touches came out black as a whole (not only under the rectangle): what an exclusive-fullscreen
    /// game does to a GDI copy. A dark crop of a lit screen (a black terminal, a letterbox bar) is not this.
    /// </summary>
    public bool WholeMonitorsLookBlack(RectI virtualRect)
    {
        bool any = false;
        foreach (var m in Monitors)
        {
            if (!m.Bounds.Intersects(virtualRect)) continue;
            any = true;
            if (!m.Image.LooksBlack(m.Image.Bounds)) return false;
        }
        return any;
    }
}
