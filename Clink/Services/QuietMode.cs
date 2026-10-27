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

/// <summary>What the <c>--dev-quiet</c> creation hook makes of a window that is about to be created.</summary>
internal enum QuietWindowKind
{
    /// <summary>A child window or a message-only window: never shown on its own, left alone.</summary>
    NotTopLevel,
    /// <summary>The capture overlay (<see cref="QuietMode.OverlayTitle"/>): left alone.</summary>
    Overlay,
    /// <summary>A window of the application (a WPF window or popup, a dialog): marked, cloaked, placed off-screen, its activation refused.</summary>
    Ours,
    /// <summary>Anything else created on the thread (a native menu, an IME or text-services window, a WinForms helper window): left alone.</summary>
    Foreign,
}

/// <summary>
/// The pure decisions of <c>--dev-quiet</c>'s window handling (no Win32), so they can be checked: which windows are the application's own,
/// and what to do when the foreground changes (<see cref="QuietForegroundGuard"/>).
/// </summary>
internal static class QuietWindows
{
    private const uint WS_CHILD = 0x40000000;

    /// <summary>The atom of the dialog class (<c>#32770</c>: message boxes, common dialogs), as a CREATESTRUCT may carry it.</summary>
    public const int DialogAtom = 0x8002;

    /// <summary>
    /// Classifies a window by its class alone (<paramref name="classAtom"/>: the class as an atom, 0 when it was given by name; <paramref name="className"/>:
    /// the name, null for an atom). The style does not matter: the main window (<c>WindowStyle="None"</c>, transparent) has neither WS_POPUP nor
    /// WS_CAPTION, and it is the one window that most needs marking. WPF windows and popups are <c>HwndWrapper[...]</c>, dialogs <c>#32770</c>.
    /// </summary>
    public static QuietWindowKind Classify(bool messageOnly, uint style, int classAtom, string? className, string? title)
    {
        if (messageOnly || (style & WS_CHILD) != 0) return QuietWindowKind.NotTopLevel;
        if (title == QuietMode.OverlayTitle) return QuietWindowKind.Overlay;
        if (classAtom == DialogAtom || className == "#32770") return QuietWindowKind.Ours;
        // WPF's own zero-size helper windows are left unmarked (least risky): they may rely on their placement for per-monitor DPI, they are
        // never shown and never take the foreground, so there is nothing to hide.
        if (title is "SystemResourceNotifyWindow" or "MediaContextNotificationWindow") return QuietWindowKind.Foreign;
        if (className != null && className.StartsWith("HwndWrapper[", StringComparison.Ordinal)) return QuietWindowKind.Ours;
        return QuietWindowKind.Foreign;
    }

    /// <summary>
    /// Whether a remembered window is a valid place to give the foreground back to: it exists, is visible, is not DWM-cloaked and is not a window
    /// of this process. Minimized is fine (a minimized full-screen game is a valid target).
    /// </summary>
    public static bool IsUsableTarget(bool exists, bool visible, bool cloaked, bool ownProcess) => exists && visible && !cloaked && !ownProcess;
}

/// <summary>What <see cref="QuietForegroundGuard.OnForeground"/> decided.</summary>
internal enum QuietForegroundAction
{
    /// <summary>Nothing to do (no window, or a visible window of the application such as the tray menu).</summary>
    None,
    /// <summary>A window of another process has the foreground: remembered as the one to give it back to.</summary>
    Remembered,
    /// <summary>The capture overlay has the foreground: it is meant to.</summary>
    Exempt,
    /// <summary>A hidden window of the application has the foreground: give it back to <see cref="QuietForegroundGuard.LastOther"/>.</summary>
    Restore,
    /// <summary>A hidden window of the application has the foreground, but no live window of another process is known to give it back to.</summary>
    NothingToRestore,
    /// <summary>The event is stale: a hidden window of the application got the foreground but no longer holds it (the user has moved on); left alone.</summary>
    Stale,
    /// <summary>Too many restores in a short time (something keeps taking it back): this one is skipped, so the two never fight.</summary>
    Throttled,
}

/// <summary>
/// <c>--dev-quiet</c>'s safety net, the pure part: it remembers the last foreground window of another process and, when the foreground goes to
/// a hidden (marked or cloaked) window of this process, says to give it back to that window. The capture overlay is exempt; a remembered window
/// that has gone (or whose handle is now this process's) is not used; at most <see cref="MaxRestores"/> restores in <see cref="WindowMs"/>.
/// </summary>
internal sealed class QuietForegroundGuard
{
    public const int MaxRestores = 5;
    public const long WindowMs = 1000;

