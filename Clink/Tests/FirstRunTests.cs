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
        foreach (var loc in new[] { LocationClass.Downloads, LocationClass.Desktop, LocationClass.Documents, LocationClass.Other, LocationClass.Cloud, LocationClass.Temp, LocationClass.Network, LocationClass.Removable, LocationClass.ProgramFiles })
            Check.Equal(FirstRunMode.Full, D(new FirstRunInputs(Location: loc)), loc.ToString());
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
}
