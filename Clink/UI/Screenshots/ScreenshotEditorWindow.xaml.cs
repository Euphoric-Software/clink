using System.ComponentModel;
using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Services;

namespace Clink.UI.Screenshots;

/// <summary>
/// The standalone screenshot editor (edit after capture, edit last, the toast's Edit button): TrimWindow-style chrome around an
/// <see cref="EditorToolbar"/> and an <see cref="EditorCanvas"/> over one <see cref="EditorSession"/>. Done saves (the service does
/// the file, history, clipboard and toast through <see cref="EditorRequest.SaveAsync"/>) and closes; Esc / Discard close without
/// saving; closing with unsaved edits asks. Kept out of screen capture and recordings (<see cref="EditorRequest.ExcludeFromCapture"/>,
/// on by default: it shows what the user is covering up), its picture picker with it. On close every picture
/// reference is dropped and the large-object heap is compacted once (Clink is a long-running tray app).
/// </summary>
public partial class ScreenshotEditorWindow : Window, ICaptureExcludedWindow
{
    private EditorRequest? _request;
    private EditorSession? _session;
    private bool _closeApproved, _busy, _everSaved;
    private bool _closeWhenIdle;   // a close was asked for while a save was under way: asked again when it ends

    static ScreenshotEditorWindow() => CaptureExclusion.Register();

    bool ICaptureExcludedWindow.CaptureExcluded => _request?.ExcludeFromCapture ?? true;

    internal ScreenshotEditorWindow(EditorRequest request)
    {
        InitializeComponent();
        _request = request;
        _session = new EditorSession(request.Document, request.Options, request.PersistOptions);
        TitleText.Text = request.Title;
        if (!string.IsNullOrWhiteSpace(request.Hint))
        {
            HintText.Text = request.Hint;
            HintText.Visibility = Visibility.Visible;
        }
        DiscardButton.Content = request.IsNewCapture ? "Discard" : "Close";
        Canvas.Attach(_session);
        Toolbar.Attach(_session);
        Toolbar.CopyRequested += () => Run(CopyAsync);
        Toolbar.SaveRequested += () => Run(() => SaveCoreAsync(finish: false));
        Toolbar.FitRequested += Canvas.ZoomToFit;
        Toolbar.ActualRequested += Canvas.ZoomActual;
        Toolbar.FocusCanvasRequested += () => Canvas.Focus();
        Toolbar.InsertRequested += OpenInsertPicker;
        Canvas.InsertImageRequested += OpenInsertPicker;
        Canvas.PasteRequested += PasteImage;
        _session.Notice += Say;
        Canvas.ViewChanged += () => Toolbar.SetZoom(Canvas.Zoom);
        Toolbar.SetZoom(Canvas.Zoom);
        SourceInitialized += (_, _) =>
        {
            Place(request);
            if (request.ExcludeFromCapture) ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        };
        Loaded += (_, _) => Canvas.Focus();
        Deactivated += (_, _) => Canvas.SpaceDown = false;   // the key-up goes to another window
    }

    internal EditorSession? Session => _session;

    /// <summary>
    /// The window rectangle (physical pixels) for a picture: at least 1000 x 700 DIPs, big enough for the picture and the chrome, but
    /// never more than 90% of the work area (and never beyond it), centred in it. Pure; no screen-derived value is ever a bound of a Clamp.
    /// </summary>
    internal static (int X, int Y, int W, int H) Place(int imageW, int imageH, double dpi, int workLeft, int workTop, int workW, int workH)
    {
        int w = (int)Math.Min(Math.Max(imageW + 40 * dpi, 1000 * dpi), workW * 0.9);
        int h = (int)Math.Min(Math.Max(imageH + 232 * dpi, 700 * dpi), workH * 0.9);
        w = Math.Min(w, workW);
        h = Math.Min(h, workH);
        return (workLeft + (workW - w) / 2, workTop + (workH - h) / 2, w, h);
    }

