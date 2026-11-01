using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Clink.Services;

namespace Clink.UI;

/// <summary>The small themed menu a video clip's Share button opens (<see cref="ShareChoices"/>).</summary>
internal static class ShareMenu
{
    /// <summary>
    /// Drops the menu below <paramref name="target"/>. <paramref name="onPick"/> runs for the chosen item; <paramref name="onClosed"/>
    /// when the menu closes (picked or not). Opened from the keyboard, the first item takes the focus. A menu on a toast is kept out
    /// of screen capture like the toast (<see cref="CaptureExclusion"/>, through its PlacementTarget). Returns the menu (to close it early).
    /// </summary>
    public static ContextMenu Open(Button target, IReadOnlyList<ShareChoiceItem> items, Action<ShareChoice> onPick, Action? onClosed = null, FrameworkElement? below = null) =>
        Show(target, items.Select(i => (i.Header, i.ToolTip, true, (Action)(() => onPick(i.Choice)))).ToList(), onClosed, below);

    /// <summary>The same menu for the library's selection bar (<see cref="BulkActions.ShareMenu"/>): a disabled entry stays in the menu and says why in its tooltip.</summary>
    public static ContextMenu OpenBulk(Button target, IReadOnlyList<BulkActionItem> items, Action<BulkAction> onPick, Action? onClosed = null, FrameworkElement? below = null) =>
        Show(target, items.Select(i => (i.Header, i.ToolTip, i.Enabled, (Action)(() => onPick(i.Action)))).ToList(), onClosed, below);

    /// <summary>Alt+Down or Shift+F10: the keys that open the menu from the main half of a split button.</summary>
    public static bool IsOpenKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        return (key == Key.Down && mods.HasFlag(ModifierKeys.Alt)) || (key == Key.F10 && mods.HasFlag(ModifierKeys.Shift));
    }

    private static ContextMenu Show(Button target, IReadOnlyList<(string Header, string ToolTip, bool Enabled, Action Pick)> items, Action? onClosed, FrameworkElement? below)
    {
        var menu = new ContextMenu
        {
            Style = (Style)target.FindResource("FlyoutMenu"),
            PlacementTarget = target,   // always the arrow: it is never disabled (the main half can be) and the menu items inherit from it
            Placement = PlacementMode.Bottom,
        };
        // a split button: the menu drops from the whole button, left-aligned with the main half that sits right before the arrow
        if (below != null)
            menu.PlacementRectangle = new Rect(-below.ActualWidth, 0, below.ActualWidth + target.ActualWidth, target.ActualHeight);
        var itemStyle = (Style)target.FindResource("FlyoutMenuItem");
        bool picked = false;
        foreach (var item in items)
        {
            var pick = item.Pick;
            var mi = new MenuItem { Header = item.Header, ToolTip = item.ToolTip, Style = itemStyle, IsEnabled = item.Enabled };
            ToolTipService.SetShowOnDisabled(mi, true);
            AutomationProperties.SetName(mi, item.Header.Replace("_", ""));
            mi.Click += (_, _) => { if (picked) return; picked = true; pick(); };
            menu.Items.Add(mi);
        }
        menu.Closed += (_, _) => onClosed?.Invoke();
        bool fromKeyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        menu.IsOpen = true;
        if (fromKeyboard && menu.Items.Count > 0)
            menu.Dispatcher.BeginInvoke(new Action(() => (menu.Items[0] as MenuItem)?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        return menu;
    }
}
