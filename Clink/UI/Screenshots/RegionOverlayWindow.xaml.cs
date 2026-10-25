using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Screenshots.Sources;
using Clink.Services;

namespace Clink.UI.Screenshots;

/// <summary>
/// One borderless, opaque, topmost window per monitor that shows that monitor's frozen picture, dimmed outside the selection, with
/// the size label and the loupe. Positioned in physical pixels with SetWindowPos (two passes, because moving a window to a monitor
/// with another DPI rescales it) and checked against the monitor rectangle; the picture is sized so one image pixel is one screen
/// pixel. The pointer is read with GetCursorPos (physical), never from WPF's DIP positions. Selection state lives in the shared
/// <see cref="RegionSelection"/>; every window draws the part of it on its own monitor, so a drag may cross monitors. Kept out of
/// screen captures (a replay that is being recorded shows the live game, not this).
/// </summary>
public partial class RegionOverlayWindow : Window, ICaptureExcludedWindow
{
    private const double IdleDim = 0.30, SelectionDim = 0.60;
    private const int LoupeCells = 16;   // source pixels across the loupe: 128 DIP / 16 = 8x
    private static readonly SolidColorBrush s_idleDim = FrozenBrush(Color.FromArgb((byte)(IdleDim * 255), 0, 0, 0));
    private static readonly SolidColorBrush s_selectionDim = FrozenBrush(Color.FromArgb((byte)(SelectionDim * 255), 0, 0, 0));

    private readonly FrozenMonitor _mon;
    private readonly int _index;
    private readonly RegionSelection _sel;
    private readonly bool _loupe;
    private readonly bool _takeFocus;
    private readonly Action _forceClose;
    private readonly BitmapSource _bitmap;
    private readonly ImageBrush _loupeBrush;
    private bool _placing;

    static RegionOverlayWindow() => CaptureExclusion.Register();

    private static SolidColorBrush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <param name="forceClose">Closes every overlay window of this pick at once (Esc after the selection already ended, when nothing else would).</param>
    internal RegionOverlayWindow(FrozenMonitor monitor, int index, RegionSelection selection, bool loupe, bool takeFocus, Action forceClose)
    {
        InitializeComponent();
        _mon = monitor;
        _index = index;
        _sel = selection;
        _loupe = loupe;
        _takeFocus = takeFocus;
        _forceClose = forceClose;
        ShowActivated = takeFocus;
        _bitmap = monitor.Image.ToBitmapSource();   // 96 dpi: one pixel is one unit of an absolute brush viewbox
        Frozen.Source = _bitmap;
        _loupeBrush = new ImageBrush(_bitmap) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, Viewbox = new Rect(0, 0, LoupeCells, LoupeCells) };
        LoupeView.Fill = _loupeBrush;
        // A first guess at the size before the window exists; Place() makes it exact.
        Width = Math.Max(1, monitor.Image.Width);
        Height = Math.Max(1, monitor.Image.Height);

