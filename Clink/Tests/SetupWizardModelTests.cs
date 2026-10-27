using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>The first-run wizard's rules (no window): page order, the Location table, busy state, outcomes, results, texts, and SetupApply.</summary>
internal static class SetupWizardModelTests
{
    private const string Source = @"C:\Users\x\Downloads\Clink.exe";
    private const string Target = @"C:\Users\x\AppData\Local\Programs\Clink\Clink.exe";
    private const string DocsTarget = @"C:\Users\x\Documents\Clink\Clink.exe";

    internal static HotkeyAction Clip(int vk = 0x78) => new() { Keys = new HotkeyBinding { Vk = vk }, Action = "clip", Seconds = 0 };

    internal static SetupContext Ctx() => new(
        Mode: FirstRunMode.Full, Location: LocationClass.Downloads, DuplicateName: false, PlaceText: "your Downloads folder",
        SingleFile: true, Elevated: false, Dev: false, DocumentsCloudSynced: false,
        InstallPlan: new RelocationPlan(RelocationKind.Install, Source, Target),
        DocumentsPlan: new RelocationPlan(RelocationKind.Install, Source, DocsTarget),
        TargetInstallerManaged: false, OfferProgramFiles: false, FreshStore: true, ClipsEnabled: true,
        Quality: QualityPreset.Balanced, ReplaySeconds: 45, RunAtStartup: false, Hotkeys: new[] { Clip() });

    private static SetupWizardModel Model(SetupContext? c = null) => new(c ?? Ctx());

    private static SetupPage Shown(SetupStep s) => ((SetupStep.ShowPage)s).Page;

    private static List<SetupPage> Walk(SetupWizardModel m)
    {
        var seen = new List<SetupPage> { m.Page };
        while (m.Page != SetupPage.Done) { m.Choice = LocationChoice.KeepHere; Shown(m.Next()); seen.Add(m.Page); }
        return seen;
    }

    // ------------------------------------------------------------------ pages

    [UnitTest]
    public static void Full_runs_Location_Quality_Hotkey_Startup_Done_and_back_returns()
    {
        var m = Model();
        Check.Equal(new[] { SetupPage.Location, SetupPage.Quality, SetupPage.Hotkey, SetupPage.Startup, SetupPage.Done }, m.Pages.ToArray());
        Check.Equal(SetupPage.Location, m.Page);
        Check.False(m.CanBack, "no Back on the first page");
        m.Choice = LocationChoice.KeepHere;
        Check.Equal(SetupPage.Quality, Shown(m.Next()));
        Check.True(m.CanBack);
        Check.Equal(SetupPage.Hotkey, Shown(m.Next()));
        Check.Equal(SetupPage.Startup, Shown(m.Next()));
        Check.Equal(SetupPage.Done, Shown(m.Next()));
        Check.Equal(SetupPage.Startup, Shown(m.Back()));
        Check.Equal(SetupPage.Done, Shown(m.Next()));
        Check.True(m.Next() is SetupStep.Finish { Result.Skipped: false }, "Next on Done finishes");
    }

    [UnitTest]
    public static void Step_numbers_count_the_pages_before_Done()
    {
        var m = Model();
        Check.Equal(4, m.StepCount);
        Check.Equal(1, m.StepNumber);
        m.Choice = LocationChoice.KeepHere; m.Next();
        Check.Equal(2, m.StepNumber);
        while (m.Page != SetupPage.Done) m.Next();
        Check.Equal(4, m.StepNumber, "Done shows the last number");
    }

    [UnitTest]
    public static void OptionsOnly_starts_at_Quality_without_a_location_page()
    {
        var m = Model(Ctx() with { Mode = FirstRunMode.OptionsOnly });
        Check.Equal(new[] { SetupPage.Quality, SetupPage.Hotkey, SetupPage.Startup, SetupPage.Done }, m.Pages.ToArray());
        Check.Equal(SetupPage.Quality, m.Page);
        Check.Equal(3, m.StepCount);
        Check.Equal(LocationChoice.KeepHere, m.Choice);
    }

    [UnitTest]
    public static void Elevated_has_no_location_page_and_no_shortcuts()
    {
        var m = Model(Ctx() with { Elevated = true });
        Check.Equal(SetupPage.Quality, m.Page);
        Check.False(m.ShortcutsAvailable);
        m.StartMenu = true; m.Desktop = true;
        while (m.Page != SetupPage.Done) m.Next();
        var r = ((SetupStep.Finish)m.Next()).Result;
        Check.False(r.StartMenu, "no Start menu shortcut while elevated");
        Check.False(r.Desktop, "no desktop shortcut while elevated");
        Check.Null(m.Warning, "no location warning without a location page");
    }

