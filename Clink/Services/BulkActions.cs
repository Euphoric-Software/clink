using System.IO;
using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>One file for the multi-file share window: where it is, whether it is on the archive share, and what the card shows of it.</summary>
internal sealed record ShareFileItem(string Path, bool IsArchived, long Size, double Duration);

/// <summary>What the library's selection bar can do with the selected rows.</summary>
internal enum BulkAction { Upload, CopyLinks, ShareFiles, SaveCopies, Delete }

/// <summary>Which share button of the bar a <see cref="BulkBarButton"/> is: the split "Share" + arrow, "Share files…", or "Copy links". The bar acts by the kind, never by the text of the label (which is translated).</summary>
internal enum BulkBarButtonKind { Split, ShareFiles, CopyLinks }

/// <summary>One share button of the selection bar: what it does and the label it shows.</summary>
internal readonly record struct BulkBarButton(BulkBarButtonKind Kind, string Label);

/// <summary>One entry of the bar's Share menu. <paramref name="Header"/> carries its access key (underscore); a disabled entry says why in <paramref name="ToolTip"/>.</summary>
internal sealed record BulkActionItem(BulkAction Action, string Header, string ToolTip, bool Enabled = true);

/// <summary>The library selection bar's rules (which Share entries, the labels, the texts, the names of saved copies). Pure.</summary>
internal static class BulkActions
{
    /// <summary>The most files one "Share files…" window takes.</summary>
    public const int MaxShareFiles = 100;

    public static string TooManyToShareText => L.Plural("Select up to {count} files to share at once", MaxShareFiles);

    // The bar's split button: "Share" runs Share files…, "▾" opens the menu (the arrow's label, name and tip are the clip row's).
    public static string SplitMainLabel => ShareChoices.SplitMainLabel;
    public static string SplitMainName => L.T("Share files");
    public static string SplitMainTip => L.T("Open the share window: drag the files into Discord, a browser or a folder");
    public const BulkAction SplitMainAction = BulkAction.ShareFiles;

    /// <summary>The bar's Share button is split (a menu exists): a token and cloud sharing on.</summary>
    public static bool ShareIsSplit(bool hasToken, bool cloudOn) => hasToken && cloudOn;

    /// <summary>The bar's split halves: the main half (Share files) is off above <see cref="MaxShareFiles"/>; the arrow stays on, its menu still offers Save copies.</summary>
    public static (bool Main, bool Arrow) SplitEnabled(int count) => (count <= MaxShareFiles, true);

    /// <summary>
    /// The Share menu of the bar, or null when Share acts directly (no token, or cloud sharing off). <paramref name="linked"/> of
    /// <paramref name="count"/> selected clips have a link; "Copy links" needs all of them linked. <paramref name="uploadOn"/>:
    /// "Upload and copy links" is offered (first) for the clips that have none.
    /// </summary>
    public static IReadOnlyList<BulkActionItem>? ShareMenu(bool hasToken, bool cloudOn, int count, int linked, bool uploadOn, bool screenshots = false)
    {
        if (!hasToken || !cloudOn) return null;
        var items = new List<BulkActionItem>();
        if (uploadOn && linked < count)
            items.Add(new BulkActionItem(BulkAction.Upload, L.T("_Upload and copy links"), UploadTip(screenshots)));
        if (count > 0 && linked == count)
            items.Add(new BulkActionItem(BulkAction.CopyLinks, L.T("_Copy links"), L.T("Copy the Copium links of the selected clips, one per line")));
        bool tooMany = count > MaxShareFiles;
        items.Add(new BulkActionItem(BulkAction.ShareFiles, L.T("_Share files…"),
            tooMany ? TooManyToShareText : L.T("Drag or copy the files into Discord, a browser or a folder"), !tooMany));
        items.Add(new BulkActionItem(BulkAction.SaveCopies, L.T("S_ave copies…"), L.T("Save copies of the selected clip files to another folder")));
        return items;
    }

