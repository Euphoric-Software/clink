using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Clink.Native;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Screenshots.Sources;
using Clink.Services;
using Clink.UI;

namespace Clink.Screenshots;

internal enum ScreenshotTrigger { Hotkey, Tray, Cli }

/// <summary>"none" | "before" | "after": what happens after the region is picked. Only "none" (save at once) exists in this version.</summary>
internal static class ScreenshotModes
{
    public const string None = "none", Before = "before", After = "after";
    public static readonly string[] All = { None, Before, After };

    /// <summary>The stored mode, lower-cased; anything unknown or blank is <see cref="None"/>.</summary>
    public static string Normalize(string? mode)
    {
        string m = (mode ?? "").Trim().ToLowerInvariant();
        return All.Contains(m) ? m : None;
    }
}

/// <summary>What the "Screenshot saved" toast says about the clipboard.</summary>
internal readonly record struct ScreenshotToastState(bool Copied, bool ClipboardFailed);

/// <summary>A committed region: the rectangle (virtual-screen pixels), the monitor that holds most of it (<see cref="SelectionMath.MonitorIndexOf"/>, an index into <see cref="FrozenDesktop.Monitors"/>) and how it was made.</summary>
internal sealed record RegionPickResult(RectI Rect, int MonitorIndex, PickOutcome HowEnded);

/// <summary>Shows the region picker over a frozen desktop and returns the region chosen, or null when the capture was cancelled.</summary>
internal interface IRegionPicker
{
    /// <summary>
    /// UI thread. Shows the overlay and completes when the selection is COMMITTED, with the overlay still on screen: the caller owns
    /// closing it (<see cref="Close"/>), so a later version can go on editing inside it. Completes with null when the capture was
    /// cancelled (Esc, right-click, <see cref="Cancel"/>, <paramref name="ct"/>, a change of the monitor layout, no input for a while,
    /// a window closed from outside); the overlay has then closed itself. At once with null when <paramref name="ct"/> is already cancelled.
    /// </summary>
    Task<RegionPickResult?> PickAsync(FrozenDesktop desktop, bool loupe, CancellationToken ct);
    /// <summary>Cancels the pick that is on screen (it completes with null and closes); nothing when there is none.</summary>
    void Cancel();
    /// <summary>Closes the overlay of the last pick (after a commit; harmless when it is closed already).</summary>
    void Close();
}

/// <summary>The toasts the screenshot subsystem shows (<see cref="Toasts"/>; a test records them instead).</summary>
internal interface IScreenshotToasts
{
    void ShowNotice(string title, string text, bool paused = false);
    void ShowProblem(string title, string text);
    void ShowScreenshot(ScreenshotEntry entry, ScreenshotToastState state);
}

/// <summary>What a saved screenshot is: the entry (in the history) and the encoded PNG (also for the clipboard).</summary>
internal sealed record ScreenshotSaveResult(ScreenshotEntry Entry, byte[] Png);

/// <summary>
/// The screenshot subsystem's coordinator (UI thread): hotkey / tray / CLI -> frozen desktop on a worker -> region picker -> crop ->
/// PNG on a worker -> history, clipboard, toast. Separate from the clip pipeline: own folder, own history, never a ClipEntry.
/// What can be tested without a screen is static (<see cref="SaveToDisk"/>, <see cref="FolderFor"/>, <see cref="Subtitle"/>).
/// </summary>
internal sealed class ScreenshotService
{
    internal static readonly TimeSpan TrayDelay = TimeSpan.FromMilliseconds(250);   // the tray menu fades out and is not capture-excluded
    private static readonly object s_nameGate = new();   // SaveToDisk: choosing a free name and taking it

    private readonly Settings _settings;
    private readonly IScreenshotToasts _toasts;
    private readonly IRegionPicker _picker;
    private readonly IReadOnlyList<IScreenshotSource> _sources;
    private readonly Func<IReadOnlyList<MonitorInfo>> _monitors;
    private readonly Func<string?> _game;
    private readonly Func<MonitorInfo?> _recordedMonitor;
    private readonly IScreenshotEditor? _editor;
    private ScreenshotHistory? _history;
    private string? _historyFolder;
    private bool _busy;
    private CancellationTokenSource? _pick;   // the capture under way; a second trigger cancels it

