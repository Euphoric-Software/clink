using System.IO;
using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// What an import does with the other copy's clip list (merged, or not merged and said so, and tried again at every start), what it
/// keeps of this installation's own values and of a Copium token that cannot be used here, and how it keeps what it replaces.
/// </summary>
internal static class ImportMergeTests
{
    // ------------------------------------------------------------------ helpers

    private static string FakeCopy(TempDir root, string package, string settingsJson, string? clipsJson = null)
    {
        string folder = root.File(Path.Combine("Packages", package, "LocalCache", "Roaming", "Clink"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), settingsJson);
        if (clipsJson != null) File.WriteAllText(Path.Combine(folder, "clips.json"), clipsJson);
        return folder;
    }

    private static ForeignSettings Copy(TempDir root) => SettingsImport.FindForeignCopies(root.Path).Single();

    private static Settings LoadFrom(TempDir dir, string? json)
    {
        Settings.DirOverride = dir.Path;
        if (json != null) File.WriteAllText(dir.File("settings.json"), json);
        return Settings.Load();
    }

    private static string Json(Action<Settings>? change = null)
    {
        var s = Settings.FromJson("{}");
        change?.Invoke(s);
        return s.ToJson();
    }

    private static List<string> Logged(Action action)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => lines.Enqueue(line);
        Log.Line += Collect;
        try { action(); }
        finally { Log.Line -= Collect; }
        return lines.ToList();
    }

    private static string[] PathsIn(string clipsJsonFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(clipsJsonFile));
        return doc.RootElement.EnumerateArray().Select(c => c.GetProperty("Path").GetString()!).ToArray();
    }

    private const string TheirClip = """[ { "Path": "C:\\clips\\theirs.mp4", "CopiumLink": "https://theirs/t" } ]""";

    // ------------------------------------------------------------------ reading ours: the backup, then a reason

    [UnitTest]
    public static void Our_clip_list_is_read_from_its_backup_when_it_is_damaged()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json()).Save();
        ClipLibrary.IndexPathOverride = home.File("clips.json");
        File.WriteAllText(home.File("clips.json"), "{ damaged");
        File.WriteAllText(home.File("clips.json.bak"), """[ { "Path": "C:\\clips\\mine.mp4", "Size": 1 } ]""");
        FakeCopy(root, "Foo_abc", Json(s => s.Codec = "hevc"), TheirClip);

        var result = SettingsImport.Import(Copy(root));

        Check.Null(result.ClipsProblem, "the backup was good enough");
        Check.Null(result.ClipsNotice);
        Check.Null(SettingsImport.PendingClipMerge());
        Check.Null(SettingsImport.TakeClipsNotice());
        Check.Equal(new[] { @"C:\clips\mine.mp4", @"C:\clips\theirs.mp4" }, PathsIn(home.File("clips.json")));
        Check.Equal("{ damaged", File.ReadAllText(home.File("clips.json.unreadable")), "and what was damaged is kept");
    }

    [UnitTest]
    public static void A_clip_list_that_cannot_be_merged_is_said_logged_and_tried_again_at_every_start()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json()).Save();
        ClipLibrary.IndexPathOverride = home.File("clips.json");
        File.WriteAllText(home.File("clips.json"), "{ damaged");   // and no backup
        string theirsSettings = Json(s => s.Codec = "hevc");
        string folder = FakeCopy(root, "Foo_abc", theirsSettings, TheirClip);
        var copy = Copy(root);

        ImportResult result = null!;
        var logged = Logged(() => result = SettingsImport.Import(copy));

        // the settings are imported all the same, and the user is told what did not work
        Check.Equal("hevc", Settings.Load().Codec);
        Check.NotNull(result.ClipsProblem);
        Check.Contains(result.ClipsProblem, "your clip list cannot be read");
        Check.Equal("Settings imported; the clip list could not be merged: " + result.ClipsProblem, result.ClipsNotice);
        Check.Equal(result.ClipsNotice, SettingsImport.TakeClipsNotice(), "AppController.Start says it");
        Check.Null(SettingsImport.TakeClipsNotice(), "once");
        Check.True(logged.Any(l => l.Contains("the clip list was not merged") && l.Contains("your clip list cannot be read")), "and the log has the reason");
        Check.Equal("{ damaged", File.ReadAllText(home.File("clips.json")), "ours is not replaced by a guess");

        // a marker says "clips merge pending from <folder>"
        var pending = SettingsImport.PendingClipMerge();
        Check.NotNull(pending);
        Check.Equal(folder, pending!.Value.From);
        Check.Equal(1, pending.Value.Attempts);
        Check.Contains(pending.Value.Reason, "your clip list cannot be read");
        Check.True(File.Exists(SettingsImport.ClipsMarkerPath));

        // the settings no longer differ from the other copy's, so nothing would ever offer it again: the retry does not depend on that
        Check.Equal(0, SettingsImport.Differences(File.ReadAllText(home.File("settings.json")), theirsSettings).Count);
        SettingsImport.PackagesRoot = root.File("Packages");
        SettingsImport.Scan(Settings.Load()).Wait();
        Check.Null(SettingsImport.Pending, "the copy is there, and the same");

        // the next start: ours is still damaged, so it is said again (the import's own notice died with the process that restarted), once
        SettingsImport.RetryPendingClipMerge();
        Check.Contains(SettingsImport.TakeClipsNotice(), "the clip list could not be merged");
        Check.Equal(2, SettingsImport.PendingClipMerge()!.Value.Attempts);
        SettingsImport.RetryPendingClipMerge();
        Check.Null(SettingsImport.TakeClipsNotice(), "the starts after that only log");
        Check.Equal(3, SettingsImport.PendingClipMerge()!.Value.Attempts);

        // ours is back in order: the next start finishes the merge, and nothing is left to say
        File.WriteAllText(home.File("clips.json"), """[ { "Path": "C:\\clips\\mine.mp4" } ]""");
        var finished = Logged(SettingsImport.RetryPendingClipMerge);
        Check.Null(SettingsImport.PendingClipMerge());
        Check.False(File.Exists(SettingsImport.ClipsMarkerPath));
        Check.Null(SettingsImport.TakeClipsNotice());
        Check.True(finished.Any(l => l.Contains("finished the clip merge")));
        Check.Equal(new[] { @"C:\clips\mine.mp4", @"C:\clips\theirs.mp4" }, PathsIn(home.File("clips.json")));
        // and a start with nothing pending does nothing
        SettingsImport.RetryPendingClipMerge();
        Check.Null(SettingsImport.TakeClipsNotice());
    }

    [UnitTest]
    public static void A_pending_clip_merge_ends_when_the_other_copy_has_no_clip_list_any_more()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json()).Save();
        ClipLibrary.IndexPathOverride = home.File("clips.json");
        File.WriteAllText(home.File("clips.json"), "{ damaged");
        string folder = FakeCopy(root, "Foo_abc", Json(), TheirClip);
        SettingsImport.Import(Copy(root));
        Check.NotNull(SettingsImport.PendingClipMerge());
        SettingsImport.TakeClipsNotice();

        File.Delete(Path.Combine(folder, "clips.json"));
        SettingsImport.RetryPendingClipMerge();
        Check.Null(SettingsImport.PendingClipMerge(), "there is nothing to merge from");
        Check.Null(SettingsImport.TakeClipsNotice());

        // and a later import that does merge clears a marker an earlier one left
        File.WriteAllText(home.File("clips.json"), "{ damaged");
        SettingsImport.Import(Copy(root));   // no clips.json in the copy now: nothing to merge, nothing pending
        Check.Null(SettingsImport.PendingClipMerge());
        File.WriteAllText(Path.Combine(folder, "clips.json"), TheirClip);
        SettingsImport.Import(Copy(root));   // damaged again: pending
        Check.NotNull(SettingsImport.PendingClipMerge());
        File.WriteAllText(home.File("clips.json"), "[]");
        SettingsImport.Import(Copy(root));   // fine now
        Check.Null(SettingsImport.PendingClipMerge());
    }

    [UnitTest]
    public static void A_clip_list_the_other_copy_cannot_give_is_a_reason_too()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json()).Save();
        ClipLibrary.IndexPathOverride = home.File("clips.json");
        File.WriteAllText(home.File("clips.json"), """[ { "Path": "C:\\clips\\mine.mp4" } ]""");
        string folder = FakeCopy(root, "Foo_abc", Json(), "{ not json");

        var damaged = SettingsImport.Import(Copy(root));
        Check.Contains(damaged.ClipsProblem, "the other copy's clip list cannot be read");
        Check.True(damaged.ClipsProblem!.Contains("line", StringComparison.OrdinalIgnoreCase), "it says where: " + damaged.ClipsProblem);

        File.WriteAllText(Path.Combine(folder, "clips.json"), """{ "Path": "x" }""");
        var notAList = SettingsImport.Import(Copy(root));
        Check.Contains(notAList.ClipsProblem, "the other copy's clip list cannot be read");
        Check.Contains(notAList.ClipsProblem, "JSON array");
        Check.Equal(new[] { @"C:\clips\mine.mp4" }, PathsIn(home.File("clips.json")), "ours is left as it was");
        Check.Equal(1, SettingsImport.PendingClipMerge()!.Value.Attempts);

        // no clip list at all is nothing to merge, not a failure
        File.Delete(Path.Combine(folder, "clips.json"));
        var none = SettingsImport.Import(Copy(root));
        Check.Null(none.ClipsProblem);
        Check.Null(SettingsImport.PendingClipMerge());
    }

    [UnitTest]
    public static void A_file_over_the_size_limit_is_skipped_and_the_skip_is_logged()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json()).Save();
        ClipLibrary.IndexPathOverride = home.File("clips.json");
        File.WriteAllText(home.File("clips.json"), """[ { "Path": "C:\\clips\\mine.mp4" } ]""");
        string big = "[" + new string(' ', 4_200_000) + "]";
        string folder = FakeCopy(root, "Foo_abc", Json(s => s.Codec = "hevc"), big);

        ImportResult result = null!;
        var logged = Logged(() => result = SettingsImport.Import(Copy(root)));
        Check.Equal("hevc", Settings.Load().Codec, "the settings are imported");
        Check.Contains(result.ClipsProblem, "too big");
        Check.Contains(result.ClipsProblem, "4,000,000");
        Check.True(logged.Any(l => l.Contains("the clip list was not merged") && l.Contains("too big")), "logged, not silent");
        Check.Equal(new[] { @"C:\clips\mine.mp4" }, PathsIn(home.File("clips.json")));

        // a settings.json that big is not looked at either, and that is logged
        string bigSettings = "{" + new string(' ', 4_100_000) + "}";
        FakeCopy(root, "Huge_1", bigSettings);
        var found = new List<ForeignSettings>();
        var listLog = Logged(() => found.AddRange(SettingsImport.FindForeignCopies(root.Path)));
        Check.Equal(new[] { "Foo_abc" }, found.Select(f => f.Package).ToArray());
        Check.True(listLog.Any(l => l.Contains("Huge_1") && l.Contains("skipped") && l.Contains("4,100")), "the skipped settings.json is logged");
        _ = folder;
    }

    [UnitTest]
    public static void Merging_says_what_it_leaves_out()
    {
        const string ours = """[ { "Path": "C:\\a.mp4", "Size": 1 }, 5, { "Path": "c:\\A.mp4" }, [1], { "Size": 9 } ]""";
        const string theirs = """[ { "Size": 3 }, "x", { "Path": "C:\\b.mp4" } ]""";
        var skipped = new List<string>();
        using var doc = JsonDocument.Parse(SettingsImport.MergeClips(ours, theirs, skipped));
        Check.Equal(new[] { @"C:\a.mp4", @"C:\b.mp4" }, doc.RootElement.EnumerateArray().Where(c => c.TryGetProperty("Path", out _)).Select(c => c.GetProperty("Path").GetString()!).ToArray());
        Check.Equal(3, doc.RootElement.GetArrayLength(), "a, b, and our own entry without a path");
        string all = string.Join(" | ", skipped);
        Check.Contains(all, "your clip list has 2 entries that are not a clip: left out");
        Check.Contains(all, "your clip list names 1 clip twice: the repeats are dropped");
        Check.Contains(all, "the other copy's clip list has 1 entry that is not a clip: left out");
        Check.Contains(all, "the other copy's clip list has 1 entry without a path: left out");

        var quiet = new List<string>();
        SettingsImport.MergeClips("[]", """[ { "Path": "C:\\c.mp4" } ]""", quiet);
        Check.Equal(0, quiet.Count, "nothing is left out, nothing is said");
    }

    [UnitTest]
    public static void Clip_paths_are_compared_as_the_file_system_does()
    {
        const string ours = """[ { "Path": "C:\\clips\\a.mp4", "Size": 100 }, { "Path": "\\\\nas\\clips\\b.mp4", "Size": 5 } ]""";
        const string theirs = """
            [
              { "Path": "c:/CLIPS/a.mp4", "CopiumLink": "https://slash" },
              { "Path": "C:\\clips\\sub\\..\\a.mp4", "CopiumId": "dots" },
              { "Path": "C:\\clips\\\\a.mp4", "Game": "doubled" },
              { "Path": "\\\\?\\C:\\clips\\a.mp4", "CopiumPageUrl": "https://prefix" },
              { "Path": "C:\\clips\\a.mp4\\", "AudioTracks": ["Mix"] },
              { "Path": "\\\\?\\UNC\\nas\\clips\\b.mp4", "CopiumLink": "https://unc" },
              { "Path": "Z:\\b.mp4", "CopiumLink": "https://mapped" }
            ]
            """;
        using var doc = JsonDocument.Parse(SettingsImport.MergeClips(ours, theirs));
        var clips = doc.RootElement.EnumerateArray().ToList();
        Check.Equal(3, clips.Count, "a once, b once, and the mapped drive, which stays a different path");
        var a = clips[0];
        Check.Equal(100, a.GetProperty("Size").GetInt32(), "ours first, ours wins");
        Check.Equal("https://slash", a.GetProperty("CopiumLink").GetString());
        Check.Equal("dots", a.GetProperty("CopiumId").GetString());
        Check.Equal("doubled", a.GetProperty("Game").GetString());
        Check.Equal("https://prefix", a.GetProperty("CopiumPageUrl").GetString());
        Check.Equal("Mix", a.GetProperty("AudioTracks")[0].GetString());
        Check.Equal("https://unc", clips[1].GetProperty("CopiumLink").GetString(), @"\\?\UNC\nas\... is \\nas\...");
        Check.Equal("Z:\\b.mp4", clips[2].GetProperty("Path").GetString(), "a mapped drive for a network path may stay a duplicate");

        Check.Equal(@"C:\clips\a.mp4", SettingsImport.NormalizePath("c:/clips//sub/../a.mp4/").Replace("c:", "C:"));
        Check.Equal(@"\\nas\clips\b.mp4", SettingsImport.NormalizePath(@"\\?\UNC\nas\clips\b.mp4"));
        Check.Equal(@"C:\", SettingsImport.NormalizePath("C:/"), "a root keeps its separator");
    }

    // ------------------------------------------------------------------ this installation's own values

    [UnitTest]
    public static void An_import_keeps_this_installations_own_values()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json(s =>
        {
            s.Codec = "h264";
            s.WindowX = 100; s.WindowY = 200; s.WindowWidth = 900; s.WindowHeight = 700;
            s.LastUpdateCheck = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
            s.UpdateRepo = "me/mine"; s.PendingUpdateVersion = "0.2.9"; s.PendingUpdateSha256 = new string('a', 64);
            s.ImportDeclined = "Claude_x|5";
        })).Save();
        string theirsText = Json(s =>
        {
            s.Codec = "hevc"; s.BufferSeconds = 48; s.Theme = "light";
            s.WindowX = -1500; s.WindowY = 350; s.WindowWidth = 1346; s.WindowHeight = 793;
            s.LastUpdateCheck = new DateTime(2026, 10, 21, 18, 32, 30, DateTimeKind.Utc);
            s.UpdateRepo = "them/theirs"; s.PendingUpdateVersion = "0.2.4"; s.PendingUpdateSha256 = new string('0', 64);
            s.ImportDeclined = "Other_y|9";
        }).Replace("\"SchemaVersion\"", "\"FutureKey\": { \"a\": 1 },\n  \"SchemaVersion\"");
        FakeCopy(root, "Foo_abc", theirsText);

        SettingsImport.Import(Copy(root));

        var after = Settings.Load();
        Check.Equal("hevc", after.Codec, "everything else is theirs");
        Check.Equal(48, after.BufferSeconds);
        Check.Equal("light", after.Theme);
        Check.Equal(1, after.Unknown!["FutureKey"].GetProperty("a").GetInt32(), "keys this version does not know come along");
        Check.Equal((double?)100, after.WindowX);
        Check.Equal((double?)200, after.WindowY);
        Check.Equal((double?)900, after.WindowWidth);
        Check.Equal((double?)700, after.WindowHeight);
        Check.Equal(new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), after.LastUpdateCheck!.Value.ToUniversalTime());
        Check.Equal("me/mine", after.UpdateRepo);
        Check.Equal("0.2.9", after.PendingUpdateVersion);
        Check.Equal(new string('a', 64), after.PendingUpdateSha256);
        Check.Equal("Claude_x|5", after.ImportDeclined, "which copies were turned down is ours");

        // the same copy is not offered again for the values it was not allowed to change
        SettingsImport.PackagesRoot = root.File("Packages");
        SettingsImport.Scan(after).Wait();
        Check.Null(SettingsImport.Pending);
        Check.Equal(0, SettingsImport.Differences(File.ReadAllText(home.File("settings.json")), theirsText).Count);
    }

    [UnitTest]
    public static void A_value_of_ours_that_does_not_exist_is_not_taken_from_the_other_copy()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json()).Save();   // never moved the window, no update, no repository
        FakeCopy(root, "Foo_abc", Json(s =>
        {
            s.Codec = "hevc";
            s.WindowX = -1500; s.WindowWidth = 1346;
            s.LastUpdateCheck = new DateTime(2026, 10, 21, 18, 32, 30, DateTimeKind.Utc);
            s.UpdateRepo = "them/theirs"; s.PendingUpdateVersion = "0.2.4"; s.PendingUpdateSha256 = new string('0', 64);
            s.ImportDeclined = "Other_y|9";
        }));
        SettingsImport.Import(Copy(root));
        var after = Settings.Load();
        Check.Equal("hevc", after.Codec);
        Check.Null(after.WindowX);
        Check.Null(after.WindowWidth);
        Check.Null(after.LastUpdateCheck);
        Check.Null(after.UpdateRepo);
        Check.Null(after.PendingUpdateVersion);
        Check.Null(after.PendingUpdateSha256);
        Check.Null(after.ImportDeclined);

        // a first start (no settings at all) takes none of them either
        using var fresh = new TempDir();
        Settings.DirOverride = fresh.Path;
        SettingsImport.PackagesRoot = root.File("Packages");
        Check.NotNull(SettingsImport.ImportNewest());
        var first = Settings.Load();
        Check.Equal("hevc", first.Codec);
        Check.Null(first.WindowX);
        Check.Null(first.UpdateRepo);
        Check.Null(first.PendingUpdateVersion);
    }

    [UnitTest]
    public static void KeepOurOwn_changes_nothing_when_the_other_copy_agrees_and_the_text_is_then_theirs_as_it_is()
    {
        string theirs = "{ \"Fps\": 30,\n   \"WindowX\": 10 }";   // formatted its own way
        Check.Equal(theirs, SettingsImport.KeepOurOwn(theirs, "{ \"WindowX\": 10 }", keepOurToken: false));
        using (var none = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, null, keepOurToken: false)))
        {
            Check.Equal(30, none.RootElement.GetProperty("Fps").GetInt32());
            Check.False(none.RootElement.TryGetProperty("WindowX", out _), "with no settings of ours, theirs' WindowX goes");
        }
        string changed = SettingsImport.KeepOurOwn(theirs, "{ \"WindowX\": 99, \"windowy\": 5, \"Fps\": 120 }", keepOurToken: false);
        using var doc = JsonDocument.Parse(changed);
        Check.Equal(30, doc.RootElement.GetProperty("Fps").GetInt32());
        Check.Equal(99, doc.RootElement.GetProperty("WindowX").GetInt32());
        Check.Equal(5, doc.RootElement.GetProperty("windowy").GetInt32(), "a key of ours that theirs lacks is added in our spelling");
        // not a JSON object (the caller has refused that already): returned as it is
        Check.Equal("[1]", SettingsImport.KeepOurOwn("[1]", "{}", keepOurToken: false));
    }

    // ------------------------------------------------------------------ what an import replaces is kept

    [UnitTest]
    public static void The_replaced_settings_are_never_written_over_an_earlier_copy()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json(s => s.Fps = 30)).Save();
        File.WriteAllText(home.File("settings.pre-import.json"), "precious: an earlier import's copy");
        FakeCopy(root, "Foo_abc", Json(s => s.Fps = 120));
        var copy = Copy(root);

        string first = File.ReadAllText(home.File("settings.json"));
        var one = SettingsImport.Import(copy);
        Check.Equal("precious: an earlier import's copy", File.ReadAllText(home.File("settings.pre-import.json")), "never overwritten");
        Check.Equal(home.File("settings.pre-import.1.json"), one.BackupPath);
        Check.Equal(first, File.ReadAllText(home.File("settings.pre-import.1.json")));

        string second = File.ReadAllText(home.File("settings.json"));
        File.WriteAllText(copy.SettingsPath, Json(s => s.Fps = 15));
        var two = SettingsImport.Import(copy);
        Check.Equal(home.File("settings.pre-import.2.json"), two.BackupPath);
        Check.Equal(second, File.ReadAllText(home.File("settings.pre-import.2.json")));
        Check.Equal(first, File.ReadAllText(home.File("settings.pre-import.1.json")), "nor the one before");

        // a name taken in between is skipped, not used
        File.WriteAllText(home.File("settings.pre-import.3.json"), "taken");
        File.WriteAllText(copy.SettingsPath, Json(s => s.Fps = 24));
        var three = SettingsImport.Import(copy);
        Check.Equal(home.File("settings.pre-import.4.json"), three.BackupPath);
        Check.Equal("taken", File.ReadAllText(home.File("settings.pre-import.3.json")));
        Check.Equal(24, Settings.Load().Fps);

        // a first start has nothing to keep
        using var fresh = new TempDir();
        Settings.DirOverride = fresh.Path;
        Check.Null(SettingsImport.Import(copy).BackupPath);
        Check.Equal(0, Directory.GetFiles(fresh.Path, "settings.pre-import*").Length);
    }

    [UnitTest]
    public static void Settings_that_cannot_be_kept_first_stop_the_import_before_anything_changes()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json(s => s.Fps = 30)).Save();
        string before = File.ReadAllText(home.File("settings.json"));
        FakeCopy(root, "Foo_abc", Json(s => s.Fps = 120), TheirClip);
        var copy = Copy(root);
        ClipLibrary.IndexPathOverride = home.File("clips.json");

        using (new FileStream(home.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))   // locked: cannot be copied
        {
            var ex = Check.Throws<IOException>(() => SettingsImport.Import(copy));
            Check.Contains(ex.Message, "could not be kept first");
        }
        Check.Equal(before, File.ReadAllText(home.File("settings.json")), "nothing was replaced");
        Check.Equal(0, Directory.GetFiles(home.Path, "settings.pre-import*").Length);
        Check.False(File.Exists(home.File("clips.json")), "nor merged");
        Check.Null(SettingsImport.PendingClipMerge());
        Check.Equal(30, Settings.Load().Fps);
    }

    // ------------------------------------------------------------------ a Copium token that cannot be used here

    private static readonly string Garbage = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });   // not DPAPI data

    [UnitTest]
    public static void An_incoming_Copium_token_that_cannot_be_decrypted_is_named_and_never_offered_on_its_own()
    {
        var mine = Settings.FromJson("{}");
        mine.CopiumToken = "ours-token";
        mine.Codec = "h264";
        string ours = mine.ToJson();
        string theirs = Json(s => { s.Codec = "hevc"; s.CopiumTokenProtected = Garbage; });

        var differences = SettingsImport.Differences(ours, theirs);
        Check.Equal(new[] { "video quality", SettingsImport.UnusableTokenNote }, differences.ToArray());
        Check.Equal("the incoming Copium token cannot be used on this machine", SettingsImport.UnusableTokenNote);
        Check.False(differences.Contains("Copium token"), "it is not said to differ");

        // beside no other difference it is nothing to offer: an import would only keep ours, and the copy would come back for ever
        Check.Equal(0, SettingsImport.Differences(ours, Json(s => { s.Codec = "h264"; s.CopiumTokenProtected = Garbage; })).Count);
        // with no token of ours at all it is still said, beside a real difference
        Check.Equal(new[] { "video quality", SettingsImport.UnusableTokenNote }, SettingsImport.Differences(Json(), theirs).ToArray());

        // a token that decrypts is compared as before
        var theirsGood = Settings.FromJson("{}");
        theirsGood.CopiumToken = "their-token";
        theirsGood.Codec = "h264";
        Check.Equal(new[] { "Copium token" }, SettingsImport.Differences(ours, theirsGood.ToJson()).ToArray());
    }

    [UnitTest]
    public static void An_import_keeps_our_Copium_token_when_the_incoming_one_cannot_be_decrypted()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        var ours = LoadFrom(home, Json(s => { s.CopiumToken = "ours-token"; s.Codec = "h264"; }));
        ours.Save();
        string oursProtected = ours.CopiumTokenProtected!;
        FakeCopy(root, "Foo_abc", Json(s => { s.Codec = "hevc"; s.CopiumTokenProtected = Garbage; }));

        var logged = Logged(() => SettingsImport.Import(Copy(root)));

        var after = Settings.Load();
        Check.Equal("hevc", after.Codec, "the rest is imported");
        Check.Equal("ours-token", after.CopiumToken, "ours still works");
        Check.Equal(oursProtected, after.CopiumTokenProtected, "and is the very text it was");
        Check.False(logged.Any(l => l.Contains("ours-token") || l.Contains(oursProtected) || l.Contains(Garbage)), "no token in the log");

        // nothing of ours to keep: the garbage is not put in
        using var home2 = new TempDir();
        LoadFrom(home2, Json()).Save();
        SettingsImport.Import(Copy(root));
        var none = Settings.Load();
        Check.Null(none.CopiumTokenProtected);
        Check.Null(none.CopiumToken);
        Check.Equal("hevc", none.Codec);
    }

    [UnitTest]
    public static void An_incoming_Copium_token_that_decrypts_still_replaces_ours()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json(s => s.CopiumToken = "ours-token")).Save();
        FakeCopy(root, "Foo_abc", Json(s => s.CopiumToken = "their-token"));
        SettingsImport.Import(Copy(root));
        Check.Equal("their-token", Settings.Load().CopiumToken);

        // and an incoming copy with no token at all is the other copy's truth too
        File.WriteAllText(Copy(root).SettingsPath, Json());
        SettingsImport.Import(Copy(root));
        Check.Null(Settings.Load().CopiumToken);
    }
}
