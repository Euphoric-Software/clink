using System.IO;
using System.Windows.Media.Imaging;
using Clink.Services;

namespace Clink.Screenshots.Editing;

/// <summary>
/// What the insert-image picker shows, without the window: the recent screenshots (history entries through the service's path rule,
/// newest first, the file being edited left out, at most <see cref="MaxTiles"/>, each file checked on disk) and their thumbnails.
/// <b>The constructor touches nothing</b>: the history read, the file checks (<see cref="ScreenshotService.IsOpenableFile"/>) and the
/// thumbnail decoding all run on workers (<see cref="LoadAsync"/>, <see cref="ThumbnailAsync"/>), never on the UI thread.
/// </summary>
internal sealed class InsertPickerModel
{
    /// <summary>How many recent screenshots are listed.</summary>
    public const int MaxTiles = 24;
    /// <summary>Thumbnails are decoded this many pixels wide (a 160-DIP tile, a little more for a high-DPI screen).</summary>
    public const int ThumbnailDecodeWidth = 176;

    private readonly Func<(IReadOnlyList<ScreenshotEntry> Entries, IReadOnlyList<string> Exclude)>? _source;
    private readonly Func<string, bool> _fileOk;
    private readonly Func<string, BitmapSource?> _decode;

    /// <param name="source">The entries and the paths to leave out (<see cref="EditorRequest.Recent"/>); called on a worker. Null = no list.</param>
    /// <param name="fileOk">The disk check of one file (default: <see cref="ScreenshotService.IsOpenableFile"/> anywhere: exists, a regular file, no link); called on a worker.</param>
    /// <param name="decode">The thumbnail of one file (default: <see cref="DecodeThumbnail"/>); called on a worker.</param>
    public InsertPickerModel(Func<(IReadOnlyList<ScreenshotEntry> Entries, IReadOnlyList<string> Exclude)>? source,
        Func<string, bool>? fileOk = null, Func<string, BitmapSource?>? decode = null)
    {
        _source = source;
        _fileOk = fileOk ?? (p => ScreenshotService.IsOpenableFile(p, null));
        _decode = decode ?? DecodeThumbnail;
    }

    /// <summary>The tiles to show, worked out on a worker.</summary>
    public Task<List<ScreenshotEntry>> LoadAsync() => Task.Run(() =>
    {
        if (_source == null) return new List<ScreenshotEntry>();
        var (entries, exclude) = _source();
        return Candidates(entries, exclude, _fileOk);
    });

    /// <summary>The thumbnail of <paramref name="path"/> (frozen), decoded on a worker; null when it cannot be read.</summary>
    public Task<BitmapSource?> ThumbnailAsync(string path) => Task.Run(() => _decode(path));

    /// <summary>The recent screenshots to list: newest first, <paramref name="exclude"/>d ones and files that fail <paramref name="exists"/> left out, at most <see cref="MaxTiles"/> (so at most that many files are probed, plus the ones skipped). Pure apart from <paramref name="exists"/>.</summary>
    public static List<ScreenshotEntry> Candidates(IEnumerable<ScreenshotEntry> entries, IEnumerable<string> exclude, Func<string, bool>? exists = null)
    {
        exists ??= p => ScreenshotService.IsOpenableFile(p, null);
        var skip = SkipSet(exclude);
        var list = new List<ScreenshotEntry>();
        foreach (var e in entries)
        {
            if (list.Count >= MaxTiles) break;
            if (string.IsNullOrEmpty(e.Path) || skip.Contains(Normalize(e.Path)) || !exists(e.Path)) continue;
            list.Add(e);
        }
        return list;
    }

    /// <summary>The entries without the <paramref name="exclude"/>d paths (compared case-insensitively, normalised). Pure.</summary>
    public static List<ScreenshotEntry> WithoutExcluded(IEnumerable<ScreenshotEntry> entries, IEnumerable<string> exclude)
    {
        var skip = SkipSet(exclude);
        return entries.Where(e => !string.IsNullOrEmpty(e.Path) && !skip.Contains(Normalize(e.Path))).ToList();
    }

    private static HashSet<string> SkipSet(IEnumerable<string> exclude) =>
        new(exclude.Where(p => !string.IsNullOrEmpty(p)).Select(Normalize), StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>
    /// The picture decoded at <see cref="ThumbnailDecodeWidth"/> px wide (BitmapImage: DecodePixelWidth, OnLoad, IgnoreColorProfile;
    /// frozen, so the UI thread may use it); null when it cannot be read or its header says it is over the size limits. Worker thread.
    /// </summary>
    internal static BitmapSource? DecodeThumbnail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (ImageInput.PeekSize(fs) is { } size && !ImageInput.WithinLimits(size.Width, size.Height)) return null;
            fs.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = fs;
            bmp.DecodePixelWidth = ThumbnailDecodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn("screenshot editor: thumbnail: " + ex.Message);
            return null;
        }
    }
}
