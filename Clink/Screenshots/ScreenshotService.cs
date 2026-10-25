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

/// <summary>"none" (save at once) | "before" (edit on the screen, inside the capture overlay) | "after" (edit in the editor window): what happens after the region is picked.</summary>
internal static class ScreenshotModes
{
    public const string None = "none", Before = "before", After = "after";
    public static readonly string[] All = { None, Before, After };

    /// <summary>The "After capture" combo's items (stored value, label, tooltip), in the order shown; the settings tab fills the combo from it.</summary>
    public static readonly (string Value, string Label, string Tip)[] Choices =
    {
        (None, "Save only", "Save the picture at once; the notification has an Edit button"),
        (After, "Open editor", "Open the editor on the picture first; Done saves it"),
        (Before, "Edit on screen", "Edit right on the screen before saving: draw on the chosen area, then Enter or Done saves it. Esc steps back (drops a drag, deselects, returns to Select) and then throws it away. An area over several monitors opens the editor window instead"),
    };

    /// <summary>The stored mode, lower-cased; anything unknown or blank is <see cref="None"/>.</summary>
    public static string Normalize(string? mode)
    {
        string m = (mode ?? "").Trim().ToLowerInvariant();
        return All.Contains(m) ? m : None;
    }
}

/// <summary>What the "Screenshot saved" toast says about the clipboard, and whether it offers an Upload button (a token is set and no automatic upload follows).</summary>
internal readonly record struct ScreenshotToastState(bool Copied, bool ClipboardFailed, bool CanUpload = false);

/// <summary>
/// A committed region: the rectangle (virtual-screen pixels), the monitor that holds most of it (<see cref="SelectionMath.MonitorIndexOf"/>,
/// an index into <see cref="FrozenDesktop.Monitors"/>) and how it was made. After editing on the screen (mode "before",
/// <see cref="OverlayEditSetup"/>), <see cref="Document"/> is the edited document (base = that monitor's frozen picture, crop = the final
/// crop in monitor pixels) and <see cref="Rect"/> is its crop in virtual-screen pixels. <see cref="SpansMonitors"/>: mode "before" with an
/// area over several monitors, which is not edited in the overlay (the service opens the editor window on the composed crop).
/// </summary>
internal sealed record RegionPickResult(RectI Rect, int MonitorIndex, PickOutcome HowEnded, DocumentState? Document = null, bool SpansMonitors = false);

/// <summary>What the overlay needs to edit on the screen (mode "before"): the editor's tool options (and where to persist them) and the insert-image list. Passing it to <see cref="IRegionPicker.PickAsync"/> turns editing on.</summary>
internal sealed record OverlayEditSetup(
    ScreenshotEditorOptions Options,
    Action<ScreenshotEditorOptions>? PersistOptions,
    Func<(IReadOnlyList<ScreenshotEntry> Entries, IReadOnlyList<string> Exclude)>? Recent);

/// <summary>Shows the region picker over a frozen desktop and returns the region chosen, or null when the capture was cancelled.</summary>
internal interface IRegionPicker
{
    /// <summary>
    /// UI thread. Shows the overlay and completes when the selection is COMMITTED, with the overlay still on screen: the caller owns
    /// closing it (<see cref="Close"/>). Completes with null when the capture was
    /// cancelled (Esc, right-click, <see cref="Cancel"/>, <paramref name="ct"/>, a change of the monitor layout, no input for a while,
    /// a window closed from outside); the overlay has then closed itself. At once with null when <paramref name="ct"/> is already cancelled.
    /// With <paramref name="edit"/> (mode "before") a committed selection inside one monitor is not the end: that monitor's overlay
    /// goes on to edit it, and the pick completes when the user confirms (Enter / Done) with <see cref="RegionPickResult.Document"/> set
    /// (Esc cancels at once, without a question); a selection over several monitors completes with
    /// <see cref="RegionPickResult.SpansMonitors"/> set and no document.
    /// </summary>
    Task<RegionPickResult?> PickAsync(FrozenDesktop desktop, bool loupe, CancellationToken ct, OverlayEditSetup? edit = null);
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
    /// <summary>
    /// An upload of <paramref name="entry"/> begins: the screenshot toast that is still up for it turns into "Uploading…" (one is made
    /// when it is gone). Nothing is shown when the screenshot toast is switched off. The handle reports the end.
    /// </summary>
    IScreenshotUploadToast ShowScreenshotUploading(ScreenshotEntry entry);
}

