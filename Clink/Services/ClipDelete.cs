using System.IO;
using Clink.Screenshots;
using Clink.UI;

namespace Clink.Services;

/// <summary>How a clip delete ended.</summary>
internal enum ClipDeleteResult
{
    /// <summary>The file went to the Recycle Bin (or was already gone) and the library was updated.</summary>
    Deleted,
    /// <summary>Not touched: not a .mp4 file inside the clips or archive folder.</summary>
    Refused,
    /// <summary>Not touched: the drive is not a local fixed drive, or is not there (the Recycle Bin would delete the file for good, or cannot be reached).</summary>
    NotLocal,
    /// <summary>Not touched: the path is a link or mount point (not a plain file, and not a cloud-sync placeholder).</summary>
    Link,
    /// <summary>Not touched: the clip is being uploaded to Copium.</summary>
    Uploading,
    /// <summary>Not touched: the file or its folder could not be reached, so it is unknown whether the file is gone; the entry stays.</summary>
    Unreachable,
    /// <summary>Not touched: the trim window of this clip is open.</summary>
    TrimOpen,
    /// <summary>The Recycle Bin did not take the file; the file and the entry are as they were.</summary>
    Failed,
}

/// <summary>
/// Deletes a clip from the library: the decision logic of the Clips page's Delete button, with no window code (the pane asks, this
/// decides). The file only ever goes to the Recycle Bin (<see cref="RecycleBin"/>), never a permanent delete, and only when it is
/// the entry's own .mp4 inside the clips folder (any subfolder) or the archive folder. A shared clip keeps its entry (the link is
/// what is left of it); any other leaves the library. Trims cut from the clip are not touched: they become top-level rows by
/// themselves (<see cref="ClipGrouping"/> finds no source for them).
/// </summary>
internal sealed class ClipDeleter
{
    private readonly ClipLibrary _library;
    private readonly Func<string> _clipsFolder;
    private readonly Func<string?> _archiveFolder;

    /// <summary>Test seam: sends one file (full path) to the Recycle Bin (throws when it cannot); the second argument is the owner window handle.</summary>
    internal Action<string, nint> Recycler { get; set; } = RecycleBin.Recycle;

    /// <summary>Test seam: the file is on a local fixed drive (where the Recycle Bin takes it).</summary>
    internal Func<string, bool> IsFixedDrive { get; set; } = ScreenshotService.IsOnLocalFixedDrive;

    /// <summary>What a look at the file found: there, gone for sure, or unknown (an error other than "no such file in a folder that is there").</summary>
    internal enum FileProbe { Present, Gone, Unavailable }

    /// <summary>
    /// Test seam: looks at the file (attributes) without trusting <c>Exists</c>, which says false for ANY error (an offline mapped drive,
    /// an unplugged disk, an SMB timeout). Gone only on "file not found" with the parent folder still answering; anything else is Unavailable.
    /// </summary>
    internal Func<string, (FileProbe State, FileAttributes Attributes)> Probe { get; set; } = DefaultProbe;