    public static string ShareFilesLabel => L.T("Share files…");
    public static string CopyLinksLabel => L.T("Copy links");
    public static string UploadLabel => L.T("Upload and copy links");

    /// <summary>The tooltip of "Upload and copy links".</summary>
    public static string UploadTip(bool screenshots) => screenshots
        ? L.T("Upload the screenshots that have no link yet, then copy all links")
        : L.T("Upload the clips that have no link yet, then copy all links");

    /// <summary>"Upload and copy links" is offered on the bar: a token, cloud sharing on and at least one selected item without a link.</summary>
    public static bool UploadOffered(bool hasToken, bool cloudOn, int count, int linked) => hasToken && cloudOn && count > 0 && linked < count;

    /// <summary>The status after Cancel was pressed during a bulk upload.</summary>
    public static string UploadStoppingText => L.T("Stopping after this one…");

    /// <summary>The bar's status when a bulk upload ends: the summary, or why nothing was tried (no token, cloud off, the user said "Not now").</summary>
    public static string UploadStatus(BulkShareResult r, bool screenshots) => UploadStatusNote(r, screenshots).Shown;

    /// <summary><see cref="UploadStatus"/> as a note: the bar shows it translated, a log gets English.</summary>
    public static Note UploadStatusNote(BulkShareResult r, bool screenshots)
    {
        if (r.Declined) return Note.Of("Not uploaded: sharing with Copium is not enabled");
        bool tried = r.Uploaded + r.AlreadyLinked + r.Refused + r.Busy + r.Failed > 0 || r.Cancelled;
        if (!tried && !string.IsNullOrWhiteSpace(r.FirstError)) return Note.Verbatim(r.FirstError);
        return UploadSummaryNote(r, screenshots);
    }

    /// <summary>The summary of a finished bulk upload (the notice and, unless nothing was tried, the bar's status).</summary>
    public static string UploadSummary(BulkShareResult r, bool screenshots) => UploadSummaryNote(r, screenshots).Shown;

    /// <summary><see cref="UploadSummary(BulkShareResult, bool)"/> as a note.</summary>
    public static Note UploadSummaryNote(BulkShareResult r, bool screenshots)
    {
        // With no failure the stop reason is also FirstError: pass it once, as the stop reason.
        string? failError = r.Failed > 0 || r.StopError == null ? r.FirstError : null;
        bool allLinked = r.Uploaded + r.AlreadyLinked == r.Total;   // then "Copy links" is offered for this selection
        return UploadSummaryNote(r.Uploaded, r.AlreadyLinked, r.Total, r.Refused, r.Busy, r.Failed, Reason(failError), r.Cancelled, screenshots,
            Reason(r.StopError), r.NotTried, r.NotCopied, allLinked);
    }

    /// <summary>The status while a bulk upload runs: "Uploading 2 of 6…" (with " 40%" once the item reports progress).</summary>
    public static string UploadingStatus(int index, int total, double fraction = -1) =>
        fraction > 0
            ? L.T("Uploading {index} of {total}… {percent}", ("index", index), ("total", total), ("percent", fraction.ToString("P0", L.FormatCulture)))
            : L.T("Uploading {index} of {total}…", ("index", index), ("total", total));

    /// <summary>
    /// What the bar and the toast say after "Upload and copy links". <paramref name="uploaded"/> were uploaded now,
    /// <paramref name="alreadyLinked"/> had a link already; every selected item is in <paramref name="total"/>. The links on the clipboard
    /// are the sum of the two. Refused (over the account's limit), busy, failed (first error as told) and a cancel are added.
    /// </summary>
    public static string UploadSummary(int uploaded, int alreadyLinked, int total, int refused, int busy, int failed, string? firstError, bool cancelled, bool screenshots,
        string? stopError = null, int notTried = 0, bool notCopied = false, bool canRetryCopy = false) =>
        UploadSummaryNote(uploaded, alreadyLinked, total, refused, busy, failed, Reason(firstError), cancelled, screenshots, Reason(stopError), notTried, notCopied, canRetryCopy).Shown;

