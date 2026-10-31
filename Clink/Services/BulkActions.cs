using System.IO;

namespace Clink.Services;

/// <summary>One file for the multi-file share window: where it is, whether it is on the archive share, and what the card shows of it.</summary>
internal sealed record ShareFileItem(string Path, bool IsArchived, long Size, double Duration);

/// <summary>What the library's selection bar can do with the selected rows.</summary>
internal enum BulkAction { Upload, CopyLinks, ShareFiles, SaveCopies, Delete }

/// <summary>One entry of the bar's Share menu. <paramref name="Header"/> carries its access key (underscore); a disabled entry says why in <paramref name="ToolTip"/>.</summary>
internal sealed record BulkActionItem(BulkAction Action, string Header, string ToolTip, bool Enabled = true);

/// <summary>The library selection bar's rules (which Share entries, the labels, the texts, the names of saved copies). Pure.</summary>
internal static class BulkActions
{
    /// <summary>The most files one "Share files…" window takes.</summary>
    public const int MaxShareFiles = 100;

    public const string TooManyToShareText = "Select up to 100 files to share at once";
    public const string MenuLabel = ShareChoices.MenuLabel;

    /// <summary>
    /// The Share menu of the bar, or null when Share acts directly (no token, or cloud sharing off). <paramref name="linked"/> of
    /// <paramref name="count"/> selected clips have a link; "Copy links" needs all of them linked. <paramref name="uploadOn"/>:
    /// "Upload and copy links" is offered for the clips that have none (a later slice turns it on; the library passes false for now).
    /// </summary>
    public static IReadOnlyList<BulkActionItem>? ShareMenu(bool hasToken, bool cloudOn, int count, int linked, bool uploadOn)
    {
        if (!hasToken || !cloudOn) return null;
        var items = new List<BulkActionItem>();
        if (uploadOn && linked < count)
            items.Add(new BulkActionItem(BulkAction.Upload, "_Upload and copy links", "Upload the clips that have no link to your Copium account, then copy every link"));
        if (count > 0 && linked == count)
            items.Add(new BulkActionItem(BulkAction.CopyLinks, "_Copy links", "Copy the Copium links of the selected clips, one per line"));
        bool tooMany = count > MaxShareFiles;
        items.Add(new BulkActionItem(BulkAction.ShareFiles, "_Share files…",
            tooMany ? TooManyToShareText : "Drag or copy the files into Discord, a browser or a folder", !tooMany));
        items.Add(new BulkActionItem(BulkAction.SaveCopies, "S_ave copies…", "Save copies of the selected clip files to another folder"));
        return items;
    }

    public const string ShareFilesLabel = "Share files…", CopyLinksLabel = "Copy links";

    /// <summary>
    /// The bar's share buttons, in order: the menu (token and cloud sharing on), or "Share files…" (cloud on, no token) followed by
    /// "Copy links" when every selected clip is linked (as the single row offers both), or "Copy links" alone (cloud off, every clip
    /// linked), or none.
    /// </summary>
    public static IReadOnlyList<string> ShareButtonLabels(bool hasToken, bool cloudOn, int count, int linked)
    {
        bool allLinked = count > 0 && linked == count;
        if (cloudOn && hasToken) return new[] { MenuLabel };
        var labels = new List<string>();
        if (cloudOn) labels.Add(ShareFilesLabel);
        if (allLinked) labels.Add(CopyLinksLabel);
        return labels;
    }

    /// <summary>
    /// The selection bar is shown while something is selected, and also while copies are being saved (it then holds the only Cancel,
    /// so emptying the selection or switching the list must not take it away).
    /// </summary>
    public static bool BarVisible(int selectedCount, bool saving) => selectedCount > 0 || saving;

    /// <summary>"6 clips selected", "1 clip selected", "3 screenshots selected".</summary>
    public static string CountLabel(int n, bool screenshots)
    {
        string noun = screenshots ? (n == 1 ? "screenshot" : "screenshots") : (n == 1 ? "clip" : "clips");
        return $"{n} {noun} selected";
    }

    /// <summary>The links one per line (CRLF), without empty ones and without repeats, in the order given.</summary>
    public static string JoinLinks(IEnumerable<string?> links)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var l in links)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            string t = l.Trim();
            if (seen.Add(t)) list.Add(t);
        }
        return string.Join("\r\n", list);
    }

    /// <summary>
    /// A file name for a copy in <paramref name="folder"/> that clashes with nothing: not a file there (<paramref name="exists"/> says
    /// so for a full path) and not a name already <paramref name="taken"/> in this batch (case-insensitive). "clip.mp4" becomes
    /// "clip (2).mp4", "clip (3).mp4"... The name returned is added to <paramref name="taken"/>.
    /// </summary>
    public static string UniqueName(string folder, string fileName, Func<string, bool> exists, ISet<string> taken)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        string name = fileName;
        for (int n = 2; Clashes(name); n++) name = $"{stem} ({n}){ext}";
        taken.Add(name);
        return name;

        bool Clashes(string candidate)
        {
            foreach (var t in taken) if (string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return exists(Path.Combine(folder, candidate));
        }
    }

    private static string Copies(int n) => n == 1 ? "1 copy" : $"{n} copies";

    /// <summary>
    /// What the bar says after the copies: all saved, some, or none, with the first error, how many files could not be reached
    /// (<paramref name="unreachable"/>) and how many were not tried because they were busy (uploading or being deleted). Every selected
    /// row is in <paramref name="total"/>.
    /// </summary>
    public static string SaveSummary(int saved, int total, string? firstError, int unreachable = 0, int busy = 0)
    {
        string why = string.IsNullOrWhiteSpace(firstError) ? "" : " " + firstError.TrimEnd('.') + ".";
        if (unreachable > 0) why += unreachable == 1 ? " 1 could not be reached." : $" {unreachable} could not be reached.";
        if (busy > 0) why += busy == 1 ? " 1 was busy." : $" {busy} were busy.";
        if (saved >= total && total > 0) return $"Saved {Copies(saved)}.";
        if (saved == 0) return "Nothing was saved." + why;
        return $"Saved {saved} of {total} copies." + why;
    }

    /// <summary>The warning under the share window's card when files were left out, or null when every file is there.</summary>
    public static string? ShareFilesSummary(int present, int total)
    {
        int missing = total - present;
        if (missing <= 0) return null;
        if (present == 0) return total == 1 ? "The file could not be reached." : "None of the files could be reached.";
        return missing == 1 ? "1 file could not be reached and was left out." : $"{missing} files could not be reached and were left out.";
    }

    /// <summary>"Share 6 clips" / "Share 1 clip" / "Share 6 screenshots": the multi-file share window's title.</summary>
    public static string ShareWindowTitle(int count, bool screenshots)
    {
        string noun = screenshots ? (count == 1 ? "screenshot" : "screenshots") : (count == 1 ? "clip" : "clips");
        return $"Share {count} {noun}";
    }
}
