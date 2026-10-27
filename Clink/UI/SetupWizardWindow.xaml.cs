using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using Clink.Services;

namespace Clink.UI;

/// <summary>A stack of options that screen readers announce as a group (named after the page heading).</summary>
internal sealed class OptionGroup : StackPanel
{
    /// <summary>One Tab stop per group, arrow keys move between its options (and wrap round).</summary>
    public OptionGroup()
    {
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Cycle);
    }

    /// <summary>When focus arrives from outside the group, it lands on the checked option (not whichever radio the Tab order met first).</summary>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (FocusTarget(e.OldFocus as DependencyObject, e.NewFocus) is { } target)
        {
            e.Handled = true;
            target.Focus();
        }
    }

    /// <summary>The checked, enabled radio button to move focus to, or null to leave focus where it went (focus came from inside the group, landed on a non-radio, or is already there).</summary>
    internal RadioButton? FocusTarget(DependencyObject? oldFocus, object? newFocus)
    {
        if (newFocus is not RadioButton) return null;
        for (var p = oldFocus; p != null; p = (p as Visual) != null ? VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (ReferenceEquals(p, this)) return null;
        var checkedOne = Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true && r.IsEnabled && r.Visibility == Visibility.Visible);
        return checkedOne == null || ReferenceEquals(checkedOne, newFocus) ? null : checkedOne;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new OptionGroupPeer(this);

    private sealed class OptionGroupPeer : FrameworkElementAutomationPeer
    {
        public OptionGroupPeer(OptionGroup owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(OptionGroup);
        protected override bool IsControlElementCore() => true;
    }
}

/// <summary>
/// The first-run setup window: shows a <see cref="SetupWizardModel"/> (all rules live there) with the CopiumConsentWindow's chrome.
/// Modal (<see cref="Window.ShowDialog"/>); after it closes read <see cref="Result"/> (null when <see cref="ExitRequested"/>: the
/// copy at the new place took over and this process must exit). Nothing is moved before the user presses Install / Open Clink;
/// Skip, Esc and the close button never move anything (on the Done page Esc and the close button finish).
/// The keyboard hook that key capture needs is not installed here: the caller passes <c>ensureHotkeyHook</c> (the 3b caller passes
/// the <c>Hotkeys.Install</c> call), which runs once, before the first capture can arm, and not in quiet mode (a quiet instance never captures).
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardModel _m;
    private readonly Func<RelocationPlan, nint, Task<RelocationResult>> _relocate;
    private readonly Action? _ensureHotkeyHook;
    private bool _refreshing, _finished, _hookInstalled;
    private SetupPage? _focusedPage;
    private string? _lastStatus, _lastError;
    private readonly Dictionary<int, RadioButton> _replayRadios = new();

    /// <summary>What the user decided; null while open, and when <see cref="ExitRequested"/>.</summary>
    internal SetupResult? Result { get; private set; }

    /// <summary>The wizard started a copy at the new place and this process must end now (release the single-instance mutex on the UI thread first).</summary>
    internal bool ExitRequested { get; private set; }

    internal SetupWizardModel Model => _m;

    internal SetupWizardWindow(SetupWizardModel model, Func<RelocationPlan, nint, Task<RelocationResult>> relocate, Action? ensureHotkeyHook = null)
    {
        InitializeComponent();
        _m = model;
        _relocate = relocate;
        _ensureHotkeyHook = ensureHotkeyHook;
        ShowActivated = !QuietMode.Active;   // --dev-quiet: never takes the focus; never Topmost either way
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 24);
        Title = SetupWizardTexts.Title;
        if (_m.Context.Dev) DevInstanceMarker.KeepTitleTagged(this);   // the main window is not the first one: TagTestWindows would miss this one

        AutomationProperties.SetName(LocationGroup, SetupWizardTexts.LocationHeading);
        AutomationProperties.SetName(QualityGroup, SetupWizardTexts.QualityHeading);
        AutomationProperties.SetName(ReplayGroup, SetupWizardTexts.ReplayHeading);
        AutomationProperties.SetName(StartupGroup, SetupWizardTexts.StartupHeading);
        foreach (int n in _m.ReplayOptions) AddReplayRadio(n);
        Editor.Load(_m.HotkeysCopy, _m.ReplaySeconds);
        _m.SetHotkeys(Editor.Actions.ToList());
        Editor.Changed += () => _m.SetHotkeys(Editor.Actions.ToList());

        _m.Changed += Refresh;
        Refresh();
    }

    private void AddReplayRadio(int seconds)
    {
        bool standard = SetupPresets.IsReplayChoice(seconds);
        var r = new RadioButton
        {
            GroupName = "Replay",
            Style = (Style)FindResource("OptionRadio"),
            Margin = new Thickness(0, 0, 6, 6),
            Content = new TextBlock { Text = standard ? SetupPresets.ReplayLabel(seconds) : SetupWizardTexts.ReplayKeep(seconds), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold },
            Tag = seconds,
        };
        AutomationProperties.SetAutomationId(r, standard ? $"SetupReplay{seconds}" : "SetupReplayKeep");
        AutomationProperties.SetName(r, standard ? SetupPresets.ReplayLabel(seconds) : SetupWizardTexts.ReplayKeep(seconds));
        r.Checked += (_, _) => { if (!_refreshing) _m.ReplaySeconds = seconds; };
        _replayRadios[seconds] = r;
        ReplayPanel.Children.Add(r);
    }

    // ------------------------------------------------------------------ showing the model

    private static void Describe(RadioButton r, TextBlock label, TextBlock hint, string labelText, string hintText)
    {
        label.Text = labelText;
        hint.Text = hintText;
        hint.Visibility = hintText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(r, labelText);
        AutomationProperties.SetHelpText(r, hintText);
    }

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var m = _m;
            var page = m.Page;
            LocationPage.Visibility = page == SetupPage.Location ? Visibility.Visible : Visibility.Collapsed;
            QualityPage.Visibility = page == SetupPage.Quality ? Visibility.Visible : Visibility.Collapsed;
            HotkeyPage.Visibility = page == SetupPage.Hotkey ? Visibility.Visible : Visibility.Collapsed;
            StartupPage.Visibility = page == SetupPage.Startup ? Visibility.Visible : Visibility.Collapsed;
            DonePage.Visibility = page == SetupPage.Done ? Visibility.Visible : Visibility.Collapsed;

            TitleText.Text = SetupWizardTexts.Title;
            StepText.Text = page == SetupPage.Done ? "" : SetupWizardTexts.StepOf(m.StepNumber, m.StepCount);

            switch (page)
            {
                case SetupPage.Location: RefreshLocation(); break;
                case SetupPage.Quality: RefreshQuality(); break;
                case SetupPage.Hotkey: RefreshHotkey(); break;
                case SetupPage.Startup: RefreshStartup(); break;
                case SetupPage.Done:
                    DoneHeading.Text = SetupWizardTexts.DoneHeading;
                    DoneBody.Text = SetupWizardTexts.DoneBody(m.MainHotkeyText, m.Context.ClipsEnabled);
                    break;
            }

            BackButton.Visibility = m.Page == m.Pages[0] ? Visibility.Collapsed : Visibility.Visible;
            BackButton.IsEnabled = m.CanBack;
            bool done = page == SetupPage.Done;
            NextButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            NextButton.IsDefault = !done;
            NextButton.Content = new AccessText { Text = "_" + m.NextLabel };
            AutomationProperties.SetName(NextButton, m.NextLabel);
            NextButton.IsEnabled = m.CanNext;
            FinishButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            FinishButton.IsDefault = done;
            FinishButton.IsEnabled = m.CanNext;
            SkipButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            SkipButton.IsEnabled = m.CanSkip;
            CloseButton.IsEnabled = !m.Busy;
            CloseButton.ToolTip = done ? SetupWizardTexts.Finish : SetupWizardTexts.Skip;   // the X finishes on Done, skips elsewhere
            AutomationProperties.SetName(CloseButton, done ? SetupWizardTexts.CloseName : SetupWizardTexts.CloseNameSkips);
        }
        finally { _refreshing = false; }
    }

    private void RefreshLocation()
    {
        var m = _m;
        LocationHeading.Text = SetupWizardTexts.LocationHeading;
        AutomationProperties.SetName(LocationGroup, SetupWizardTexts.LocationHeading);
        PlaceLine.Text = SetupWizardTexts.RunningFrom(m.Context.PlaceText);
        PlaceLine.ToolTip = string.IsNullOrEmpty(m.Context.PlaceTooltip) ? null : m.Context.PlaceTooltip;
        AutomationProperties.SetHelpText(PlaceLine, m.Context.PlaceTooltip ?? "");   // the full path, for screen readers too
        WarningPanel.Visibility = string.IsNullOrEmpty(m.Warning) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = m.Warning ?? "";

        void Show(LocationChoice choice, RadioButton r, TextBlock label, TextBlock hint)
        {
            var o = m.LocationOptions.First(x => x.Choice == choice);
            Describe(r, label, hint, o.Label, o.Enabled || string.IsNullOrEmpty(o.DisabledReason) ? o.Hint : o.DisabledReason);
            r.Visibility = o.Visible ? Visibility.Visible : Visibility.Collapsed;
            r.IsEnabled = o.Enabled && !m.Busy;
            r.IsChecked = m.Choice == choice;
        }
        Show(LocationChoice.InstallForMe, LocInstall, LocInstallLabel, LocInstallHint);
        Show(LocationChoice.Documents, LocDocuments, LocDocumentsLabel, LocDocumentsHint);
        Show(LocationChoice.ProgramFiles, LocProgramFiles, LocProgramFilesLabel, LocProgramFilesHint);
        Show(LocationChoice.KeepHere, LocKeep, LocKeepLabel, LocKeepHint);
        LocKeep.IsEnabled = !m.Busy;

        RecycleHint.Text = SetupWizardTexts.RecycleHint;
        RecycleHint.Visibility = m.Choice == LocationChoice.KeepHere ? Visibility.Collapsed : Visibility.Visible;

        SetLive(StatusText, m.Status, ref _lastStatus);
        SetLive(ErrorText, m.Error, ref _lastError);
    }

    /// <summary>Shows a status / error line (hidden when empty) and tells assistive technology when its text changes.</summary>
    private void SetLive(TextBlock box, string? text, ref string? last)
    {
        box.Text = text ?? "";
        box.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(text) && text != last)
        {
            // After the visibility change has been laid out, and only when someone listens (the peer is created on demand).
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
                    UIElementAutomationPeer.CreatePeerForElement(box)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        last = text;
    }

    private void RefreshQuality()
    {
        var m = _m;
        QualityHeading.Text = SetupWizardTexts.QualityHeading;
        AutomationProperties.SetName(QualityGroup, SetupWizardTexts.QualityHeading);
        Describe(QLight, QLightLabel, QLightHint, SetupWizardTexts.LightLabel, SetupWizardTexts.LightHint);
        Describe(QBalanced, QBalancedLabel, QBalancedHint, SetupWizardTexts.BalancedLabel, SetupWizardTexts.BalancedHint);
        Describe(QHigh, QHighLabel, QHighHint, SetupWizardTexts.HighLabel, SetupWizardTexts.HighHint);
        Describe(QCustom, QCustomLabel, QCustomHint, SetupWizardTexts.CustomLabel, m.Context.CustomQualityText ?? "");
        QCustom.Visibility = m.OffersCustomQuality ? Visibility.Visible : Visibility.Collapsed;
        QLight.IsChecked = m.Quality == QualityPreset.Light;
        QBalanced.IsChecked = m.Quality == QualityPreset.Balanced;
        QHigh.IsChecked = m.Quality == QualityPreset.High;
        QCustom.IsChecked = m.Quality == QualityPreset.Custom;
        ReplayHeading.Text = SetupWizardTexts.ReplayHeading;
        AutomationProperties.SetName(ReplayGroup, SetupWizardTexts.ReplayHeading);
        foreach (var (seconds, r) in _replayRadios) r.IsChecked = m.ReplaySeconds == seconds;
        ReplayHint.Text = SetupWizardTexts.ReplayHint;
    }

    private void RefreshHotkey()
    {
        HotkeyHeading.Text = SetupWizardTexts.HotkeyHeading;
        HotkeyBody.Text = SetupWizardTexts.HotkeyBody(_m.ReplaySeconds);
        HotkeyFooter.Text = SetupWizardTexts.HotkeyFooter;
        Editor.DefaultSeconds = _m.ReplaySeconds;
        _m.SetHotkeys(Editor.Actions.ToList());
        EnsureHotkeyHook();
    }

    /// <summary>
    /// Key capture needs the keyboard hook, which AppController.Start installs later: the caller's <c>ensureHotkeyHook</c> installs it
    /// (idempotent, no bindings). Runs once, when the window is on screen and the Hotkey page shows; never in quiet mode (capture cannot
    /// arm there) and never in tests (the window is not shown).
    /// </summary>
    private void EnsureHotkeyHook()
    {
        if (_hookInstalled || !IsLoaded || QuietMode.Active || _ensureHotkeyHook == null) return;
        _hookInstalled = true;
        try { _ensureHotkeyHook(); }
        catch (Exception ex) { Log.Warn("setup: the hotkey hook could not be installed: " + ex.Message); }
    }

    private void RefreshStartup()
    {
        var m = _m;
        StartupHeading.Text = SetupWizardTexts.StartupHeading;
        AutomationProperties.SetName(StartupGroup, SetupWizardTexts.StartupHeading);
        RunAtStartupCheck.Content = new AccessText { Text = SetupWizardTexts.RunAtStartupAccess, TextWrapping = TextWrapping.Wrap };
        StartMenuCheck.Content = new AccessText { Text = SetupWizardTexts.StartMenuAccess, TextWrapping = TextWrapping.Wrap };
        DesktopCheck.Content = new AccessText { Text = SetupWizardTexts.DesktopAccess, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(RunAtStartupCheck, SetupWizardTexts.RunAtStartupLabel);
        AutomationProperties.SetName(StartMenuCheck, SetupWizardTexts.StartMenuLabel);
        AutomationProperties.SetName(DesktopCheck, SetupWizardTexts.DesktopLabel);
        RunAtStartupCheck.IsChecked = m.RunAtStartup;
        StartMenuCheck.IsChecked = m.StartMenu;
        DesktopCheck.IsChecked = m.Desktop;
        var shortcuts = m.ShortcutsAvailable ? Visibility.Visible : Visibility.Collapsed;
        StartMenuCheck.Visibility = DesktopCheck.Visibility = shortcuts;
        ShortcutsNote.Text = SetupWizardTexts.ElevatedShortcutsNote;
        ShortcutsNote.Visibility = m.ShortcutsAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ input

    private void Loc_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _m.Choice = ReferenceEquals(sender, LocInstall) ? LocationChoice.InstallForMe
            : ReferenceEquals(sender, LocDocuments) ? LocationChoice.Documents
            : ReferenceEquals(sender, LocProgramFiles) ? LocationChoice.ProgramFiles : LocationChoice.KeepHere;
    }

    private void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _m.Quality = ReferenceEquals(sender, QLight) ? QualityPreset.Light
            : ReferenceEquals(sender, QBalanced) ? QualityPreset.Balanced
            : ReferenceEquals(sender, QHigh) ? QualityPreset.High : QualityPreset.Custom;
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        bool on = ((CheckBox)sender).IsChecked == true;
        if (ReferenceEquals(sender, RunAtStartupCheck)) _m.RunAtStartup = on;
        else if (ReferenceEquals(sender, StartMenuCheck)) _m.StartMenu = on;
        else _m.Desktop = on;
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await RunStep(_m.Next());
    private async void Finish_Click(object sender, RoutedEventArgs e) => await RunStep(_m.Next());
    private async void Back_Click(object sender, RoutedEventArgs e) => await RunStep(_m.Back());
    private async void Skip_Click(object sender, RoutedEventArgs e) => await RunStep(_m.Skip());
    /// <summary>The header X: finishes on the Done page, skips anywhere else (the same as Esc).</summary>
    private async void Close_Click(object sender, RoutedEventArgs e) => await RunStep(_m.Page == SetupPage.Done ? _m.Next() : _m.Skip());

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        if (Editor.IsCapturing) { Editor.CancelCapture(); return; }   // Esc cancels the key capture, nothing more
        await RunStep(_m.Page == SetupPage.Done ? _m.Next() : _m.Skip());
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        if (_m.Page == SetupPage.Hotkey) EnsureHotkeyHook();
        FocusFirstOption();
    }

    /// <summary>Acts on what the model asked for. Internal so a test can wait for a relocation.</summary>
    internal async Task RunStep(SetupStep step)
    {
        switch (step)
        {
            case SetupStep.ShowPage show:
                if (_focusedPage != show.Page) FocusFirstOption();
                break;
            case SetupStep.Relocate relocate:
                RelocationResult result;
                try { result = await _relocate(relocate.Plan, new WindowInteropHelper(this).Handle); }
                catch (Exception ex) { result = new RelocationResult(RelocationOutcome.Failed, ex.Message); }
                await RunStep(_m.OnRelocated(result));
                // A refused / failed run leaves the page as it was, but the options were disabled while busy and lost the focus.
                if (!_finished && _m.Page == SetupPage.Location) FocusFirstOption();
                break;
            case SetupStep.Finish finish:
                Result = finish.Result;
                _finished = true;
                Close();
                break;
            case SetupStep.Exit:
                ExitRequested = true;
                _finished = true;
                Close();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // A closed window must not stay the application's main window (the caller may show the real one next).
        if (Application.Current?.MainWindow == this) Application.Current.MainWindow = null;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            if (_m.Busy) { e.Cancel = true; return; }   // an install is running
            Result = _m.ResultFor(skipped: _m.Page != SetupPage.Done);
            _finished = true;
        }
        Editor.CancelCapture();
        base.OnClosing(e);
    }

    /// <summary>Puts the focus on the first enabled option of the page (without choosing it); only once the window is on screen.</summary>
    private void FocusFirstOption()
    {
        _focusedPage = _m.Page;
        if (!IsLoaded) return;
        UIElement? target = _m.Page switch
        {
            SetupPage.Location => new RadioButton[] { LocInstall, LocDocuments, LocKeep }.FirstOrDefault(r => r.IsChecked == true && r.IsEnabled && r.IsVisible) ?? LocKeep,
            SetupPage.Quality => new RadioButton[] { QLight, QBalanced, QHigh, QCustom }.FirstOrDefault(r => r.IsChecked == true && r.IsVisible),
            // Enter on this page goes to the focused keys button (it starts a capture), not to Next: accepted, the button is what the page is for.
            SetupPage.Hotkey => Editor,
            SetupPage.Startup => RunAtStartupCheck,
            _ => FinishButton,
        };
        Dispatcher.BeginInvoke(() =>
        {
            if (target is RadioButton or CheckBox or Button) target.Focus();
            else target?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
