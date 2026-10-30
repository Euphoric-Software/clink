using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The welcome after the first "Sign in with Copium": what sharing does, and the automatic screenshot upload as a headline box (ticked at first).
/// Not modal; kept out of screen captures; in --dev-quiet it is shown without taking the focus (the quiet window hook keeps it cloaked).
/// Every way it ends reaches <see cref="CopiumWelcomeModel"/> once: Start sharing, Not now, Esc, the close button, Alt+F4. Enter on the default
/// button is ignored for the first <see cref="CopiumConsentWindow.InputDelay"/> (a stray key from the browser must not give consent).
/// </summary>
public partial class CopiumWelcomeWindow : Window, ICaptureExcludedWindow
{
    private readonly CopiumWelcomeModel _model;
    private readonly Func<bool> _appShuttingDown;
    private long _shownAt = long.MaxValue;

    bool ICaptureExcludedWindow.CaptureExcluded => true;

    static CopiumWelcomeWindow() => CaptureExclusion.Register();

    /// <param name="username">The account name (plain text).</param>
    /// <param name="appShuttingDown">True while the app exits: a window that WPF closes then is not an answer.</param>
    internal CopiumWelcomeWindow(CopiumWelcomeModel model, string? username, Func<bool> appShuttingDown)
    {
        InitializeComponent();
        _model = model;
        _appShuttingDown = appShuttingDown;
        Title = CopiumWelcomeModel.Title;
        TitleText.Text = CopiumWelcomeModel.Title;
        SignedInText.Text = CopiumWelcomeModel.SignedInLine(username);
        AutoUploadLabel.Text = CopiumWelcomeModel.AutoUploadLabel;
        AutomationProperties.SetName(AutoUploadCheck, CopiumWelcomeModel.AutoUploadLabel);
        AutoUploadCheck.IsChecked = model.InitialAutoUpload;
        AutoUploadDetail.Text = CopiumWelcomeModel.AutoUploadDetail;
        ClipsText.Text = CopiumWelcomeModel.ClipsLine;
        ChangeLaterText.Text = CopiumWelcomeModel.ChangeLaterLine;
        StartButton.Content = CopiumWelcomeModel.StartLabel;
        NotNowButton.Content = CopiumWelcomeModel.NotNowLabel;
        CloseButton.ToolTip = CopiumWelcomeModel.CloseLabel;
        ShowActivated = !QuietMode.Active;   // a --dev-quiet instance never takes the focus
        Topmost = false;
        SourceInitialized += (_, _) => ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        // The one place every path ends in; a click already answered. Windows sign-out / restart (SessionEnding): WPF closes this window
        // before the app's own shutdown flag is set, so it counts as Not now (no consent, one nudge) - the safe answer, accepted.
        Closed += (_, _) => _model.Ended(_appShuttingDown());
    }

    /// <summary>Test seam: milliseconds (Environment.TickCount64 by default) for the 400 ms input guard.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MarkShown();
        AutoUploadCheck.Focus();   // the headline is the first focus
    }

    /// <summary>The window is on screen now: the input guard counts from here (a window that was never shown accepts no click).</summary>
    internal void MarkShown() => _shownAt = Clock();

    private bool InputReady => CopiumConsentWindow.AcceptsInput(_shownAt, Clock());

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!InputReady) return;
        _model.StartSharing(AutoUploadCheck.IsChecked == true);
        Close();
    }

    private void NotNow_Click(object sender, RoutedEventArgs e) { _model.NotNow(); Close(); }

    private void Close_Click(object sender, RoutedEventArgs e) { _model.NotNow(); Close(); }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A held Enter / Space (key repeat) is never an answer: sharing starts only by a deliberate click or one press on the focused button.
        if (e.IsRepeat && (e.Key == Key.Enter || e.Key == Key.Space)) { e.Handled = true; return; }
        if (e.Key == Key.Escape) { _model.NotNow(); e.Handled = true; Close(); }
    }
}
