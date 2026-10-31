using System.IO;
using System.Windows;
using Clink.Screenshots;
using Clink.UI;

namespace Clink.Services;

/// <summary>How a bulk "Save copies…" ended: how many files were written (of <paramref name="Total"/>, every file asked for), where, the first error, whether the user cancelled, how many files could not be reached (missing source; not part of the first error) and how many were skipped as busy when their turn came (<paramref name="Busy"/>).</summary>
internal sealed record BulkSaveResult(int Saved, int Total, string? Folder, string? FirstError, bool Cancelled, int Unreachable = 0, int Busy = 0);

/// <summary>
/// How a bulk "Upload and copy links" ended. <paramref name="Uploaded"/> were uploaded now, <paramref name="AlreadyLinked"/> had a link
/// already; <paramref name="Links"/> are all the links, in selection order (the clipboard text). <paramref name="Refused"/> were over the
/// account's limit, <paramref name="Busy"/> were skipped because their row was busy, <paramref name="Failed"/> failed (the first reason is
/// <paramref name="FirstError"/>, which also explains a batch that could not start or stopped early). <paramref name="Declined"/>: the
/// user said no to the sharing question (nothing was uploaded). <paramref name="Cancelled"/>: stopped between two items.
/// <paramref name="StopError"/> is why the batch stopped early (token rejected, no token...), kept apart from the failures'
/// <paramref name="FirstError"/>; <paramref name="NotTried"/> items were never reached; <paramref name="NotCopied"/>: the links could not be put on the clipboard.
/// </summary>
internal sealed record BulkShareResult(int Uploaded, int AlreadyLinked, int Total, IReadOnlyList<string> Links, int Refused, int Busy, int Failed,
    string? FirstError, bool Declined, bool Cancelled, string? StopError = null, int NotTried = 0, bool NotCopied = false);

internal sealed partial class AppController
{
    private ShareFilesWindow? _shareFilesWindow;   // the open multi-file share window (UI thread): one at a time

    /// <summary>Test seam: receives the bulk upload's one closing notice (title, text) instead of the toast.</summary>
    internal Action<string, string>? BulkNoticeSink { get; set; }

    /// <summary>The bulk upload's error without a Copium token.</summary>
    internal const string BulkNoTokenText = "Sign in with Copium under App → Copium to upload";

    private enum StepKind { Uploaded, Refused, Failed, Stop }
    private readonly record struct Step(StepKind Kind, string? Error = null);

    /// <summary>Test seam: opens the multi-file share window instead of the real one (a unit test must not put a window on the screen).</summary>
    internal Func<IReadOnlyList<ShareFileItem>, bool, bool>? ShareFilesWindowOpener { get; set; }

    /// <summary>Test seam: picks the folder the copies go to instead of the folder dialog (null = the user cancelled).</summary>
    internal Func<string?>? SaveFolderPicker { get; set; }

