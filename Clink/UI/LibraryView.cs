using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Services;

namespace Clink.UI;

/// <summary>The library pane's top switch: the clip library or the screenshot history (two stores, one pane).</summary>
internal enum LibraryFeature { Clips, Screenshots }

/// <summary>The library pane's second switch: what is on this computer, or what has a Copium link.</summary>
internal enum LibraryLevel { Local, Shared }

/// <summary>
/// What the library pane shows, without the window (everything here is pure and tested): which feature / level is selected and how
/// the saved choice and the feature switches resolve to it, which items a level (and the Trimmed filter) lists, the counts in the
/// segment labels, and which actions a row offers. The pane is <see cref="ClipsPane"/> (the name is from when it listed clips only).
/// </summary>
internal static class LibraryView
{
    public const string ClipsKey = "clips", ScreenshotsKey = "screenshots", LocalKey = "local", SharedKey = "shared";

    public static string Key(LibraryFeature f) => f == LibraryFeature.Screenshots ? ScreenshotsKey : ClipsKey;
    public static string Key(LibraryLevel l) => l == LibraryLevel.Shared ? SharedKey : LocalKey;

    /// <summary>The saved feature as one of the two keys (anything else = clips).</summary>
    public static string NormalizeFeature(string? s) => string.Equals(s?.Trim(), ScreenshotsKey, StringComparison.OrdinalIgnoreCase) ? ScreenshotsKey : ClipsKey;
    /// <summary>The saved level as one of the two keys (anything else = local).</summary>
    public static string NormalizeLevel(string? s) => string.Equals(s?.Trim(), SharedKey, StringComparison.OrdinalIgnoreCase) ? SharedKey : LocalKey;

    /// <summary>
    /// The feature and level the pane opens on: the saved ones, except that a feature which is switched off is replaced by the one that
    /// is on (both off cannot happen: Settings keeps one on; the saved feature then stands).
    /// </summary>
    public static (LibraryFeature Feature, LibraryLevel Level) Resolve(string? savedFeature, string? savedLevel, bool clipsOn, bool screenshotsOn)
    {
        var feature = NormalizeFeature(savedFeature) == ScreenshotsKey ? LibraryFeature.Screenshots : LibraryFeature.Clips;
        var level = NormalizeLevel(savedLevel) == SharedKey ? LibraryLevel.Shared : LibraryLevel.Local;
        return (Fallback(feature, clipsOn, screenshotsOn), level);
    }

    /// <summary>The feature to show when <paramref name="feature"/> was selected and the switches are now as given.</summary>
    public static LibraryFeature Fallback(LibraryFeature feature, bool clipsOn, bool screenshotsOn)
    {
        if (feature == LibraryFeature.Clips && !clipsOn && screenshotsOn) return LibraryFeature.Screenshots;
        if (feature == LibraryFeature.Screenshots && !screenshotsOn && clipsOn) return LibraryFeature.Clips;
        return feature;
    }

    /// <summary>The library (tab / column) is there whenever any feature is on.</summary>
    public static bool ShowLibrary(bool clipsOn, bool screenshotsOn) => clipsOn || screenshotsOn;

    /// <summary>The top switch is only there when there is a choice; with one feature on, that one is implied.</summary>
    public static bool ShowFeatureSwitch(bool clipsOn, bool screenshotsOn) => clipsOn && screenshotsOn;

    /// <summary>The Trimmed chip belongs to the clips side only (screenshots are not trimmed).</summary>
    public static bool ShowTrimmedChip(LibraryFeature feature) => feature == LibraryFeature.Clips;

    /// <summary>The Trimmed filter in effect: only the clips side has one.</summary>
    public static bool TrimmedOnly(LibraryFeature feature, bool chipOn) => feature == LibraryFeature.Clips && chipOn;

    /// <summary>A screenshot row's Upload button: cloud sharing is on, it has no link yet and its file is still there. A row with a link shows Copy link instead (any time).</summary>
    public static bool UploadVisible(bool cloudOn, bool hasLink, bool hasFile) => cloudOn && !hasLink && hasFile;

