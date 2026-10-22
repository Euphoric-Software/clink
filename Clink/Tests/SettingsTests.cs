using System.IO;
using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Settings: fresh-install defaults, loading older files, the main-hotkey model, schema 3, the size-target table, --dev.</summary>
internal static class SettingsTests
{
    // A v0.2.4 settings.json shaped like the one a real user has (a ScrollLock 10 s hotkey and an unset 15 s one, no hotkey that
    // saves the full replay); device ids, paths and the window position are made up.
    private const string V024 = """
        {
          "MonitorDevice": null,
          "Fps": 60,
          "BitrateMbps": 16,
          "Codec": "hevc",
          "Resolution": "1080p",
          "BufferSeconds": 48,
          "CaptureCursor": true,
          "Hotkey": { "Ctrl": false, "Alt": false, "Shift": false, "Win": false, "Vk": 145 },
          "TrackDesktop": true,
          "TrackGame": true,
          "TrackMic": true,
          "TrackVoice": true,
          "AllDeviceId": "out:{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}",
          "GameProcess": "out:{0.0.0.00000000}.{22222222-2222-2222-2222-222222222222}",
          "MicDeviceId": "{0.0.1.00000000}.{33333333-3333-3333-3333-333333333333}",
          "VoiceApp": "out:{0.0.0.00000000}.{44444444-4444-4444-4444-444444444444}",
          "ClickSound": true,
          "ClickVolume": 0.5,
          "ClipsFolder": "C:\\Users\\Player\\Videos\\Clink",
          "CopiumTokenProtected": null,
          "StartMinimized": false,
          "RunAtStartup": true,
          "ShowToast": true,
          "HdrKnee": 0.75,
          "HdrPeak": 4,
          "KeyframeSeconds": 1,
          "WindowX": -1500,
          "WindowY": 350,
          "AutoUpdate": true,
          "UpdateRepo": null,
          "LastUpdateCheck": "2026-10-21T18:32:30.3344936Z",
          "RecordingMode": "game",
          "CustomApps": [],
          "Hotkeys": [
            { "Keys": { "Ctrl": false, "Alt": false, "Shift": false, "Win": false, "Vk": 145 }, "Action": "clip", "Seconds": 10 },
            { "Keys": { "Ctrl": false, "Alt": false, "Shift": false, "Win": false, "Vk": 0 }, "Action": "clip", "Seconds": 15 }
          ],
          "ToastPosition": "top-left",
          "ToastClip": { "Enabled": true, "Seconds": 4 },
          "ToastShared": { "Enabled": true, "Seconds": 4 },
          "ToastRecording": { "Enabled": true, "Seconds": 4 },
          "StatusDot": false,
          "StatusDotPosition": "top-left",
          "SizeTarget": "copium200",
          "CopiumPlus": false,
          "AudioSetupSeen": true,
          "ArchiveEnabled": true,
          "ArchivePath": "\\\\nas\\clips\\Clink",
          "ArchiveAfterDays": 1,
          "ArchiveWhenLocalExceedsGB": 100,
          "WindowWidth": 1346,
          "WindowHeight": 793,
          "PendingUpdateVersion": "0.2.4",
          "PendingUpdateSha256": "0000000000000000000000000000000000000000000000000000000000000000"
        }
        """;

    // ------------------------------------------------------------------ helpers

    /// <summary>Loads settings from <paramref name="dir"/>, after writing <paramref name="json"/> as its settings.json (null: an empty folder, a first start).</summary>
    private static Settings LoadFrom(TempDir dir, string? json)
    {
        Settings.DirOverride = dir.Path;
        if (json != null) File.WriteAllText(dir.File("settings.json"), json);
        return Settings.Load();
    }

    /// <summary>The hotkey list as short strings: "Alt+C clip 0", "unset clip 15", "F10 pause 0".</summary>
    private static string[] Rows(Settings s) =>
        s.Hotkeys.Select(h => $"{(h.Keys.Vk > 0 ? h.Keys.ToString() : "unset")} {h.Action} {h.Seconds}").ToArray();

