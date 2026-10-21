using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Clink.Native;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The always-on status dot (docs/SPEC-v2.md §7): a 12 px round, topmost, click-through window in a corner of the captured
/// monitor's work area. Green = recording, amber = waiting for a game / starting, red = error, grey = paused.
/// </summary>
public partial class StatusDotWindow : Window
{
    private const int EdgeMargin = 8;

    public StatusDotWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>
    /// WS_EX_TRANSPARENT (clicks fall through), WS_EX_TOOLWINDOW (no Alt-Tab entry), WS_EX_NOACTIVATE (never takes focus from the
    /// game), and kept out of capture so the dot is not recorded into every clip of the monitor it sits on.
    /// </summary>
    private unsafe void MakeClickThrough()
    {
        var hwnd = new HWND(new WindowInteropHelper(this).Handle);
        nint ex = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= (nint)(uint)(WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE);
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex);
        ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }

    internal void Apply(RecordingState state)
    {
        string key = state switch
        {
            RecordingState.Recording => "Good",
            RecordingState.Error => "Danger",
            RecordingState.Paused => "Muted",
            _ => "Warn",
        };
        Dot.Fill = TryFindResource(key) as Brush ?? Brushes.Gray;
        Dot.ToolTip = null;
        // Called about once a second: a game going (DXGI) fullscreen, or any always-on-top window shown later, becomes topmost
        // above the dot, so it is put back on top each time (z-order only, never activated).
        if (IsVisible)
            PInvoke.SetWindowPos(ScreenPlacement.HwndOf(this), HWND.HWND_TOPMOST, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Shows the dot in the chosen corner ("top-left" | "top-right" | "bottom-left" | "bottom-right") of the monitor's work area.</summary>
    internal void Place(MonitorInfo? monitor, string? position)
    {
        if (!IsVisible) { Opacity = 0; Show(); }
        var wa = ScreenPlacement.WorkAreaOf(monitor);
        string pos = (position ?? "top-right").Trim().ToLowerInvariant();
        bool bottom = pos.StartsWith("bottom"), left = pos.EndsWith("left");
        ScreenPlacement.Place(this, (w, h) =>
        {
            double scale = ScreenPlacement.ScaleOf(this);
            int margin = (int)Math.Round(EdgeMargin * scale);
            int x = left ? wa.left + margin : wa.right - margin - w;
            int y = bottom ? wa.bottom - margin - h : wa.top + margin;
            return (x, y);
        });
        Opacity = 1;
    }
}