    /// <summary>The items a level lists, in the order given: Local = <paramref name="isLocal"/>, Shared = <paramref name="isShared"/>; the Trimmed filter keeps only <paramref name="isTrim"/> ones.</summary>
    public static List<T> Pick<T>(IEnumerable<T> items, LibraryLevel level, bool trimmedOnly, Func<T, bool> isLocal, Func<T, bool> isShared, Func<T, bool> isTrim) =>
        items.Where(i => (level == LibraryLevel.Local ? isLocal(i) : isShared(i)) && (!trimmedOnly || isTrim(i))).ToList();

    /// <summary>How many items each level lists with the same filter (the numbers in the segment labels).</summary>
    public static (int Local, int Shared) Counts<T>(IEnumerable<T> items, bool trimmedOnly, Func<T, bool> isLocal, Func<T, bool> isShared, Func<T, bool> isTrim)
    {
        var all = items as IReadOnlyCollection<T> ?? items.ToList();
        return (Pick(all, LibraryLevel.Local, trimmedOnly, isLocal, isShared, isTrim).Count, Pick(all, LibraryLevel.Shared, trimmedOnly, isLocal, isShared, isTrim).Count);
    }

    /// <summary>"Local 12", or just "Local" when there is nothing in it.</summary>
    public static string Label(string name, int count) => count > 0 ? $"{name} {count}" : name;

    /// <summary>The text of the empty list.</summary>
    public static string EmptyText(LibraryFeature feature, LibraryLevel level, bool trimmedOnly) => (feature, level) switch
    {
        (LibraryFeature.Screenshots, LibraryLevel.Local) => "No screenshots yet — press PrintScreen",
        (LibraryFeature.Screenshots, _) => "No shared screenshots",
        (_, LibraryLevel.Local) when trimmedOnly => "No trimmed clips yet. Trim saves a cut copy next to the original.",
        (_, LibraryLevel.Local) => "No clips yet. Press the hotkey while gaming.",
        _ when trimmedOnly => "No trimmed clips are shared.",
        _ => "Nothing shared to Copium yet. Clips shared to Copium are listed here, even after the local file is gone.",
    };
}

/// <summary>One screenshot of the history with what a worker found out about its file.</summary>
internal sealed record ScreenshotItem(ScreenshotEntry Entry, bool Exists, int Width, int Height);

/// <summary>
/// What the screenshots side lists, without the window. <b>The constructor touches nothing</b>: reading the history, checking the
/// files, reading a picture's size from its header and decoding thumbnails all run on workers (<see cref="LoadAsync"/>,
/// <see cref="ThumbnailAsync"/>), never on the UI thread. Thumbnails are <see cref="InsertPickerModel.DecodeThumbnail"/>'s (176 px
/// wide, IgnoreColorProfile, OnLoad, frozen), cached per file by the pane.
/// </summary>
internal sealed class ScreenshotListModel
{
    private readonly Func<IReadOnlyList<ScreenshotEntry>> _source;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, (int Width, int Height)?> _size;
    private readonly Func<string, BitmapSource?> _decode;
    private readonly Func<IReadOnlyList<string>>? _folders;

    /// <param name="folders">The screenshots folders the app knows (<see cref="ScreenshotService.KnownFolders"/>). When given, an entry whose path fails the path rule
    /// (<see cref="ScreenshotService.IsOpenable(string?, IEnumerable{string}?)"/>: a .png inside one of them; a network path only for a network folder) is not listed
    /// and its file is not touched at all: a hand-edited history cannot make the library reach a server or read an arbitrary file. Null = no filter (tests).</param>
    public ScreenshotListModel(Func<IReadOnlyList<ScreenshotEntry>> source, Func<string, bool>? exists = null,
        Func<string, (int Width, int Height)?>? size = null, Func<string, BitmapSource?>? decode = null, Func<IReadOnlyList<string>>? folders = null)
    {
        _folders = folders;
        _source = source;
        _exists = exists ?? (p => { try { return File.Exists(p); } catch { return false; } });
        _size = size ?? PngFile.ReadSize;
        _decode = decode ?? InsertPickerModel.DecodeThumbnail;
    }

