using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>The first-run wizard window, built but never shown: its accessibility ids, that its pages follow the model, hotkey cloning, relocation hand-off.</summary>
internal static class SetupWizardWindowTests
{
    private static SetupContext Ctx() => SetupWizardModelTests.Ctx();

    private static SetupWizardWindow Build(SetupContext? c = null, Func<RelocationPlan, nint, Task<RelocationResult>>? relocate = null, Action? hook = null)
        => new(new SetupWizardModel(c ?? Ctx()), relocate ?? ((_, _) => Task.FromResult(new RelocationResult(RelocationOutcome.Moved))), hook);

    private static T Find<T>(Window w, string name) where T : class => (w.FindName(name) as T) ?? throw new TestFailure("no element named " + name);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d)
                foreach (var x in Descendants(d)) yield return x;
    }

    private static HashSet<string> AutomationIds(Window w) =>
        Descendants(w).Select(AutomationProperties.GetAutomationId).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();

    private static Visibility Page(Window w, string name) => Find<StackPanel>(w, name).Visibility;

    [UnitTest]
    public static void Every_accessibility_id_exists()
    {
        var w = Build(Ctx() with { ReplaySeconds = 80 });
        var ids = AutomationIds(w);
        foreach (string id in new[]
        {
            "SetupWizard", "SetupBack", "SetupNext", "SetupSkip", "SetupClose", "SetupLocInstall", "SetupLocDocuments", "SetupLocProgramFiles", "SetupLocKeep",
            "SetupWarning", "SetupStatus", "SetupError", "SetupQualityLight", "SetupQualityBalanced", "SetupQualityHigh", "SetupQualityCustom",
            "SetupReplay30", "SetupReplay45", "SetupReplay60", "SetupReplay120", "SetupReplay300", "SetupReplayKeep",
            "SetupRunAtStartup", "SetupStartMenu", "SetupDesktop", "SetupFinish",
        })
            Check.True(ids.Contains(id), "AutomationId " + id);
        Check.Equal("SetupWizard", AutomationProperties.GetAutomationId(w));
    }

    [UnitTest]
    public static void Headings_are_level_one_and_status_lines_are_live_regions()
    {
        var w = Build();
        foreach (string heading in new[] { "LocationHeading", "QualityHeading", "HotkeyHeading", "StartupHeading", "DoneHeading" })
            Check.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(Find<TextBlock>(w, heading)), heading);
        Check.Equal(AutomationHeadingLevel.Level2, AutomationProperties.GetHeadingLevel(Find<TextBlock>(w, "ReplayHeading")), "the replay length is a sub-heading of the quality page");
        Check.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(Find<TextBlock>(w, "StatusText")));
        Check.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(Find<TextBlock>(w, "ErrorText")));
    }

    [UnitTest]
    public static void Options_carry_their_hint_as_help_text_and_groups_are_named_after_the_heading()
    {
        var w = Build();
        var install = Find<RadioButton>(w, "LocInstall");
        Check.Equal(SetupWizardTexts.InstallForMeLabel, AutomationProperties.GetName(install));
        Check.Equal(SetupWizardTexts.InstallForMeHint, AutomationProperties.GetHelpText(install));
        Check.Equal(SetupWizardTexts.LocationHeading, AutomationProperties.GetName(Find<StackPanel>(w, "LocationGroup")));
        Check.Equal(SetupWizardTexts.ReplayHeading, AutomationProperties.GetName(Find<StackPanel>(w, "ReplayGroup")));
        Check.Equal(SetupWizardTexts.QualityHeading, AutomationProperties.GetName(Find<StackPanel>(w, "QualityGroup")) ?? "");
    }

    [UnitTest]
    public static void The_window_is_not_topmost_and_activates_unless_quiet()
    {
        var w = Build();
        Check.False(w.Topmost);
        Check.Equal(!QuietMode.Active, w.ShowActivated);
        Check.Equal(SetupWizardTexts.Title, w.Title);
        Check.Equal(SizeToContent.Height, w.SizeToContent);
        var dev = Build(Ctx() with { Dev = true });
        Check.Equal(DevInstanceMarker.TagTitle(SetupWizardTexts.Title), dev.Title, "a dev window is tagged");
    }

    [UnitTest]
    public static void The_pages_follow_the_model()
    {
        var w = Build();
        var m = w.Model;
        Check.Equal(Visibility.Visible, Page(w, "LocationPage"));
        Check.Equal(Visibility.Collapsed, Page(w, "QualityPage"));
        Check.Equal(Visibility.Collapsed, Find<Button>(w, "BackButton").Visibility, "no Back on the first page");
        Check.Equal("Step 1 of 4", Find<TextBlock>(w, "StepText").Text);
        Check.True(Find<RadioButton>(w, "LocInstall").IsChecked == true, "install is the default");

        Find<RadioButton>(w, "LocKeep").IsChecked = true;
        Check.Equal(LocationChoice.KeepHere, m.Choice, "a radio sets the model");
        Check.Equal(SetupWizardTexts.Next, (string)AutomationProperties.GetName(Find<Button>(w, "NextButton")));
        Click(Find<Button>(w, "NextButton"));
        Check.Equal(SetupPage.Quality, m.Page);
        Check.Equal(Visibility.Visible, Page(w, "QualityPage"));
        Check.Equal(Visibility.Collapsed, Page(w, "LocationPage"));
        Check.Equal(Visibility.Visible, Find<Button>(w, "BackButton").Visibility);
        Check.True(Find<RadioButton>(w, "QBalanced").IsChecked == true);
        Check.Equal(Visibility.Collapsed, Find<RadioButton>(w, "QCustom").Visibility, "no custom quality saved");

        Find<RadioButton>(w, "QHigh").IsChecked = true;
        Check.Equal(QualityPreset.High, m.Quality);
        Click(Find<Button>(w, "NextButton"));
        Check.Equal(Visibility.Visible, Page(w, "HotkeyPage"));
        Click(Find<Button>(w, "NextButton"));
        Check.Equal(Visibility.Visible, Page(w, "StartupPage"));
        Check.Equal(Visibility.Visible, Find<CheckBox>(w, "StartMenuCheck").Visibility);
        Find<CheckBox>(w, "RunAtStartupCheck").IsChecked = true;
        Check.True(m.RunAtStartup);
        Click(Find<Button>(w, "NextButton"));
        Check.Equal(Visibility.Visible, Page(w, "DonePage"));
        Check.Equal(Visibility.Visible, Find<Button>(w, "FinishButton").Visibility);
        Check.Equal(Visibility.Collapsed, Find<Button>(w, "NextButton").Visibility);
        Check.Equal(Visibility.Collapsed, Find<Button>(w, "SkipButton").Visibility);
        Click(Find<Button>(w, "BackButton"));
        Check.Equal(Visibility.Visible, Page(w, "StartupPage"));
    }

    [UnitTest]
    public static void Elevated_hides_the_shortcut_options_and_says_why()
    {
        var w = Build(Ctx() with { Elevated = true });
        Check.Equal(Visibility.Visible, Page(w, "QualityPage"));
        Click(Find<Button>(w, "NextButton")); Click(Find<Button>(w, "NextButton"));
        Check.Equal(Visibility.Visible, Page(w, "StartupPage"));
        Check.Equal(Visibility.Collapsed, Find<CheckBox>(w, "StartMenuCheck").Visibility);
        Check.Equal(Visibility.Collapsed, Find<CheckBox>(w, "DesktopCheck").Visibility);
        Check.Equal(Visibility.Visible, Find<TextBlock>(w, "ShortcutsNote").Visibility);
    }

    [UnitTest]
    public static void The_location_page_shows_the_table_and_the_warning()
    {
        var w = Build(Ctx() with { Location = LocationClass.Temp, SingleFile = false });
        Check.Equal(Visibility.Visible, Find<Border>(w, "WarningPanel").Visibility);
        Check.Equal(SetupWizardTexts.WarnTemp, Find<TextBlock>(w, "WarningText").Text);
        Check.False(Find<RadioButton>(w, "LocInstall").IsEnabled, "this build cannot install itself");
        Check.Equal(SetupWizardTexts.CannotInstallSelf, Find<TextBlock>(w, "LocInstallHint").Text);
        Check.Equal(Visibility.Collapsed, Find<RadioButton>(w, "LocProgramFiles").Visibility);
        Check.True(Find<RadioButton>(w, "LocKeep").IsChecked == true);
        Check.Equal("It is running from your Downloads folder.", Find<TextBlock>(w, "PlaceLine").Text);
        var plain = Build(Ctx() with { Location = LocationClass.Other });
        Check.Equal(Visibility.Collapsed, Find<Border>(plain, "WarningPanel").Visibility, "no warning for a plain folder");
    }

    [UnitTest]
    public static void The_hotkey_page_edits_clones()
    {
        var original = SetupWizardModelTests.Clip(0x78);
        var w = Build(Ctx() with { Hotkeys = new[] { original } });
        Check.False(ReferenceEquals(original, w.Editor.Actions[0]), "the editor holds a clone");
        Check.False(w.Editor.ShowMoreHotkeys, "main hotkey only");
        w.Editor.Actions[0].Keys.Vk = 0x7A;
        Check.Equal(0x78, original.Keys.Vk, "the settings' hotkey is not edited");
        Check.True(ReferenceEquals(w.Editor.Actions[0], w.Model.HotkeysCopy[0]), "the model sees the editor's list");
    }

    [UnitTest]
    public static void Install_hands_the_plan_to_the_relocate_delegate_and_each_outcome_is_followed()
    {
        RelocationPlan? seen = null;
        RelocationOutcome outcome = RelocationOutcome.Failed;
        var w = Build(relocate: (plan, _) => { seen = plan; return Task.FromResult(new RelocationResult(outcome, "disk full")); });
        var next = Find<Button>(w, "NextButton");

        Check.Equal(SetupWizardTexts.Install, AutomationProperties.GetName(next));
        Click(next);
        Check.NotNull(seen, "the delegate ran");
        Check.Equal(SetupPage.Location, w.Model.Page, "Failed stays");
        Check.False(w.Model.Busy);
        Check.Equal(Visibility.Visible, Find<TextBlock>(w, "ErrorText").Visibility);
        Check.Contains(Find<TextBlock>(w, "ErrorText").Text, "disk full");
        Check.Equal(SetupWizardTexts.TryAgain, AutomationProperties.GetName(next));
        Check.Null(w.Result);
        Check.False(w.ExitRequested);

        w.Model.Choice = LocationChoice.KeepHere;
        Check.Equal(Visibility.Collapsed, Find<TextBlock>(w, "ErrorText").Visibility, "choosing again clears the error");

        // NothingToDo carries on
        seen = null; outcome = RelocationOutcome.NothingToDo;
        w = Build(relocate: (plan, _) => { seen = plan; return Task.FromResult(new RelocationResult(outcome)); });
        Click(Find<Button>(w, "NextButton"));
        Check.Equal(SetupPage.Quality, w.Model.Page);

        // Moved ends the wizard with no result
        outcome = RelocationOutcome.Moved;
        w = Build(relocate: (plan, _) => Task.FromResult(new RelocationResult(outcome)));
        Click(Find<Button>(w, "NextButton"));
        Check.True(w.ExitRequested, "Moved: exit requested");
        Check.Null(w.Result);
    }

    [UnitTest]
    public static void A_relocate_delegate_that_throws_is_a_failure_not_a_crash()
    {
        var w = Build(relocate: (_, _) => throw new InvalidOperationException("boom"));
        Click(Find<Button>(w, "NextButton"));
        Check.False(w.Model.Busy);
        Check.Contains(w.Model.Error, "boom");
        Check.False(w.ExitRequested);
    }

    [UnitTest]
    public static void Skip_closes_with_a_skipped_result_and_never_calls_the_relocate_delegate()
    {
        bool called = false;
        var w = Build(relocate: (_, _) => { called = true; return Task.FromResult(new RelocationResult(RelocationOutcome.Moved)); });
        Click(Find<Button>(w, "SkipButton"));
        Check.False(called, "Skip moves nothing");
        Check.True(w.Result is { Skipped: true });
        Check.False(w.ExitRequested);

        called = false;
        w = Build(relocate: (_, _) => { called = true; return Task.FromResult(new RelocationResult(RelocationOutcome.Moved)); });
        Click(Find<Button>(w, "CloseButton"));
        Check.False(called, "the close button is Skip");
        Check.True(w.Result is { Skipped: true });
    }

    [UnitTest]
    public static void The_close_button_finishes_on_the_Done_page_and_says_so()
    {
        var w = Build(Ctx() with { Mode = FirstRunMode.OptionsOnly });
        var close = Find<Button>(w, "CloseButton");
        Check.Equal(SetupWizardTexts.Skip, (string)close.ToolTip, "elsewhere the X skips");
        Check.Equal(SetupWizardTexts.CloseNameSkips, AutomationProperties.GetName(close));
        while (w.Model.Page != SetupPage.Done) Click(Find<Button>(w, "NextButton"));
        Check.Equal(SetupWizardTexts.Finish, (string)close.ToolTip);
        Check.Equal("Close setup", AutomationProperties.GetName(close));
        Click(close);
        Check.True(w.Result is { Skipped: false }, "the X on Done finishes, it does not skip");
        Check.NotNull(w.Result!.Hotkeys);
    }

    [UnitTest]
    public static void The_hotkey_hook_comes_from_the_caller_and_never_runs_for_a_window_that_is_not_shown()
    {
        int calls = 0;
        var w = Build(hook: () => calls++);
        Find<RadioButton>(w, "LocKeep").IsChecked = true;
        Click(Find<Button>(w, "NextButton")); Click(Find<Button>(w, "NextButton"));
        Check.Equal(SetupPage.Hotkey, w.Model.Page);
        Check.Equal(0, calls, "not loaded: nothing installed");
        Build(hook: null);   // no hook given: fine
    }

    [UnitTest]
    public static void On_the_hotkey_page_Enter_goes_to_the_focused_keys_button_not_to_Next_accepted()
    {
        // m11, accepted: Next stays the default button, but a focused Button takes Enter first (it starts a capture). Esc/Skip/Next by mouse or access key still work.
        var w = Build();
        Find<RadioButton>(w, "LocKeep").IsChecked = true;
        Click(Find<Button>(w, "NextButton")); Click(Find<Button>(w, "NextButton"));
        Check.Equal(SetupPage.Hotkey, w.Model.Page);
        Check.True(Find<Button>(w, "NextButton").IsDefault);
        Check.True(w.Editor.MainKeys is Button, "the page opens on a Button, so Enter starts a capture");
    }

    [UnitTest]
    public static void The_place_line_carries_the_full_path_as_help_text_and_the_done_page_follows_clips()
    {
        var w = Build(Ctx() with { PlaceTooltip = @"C:\Users\x\Downloads\Clink.exe" });
        Check.Equal(@"C:\Users\x\Downloads\Clink.exe", AutomationProperties.GetHelpText(Find<TextBlock>(w, "PlaceLine")));
        var off = Build(Ctx() with { ClipsEnabled = false });
        Find<RadioButton>(off, "LocKeep").IsChecked = true;
        while (off.Model.Page != SetupPage.Done) Click(Find<Button>(off, "NextButton"));
        Check.Equal(SetupWizardTexts.DoneTray, Find<TextBlock>(off, "DoneBody").Text);
    }

    [UnitTest]
    public static void Finish_closes_with_the_answers()
    {
        var w = Build(Ctx() with { Mode = FirstRunMode.OptionsOnly });
        Find<RadioButton>(w, "QLight").IsChecked = true;
        while (w.Model.Page != SetupPage.Done) Click(Find<Button>(w, "NextButton"));
        Click(Find<Button>(w, "FinishButton"));
        Check.True(w.Result is { Skipped: false, Quality: QualityPreset.Light });
        Check.NotNull(w.Result!.Hotkeys);
    }

    [UnitTest]
    public static void Access_keys_are_AccessText_so_no_button_or_checkbox_shows_a_literal_underscore()
    {
        var w = Build(Ctx() with { Mode = FirstRunMode.OptionsOnly });
        for (int n = 0; n < 6 && w.Model.Page != SetupPage.Startup; n++) Click(Find<Button>(w, "NextButton"));
        Check.Equal(SetupPage.Startup, w.Model.Page);   // the Startup checkboxes get their content when that page shows
        foreach (var d in Descendants(w))
            if (d is Button or CheckBox)
                Check.False(((ContentControl)d).Content is string s && s.Contains('_'), "literal underscore in " + ((FrameworkElement)d).Name);
        foreach (var (name, key) in new[] { ("BackButton", "B"), ("NextButton", w.Model.NextLabel[..1].ToUpperInvariant()), ("FinishButton", "F"), ("SkipButton", "K"), ("RunAtStartupCheck", "S"), ("StartMenuCheck", "M"), ("DesktopCheck", "D") })
        {
            var content = Find<ContentControl>(w, name).Content as AccessText ?? throw new TestFailure(name + " content is not an AccessText");
            Check.Equal(key, char.ToUpperInvariant(content.AccessKey).ToString(), name + " access key");
        }
        Check.Equal(w.Model.NextLabel, AutomationProperties.GetName(Find<Button>(w, "NextButton")));
        Check.Equal(SetupWizardTexts.RunAtStartupLabel, AutomationProperties.GetName(Find<CheckBox>(w, "RunAtStartupCheck")));
    }

    [UnitTest]
    public static void The_startup_checkboxes_are_separate_tab_stops_and_the_radio_groups_one_each()
    {
        var w = Build();
        Check.Equal(KeyboardNavigationMode.Continue, KeyboardNavigation.GetTabNavigation(Find<StackPanel>(w, "StartupGroup")));
        foreach (string g in new[] { "LocationGroup", "QualityGroup", "ReplayGroup" })
            Check.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(Find<StackPanel>(w, g)), g);
    }

    [UnitTest]
    public static void Focus_entering_an_option_group_goes_to_the_checked_option()
    {
        var w = Build();
        var group = Find<OptionGroup>(w, "QualityGroup");
        var light = Find<RadioButton>(w, "QLight");
        var balanced = Find<RadioButton>(w, "QBalanced");
        balanced.IsChecked = true;
        Check.True(ReferenceEquals(balanced, group.FocusTarget(Find<Button>(w, "BackButton"), light)), "from outside: the checked one");
        Check.True(group.FocusTarget(Find<Button>(w, "BackButton"), balanced) is null, "already on the checked one");
        Check.True(group.FocusTarget(light, balanced) is null, "from inside the group: leave it");
        Check.True(group.FocusTarget(null, light) is not null, "from nowhere counts as outside");
        balanced.IsEnabled = false;
        Check.True(group.FocusTarget(null, light) is null, "a disabled checked option is not a target");
    }}