        SourceInitialized += (_, _) =>
        {
            ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
            Place();
        };
        Loaded += (_, _) => { Place(); Layout(); };
        DpiChanged += (_, _) => { Place(); Layout(); };
        _sel.Changed += OnSelectionChanged;
        Closed += (_, _) =>
        {
            _sel.Changed -= OnSelectionChanged;
            ReleaseEditing();
            Frozen.Source = null;
            LoupeView.Fill = null;
        };
    }

    /// <summary>The user clicked this window while another one is editing: the keyboard goes back to the editing window (set by the pick).</summary>
    internal Action? InertClick;

    /// <summary>Physical pixels per DIP of this window's monitor.</summary>
    private double Scale => ScreenPlacement.ScaleOf(this);

    // ------------------------------------------------------------------ placement

    /// <summary>Puts the window exactly on its monitor (physical pixels, topmost, not activating) and checks it; again when the first move changed the DPI.</summary>
    private void Place()
    {
        if (_placing) return;
        _placing = true;
        try
        {
            var hwnd = ScreenPlacement.HwndOf(this);
            if (hwnd.IsNull) return;
            var b = _mon.Bounds;
            RECT rc = default;
            for (int pass = 0; pass < 3; pass++)
            {
                PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                PInvoke.GetWindowRect(hwnd, out rc);
                if (rc.left == b.X && rc.top == b.Y && rc.right - rc.left == b.Width && rc.bottom - rc.top == b.Height) return;
            }
            Log.Warn($"screenshot: overlay for {_mon.Monitor.ShortName} is at {rc.left},{rc.top} {rc.right - rc.left}x{rc.bottom - rc.top}, wanted {b}");
        }
        finally { _placing = false; }
    }

    /// <summary>Sizes the picture to the monitor in DIPs and redraws.</summary>
    private void Layout()
    {
        double s = Scale;
        Frozen.Width = _mon.Image.Width / s;
        Frozen.Height = _mon.Image.Height / s;
        Canvas.SetLeft(Frozen, 0);
        Canvas.SetTop(Frozen, 0);
        if (_editCanvas != null) { SizeEditCanvas(_editCanvas); PlaceToolbar(); }
        Redraw();
    }

    // ------------------------------------------------------------------ drawing

    private void OnSelectionChanged() => Redraw();

    private void Redraw()
    {
        if (_editing) return;   // the editor canvas draws the dim outside the crop itself
        if (!IsLoaded && PresentationSource.FromVisual(this) == null) return;
        double s = Scale;
        double areaW = _mon.Image.Width / s, areaH = _mon.Image.Height / s;
        var full = new Rect(0, 0, areaW, areaH);
        var part = _sel.Selection.Intersect(_mon.Bounds);

        if (part.IsEmpty)
        {
            Dim.Data = new RectangleGeometry(full);
            Dim.Fill = s_idleDim;
            Frame.Visibility = SizeBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            var r = SelectionMath.ToDip(part, _mon.Bounds, s);
            var rect = new Rect(r.X, r.Y, r.Width, r.Height);
            Dim.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { new RectangleGeometry(full), new RectangleGeometry(rect) } };
            Dim.Fill = s_selectionDim;
            Canvas.SetLeft(Frame, r.X); Canvas.SetTop(Frame, r.Y);
            Frame.Width = r.Width; Frame.Height = r.Height;
            Frame.Visibility = Visibility.Visible;

            // The size label sits on the monitor the pointer is on (that is where the eyes are), measured for the whole selection.
            if (SelectionMath.MonitorIndexAt(_sel.CursorX, _sel.CursorY, _sel.Monitors) == _index)
            {
                SizeText.Text = SelectionMath.SizeLabel(_sel.Selection);
                SizeBox.Visibility = Visibility.Visible;
                SizeBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var (lx, ly) = SelectionMath.PlaceLabel(r, SizeBox.DesiredSize.Width, SizeBox.DesiredSize.Height, areaW, areaH);
                Canvas.SetLeft(SizeBox, lx); Canvas.SetTop(SizeBox, ly);
            }
            else SizeBox.Visibility = Visibility.Collapsed;
        }
        RedrawLoupe(s, areaW, areaH);
    }

    private void RedrawLoupe(double s, double areaW, double areaH)
    {
        bool here = _loupe && !_sel.Finished && SelectionMath.MonitorIndexAt(_sel.CursorX, _sel.CursorY, _sel.Monitors) == _index;
        if (!here) { LoupeBox.Visibility = Visibility.Collapsed; return; }
        int lx = Math.Clamp(_sel.CursorX - _mon.Bounds.X, 0, _mon.Image.Width - 1);
        int ly = Math.Clamp(_sel.CursorY - _mon.Bounds.Y, 0, _mon.Image.Height - 1);
        // Centre the zoom on the pixel under the pointer: that pixel spans [lx, lx + 1), so the view starts half a cell before its middle.
        _loupeBrush.Viewbox = new Rect(lx + 0.5 - LoupeCells / 2.0, ly + 0.5 - LoupeCells / 2.0, LoupeCells, LoupeCells);
        var (b, g, r, _) = _mon.Image.GetPixel(lx, ly);
        LoupeText.Text = $"{_sel.CursorX}, {_sel.CursorY}  #{r:X2}{g:X2}{b:X2}";
        LoupeBox.Visibility = Visibility.Visible;
        LoupeBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = LoupeBox.DesiredSize;
        var (x, y) = SelectionMath.PlaceLoupe(lx / s, ly / s, size.Width, size.Height, areaW, areaH);
        Canvas.SetLeft(LoupeBox, x); Canvas.SetTop(LoupeBox, y);
    }

    // ------------------------------------------------------------------ input

    private static bool TryCursor(out int x, out int y)
    {
        if (PInvoke.GetCursorPos(out System.Drawing.Point p)) { x = p.X; y = p.Y; return true; }
        x = y = 0;
        return false;
    }

    private static bool ShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editing) return;   // the editor canvas and the toolbar have it
        if (_sel.Finished)
        {
            // another monitor's window is editing: this one is inert; a click only gives the keyboard back to the editing window
            InertClick?.Invoke();
            e.Handled = true;
            return;
        }
        if (!TryCursor(out int x, out int y)) return;
        CaptureMouse();   // the drag continues over the other monitors' windows
        _sel.BeginDrag(x, y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_sel.Finished || !TryCursor(out int x, out int y)) return;
        _sel.MoveCursor(x, y, ShiftDown);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        // The keyboard follows the pointer between the monitors' windows: once, when it comes onto this one (not on every move).
        if (!_sel.Finished && !IsActive && Mouse.Captured == null) Activate();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_sel.Dragging) return;
        try
        {
            if (TryCursor(out int x, out int y)) _sel.EndDrag(x, y, ShiftDown);
        }
        finally { if (IsMouseCaptured) ReleaseMouseCapture(); }   // after EndDrag: losing the capture mid-drag clears the band
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_editing) return;
        if (IsMouseCaptured) { _sel.RightClick(); ReleaseMouseCapture(); }
        else _sel.RightClick();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_sel.Dragging && !_sel.Finished && Mouse.Captured == null) _sel.RightClick();   // taken away mid-drag (another app): drop the band
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editing) return;   // the editing phase's keys are handled in OnPreviewKeyDown
        switch (e.Key)
        {
            case Key.Escape:
                // Normally a cancel. When the selection has ended already and the windows are still up (whatever went wrong on the
                // way out), Esc closes them all regardless: the user is never left behind an opaque topmost picture.
                if (_sel.Finished) _forceClose(); else _sel.Cancel();
                e.Handled = true;
                break;
            case Key.Enter: case Key.Space: _sel.Confirm(); e.Handled = true; break;
            case Key.Left: case Key.Right: case Key.Up: case Key.Down:
                // Precise starts without a steady mouse hand: the arrow keys move the pointer 1 px (Shift 10); not once the selection has ended.
                if (!_sel.Finished && TryCursor(out int x, out int y))
                {
                    int step = ShiftDown ? 10 : 1;
                    x += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                    y += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                    x = Math.Clamp(x, _sel.VirtualBounds.X, _sel.VirtualBounds.Right - 1);
                    y = Math.Clamp(y, _sel.VirtualBounds.Y, _sel.VirtualBounds.Bottom - 1);
                    PInvoke.SetCursorPos(x, y);
                    _sel.MoveCursor(x, y, ShiftDown);
                }
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ focus

    /// <summary>Takes the keyboard (Esc / Enter): the window is shown without activating, so ask for the foreground; over the game only a click can.</summary>
    internal void TakeKeyboard()
    {
        try
        {
            var hwnd = ScreenPlacement.HwndOf(this);
            if (!hwnd.IsNull && PInvoke.GetForegroundWindow() != hwnd)
            {
                Hotkeys.GrantForeground();   // no-op over the detected game
                PInvoke.SetForegroundWindow(hwnd);
            }
            Activate();
            Focus();
        }
        catch (Exception ex) { Log.Warn("screenshot: overlay focus: " + ex.Message); }
    }

    internal bool WantsKeyboard => _takeFocus;

    // ------------------------------------------------------------------ the editing phase (mode "before")

    private bool _editing, _editLocked, _inserting;
    private OverlayEditState? _state;
    private OverlayEditSetup? _setup;
    private EditorSession? _edit;
    private EditorCanvas? _editCanvas;
    private EditorToolbar? _toolbar;
    private Border? _toolbarHost, _noticeHost;
    private TextBlock? _noticeText;
    private System.Windows.Threading.DispatcherTimer? _noticeTimer;
    private Action<DocumentState>? _onDone;
    private Action? _onCancel, _onInput;
    private Action<Exception>? _onFail;

    internal bool IsEditing => _editing;

    /// <summary>
    /// The selection is final and inside this monitor: this window now edits it. An <see cref="EditorSession"/> over the monitor's frozen
    /// picture (crop = the selection) is shown by an <see cref="EditorCanvas"/> in overlay mode (1:1, crop handles on, the dim outside
    /// the crop drawn by the canvas) with a compact <see cref="EditorToolbar"/> floating next to the crop. Done / Enter calls
    /// <paramref name="onDone"/> with the document; Esc, Cancel <paramref name="onCancel"/>; every input <paramref name="onInput"/> (the idle
    /// watchdog); an exception <paramref name="onFail"/>.
    /// </summary>
    internal void BeginEditing(OverlayEditState state, OverlayEditSetup setup, Action<DocumentState> onDone, Action onCancel, Action onInput, Action<Exception> onFail)
    {
        _state = state;
        _setup = setup;
        _onDone = onDone; _onCancel = onCancel; _onInput = onInput; _onFail = onFail;
        _editing = true;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Dim.Visibility = Frame.Visibility = SizeBox.Visibility = LoupeBox.Visibility = Frozen.Visibility = Visibility.Collapsed;

        var doc = DocumentState.Create(_mon.Image).WithCrop(state.Crop);
        var edit = new EditorSession(doc, setup.Options, setup.PersistOptions);
        edit.Notice += ShowNotice;
        _edit = edit;

        var canvas = new EditorCanvas { OverlayMode = true, UseLayoutRounding = true, SnapsToDevicePixels = true };
        SizeEditCanvas(canvas);
        _editCanvas = canvas;
        Root.Children.Add(canvas);
        canvas.Attach(edit);
        canvas.InsertImageRequested += OpenInsertPicker;
        canvas.PasteRequested += PasteImage;

        var toolbar = new EditorToolbar { Compact = true };
        toolbar.Attach(edit);
        toolbar.FocusCanvasRequested += () => { if (_editing && !_editLocked) canvas.Focus(); };
        toolbar.InsertRequested += OpenInsertPicker;
        toolbar.DoneRequested += CommitEdit;
        toolbar.CancelRequested += CancelEdit;
        _toolbar = toolbar;
        var host = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Child = toolbar, Cursor = Cursors.Arrow, SnapsToDevicePixels = true };
        host.SetResourceReference(Border.BackgroundProperty, "Bg");
        host.SetResourceReference(Border.BorderBrushProperty, "Border");
        host.SizeChanged += (_, _) => PlaceToolbar();
        _toolbarHost = host;
        Root.Children.Add(host);

        edit.StateChanged += () => state.SetCrop(edit.State.Crop);
        state.CropChanged += PlaceToolbar;
        PlaceToolbar();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PlaceToolbar);   // once measured for real
        TakeKeyboard();
        canvas.Focus();
    }

    private void SizeEditCanvas(EditorCanvas canvas)
    {
        double s = Scale;
        canvas.Width = _mon.Image.Width / s;
        canvas.Height = _mon.Image.Height / s;
        Canvas.SetLeft(canvas, 0);
        Canvas.SetTop(canvas, 0);
    }

    /// <summary>Puts the toolbar below the crop, else above it, else inside at the bottom (<see cref="OverlayEditState.ToolbarPosition"/>), in whole physical pixels.</summary>
    private void PlaceToolbar()
    {
        if (_toolbarHost == null || _state == null || !_state.IsEditing) return;
        _toolbarHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = _toolbarHost.DesiredSize;
        double s = Scale;
        var (x, y) = _state.ToolbarPosition(((int)Math.Ceiling(d.Width * s), (int)Math.Ceiling(d.Height * s)));
        Canvas.SetLeft(_toolbarHost, x / s);
        Canvas.SetTop(_toolbarHost, y / s);
    }

    /// <summary>Enter / Done: ends the editing with the document (a text box that is open is committed first).</summary>
    private void CommitEdit()
    {
        if (!_editing || _editLocked || _edit == null || _editCanvas == null) return;
        try
        {
            if (_editCanvas.IsTextEditing) _editCanvas.CommitText();
            if (_edit.HasPreview) _edit.CommitPreview();
            _editLocked = true;   // nothing more is edited while the pick hands the document over
            _editCanvas.IsEnabled = false;
            _toolbar!.IsEnabled = false;
            _onDone?.Invoke(_edit.Committed);
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Esc with nothing left to drop or deselect, and the Cancel button: the whole capture ends with nothing (no question: nothing was saved, the edits are in memory only).</summary>
    private void CancelEdit()
    {
        if (!_editing || _editLocked) return;
        try { _onCancel?.Invoke(); }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        Log.Error("screenshot: overlay editing", ex);
        _editLocked = true;
        _onFail?.Invoke(ex);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_editing || _editCanvas == null) return;
        try
        {
            _onInput?.Invoke();
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_editLocked)
            {
                // the document is being handed over: nothing is edited, but Esc still closes the windows (never stranded behind them)
                if (key == Key.Escape) _forceClose();
                e.Handled = true;
                return;
            }
            var mods = Keyboard.Modifiers;
            bool ctrl = mods.HasFlag(ModifierKeys.Control);
            var focus = Keyboard.FocusedElement;
            bool typing = _editCanvas.IsTextEditing || focus is System.Windows.Controls.Primitives.TextBoxBase || focus is Slider || focus is ComboBox;
            // Esc inside the Adjust flyout closes the flyout and nothing else
            if (key == Key.Escape && _toolbar?.AdjustPopup.IsOpen == true) { _toolbar.AdjustPopup.IsOpen = false; e.Handled = true; return; }
            bool isButtonBase = focus is System.Windows.Controls.Primitives.ButtonBase;
            // Enter presses only a real button (a toggle takes Space); Space presses any button base
            bool pressesEnter = focus is System.Windows.Controls.Button || (focus is DependencyObject fo && KeyboardNavigation.GetAcceptsReturn(fo));
            var kind = OverlayEditKeys.Classify(key, typing, isButtonBase, pressesEnter);
            switch (OverlayEditKeys.Decide(key, mods, _editCanvas.HasGesture, _edit?.Selected != null, (_edit?.Tool ?? EditorTool.Select) == EditorTool.Select, kind, e.IsRepeat))
            {
                case OverlayKeyAction.Pass: return;
                case OverlayKeyAction.Commit: CommitEdit(); e.Handled = true; return;
                case OverlayKeyAction.Cancel: CancelEdit(); e.Handled = true; return;
                case OverlayKeyAction.Swallow: e.Handled = true; return;
            }
            // the canvas: Esc drops a gesture, then deselects, then back to Select; when it has none of that the capture is cancelled
            if (_editCanvas.HandleKey(key, mods)) { e.Handled = true; return; }
            if (key == Key.Escape && !ctrl) { CancelEdit(); e.Handled = true; }
        }
        catch (Exception ex) { Fail(ex); }
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_editing) _onInput?.Invoke();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (_editing) _onInput?.Invoke();
    }

    // ---- insert image, paste, notices

    private void OpenInsertPicker() => _ = OpenInsertPickerAsync();

    private async Task OpenInsertPickerAsync()
    {
        var edit = _edit;
        var canvas = _editCanvas;
        if (!_editing || _editLocked || edit == null || canvas == null || _inserting) return;
        _inserting = true;
        _state?.BeginInsert();
        try
        {
            if (canvas.IsTextEditing) canvas.CommitText();
            bool canPaste = false;
            try { canPaste = Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"); } catch { }
            var picker = new InsertImagePicker(new InsertPickerModel(_setup?.Recent), canPaste) { Owner = this };
            picker.SourceInitialized += (_, _) => ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(picker).Handle);   // like the overlay: not in a replay clip
            bool chosen;
            _state?.Suspend();   // the picker and the file dialog take the input: no idle cancel while they are open
            try { chosen = picker.ShowDialog() == true; }
            finally { _state?.Resume(); }
            if (chosen && _edit == edit)
            {
                var centre = canvas.ViewCentreDoc;
                if (picker.PasteChosen) await edit.PasteImageAsync(centre);
                else if (picker.PickedPath is { } path) await edit.InsertImageFileAsync(path, centre);   // a refusal goes to the notice
            }
        }
        catch (Exception ex) { Log.Error("screenshot: overlay insert image", ex); }
        finally { _inserting = false; }   // (the picker, and the file dialog it opened, are closed by now)
        if (_state?.EndInsert() == true) InsertEnded?.Invoke();   // a cancel that came while the dialogs were modal runs now
        if (_editing && !_editLocked) _editCanvas?.Focus();
    }

    /// <summary>Called when an insert / paste ended and a close came meanwhile: the session runs it now.</summary>
    internal Action? InsertEnded { get; set; }

    private void PasteImage()
    {
        var edit = _edit;
        var canvas = _editCanvas;
        if (!_editing || _editLocked || edit == null || canvas == null || _inserting) return;
        _ = PasteAsync(edit, canvas);
    }

    private async Task PasteAsync(EditorSession edit, EditorCanvas canvas)
    {
        _inserting = true;
        _state?.BeginInsert();
        try { await edit.PasteImageAsync(canvas.ViewCentreDoc); }
        catch (Exception ex) { Log.Error("screenshot: overlay paste", ex); }
        finally { _inserting = false; }
        if (_state?.EndInsert() == true) InsertEnded?.Invoke();
        InsertEnded?.Invoke();
    }

    /// <summary>The session's short messages ("Could not read that picture", "Preview failed") in a small box at the bottom of the monitor for a few seconds.</summary>
    private void ShowNotice(string text)
    {
        if (!_editing) return;
        if (_noticeHost == null)
        {
            _noticeText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
            _noticeText.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            _noticeHost = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 7, 12, 7), BorderThickness = new Thickness(1), Child = _noticeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _noticeHost.SetResourceReference(Border.BackgroundProperty, "Panel");
            _noticeHost.SetResourceReference(Border.BorderBrushProperty, "Border");
            _noticeTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
            {
                _noticeTimer?.Stop();
                if (_noticeHost != null) _noticeHost.Visibility = Visibility.Collapsed;
            }, Dispatcher);
            _noticeTimer.Stop();
            Root.Children.Add(_noticeHost);
            _noticeHost.SizeChanged += (_, _) =>
            {
                double s = Scale;
                Canvas.SetLeft(_noticeHost, Math.Max(0, (_mon.Image.Width / s - _noticeHost.ActualWidth) / 2));
                Canvas.SetTop(_noticeHost, Math.Max(0, _mon.Image.Height / s - _noticeHost.ActualHeight - 24));
            };
        }
        if (string.IsNullOrWhiteSpace(text)) { _noticeHost.Visibility = Visibility.Collapsed; _noticeTimer?.Stop(); return; }
        _noticeText!.Text = text;
        _noticeHost.Visibility = Visibility.Visible;
        _noticeTimer?.Stop();
        _noticeTimer?.Start();
    }

    /// <summary>The window is closing: the editor session lets go of its pictures, the tool options are written back.</summary>
    private void ReleaseEditing()
    {
        _noticeTimer?.Stop();
        var edit = _edit;
        _edit = null;
        _editing = false;
        _editLocked = true;
        if (_state != null) _state.CropChanged -= PlaceToolbar;
        try { _editCanvas?.Detach(); } catch (Exception ex) { Log.Warn("screenshot: overlay editor detach: " + ex.Message); }
        if (edit == null) return;
        try { edit.FlushOptions(); } catch (Exception ex) { Log.Warn("screenshot: overlay editor options: " + ex.Message); }
        try { edit.Release(); } catch (Exception ex) { Log.Warn("screenshot: overlay editor release: " + ex.Message); }
    }
}

