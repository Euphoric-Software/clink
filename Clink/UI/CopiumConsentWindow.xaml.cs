using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The "Share with Copium?" pop-up (S7), and, in <see cref="Mode.ConfirmCloudSharing"/>, the confirmation that turns the cloud kill
/// switch back on: one dialog, one warning panel (<see cref="WarningTextValue"/>). Modal (<see cref="ShowDialog"/>), Theme-styled like
/// <see cref="ArchiveWindow"/>. When it comes from a screenshot action (a toast, the overlay) it is topmost and kept out of screen
/// captures (<see cref="ICaptureExcludedWindow"/>) so it never lands in a recording or the next screenshot.
/// </summary>
public partial class CopiumConsentWindow : Window, ICaptureExcludedWindow
{
    public enum Mode { Consent, ConfirmCloudSharing }

    /// <summary>The warning shown in both modes: sharing is public and cannot be taken back by Clink.</summary>
    internal const string WarningTextValue = "Anything you upload is immediately publicly accessible to anyone who has the link. Links are not private and cannot be recalled by Clink.";

    private readonly bool _excludeFromCapture;
    private readonly Mode _mode;

    /// <summary>What the user chose; <see cref="ConsentChoice.NotNow"/> when the window was closed another way.</summary>
    internal ConsentAnswer Answer { get; private set; } = new(ConsentChoice.NotNow);

    bool ICaptureExcludedWindow.CaptureExcluded => _excludeFromCapture;

    static CopiumConsentWindow() => CaptureExclusion.Register();

    /// <param name="offerAutoUpload">Show "Also upload every screenshot I take automatically" (unchecked).</param>
    /// <param name="excludeFromCapture">Keep the window out of captures and recordings, and on top (a screenshot action, or no window of ours in front).</param>
    internal CopiumConsentWindow(Mode mode, bool offerAutoUpload, bool excludeFromCapture)
    {
        InitializeComponent();
        _mode = mode;
        _excludeFromCapture = excludeFromCapture;
        WarningText.Text = WarningTextValue;
        AutoUploadCheck.Visibility = mode == Mode.Consent && offerAutoUpload ? Visibility.Visible : Visibility.Collapsed;
        if (mode == Mode.ConfirmCloudSharing)
        {
            Title = "Allow cloud sharing?";
            TitleText.Text = "Allow cloud sharing?";
            BodyText.Text = "Turning cloud sharing on lets Clink upload clips and screenshots to your Copium account when you ask it to, and shows the sharing buttons again.";
            EnableButton.Content = "Yes, allow cloud sharing";
            NotNowButton.Content = "Cancel";
            DontAskButton.Visibility = Visibility.Collapsed;
        }
        if (excludeFromCapture)
        {
            Topmost = !QuietMode.Active;   // --dev-quiet: never topmost
            SourceInitialized += (_, _) => ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        }
    }

    private void Finish(ConsentChoice choice)
    {
        Answer = new ConsentAnswer(choice, choice == ConsentChoice.Enable && _mode == Mode.Consent && AutoUploadCheck.Visibility == Visibility.Visible && AutoUploadCheck.IsChecked == true);
        DialogResult = choice == ConsentChoice.Enable;
    }

    /// <summary>A click or key that lands this soon after the window appears is not an answer (a hotkey's key-up, a game's input, a double click).</summary>
    internal static readonly TimeSpan InputDelay = TimeSpan.FromMilliseconds(400);

    private long _shownAt = long.MaxValue;   // Environment.TickCount64 when loaded; not loaded yet = ignore input

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _shownAt = Environment.TickCount64;
        NotNowButton.Focus();   // a stray Enter / Space lands on Not now
    }

    /// <summary>Input is accepted once <see cref="InputDelay"/> has passed since the window opened.</summary>
    internal static bool AcceptsInput(long shownAtTicks, long nowTicks) => nowTicks - shownAtTicks >= (long)InputDelay.TotalMilliseconds;

    private bool InputReady => AcceptsInput(_shownAt, Environment.TickCount64);

    private void Enable_Click(object sender, RoutedEventArgs e) { if (InputReady) Finish(ConsentChoice.Enable); }
    private void NotNow_Click(object sender, RoutedEventArgs e) => Finish(ConsentChoice.NotNow);
    private void DontAsk_Click(object sender, RoutedEventArgs e) { if (InputReady) Finish(ConsentChoice.DontAskAgain); }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(ConsentChoice.NotNow); e.Handled = true; }
    }
}