    [UnitTest]
    public static void Clips_off_hides_the_quality_and_hotkey_pages()
    {
        var m = Model(Ctx() with { ClipsEnabled = false });
        Check.Equal(new[] { SetupPage.Location, SetupPage.Startup, SetupPage.Done }, m.Pages.ToArray());
        m.Choice = LocationChoice.KeepHere;
        while (m.Page != SetupPage.Done) m.Next();
        var r = ((SetupStep.Finish)m.Next()).Result;
        Check.Null(r.Hotkeys, "hotkeys not shown: nothing to apply");
        var o = Model(Ctx() with { Mode = FirstRunMode.OptionsOnly, ClipsEnabled = false });
        Check.Equal(new[] { SetupPage.Startup, SetupPage.Done }, o.Pages.ToArray());
    }

    // ------------------------------------------------------------------ install and outcomes

    [UnitTest]
    public static void Install_returns_a_Relocate_step_and_goes_busy()
    {
        var m = Model();
        Check.Equal(LocationChoice.InstallForMe, m.Choice);
        Check.Equal(SetupWizardTexts.Install, m.NextLabel);
        var step = m.Next();
        Check.True(step is SetupStep.Relocate r && r.Plan.Target == Target, "Relocate with the install plan");
        Check.True(m.Busy);
        Check.Equal(SetupWizardTexts.BusyInstalling, m.Status);
        Check.False(m.CanNext, "Next blocked");
        Check.False(m.CanBack, "Back blocked");
        Check.False(m.CanSkip, "Skip blocked");
        Check.True(m.Skip() is SetupStep.ShowPage, "Skip ignored while busy");
        Check.True(m.Back() is SetupStep.ShowPage, "Back ignored while busy");
        Check.True(m.Next() is SetupStep.ShowPage, "a second Next does not start a second install");
        m.Choice = LocationChoice.KeepHere;
        Check.Equal(LocationChoice.InstallForMe, m.Choice, "the choice is frozen while busy");
    }

    [UnitTest]
    public static void Documents_choice_relocates_with_the_documents_plan()
    {
        var m = Model();
        m.Choice = LocationChoice.Documents;
        Check.True(m.Next() is SetupStep.Relocate r && r.Plan.Target == DocsTarget);
    }

    [UnitTest]
    public static void Launching_the_installed_copy_says_so()
    {
        var plan = new RelocationPlan(RelocationKind.LaunchExisting, Source, Target, "installed");
        var m = Model(Ctx() with { InstallPlan = plan, InstalledVersionText = "0.4.2" });
        Check.Equal(SetupWizardTexts.OpenClink, m.NextLabel);
        Check.True(m.Next() is SetupStep.Relocate);
        Check.Equal(SetupWizardTexts.BusyStartingInstalled, m.Status);
    }

    [UnitTest]
    public static void The_installed_copy_option_only_exists_for_a_LaunchExisting_plan()
    {
        var install = Model(Ctx() with { TargetInstallerManaged = true });
        Check.Equal(SetupWizardTexts.InstallForMeLabel, install.LocationOptions[0].Label, "no \"use the installed Clink\" for an Install plan");
        Check.False(install.LocationOptions[0].Enabled);
        Check.Equal(SetupWizardTexts.InstalledByInstaller, install.LocationOptions[0].DisabledReason);
        Check.Equal(LocationChoice.KeepHere, install.Choice);
        Check.NotEqual(SetupWizardTexts.OpenClink, install.NextLabel);
        install.Choice = LocationChoice.InstallForMe;
        Check.Equal(LocationChoice.KeepHere, install.Choice, "the disabled option cannot be chosen");
        Check.True(install.Next() is SetupStep.ShowPage { Page: SetupPage.Quality }, "no relocation starts");
        Check.False(install.Busy);
    }

    [UnitTest]
    public static void A_failed_launch_of_the_installed_copy_says_so_in_its_own_words()
    {
        var plan = new RelocationPlan(RelocationKind.LaunchExisting, Source, Target, "installed");
        var m = Model(Ctx() with { InstallPlan = plan });
        m.Next();
        m.OnRelocated(new RelocationResult(RelocationOutcome.Failed, "access denied"));
        Check.Contains(m.Error, "The installed Clink could not be started: access denied");
        Check.False(m.Error!.Contains("could not be installed"), "not the install wording");
    }

