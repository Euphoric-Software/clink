using System.Diagnostics;
using System.IO;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// The import and the restart as the running Clink does them: what is frozen and stopped before the other copy's settings are taken
/// over, that the new Clink is started only afterwards and waits for this one, what happens when it cannot be started, that a second
/// import is refused, when an import is offered or made on its own, a dev instance starting paused, and where a folder really is.
/// </summary>
internal static class ImportFlowTests
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

    private static string Json(Action<Settings>? change = null)
    {
        var s = Settings.FromJson("{}");
        s.ClickSound = false;   // a refused clip must not beep in the middle of a test run
        change?.Invoke(s);
        return s.ToJson();
    }

    private static Settings LoadFrom(TempDir dir, string? json)
    {
        Settings.DirOverride = dir.Path;
        if (json != null) File.WriteAllText(dir.File("settings.json"), json);
        return Settings.Load();
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

    /// <summary>What the controller's fake "start the new Clink" saw at the moment it was asked to start it.</summary>
    private sealed record AtStart(bool SettingsFrozen, bool LibraryFrozen, string SettingsText, string ClipsText, bool ImportInProgress);

    /// <summary>A controller with settings, a copy of another Clink's settings to import, and a fake way to start and end processes.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly TempDir Home = new(), Root = new();
        public readonly Settings Ours;
        public readonly AppController App;
        public readonly List<ProcessStartInfo> Started = new();
        public AtStart? Seen;
        public int Exits;
        public bool FailStart;

        public Rig(Action<Settings>? ours = null, string? theirs = null, string? theirClips = null)
        {
            Ours = LoadFrom(Home, Json(ours ?? (s => s.Codec = "h264")));
            ClipLibrary.IndexPathOverride = Home.File("clips.json");
            FakeCopy(Root, "Foo_abc", theirs ?? Json(s => s.Codec = "hevc"), theirClips);
            SettingsImport.PackagesRoot = Root.File("Packages");
            App = new AppController(Ours, new Updater(Ours))
            {
                CommandLine = new[] { "--minimized", "--no-update" },
                ExitForRestart = () => Exits++,
                StartProcess = psi =>
                {
                    Started.Add(psi);
                    Seen = new AtStart(Settings.SavesFrozen, App!.Library.SavesFrozen, File.ReadAllText(Home.File("settings.json")),
                        File.Exists(Home.File("clips.json")) ? File.ReadAllText(Home.File("clips.json")) : "", App.ImportInProgress);
                    if (FailStart) throw new InvalidOperationException("no memory for another Clink");
                },
            };
        }

        public ForeignSettings Copy => SettingsImport.FindForeignCopies(Root.Path).Single();
        public string[] BackupNames => Directory.GetFiles(Home.Path, "settings.pre-import*").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

        public void Dispose()
        {
            App.Dispose();
            Home.Dispose();
            Root.Dispose();
        }
    }

    private static string[] PathsIn(string clipsJsonFile)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(clipsJsonFile));
        return doc.RootElement.EnumerateArray().Select(c => c.GetProperty("Path").GetString()!).ToArray();
    }

    // ------------------------------------------------------------------ the sequence

    [UnitTest]
    public static void An_import_stops_what_writes_imports_and_only_then_starts_the_new_Clink()
    {
        using var rig = new Rig(theirClips: """[ { "Path": "C:\\clips\\theirs.mp4", "CopiumLink": "https://theirs/t" } ]""");
        rig.App.Library.Add(@"C:\clips\mine.mp4", 10, 1000, "Game", new[] { "Mix" });   // written to clips.json now, before anything is frozen
        string before = File.ReadAllText(rig.Home.File("settings.json"));

        rig.App.ImportForeignSettings(rig.Copy);

        // the new Clink was started once, after the import, at a moment when this one could no longer write the files
        Check.Equal(1, rig.Started.Count);
        Check.NotNull(rig.Seen);
        Check.True(rig.Seen!.SettingsFrozen, "settings saves were frozen");
        Check.True(rig.Seen.LibraryFrozen, "so were the clip index's");
        Check.True(rig.Seen.ImportInProgress);
        Check.Equal("hevc", Settings.FromJson(rig.Seen.SettingsText).Codec, "the other copy's settings were already in place");
        Check.Contains(rig.Seen.ClipsText, "theirs.mp4");
        Check.Contains(rig.Seen.ClipsText, "mine.mp4");
        Check.Equal(1, rig.Exits, "and this process ends");

        // started like this one, once-only options left out, waiting for this process
        Check.Equal(new[] { "--show", "--after-pid", Environment.ProcessId.ToString() }, rig.Started[0].ArgumentList.ToArray());
        Check.Equal(Environment.ProcessPath, rig.Started[0].FileName);
        Check.False(rig.Started[0].UseShellExecute);

        // what was replaced is kept
        Check.Equal(new[] { "settings.pre-import.json" }, rig.BackupNames);
        Check.Equal(before, File.ReadAllText(rig.Home.File("settings.pre-import.json")));

        // a second import is refused while the first is under way: one new Clink, one exit, one backup
        Check.True(rig.App.ImportInProgress);
        rig.App.ImportForeignSettings(rig.Copy);
        Check.Equal(1, rig.Started.Count);
        Check.Equal(1, rig.Exits);
        Check.Equal(new[] { "settings.pre-import.json" }, rig.BackupNames);
        Check.Equal("An import is already under way", rig.App.LastNotice);

        // nothing this process still does can put the old files back: a clip that was being saved, a save of the settings
        string clips = File.ReadAllText(rig.Home.File("clips.json"));
        rig.App.Library.Add(@"C:\clips\late.mp4", 5, 500, null, Array.Empty<string>());
        Check.Equal(clips, File.ReadAllText(rig.Home.File("clips.json")), "the clip index is not written any more");
        Check.NotNull(rig.App.Library.Find(@"C:\clips\late.mp4"), "the clip is still in memory");
        rig.Ours.Fps = 24;
        rig.Ours.Save();
        Check.Equal("hevc", Settings.Load().Codec, "the settings file is still the imported one");
        Check.NotEqual(24, Settings.Load().Fps);

        // no clip is saved and the recorder is not started again while the process ends
        rig.App.ClipNow();
        Check.Equal("Clink is restarting", rig.App.LastNotice);
        rig.App.StartEngine();
        Check.False(rig.App.Engine.IsRunning);
    }

    [UnitTest]
    public static void When_the_new_Clink_cannot_be_started_this_one_carries_on_with_the_settings_it_had()
    {
        using var rig = new Rig();
        rig.FailStart = true;
        string before = File.ReadAllText(rig.Home.File("settings.json"));

        rig.App.ImportForeignSettings(rig.Copy);

        Check.Equal(1, rig.Started.Count, "it tried");
        Check.Equal(0, rig.Exits, "and did not end");
        Check.False(Settings.SavesFrozen, "saves are thawed");
        Check.False(rig.App.Library.SavesFrozen);
        Check.False(rig.App.ImportInProgress, "a new import can be made");
        Check.Contains(rig.App.LastNotice, "Clink could not restart itself");
        Check.Contains(rig.App.LastNotice, "no memory for another Clink");
        Check.Contains(rig.App.LastNotice, "your settings are as they were");

        // what is saved from now on is what runs: the imported file is put back to the settings this Clink has, not left as a trap
        Check.Equal("h264", Settings.Load().Codec);
        rig.Ours.Fps = 24;
        rig.Ours.Save();
        Check.Equal(24, Settings.Load().Fps, "saves work again");
        Check.Equal("h264", Settings.Load().Codec);
        _ = before;

        // the clip index too
        rig.App.Library.Add(@"C:\clips\after.mp4", 5, 500, null, Array.Empty<string>());
        Check.Contains(File.ReadAllText(rig.Home.File("clips.json")), "after.mp4");

        // the import is offered again at once (the App tab's banner looks at Pending as soon as the call returns), and it can be made
        Check.NotNull(SettingsImport.Pending);
        Check.Equal("Foo_abc", SettingsImport.Pending!.Package);
        rig.FailStart = false;
        rig.App.ImportForeignSettings(SettingsImport.Pending!);
        Check.Equal(2, rig.Started.Count);
        Check.Equal(1, rig.Exits);
        Check.True(rig.App.ImportInProgress);
        Check.Equal("hevc", Settings.Load().Codec);
        Check.Equal(2, rig.BackupNames.Length, "each import kept what it replaced, beside the other");
    }

    [UnitTest]
    public static void An_import_that_fails_changes_nothing_and_Clink_carries_on()
    {
        using var rig = new Rig(theirs: "{ this is not json");
        string before = File.ReadAllText(rig.Home.File("settings.json"));
        rig.App.Library.Add(@"C:\clips\mine.mp4", 10, 1000, null, Array.Empty<string>());

        rig.App.ImportForeignSettings(rig.Copy);

        Check.Equal(0, rig.Started.Count, "no new Clink");
        Check.Equal(0, rig.Exits);
        Check.False(Settings.SavesFrozen);
        Check.False(rig.App.Library.SavesFrozen);
        Check.False(rig.App.ImportInProgress);
        Check.Contains(rig.App.LastNotice, "Could not import those settings");
        Check.Equal(before, File.ReadAllText(rig.Home.File("settings.json")));
        Check.Equal(0, rig.BackupNames.Length);
        Check.Equal(new[] { @"C:\clips\mine.mp4" }, PathsIn(rig.Home.File("clips.json")));
        rig.App.Library.Add(@"C:\clips\more.mp4", 10, 1000, null, Array.Empty<string>());
        Check.Equal(2, PathsIn(rig.Home.File("clips.json")).Length, "the clip index is written as before");

        // and a copy that can be read is imported next time
        File.WriteAllText(Path.Combine(rig.Copy.Folder, "settings.json"), Json(s => s.Codec = "hevc"));
        rig.App.ImportForeignSettings(rig.Copy);
        Check.Equal(1, rig.Started.Count);
    }

    [UnitTest]
    public static void A_clip_list_that_cannot_be_merged_is_said_before_the_restart_and_left_to_the_next_start()
    {
        using var rig = new Rig(theirClips: """[ { "Path": "C:\\clips\\theirs.mp4" } ]""");
        File.WriteAllText(rig.Home.File("clips.json"), "{ damaged");

        rig.App.ImportForeignSettings(rig.Copy);

        Check.Equal(1, rig.Started.Count, "the settings are imported and Clink restarts all the same");
        Check.True(rig.App.LastNotice.StartsWith("Settings imported; the clip list could not be merged: ", StringComparison.Ordinal), rig.App.LastNotice);
        Check.NotNull(SettingsImport.PendingClipMerge(), "and the next start tries again");
        Check.Equal("hevc", Settings.Load().Codec);
    }

    // ------------------------------------------------------------------ waiting for the old Clink

    [UnitTest]
    public static void A_restarted_Clink_is_given_the_pid_of_the_one_it_waits_for()
    {
        Check.Equal((int?)1234, AppController.AfterPid(new[] { "--show", "--after-pid", "1234" }));
        Check.Equal((int?)77, AppController.AfterPid(new[] { "--AFTER-PID", "77", "--minimized" }));
        Check.Null(AppController.AfterPid(Array.Empty<string>()));
        Check.Null(AppController.AfterPid(new[] { "--after-pid" }), "no value");
        Check.Null(AppController.AfterPid(new[] { "--after-pid", "soon" }));
        Check.Null(AppController.AfterPid(new[] { "--after-pid", "-5" }));
        Check.Null(AppController.AfterPid(new[] { "--after-pid", "0" }));

        string[] mine = AppController.AfterPidArguments();
        Check.Equal(new[] { "--after-pid", Environment.ProcessId.ToString() }, mine);

        // a restart does not hand on the pid it was given: it waits for the one that restarts it
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(new[] { "--after-pid", "99", "--minimized" }).ToArray());
        Check.Equal(new[] { "--foo", "--show" }, AppController.RestartArguments(new[] { "--foo", "--AFTER-PID", "99" }).ToArray());
        Check.Equal(new[] { "soon", "--show" }, AppController.RestartArguments(new[] { "--after-pid", "soon" }).ToArray(), "the switch goes, a value that is no pid is not swallowed with it");

        // an update's restart waits too, and keeps its window and instance
        SettingsImport.PackagesRoot = null;
        Check.Equal(new[] { "--show", "--after-pid", mine[1] }, AppController.UpdateRestartArguments(windowVisible: true).ToArray());
        Check.Equal(new[] { "--minimized", "--after-pid", mine[1] }, AppController.UpdateRestartArguments(windowVisible: false).ToArray());
        AppController.InstanceSuffix = ".dev.test";
        Settings.DirOverride = @"C:\dev one";
        AppController.DevHotkeys = true;
        Check.Equal(new[] { "--minimized", "--dev", @"C:\dev one", "--dev-hotkeys", "--after-pid", mine[1] }, AppController.UpdateRestartArguments(windowVisible: false).ToArray());
    }

    private static Process? StartSleeper(int pings)
    {
        try
        {
            var psi = new ProcessStartInfo("ping.exe", $"-n {pings} 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("[unittest] ping.exe could not be started: " + ex.Message);
            return null;
        }
    }

    [UnitTest]
    public static void A_restarted_Clink_waits_for_the_old_one_to_end_but_not_for_ever()
    {
        Check.True(AppController.WaitForExit(Environment.ProcessId, TimeSpan.FromSeconds(30)), "this very process: nothing to wait for");
        Check.True(AppController.WaitForExit(int.MaxValue, TimeSpan.FromSeconds(30)), "no such process: nothing to wait for");

        // a process that ends by itself in a second or so is waited for
        using (var brief = StartSleeper(2))
        {
            if (brief != null)
            {
                var sw = Stopwatch.StartNew();
                Check.True(AppController.WaitForExit(brief.Id, TimeSpan.FromSeconds(20)), "it ended");
                Check.True(sw.ElapsedMilliseconds >= 300, "and it was waited for: " + sw.ElapsedMilliseconds + " ms");
                Check.True(brief.HasExited);
            }
        }
        // one that does not is waited for only as long as asked
        var long_ = StartSleeper(30);
        if (long_ == null) return;
        try
        {
            var sw = Stopwatch.StartNew();
            Check.False(AppController.WaitForExit(long_.Id, TimeSpan.FromMilliseconds(300)), "still running when the limit is up");
            Check.True(sw.ElapsedMilliseconds < 8000, "and it did not wait much longer: " + sw.ElapsedMilliseconds + " ms");
            long_.Kill();
            Check.True(AppController.WaitForExit(long_.Id, TimeSpan.FromSeconds(10)), "gone once it is killed");
        }
        finally { try { long_.Kill(); } catch { } long_.Dispose(); }
    }

    /// <summary>Another thread tries to take the mutex (and gives it back): what the next Clink does once it has waited for this one.</summary>
    private static bool Takes(Mutex mutex) => Task.Run(() =>
    {
        if (!mutex.WaitOne(0)) return false;
        mutex.ReleaseMutex();
        return true;
    }).Result;

    [UnitTest]
    public static void The_single_instance_mutex_is_released_before_it_is_closed()
    {
        AppController.InstanceSuffix = ".dev.unittest-" + Guid.NewGuid().ToString("N");   // a name no real Clink has
        Check.True(AppController.AcquireSingleInstance(Array.Empty<string>()));
        using var other = Mutex.OpenExisting(AppController.MutexName);   // whoever else holds the name: the next Clink, waiting for it

        Check.False(Takes(other), "while this thread owns it nobody else can take it");
        AppController.ReleaseSingleInstance();
        Check.True(Takes(other), "released: another thread or process takes it at once, and does not find it abandoned");
        AppController.ReleaseSingleInstance();   // twice is harmless
    }

    [UnitTest]
    public static void The_old_Clink_gives_up_the_mutex_only_once_the_new_one_is_started()
    {
        AppController.InstanceSuffix = ".dev.unittest-" + Guid.NewGuid().ToString("N");
        using var rig = new Rig();
        Check.True(AppController.AcquireSingleInstance(Array.Empty<string>()));
        using var other = Mutex.OpenExisting(AppController.MutexName);

        rig.FailStart = true;
        Check.False(rig.App.RestartClink(out string? error));
        Check.Equal("no memory for another Clink", error);
        Check.Equal(0, rig.Exits, "it carries on");
        Check.False(Takes(other), "and is still the one Clink: nothing was given up for a new one that never started");

        rig.FailStart = false;
        Check.True(rig.App.RestartClink(out error));
        Check.Null(error);
        Check.Equal(1, rig.Exits);
        Check.True(Takes(other), "given up as soon as the new one is started, so it can have it when its wait is over");
        Check.Equal(new[] { "--after-pid", Environment.ProcessId.ToString() }, rig.Started[^1].ArgumentList.Skip(rig.Started[^1].ArgumentList.Count - 2).ToArray());
        Check.True(rig.Started[^1].ArgumentList.Contains("--dev"), "a dev instance comes back as that dev instance");
    }

    // ------------------------------------------------------------------ the clip index

    [UnitTest]
    public static void A_frozen_clip_library_writes_nothing_and_a_thawed_one_writes_everything()
    {
        using var dir = new TempDir();
        ClipLibrary.IndexPathOverride = dir.File("clips.json");
        var library = new ClipLibrary();
        library.Add(@"C:\clips\a.mp4", 1, 1, null, Array.Empty<string>());
        string one = File.ReadAllText(dir.File("clips.json"));
        Check.Contains(one, "a.mp4");

        library.FreezeSaves();
        Check.True(library.SavesFrozen);
        var b = library.Add(@"C:\clips\b.mp4", 2, 2, null, Array.Empty<string>());
        b.CopiumLink = "https://x";
        library.Update(b);
        library.Remove(library.Find(@"C:\clips\a.mp4")!);
        Check.Equal(one, File.ReadAllText(dir.File("clips.json")), "changes in memory, nothing on disk");
        Check.Equal(1, library.Entries.Count);

        library.ThawSaves();
        Check.False(library.SavesFrozen);
        library.Update(b);
        string two = File.ReadAllText(dir.File("clips.json"));
        Check.Contains(two, "b.mp4");
        Check.Contains(two, "https://x");
        Check.DoesNotContain(two, "a.mp4");
    }

    // ------------------------------------------------------------------ when an import is offered, and when it is made on its own

    private static Settings Existing(TempDir dir) => LoadFrom(dir, Json());

    private static Settings Fresh(TempDir dir) => LoadFrom(dir, null);

    /// <summary>Makes the process look as if it ran inside a packaged app (<see cref="AppDataRedirect.Detect"/> on a folder shaped like its private AppData).</summary>
    private static void PretendToBeInsideAPackagedApp(TempDir dir)
    {
        string before = Settings.DirOverride!;
        Settings.DirOverride = dir.File(@"AppData\Local\Packages\Fake_abc\LocalCache\Roaming\Clink");
        Check.True(AppDataRedirect.Detect());
        Settings.DirOverride = before;
    }

    [UnitTest]
    public static void A_Clink_with_settings_of_its_own_looks_for_other_copies_unless_it_is_in_a_packaged_app_or_a_test()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        SettingsImport.PackagesRoot = null;   // the runner points it at a scratch folder; a real run has none
        var existing = Existing(dir);
        Check.True(existing.LoadedFromFile);
        Check.True(SettingsImport.CanOffer(existing, isDev: false), "an existing store of a real Clink");
        Check.False(SettingsImport.CanOffer(Fresh(other), isDev: false), "a first start takes the newest copy over instead (CanTakeOverAtStart)");
        Settings.DirOverride = dir.Path;

        var imported = Existing(dir);
        imported.ImportedFrom = new ForeignSettings("Notes_x", "x", DateTime.UtcNow, Array.Empty<string>());
        Check.False(SettingsImport.CanOffer(imported, isDev: false), "a start that has just taken a copy over has nothing left to offer");

        // a dev instance looks only where --packages-root says
        Check.False(SettingsImport.CanOffer(existing, isDev: true));
        SettingsImport.PackagesRoot = dir.File("fake-packages");
        Check.True(SettingsImport.CanOffer(existing, isDev: true));

        // inside a packaged app, no
        PretendToBeInsideAPackagedApp(dir);
        Check.False(SettingsImport.CanOffer(existing, isDev: false));
        Check.False(SettingsImport.CanOffer(existing, isDev: true));
    }

    [UnitTest]
    public static void The_controller_offers_an_import_by_that_rule()
    {
        using var dir = new TempDir();
        var existing = Existing(dir);
        using var app = new AppController(existing, new Updater(existing));
        SettingsImport.PackagesRoot = null;
        Check.True(app.CanOfferImport);
        AppController.InstanceSuffix = ".dev.test";
        Check.False(app.CanOfferImport, "a dev instance with no --packages-root");
        SettingsImport.PackagesRoot = dir.File("fake-packages");
        Check.True(app.CanOfferImport, "and with one");
        existing.ImportedFrom = new ForeignSettings("Notes_x", "x", DateTime.UtcNow, Array.Empty<string>());
        Check.False(app.CanOfferImport);
    }

    [UnitTest]
    public static void Only_a_first_start_outside_a_packaged_app_takes_a_copy_over_by_itself()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        SettingsImport.PackagesRoot = null;
        Check.True(SettingsImport.CanTakeOverAtStart(freshStore: true, isDev: false), "a first start of a real Clink");
        Check.False(SettingsImport.CanTakeOverAtStart(freshStore: false, isDev: false), "an existing store is only ever offered a copy");
        Check.False(SettingsImport.CanTakeOverAtStart(freshStore: true, isDev: true), "a dev instance has no packages root");
        SettingsImport.PackagesRoot = root.File("Packages");
        Check.True(SettingsImport.CanTakeOverAtStart(freshStore: true, isDev: true), "and with one it is a test of exactly this");
        Check.False(SettingsImport.CanTakeOverAtStart(freshStore: false, isDev: true));

        // the sequence App.RunNormal makes: a fresh store imports the copy that is there, an existing store leaves it
        FakeCopy(root, "Foo_abc", Json(s => s.Codec = "hevc"));
        var fresh = Fresh(home);
        Check.False(fresh.LoadedFromFile);
        Check.True(SettingsImport.CanTakeOverAtStart(!fresh.LoadedFromFile, isDev: false));
        Check.NotNull(SettingsImport.ImportNewest());
        Check.Equal("hevc", Settings.Load().Codec);

        using var existingHome = new TempDir();
        var existing = Existing(existingHome);
        string before = File.ReadAllText(existingHome.File("settings.json"));
        Check.False(SettingsImport.CanTakeOverAtStart(!existing.LoadedFromFile, isDev: false));
        Check.Equal(before, File.ReadAllText(existingHome.File("settings.json")), "nothing was read, nothing imported");

        // inside a packaged app, never, even on a first start
        PretendToBeInsideAPackagedApp(home);
        Check.False(SettingsImport.CanTakeOverAtStart(freshStore: true, isDev: false));
        Check.False(SettingsImport.CanTakeOverAtStart(freshStore: true, isDev: true));
    }

    // ------------------------------------------------------------------ a dev instance starts paused

    [UnitTest]
    public static void A_dev_instance_starts_paused_before_anything_else_and_a_normal_one_does_not()
    {
        // AppController.Start() begins with this step (it then hooks the keyboard, adds the tray icon and starts the recorder, which a unit test must not)
        var settings = Settings.FromJson("{}");
        using (var normal = new AppController(settings, new Updater(settings)))
        {
            normal.ApplyStartPause();
            Check.False(normal.IsPaused, "a normal Clink records");
            Check.NotEqual(RecordingState.Paused, normal.State);
        }

        AppController.InstanceSuffix = ".dev.test";
        using (var dev = new AppController(settings, new Updater(settings)))
        {
            Check.False(dev.IsPaused);
            var logged = Logged(dev.ApplyStartPause);
            Check.True(dev.IsPaused, "a dev instance does not record next to the user's game");
            Check.Equal(RecordingState.Paused, dev.State);
            Check.Equal("Paused", dev.StatusText);
            Check.True(logged.Any(l => l.Contains("dev: paused")));
            dev.StartEngine();
            Check.False(dev.Engine.IsRunning, "and starting the recorder does nothing while it is paused");
            Check.Equal(RecordingState.Paused, dev.State);
        }

        AppController.DevRecord = true;
        using (var recording = new AppController(settings, new Updater(settings)))
        {
            recording.ApplyStartPause();
            Check.False(recording.IsPaused, "--dev-record records");
        }
    }

    // ------------------------------------------------------------------ where a folder really is

    [UnitTest]
    public static void Detect_follows_a_junction_into_a_packaged_apps_private_copy()
    {
        using var dir = new TempDir();
        string privateCopy = dir.File(@"AppData\Local\Packages\Fake_abc\LocalCache\Roaming\Clink");
        string plain = dir.File("an-ordinary-folder");
        Directory.CreateDirectory(privateCopy);
        Directory.CreateDirectory(plain);
        string toPrivate = dir.File("settings-link"), toPlain = dir.File("plain-link");
        if (!TestJunction.TryMake(toPrivate, privateCopy, out string why)) { TestJunction.Skipped(nameof(Detect_follows_a_junction_into_a_packaged_apps_private_copy), why); return; }
        try
        {
            if (!TestJunction.TryMake(toPlain, plain, out why)) { TestJunction.Skipped(nameof(Detect_follows_a_junction_into_a_packaged_apps_private_copy), why); return; }
            try
            {
                // the folder the settings are said to be in is a link: where the files really land is what counts
                Settings.DirOverride = toPrivate;
                Check.True(AppDataRedirect.Detect());
                Check.Equal("Fake_abc", AppDataRedirect.Package);
                Check.Equal("Fake", AppDataRedirect.FriendlyName);
                Check.True(AppDataRedirect.FinalFolder!.EndsWith(@"\LocalCache\Roaming\Clink", StringComparison.OrdinalIgnoreCase), AppDataRedirect.FinalFolder);
                Check.Equal(0, Directory.GetFileSystemEntries(privateCopy).Length, "the probe file is gone with its handle");

                Settings.DirOverride = toPlain;
                Check.False(AppDataRedirect.Detect(), "a link to an ordinary folder is an ordinary folder");
                Check.Null(AppDataRedirect.Package);
            }
            finally { TestJunction.Remove(toPlain); }
        }
        finally { TestJunction.Remove(toPrivate); }
    }

    [UnitTest]
    public static void ResolvedPath_follows_a_junction_and_adds_back_what_does_not_exist_yet()
    {
        using var dir = new TempDir();
        string target = dir.File(@"target\inner");
        Directory.CreateDirectory(target);
        string link = dir.File("link");
        string plainPath = dir.File("not-a-link");
        Directory.CreateDirectory(plainPath);
        Check.Equal(plainPath, AppDataRedirect.ResolvedPath(plainPath), "an ordinary folder is itself, in the C:\\ form");
        Check.Equal(Path.Combine(plainPath, "a", "b"), AppDataRedirect.ResolvedPath(Path.Combine(plainPath, "a", "b")), "and so is what is yet to be made inside it");
        Check.Equal(plainPath, AppDataRedirect.ResolvedPath(plainPath + @"\x\.."), "..");

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char free = "ZYXWVU".FirstOrDefault(c => !used.Contains(c));
        if (free != '\0') Check.Null(AppDataRedirect.ResolvedPath(free + @":\nothing\here"), "no part of it exists");

        if (!TestJunction.TryMake(link, target, out string why)) { TestJunction.Skipped(nameof(ResolvedPath_follows_a_junction_and_adds_back_what_does_not_exist_yet), why); return; }
        try
        {
            Check.Equal(target, AppDataRedirect.ResolvedPath(link));
            Check.Equal(Path.Combine(target, "new", "deeper"), AppDataRedirect.ResolvedPath(Path.Combine(link, "new", "deeper")));
            Directory.CreateDirectory(Path.Combine(target, "made"));
            Check.Equal(Path.Combine(target, "made"), AppDataRedirect.ResolvedPath(Path.Combine(link, "made")), "a folder that exists behind the link");
        }
        finally { TestJunction.Remove(link); }
    }
}