    /// <summary>The minimum size (DIPs): the one the XAML asks for, but never more than 90% of the work area at the target monitor's scale (a 1920 x 1080 screen at 200% has 960 x 520 DIPs).</summary>
    internal static (double W, double H) MinSizeFor(double xamlMinW, double xamlMinH, double dpi, int workW, int workH) =>
        (Math.Min(xamlMinW, 0.9 * workW / dpi), Math.Min(xamlMinH, 0.9 * workH / dpi));

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>The scale (1.0 = 96 dpi) of the monitor the window is about to go to; the window's own scale when it cannot be told.</summary>
    private unsafe double ScaleOfTarget(EditorRequest request, RECT wa)
    {
        try
        {
            nint hm = request.Monitor != null ? request.Monitor.Handle : 0;
            if (hm == 0) hm = (nint)PInvoke.MonitorFromPoint(new System.Drawing.Point((wa.left + wa.right) / 2, (wa.top + wa.bottom) / 2), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value;
            if (hm != 0 && GetDpiForMonitor(hm, 0, out uint dx, out _) == 0 && dx > 0) return dx / 96.0;
        }
        catch (Exception ex) { Log.Warn("screenshot editor: monitor scale: " + ex.Message); }
        return ScreenPlacement.ScaleOf(this);
    }

    /// <summary>Centres the window on the monitor of the capture, sized to the picture within 90% of the work area (physical pixels; one window never changes size by moving).</summary>
    private void Place(EditorRequest request)
    {
        try
        {
            var hwnd = new HWND(new WindowInteropHelper(this).Handle);
            var wa = ScreenPlacement.WorkAreaOf(request.Monitor);
            double dpi = ScaleOfTarget(request, wa);
            var (minW, minH) = MinSizeFor(MinWidth, MinHeight, dpi, wa.right - wa.left, wa.bottom - wa.top);
            MinWidth = minW;
            MinHeight = minH;
            var img = request.Document.Base;
            var (x, y, w, h) = Place(img.Width, img.Height, dpi, wa.left, wa.top, wa.right - wa.left, wa.bottom - wa.top);
            for (int pass = 0; pass < 2; pass++)   // a move to a monitor with another DPI rescales the window: set it again
            {
                PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, w, h, SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                if (PInvoke.GetWindowRect(hwnd, out RECT rc) && rc.right - rc.left == w && rc.bottom - rc.top == h) break;
            }
        }
        catch (Exception ex) { Log.Warn("screenshot editor: placement: " + ex.Message); }
    }

    // ------------------------------------------------------------------ status

    private void Say(string text) => StatusText.Text = text;

    // ------------------------------------------------------------------ insert image

    private bool _inserting;

    /// <summary>The I key and the Insert image button: pick a recent screenshot, a file or the clipboard, and put it on the picture.</summary>
    private void OpenInsertPicker() => Run(OpenInsertPickerAsync);

    private async Task OpenInsertPickerAsync()
    {
        if (_session == null || _busy || _inserting) return;
        if (Canvas.IsTextEditing) Canvas.CommitText();
        try
        {
            bool canPaste = false;
            try { canPaste = Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"); } catch { }
            // the model reads the history and checks the files on a worker: nothing here touches the disk
            var picker = new InsertImagePicker(new InsertPickerModel(_request?.Recent), canPaste) { Owner = this };
            if (picker.ShowDialog() == true && _session != null)
            {
                var centre = Canvas.ViewCentreDoc;
                if (picker.PasteChosen) await InsertAsync(() => _session.PasteImageAsync(centre));
                else if (picker.PickedPath is { } path) await InsertAsync(() => _session.InsertImageFileAsync(path, centre));   // a refusal goes to the status line
            }
        }
        catch (Exception ex)
        {
            Log.Error("screenshot editor: insert image", ex);
            Say("Could not insert the image: " + ex.Message);
        }
        Canvas.Focus();
    }

    /// <summary>One insert at a time; the file is read and the picture reduced on a worker (the status line says so meanwhile).</summary>
    private async Task InsertAsync(Func<Task<(bool Ok, string? Error)>> insert)
    {
        if (_session == null || _inserting) return;
        _inserting = true;
        Say("Reading the picture…");
        try
        {
            var (ok, _) = await insert();
            if (ok) Say("");   // a refusal was said already (the session's notice)
        }
        finally { _inserting = false; }
    }

    /// <summary>Ctrl+V: the clipboard picture goes straight onto the picture.</summary>
    private void PasteImage()
    {
        if (_session == null || _busy) return;
        var centre = Canvas.ViewCentreDoc;
        Run(async () =>
        {
            if (_session != null) await InsertAsync(() => _session.PasteImageAsync(centre));
            Canvas.Focus();
        });
    }

    // ------------------------------------------------------------------ output

    /// <summary>Runs an async action from an event handler: nothing is lost silently, a failure is logged.</summary>
    private static async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.Error("screenshot editor", ex); }
    }