    [UnitTest]
    public static void Each_relocation_outcome_moves_the_wizard_as_specified()
    {
        var m = Model(); m.Next();
        Check.True(m.OnRelocated(new RelocationResult(RelocationOutcome.Moved)) is SetupStep.Exit, "Moved exits");
        m = Model(); m.Next();
        Check.True(m.OnRelocated(new RelocationResult(RelocationOutcome.LaunchedExisting)) is SetupStep.Exit, "LaunchedExisting exits");

        m = Model(); m.Next();
        Check.Equal(SetupPage.Quality, Shown(m.OnRelocated(new RelocationResult(RelocationOutcome.NothingToDo))), "NothingToDo carries on");
        Check.False(m.Busy);

        foreach (var outcome in new[] { RelocationOutcome.Failed, RelocationOutcome.Refused })
        {
            m = Model(); m.Next();
            var step = m.OnRelocated(new RelocationResult(outcome, "disk full"));
            Check.Equal(SetupPage.Location, Shown(step), outcome + " stays");
            Check.False(m.Busy, outcome + ": not busy");
            Check.Null(m.Status, outcome + ": status cleared");
            Check.Contains(m.Error, "Clink could not be installed: disk full", outcome + ": error shown");
            Check.Contains(m.Error, "It still runs from here.", outcome + ": reassurance");
            Check.Equal(SetupWizardTexts.TryAgain, m.NextLabel, outcome + ": retry");
            Check.True(m.CanSkip && m.CanNext, outcome + ": buttons back");
            m.Choice = LocationChoice.KeepHere;
            Check.Equal(LocationChoice.KeepHere, m.Choice, outcome + ": Keep stays selectable");
            Check.Null(m.Error, "choosing again clears the error");
            Check.Equal(SetupPage.Quality, Shown(m.Next()), outcome + ": Keep goes on without moving");
        }
        Check.True(Model().OnRelocated(new RelocationResult(RelocationOutcome.Moved)) is SetupStep.ShowPage, "a result nobody asked for is ignored");
    }

    [UnitTest]
    public static void Keep_here_advances_without_a_relocation()
    {
        var m = Model();
        m.Choice = LocationChoice.KeepHere;
        Check.Equal(SetupWizardTexts.Next, m.NextLabel);
        Check.True(m.Next() is SetupStep.ShowPage { Page: SetupPage.Quality });
        Check.False(m.Busy);
    }

    [UnitTest]
    public static void Skip_never_relocates_and_keeps_the_saved_values()
    {
        var m = Model(Ctx() with { Quality = QualityPreset.High, ReplaySeconds = 120, RunAtStartup = true });
        m.Quality = QualityPreset.Light; m.ReplaySeconds = 30; m.RunAtStartup = false;   // choices made, then Skip
        Check.Equal(LocationChoice.InstallForMe, m.Choice);
        var step = m.Skip();
        var r = ((SetupStep.Finish)step).Result;
        Check.True(r.Skipped);
        Check.Equal(QualityPreset.High, r.Quality, "Skip reports the saved quality");
        Check.Equal(120, r.ReplaySeconds);
        Check.True(r.RunAtStartup);
        Check.False(r.StartMenu || r.Desktop, "Skip makes no shortcuts");
        Check.Null(r.Hotkeys);
        Check.False(m.Busy, "Skip did not start an install");
        Check.False(Model(Ctx() with { Mode = FirstRunMode.OptionsOnly }).Next() is SetupStep.Relocate);
        // not on the Done page: there is nothing to skip
        var d = Model(); Walk(d);
        Check.False(d.CanSkip);
    }

    [UnitTest]
    public static void Back_never_leaves_the_location_page_backwards_or_skips_pages()
    {
        var m = Model();
        Check.True(m.Back() is SetupStep.ShowPage { Page: SetupPage.Location });
        var seen = Walk(m);
        Check.Equal(5, seen.Count);
    }

    // ------------------------------------------------------------------ the location table (one row per rule)

    private sealed record Row(string Name, SetupContext Ctx, bool InstallVisible, bool InstallEnabled, string InstallLabel, bool DocsVisible, bool DocsEnabled, bool PfVisible, string KeepLabel, LocationChoice Default, string? InstallReason = null);

