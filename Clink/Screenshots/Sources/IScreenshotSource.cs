using Clink.Native;

namespace Clink.Screenshots.Sources;

/// <summary>
/// Where the pixels of one monitor come from. Chosen per monitor (<see cref="FrozenDesktop.CaptureAll"/> takes the first source
/// that <see cref="Handles"/> it): today only <see cref="GdiScreenshotSource"/>; a Windows Graphics Capture source for HDR
/// monitors can slot in later without touching the callers.
/// </summary>
internal interface IScreenshotSource
{
    string Name { get; }
    bool Handles(MonitorInfo monitor);
    /// <summary>Copies the monitor's current pixels (opaque, physical size). Any thread. Throws <see cref="ScreenshotCaptureException"/> when it cannot.</summary>
    FrozenMonitor Capture(MonitorInfo monitor);
}

/// <summary>A capture that failed (the screen could not be copied); the message says what to tell the user.</summary>
internal sealed class ScreenshotCaptureException : Exception
{
    public ScreenshotCaptureException(string message) : base(message) { }
    public ScreenshotCaptureException(string message, Exception inner) : base(message, inner) { }
}