/// <summary>
/// Opens a <see cref="RegionOverlayWindow"/> on every monitor over a frozen desktop and reports the region chosen (<see cref="IRegionPicker"/>).
/// Every way out ends the pick exactly once and never strands a window: a commit (the windows stay until <see cref="Close"/>), a
/// cancel (Esc / right-click / <see cref="Cancel"/> / the token: the windows close), one window closed from outside (Alt+F4: a
/// cancel, so the others close too), a real change of the monitor layout, and <see cref="IdleTimeout"/> without any input (the
/// overlay over a game that kept the keyboard and the mouse).
/// </summary>
internal sealed class RegionOverlayPicker : IRegionPicker
{
    /// <summary>No pointer move, drag or key for this long: the pick is cancelled.</summary>
    internal static readonly TimeSpan IdleTimeout = OverlayEditState.SelectingIdleTimeout;

    private readonly Action<string, string>? _notice;

    /// <param name="notice">Shows a short toast (title, text): the watchdog uses it when it cancels an editing session.</param>
    public RegionOverlayPicker(Action<string, string>? notice = null) => _notice = notice;

    private Session? _session;

    public Task<RegionPickResult?> PickAsync(FrozenDesktop desktop, bool loupe, CancellationToken ct, OverlayEditSetup? edit = null)
    {
        if (ct.IsCancellationRequested) return Task.FromResult<RegionPickResult?>(null);
        Close();   // a previous pick the caller did not close
        var session = new Session(desktop, loupe, edit, _notice);
        _session = session;
        return session.Start(ct);
    }

