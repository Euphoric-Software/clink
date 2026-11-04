using Clink.Native;
using Clink.Services;

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
    public ScreenshotCaptureException(string message) : base(message) { }
    public ScreenshotCaptureException(string message, Exception inner) : base(message, inner) { }
    /// <param name="message">The English text for the log.</param>
    /// <param name="note">The sentence for the user (catalog key + arguments): translated when it is shown (<see cref="Clink.Services.Localization.UserText"/>).</param>
    public ScreenshotCaptureException(string message, Note note) : base(message) { Clink.Services.Localization.UserText.Attach(this, note); }

    /// <summary>What to tell the user: the attached note in the language of the UI, else <see cref="Exception.Message"/>.</summary>
    public string UserText => Clink.Services.Localization.UserText.Of(this);
}
