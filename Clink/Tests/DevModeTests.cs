using System.IO;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// <c>--dev</c>: the instance that tests run next to the user's own Clink. It must never act on the user's clips, archive, account,
/// updates, mutex or game; and a mistake in its switches must never turn into a normal start.
/// </summary>
internal static class DevModeTests
{
    private static string Problem(params string[] args) => Check.Throws<ArgumentException>(() => DevOptions.Parse(args)).Message;

    private static DevOptions Dev(TempDir dir, params string[] more) => DevOptions.Parse(new[] { "--dev", dir.Path }.Concat(more).ToArray())!;

    // ------------------------------------------------------------------ the switches

    [UnitTest]
    public static void A_mistake_in_a_dev_switch_is_an_error_not_a_normal_start()
    {
        // each of these used to be a normal start through the real single-instance path: it would take the real Clink's mutex or forward to it
        Check.Contains(Problem("--dev-hotkeys"), "--dev");
        Check.Contains(Problem("--dev-record"), "--dev");
        Check.Contains(Problem("--dev-token"), "--dev");
        Check.Contains(Problem("--dev-accept-import"), "--dev");
        Check.Contains(Problem("--packages-root", @"D:\x"), "--dev");
        Check.Contains(Problem("--DEV-HOTKEYS"), "--dev");
        // a typo, an unknown one
        Check.Contains(Problem("--dev", @"C:\d", "--dev-hotkey"), "--dev-hotkey");
        Check.Contains(Problem("--devv"), "--devv");
        Check.Contains(Problem("--dev:C:\\d"), "--dev:C:");
        // the same thing twice, in either form
        Check.Contains(Problem("--dev", @"C:\a", "--dev", @"C:\b"), "more than once");
        Check.Contains(Problem("--dev", @"C:\a", @"--dev=C:\b"), "more than once");
        Check.Contains(Problem(@"--dev=C:\a", @"--dev=C:\b"), "more than once");
        // --dev= with nothing after it
        Check.Contains(Problem("--dev="), "folder");
        Check.Contains(Problem("--dev=\"\""), "folder");
        Check.Contains(Problem("--dev=  "), "folder");

        // and what is not about --dev at all is no dev start
        Check.Null(DevOptions.Parse(Array.Empty<string>()));
        Check.Null(DevOptions.Parse(new[] { "--show", "--minimized", "--no-update", "--clip", "--trim-last", "--share-last" }));
    }

    [UnitTest]
    public static void Dev_can_be_written_with_an_equals_sign()
    {
        var eq = DevOptions.Parse(new[] { @"--dev=C:\scratch\dev one", "--dev-hotkeys" })!;
        Check.Equal(@"C:\scratch\dev one", eq.Folder);
        Check.True(eq.BindHotkeys);
        Check.Equal(@"C:\q", DevOptions.Parse(new[] { "--DEV=\"C:\\q\"" })!.Folder, "quotes and case do not matter");
        Check.Equal(DevOptions.DefaultFolder, DevOptions.Parse(new[] { "--dev", "--dev-record" })!.Folder, "a bare --dev: the default folder");
        Check.Equal(@"C:\a", DevOptions.Parse(new[] { "--show", @"--dev=C:\a", "--minimized" })!.Folder, "anywhere on the command line");
        // both forms are the same instance
        Check.Equal(DevOptions.Parse(new[] { "--dev", @"C:\a" })!.InstanceSuffix, DevOptions.Parse(new[] { @"--dev=C:\a" })!.InstanceSuffix);
    }

    [UnitTest]
    public static void The_other_dev_flags_are_read()
    {
        var all = DevOptions.Parse(new[] { "--dev", @"C:\d", "--dev-record", "--dev-token", "--dev-hotkeys", "--dev-accept-import" })!;
        Check.True(all.Record && all.AllowToken && all.BindHotkeys && all.AcceptImport);
        var none = DevOptions.Parse(new[] { "--dev", @"C:\d" })!;
        Check.False(none.Record || none.AllowToken || none.BindHotkeys || none.AcceptImport, "everything is off unless asked for");
    }

