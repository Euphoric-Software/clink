using System.Windows;
using System.Windows.Input;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// "Third-party notices" (App tab, About): the licences of the software Clink includes, in a read-only text box (select and copy work). Not modal, and one at a time: opening it again brings the open one forward. Reads the embedded resource only: no links, no
/// network, no files written. Esc and Close close it. Every colour is a DynamicResource.
/// </summary>
public partial class ThirdPartyNoticesWindow : Window
{
    private static ThirdPartyNoticesWindow? s_open;

    internal ThirdPartyNoticesWindow()
    {
        InitializeComponent();
        NoticesText.Text = ThirdPartyNotices.Load();
        ShowActivated = !QuietMode.Active;   // a --dev-quiet instance never takes the focus
        Loaded += (_, _) => NoticesText.Focus();
        Closed += (_, _) => { if (ReferenceEquals(s_open, this)) s_open = null; };
    }

    /// <summary>The open window, or null (a seam for the tests).</summary>
    internal static ThirdPartyNoticesWindow? Open => s_open;

    /// <summary>
    /// Opens the window over <paramref name="owner"/>, or brings the open one forward, and returns it. <paramref name="show"/> is a seam
    /// for the tests (they must not put a window on screen); the default is <c>Show()</c>. The single-instance reference is set only
    /// after it succeeded.
    /// </summary>
    internal static ThirdPartyNoticesWindow ShowFor(Window? owner, Action<Window>? show = null)
    {
        if (s_open != null)
        {
            var open = s_open;
            if (open.WindowState == WindowState.Minimized)
            {
                if (QuietMode.Active) QuietMode.ShowWithoutActivating(open);   // SW_SHOWNOACTIVATE: a quiet instance never takes the focus
                else open.WindowState = WindowState.Normal;
            }
            if (!QuietMode.Active) open.Activate();
            return open;
        }
        var window = new ThirdPartyNoticesWindow { Owner = owner is { IsVisible: true } ? owner : null };
        (show ?? (w => w.Show()))(window);
        s_open = window;
        return window;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