    public void Cancel() => _session?.Cancel();

    public void Close()
    {
        var s = _session;
        _session = null;
        s?.CloseWindows();
    }

    /// <summary>One pick: its selection, its windows and the ways it can end.</summary>
    private sealed class Session
    {
        private readonly FrozenDesktop _desktop;
        private readonly bool _loupe;
        private readonly RegionSelection _selection;
        private readonly List<RegionOverlayWindow> _windows = new();
        // Asynchronous continuations: the caller's await never runs inside Finish (it closes the windows, which re-enters here).
        private readonly TaskCompletionSource<RegionPickResult?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
        private EventHandler? _displayChanged;
        private System.Windows.Threading.DispatcherTimer? _watchdog;
        private CancellationTokenRegistration _ctRegistration;
        private readonly OverlayEditSetup? _editSetup;
        private readonly OverlayEditState _state;
        private bool _done, _closed;

        private readonly Action<string, string>? _notice;

        public Session(FrozenDesktop desktop, bool loupe, OverlayEditSetup? edit, Action<string, string>? notice = null)
        {
            _notice = notice;
            _desktop = desktop;
            _loupe = loupe;
            _editSetup = edit;
            _selection = new RegionSelection(desktop.Monitors.Select(m => m.Bounds).ToList());
            _state = new OverlayEditState(_selection.Monitors, editBefore: edit != null);
        }

