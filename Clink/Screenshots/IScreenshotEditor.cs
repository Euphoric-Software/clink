using Clink.Native;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots;

/// <summary>What the service hands the editor window to open a picture: the document, how to save it, and where to show it.</summary>
internal sealed class EditorRequest
{
    public required DocumentState Document { get; init; }
    /// <summary>The header text: the file name, or "New screenshot".</summary>
    public required string Title { get; init; }
    /// <summary>One muted line in the header (e.g. why the editor opened in a window); null = none.</summary>
    public string? Hint { get; init; }
    /// <summary>A capture that has not been saved yet (edit-after): closing it without Done discards it, and Done always saves.</summary>
    public bool IsNewCapture { get; init; }
    /// <summary>The monitor to open on (null = the primary one).</summary>
    public MonitorInfo? Monitor { get; init; }
    public required ScreenshotEditorOptions Options { get; init; }
    public Action<ScreenshotEditorOptions>? PersistOptions { get; init; }
    /// <summary>
    /// Saves the picture: (flattened image, document it came from, finish). <c>finish</c> is Done (clipboard and toast too); false is a
    /// plain Save that keeps the editor open. True when the file was written. Never throws.
    /// </summary>
    public required Func<BgraImage, DocumentState, bool, Task<bool>> SaveAsync { get; init; }
    /// <summary>
    /// The recent screenshots the insert-image picker may list (newest first, already through the path rule of the screenshots
    /// folders), and the files to leave out of it (the one being edited). Called on a worker (it reads the history); the picker checks
    /// each file on disk there too. Null = no list.
    /// </summary>
    public Func<(IReadOnlyList<ScreenshotEntry> Entries, IReadOnlyList<string> Exclude)>? Recent { get; init; }
    /// <summary>The editor window (and its picture picker) is kept out of screen captures and recordings (<see cref="Services.Settings.ScreenshotEditorExcludeFromCapture"/>, hidden, on by default).</summary>
    public bool ExcludeFromCapture { get; init; } = true;
}

/// <summary>The editor window the service opens (a made-up one in tests). UI thread.</summary>
internal interface IScreenshotEditor
{
    /// <summary>An editor window is open.</summary>
    bool IsOpen { get; }
    /// <summary>Brings the open editor to the front.</summary>
    void Activate();
    /// <summary>Opens the editor; completes when it has been closed (true), or at once with false when an editor is open already (it is brought forward; never two).</summary>
    Task<bool> EditAsync(EditorRequest request);
}

/// <summary>The last picture an editor saved, kept in memory: while the file is still the newest and unchanged, "edit last" reopens this document with its objects still editable instead of the flattened PNG.</summary>
internal sealed record LastEditInfo(DocumentState Document, string Path, DateTime FileTimeUtc);