/// <summary>The toast of one running upload.</summary>
internal interface IScreenshotUploadToast
{
    void Report(double fraction);
    /// <summary>What the toast's Cancel button calls (set by the service for the upload that is running; null when there is nothing to cancel).</summary>
    Action? OnCancel { get; set; }
    /// <summary>Uploaded: the toast shows the link with Copy link / Open / Edit / Folder; <paramref name="linkCopied"/> says the link is on the clipboard.</summary>
    void Succeeded(string link, bool linkCopied);
    /// <summary>The upload ended without a link (failed, cancelled): the toast goes back to its saved state with a Retry button. The reason is a separate problem toast.</summary>
    void Failed();
    /// <summary>The upload was stopped on purpose by the cloud kill switch: the toast goes neutral (saved, no Upload / Retry button).</summary>
    void Declined();
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
    /// <summary>The door to Copium (consent + kill switch). The app passes its own, whose pop-up is real; without one the pop-up is a "Not now" (no window).</summary>
    internal ConsentGate Consent { get; set; }
    private ScreenshotHistory? _history;
    private string? _historyFolder;
    private bool _busy;
    private CancellationTokenSource? _pick;   // the capture under way; a second trigger cancels it

    /// <param name="monitors">The monitors to copy (default: <see cref="Native.Monitors.Enumerate"/>); a test passes made-up ones with <paramref name="sources"/>.</param>
    /// <param name="editor">Opens the editor window (mode "after", edit last, the toast's Edit); null = no editor: "after" then saves at once and edit-last says so.</param>
    public ScreenshotService(Settings settings, IScreenshotToasts toasts, IRegionPicker picker, Func<string?> game, Func<MonitorInfo?> recordedMonitor,
        IReadOnlyList<IScreenshotSource>? sources = null, Func<IReadOnlyList<MonitorInfo>>? monitors = null, IScreenshotEditor? editor = null, ConsentGate? consent = null)
    {
        _settings = settings;
        Consent = consent ?? new ConsentGate(settings) { Notice = (_, title, text) => toasts.ShowNotice(title, text) };
        _toasts = toasts;
        _picker = picker;
        _game = game;
        _recordedMonitor = recordedMonitor;
        _sources = sources ?? new IScreenshotSource[] { new GdiScreenshotSource() };
        _monitors = monitors ?? (() => Native.Monitors.Enumerate());
        _editor = editor;
    }

    // ------------------------------------------------------------------ edit last: how long the live document is kept

    /// <summary>How long the document of the last editor save is kept without an edit-last / editor interaction. It holds what the user cropped out and covered up, so it does not linger.</summary>
    internal static readonly TimeSpan LastEditLifetime = TimeSpan.FromMinutes(15);

    private readonly object _lastEditGate = new();
    private LastEditInfo? _lastEdit;
    private DateTime _lastEditTouched;
    private System.Threading.Timer? _lastEditTimer;
    private Microsoft.Win32.SessionSwitchEventHandler? _sessionHandler;

    /// <summary>
    /// The last picture an editor saved (document + file + the file's time), for "edit last". In memory only: after a restart the
    /// flattened PNG is opened instead. Dropped when the session is locked / switched / signed out, and after
    /// <see cref="LastEditLifetime"/> without an interaction (<see cref="TouchLastEdit"/>).
    /// </summary>
    internal LastEditInfo? LastEdit
    {
        get => Volatile.Read(ref _lastEdit);
        set
        {
            lock (_lastEditGate)
            {
                _lastEdit = value;
                _lastEditTouched = UtcNow();
                if (value != null) ArmLastEdit(); else DisarmLastEdit();
            }
        }
    }

    /// <summary>An edit-last / editor interaction: the live document is kept another <see cref="LastEditLifetime"/>.</summary>
    internal void TouchLastEdit() { lock (_lastEditGate) _lastEditTouched = UtcNow(); }