    /// <summary>A "Hotkeys" array for a v0.2 settings file from rows written like <see cref="Rows"/> output: (vk, modifiers "ctrl|alt|shift", action, seconds).</summary>
    private static string HotkeysJson(params (int Vk, string Mods, string Action, int Seconds)[] rows) =>
        "[" + string.Join(",", rows.Select(r =>
            $"{{\"Keys\":{{\"Ctrl\":{(r.Mods.Contains("ctrl") ? "true" : "false")},\"Alt\":{(r.Mods.Contains("alt") ? "true" : "false")}," +
            $"\"Shift\":{(r.Mods.Contains("shift") ? "true" : "false")},\"Win\":false,\"Vk\":{r.Vk}}},\"Action\":\"{r.Action}\",\"Seconds\":{r.Seconds}}}")) + "]";

    private static bool JsonSame(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var bp = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                int n = 0;
                foreach (var p in a.EnumerateObject())
                {
                    n++;
                    if (!bp.TryGetValue(p.Name, out var other) || !JsonSame(p.Value, other)) return false;
                }
                return n == bp.Count;
            case JsonValueKind.Array:
                var al = a.EnumerateArray().ToList();
                var bl = b.EnumerateArray().ToList();
                return al.Count == bl.Count && al.Zip(bl).All(x => JsonSame(x.First, x.Second));
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Number: return a.GetDouble() == b.GetDouble();
            default: return true;   // null, true, false: the kind says it all
        }
    }

    // ------------------------------------------------------------------ fresh install, defaults

    [UnitTest]
    public static void Fresh_install_has_the_v025_defaults()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, null);
        Check.False(s.LoadedFromFile, "nothing was read");
        Check.Equal("1080p", s.Resolution);
        Check.Equal(60, s.Fps);
        Check.Equal(20, s.BitrateMbps);
        Check.Equal("h264", s.Codec);
        Check.Equal("none", s.SizeTarget);
        Check.True(s.CaptureCursor);
        Check.Equal("game", s.RecordingMode);
        Check.Equal(45, s.BufferSeconds);
        Check.Equal("dark", s.Theme);
        Check.Equal("cyan", s.Accent);
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(s));
        Check.Equal("Alt+C", s.Hotkey.ToString());
        Check.Equal("Alt+C", s.MainHotkey.Keys.ToString());
        Check.Equal(3, s.SchemaVersion);
        Check.True(s.Migrated, "a first start is saved once");
        Check.False(s.MainHotkeyAssigned, "Alt+C is the default there, not a change to announce");
        Check.False(s.MainHotkeyNeedsSetup);
    }

    [UnitTest]
    public static void Defaults_come_from_one_place()
    {
        var s = new Settings();
        Check.Equal(SettingsDefaults.Resolution, s.Resolution);
        Check.Equal(SettingsDefaults.Fps, s.Fps);
        Check.Equal(SettingsDefaults.BitrateMbps, s.BitrateMbps);
        Check.Equal(SettingsDefaults.Codec, s.Codec);
        Check.Equal(SettingsDefaults.SizeTarget, s.SizeTarget);
        Check.Equal(SettingsDefaults.CaptureCursor, s.CaptureCursor);
        Check.Equal(SettingsDefaults.RecordingMode, s.RecordingMode);
        Check.Equal(SettingsDefaults.BufferSeconds, s.BufferSeconds);
        Check.Equal(SettingsDefaults.Theme, s.Theme);
        Check.Equal(SettingsDefaults.Accent, s.Accent);
        Check.True(s.Hotkey.SameKeys(SettingsDefaults.MainHotkey()));
        var main = SettingsDefaults.MainHotkey();
        Check.True(main.Alt && !main.Ctrl && !main.Shift && !main.Win, "Alt only");
        Check.Equal(0x43, main.Vk);
        Check.False(ReferenceEquals(SettingsDefaults.MainHotkey(), SettingsDefaults.MainHotkey()), "every call is a new object");
    }

    [UnitTest]
    public static void A_missing_or_null_key_gets_the_default_and_a_present_one_keeps_its_value()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Codec": null, "Resolution": null, "RecordingMode": null, "SizeTarget": null, "Theme": null, "Accent": "  ", "Fps": 30 }""");
        Check.Equal("h264", s.Codec);
        Check.Equal("1080p", s.Resolution);
        Check.Equal("game", s.RecordingMode);
        Check.Equal("none", s.SizeTarget);
        Check.Equal("dark", s.Theme);
        Check.Equal("cyan", s.Accent);
        Check.Equal(30, s.Fps);
        Check.Equal(45, s.BufferSeconds);   // missing key: the new default
    }

    [UnitTest]
    public static void Old_defaults_stay_in_a_file_that_has_them()
    {
        // The v0.2.x defaults, written out by a v0.2.x Clink: none of them may be replaced by a v0.2.5 default.
        using var dir = new TempDir();
        string json = $$"""
            { "Resolution": "native", "BitrateMbps": 25, "BufferSeconds": 120, "SizeTarget": "copium200",
              "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }
            """;
        var s = LoadFrom(dir, json);
        Check.Equal("native", s.Resolution);
        Check.Equal(25, s.BitrateMbps);
        Check.Equal(120, s.BufferSeconds);
        Check.Equal("copium200", s.SizeTarget);
        Check.Equal(new[] { "F9 clip 0" }, Rows(s));
        Check.Equal("F9", s.Hotkey.ToString());
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
    }

    // ------------------------------------------------------------------ older files

    [UnitTest]
    public static void V024_file_keeps_every_value_and_gets_an_Alt_C_main()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, V024);
        Check.True(s.LoadedFromFile);
        // a few values by name, for a readable failure ...
        Check.Equal("hevc", s.Codec);
        Check.Equal(16, s.BitrateMbps);
        Check.Equal(48, s.BufferSeconds);
        Check.Equal("copium200", s.SizeTarget);
        Check.Equal("top-left", s.ToastPosition);
        Check.Equal("\\\\nas\\clips\\Clink", s.ArchivePath);
        Check.Equal("dark", s.Theme);
        Check.Equal("cyan", s.Accent);

        // ... the hotkeys: no row saved the full replay, so the main is Alt+C and the two rows stay what they were
        Check.Equal(new[] { "Alt+C clip 0", "ScrollLock clip 10", "unset clip 15" }, Rows(s));
        Check.True(s.MainHotkeyAssigned, "the user is told Alt+C now saves the full replay");
        Check.False(s.MainHotkeyNeedsSetup);
        Check.Equal("Alt+C", s.Hotkey.ToString());   // the v1 field follows the main, which has keys now
        Check.True(s.Migrated);
        Check.Equal(3, s.SchemaVersion);

        // ... and every key of the file is still there, with its value, after a save
        s.Save();
        string saved = File.ReadAllText(dir.File("settings.json"));
        using var before = JsonDocument.Parse(V024);
        using var after = JsonDocument.Parse(saved);
        foreach (var p in before.RootElement.EnumerateObject())
        {
            if (p.Name is "Hotkey" or "Hotkeys") continue;   // reshaped on purpose, checked above
            Check.True(after.RootElement.TryGetProperty(p.Name, out var now), $"{p.Name} is still in the file");
            Check.True(JsonSame(p.Value, now), $"{p.Name} keeps its value ({p.Value} became {now})");
        }
        Check.Equal(3, after.RootElement.GetProperty("SchemaVersion").GetInt32());
        Check.Equal("dark", after.RootElement.GetProperty("Theme").GetString());
        Check.Equal("cyan", after.RootElement.GetProperty("Accent").GetString());

        // the saved file is in shape: loading it again changes nothing and announces nothing
        var again = Settings.Load();
        Check.Equal(Rows(s), Rows(again));
        Check.False(again.Migrated, "a v3 file in shape is not migrated again");
        Check.False(again.MainHotkeyAssigned);
        Check.False(again.MainHotkeyNeedsSetup);
    }

    [UnitTest]
    public static void V1_file_without_a_hotkey_list_uses_its_Hotkey_as_the_main()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "ShowToast": false, "Hotkey": { "Ctrl": true, "Vk": 119 } }""");   // Ctrl+F8
        Check.False(s.HotkeysFromFile);
        Check.Equal(new[] { "Ctrl+F8 clip 0" }, Rows(s));
        Check.Equal("Ctrl+F8", s.Hotkey.ToString());
        Check.False(s.ToastClip.Enabled, "ShowToast:false turns the clip toast off");
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.True(s.Migrated);
    }

    [UnitTest]
    public static void An_empty_hotkey_list_gets_an_Alt_C_main()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Fps": 30, "Hotkeys": [] }""");
        Check.True(s.HotkeysFromFile);
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(s));
        Check.True(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.Equal("Alt+C", s.Hotkey.ToString());
    }

    [UnitTest]
    public static void A_null_hotkey_list_is_treated_like_an_empty_one()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Hotkeys": null }""");
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(s));
        Check.True(s.MainHotkeyAssigned);
    }

    [UnitTest]
    public static void A_full_replay_hotkey_is_promoted_to_the_main_and_keeps_its_keys()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((120, "shift", "clip", 30), (120, "", "clip", 0))}} }""");   // Shift+F9 30 s, F9 full
        Check.Equal(new[] { "F9 clip 0", "Shift+F9 clip 30" }, Rows(s));
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.Equal("F9", s.Hotkey.ToString());
    }

    [UnitTest]
    public static void The_main_prefers_a_full_replay_row_that_has_keys()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((0, "", "clip", 0), (120, "", "clip", 0))}} }""");
        Check.Equal(new[] { "F9 clip 0", "unset clip 0" }, Rows(s));
        Check.False(s.MainHotkeyAssigned);
    }

    [UnitTest]
    public static void Other_actions_keep_their_place_behind_the_main()
    {
        using var dir = new TempDir();
        string hotkeys = HotkeysJson((121, "", "pause", 0), (122, "", "share-last", 0), (120, "", "clip", 0), (123, "", "clip", 20));   // F10 F11 F9 F12
        var s = LoadFrom(dir, $$"""{ "Hotkeys": {{hotkeys}} }""");
        Check.Equal(new[] { "F9 clip 0", "F10 pause 0", "F11 share-last 0", "F12 clip 20" }, Rows(s));
    }

    [UnitTest]
    public static void Only_fixed_length_clips_get_an_Alt_C_main_in_front()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((120, "shift", "clip", 30), (121, "", "clip", 15))}} }""");
        Check.Equal(new[] { "Alt+C clip 0", "Shift+F9 clip 30", "F10 clip 15" }, Rows(s));
        Check.True(s.MainHotkeyAssigned);
    }

    [UnitTest]
    public static void When_Alt_C_is_taken_the_new_main_stays_unset()
    {
        using var dir = new TempDir();
        // Alt+C is the pause key of this file; F9 saves 15 s. Nothing may be bound twice, and nothing may change what it does.
        string json = $$"""{ "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((0x43, "alt", "pause", 0), (120, "", "clip", 15))}} }""";
        var s = LoadFrom(dir, json);
        Check.Equal(new[] { "unset clip 0", "Alt+C pause 0", "F9 clip 15" }, Rows(s));
        Check.True(s.MainHotkeyNeedsSetup);
        Check.False(s.MainHotkeyAssigned);
        Check.Equal("F9", s.Hotkey.ToString());   // an unset main leaves the v1 field alone
        Check.True(s.Migrated);
    }

    [UnitTest]
    public static void A_main_the_user_cleared_stays_cleared_without_a_notice()
    {
        using var dir = new TempDir();
        string json = $$"""{ "SchemaVersion": 3, "Hotkeys": {{HotkeysJson((0, "", "clip", 0), (120, "", "clip", 15))}} }""";
        var s = LoadFrom(dir, json);
        Check.Equal(new[] { "unset clip 0", "F9 clip 15" }, Rows(s));
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.False(s.Migrated, "a v3 file that is in shape is left alone");
    }

    [UnitTest]
    public static void The_main_is_always_a_plain_full_replay_clip_and_odd_rows_are_repaired()
    {
        using var dir = new TempDir();
        // "CLIP" in capitals and a negative length: the main is rewritten as a plain full-replay "clip"
        var s = LoadFrom(dir, """{ "Hotkeys": [ {"Keys":{"Vk":120},"Action":"CLIP","Seconds":-5} ] }""");
        Check.Equal(new[] { "F9 clip 0" }, Rows(s));
        // a row without an action, a null row and a row whose keys are null are repaired or dropped, not a crash
        // (a row with no Keys at all has always meant F9)
        var t = LoadFrom(dir, """{ "Hotkeys": [ {"Action":null,"Seconds":0}, null, {"Keys":null,"Action":"pause"} ] }""");
        Check.Equal(new[] { "F9 clip 0", "unset pause 0" }, Rows(t));
    }

    [UnitTest]
    public static void The_v1_Hotkey_field_follows_the_main_hotkey()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, $$"""{ "SchemaVersion": 3, "Hotkeys": {{HotkeysJson((120, "ctrl", "clip", 0))}} }""");   // no Hotkey key: Alt+C by default
        Check.Equal("Ctrl+F9", s.Hotkey.ToString());
        Check.True(s.Migrated, "the file's Hotkey field was out of step: save once");
        Check.False(ReferenceEquals(s.Hotkey, s.MainHotkey.Keys), "a copy, so changing one never moves the other");
    }

    [UnitTest]
    public static void The_hotkey_model_is_idempotent()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, V024);
        var rows = Rows(s);
        s.EnsureHotkeyModel();
        s.Normalize();
        Check.Equal(rows, Rows(s));
        Check.Equal("Alt+C", s.Hotkey.ToString());
    }

    // ------------------------------------------------------------------ schema, unknown keys, odd values

    [UnitTest]
    public static void Schema_comes_from_the_file_and_is_written_as_3_or_higher()
    {
        using var dir = new TempDir();
        Check.True(LoadFrom(dir, """{ "Fps": 30 }""").Migrated, "no Hotkeys list: schema 1");
        Check.True(LoadFrom(dir, """{ "Hotkeys": [] }""").Migrated, "a Hotkeys list: schema 2");
        Check.True(LoadFrom(dir, """{ "SchemaVersion": 2, "Hotkeys": [] }""").Migrated);

        // a file from a newer Clink keeps its number
        var newer = LoadFrom(dir, $$"""{ "SchemaVersion": 7, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }""");
        Check.Equal(7, newer.SchemaVersion);
        Check.False(newer.Migrated);
        newer.Save();
        using (var doc = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json"))))
            Check.Equal(7, doc.RootElement.GetProperty("SchemaVersion").GetInt32());

        // a hand edit that lowers it is raised again on the next save
        var low = LoadFrom(dir, $$"""{ "SchemaVersion": 1, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }""");
        Check.True(low.Migrated);
        low.SchemaVersion = 1;
        low.Save();
        using (var doc = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json"))))
            Check.Equal(3, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [UnitTest]
    public static void Unknown_keys_survive_a_load_save_load()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Fps": 30, "FutureKey": { "a": 1 }, "AlsoNew": [1, 2], "Hotkeys": [] }""");
        var unknown = s.Unknown ?? throw new TestFailure("the keys this version does not know were dropped");
        Check.True(unknown.ContainsKey("FutureKey"));
        Check.Equal(1, unknown["FutureKey"].GetProperty("a").GetInt32());
        s.Save();
        Check.Contains(File.ReadAllText(dir.File("settings.json")), "FutureKey");
        var again = Settings.Load();
        var kept = again.Unknown ?? throw new TestFailure("the keys this version does not know did not come back");
        Check.Equal(30, again.Fps);
        Check.Equal(1, kept["FutureKey"].GetProperty("a").GetInt32());
        Check.Equal(2, kept["AlsoNew"].GetArrayLength());
    }

    [UnitTest]
    public static void A_NaN_does_not_stop_the_settings_from_being_saved()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, null);
        s.WindowX = double.NaN;
        s.HdrKnee = float.NaN;
        s.Fps = 30;
        s.Save();
        Check.True(File.Exists(dir.File("settings.json")), "Save wrote the file");
        var again = Settings.Load();
        Check.Null(again.LoadProblem);
        Check.Equal(30, again.Fps);
    }

    [UnitTest]
    public static void Settings_follow_the_dir_override()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        Check.Equal(dir.Path, Settings.Dir);
        Check.Equal(dir.File("settings.json"), Settings.FilePath);
        Settings.DirOverride = null;
        Check.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink"), Settings.Dir);
    }

    // ------------------------------------------------------------------ hotkey helpers

    [UnitTest]
    public static void HotkeyBinding_clones_and_compares_by_keys()
    {
        var a = new HotkeyBinding { Ctrl = true, Shift = true, Vk = 0x46 };
        var b = a.Clone();
        Check.False(ReferenceEquals(a, b));
        Check.True(a.SameKeys(b));
        b.Alt = true;
        Check.False(a.SameKeys(b));
        Check.False(a.SameKeys(null));
        Check.Equal("Ctrl+Shift+F", a.ToString());
        Check.Equal(0x78, new HotkeyBinding().Vk);   // a hand-edited binding without a Vk has always meant F9
        Check.True(new HotkeyBinding { Vk = 0 }.SameKeys(new HotkeyBinding { Vk = 0 }));
    }

    [UnitTest]
    public static void The_clip_hotkey_label_is_the_main_then_the_first_set_additional_clip()
    {
        HotkeyAction Row(int vk, string action, int seconds, bool alt = false) =>
            new() { Keys = new HotkeyBinding { Vk = vk, Alt = alt }, Action = action, Seconds = seconds };

        Check.Equal("Alt+C", AppController.ClipHotkeyLabelFor(new[] { Row(0x43, "clip", 0, alt: true), Row(120, "clip", 15) }));
        Check.Equal("F9", AppController.ClipHotkeyLabelFor(new[] { Row(0, "clip", 0), Row(121, "pause", 0), Row(120, "clip", 15) }));
        Check.Null(AppController.ClipHotkeyLabelFor(new[] { Row(0, "clip", 0), Row(121, "pause", 0) }));
        Check.Null(AppController.ClipHotkeyLabelFor(new[] { Row(0, "clip", 0), Row(0, "clip", 15) }));
        Check.Null(AppController.ClipHotkeyLabelFor(Array.Empty<HotkeyAction>()));
    }

    // ------------------------------------------------------------------ --dev

    [UnitTest]
    public static void Dev_options_are_read_from_anywhere_on_the_command_line()
    {
        Check.Null(DevOptions.Parse(new[] { "--show" }));
        Check.Null(DevOptions.Parse(Array.Empty<string>()));

        var bare = DevOptions.Parse(new[] { "--dev" })!;
        Check.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clink-dev")), bare.Folder);
        Check.False(bare.BindHotkeys);

        var withFolder = DevOptions.Parse(new[] { "--show", "--dev", @"C:\scratch\dev one", "--dev-hotkeys" })!;
        Check.Equal(@"C:\scratch\dev one", withFolder.Folder);
        Check.True(withFolder.BindHotkeys);

        var switchNext = DevOptions.Parse(new[] { "--dev", "--dev-hotkeys" })!;   // a switch after --dev is not its folder
        Check.Equal(bare.Folder, switchNext.Folder);
        Check.True(switchNext.BindHotkeys);

        Check.Equal(@"C:\scratch\a", DevOptions.Parse(new[] { "--DEV", "\"C:\\scratch\\a\"" })!.Folder);
    }

    [UnitTest]
    public static void Each_dev_folder_has_its_own_mutex_and_pipe_name_that_never_matches_the_real_one()
    {
        var a = DevOptions.Parse(new[] { "--dev", @"C:\scratch\one" })!;
        var a2 = DevOptions.Parse(new[] { "--dev", @"c:\SCRATCH\one\" })!;
        var b = DevOptions.Parse(new[] { "--dev", @"C:\scratch\two" })!;
        Check.Equal(a.InstanceSuffix, a2.InstanceSuffix, "the same folder, however it is spelled");
        Check.NotEqual(a.InstanceSuffix, b.InstanceSuffix);
        Check.True(a.InstanceSuffix.StartsWith(".dev.", StringComparison.Ordinal));
        Check.False(a.InstanceSuffix.Contains('\\'), "a pipe name cannot hold a backslash");

        string before = AppController.InstanceSuffix;
        try
        {
            Check.False(AppController.IsDev);
            Check.Equal("Clink.Control", AppController.PipeName);
            AppController.InstanceSuffix = a.InstanceSuffix;
            Check.True(AppController.IsDev);
            Check.Equal("Clink.Control" + a.InstanceSuffix, AppController.PipeName);
            Check.NotEqual("Clink.Control", AppController.PipeName);
        }
        finally { AppController.InstanceSuffix = before; }
    }

    // ------------------------------------------------------------------ size targets

    [UnitTest]
    public static void Size_targets_are_listed_in_the_combo_order()
    {
        Check.Equal(new[] { "none", "discord10", "discord50", "copium200", "discord500", "copiumplus" }, Capabilities.SizeTargets.Select(t => t.Id).ToArray());
        Check.Equal(
            new[] { "None", "Discord (10 MB)", "Discord Nitro Basic (50 MB)", "Copium (200 MB, 10 min)", "Discord Nitro (500 MB)", "Copium Plus (2 GB, 60 min)" },
            Capabilities.SizeTargets.Select(t => t.Name).ToArray());
        Check.Equal(new long[] { 0, 10_000_000, 50_000_000, 200_000_000, 500_000_000, 2_000_000_000 }, Capabilities.SizeTargets.Select(t => t.Bytes).ToArray());
        Check.Equal(new[] { 0, 0, 0, 600, 0, 3600 }, Capabilities.SizeTargets.Select(t => t.MaxSeconds).ToArray());

        var plus = Capabilities.SizeTargetFor("copiumplus");
        Check.Equal(2_000_000_000L, plus.Bytes);
        Check.Equal(3600, plus.MaxSeconds);
        Check.Equal(2_000_000_000L, Capabilities.CopiumPlusBytes);
        Check.Equal(3600, Capabilities.CopiumPlusSeconds);
        Check.Equal(2_000_000_000L, Capabilities.SizeTargetBytes("copiumplus"));
        Check.Equal("Copium Plus (2 GB, 60 min)", Capabilities.SizeTargetName("copiumplus"));
        Check.Equal(200_000_000L, Capabilities.SizeTargetBytes("copium200"));
        Check.Equal("Copium (200 MB, 10 min)", Capabilities.SizeTargetName("copium200"));
    }

    [UnitTest]
    public static void An_unknown_size_target_means_none()
    {
        foreach (string? id in new string?[] { null, "", "  ", "bogus", "copium", "copium2000" })
        {
            Check.Equal("none", Capabilities.SizeTargetFor(id).Id, $"'{id}'");
            Check.Equal(0L, Capabilities.SizeTargetBytes(id));
            Check.Equal("None", Capabilities.SizeTargetName(id));
        }
        Check.Equal("copium200", Capabilities.SizeTargetFor("  Copium200 ").Id);   // any case, spaces ignored, as before
        Check.Equal("copiumplus", Capabilities.SizeTargetFor("COPIUMPLUS").Id);
    }

    [UnitTest]
    public static void A_target_with_a_length_limit_fits_at_most_that_long()
    {
        Check.Equal(45, Capabilities.FitSeconds("copium200", 45));
        Check.Equal(600, Capabilities.FitSeconds("copium200", 600));
        Check.Equal(600, Capabilities.FitSeconds("copium200", 1800));
        Check.Equal(1800, Capabilities.FitSeconds("copiumplus", 1800));
        Check.Equal(3600, Capabilities.FitSeconds("copiumplus", 3600));
        Check.Equal(3600, Capabilities.FitSeconds("copiumplus", 7200));
        Check.Equal(1800, Capabilities.FitSeconds("discord10", 1800));
        Check.Equal(1800, Capabilities.FitSeconds("none", 1800));
        Check.Equal(1800, Capabilities.FitSeconds(null, 1800));
        Check.True(Capabilities.IsCopiumTarget("copium200") && Capabilities.IsCopiumTarget("copiumplus") && !Capabilities.IsCopiumTarget("discord500"));
    }

    [UnitTest]
    public static void Copium_limits_name_the_tier()
    {
        var free = Capabilities.CopiumLimits(false);
        Check.Equal(200_000_000L, free.Bytes);
        Check.Equal(600, free.Seconds);
        Check.Equal("Copium's free limit (200 MB / 10 min)", free.Label);
        var plus = Capabilities.CopiumLimits(true);
        Check.Equal(2_000_000_000L, plus.Bytes);
        Check.Equal(3600, plus.Seconds);
        Check.Equal("Copium Plus's limit (2 GB / 60 min)", plus.Label);
        Check.Equal(Capabilities.CopiumFreeBytes, free.Bytes);
        Check.Equal(Capabilities.CopiumFreeSeconds, free.Seconds);
    }

    [UnitTest]
    public static void The_Copium_free_target_keeps_its_bitrate_arithmetic_and_notes()
    {
        var s = new Settings { SizeTarget = "copium200", BitrateMbps = 16, KeyframeSeconds = 1 };
        // 45 s with Mix + Game + Mic: far below the limit, so the chosen bitrate stays, and there is no length note
        var (mbps, note) = Capabilities.ResolveBitrate(s, 3, 45);
        Check.Near(16, mbps);
        Check.Equal("a 45 s clip at 16 Mbps fits 200 MB", note);
        Check.False(Capabilities.IsWarningNote(note));

        // a replay of exactly 10 min: the bitrate that fits 10:00 in 200 MB, and the old warning text, unchanged
        (mbps, note) = Capabilities.ResolveBitrate(s, 3, 600);
        Check.Near(1.9, mbps);
        Check.Equal("lowered to 1.9 Mbps so a 10:00 clip fits 200 MB" + Capabilities.CopiumLengthNote + " (a 10:00 replay saves a hair over 10 min)", note);
        Check.True(Capabilities.IsWarningNote(note));

        // longer than 10 min: still fitted for 10 min only, with the plain note
        (mbps, note) = Capabilities.ResolveBitrate(s, 3, 1800);
        Check.Near(1.9, mbps);
        Check.Equal("lowered to 1.9 Mbps so a 10:00 clip fits 200 MB" + Capabilities.CopiumLengthNote, note);
        Check.True(Capabilities.IsWarningNote(note));
    }

    [UnitTest]
    public static void The_Copium_Plus_target_fits_two_gigabytes_and_warns_from_an_hour()
    {
        var s = new Settings { SizeTarget = "copiumplus", BitrateMbps = 16, KeyframeSeconds = 1 };
        var (mbps, note) = Capabilities.ResolveBitrate(s, 3, 45);
        Check.Near(16, mbps);
        Check.Equal("a 45 s clip at 16 Mbps fits 2 GB", note);
        Check.False(Capabilities.IsWarningNote(note));

        // 30 min at 16 Mbps is about 3.6 GB, so 2 GB lowers the bitrate; nothing about the length is wrong yet
        (mbps, note) = Capabilities.ResolveBitrate(s, 3, 1800);
        Check.Near(7.8, mbps);
        Check.Equal("lowered to 7.8 Mbps so a 30:00 clip fits 2 GB", note);
        Check.False(Capabilities.IsWarningNote(note));

        // a 1 h replay is a hair over Copium Plus's 60 min: the length warning, and the bitrate that fits an hour
        (mbps, note) = Capabilities.ResolveBitrate(s, 3, 3600);
        Check.Near(3.6, mbps);
        Check.Equal("lowered to 3.6 Mbps so a 1 h clip fits 2 GB" + Capabilities.CopiumPlusLengthNote + " (a 60:00 replay saves a hair over 60 min)", note);
        Check.True(Capabilities.IsWarningNote(note));
    }

    [UnitTest]
    public static void Warning_notes_are_the_ones_no_bitrate_can_fix()
    {
        Check.False(Capabilities.IsWarningNote(null));
        Check.False(Capabilities.IsWarningNote(""));
        Check.False(Capabilities.IsWarningNote("a 45 s clip at 16 Mbps fits 200 MB"));
        Check.False(Capabilities.IsWarningNote("lowered to 4.2 Mbps so a 2:00 clip fits 50 MB"));
        Check.True(Capabilities.IsWarningNote("a 1 h clip does not fit in 10 MB even at 0.5 Mbps; shorten the replay length"));
        Check.True(Capabilities.IsWarningNote(Capabilities.CopiumLengthNote));
        Check.True(Capabilities.IsWarningNote(Capabilities.CopiumPlusLengthNote));

        // and straight from ResolveBitrate: a 10 MB target for an hour cannot fit
        var (_, note) = Capabilities.ResolveBitrate(new Settings { SizeTarget = "discord10" }, 3, 3600);
        Check.Contains(note, "does not fit");
        Check.True(Capabilities.IsWarningNote(note));
    }
}