        public Task<RegionPickResult?> Start(CancellationToken ct)
        {
            var selection = _selection;
            int focus = 0;
            if (TryGetCursor(out int px, out int py)) focus = Math.Max(0, SelectionMath.MonitorIndexAt(px, py, selection.Monitors));

            selection.Changed += _state.NoteInput;   // every pointer move, drag and key goes through Changed
            selection.Committed += r => _dispatcher.BeginInvoke(() => OnSelectionCommitted(r));
            selection.Cancelled += () => _dispatcher.BeginInvoke(() => Finish(null, close: true));
            _displayChanged = (_, _) => _dispatcher.BeginInvoke(() => OnDisplayChanged());
            SystemEvents.DisplaySettingsChanged += _displayChanged;
            _ctRegistration = ct.Register(() => _dispatcher.BeginInvoke(() => Cancel()));
            _watchdog = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Background, (_, _) => CheckIdle(), _dispatcher);

            try
            {
                for (int i = 0; i < _desktop.Monitors.Count; i++)
                {
                    var w = new RegionOverlayWindow(_desktop.Monitors[i], i, selection, _loupe, takeFocus: i == focus, forceClose: ForceClose);
                    // Closed from outside (Alt+F4, a crash of one window): the whole pick is cancelled and the other windows close.
                    w.Closed += (_, _) =>
                    {
                        if (!selection.Finished) selection.Cancel();
                        else if (_state.IsEditing && !_done && !_closed) ForceClose();   // while editing: one window gone ends the capture, the others close
                        else if (!_done && !_closed && !_state.IsEnded && _editSetup != null) ForceClose();   // closed between the commit and its dispatch (the editing phase has not begun)
                    };
                    w.InsertEnded = ForceClose;   // a cancel that came while the insert-image dialogs were modal
                    _windows.Add(w);
                }
                foreach (var w in _windows.Where(w => !w.WantsKeyboard)) w.Show();
                var main = _windows[focus];
                main.Show();
                main.TakeKeyboard();
                if (TryGetCursor(out int cx, out int cy)) selection.MoveCursor(cx, cy);
                _watchdog.Start();
            }
            catch (Exception ex)
            {
                Log.Error("screenshot: overlay", ex);
                Finish(null, close: true);
                throw;
            }
            return _tcs.Task;
        }