    private void ArmLastEdit()
    {
        try
        {
            if (_sessionHandler == null)
            {
                _sessionHandler = (_, e) => OnSessionSwitch(e.Reason);
                Microsoft.Win32.SystemEvents.SessionSwitch += _sessionHandler;
            }
            _lastEditTimer ??= new System.Threading.Timer(_ => ExpireLastEditIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        catch (Exception ex) { Log.Warn("screenshot: edit-last guard: " + ex.Message); }
    }

    private void DisarmLastEdit()
    {
        try
        {
            if (_sessionHandler != null) { Microsoft.Win32.SystemEvents.SessionSwitch -= _sessionHandler; _sessionHandler = null; }
            _lastEditTimer?.Dispose();
            _lastEditTimer = null;
        }
        catch (Exception ex) { Log.Warn("screenshot: edit-last guard: " + ex.Message); }
    }

    /// <summary>The Windows session was locked, switched, disconnected or signed out (any <see cref="Microsoft.Win32.SystemEvents.SessionSwitch"/>): the live document goes.</summary>
    internal void OnSessionSwitch(Microsoft.Win32.SessionSwitchReason reason) => ClearLastEdit("the session changed (" + reason + ")");

    /// <summary>Forgets the live document of the last editor save (the session was locked or switched; the timer ran out).</summary>
    internal void ClearLastEdit(string why)
    {
        lock (_lastEditGate)
        {
            if (_lastEdit == null) return;
            Log.Info("screenshot: edit last: the live document was dropped: " + why);
            LastEdit = null;
        }
    }

    /// <summary>Drops the live document when it has not been touched for <see cref="LastEditLifetime"/>. The timer calls it every minute.</summary>
    internal void ExpireLastEditIfIdle()
    {
        bool idle;
        lock (_lastEditGate) idle = _lastEdit != null && UtcNow() - _lastEditTouched >= LastEditLifetime;
        if (idle) ClearLastEdit("idle for " + (int)LastEditLifetime.TotalMinutes + " min");
    }

    /// <summary>A capture is in progress (frozen desktop being taken or the overlay up).</summary>
    public bool IsBusy => _busy;

    // ------------------------------------------------------------------ upload to Copium

    /// <summary>Test seams: the upload call, the clipboard text copy, the clock and the "a game is in front" check (a proactive pop-up never opens over one).</summary>
    internal Func<string, string, string?, IProgress<double>?, CancellationToken, string?, Task<CopiumUpload>> Uploader { get; set; } = CopiumClient.UploadAsync;
    internal Func<string, bool> LinkCopier { get; set; } = ScreenshotClipboard.CopyText;
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    internal Func<bool> GameInFront { get; set; } = () => Hotkeys.GameInFront() || UserNotificationState.FullScreenInFront();
    /// <summary>Test seams: the clipboard image copy and the clipboard's change counter (see <see cref="ScreenshotClipboard.Sequence"/>).</summary>
    internal Func<BitmapSource, byte[], bool> ImageCopier { get; set; } = ScreenshotClipboard.Copy;
    internal Func<uint> ClipboardSequence { get; set; } = ScreenshotClipboard.Sequence;

    /// <summary>Copium turned a picture down (HTTP 415, or a 400 about the type): no further AUTOMATIC uploads in this process; the toast's Upload button still tries.</summary>
    internal bool CopiumImagesRefused { get; private set; }

    private string? _rejectedToken;
    /// <summary>Copium rejected the token now set (HTTP 401 / 403): automatic uploads wait until the token is changed; the toast's Upload button still tries.</summary>
    internal bool CopiumTokenRejected => _rejectedToken != null && string.Equals(_rejectedToken, _settings.CopiumToken, StringComparison.Ordinal);

    private bool AutoUploadPaused => CopiumImagesRefused || CopiumTokenRejected;

    private readonly object _uploadGate = new();
    private Task _uploadTail = Task.CompletedTask;       // uploads run one at a time, in the order asked
    private readonly CancellationTokenSource _uploadCts = new();
    private Task? _clientWork;                           // the HTTP part running now (a worker task: waiting for it needs no UI thread)
    private bool _overrideNoticeShown;

    /// <summary>Completes when every upload asked for so far has ended (a test waits on it).</summary>
    internal Task UploadQueue { get { lock (_uploadGate) return _uploadTail; } }

    private bool HasToken =>!string.IsNullOrWhiteSpace(_settings.CopiumToken);

    /// <summary>
    /// Uploads the screenshot to Copium: queued behind any upload under way, then the toast shows the progress (with a Cancel
    /// button), the entry gets the link (history), the link replaces the image on the clipboard when
    /// <see cref="Settings.ScreenshotCopyLink"/> is on and the clipboard still holds what this screenshot put there (or the upload
    /// was <paramref name="manual"/>), and the toast shows it. Failures are told in a toast ("saved locally" first) and never throw;
    /// the file and the clipboard image were done before this started. <paramref name="manual"/> (the toast's Upload / Retry button)
    /// ignores <see cref="CopiumImagesRefused"/>. <paramref name="clipboardSequence"/>: the clipboard's change counter right after
    /// the image was copied (null when it was not). UI thread. Returns true when the screenshot has a link afterwards.
    /// </summary>
    internal async Task<bool> UploadAsync(ScreenshotEntry entry, bool manual = false, uint? clipboardSequence = null)
    {
        // Sharing is opt-in and has a kill switch. A manual upload (the toast's Upload / Retry) asks once if the user has not agreed
        // yet; an automatic one never asks: it needs the agreement and the setting already (AnnounceSaved checks; UploadCoreAsync again).
        if (manual)
        {
            if (!await Consent.EnsureAsync(ConsentContext.ScreenshotUploadManual)) return false;
        }
        else if (!Consent.CloudOn || !_settings.CopiumConsent)
        {
            Log.Info("screenshot: automatic upload skipped: " + (Consent.CloudOn ? "sharing with Copium is not agreed to" : "cloud sharing is off"));
            return false;
        }
        Task previous;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_uploadGate)
        {
            previous = _uploadTail;
            _uploadTail = gate.Task;   // in place before any of the work runs
        }
        return await RunQueuedAsync(previous, gate, entry, manual, clipboardSequence);
    }

    private async Task<bool> RunQueuedAsync(Task previous, TaskCompletionSource gate, ScreenshotEntry entry, bool manual, uint? clipboardSequence)
    {
        try
        {
            try { await previous; } catch { }
            try { return await UploadCoreAsync(entry, manual, clipboardSequence); }
            catch (Exception ex)
            {
                Log.Error("screenshot: upload", ex);
                return false;
            }
        }
        finally { gate.TrySetResult(); }
    }

    /// <summary>How far the write time of a file may be from the one recorded at the save before it counts as changed.</summary>
    internal static readonly TimeSpan WriteTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Why the file is not the one that was saved (size / write time differ), or null when it is. A file that changed after the save is not what the user took.</summary>
    private static string? ChangedSinceSaved(ScreenshotEntry e)
    {
        try
        {
            var info = new FileInfo(e.Path);
            if (!info.Exists) return "the file is gone";
            if (e.Size > 0 && info.Length != e.Size) return "the file changed after it was saved";
            // 2 s of slack: a file system with a coarse clock (FAT, a NAS share) reads the write time back rounded
            if (e.WrittenUtc is DateTime written && Math.Abs((info.LastWriteTimeUtc - written).TotalSeconds) > WriteTimeTolerance.TotalSeconds) return "the file changed after it was saved";
            return null;
        }
        catch (Exception ex) { return "the file could not be checked (" + ex.Message + ")"; }
    }

    private async Task<bool> UploadCoreAsync(ScreenshotEntry entry, bool manual, uint? clipboardSequence)
    {
        var ct = _uploadCts.Token;
        if (ct.IsCancellationRequested) return false;
        if (!string.IsNullOrEmpty(entry.CopiumLink)) return true;   // uploaded already (a second click, or queued twice)
        // Defence in depth: whatever got here, nothing leaves the computer with the kill switch off or without the user's yes.
        if (!_settings.CloudSharingEnabled)
        {
            Log.Info("screenshot: upload refused: cloud sharing is off");
            if (!_cloudOffNoticeShown)   // once per flip, not once per queued screenshot
            {
                _cloudOffNoticeShown = true;
                _toasts.ShowNotice(ConsentGate.CloudOffTitle, ConsentGate.CloudOffText);
            }
            return false;
        }
        if (!_settings.CopiumConsent)
        {
            Log.Info("screenshot: upload refused: sharing with Copium is not agreed to");
            return false;
        }
        if (!manual && AutoUploadPaused)
        {
            Log.Info("screenshot: upload skipped: Copium turned a picture down or rejected the token earlier in this session");
            return false;
        }
        string? token = _settings.CopiumToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            _toasts.ShowProblem("Screenshot not uploaded", "Saved locally. Add your Copium token under App → Copium to upload screenshots.");
            return false;
        }
        // The size limit of the account is the only one that applies to a picture (no length).
        if (AppController.ShareRefusalFor(entry.Size, 0, _settings.CopiumPlus) != null)
        {
            var (maxBytes, _, _) = Capabilities.CopiumLimits(_settings.CopiumPlus);
            _toasts.ShowProblem("Screenshot not uploaded", $"Saved locally. The picture is {Capabilities.FormatBytes(entry.Size)}, over your Copium upload limit of {Capabilities.FormatBytes(maxBytes)}.");
            return false;
        }

        // What goes up must be what was saved.
        if (ChangedSinceSaved(entry) is string changed)
        {
            Log.Warn($"screenshot: upload skipped for {System.IO.Path.GetFileName(entry.Path)}: {changed}");
            _toasts.ShowProblem("Screenshot not uploaded", "Saved locally, but it was not uploaded: " + changed + ".");
            return false;
        }

        string? imageUrl = _settings.CopiumImageUploadUrl;
        if (CopiumClient.OverrideUri(imageUrl) is { } overrideUri && !_overrideNoticeShown)
        {
            _overrideNoticeShown = true;
            Log.Info("screenshot: pictures are uploaded to " + overrideUri.Host + " (CopiumImageUploadUrl)");
            _toasts.ShowNotice("Screenshots go to " + overrideUri.Host, "The hidden CopiumImageUploadUrl setting sends screenshots (with your token) to this Copium address");
        }

        var history = History;   // the one this screenshot is listed in: the folder setting may change while it uploads
        using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);   // this upload only: the toast's Cancel button
        lock (_uploadGate) _currentOne = one;   // CancelRunningUpload (the cloud kill switch)
        var toast = _toasts.ShowScreenshotUploading(entry);
        toast.OnCancel = () => { try { one.Cancel(); } catch (ObjectDisposedException) { } };
        string title = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
        string path = entry.Path;
        var progress = new Progress<double>(p => toast.Report(p));   // made here, on the UI thread: it reports there
        var t = Stopwatch.StartNew();
        var uploadToken = one.Token;
        // Off the UI thread: opening the file can block.
        var work = Task.Run(() => Uploader(token, path, title, progress, uploadToken, imageUrl));
        _clientWork = work;
        try
        {
            var up = await work;
            entry.CopiumLink = up.Link;
            entry.CopiumPageUrl = string.IsNullOrEmpty(up.PageUrl) ? null : up.PageUrl;
            entry.CopiumId = string.IsNullOrEmpty(up.Id) ? null : up.Id;
            try
            {
                history.Update(entry);
                if (!ReferenceEquals(history, History)) History.Update(entry, warn: false);   // the folder changed meanwhile: that list may hold the entry too
            }
            catch (Exception ex) { Log.Warn("screenshot: history not updated with the link: " + ex.Message); }

            // The link replaces the image on the clipboard only if the clipboard still holds the image this screenshot put there (or the
            // user asked for the upload): a link must not overwrite something copied since.
            bool copied = false;
            string linkNote;
            if (!_settings.ScreenshotCopyLink) linkNote = "not copied (off)";
            else if (!manual && clipboardSequence == null) linkNote = "not copied (the image was not put on the clipboard)";
            else if (!manual && ClipboardSequence() != clipboardSequence) linkNote = "not copied (clipboard changed)";
            else
            {
                copied = LinkCopier(up.Link);
                linkNote = copied ? "copied" : "not copied (clipboard busy)";
            }
            Log.Info($"screenshot: uploaded {System.IO.Path.GetFileName(path)} ({entry.Size / 1024} KB) in {t.ElapsedMilliseconds} ms, link {linkNote}");
            toast.Succeeded(up.Link, copied);
            return true;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            Log.Info("screenshot: upload cancelled (Clink is closing)");
            toast.Failed();
            return false;
        }
        catch (Exception) when (one.IsCancellationRequested && !_settings.CloudSharingEnabled)
        {
            // Stopped by the kill switch, not a failure: neutral, nothing to retry.
            Log.Info("screenshot: upload cancelled (cloud sharing was turned off)");
            toast.Declined();
            return false;
        }
        catch (Exception) when (one.IsCancellationRequested)
        {
            Log.Info("screenshot: upload cancelled by the user");
            toast.Failed();   // Retry; nothing is linked, and no problem toast: it was asked for
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("screenshot: upload failed: " + ex.Message);
            toast.Failed();
            if (ex is CopiumHttpException refusal && CopiumClient.IsTypeRefusal(refusal.StatusCode, refusal.ErrorText))
            {
                CopiumImagesRefused = true;
                _toasts.ShowProblem("Copium did not accept the image", "Saved locally. Automatic uploads are paused until Clink restarts; the Upload button on a screenshot notification still tries. (" + ex.Message + ")");
            }
            else if (ex is CopiumHttpException tokenRefusal && CopiumClient.IsTokenRejection(tokenRefusal.StatusCode, tokenRefusal.ErrorText))
            {
                // Every further try would fail the same way: pause until the token is changed, and say so once.
                _rejectedToken = token;
                _toasts.ShowProblem("Copium token rejected", "Saved locally. Check the token in Settings → App → Copium; automatic uploads are paused until it is changed. (" + ex.Message + ")");
            }
            else _toasts.ShowProblem("Screenshot not uploaded", "Saved locally. " + ex.Message);
            return false;
        }
        finally
        {
            toast.OnCancel = null;
            lock (_uploadGate) { if (ReferenceEquals(_currentOne, one)) _currentOne = null; }
            if (ReferenceEquals(_clientWork, work)) _clientWork = null;
        }
    }

    private bool _cloudOffNoticeShown;   // "Cloud sharing is off" was shown since the last flip off
    private CancellationTokenSource? _currentOne;   // the upload running now (under _uploadGate)

    /// <summary>The cloud kill switch went off: cancels the upload that is running. Queued ones refuse themselves (UploadCoreAsync checks the switch).</summary>
    internal void CancelRunningUpload()
    {
        _cloudOffNoticeShown = false;   // a new flip: the next refused upload says so once
        CancellationTokenSource? one;
        lock (_uploadGate) one = _currentOne;
        if (one == null) return;
        Log.Info("screenshot: the running upload is cancelled (cloud sharing was turned off)");
        try { one.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Clink is closing: cancels the running and queued uploads and waits for the HTTP call to end, at most <paramref name="wait"/>.
    /// Waits for the worker task only, never for UI-thread work, so it is safe on the UI thread.
    /// </summary>
    internal void CancelUploads(TimeSpan wait)
    {
        Task? work;
        lock (_uploadGate) work = _clientWork;
        try { _uploadCts.Cancel(); } catch (Exception ex) { Log.Warn("screenshot: upload cancel: " + ex.Message); }
        if (work == null) return;
        try
        {
            if (!work.Wait(wait)) Log.Warn($"Exit: the screenshot upload was still ending after {wait.TotalSeconds:0} s");
        }
        catch (AggregateException) { }   // cancelled / failed: the upload already reported it
    }

    /// <summary>
    /// A screenshot was saved (and copied): the toast, then what Copium does about it. Nothing is uploaded unasked: an automatic
    /// upload needs <see cref="Settings.ScreenshotUpload"/> AND the user's yes (<see cref="Settings.CopiumConsent"/>) AND cloud sharing
    /// on AND a token; the toast then offers an Upload button instead (which asks first). After the 2nd saved screenshot on an install
    /// that has not decided, the "Share with Copium?" pop-up may follow (<see cref="MaybePromptAsync"/>). A black picture gets only its warning.
    /// </summary>
    private void AnnounceSaved(ScreenshotEntry entry, bool copied, bool clipboardFailed, bool black, uint? clipboardSequence = null)
    {
        if (black)
        {
            _toasts.ShowProblem("Screenshot is black", "Saved, but the picture is all black. Is the game in exclusive fullscreen? Use borderless windowed mode.");
            return;
        }
        bool cloud = _settings.CloudSharingEnabled;
        bool token = HasToken;
        bool noLink = string.IsNullOrEmpty(entry.CopiumLink);
        bool auto = Consent.AutoUploadAllowed && token && !AutoUploadPaused && noLink;
        _toasts.ShowScreenshot(entry, new ScreenshotToastState(copied, clipboardFailed, CanUpload: cloud && token && !auto && noLink));
        if (auto)
        {
            // With the image copy off the clipboard holds whatever it held: the link may still replace it if nothing else is copied
            // from now until the upload ends (the same change counter test as after an image copy).
            _ = UploadAsync(entry, manual: false, clipboardSequence ?? ClipboardSequence());
        }
        else CopiumNudge.CountScreenshot(_settings);
        if (!auto) PromptTask = MaybePromptAsync();
    }

    /// <summary>The proactive pop-up that the last save started (a test waits on it); completed when none was due.</summary>
    internal Task PromptTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The unasked "Share with Copium?" pop-up (<see cref="CopiumNudge"/>): after the 2nd saved screenshot without agreement, at most
    /// twice, a day apart, never with cloud sharing off. Not over a game (it would take the focus): skipped now, due again next time.
    /// Runs after the save's own work, off the capture's call stack.
    /// </summary>
    private async Task MaybePromptAsync()
    {
        try
        {
            var now = UtcNow();
            if (!CopiumNudge.IsDue(_settings, now)) return;
            if (GameInFront())
            {
                Log.Info("copium consent: the pop-up waits: a game is in front");
                return;
            }
            CopiumNudge.MarkPrompted(_settings, now);   // before it shows: a crash must not make it come back at once
            await Task.Yield();
            await Consent.EnsureAsync(ConsentContext.Proactive);
        }
        catch (Exception ex) { Log.Warn("copium consent: proactive pop-up: " + ex.Message); }
    }
    /// <summary>Copies the picture to the clipboard (when that option is on) and returns the clipboard's change counter right after, for the link that may replace it later.</summary>
    private (bool Copied, bool Failed, uint? Sequence) CopyImage(BitmapSource bitmap, byte[] png)
    {
        if (!_settings.ScreenshotCopyImage) return (false, false, null);
        bool ok = ImageCopier(bitmap, png);
        return (ok, !ok, ok ? ClipboardSequence() : null);
    }

    /// <summary>Where screenshots go: <see cref="Settings.ScreenshotsFolder"/>, else a <c>Screenshots</c> folder inside the clips folder (the library and the archive look at the clips folder's own files only).</summary>
    public static string FolderFor(Settings s) =>
        !string.IsNullOrWhiteSpace(s.ScreenshotsFolder) ? s.ScreenshotsFolder.Trim() : System.IO.Path.Combine(s.ClipsFolder, "Screenshots");

    private readonly object _historyGate = new();

    /// <summary>The history of the current folder (made again when the folder setting changed). Thread-safe: the insert-image picker reads it on a worker.</summary>
    private ScreenshotHistory History
    {
        get
        {
            lock (_historyGate)
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
                    picked = await _picker.PickAsync(desktop, _settings.ScreenshotLoupe, cts.Token,
                        ScreenshotModes.Normalize(_settings.ScreenshotMode) == ScreenshotModes.Before ? EditSetup() : null);
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
            // The picker hands the overlay over still open after a commit (see IRegionPicker.PickAsync); whatever was edited in it is in the result.
            if (shown)
            {
                try { _picker.Close(); }
                catch (Exception ex) { Log.Warn("screenshot: overlay close: " + ex.Message); }
            }
            _pick = null;
            cts.Dispose();
            _busy = false;
        }

        if (desktop == null || picked == null || picked.Rect.IsEmpty)
        {
            Log.Info("screenshot: cancelled");
            return;
        }
        // What happens follows what the pick says (the mode may have been changed in settings while the overlay was up), not the mode now.
        // Edited on the screen (mode "before"): the overlay is closed and the edited document is exported, saved, copied.
        if (picked.Document != null)
        {
            var rect = picked.Rect;
            // the black-picture warning is judged on the monitor's frozen picture, before it is let go
            bool black = WarnBlack(desktop, rect, GameFor(rect) != null);
            desktop = null;   // only the edited document (its base is the one monitor's picture) is needed now
            await CommitEditedAsync(TakeDocument(ref picked), rect, black);   // (nothing here keeps the original document: it is rebased to the crop)
            return;
        }
        var pick = picked;
        // Mode "after" (and "before" over several monitors, which is not edited in the overlay): the editor window first (one editor
        // at a time: a capture made while it is open is saved as it is, the toast has an Edit button).
        string mode = ScreenshotModes.Normalize(_settings.ScreenshotMode);
        bool window = pick.SpansMonitors || mode == ScreenshotModes.After;
        if (window && _editor is { IsOpen: false })
            await EditAfterAsync(Take(ref desktop), pick, pick.SpansMonitors ? SpanHint : null);   // not kept by this method while the editor is open (~100 MB for 3 x 4K)
        else
            await CommitAsync(desktop, pick.Rect);
    }

    /// <summary>The line in the editor's header when a selection over several monitors could not be edited on the screen.</summary>
    internal const string SpanHint = "Selection spanned monitors — editing in a window";

    private OverlayEditSetup EditSetup() => new(
        _settings.ScreenshotEditor,
        o =>
        {
            _settings.ScreenshotEditor = o;
            try { _settings.Save(); } catch (Exception ex) { Log.Warn("screenshot editor: options not saved: " + ex.Message); }
        },
        () => RecentForPicker());

    /// <summary>
    /// Mode "before": the overlay is closed; exports the edited document (the crop of the monitor's picture with the annotations
    /// clipped to it; only the WPF vector drawing runs on the UI thread, see <see cref="Exporter.ExportAsync"/>), then the normal save: one file on a worker, history,
    /// clipboard, one toast, and the document becomes the live "edit last" document.
    /// </summary>
    internal async Task CommitEditedAsync(DocumentState doc, RectI rect, bool black = false)
    {
        if (_settings.ClickSound) ClickSound.Play(_settings.ClickVolume);
        BgraImage image;
        try
        {
            var composer = RasterOps.NewComposer();
            // the vector render (WPF) happens here on the UI thread, the raster compose and the blend on workers
            try { image = await Exporter.ExportAsync(doc, composer); }
            finally { composer.Clear(); }
            // "edit last" keeps the crop only, not the whole monitor picture (and cannot reveal what the user cropped out)
            doc = doc.Rebase(doc.Crop);
        }
        catch (Exception ex)
        {
            Log.Error("screenshot: export", ex);
            _toasts.ShowProblem("Screenshot not saved", ex.Message);
            return;
        }
        await SaveEditedAsync(new EditContext { Game = GameFor(rect), Black = black }, image, doc, finish: true);
        // the original document (the whole monitor picture and its undo states) is gone: compact once, after the save
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, CompactMemory);
    }

    private static DocumentState TakeDocument(ref RegionPickResult? picked)
    {
        var d = picked!.Document!;
        picked = null;
        return d;
    }

    /// <summary>Gives the big pictures of a finished editing session back now (one compaction of the large-object heap); Clink runs for weeks.</summary>
    internal static void CompactMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
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
        /// <summary>The picture looked black (exclusive fullscreen): Done shows the warning instead of the normal toast.</summary>
        public bool Black;
        /// <summary>The file this picture was opened from (history's <c>EditedFrom</c>); the original is never overwritten.</summary>
        public string? EditedFrom;
        public string? SavedPath;
        public DocumentState? SavedDoc;
        public ScreenshotEntry? Entry;
        public System.Windows.Media.Imaging.BitmapSource? Bitmap;
        public byte[]? Png;
    }

    /// <summary>The overlay is closed: compose the crop and open the editor on it. Nothing is saved until Done / Save.</summary>
    internal async Task EditAfterAsync(FrozenDesktop desktop, RegionPickResult pick, string? hint = null)
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
        await OpenEditorAsync(document, new EditContext { Game = game }, "New screenshot", isNew: true, monitor, hint);
    }

    private async Task OpenEditorAsync(DocumentState doc, EditContext ctx, string title, bool isNew, MonitorInfo? monitor, string? hint = null)
    {
        var editor = _editor!;
        var request = new EditorRequest
        {
            Document = doc,
            Title = title,
            Hint = hint,
            IsNewCapture = isNew,
            Monitor = monitor ?? _recordedMonitor(),
            Options = _settings.ScreenshotEditor,
            PersistOptions = o =>
            {
                _settings.ScreenshotEditor = o;
                try { _settings.Save(); } catch (Exception ex) { Log.Warn("screenshot editor: options not saved: " + ex.Message); }
            },
            SaveAsync = (image, d, finish) => SaveEditedAsync(ctx, image, d, finish),
            Recent = () => RecentForPicker(ctx.EditedFrom, ctx.SavedPath),
            ExcludeFromCapture = _settings.ScreenshotEditorExcludeFromCapture,
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
                // once: the PNG is encoded from it and the clipboard gets the same frozen instance (both made on a worker: a 4K picture is 33 MB)
                var (saved, bitmap) = await Task.Run(() =>
                {
                    var made = image.ToBitmapSource();
                    return (SaveToDisk(image, folder, ctx.Game, DateTime.Now, history, made, ctx.SavedPath, ctx.EditedFrom), made);
                });
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
                var (copied, failed, sequence) = CopyImage(ctx.Bitmap, ctx.Png);
                AnnounceSaved(ctx.Entry, copied, failed, ctx.Black, sequence);
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
        TouchLastEdit();
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
            TouchLastEdit();
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
            // a device-path root (\\?\, \\.\, \??\) is never a screenshots folder, whatever the current one is
            list.AddRange(History.Roots.Where(r => !IsDevicePath(r) && (uncOk || !IsUnc(r))));
        }
        catch (Exception ex) { Log.Warn("screenshot: folders: " + ex.Message); }
        return list;
    }

    /// <summary>A device path: <c>\\?\</c> (also <c>\\?\UNC\</c>), <c>\\.\</c> or the NT form <c>\??\</c> (either slash).</summary>
    internal static bool IsDevicePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string p = path.Trim().Replace('/', '\\');
        return p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal);
    }

    /// <summary>A network path (<c>\\host\share</c>, either slash) or a device path (<see cref="IsDevicePath"/>, which can name a network share too): neither is touched unless the user chose such a folder.</summary>
    internal static bool IsUnc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string p = path.Trim().Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal) || IsDevicePath(p);
    }

    /// <summary>Test seam: the drive type of a root such as <c>Z:\</c> (<c>GetDriveType</c>; 4 = remote).</summary>
    internal static Func<string, uint> DriveTypeOf { get; set; } = root => Windows.Win32.PInvoke.GetDriveType(root);
    private const uint DriveRemote = 4;

    /// <summary>
    /// A folder on the network: a UNC or device path (<see cref="IsUnc"/>), or a path on a mapped network drive (<c>Z:\</c> that
    /// <c>GetDriveType</c> reports as remote). Used where a folder in another copy of the settings is judged; a drive that cannot be
    /// looked at counts as local.
    /// </summary>
    internal static bool IsNetworkFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsUnc(path)) return true;
        string p = path.Trim();
        if (p.Length < 2 || p[1] != ':' || !char.IsAsciiLetter(p[0])) return false;
        try { return DriveTypeOf(p[..2] + "\\") == DriveRemote; }
        catch (Exception) { return false; }
    }

    /// <summary>A history entry (or the toast's file) that may be opened: see <see cref="IsOpenableFile"/> for the current and earlier screenshot folders.</summary>
    internal bool IsOpenableEntry(string? path) => IsOpenableFile(path, KnownFolders());

    /// <summary>
    /// The recent screenshots the editor's insert-image picker may list, newest first: only entries that pass the path rule of
    /// <see cref="IsOpenableEntry"/> (inside a known screenshots folder; a network path only when the current folder is one), and not
    /// <paramref name="exclude"/>d (the file being edited, the file this session saved). No disk access for the entries: the picker
    /// checks the files themselves (<see cref="IsOpenableFile"/>: exists, regular, no link) on its worker, which is also where this
    /// runs. Thread-safe.
    /// </summary>
    internal (IReadOnlyList<ScreenshotEntry> Entries, IReadOnlyList<string> Exclude) RecentForPicker(params string?[] exclude)
    {
        var folders = KnownFolders();
        var skip = exclude.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        var entries = History.Entries.Where(e => IsOpenable(e.Path, folders)).ToList();
        return (InsertPickerModel.WithoutExcluded(entries, skip), skip);
    }

    /// <summary>
    /// A PNG given explicitly by the user on the command line: fully qualified, exactly <c>.png</c>, no alternate data stream,
    /// an existing regular file (not a directory or a link). Anywhere on a local drive: a network (UNC) or device path is refused
    /// (the pipe is the one door for it, and it must not make the app reach out to a server).
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
        var (copied, failed, sequence) = CopyImage(bitmap, saved.Png);
        Log.Info($"screenshot: saved {saved.Entry.Path} ({saved.Entry.Width}x{saved.Entry.Height}, {saved.Entry.Size / 1024} KB), crop+encode+write {encodeMs} ms, clipboard {(copied ? "ok" : _settings.ScreenshotCopyImage ? "failed" : "off")}");
        AnnounceSaved(saved.Entry, copied, failed, black, sequence);
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
            if (IsDevicePath(path)) return false;
            if (!System.IO.Path.IsPathFullyQualified(path)) return false;
            string full = System.IO.Path.GetFullPath(path);
            if (IsDevicePath(full)) return false;
            if (full.IndexOf(':', 2) >= 0 || (full.Length >= 2 && full[1] == ':' && !char.IsAsciiLetter(full[0]))) return false;
            // A drive root (C:\) or, only inside a network folder the user chose, a share root: nothing else (no \\.\, \??\, ...).
            string pathRoot = System.IO.Path.GetPathRoot(full) ?? "";
            bool driveRoot = pathRoot.Length == 3 && char.IsAsciiLetter(pathRoot[0]) && pathRoot[1] == ':' && pathRoot[2] == '\\';
            if (!driveRoot && (folders == null || !IsUnc(full))) return false;
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
            WrittenUtc = SafeWriteTime(path) is var written && written != DateTime.MinValue ? written : null,   // an upload checks the file against this
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
