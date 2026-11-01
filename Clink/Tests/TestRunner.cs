using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Clink.Services;

namespace Clink.Tests;

// The in-exe unit tests (docs: v0.2.5 plan section 4.6). There is no test project, and no test framework in the NuGet cache, so
// `Clink.exe --unittest [nameFilter]` finds every [UnitTest] method in namespace Clink.Tests by reflection, runs them one after
// the other on the UI thread, prints PASS / FAIL / SKIP per test and exits 0 only when none failed (a skip is a failure when
// CLINK_TESTS_NO_SKIP=1: see TestSkipped). Each area adds its own
// Clink/Tests/<Area>Tests.cs; nothing is registered anywhere.
//
// A test is `[UnitTest] public static void Name()` in a static class of namespace Clink.Tests, using Check for its assertions.
// Tests are synchronous and pure: they must not touch %APPDATA%\Clink, the single-instance mutex or the pipe. Code that reads a
// path goes through Settings.DirOverride / ClipLibrary.IndexPathOverride, pointed at a TempDir. The runner points both at a
// scratch folder for the whole run and puts them back after every test, so a test that forgets still cannot reach the real files.

/// <summary>Marks a static, parameterless, void method of namespace <c>Clink.Tests</c> as a test for <c>Clink.exe --unittest</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class UnitTestAttribute : Attribute { }

/// <summary>A failed check: the runner prints the message as the reason for the FAIL.</summary>
internal sealed class TestFailure : Exception
{
    public TestFailure(string message) : base(message) { }
}

/// <summary>
/// A test that cannot run on this machine because a Windows feature it needs is missing (see <see cref="Check.Skip"/>): the runner
/// prints SKIP and counts it, and it is a FAIL when the environment variable CLINK_TESTS_NO_SKIP is 1, which is how a machine
/// that is supposed to have the feature (a developer's, the PM's) runs them so that nothing is skipped by accident.
/// </summary>
internal sealed class TestSkipped : Exception
{
    public TestSkipped(string why) : base(why) { }
}

/// <summary>Assertions. Every check takes an optional <c>what</c> that is put in front of the failure message.</summary>
internal static class Check
{
    public static void Fail(string message) => throw new TestFailure(message);

    /// <summary>Stops the test as skipped: <paramref name="why"/> names the missing feature.</summary>
    public static void Skip(string why) => throw new TestSkipped(why);

    public static void True(bool condition, string? what = null)
    {
        if (!condition) throw new TestFailure(Say(what, "expected true but was false"));
    }

    public static void False(bool condition, string? what = null)
    {
        if (condition) throw new TestFailure(Say(what, "expected false but was true"));
    }

    public static void Null(object? actual, string? what = null)
    {
        if (actual != null) throw new TestFailure(Say(what, $"expected null but was {Show(actual)}"));
    }

    public static void NotNull(object? actual, string? what = null)
    {
        if (actual == null) throw new TestFailure(Say(what, "expected a value but was null"));
    }