    internal static (FileProbe, FileAttributes) DefaultProbe(string path)
    {
        try { return (FileProbe.Present, File.GetAttributes(path)); }
        catch (FileNotFoundException)
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            bool dirThere;
            try { dirThere = dir != null && Directory.Exists(dir); } catch { dirThere = false; }
            return (dirThere ? FileProbe.Gone : FileProbe.Unavailable, 0);
        }
        catch { return (FileProbe.Unavailable, 0); }
    }

    /// <summary>Test seam: the reparse tag of a path that has the reparse-point attribute, read without opening (hydrating) the file.</summary>
    internal Func<string, uint> ReparseTagOf { get; set; } = RecycleBin.ReparseTag;

    /// <summary>A cloud-sync placeholder tag (IO_REPARSE_TAG_CLOUD and CLOUD_1..F: OneDrive and the like). Any other reparse tag (symlink, mount point...) is not a plain file.</summary>
    internal static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;

    /// <summary>Test seam: this clip is being uploaded (<see cref="AppController.IsUploading"/>). Asked on the calling thread.</summary>
    internal Func<ClipEntry, bool> IsUploading { get; set; } = _ => false;

    /// <summary>Test seam: the trim window for this clip is open (<see cref="UI.TrimWindow.Find"/>). Asked on the calling thread.</summary>
    internal Func<ClipEntry, bool> IsTrimOpen { get; set; } = _ => false;

    internal ClipDeleter(ClipLibrary library, Func<string> clipsFolder, Func<string?> archiveFolder)
    {
        _library = library;
        _clipsFolder = clipsFolder;
        _archiveFolder = archiveFolder;
    }

    /// <summary>The pop-up is needed unless the user ticked "Do not ask again".</summary>
    internal static bool ConfirmNeeded(Settings settings) => !settings.ClipDeleteConfirmDismissed;

    /// <summary>The pop-up's answer: remembers "Do not ask again" only for a confirmed delete (Cancel never saves it).</summary>
    internal static bool Remember(bool confirmed, bool doNotAskAgain) => confirmed && doNotAskAgain;

    /// <summary>A shared clip keeps its library entry after the file is deleted (its link stays in the Shared list); any other leaves it.</summary>
    internal static bool KeepEntry(ClipEntry entry) => entry.IsShared;

    /// <summary>
    /// The path rule (no disk access): fully qualified, a drive-letter path (no network or device path, no alternate stream), the
    /// extension exactly <c>.mp4</c>, and inside <paramref name="clipsFolder"/> (any subfolder) or <paramref name="archiveFolder"/>
    /// after both are fully resolved (so <c>..</c> cannot climb out). The folder itself is not a clip.
    /// </summary>
    internal static bool IsAllowedPath(string? path, string? clipsFolder, string? archiveFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (path.IndexOfAny(new[] { '*', '?' }) >= 0) return false;   // SHFileOperation expands wildcards in its file list
            if (ScreenshotService.IsUnc(path) || !System.IO.Path.IsPathFullyQualified(path)) return false;
            string full = System.IO.Path.GetFullPath(path);
            if (ScreenshotService.IsUnc(full)) return false;
            if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\' || full.IndexOf(':', 2) >= 0) return false;
            if (!string.Equals(System.IO.Path.GetExtension(full), ".mp4", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string? folder in new[] { clipsFolder, archiveFolder })
            {
                if (string.IsNullOrWhiteSpace(folder) || !System.IO.Path.IsPathFullyQualified(folder)) continue;
                string root = System.IO.Path.GetFullPath(folder);   // a drive root ("D:\") already ends in a separator
                if (!System.IO.Path.EndsInDirectorySeparator(root)) root += '\\';
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>The clip has trims cut from it (their <see cref="ClipEntry.SourcePath"/> is this clip).</summary>
    internal static bool HasTrims(IEnumerable<ClipEntry> entries, ClipEntry clip)
    {
        string key = ClipGrouping.Key(clip.Path);
        return entries.Any(e => e.IsTrim && !ReferenceEquals(e, clip) && !string.IsNullOrWhiteSpace(e.SourcePath)
            && string.Equals(ClipGrouping.Key(e.SourcePath), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The pop-up's body: what happens, plus a line for the trims that stay and the link that keeps working.</summary>
    internal static string ConfirmBody(string title, bool hasTrims, bool shared)
    {
        string text = $"{title} will be moved to the Recycle Bin, where you can restore it.";
        if (hasTrims) text += " Its trims stay in the list.";
        if (shared) text += " The shared link keeps working.";
        return text;
    }

    /// <summary>What the row says when a delete did not happen.</summary>
    internal static string ProblemText(ClipDeleteResult result) => result switch
    {
        ClipDeleteResult.TrimOpen => "Close the trim window for this clip first.",
        ClipDeleteResult.Refused => "Clink only deletes clips that are in the clips or archive folder.",
        ClipDeleteResult.NotLocal => "This clip is on a network or removable drive, where the Recycle Bin can't take it.",
        ClipDeleteResult.Link => "This clip is a shortcut to another location, so Clink won't delete it.",
        ClipDeleteResult.Uploading => "This clip is uploading. Delete it after the upload finishes.",
        ClipDeleteResult.Unreachable => "Clink couldn't reach this clip's file. Check that its drive and folder are available, then try again.",
        ClipDeleteResult.Failed => "Windows couldn't move this clip to the Recycle Bin, so it was not deleted.",
        _ => "",
    };

    /// <summary>
    /// What can be said before anything is asked or touched (path rule, then the trim window), or null when the delete may go on.
    /// Call on the UI thread (the trim windows are listed there).
    /// </summary>
    internal ClipDeleteResult? Precheck(ClipEntry entry)
    {
        if (ScreenshotService.IsUnc(entry.Path))
        {
            Log.Warn("clip: not deleted: " + System.IO.Path.GetFileName(entry.Path) + " is on a network path (it would not go to the Recycle Bin)");
            return ClipDeleteResult.NotLocal;
        }
        if (!IsAllowedPath(entry.Path, _clipsFolder(), _archiveFolder()))
        {
            Log.Warn("clip: not deleted: " + System.IO.Path.GetFileName(entry.Path) + " is not a .mp4 in the clips or archive folder");
            return ClipDeleteResult.Refused;
        }
        if (!IsFixedDrive(entry.Path))   // cheap: a drive-type lookup, so mapped / removable drives are refused before the pop-up
        {
            Log.Warn("clip: not deleted: " + System.IO.Path.GetFileName(entry.Path) + " is not on an available local fixed drive (it would not go to the Recycle Bin)");
            return ClipDeleteResult.NotLocal;
        }
        if (IsUploading(entry))
        {
            Log.Info("clip: not deleted: " + System.IO.Path.GetFileName(entry.Path) + " is uploading");
            return ClipDeleteResult.Uploading;
        }
        if (IsTrimOpen(entry))
        {
            Log.Info("clip: not deleted: " + System.IO.Path.GetFileName(entry.Path) + " is open in the trim window");
            return ClipDeleteResult.TrimOpen;
        }
        return null;
    }

    /// <summary>Deletes the clip (see the class summary). The path rule and trim window are checked on the calling thread, the Recycle Bin call on a worker. Never throws.</summary>
    internal Task<ClipDeleteResult> DeleteAsync(ClipEntry entry, nint owner = 0)
    {
        if (Precheck(entry) is { } refused) return Task.FromResult(refused);
        return Task.Run(() =>
        {
            string path = entry.Path;
            string name = System.IO.Path.GetFileName(path);
            try
            {
                // The drive first: a missing or remote root is not "fixed", and then nothing below can say whether the file is gone.
                if (!IsFixedDrive(path))
                {
                    Log.Warn("clip: not deleted: " + name + " is not on an available local fixed drive (it would not go to the Recycle Bin)");
                    return ClipDeleteResult.NotLocal;
                }
                var (state, attrs) = Probe(path);
                if (state == FileProbe.Unavailable)
                {
                    Log.Warn("clip: not deleted: " + name + " could not be looked at (drive or folder not reachable); the entry stays");
                    return ClipDeleteResult.Unreachable;
                }
                bool exists = state == FileProbe.Present;
                if (exists)
                {
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        Log.Warn("clip: not deleted: " + name + " is a folder");
                        return ClipDeleteResult.Refused;
                    }
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        uint tag = ReparseTagOf(path);
                        if (!IsCloudTag(tag))
                        {
                            Log.Warn("clip: not deleted: " + name + " is a link or mount point (reparse tag 0x" + tag.ToString("X") + ")");
                            return ClipDeleteResult.Link;
                        }
                    }
                    try { Recycler(System.IO.Path.GetFullPath(path), owner); }
                    catch (Exception ex)
                    {
                        Log.Warn("clip: not deleted: " + ex.Message);
                        return ClipDeleteResult.Failed;
                    }
                }
                // A reload (Load) between the row and now may have replaced the entry: act on the one the library holds.
                var live = _library.Find(path);
                // Nothing at this path any more and the entry points elsewhere: the archive service moved the clip meanwhile, so
                // its file survives at the new path and the entry must stay with it.
                if (live == null && !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)) return ClipDeleteResult.Unreachable;
                live ??= entry;
                if (KeepEntry(live)) _library.Update(live);   // the entry stays (its link); the list shows it without the file
                else _library.Remove(live);
                Log.Info("clip: deleted " + name + (exists ? " (Recycle Bin)" : " (file was gone)") + (KeepEntry(live) ? "; the entry stays for its Copium link" : ""));
                return ClipDeleteResult.Deleted;
            }
            catch (Exception ex)
            {
                Log.Warn("clip: not deleted: " + ex.Message);
                return ClipDeleteResult.Failed;
            }
        });
    }
}
