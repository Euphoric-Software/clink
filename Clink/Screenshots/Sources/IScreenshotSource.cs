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

/// <summary>
/// A capture that failed (the screen could not be copied). <see cref="Exception.Message"/> is English (it goes to the log);
/// <see cref="UserText"/> is the same sentence in the user's language, for the "Screenshot not taken" toast.
/// </summary>
internal sealed class ScreenshotCaptureException : Exception
{
    private readonly string? _userText;

    public ScreenshotCaptureException(string message) : base(message) { }
    public ScreenshotCaptureException(string message, Exception inner) : base(message, inner) { }
    /// <param name="message">The English text for the log.</param>
    /// <param name="userText">The text to show the user (built with <c>L.T</c> at the throw site).</param>
    public ScreenshotCaptureException(string message, string userText) : base(message) { _userText = userText; }

    /// <summary>What to tell the user: the translated text when the thrower gave one, else <see cref="Exception.Message"/>.</summary>
    public string UserText => _userText ?? Message;
}
