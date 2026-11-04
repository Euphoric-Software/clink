using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Clink.Native;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.UI;

/// <summary>
/// The share window: what Share opens for a clip when there is no Copium token to upload it with (<see cref="AppController.ShareAsync"/>),
/// and what "Share file…" opens even with one (<see cref="AppController.ShareFile"/>).
/// Its file card is the clip's file, ready to drag out into Discord, a browser upload box or an Explorer folder; Copy file puts the
/// same file on the clipboard for Ctrl+V; Open folder shows it in Explorer; Set up Copium… brings the main window forward to add a
/// token. Dragging and copying only ever <b>copy</b> (<see cref="DragEffects"/>, <see cref="FileData"/>), so a drop target can never
/// move the clip out of the library. The file is looked at on a worker, never on the UI thread (an archive share may be offline or
/// asleep): when the window opens, whenever it comes forward again (the clip may have been archived or replaced by a trim since) and
/// before Open folder; the card is disabled, saying why, while the file cannot be reached.
/// <para>
/// The window floats above other windows (Topmost) so it stays in reach while the drop target is brought forward, except over a game:
/// it goes behind the game while that has the focus, and opens without taking the focus, at the toasts' position, when a game is
/// running (<see cref="ShowOverGame"/>). It has no owner (minimizing or hiding the main window does not take it along), and it and
/// its tooltips stay out of screen captures like the toasts do (<see cref="ICaptureExcludedWindow"/>), so it never ends up in a
/// clip. It shows no file path anywhere. Colours are all DynamicResources.
/// </para>
/// </summary>
public partial class ShareWindow : Window, ICaptureExcludedWindow
{
    private static string DragHint => L.T("Drag into Discord, a browser upload box or a folder");
    /// <summary>The clipboard / drag format Explorer reads to decide between copy and move.</summary>
    internal const string PreferredDropEffect = "Preferred DropEffect";
    /// <summary>
    /// What a drop target may do with the dragged file: <b>copy it</b>, nothing else. Move would let Explorer move the clip out of
    /// the library. The drag allows exactly this, and the "Preferred DropEffect" of the data says the same.
    /// </summary>
    internal const DragDropEffects DragEffects = DragDropEffects.Copy;
    /// <summary>The toasts' distance from the edges of the work area, in DIPs (<c>Toasts</c>).</summary>
    private const int ToastMargin = 16;

    internal enum FileState { Unknown, Present, Missing, Offline }

    private FileState _state;
    private bool _checking, _closed, _dragArmed, _stateNote, _afterDrag, _openingFolder;
    private long _thumbSize;       // the clip's size when the thumbnail on the card was made: another size is another file
    private string? _thumbMissFor; // "path|size" of a file the shell could make no thumbnail for: not asked again until it changes
    private Point _dragFrom;

    internal ClipEntry Entry { get; }

    /// <summary>The tooltips of this window are kept out of capture too, whichever window class is the first one shown (<see cref="CaptureExclusion"/>).</summary>
    static ShareWindow() => CaptureExclusion.Register();