    private readonly Queue<long> _restores = new();

    /// <summary>The last window of another process that had the foreground (0: none known).</summary>
    public nint LastOther { get; private set; }

    /// <summary>
    /// The foreground went to <paramref name="hwnd"/>. <paramref name="ownProcess"/>: it is a window of this process; <paramref name="overlay"/>: it
    /// is the capture overlay; <paramref name="hidden"/>: it is marked as the application's or cloaked (invisible to the user);
    /// <paramref name="isUsable"/>: whether a remembered window is a valid target (see <see cref="QuietWindows.IsUsableTarget"/>);
    /// <paramref name="currentForeground"/>: the window that has the foreground right now (events are handled late; a restore is only done while
    /// the hidden window still holds it, never over a choice the user has made since); <paramref name="nowMs"/>: a
    /// monotonic clock. Returns the action and, for <see cref="QuietForegroundAction.Restore"/>, the window to give the foreground to.
    /// </summary>
    public (QuietForegroundAction Action, nint Target) OnForeground(nint hwnd, bool ownProcess, bool overlay, bool hidden, Func<nint, bool> isUsable, nint currentForeground, long nowMs)
    {
        if (hwnd == 0) return (QuietForegroundAction.None, 0);
        if (!ownProcess) { LastOther = hwnd; return (QuietForegroundAction.Remembered, 0); }
        if (overlay) return (QuietForegroundAction.Exempt, 0);
        if (!hidden) return (QuietForegroundAction.None, 0);
        if (currentForeground != hwnd) return (QuietForegroundAction.Stale, 0);
        if (LastOther == 0 || LastOther == hwnd || !isUsable(LastOther)) { LastOther = 0; return (QuietForegroundAction.NothingToRestore, 0); }
        while (_restores.Count > 0 && nowMs - _restores.Peek() >= WindowMs) _restores.Dequeue();
        if (_restores.Count >= MaxRestores) return (QuietForegroundAction.Throttled, 0);
        _restores.Enqueue(nowMs);
        return (QuietForegroundAction.Restore, LastOther);
    }

    /// <summary>Forgets everything (the hook was removed).</summary>
    public void Reset() { LastOther = 0; _restores.Clear(); }
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

    /// <summary>
    /// <c>--dev-no-marker</c>: the TEST frame, badge, title text and toast tag are not drawn. Only in a quiet dev instance that asked for it
    /// (documentation screenshots); a normal run, a plain <c>--dev</c> run and a quiet run without the switch keep their marker.
    /// </summary>
    public static bool HidesMarker(bool isDev, bool devQuiet, bool devNoMarker) => IsQuiet(isDev, devQuiet) && devNoMarker;

    /// <summary>
    /// <c>--dev-demo-status "game"</c>: the header text that stands in for the real status, "Recording · game"; null (show the real
    /// status) unless this is a quiet dev instance that was given a non-blank game. A display override only: nothing records.
    /// </summary>
    public static string? DemoStatusText(bool isDev, bool devQuiet, string? game) =>
        DemoGameName(isDev, devQuiet, game) is { } g ? "Recording · " + g : null;

    /// <summary>The game <c>--dev-demo-status</c> pretends to have detected (trimmed), or null when the override is not in force.</summary>
    public static string? DemoGameName(bool isDev, bool devQuiet, string? game) =>
        IsQuiet(isDev, devQuiet) && !string.IsNullOrWhiteSpace(game) ? game.Trim() : null;

    /// <summary>The game name the Alerts tab's sample toasts show: the demo game when <c>--dev-demo-status</c> is in force, otherwise the built-in "Overwatch".</summary>
    public static string SampleToastGame(string? demoGame) => string.IsNullOrWhiteSpace(demoGame) ? "Overwatch" : demoGame.Trim();

    /// <summary>What a normal install keeps its settings in, as the documentation pictures show it.</summary>
    public const string DocSettingsDir = @"%APPDATA%\Clink";
    /// <summary>The default clips folder (<c>Settings.ClipsFolder</c>'s default: Videos\Clink), as the documentation pictures show it.</summary>
    public const string DocClipsFolder = @"%USERPROFILE%\Videos\Clink";
    /// <summary>The default screenshots folder: <c>Screenshots</c> inside the clips folder (<c>ScreenshotService.FolderFor</c>).</summary>
    public const string DocScreenshotsFolder = DocClipsFolder + @"\Screenshots";

