using System.Windows;
using Clink.Services.Localization;

namespace Clink.UI;

/// <summary>
/// The one place a Windows message box is shown (a test fails on any other <c>MessageBox.Show</c> call): in a right-to-left language the
/// box is laid out right-to-left and its text aligned right, as Windows does for its own boxes in Arabic.
/// </summary>
internal static class Dialog
{
    /// <summary>The MessageBox options for the active language: right-to-left reading and alignment for a right-to-left language, none otherwise.</summary>
    internal static MessageBoxOptions Options => L.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

    /// <summary>Shows a message box (over <paramref name="owner"/> when there is one) and returns the button pressed.</summary>
    internal static MessageBoxResult Show(Window? owner, string text, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult def = MessageBoxResult.None, string caption = "Clink") =>
        owner != null
            ? MessageBox.Show(owner, text, caption, buttons, icon, def, Options)
            : MessageBox.Show(text, caption, buttons, icon, def, Options);
}
