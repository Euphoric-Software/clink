using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Clink.UI;

/// <summary>
/// Marks a window that is kept out of screen captures (<see cref="ScreenPlacement.ExcludeFromCapture"/>: the toasts, the share
/// window), so that its tooltips are too: a tooltip is a window of its own, and would be recorded into a clip over a game.
/// A window class that implements it calls <see cref="CaptureExclusion.Register"/> from its static constructor.
/// </summary>
internal interface ICaptureExcludedWindow
{
    /// <summary>False when the window opted out (a setting): it is then an ordinary window, tooltips included.</summary>
    bool CaptureExcluded => true;
}

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
            if (s is ToolTip { PlacementTarget: { } owner } tip && IsExcluded(OwnerWindow(owner)) && PresentationSource.FromVisual(tip) is HwndSource src)
                ScreenPlacement.ExcludeFromCapture(src.Handle);
        }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ContextMenu { PlacementTarget: { } owner } menu && IsExcluded(Window.GetWindow(owner)) && PresentationSource.FromVisual(menu) is HwndSource src)
                ScreenPlacement.ExcludeFromCapture(src.Handle);
        }));
        // A window owned by an excluded one (the insert-image picker over the capture overlay) is out of capture itself.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window { Owner: { } parent } w && IsExcluded(parent) && PresentationSource.FromVisual(w) is HwndSource src)
                ScreenPlacement.ExcludeFromCapture(src.Handle);
        }));
    }

    /// <summary>
    /// The window an element belongs to. A menu item's tooltip has none (a context menu is a popup of its own): then the window of
    /// the element the item's context menu opened from (the toast's Share button).
    /// </summary>
    private static Window? OwnerWindow(DependencyObject element)
    {
        if (Window.GetWindow(element) is { } window) return window;
        DependencyObject? current = element;
        for (int depth = 0; current != null && depth < 16; depth++)
        {
            if (current is ContextMenu { PlacementTarget: { } target }) return Window.GetWindow(target);
            DependencyObject? parent = current is MenuItem item ? ItemsControl.ItemsControlFromItemContainer(item) : null;
            parent ??= current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current) : null;
            parent ??= LogicalTreeHelper.GetParent(current);
            current = parent;
        }
        return null;
    }

    /// <summary>The window, or one that owns it (directly or further up), is an <see cref="ICaptureExcludedWindow"/>: its tooltips, menus and dialogs are kept out of capture too.</summary>
    internal static bool IsExcluded(Window? window)
    {
        for (int depth = 0; window != null && depth < 16; window = window.Owner, depth++)
            if (window is ICaptureExcludedWindow { CaptureExcluded: true }) return true;
        return false;
    }
}