    private async Task CopyAsync()
    {
        if (_session == null || _busy) return;
        if (Canvas.IsTextEditing) Canvas.CommitText();
        SetBusy(true);   // the buttons are off while the picture is made
        Say("Copying…");
        try
        {
            var image = await _session.ExportAsync();
            // the bitmap and the PNG are made on a worker (a 4K picture is 33 MB); the clipboard itself needs this thread
            var (bitmap, png) = await Task.Run(() =>
            {
                var made = image.ToBitmapSource();
                return (made, PngFile.Encode(made));
            });
            bool ok = ScreenshotClipboard.Copy(bitmap, png);
            Say(ok ? "Copied to the clipboard" : "The clipboard is busy: not copied");
        }
        catch (Exception ex)
        {
            Log.Error("screenshot editor: copy", ex);
            Say("Could not copy: " + ex.Message);
        }
        finally { SetBusy(false); }
        Canvas.Focus();
    }

    /// <summary>Exports and saves through the service. True when the file was written; the editor stays as it is.</summary>
    internal async Task<bool> SaveCoreAsync(bool finish)
    {
        if (_session == null || _request == null || _busy) return false;
        if (Canvas.IsTextEditing) Canvas.CommitText();
        SetBusy(true);
        Say("Saving…");
        try
        {
            var doc = _session.Committed;
            var image = await _session.ExportAsync();
            if (_session == null || _request == null) return false;   // closed meanwhile
            bool ok = await _request.SaveAsync(image, doc, finish);
            if (_session == null) return ok;
            if (ok)
            {
                _everSaved = true;
                if (ReferenceEquals(_session.Committed, doc)) _session.MarkSaved();
                Say(finish ? "" : "Saved");
            }
            else Say("Could not save (the reason is in the notification)");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("screenshot editor: save", ex);
            Say("Could not save: " + ex.Message);
            return false;
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (!busy && _closeWhenIdle)
        {
            _closeWhenIdle = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Run(RequestCloseAsync));   // the close that came in meanwhile (Esc, Alt+F4, the close button)
        }
        ToolbarHost.IsEnabled = !busy;
        DoneButton.IsEnabled = !busy;
        DiscardButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        Canvas.IsEnabled = !busy;
    }

    private async Task DoneAsync()
    {
        if (_session == null || _request == null || _busy) return;
        if (Canvas.IsTextEditing) Canvas.CommitText();
        // A picture that came from a file and was not touched has nothing to save (a copy of it would only be a duplicate).
        if (!_request.IsNewCapture && !_everSaved && !_session.IsDirty) { CloseApproved(); return; }
        if (await SaveCoreAsync(finish: true)) CloseApproved();
    }

    /// <summary>The app is quitting (see <see cref="EditorWindowHost.ConfirmQuitAsync"/>). True = the quit may go on.</summary>
    internal async Task<bool> ResolveForQuitAsync(bool userInitiated)
    {
        _resolvingQuit = true;   // a close that was deferred while a save ran must not ask a second question of its own
        try { return await ResolveForQuitCoreAsync(userInitiated); }
        finally { _resolvingQuit = false; }
    }

    private bool _resolvingQuit;
    /// <summary>The quit's own question is being resolved: <see cref="RequestCloseAsync"/> stands aside (test seam).</summary>
    internal bool ResolvingQuit => _resolvingQuit;

    private async Task<bool> ResolveForQuitCoreAsync(bool userInitiated)
    {
        for (int i = 0; _busy && i < 100; i++) await Task.Delay(50);   // a save under way finishes first
        if (_session == null) return true;
        if (Canvas.IsTextEditing) Canvas.CommitText();
        if (!_session.IsDirty) return true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!userInitiated)
        {
            Log.Info("screenshot editor: unsaved edits at exit: saving a copy");
            if (!await SaveCoreAsync(finish: false)) Log.Warn("screenshot editor: the edits could not be saved at exit");
            return true;
        }
        Hotkeys.GrantForeground();
        Activate();
        var answer = MessageBox.Show(this, "Save your edits before Clink quits?\n\nYes saves the picture, No discards the edits, Cancel keeps Clink running.",
            "Clink screenshot editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes && !await SaveCoreAsync(finish: false)) return false;
        return true;
    }

    internal void CloseApproved()
    {
        _closeApproved = true;
        Close();
    }

