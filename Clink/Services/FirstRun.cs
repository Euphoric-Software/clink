namespace Clink.Services;

/// <summary>What the first-run setup shows.</summary>
internal enum FirstRunMode
{
    /// <summary>Nothing: start as usual.</summary>
    None,
    /// <summary>The options pages only: no "where should Clink live" step (already moved, installed, or cannot be moved).</summary>
    OptionsOnly,
    /// <summary>The whole wizard, including the location step.</summary>
    Full,
}

/// <summary>What <see cref="FirstRun.Decide"/> looks at, all gathered by the caller (App startup).</summary>
/// <param name="Dev">Started with <c>--dev</c>.</param>
/// <param name="DevFirstRun">Started with <c>--dev-first-run</c>.</param>
/// <param name="SettingsLoadedFromFile">A settings.json existed and was read (<c>Settings.LoadedFromFile</c>).</param>
/// <param name="SettingsImported">Settings were taken over from another copy of Clink at this start.</param>
/// <param name="ForwardedCommand">The command line carries a command for the running Clink (clip, screenshot, ...).</param>
/// <param name="Minimized">Started with <c>--minimized</c> (the Run key does that).</param>
/// <param name="SetupContinue">Started with <c>--setup-continue</c>: the wizard moved the exe and this is the new copy.</param>
/// <param name="MovedFrom">The path after <c>--setup-moved-from</c> (null when absent).</param>
/// <param name="MovedFromSha256">The hash after it (null when absent).</param>
/// <param name="Elevated">The process is elevated.</param>
/// <param name="Location">Where this exe sits.</param>
/// <param name="SetupRequested">Started with <c>--setup</c> ("Run setup again" in the app, one start only): show the wizard whatever the settings say.</param>
internal sealed record FirstRunInputs(
    bool Dev = false,
    bool DevFirstRun = false,
    bool SettingsLoadedFromFile = false,
    bool SettingsImported = false,
    bool ForwardedCommand = false,
    bool Minimized = false,
    bool SetupContinue = false,
    bool Elevated = false,
    LocationClass Location = LocationClass.Other,
    string? MovedFrom = null,
    string? MovedFromSha256 = null,
    bool SetupRequested = false)
{
    /// <summary><c>--setup-continue</c> with a usable moved-from path and hash: the wizard really moved this exe.</summary>
    public bool IsContinuation => SetupContinue && SelfInstall.IsValidMoveRecord(MovedFrom, MovedFromSha256);
}

/// <summary>The decision table for the first-run setup. First matching row wins: a continuation after the move, a requested setup (--setup), the --dev rule, existing/imported settings, forwarded/minimized starts, elevated or installed, else the full wizard.</summary>
internal static class FirstRun
{
    public static FirstRunMode Decide(FirstRunInputs i)
    {
        if (i.IsContinuation) return FirstRunMode.OptionsOnly;                     // just moved by the wizard (also under --dev): the location question is answered
        if (i.SetupRequested) return i.Elevated || i.Location is LocationClass.ProgramsFolder or LocationClass.ProgramFiles ? FirstRunMode.OptionsOnly : FirstRunMode.Full;   // asked for: even with settings, even under --dev
        if (i.Dev) return i.DevFirstRun ? FirstRunMode.Full : FirstRunMode.None;   // a --dev instance never nags unless asked
        if (i.SettingsLoadedFromFile) return FirstRunMode.None;                    // not a first run
        if (i.SettingsImported) return FirstRunMode.None;                          // the user's settings came along
        if (i.ForwardedCommand || i.Minimized) return FirstRunMode.None;           // started by a script or the Run key, not by a person
        if (i.Elevated) return FirstRunMode.OptionsOnly;                           // never moves an exe or makes shortcuts from an elevated process
        if (i.Location is LocationClass.ProgramsFolder or LocationClass.ProgramFiles) return FirstRunMode.OptionsOnly;   // installed already (per-user installer / an earlier setup / the all-users installer)
        return FirstRunMode.Full;
    }
}

/// <summary>What the setup switches of a command line say. See <see cref="SetupArgs.Parse"/>.</summary>
/// <param name="Continue"><c>--setup-continue</c> was given.</param>
/// <param name="MovedFrom">The first value after <c>--setup-moved-from</c> (null when absent).</param>
/// <param name="MovedFromSha256">The second value after it (null when absent).</param>
/// <param name="Requested"><c>--setup</c> was given.</param>
internal readonly record struct ParsedSetupArgs(bool Continue, string? MovedFrom, string? MovedFromSha256, bool Requested)
{
    /// <summary><c>--setup-continue</c> with a usable moved-from record.</summary>
    public bool IsContinuation => Continue && SelfInstall.IsValidMoveRecord(MovedFrom, MovedFromSha256);
}

/// <summary>Reads <c>--setup</c>, <c>--setup-continue</c> and <c>--setup-moved-from &lt;path&gt; &lt;sha256&gt;</c> (switches match whole and without regard to case; the two values are the next arguments that do not start with <c>--</c>).</summary>
internal static class SetupArgs
{
    public const string Setup = "--setup";
    public const string Continue = "--setup-continue";
    public const string MovedFrom = "--setup-moved-from";

    public static ParsedSetupArgs Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        bool cont = false, requested = false;
        string? from = null, sha = null;
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (a.Equals(Setup, StringComparison.OrdinalIgnoreCase)) requested = true;
            else if (a.Equals(Continue, StringComparison.OrdinalIgnoreCase)) cont = true;
            else if (a.Equals(MovedFrom, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    from ??= list[i + 1];
                    i++;
                    if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) { sha ??= list[i + 1]; i++; }
                }
            }
        }
        return new ParsedSetupArgs(cont, from, sha, requested);
    }

    /// <summary>True when the command line is the one the wizard's new copy is started with (a continuation).</summary>
    public static bool IsContinuation(IEnumerable<string> args) => Parse(args).IsContinuation;
}