    internal ShareWindow(ClipEntry entry)
    {
        Entry = entry;
        InitializeComponent();
        // tooltips with key names in them (the {l:T} extension takes no arguments)
        CloseX.ToolTip = L.T("Close ({keys})", ("keys", L.Ltr("Esc")));
        CopyButton.ToolTip = L.T("Copy the file, then paste it into Discord or a folder with {paste} ({copy} in this window does the same)", ("paste", L.Ltr("Ctrl+V")), ("copy", L.Ltr("Ctrl+C")));
        // The panel's button texts come from the model only (SetFit keeps them current).
        MakeCopyButton.Content = ShareFitModel.MakeCopyText(_fitTarget);
        CancelCopyButton.Content = ShareFitModel.CancelText;
        UseOriginalButton.Content = ShareFitModel.UseOriginalText;
        AppIcons.Attach(this);   // has its own taskbar button (no owner): the icon size the taskbar draws, not WPF's default frame
        // Cloud sharing off (Settings → App): the file to drag is local, but there is nothing to set up.
        // Already signed in (or a token pasted): nothing to set up either.
        bool hasToken = HasToken();
        bool nothingToSetUp = CloudSharingOff() || hasToken;
        if (nothingToSetUp) SetUpCopiumButton.Visibility = Visibility.Collapsed;
        IntroText.Text = IntroFor(nothingToSetUp);
        ShowEntry();
        // A local file is taken to be there (the worker's look is quick and only matters when it is not); an archived one waits for
        // its share to answer, which can take the SMB timeout when it is offline.
        _state = entry.IsArchived ? FileState.Unknown : FileState.Present;
        ShowState();
        Loaded += (_, _) => _ = CheckAsync();
        // Whenever the window comes forward (Share pressed again, the user came back to it): look at the clip again, quietly. It may
        // have been archived, replaced by a trim or deleted since, and a share that was down may be back.
        Activated += (_, _) => { if (!QuietMode.Active) Topmost = true; _ = CheckAsync(); };
        // Not over a game: once the window has lost the focus to the game it goes behind it (Topmost is back when it is activated, or
        // when the game loses the focus; the controller's once-a-second state tick catches a game that took the focus without
        // this window ever having had it).
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateTopmost));
        SourceInitialized += (_, _) =>
        {
            // Topmost and on the captured monitor: keep it out of the clips, like the toasts and the status dot.
            ScreenPlacement.ExcludeFromCaptureUnlessDocShots(new WindowInteropHelper(this).Handle);
            AppController.Instance.StateChanged += OnStateTick;   // once there is a window; it is let go of when it closes
        };
        Closed += (_, _) =>
        {
            _closed = true;
            AppController.Instance.StateChanged -= OnStateTick;
            LeaveFit();
        };
    }

    /// <summary>The controller's once-a-second state tick: the Topmost rule, and a changed Discord plan (or clip size) is looked at again.</summary>
    internal void OnStateTick()
    {
        UpdateTopmost();
        RefreshFit(comingForward: false);
    }

    /// <summary>The line above the card: the no-token explanation, or just what to do with the file when a token exists (Share file…).</summary>
    internal static string IntroFor(bool hasToken) => hasToken
        ? L.T("Drag the file where you want it, or copy it:")
        : L.T("No Copium token yet, so there is no link to copy. Share the file itself:");

    private static bool HasToken() => AppController.Instance is { } app && app.HasCopiumToken;
    private static bool CloudSharingOff() => AppController.Instance?.Settings.CloudSharingEnabled == false;

    // ------------------------------------------------------------------ what is dragged and copied

    /// <summary>
    /// The clip's file as something to drag or paste: a file drop (what Explorer, Discord and browser upload boxes take), allowed
    /// to be <b>copied only</b> (<see cref="DragEffects"/>). "Preferred DropEffect" = Copy tells Explorer's paste the same, and the
    /// drag itself offers no other effect, so no target can move the clip out of the library.
    /// </summary>
    internal static DataObject FileData(string path) => FileData(new[] { path });

    /// <summary>Several files as one drop (the multi-file share window): the same copy-only rules as <see cref="FileData(string)"/>, the list in the order given.</summary>
    internal static DataObject FileData(IReadOnlyList<string> paths)
    {
        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes((int)DragEffects)));
        return data;
    }

    /// <summary>"190 MB · 1:30": the size, then the length when it is known (a clip found in the folder has none).</summary>
    internal static string MetaFor(long size, double duration)
    {
        var parts = new List<string>(2);
        if (size > 0) parts.Add(L.Bytes(size));
        if (duration > 0) parts.Add(L.ClipLength(duration));
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ the file behind the card

    /// <summary>Any thread, never the UI one for an archived path: an offline share blocks for its SMB timeout. Same reading as the clip rows.</summary>
    internal static FileState Probe(string path, bool archived)
    {
        string? dir = Path.GetDirectoryName(path);
        try
        {
            if (!archived) return File.Exists(path) ? FileState.Present : FileState.Missing;
            if (dir == null || !Directory.Exists(dir)) return FileState.Offline;
            // Not File.Exists: it says false for any error (a dropped SMB session, access denied while the share signs in again).
            // Only the server's "no such file" with the folder still answering is a deleted clip.
            File.GetAttributes(path);
            return FileState.Present;
        }
        catch (FileNotFoundException)
        {
            try { return dir != null && Directory.Exists(dir) ? FileState.Missing : FileState.Offline; }
            catch { return FileState.Offline; }
        }
        catch { return FileState.Offline; }
    }

    /// <summary>
    /// Name, size and length from the entry as it is now (an archive move changes the path, a trim that replaced the original the
    /// size and length), and the thumbnail dropped when the size changed: it is another file then.
    /// </summary>
    private void ShowEntry()
    {
        NameText.Text = Entry.FileName;
        // The card is the file that is shared: the smaller copy's size, length and quality while it is the one.
        MetaText.Text = _fit.Kind == ShareFitStateKind.Done && _fit.Copy is { } copy
            ? ShareFitModel.DoneDetails(copy.Bytes, copy.Seconds, copy.Label)
            : MetaFor(Entry.Size, Entry.Duration);
        if (_thumbSize != Entry.Size) ThumbImage.Source = null;
    }

    /// <summary>
    /// Looks at the clip again: the entry's name, size and length; then the file, on a worker, which enables or disables the card at
    /// once; then, apart from that, its thumbnail (on an archive share that takes a while, and the card must not wait for it).
    /// Runs again when the entry changed while it was looking, or a drag ended meanwhile. One look at a time.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_closed) return;
        RefreshFit(comingForward: true);   // the smaller copy: the plan, the clip's size and a copy made meanwhile (not tied to the file look below)
        if (_checking) return;
        _checking = true;
        try
        {
            while (!_closed)
            {
                ShowEntry();
                string path = Entry.Path;
                bool archived = Entry.IsArchived;
                long size = Entry.Size;
                bool afterDrag = _afterDrag;   // this look starts after a drag ended: a gone file is worth a warning
                _afterDrag = false;
                // Looking again: a card that is off stays off with "Looking in the archive…"; one that is on is left alone (a drag may be starting).
                if (_state != FileState.Present) { _state = FileState.Unknown; ShowState(); }
                var state = await Task.Run(() => Probe(path, archived));
                if (_closed) return;
                if (afterDrag && state != FileState.Present)
                    Log.Warn($"share window: after the drag {Path.GetFileName(path)} is {state}: a drop target may have moved it out of the library");
                _state = state;
                ShowState();   // usable as soon as the file is known to be there, before its thumbnail is made
                string key = path + "|" + size;
                if (state == FileState.Present && ThumbImage.Source == null && _thumbMissFor != key)
                {
                    BitmapSource? thumb = await Task.Run(() => Thumbnails.Get(path, 176, 100));
                    // Not when the clip became another file while it was made (the next look makes it again).
                    if (!_closed && Entry.Size == size)
                    {
                        if (thumb != null && ThumbImage.Source == null) { ThumbImage.Source = thumb; _thumbSize = size; }
                        else if (thumb == null) _thumbMissFor = key;
                    }
                }
                if (_afterDrag) continue;   // a drag ended while this look was running
                if (Entry.Path == path && Entry.IsArchived == archived && Entry.Size == size) break;   // nothing changed meanwhile
            }
        }
        finally { _checking = false; }
    }

    /// <summary>Card, Copy file and Open folder work only while the file is there; the hint under the name says why not.</summary>
    private void ShowState()
    {
        bool present = _state == FileState.Present;
        Card.IsEnabled = CopyButton.IsEnabled = FolderButton.IsEnabled = present;
        switch (_state)
        {
            case FileState.Present:
                HintText.Text = DragHint;
                HintText.ClearValue(TextBlock.ForegroundProperty);
                if (_stateNote) { SetStatus(null); _stateNote = false; }
                return;
            case FileState.Unknown:
                HintText.Text = ClipRow.LookingText;
                HintText.ClearValue(TextBlock.ForegroundProperty);
                return;
            case FileState.Offline:
                HintText.Text = ClipRow.ArchiveOffline;
                SetStatus(L.T("Clink looks again when you come back to this window."), "Muted");
                _stateNote = true;
                break;
            default:
                HintText.Text = Entry.IsArchived ? ClipRow.DeletedFromArchiveText : ClipRow.LocalDeleted;
                SetStatus(null);
                break;
        }
        HintText.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
    }

    /// <summary>
    /// The line under the card; <paramref name="brush"/> is a palette key (a DynamicResource reference, so it follows the theme).
    /// Accent2 by default, not Accent: the accent as text fails 4.5:1 on the light theme for several accents (e.g. Orange, Pink).
    /// </summary>
    private void SetStatus(string? text, string brush = "Accent2")
    {
        string shown = text ?? "";
        bool changed = StatusText.Text != shown;
        StatusText.Text = shown;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (changed && shown.Length > 0) { StatusAnnouncements++; RaiseLive(StatusText); }   // LiveSetting alone announces nothing: code raises the event
    }

    // ------------------------------------------------------------------ coming forward, and not over the game

    /// <summary>
    /// Brings an open window forward and looks at the clip again: Share was pressed for the same clip once more. Without
    /// <paramref name="activate"/> (over a game) the window is only made visible, it takes no focus.
    /// </summary>
    internal void BringForward(bool activate = true)
    {
        if (_closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _ = CheckAsync();
        if (activate && !QuietMode.Active) Activate();
    }

    /// <summary>
    /// Shows the window for a user who is over a game (a toast over it was clicked, or it has the focus): without taking the focus,
    /// and at the toasts' position on the captured monitor (<paramref name="toastPosition"/>, "bottom-right" and so on), not in the
    /// middle of the picture.
    /// </summary>
    internal void ShowOverGame(MonitorInfo? monitor, string? toastPosition)
    {
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;                     // shown invisible, placed, then made visible: no flash at the wrong spot (like the toasts)
        Show();
        UpdateLayout();
        var wa = ScreenPlacement.WorkAreaOf(monitor);
        ScreenPlacement.Place(this, (w, h) => ToastCorner(wa.left, wa.top, wa.right, wa.bottom, toastPosition, w, h, (int)Math.Round(ToastMargin * ScreenPlacement.ScaleOf(this))));
        Opacity = 1;
    }

    /// <summary>
    /// Where a window of this size goes at a toast position of a work area ("top-left", "top", "top-right", "left", "right",
    /// "bottom-left", "bottom", "bottom-right"; none set means bottom-right), <paramref name="margin"/> pixels from the edges:
    /// the toasts' own rule (<c>Toasts.Layout</c>), so a side the name does not give is the middle. Physical pixels. Pure.
    /// </summary>
    internal static (int x, int y) ToastCorner(int left, int top, int right, int bottom, string? position, int w, int h, int margin)
    {
        string pos = (position ?? "bottom-right").Trim().ToLowerInvariant();
        bool atTop = pos.StartsWith("top", StringComparison.Ordinal), atBottom = pos.StartsWith("bottom", StringComparison.Ordinal);
        bool atLeft = pos.EndsWith("left", StringComparison.Ordinal), atRight = pos.EndsWith("right", StringComparison.Ordinal);
        int x = atLeft ? left + margin : atRight ? right - margin - w : (left + right - w) / 2;
        int y = atTop ? top + margin : atBottom ? bottom - margin - h : (top + bottom - h) / 2;
        return (x, y);
    }

    /// <summary>
    /// Topmost keeps the window in reach while the drop target is brought forward, but never over the game: while the detected game
    /// has the focus the window goes behind it, and comes back on top once the game has lost the focus.
    /// </summary>
    private void UpdateTopmost()
    {
        if (_closed || QuietMode.Active) return;   // --dev-quiet: never topmost
        KeepBehindGame(this);
    }

    /// <summary>The Topmost / behind-the-game rule of <see cref="UpdateTopmost"/> for any share window (UI thread).</summary>
    internal static void KeepBehindGame(Window window)
    {
        bool game = Hotkeys.GameInFront();
        if (game && window.Topmost)
        {
            window.Topmost = false;
            // Not enough on its own: a window that stops being topmost stays above the ordinary windows, the game's included. Slip it in
            // right behind the game's window (the foreground one).
            var fg = PInvoke.GetForegroundWindow();
            nint self = new WindowInteropHelper(window).Handle;
            if (!fg.IsNull && self != 0)
                PInvoke.SetWindowPos(new HWND(self), fg, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        else if (!game && !window.Topmost) window.Topmost = true;
    }

    // ------------------------------------------------------------------ dragging the card out

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(null);
        _dragArmed = _state == FileState.Present;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragArmed = false;

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragArmed = false; return; }
        var at = e.GetPosition(null);
        if (Math.Abs(at.X - _dragFrom.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(at.Y - _dragFrom.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        StartDrag();
    }

    private void StartDrag()
    {
        try
        {
            SetStatus(null);
            // Copy only: allowing Move would let Explorer move the clip out of the library.
            string? path = SharePathNow();
            if (path == null) return;
            var effect = DragDrop.DoDragDrop(Card, FileData(path), DragEffects);
            if (effect == DragEffects) SetStatus(L.T("Dropped. The clip stays in your library."));
            // The guard is the allowed effect above; if a target still answers Move, or the file is gone afterwards, the clip may
            // have left the library: say so in the log, and look at the card again (a gone file disables it).
            if ((effect & DragDropEffects.Move) != 0)
                Log.Warn($"share window: a drop target answered {effect} to the copy-only drag of {Entry.FileName}");
            _afterDrag = true;
            _ = CheckAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("share window: drag failed: " + ex.Message);
            SetStatus(L.T("Could not start the drag: {error}", ("error", ex.Message)), "Warn");
        }
    }

    // ------------------------------------------------------------------ the smaller copy (a clip over the Discord plan's limit)

    // The sequence (AppController.ShareFit.cs): open / come forward = Find (null: the "Make a ... copy" button); click = Make (at once
    // "Preparing...": nothing is reported while the clip is probed); Cancel = cancel the token, then a fresh one; close = Abandon the
    // pending task (the copy goes on in the background and is ready next time). Every ask has a generation: an answer or a report of an
    // older ask, or one that comes after the window closed, is ignored.
    private ShareFitState _fit = ShareFitState.Fits;
    private ShareFitTarget _fitTarget = ShareFitModel.ForPlan(null);
    private long _fitSize;                 // the clip's size the current ask was made for: another size is another file
    private bool _fitAsked, _fitDeclined;  // _fitDeclined: "Use the original" was pressed; a copy found later does not take the card back
    private int _fitGen;
    private Task? _fitPending;             // exactly the task Make / Find returned (ShareCopyResult.Abandon wants that one)
    private CancellationTokenSource _fitCts = new();
    private ShareCopyResult? _fitCopy;     // the copy being shared (Done); disposed when it is no longer

    /// <summary>What the window is showing for the smaller copy.</summary>
    internal ShareFitStateKind FitKind => _fit.Kind;

    /// <summary>No ask is out (the last Find / Make has been answered or abandoned).</summary>
    internal bool FitIdle => _fitPending == null;

    /// <summary>The target of the Discord plan the panel was made for.</summary>
    internal ShareFitTarget FitTarget => _fitTarget;

    /// <summary>The file the card, the drag and Copy file use: the smaller copy while it is the one in use, else the original. Open folder always shows the original.</summary>
    internal string SharePath => _fit.Kind == ShareFitStateKind.Done && _fit.Copy is { } copy ? copy.Path : Entry.Path;

    /// <summary>The path to share right now, or null (saying so) when the smaller copy was cleaned up since: the panel then looks again.</summary>
    internal string? SharePathNow()
    {
        string path = SharePath;
        if (_fit.Kind != ShareFitStateKind.Done || File.Exists(path)) return path;
        LeaveFit();   // drops the copy and what is pending
        SetFit(ShareFitState.Over);
        SetStatus(L.T("The smaller copy is not there any more. Clink looks again."), "Warn");
        RefreshFit(comingForward: true);
        return null;
    }

    /// <summary>
    /// Looks at the smaller copy again. A changed Discord plan or clip size starts afresh (the old ask is abandoned: a copy under way
    /// goes on); <paramref name="comingForward"/> (the window came forward, a drag ended) also asks again when nothing is under way, so a
    /// copy made meanwhile is found and one that was cleaned up is not offered. Failures, "can't fit" and "not available" stay as they are
    /// until the plan or the size changes.
    /// </summary>
    internal void RefreshFit(bool comingForward)
    {
        if (_closed || AppController.Instance is not { } app) return;
        var target = ShareFitModel.ForSettings(app.Settings);
        long size = Entry.Size;
        bool changed = !_fitAsked || target.LimitBytes != _fitTarget.LimitBytes || size != _fitSize;
        if (changed) { _fitDeclined = false; BeginFit(app, target, size, make: false); return; }
        if (!comingForward || _fitPending != null) return;
        if (_fit.Kind is ShareFitStateKind.Over or ShareFitStateKind.Done) BeginFit(app, target, size, make: false);
    }

    /// <summary>Starts a Find (or, for the button, a Make) for the target; abandons whatever was pending.</summary>
    private void BeginFit(AppController app, ShareFitTarget target, long size, bool make)
    {
        bool keepCopy = !make && _fit.Kind == ShareFitStateKind.Done && target.LimitBytes == _fitTarget.LimitBytes && size == _fitSize;
        var shown = _fit;
        LeaveFit(keepCopyOpen: keepCopy);
        _fitTarget = target;
        _fitSize = size;
        _fitAsked = true;
        var first = ShareFitModel.StateFor(size, target);
        if (first.Kind == ShareFitStateKind.Fits) { SetFit(ShareFitState.Fits); return; }   // nothing to offer: the panel stays away
        int gen = _fitGen;
        if (make)
        {
            SetFit(ShareFitState.Preparing);
            var task = app.MakeShareCopyAsync(Entry, target, new FitSink(this, gen), _fitCts.Token);
            _fitPending = task;
            _ = FitAnswerAsync(new WeakReference<ShareWindow>(this), Dispatcher, task, gen);
        }
        else
        {
            SetFit(keepCopy ? shown : ShareFitState.Over);   // the button is there while the answer is out; a copy in use stays in use
            var task = app.FindShareCopyAsync(Entry, target, new FitSink(this, gen), _fitCts.Token);
            _fitPending = task;
            _ = FitAnswerAsync(new WeakReference<ShareWindow>(this), Dispatcher, task, gen);
        }
    }

    /// <summary>The ask's answer: waited for on any thread, then handed to the UI thread (explicitly, not through the await's context).</summary>
    /// <summary>Static, holding the window only weakly: a window closed while its copy is still made is not kept alive until the copy ends.</summary>
    private static async Task FitAnswerAsync(WeakReference<ShareWindow> window, Dispatcher dispatcher, Task task, int gen)
    {
        ShareCopyResult? result = null;
        try
        {
            await task.ConfigureAwait(false);
            result = ((Task<ShareCopyResult?>)task).Result;
        }
        catch (Exception ex) { Log.Warn("share window: the smaller copy: " + ex.Message); }
        try
        {
            var answer = result;
            await dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (window.TryGetTarget(out var w)) w.ApplyFitAnswer(answer, gen);
                else answer?.Dispose();   // the window is gone
            }));
        }
        catch (Exception) { result?.Dispose(); }   // the dispatcher is shutting down
    }

    private void ApplyFitAnswer(ShareCopyResult? result, int gen)
    {
        if (_closed || gen != _fitGen)
        {
            result?.Dispose();   // an older ask or a closed window: not wanted (Abandon released its other mark already)
            return;
        }
        _fitPending = null;
        if (result == null)
        {
            // Nothing found: the button (a Done copy that vanished from the cache goes too).
            FitDisposeCopy();
            SetFit(ShareFitModel.StateFor(Entry.Size, _fitTarget));
            return;
        }
        ShareFitState state = ShareFitModel.StateFor(result);
        if (result.Outcome == ShareCopyOutcome.Failed && result.ErrorKind == ShareCopyError.FileMissing) state = ShareFitState.Fits;   // the card says the file is gone already
        if (state.Kind == ShareFitStateKind.Done)
        {
            if (_fitDeclined) { result.Dispose(); FitDisposeCopy(); SetFit(ShareFitState.Over); return; }   // the user chose the original
            FitDisposeCopy();
            _fitCopy = result;
            SetFit(state);
            return;
        }
        // Not a copy: the result holds nothing else of use.
        result.Dispose();
        FitDisposeCopy();
        SetFit(state);
    }

    private void FitDisposeCopy()
    {
        var old = _fitCopy;
        _fitCopy = null;
        old?.Dispose();
    }

    /// <summary>Leaves the pending ask (the window no longer listens, and the ask's mark is released; the job goes on) and lets go of the copy in use.</summary>
    private void LeaveFit(bool keepCopyOpen = false)
    {
        _fitGen++;   // answers and reports of the old ask are ignored from now on
        var pending = _fitPending;
        _fitPending = null;
        if (pending != null) ShareCopyResult.Abandon(pending);
        if (!keepCopyOpen) FitDisposeCopy();
    }

    /// <summary>A progress report from the job's thread, to the UI thread, in order; late ones (another ask, closed) are dropped there.</summary>
    private sealed class FitSink(ShareWindow window, int gen) : IProgress<ShareCopyProgress>
    {
        private ShareWindow? _window = window;
        public void Report(ShareCopyProgress progress)
        {
            var w = _window;
            if (w == null) return;
            try { w.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => w.OnFitProgress(gen, progress))); }
            catch (Exception) { }   // the dispatcher is shutting down
        }
    }

    private void OnFitProgress(int gen, ShareCopyProgress progress)
    {
        if (_closed || gen != _fitGen || _fitPending == null) return;   // not the ask in hand, or it is answered already
        SetFit(ShareFitModel.StateFor(progress));
    }

    /// <summary>Shows a state in the panel (texts from <see cref="ShareFitModel"/>) and on the card.</summary>
    private void SetFit(ShareFitState state)
    {
        bool hadFocus = FitPanel.IsKeyboardFocusWithin;   // the pressed button is about to collapse: the focus must not fall out of the window
        var before = _fit.Kind;
        _fit = state;
        var kind = state.Kind;
        bool busy = kind is ShareFitStateKind.Preparing or ShareFitStateKind.Working or ShareFitStateKind.Waiting;
        FitPanel.Visibility = kind == ShareFitStateKind.Fits ? Visibility.Collapsed : Visibility.Visible;
        FitText.Text = ShareFitModel.Text(state, _fitTarget, Entry.Size, Entry.Duration) ?? "";
        FitText.SetResourceReference(TextBlock.ForegroundProperty, kind is ShareFitStateKind.Failed or ShareFitStateKind.CantFit or ShareFitStateKind.Refused ? "Warn" : "Accent2");
        MakeCopyButton.Content = ShareFitModel.MakeCopyText(_fitTarget);
        MakeCopyButton.Visibility = ShareFitModel.CanMakeCopy(state) ? Visibility.Visible : Visibility.Collapsed;
        CancelCopyButton.Content = ShareFitModel.CancelText;
        CancelCopyButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UseOriginalButton.Content = ShareFitModel.UseOriginalText;
        UseOriginalButton.Visibility = kind == ShareFitStateKind.Done ? Visibility.Visible : Visibility.Collapsed;
        FitProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FitProgress.IsIndeterminate = kind is ShareFitStateKind.Preparing or ShareFitStateKind.Waiting;
        FitProgress.Value = kind == ShareFitStateKind.Working ? state.Progress : 0;   // its name stays fixed (XAML): the bar reports its own value
        ShowEntry();   // the card's size line follows the copy in use
        if (kind != before)
        {
            // Announced when the kind of state changes, not on every progress tick.
            FitAnnouncements++;
            RaiseLive(FitText);
            if (hadFocus) FocusFor(kind, state).Focus();
        }
    }

    /// <summary>The button that takes the keyboard focus when a state replaces the one whose button was pressed: Cancel while busy, "Copy file" after Done (else "Use the original"), the Make button when it is offered, else "Copy file".</summary>
    internal Button FocusFor(ShareFitStateKind kind, ShareFitState state)
    {
        if (kind is ShareFitStateKind.Preparing or ShareFitStateKind.Working or ShareFitStateKind.Waiting) return CancelCopyButton;
        if (kind == ShareFitStateKind.Done) return CopyButton.IsEnabled ? CopyButton : UseOriginalButton;
        return ShareFitModel.CanMakeCopy(state) ? MakeCopyButton : CopyButton;
    }

    /// <summary>How many times the panel's text was announced to a screen reader (the state kind changed); for the tests.</summary>
    internal int FitAnnouncements { get; private set; }

    /// <summary>How many times the status line was announced (it changed to some text); for the tests.</summary>
    internal int StatusAnnouncements { get; private set; }

    /// <summary>
    /// WPF announces a <c>LiveSetting</c> text block only when code raises <c>LiveRegionChanged</c> (the same helper as the main window's
    /// and the clips pane's). Nothing happens when no screen reader listens or the block is not shown (no peer to build).
    /// </summary>
    internal static void RaiseLive(TextBlock tb)
    {
        if (!tb.IsVisible || !System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged)) return;
        try { (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(tb) ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(tb))?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged); }
        catch (Exception ex) { Log.Warn("live region: " + ex.Message); }
    }

    private void MakeCopy_Click(object sender, RoutedEventArgs e) => MakeCopy();

    /// <summary>The "Make a ... copy" button: a Make for the current plan, "Preparing..." at once. Not on its own: only the button starts a copy. Offered after Over, a refusal and a failure that another try may cure (<see cref="ShareFitModel.CanMakeCopy"/>).</summary>
    internal void MakeCopy()
    {
        if (_closed || !ShareFitModel.CanMakeCopy(_fit) || AppController.Instance is not { } app) return;
        _fitDeclined = false;
        BeginFit(app, ShareFitModel.ForSettings(app.Settings), Entry.Size, make: true);
    }

    private void CancelCopy_Click(object sender, RoutedEventArgs e) => CancelCopy();

    /// <summary>Cancel: stops the copy for everyone who joined it, then a fresh token so the next click works. The panel offers the button again.</summary>
    internal void CancelCopy()
    {
        if (_closed || _fit.Kind is not (ShareFitStateKind.Preparing or ShareFitStateKind.Working or ShareFitStateKind.Waiting)) return;
        var old = _fitCts;
        _fitCts = new CancellationTokenSource();
        try { old.Cancel(); } catch (ObjectDisposedException) { }
        LeaveFit();   // its Cancelled answer is not needed: the button comes back now (a copy that finished just before the cancel is disposed on arrival)
        _fitGen++;
        SetFit(ShareFitState.Over);
    }

    private void UseOriginal_Click(object sender, RoutedEventArgs e) => UseOriginal();

    /// <summary>"Use the original": the card, the drag and Copy file go back to the clip itself; the button to make (find) the copy comes back.</summary>
    internal void UseOriginal()
    {
        if (_closed || _fit.Kind != ShareFitStateKind.Done) return;
        _fitDeclined = true;
        LeaveFit();
        SetFit(ShareFitState.Over);
    }

    // ------------------------------------------------------------------ buttons

    private void CopyFile_Click(object sender, RoutedEventArgs e) => CopyFile();

    private void CopyFile()
    {
        if (_state != FileState.Present) return;
        try
        {
            string? path = SharePathNow();
            if (path == null) return;
            Clipboard.SetDataObject(FileData(path), true);
            SetStatus(L.T("File copied. Paste it with {keys} into Discord or a folder.", ("keys", L.Ltr("Ctrl+V"))));
        }
        catch (Exception ex)
        {
            Log.Warn("share window: clipboard: " + ex.Message);
            SetStatus(L.T("Could not copy the file: {error}", ("error", ex.Message)), "Warn");
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync();

    /// <summary>
    /// Shows the file in Explorer. The file is looked at again on a worker first, so a share that went to sleep cannot freeze the
    /// window (on the UI thread that look would block for the SMB timeout) and a clip that was archived or deleted since the last
    /// look is not acted on; Explorer is then started on the path itself, not through <see cref="AppController.OpenFolder"/>, which
    /// tests the file on the UI thread and falls back to the clips folder.
    /// </summary>
    private async Task OpenFolderAsync()
    {
        if (_state != FileState.Present || _openingFolder) return;
        _openingFolder = true;
        try
        {
            string path = Entry.Path;
            bool archived = Entry.IsArchived;
            SetStatus(L.T("Looking for the file…"), "Muted");   // an archive share that is waking up can take a while
            var state = await Task.Run(() => Probe(path, archived));
            if (_closed) return;
            if (state != FileState.Present) { _state = state; ShowState(); return; }
            SetStatus(null);
            TestGuard.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Warn("share window: open folder: " + ex.Message);
            SetStatus(L.T("Could not open the folder: {error}", ("error", ex.Message)), "Warn");
        }
        finally { _openingFolder = false; }
    }

    private void SetUpCopium_Click(object sender, RoutedEventArgs e)
    {
        // The main window comes forward with the Copium card to fill in; this window would only sit on top of it.
        AppController.Instance.ShowCopiumSetup();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyFile(); }
    }
}
