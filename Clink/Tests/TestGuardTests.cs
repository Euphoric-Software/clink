using System.IO;
using Clink.Screenshots;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// The guard that keeps a unit-test run away from the user's real settings folder (%APPDATA%\Clink): test mode is on, the settings
/// folders point at scratch, and a write under the true real folder is refused. None of these touch the real folder: the refused
/// write never reaches the disk, and the rest are path computations.
/// </summary>
internal static class TestGuardTests
{
    /// <summary>The start of every test here: test mode must be on (else the test would do real harm: bail out) and no refusal may be left over from before.</summary>
    private static void Precondition()
    {
        Check.True(TestGuard.Active, "--unittest turns test mode on; without it these tests must not run");
        Check.Equal(0, TestGuard.ViolationCount, "a refusal left over from before is not this test's to discard");
    }

    [UnitTest]
    public static void A_write_under_the_true_real_folder_is_refused_in_test_mode()
    {
        Precondition();
        string real = TestGuard.TrueRealDir;
        string target = Path.Combine(real, "test-guard-" + Guid.NewGuid().ToString("N") + ".json");
        // The guard first, on its own: only when it refuses is AtomicFile safe to call (it would write the real folder otherwise).
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(target));
        try
        {
            var ex = Check.Throws<InvalidOperationException>(() => AtomicFile.WriteAllText(target, "{}"));
            Check.Contains(ex.Message, "real settings folder");
            Check.Contains(ex.Message, nameof(A_write_under_the_true_real_folder_is_refused_in_test_mode), "names the running test");
            Check.False(File.Exists(target) || File.Exists(target + ".tmp"), "nothing was written");
        }
        finally
        {
            foreach (string leftover in new[] { target, target + ".tmp", target + ".bak" }) try { File.Delete(leftover); } catch { }
        }
        // the other write paths go through the same check
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(Path.Combine(real, "settings.pre-v3.json")));
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(Path.Combine(real, "sub", "x.bak")));
        var refused = TestGuard.TakeViolations();   // this test provoked exactly these and asserts them
        Check.Equal(4, refused.Length);
        Check.Contains(refused[0], "at Clink", "carries the stack trace");
    }

    [UnitTest]
    public static void A_write_under_the_true_local_folder_is_refused_in_test_mode()
    {
        Precondition();
        string local = TestGuard.TrueLocalDir;
        string target = Path.Combine(local, "updates", "test-guard-" + Guid.NewGuid().ToString("N") + ".json");
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(target));
        try
        {
            var ex = Check.Throws<InvalidOperationException>(() => AtomicFile.WriteAllText(target, "{}"));
            Check.Contains(ex.Message, "real local Clink folder");
        }
        finally
        {
            foreach (string leftover in new[] { target, target + ".tmp", target + ".bak" }) try { File.Delete(leftover); } catch { }
        }
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(local));
        Check.Equal(3, TestGuard.TakeViolations().Length);
        // the updater's own default folder is guarded too: with no override it would resolve into the true local folder
        string defaultUpdates = Path.Combine(local, "updates");
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(defaultUpdates));   // the guard refuses it, so the property below cannot create it
        string? saved = Updater.UpdatesDirOverride;
        try
        {
            Updater.UpdatesDirOverride = null;
            Check.Throws<InvalidOperationException>(() => { _ = Updater.UpdatesDir; });
        }
        finally { Updater.UpdatesDirOverride = saved; }
        Check.Equal(2, TestGuard.TakeViolations().Length);
        Check.False(TestGuard.IsUnderTrueRealDir(Updater.UpdatesDir, TestGuard.TrueLocalDir), "the runner's updates folder is scratch");
    }

    [UnitTest]
    public static void A_write_under_the_true_clips_folder_is_refused_in_test_mode()
    {
        Precondition();
        string clips = TestGuard.TrueClipsDir;
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(Path.Combine(clips, "Clip " + Guid.NewGuid().ToString("N") + ".mp4")));
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(Path.Combine(clips, "Screenshots", "a.png")));
        Check.Throws<InvalidOperationException>(() => TestGuard.Check(clips));
        Check.Equal(3, TestGuard.TakeViolations().Length);
        Check.False(TestGuard.IsUnderTrueRealDir(clips + "-other", clips), "a sibling that starts the same is not inside");
        // a fresh Settings keeps its clips (and so its screenshots) folder in scratch, never in Videos\Clink
        var s = new Settings();
        Check.False(TestGuard.IsUnderTrueRealDir(s.ClipsFolder, clips), "default clips folder: " + s.ClipsFolder);
        Check.False(TestGuard.IsUnderTrueRealDir(ScreenshotService.FolderFor(s), clips), "default screenshots folder: " + ScreenshotService.FolderFor(s));
        // the product's own writers refuse it, before anything is created
        Check.Throws<InvalidOperationException>(() => SaveCopy.CopyAsync(Path.Combine(clips, "none.mp4"), Path.Combine(clips, "copy.mp4"), null, CancellationToken.None).GetAwaiter().GetResult());
        Check.Equal(1, TestGuard.TakeViolations().Length);
    }

    [UnitTest]
    public static void A_launch_is_refused_and_recorded_in_test_mode()
    {
        Precondition();
        // Only a target that cannot do harm even if the guard were broken: a missing exe inside the run's own scratch folder, no shell.
        using var dir = new TempDir();
        string nope = Path.Combine(dir.Path, "nope.exe");
        var ex = Check.Throws<InvalidOperationException>(() => TestGuard.CheckLaunch("a thing"));
        Check.Contains(ex.Message, "a thing");
        Check.Throws<InvalidOperationException>(() => TestGuard.Start(new System.Diagnostics.ProcessStartInfo(nope) { UseShellExecute = false }));
        Check.Throws<InvalidOperationException>(() => TestGuard.Start(nope, "--x"));
        var refused = TestGuard.TakeViolations();   // this test provoked exactly these and asserts them
        Check.Equal(3, refused.Length);
        Check.Contains(refused[0], nameof(A_launch_is_refused_and_recorded_in_test_mode));
        Check.Contains(refused[1], "nope.exe");
    }

    [UnitTest]
    public static void A_window_that_gets_a_handle_in_test_mode_is_recorded_and_closed_unless_allowed()
    {
        Precondition();
        // A handle is made without showing anything (EnsureHandle creates an invisible HWND), which is all the sweep needs.
        var never = new System.Windows.Window { ShowActivated = false, ShowInTaskbar = false, Width = 10, Height = 10 };
        never.Measure(new System.Windows.Size(10, 10));
        never.Arrange(new System.Windows.Rect(0, 0, 10, 10));
        TestGuard.SweepWindows();
        Check.Equal(0, TestGuard.ViolationCount, "a window that was only measured and arranged is not shown");
        var shown = new System.Windows.Window { ShowActivated = false, ShowInTaskbar = false, Width = 10, Height = 10, Title = "guard-test-window" };
        var allowed = new System.Windows.Window { ShowActivated = false, ShowInTaskbar = false, Width = 10, Height = 10, Title = "guard-test-allowed" };
        try
        {
            new System.Windows.Interop.WindowInteropHelper(shown).EnsureHandle();
            new System.Windows.Interop.WindowInteropHelper(allowed).EnsureHandle();
            TestGuard.AllowedWindows.Add(allowed);
            TestGuard.SweepWindows();
            var refused = TestGuard.TakeViolations();   // this test provoked exactly this and asserts it
            Check.Equal(1, refused.Length);
            Check.Contains(refused[0], "guard-test-window");
            Check.Contains(refused[0], nameof(A_window_that_gets_a_handle_in_test_mode_is_recorded_and_closed_unless_allowed), "names the running test");
        }
        finally
        {
            TestGuard.AllowedWindows.Remove(allowed);
            try { shown.Close(); } catch { }
            try { allowed.Close(); } catch { }
            try { never.Close(); } catch { }
        }
    }

    [UnitTest]
    public static void A_window_that_cancels_its_close_is_recorded_once_and_hidden()
    {
        Precondition();
        var stubborn = new System.Windows.Window { ShowActivated = false, ShowInTaskbar = false, Width = 10, Height = 10, Title = "guard-test-stubborn" };
        bool cancel = true;
        stubborn.Closing += (_, e) => e.Cancel = cancel;
        try
        {
            new System.Windows.Interop.WindowInteropHelper(stubborn).EnsureHandle();
            TestGuard.SweepWindows();
            Check.Equal(1, TestGuard.TakeViolations().Length);   // this test provoked exactly this and asserts it
            Check.False(stubborn.IsVisible, "hidden after the cancelled close");
            TestGuard.SweepWindows();
            Check.Equal(0, TestGuard.ViolationCount, "a second sweep does not record it again");
        }
        finally
        {
            cancel = false;
            try { stubborn.Close(); } catch { }
        }
    }

    /// <summary>Run through <see cref="TestRunner.ExecuteWatched"/> by the test below, not a test itself.</summary>
    public static void NestedProbe() => Check.Throws<InvalidOperationException>(() => TestGuard.CheckLaunch("the nested probe"));

    [UnitTest]
    public static void A_nested_run_does_not_consume_the_refusals_of_the_test_that_started_it()
    {
        Precondition();
        Check.Throws<InvalidOperationException>(() => TestGuard.CheckLaunch("the outer test's own refusal"));
        var inner = TestRunner.ExecuteWatched(typeof(TestGuardTests).GetMethod(nameof(NestedProbe))!, noSkip: false);
        Check.Equal(TestState.Failed, inner.State, "the probe's own refusal fails the probe");
        Check.Contains(inner.Text ?? "", "the nested probe");
        var mine = TestGuard.TakeViolations();   // the outer test's refusal is still here, and only it
        Check.Equal(1, mine.Length);
        Check.Contains(mine[0], "the outer test's own refusal");
    }

    [UnitTest]
    public static void A_write_outside_the_true_real_folder_is_not_refused()
    {
        using var dir = new TempDir();
        string file = dir.File("a.json");
        AtomicFile.WriteAllText(file, "{}");
        Check.True(File.Exists(file));
        Check.Equal(0, TestGuard.ViolationCount, "a write to scratch is not refused");
    }

    [UnitTest]
    public static void The_real_folder_check_is_a_plain_path_comparison()
    {
        const string root = @"C:\Users\Someone\AppData\Roaming\Clink";
        Check.True(TestGuard.IsUnderTrueRealDir(root, root));
        Check.True(TestGuard.IsUnderTrueRealDir(root + @"\settings.json", root));
        Check.True(TestGuard.IsUnderTrueRealDir(root.ToUpperInvariant() + @"\a\..\b.json", root));
        Check.False(TestGuard.IsUnderTrueRealDir(root + "-dev" + @"\settings.json", root), "a sibling that starts the same is not inside");
        Check.False(TestGuard.IsUnderTrueRealDir(@"C:\Users\Someone\AppData\Roaming\settings.json", root));
        Check.False(TestGuard.IsUnderTrueRealDir(root + @"\..\x.json", root), "dot segments are resolved");
    }

    [UnitTest]
    public static void In_test_mode_the_settings_folders_never_resolve_to_the_true_real_folder()
    {
        string real = TestGuard.TrueRealDir;
        Check.True(TestGuard.Active);
        Check.False(TestGuard.IsUnderTrueRealDir(Settings.Dir), "Settings.Dir: " + Settings.Dir);
        Check.False(TestGuard.IsUnderTrueRealDir(Settings.RealDir), "Settings.RealDir: " + Settings.RealDir);
        Check.False(TestGuard.IsUnderTrueRealDir(Settings.FilePath), "Settings.FilePath: " + Settings.FilePath);
        Check.NotEqual(real, Settings.Dir);
        Check.NotEqual(real, Settings.RealDir);
        // the startup redirect, which holds between the per-test resets and after the last one
        Check.NotNull(TestGuard.StartupScratch);
        Check.False(TestGuard.IsUnderTrueRealDir(TestGuard.StartupScratch!), "startup scratch: " + TestGuard.StartupScratch);
    }
}
