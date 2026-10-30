namespace Clink.Services;

/// <summary>
/// The first-link welcome window, without WPF: its texts and the rule that its answer reaches the controller exactly once, whichever way the
/// window ends (Start sharing, Not now, Esc, the close button, Alt+F4). Texts that carry the account name are plain text (the window sets
/// Text, never markup) with the name in Unicode isolates and cut to <see cref="MaxNameChars"/>.
/// </summary>
internal sealed class CopiumWelcomeModel
{
    internal const string Title = "You're connected to Copium";
    internal const string AutoUploadLabel = "Automatically upload every screenshot I take";
    internal const string AutoUploadDetail = "Each screenshot is uploaded to your Copium account and gets a link you can share. Anyone with the link can see it.";
    internal const string ClipsLine = "Clips are only uploaded when you share them.";
    internal const string ChangeLaterLine = "You can change this any time in Clink's settings or from the version number at the bottom of the window.";
    internal const string StartLabel = "Start sharing";
    internal const string NotNowLabel = "Not now";
    internal const string CloseLabel = "Close";
    internal const int MaxNameChars = 40;

    /// <summary>"Signed in as {Username}" (the name isolated and cut); "Signed in" when there is no name.</summary>
    internal static string SignedInLine(string? username)
    {
        string name = (username ?? "").Trim();
        if (name.Length == 0) return "Signed in";
        if (name.Length > MaxNameChars) name = name[..MaxNameChars].TrimEnd() + "…";
        return "Signed in as " + CopiumCardModel.Iso(name);
    }

    private readonly Action<bool, bool> _apply;
    private int _answered;   // 0 = open, 1 = answered (Interlocked: the window can end on any path, twice)

    /// <param name="initialAutoUpload">The headline box when the window opens (<c>AppController.WelcomeInitialAutoUpload</c>).</param>
    /// <param name="apply">The controller's <c>ApplyWelcome(startSharing, autoUpload)</c>.</param>
    internal CopiumWelcomeModel(bool initialAutoUpload, Action<bool, bool> apply)
    {
        InitialAutoUpload = initialAutoUpload;
        _apply = apply;
    }

    internal bool InitialAutoUpload { get; }

    /// <summary>The answer was given (or the window ended without one that had to be applied).</summary>
    internal bool Answered => Volatile.Read(ref _answered) != 0;

    /// <summary>Start sharing: consent, and the automatic upload as the box says. False when the window had been answered already.</summary>
    internal bool StartSharing(bool autoUpload) => Answer(startSharing: true, autoUpload);

    /// <summary>Not now, Esc, the close button, Alt+F4: no consent, the box is ignored. False when the window had been answered already.</summary>
    internal bool NotNow() => Answer(startSharing: false, autoUpload: false);

    /// <summary>
    /// The window ended without a click (<paramref name="appShuttingDown"/>: the app is exiting and WPF closed it). While the app runs that is Not now.
    /// At shutdown nobody answered, so nothing is applied (no consent, no nudge counted; the welcome stays unshown) and the answer is spent.
    /// </summary>
    internal bool Ended(bool appShuttingDown)
    {
        if (!appShuttingDown) return NotNow();
        return Interlocked.Exchange(ref _answered, 1) == 0;
    }

    private bool Answer(bool startSharing, bool autoUpload)
    {
        if (Interlocked.Exchange(ref _answered, 1) != 0) return false;
        _apply(startSharing, startSharing && autoUpload);
        return true;
    }
}