    [UnitTest]
    public static void A_dev_folder_that_is_the_real_settings_folder_is_refused()
    {
        string real = Settings.RealDir;
        Check.Contains(Problem("--dev", real), "real");
        Check.Contains(Problem("--dev=" + real), "real");
        Check.Contains(Problem("--dev", real.ToUpperInvariant() + @"\"), "real");
        Check.Contains(Problem("--dev", real.Replace('\\', '/')), "real");
        Check.Contains(Problem("--dev", Path.Combine(real, "..", Path.GetFileName(real))), "real", "spelled another way");
        // beside it, or inside it, is a folder of its own
        Check.Equal(real + "-dev", DevOptions.Parse(new[] { "--dev", real + "-dev" })!.Folder);
        Check.Equal(Path.Combine(real, "dev"), DevOptions.Parse(new[] { "--dev", Path.Combine(real, "dev") })!.Folder);
    }

    [UnitTest]
    public static void A_dev_folder_that_is_not_a_path_is_an_error_too()
    {
        Check.Contains(Problem("--dev", "C:\\bad\0name"), "not a valid path");
    }

    // ------------------------------------------------------------------ the guards on every start

    private static Settings Realistic()
    {
        var s = Settings.FromJson("{}");
        s.ClipsFolder = @"C:\Users\Someone\Videos\Clink";
        s.ArchiveEnabled = true; s.ArchivePath = @"\\nas\clips\Clink"; s.ArchiveAfterDays = 1;
        s.AutoUpdate = true; s.RunAtStartup = true;
        s.CopiumToken = "the-real-token";
        return s;
    }

    [UnitTest]
    public static void Whatever_the_settings_file_says_a_dev_instance_stays_away_from_the_real_world()
    {
        using var dir = new TempDir();
        var dev = Dev(dir);
        var s = Realistic();
        Check.NotNull(s.CopiumTokenProtected);
        var changed = string.Join("; ", dev.Isolate(s, freshStore: false));
        Check.False(s.AutoUpdate);
        Check.False(s.RunAtStartup);
        Check.Equal(Path.Combine(dir.Path, "clips"), s.ClipsFolder, "the real clips are not scanned, trimmed, shared or archived");
        Check.False(s.ArchiveEnabled, "nothing is moved or deleted");
        Check.Null(s.ArchivePath, "and a manual Archive now, which needs no switch, has nowhere to write");
        Check.Null(s.CopiumTokenProtected);
        Check.Null(s.CopiumToken, "nothing can be shared to the real account");
        Check.True(s.ToastRecording.Enabled, "an existing store keeps its toast settings");

        // what it did is said (for the log), without saying the token
        foreach (string said in new[] { "automatic updates off", "run at startup off", "clips folder", @"C:\Users\Someone\Videos\Clink", "archive off", "no Copium token" })
            Check.Contains(changed, said);
        Check.DoesNotContain(changed, "the-real-token");
        Check.DoesNotContain(changed, "Recording started");
        Check.Equal(0, dev.Isolate(s, freshStore: false).Count, "and a second time there is nothing left to do");
    }

    [UnitTest]
    public static void A_fresh_dev_store_gets_no_Recording_started_toast()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("{}");
        Check.True(s.ToastRecording.Enabled);
        var changed = string.Join("; ", Dev(dir).Isolate(s, freshStore: true));
        Check.False(s.ToastRecording.Enabled, "a recording dev instance does not put a toast over the game");
        Check.Contains(changed, "Recording started");
        Check.True(s.ToastClip.Enabled, "the others stay: a test of the clip toast needs it");
    }

    [UnitTest]
    public static void Paths_inside_the_dev_folder_are_left_alone()
    {
        using var dir = new TempDir();
        var dev = Dev(dir);
        var s = Settings.FromJson("{}");
        s.ClipsFolder = Path.Combine(dir.Path, "my clips");
        s.ArchiveEnabled = true; s.ArchivePath = Path.Combine(dir.Path, "archive");
        dev.Isolate(s, freshStore: false);
        Check.Equal(Path.Combine(dir.Path, "my clips"), s.ClipsFolder);
        Check.True(s.ArchiveEnabled, "an archive to a folder of the dev instance's own is what the archive tests need");
        Check.Equal(Path.Combine(dir.Path, "archive"), s.ArchivePath);

        // a location outside is forgotten even with the switch off: the manual Archive now would use it
        var manual = Settings.FromJson("{}");
        manual.ArchiveEnabled = false; manual.ArchivePath = @"\\nas\clips";
        Check.Contains(string.Join("; ", dev.Isolate(manual, freshStore: false)), "archive off and its location cleared");
        Check.Null(manual.ArchivePath);

        s.ClipsFolder = dir.Path;   // the dev folder itself
        dev.Isolate(s, freshStore: false);
        Check.Equal(dir.Path, s.ClipsFolder);

        // a folder that only starts with the same letters is not inside it; a relative or empty one is not either
        foreach (string? outside in new string?[] { dir.Path + "2", dir.Path + "2" + @"\clips", dir.Path + "-copy", @"..\clips", "clips", "", "  ", null })
        {
            var t = Settings.FromJson("{}");
            t.ClipsFolder = outside!;
            t.ArchiveEnabled = true; t.ArchivePath = outside;
            dev.Isolate(t, freshStore: false);
            Check.Equal(Path.Combine(dir.Path, "clips"), t.ClipsFolder, $"'{outside}'");
            Check.False(t.ArchiveEnabled, $"'{outside}'");
        }
        Check.True(dev.IsInsideFolder(Path.Combine(dir.Path, "a", "..", "b")));
        Check.False(dev.IsInsideFolder(Path.Combine(dir.Path, "..", "b")));
        Check.True(dev.IsInsideFolder(dir.Path.ToUpperInvariant() + @"\X"), "paths ignore case");
        Check.False(dev.IsInsideFolder("C:\\bad\0name"));
    }

    [UnitTest]
    public static void A_Copium_token_is_kept_only_with_dev_token()
    {
        using var dir = new TempDir();
        var without = Realistic();
        Dev(dir).Isolate(without, freshStore: false);
        Check.Null(without.CopiumToken);
        var with = Realistic();
        Dev(dir, "--dev-token").Isolate(with, freshStore: false);
        Check.Equal("the-real-token", with.CopiumToken);
        Check.False(with.AutoUpdate, "the other guards do not depend on it");
        Check.False(with.ArchiveEnabled);
    }

    [UnitTest]
    public static void The_guards_hold_on_every_start_for_a_file_that_is_already_there_unreadable_or_imported()
    {
        using var dir = new TempDir();
        var dev = Dev(dir);

        // a settings.json that is already in the folder (a copied fixture, a real Clink's)
        File.WriteAllText(dir.File("settings.json"), Realistic().ToJson());
        Settings.DirOverride = dir.Path;
        var existing = Settings.Load();
        Check.True(existing.LoadedFromFile);
        dev.Isolate(existing, freshStore: !existing.LoadedFromFile);
        Check.Equal(Path.Combine(dir.Path, "clips"), existing.ClipsFolder);
        Check.False(existing.ArchiveEnabled);
        Check.Null(existing.CopiumToken);
        Check.False(existing.AutoUpdate);
        Check.Contains(File.ReadAllText(dir.File("settings.json")), @"Videos\\Clink", "in memory: the file is left as it is until something saves");

        // one that cannot be read: LoadedFromFile is true all the same, and the defaults point at the real Videos\Clink
        using var broken = new TempDir();
        File.WriteAllText(broken.File("settings.json"), "{ not json");
        Settings.DirOverride = broken.Path;
        var unreadable = Settings.Load();
        Check.True(unreadable.LoadedFromFile);
        Check.True(unreadable.FileWasSkipped);
        Check.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink"), unreadable.ClipsFolder);
        Dev(broken).Isolate(unreadable, freshStore: !unreadable.LoadedFromFile);
        Check.Equal(Path.Combine(broken.Path, "clips"), unreadable.ClipsFolder);

        // the settings another copy of Clink saved, taken over into a dev store
        using var home = new TempDir();
        using var root = new TempDir();
        var theirs = Realistic();
        string folder = root.File(@"Packages\Notes_x\LocalCache\Roaming\Clink");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), theirs.ToJson());
        Settings.DirOverride = home.Path;
        SettingsImport.PackagesRoot = root.File("Packages");
        Check.NotNull(SettingsImport.ImportNewest());
        var imported = Settings.Load();
        Check.Equal(@"C:\Users\Someone\Videos\Clink", imported.ClipsFolder, "the import brings the other copy's paths");
        Dev(home).Isolate(imported, freshStore: true);
        Check.Equal(Path.Combine(home.Path, "clips"), imported.ClipsFolder);
        Check.False(imported.ArchiveEnabled);
        Check.Null(imported.CopiumToken);