    /// <param name="monitors">The monitors to copy (default: <see cref="Native.Monitors.Enumerate"/>); a test passes made-up ones with <paramref name="sources"/>.</param>
    /// <param name="editor">Opens the editor window (mode "after", edit last, the toast's Edit); null = no editor: "after" then saves at once and edit-last says so.</param>
    public ScreenshotService(Settings settings, IScreenshotToasts toasts, IRegionPicker picker, Func<string?> game, Func<MonitorInfo?> recordedMonitor,
        IReadOnlyList<IScreenshotSource>? sources = null, Func<IReadOnlyList<MonitorInfo>>? monitors = null, IScreenshotEditor? editor = null)
    {
        _settings = settings;
        _toasts = toasts;
        _picker = picker;
        _game = game;
        _recordedMonitor = recordedMonitor;
        _sources = sources ?? new IScreenshotSource[] { new GdiScreenshotSource() };
        _monitors = monitors ?? (() => Native.Monitors.Enumerate());
        _editor = editor;
    }

    /// <summary>The last picture an editor saved (document + file + the file's time), for "edit last". In memory only: after a restart the flattened PNG is opened instead.</summary>
    internal LastEditInfo? LastEdit { get; set; }

    /// <summary>A capture is in progress (frozen desktop being taken or the overlay up).</summary>
    public bool IsBusy => _busy;

    /// <summary>Where screenshots go: <see cref="Settings.ScreenshotsFolder"/>, else a <c>Screenshots</c> folder inside the clips folder (the library and the archive look at the clips folder's own files only).</summary>
    public static string FolderFor(Settings s) =>
        !string.IsNullOrWhiteSpace(s.ScreenshotsFolder) ? s.ScreenshotsFolder.Trim() : System.IO.Path.Combine(s.ClipsFolder, "Screenshots");