    private static Note? Reason(string? text) => string.IsNullOrWhiteSpace(text) ? null : Note.Verbatim(text);

    /// <summary>
    /// <see cref="UploadSummary(int, int, int, int, int, int, string?, bool, bool, string?, int, bool, bool)"/> as a note: the first error and
    /// the stop reason are notes too, so the log has them in English.
    /// </summary>
    public static Note UploadSummaryNote(int uploaded, int alreadyLinked, int total, int refused, int busy, int failed, Note? firstError, bool cancelled, bool screenshots,
        Note? stopError = null, int notTried = 0, bool notCopied = false, bool canRetryCopy = false) => Note.Build(() =>
    {
        int links = uploaded + alreadyLinked;
        // each part is a whole sentence of its own; they are joined by a space (as the one string was before)
        var why = new List<string>();
        if (refused > 0) why.Add(L.Plural("{count} are too large to share.", refused));
        if (busy > 0) why.Add(L.Plural("{count} were busy.", busy));
        if (failed > 0 || firstError != null)
        {
            // (.Shown: inside Note.Build it is the language being composed, English for the log)
            if (firstError != null && failed > 1) why.Add(L.Plural("{count} failed: {error}.", failed, ("error", firstError.WithoutFinalPeriod().Shown)));
            else if (firstError != null) why.Add(L.T("{error}.", ("error", firstError.WithoutFinalPeriod().Shown)));
            else why.Add(L.Plural("{count} failed.", failed));
        }
        if (stopError != null)
        {
            bool afterFailure = failed > 0 || firstError != null;
            why.Add(afterFailure ? L.T("Stopped: {error}.", ("error", stopError.WithoutFinalPeriod().Shown)) : L.T("{error}.", ("error", stopError.WithoutFinalPeriod().Shown)));
        }
        if (notTried > 0) why.Add(L.Plural("{count} were not tried.", notTried));
        if (cancelled) why.Add(L.T("Cancelled."));
        if (links == 0) return string.Join(" ", why.Prepend(L.T("Nothing was uploaded.")));
        int toUpload = total - alreadyLinked;
        // "Uploaded {count}": the noun agrees with the number shown. "Uploaded {uploaded} of {count}": it agrees with {count}, the noun's own number.
        string? head = toUpload <= 0 ? null
            : uploaded >= toUpload
                ? (screenshots ? L.Plural("Uploaded {count} screenshots.", uploaded) : L.Plural("Uploaded {count} clips.", uploaded))
                : (screenshots ? L.Plural("Uploaded {uploaded} of {count} screenshots.", toUpload, ("uploaded", uploaded)) : L.Plural("Uploaded {uploaded} of {count} clips.", toUpload, ("uploaded", uploaded)));
        string onClipboard = notCopied
            ? L.Plural("{count} links are ready but couldn't be copied to the clipboard.", links)
            : L.Plural("{count} links are on the clipboard.", links);
        var parts = new List<string>();
        if (head != null) parts.Add(head);
        parts.Add(onClipboard);
        parts.AddRange(why);
        if (notCopied && canRetryCopy) parts.Add(L.T("Use Copy links to try again."));
        return string.Join(" ", parts);
    });

    /// <summary>
    /// The bar's share buttons, in order: the menu (token and cloud sharing on), or "Share files…" (cloud on, no token) followed by
    /// "Copy links" when every selected clip is linked (as the single row offers both), or "Copy links" alone (cloud off, every clip
    /// linked), or none.
    /// </summary>
    public static IReadOnlyList<string> ShareButtonLabels(bool hasToken, bool cloudOn, int count, int linked) =>
        ShareButtons(hasToken, cloudOn, count, linked).Select(b => b.Label).ToList();