        /// <summary>
        /// The selection was committed (on the dispatcher, after the mouse-up / key that ended it). Without editing, or over several
        /// monitors, the pick completes (windows stay for the caller); inside one monitor in mode "before" that monitor's window edits it.
        /// </summary>
        private void OnSelectionCommitted(RectI r)
        {
            if (_done || _closed) return;   // (a window closed between the commit and this dispatch)
            int index = SelectionMath.MonitorIndexOf(r, _selection.Monitors);
            switch (_state.SelectionCommitted(r))
            {
                case OverlayPhase.Done:
                    Finish(new RegionPickResult(r, index, _selection.Outcome), close: false);
                    break;
                case OverlayPhase.SpansMonitors:
                    Finish(new RegionPickResult(r, index, _selection.Outcome, null, SpansMonitors: true), close: false);
                    break;
                case OverlayPhase.Editing:
                    try
                    {
                        int at = _state.MonitorIndex;
                        for (int i = 0; i < _windows.Count; i++)
                            if (i != at) _windows[i].InertClick = () => { if (_state.IsEditing && !_closed) _windows[at].TakeKeyboard(); };
                        _windows[at].BeginEditing(_state, _editSetup!, OnEditDone, Cancel, () => _state.NoteInput(), FailEditing);                    }
                    catch (Exception ex) { FailEditing(ex); }
                    break;
                default:
                    Finish(null, close: true);
                    break;
            }
        }