    /// <summary>
    /// Opens the share window for several files (the library's "Share files…"): a new one closes the one still open. Same placement
    /// rules as <see cref="OpenShareWindow"/> (no owner, on top, over a game without the focus). UI thread; from another it runs
    /// there. False when the window could not be opened (logged).
    /// </summary>
    public bool OpenShareFilesWindow(IReadOnlyList<ShareFileItem> items, bool screenshots)
    {
        if (ShareFilesWindowOpener is { } opener) return opener(items, screenshots);
        if (!_ui.CheckAccess()) return _ui.Invoke(() => OpenShareFilesWindow(items, screenshots));
        try
        {
            bool overGame = OverGame;
            var old = _shareFilesWindow;
            _shareFilesWindow = null;
            if (old != null) { try { old.Close(); } catch (Exception ex) { Log.Warn("share files window: close: " + ex.Message); } }
            var window = new ShareFilesWindow(items, screenshots);
            _shareFilesWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_shareFilesWindow, window)) _shareFilesWindow = null; };
            try
            {
                if (overGame) window.ShowOverGame(SelectedMonitor, Settings.ToastPosition);
                else window.Show();
            }
            catch { if (ReferenceEquals(_shareFilesWindow, window)) _shareFilesWindow = null; throw; }
            Say(screenshots ? "Drag the screenshots where you want to share them" : "Drag the clips where you want to share them");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("share files window", ex);
            return false;
        }
    }

    /// <summary>
    /// "Save copies…": one folder is asked for, then the clips are copied into it one after the other (each through
    /// <see cref="SaveCopy.CopyUniqueAsync"/>, so a cancel or a failure leaves no partial file). Nothing is ever overwritten, not even a
    /// file that appears while a copy runs: a clash gets "name (2).mp4" (<see cref="BulkActions.UniqueName"/>). A folder inside Clink's clips folder refuses the whole batch and writes
    /// nothing; a file that cannot be saved is skipped and the rest go on. <paramref name="progress"/> gets (index in
    /// <paramref name="entries"/>, 0..1) from any thread; <paramref name="ct"/> and quitting both cancel. The folder is remembered
    /// like a single save's. <paramref name="begin"/> is asked (on the caller's thread) when entry i's turn comes, before its progress
    /// or any disk access: false skips it as busy (<see cref="BulkSaveResult.Busy"/>). When the chosen folder is gone in the middle (a
    /// USB drive unplugged), the batch stops with <see cref="FolderGoneText"/> instead of failing every remaining file.
    /// </summary>
    public async Task<BulkSaveResult> SaveCopiesAsync(IReadOnlyList<ClipEntry> entries, Window? owner, Action<int, double>? progress, CancellationToken ct,
        Func<int, bool>? begin = null)
    {
        int total = entries.Count;
        string? folder = SaveFolderPicker is { } picker ? picker() : PickSaveFolder(owner);
        if (string.IsNullOrWhiteSpace(folder)) return new BulkSaveResult(0, total, null, null, true);
        string? problem = FolderProblem(folder);
        if (problem != null)
        {
            Say("Not saved: " + problem);
            return new BulkSaveResult(0, total, folder, problem, false);
        }
        Say(total == 1 ? "Saving a copy…" : $"Saving {total} copies…");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int saved = 0, unreachable = 0, busy = 0;
        string? firstError = null, firstTarget = null;
        bool cancelled = false;
        for (int i = 0; i < total && !cancelled; i++)
        {
            if (linked.IsCancellationRequested) { cancelled = true; break; }
            if (begin != null && !begin(i)) { busy++; continue; }
            string source = entries[i].Path;
            int index = i;
            try
            {
                progress?.Invoke(index, 0);
                // Everything that touches the disk runs on the worker (an archived clip may sit on a dropped network share): the
                // source check, the name that clashes with nothing, and the copy (which never replaces a file that appeared meanwhile).
                var copy = Task.Run(async () =>
                {
                    if (!File.Exists(source)) throw new FileNotFoundException(Gone);
                    try
                    {
                        return await SaveCopy.CopyUniqueAsync(source, folder, Path.GetFileName(source), taken, new DirectProgress(p => progress?.Invoke(index, p)), linked.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && !FolderStillThere(folder))
                    {
                        throw new FolderGoneException(ex);   // the target folder went away (not the source): no later file can be saved either
                    }
                });
                lock (_saves) _saves.Add(copy);
                string done;
                try { done = await copy; }
                finally { lock (_saves) _saves.Remove(copy); }
                saved++;
                firstTarget ??= done;
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (FolderGoneException ex)
            {
                Log.Warn("save copies: the folder is no longer there (" + ex.InnerException?.Message + "); stopped");
                firstError = FolderGoneText;
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Log.Warn("save copies: " + Path.GetFileName(source) + " could not be reached: " + ex.Message);
                unreachable++;
            }
            catch (Exception ex)
            {
                Log.Error("save copies", ex);
                firstError ??= ex.Message;
            }
        }
        try
        {
            string? full = firstTarget != null ? Path.GetDirectoryName(Path.GetFullPath(firstTarget)) : null;
            if (full != null && ScreenshotService.IsOnLocalFixedDrive(firstTarget)
                && !string.Equals(full, Settings.SaveCopyFolder, StringComparison.OrdinalIgnoreCase))
            {
                Settings.SaveCopyFolder = full;
                Settings.Save();
            }
        }
        catch (Exception ex) { Log.Warn("save copies: could not remember the folder: " + ex.Message); }
        Say(cancelled ? "Save cancelled" : BulkActions.SaveSummary(saved, total, firstError, unreachable, busy));
        return new BulkSaveResult(saved, total, folder, firstError, cancelled, unreachable, busy);
    }

    /// <summary>
    /// "Upload and copy links" for clips: needs a Copium token and cloud sharing on (otherwise nothing is sent and the result says why).
    /// The sharing question is asked once, up front (<see cref="ConsentContext.ClipShare"/>); "Not now" uploads nothing. Then one clip at
    /// a time (the shared outcome fields and the per-clip upload dedupe make parallel uploads unsafe): a clip with a link keeps it (no
    /// upload), one over the account's limit is skipped with <see cref="BulkShareResult.Refused"/>, one already being uploaded by a
    /// single Share is waited for and uses its result, and <paramref name="begin"/> (asked on the caller's thread before an upload) false
    /// skips the clip as busy. <paramref name="ct"/> stops the batch after the item in hand (only quitting aborts an upload).
    /// At the end all links go to the clipboard in one write, in selection order, and one notice says what happened.
    /// </summary>
    public Task<BulkShareResult> ShareManyAsync(IReadOnlyList<ClipEntry> entries, Func<int, bool>? begin, Func<ClipEntry, IProgress<double>?> progressFor, CancellationToken ct) =>
        RunBatchAsync(entries, false, ConsentContext.ClipShare, e => e.CopiumLink,
            e => ShareRefusalFor(e.Size, e.Duration, Settings.CopiumPlus) != null,
            e => _uploads.ContainsKey(e), begin, progressFor, UploadClipStepAsync, ct);

    /// <summary>"Upload and copy links" for screenshots: the same rules as <see cref="ShareManyAsync"/> (consent asked as the library's Upload button does); the pictures go through the screenshot service's queue in batch mode (no clipboard write, no success toast).</summary>
    public Task<BulkShareResult> UploadShotsAsync(IReadOnlyList<ScreenshotEntry> entries, Func<int, bool>? begin, Func<ScreenshotEntry, IProgress<double>?> progressFor, CancellationToken ct) =>
        RunBatchAsync(entries, true, ConsentContext.ScreenshotUploadLibrary, e => e.CopiumLink,
            e => ShareRefusalFor(e.Size, 0, Settings.CopiumPlus) != null,
            _ => false, begin, progressFor, UploadShotStepAsync, ct);

    private async Task<Step> UploadClipStepAsync(ClipEntry entry, IProgress<double>? progress, Action release)
    {
        bool ok = await ShareAsync(entry, progress, context: ConsentContext.ClipShare, quiet: true, released: release);
        if (ok) return new Step(StepKind.Uploaded);
        if (_cts.IsCancellationRequested) return new Step(StepKind.Stop, "Clink is closing");
        // No token (the quiet path) or a rejected token: every further clip would fail the same way, so the batch stops.
        if (LastShareOutcome == ShareOutcome.Failed && (ShareTokenRejected || string.IsNullOrWhiteSpace(Settings.CopiumToken)))
            return new Step(StepKind.Stop, ShareTokenRejected ? (ShareError ?? "Copium rejected the token") : BulkNoTokenText);
        return LastShareOutcome switch
        {
            ShareOutcome.Refused => new Step(StepKind.Refused),
            ShareOutcome.Declined => new Step(StepKind.Stop, ShareError ?? "Not uploaded: sharing with Copium is not enabled"),
            _ => new Step(StepKind.Failed, ShareError),
        };
    }

    private async Task<Step> UploadShotStepAsync(ScreenshotEntry entry, IProgress<double>? progress, Action release)
    {
        string? problem = null;
        int rejectionsBefore = Screenshots.TokenRejections;
        bool ok = await Screenshots.UploadAsync(entry, manual: true, askAs: ConsentContext.ScreenshotUploadLibrary, batch: true, batchProgress: progress,
            batchProblem: text => problem ??= text);   // the service tells a batch's problems here, not in a toast each
        if (ok && !string.IsNullOrEmpty(entry.CopiumLink)) return new Step(StepKind.Uploaded);
        if (_cts.IsCancellationRequested) return new Step(StepKind.Stop, "Clink is closing");
        if (!Settings.CloudSharingEnabled) return new Step(StepKind.Stop, CloudOffText);
        if (string.IsNullOrWhiteSpace(Settings.CopiumToken)) return new Step(StepKind.Stop, BulkNoTokenText);
        if (Screenshots.TokenRejections != rejectionsBefore && Screenshots.CopiumTokenRejected) return new Step(StepKind.Stop, problem ?? "Copium rejected the token");
        return new Step(StepKind.Failed, problem);
    }

    private async Task<BulkShareResult> RunBatchAsync<T>(IReadOnlyList<T> items, bool screenshots, ConsentContext context, Func<T, string?> linkOf, Func<T, bool> refused,
        Func<T, bool> inFlight, Func<int, bool>? begin, Func<T, IProgress<double>?> progressFor, Func<T, IProgress<double>?, Action, Task<Step>> upload, CancellationToken ct)
    {
        int total = items.Count;
        var links = new List<string>();
        BulkShareResult Early(string error, bool declined = false)
        {
            Say(error);
            return new BulkShareResult(0, 0, total, links, 0, 0, 0, error, declined, false);
        }
        if (!Settings.CloudSharingEnabled) return Early(CloudOffText);
        if (string.IsNullOrWhiteSpace(Settings.CopiumToken)) return Early(BulkNoTokenText);
        bool needsUpload = false;   // the question is asked only when something will actually be uploaded (not linked already, not over the limit)
        foreach (var item in items) if (string.IsNullOrEmpty(linkOf(item)) && !refused(item)) { needsUpload = true; break; }
        if (needsUpload && !await EnsureCopiumConsentAsync(context))
        {
            string why = Settings.CloudSharingEnabled ? "Not uploaded: sharing with Copium is not enabled" : CloudOffText;
            Say(why);
            return new BulkShareResult(0, 0, total, links, 0, 0, 0, null, true, false);
        }
        int uploaded = 0, already = 0, refusedN = 0, busy = 0, failed = 0;
        string? firstError = null, stopError = null;
        int notTried = 0;
        bool cancelled = false;
        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested || _cts.IsCancellationRequested) { cancelled = true; break; }
            var item = items[i];
            if (!string.IsNullOrEmpty(linkOf(item))) { links.Add(linkOf(item)!); already++; continue; }
            if (refused(item)) { refusedN++; continue; }
            bool join = inFlight(item);   // a single Share is uploading it: wait for that, the row is not ours
            if (!join && begin != null && !begin(i)) { busy++; continue; }
            Step step;
            try { step = await upload(item, join ? null : progressFor(item), () => begin?.Invoke(-1)); }
            catch (OperationCanceledException) { step = new Step(StepKind.Stop, "Upload cancelled"); }
            catch (Exception ex)
            {
                Log.Error("bulk upload", ex);
                step = new Step(StepKind.Failed, ex.Message);
            }
            if (step.Kind == StepKind.Uploaded && !string.IsNullOrEmpty(linkOf(item))) { links.Add(linkOf(item)!); uploaded++; }
            else if (step.Kind == StepKind.Refused) refusedN++;
            else if (step.Kind == StepKind.Stop)
            {
                stopError = step.Error ?? "Stopped";
                notTried = total - i - 1;
                if (failed == 0) firstError = stopError;   // nothing failed: the stop reason is also the first error (what the bar shows when nothing was tried)
                break;
            }
            else { failed++; firstError ??= step.Error; }
        }
        if (_cts.IsCancellationRequested)   // Clink is exiting: no clipboard write, no notice
            return new BulkShareResult(uploaded, already, total, links, refusedN, busy, failed, firstError, false, true, stopError, notTried);
        bool notCopied = links.Count > 0 && !TryCopyToClipboard(BulkActions.JoinLinks(links));
        var result = new BulkShareResult(uploaded, already, total, links, refusedN, busy, failed, firstError, false, cancelled, stopError, notTried, notCopied);
        string summary = BulkActions.UploadSummary(result, screenshots);
        string title = notCopied ? "Links not copied" : links.Count > 0 ? "Links copied" : "Nothing uploaded";
        Say(summary);
        if (BulkNoticeSink is { } sink) sink(title, summary);
        else
        {
            try { Toasts.ShowNotice(title, summary); }
            catch (Exception ex) { Log.Warn("bulk upload: notice: " + ex.Message); }
        }
        return result;
    }

    /// <summary>The bulk save's error when the folder the copies go to disappeared in the middle (a USB drive unplugged, a share dropped).</summary>
    internal const string FolderGoneText = "The folder is no longer there";

    private static bool FolderStillThere(string folder)
    {
        try { return Directory.Exists(folder); }
        catch { return false; }
    }

    /// <summary>A copy failed because the target folder is gone (not the source file): the bulk save stops.</summary>
    private sealed class FolderGoneException(Exception inner) : IOException(FolderGoneText, inner);

    /// <summary>Why the folder cannot take copies (null = it can): it must exist and lie outside Clink's clips folder.</summary>
    private string? FolderProblem(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "That folder is not there";
        }
        catch (Exception) { return "That folder is not there"; }
        // the folder judged as a file inside it (a name no clip has), so the same rule as a single save applies
        return SaveCopy.TargetProblem(Path.Combine(folder, ".clink-folder-check"), Path.Combine(Path.GetTempPath(), ".clink-folder-source"), Settings.ClipsFolder);
    }

    private string? PickSaveFolder(Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Save copies of the clips to",
            InitialDirectory = SaveCopy.InitialFolder(Settings.SaveCopyFolder, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        bool? ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FolderName : null;
    }

    /// <summary>Reports on the thread that reports (unlike <see cref="Progress{T}"/>, which posts): the caller keeps the order.</summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
