using System.IO;
using System.Windows;
using Clink.Screenshots;
using Clink.UI;

namespace Clink.Services;

/// <summary>How a bulk "Save copies…" ended: how many files were written (of <paramref name="Total"/>, every file asked for), where, the first error, whether the user cancelled, how many files could not be reached (missing source; not part of the first error) and how many were skipped as busy when their turn came (<paramref name="Busy"/>).</summary>
internal sealed record BulkSaveResult(int Saved, int Total, string? Folder, string? FirstError, bool Cancelled, int Unreachable = 0, int Busy = 0);

internal sealed partial class AppController
{
    private ShareFilesWindow? _shareFilesWindow;   // the open multi-file share window (UI thread): one at a time

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