        /// <summary>Done / Enter in the editing window: the pick completes with the edited document and its final crop (virtual-screen pixels).</summary>
        private void OnEditDone(DocumentState doc)
        {
            if (_done) return;
            _state.SetCrop(doc.Crop);
            if (!_state.Confirm()) return;
            Finish(new RegionPickResult(_state.CropVirtual, _state.MonitorIndex, _selection.Outcome, doc), close: false);
        }

        /// <summary>The editing phase failed: the capture ends, every window closes, and the caller is told (it reports it).</summary>
        private void FailEditing(Exception ex)
        {
            Log.Error("screenshot: overlay editing", ex);
            _state.Cancel();
            if (!_done)
            {
                _done = true;
                StopWatching();
                _tcs.TrySetException(ex);
            }
            CloseWindows();
        }

        /// <summary>A second trigger, the token: a cancel; when the selection has ended already, the windows are closed regardless.</summary>
        public void Cancel()
        {
            if (!_selection.Finished) _selection.Cancel();
            else ForceClose();
        }

        /// <summary>Ends the pick with nothing (if it has not ended) and closes every window.</summary>
        private void ForceClose()
        {
            if (_state.TryDeferClose()) return;   // the insert-image picker / file dialog is modal: the close runs when it returns
            Finish(null, close: true);
        }

