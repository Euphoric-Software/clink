using System.IO;
using System.Windows;
using Clink.Screenshots;
using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>How a "Save a copy…" ended.</summary>
internal enum SaveOutcome { Saved, Cancelled, Refused, Failed }

/// <summary>What one <see cref="AppController.SaveCopyAsync"/> did: how it ended, the file it wrote (Saved only) and why not (Refused / Failed / Cancelled).</summary>
internal sealed record SaveResult(SaveOutcome Outcome, string? SavedTo, string? Error);

internal sealed partial class AppController
{
    private readonly List<Task> _saves = new();   // the copies running on workers (guarded by itself): Dispose waits a moment for them to remove their part files

    /// <summary>Test seam: picks the target file instead of the save dialog (null = the user cancelled).</summary>
    internal Func<ClipEntry, string?>? SaveTargetPicker { get; set; }

    private const string Gone = "The clip's file is not there any more";   // (an exception message: stays English, and is what ends up in the log)

    /// <summary>The clip's file vanished before the copy began (message <see cref="Gone"/>, English for the log); the user is told in their language.</summary>
    private sealed class ClipFileGoneException() : FileNotFoundException(Gone);

    /// <summary>What the user is told about a failed copy: the clip's file being gone in their language, else the exception's message.</summary>
    private static object CopyErrorOf(Exception ex) => ex is ClipFileGoneException ? Note.Of("The clip's file is not there any more") : ex.Message;

    /// <summary>
    /// Saves a copy of the clip's file where the user picks. The copy is not added to the library and the entry is not changed.
    /// Runs the copy off the UI thread; leaving Clink cancels it and removes the partial file.
    /// </summary>
    public async Task<SaveResult> SaveCopyAsync(ClipEntry entry, Window? owner, IProgress<double>? progress)
    {
        string? target = SaveTargetPicker is { } picker ? picker(entry) : PickSaveTarget(entry, owner);
        if (string.IsNullOrWhiteSpace(target)) return new SaveResult(SaveOutcome.Cancelled, null, null);
        string source = entry.Path;
        if (SaveCopy.TargetProblem(target, source, Settings.ClipsFolder) is { } problem)
        {
            SayT("Not saved: {reason}", ("reason", problem));   // (SaveCopy.TargetProblem words it: slice S2)
            return new SaveResult(SaveOutcome.Refused, null, problem);
        }
        try
        {
            SayT("Saving a copy…");
            var copy = Task.Run(async () =>
            {
                if (!File.Exists(source)) throw new ClipFileGoneException();   // off the UI thread: an archived clip may sit on a dropped network share
                await SaveCopy.CopyAsync(source, target, progress, _cts.Token);
            });
            lock (_saves) _saves.Add(copy);
            try { await copy; }
            finally { lock (_saves) _saves.Remove(copy); }
        }
        catch (OperationCanceledException)
        {
            SayT("Save cancelled");
            return new SaveResult(SaveOutcome.Cancelled, null, "Cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("save a copy", ex);
            var error = CopyErrorOf(ex);
            SayT("Save failed: {error}", ("error", error));
            return new SaveResult(SaveOutcome.Failed, null, error is Note shown ? shown.Shown : ex.Message);
        }
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(target));
            if (folder != null && ScreenshotService.IsOnLocalFixedDrive(target)
                && !string.Equals(folder, Settings.SaveCopyFolder, StringComparison.OrdinalIgnoreCase))
            {
                Settings.SaveCopyFolder = folder;
                Settings.Save();
            }
        }
        catch (Exception ex) { Log.Warn("save a copy: could not remember the folder: " + ex.Message); }
        SayT("Saved a copy to {path}", ("path", Note.Ltr(target)));
        return new SaveResult(SaveOutcome.Saved, target, null);
    }

    /// <summary>Quit: gives the running copies a moment (at most <paramref name="limit"/>) to see the cancel and remove their part files.</summary>
    private void WaitForSaves(TimeSpan limit)
    {
        Task[] running;
        lock (_saves) running = _saves.ToArray();
        if (running.Length == 0) return;
        try { Task.WaitAll(running, limit); }
        catch (Exception) { }   // a failed or cancelled copy has already cleaned up
    }

    private string? PickSaveTarget(ClipEntry entry, Window? owner)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L.T("MP4 video (*.mp4)") + "|*.mp4|" + L.T("All files (*.*)") + "|*.*",
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true,
            Title = L.T("Save a copy of the clip"),
            FileName = SaveCopy.DefaultFileName(entry.Path),
            InitialDirectory = SaveCopy.InitialFolder(Settings.SaveCopyFolder, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        TestGuard.CheckLaunch("the save-a-copy dialog");
        bool? ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    /// <summary>
    /// "Share file…": opens the share window with the clip's file to drag into Discord, a browser or a folder (what Share does
    /// without a Copium token). <see cref="LastShareOutcome"/> is <see cref="ShareOutcome.FileShareOpened"/>, or
    /// <see cref="ShareOutcome.WindowFailed"/> (with <see cref="ShareError"/>) when the window could not open. True when it opened.
    /// </summary>
    public bool ShareFile(ClipEntry entry)
    {
        ShareRefusal = null;
        ShareError = null;
        if (!OpenShareWindow(entry))
        {
            var failed = Note.Of("Could not open the share window");   // (AppController.ShareWindowFailedText is the same key, for the clip list)
            ShareError = failed.Shown;
            Say(failed);
            LastShareOutcome = ShareOutcome.WindowFailed;
            return false;
        }
        SayT("Drag the clip where you want to share it");
        LastShareOutcome = ShareOutcome.FileShareOpened;
        return true;
    }
}