    private static IEnumerable<Row> Rows()
    {
        var c = Ctx();
        const string install = SetupWizardTexts.InstallForMeLabel, use = SetupWizardTexts.UseInstalledLabel, keep = SetupWizardTexts.KeepLabel;
        yield return new("plan Install", c, true, true, install, true, true, false, keep, LocationChoice.InstallForMe);
        yield return new("not a single file", c with { SingleFile = false }, true, false, install, true, false, false, keep, LocationChoice.KeepHere, SetupWizardTexts.CannotInstallSelf);
        yield return new("plan LaunchExisting", c with { InstallPlan = c.InstallPlan! with { Kind = RelocationKind.LaunchExisting } }, true, true, use, true, true, false, keep, LocationChoice.InstallForMe);
        yield return new("installer-managed target, plan Install: shown off", c with { TargetInstallerManaged = true }, true, false, install, true, true, false, keep, LocationChoice.KeepHere, SetupWizardTexts.InstalledByInstaller);
        yield return new("installer-managed target, plan LaunchExisting", c with { TargetInstallerManaged = true, InstallPlan = c.InstallPlan! with { Kind = RelocationKind.LaunchExisting } }, true, true, use, true, true, false, keep, LocationChoice.InstallForMe);
        yield return new("no install plan (null)", c with { InstallPlan = null }, false, false, install, true, true, false, keep, LocationChoice.KeepHere);
        yield return new("plan Refused", c with { InstallPlan = c.InstallPlan! with { Kind = RelocationKind.Refused, Reason = "Nope." } }, true, false, install, true, true, false, keep, LocationChoice.KeepHere, "Nope.");
        yield return new("plan NoOp", c with { InstallPlan = c.InstallPlan! with { Kind = RelocationKind.NoOp } }, false, false, install, true, true, false, keep, LocationChoice.KeepHere);
        yield return new("documents cloud-synced", c with { DocumentsCloudSynced = true }, true, true, install, false, false, false, keep, LocationChoice.InstallForMe);
        yield return new("documents plan NoOp", c with { DocumentsPlan = c.DocumentsPlan! with { Kind = RelocationKind.NoOp } }, true, true, install, false, false, false, SetupWizardTexts.KeepInDocumentsLabel, LocationChoice.InstallForMe);
        yield return new("no documents plan", c with { DocumentsPlan = null }, true, true, install, false, false, false, keep, LocationChoice.InstallForMe);
        yield return new("documents plan Refused", c with { DocumentsPlan = c.DocumentsPlan! with { Kind = RelocationKind.Refused, Reason = "Nope." } }, true, true, install, true, false, false, keep, LocationChoice.InstallForMe);
        yield return new("program files offered (not wired yet)", c with { OfferProgramFiles = true }, true, true, install, true, true, true, keep, LocationChoice.InstallForMe);
        yield return new("elevated never shows program files", c with { OfferProgramFiles = true, Elevated = true }, true, true, install, true, true, false, keep, LocationChoice.KeepHere);
    }

    [UnitTest]
    public static void The_location_table_follows_the_design_row_by_row()
    {
        foreach (var row in Rows())
        {
            var o = SetupWizardModel.BuildOptions(row.Ctx);
            var install = o.First(x => x.Choice == LocationChoice.InstallForMe);
            var docs = o.First(x => x.Choice == LocationChoice.Documents);
            var pf = o.First(x => x.Choice == LocationChoice.ProgramFiles);
            var keep = o.First(x => x.Choice == LocationChoice.KeepHere);
            Check.Equal(row.InstallVisible, install.Visible, row.Name + ": install visible");
            if (row.InstallVisible) Check.Equal(row.InstallEnabled, install.Enabled, row.Name + ": install enabled");
            if (row.InstallVisible) Check.Equal(row.InstallLabel, install.Label, row.Name + ": install label");
            if (row.InstallReason != null) Check.Equal(row.InstallReason, install.DisabledReason, row.Name + ": install reason");
            Check.Equal(row.DocsVisible, docs.Visible, row.Name + ": documents visible");
            if (row.DocsVisible) Check.Equal(row.DocsEnabled, docs.Enabled, row.Name + ": documents enabled");
            Check.Equal(row.PfVisible, pf.Visible, row.Name + ": program files visible");
            Check.True(keep.Visible && keep.Enabled, row.Name + ": keep always available");
            Check.Equal(row.KeepLabel, keep.Label, row.Name + ": keep label");
            Check.Equal(row.Default, new SetupWizardModel(row.Ctx).Choice, row.Name + ": default");
        }
        var notSingle = SetupWizardModel.BuildOptions(Ctx() with { SingleFile = false });
        Check.Equal(SetupWizardTexts.CannotInstallSelf, notSingle.First(x => x.Choice == LocationChoice.Documents).DisabledReason, "documents reason when not a single file");
    }