    /// <summary>Every history entry (newest first) with whether its file is there and the picture's size (the history's, else the PNG header's).</summary>
    public Task<List<ScreenshotItem>> LoadAsync() => Task.Run(() =>
    {
        var list = new List<ScreenshotItem>();
        var folders = _folders?.Invoke();
        foreach (var e in _source())
        {
            if (e == null || string.IsNullOrEmpty(e.Path)) continue;
            if (folders != null && !ScreenshotService.IsOpenable(e.Path, folders)) continue;   // before ANY disk probe
            bool exists = _exists(e.Path);
            int w = e.Width, h = e.Height;
            if (exists && (w <= 0 || h <= 0))
            {
                try { if (_size(e.Path) is { } s) { w = s.Width; h = s.Height; } } catch { }
            }
            list.Add(new ScreenshotItem(e, exists, Math.Max(0, w), Math.Max(0, h)));
        }
        return list;
    });

    /// <summary>The thumbnail of one file (frozen), decoded on a worker; null when it cannot be read.</summary>
    public Task<BitmapSource?> ThumbnailAsync(string path) => Task.Run(() => _decode(path));

    /// <summary>The Local level lists the screenshots whose file is there, in the order of the history (newest first).</summary>
    public static bool IsLocal(ScreenshotItem i) => i.Exists;
    /// <summary>The Shared level lists the screenshots that have a Copium link, even when the file is gone.</summary>
    public static bool IsShared(ScreenshotItem i) => !string.IsNullOrEmpty(i.Entry.CopiumLink);

    public static List<ScreenshotItem> Pick(IEnumerable<ScreenshotItem> items, LibraryLevel level) =>
        LibraryView.Pick(items, level, false, IsLocal, IsShared, _ => false);
}

/// <summary>One screenshot as a row of the library list.</summary>
internal sealed class ScreenshotRow : INotifyPropertyChanged
{
    public ScreenshotEntry Entry { get; private set; }
    private bool _exists;
    private int _w, _h;
    private BitmapSource? _thumb;
    private bool _uploading, _deleting, _problem, _thumbRequested;
    private int _thumbVersion;
    private string? _status;

    public ScreenshotRow(ScreenshotItem item)
    {
        Entry = item.Entry; _exists = item.Exists; _w = item.Width; _h = item.Height;
    }

    /// <summary>The same row for a fresh look at the history (keeps its thumbnail and status).</summary>
    public void Update(ScreenshotItem item)
    {
        // Another file under the same name (an editor session saved over it), or back from the Recycle Bin: make the thumbnail again.
        // The old picture must not stay up meanwhile.
        // A decode still running for the old file must not land on this one (the version), and the row asks again (not requested).
        if (item.Entry.Size != Entry.Size || item.Exists != _exists) { _thumbVersion++; _thumbRequested = false; Thumbnail = null; }
        Entry = item.Entry; _exists = item.Exists; _w = item.Width; _h = item.Height;
        Refresh();
    }

    public void SetProblem(string text) { IsProblem = true; Status = text; }

    public const string NotLocalText = "Delete is only available for screenshots on a local fixed drive";
    public const string RemoveHint = "Remove from this list. The link stays on Copium";

    /// <summary>The question before a delete: names the file; a shared one says its link stays.</summary>
    public static string ConfirmText(ScreenshotEntry e, bool hasFile)
    {
        string name = System.IO.Path.GetFileName(e.Path);
        string kept = string.IsNullOrEmpty(e.CopiumLink) ? "" : " The link stays on Copium.";
        return hasFile ? $"Move '{name}' to the Recycle Bin?{kept} If it cannot go to the Recycle Bin, Windows asks again before anything is deleted for good." :$"Remove '{name}' from the list? Its file is already gone.{kept}";
    }

