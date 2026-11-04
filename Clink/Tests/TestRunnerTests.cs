using System.IO;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The test helpers every other area's tests lean on, and the runner's promise that no test reaches the real files.</summary>
internal static class TestRunnerTests
{
    [UnitTest]
    public static void A_test_starts_with_the_settings_and_clip_index_in_a_scratch_folder()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink");
        Check.NotNull(Settings.DirOverride, "Settings.DirOverride");
        Check.NotEqual(real, Settings.Dir);
        Check.False(Settings.FilePath.StartsWith(real, StringComparison.OrdinalIgnoreCase), "settings.json is not the real one");
        Check.NotNull(ClipLibrary.IndexPathOverride, "ClipLibrary.IndexPathOverride");
        Check.False(ClipLibrary.IndexPathOverride!.StartsWith(real, StringComparison.OrdinalIgnoreCase), "clips.json is not the real one");
        Check.False(AppController.IsDev, "a test run is never a --dev instance");
    }

    [UnitTest]
    public static void Equal_compares_sequences_item_by_item_and_names_the_first_difference()
    {
        Check.Equal(new[] { 1, 2, 3 }, new[] { 1, 2, 3 });
        Check.Equal(new List<string> { "a", "b" }, new[] { "a", "b" }.ToList());
        Check.Equal(new[] { new[] { 1 }, new[] { 2, 3 } }, new[] { new[] { 1 }, new[] { 2, 3 } });
        Check.Equal<object?>(null, null);
        Check.Equal("a", "a");

        var different = Check.Throws<TestFailure>(() => Check.Equal(new[] { 1, 2, 3 }, new[] { 1, 5, 3 }));
        Check.Contains(different.Message, "item 1");
        Check.Contains(different.Message, "expected 2 but was 5");
        var shorter = Check.Throws<TestFailure>(() => Check.Equal(new[] { 1, 2, 3 }, new[] { 1, 2 }));
        Check.Contains(shorter.Message, "expected 3 item(s)");
        Check.Throws<TestFailure>(() => Check.Equal("a", "A"));   // ordinal
        Check.Throws<TestFailure>(() => Check.Equal<string?>("a", null));
        Check.Throws<TestFailure>(() => Check.NotEqual(new[] { 1 }, new[] { 1 }));
    }

    [UnitTest]
    public static void Throws_returns_the_exception_and_fails_without_one()
    {
        var ex = Check.Throws<ArgumentException>(() => throw new ArgumentOutOfRangeException("x"));   // a derived type counts
        Check.Equal("x", ((ArgumentOutOfRangeException)ex).ParamName);
        Check.Contains(Check.Throws<TestFailure>(() => Check.Throws<InvalidOperationException>(() => { })).Message, "nothing was thrown");
        Check.Contains(Check.Throws<TestFailure>(() => Check.Throws<InvalidOperationException>(() => throw new FormatException("f"))).Message, "FormatException");
    }

    [UnitTest]
    public static void The_other_checks_pass_and_fail_as_named()
    {
        Check.True(true);
        Check.False(false);
        Check.Null(null);
        Check.NotNull("x");
        Check.Near(1.0, 1.0000004);
        Check.Near(10, 10.4, 0.5);
        Check.Contains("hello world", "o w");
        Check.DoesNotContain("hello", "z");
        Check.Throws<TestFailure>(() => Check.True(false, "why"));
        Check.Contains(Check.Throws<TestFailure>(() => Check.True(false, "why")).Message, "why");
        Check.Throws<TestFailure>(() => Check.False(true));
        Check.Throws<TestFailure>(() => Check.Null("x"));
        Check.Throws<TestFailure>(() => Check.NotNull(null));
        Check.Throws<TestFailure>(() => Check.Near(1, 2));
        Check.Throws<TestFailure>(() => Check.Near(1, double.NaN));
        Check.Throws<TestFailure>(() => Check.Contains("abc", "z"));
        Check.Throws<TestFailure>(() => Check.DoesNotContain("abc", "b"));
        Check.Throws<TestFailure>(() => Check.Fail("no"));
    }

    // Helpers for the next test: not [UnitTest], so the runner does not run them as tests.
    private static void SkipsForAMissingFeature() => Check.Skip("no AAC encoder here");
    private static void Passes() { }
    private static void Fails() => Check.Fail("nope");

    [UnitTest]
    public static void A_skipped_test_is_counted_apart_and_a_failure_only_when_nothing_may_be_skipped()
    {
        System.Reflection.MethodInfo Method(string name) => typeof(TestRunnerTests).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var skip = TestRunner.Execute(Method(nameof(SkipsForAMissingFeature)), noSkip: false);
        Check.Equal(TestState.Skipped, skip.State);
        Check.Equal("no AAC encoder here", skip.Text);
        var strict = TestRunner.Execute(Method(nameof(SkipsForAMissingFeature)), noSkip: true);
        Check.Equal(TestState.Failed, strict.State, "CLINK_TESTS_NO_SKIP turns a skip into a failure");
        Check.Contains(strict.Text, "no AAC encoder here");
        Check.Contains(strict.Text, TestRunner.NoSkipVariable);
        Check.Equal(TestState.Passed, TestRunner.Execute(Method(nameof(Passes)), noSkip: true).State);
        var failed = TestRunner.Execute(Method(nameof(Fails)), noSkip: false);
        Check.Equal(TestState.Failed, failed.State);
        Check.Equal("nope", failed.Text);
    }

    // ---- the per-test time limit (TestRunner.Session). Helpers, not [UnitTest]: the tests below run them through a Session of their own.
    private static ManualResetEventSlim s_hold = new();
    private static void HoldsUntilReleased() => s_hold.Wait(TimeSpan.FromSeconds(60));

    private static List<(string, System.Reflection.MethodInfo)> Methods(params string[] names)
        => names.Select(n => (n, typeof(TestRunnerTests).GetMethod(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!)).ToList();

    [UnitTest]
    public static void A_test_over_its_time_limit_is_reported_as_timed_out_and_the_run_goes_on()
    {
        s_hold = new ManualResetEventSlim();
        var lines = new List<string>();
        int exited = -1;
        var session = new TestRunner.Session(Methods(nameof(Passes), nameof(HoldsUntilReleased), nameof(Fails), nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromSeconds(1), before: () => { }, after: () => { }, cleanup: () => { },
            print: l => { lock (lines) lines.Add(l); }, exit: c => exited = c);
        var caller = new Thread(() => session.Run()) { IsBackground = true };
        try
        {
            caller.Start();   // stands in for the UI thread: it is the one stuck in the second test
            Check.True(session.Done.WaitOne(TimeSpan.FromSeconds(30)), "the run finished although a test is stuck");
            string[] seen; lock (lines) seen = lines.ToArray();
            Check.True(seen[0].StartsWith("PASS  T0"), seen[0]);
            Check.True(seen[1].StartsWith("FAIL  T1  timed out after 1 s"), seen[1]);
            Check.True(seen[2].StartsWith("FAIL  T2  nope"), "the run went on with the next test: " + seen[2]);
            Check.True(seen[3].StartsWith("PASS  T3"), seen[3]);
            Check.Equal("2 passed, 2 failed, 0 skipped", seen[4].Substring(0, seen[4].IndexOf(" (", StringComparison.Ordinal)));
            Check.Equal("2 tests ran after a timeout on a non-UI thread; their results are best effort", seen[5]);
            Check.Equal(6, seen.Length);
            Check.Equal(2, session.Failed);
            Check.Equal(TimeSpan.FromSeconds(1), session.CurrentLimit, "a limit below the cap stays as it is");
            Check.Equal(2, exited, "a takeover ends the process with the number of failures");
        }
        finally { s_hold.Set(); }
        Check.True(caller.Join(TimeSpan.FromSeconds(30)), "the stuck test, once released, lets its thread go");
        lock (lines) Check.Equal(6, lines.Count);   // and says nothing more: it was reported already
    }

    [UnitTest]
    public static void A_test_within_its_time_limit_is_not_touched_by_the_watchdog()
    {
        var lines = new List<string>();
        int exited = -1;
        var session = new TestRunner.Session(Methods(nameof(Passes), nameof(SkipsForAMissingFeature), nameof(Fails)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromSeconds(60), before: () => { }, after: () => { }, cleanup: () => { }, print: lines.Add, exit: c => exited = c);
        int failed = session.Run();   // on this thread, as the real run does
        Check.Equal(1, failed);
        Check.Equal(4, lines.Count);
        Check.True(lines[0].StartsWith("PASS  T0"), lines[0]);
        Check.True(lines[1].StartsWith("SKIP  T1"), lines[1]);
        Check.True(lines[2].StartsWith("FAIL  T2  nope"), lines[2]);
        Check.True(lines[3].StartsWith("1 passed, 1 failed, 1 skipped"), lines[3]);
        Check.DoesNotContain(string.Join("\n", lines), "timed out");
        Check.True(session.Done.WaitOne(0), "done");
        Check.Equal(-1, exited, "no takeover, so the process is not ended by the session");
    }

    [UnitTest]
    public static void The_time_limit_comes_from_CLINK_TEST_TIMEOUT_S_or_is_the_default()
    {
        Check.Equal("CLINK_TEST_TIMEOUT_S", TestRunner.TimeoutVariable);
        Check.Equal(TimeSpan.FromSeconds(120), TestRunner.DefaultTimeout);
        Check.Equal(TestRunner.DefaultTimeout, TestRunner.TimeoutFrom(null));
        Check.Equal(TimeSpan.FromSeconds(5), TestRunner.TimeoutFrom("5"));
        Check.Equal(TimeSpan.FromSeconds(1), TestRunner.TimeoutFrom("0.5"), "below the minimum of 1 s: raised to it");
        foreach (string bad in new[] { "", "abc", "0", "-3", "NaN", "1e9", "120s" })
            Check.Equal(TestRunner.DefaultTimeout, TestRunner.TimeoutFrom(bad), $"'{bad}' is no limit");
    }

    [UnitTest]
    public static void The_time_limit_is_at_least_a_second_and_the_run_says_which_limit_it_uses()
    {
        Check.Equal(TimeSpan.FromSeconds(1), TestRunner.ParseTimeout("0.001", out string? tiny));
        Check.Contains(tiny, "1 s");
        Check.Contains(tiny, "minimum");
        Check.Equal(TimeSpan.FromSeconds(7.5), TestRunner.ParseTimeout("7.5", out string? ok));
        Check.Contains(ok, "Time limit per test: 7.5 s");
        Check.Equal(TestRunner.DefaultTimeout, TestRunner.ParseTimeout(null, out string? unset));
        Check.Null(unset, "unset: no note");
        Check.Equal(TestRunner.DefaultTimeout, TestRunner.ParseTimeout("120s", out string? bad));
        Check.Contains(bad, "Warning");
        Check.Contains(bad, "'120s'");
        Check.Contains(bad, "default of 120 s");
    }

    [UnitTest]
    public static void The_watchdog_does_not_run_under_a_debugger_unless_a_limit_is_asked_for()
    {
        Check.True(TestRunner.ShouldWatch(debuggerAttached: false, timeoutValue: null));
        Check.False(TestRunner.ShouldWatch(debuggerAttached: true, timeoutValue: null), "a pause in the debugger is no hang");
        Check.False(TestRunner.ShouldWatch(debuggerAttached: true, timeoutValue: ""));
        Check.True(TestRunner.ShouldWatch(debuggerAttached: true, timeoutValue: "30"), "an explicit limit is honoured");

        // And a session without the watchdog lets a slow test take as long as it takes.
        s_hold = new ManualResetEventSlim();
        var lines = new List<string>();
        var release = new Timer(_ => s_hold.Set(), null, 500, Timeout.Infinite);
        int exited = -1;
        var session = new TestRunner.Session(Methods(nameof(HoldsUntilReleased), nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromMilliseconds(100), before: () => { }, after: () => { }, cleanup: () => { }, print: lines.Add, exit: c => exited = c, watch: false);
        try { Check.Equal(0, session.Run()); }
        finally { release.Dispose(); s_hold.Set(); }
        Check.DoesNotContain(string.Join("\n", lines), "timed out");
        Check.True(lines[0].StartsWith("PASS  T0"), lines[0]);
        Check.True(lines[2].StartsWith("2 passed, 0 failed"), lines[2]);
        Check.Equal(-1, exited);
    }

    [UnitTest]
    public static void After_a_timeout_the_limit_drops_and_the_run_says_its_results_are_best_effort()
    {
        Check.Equal(TimeSpan.FromSeconds(10), TestRunner.AfterTimeoutLimit);
        s_hold = new ManualResetEventSlim();
        var lines = new List<string>();
        int exited = -1;
        var session = new TestRunner.Session(Methods(nameof(HoldsUntilReleased), nameof(HoldsUntilReleased), nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromMilliseconds(800), before: () => { }, after: () => { }, cleanup: () => { },
            print: l => { lock (lines) lines.Add(l); }, exit: c => exited = c, afterTimeoutLimit: TimeSpan.FromMilliseconds(500));
        Check.Equal(TimeSpan.FromMilliseconds(800), session.CurrentLimit);
        var caller = new Thread(() => session.Run()) { IsBackground = true };
        try
        {
            caller.Start();
            Check.True(session.Done.WaitOne(TimeSpan.FromSeconds(30)), "the run finished");
            Check.Equal(TimeSpan.FromMilliseconds(500), session.CurrentLimit, "lowered after the first timeout");
            string[] seen; lock (lines) seen = lines.ToArray();
            Check.True(seen[0].StartsWith("FAIL  T0  timed out after 0.8 s"), seen[0]);
            Check.True(seen[1].StartsWith("FAIL  T1  timed out after 0.5 s"), "the second one got the short limit: " + seen[1]);
            Check.True(seen[2].StartsWith("PASS  T2"), seen[2]);
            Check.True(seen[3].StartsWith("1 passed, 2 failed, 0 skipped"), seen[3]);
            Check.Equal("2 tests ran after a timeout on a non-UI thread; their results are best effort", seen[4]);
            Check.Equal(5, seen.Length);
            Check.Equal(2, exited);
        }
        finally { s_hold.Set(); }
        Check.True(caller.Join(TimeSpan.FromSeconds(30)));

        // A limit already below the cap stays as it is, and a run without a timeout has no such note (the other tests above).
        var low = new TestRunner.Session(Methods(nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromSeconds(3), before: () => { }, after: () => { }, cleanup: () => { }, print: _ => { }, exit: _ => { });
        Check.Equal(TimeSpan.FromSeconds(3), low.CurrentLimit);
    }

    private static ManualResetEventSlim s_entered = new();
    private static void SignalsThenHolds() { s_entered.Set(); s_hold.Wait(TimeSpan.FromSeconds(60)); }

    [UnitTest]
    public static void After_a_takeover_the_real_paths_are_not_put_back()
    {
        int restored = 0, cleaned = 0, exited = -1;

        // No timeout: the real paths come back, then the scratch folder goes.
        var calm = new TestRunner.Session(Methods(nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromSeconds(60), before: () => { }, after: () => { }, cleanup: () => { Check.Equal(1, restored, "restore comes before cleanup"); cleaned++; },
            print: _ => { }, exit: c => exited = c, restore: () => restored++);
        calm.Run();
        Check.Equal(1, restored);
        Check.Equal(1, cleaned);
        Check.Equal(-1, exited);

        // A takeover: the stuck test may resume and save, so the redirection stays (cleanup still runs, the process is ending).
        restored = 0; cleaned = 0;
        s_hold = new ManualResetEventSlim();
        var stuck = new TestRunner.Session(Methods(nameof(HoldsUntilReleased), nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromSeconds(1), before: () => { }, after: () => { }, cleanup: () => cleaned++,
            print: _ => { }, exit: c => exited = c, restore: () => restored++);
        var caller = new Thread(() => stuck.Run()) { IsBackground = true };
        try
        {
            caller.Start();
            Check.True(stuck.Done.WaitOne(TimeSpan.FromSeconds(30)), "done");
            Check.Equal(0, restored, "the real paths are not put back after a takeover");
            Check.Equal(1, cleaned);
            Check.Equal(1, exited);
        }
        finally { s_hold.Set(); }
        Check.True(caller.Join(TimeSpan.FromSeconds(30)));
        Check.Equal(0, restored, "nor when the stuck test lets go");
    }

    [UnitTest]
    public static void A_hook_that_throws_after_the_takeover_leaves_the_finish_to_the_takeover_thread()
    {
        s_hold = new ManualResetEventSlim();
        s_entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        int hookCalls = 0, restored = 0, cleaned = 0, exited = -1;
        var lines = new List<string>();
        Exception? escaped = null;
        var session = new TestRunner.Session(Methods(nameof(Passes), nameof(SignalsThenHolds), nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromSeconds(3),
            before: () =>
            {
                if (Interlocked.Increment(ref hookCalls) != 1) return;
                gate.Wait(TimeSpan.FromSeconds(60));   // stuck before the first test: the watchdog fires meanwhile
                throw new InvalidOperationException("the hook failed");
            },
            after: () => { }, cleanup: () => Interlocked.Increment(ref cleaned),
            print: l => { lock (lines) lines.Add(l); }, exit: c => exited = c, restore: () => Interlocked.Increment(ref restored));
        // T0 is the one the hook is stuck before (reported as timed out); the takeover thread then runs T1 (holds) and T2.
        var caller = new Thread(() => { try { session.Run(); } catch (Exception ex) { escaped = ex; } }) { IsBackground = true };
        try
        {
            caller.Start();
            Check.True(s_entered.Wait(TimeSpan.FromSeconds(30)), "the takeover thread is running a test");
            gate.Set();   // the superseded thread's hook throws now
            Thread.Sleep(200);
            Check.True(caller.IsAlive, "the superseded thread waits for the takeover instead of finishing");
            Check.Equal(0, cleaned, "no cleanup while the takeover thread is still running tests");
            Check.Equal(0, restored);
            Check.Equal(-1, exited);
            s_hold.Set();
            Check.True(session.Done.WaitOne(TimeSpan.FromSeconds(30)), "the takeover thread finished the run");
        }
        finally { s_hold.Set(); gate.Set(); }
        Check.True(caller.Join(TimeSpan.FromSeconds(30)));
        Check.Null(escaped, "the hook's exception is not rethrown by a superseded thread");
        Check.Equal(1, cleaned);
        Check.Equal(0, restored);
        Check.Equal(1, exited, "only T0 (timed out) failed: the takeover exits with the failure count");
        lock (lines) Check.Contains(string.Join("\n", lines), "2 tests ran after a timeout");
    }

    [UnitTest]
    public static void A_save_queued_by_a_background_thread_lands_in_the_scratch_folder_not_in_the_real_one()
    {
        // The end of a run used to point the settings back at the real folder before the UI work a test had queued (Settings.Save from a
        // background thread) ran, so that save wrote the real settings.json. Now the queue is run first, in the scratch folder, and the
        // paths are not put back. Here the "real" folder is a made-up one.
        var ui = System.Windows.Application.Current?.Dispatcher;
        Check.NotNull(ui, "the tests run in a process with an Application");
        Check.True(ui!.CheckAccess(), "and on its UI thread");
        using var scratch = new TempDir();
        using var home = new TempDir();
        string fakeReal = home.File(@"AppData\Roaming\Clink");
        Settings.DirOverride = scratch.Path;
        Settings.RealDirOverride = fakeReal;
        string? dirWhenRun = null; bool wroteScratch = false;
        var saver = new Thread(() => Settings.FromJson("{}").Save());   // queued onto the UI thread: nothing pumps it during a test
        saver.Start();
        Check.True(saver.Join(TimeSpan.FromSeconds(30)));
        ui.BeginInvoke(new Action(() => { dirWhenRun = Settings.Dir; wroteScratch = File.Exists(Settings.FilePath); }));   // queued after the save
        Check.False(File.Exists(scratch.File("settings.json")), "the save has not run yet");

        // The real runner's hooks: no restore, the end of the run as TestRunner.Run does it. No test in this session: each test's own
        // drain (ExecuteWatched) would run the queue first, and this is about the end of the run's.
        var session = new TestRunner.Session(new List<(string, System.Reflection.MethodInfo)>(),
            noSkip: false, limit: TimeSpan.FromSeconds(60), before: () => { }, after: () => { }, cleanup: () => TestRunner.EndOfRun(scratch.Path),
            print: _ => { }, exit: _ => { });
        Check.Equal(0, session.Run());

        Check.Equal(scratch.Path, dirWhenRun, "the queued work ran before the end of the run, in the scratch folder");
        Check.True(wroteScratch, "the save was written there");
        Check.False(Directory.Exists(fakeReal), "nothing was written to the real folder");
        Check.False(File.Exists(Path.Combine(fakeReal, "settings.json")));
        Check.Equal(scratch.Path, Settings.Dir, "the settings were not pointed back at the real folder");
        Check.Equal(fakeReal, Settings.RealDir);
    }

    [UnitTest]
    public static void A_refusal_outside_any_test_fails_the_run_after_the_cleanup()
    {
        // The real runner passes the guard's refusals that no test took (e.g. a queued save the end of the run's drain ran): each fails the run.
        var lines = new List<string>();
        bool cleaned = false;
        var session = new TestRunner.Session(Methods(nameof(Passes)).Select((m, i) => ($"T{i}", m.Item2)).ToList(),
            noSkip: false, limit: TimeSpan.FromSeconds(60), before: () => { }, after: () => { }, cleanup: () => cleaned = true,
            print: lines.Add, exit: _ => { }, leftovers: () => { Check.True(cleaned, "asked after the cleanup"); return new[] { "test guard: a write was refused" }; });
        Check.Equal(1, session.Run());
        Check.Equal(3, lines.Count);
        Check.True(lines[0].StartsWith("PASS  T0"), lines[0]);
        Check.Equal("FAIL  (after the last test)  test guard: a write was refused", lines[1]);
        Check.True(lines[2].StartsWith("1 passed, 1 failed, 0 skipped"), lines[2]);
    }

    [UnitTest]
    public static void TempDir_exists_while_used_and_is_removed_after()
    {
        string path;
        using (var dir = new TempDir())
        {
            path = dir.Path;
            Check.True(Directory.Exists(path));
            Check.True(path.StartsWith(Path.Combine(Path.GetTempPath(), "clink-unittest"), StringComparison.OrdinalIgnoreCase), "under %TEMP%\\clink-unittest");
            File.WriteAllText(dir.File("a.txt"), "x");
            Check.True(File.Exists(dir.File("a.txt")));
            using var other = new TempDir();
            Check.NotEqual(path, other.Path);
        }
        Check.False(Directory.Exists(path));
    }
}
