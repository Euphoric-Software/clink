using System.Runtime.InteropServices;
using System.Windows;

namespace Clink.Services;

/// <summary>
/// Where <c>--dev-quiet</c> puts a window: fully to the right of every monitor. Pure arithmetic (no window, no Win32), so it can be checked.
/// </summary>
internal static class QuietPlacement
{
    /// <summary>The gap between the right edge of the virtual screen and an off-screen window, in the unit of the rectangle.</summary>
    public const double Margin = 200;

    /// <summary>
    /// The top-left corner for a window of <paramref name="windowSize"/>: right of the whole virtual screen (<paramref name="virtualScreen"/>:
    /// the box around every monitor, its Left/Top may be negative) by <paramref name="margin"/>, at the virtual screen's top. No monitor can
    /// show any part of it, whatever the arrangement, and the window keeps its size (PrintWindow and UI Automation still work).
    /// </summary>
    public static Point OffScreen(Rect virtualScreen, Size windowSize, double margin = Margin)
    {
        // The window size does not matter for "right of everything"; it is part of the signature so a different rule (below, left) needs no new callers.
        _ = windowSize;
        return new Point(virtualScreen.Right + Math.Max(0, margin), virtualScreen.Top);
    }

    /// <summary>
    /// The top-left corner that leaves exactly the window's left-most pixel column over the right-most monitor's right edge (<paramref name="monitors"/>:
    /// each monitor's rectangle, negative coordinates allowed), at that monitor's top; null when there is no monitor. Used for a window that is
    /// cloaked (<see cref="QuietMode.Conceal"/>): Windows does not keep the picture of a window that touches no monitor at all, so PrintWindow of a
    /// fully off-screen window is blank; one pixel on a monitor keeps it drawn, and the cloak keeps that pixel (and the window) invisible.
    /// </summary>
    public static Point? Sliver(IReadOnlyList<Rect> monitors, Size windowSize)
    {
        _ = windowSize;
        Rect? best = null;
        foreach (var m in monitors)
            if (m.Width > 0 && m.Height > 0 && (best == null || m.Right > best.Value.Right)) best = m;
        return best is { } b ? new Point(b.Right - 1, b.Top) : null;
    }

    /// <summary>True when the window rectangle touches no part of the virtual screen's box (so no monitor shows any of it).</summary>
    public static bool IsOffScreen(Rect virtualScreen, Rect window) => !window.IntersectsWith(virtualScreen);
}

/// <summary>
/// <c>--dev-quiet</c>: a test instance (<c>Clink.exe --dev &lt;folder&gt; --dev-quiet</c>) that never gets in the way of the person at the PC:
/// no window takes the focus, comes to the front, sits topmost or has a taskbar button; every window is placed beyond the right edge of the
/// virtual screen (still rendered, so PrintWindow and UI Automation tree walks work); toasts and balloons, sounds and the screen capture are
/// off. An error without <c>--dev</c>, like every dev switch. The gating is pure (<see cref="IsQuiet"/>, <see cref="StartsPaused"/>); the window handling is a thread
/// hook (<see cref="InstallWindowHook"/>) plus a few explicit guards where Clink itself activates or places a window.
/// </summary>
internal static unsafe class QuietMode
{
    /// <summary>Quiet only for a dev instance that was asked for it: a normal run and a plain <c>--dev</c> run are never quiet.</summary>
    public static bool IsQuiet(bool isDev, bool devQuiet) => isDev && devQuiet;

    /// <summary>
    /// Whether a dev instance starts paused (no screen capture). Normally unless <c>--dev-record</c>; in quiet mode only <c>--dev-quiet-record</c>
    /// records (a quiet instance is not to draw a capture border on the user's screen because a plain flag said so). A normal run never.
    /// </summary>
    public static bool StartsPaused(bool isDev, bool devRecord, bool devQuiet, bool devQuietRecord) =>
        isDev && !(IsQuiet(isDev, devQuiet) ? devQuietRecord : devRecord);

    /// <summary>This process is a quiet dev instance.</summary>
    public static bool Active => AppController.IsQuiet;

    // ------------------------------------------------------------------ windows

    /// <summary>The title the capture overlay carries: capture overlays are left alone (explicit screenshot actions only; never marked as ours, so never cloaked or refused).</summary>
    internal const string OverlayTitle = "Clink screenshot";

