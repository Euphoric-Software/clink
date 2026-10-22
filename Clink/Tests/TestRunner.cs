using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Clink.Services;

namespace Clink.Tests;

// The in-exe unit tests (docs: v0.2.5 plan section 4.6). There is no test project, and no test framework in the NuGet cache, so
// `Clink.exe --unittest [nameFilter]` finds every [UnitTest] method in namespace Clink.Tests by reflection, runs them one after
// the other on the UI thread, prints PASS / FAIL per test and exits 0 only when all passed. Each area adds its own
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

/// <summary>Assertions. Every check takes an optional <c>what</c> that is put in front of the failure message.</summary>
internal static class Check
{
    public static void Fail(string message) => throw new TestFailure(message);

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

internal static class TestRunner
{
    private const string TestNamespace = "Clink.Tests";

    /// <summary>
    /// Runs the tests whose "Class.Method" name contains the filter (the first argument after <c>--unittest</c> that is not a
    /// switch; no filter runs all), prints one PASS / FAIL line per test and a summary, and returns the number of tests that
    /// failed (1 when the filter matched nothing, so a mistyped filter is not a pass).
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
        int passed = 0, failed = 0;
        var total = Stopwatch.StartNew();
        try
        {
            foreach (var (name, method) in tests)
            {
                ResetShared(before, scratch);
                var sw = Stopwatch.StartNew();
                string? problem = Invoke(method);
                ResetShared(before, scratch);
                if (problem == null) { passed++; Print($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)"); }
                else { failed++; Print($"FAIL  {name}  {problem}"); }
            }
        }
        finally
        {
            ResetShared(before, null);
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
        Print($"{passed} passed, {failed} failed ({total.ElapsedMilliseconds} ms)");
        return failed;
    }

    /// <summary>The process-wide switches a test could change and forget: where the settings, the clip index, the updates, the real Clink's folder and the other apps' packages live, and the --dev state.</summary>
    private sealed record Shared(string? SettingsDir, string? ClipIndex, string? UpdatesDir, string InstanceSuffix, bool DevHotkeys, bool DevAcceptImport, bool DevToken, bool DevRecord,
        string? RealDir, string? PackagesRoot)
    {
        public static Shared Capture() => new(Settings.DirOverride, ClipLibrary.IndexPathOverride, Updater.UpdatesDirOverride,
            AppController.InstanceSuffix, AppController.DevHotkeys, AppController.DevAcceptImport, AppController.DevToken, AppController.DevRecord,
            Settings.RealDirOverride, SettingsImport.PackagesRoot);
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
        Settings.DirOverride = scratch ?? before.SettingsDir;
        Settings.RealDirOverride = scratch != null ? Path.Combine(scratch, "RealClink") : before.RealDir;
        ClipLibrary.IndexPathOverride = scratch != null ? Path.Combine(scratch, "clips.json") : before.ClipIndex;
        Updater.UpdatesDirOverride = scratch != null ? Path.Combine(scratch, "updates") : before.UpdatesDir;
        Settings.ThawSaves();
        SettingsImport.Reset();
        SettingsImport.PackagesRoot = scratch != null ? Path.Combine(scratch, "Packages") : before.PackagesRoot;
        AppDataRedirect.Reset();
    }

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

    /// <summary>Runs one test; null when it passed, otherwise the reason it failed (one line).</summary>
    private static string? Invoke(MethodInfo method)
    {
        if (!method.IsStatic || method.GetParameters().Length != 0 || method.ReturnType != typeof(void))
            return "a test must be `public static void Name()` with no parameters";
        try
        {
            method.Invoke(null, null);
            return null;
        }
        catch (TargetInvocationException tie)
        {
            var ex = tie.InnerException ?? tie;
            if (ex is TestFailure) return ex.Message;
            // Anything else is a bug in the code under test or in the test: show where.
            string where = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return $"{ex.GetType().Name}: {ex.Message} {where}".Replace('\r', ' ').Replace('\n', ' ');
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void Print(string line)
    {
        try { Console.WriteLine(line); } catch { }
        Log.Info("[unittest] " + line);
    }
}
