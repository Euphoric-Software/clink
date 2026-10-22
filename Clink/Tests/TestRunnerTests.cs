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