        /// <summary>Ends the pick once (later calls only close, when asked to); a commit leaves the windows to the caller.</summary>
        private void Finish(RegionPickResult? result, bool close)
        {
            if (!_done)
            {
                _done = true;
                if (result == null) _state.Cancel();
                StopWatching();
                _tcs.TrySetResult(result);
            }
            if (close) CloseWindows();
        }

        private void StopWatching()
        {
            if (_displayChanged != null) { SystemEvents.DisplaySettingsChanged -= _displayChanged; _displayChanged = null; }
            _watchdog?.Stop();
            _ctRegistration.Dispose();
        }

        /// <summary>Closes every window, once; the pick ends with nothing if it had not ended (no caller is ever left waiting).</summary>
        public void CloseWindows()
        {
            if (_closed) return;
            if (_state.TryDeferClose()) return;   // (see ForceClose)
            _closed = true;
            foreach (var w in _windows.ToList()) { try { w.Close(); } catch (Exception ex) { Log.Warn("screenshot: overlay close: " + ex.Message); } }
            if (!_done) { _done = true; StopWatching(); _tcs.TrySetResult(null); }
            // Clink runs for weeks: after an editing session (a 4K monitor picture and its undo states) give the big pictures back now,
            // like the editor window does (one compaction of the large-object heap).
            // (a finished pick: ScreenshotService compacts after it has saved and dropped the document)
            if (_state.MonitorIndex >= 0 && _state.Phase != OverlayPhase.Done)
                _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, ScreenshotService.CompactMemory);
        }

        /// <summary>
        /// A display change cancels only when the monitors really are elsewhere now: an exclusive-fullscreen game that drops out of
        /// exclusive mode as the overlay appears also raises it, with the layout unchanged, and must not make the overlay flash away.
        /// </summary>
        private void OnDisplayChanged()
        {
            if (_done || (_selection.Finished && !_state.IsEditing)) return;
            List<RectI> now;
            try { now = Clink.Native.Monitors.Enumerate().Select(m => new RectI(m.Left, m.Top, m.Width, m.Height)).ToList(); }
            catch (Exception ex) { Log.Warn("screenshot: monitors after a display change: " + ex.Message); now = new(); }
            if (SelectionMath.SameLayout(_selection.Monitors, now))
            {
                Log.Info("screenshot: display settings changed, the monitors did not: the overlay stays");
                return;
            }
            Log.Info("screenshot: the monitor layout changed: cancelled");
            Cancel();
        }

        private void CheckIdle()
        {
            if (_done) { _watchdog?.Stop(); return; }
            var limit = _state.IdleLimit;   // 2 minutes choosing the area, 20 while editing
            if (!_state.IsIdle(limit)) return;
            bool editing = _state.IsEditing;
            Log.Info($"screenshot: no input for {limit.TotalMinutes:0} minutes: cancelled");
            if (editing) { try { _notice?.Invoke("Screenshot cancelled", $"Screenshot cancelled after {limit.TotalMinutes:0} minutes without input"); } catch (Exception ex) { Log.Warn("screenshot: idle notice: " + ex.Message); } }
            Cancel();
        }
    }

    private static bool TryGetCursor(out int x, out int y)
    {
        if (PInvoke.GetCursorPos(out System.Drawing.Point p)) { x = p.X; y = p.Y; return true; }
        x = y = 0;
        return false;
    }
}
