using System.IO;
using System.Reflection;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Reading a settings file that holds things that make no sense: a NaN is put right when it is read, a hotkey row that cannot be read costs only itself.</summary>
internal static class SettingsToleranceTests
{
    private static Settings LoadFrom(TempDir dir, string? json)
    {
        Settings.DirOverride = dir.Path;
        if (json != null) File.WriteAllText(dir.File("settings.json"), json);
        return Settings.Load();
    }

    private static string[] Rows(Settings s) =>
        s.Hotkeys.Select(h => $"{(h.Keys.Vk > 0 ? h.Keys.ToString() : "unset")} {h.Action} {h.Seconds}").ToArray();

    private static List<string> Logged(Action action)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => lines.Enqueue(line);
        Log.Line += Collect;
        try { action(); }
        finally { Log.Line -= Collect; }
        return lines.ToList();
    }

    // ------------------------------------------------------------------ NaN

    private const string NaNFile = """
        { "WindowX": "NaN", "WindowY": "Infinity", "WindowWidth": "-Infinity", "WindowHeight": "NaN", "HdrKnee": "NaN", "HdrPeak": "Infinity",
          "KeyframeSeconds": "NaN", "ClickVolume": "NaN", "ArchiveWhenLocalExceedsGB": "Infinity", "Fps": 30 }
        """;

    /// <summary>Every floating-point setting is a finite number (or null), found by reflection, so a setting added later is checked too.</summary>
    private static void AssertNoNonFinite(Settings s)
    {
        foreach (var p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            switch (p.GetValue(s))
            {
                case double d: Check.True(double.IsFinite(d), p.Name); break;
                case float f: Check.True(float.IsFinite(f), p.Name); break;
            }
        }
    }

    private static void AssertClean(Settings s)
    {
        AssertNoNonFinite(s);
        Check.Null(s.WindowX);
        Check.Null(s.WindowY);
        Check.Null(s.WindowWidth);
        Check.Null(s.WindowHeight);
        Check.Equal(0.75f, s.HdrKnee);
        Check.Equal(4f, s.HdrPeak);
        Check.Equal(1f, s.KeyframeSeconds);
        Check.Equal(0.5, s.ClickVolume);
        Check.Equal(0.0, s.ArchiveWhenLocalExceedsGB);
        Check.Equal(30, s.Fps, "the rest of the file is read as it is");
    }

    [UnitTest]
    public static void A_NaN_in_a_file_is_put_right_when_it_is_loaded()
    {
        // BuildConfig, the encoder and the window read the settings straight after Load: a NaN must not get that far
        using var dir = new TempDir();
        Settings s = null!;
        var logged = Logged(() => s = LoadFrom(dir, NaNFile));
        AssertClean(s);
        Check.Null(s.LoadProblem, "it is not a complaint for the user: the value is simply its default");
        Check.True(logged.Any(l => l.Contains("HdrKnee was not a number")), "but it is in the log");
        Check.Contains(File.ReadAllText(dir.File("settings.json")), "NaN", "the file itself is left alone until the next save");
        s.Save();
        Check.DoesNotContain(File.ReadAllText(dir.File("settings.json")), "NaN");
        AssertClean(Settings.Load());
    }

    [UnitTest]
    public static void A_NaN_in_a_file_is_put_right_when_it_is_read_for_a_comparison_too()
    {
        var logged = Logged(() => AssertClean(Settings.FromJson(NaNFile)));
        Check.Equal(0, logged.Count, "FromJson says nothing in the log");
        // so two copies are compared as they would be once loaded: a NaN is the default, not a difference
        Check.Equal(0, SettingsImport.Differences(NaNFile, """{ "Fps": 30 }""").Count);
        Check.Equal(0, SettingsImport.Differences("""{ "Fps": 30 }""", NaNFile).Count);
    }

    // ------------------------------------------------------------------ hotkey rows

    private const string Good = """{ "Keys": { "Vk": 120 }, "Action": "clip", "Seconds": 0 }""";
    private const string Bad = """{ "Keys": { "Vk": "x" }, "Action": "clip", "Seconds": 10 }""";
    private const string Good30 = """{ "Keys": { "Shift": true, "Vk": 121 }, "Action": "clip", "Seconds": 30 }""";

    [UnitTest]
    public static void A_hotkey_row_that_cannot_be_read_costs_only_that_row()
    {
        using var dir = new TempDir();
        string file = $$"""{ "SchemaVersion": 3, "Codec": "hevc", "Hotkeys": [ {{Good}}, {{Bad}}, {{Good30}} ] }""";
        Settings s = null!;
        var logged = Logged(() => s = LoadFrom(dir, file));

        Check.Equal(new[] { "F9 clip 0", "Shift+F10 clip 30" }, Rows(s), "the rows around it are kept, in order");
        Check.Equal(new[] { "Hotkeys row 2" }, s.UnreadableValues.ToArray(), "and it is listed as a row, not as the whole list");
        Check.Equal("settings.json: left out hotkey row 2 (unreadable); the original is kept as settings.json.unreadable", s.LoadProblem);
        Check.False(s.FileWasSkipped);
        Check.Equal("hevc", s.Codec);
        Check.False(s.MainHotkeyAssigned || s.MainHotkeyNeedsSetup, "the main hotkey is the one the file has");
        Check.True(logged.Any(l => l.Contains("hotkey row 2 could not be read") && l.Contains("the other rows are kept")), "logged");
        Check.Equal(file, File.ReadAllText(dir.File("settings.json.unreadable")), "the original is kept");

        s.Save();   // the saved file no longer has the row
        var again = Settings.Load();
        Check.Null(again.LoadProblem);
        Check.Equal(new[] { "F9 clip 0", "Shift+F10 clip 30" }, Rows(again));
    }

    [UnitTest]
    public static void Several_unreadable_hotkey_rows_and_unreadable_values_are_each_said()
    {
        using var dir = new TempDir();
        // a null row is no row (it always was dropped, without a word); a number is a row that cannot be read
        string file = $$"""{ "Fps": "sixty", "Hotkeys": [ {{Bad}}, {{Good}}, 5, null, {{Good30}} ] }""";
        var s = LoadFrom(dir, file);
        Check.Equal(new[] { "F9 clip 0", "Shift+F10 clip 30" }, Rows(s));
        Check.Equal(new[] { "Fps", "Hotkeys row 1", "Hotkeys row 3" }, s.UnreadableValues.ToArray());
        Check.Equal(60, s.Fps);
        Check.Equal("settings.json: kept the defaults for Fps (unreadable values); left out hotkey rows 1, 3 (unreadable); the original is kept as settings.json.unreadable", s.LoadProblem);
    }

    [UnitTest]
    public static void A_hotkey_list_with_no_row_that_can_be_read_is_an_empty_list_and_a_list_that_is_no_list_is_unreadable()
    {
        using var dir = new TempDir();
        var allBad = LoadFrom(dir, $$"""{ "Hotkeys": [ {{Bad}}, "x" ] }""");
        Check.Equal(new[] { "Hotkeys row 1", "Hotkeys row 2" }, allBad.UnreadableValues.ToArray());
        Check.Equal(new[] { "unset clip 0" }, Rows(allBad), "nothing is bound for someone whose hotkeys are not known");
        Check.True(allBad.MainHotkeyNeedsSetup);
        Check.False(allBad.MainHotkeyAssigned);

        using var dir2 = new TempDir();
        var notAList = LoadFrom(dir2, """{ "Hotkeys": 5, "Codec": "hevc" }""");
        Check.Equal(new[] { "Hotkeys" }, notAList.UnreadableValues.ToArray(), "a value that is not a list is still the whole setting");
        Check.Equal("settings.json: kept the defaults for Hotkeys (unreadable values); the original is kept as settings.json.unreadable", notAList.LoadProblem);
        Check.Equal(new[] { "unset clip 0" }, Rows(notAList));
        Check.Equal("hevc", notAList.Codec);
    }

    [UnitTest]
    public static void An_older_file_keeps_its_other_rows_when_the_first_one_cannot_be_read()
    {
        using var dir = new TempDir();
        // a v0.2 file (no SchemaVersion): the first row that saves the full replay and has keys is the main
        string file = $$"""{ "Hotkeys": [ {{Bad}}, {{Good30}}, {{Good}} ] }""";
        var s = LoadFrom(dir, file);
        Check.Equal(new[] { "F9 clip 0", "Shift+F10 clip 30" }, Rows(s));
        Check.Equal(new[] { "Hotkeys row 1" }, s.UnreadableValues.ToArray());
    }

    [UnitTest]
    public static void A_hotkey_row_that_cannot_be_read_in_a_copy_does_not_stop_the_comparison()
    {
        string theirs = $$"""{ "SchemaVersion": 3, "Hotkeys": [ {{Good}}, {{Bad}} ] }""";
        string ours = $$"""{ "SchemaVersion": 3, "Hotkeys": [ {{Good}} ] }""";
        var logged = Logged(() => Check.Equal(0, SettingsImport.Differences(ours, theirs).Count));
        Check.Equal(0, logged.Count, "a comparison logs nothing");
        Check.Equal(new[] { "hotkeys" }, SettingsImport.Differences(ours, $$"""{ "SchemaVersion": 3, "Hotkeys": [ {{Good}}, {{Bad}}, {{Good30}} ] }""").ToArray());
    }
}
