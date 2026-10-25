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
            Frozen.Source = null;
            LoupeView.Fill = null;
        };
    }

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
        Redraw();
    }

    // ------------------------------------------------------------------ drawing

    private void OnSelectionChanged() => Redraw();

    private void Redraw()
    {
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
                // Precise starts without a steady mouse hand: the arrow keys move the pointer 1 px (Shift 10).
                if (TryCursor(out int x, out int y))
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
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private Session? _session;

    public Task<RegionPickResult?> PickAsync(FrozenDesktop desktop, bool loupe, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult<RegionPickResult?>(null);
        Close();   // a previous pick the caller did not close
        var session = new Session(desktop, loupe);
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
        private long _lastInput = Environment.TickCount64;
        private bool _done, _closed;

        public Session(FrozenDesktop desktop, bool loupe)
        {
            _desktop = desktop;
            _loupe = loupe;
            _selection = new RegionSelection(desktop.Monitors.Select(m => m.Bounds).ToList());
        }

        public Task<RegionPickResult?> Start(CancellationToken ct)
        {
            var selection = _selection;
            int focus = 0;
            if (TryGetCursor(out int px, out int py)) focus = Math.Max(0, SelectionMath.MonitorIndexAt(px, py, selection.Monitors));

            selection.Changed += () => _lastInput = Environment.TickCount64;   // every pointer move, drag and key goes through Changed
            selection.Committed += r => _dispatcher.BeginInvoke(() =>
                Finish(new RegionPickResult(r, SelectionMath.MonitorIndexOf(r, selection.Monitors), selection.Outcome), close: false));
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
                    w.Closed += (_, _) => { if (!selection.Finished) selection.Cancel(); };
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

        /// <summary>A second trigger, the token: a cancel; when the selection has ended already, the windows are closed regardless.</summary>
        public void Cancel()
        {
            if (!_selection.Finished) _selection.Cancel();
            else ForceClose();
        }

        /// <summary>Ends the pick with nothing (if it has not ended) and closes every window.</summary>
        private void ForceClose() => Finish(null, close: true);

        /// <summary>Ends the pick once (later calls only close, when asked to); a commit leaves the windows to the caller.</summary>
        private void Finish(RegionPickResult? result, bool close)
        {
            if (!_done)
            {
                _done = true;
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
            _closed = true;
            foreach (var w in _windows.ToList()) { try { w.Close(); } catch (Exception ex) { Log.Warn("screenshot: overlay close: " + ex.Message); } }
            if (!_done) { _done = true; StopWatching(); _tcs.TrySetResult(null); }
        }

        /// <summary>
        /// A display change cancels only when the monitors really are elsewhere now: an exclusive-fullscreen game that drops out of
        /// exclusive mode as the overlay appears also raises it, with the layout unchanged, and must not make the overlay flash away.
        /// </summary>
        private void OnDisplayChanged()
        {
            if (_done || _selection.Finished) return;
            List<RectI> now;
            try { now = Clink.Native.Monitors.Enumerate().Select(m => new RectI(m.Left, m.Top, m.Width, m.Height)).ToList(); }
            catch (Exception ex) { Log.Warn("screenshot: monitors after a display change: " + ex.Message); now = new(); }
            if (SelectionMath.SameLayout(_selection.Monitors, now))
            {
                Log.Info("screenshot: display settings changed, the monitors did not: the overlay stays");
                return;
            }
            Log.Info("screenshot: the monitor layout changed: cancelled");
            _selection.Cancel();
        }

        private void CheckIdle()
        {
            if (_done) { _watchdog?.Stop(); return; }
            if (Environment.TickCount64 - _lastInput < (long)IdleTimeout.TotalMilliseconds) return;
            Log.Info($"screenshot: no input for {IdleTimeout.TotalMinutes:0} minutes: cancelled");
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