    internal async Task RequestCloseAsync()
    {
        if (_session == null) return;
        if (_resolvingQuit) { _closeWhenIdle = false; return; }   // the quit asks (or saves) itself; a second question would double the prompt
        if (_busy) { _closeWhenIdle = true; return; }
        if (Canvas.IsTextEditing) Canvas.CommitText();
        if (_session.IsDirty)
        {
            var answer = MessageBox.Show(this, "Save your edits?\n\nYes saves the picture, No discards the edits, Cancel keeps editing.",
                "Clink screenshot editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes && !await SaveCoreAsync(finish: true)) return;
        }
        CloseApproved();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeApproved)
        {
            e.Cancel = true;   // Alt+F4, the taskbar, the close button: the same questions as Esc
            // Close() cannot be called from inside Closing: ask (and close) once this event is over
            if (_busy) _closeWhenIdle = true;
            else Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Run(RequestCloseAsync));
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _session?.FlushOptions(); } catch (Exception ex) { Log.Warn("screenshot editor: options: " + ex.Message); }
        Canvas.Detach();
        _session?.Release();
        _session = null;
        _request = null;
        base.OnClosed(e);
        // Clink runs for weeks: give back the big pictures now (one compaction of the large-object heap, not a habit).
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });
    }

    // ------------------------------------------------------------------ chrome and keys

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Run(RequestCloseAsync);
    private void Discard_Click(object sender, RoutedEventArgs e) => Run(RequestCloseAsync);
    private void Done_Click(object sender, RoutedEventArgs e) => Run(DoneAsync);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy) { e.Handled = true; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        var focus = Keyboard.FocusedElement;
        bool typing = Canvas.IsTextEditing || focus is TextBoxBase || focus is Slider || focus is ComboBox;
        if (ctrl && key == Key.S) { Run(() => SaveCoreAsync(finish: false)); e.Handled = true; return; }
        if (ctrl && key == Key.Return) { Run(DoneAsync); e.Handled = true; return; }
        if (key == Key.Escape && Toolbar.AdjustPopup.IsOpen) { Toolbar.AdjustPopup.IsOpen = false; e.Handled = true; return; }   // Esc closes the Adjust flyout and nothing else
        if (typing) return;
        if (ctrl && key == Key.C) { Run(CopyAsync); e.Handled = true; return; }
        if (key == Key.Space && !e.IsRepeat && !mods.HasFlag(ModifierKeys.Alt) && focus is not ButtonBase)
        {
            Canvas.SpaceDown = true;
            e.Handled = true;
            return;
        }
        if (_session == null) return;
        if (Canvas.HandleKey(key, mods)) { e.Handled = true; return; }
        if (key == Key.Escape && !ctrl)
        {
            Run(RequestCloseAsync);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.Space) Canvas.SpaceDown = false;
    }
}

/// <summary>Opens <see cref="ScreenshotEditorWindow"/>s for the screenshot service; one at a time.</summary>
internal sealed class EditorWindowHost : IScreenshotEditor
{
    private ScreenshotEditorWindow? _window;

    /// <summary>False only in a test: the window is made and counted but never shown (no focus, no key sent).</summary>
    internal bool ShowWindows = true;

    internal ScreenshotEditorWindow? Window => _window;

    public bool IsOpen => _window != null;

    public void Activate()
    {
        var w = _window;
        if (w == null || !ShowWindows) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        // A hotkey (low-level hook) gives no right to take the foreground: ask for it first (a no-op while a game is in front)
        Hotkeys.GrantForeground();
        w.Activate();
    }

    /// <summary>Opens the editor; false (and the open one is brought forward) when there is one already: never two windows.</summary>
    public Task<bool> EditAsync(EditorRequest request)
    {
        if (_window != null)
        {
            Log.Info("screenshot editor: already open: brought forward, not opened a second time");
            Activate();
            return Task.FromResult(false);
        }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new ScreenshotEditorWindow(request);
        _window = window;
        window.Closed += (_, _) =>
        {
            _window = null;
            done.TrySetResult(true);
        };
        if (ShowWindows)
        {
            try
            {
                window.Show();
                Hotkeys.GrantForeground();
                window.Activate();
            }
            catch
            {
                _window = null;   // not stuck "open" with a window nobody sees
                try { window.Close(); } catch { }
                throw;
            }
        }
        return done.Task;
    }

    /// <summary>
    /// The app is quitting: with unsaved edits the editor comes forward and asks (a user's quit can be cancelled: false); an update
    /// restart or a session end cannot be refused: a copy of the edits is saved and the quit goes on. True = go on quitting.
    /// </summary>
    public async Task<bool> ConfirmQuitAsync(bool userInitiated)
    {
        var w = _window;
        return w == null || await w.ResolveForQuitAsync(userInitiated);
    }
}