    [UnitTest]
    public static void A_disabled_option_cannot_be_chosen_or_started()
    {
        var m = Model(Ctx() with { SingleFile = false });
        m.Choice = LocationChoice.InstallForMe;
        Check.Equal(LocationChoice.KeepHere, m.Choice);
        m.Choice = LocationChoice.Documents;
        Check.Equal(LocationChoice.KeepHere, m.Choice);
        Check.True(m.Next() is SetupStep.ShowPage { Page: SetupPage.Quality });
    }

    [UnitTest]
    public static void One_warning_line_per_location_class_and_duplicate_name()
    {
        (LocationClass, string?)[] expected =
        {
            (LocationClass.Temp, SetupWizardTexts.WarnTemp), (LocationClass.Removable, SetupWizardTexts.WarnRemovable),
            (LocationClass.Network, SetupWizardTexts.WarnNetwork), (LocationClass.Cloud, SetupWizardTexts.WarnCloud),
            (LocationClass.Downloads, SetupWizardTexts.WarnDownloads), (LocationClass.Desktop, null),
            (LocationClass.Documents, null), (LocationClass.Other, null),
        };
        foreach (var (loc, text) in expected)
        {
            Check.Equal(text, Model(Ctx() with { Location = loc }).Warning, loc.ToString());
            Check.Equal(text ?? SetupWizardTexts.WarnDuplicate, Model(Ctx() with { Location = loc, DuplicateName = true }).Warning, loc + " with a duplicate name");
        }
    }

    // ------------------------------------------------------------------ defaults

    [UnitTest]
    public static void Defaults_come_from_the_context()
    {
        var m = Model();
        Check.Equal(QualityPreset.Balanced, m.Quality);
        Check.Equal(45, m.ReplaySeconds);
        Check.False(m.RunAtStartup);
        Check.False(m.Desktop);
        var saved = Model(Ctx() with { Quality = QualityPreset.High, ReplaySeconds = 120, RunAtStartup = true, FreshStore = false });
        Check.Equal(QualityPreset.High, saved.Quality);
        Check.Equal(120, saved.ReplaySeconds);
        Check.True(saved.RunAtStartup);
    }

    [UnitTest]
    public static void Start_menu_is_on_except_for_a_copy_that_stays_in_a_vanishing_place()
    {
        foreach (var (loc, keepOn) in new[] { (LocationClass.Downloads, true), (LocationClass.Temp, false), (LocationClass.Removable, false), (LocationClass.Network, false), (LocationClass.Other, true) })
        {
            var m = Model(Ctx() with { Location = loc });
            m.Choice = LocationChoice.KeepHere;
            while (m.Page != SetupPage.Startup) m.Next();
            Check.Equal(keepOn, m.StartMenu, loc + " kept in place");
        }
        var installed = Model(Ctx() with { Mode = FirstRunMode.OptionsOnly, Location = LocationClass.ProgramsFolder });
        Check.True(installed.StartMenu, "installed copy: on");
        var touched = Model(Ctx() with { Location = LocationClass.Temp });
        touched.StartMenu = true;
        touched.Choice = LocationChoice.KeepHere;
        while (touched.Page != SetupPage.Startup) touched.Next();
        Check.True(touched.StartMenu, "the user's own choice stays");
    }

    [UnitTest]
    public static void Custom_quality_is_offered_only_when_something_custom_is_saved()
    {
        var plain = Model();
        Check.False(plain.OffersCustomQuality);
        plain.Quality = QualityPreset.Custom;
        Check.Equal(QualityPreset.Balanced, plain.Quality, "cannot pick Custom on a store that has none");
        var custom = Model(Ctx() with { Quality = QualityPreset.Custom, CustomQualityText = "1080p, 30 fps, 12 Mbps." });
        Check.True(custom.OffersCustomQuality);
        Check.Equal(QualityPreset.Custom, custom.Quality);
        custom.Quality = QualityPreset.Light;
        Check.Equal(QualityPreset.Light, custom.Quality);
        custom.Quality = QualityPreset.Custom;
        Check.Equal(QualityPreset.Custom, custom.Quality);
    }

    [UnitTest]
    public static void A_saved_replay_length_that_is_no_choice_gets_its_own_Keep_option()
    {
        Check.Equal(new[] { 30, 45, 60, 120, 300 }, Model().ReplayOptions.ToArray());
        var odd = Model(Ctx() with { ReplaySeconds = 80 });
        Check.Equal(new[] { 30, 45, 60, 120, 300, 80 }, odd.ReplayOptions.ToArray());
        odd.ReplaySeconds = 30;
        odd.ReplaySeconds = 80;
        Check.Equal(80, odd.ReplaySeconds, "the saved value can be picked again");
        odd.ReplaySeconds = 77;
        Check.Equal(80, odd.ReplaySeconds, "a value nobody offers is refused");
    }