    private ScreenshotHistory History
    {
        get
        {
            string folder = FolderFor(_settings);
            if (_history == null || !string.Equals(_historyFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                _history = new ScreenshotHistory(ScreenshotHistory.DefaultFile, folder);
                _historyFolder = folder;
                try { _history.NoteRoot(folder); }   // an earlier folder stays openable after the setting changes
                catch (Exception ex) { Log.Warn("screenshot: folder not recorded: " + ex.Message); }
            }
            return _history;
        }
    }

    // ------------------------------------------------------------------ capture

    /// <summary>
    /// The screenshot hotkey / tray item / <c>--screenshot</c>. UI thread. Fire and forget: every failure is reported to the user
    /// here and nothing is thrown.
    /// </summary>
    public async void CaptureRegion(ScreenshotTrigger trigger)
    {
        try { await CaptureRegionAsync(trigger); }
        catch (Exception ex)
        {
            Log.Error("screenshot", ex);
            _toasts.ShowProblem("Screenshot failed", ex.Message);
        }
    }

    internal async Task CaptureRegionAsync(ScreenshotTrigger trigger)
    {
        if (!_settings.ScreenshotsEnabled)
        {
            Log.Info($"screenshot: ignored ({trigger}): screenshot tools are disabled");
            _toasts.ShowNotice("Screenshots are off", DisabledText);
            return;
        }
        if (_busy)
        {
            // A second press while a capture is under way CANCELS it (as ShareX does): the one way out that always works, also when
            // the overlay cannot take the keyboard (over a game that keeps the foreground) and Esc never reaches it.
            Log.Info($"screenshot: {trigger} while a capture is under way: cancelled");
            _pick?.Cancel();
            _picker.Cancel();
            return;
        }
        _busy = true;
        var cts = new CancellationTokenSource();
        _pick = cts;
        RegionPickResult? picked = null;
        FrozenDesktop? desktop = null;
        bool shown = false;
        var total = Stopwatch.StartNew();
        try
        {
            if (trigger == ScreenshotTrigger.Tray) await Task.Delay(TrayDelay);
            if (!cts.IsCancellationRequested)
            {
                // Before any window of ours exists: the copy of the screen is taken on a worker (GDI is thread-agnostic).
                var monitors = _monitors();
                desktop = await Task.Run(() => FrozenDesktop.CaptureAll(monitors, _sources));
                long captureMs = total.ElapsedMilliseconds;
                Log.Info($"screenshot: {trigger}, {desktop.Monitors.Count} monitor(s), virtual {desktop.VirtualBounds}, capture {captureMs} ms");
                if (!cts.IsCancellationRequested)
                {
                    shown = true;
                    picked = await _picker.PickAsync(desktop, _settings.ScreenshotLoupe, cts.Token);
                }
            }
        }
        catch (ScreenshotCaptureException ex)
        {
            Log.Warn("screenshot: " + ex.Message);
            _toasts.ShowProblem("Screenshot not taken", ex.Message);
            return;
        }
        finally
        {
            // The picker hands the overlay over still open after a commit (see IRegionPicker.PickAsync): nothing is edited in it yet.
            if (shown)
            {
                try { _picker.Close(); }
                catch (Exception ex) { Log.Warn("screenshot: overlay close: " + ex.Message); }
            }
            _pick = null;
            cts.Dispose();
            _busy = false;
        }

        if (desktop == null || picked is not { } pick || pick.Rect.IsEmpty)
        {
            Log.Info("screenshot: cancelled");
            return;
        }
        // Mode "after": the editor first (one editor at a time: a capture made while it is open is saved as it is, the toast has an Edit button).
        if (ScreenshotModes.Normalize(_settings.ScreenshotMode) == ScreenshotModes.After && _editor is { IsOpen: false })
            await EditAfterAsync(Take(ref desktop), pick);   // not kept by this method while the editor is open (~100 MB for 3 x 4K)
        else
            await CommitAsync(desktop, pick.Rect);
    }

    private static FrozenDesktop Take(ref FrozenDesktop? desktop)
    {
        var d = desktop!;
        desktop = null;
        return d;
    }

    // ------------------------------------------------------------------ the editor

    /// <summary>What one editor session needs to remember between its saves.</summary>
    private sealed class EditContext
    {
        public string? Game;
        /// <summary>The file this picture was opened from (history's <c>EditedFrom</c>); the original is never overwritten.</summary>
        public string? EditedFrom;
        public string? SavedPath;
        public DocumentState? SavedDoc;
        public ScreenshotEntry? Entry;
        public System.Windows.Media.Imaging.BitmapSource? Bitmap;
        public byte[]? Png;
    }

    /// <summary>The overlay is closed: compose the crop and open the editor on it. Nothing is saved until Done / Save.</summary>
    internal async Task EditAfterAsync(FrozenDesktop desktop, RegionPickResult pick)
    {
        if (_settings.ClickSound) ClickSound.Play(_settings.ClickVolume);
        string? game = GameFor(pick.Rect);
        BgraImage picture;
        bool black;
        try
        {
            (picture, black) = await Task.Run(() => (desktop.Compose(pick.Rect), WarnBlack(desktop, pick.Rect, game != null)));
        }
        catch (Exception ex)
        {
            Log.Error("screenshot: compose", ex);
            _toasts.ShowProblem("Screenshot not taken", ex.Message);
            return;
        }
        // An editor may have been opened (edit last, the toast's Edit) while the picture was composed: one at a time, so this one is saved as it is.
        // (or a second capture began: its overlay must not be taken over by this editor)
        if (_editor!.IsOpen || _busy)
        {
            Log.Info("screenshot: an editor opened or another capture began meanwhile: saving the capture as it is");
            await CommitAsync(desktop, pick.Rect, playSound: false);   // the click sounded already
            return;
        }
        if (black)
            _toasts.ShowProblem("Screenshot is black", "The picture is all black. Is the game in exclusive fullscreen? Use borderless windowed mode.");
        var monitor = pick.MonitorIndex >= 0 && pick.MonitorIndex < desktop.Monitors.Count ? desktop.Monitors[pick.MonitorIndex].Monitor : null;
        var document = DocumentState.Create(picture);
        desktop = null!;   // the frozen desktop is not needed any more: only the crop (in the document) is edited
        await OpenEditorAsync(document, new EditContext { Game = game }, "New screenshot", isNew: true, monitor);
    }

    private async Task OpenEditorAsync(DocumentState doc, EditContext ctx, string title, bool isNew, MonitorInfo? monitor)
    {
        var editor = _editor!;
        var request = new EditorRequest
        {
            Document = doc,
            Title = title,
            IsNewCapture = isNew,
            Monitor = monitor ?? _recordedMonitor(),
            Options = _settings.ScreenshotEditor,
            PersistOptions = o =>
            {
                _settings.ScreenshotEditor = o;
                try { _settings.Save(); } catch (Exception ex) { Log.Warn("screenshot editor: options not saved: " + ex.Message); }
            },
            SaveAsync = (image, d, finish) => SaveEditedAsync(ctx, image, d, finish),
        };
        Log.Info($"screenshot: editor opened ({title}, {doc.Base.Width}x{doc.Base.Height})");
        bool failed = false;
        try
        {
            if (!await editor.EditAsync(request))
            {
                Log.Info("screenshot: editor not opened: another one is open");
                _toasts.ShowNotice("Editor already open", "Finish or close the open screenshot editor first");
                failed = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("screenshot: editor", ex);
            _toasts.ShowProblem("Screenshot editor failed", ex.Message);
            failed = true;
        }
        // A new capture that never reached an editor is not lost: it is saved as it is.
        if (failed && isNew)
        {
            Log.Info("screenshot: the editor did not open: saving the capture as it is");
            await SaveEditedAsync(ctx, doc.Base, doc, finish: true);
        }
        if (!failed) Log.Info("screenshot: editor closed");
    }

    /// <summary>
    /// The editor's Save (<paramref name="finish"/> false) and Done (true). A picture is written to a NEW file the first time (the
    /// original of an edited screenshot stays); later saves of the same session replace that file. Done also copies the image to the
    /// clipboard and shows the toast, once. Done on a document that is already saved just does that last part. Never throws.
    /// </summary>
    private async Task<bool> SaveEditedAsync(EditContext ctx, BgraImage image, DocumentState doc, bool finish)
    {
        try
        {
            if (ctx.SavedPath == null || !ReferenceEquals(ctx.SavedDoc, doc))
            {
                var t = Stopwatch.StartNew();
                string folder = FolderFor(_settings);
                var history = History;
                var bitmap = image.ToBitmapSource();   // once: the PNG is encoded from it and the clipboard gets the same frozen instance
                var saved = await Task.Run(() => SaveToDisk(image, folder, ctx.Game, DateTime.Now, history, bitmap, ctx.SavedPath, ctx.EditedFrom));
                ctx.Entry = saved.Entry;
                ctx.SavedPath = saved.Entry.Path;
                ctx.SavedDoc = doc;
                ctx.Bitmap = bitmap;
                ctx.Png = saved.Png;
                LastEdit = new LastEditInfo(doc, saved.Entry.Path, SafeWriteTime(saved.Entry.Path));
                Log.Info($"screenshot: editor saved {saved.Entry.Path} ({saved.Entry.Width}x{saved.Entry.Height}, {saved.Entry.Size / 1024} KB) in {t.ElapsedMilliseconds} ms");
            }
            if (finish && ctx.Entry != null && ctx.Bitmap != null && ctx.Png != null)
            {
                bool copied = false, failed = false;
                if (_settings.ScreenshotCopyImage)
                {
                    copied = ScreenshotClipboard.Copy(ctx.Bitmap, ctx.Png);
                    failed = !copied;
                }
                _toasts.ShowScreenshot(ctx.Entry, new ScreenshotToastState(copied, failed));
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("screenshot: editor save", ex);
            _toasts.ShowProblem("Screenshot not saved", ex.Message);
            return false;
        }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    // ------------------------------------------------------------------ edit last / edit a file

    /// <summary>The "edit last screenshot" hotkey / tray item / <c>--screenshot-edit-last</c>. UI thread. Every failure is reported here; nothing is thrown.</summary>
    public async void EditLast()
    {
        try { await EditLastAsync(); }
        catch (Exception ex)
        {
            Log.Error("screenshot: edit last", ex);
            _toasts.ShowProblem("Screenshot not opened", ex.Message);
        }
    }

    /// <summary>
    /// Brings the open editor forward; else reopens the document of the last editor save when its file is still the newest and
    /// unchanged (the objects are still editable); else opens the newest screenshot of the history as a flattened picture.
    /// </summary>
    internal async Task EditLastAsync()
    {
        if (!CanEdit()) return;
        if (_editor!.IsOpen) { Log.Info("screenshot: edit last: the editor is open: brought forward"); _editor.Activate(); return; }
        ScreenshotEntry? newest = History.Entries.FirstOrDefault(e => IsOpenableEntry(e.Path));
        if (newest == null)
        {
            Log.Info("screenshot: edit last: nothing to edit (no openable screenshot in the history)");
            _toasts.ShowNotice("No screenshot to edit", "Take a screenshot first, then edit it here");
            return;
        }
        await EditFileAsync(newest.Path, explicitPath: false);
    }

    /// <summary>The toast's Edit button / <c>--screenshot-edit</c>. UI thread; reports failures itself.</summary>
    public async void EditFile(string path, bool explicitPath = false)
    {
        try { await EditFileAsync(path, explicitPath); }
        catch (Exception ex)
        {
            Log.Error("screenshot: edit file", ex);
            _toasts.ShowProblem("Screenshot not opened", ex.Message);
        }
    }

    private bool CanEdit()
    {
        if (!_settings.ScreenshotsEnabled)
        {
            _toasts.ShowNotice("Screenshots are off", DisabledText);
            return false;
        }
        if (_editor == null)
        {
            _toasts.ShowNotice("Editor not available", "The screenshot editor could not be started");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Opens a PNG in the editor. <paramref name="explicitPath"/> (given on the command line by the user) may be anywhere; otherwise
    /// the file has to be one of this app's screenshots (<see cref="IsOpenableEntry"/>). If the file is the one the last editor
    /// session saved and it has not changed, that session's document is reopened instead (objects still editable). The edit is
    /// always saved as a new file.
    /// </summary>
    internal async Task EditFileAsync(string path, bool explicitPath)
    {
        if (!CanEdit()) return;
        if (_editor!.IsOpen)
        {
            Log.Info("screenshot: edit: the editor is open already: brought forward, not opened a second time");
            _editor.Activate();
            return;
        }
        bool allowed = explicitPath ? IsExplicitEditable(path) : IsOpenableEntry(path);
        if (!allowed)
        {
            Log.Warn("screenshot: not opened: not a readable .png" + (explicitPath ? "" : " in the screenshots folder"));
            _toasts.ShowProblem("Screenshot not opened", explicitPath ? "That is not a PNG file that can be read" : "That file is not a screenshot of this app, or it is gone");
            return;
        }
        string full = System.IO.Path.GetFullPath(path);
        string name = System.IO.Path.GetFileName(full);
        string? game = History.Find(full)?.Game;
        var live = LastEdit;
        if (live != null && string.Equals(live.Path, full, StringComparison.OrdinalIgnoreCase) && SafeWriteTime(full) == live.FileTimeUtc)
        {
            Log.Info("screenshot: edit: reopening the live document of " + name);
            await OpenEditorAsync(live.Document, new EditContext { Game = game, EditedFrom = full }, name, isNew: false, null);
            return;
        }
        BgraImage? image;
        try { image = await Task.Run(() => PngFile.Decode(full)); }
        catch (Exception ex)
        {
            Log.Warn("screenshot: edit: could not read " + name + ": " + ex.Message);
            image = null;
        }
        if (image == null)
        {
            _toasts.ShowProblem("Screenshot not opened", "The file could not be read as a picture, or it is too large");
            return;
        }
        // Somebody may have opened an editor while the file was being read.
        if (_editor.IsOpen) { Log.Info("screenshot: edit: an editor opened meanwhile: brought forward"); _editor.Activate(); return; }
        await OpenEditorAsync(DocumentState.Create(image), new EditContext { Game = game, EditedFrom = full }, name, isNew: false, null);
    }

    /// <summary>
    /// The folders whose PNGs the app may open as screenshots: the current screenshots folder and every folder screenshots were
    /// saved to (recorded roots, not the folders of whatever the history file lists). A network (UNC) folder counts only when the
    /// current screenshots folder is one itself: a tampered history cannot make the app reach out to a server.
    /// </summary>
    internal IReadOnlyList<string> KnownFolders()
    {
        string current = FolderFor(_settings);
        var list = new List<string> { current };
        try
        {
            bool uncOk = IsUnc(current);
            list.AddRange(History.Roots.Where(r => uncOk || !IsUnc(r)));
        }
        catch (Exception ex) { Log.Warn("screenshot: folders: " + ex.Message); }
        return list;
    }

    private static bool IsUnc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string p = path.Trim();
        return p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>A history entry (or the toast's file) that may be opened: see <see cref="IsOpenableFile"/> for the current and earlier screenshot folders.</summary>
    internal bool IsOpenableEntry(string? path) => IsOpenableFile(path, KnownFolders());

    /// <summary>
    /// A PNG given explicitly by the user on the command line: fully qualified, exactly <c>.png</c>, no alternate data stream,
    /// an existing regular file (not a directory or a link). Anywhere on disk.
    /// </summary>
    internal static bool IsExplicitEditable(string? path) => IsOpenableFile(path, null);

    /// <summary>
    /// The overlay is closed: cuts the picture out of the frozen desktop, checks it for black and encodes and saves it on a worker
    /// (a 3 x 4K composite is ~100 MB: never on the UI thread), then (UI thread) clipboard, sound and toast.
    /// </summary>
    internal async Task CommitAsync(FrozenDesktop desktop, RectI rect, bool playSound = true)
    {
        if (playSound && _settings.ClickSound) ClickSound.Play(_settings.ClickVolume);
        var t = Stopwatch.StartNew();
        BitmapSource bitmap;
        ScreenshotSaveResult saved;
        bool black;
        try
        {
            string? game = GameFor(rect);
            string folder = FolderFor(_settings);
            var history = History;
            (bitmap, saved, black) = await Task.Run(() =>
            {
                var picture = desktop.Compose(rect);
                bool looksBlack = WarnBlack(desktop, rect, game != null);
                var made = picture.ToBitmapSource();   // once: the PNG is encoded from it and the clipboard (UI thread) gets the same frozen instance
                var result = SaveToDisk(picture, folder, game, DateTime.Now, history, made);
                return (made, result, looksBlack);
            });
        }
        catch (Exception ex)
        {
            Log.Error("screenshot: save", ex);
            _toasts.ShowProblem("Screenshot not saved", ex.Message);
            return;
        }
        long encodeMs = t.ElapsedMilliseconds;
        bool copied = false, failed = false;
        if (_settings.ScreenshotCopyImage)
        {
            copied = ScreenshotClipboard.Copy(bitmap, saved.Png);
            failed = !copied;
        }
        Log.Info($"screenshot: saved {saved.Entry.Path} ({saved.Entry.Width}x{saved.Entry.Height}, {saved.Entry.Size / 1024} KB), crop+encode+write {encodeMs} ms, clipboard {(copied ? "ok" : _settings.ScreenshotCopyImage ? "failed" : "off")}");
        if (black)
            _toasts.ShowProblem("Screenshot is black", "Saved, but the picture is all black. Is the game in exclusive fullscreen? Use borderless windowed mode.");
        else
            _toasts.ShowScreenshot(saved.Entry, new ScreenshotToastState(copied, failed));
    }

    internal const string DisabledText = "Screenshot tools are disabled in Settings";

    /// <summary>The detected game, when the picture overlaps the monitor that is being recorded; otherwise null (the name is then "Screenshot"). See <see cref="SelectionMath.GameFor"/>.</summary>
    private string? GameFor(RectI rect)
    {
        var mon = _recordedMonitor();
        return SelectionMath.GameFor(_game(), mon == null ? null : new RectI(mon.Left, mon.Top, mon.Width, mon.Height), rect);
    }

    // ------------------------------------------------------------------ pure-ish pieces

    /// <summary>
    /// The "Screenshot is black" warning (exclusive fullscreen) replaces the normal toast only when the picture looks black AND either
    /// every monitor under it is black as a whole or the detected game is on it. A dark crop of a lit desktop (a black terminal, a
    /// letterbox bar) gets the normal toast with its Open / Folder buttons.
    /// </summary>
    internal static bool WarnBlack(FrozenDesktop desktop, RectI rect, bool gameOnIt) =>
        desktop.LooksBlack(rect) && (gameOnIt || desktop.WholeMonitorsLookBlack(rect));

    /// <summary>
    /// The toast's Open button may hand <paramref name="path"/> to the shell only when it is a .png inside <paramref name="folder"/>
    /// (the screenshots folder): history entries are read from a file and flow through here too, and a shell-executed path is a
    /// program start.
    /// </summary>
    internal static bool IsOpenable(string? path, string folder) => IsOpenable(path, new[] { folder });

    /// <summary>
    /// The path rule (no disk access): fully qualified, extension exactly <c>.png</c>, no <c>:</c> after the drive letter (an
    /// alternate data stream such as <c>a.png:evil.exe</c>), no device-path prefix, and inside one of <paramref name="folders"/>
    /// (the current screenshots folder and every earlier one); <c>null</c> folders = anywhere (only for a path the user typed).
    /// </summary>
    internal static bool IsOpenable(string? path, IEnumerable<string>? folders)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return false;
            string full = System.IO.Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            if (full.IndexOf(':', 2) >= 0 || (full.Length >= 2 && full[1] == ':' && !char.IsAsciiLetter(full[0]))) return false;
            if (!string.Equals(System.IO.Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase)) return false;
            if (folders == null) return true;
            foreach (string folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !System.IO.Path.IsPathFullyQualified(folder)) continue;
                string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// <see cref="IsOpenable(string?, IEnumerable{string}?)"/> plus the disk: the file exists and is a regular file (not a directory
    /// and not a symbolic link / junction), so the history file cannot make the app open something it was not meant to.
    /// </summary>
    internal static bool IsOpenableFile(string? path, IEnumerable<string>? folders)
    {
        if (!IsOpenable(path, folders)) return false;
        try
        {
            var info = new FileInfo(System.IO.Path.GetFullPath(path!));
            return info.Exists && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Encodes the image, writes it as <c>folder\{name}.png</c> (via a .partial file) and records it in the history. Picks the next
    /// free <c>" (n)"</c> name, again if another screenshot took it in the meantime. Worker thread or UI thread; throws on a disk problem.
    /// </summary>
    internal static ScreenshotSaveResult SaveToDisk(BgraImage image, string folder, string? game, DateTime when, ScreenshotHistory history, BitmapSource? bitmap = null,
        string? overwritePath = null, string? editedFrom = null)
    {
        if (image.Width <= 0 || image.Height <= 0) throw new InvalidOperationException("The picture is empty");
        Directory.CreateDirectory(folder);
        byte[] png = PngFile.Encode(bitmap ?? image.ToBitmapSource());
        string baseName = ScreenshotNaming.BaseName(game, when);
        string path = "";
        // The bytes go to a file of their own first, outside the lock (a big PNG must not hold the other saves up).
        // A file this editor session wrote before (overwritePath) is replaced; it is never the original of an edited screenshot.
        string partialDir = overwritePath != null ? System.IO.Path.GetDirectoryName(overwritePath) ?? folder : folder;
        string partial = System.IO.Path.Combine(partialDir, baseName + "." + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(png, 0, png.Length);
                fs.Flush(true);
            }
            // Picking the name and moving the file there is one step for this process (two saves in the same second take " (1)", " (2)"
            // in turn); another process (a --dev instance on the same folder) can still take a name in between: then the next one.
            lock (s_nameGate)
            {
                if (overwritePath != null)
                {
                    File.Move(partial, overwritePath, overwrite: true);
                    path = overwritePath;
                }
                else for (int attempt = 0; ; attempt++)
                {
                    path = ScreenshotNaming.UniquePath(folder, baseName);
                    try { File.Move(partial, path, overwrite: false); break; }
                    catch (IOException) when (attempt < 5 && File.Exists(path)) { }   // the name was taken between the check and the move
                }
            }
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
        var entry = new ScreenshotEntry
        {
            Path = path, Created = when, Width = image.Width, Height = image.Height, Size = png.Length,
            Game = string.IsNullOrWhiteSpace(game) ? null : game,
            EditedFrom = editedFrom,
        };
        try { history.Add(entry); }
        catch (Exception ex) { Log.Warn("screenshot: history not updated: " + ex.Message); }   // the PNG is on disk: that is the result
        return new ScreenshotSaveResult(entry, png);
    }

    /// <summary>The toast's second line: "1280 x 720 · copied to clipboard" / "· clipboard busy, not copied" / just the size when copying is off.</summary>
    internal static string Subtitle(ScreenshotEntry e, ScreenshotToastState s)
    {
        string size = $"{e.Width} x {e.Height}";
        if (s.Copied) return size + " · copied to clipboard";
        if (s.ClipboardFailed) return size + " · clipboard busy, not copied";
        return size;
    }
}