    /// <summary><see cref="ShareButtonLabels"/> with the kind of each button, so the bar acts by kind.</summary>
    public static IReadOnlyList<BulkBarButton> ShareButtons(bool hasToken, bool cloudOn, int count, int linked)
    {
        bool allLinked = count > 0 && linked == count;
        if (ShareIsSplit(hasToken, cloudOn)) return new[] { new BulkBarButton(BulkBarButtonKind.Split, SplitMainLabel) };   // the split button: the arrow half is added with it
        var buttons = new List<BulkBarButton>();
        if (cloudOn) buttons.Add(new BulkBarButton(BulkBarButtonKind.ShareFiles, ShareFilesLabel));
        if (allLinked) buttons.Add(new BulkBarButton(BulkBarButtonKind.CopyLinks, CopyLinksLabel));
        return buttons;
    }

    /// <summary>
    /// The selection bar is shown while something is selected, and also while copies are being saved or links uploaded (it then holds
    /// the only Cancel, so emptying the selection or switching the list must not take it away).
    /// </summary>
    public static bool BarVisible(int selectedCount, bool longRunning) => selectedCount > 0 || longRunning;

    /// <summary>"6 clips selected", "1 clip selected", "3 screenshots selected".</summary>
    public static string CountLabel(int n, bool screenshots)
    {
        return screenshots ? L.Plural("{count} screenshots selected", n) : L.Plural("{count} clips selected", n);
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

    /// <summary>
    /// What the bar says after the copies: all saved, some, or none, with the first error, how many files could not be reached
    /// (<paramref name="unreachable"/>) and how many were not tried because they were busy (uploading or being deleted). Every selected
    /// row is in <paramref name="total"/>.
    /// </summary>
    public static string SaveSummary(int saved, int total, string? firstError, int unreachable = 0, int busy = 0) => SaveSummaryNote(saved, total, firstError, unreachable, busy).Shown;

    /// <summary><see cref="SaveSummary"/> as a note (the first error is shown and logged as it is).</summary>
    public static Note SaveSummaryNote(int saved, int total, string? firstError, int unreachable = 0, int busy = 0) =>
        SaveSummaryNote(saved, total, Reason(firstError), unreachable, busy);

    /// <summary><see cref="SaveSummary"/> as a note whose first error is a note too.</summary>
    public static Note SaveSummaryNote(int saved, int total, Note? firstError, int unreachable = 0, int busy = 0) => Note.Build(() =>
    {
        // each part is a whole sentence of its own; they are joined by a space
        var why = new List<string>();
        if (firstError != null) why.Add(L.T("{error}.", ("error", firstError.WithoutFinalPeriod().Shown)));
        if (unreachable > 0) why.Add(L.Plural("{count} could not be reached.", unreachable));
        if (busy > 0) why.Add(L.Plural("{count} were busy.", busy));
        if (saved >= total && total > 0) return L.Plural("Saved {count} copies.", saved);
        string head = saved == 0 ? L.T("Nothing was saved.") : L.Plural("Saved {saved} of {count} copies.", total, ("saved", saved));
        return string.Join(" ", why.Prepend(head));
    });

    /// <summary>The warning under the share window's card when files were left out, or null when every file is there.</summary>
    public static string? ShareFilesSummary(int present, int total)
    {
        int missing = total - present;
        if (missing <= 0) return null;
        if (present == 0) return total == 1 ? L.T("The file could not be reached.") : L.T("None of the files could be reached.");
        return L.Plural("{count} files could not be reached and were left out.", missing);
    }

    /// <summary>"Share 6 clips" / "Share 1 clip" / "Share 6 screenshots": the multi-file share window's title.</summary>
    public static string ShareWindowTitle(int count, bool screenshots)
    {
        return screenshots ? L.Plural("Share {count} screenshots", count) : L.Plural("Share {count} clips", count);
    }
}