    [UnitTest]
    public static void The_hotkeys_are_clones_and_come_back_with_the_result()
    {
        var original = Clip(0x78);
        var ctx = Ctx() with { Hotkeys = new[] { original } };
        var m = Model(ctx);
        Check.False(ReferenceEquals(original, m.HotkeysCopy[0]), "a clone");
        Check.False(ReferenceEquals(original.Keys, m.HotkeysCopy[0].Keys), "keys cloned too");
        m.HotkeysCopy[0].Keys.Vk = 0x79;
        Check.Equal(0x78, original.Keys.Vk, "the settings' hotkey is untouched");
        m.Choice = LocationChoice.KeepHere;
        while (m.Page != SetupPage.Done) m.Next();
        var r = ((SetupStep.Finish)m.Next()).Result;
        Check.Equal(0x79, r.Hotkeys![0].Keys.Vk);
        Check.False(ReferenceEquals(r.Hotkeys[0], m.HotkeysCopy[0]), "the result holds its own copies");
    }

    [UnitTest]
    public static void The_done_page_names_the_main_hotkey_or_asks_for_one()
    {
        var m = Model();
        Check.Equal("F9", m.MainHotkeyText);
        Check.Contains(SetupWizardTexts.DoneBody(m.MainHotkeyText), "Press F9 to save a clip.");
        var none = Model(Ctx() with { Hotkeys = new[] { Clip(0) } });
        Check.Null(none.MainHotkeyText);
        Check.Equal("Set a hotkey in Settings to save clips.", SetupWizardTexts.DoneBody(none.MainHotkeyText));
    }

    [UnitTest]
    public static void The_done_page_with_clips_off_only_says_where_Clink_runs()
    {
        Check.Equal("Clink keeps running in the tray, next to the clock.", SetupWizardTexts.DoneBody("F9", clipsEnabled: false));
        Check.Equal(SetupWizardTexts.DoneBody(null, clipsEnabled: false), SetupWizardTexts.DoneBody("F9", clipsEnabled: false), "no hotkey sentence either way");
    }

    [UnitTest]
    public static void Access_key_texts_are_the_plain_labels_with_one_underscore()
    {
        Check.Equal(SetupWizardTexts.RunAtStartupLabel, SetupWizardTexts.RunAtStartupAccess.Replace("_", ""));
        Check.Equal(SetupWizardTexts.StartMenuLabel, SetupWizardTexts.StartMenuAccess.Replace("_", ""));
        Check.Equal(SetupWizardTexts.DesktopLabel, SetupWizardTexts.DesktopAccess.Replace("_", ""));
        foreach (var a in new[] { SetupWizardTexts.RunAtStartupAccess, SetupWizardTexts.StartMenuAccess, SetupWizardTexts.DesktopAccess })
            Check.Equal(1, a.Count(ch => ch == (char)95), a);
    }

    // ------------------------------------------------------------------ texts

