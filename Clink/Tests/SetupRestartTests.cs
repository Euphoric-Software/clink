using System.Diagnostics;
using Clink.Services;

namespace Clink.Tests;

/// <summary>A restart never carries the first-run setup switches along, and "Run setup again" restarts with <c>--setup</c> for one start only.</summary>
internal static class SetupRestartTests
{
    private const string Moved = @"C:\Users\u\Downloads\Clink.exe";
    private static readonly string Hash = new string('A', 64);

    private static string[] Restart(params string[] args) => AppController.RestartArguments(args).ToArray();

    [UnitTest]
    public static void A_restart_drops_the_continuation_switches_and_the_move_record()
    {
        Check.Equal(new[] { "--show" }, Restart("--setup-continue", "--setup-moved-from", Moved, Hash, "--minimized"));
        Check.Equal(new[] { "--show" }, Restart("--after-pid", "42", "--setup-continue", "--setup-moved-from", Moved, Hash));
        Check.Equal(new[] { "--show" }, Restart("--SETUP-CONTINUE", "--Setup-Moved-From", Moved, Hash));
        Check.Equal(new[] { "--foo", "--bar", "--show" }, Restart("--foo", "--setup-moved-from", Moved, Hash, "--bar"), "other switches stay");
        Check.Equal(new[] { "--show" }, Restart("--setup-moved-from", Moved, Hash, "--setup-continue"), "any order");
    }

    [UnitTest]
    public static void A_restart_drops_setup_and_takes_at_most_two_values_with_the_record()
    {
        Check.Equal(new[] { "--show" }, Restart("--setup"));
        Check.Equal(new[] { "--foo", "--show" }, Restart("--foo", "--SETUP"));
        Check.Equal(new[] { "--show" }, Restart("--setup", "--setup-continue"));
        Check.Equal(new[] { "stray", "--show" }, Restart("--setup-moved-from", Moved, Hash, "stray"), "a third value is not part of the record");
        Check.Equal(new[] { "--foo", "--show" }, Restart("--setup-moved-from", Moved, "--foo"), "a record cut short by a switch");
        Check.Equal(new[] { "--foo", "--show" }, Restart("--setup-moved-from", "--foo"));
        Check.Equal(new[] { "--show" }, Restart("--setup-moved-from", Moved));
        Check.Equal(new[] { "--show" }, Restart("--setup-moved-from"));
        Check.Equal(new[] { "--setup-continues", "--show" }, Restart("--setup-continues"), "whole switches only: what it does not know it passes on");
    }

    [UnitTest]
    public static void A_restarted_command_line_is_no_continuation_and_asks_for_no_setup()
    {
        var args = new[] { "--after-pid", "7", "--setup-continue", "--setup-moved-from", Moved, Hash, "--setup", "--minimized" };
        Check.True(SetupArgs.IsContinuation(args));
        var again = AppController.RestartArguments(args);
        Check.False(SetupArgs.IsContinuation(again));
        Check.False(SetupArgs.Parse(again).Requested);
        Check.False(SetupArgs.Parse(again).Continue);
    }

    [UnitTest]
    public static void A_dev_restart_keeps_its_dev_switches_and_loses_the_setup_ones()
    {
        AppController.InstanceSuffix = ".dev.test";
        Settings.DirOverride = @"C:\dev one";
        AppController.DevHotkeys = true;
        AppController.DevToken = true;
        AppController.DevRecord = false;
        AppController.DevAcceptImport = false;
        SettingsImport.PackagesRoot = @"D:\fake";
        var expected = new[] { "--show", "--dev", @"C:\dev one", "--dev-hotkeys", "--dev-token", "--packages-root", @"D:\fake" };

        Check.Equal(expected, Restart("--dev", @"C:\dev one", "--dev-hotkeys", "--setup-continue", "--setup-moved-from", Moved, Hash, "--dev-token", "--packages-root", @"D:\fake"));
        Check.Equal(expected, Restart(@"--dev=C:\dev one", "--setup", "--minimized"));
        Check.Equal(expected, Restart("--setup-moved-from", Moved, Hash, @"--dev=C:\dev one", "--setup-continue"));
        var again = DevOptions.Parse(Restart("--dev", @"C:\dev one", "--setup", "--setup-continue", "--setup-moved-from", Moved, Hash))!;
        Check.Equal(@"C:\dev one", again.Folder);
    }

    // ------------------------------------------------------------------ Run setup again

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Home = new();
        public readonly AppController App;
        public readonly List<ProcessStartInfo> Started = new();
        public int Exits;
        public bool FailStart;

        public Rig(params string[] commandLine)
        {
            Settings.DirOverride = Home.Path;
            var s = Settings.FromJson("{}");
            App = new AppController(s, new Updater(s))
            {
                CommandLine = commandLine,
                ExitForRestart = () => Exits++,
                StartProcess = psi =>
                {
                    if (FailStart) throw new InvalidOperationException("no memory for another Clink");
                    Started.Add(psi);
                },
            };
        }

        public void Dispose()
        {
            App.Dispose();
            Home.Dispose();
        }
    }

    [UnitTest]
    public static void Run_setup_again_restarts_with_setup_for_that_one_start()
    {
        using var rig = new Rig("--minimized", "--setup-continue", "--setup-moved-from", Moved, Hash);
        Check.True(rig.App.RunSetupAgain());
        Check.Equal(1, rig.Exits);
        var args = rig.Started.Single().ArgumentList.ToArray();
        Check.Equal(new[] { "--show", "--setup", "--after-pid", Environment.ProcessId.ToString() }, args);

        // the restart after that one carries no --setup
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(args).ToArray());
    }

    [UnitTest]
    public static void Run_setup_again_that_cannot_restart_gives_nothing_up()
    {
        using var rig = new Rig("--show") { FailStart = true };
        Check.False(rig.App.RunSetupAgain());
        Check.Equal(0, rig.Exits);
        Check.Equal(0, rig.Started.Count);
    }

    [UnitTest]
    public static void A_restart_takes_extra_arguments_for_that_start_only()
    {
        using var rig = new Rig("--minimized");
        Check.True(rig.App.RestartClink(out string? error, new[] { "--setup" }));
        Check.Null(error);
        Check.Equal(new[] { "--show", "--setup", "--after-pid", Environment.ProcessId.ToString() }, rig.Started[0].ArgumentList.ToArray());
        Check.True(rig.App.RestartClink(out error));
        Check.Equal(new[] { "--show", "--after-pid", Environment.ProcessId.ToString() }, rig.Started[1].ArgumentList.ToArray());
    }
}
