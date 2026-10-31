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
    public static ContextMenu Open(Button target, IReadOnlyList<ShareChoiceItem> items, Action<ShareChoice> onPick, Action? onClosed = null)
    {
        var menu = new ContextMenu
        {
            Style = (Style)target.FindResource("FlyoutMenu"),
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
        };
        var itemStyle = (Style)target.FindResource("FlyoutMenuItem");
        bool picked = false;
        foreach (var item in items)
        {
            var choice = item.Choice;
            var mi = new MenuItem { Header = item.Header, ToolTip = item.ToolTip, Style = itemStyle };
            AutomationProperties.SetName(mi, item.Header.Replace("_", ""));
            mi.Click += (_, _) => { if (picked) return; picked = true; onPick(choice); };
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