    [UnitTest]
    public static void Texts_stay_within_the_length_limits_with_worst_case_placeholders()
    {
        const string version = "10.10.10-dev.99", hotkey = "Ctrl+Alt+Shift+Win+PrintScreen";
        void Max(string what, string text, int limit) => Check.True(text.Length <= limit, $"{what} is {text.Length} long (limit {limit}): {text}");
        Max("title", SetupWizardTexts.Title, SetupWizardTexts.MaxHeading);
        foreach (var h in new[] { SetupWizardTexts.LocationHeading, SetupWizardTexts.QualityHeading, SetupWizardTexts.ReplayHeading, SetupWizardTexts.HotkeyHeading, SetupWizardTexts.StartupHeading, SetupWizardTexts.DoneHeading })
            Max("heading", h, SetupWizardTexts.MaxHeading);
        foreach (var b in new[] { SetupWizardTexts.Back, SetupWizardTexts.Next, SetupWizardTexts.Install, SetupWizardTexts.Skip, SetupWizardTexts.Finish, SetupWizardTexts.TryAgain, SetupWizardTexts.OpenClink })
            Max("button", b, SetupWizardTexts.MaxButton);
        foreach (var l in new[]
        {
            SetupWizardTexts.InstallForMeLabel, SetupWizardTexts.UseInstalledLabel, SetupWizardTexts.DocumentsLabel, SetupWizardTexts.ProgramFilesLabel, SetupWizardTexts.KeepLabel, SetupWizardTexts.KeepInDocumentsLabel,
            SetupWizardTexts.LightLabel, SetupWizardTexts.BalancedLabel, SetupWizardTexts.HighLabel, SetupWizardTexts.CustomLabel, SetupWizardTexts.ReplayKeep(300), SetupWizardTexts.ReplayKeep(5400),
            SetupWizardTexts.RunAtStartupLabel, SetupWizardTexts.StartMenuLabel, SetupWizardTexts.DesktopLabel,
        }) Max("label", l, SetupWizardTexts.MaxLabel);
        foreach (int s in SetupPresets.ReplayChoices) Max("replay label", SetupPresets.ReplayLabel(s), SetupWizardTexts.MaxLabel);
        foreach (var h in new[]
        {
            SetupWizardTexts.InstallForMeHint, SetupWizardTexts.UseInstalledHint(version), SetupWizardTexts.UseInstalledHintNoVersion, SetupWizardTexts.DocumentsHint, SetupWizardTexts.ProgramFilesHint, SetupWizardTexts.KeepHint,
            SetupWizardTexts.RecycleHint, SetupWizardTexts.CannotInstallSelf, SetupWizardTexts.ProgramFilesUnavailable,
            SetupWizardTexts.WarnTemp, SetupWizardTexts.WarnRemovable, SetupWizardTexts.WarnNetwork, SetupWizardTexts.WarnCloud, SetupWizardTexts.WarnDownloads, SetupWizardTexts.WarnDuplicate,
            SetupWizardTexts.BusyInstalling, SetupWizardTexts.BusyStartingInstalled, SetupWizardTexts.InstalledByInstaller, SetupWizardTexts.DoneTray,
            SetupWizardTexts.LightHint, SetupWizardTexts.BalancedHint, SetupWizardTexts.HighHint, SetupWizardTexts.CustomHint("native", 144, 100), SetupWizardTexts.ReplayHint,
            SetupWizardTexts.HotkeyBody(300), SetupWizardTexts.HotkeyFooter, SetupWizardTexts.ElevatedShortcutsNote,
        }) Max("hint", h, SetupWizardTexts.MaxHint);
        foreach (var loc in Enum.GetValues<LocationClass>())
            if (SetupWizardTexts.WarningFor(loc, true) is { } w) Max("warning", w, SetupWizardTexts.MaxHint);
        Max("place line", SetupWizardTexts.RunningFrom(SetupWizardTexts.Place(LocationClass.Cloud, null)), SetupWizardTexts.MaxHint);
        Max("done body", SetupWizardTexts.DoneBody(hotkey), SetupWizardTexts.MaxBody);
        Max("done body (clips off)", SetupWizardTexts.DoneBody(hotkey, false), SetupWizardTexts.MaxHint);
        Max("done body (no hotkey)", SetupWizardTexts.DoneBody(null), SetupWizardTexts.MaxHint);
        Max("step", SetupWizardTexts.StepOf(10, 10), SetupWizardTexts.MaxLabel);
    }

