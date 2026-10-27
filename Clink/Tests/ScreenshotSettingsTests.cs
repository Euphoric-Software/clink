using System.IO;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The screenshot settings: defaults, Normalize, the one-time offer of the PrintScreen hotkey (no schema change), the dev folder and the import labels.</summary>
internal static class ScreenshotSettingsTests
{
    private static Settings LoadFrom(TempDir dir, string? json)
    {
        Settings.DirOverride = dir.Path;
        Settings.SeedDefaults = true;   // the runner turns it off for the older tests; these are about it
        if (json != null) File.WriteAllText(dir.File("settings.json"), json);
        return Settings.Load();
    }

    private static string[] Rows(Settings s) =>
        s.Hotkeys.Select(h => $"{(h.Keys.Vk > 0 ? h.Keys.ToString() : "unset")} {h.Action} {h.Seconds}").ToArray();

    private const string SchemaThreeFile = """
        { "SchemaVersion": 3, "Hotkeys": [
            { "Keys": { "Alt": true, "Vk": 67 }, "Action": "clip", "Seconds": 0 },
            { "Keys": { "Shift": true, "Vk": 120 }, "Action": "clip", "Seconds": 30 } ] }
        """;

    // ------------------------------------------------------------------ defaults and Normalize

    [UnitTest]
    public static void The_schema_is_still_3_because_the_default_hotkeys_are_seeded_not_migrated()
    {
        Check.Equal(3, Settings.CurrentSchema);
        using var dir = new TempDir();
        var s = LoadFrom(dir, SchemaThreeFile);
        Check.Equal(3, s.SchemaVersion);
        s.Save();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dir.File("settings.json")));
        Check.Equal(3, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
        Check.False(File.Exists(dir.File("settings.pre-v3.json")), "a schema-3 file is not an older file: no pre-v3 copy");
    }

    [UnitTest]
    public static void The_screenshot_settings_start_on()
    {
        var s = Settings.FromJson("{}");
        Check.True(s.ScreenshotsEnabled, "the master switch");
        Check.Null(s.ScreenshotsFolder);
        Check.Equal("none", s.ScreenshotMode);
        Check.True(s.ScreenshotCopyImage);
        Check.True(s.ScreenshotLoupe);
        Check.True(s.ToastScreenshot.Enabled);
        Check.Equal(6, s.ToastScreenshot.Seconds);
        Check.Equal(0, s.SeededHotkeyDefaults.Count);
        Check.Equal("none", new Settings().ScreenshotMode, "a fresh Settings object agrees");
    }

    [UnitTest]
    public static void Normalize_puts_screenshot_settings_that_make_no_sense_back_to_their_defaults()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """
            { "ScreenshotMode": "sideways", "ScreenshotsFolder": "   ", "ToastScreenshot": null, "SeededHotkeyDefaults": null }
            """);
        Check.Equal("none", s.ScreenshotMode, "an unknown mode");
        Check.Null(s.ScreenshotsFolder, "blank = the default folder");
        Check.True(s.ToastScreenshot.Enabled);
        Check.Equal(6, s.ToastScreenshot.Seconds);
        Check.NotNull(s.SeededHotkeyDefaults);

        Check.Equal("after", LoadFrom(dir, """{ "ScreenshotMode": " After " }""").ScreenshotMode, "known modes are kept, tidied");
        Check.Equal("none", LoadFrom(dir, """{ "ScreenshotMode": "" }""").ScreenshotMode);
        Check.Equal("none", LoadFrom(dir, """{ "ScreenshotMode": null }""").ScreenshotMode);
        Check.Equal(@"E:\Shots", LoadFrom(dir, """{ "ScreenshotsFolder": "E:\\Shots" }""").ScreenshotsFolder);
        Check.False(LoadFrom(dir, """{ "ScreenshotsEnabled": false }""").ScreenshotsEnabled, "the switch is kept");
    }

    [UnitTest]
    public static void Normalize_drops_a_screenshots_folder_that_is_not_a_full_path()
    {
        // At sign-in the current directory is System32: a relative folder would put screenshots there.
        using var dir = new TempDir();
        foreach (string bad in new[] { "Shots", @"..\Shots", @"\Shots", "C:Shots", "C:" })
        {
            string json = "{ \"ScreenshotsFolder\": \"" + bad.Replace(@"\", @"\\") + "\" }";
            Check.Null(LoadFrom(dir, json).ScreenshotsFolder, $"'{bad}' from settings.json");
            Check.Null(Settings.FromJson(json).ScreenshotsFolder, $"'{bad}' from an imported copy");
        }
        Check.Equal(@"D:\Shots", Settings.FromJson("""{ "ScreenshotsFolder": "D:\\Shots" }""").ScreenshotsFolder);
        Check.Equal(@"\\nas\share\Shots", Settings.FromJson("""{ "ScreenshotsFolder": "\\\\nas\\share\\Shots" }""").ScreenshotsFolder, "a UNC path is a full path");
        var s = Settings.FromJson("""{ "ScreenshotsFolder": "Shots", "ClipsFolder": "D:\\Clips" }""");
        Check.Equal(@"D:\Clips\Screenshots", Clink.Screenshots.ScreenshotService.FolderFor(s), "the default instead");
    }

    // ------------------------------------------------------------------ seeding PrintScreen

    [UnitTest]
    public static void A_fresh_install_and_an_existing_file_get_a_PrintScreen_screenshot_row_once()
    {
        using var dir = new TempDir();
        var fresh = LoadFrom(dir, null);
        Check.Equal(new[] { "Alt+C clip 0", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(fresh));
        Check.Equal(new[] { "screenshot", "screenshot-edit-last" }, fresh.SeededHotkeyDefaults.ToArray());
        Check.True(fresh.Migrated, "so the caller saves it");
        fresh.Save();

        var again = Settings.Load();
        Check.Equal(new[] { "Alt+C clip 0", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(again), "a second load adds nothing");
        Check.False(again.Migrated, "and has nothing to save");

        using var dir2 = new TempDir();
        var existing = LoadFrom(dir2, SchemaThreeFile);
        Check.Equal(new[] { "Alt+C clip 0", "Shift+F9 clip 30", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(existing), "appended after the user's rows; the main is untouched");
        Check.True(existing.Migrated);
        Check.False(existing.MainHotkeyAssigned || existing.MainHotkeyNeedsSetup, "no main-hotkey notice");
        existing.Save();
        Check.Equal(Rows(existing), Rows(Settings.Load()));
    }

    /// <summary>The same file loaded without seeding (what the hotkey model alone makes of it).</summary>
    private static Settings LoadUnseeded(TempDir dir, string json)
    {
        Settings.DirOverride = dir.Path;
        Settings.SeedDefaults = false;
        File.WriteAllText(dir.File("settings.json"), json);
        try { return Settings.Load(); }
        finally { Settings.SeedDefaults = true; }
    }

    [UnitTest]
    public static void Older_schema_files_keep_their_main_hotkey_rules_and_get_the_PrintScreen_row_after_them()
    {
        foreach (var (name, json, expected) in new (string, string, string[])[]
        {
            // v0.1: one Hotkey, no list: the main is that key
            ("schema 1", """{ "Hotkey": { "Ctrl": true, "Vk": 120 } }""", new[] { "Ctrl+F9 clip 0", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }),
            // v0.2: a full-replay row below a shorter one is promoted to the main
            ("schema 2, promoted", """{ "Hotkeys": [ { "Keys": { "Shift": true, "Vk": 120 }, "Action": "clip", "Seconds": 30 }, { "Keys": { "Ctrl": true, "Vk": 121 }, "Action": "clip", "Seconds": 0 } ] }""",
                new[] { "Ctrl+F10 clip 0", "Shift+F9 clip 30", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }),
            // v0.2: no full-replay row: the main is added on Alt+C (MainHotkeyAssigned)
            ("schema 2, assigned", """{ "Hotkeys": [ { "Keys": { "Shift": true, "Vk": 120 }, "Action": "clip", "Seconds": 30 } ] }""",
                new[] { "Alt+C clip 0", "Shift+F9 clip 30", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }),
            // v0.2: Alt+C is taken by a pause row: the main is left unset (MainHotkeyNeedsSetup)
            ("schema 2, needs setup", """{ "Hotkeys": [ { "Keys": { "Alt": true, "Vk": 67 }, "Action": "pause", "Seconds": 0 } ] }""",
                new[] { "unset clip 0", "Alt+C pause 0", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }),
        })
        {
            using var plainDir = new TempDir();
            var plain = LoadUnseeded(plainDir, json);
            using var dir = new TempDir();
            var s = LoadFrom(dir, json);
            Check.Equal(expected, Rows(s), name);
            Check.Equal(Rows(plain).Concat(new[] { "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }).ToArray(), Rows(s), name + ": the hotkey model's rows, then the PrintScreen rows");
            Check.Equal((plain.MainHotkeyAssigned, plain.MainHotkeyNeedsSetup), (s.MainHotkeyAssigned, s.MainHotkeyNeedsSetup), name + ": the main-hotkey notices are unchanged");
            Check.True(s.SchemaMigrated && s.Migrated, name + ": an older file is migrated");
            Check.Equal(new[] { "screenshot", "screenshot-edit-last" }, s.AddedHotkeyDefaults.ToArray(), name);
            Check.NotNull(AppController.SeededHotkeyNotice(s), name + ": the user is told about PrintScreen");

            s.Save();
            string preV3 = dir.File("settings.pre-v3.json");
            Check.True(File.Exists(preV3), name + ": the old file is kept");
            Check.Equal(json, File.ReadAllText(preV3), name + ": as it was");
            var again = Settings.Load();
            Check.Equal(expected, Rows(again), name + ": a second load changes nothing");
            Check.False(again.Migrated, name);
            Check.Null(AppController.SeededHotkeyNotice(again), name + ": told once");
            again.Save();
            Check.Equal(json, File.ReadAllText(preV3), name + ": the copy is written once and never replaced");
        }
    }

    [UnitTest]
    public static void Taking_over_PrintScreen_is_announced_once_and_the_log_reason_is_told_apart()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, SchemaThreeFile);
        Check.Equal("PrintScreen now takes a Clink screenshot — change it under Capture → Hotkeys", AppController.SeededHotkeyNotice(s));
        Check.True(s.Migrated, "saved once");
        Check.False(s.SchemaMigrated, "but it is no schema migration: the log says the defaults were offered");
        Check.Equal(new[] { "screenshot", "screenshot-edit-last" }, s.OfferedHotkeyDefaults.ToArray());
        s.Save();
        Check.Null(AppController.SeededHotkeyNotice(Settings.Load()), "not again on the next start");

        using var dir2 = new TempDir();
        var taken = LoadFrom(dir2, """
            { "SchemaVersion": 3, "Hotkeys": [
                { "Keys": { "Alt": true, "Vk": 67 }, "Action": "clip", "Seconds": 0 },
                { "Keys": { "Vk": 44 }, "Action": "record", "Seconds": 0 } ] }
            """);
        Check.Equal(new[] { "screenshot", "screenshot-edit-last" }, taken.OfferedHotkeyDefaults.ToArray(), "offered");
        Check.Equal(new[] { "screenshot-edit-last" }, taken.AddedHotkeyDefaults.ToArray(), "but PrintScreen was theirs; Shift+PrintScreen was free");
        Check.Equal("Shift+PrintScreen now edits your last Clink screenshot — change it under Capture → Hotkeys", AppController.SeededHotkeyNotice(taken), "only the key that was taken is announced");

        using var dir3 = new TempDir();
        var off = LoadFrom(dir3, SchemaThreeFile);
        Check.Equal(new[] { "screenshot", "screenshot-edit-last" }, off.AddedHotkeyDefaults.ToArray(), "seeded as before");
        off.ScreenshotsEnabled = false;
        Check.Null(AppController.SeededHotkeyNotice(off), "tools off: no notice");
    }

    [UnitTest]
    public static void A_PrintScreen_binding_the_user_already_has_is_respected_and_the_offer_is_still_recorded()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """
            { "SchemaVersion": 3, "Hotkeys": [
                { "Keys": { "Alt": true, "Vk": 67 }, "Action": "clip", "Seconds": 0 },
                { "Keys": { "Vk": 44 }, "Action": "record", "Seconds": 0 } ] }
            """);
        Check.Equal(new[] { "Alt+C clip 0", "PrintScreen record 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(s), "their PrintScreen stays what it was");
        Check.Equal(new[] { "screenshot", "screenshot-edit-last" }, s.SeededHotkeyDefaults.ToArray(), "offered once, even though it could not be added");
        s.Hotkeys.RemoveAt(1);
        s.Save();
        Check.Equal(new[] { "Alt+C clip 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(Settings.Load()), "freeing the key later does not bring the offer back");
    }

    [UnitTest]
    public static void A_screenshot_row_the_user_has_on_other_keys_is_enough()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """
            { "SchemaVersion": 3, "Hotkeys": [
                { "Keys": { "Alt": true, "Vk": 67 }, "Action": "clip", "Seconds": 0 },
                { "Keys": { "Ctrl": true, "Vk": 44 }, "Action": "Screenshot", "Seconds": 0 } ] }
            """);
        Check.Equal(new[] { "Alt+C clip 0", "Ctrl+PrintScreen Screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(s), "no second screenshot row");
        Check.Equal(2, s.SeededHotkeyDefaults.Count);
    }

    [UnitTest]
    public static void A_deleted_screenshot_row_stays_deleted_across_restarts()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, SchemaThreeFile);
        Check.Equal(4, s.Hotkeys.Count);
        s.Hotkeys.RemoveRange(2, 2);   // the user removes the PrintScreen rows in the hotkey editor
        s.Save();
        for (int i = 0; i < 3; i++)
        {
            var reloaded = Settings.Load();
            Check.Equal(new[] { "Alt+C clip 0", "Shift+F9 clip 30" }, Rows(reloaded), $"restart {i + 1}");
            Check.False(reloaded.Migrated);
            reloaded.Save();
        }
        // a settings file that already lists the actions as offered never gets them
        var listed = LoadFrom(dir, SchemaThreeFile.Replace("\"SchemaVersion\": 3,", "\"SchemaVersion\": 3, \"SeededHotkeyDefaults\": [\"screenshot\", \"screenshot-edit-last\"],"));
        Check.Equal(2, listed.Hotkeys.Count);
        Check.False(listed.Migrated, "nothing changed, nothing to save");
        // a file from the first screenshot version lists only "screenshot": the newer action is offered once
        var s1 = LoadFrom(dir, SchemaThreeFile.Replace("\"SchemaVersion\": 3,", "\"SchemaVersion\": 3, \"SeededHotkeyDefaults\": [\"screenshot\"],"));
        Check.Equal(new[] { "Alt+C clip 0", "Shift+F9 clip 30", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(s1), "edit-last is added, screenshot is not");
        Check.Equal(new[] { "screenshot-edit-last" }, s1.AddedHotkeyDefaults.ToArray());
        Check.True(s1.Migrated, "saved once");
    }

    [UnitTest]
    public static void Seeding_is_part_of_Load_and_of_the_import_comparison_but_not_of_Normalize()
    {
        Settings.SeedDefaults = true;
        var s = new Settings();
        s.Normalize();
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(s), "Normalize alone adds nothing");
        Check.Equal(new[] { "Alt+C clip 0", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(Settings.FromJson("{}")), "FromJson reads a copy as Load would");

        Settings.SeedDefaults = false;   // the seam the test runner uses
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(Settings.FromJson("{}")));
    }

    [UnitTest]
    public static void The_screenshot_hotkey_survives_a_save_and_a_load_with_its_action_text()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, null);
        s.ScreenshotsEnabled = false;
        s.ScreenshotMode = "after";
        s.ScreenshotsFolder = @"E:\Shots";
        s.ScreenshotLoupe = false;
        s.Save();
        var back = Settings.Load();
        Check.Equal(new[] { "Alt+C clip 0", "PrintScreen screenshot 0", "Shift+PrintScreen screenshot-edit-last 0" }, Rows(back));
        Check.False(back.ScreenshotsEnabled);
        Check.Equal("after", back.ScreenshotMode);
        Check.Equal(@"E:\Shots", back.ScreenshotsFolder);
        Check.False(back.ScreenshotLoupe);
        Check.True(back.ScreenshotCopyImage);
    }

    // ------------------------------------------------------------------ dev folder

    [UnitTest]
    public static void A_dev_instance_does_not_keep_a_screenshots_folder_outside_its_own_folder()
    {
        using var dir = new TempDir();
        var dev = DevOptions.Parse(new[] { "--dev", dir.Path })!;
        var s = Settings.FromJson("{}");
        s.ScreenshotsFolder = @"C:\Users\Someone\Pictures\Shots";
        var changed = string.Join("; ", dev.Isolate(s, freshStore: false));
        Check.Null(s.ScreenshotsFolder, "back to the default, which is inside the (redirected) clips folder");
        Check.Contains(changed, "screenshots folder");
        Check.DoesNotContain(changed, "Pictures", "no path of the real world in the log line");
        Check.Equal(Path.Combine(dir.Path, "clips", "Screenshots"), Clink.Screenshots.ScreenshotService.FolderFor(s));

        s.ScreenshotsFolder = Path.Combine(dir.Path, "my shots");
        dev.Isolate(s, freshStore: false);
        Check.Equal(Path.Combine(dir.Path, "my shots"), s.ScreenshotsFolder, "a folder of its own is kept");

        var untouched = Settings.FromJson("{}");
        Check.False(string.Join("; ", dev.Isolate(untouched, freshStore: false)).Contains("screenshots folder"), "nothing to say when it was the default");
        Check.Null(untouched.ScreenshotsFolder);
    }

    // ------------------------------------------------------------------ import labels

    private static string Json(Action<Settings>? change = null)
    {
        var s = Settings.FromJson("{}");
        change?.Invoke(s);
        return s.ToJson();
    }

    [UnitTest]
    public static void Import_names_a_changed_screenshot_setting_screenshots_and_the_toast_alerts()
    {
        foreach (var change in new Action<Settings>[]
        {
            s => s.ScreenshotsEnabled = false, s => s.ScreenshotsFolder = @"E:\Shots", s => s.ScreenshotMode = "after",
            s => s.ScreenshotCopyImage = false, s => s.ScreenshotLoupe = false,
        })
            Check.Equal(new[] { "screenshots" }, SettingsImport.Differences(Json(), Json(change)).ToArray());
        // the editor's last-used colour and width say nothing: two installs do not differ over them
        Check.Equal(0, SettingsImport.Differences(Json(), Json(s => { s.ScreenshotEditor.Width = 9; s.ScreenshotEditor.TextBackdrop = "box"; })).Count());
        Check.Equal(new[] { "alerts" }, SettingsImport.Differences(Json(), Json(s => s.ToastScreenshot.Seconds = 12)).ToArray());
        Check.Equal(new[] { "alerts" }, SettingsImport.Differences(Json(), Json(s => s.ToastScreenshot.Enabled = false)).ToArray());
        Check.Equal(0, SettingsImport.Differences(Json(), Json(s => s.SeededHotkeyDefaults.Add("screenshot"))).Count, "which defaults were offered says nothing");
        Check.Equal(new[] { "alerts", "screenshots" }, SettingsImport.Differences(Json(), Json(s => { s.ScreenshotLoupe = false; s.ToastScreenshot.Seconds = 2; })).ToArray(), "listed in the label order");
    }

    [UnitTest]
    public static void Every_setting_a_file_can_hold_has_an_import_label_or_is_volatile()
    {
        // A setting in neither list shows up as "other settings" in the import offer. Found by reflection, as Settings binds a file:
        // public with a public setter, not [JsonIgnore], not the extension data. So a setting added later cannot be forgotten.
        var bindable = typeof(Settings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic && p.GetIndexParameters().Length == 0
                        // the same rule as Settings.Bindable: only an unconditional [JsonIgnore] keeps a member from being read
                        && System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>(p)
                            is not { Condition: System.Text.Json.Serialization.JsonIgnoreCondition.Always }
                        && !p.IsDefined(typeof(System.Text.Json.Serialization.JsonExtensionDataAttribute), true))
            .Select(p => p.Name).ToList();
        Check.True(bindable.Count > 40, $"found {bindable.Count} settings");
        Check.True(bindable.Contains(nameof(Settings.ScreenshotsFolder)) && bindable.Contains(nameof(Settings.ToastScreenshot)), "the screenshot settings are among them");
        Check.Equal(Array.Empty<string>(), bindable.Where(n => !SettingsImport.HasLabelOrIsVolatile(n)).ToArray(), "settings with no label");
        Check.False(SettingsImport.HasLabelOrIsVolatile("FutureKey"), "a key this version does not know is \"other settings\"");
    }
}