        // twice is the same as once
        var again = Realistic();
        dev.Isolate(again, true);
        string once = again.ToJson();
        dev.Isolate(again, true);
        Check.Equal(once, again.ToJson());
    }

    [UnitTest]
    public static void A_dev_instance_compares_the_other_copy_as_it_would_be_after_the_guards()
    {
        // Otherwise the clips folder, the archive, run-at-startup and updates, which the guards always change, would differ for ever,
        // and the same copy would be offered again after every import.
        using var dir = new TempDir();
        var dev = Dev(dir);
        var ours = Settings.FromJson("{}");
        ours.ArchiveAfterDays = 1;   // the one archive setting the guards leave alone, and Realistic() has
        dev.Isolate(ours, freshStore: false);
        var theirs = Realistic();
        theirs.Codec = "hevc";
        string o = ours.ToJson(), t = theirs.ToJson();

        string raw = string.Join(",", SettingsImport.Differences(o, t));
        foreach (string label in new[] { "video quality", "clips folder", "archive", "startup", "updates", "Copium token" })
            Check.Contains(raw, label);
        Check.Equal(new[] { "video quality" }, SettingsImport.Differences(o, t, s => dev.Isolate(s, freshStore: false)).ToArray());

        // and Scan uses the guard it is given: with it nothing is left to offer once only the guarded settings differ
        using var root = new TempDir();
        string folder = root.File(@"Packages\Notes_x\LocalCache\Roaming\Clink");
        Directory.CreateDirectory(folder);
        var onlyGuarded = Realistic();   // differs from ours in nothing but what the guards change
        File.WriteAllText(Path.Combine(folder, "settings.json"), onlyGuarded.ToJson());
        SettingsImport.PackagesRoot = root.File("Packages");
        SettingsImport.Scan(ours).Wait();
        Check.NotNull(SettingsImport.Pending);
        SettingsImport.Reset();
        SettingsImport.PackagesRoot = root.File("Packages");
        SettingsImport.ForeignGuard = s => dev.Isolate(s, freshStore: false);
        SettingsImport.Scan(ours).Wait();
        Check.Null(SettingsImport.Pending);
    }

    // ------------------------------------------------------------------ not recording, not updating

    [UnitTest]
    public static void A_dev_instance_starts_paused_unless_it_is_asked_to_record()
    {
        Check.False(AppController.StartsPaused, "a normal Clink records");
        AppController.InstanceSuffix = ".dev.test";
        Check.True(AppController.StartsPaused, "a dev instance does not record next to the user's game");
        AppController.DevRecord = true;
        Check.False(AppController.StartsPaused, "--dev-record");
    }

    [UnitTest]
    public static void The_updater_does_nothing_in_a_dev_instance()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        string real = Path.Combine(Updater.UpdatesDir, "Clink-9.9.9.exe");   // the real Clink's pending download, say
        File.WriteAllText(real, "exe");
        var settings = Settings.FromJson("""{ "UpdateRepo": "owner/repo", "AutoUpdate": true }""");

        var normal = new Updater(settings);
        Check.True(normal.UpdatesEnabled, "outside --dev a build with a signed feed updates");

        Check.Null(normal.LastError, "a normal Clink starts without a complaint");
        AppController.InstanceSuffix = ".dev.test";
        var up = new Updater(settings);
        Check.Equal("updates are off in dev mode", up.LastError, "the window shows this in the Updates card from the start");
        Check.True(up.CheckFailed);
        Check.False(up.UpdatesEnabled, "a dev instance never updates, whatever the feed says");
        Check.False(up.TryGetPendingUpdate(out _, out _));
        var info = up.CheckNowAsync().GetAwaiter().GetResult();   // returns at once: nothing is awaited
        Check.Null(info);
        Check.True(up.CheckFailed);
        Check.Equal(Updater.DevModeNote.ToLowerInvariant(), up.LastError);
        Check.Contains(up.LastError, "dev mode");
        Check.Throws<InvalidOperationException>(() => up.LaunchApply(real, @"C:\x\Clink.exe", Array.Empty<string>()));
        Check.Throws<InvalidOperationException>(() => up.PrepareApply(real));
        var release = new UpdateInfo(new ReleaseVersion(new Version(9, 9, 9), null), "v9.9.9", "https://example.invalid/Clink.exe", 1, new string('a', 64), "");
        Check.Throws<InvalidOperationException>(() => up.DownloadAsync(release, null, CancellationToken.None).GetAwaiter().GetResult());
        Check.Equal("exe", File.ReadAllText(real), "what is in the updates folder is left alone");
        Check.Equal(1, Directory.GetFileSystemEntries(Updater.UpdatesDir).Length);
    }

    [UnitTest]
    public static void Every_restart_of_a_dev_instance_carries_its_dev_switches()
    {
        AppController.InstanceSuffix = ".dev.test";
        Settings.DirOverride = @"C:\dev one";
        AppController.DevHotkeys = true;
        AppController.DevToken = true;
        AppController.DevRecord = false;
        AppController.DevAcceptImport = true;
        SettingsImport.PackagesRoot = @"D:\fake";

        // the same, however it was started: --dev <folder>, --dev=<folder>, with the one-start flag or without
        var expected = new[] { "--show", "--dev", @"C:\dev one", "--dev-hotkeys", "--dev-token", "--packages-root", @"D:\fake" };
        Check.Equal(expected, AppController.RestartArguments(new[] { "--dev", @"C:\dev one", "--dev-hotkeys", "--dev-token", "--packages-root", @"D:\fake", "--dev-accept-import" }).ToArray());
        Check.Equal(expected, AppController.RestartArguments(new[] { @"--dev=C:\dev one", "--minimized", "--dev-accept-import" }).ToArray());
        Check.Equal(expected, AppController.RestartArguments(new[] { "--show", @"--dev=C:\dev one" }).ToArray());
        Check.Equal(new[] { "--dev", @"C:\dev one", "--dev-hotkeys", "--dev-token", "--packages-root", @"D:\fake" }, AppController.DevSwitches().ToArray(), "what UpdateNow adds");

        // the parser reads the restarted command line as the same dev instance
        var again = DevOptions.Parse(AppController.RestartArguments(new[] { "--dev", @"C:\dev one" }).ToArray())!;
        Check.Equal(@"C:\dev one", again.Folder);
        Check.True(again.BindHotkeys && again.AllowToken && !again.Record && !again.AcceptImport);
        Check.Equal(@"D:\fake", again.PackagesRoot);

        // --dev-record is carried too
        AppController.DevRecord = true;
        Check.Contains(string.Join(' ', AppController.RestartArguments(new[] { "--dev", @"C:\dev one" })), "--dev-record");
    }

    // ------------------------------------------------------------------ the test runner

    [UnitTest]
    public static void The_updates_folder_in_a_test_is_a_scratch_folder_not_the_real_Clinks()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink", "updates");
        Check.NotNull(Updater.UpdatesDirOverride);
        Check.NotEqual(real, Updater.UpdatesDir);
        Check.False(Updater.UpdatesDir.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink"), StringComparison.OrdinalIgnoreCase));
    }

    // Two tests in a row (they run in name order): the first changes the dev state and the paths, the second finds everything put back.
    [UnitTest]
    public static void The_runner_puts_the_dev_state_back_1_change_everything()
    {
        AppController.InstanceSuffix = ".dev.leftover";
        AppController.DevHotkeys = true;
        AppController.DevToken = true;
        AppController.DevRecord = true;
        AppController.DevAcceptImport = true;
        Settings.DirOverride = @"C:\leftover";
        ClipLibrary.IndexPathOverride = @"C:\leftover\clips.json";
        Updater.UpdatesDirOverride = @"C:\leftover\updates";
        SettingsImport.PackagesRoot = @"C:\leftover\Packages";
        Settings.RealDirOverride = @"C:\leftover\RealClink";
        Settings.FreezeSaves();
    }

    [UnitTest]
    public static void The_runner_puts_the_dev_state_back_2_nothing_is_left()
    {
        Check.Equal("", AppController.InstanceSuffix);
        Check.False(AppController.IsDev);
        Check.False(AppController.DevHotkeys || AppController.DevToken || AppController.DevRecord || AppController.DevAcceptImport);
        Check.False(Settings.DirOverride!.Contains("leftover"));
        Check.False(ClipLibrary.IndexPathOverride!.Contains("leftover"));
        Check.False(Updater.UpdatesDirOverride!.Contains("leftover"));
        Check.False(SettingsImport.PackagesRoot!.Contains("leftover"));
        Check.False(Settings.SavesFrozen);
        Check.False(Settings.RealDir.Contains("leftover"));
    }

    [UnitTest]
    public static void The_runner_points_the_other_apps_packages_and_the_real_Clink_at_scratch_folders()
    {
        // A test that forgets to set a packages root cannot read the packages of whoever runs it, nor the real settings.json.
        string temp = Path.Combine(Path.GetTempPath(), "clink-unittest");
        string realPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        Check.NotNull(SettingsImport.PackagesRoot);
        Check.True(SettingsImport.PackagesRoot!.StartsWith(temp, StringComparison.OrdinalIgnoreCase), SettingsImport.PackagesRoot);
        Check.NotEqual(realPackages, SettingsImport.PackagesRoot);
        Check.Equal(0, SettingsImport.FindForeignCopies().Count, "nothing is there");
        Check.NotNull(Settings.RealDirOverride);
        Check.True(Settings.RealDir.StartsWith(temp, StringComparison.OrdinalIgnoreCase), Settings.RealDir);
        Check.NotEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink"), Settings.RealDir);
    }
}
