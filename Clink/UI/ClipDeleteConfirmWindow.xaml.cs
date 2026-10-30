using System.Windows;
using System.Windows.Input;

namespace Clink.UI;

/// <summary>
/// The "Delete clip?" pop-up of a clip row's Delete button: what happens (the Recycle Bin), a "Do not ask again" box and Delete /
/// Cancel. Modal, Theme-styled like <see cref="CopiumConsentWindow"/>, centred on its owner. Cancel is the default and the Esc
/// button, and input in the first moments is ignored, so a stray Enter or a double click never deletes. It is an ordinary window
/// (not kept out of screen captures: a pop-up that vanishes from a screen share would confuse more than it protects).
/// </summary>
public partial class ClipDeleteConfirmWindow : Window
{
    /// <summary>True when the user pressed Delete.</summary>
    internal bool Confirmed { get; private set; }

    /// <summary>The box was ticked; only meaningful together with <see cref="Confirmed"/> (Cancel never saves it).</summary>
    internal bool DoNotAskAgain => Confirmed && DoNotAskCheck.IsChecked == true;

    internal ClipDeleteConfirmWindow(string body, Window? owner)
    {
        InitializeComponent();
        BodyText.Text = body;
        if (owner != null && owner.IsLoaded)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    private long _shownAt = long.MaxValue;   // Environment.TickCount64 when loaded; not loaded yet = ignore input

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _shownAt = Environment.TickCount64;
        CancelButton.Focus();   // a stray Enter / Space lands on Cancel
    }

    private bool InputReady => CopiumConsentWindow.AcceptsInput(_shownAt, Environment.TickCount64);   // the same 400 ms guard

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!InputReady) return;
        Confirmed = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel_Click(sender, e); e.Handled = true; }
    }
}