    /// <summary>
    /// DISPLAY ONLY. The text a path box or label shows: <paramref name="real"/> normally, <paramref name="docPlaceholder"/> (an
    /// environment-variable form, never a fully qualified path) in documentation-screenshot mode, so no scratch folder or user name is
    /// pictured. Never store the result: the placeholder is not a folder, and the path-box handlers also return at once in that mode.
    /// </summary>
    public static string Shown(bool docShots, string real, string docPlaceholder) => docShots ? docPlaceholder : real;

    /// <summary>This process is a quiet dev instance.</summary>
    public static bool Active => AppController.IsQuiet;

    // ------------------------------------------------------------------ windows

    /// <summary>The title the capture overlay carries: capture overlays are left alone (explicit screenshot actions only; never marked as ours, so never cloaked or refused).</summary>
    internal const string OverlayTitle = "Clink screenshot";

    private const int WH_CBT = 5, HCBT_CREATEWND = 3, HCBT_DESTROYWND = 4, HCBT_ACTIVATE = 5;
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
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0;
    private static nint s_foregroundHook;
    private static WinEventProc? s_foregroundProc;
    private static readonly QuietForegroundGuard s_guard = new();
    private static bool s_loggedThrottle;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, char* text, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    private const int DWMWA_CLOAKED = 14;
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
    /// windows again when they are shown, in case the cloak did not stick that early. A system-wide foreground hook is the safety net: should
    /// a hidden window of the application get the foreground all the same, it is given straight back to the window of another process that
    /// had it last (<see cref="QuietForegroundGuard"/>). Capture overlays are left alone. Best effort: a failure is logged and the explicit guards remain.
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
        // The safety net: every process (the window to give the foreground back to belongs to another one), out of context, so the callback
        // runs on this (UI) thread from its message loop; the delegate is rooted by the field for as long as the hook lives.
        s_guard.Reset();
        s_loggedThrottle = false;
        s_foregroundProc = OnForegroundChanged;
        s_foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, s_foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (s_foregroundHook == 0) Log.Warn("quiet: the foreground hook could not be installed; a hidden window that takes the foreground is not given it back");
        else OnForegroundChanged(0, EVENT_SYSTEM_FOREGROUND, GetForegroundWindow(), 0, 0, 0, 0);   // remember the window the user has now
    }

    private static uint ProcessOf(nint hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return pid; }

    private static string ClassOf(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int n = GetClassNameW(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    private static string TitleOf(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int n = GetWindowTextW(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    private static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// The foreground changed (any process). A hidden window of this process (marked, or cloaked) that got it gives it straight back to the
    /// window of another process that had it last: allowed, because this process holds the foreground at that moment. See <see cref="QuietForegroundGuard"/>.
    /// </summary>
    private static void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == 0 || idObject != 0 || idChild != 0) return;
            uint self = (uint)Environment.ProcessId;
            bool own = ProcessOf(hwnd) == self;
            bool overlay = own && TitleOf(hwnd) == OverlayTitle;
            bool hidden = own && !overlay && (IsOurs(hwnd) || IsCloaked(hwnd));
            var (action, target) = s_guard.OnForeground(hwnd, own, overlay, hidden, h => QuietWindows.IsUsableTarget(IsWindow(h), IsWindowVisible(h), IsCloaked(h), ProcessOf(h) == self), GetForegroundWindow(), Environment.TickCount64);
            switch (action)
            {
                case QuietForegroundAction.Restore:
                    s_loggedThrottle = false;
                    bool given = SetForegroundWindow(target);
                    Log.Warn($"quiet: hidden window 0x{hwnd:X} ({ClassOf(hwnd)}) took the foreground; given back to 0x{target:X} ({ClassOf(target)}): {(given ? "done" : "refused")}");
                    if (IsOurs(hwnd)) Cloak(hwnd);
                    break;
                case QuietForegroundAction.NothingToRestore:
                    Log.Warn($"quiet: hidden window 0x{hwnd:X} ({ClassOf(hwnd)}) took the foreground and no window of another process is known to give it back to");
                    break;
                case QuietForegroundAction.Throttled:
                    if (!s_loggedThrottle) { s_loggedThrottle = true; Log.Warn($"quiet: hidden window 0x{hwnd:X} keeps taking the foreground; not giving it back again for now (at most {QuietForegroundGuard.MaxRestores} a second)"); }
                    break;
            }
        }
        catch (Exception) { }   // a callback must never throw into the message loop
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
    private static int s_loggedCreations;
    private const int MaxLoggedCreations = 64;

    /// <summary>
    /// Marks, cloaks and moves a top-level window of the application that is about to be created (<paramref name="hwnd"/>; x64 layout of the
    /// CREATESTRUCT: parent 24, cy 32, cx 36, y 40, x 44, style 48, name 56, class 64, ex-style 72). Which windows are the application's is decided by
    /// the class alone (<see cref="QuietWindows.Classify"/>). Only the position and size in the structure are honoured by a CBT hook, so only those are rewritten.
    /// </summary>
    private static void Adjust(nint hwnd, nint cs)
    {
        if (cs == 0 || hwnd == 0 || IntPtr.Size != 8) return;   // the offsets below are the 64-bit layout
        nint parent = Marshal.ReadIntPtr(cs, 24);
        int cy = Marshal.ReadInt32(cs, 32), cx = Marshal.ReadInt32(cs, 36);
        uint style = (uint)Marshal.ReadInt32(cs, 48);
        uint exStyle = (uint)Marshal.ReadInt32(cs, 72);
        nint name = Marshal.ReadIntPtr(cs, 56), cls = Marshal.ReadIntPtr(cs, 64);
        // A message-only window is created with HWND_MESSAGE, which the structure may already carry as the message root (class "Message").
        bool messageOnly = parent == HwndMessage || (parent != 0 && ClassOf(parent) == "Message");
        string? className = cls > 0xFFFF ? Marshal.PtrToStringUni(cls) : null;
        int classAtom = cls is > 0 and <= 0xFFFF ? (int)cls : 0;
        string? title = name > 0xFFFF ? Marshal.PtrToStringUni(name) : null;
        var kind = QuietWindows.Classify(messageOnly, style, classAtom, className, title);
        if (kind == QuietWindowKind.NotTopLevel) return;
        string classText = className ?? "#atom 0x" + classAtom.ToString("X");
        if (s_loggedCreations < MaxLoggedCreations)
        {
            s_loggedCreations++;
            Log.Info($"quiet: window 0x{hwnd:X} class {classText} title \"{title}\" style 0x{style:X8} ex-style 0x{exStyle:X8} owner 0x{parent:X}: "
                + (kind == QuietWindowKind.Ours ? "marked" : kind == QuietWindowKind.Overlay ? "the capture overlay, left alone" : "not the application's, left alone")
                + (s_loggedCreations == MaxLoggedCreations ? " (further windows are not logged)" : ""));
        }
        if (kind != QuietWindowKind.Ours) return;
        lock (s_ours) s_ours.Add(hwnd);
        bool cloaked = Cloak(hwnd);
        if (!s_loggedCloak) { s_loggedCloak = true; Log.Info($"quiet: cloaking a window at creation (0x{hwnd:X}, {classText}) " + (cloaked ? "worked" : "was refused; it is cloaked when shown")); }
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
            if (hwnd == 0) return;
            bool added;
            lock (s_ours) added = s_ours.Add(hwnd);   // a window the creation hook missed is the application's all the same: refuse its activation from now on
            if (added) Log.Warn($"quiet: {w.GetType().Name} (0x{hwnd:X}, {ClassOf(hwnd)}) was not marked when it was created; marked now");
            Conceal(hwnd);
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

    /// <summary>Removes the hooks (called from <c>App.OnExit</c>, and available to tests).</summary>
    public static void RemoveWindowHook()
    {
        if (s_foregroundHook != 0) { UnhookWinEvent(s_foregroundHook); s_foregroundHook = 0; }   // the delegate fields stay rooted: a queued out-of-context callback may still be delivered
        s_guard.Reset();
        if (s_winEventHook != 0) { UnhookWinEvent(s_winEventHook); s_winEventHook = 0; }
        if (s_hook != 0) { UnhookWindowsHookEx(s_hook); s_hook = 0; }
        lock (s_ours) s_ours.Clear();
    }
}