    /// <summary>
    /// Equal values. Strings compare ordinally; two sequences (arrays, lists, nested ones too) are equal when they have the same
    /// items in the same order, and a failure names the first item that differs.
    /// </summary>
    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        string? diff = Difference(expected, actual);
        if (diff != null) throw new TestFailure(Say(what, diff));
    }

    public static void NotEqual<T>(T notExpected, T actual, string? what = null)
    {
        if (Difference(notExpected, actual) == null) throw new TestFailure(Say(what, $"expected anything but {Show(actual)}"));
    }

    /// <summary>Numbers within <paramref name="tolerance"/> of each other.</summary>
    public static void Near(double expected, double actual, double tolerance = 1e-6, string? what = null)
    {
        if (!(Math.Abs(expected - actual) <= tolerance)) throw new TestFailure(Say(what, $"expected {Show(expected)} ± {Show(tolerance)} but was {Show(actual)}"));
    }

    public static void Contains(string? actual, string expected, string? what = null)
    {
        if (actual == null || !actual.Contains(expected, StringComparison.Ordinal)) throw new TestFailure(Say(what, $"expected {Show(actual)} to contain {Show(expected)}"));
    }

    public static void DoesNotContain(string? actual, string unexpected, string? what = null)
    {
        if (actual != null && actual.Contains(unexpected, StringComparison.Ordinal)) throw new TestFailure(Say(what, $"expected {Show(actual)} not to contain {Show(unexpected)}"));
    }

    /// <summary>The action throws <typeparamref name="TException"/> (or a type derived from it); returns the exception for further checks.</summary>
    public static TException Throws<TException>(Action action, string? what = null) where TException : Exception
    {
        try { action(); }
        catch (TException ex) { return ex; }
        catch (Exception ex) { throw new TestFailure(Say(what, $"expected {typeof(TException).Name} but {ex.GetType().Name} was thrown: {ex.Message}")); }
        throw new TestFailure(Say(what, $"expected {typeof(TException).Name} but nothing was thrown"));
    }

    private static string Say(string? what, string message) => what == null ? message : what + ": " + message;

    private static string? Difference(object? expected, object? actual, string at = "")
    {
        if (expected == null && actual == null) return null;
        if (expected == null || actual == null) return $"expected {Show(expected)} but was {Show(actual)}{at}";
        if (expected is not string && actual is not string && expected is IEnumerable e && actual is IEnumerable a)
        {
            var el = e.Cast<object?>().ToList();
            var al = a.Cast<object?>().ToList();
            for (int i = 0; i < Math.Min(el.Count, al.Count); i++)
            {
                string? d = Difference(el[i], al[i], $" (item {i})");
                if (d != null) return d;
            }
            if (el.Count != al.Count) return $"expected {el.Count} item(s) {Show(expected)} but was {al.Count} item(s) {Show(actual)}{at}";
            return null;
        }
        return expected.Equals(actual) ? null : $"expected {Show(expected)} but was {Show(actual)}{at}";
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        char c => "'" + c + "'",
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

/// <summary>A scratch folder under <c>%TEMP%\clink-unittest\&lt;guid&gt;</c> that is deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-unittest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>A path inside the folder.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public override string ToString() => Path;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

internal enum TestState { Passed, Failed, Skipped }

/// <param name="Text">Why it failed or was skipped; null for a pass.</param>
internal readonly record struct TestOutcome(TestState State, string? Text);

internal static class TestRunner
{
    private const string TestNamespace = "Clink.Tests";

    /// <summary>The environment variable that turns a skipped test into a failure.</summary>
    internal const string NoSkipVariable = "CLINK_TESTS_NO_SKIP";

    /// <summary>
    /// Runs the tests whose "Class.Method" name contains the filter (the first argument after <c>--unittest</c> that is not a
    /// switch; no filter runs all), prints one PASS / FAIL / SKIP line per test and a summary, and returns the number of tests that
    /// failed (1 when the filter matched nothing, so a mistyped filter is not a pass). A skipped test is not a failure unless
    /// CLINK_TESTS_NO_SKIP=1.
    /// </summary>
    public static int Run(string[] args)
    {
        string? filter = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var all = Discover();
        var tests = all.Where(t => filter == null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tests.Count == 0)
        {
            Print(all.Count == 0 ? "No tests found." : $"No test matches '{filter}' (of {all.Count}).");
            return 1;
        }

        // Nothing a test does by accident may reach the real settings, clip index or updates folder.
        string scratch = Path.Combine(Path.GetTempPath(), "clink-unittest", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var before = Shared.Capture();
        bool noSkip = IsOn(Environment.GetEnvironmentVariable(NoSkipVariable));
        string? timeoutValue = Environment.GetEnvironmentVariable(TimeoutVariable);
        var limit = ParseTimeout(timeoutValue, out string? limitNote);
        bool watch = ShouldWatch(Debugger.IsAttached, timeoutValue);
        if (!watch) Print($"A debugger is attached: no time limit per test (set {TimeoutVariable} to have one).");
        else if (limitNote != null) Print(limitNote);
        // The paths are never put back to the real ones: this process only ends after the run (App.OnStartup shuts down), nothing
        // here needs %APPDATA%\Clink, and a save a test queued for the UI thread (Settings.Save from a background thread) resolves its
        // folder when it runs, which can be after the run: it must land in the scratch folder, not in the user's real settings.json.
        // So the queued UI work is run first (DrainUi), while the scratch folder is still in force, and the scratch folder stays
        // pointed at afterwards (a late save may recreate it; that is harmless). Same after a takeover, where nothing is drained
        // (the UI thread is the stuck one) and the process exits at once.
        var session = new Session(tests, noSkip, limit,
            before: () => ResetShared(before, scratch), after: () => ResetShared(before, scratch),
            cleanup: () => EndOfRun(scratch),
            print: Print, exit: code => Environment.Exit(code == 0 ? 0 : 1), watch: watch);
        return session.Run();
    }

    /// <summary>The environment variable that sets the per-test time limit, in seconds (a number of seconds above 0, up to 86400; anything else means <see cref="DefaultTimeout"/>).</summary>
    internal const string TimeoutVariable = "CLINK_TEST_TIMEOUT_S";

    /// <summary>
    /// The longest a test may run before it is reported as failed. The slowest test of a full run takes about 12 s (the PM's machine,
    /// a fake exe handed to Windows can take 10 s more), so this is ten times that: a limit only a hang reaches.
    /// </summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The shortest limit that is honoured: a smaller positive value is raised to this.</summary>
    internal static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The time limit for a value of <see cref="TimeoutVariable"/> (see <see cref="ParseTimeout"/>).</summary>
    internal static TimeSpan TimeoutFrom(string? value) => ParseTimeout(value, out _);

    /// <summary>
    /// The time limit for a value of <see cref="TimeoutVariable"/>: a number of seconds above 0 and up to 86400, at least
    /// <see cref="MinimumTimeout"/>; null or empty or anything else is <see cref="DefaultTimeout"/>. <paramref name="note"/> says which
    /// limit is in use when the variable is set (null when it is not), and warns when the value is no good.
    /// </summary>
    internal static TimeSpan ParseTimeout(string? value, out string? note)
    {
        string Secs(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";
        if (string.IsNullOrEmpty(value)) { note = null; return DefaultTimeout; }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) || !(s > 0 && s <= 86400))
        {
            note = $"Warning: {TimeoutVariable}='{value}' is not a number of seconds above 0, up to 86400: using the default of {Secs(DefaultTimeout)} per test.";
            return DefaultTimeout;
        }
        var limit = TimeSpan.FromSeconds(s);
        if (limit < MinimumTimeout)
        {
            note = $"Time limit per test: {Secs(MinimumTimeout)} ({TimeoutVariable}='{value}' is below the minimum of {Secs(MinimumTimeout)}).";
            return MinimumTimeout;
        }
        note = $"Time limit per test: {Secs(limit)} ({TimeoutVariable}).";
        return limit;
    }

    /// <summary>
    /// Whether the watchdog runs: not with a debugger attached (a pause in the debugger would count as a hang and the takeover then
    /// ends the debug session), unless <see cref="TimeoutVariable"/> is set, which asks for a limit explicitly.
    /// </summary>
    internal static bool ShouldWatch(bool debuggerAttached, string? timeoutValue) => !debuggerAttached || !string.IsNullOrEmpty(timeoutValue);

    /// <summary>The limit once a test has timed out and the run went on elsewhere (at most this, whatever the limit was): see <see cref="Session"/>.</summary>
    internal static readonly TimeSpan AfterTimeoutLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One run of a list of tests with a time limit per test. The tests run one after the other on the calling thread (they need the
    /// UI thread). A watchdog timer watches the one running; when it is over the limit the test is reported as a FAIL ("timed out") and
    /// the rest of the run goes on from the next test on a new thread, because a thread stuck in a call cannot be aborted in .NET: the
    /// stuck one is left where it is. Whoever finishes the run prints the summary; a takeover then ends the process (the stuck thread
    /// is the main thread and would keep it alive), the caller of <see cref="Run"/> otherwise just gets the number of failures.
    /// </summary>
    internal sealed class Session
    {
        private readonly List<(string Name, MethodInfo Method)> _tests;
        private readonly bool _noSkip;
        private TimeSpan _limit;
        private readonly TimeSpan _afterTimeoutLimit;
        private readonly bool _watch;
        private readonly Action _before, _after, _cleanup;
        private readonly Action? _restore;
        private readonly Action<string> _print;
        private readonly Action<int> _exit;
        private readonly object _gate = new();
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly ManualResetEventSlim _done = new();
        private Timer? _watchdog;
        private int _passed, _failed, _skipped, _generation, _ranAfterTimeout, _current = -1;
        private long _startedAt;
        private bool _finished;

        /// <param name="cleanup">Always runs once at the end (runs the queued UI work, deletes the scratch folder).</param>
        /// <param name="restore">Optional: puts back what needs to be in force after the run; runs before <paramref name="cleanup"/> and only when no takeover happened. The real runner passes none: it never points anything back at the real folders (see <see cref="Run"/>).</param>
        /// <param name="watch">False: no watchdog (a debugger is attached).</param>
        /// <param name="afterTimeoutLimit">The cap on the limit once a test has timed out (<see cref="TestRunner.AfterTimeoutLimit"/>).</param>
        public Session(List<(string Name, MethodInfo Method)> tests, bool noSkip, TimeSpan limit, Action before, Action after, Action cleanup, Action<string> print, Action<int> exit,
            Action? restore = null, bool watch = true, TimeSpan? afterTimeoutLimit = null)
        {
            _tests = tests; _noSkip = noSkip; _limit = limit; _before = before; _after = after; _cleanup = cleanup; _print = print; _exit = exit;
            _restore = restore; _watch = watch; _afterTimeoutLimit = afterTimeoutLimit ?? AfterTimeoutLimit;
        }

        /// <summary>The limit now in force: lowered to <c>min(limit, 10 s)</c> after the first timeout, because the thread that is stuck is the UI thread and any test that needs it (a Dispatcher.Invoke) would otherwise cost the whole limit each.</summary>
        public TimeSpan CurrentLimit { get { lock (_gate) return _limit; } }

        private static TimeSpan PeriodFor(TimeSpan limit) => TimeSpan.FromMilliseconds(Math.Clamp(limit.TotalMilliseconds / 4, 10, 1000));

        /// <summary>Set once the summary is out (by the caller of <see cref="Run"/> or by a takeover).</summary>
        public WaitHandle Done => _done.WaitHandle;

        /// <summary>The number of failed tests (a timed-out one counts); valid once <see cref="Done"/> is set.</summary>
        public int Failed => _failed;

        public int Run()
        {
            if (_watch)
            {
                var period = PeriodFor(_limit);
                _watchdog = new Timer(_ => Watch(), null, period, period);
            }
            bool completed;
            try { completed = Loop(0, 0); }
            catch
            {
                // This thread was superseded while it was stuck before a test (the hook), and the hook then threw: the takeover thread
                // is running tests, so it is the one that finishes (cleaning up now would pull the rug from under it).
                if (!Finish(takeover: false, summary: false, unlessSuperseded: true)) { _done.Wait(); return _failed; }
                throw;
            }
            if (completed) Finish(takeover: false, summary: true);
            else _done.Wait();   // this thread was stuck in a test that has since been reported; the takeover does the rest
            return _failed;
        }

        /// <summary>Runs the tests from <paramref name="from"/> on; false when this loop was superseded by a timeout (it then stops, saying nothing).</summary>
        private bool Loop(int from, int generation)
        {
            for (int i = from; i < _tests.Count; i++)
            {
                var (name, method) = _tests[i];
                lock (_gate)
                {
                    if (_generation != generation) return false;
                    _current = i;
                    _startedAt = Stopwatch.GetTimestamp();
                    if (generation != 0) _ranAfterTimeout++;
                }
                _before();
                var sw = Stopwatch.StartNew();
                var outcome = ExecuteWatched(method, _noSkip);
                lock (_gate)
                {
                    if (_generation != generation) return false;   // timed out meanwhile: already reported, the run went on elsewhere
                    _current = -1;
                }
                _after();
                switch (outcome.State)
                {
                    case TestState.Passed: _passed++; _print($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)"); break;
                    case TestState.Skipped: _skipped++; _print($"SKIP  {name}  {outcome.Text}"); break;
                    default: _failed++; _print($"FAIL  {name}  {outcome.Text}"); break;
                }
            }
            return true;
        }

        private void Watch()
        {
            int next, generation;
            TimeSpan newLimit;
            lock (_gate)
            {
                if (_finished || _current < 0 || Stopwatch.GetElapsedTime(_startedAt) < _limit) return;
                next = _current + 1;
                generation = ++_generation;
                _failed++;
                _print($"FAIL  {_tests[_current].Name}  timed out after {_limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s (the test is left running on its thread; the run goes on with the next test)");
                _current = -1;
                // From here on the UI thread is stuck: a test that calls Dispatcher.Invoke (the theme, the recycle bin) or saves settings
                // blocks for the whole limit. So the rest of the run gets a short one.
                _limit = newLimit = _limit < _afterTimeoutLimit ? _limit : _afterTimeoutLimit;
                _watchdog?.Change(PeriodFor(newLimit), PeriodFor(newLimit));   // under the lock: Finish sets _finished under it before it disposes the timer
            }
            var thread = new Thread(() =>
            {
                try { if (Loop(next, generation)) Finish(takeover: true, summary: true); }
                catch (Exception ex)
                {
                    // A hook threw on this thread. A later timeout may have superseded it: then the newer thread owns the run, and this says nothing.
                    lock (_gate) { if (_generation != generation) return; _failed++; }
                    _print("the runner crashed after a timeout: " + ex);
                    Finish(takeover: true, summary: false);
                }
            }) { IsBackground = true, Name = "unittest-after-timeout" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>Ends the run once; false when it was not this call that did (already finished, or <paramref name="unlessSuperseded"/> and a takeover has happened).</summary>
        private bool Finish(bool takeover, bool summary, bool unlessSuperseded = false)
        {
            bool tookOver;
            lock (_gate)
            {
                if (unlessSuperseded && _generation != 0) return false;
                if (_finished) return true;
                _finished = true;   // the watchdog checks this under the same lock: no takeover can start after this point
                tookOver = takeover || _generation != 0;
            }
            _watchdog?.Dispose();
            // After a takeover a test may still be alive on its stuck thread: the real paths are not put back (see the constructor).
            if (!tookOver) try { _restore?.Invoke(); } catch { }
            try { _cleanup(); } catch { }
            if (summary)
            {
                _print($"{_passed} passed, {_failed} failed, {_skipped} skipped ({_total.ElapsedMilliseconds} ms)");
                if (_ranAfterTimeout > 0) _print($"{_ranAfterTimeout} tests ran after a timeout on a non-UI thread; their results are best effort");
            }
            if (tookOver) _exit(_failed);   // the real one ends the process here; before Done, so a waiter that sees Done also sees the exit
            _done.Set();
            return true;
        }
    }

    /// <summary>The process-wide switches a test could change and forget: where the settings, the clip index, the updates, the real Clink's folder and the other apps' packages live, the --dev state, and the updater's fake network and feed trust anchors.</summary>
    private sealed record Shared(string? SettingsDir, string? ClipIndex, string? UpdatesDir, string InstanceSuffix, bool DevHotkeys, bool DevAcceptImport, bool DevToken, bool DevRecord, bool DevQuiet, bool DevQuietRecord, bool DevNoMarker, string? DevDemoStatus,
        string? RealDir, string? PackagesRoot,
        Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>>? UpdaterHttp, FeedTrust? FeedTrust,
        TimeSpan? FeedTimeout, Packaging? Packaging, Func<string, TimeSpan, Updater.PreflightResult>? Preflight, IReadOnlyList<Version>? Runtimes, bool? Elevated,
        Func<DateTimeOffset>? UpdaterClock, TimeSpan? DownloadAnswerTimeout, TimeSpan? DownloadReadTimeout, Action<string>? SendObserver, Action<string>? Browser,
        TimeSpan? DownloadTotalTimeout, long? DownloadMinRate, TimeSpan? DownloadRateWindow, (string? Stable, string? Dev)? FloorSeeds)
    {
        public static Shared Capture() => new(Settings.DirOverride, ClipLibrary.IndexPathOverride, Updater.UpdatesDirOverride,
            AppController.InstanceSuffix, AppController.DevHotkeys, AppController.DevAcceptImport, AppController.DevToken, AppController.DevRecord, AppController.DevQuiet, AppController.DevQuietRecord, AppController.DevNoMarker, AppController.DevDemoStatus,
            Settings.RealDirOverride, SettingsImport.PackagesRoot,
            Updater.HttpOverride, Updater.FeedOverride, Updater.FeedTimeoutOverride,
            Updater.PackagingOverride, Updater.PreflightOverride, DotNetRuntimes.Override, Updater.IsElevatedOverride,
            Updater.ClockOverride, Updater.DownloadAnswerTimeoutOverride, Updater.DownloadReadTimeoutOverride, Updater.SendObserver, Updater.BrowserOverride,
            Updater.DownloadTotalTimeoutOverride, Updater.DownloadMinRateOverride, Updater.DownloadRateWindowOverride, Updater.FloorSeedOverride);
    }

    /// <summary>The feed trust a test starts with: On, with a made-up base and two keys that never signed anything (tests that need a feed bring their own).</summary>
    private static readonly Lazy<FeedTrust> TestFeed = new(() =>
    {
        string Key() { using var k = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256); return Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()); }
        return FeedTrust.Create("https://cdn.example/clink/", new[] { Key() }, new[] { Key() });
    });

    // ---- strays: a request nobody faked, a browser nobody faked. The default fakes below throw a TestFailure, but code under test often
    // swallows exceptions (a check turns any failure into LastError, OpenReleasePage into a message), so they also record here, and the
    // runner fails a test that left anything in this list, whatever the test itself concluded.

    private static readonly List<string> s_strays = new();

    /// <summary>Records something a test did that nothing faked (see above); the test fails once it has run.</summary>
    internal static void RecordStray(string what)
    {
        lock (s_strays) s_strays.Add(what);
    }

    /// <summary>
    /// <see cref="Execute"/>, failing the test when it left a stray (<see cref="RecordStray"/>) even though it passed or was skipped.
    /// The list is per run of this method: it is empty when the test starts and put back as it was afterwards, so a test may run another
    /// method through this (the runner's own tests do) without the inner strays counting against itself.
    /// </summary>
    internal static TestOutcome ExecuteWatched(MethodInfo method, bool noSkip)
    {
        List<string> outer;
        lock (s_strays) { outer = new List<string>(s_strays); s_strays.Clear(); }
        try
        {
            var outcome = Execute(method, noSkip);
            string[] strays;
            lock (s_strays) strays = s_strays.ToArray();
            if (strays.Length == 0) return outcome;
            string note = $"{strays.Length} unfaked call(s) (swallowed by the code under test or not), first: {strays[0]}";
            return new(TestState.Failed, outcome.State == TestState.Failed ? outcome.Text + "; also " + note : note);
        }
        finally
        {
            lock (s_strays) { s_strays.Clear(); s_strays.AddRange(outer); }
        }
    }

    /// <summary>The last thing a run does: runs the queued UI work (<see cref="DrainUi"/>) with the scratch folder still in force, then deletes it. Nothing is pointed back at the real folders.</summary>
    internal static void EndOfRun(string scratch)
    {
        DrainUi();
        try { Directory.Delete(scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// Runs what the test run left queued for the UI thread, while the scratch folder is still in force: the tests run inside one
    /// UI-thread call, so the dispatcher does not pump until the run is over, and a save a background thread asked for (the updater's
    /// checks and downloads: <see cref="Settings.Save"/> queues it onto the UI thread) would run only after that. Does nothing off the UI thread.
    /// </summary>
    internal static void DrainUi()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || !dispatcher.CheckAccess() || dispatcher.HasShutdownStarted) return;
        var frame = new System.Windows.Threading.DispatcherFrame();
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Puts back what a test may have changed that others would trip over: everything in <paramref name="before"/> (the --dev state, so
    /// no test leaves the process looking like a dev instance), frozen saves, the import state and the redirect findings; and points the
    /// settings folder, the clip index, the updates folder, the "real" %APPDATA%\Clink (<see cref="Settings.RealDirOverride"/>: nothing is
    /// there) and the folder that holds other apps' packages (<see cref="SettingsImport.PackagesRoot"/>: nothing is there) at
    /// <paramref name="scratch"/> (or at where they were, when null). So a test never looks at the real settings or at the packages of
    /// whoever runs it. Before and after every test.
    /// </summary>
    private static void ResetShared(Shared before, string? scratch)
    {
        AppController.InstanceSuffix = before.InstanceSuffix;
        AppController.DevHotkeys = before.DevHotkeys;
        AppController.DevAcceptImport = before.DevAcceptImport;
        AppController.DevToken = before.DevToken;
        AppController.DevRecord = before.DevRecord;
        AppController.DevQuiet = before.DevQuiet;
        AppController.DevQuietRecord = before.DevQuietRecord;
        AppController.DevNoMarker = before.DevNoMarker;
        AppController.DevDemoStatus = before.DevDemoStatus;
        Settings.DirOverride = scratch ?? before.SettingsDir;
        Settings.RealDirOverride = scratch != null ? Path.Combine(scratch, "RealClink") : before.RealDir;
        ClipLibrary.IndexPathOverride = scratch != null ? Path.Combine(scratch, "clips.json") : before.ClipIndex;
        Updater.UpdatesDirOverride = scratch != null ? Path.Combine(scratch, "updates") : before.UpdatesDir;
        // No test leaves a fake network or feed behind for the next, and during a test any request the test did not fake fails it: it
        // throws (so it never reaches the network) and is recorded as a stray, which fails the test after it ran even when the code under
        // test swallowed the exception (ExecuteWatched).
        Updater.HttpOverride = scratch != null ? static (req, _) =>
        {
            RecordStray("a request nobody faked: " + req.RequestUri);
            throw new TestFailure("a test must not reach the network: " + req.RequestUri);
        } : before.UpdaterHttp;
        Updater.FeedOverride = scratch != null ? TestFeed.Value : before.FeedTrust;   // a test runs as a build with a signed feed (keys that sign nothing), unless it says otherwise
        Updater.FeedTimeoutOverride = before.FeedTimeout;
        Updater.ClockOverride = before.UpdaterClock;        // the updater's fake clock, its test timeouts and the client probe
        Updater.DownloadAnswerTimeoutOverride = before.DownloadAnswerTimeout;
        Updater.DownloadReadTimeoutOverride = before.DownloadReadTimeout;
        Updater.SendObserver = before.SendObserver;
        Updater.BrowserOverride = scratch != null ? static url =>
        {
            RecordStray("a browser nobody faked: " + url);
            throw new TestFailure("a test must not open a browser: " + url);
        } : before.Browser;
        Updater.FloorSeedOverride = scratch != null ? (null, null) : before.FloorSeeds;   // no built-in floors during a test (a CI build has them), unless it sets its own
        Updater.DownloadTotalTimeoutOverride = before.DownloadTotalTimeout;
        Updater.DownloadMinRateOverride = before.DownloadMinRate;
        Updater.DownloadRateWindowOverride = before.DownloadRateWindow;
        Updater.PackagingOverride = scratch != null ? Packaging.SelfContained : before.Packaging;   // tests run as the portable flavour (updates\ folder, Clink.exe) unless they say otherwise
        Updater.PreflightOverride = scratch != null ? static (_, _) => new(0, false, null) : before.Preflight;   // never a real process; a test installs its own fake
        DotNetRuntimes.Override = scratch != null ? new[] { new Version(10, 0, 0) } : before.Runtimes;
        Updater.IsElevatedOverride = before.Elevated;   // null = ask Windows; a test that pretends to be elevated does not leave it so
        Settings.ThawSaves();
        Settings.SeedDefaults = scratch == null;   // off during a test (the older tests pin exact hotkey lists); the seeding tests turn it on themselves
        SettingsImport.Reset();
        SettingsImport.PackagesRoot = scratch != null ? Path.Combine(scratch, "Packages") : before.PackagesRoot;
        AppDataRedirect.Reset();
        Updater.LaunchOverride = null;   // no launch seam left over: a test that starts the apply installs its own
    }

    private static bool IsOn(string? value) => value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every [UnitTest] method of namespace Clink.Tests, by "Class.Method" name.</summary>
    private static List<(string Name, MethodInfo Method)> Discover()
    {
        const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return typeof(TestRunner).Assembly.GetTypes()
            .Where(t => t.IsClass && t.Namespace == TestNamespace)
            .SelectMany(t => t.GetMethods(all).Where(m => m.GetCustomAttribute<UnitTestAttribute>() != null).Select(m => (Name: t.Name + "." + m.Name, Method: m)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Runs one test: a pass, a failure with the reason (one line), or a skip with the reason. With <paramref name="noSkip"/> a skip
    /// is reported as a failure that says so.
    /// </summary>
    internal static TestOutcome Execute(MethodInfo method, bool noSkip)
    {
        if (!method.IsStatic || method.GetParameters().Length != 0 || method.ReturnType != typeof(void))
            return new(TestState.Failed, "a test must be `public static void Name()` with no parameters");
        try
        {
            method.Invoke(null, null);
            return new(TestState.Passed, null);
        }
        catch (TargetInvocationException tie)
        {
            var ex = tie.InnerException ?? tie;
            if (ex is TestSkipped skip)
                return noSkip ? new(TestState.Failed, $"skipped ({skip.Message}), and {NoSkipVariable}=1 says nothing may be") : new(TestState.Skipped, skip.Message);
            if (ex is TestFailure) return new(TestState.Failed, ex.Message);
            // Anything else is a bug in the code under test or in the test: show where.
            string where = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return new(TestState.Failed, $"{ex.GetType().Name}: {ex.Message} {where}".Replace('\r', ' ').Replace('\n', ' '));
        }
        catch (Exception ex)
        {
            return new(TestState.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Print(string line)
    {
        try { Console.WriteLine(line); } catch { }
        Log.Info("[unittest] " + line);
    }
}
