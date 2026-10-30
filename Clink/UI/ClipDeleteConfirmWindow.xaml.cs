using System.Windows;
using System.Windows.Input;
using Clink.Services;

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

    /// <summary>What the user picked (Cancel until a delete button is pressed). Only the trims mode offers <see cref="ClipDeleteChoice.ClipAndTrims"/>.</summary>
    internal ClipDeleteChoice Choice { get; private set; } = ClipDeleteChoice.Cancel;

    internal ClipDeleteConfirmWindow(string body, Window? owner) : this(body, owner, 0, null) { }

    /// <summary>
    /// <paramref name="trimCount"/> above 0 is the trims mode: "Delete full clip only" and "Delete clip and N trims" replace Delete,
    /// and there is no "Do not ask again" (the question is a choice, so it is always asked). <paramref name="blockedText"/> (not
    /// null) says why the trims cannot go now: it shows under the body and turns the second button off.
    /// </summary>
    internal ClipDeleteConfirmWindow(string body, Window? owner, int trimCount, string? blockedText)
    {
        InitializeComponent();
        BodyText.Text = body;
        if (ClipDeleter.TrimsMode(trimCount))
        {
            Width = 560;   // Cancel + "Delete full clip only" + "Delete clip and N trims" side by side, with room for larger system text
            DeleteButton.Visibility = Visibility.Collapsed;
            DoNotAskCheck.Visibility = Visibility.Collapsed;
            ClipOnlyButton.Visibility = Visibility.Visible;
            WithTrimsButton.Visibility = Visibility.Visible;
            WithTrimsButton.Content = $"Delete clip and {ClipGrouping.TrimsLabel(trimCount)}";
            if (!string.IsNullOrEmpty(blockedText))
            {
                BlockedText.Text = blockedText;
                BlockedText.Visibility = Visibility.Visible;
                WithTrimsButton.IsEnabled = false;
                System.Windows.Automation.AutomationProperties.SetHelpText(WithTrimsButton, blockedText);
            }
        }
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
        Choice = ClipDeleteChoice.ClipOnly;
        DialogResult = true;
    }

    private void ClipOnly_Click(object sender, RoutedEventArgs e) => Delete_Click(sender, e);

    private void WithTrims_Click(object sender, RoutedEventArgs e)
    {
        if (!InputReady) return;
        Confirmed = true;
        Choice = ClipDeleteChoice.ClipAndTrims;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Choice = ClipDeleteChoice.Cancel;
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
