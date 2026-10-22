using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Clink.UI;

/// <summary>
/// Marks a window that is kept out of screen captures (<see cref="ScreenPlacement.ExcludeFromCapture"/>: the toasts, the share
/// window), so that its tooltips are too: a tooltip is a window of its own, and would be recorded into a clip over a game.
/// A window class that implements it calls <see cref="CaptureExclusion.Register"/> from its static constructor.
/// </summary>
internal interface ICaptureExcludedWindow { }

internal static class CaptureExclusion
{
    private static int s_registered;

    /// <summary>
    /// Keeps every tooltip on an <see cref="ICaptureExcludedWindow"/>'s elements (a plain string or a ToolTip, wherever it is set) out
    /// of capture, like the window it belongs to; a tooltip's PlacementTarget is its owner element. Safe to call from every such
    /// class: the handler is registered once. Call it from the static constructor, so it is in place before the first of those windows
    /// can show a tooltip, whichever of them is the first one shown.
    /// </summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ToolTip { PlacementTarget: { } owner } tip && Window.GetWindow(owner) is ICaptureExcludedWindow && PresentationSource.FromVisual(tip) is HwndSource src)
                ScreenPlacement.ExcludeFromCapture(src.Handle);
        }));
    }
}
