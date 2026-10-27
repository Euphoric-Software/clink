using System.Windows;
using Clink.Services;

namespace Clink.Tests;

/// <summary><c>--dev-quiet</c>: the switches, the off-screen placement and the gating that keeps a normal or a plain <c>--dev</c> run unchanged.</summary>
internal static class QuietModeTests
{
    private static DevOptions Dev(TempDir dir, params string[] more) => DevOptions.Parse(new[] { "--dev", dir.Path }.Concat(more).ToArray())!;

    // ------------------------------------------------------------------ the switches

    [UnitTest]
    public static void Dev_quiet_without_dev_is_an_error_and_on_with_it()
    {
        using var dir = new TempDir();
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev-quiet" }), "--dev-quiet alone is an error, never a normal start");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev-quiet-record" }), "so is --dev-quiet-record alone");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev-quiet", "--dev-quiet-record" }));
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--DEV-QUIET", "--show" }), "case does not matter");
        var plain = Dev(dir);
        Check.False(plain.Quiet, "plain --dev is not quiet");
        Check.False(plain.QuietRecord);
        var quiet = Dev(dir, "--dev-quiet");
        Check.True(quiet.Quiet, "--dev --dev-quiet");
        Check.False(quiet.QuietRecord, "quiet does not record unless asked");
        Check.True(DevOptions.Parse(new[] { "--dev-quiet", "--dev", dir.Path })!.Quiet, "the order does not matter");
        var rec = Dev(dir, "--dev-quiet", "--dev-quiet-record");
        Check.True(rec.Quiet && rec.QuietRecord, "--dev-quiet-record");
        // a look-alike is still an error
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-quiett" }));
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "-dev-quiet" }));
    }

    [UnitTest]
    public static void A_restart_keeps_the_quiet_switches_and_the_command_line_helper_drops_them()
    {
        var list = DevOptions.Switches(@"C:\d", false, false, false, null, quiet: true, quietRecord: true);
        Check.Equal(new[] { "--dev", @"C:\d", "--dev-quiet", "--dev-quiet-record" }, list.ToArray());
        Check.Equal(new[] { "--dev", @"C:\d" }, DevOptions.Switches(@"C:\d", false, false, false, null).ToArray(), "nothing quiet unless asked");
        Check.Equal(new[] { "--show" }, DevOptions.WithoutSwitches(new[] { "--dev", @"C:\d", "--dev-quiet", "--show", "--dev-quiet-record" }).ToArray());
    }

    // ------------------------------------------------------------------ the gating

    [UnitTest]
    public static void Quiet_is_false_for_a_normal_run_and_for_a_plain_dev_run()
    {
        Check.False(QuietMode.IsQuiet(isDev: false, devQuiet: false), "normal run");
        Check.False(QuietMode.IsQuiet(isDev: false, devQuiet: true), "--dev-quiet without --dev");
        Check.False(QuietMode.IsQuiet(isDev: true, devQuiet: false), "plain --dev");
        Check.True(QuietMode.IsQuiet(isDev: true, devQuiet: true), "--dev --dev-quiet");

        Check.False(AppController.IsQuiet, "a test process is not quiet");
        Check.False(QuietMode.Active);
        AppController.DevQuiet = true;
        Check.False(AppController.IsQuiet, "the flag alone, with no --dev, does nothing");
        AppController.InstanceSuffix = ".dev.test";
        Check.True(AppController.IsQuiet, "--dev and --dev-quiet");
        AppController.DevQuiet = false;
        Check.False(AppController.IsQuiet, "plain --dev");
        // the state is put back by the runner after the test
    }

    [UnitTest]
    public static void A_quiet_instance_starts_paused_unless_it_is_asked_to_record_quietly()
    {
        Check.False(QuietMode.StartsPaused(false, false, false, false), "a normal run records");
        Check.False(QuietMode.StartsPaused(false, true, true, true), "a normal run records whatever else is set");
        Check.True(QuietMode.StartsPaused(true, false, false, false), "plain --dev is paused");
        Check.False(QuietMode.StartsPaused(true, true, false, false), "--dev-record records");
        Check.True(QuietMode.StartsPaused(true, false, true, false), "--dev-quiet is paused");
        Check.True(QuietMode.StartsPaused(true, true, true, false), "--dev-quiet is paused even with --dev-record: no capture border on the user's screen");
        Check.False(QuietMode.StartsPaused(true, false, true, true), "--dev-quiet-record records");
        Check.True(QuietMode.StartsPaused(true, false, false, true), "--dev-quiet-record without --dev-quiet changes nothing: still paused");

        AppController.InstanceSuffix = ".dev.test";
        AppController.DevQuiet = true;
        Check.True(AppController.StartsPaused);
        AppController.DevQuietRecord = true;
        Check.False(AppController.StartsPaused);
    }

    // ------------------------------------------------------------------ the placement

    [UnitTest]
    public static void A_quiet_window_is_placed_right_of_every_monitor_at_its_size()
    {
        var size = new Size(1100, 800);
        // one monitor
        var one = new Rect(0, 0, 1920, 1080);
        var at = QuietPlacement.OffScreen(one, size);
        Check.Equal(1920 + QuietPlacement.Margin, at.X);
        Check.Equal(0.0, at.Y);
        Check.True(QuietPlacement.IsOffScreen(one, new Rect(at, size)));
        // two monitors, the second one to the left of the primary (negative coordinates)
        var left = new Rect(-2560, -200, 2560 + 1920, 1440);
        at = QuietPlacement.OffScreen(left, size);
        Check.Equal(1920 + QuietPlacement.Margin, at.X);
        Check.Equal(-200.0, at.Y);
        Check.True(QuietPlacement.IsOffScreen(left, new Rect(at, size)));
        // all of the virtual screen at negative coordinates
        var negative = new Rect(-5000, -3000, 3000, 2000);
        at = QuietPlacement.OffScreen(negative, size);
        Check.Equal(-2000 + QuietPlacement.Margin, at.X);
        Check.Equal(-3000.0, at.Y);
        Check.True(QuietPlacement.IsOffScreen(negative, new Rect(at, size)));
        // a tall monitor above and one below: the box is what counts, not any one monitor
        var stacked = new Rect(0, -1080, 1920, 2160);
        at = QuietPlacement.OffScreen(stacked, size);
        Check.True(QuietPlacement.IsOffScreen(stacked, new Rect(at, size)));
        // a custom margin, and a margin that cannot be negative
        Check.Equal(1920 + 50.0, QuietPlacement.OffScreen(one, size, 50).X);
        Check.Equal(1920.0, QuietPlacement.OffScreen(one, size, -400).X, "a negative margin would put the window back on screen");
    }

    [UnitTest]
    public static void Off_screen_means_touching_no_part_of_the_virtual_screen()
    {
        var screen = new Rect(-1920, 0, 3840, 1080);
        Check.False(QuietPlacement.IsOffScreen(screen, new Rect(100, 100, 50, 50)), "inside");
        Check.False(QuietPlacement.IsOffScreen(screen, new Rect(1900, 1000, 500, 500)), "sticking out at the corner");
        Check.False(QuietPlacement.IsOffScreen(screen, new Rect(-2000, 10, 200, 200)), "sticking out on the left");
        Check.True(QuietPlacement.IsOffScreen(screen, new Rect(1920 + 1, 0, 500, 500)), "just beyond the right edge");
        Check.True(QuietPlacement.IsOffScreen(screen, new Rect(-4000, 0, 500, 500)), "beyond the left edge");
    }

    [UnitTest]
    public static void A_concealed_window_keeps_one_pixel_column_over_the_right_most_monitor()
    {
        var size = new Size(470, 780);
        // one monitor
        var one = new Rect(0, 0, 1920, 1080);
        var at = QuietPlacement.Sliver(new[] { one }, size)!.Value;
        Check.Equal(1919.0, at.X);
        Check.Equal(0.0, at.Y);
        Check.Equal(1.0, Rect.Intersect(new Rect(at, size), one).Width, "exactly one pixel column on the monitor");
        // three monitors, two of them at negative coordinates, the right-most not the first in the list and not the biggest
        var monitors = new[] { new Rect(-1920, -1080, 1920, 1080), new Rect(0, 0, 2560, 1440), new Rect(2560, -200, 1080, 1920) };
        at = QuietPlacement.Sliver(monitors, size)!.Value;
        Check.Equal(3639.0, at.X);
        Check.Equal(-200.0, at.Y, "at the top of that monitor, so the pixel is on it and not in the gap beside a shorter neighbour");
        Check.Equal(1.0, Rect.Intersect(new Rect(at, size), monitors[2]).Width);
        Check.True(Rect.Intersect(new Rect(at, size), monitors[1]).IsEmpty, "and on no other monitor");
        // every monitor at negative coordinates
        at = QuietPlacement.Sliver(new[] { new Rect(-3000, -500, 1000, 800), new Rect(-2000, -300, 1500, 900) }, size)!.Value;
        Check.Equal(-501.0, at.X);
        Check.Equal(-300.0, at.Y);
        // nothing to be over
        Check.True(QuietPlacement.Sliver(Array.Empty<Rect>(), size) == null, "no monitor");
        Check.True(QuietPlacement.Sliver(new[] { new Rect(0, 0, 0, 0) }, size) == null, "an empty rectangle is no monitor");
    }

    // ------------------------------------------------------------------ which windows are the application's

    [UnitTest]
    public static void A_window_is_the_applications_by_its_class_alone_whatever_its_style()
    {
        const string wpf = "HwndWrapper[Clink;;e26b1e74-312e-4876-8b99-6c0ace406670]";
        // the main window (WindowStyle=None, transparent): neither WS_POPUP nor WS_CAPTION, as logged by a quiet instance
        Check.Equal(QuietWindowKind.Ours, QuietWindows.Classify(false, 0x020F0000, 0, wpf, "Clink TEST (dev1)"), "the main window is marked although it has no popup or caption style");
        Check.Equal(QuietWindowKind.Ours, QuietWindows.Classify(false, 0x00000000, 0, wpf, null), "a style of 0 does not matter either");
        Check.Equal(QuietWindowKind.Ours, QuietWindows.Classify(false, 0x80000000, 0, wpf, ""), "a WPF popup (ComboBox dropdown, tooltip)");
        Check.Equal(QuietWindowKind.Ours, QuietWindows.Classify(false, 0x00CF0000, 0, wpf, "Hidden Window"), "WPF's hidden owner window");
        Check.Equal(QuietWindowKind.Ours, QuietWindows.Classify(false, 0x94C80000, QuietWindows.DialogAtom, null, "Clink"), "a message box (the dialog class as an atom)");
        Check.Equal(QuietWindowKind.Ours, QuietWindows.Classify(false, 0x00000000, 0, "#32770", "Select a folder"), "a dialog by name");
        // left alone
        Check.Equal(QuietWindowKind.NotTopLevel, QuietWindows.Classify(false, 0x40000000 | 0x10000000, 0, wpf, null), "a child window");
        Check.Equal(QuietWindowKind.NotTopLevel, QuietWindows.Classify(true, 0x00000000, 0, wpf, null), "a message-only window");
        Check.Equal(QuietWindowKind.Overlay, QuietWindows.Classify(false, 0x80000000, 0, wpf, QuietMode.OverlayTitle), "the capture overlay is exempt");
        Check.Equal(QuietWindowKind.Foreign, QuietWindows.Classify(false, 0x00000000, 0, "WindowsForms10.Window.0.app.0.5c39d4_r3_ad1", ""), "the tray icon's WinForms window");
        Check.Equal(QuietWindowKind.Foreign, QuietWindows.Classify(false, 0x88000000, 0, "CicMarshalWndClass", "CicMarshalWnd"), "a text-services window");
        Check.Equal(QuietWindowKind.Foreign, QuietWindows.Classify(false, 0x80000000, 0x8000, null, null), "a native menu (#32768, an atom)");
        Check.Equal(QuietWindowKind.Foreign, QuietWindows.Classify(false, 0x80000000, 0, "hwndwrapper[x]", null), "the class prefix is case-sensitive, as Windows spells it");
        Check.Equal(QuietWindowKind.Foreign, QuietWindows.Classify(false, 0x80000000, 0, null, null), "no class at all");
    }

    // ------------------------------------------------------------------ the foreground safety net

    private const nint Other = 0x1111, Other2 = 0x2222, Main = 0x3333, Overlay = 0x4444, Tray = 0x5555;

    [UnitTest]
    public static void The_foreground_goes_back_to_the_last_window_of_another_process()
    {
        var g = new QuietForegroundGuard();
        Func<nint, bool> alive = _ => true;
        Check.Equal(QuietForegroundAction.None, g.OnForeground(0, false, false, false, alive, 0).Action, "no window");
        Check.Equal(QuietForegroundAction.Remembered, g.OnForeground(Other, false, false, false, alive, 0).Action);
        Check.Equal(Other, g.LastOther);
        Check.Equal(QuietForegroundAction.Remembered, g.OnForeground(Other2, false, false, false, alive, 10).Action);
        Check.Equal(Other2, g.LastOther, "the latest one is remembered");
        var (action, target) = g.OnForeground(Main, true, false, true, alive, 20);
        Check.Equal(QuietForegroundAction.Restore, action, "a hidden window of ours took the foreground");
        Check.Equal(Other2, target);
        Check.Equal(Other2, g.LastOther, "a window of ours is never remembered");
        // the overlay is meant to have the foreground; a visible window of ours (the tray menu) too
        Check.Equal(QuietForegroundAction.Exempt, g.OnForeground(Overlay, true, true, false, alive, 30).Action, "the capture overlay");
        Check.Equal(QuietForegroundAction.None, g.OnForeground(Tray, true, false, false, alive, 40).Action, "a visible window of ours");
        Check.Equal(Other2, g.LastOther);
    }

    [UnitTest]
    public static void The_foreground_is_not_given_to_a_window_that_has_gone_and_never_fought_over()
    {
        var g = new QuietForegroundGuard();
        Check.Equal(QuietForegroundAction.NothingToRestore, g.OnForeground(Main, true, false, true, _ => true, 0).Action, "nothing remembered yet");
        g.OnForeground(Other, false, false, false, _ => true, 0);
        Check.Equal(QuietForegroundAction.NothingToRestore, g.OnForeground(Main, true, false, true, h => h != Other, 10).Action, "the remembered window has gone (or its handle is ours now)");
        Check.Equal((nint)0, g.LastOther, "a dead handle is forgotten");
        Check.Equal(QuietForegroundAction.NothingToRestore, g.OnForeground(Main, true, false, true, _ => true, 20).Action, "and not used later");

        // a loop: at most MaxRestores in WindowMs, then skipped until the window has passed
        g.OnForeground(Other, false, false, false, _ => true, 1000);
        for (int i = 0; i < QuietForegroundGuard.MaxRestores; i++)
            Check.Equal(QuietForegroundAction.Restore, g.OnForeground(Main, true, false, true, _ => true, 1000 + i).Action, "restore " + i);
        Check.Equal(QuietForegroundAction.Throttled, g.OnForeground(Main, true, false, true, _ => true, 1100).Action, "one too many in a second");
        Check.Equal(QuietForegroundAction.Restore, g.OnForeground(Main, true, false, true, _ => true, 1000 + QuietForegroundGuard.WindowMs).Action, "again once the second has passed");
        g.Reset();
        Check.Equal((nint)0, g.LastOther);
    }
}