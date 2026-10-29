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

    // ------------------------------------------------------------------ a v0.4.1 settings.json (users of v0.4.1 upgrade by hand)

    // Every key v0.4.1 wrote (git show v0.4.1:Clink/Services/Settings.cs), with values a real user has and none of them a default: hotkeys,
    // folders, encoder, toasts, archive, the legacy UpdateRepo and the pending-update record of a download v0.4.1 made itself.
    private const string V041 = """
        {
          "SchemaVersion": 3,
          "MonitorDevice": "\\\\.\\DISPLAY2",
          "Fps": 30,
          "BitrateMbps": 35,
          "Codec": "hevc",
          "Resolution": "1440p",
          "BufferSeconds": 120,
          "CaptureCursor": false,
          "Hotkey": { "Ctrl": true, "Alt": true, "Shift": false, "Win": false, "Vk": 121 },
          "TrackDesktop": true,
          "TrackGame": false,
          "TrackMic": false,
          "TrackVoice": true,
          "AllDeviceId": "in:{0.0.1.00000000}.{55555555-5555-5555-5555-555555555555}",
          "GameProcess": "VALORANT-Win64-Shipping",
          "MicDeviceId": "{0.0.1.00000000}.{33333333-3333-3333-3333-333333333333}",
          "VoiceApp": "Discord",
          "ClickSound": false,
          "ClickVolume": 0.35,
          "ClipsFolder": "D:\\Videos\\Clink Clips",
          "CopiumTokenProtected": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAexample",
          "StartMinimized": true,
          "RunAtStartup": true,
          "ShowToast": false,
          "HdrKnee": 0.8,
          "HdrPeak": 6,
          "KeyframeSeconds": 2,
          "WindowX": 140,
          "WindowY": 90,
          "AutoUpdate": false,
          "UseDevBuilds": true,
          "UpdateRepo": "Euphoric-Software/clink",
          "LastUpdateCheck": "2026-10-21T18:32:30.3344936Z",
          "RecordingMode": "always",
          "CustomApps": [ "obs64", "VALORANT-Win64-Shipping" ],
          "Hotkeys": [
            { "Keys": { "Ctrl": true, "Alt": true, "Shift": false, "Win": false, "Vk": 121 }, "Action": "clip", "Seconds": 0 },
            { "Keys": { "Ctrl": false, "Alt": false, "Shift": false, "Win": false, "Vk": 120 }, "Action": "clip", "Seconds": 30 },
            { "Keys": { "Ctrl": false, "Alt": false, "Shift": false, "Win": false, "Vk": 19 }, "Action": "pause", "Seconds": 0 },
            { "Keys": { "Ctrl": true, "Alt": false, "Shift": true, "Win": false, "Vk": 83 }, "Action": "share-last", "Seconds": 0 },
            { "Keys": { "Ctrl": false, "Alt": false, "Shift": false, "Win": false, "Vk": 44 }, "Action": "screenshot", "Seconds": 0 },
            { "Keys": { "Ctrl": false, "Alt": false, "Shift": true, "Win": false, "Vk": 44 }, "Action": "screenshot-edit-last", "Seconds": 0 }
          ],
          "ToastPosition": "top-right",
          "ToastClip": { "Enabled": false, "Seconds": 9 },
          "ToastShared": { "Enabled": false, "Seconds": 3 },
          "ToastRecording": { "Enabled": false, "Seconds": 2 },
          "StatusDot": true,
          "StatusDotPosition": "bottom-left",
          "SizeTarget": "discord50",
          "CopiumPlus": true,
          "AudioSetupSeen": true,
          "CaptureBorderNoticeDismissed": true,
          "ArchiveEnabled": true,
          "ArchivePath": "\\\\nas\\clips\\Clink",
          "ArchiveAfterDays": 14,
          "ArchiveWhenLocalExceedsGB": 250.5,
          "WindowWidth": 1346,
          "WindowHeight": 793,
          "PendingUpdateVersion": "0.4.1",
          "PendingUpdateSha256": "0A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F9",
          "PendingUpdateFrom": "0.4.0",
          "Theme": "light",
          "Accent": "violet",
          "ImportDeclined": "Some.Package_abc|638650000000000000",
          "ScreenshotsEnabled": true,
          "ScreenshotsFolder": "D:\\Pictures\\Clink Shots",
          "ScreenshotMode": "after",
          "ScreenshotEditor": {
            "Color": "#43A047", "Width": 6, "TextColor": "#000000", "FontSize": 36, "Bold": true, "TextBackdrop": "box", "BackdropColor": "#FFFFFF",
            "BlurRadius": 10, "PixelateBlock": 16, "HighlightColor": "#FF9800", "HighlightWidth": 24, "HighlightFreehand": true,
            "SpotDim": 0.6, "SpotRadius": 20, "SpotGradient": true, "SpotFalloffPercent": 50, "MagnifyZoom": 3, "MagnifyOutline": false,
            "MagnifyOutlineColor": "#000000", "MagnifyOutlineWidth": 5, "MagnifySmooth": true, "ImageOpacity": 0.9
          },
          "ScreenshotCopyImage": false,
          "ScreenshotLoupe": false,
          "ScreenshotUpload": true,
          "ScreenshotCopyLink": false,
          "CopiumImageUploadUrl": "https://img.copium.pics/upload",
          "CopiumConsent": true,
          "CopiumConsentDontAsk": true,
          "CopiumConsentPromptedUtc": "2026-09-30T08:15:00Z",
          "CopiumConsentPromptCount": 2,
          "CopiumConsentScreenshots": 7,
          "CloudSharingEnabled": false,
          "ClipsEnabled": false,
          "LibraryFeature": "screenshots",
          "LibraryLevel": "shared",
          "ScreenshotEditorExcludeFromCapture": false,
          "ToastScreenshot": { "Enabled": false, "Seconds": 11 },
          "SeededHotkeyDefaults": [ "screenshot", "screenshot-edit-last" ]
        }
        """;

    /// <summary>Every key (nested ones too) of <paramref name="expected"/> is in <paramref name="actual"/> with the same value; null if so, else the first path that differs.</summary>
    private static string? Missing(JsonElement expected, JsonElement actual, string at)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (actual.ValueKind != JsonValueKind.Object) return at;
            foreach (var p in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(p.Name, out var other)) return at + "." + p.Name + " (gone)";
                if (Missing(p.Value, other, at + "." + p.Name) is { } where) return where;
            }
            return null;
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != expected.GetArrayLength()) return at + " (length)";
            int i = 0;
            foreach (var (e, a) in expected.EnumerateArray().Zip(actual.EnumerateArray()))
                if (Missing(e, a, $"{at}[{i++}]") is { } where) return where;
            return null;
        }
        return JsonSame(expected, actual) ? null : $"{at}: {expected.GetRawText()} became {actual.GetRawText()}";
    }

    [UnitTest]
    public static void A_v041_settings_file_loads_whole_and_every_key_survives_a_save()
    {
        Settings.SeedDefaults = true;   // as in the app (the file has seen both defaults, so nothing is added)
        using var dir = new TempDir();
        using var fixture = JsonDocument.Parse(V041);
        var s = LoadFrom(dir, V041);
        Check.True(s.LoadedFromFile);
        Check.False(s.FileWasSkipped);
        Check.Equal(Array.Empty<string>(), s.UnreadableValues.ToArray(), "nothing unreadable");
        Check.Null(s.LoadProblem);
        Check.True(s.Unknown == null || s.Unknown.Count == 0, "every v0.4.1 key is still a setting: " + string.Join(", ", s.Unknown?.Keys ?? Enumerable.Empty<string>()));
        Check.Null(Missing(fixture.RootElement, s.ToElement(), "$"), "loaded as written, nothing reset to its default");
        Check.Equal("Euphoric-Software/clink", s.UpdateRepo, "the legacy key is kept (ignored)");
        Check.Equal(@"D:\Videos\Clink Clips", s.ClipsFolder);
        Check.Equal(6, s.Hotkeys.Count);
        Check.Equal(121, s.Hotkeys[0].Keys.Vk);
        Check.Null(s.PendingUpdateSignedSha256, "v0.4.1 never had the signed hash: its download is not one to install");

        s.Save();
        var again = Settings.Load();
        Check.Equal(Array.Empty<string>(), again.UnreadableValues.ToArray());
        Check.Null(again.LoadProblem);
        using var saved = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json")));
        Check.Null(Missing(fixture.RootElement, saved.RootElement, "$"), "every key is in the saved file, with its value");
        Check.Null(Missing(fixture.RootElement, again.ToElement(), "$"), "and reads back the same");
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
        Check.Equal("spark", s.Accent);
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
        Check.Equal("spark", s.Accent);
        Check.Equal(30, s.Fps);
        Check.Equal(45, s.BufferSeconds);   // missing key: the new default
    }

    [UnitTest]
    public static void A_saved_cyan_accent_loads_as_spark()
    {
        using var dir = new TempDir();
        Check.Equal("spark", LoadFrom(dir, """{ "Accent": "cyan" }""").Accent);
        Check.Equal("Violet", Settings.FromJson("""{ "Accent": "Violet" }""").Accent);
        var differences = SettingsImport.Differences("""{ "Accent": "cyan" }""", """{ "Accent": "spark" }""");
        Check.Equal(Array.Empty<string>(), differences.ToArray(), "cyan and spark are the same accent");

        // a stored "cyan" is written back as "spark" (current schema, so this is the plain load-and-save path)
        var stored = LoadFrom(dir, $$"""{ "SchemaVersion": {{Settings.CurrentSchema}}, "Accent": "cyan" }""");
        Check.Equal("spark", stored.Accent);
        stored.Save();
        using var saved = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json")));
        Check.Equal("spark", saved.RootElement.GetProperty("Accent").GetString());
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
        Check.Equal("spark", s.Accent);

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
        Check.Equal("spark", after.RootElement.GetProperty("Accent").GetString());

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
    public static void A_hotkey_list_that_is_empty_by_choice_gets_an_unset_main_not_Alt_C()
    {
        // "no hotkeys, by choice" in v0.2.4 (Stream Deck and --clip are enough): Alt+C must not be bound, and swallowed system-wide
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Fps": 30, "Hotkeys": [] }""");
        Check.True(s.HotkeysFromFile);
        Check.Equal(new[] { "unset clip 0" }, Rows(s));
        Check.True(s.MainHotkeyNeedsSetup, "the user is told once to set it");
        Check.False(s.MainHotkeyAssigned);
        Check.Equal("Alt+C", s.Hotkey.ToString(), "the v1 field is left alone: the main has no keys to follow");
        Check.True(s.Migrated);

        // saved, it is a main the user has not set, which stays quiet from then on
        s.Save();
        var again = Settings.Load();
        Check.Equal(new[] { "unset clip 0" }, Rows(again));
        Check.False(again.MainHotkeyNeedsSetup);
        Check.False(again.MainHotkeyAssigned);
        Check.False(again.Migrated);
    }

    [UnitTest]
    public static void A_first_start_and_a_v1_file_still_get_their_main_on_Alt_C_or_their_own_key()
    {
        using var fresh = new TempDir();
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(LoadFrom(fresh, null)));
        using var v1 = new TempDir();
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(LoadFrom(v1, """{ "Fps": 30 }""")), "a v0.1 file without a hotkey: the default");
        using var v1own = new TempDir();
        var own = LoadFrom(v1own, """{ "Hotkey": { "Vk": 121 } }""");
        Check.Equal(new[] { "F10 clip 0" }, Rows(own), "a v0.1 file with one: its own");
        Check.False(own.MainHotkeyAssigned);
    }

    [UnitTest]
    public static void A_null_hotkey_list_is_treated_like_an_empty_one()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Hotkeys": null }""");
        Check.Equal(new[] { "unset clip 0" }, Rows(s));
        Check.True(s.MainHotkeyNeedsSetup);
        Check.False(s.MainHotkeyAssigned);
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
    public static void A_cleared_main_stays_the_main_when_another_full_replay_row_has_keys()
    {
        // v0.2.4's Add hotkey made Seconds-0 rows, so [F9 clip 0, Shift+F9 clip 0] is common. Clearing the main saves
        // [unset clip 0, Shift+F9 clip 0] in schema 3; the next load must not read Shift+F9 as the main and push the cleared row down.
        using var dir = new TempDir();
        string json = $$"""{ "SchemaVersion": 3, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((0, "", "clip", 0), (120, "shift", "clip", 0))}} }""";
        var s = LoadFrom(dir, json);
        Check.Equal(new[] { "unset clip 0", "Shift+F9 clip 0" }, Rows(s), "unchanged: the cleared row is still the main");
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.False(s.Migrated, "nothing was changed, so nothing is saved");
        Check.Equal("F9", s.Hotkey.ToString(), "an unset main leaves the v1 field alone");

        // and it stays so: saved and loaded again, and a second pass over the same list
        s.Save();
        var again = Settings.Load();
        Check.Equal(new[] { "unset clip 0", "Shift+F9 clip 0" }, Rows(again));
        Check.False(again.Migrated);
        again.EnsureHotkeyModel();
        Check.Equal(new[] { "unset clip 0", "Shift+F9 clip 0" }, Rows(again));
        Check.False(again.MainHotkeyAssigned || again.MainHotkeyNeedsSetup);

        // a main that has keys keeps leading too, with another full-replay row below it
        var keyed = LoadFrom(dir, $$"""{ "SchemaVersion": 3, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0), (120, "shift", "clip", 0), (0, "", "clip", 0))}} }""");
        Check.Equal(new[] { "F9 clip 0", "Shift+F9 clip 0", "unset clip 0" }, Rows(keyed));
        Check.False(keyed.Migrated);

        // a later row with keys is also left alone behind a cleared main when there are several, and other actions come along unchanged
        var several = LoadFrom(dir, $$"""{ "SchemaVersion": 3, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((0, "", "clip", 0), (121, "", "pause", 0), (120, "shift", "clip", 0), (122, "", "clip", 0))}} }""");
        Check.Equal(new[] { "unset clip 0", "F10 pause 0", "Shift+F9 clip 0", "F11 clip 0" }, Rows(several));
        Check.False(several.Migrated);
    }

    [UnitTest]
    public static void In_a_v0_2_file_the_same_list_still_promotes_the_row_that_has_keys()
    {
        // Without a SchemaVersion (v0.2.4) the unset row is the "Default" one that Add hotkey made and never gave keys: not a choice
        using var dir = new TempDir();
        var s = LoadFrom(dir, $$"""{ "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((0, "", "clip", 0), (120, "shift", "clip", 0))}} }""");
        Check.Equal(new[] { "Shift+F9 clip 0", "unset clip 0" }, Rows(s));
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.True(s.Migrated);

        // a SchemaVersion of 2 says the same
        var two = LoadFrom(dir, $$"""{ "SchemaVersion": 2, "Hotkeys": {{HotkeysJson((0, "", "clip", 0), (120, "shift", "clip", 0))}} }""");
        Check.Equal(new[] { "Shift+F9 clip 0", "unset clip 0" }, Rows(two));

        // and once the list has been put in shape, a second pass over the same object leaves it as it is
        s.EnsureHotkeyModel();
        Check.Equal(new[] { "Shift+F9 clip 0", "unset clip 0" }, Rows(s));
    }

    [UnitTest]
    public static void In_a_v3_file_only_a_full_replay_clip_in_front_counts_as_the_main()
    {
        // row 0 is a shorter clip here, so it is not the main: the first full-replay row that has keys leads, as ever
        using var dir = new TempDir();
        var s = LoadFrom(dir, $$"""{ "SchemaVersion": 3, "Hotkeys": {{HotkeysJson((120, "shift", "clip", 30), (120, "", "clip", 0), (0, "", "clip", 0))}} }""");
        Check.Equal(new[] { "F9 clip 0", "Shift+F9 clip 30", "unset clip 0" }, Rows(s));
        Check.False(s.MainHotkeyAssigned);

        // a pause in front is no main either
        var pause = LoadFrom(dir, $$"""{ "SchemaVersion": 3, "Hotkeys": {{HotkeysJson((121, "", "pause", 0), (0, "", "clip", 0), (120, "", "clip", 0))}} }""");
        Check.Equal(new[] { "F9 clip 0", "F10 pause 0", "unset clip 0" }, Rows(pause));
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

    // ------------------------------------------------------------------ the hotkey model: files written by an older Clink

    /// <summary>What each combo does when pressed: the first binding on it fires (Hotkeys.Match), so this is what the list means. "F9=pause 0".</summary>
    private static string[] Fires(Settings s) =>
        s.Hotkeys.Where(h => h.Keys.Vk > 0).GroupBy(h => h.Keys.ToString()).Select(g => $"{g.Key}={g.First().Action} {g.First().Seconds}").OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [UnitTest]
    public static void A_Default_row_that_was_never_given_keys_is_not_the_main_it_is_given_Alt_C()
    {
        // v0.2.4's Add hotkey makes {Vk 0, clip, 0}, shown as "Default": nobody chose that row to be the main
        using var dir = new TempDir();
        var s = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((145, "", "clip", 10), (0, "", "clip", 0))}} }""");
        Check.Equal(new[] { "Alt+C clip 0", "ScrollLock clip 10" }, Rows(s));
        Check.True(s.MainHotkeyAssigned, "the user is told Alt+C now saves the full replay");
        Check.False(s.MainHotkeyNeedsSetup);
        Check.Equal("Alt+C", s.Hotkey.ToString());
        Check.True(s.Migrated);

        // alone, the same
        var alone = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((0, "", "clip", 0))}} }""");
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(alone));
        Check.True(alone.MainHotkeyAssigned);

        // with another row on Alt+C it stays unset (the same row, in front) and the user is asked to give it keys
        var taken = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((0x43, "alt", "pause", 0), (0, "", "clip", 0))}} }""");
        Check.Equal(new[] { "unset clip 0", "Alt+C pause 0" }, Rows(taken));
        Check.True(taken.MainHotkeyNeedsSetup);
        Check.False(taken.MainHotkeyAssigned);

        // two of them: one becomes the main, the other stays an unset row behind it
        var two = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((0, "", "clip", 0), (145, "", "clip", 15), (0, "", "clip", 0))}} }""");
        Check.Equal(new[] { "Alt+C clip 0", "ScrollLock clip 15", "unset clip 0" }, Rows(two));
    }

    [UnitTest]
    public static void In_a_v3_file_a_full_replay_row_without_keys_is_the_main_the_user_cleared()
    {
        using var dir = new TempDir();
        string json = $$"""{ "SchemaVersion": 3, "Hotkey": { "Vk": 145 }, "Hotkeys": {{HotkeysJson((145, "", "clip", 10), (0, "", "clip", 0))}} }""";
        var s = LoadFrom(dir, json);
        Check.Equal(new[] { "unset clip 0", "ScrollLock clip 10" }, Rows(s), "it leads the list, as it is");
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.Equal("ScrollLock", s.Hotkey.ToString(), "an unset main leaves the v1 field alone");
        s.Save();
        var again = Settings.Load();
        Check.Equal(new[] { "unset clip 0", "ScrollLock clip 10" }, Rows(again));
        Check.False(again.Migrated);
    }

    [UnitTest]
    public static void A_binding_is_never_moved_ahead_of_another_on_the_same_keys()
    {
        // The first binding of a combo fires. [F9 pause, F9 clip 0] pauses on F9 today and has to go on doing so.
        using var dir = new TempDir();
        var shadowed = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((120, "", "pause", 0), (120, "", "clip", 0))}} }""");
        Check.Equal(new[] { "Alt+C clip 0", "F9 pause 0", "F9 clip 0" }, Rows(shadowed), "the dead F9 row stays where it was; the main is a new row");
        Check.Equal(new[] { "Alt+C=clip 0", "F9=pause 0" }, Fires(shadowed));
        Check.True(shadowed.MainHotkeyAssigned);

        // the same for a shorter clip: F9 keeps saving 30 s
        var shorter = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((120, "", "clip", 30), (120, "", "clip", 0))}} }""");
        Check.Equal(new[] { "Alt+C clip 0", "F9 clip 30", "F9 clip 0" }, Rows(shorter));
        Check.Equal(new[] { "Alt+C=clip 0", "F9=clip 30" }, Fires(shorter));

        // a full-replay row on other keys, further down, is the one that can lead
        var other = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((120, "", "pause", 0), (120, "", "clip", 0), (121, "", "clip", 0))}} }""");
        Check.Equal(new[] { "F10 clip 0", "F9 pause 0", "F9 clip 0" }, Rows(other));
        Check.Equal(new[] { "F10=clip 0", "F9=pause 0" }, Fires(other));
        Check.False(other.MainHotkeyAssigned);

        // Alt+C taken as well: the new main is unset, and Alt+C still does what it did
        var taken = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((0x43, "alt", "pause", 0), (0x43, "alt", "clip", 0))}} }""");
        Check.Equal(new[] { "unset clip 0", "Alt+C pause 0", "Alt+C clip 0" }, Rows(taken));
        Check.Equal(new[] { "Alt+C=pause 0" }, Fires(taken));
        Check.True(taken.MainHotkeyNeedsSetup);

        // different modifiers are different combos: Shift+F9 does not shadow F9
        var modifiers = LoadFrom(dir, $$"""{ "Hotkeys": {{HotkeysJson((120, "shift", "pause", 0), (120, "", "clip", 0))}} }""");
        Check.Equal(new[] { "F9 clip 0", "Shift+F9 pause 0" }, Rows(modifiers));
        Check.Equal(new[] { "F9=clip 0", "Shift+F9=pause 0" }, Fires(modifiers));
    }

    // ------------------------------------------------------------------ schema, unknown keys, odd values

    [UnitTest]
    public static void An_older_file_is_migrated_by_its_schema_alone_even_when_its_hotkeys_are_in_shape()
    {
        // The other migration tests would pass through the hotkey reshape; this file needs none: a v0.2.4 file with its one hotkey on F9.
        using var dir = new TempDir();
        string inShape = $$"""{ "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }""";
        var s = LoadFrom(dir, inShape);
        Check.Equal(new[] { "F9 clip 0" }, Rows(s));
        Check.Equal("F9", s.Hotkey.ToString());
        Check.False(s.MainHotkeyAssigned);
        Check.False(s.MainHotkeyNeedsSetup);
        Check.True(s.Migrated, "no SchemaVersion, a Hotkeys list: schema 2, so the file is rewritten as 3");
        Check.Equal(3, s.SchemaVersion);
        s.Save();
        Check.Contains(File.ReadAllText(dir.File("settings.json")), "\"SchemaVersion\": 3");

        // the same hotkeys with a SchemaVersion of 2 say the same; of 3, there is nothing to do
        Check.True(LoadFrom(dir, $$"""{ "SchemaVersion": 2, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }""").Migrated);
        Check.False(LoadFrom(dir, $$"""{ "SchemaVersion": 3, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }""").Migrated);
        // and without any hotkey list it is schema 1
        Check.True(LoadFrom(dir, """{ "Fps": 30 }""").Migrated);
    }

    [UnitTest]
    public static void A_save_always_writes_schema_3_over_any_other_number()
    {
        using var dir = new TempDir();
        // a file from a newer Clink: read as it is, nothing to migrate, but the number it carries is not trusted after a rewrite
        var newer = LoadFrom(dir, $$"""{ "SchemaVersion": 7, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }""");
        Check.Equal(3, newer.SchemaVersion);
        Check.False(newer.Migrated, "nothing to do until something is saved");
        newer.Save();
        using (var doc = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json"))))
            Check.Equal(3, doc.RootElement.GetProperty("SchemaVersion").GetInt32());

        // a hand edit that lowers it, and one that sets it by code, are raised on the next save as well
        var low = LoadFrom(dir, $$"""{ "SchemaVersion": 1, "Hotkey": { "Vk": 120 }, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}} }""");
        Check.True(low.Migrated);
        low.SchemaVersion = 1;
        low.Save();
        using (var doc = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json"))))
            Check.Equal(3, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
        low.SchemaVersion = 9;
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
    public static void Unknown_keys_inside_hotkeys_and_toast_settings_survive_too()
    {
        using var dir = new TempDir();
        const string json = """
            { "SchemaVersion": 3,
              "Hotkey": { "Alt": true, "Vk": 67, "Future": 1 },
              "Hotkeys": [ { "Keys": { "Alt": true, "Vk": 67, "Layer": "x" }, "Action": "clip", "Seconds": 0, "Label": "main" },
                           { "Keys": { "Vk": 120 }, "Action": "pause", "Seconds": 0, "Icon": [1, 2] } ],
              "ToastClip": { "Enabled": true, "Seconds": 4, "Sound": "pop" } }
            """;
        var s = LoadFrom(dir, json);
        s.Save();
        using var doc = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json")));
        var root = doc.RootElement;
        Check.Equal(1, root.GetProperty("Hotkey").GetProperty("Future").GetInt32());
        Check.Equal("x", root.GetProperty("Hotkeys")[0].GetProperty("Keys").GetProperty("Layer").GetString());
        Check.Equal("main", root.GetProperty("Hotkeys")[0].GetProperty("Label").GetString());
        Check.Equal(2, root.GetProperty("Hotkeys")[1].GetProperty("Icon").GetArrayLength());
        Check.Equal("pop", root.GetProperty("ToastClip").GetProperty("Sound").GetString());

        // moving a row to the front takes its own extra keys with it, and a copy of a binding has its own
        var reshaped = LoadFrom(dir, """{ "Hotkeys": [ { "Keys": { "Shift": true, "Vk": 120 }, "Action": "clip", "Seconds": 30, "Label": "a" }, { "Keys": { "Vk": 120, "Layer": "b" }, "Action": "clip", "Seconds": 0, "Label": "b" } ] }""");
        Check.Equal(new[] { "F9 clip 0", "Shift+F9 clip 30" }, Rows(reshaped));
        Check.Equal("b", reshaped.Hotkeys[0].Unknown!["Label"].GetString());
        Check.Equal("a", reshaped.Hotkeys[1].Unknown!["Label"].GetString());
        var copy = reshaped.Hotkeys[0].Keys.Clone();
        Check.Equal("b", copy.Unknown!["Layer"].GetString());
        copy.Unknown.Remove("Layer");
        Check.True(reshaped.Hotkeys[0].Keys.Unknown!.ContainsKey("Layer"), "the copy's dictionary is its own");
        Check.Null(new HotkeyBinding().Clone().Unknown);
    }

    [UnitTest]
    public static void Keys_named_like_members_that_are_not_saved_are_kept_and_such_names_are_never_saved()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Fps": 30, "MainHotkey": { "a": 1 }, "Migrated": "later", "CopiumToken": "x", "LoadProblem": 5, "HotkeysFromFile": true, "Unknown": [1] }""");
        Check.Equal(30, s.Fps);
        Check.Null(s.LoadProblem, "a key called LoadProblem is not the member");
        Check.False(s.UnreadableValues.Any());
        Check.Equal(new[] { "CopiumToken", "HotkeysFromFile", "LoadProblem", "MainHotkey", "Migrated", "Unknown" },
            s.Unknown!.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Check.Null(s.CopiumTokenProtected, "and the plain text under that name is not a token");
        s.Save();
        using (var doc = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json"))))
        {
            Check.Equal(1, doc.RootElement.GetProperty("MainHotkey").GetProperty("a").GetInt32());
            Check.Equal("later", doc.RootElement.GetProperty("Migrated").GetString());
            Check.Equal("x", doc.RootElement.GetProperty("CopiumToken").GetString());
            Check.Equal(5, doc.RootElement.GetProperty("LoadProblem").GetInt32());
            Check.True(doc.RootElement.GetProperty("HotkeysFromFile").GetBoolean());
            Check.Equal(1, doc.RootElement.GetProperty("Unknown")[0].GetInt32());
        }

        // this version itself never writes a key with the name of a member it leaves out, so such keys can only be someone else's
        // (the same rule as Settings.Bindable: only an unconditional [JsonIgnore]; WhenWritingNull members such as FeedFloorStable are read and saved)
        var notSaved = typeof(Settings).GetProperties()
            .Where(p => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>(p)
                is { Condition: System.Text.Json.Serialization.JsonIgnoreCondition.Always })
            .Select(p => p.Name).ToList();
        Check.True(notSaved.Contains("MainHotkey") && notSaved.Contains("Migrated") && notSaved.Contains("CopiumToken"), "the reflection finds the members");
        using var written = JsonDocument.Parse(Settings.FromJson("{}").ToJson());
        Check.False(written.RootElement.EnumerateObject().Any(p => notSaved.Contains(p.Name)));
    }

    [UnitTest]
    public static void A_NaN_never_reaches_the_file()
    {
        // "NaN" as text is no number to anything but the reader that was told to accept it, and means nothing as a window position or
        // a volume: the values are put back to their defaults before the file is written.
        using var dir = new TempDir();
        var s = LoadFrom(dir, null);
        s.Fps = 30;
        s.WindowX = double.NaN; s.WindowY = double.PositiveInfinity; s.WindowWidth = double.NaN; s.WindowHeight = double.NegativeInfinity;
        s.HdrKnee = float.NaN; s.HdrPeak = float.PositiveInfinity; s.KeyframeSeconds = float.NaN;
        s.ClickVolume = double.NaN; s.ArchiveWhenLocalExceedsGB = double.PositiveInfinity;
        s.Save();

        string text = File.ReadAllText(dir.File("settings.json"));
        Check.DoesNotContain(text, "NaN");
        Check.DoesNotContain(text, "Infinity");
        using (var doc = JsonDocument.Parse(text))
        {
            var root = doc.RootElement;
            foreach (string key in new[] { "WindowX", "WindowY", "WindowWidth", "WindowHeight" })
                Check.Equal(JsonValueKind.Null, root.GetProperty(key).ValueKind, key + " is back to \"let Windows place it\"");
            Check.Equal(0.75, root.GetProperty("HdrKnee").GetDouble());
            Check.Equal(4.0, root.GetProperty("HdrPeak").GetDouble());
            Check.Equal(1.0, root.GetProperty("KeyframeSeconds").GetDouble());
            Check.Equal(0.5, root.GetProperty("ClickVolume").GetDouble());
            Check.Equal(0.0, root.GetProperty("ArchiveWhenLocalExceedsGB").GetDouble());
            Check.Equal(30, root.GetProperty("Fps").GetInt32());
        }
        // the running settings were put right as well, and a reload agrees
        Check.Null(s.WindowX);
        Check.Equal(0.75f, s.HdrKnee);
        var again = Settings.Load();
        Check.Null(again.LoadProblem);
        Check.Null(again.WindowX);
        Check.Null(again.WindowWidth);
        Check.Equal(0.75f, again.HdrKnee);
        Check.Equal(30, again.Fps);
    }

    [UnitTest]
    public static void A_NaN_written_as_text_by_an_earlier_save_is_put_right_as_soon_as_it_is_loaded()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "HdrKnee": "NaN", "WindowX": "NaN", "ClickVolume": "Infinity", "Fps": 30 }""");
        Check.Null(s.LoadProblem);
        // clean after Load, not only after the next save: BuildConfig and the encoder never see a NaN
        Check.Equal(0.75f, s.HdrKnee);
        Check.Null(s.WindowX);
        Check.Equal(0.5, s.ClickVolume);
        Check.Equal(30, s.Fps);
        s.Save();
        var again = Settings.Load();
        Check.Equal(0.75f, again.HdrKnee);
        Check.Null(again.WindowX);
        Check.Equal(0.5, again.ClickVolume);
        Check.Equal(30, again.Fps);
        Check.DoesNotContain(File.ReadAllText(dir.File("settings.json")), "NaN");
    }

    // ------------------------------------------------------------------ a file with values that make no sense

    [UnitTest]
    public static void Unreadable_values_keep_their_defaults_and_everything_else_loads()
    {
        using var dir = new TempDir();
        const string json = """{ "Fps": "sixty", "BufferSeconds": [1], "Codec": "hevc", "BitrateMbps": 16, "ToastPosition": "top-left", "FutureKey": 1 }""";
        var s = LoadFrom(dir, json);
        Check.Equal(60, s.Fps);
        Check.Equal(45, s.BufferSeconds);
        Check.Equal("hevc", s.Codec);
        Check.Equal(16, s.BitrateMbps);
        Check.Equal("top-left", s.ToastPosition);
        Check.Equal(new[] { "Fps", "BufferSeconds" }, s.UnreadableValues.ToArray());
        Check.Equal("settings.json: kept the defaults for Fps, BufferSeconds (unreadable values); the original is kept as settings.json.unreadable", s.LoadProblem);
        Check.False(s.FileWasSkipped, "the file was read, only two values were not");
        Check.Equal(json, File.ReadAllText(dir.File("settings.json.unreadable")), "what the file said, kept before anything is saved");
        Check.Equal(json, File.ReadAllText(dir.File("settings.json")), "the file itself is left alone until something saves");

        // a save writes the repaired values, and every other key stays, the unknown one too
        s.Save();
        using var doc = JsonDocument.Parse(File.ReadAllText(dir.File("settings.json")));
        Check.Equal(60, doc.RootElement.GetProperty("Fps").GetInt32());
        Check.Equal(45, doc.RootElement.GetProperty("BufferSeconds").GetInt32());
        Check.Equal("hevc", doc.RootElement.GetProperty("Codec").GetString());
        Check.Equal(16, doc.RootElement.GetProperty("BitrateMbps").GetInt32());
        Check.Equal("top-left", doc.RootElement.GetProperty("ToastPosition").GetString());
        Check.Equal(1, doc.RootElement.GetProperty("FutureKey").GetInt32());
        Check.Equal(json, File.ReadAllText(dir.File("settings.json.unreadable")), "and the copy of the original stays");
        Check.Null(Settings.Load().LoadProblem, "the repaired file is fine");
    }

    [UnitTest]
    public static void A_value_of_the_wrong_kind_or_a_null_for_a_number_is_unreadable()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """
            { "BufferSeconds": 45.5, "Fps": null, "CaptureCursor": null, "ClipsFolder": 5, "HdrKnee": "0.9", "WindowX": null,
              "CopiumPlus": "yes", "ToastClip": { "Seconds": "x" }, "Resolution": null }
            """);
        Check.Equal(new[] { "BufferSeconds", "Fps", "CaptureCursor", "ClipsFolder", "CopiumPlus", "ToastClip" }, s.UnreadableValues.ToArray());
        Check.Equal(45, s.BufferSeconds);
        Check.Equal(60, s.Fps);
        Check.True(s.CaptureCursor);
        Check.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink"), s.ClipsFolder);
        Check.False(s.CopiumPlus);
        Check.Equal(7, s.ToastClip.Seconds);
        Check.Equal(0.9f, s.HdrKnee);          // a number in quotes is fine
        Check.Null(s.WindowX);                 // so is a null for an optional one
        Check.Equal("1080p", s.Resolution);    // and a null for a text gets the default, as it always did
    }

    [UnitTest]
    public static void An_unreadable_hotkey_list_costs_only_the_hotkeys()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, """{ "Codec": "hevc", "Hotkeys": [ { "Keys": { "Vk": "x" } } ] }""");
        Check.Equal(new[] { "Hotkeys row 1" }, s.UnreadableValues.ToArray(), "the one row there is, left out");
        Check.Equal("hevc", s.Codec);
        Check.Equal(new[] { "unset clip 0" }, Rows(s));   // as if the list were empty: nothing is bound for someone whose hotkeys are not known
        Check.True(s.MainHotkeyNeedsSetup);
        Check.False(s.MainHotkeyAssigned);
    }

    [UnitTest]
    public static void Text_that_is_not_JSON_still_sends_Load_to_the_backup()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json.bak"), """{ "Fps": 30 }""");
        var s = LoadFrom(dir, "{ not json");
        Check.Equal(30, s.Fps);
        Check.True(s.FileWasSkipped);
        Check.Contains(s.LoadProblem, "settings.json was not used");
        Check.Contains(s.LoadProblem, "the previous settings");
        Check.Equal("{ not json", File.ReadAllText(dir.File("settings.json.unreadable")));

        // a backup that is no better: the defaults, and the same story
        using var dir2 = new TempDir();
        var d = LoadFrom(dir2, "[1, 2]");   // JSON, but not a settings object
        Check.True(d.FileWasSkipped);
        Check.Equal(60, d.Fps);
        Check.Contains(d.LoadProblem, "default settings");
        Check.True(File.Exists(dir2.File("settings.json.unreadable")));
    }

    [UnitTest]
    public static void A_file_that_says_null_is_an_empty_one()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, "null");
        Check.Null(s.LoadProblem);
        Check.False(s.FileWasSkipped);
        Check.Equal(60, s.Fps);
        Check.Equal(new[] { "Alt+C clip 0" }, Rows(s));
    }

    [UnitTest]
    public static void FromJson_reads_the_way_Load_does_without_touching_anything()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => logged.Enqueue(line);
        Log.Line += Collect;
        try
        {
            var s = Settings.FromJson("""{ "Fps": "sixty", "ShowToast": false, "FutureKey": 1 }""");
            Check.Equal(60, s.Fps);
            Check.False(s.ToastClip.Enabled);
            Check.NotNull(s.Unknown);
            Check.Equal(new[] { "Alt+C clip 0" }, Rows(s));
            Check.Throws<JsonException>(() => Settings.FromJson("{ not json"));
        }
        finally { Log.Line -= Collect; }
        Check.Equal(0, logged.Count, "nothing is logged");
        Check.Equal(0, Directory.GetFileSystemEntries(Settings.Dir).Length, "and no file is written or copied");
    }

    // ------------------------------------------------------------------ saving

    [UnitTest]
    public static void Frozen_saves_write_nothing_until_thawed()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, null);
        s.Save();
        string before = File.ReadAllText(dir.File("settings.json"));
        s.Fps = 30;
        Settings.FreezeSaves();
        try
        {
            Check.True(Settings.SavesFrozen);
            s.Save();
            Check.Equal(before, File.ReadAllText(dir.File("settings.json")), "a frozen save writes nothing");
            Check.False(File.Exists(dir.File("settings.json.bak")), "and does not rotate the backup either");
        }
        finally { Settings.ThawSaves(); }
        Check.False(Settings.SavesFrozen);
        s.Save();
        Check.Contains(File.ReadAllText(dir.File("settings.json")), "\"Fps\": 30");
    }

    [UnitTest]
    public static void A_save_asked_for_by_another_thread_is_done_on_the_UI_thread()
    {
        using var dir = new TempDir();
        var s = LoadFrom(dir, null);
        var other = new Thread(() => s.Save());
        other.Start();
        other.Join();
        Check.False(File.Exists(dir.File("settings.json")), "that thread only queued it");
        PumpUi();
        Check.True(File.Exists(dir.File("settings.json")), "the UI thread did it");
        File.Delete(dir.File("settings.json"));
        s.Save();   // asked for on the UI thread itself: done right away
        Check.True(File.Exists(dir.File("settings.json")));
    }

    /// <summary>Lets what was queued for the UI thread run.</summary>
    private static void PumpUi()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [UnitTest]
    public static void The_first_save_of_an_older_file_keeps_it_once_as_pre_v3()
    {
        using var dir = new TempDir();
        const string old = """{ "Fps": 30, "Hotkeys": [] }""";   // schema 2
        var s = LoadFrom(dir, old);
        Check.False(File.Exists(dir.File("settings.pre-v3.json")), "nothing is copied until the file is replaced");
        s.Save();
        Check.Equal(old, File.ReadAllText(dir.File("settings.pre-v3.json")));
        Check.Contains(File.ReadAllText(dir.File("settings.json")), "\"SchemaVersion\": 3");
        s.Fps = 60;
        s.Save();
        Check.Equal(old, File.ReadAllText(dir.File("settings.pre-v3.json")), "later saves leave it alone");

        // an older file again (the user went back to an old Clink, say): the copy from the first time stays
        File.WriteAllText(dir.File("settings.json"), """{ "Fps": 120, "Hotkeys": [] }""");
        var again = Settings.Load();
        again.Save();
        Check.Equal(old, File.ReadAllText(dir.File("settings.pre-v3.json")), "never overwritten");
    }

    [UnitTest]
    public static void A_v1_file_is_kept_too_but_a_v3_file_and_a_first_start_have_nothing_to_keep()
    {
        using var v1 = new TempDir();
        const string old = """{ "Fps": 30, "Hotkey": { "Vk": 120 } }""";
        LoadFrom(v1, old).Save();
        Check.Equal(old, File.ReadAllText(v1.File("settings.pre-v3.json")));

        using var v3 = new TempDir();
        LoadFrom(v3, $$"""{ "SchemaVersion": 3, "Hotkeys": {{HotkeysJson((120, "", "clip", 0))}}, "Hotkey": { "Vk": 120 } }""").Save();
        Check.False(File.Exists(v3.File("settings.pre-v3.json")));

        using var fresh = new TempDir();
        LoadFrom(fresh, null).Save();
        Check.False(File.Exists(fresh.File("settings.pre-v3.json")));

        // a file that was read from the backup is kept as what was read
        using var viaBackup = new TempDir();
        File.WriteAllText(viaBackup.File("settings.json.bak"), old);
        LoadFrom(viaBackup, "{ not json").Save();
        Check.Equal(old, File.ReadAllText(viaBackup.File("settings.pre-v3.json")));
    }

    [UnitTest]
    public static void Settings_follow_the_dir_override()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        Check.Equal(dir.Path, Settings.Dir);
        Check.Equal(dir.File("settings.json"), Settings.FilePath);
        Settings.DirOverride = null;
        Settings.RealDirOverride = null;   // the runner points the real Clink's folder at a scratch one too
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
            Check.Equal("Clink.SingleInstance.6F1B", AppController.MutexName);   // the real Clink's, exactly
            AppController.InstanceSuffix = a.InstanceSuffix;
            Check.True(AppController.IsDev);
            Check.Equal("Clink.Control" + a.InstanceSuffix, AppController.PipeName);
            Check.NotEqual("Clink.Control", AppController.PipeName);
            Check.Equal("Clink.SingleInstance.6F1B" + a.InstanceSuffix, AppController.MutexName);
            Check.NotEqual("Clink.SingleInstance.6F1B", AppController.MutexName);
            AppController.InstanceSuffix = b.InstanceSuffix;
            Check.NotEqual("Clink.SingleInstance.6F1B" + a.InstanceSuffix, AppController.MutexName, "another folder, another mutex");
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
