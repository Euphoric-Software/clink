using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Clink.Services;

/// <summary>
/// The exe's icon at the sizes the shell really draws it at. The .ico in the exe has a frame for every size Windows asks for
/// (tools\make-icon.ps1), but the shell can only use the right one if it is handed an icon of that size. A window whose icon was
/// never set gets WPF's default: the 32 px frame as ICON_BIG and the 16 px frame as ICON_SMALL. The taskbar button is 24 px at
/// 100 % (1.5 x the small-icon size at any DPI), so it would shrink the 32 px frame and the hand-hinted 24 px one would never show.
/// <see cref="Attach"/> gives a window the small icon and the taskbar-button icon for the taskbar's DPI instead.
/// </summary>
internal static class AppIcons
{
    private const uint ICON_SMALL = 0, ICON_BIG = 1;   // WM_SETICON wParam

    private static readonly object Gate = new();
    // Icons handed to windows are kept for the life of the process: WM_SETICON does not take ownership, the shell may still be
    // drawing from a handle after a window has moved on to another size, and a session only ever sees a few DPIs.
    private static readonly Dictionary<int, System.Drawing.Icon> Cache = new();
    private static readonly ConditionalWeakTable<Window, object> Attached = new();

    /// <summary>DPI of the primary taskbar (what the tray and the taskbar buttons are drawn at); the system DPI if there is no taskbar.</summary>
    public static uint TaskbarDpi()
    {
        uint dpi = PInvoke.GetDpiForWindow(PInvoke.FindWindow("Shell_TrayWnd", null));
        return dpi != 0 ? dpi : PInvoke.GetDpiForSystem();
    }

    /// <summary>SM_CXSMICON at the taskbar's DPI: 16 / 20 / 24 / 28 / 32 px at 100 / 125 / 150 / 175 / 200 %. The tray icon is this size.</summary>
    public static int SmallIconSize() =>
        PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, TaskbarDpi());

    /// <summary>Size of the icon in a taskbar button: 1.5 x the small size, 24 / 30 / 36 / 42 / 48 px at 100 / 125 / 150 / 175 / 200 %.</summary>
    public static int TaskbarIconSize() => SmallIconSize() * 3 / 2;

    /// <summary>
    /// Gives the window the exe's icon at the sizes above: now if it already has a handle, otherwise when it gets one, and again
    /// when its DPI changes (<see cref="RefreshWindows"/> covers a change of the taskbar's own DPI).
    /// </summary>
    public static void Attach(Window w)
    {
        Attached.AddOrUpdate(w, new object());
        w.SourceInitialized += (_, _) => Apply(w);
        w.DpiChanged += (_, _) => Apply(w);
        if (new WindowInteropHelper(w).Handle != 0) Apply(w);
    }

    /// <summary>Re-applies the icons to every attached window, after a display or scaling change moved the taskbar's icon size.</summary>
    public static void RefreshWindows()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;
            foreach (Window w in app.Windows)
                if (Attached.TryGetValue(w, out _)) Apply(w);
        }
        catch (Exception ex) { Log.Warn("window icons refresh: " + ex.Message); }
    }

    private static void Apply(Window w)
    {
        try
        {
            nint hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == 0) return;
            int small = SmallIconSize();
            SetIcon(hwnd, ICON_SMALL, IconAt(small));
            SetIcon(hwnd, ICON_BIG, IconAt(small * 3 / 2));
        }
        catch (Exception ex) { Log.Warn("window icon: " + ex.Message); }
    }

    private static void SetIcon(nint hwnd, uint which, System.Drawing.Icon? icon)
    {
        if (icon != null) PInvoke.SendMessage(new HWND(hwnd), PInvoke.WM_SETICON, which, icon.Handle);
    }

    /// <summary>The exe's own icon frame of exactly this size, or null if the exe has no icon (the window then keeps WPF's default).</summary>
    private static System.Drawing.Icon? IconAt(int size)
    {
        string? exe = Environment.ProcessPath;
        if (size <= 0 || exe == null) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(size, out var cached)) return cached;
            try
            {
                var icon = System.Drawing.Icon.ExtractIcon(exe, 0, size);
                if (icon != null) Cache[size] = icon;
                return icon;
            }
            catch (Exception ex) { Log.Warn($"window icon {size} px: " + ex.Message); return null; }
        }
    }
}
