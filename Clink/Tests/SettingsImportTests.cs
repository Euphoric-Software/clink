using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Redirected AppData (a packaged app's private copy), finding another copy's settings, saying what they would change, taking them over.</summary>
internal static class SettingsImportTests
{
    private const string Redirected = @"\\?\C:\Users\Jason\AppData\Local\Packages\Contoso.Notes_pzs8sxrjxfjjc\LocalCache\Roaming\Clink\settings.json";

    // ------------------------------------------------------------------ helpers

    /// <summary>Lets what was queued for the UI thread (an event raised from a worker, a save asked for by another thread) run.</summary>
    private static void PumpUi()
    {
        var frame = new DispatcherFrame();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Writes a copy of Clink's files the way a packaged app keeps them: <c>&lt;root&gt;\Packages\&lt;package&gt;\LocalCache\Roaming\Clink</c>. Returns that folder.</summary>
    private static string FakeCopy(TempDir root, string package, string settingsJson, string? clipsJson = null, DateTime? savedUtc = null)
    {
        string folder = root.File(Path.Combine("Packages", package, "LocalCache", "Roaming", "Clink"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), settingsJson);
        if (clipsJson != null) File.WriteAllText(Path.Combine(folder, "clips.json"), clipsJson);
        if (savedUtc != null) File.SetLastWriteTimeUtc(Path.Combine(folder, "settings.json"), savedUtc.Value);
        return folder;
    }

    private static string PackagesOf(TempDir root) => root.File("Packages");

    private static Settings LoadFrom(TempDir dir, string? json)
    {
        Settings.DirOverride = dir.Path;
        if (json != null) File.WriteAllText(dir.File("settings.json"), json);
        return Settings.Load();
    }

    /// <summary>Settings text for a copy: the defaults, with a few values changed.</summary>
    private static string Json(Action<Settings>? change = null)
    {
        var s = Settings.FromJson("{}");
        change?.Invoke(s);
        return s.ToJson();
    }

    // ------------------------------------------------------------------ is AppData redirected?

    [UnitTest]
    public static void A_final_path_inside_a_packages_LocalCache_names_the_package()
    {
        Check.Equal("Contoso.Notes_pzs8sxrjxfjjc", AppDataRedirect.PackageFromFinalPath(Redirected));
        Check.Equal("Contoso.Notes_pzs8sxrjxfjjc", AppDataRedirect.PackageFromFinalPath(@"\\?\C:\Users\Jason\AppData\Local\Packages\Contoso.Notes_pzs8sxrjxfjjc\LocalCache\Local\Clink\updates\Clink-0.2.4.exe"));
        Check.Equal("contoso.notes_pzs8sxrjxfjjc", AppDataRedirect.PackageFromFinalPath(Redirected.ToLowerInvariant()), "Windows paths ignore case");
        Check.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe", AppDataRedirect.PackageFromFinalPath(@"\\?\D:\Users\x\AppData\Local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalCache\Roaming\Clink"));
    }

    [UnitTest]
    public static void An_ordinary_final_path_names_no_package()
    {
        Check.Null(AppDataRedirect.PackageFromFinalPath(@"\\?\C:\Users\x\AppData\Roaming\Clink\settings.json"));
        Check.Null(AppDataRedirect.PackageFromFinalPath(@"\\?\UNC\nas\home\x\AppData\Roaming\Clink\settings.json"));
        Check.Null(AppDataRedirect.PackageFromFinalPath(@"\\?\C:\Users\x\AppData\Local\Packages\Foo_abc\LocalState\Clink"), "only LocalCache is the private copy of AppData");
        Check.Null(AppDataRedirect.PackageFromFinalPath(@"\\?\C:\Users\x\AppData\Local\Packages\Foo_abc\LocalCache"), "the folder above Roaming and Local is not AppData");
        Check.Null(AppDataRedirect.PackageFromFinalPath(@"\\?\C:\Users\x\Videos\Packages\Foo_abc\LocalCache\Roaming\Clink"), "it has to be the user's AppData");
        Check.Null(AppDataRedirect.PackageFromFinalPath(""));
    }

    [UnitTest]
    public static void ExtendedPath_prefixes_drive_and_share_paths_and_leaves_prefixed_ones_alone()
    {
        Check.Equal(@"\\?\C:\a\b.txt", AppDataRedirect.ExtendedPath(@"C:\a\b.txt"));
        Check.Equal(@"\\?\C:\a\b.txt", AppDataRedirect.ExtendedPath(@"C:\a\x\..\b.txt"), "dots are resolved first, \\?\\ does not do it");
        Check.Equal(@"\\?\UNC\nas\share\a", AppDataRedirect.ExtendedPath(@"\\nas\share\a"));
        Check.Equal(@"\\?\C:\a", AppDataRedirect.ExtendedPath(@"\\?\C:\a"));
        Check.Equal(@"\\?\UNC\nas\share\a", AppDataRedirect.ExtendedPath(@"\\?\UNC\nas\share\a"));
        Check.Equal(@"\\.\pipe\x", AppDataRedirect.ExtendedPath(@"\\.\pipe\x"));
        Check.Equal(@"\??\C:\a", AppDataRedirect.ExtendedPath(@"\??\C:\a"), @"the NT form is not prefixed again (\\?\\??\...)");
        Check.True(AppDataRedirect.IsDevicePath(@"\??\C:\a") && AppDataRedirect.IsDevicePath("//?/C:/a") && AppDataRedirect.IsDevicePath(@"\\.\C:\a") && !AppDataRedirect.IsDevicePath(@"C:\a"));
        Check.Equal("", AppDataRedirect.ExtendedPath(""));
        string rel = AppDataRedirect.ExtendedPath(@"sub\f.txt");
        Check.Equal(@"\\?\" + Path.GetFullPath(@"sub\f.txt"), rel, "a relative path is made absolute");
        string longName = @"C:\" + string.Join(@"\", Enumerable.Repeat(new string('d', 50), 8)) + @"\f.txt";
        Check.Equal(@"\\?\" + longName, AppDataRedirect.ExtendedPath(longName), "past MAX_PATH it is only prefixed");
    }

    [UnitTest]
    public static void FinalFolderOf_and_ResolvedPath_work_past_MAX_PATH()
    {
        using var dir = new TempDir();
        string deep = dir.Path;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('p', 60));
        Directory.CreateDirectory(deep);
        string? folder = AppDataRedirect.FinalFolderOf(deep);
        Check.NotNull(folder, "the probe can be created in a folder whose path is over 260 characters");
        Check.True(folder!.EndsWith(Path.GetFileName(deep), StringComparison.OrdinalIgnoreCase), folder);
        string? resolved = AppDataRedirect.ResolvedPath(deep);
        Check.NotNull(resolved);
        Check.False(resolved!.StartsWith(@"\\?\", StringComparison.Ordinal), "the normal form is given back");
        Check.True(resolved.Length >= 300);
    }

    [UnitTest]
    public static void The_friendly_name_is_the_package_without_the_publisher()
    {
        Check.Equal("Notes", AppDataRedirect.FriendlyNameOf("Contoso.Notes_pzs8sxrjxfjjc"));
        Check.Equal("WindowsTerminal", AppDataRedirect.FriendlyNameOf("Microsoft.WindowsTerminal_8wekyb3d8bbwe"));
        Check.Equal("Foo", AppDataRedirect.FriendlyNameOf("Foo_abc"));
        Check.Equal("NoPublisher", AppDataRedirect.FriendlyNameOf("NoPublisher"));
        Check.Equal("Notes", new ForeignSettings("Contoso.Notes_pzs8sxrjxfjjc", "x", DateTime.UtcNow, Array.Empty<string>()).FriendlyName);
    }

    [UnitTest]
    public static void The_redirect_notice_says_who_and_why()
    {
        Check.Equal("Clink was started from inside Notes: Windows keeps a private copy of its settings for that app, so changes made now are not seen by Clink started from Explorer or the Start menu",
            AppDataRedirect.NoticeFor("Notes"));
        Check.Null(AppDataRedirect.Notice, "nothing is detected until Detect has run");
    }

    [UnitTest]
    public static void Detect_sees_a_folder_that_Windows_keeps_for_an_app()
    {
        // The folder is named like a packaged app's private copy, which is all GetFinalPathNameByHandle can tell by.
        using var dir = new TempDir();
        string folder = dir.File(@"AppData\Local\Packages\Fake_abc\LocalCache\Roaming\Clink");
        Settings.DirOverride = folder;
        Check.True(AppDataRedirect.Detect());
        Check.Equal("Fake_abc", AppDataRedirect.Package);
        Check.Equal("Fake", AppDataRedirect.FriendlyName);
        Check.True(AppDataRedirect.FinalFolder!.EndsWith(@"\LocalCache\Roaming\Clink", StringComparison.OrdinalIgnoreCase), AppDataRedirect.FinalFolder);
        Check.Equal(AppDataRedirect.NoticeFor("Fake"), AppDataRedirect.Notice);
        Check.True(Directory.Exists(folder), "the folder is created, as the settings need it anyway");
        Check.Equal(0, Directory.GetFileSystemEntries(folder).Length, "the probe file is gone with its handle");
    }

    [UnitTest]
    public static void Detect_says_no_for_an_ordinary_folder()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.File("settings");
        Check.False(AppDataRedirect.Detect());
        Check.Null(AppDataRedirect.Package);
        Check.Null(AppDataRedirect.FriendlyName);
        Check.Null(AppDataRedirect.FinalFolder);
        Check.Null(AppDataRedirect.Notice);
        Check.True(Directory.Exists(dir.File("settings")));
        Check.Equal(0, Directory.GetFileSystemEntries(dir.File("settings")).Length);
        // and the folder it looks in is found as itself
        Check.True(AppDataRedirect.FinalFolderOf(dir.File("settings"))!.EndsWith(@"\settings", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public static void Detect_does_not_fail_on_a_folder_it_cannot_use()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("file.txt"), "x");
        Settings.DirOverride = dir.File("file.txt");   // a file where the folder should be
        Check.False(AppDataRedirect.Detect());
        Check.Null(AppDataRedirect.Package);
        // Not redirected is not the same as "could not tell": a first start does not take a copy over by itself on a guess
        Check.True(AppDataRedirect.Unknown, "the third state");
        Check.False(SettingsImport.CanTakeOverAtStart(freshStore: true, isDev: false), "unknown: no automatic take-over");
        Settings.DirOverride = dir.File("ordinary");
        Check.False(AppDataRedirect.Detect());
        Check.False(AppDataRedirect.Unknown, "a folder that could be looked at is known");
        Check.True(SettingsImport.CanTakeOverAtStart(freshStore: true, isDev: false), "known and ordinary: as before");
    }

    // ------------------------------------------------------------------ finding other copies

    [UnitTest]
    public static void Other_copies_are_listed_newest_first()
    {
        using var root = new TempDir();
        var now = DateTime.UtcNow;
        string foo = FakeCopy(root, "Foo_abc", Json(), savedUtc: now.AddHours(-2));
        string bar = FakeCopy(root, "Bar_xyz", Json(), savedUtc: now.AddHours(-1));
        Directory.CreateDirectory(root.File(@"Packages\NoClink_1\LocalCache\Roaming"));                  // a package without a Clink folder
        FakeCopy(root, "Empty_2", "");                                                                    // an empty settings.json
        Directory.CreateDirectory(root.File(@"Packages\Wrong_3\LocalCache\Local\Clink"));                 // Local, not Roaming
        File.WriteAllText(root.File(@"Packages\Wrong_3\LocalCache\Local\Clink\settings.json"), Json());
        File.WriteAllText(root.File(@"Packages\stray-file.txt"), "x");

        var found = SettingsImport.FindForeignCopies(root.Path);
        Check.Equal(new[] { "Bar_xyz", "Foo_abc" }, found.Select(f => f.Package).ToArray());
        Check.Equal(bar, found[0].Folder);
        Check.Equal(foo, found[1].Folder);
        Check.True(Math.Abs((found[0].SavedUtc - now.AddHours(-1)).TotalSeconds) < 2, "SavedUtc is when settings.json was written");
        Check.Equal(0, found[0].Differences.Count);
        Check.Equal(Path.Combine(bar, "settings.json"), found[0].SettingsPath);
        Check.Equal(Path.Combine(bar, "clips.json"), found[0].ClipsPath);
    }

    [UnitTest]
    public static void Other_copies_are_looked_for_in_the_given_root_or_the_test_root_and_never_fail()
    {
        using var root = new TempDir();
        FakeCopy(root, "Foo_abc", Json());
        Check.Equal(1, SettingsImport.FindForeignCopies(root.Path).Count);
        SettingsImport.PackagesRoot = PackagesOf(root);
        Check.Equal(1, SettingsImport.FindForeignCopies().Count, "PackagesRoot stands in for %LOCALAPPDATA%\\Packages");
        Check.Equal(0, SettingsImport.FindForeignCopies(root.File("nothing-here")).Count);
        SettingsImport.PackagesRoot = root.File("nothing-here-either");
        Check.Equal(0, SettingsImport.FindForeignCopies().Count);
    }

    // ------------------------------------------------------------------ what would change

    [UnitTest]
    public static void Differences_ignore_where_the_window_sits_and_the_other_volatile_keys()
    {
        string ours = Json();
        string theirs = Json(s =>
        {
            s.WindowX = -1500; s.WindowY = 350; s.WindowWidth = 1346; s.WindowHeight = 793;
            s.LastUpdateCheck = new DateTime(2026, 10, 21, 18, 32, 30, DateTimeKind.Utc);
            s.PendingUpdateVersion = "0.2.4"; s.PendingUpdateSha256 = new string('0', 64);
            s.SchemaVersion = 2; s.AudioSetupSeen = true; s.ImportDeclined = "Notes_x|1";
        });
        Check.Equal(0, SettingsImport.Differences(ours, theirs).Count);
        Check.Equal(0, SettingsImport.Differences(theirs, ours).Count);
    }

    [UnitTest]
    public static void Differences_compare_what_the_settings_mean_not_how_the_file_is_written()
    {
        // a file that lacks keys (an older Clink), writes them in another case and spells numbers differently still means the defaults
        string older = """{ "fps": "60", "BUFFERSECONDS": 45 }""";
        Check.Equal(0, SettingsImport.Differences(Json(), older).Count);
        Check.Equal(0, SettingsImport.Differences(older, Json()).Count);
    }

    [UnitTest]
    public static void Differences_name_what_would_change_in_the_users_words()
    {
        string theirs = Json(s =>
        {
            s.Codec = "hevc"; s.BitrateMbps = 16; s.BufferSeconds = 48; s.Hotkeys[0].Keys.Vk = 0x75; s.TrackVoice = true; s.ToastPosition = "top-left";
            s.ArchiveEnabled = true; s.ArchivePath = @"T:\clips"; s.ClipsFolder = @"D:\Clips"; s.Theme = "light"; s.SizeTarget = "copium200";
            s.StartMinimized = true; s.CopiumPlus = true; s.AutoUpdate = false;
        });
        Check.Equal(
            new[] { "video quality", "size target", "replay length", "hotkeys", "audio", "alerts", "archive", "clips folder", "Copium+", "startup", "updates", "theme" },
            SettingsImport.Differences(Json(), theirs).ToArray());

        // one change, one label; a key this version does not know is "other settings"
        Check.Equal(new[] { "replay length" }, SettingsImport.Differences(Json(), Json(s => s.BufferSeconds = 60)).ToArray());
        Check.Equal(new[] { "other settings" }, SettingsImport.Differences(Json(), """{ "FutureKey": { "a": 1 } }""").ToArray());
        Check.Equal(new[] { "video quality", "other settings" }, SettingsImport.Differences(Json(), """{ "Fps": 30, "FutureKey": 1 }""").ToArray());
        Check.Throws<JsonException>(() => SettingsImport.Differences(Json(), "not json"));
    }

    [UnitTest]
    public static void Differences_compare_the_Copium_token_decrypted_and_never_log_it()
    {
        var a = Settings.FromJson("{}"); a.CopiumToken = "secret-token-one";
        var b = Settings.FromJson("{}"); b.CopiumToken = "secret-token-one";
        Check.NotNull(a.CopiumTokenProtected);
        Check.NotEqual(a.CopiumTokenProtected, b.CopiumTokenProtected, "DPAPI gives different text for the same token");

        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => logged.Enqueue(line);
        Log.Line += Collect;
        try
        {
            Check.Equal(0, SettingsImport.Differences(a.ToJson(), b.ToJson()).Count);
            b.CopiumToken = "secret-token-two";
            Check.Equal(new[] { "Copium token" }, SettingsImport.Differences(a.ToJson(), b.ToJson()).ToArray());
            Check.Equal(new[] { "Copium token" }, SettingsImport.Differences(Json(), a.ToJson()).ToArray(), "a token on one side only");
            Check.Equal(new[] { "Copium token" }, SettingsImport.Differences(a.ToJson(), Json()).ToArray());
        }
        finally { Log.Line -= Collect; }
        Check.False(logged.Any(l => l.Contains("secret-token") || l.Contains(a.CopiumTokenProtected!) || l.Contains(b.CopiumTokenProtected!)), "no token, plain or protected, in the log");
    }

    // ------------------------------------------------------------------ the clip index

    [UnitTest]
    public static void Merged_clip_indexes_are_a_union_that_keeps_the_links_and_archive_fields()
    {
        const string ours = """
            [
              { "Path": "C:\\clips\\a.mp4", "Created": "2026-10-21T10:00:00", "Duration": 0, "Size": 100, "Game": "X", "AudioTracks": null, "CopiumLink": null },
              { "Path": "C:\\clips\\b.mp4", "Created": "2026-10-21T11:00:00", "Duration": 12.5, "Size": 200, "CopiumLink": "https://ours/b" },
              { "Created": "2026-10-21T12:00:00", "Size": 1 }
            ]
            """;
        const string theirs = """
            [
              { "Path": "c:\\CLIPS\\A.mp4", "Created": "2026-10-20T10:00:00", "Duration": 30, "Size": 5, "Game": "Y", "CopiumLink": "https://theirs/a",
                "CopiumPageUrl": "https://theirs/a/page", "CopiumId": "id-a", "ArchivedAt": "2026-10-21T01:00:00", "AudioTracks": ["Mix", "Game"],
                "IsTrim": true, "SourcePath": "C:\\clips\\src.mp4" },
              { "Path": "C:\\clips\\b.mp4", "CopiumLink": "https://theirs/b", "Duration": 99 },
              { "Path": "C:\\clips\\c.mp4", "Created": "2026-10-19T09:00:00", "CopiumLink": "https://theirs/c", "FutureField": 7 },
              { "Size": 2 }
            ]
            """;
        using var doc = JsonDocument.Parse(SettingsImport.MergeClips(ours, theirs));
        var clips = doc.RootElement.EnumerateArray().ToList();
        Check.Equal(4, clips.Count, "a, b, c and our clip without a path");

        var a = clips.Single(c => c.TryGetProperty("Path", out var p) && p.GetString() == @"C:\clips\a.mp4");
        Check.Equal("2026-10-21T10:00:00", a.GetProperty("Created").GetString(), "ours wins where it has a value");
        Check.Equal(100, a.GetProperty("Size").GetInt32());
        Check.Equal("X", a.GetProperty("Game").GetString());
        Check.Equal(30.0, a.GetProperty("Duration").GetDouble(), "a length of 0 is nothing: theirs fills it");
        Check.Equal("https://theirs/a", a.GetProperty("CopiumLink").GetString());
        Check.Equal("https://theirs/a/page", a.GetProperty("CopiumPageUrl").GetString());
        Check.Equal("id-a", a.GetProperty("CopiumId").GetString());
        Check.Equal("2026-10-21T01:00:00", a.GetProperty("ArchivedAt").GetString());
        Check.Equal(new[] { "Mix", "Game" }, a.GetProperty("AudioTracks").EnumerateArray().Select(x => x.GetString()).ToArray());
        Check.True(a.GetProperty("IsTrim").GetBoolean());
        Check.Equal(@"C:\clips\src.mp4", a.GetProperty("SourcePath").GetString());

        var b = clips.Single(c => c.TryGetProperty("Path", out var p) && p.GetString() == @"C:\clips\b.mp4");
        Check.Equal("https://ours/b", b.GetProperty("CopiumLink").GetString(), "our link stays");
        Check.Equal(12.5, b.GetProperty("Duration").GetDouble());

        var c2 = clips.Single(c => c.TryGetProperty("Path", out var p) && p.GetString() == @"C:\clips\c.mp4");
        Check.Equal("https://theirs/c", c2.GetProperty("CopiumLink").GetString());
        Check.Equal(7, c2.GetProperty("FutureField").GetInt32(), "fields this version does not know are carried along");
        Check.Equal(1, clips.Count(c => !c.TryGetProperty("Path", out _)), "only our own clip without a path is kept");
    }

    [UnitTest]
    public static void A_fresh_clip_index_is_just_the_other_copys_and_a_damaged_one_is_not_replaced()
    {
        const string theirs = """[ { "Path": "C:\\clips\\a.mp4", "CopiumLink": "https://theirs/a" } ]""";
        using var doc = JsonDocument.Parse(SettingsImport.MergeClips(null, theirs));
        Check.Equal(1, doc.RootElement.GetArrayLength());
        Check.Equal("https://theirs/a", doc.RootElement[0].GetProperty("CopiumLink").GetString());
        using var empty = JsonDocument.Parse(SettingsImport.MergeClips("", theirs));
        Check.Equal(1, empty.RootElement.GetArrayLength());
        Check.Throws<JsonException>(() => SettingsImport.MergeClips("{ not json", theirs));
        Check.Throws<JsonException>(() => SettingsImport.MergeClips("""{ "Path": "x" }""", theirs), "an index is an array");
    }

    // ------------------------------------------------------------------ taking a copy over

    [UnitTest]
    public static void Import_writes_the_other_copys_settings_keeps_ours_and_merges_the_clips()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        var ours = LoadFrom(home, Json(s => s.Codec = "h264"));
        ours.Save();
        string oursText = File.ReadAllText(home.File("settings.json"));
        ClipLibrary.IndexPathOverride = home.File("clips.json");
        File.WriteAllText(home.File("clips.json"), """[ { "Path": "C:\\clips\\mine.mp4", "Size": 1 } ]""");
        string theirsText = Json(s => { s.Codec = "hevc"; s.BitrateMbps = 16; s.BufferSeconds = 48; });
        FakeCopy(root, "Foo_abc", theirsText, """[ { "Path": "C:\\clips\\theirs.mp4", "CopiumLink": "https://theirs/t" } ]""");
        var copy = SettingsImport.FindForeignCopies(root.Path).Single();

        SettingsImport.Import(copy);

        Check.Equal(theirsText, File.ReadAllText(home.File("settings.json")), "their settings.json, as it is");
        Check.Equal(oursText, File.ReadAllText(home.File("settings.pre-import.json")), "what was replaced is kept");
        using (var clips = JsonDocument.Parse(File.ReadAllText(home.File("clips.json"))))
            Check.Equal(new[] { @"C:\clips\mine.mp4", @"C:\clips\theirs.mp4" }, clips.RootElement.EnumerateArray().Select(c => c.GetProperty("Path").GetString()!).ToArray());
        var loaded = Settings.Load();
        Check.Equal("hevc", loaded.Codec);
        Check.Equal(16, loaded.BitrateMbps);
        Check.Equal(48, loaded.BufferSeconds);
    }

    [UnitTest]
    public static void Import_of_a_copy_that_cannot_be_read_changes_nothing()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json(s => s.Codec = "h264")).Save();
        string before = File.ReadAllText(home.File("settings.json"));
        string folder = FakeCopy(root, "Foo_abc", "{ this is not json");
        var copy = SettingsImport.FindForeignCopies(root.Path).Single();
        Check.Throws<JsonException>(() => SettingsImport.Import(copy));
        Check.Equal(before, File.ReadAllText(home.File("settings.json")));
        Check.False(File.Exists(home.File("settings.pre-import.json")));

        File.WriteAllText(Path.Combine(folder, "settings.json"), "[1, 2]");
        Check.Throws<JsonException>(() => SettingsImport.Import(copy), "settings are an object");
        File.Delete(Path.Combine(folder, "settings.json"));
        Check.Throws<IOException>(() => SettingsImport.Import(copy), "a copy that is gone");
        Check.Equal(before, File.ReadAllText(home.File("settings.json")));
    }

    [UnitTest]
    public static void A_damaged_clip_index_does_not_stop_the_settings_from_being_imported()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json()).Save();
        ClipLibrary.IndexPathOverride = home.File("clips.json");
        File.WriteAllText(home.File("clips.json"), "{ damaged");
        string theirsText = Json(s => s.Codec = "hevc");
        FakeCopy(root, "Foo_abc", theirsText, """[ { "Path": "C:\\clips\\theirs.mp4" } ]""");
        SettingsImport.Import(SettingsImport.FindForeignCopies(root.Path).Single());
        Check.Equal(theirsText, File.ReadAllText(home.File("settings.json")));
        Check.Equal("{ damaged", File.ReadAllText(home.File("clips.json")), "our index is left as it was");
    }

    [UnitTest]
    public static void A_second_import_keeps_its_own_copy_and_never_overwrites_the_first_ones()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        LoadFrom(home, Json(s => s.Fps = 30)).Save();
        string original = File.ReadAllText(home.File("settings.json"));
        FakeCopy(root, "Foo_abc", Json(s => s.Fps = 120));
        var copy = SettingsImport.FindForeignCopies(root.Path).Single();
        SettingsImport.Import(copy);
        string afterFirst = File.ReadAllText(home.File("settings.json"));
        File.WriteAllText(copy.SettingsPath, Json(s => s.Fps = 15));
        SettingsImport.Import(copy);
        Check.Equal(original, File.ReadAllText(home.File("settings.pre-import.json")), "the first copy stays what it was");
        Check.Equal(afterFirst, File.ReadAllText(home.File("settings.pre-import.1.json")), "the second import keeps what it replaced beside it");
        Check.Equal(15, Settings.Load().Fps);
    }

    [UnitTest]
    public static void A_fresh_store_takes_over_the_newest_copy_it_can_read()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        Settings.DirOverride = home.Path;
        SettingsImport.PackagesRoot = PackagesOf(root);
        Check.Null(SettingsImport.ImportNewest(), "no copy at all");
        Check.False(File.Exists(home.File("settings.json")));

        var now = DateTime.UtcNow;
        string older = Json(s => s.Codec = "hevc");
        FakeCopy(root, "Old_1", older, savedUtc: now.AddHours(-3));
        FakeCopy(root, "Broken_2", "{ not json", savedUtc: now.AddHours(-1));   // the newest cannot be used
        var taken = SettingsImport.ImportNewest();
        Check.NotNull(taken);
        Check.Equal("Old_1", taken!.Package);
        // theirs, except that the values that belong to this installation (where the window sits, the updater's state) are not taken over
        string written = File.ReadAllText(home.File("settings.json"));
        Check.Equal(SettingsImport.KeepOurOwn(older, null, keepOurToken: false), written);
        Check.DoesNotContain(written, "WindowX");
        Check.Contains(written, "\"Codec\": \"hevc\"");
        Check.False(File.Exists(home.File("settings.pre-import.json")), "there was nothing to keep");
        Check.Equal("hevc", Settings.Load().Codec);
    }

    // ------------------------------------------------------------------ an existing store: offer, decline

    [UnitTest]
    public static void Scan_offers_the_newest_copy_that_differs_and_remembers_a_refusal()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        var ours = LoadFrom(home, Json());
        SettingsImport.PackagesRoot = PackagesOf(root);
        var now = DateTime.UtcNow;
        FakeCopy(root, "Same_1", Json(), savedUtc: now.AddHours(-1));                                        // newest, but the same as ours
        string folder = FakeCopy(root, "Notes_x", Json(s => { s.Codec = "hevc"; s.BufferSeconds = 48; }), savedUtc: now.AddHours(-2));
        FakeCopy(root, "Older_3", Json(s => s.Fps = 30), savedUtc: now.AddHours(-3));

        int raised = 0;
        Action count = () => raised++;
        SettingsImport.PendingChanged += count;
        try
        {
            Check.Null(SettingsImport.Pending);
            SettingsImport.Scan(ours).Wait();
            PumpUi();   // the event is raised on the UI thread
            var pending = SettingsImport.Pending;
            Check.NotNull(pending);
            Check.Equal("Notes_x", pending!.Package);
            Check.Equal(new[] { "video quality", "replay length" }, pending.Differences.ToArray());
            Check.Equal(1, raised);

            SettingsImport.Scan(ours).Wait();   // nothing new: the same copy is still pending, and nobody is told again
            PumpUi();
            Check.Equal(1, raised);

            // turned down: remembered in the settings (and the file), no longer pending, not offered again
            SettingsImport.Decline(ours, pending);
            Check.Equal(pending.DeclineKey, ours.ImportDeclined);
            Check.Contains(File.ReadAllText(home.File("settings.json")), "ImportDeclined");
            Check.Null(SettingsImport.Pending);
            Check.Equal(2, raised);
            SettingsImport.Scan(ours).Wait();
            PumpUi();
            Check.Equal("Older_3", SettingsImport.Pending?.Package, "the next newest that differs is offered instead");
            SettingsImport.Decline(ours, SettingsImport.Pending!);
            SettingsImport.Scan(ours).Wait();
            PumpUi();
            Check.Null(SettingsImport.Pending);

            // the copy that was turned down is saved again: that is a different copy, and it is offered again
            File.SetLastWriteTimeUtc(Path.Combine(folder, "settings.json"), now.AddMinutes(-5));
            SettingsImport.Scan(ours).Wait();
            PumpUi();
            Check.Equal("Notes_x", SettingsImport.Pending?.Package);
            Check.Equal(ours.ImportDeclined, Settings.Load().ImportDeclined, "the refusal survives a restart");
        }
        finally { SettingsImport.PendingChanged -= count; }
    }

    [UnitTest]
    public static void Scan_finds_nothing_to_offer_when_every_copy_is_the_same_or_unreadable()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        var ours = LoadFrom(home, Json());
        SettingsImport.PackagesRoot = PackagesOf(root);
        SettingsImport.Scan(ours).Wait();
        Check.Null(SettingsImport.Pending, "no copies");
        FakeCopy(root, "Same_1", Json(s => { s.WindowX = 5; s.LastUpdateCheck = DateTime.UtcNow; }));
        FakeCopy(root, "Broken_2", "{ not json");
        SettingsImport.Scan(ours).Wait();
        Check.Null(SettingsImport.Pending);
    }

    [UnitTest]
    public static void A_pending_copy_that_was_imported_is_no_longer_pending()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        var ours = LoadFrom(home, Json());
        SettingsImport.PackagesRoot = PackagesOf(root);
        FakeCopy(root, "Notes_x", Json(s => s.Codec = "hevc"));
        SettingsImport.Scan(ours).Wait();
        var pending = SettingsImport.Pending!;
        Check.NotNull(pending);
        SettingsImport.Import(pending);
        Check.Null(SettingsImport.Pending);
        // looked at again with the imported settings as ours, there is nothing left to offer
        SettingsImport.Scan(Settings.Load()).Wait();
        Check.Null(SettingsImport.Pending);
    }

    [UnitTest]
    public static void Nothing_the_import_logs_holds_a_value()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        var ours = LoadFrom(home, Json());
        SettingsImport.PackagesRoot = PackagesOf(root);
        var theirs = Settings.FromJson("{}");
        theirs.CopiumToken = "secret-token-three";
        theirs.ArchivePath = @"\\secret-nas\clips";
        FakeCopy(root, "Notes_x", theirs.ToJson());
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => logged.Enqueue(line);
        Log.Line += Collect;
        try
        {
            SettingsImport.Scan(ours).Wait();
            SettingsImport.Import(SettingsImport.Pending!);
        }
        finally { Log.Line -= Collect; }
        Check.True(logged.Count > 0, "the import says what it does");
        Check.False(logged.Any(l => l.Contains("secret-token") || l.Contains("secret-nas") || l.Contains(theirs.CopiumTokenProtected!)), "no value in the log, only the names of what changes");
        Check.True(logged.Any(l => l.Contains("Copium token")));
    }

    // ------------------------------------------------------------------ restarting

    [UnitTest]
    public static void The_restarted_Clink_gets_the_same_command_line_without_what_only_makes_sense_once()
    {
        // a normal run: nothing but what it was started with, minus what is for one start, plus --show
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(Array.Empty<string>()).ToArray());
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(new[] { "--show" }).ToArray());
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(new[] { "--minimized", "--clip", "--no-update", "--trim-last", "--share-last" }).ToArray());
        Check.Equal(new[] { "--foo", "--show" }, AppController.RestartArguments(new[] { "--foo", "--MINIMIZED" }).ToArray(), "what it does not know it passes on");
        Check.Equal(0, AppController.DevSwitches().Count);
    }

    [UnitTest]
    public static void The_test_switches_of_dev_are_read_and_only_dev_has_them()
    {
        var plain = DevOptions.Parse(new[] { "--dev", @"C:\d" })!;
        Check.Null(plain.PackagesRoot);
        Check.False(plain.AcceptImport);

        var withRoot = DevOptions.Parse(new[] { "--packages-root", @"D:\fake\Packages", "--dev", @"C:\d", "--dev-accept-import" })!;
        Check.Equal(@"D:\fake\Packages", withRoot.PackagesRoot);
        Check.True(withRoot.AcceptImport);
        Check.Equal(@"C:\d", withRoot.Folder);

        Check.Null(DevOptions.Parse(new[] { "--dev", @"C:\d", "--packages-root" })!.PackagesRoot, "a switch without a value");
        // without --dev there are no dev options at all, and a switch that needs one is an error, not a normal start
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--packages-root", @"D:\x" }));

        // the real Packages folder is no test root: a test must not take over the settings of whoever owns the machine
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        Check.Contains(Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", @"C:\d", "--packages-root", real })).Message, "real");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", @"C:\d", "--packages-root", real.ToUpperInvariant() + @"\" }));
        Check.NotNull(DevOptions.Parse(new[] { "--dev", @"C:\d", "--packages-root", real + "-fake" })!.PackagesRoot);
    }
}