    private const int WH_CBT = 5, HCBT_CREATEWND = 3, HCBT_DESTROYWND = 4, HCBT_ACTIVATE = 5;
    private const uint WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const nint HwndMessage = -3, HwndBottom = 1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    private delegate nint HookProc(int code, nint wParam, nint lParam);
    private static HookProc? s_proc;
    private static nint s_hook;
    private static nint s_winEventHook;
    private static WinEventProc? s_winEventProc;
    /// <summary>The windows the creation hook recognised as the application's own (and, therefore, cloaks and does not let activate). UI thread only in practice; locked all the same.</summary>
    private static readonly HashSet<nint> s_ours = new();

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);
    private const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0;
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int idHook, HookProc proc, nint hMod, uint threadId);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect32 rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }

    /// <summary>The box around every monitor, physical pixels.</summary>
    private static Rect VirtualScreenPixels() =>
        new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>The box around every monitor, device-independent pixels (what WPF's Left/Top use).</summary>
    public static Rect VirtualScreenDips() =>
        new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// Installs, on the calling (UI) thread, a CBT hook that sees every window this thread creates or activates, including message boxes and
    /// common dialogs, before the window is ever shown: a window of the application (a WPF window or popup, a dialog) is moved beyond the
    /// right edge of the virtual screen and cloaked when it is created (the creation structure's style flags cannot be changed from a CBT
    /// hook, so topmost and the taskbar button are handled by the explicit guards and <see cref="Calm"/>); the activation of such a window
    /// is refused (no other window: the tray icon's hidden window and its menu work as usual). A process-wide WinEvent hook cloaks the same
    /// windows again when they are shown, in case the cloak did not stick that early. Capture overlays are left alone. Best effort:
    /// a failure is logged and the explicit guards remain.
    /// </summary>
    public static void InstallWindowHook()
    {
        if (s_hook != 0) return;
        s_proc = Proc;   // kept alive by the field for as long as the hook lives
        s_hook = SetWindowsHookExW(WH_CBT, s_proc, 0, GetCurrentThreadId());
        if (s_hook == 0) Log.Warn("quiet: the window hook could not be installed (error " + Marshal.GetLastWin32Error() + "); windows may take the focus");
        else Log.Info("quiet: no window will be activated, every window is created off-screen and then cloaked at the back");
        s_winEventProc = OnWindowShown;
        s_winEventHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, 0, s_winEventProc, (uint)Environment.ProcessId, 0, WINEVENT_OUTOFCONTEXT);
        if (s_winEventHook == 0) Log.Warn("quiet: the window-shown hook could not be installed; windows are cloaked at creation only");
    }

    private static nint Proc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code == HCBT_ACTIVATE) { if (IsOurs(wParam)) return 1; }   // a non-zero result refuses the activation (only for windows we marked)
            else if (code == HCBT_CREATEWND) Adjust(wParam, *(nint*)lParam);
            else if (code == HCBT_DESTROYWND) { lock (s_ours) s_ours.Remove(wParam); }
        }
        catch (Exception) { }   // a hook must never throw into the window procedure that called it
        return CallNextHookEx(s_hook, code, wParam, lParam);
    }

    private static bool IsOurs(nint hwnd) { lock (s_ours) return s_ours.Contains(hwnd); }

    /// <summary>Cloaks a window of the application when it is shown (the same cloak as at creation; harmless when it already is).</summary>
    private static void OnWindowShown(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try { if (idObject == 0 && idChild == 0 && IsOurs(hwnd)) Cloak(hwnd); }
        catch (Exception) { }
    }

    /// <summary>Hides a window from every monitor and from Alt-Tab (DWMWA_CLOAK) without moving or activating it. True when DWM took it.</summary>
    private static bool Cloak(nint hwnd)
    {
        int on = 1;
        return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref on, sizeof(int)) == 0;
    }

    private static bool s_loggedCloak;

    /// <summary>
    /// Marks, cloaks and moves a top-level window of the application that is about to be created (<paramref name="hwnd"/>; x64 layout of the
    /// CREATESTRUCT: parent 24, cy 32, cx 36, y 40, x 44, style 48, name 56, class 64). Only the position and size in the structure are honoured by
    /// a CBT hook, so only those are rewritten.
    /// </summary>
    private static void Adjust(nint hwnd, nint cs)
    {
        if (cs == 0 || hwnd == 0 || IntPtr.Size != 8) return;   // the offsets below are the 64-bit layout
        nint parent = Marshal.ReadIntPtr(cs, 24);
        int cy = Marshal.ReadInt32(cs, 32), cx = Marshal.ReadInt32(cs, 36);
        uint style = (uint)Marshal.ReadInt32(cs, 48);
        if (parent == HwndMessage || (style & WS_CHILD) != 0 || (style & (WS_POPUP | WS_CAPTION)) == 0) return;
        nint name = Marshal.ReadIntPtr(cs, 56), cls = Marshal.ReadIntPtr(cs, 64);
        if (name > 0xFFFF && Marshal.PtrToStringUni(name) == OverlayTitle) return;
        // Only windows of the application: WPF windows and popups (tooltips, ComboBox dropdowns: HwndWrapper[...]) and dialogs (#32770: message boxes, common dialogs; given as an atom). Not a native menu or an IME window.
        bool ours = cls == 0x8002 || (cls > 0xFFFF && (Marshal.PtrToStringUni(cls) ?? "").StartsWith("HwndWrapper[", StringComparison.Ordinal))
            || (cls > 0xFFFF && Marshal.PtrToStringUni(cls) == "#32770");
        if (!ours) return;
        lock (s_ours) s_ours.Add(hwnd);
        bool cloaked = Cloak(hwnd);
        if (!s_loggedCloak) { s_loggedCloak = true; Log.Info("quiet: cloaking a window at creation " + (cloaked ? "worked" : "was refused; it is cloaked when shown")); }
        var at = QuietPlacement.OffScreen(VirtualScreenPixels(), new Size(Math.Max(cx, 0), Math.Max(cy, 0)));
        Marshal.WriteInt32(cs, 40, (int)at.Y);
        Marshal.WriteInt32(cs, 44, (int)at.X);
    }

    /// <summary>
    /// For every WPF window when it has loaded (a class handler): not topmost, no taskbar button, and off-screen if anything put it on a
    /// monitor after all (WPF centres a window itself once it has its size). Capture overlays are skipped.
    /// </summary>
    public static void RegisterWindowHandlers()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, e) =>
        {
            if (sender is not Window w || !ReferenceEquals(e.OriginalSource, w) || w is Clink.UI.Screenshots.RegionOverlayWindow) return;
            Calm(w);
        }));
    }

    /// <summary>Takes the topmost flag and the taskbar button off a window, and conceals it (see <see cref="Conceal"/>).</summary>
    public static void Calm(Window w)
    {
        if (!Active) return;
        try
        {
            if (w.Topmost) w.Topmost = false;
            if (w.ShowInTaskbar) w.ShowInTaskbar = false;
            nint hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd != 0) Conceal(hwnd);
        }
        catch (Exception ex) { Log.Warn("quiet: could not calm " + w.GetType().Name + ": " + ex.Message); }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
    private const int DWMWA_CLOAK = 13;

    /// <summary>The pixel rectangle of each monitor.</summary>
    private static List<Rect> MonitorRects() =>
        System.Windows.Forms.Screen.AllScreens.Select(s => new Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height)).ToList();

    /// <summary>
    /// Makes a window unobtrusive and keeps it drawn: cloaked (DWMWA_CLOAK: invisible, no Alt-Tab entry, no input) and moved to the back with
    /// one pixel column over the right edge of the right-most monitor (<see cref="QuietPlacement.Sliver"/>), because a window that touches
    /// no monitor is not drawn at all (PrintWindow returns a blank picture). If the cloak is refused, the window is moved fully off-screen
    /// instead (<see cref="QuietPlacement.OffScreen"/>: not drawn, but nothing is ever visible). Never activates; does nothing for a window that is already placed.
    /// </summary>
    public static void Conceal(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        var window = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        bool cloaked = Cloak(hwnd);
        Point? target = cloaked ? QuietPlacement.Sliver(MonitorRects(), window.Size) : null;
        target ??= QuietPlacement.OffScreen(VirtualScreenPixels(), window.Size);
        if ((int)target.Value.X == r.Left && (int)target.Value.Y == r.Top) return;
        SetWindowPos(hwnd, HwndBottom, (int)target.Value.X, (int)target.Value.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        if (cloaked) RedrawWindow(hwnd, 0, 0, 0x1 | 0x4 | 0x80 | 0x100);   // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW: drawn again now that a pixel of it is on a monitor
    }
    // ------------------------------------------------------------------ the main window

    /// <summary>Puts the main window beyond the right edge of the virtual screen at its normal size (before it is shown); it is neither activated nor in the taskbar.</summary>
    public static void PlaceMainWindow(Window w)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Topmost = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        var at = QuietPlacement.OffScreen(VirtualScreenDips(), new Size(w.Width, w.Height));
        w.Left = at.X;
        w.Top = at.Y;
    }

    /// <summary>Shows a window that is hidden or minimized without activating it (SW_SHOWNOACTIVATE).</summary>
    public static void ShowWithoutActivating(Window w)
    {
        nint hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd != 0 && w.WindowState == WindowState.Minimized) ShowWindow(hwnd, 4);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);

    /// <summary>Removes both hooks (called from <c>App.OnExit</c>, and available to tests).</summary>
    public static void RemoveWindowHook()
    {
        if (s_winEventHook != 0) { UnhookWinEvent(s_winEventHook); s_winEventHook = 0; s_winEventProc = null; }
        if (s_hook != 0) { UnhookWindowsHookEx(s_hook); s_hook = 0; s_proc = null; }
        lock (s_ours) s_ours.Clear();
    }
}
