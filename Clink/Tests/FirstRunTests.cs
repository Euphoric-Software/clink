using Clink.Services;

namespace Clink.Tests;

/// <summary>The first-run decision table, row by row and in order.</summary>
internal static class FirstRunTests
{
    private static FirstRunMode D(FirstRunInputs i) => FirstRun.Decide(i);

    private const string Moved = @"C:\Users\u\Downloads\Clink.exe";
    private static readonly string Hash = new string('A', 64);
    private static FirstRunInputs Cont(FirstRunInputs i) => i with { SetupContinue = true, MovedFrom = Moved, MovedFromSha256 = Hash };

    [UnitTest]
    public static void A_plain_first_start_is_the_full_wizard()
    {
        Check.Equal(FirstRunMode.Full, D(new FirstRunInputs()));
        foreach (var loc in new[] { LocationClass.Downloads, LocationClass.Desktop, LocationClass.Documents, LocationClass.Other, LocationClass.Cloud, LocationClass.Temp, LocationClass.Network, LocationClass.Removable })
            Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(Location: loc)), loc.ToString());
        Check.Equal(FirstRunMode.OptionsOnly, D(new FirstRunInputs(Location: LocationClass.ProgramFiles)), "installed for all users: no location step");
    }

    [UnitTest]
    public static void Dev_starts_only_show_the_wizard_when_asked()
    {
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(Dev: true)));
        Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(Dev: true, DevFirstRun: true)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(Dev: true, SetupContinue: true)), "a bare --setup-continue without a moved-from record is not a continuation");
        Check.Equal(FirstRunMode.OptionsOnly, D(Cont(new FirstRunInputs(Dev: true, DevFirstRun: true))), "a continuation beats the dev rule");
        Check.Equal(FirstRunMode.OptionsOnly, D(Cont(new FirstRunInputs(Dev: true))));
        Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(Dev: true, DevFirstRun: true, SettingsLoadedFromFile: true)), "--dev-first-run forces it");
        Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(Dev: false, DevFirstRun: true)), "the switch alone, without --dev, changes nothing");
    }

    [UnitTest]
    public static void Existing_or_imported_settings_mean_no_wizard()
    {
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(SettingsLoadedFromFile: true)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(SettingsImported: true)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(SettingsLoadedFromFile: true, Elevated: true, Location: LocationClass.ProgramsFolder)));
    }

    [UnitTest]
    public static void A_forwarded_command_or_minimized_start_is_never_a_first_run()
    {
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(ForwardedCommand: true)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(Minimized: true)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(Minimized: true, SetupContinue: true)), "no record: not a continuation");
        Check.Equal(FirstRunMode.OptionsOnly, D(Cont(new FirstRunInputs(Minimized: true))), "a continuation is row 1");
        Check.Equal(FirstRunMode.OptionsOnly, D(Cont(new FirstRunInputs(SettingsLoadedFromFile: true))));
    }

    [UnitTest]
    public static void Setup_continue_elevated_and_installed_show_the_options_only()
    {
        Check.Equal(FirstRunMode.OptionsOnly, D(Cont(new FirstRunInputs())));
        Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(SetupContinue: true)), "--setup-continue without its record is a plain first start");
        Check.Equal(FirstRunMode.OptionsOnly, D(new FirstRunInputs(Elevated: true)));
        Check.Equal(FirstRunMode.OptionsOnly, D(new FirstRunInputs(Location: LocationClass.ProgramsFolder)));
        Check.Equal(FirstRunMode.OptionsOnly, D(Cont(new FirstRunInputs(Elevated: true, Location: LocationClass.Downloads))));
    }

    [UnitTest]
    public static void A_continuation_needs_a_full_path_without_stream_colons_and_a_hex_hash()
    {
        foreach (string? bad in new string?[] { null, "", "Clink.exe", @"C:\x\Clink.exe:evil", @"C:\x:y\Clink.exe" })
            Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(SetupContinue: true, MovedFrom: bad, MovedFromSha256: Hash)), bad ?? "null");
        foreach (string? bad in new string?[] { null, "", "abc", new string('G', 64), new string('A', 63) })
            Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(SetupContinue: true, MovedFrom: Moved, MovedFromSha256: bad)), bad ?? "null");
        Check.Equal(FirstRunMode.OptionsOnly, D(new FirstRunInputs(SetupContinue: true, MovedFrom: Moved, MovedFromSha256: Hash.ToLowerInvariant())));
    }

    // ------------------------------------------------------------------ slice 3a: --setup, --setup-continue parsing, SetupVersion

    [UnitTest]
    public static void A_requested_setup_shows_the_wizard_whatever_the_settings_say()
    {
        var asked = new FirstRunInputs(SetupRequested: true);
        Check.Equal(FirstRunMode.Full, D(asked));
        Check.Equal(FirstRunMode.Full, D(asked with { SettingsLoadedFromFile = true }));
        Check.Equal(FirstRunMode.Full, D(asked with { SettingsImported = true, ForwardedCommand = true, Minimized = true }));
        Check.Equal(FirstRunMode.Full, D(asked with { Dev = true }), "also under --dev, without --dev-first-run");
        foreach (var loc in new[] { LocationClass.Downloads, LocationClass.Desktop, LocationClass.Documents, LocationClass.Other, LocationClass.Cloud, LocationClass.Temp, LocationClass.Network, LocationClass.Removable })
            Check.Equal(FirstRunMode.Full, D(asked with { Location = loc }), loc.ToString());
        Check.Equal(FirstRunMode.OptionsOnly, D(asked with { Elevated = true }));
        Check.Equal(FirstRunMode.OptionsOnly, D(asked with { Location = LocationClass.ProgramsFolder }));
        Check.Equal(FirstRunMode.OptionsOnly, D(asked with { Location = LocationClass.ProgramFiles }));
        Check.Equal(FirstRunMode.OptionsOnly, D(Cont(asked)), "a continuation is the row before");
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(SettingsLoadedFromFile: true)), "without --setup nothing changed");
    }

    [UnitTest]
    public static void Program_files_installs_are_options_only_unless_a_row_above_decides()
    {
        Check.Equal(FirstRunMode.OptionsOnly, D(new FirstRunInputs(Location: LocationClass.ProgramFiles)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(Location: LocationClass.ProgramFiles, SettingsLoadedFromFile: true)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(Location: LocationClass.ProgramFiles, Minimized: true)));
        Check.Equal(FirstRunMode.None, D(new FirstRunInputs(Location: LocationClass.ProgramFiles, Dev: true)));
        Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(Location: LocationClass.ProgramFiles, Dev: true, DevFirstRun: true)));
    }

    [UnitTest]
    public static void The_setup_switches_are_read_from_a_command_line()
    {
        var none = SetupArgs.Parse(new[] { "--minimized" });
        Check.False(none.Continue || none.Requested || none.IsContinuation);
        Check.Null(none.MovedFrom);

        var cont = SetupArgs.Parse(new[] { "--after-pid", "12", "--setup-continue", "--setup-moved-from", Moved, Hash, "--dev", @"C:\d" });
        Check.True(cont.Continue);
        Check.False(cont.Requested);
        Check.Equal(Moved, cont.MovedFrom);
        Check.Equal(Hash, cont.MovedFromSha256);
        Check.True(cont.IsContinuation);
        Check.True(SetupArgs.IsContinuation(new[] { "--SETUP-CONTINUE", "--Setup-Moved-From", Moved, Hash }), "case does not matter");

        var req = SetupArgs.Parse(new[] { "--show", "--setup" });
        Check.True(req.Requested);
        Check.False(req.Continue);
        Check.False(SetupArgs.Parse(new[] { "--setup-continue" }).IsContinuation, "no record");
        Check.False(SetupArgs.IsContinuation(new[] { "--setup-moved-from", Moved, Hash }), "a record without --setup-continue");

        // the values are the next arguments that are not switches
        var cut = SetupArgs.Parse(new[] { "--setup-continue", "--setup-moved-from", Moved, "--dev", @"C:\d" });
        Check.Equal(Moved, cut.MovedFrom);
        Check.Null(cut.MovedFromSha256);
        Check.False(cut.IsContinuation);
        Check.Null(SetupArgs.Parse(new[] { "--setup-moved-from" }).MovedFrom);
        Check.False(SetupArgs.Parse(new[] { "--setup-continues" }).Continue, "whole switches only");
        Check.False(SetupArgs.Parse(new[] { "--setup-continue" }).Requested, "--setup-continue is not --setup");
        Check.False(SetupArgs.Parse(Array.Empty<string>()).Requested);
    }

    [UnitTest]
    public static void SetupVersion_is_left_out_of_the_file_until_set_and_is_this_installations_own()
    {
        Check.Null(Settings.FromJson("{}").SetupVersion);
        Check.False(Settings.FromJson("{}").ToJson().Contains("SetupVersion"), "a file that never saw the setup is exactly what it was");
        var s = Settings.FromJson("{}");
        s.SetupVersion = 1;
        string json = s.ToJson();
        Check.Contains(json, "\"SetupVersion\"");
        Check.Equal(1, Settings.FromJson(json).SetupVersion);
        Check.Equal(2, Settings.FromJson("{\"SetupVersion\":2}").SetupVersion);
        Check.True(SettingsImport.IsOwnKey("SetupVersion"));
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.SetupVersion)));
        Check.True(SettingsImport.IsOwnKey("setupversion"));
        Check.False(SettingsImport.IsOwnKey("SetupVersions"), "only that key");
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.RunAtStartup)));
    }
}