    [UnitTest]
    public static void Place_text_names_the_folder_or_trims_the_path()
    {
        Check.Equal("your Downloads folder", SetupWizardTexts.Place(LocationClass.Downloads, @"C:\x"));
        Check.Equal("your Desktop", SetupWizardTexts.Place(LocationClass.Desktop, @"C:\x"));
        Check.Equal("your Documents folder", SetupWizardTexts.Place(LocationClass.Documents, @"C:\x"));
        Check.Equal("a temporary folder", SetupWizardTexts.Place(LocationClass.Temp, @"C:\x"));
        Check.Equal("a removable drive", SetupWizardTexts.Place(LocationClass.Removable, @"C:\x"));
        Check.Equal("a network folder", SetupWizardTexts.Place(LocationClass.Network, @"C:\x"));
        Check.Equal("a cloud-synced folder", SetupWizardTexts.Place(LocationClass.Cloud, @"C:\x"));
        Check.Equal(@"D:\Games\Clink", SetupWizardTexts.Place(LocationClass.Other, @"D:\Games\Clink"));
        string longPath = @"D:\" + new string('a', 40) + @"\" + new string('b', 40) + @"\Clink";
        string trimmed = SetupWizardTexts.Place(LocationClass.Other, longPath);
        Check.True(trimmed.Length <= 48 && trimmed.Contains('\u2026') && trimmed.StartsWith(@"D:\"), "a long path is cut in the middle");
        Check.Equal("It is running from your Desktop.", SetupWizardTexts.RunningFrom("your Desktop"));
    }

    // ------------------------------------------------------------------ SetupApply

    private static Settings Fresh() => new();

    [UnitTest]
    public static void Apply_writes_the_answers()
    {
        var s = Fresh();
        var r = new SetupResult(false, QualityPreset.High, 120, new[] { Clip(0x79) }, true, true, false);
        SetupApply.Apply(s, r, freshStore: true);
        Check.Equal("1440p", s.Resolution);
        Check.Equal(60, s.Fps);
        Check.Equal(40, s.BitrateMbps);
        Check.Equal(120, s.BufferSeconds);
        Check.True(s.RunAtStartup);
        Check.Equal(0x79, s.Hotkeys[0].Keys.Vk);
        Check.False(ReferenceEquals(r.Hotkeys![0], s.Hotkeys[0]), "the settings own their hotkeys");
        Check.Equal(0x79, s.Hotkey.Vk, "the v1 hotkey mirrors the main one");
        Check.False(ReferenceEquals(s.Hotkey, s.Hotkeys[0].Keys), "a copy, not the same object");
        var unset = Fresh();
        int v1 = unset.Hotkey.Vk;
        SetupApply.Apply(unset, new SetupResult(false, QualityPreset.Balanced, 45, new[] { Clip(0) }, false, false, false), freshStore: true);
        Check.Equal(v1, unset.Hotkey.Vk, "an unset main hotkey leaves the v1 one alone");
    }

    [UnitTest]
    public static void Apply_stamps_the_setup_version_once_Settings_has_the_field()
    {
        // Slice 3a adds Settings.SetupVersion. When it exists, SetupApply.StampVersion must write it (see the TODO there).
        var prop = typeof(Settings).GetProperty("SetupVersion");
        if (prop == null) return;
        var s = Fresh();
        SetupApply.Apply(s, new SetupResult(false, QualityPreset.Balanced, 45, null, false, false, false), freshStore: true);
        Check.Equal(SetupApply.CurrentSetupVersion, Convert.ToInt32(prop.GetValue(s)), "Settings.SetupVersion exists now: finish the TODO in SetupApply.StampVersion (s.SetupVersion = CurrentSetupVersion)");
        var skipped = Fresh();
        SetupApply.Apply(skipped, new SetupResult(true, QualityPreset.Balanced, 45, null, false, false, false), freshStore: false);
        Check.Equal(SetupApply.CurrentSetupVersion, Convert.ToInt32(prop.GetValue(skipped)), "a skipped run is stamped too (TODO in SetupApply.StampVersion)");
    }

    [UnitTest]
    public static void Apply_leaves_custom_quality_and_custom_replay_alone()
    {
        var s = Fresh();
        s.Resolution = "native"; s.Fps = 144; s.BitrateMbps = 100; s.BufferSeconds = 80;
        SetupApply.Apply(s, new SetupResult(false, QualityPreset.Custom, 80, null, false, false, false), freshStore: false);
        Check.Equal("native", s.Resolution);
        Check.Equal(144, s.Fps);
        Check.Equal(100, s.BitrateMbps);
        Check.Equal(80, s.BufferSeconds);
    }

    [UnitTest]
    public static void Apply_without_hotkeys_keeps_the_settings_hotkeys()
    {
        var s = Fresh();
        var before = s.Hotkeys;
        SetupApply.Apply(s, new SetupResult(false, QualityPreset.Balanced, 45, null, false, false, false), freshStore: true);
        Check.True(ReferenceEquals(before, s.Hotkeys));
    }

    [UnitTest]
    public static void Skip_on_a_fresh_store_writes_the_defaults_and_on_a_rerun_changes_nothing()
    {
        var fresh = Fresh();
        fresh.Resolution = "720p"; fresh.Fps = 30; fresh.BitrateMbps = 8; fresh.BufferSeconds = 120; fresh.RunAtStartup = true;
        SetupApply.Apply(fresh, new SetupResult(true, QualityPreset.Light, 120, null, true, false, false), freshStore: true);
        Check.Equal(QualityPreset.Balanced, SetupPresets.PresetOf(fresh), "fresh store: defaults");
        Check.Equal(45, fresh.BufferSeconds);
        Check.True(fresh.RunAtStartup, "Skip never touches run at startup");

        var rerun = Fresh();
        rerun.Resolution = "720p"; rerun.Fps = 30; rerun.BitrateMbps = 8; rerun.BufferSeconds = 120;
        SetupApply.Apply(rerun, new SetupResult(true, QualityPreset.Light, 120, null, false, false, false), freshStore: false);
        Check.Equal(QualityPreset.Light, SetupPresets.PresetOf(rerun), "re-run: nothing changes");
        Check.Equal(120, rerun.BufferSeconds);
        Check.Equal(1, SetupApply.CurrentSetupVersion);
    }
}