    /// <summary>The row's problem line for a delete that did not happen (<paramref name="onLocalFixedDrive"/>: the file's drive, to tell why a refusal was one).</summary>
    public static string DeleteProblemText(ScreenshotDeleteResult result, bool onLocalFixedDrive) => result switch
    {
        ScreenshotDeleteResult.Refused when !onLocalFixedDrive => NotLocalText,
        ScreenshotDeleteResult.Refused => "Not deleted: not a file in the screenshots folder",
        ScreenshotDeleteResult.EditorOpen => "Not deleted: close the editor first",
        ScreenshotDeleteResult.ListNotUpdated => "The file was removed from disk but the list could not be updated",
        _ => "Not deleted: the file could not be moved to the Recycle Bin (or the Windows confirmation was declined)",
    };

    public static string TitleFor(ScreenshotEntry e) => string.IsNullOrWhiteSpace(e.Game) ? "Screenshot" : e.Game!;

    /// <summary>"1920×1080 · 412 KB · Oct 25, 14:03" (the size of the picture is left out when it is not known).</summary>
    public static string MetaFor(ScreenshotEntry e, int width, int height)
    {
        string dims = width > 0 && height > 0 ? $"{width}×{height} · " : "";
        return $"{dims}{Capabilities.FormatBytes(e.Size)} · {e.Created:MMM d, HH:mm}";
    }

    public string Title => TitleFor(Entry);
    public string Meta => MetaFor(Entry, _w, _h) + (_exists ? "" : " · file deleted");
    public bool HasLocalFile => _exists;
    public bool HasLink => !string.IsNullOrEmpty(Entry.CopiumLink);
    public string? Link => Entry.CopiumLink;
    /// <summary>Open page is offered for a shared screenshot with an https page (checked again at the click).</summary>
    public bool HasPage => HasLink && CopiumClient.IsHttpsUrl(Entry.CopiumPageUrl ?? Entry.CopiumLink, out _);
    public bool UploadVisible => LibraryView.UploadVisible(AppController.Instance?.Settings.CloudSharingEnabled != false, HasLink, _exists) && !_uploading && !_deleting;
    /// <summary>Delete (to the Recycle Bin) is offered while the file is there and nothing else is working on it.</summary>
    public bool DeleteVisible => _exists && !_uploading && !_deleting;
    /// <summary>"Remove from list" takes the place of Delete when the file is gone (a shared screenshot: the link stays on Copium).</summary>
    public bool RemoveVisible => !_exists && !_deleting;
    public bool IsEdited => !string.IsNullOrEmpty(Entry.EditedFrom);

    public BitmapSource? Thumbnail { get => _thumb; set { _thumb = value; Raise(); } }
    /// <summary>A thumbnail was asked for already (once per row; a row whose file changed is asked again).</summary>
    public bool ThumbnailRequested { get => _thumbRequested; set => _thumbRequested = value; }
    /// <summary>Bumped by an <see cref="Update"/> that changes the file; a decode started at one version is only shown if the row is still at it (UI thread).</summary>
    public int ThumbnailVersion => _thumbVersion;
    /// <summary>Shows a decoded picture if the row was not updated since <paramref name="version"/>; false when it was dropped as stale.</summary>
    public bool TrySetThumbnail(BitmapSource? bmp, int version)
    {
        if (bmp == null || version != _thumbVersion) return false;
        Thumbnail = bmp;
        return true;
    }
    public bool IsUploading { get => _uploading; set { _uploading = value; Raise(); Raise(nameof(UploadVisible)); Raise(nameof(DeleteVisible)); } }
    /// <summary>A delete is under way (the buttons that would race it are hidden).</summary>
    public bool IsDeleting { get => _deleting; set { _deleting = value; Raise(); Raise(nameof(UploadVisible)); Raise(nameof(DeleteVisible)); Raise(nameof(RemoveVisible)); } }
    public string? Status { get => _status; set { _status = value; Raise(); } }
    public bool IsProblem { get => _problem; set { _problem = value; Raise(); } }

    public void Refresh()
    {
        foreach (var p in new[] { nameof(Title), nameof(Meta), nameof(HasLocalFile), nameof(HasLink), nameof(Link), nameof(HasPage), nameof(UploadVisible), nameof(DeleteVisible), nameof(RemoveVisible), nameof(IsEdited) })
            Raise(p);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
