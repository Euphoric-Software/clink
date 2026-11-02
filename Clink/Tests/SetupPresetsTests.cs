using System.IO;
using System.Reflection;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Quality presets and replay lengths of the first-run setup.</summary>
internal static class SetupPresetsTests
{
    [UnitTest]
    public static void The_presets_have_the_agreed_values()
    {
        Check.Equal(("720p", 30, 8), SetupPresets.ValuesOf(QualityPreset.Light)!.Value);
        Check.Equal(("1080p", 60, 20), SetupPresets.ValuesOf(QualityPreset.Balanced)!.Value);
        Check.Equal(("1440p", 60, 40), SetupPresets.ValuesOf(QualityPreset.High)!.Value);
        Check.Null(SetupPresets.ValuesOf(QualityPreset.Custom));
    }

    [UnitTest]
    public static void Presets_round_trip_through_the_settings()
    {
        foreach (var p in new[] { QualityPreset.Light, QualityPreset.Balanced, QualityPreset.High })
        {
            var s = new Settings();
            SetupPresets.Apply(s, p);
            Check.Equal(p, SetupPresets.PresetOf(s), p.ToString());
        }
    }

    [UnitTest]
    public static void Anything_else_is_custom_and_custom_changes_nothing()
    {
        Check.Equal(QualityPreset.Custom, SetupPresets.PresetOf("1080p", 30, 20));
        Check.Equal(QualityPreset.Custom, SetupPresets.PresetOf("1080p", 60, 21));
        Check.Equal(QualityPreset.Custom, SetupPresets.PresetOf("native", 60, 20));
        Check.Equal(QualityPreset.Custom, SetupPresets.PresetOf(null, 60, 20));
        var s = new Settings { Resolution = "native", Fps = 144, BitrateMbps = 55 };
        SetupPresets.Apply(s, QualityPreset.Custom);
        Check.Equal(("native", 144, 55), (s.Resolution, s.Fps, s.BitrateMbps));
        Check.Equal(QualityPreset.Custom, SetupPresets.PresetOf(s));
    }

    [UnitTest]
    public static void Applying_a_preset_leaves_the_other_settings_alone()
    {
        var s = new Settings { Codec = "hevc", BufferSeconds = 120 };
        SetupPresets.Apply(s, QualityPreset.High);
        Check.Equal("hevc", s.Codec);
        Check.Equal(120, s.BufferSeconds);
    }

    [UnitTest]
    public static void The_defaults_equal_a_fresh_settings()
    {
        var fresh = new Settings();
        Check.Equal(SetupPresets.DefaultQuality, SetupPresets.PresetOf(fresh));
        Check.Equal(SetupPresets.DefaultReplaySeconds, fresh.BufferSeconds);
        Check.Equal(45, SetupPresets.DefaultReplaySeconds);
        var applied = new Settings();
        SetupPresets.ApplyDefaults(applied);
        Check.Equal((fresh.Resolution, fresh.Fps, fresh.BitrateMbps, fresh.BufferSeconds), (applied.Resolution, applied.Fps, applied.BitrateMbps, applied.BufferSeconds));
        Check.False(fresh.RunAtStartup, "Start with Windows is off by default");
    }

    [UnitTest]
    public static void Default_bitrates_follow_the_codec_per_preset()
    {
        // H.264 100 %, HEVC 65 %, AV1 50 %, rounded to the list (nearest, a tie up): Balanced HEVC 13 is 12 or 14 -> 14.
        var rows = new (string Res, int Fps, string Codec, int Mbps)[]
        {
            ("720p", 30, "h264", 8), ("720p", 30, "hevc", 6), ("720p", 30, "av1", 4),
            ("1080p", 60, "h264", 20), ("1080p", 60, "hevc", 14), ("1080p", 60, "av1", 10),
            ("1440p", 60, "h264", 40), ("1440p", 60, "hevc", 25), ("1440p", 60, "av1", 20),
        };
        foreach (var r in rows)
        {
            Check.Equal(r.Mbps, SetupPresets.DefaultBitrateMbps(r.Res, r.Fps, r.Codec)!.Value, $"{r.Res} {r.Fps} {r.Codec}");
            Check.True(SetupPresets.BitrateChoices.Contains(r.Mbps), $"{r.Mbps} is on the list");
        }
        Check.Equal(14, SetupPresets.DefaultBitrateMbps("1080P", 60, "H.265")!.Value, "case and codec aliases as the engine reads them");
        Check.Equal(20, SetupPresets.DefaultBitrateMbps("1080p", 60, "nonsense")!.Value, "an unknown codec is H.264");
        Check.Equal(20, SetupPresets.DefaultBitrateMbps("1080p", 60, null)!.Value);
    }

    [UnitTest]
    public static void The_bitrate_list_has_10_and_14_and_is_sorted()
    {
        var list = SetupPresets.BitrateChoices.ToArray();
        Check.True(list.Contains(10) && list.Contains(14));
        Check.Equal(list.OrderBy(x => x).Distinct().ToArray(), list);
        Check.Equal(new[] { 4, 6, 8, 10, 12, 14, 16, 20, 25, 30, 40, 50, 60, 80, 100 }, list);
    }

    // ---- a codec switch never changes the bitrate: driven through the real MainWindow handler (QualityChanged) ----

    private static MainWindow NewQualityWindow(TempDir dir, Settings s, out AppController app)
    {
        Settings.DirOverride = dir.Path;
        s.ClipsFolder = dir.File("clips");
        Directory.CreateDirectory(s.ClipsFolder);
        app = new AppController(s, new Updater(s));
        var w = new MainWindow();
        static Choice[] Of<T>(params (string Label, T Value)[] xs) => xs.Select(x => new Choice(x.Label, x.Value!)).ToArray();
        w.ResolutionCombo.ItemsSource = Of(("720p", "720p"), ("1080p", "1080p"), ("1440p", "1440p"), ("Native", "native"));
        w.FpsCombo.ItemsSource = Of(("30", 30), ("60", 60), ("120", 120));
        w.BitrateCombo.ItemsSource = SetupPresets.BitrateItems(s.BitrateMbps).Select(b => new Choice($"{b} Mbps", b)).ToArray();
        w.CodecCombo.ItemsSource = Of(("H.264", "h264"), ("HEVC", "hevc"), ("AV1", "av1"));
        w.ResolutionCombo.SelectedIndex = Array.FindIndex(((Choice[])w.ResolutionCombo.ItemsSource), c => Equals(c.Value, s.Resolution));
        w.FpsCombo.SelectedIndex = Array.FindIndex(((Choice[])w.FpsCombo.ItemsSource), c => Equals(c.Value, s.Fps));
        w.BitrateCombo.SelectedIndex = Array.FindIndex(((Choice[])w.BitrateCombo.ItemsSource), c => Equals(c.Value, s.BitrateMbps));
        w.CodecCombo.SelectedIndex = Array.FindIndex(((Choice[])w.CodecCombo.ItemsSource), c => Equals(c.Value, s.Codec));
        typeof(MainWindow).GetField("_loading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, false);
        return w;
    }

    private static void PickCodec(MainWindow w, string codec) =>
        w.CodecCombo.SelectedIndex = Array.FindIndex(((Choice[])w.CodecCombo.ItemsSource), c => Equals(c.Value, codec));

    [UnitTest]
    public static void A_codec_switch_never_changes_the_bitrate_and_saves_the_codec()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("""{ "Resolution": "1080p", "Fps": 60, "BitrateMbps": 20, "Codec": "h264" }""");
        Check.False(s.BitrateChosen);
        using var app = NewQualityWindowApp(dir, s, out var w);
        foreach (string codec in new[] { "hevc", "av1", "h264" })
        {
            PickCodec(w, codec);
            Check.Equal(codec, s.Codec, $"{codec} is set");
            Check.Equal(20, s.BitrateMbps, $"still 20 on {codec}");
            Check.False(s.BitrateChosen, $"a codec switch is no bitrate pick ({codec})");
            var saved = Settings.Load();
            Check.Equal(codec, saved.Codec, $"{codec} is saved");
            Check.Equal(20, saved.BitrateMbps, $"20 is saved on {codec}");
        }
    }

    [UnitTest]
    public static void A_chosen_bitrate_stays_through_codec_switches()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("""{ "Resolution": "1080p", "Fps": 60, "BitrateMbps": 25, "BitrateChosen": true, "Codec": "h264" }""");
        using var app = NewQualityWindowApp(dir, s, out var w);
        foreach (string codec in new[] { "hevc", "av1", "h264" })
        {
            PickCodec(w, codec);
            Check.Equal(25, s.BitrateMbps, $"25 on {codec}");
            Check.True(s.BitrateChosen);
            Check.Equal(codec, Settings.Load().Codec, $"{codec} is saved");
            Check.Equal(25, Settings.Load().BitrateMbps);
        }
    }

    [UnitTest]
    public static void A_codec_switch_keeps_the_bitrate_off_the_presets_too_and_the_bitrate_box_still_marks_a_pick()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("""{ "Resolution": "native", "Fps": 60, "BitrateMbps": 20, "Codec": "h264" }""");
        using var app = NewQualityWindowApp(dir, s, out var w);
        PickCodec(w, "hevc");
        Check.Equal(20, s.BitrateMbps);
        w.BitrateCombo.SelectedIndex = Array.FindIndex(((Choice[])w.BitrateCombo.ItemsSource), c => Equals(c.Value, 14));
        Check.Equal(14, s.BitrateMbps);
        Check.True(s.BitrateChosen, "the bitrate box itself is a pick");
        PickCodec(w, "av1");
        Check.Equal(14, s.BitrateMbps);
    }

    // owns the app and the window together; every QualityChanged starts the window's 700 ms _restartTimer, so Dispose stops it
    // (else it could fire RequestEngineRestart on the disposed app during a later test), closes the window, then disposes the app
    private sealed class QualityHarness(MainWindow window, AppController app) : IDisposable
    {
        public void Dispose()
        {
            ((System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_restartTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
            try { window.Close(); } catch (InvalidOperationException) { }
            app.Dispose();
        }
    }

    private static IDisposable NewQualityWindowApp(TempDir dir, Settings s, out MainWindow w)
    {
        w = NewQualityWindow(dir, s, out var app);
        return new QualityHarness(w, app);
    }


    [UnitTest]
    public static void Only_a_user_change_of_the_bitrate_box_marks_the_bitrate_chosen()
    {
        var s = new Settings();   // 1080p / 60 / 20
        Check.False(SetupPresets.PickBitrate(s, 20, byUser: true), "the same value is no change");
        Check.False(s.BitrateChosen);
        Check.False(SetupPresets.PickBitrate(s, 0, byUser: true), "nothing selected");
        Check.False(s.BitrateChosen);
        Check.True(SetupPresets.PickBitrate(s, 14, byUser: true), "a different value from the user");
        Check.Equal(14, s.BitrateMbps);
        Check.True(s.BitrateChosen);
        Check.False(SetupPresets.PickBitrate(s, 14, byUser: true), "the same value again");
    }

    [UnitTest]
    public static void A_bitrate_stored_by_another_box_or_a_refill_is_not_a_user_pick()
    {
        var s = new Settings();
        Check.True(SetupPresets.PickBitrate(s, 25, byUser: false));
        Check.Equal(25, s.BitrateMbps);
        Check.False(s.BitrateChosen, "stored, not chosen");
        Check.True(SetupPresets.PickBitrate(s, 30, byUser: true));
        Check.True(s.BitrateChosen);
    }

    [UnitTest]
    public static void Applying_a_preset_resets_the_chosen_flag()
    {
        var s = new Settings { BitrateMbps = 14, BitrateChosen = true };
        SetupPresets.Apply(s, QualityPreset.Balanced);
        Check.False(s.BitrateChosen);
        Check.Equal(20, s.BitrateMbps);
        s.BitrateChosen = true;
        SetupPresets.Apply(s, QualityPreset.Custom);
        Check.True(s.BitrateChosen, "Custom changes nothing");
    }

    [UnitTest]
    public static void A_stored_bitrate_that_is_not_on_the_list_is_kept_and_listed()
    {
        var items = SetupPresets.BitrateItems(13);
        Check.Equal(new[] { 4, 6, 8, 10, 12, 13, 14, 16, 20, 25, 30, 40, 50, 60, 80, 100 }, items.ToArray());
        Check.Equal(new[] { 4, 6, 8, 10, 12, 14, 16, 20, 25, 30, 40, 50, 60, 80, 100 }, SetupPresets.BitrateItems(20).ToArray());
        Check.Equal(16, SetupPresets.BitrateItems(3).Count, "below the list too");
        Check.Equal(3, SetupPresets.BitrateItems(3)[0]);
        Check.Equal(15, SetupPresets.BitrateItems(0).Count, "nothing stored: the list as is");
        var s = new Settings { BitrateMbps = 13 };
        Check.False(SetupPresets.PickBitrate(s, 13, byUser: false), "the combo selecting its own 13 entry changes nothing");
        Check.Equal(13, s.BitrateMbps);
        Check.False(s.BitrateChosen);
    }

    [UnitTest]
    public static void Off_preset_resolution_or_fps_has_no_default_bitrate()
    {
        foreach (var (res, fps) in new[] { ("native", 60), ("1080p", 30), ("1440p", 120) })
            Check.Null(SetupPresets.DefaultBitrateMbps(res, fps, "h264"), $"{res} {fps}");
    }

    [UnitTest]
    public static void Applying_a_preset_uses_the_codec_default_and_still_reads_as_that_preset()
    {
        var s = new Settings { Codec = "hevc" };
        SetupPresets.Apply(s, QualityPreset.Balanced);
        Check.Equal(14, s.BitrateMbps);
        Check.Equal(QualityPreset.Balanced, SetupPresets.PresetOf(s));
        s.Codec = "av1";
        Check.Equal(QualityPreset.Custom, SetupPresets.PresetOf(s), "14 is not AV1's default");
        SetupPresets.Apply(s, QualityPreset.Light);
        Check.Equal(4, s.BitrateMbps);
        Check.Equal(QualityPreset.Light, SetupPresets.PresetOf(s));
    }

    [UnitTest]
    public static void Replay_lengths_are_the_five_choices()
    {
        Check.Equal(new[] { 30, 45, 60, 120, 300 }, SetupPresets.ReplayChoices.ToArray());
        foreach (int sec in SetupPresets.ReplayChoices)
        {
            var s = new Settings();
            SetupPresets.ApplyReplay(s, sec);
            Check.Equal(sec, s.BufferSeconds);
        }
        var odd = new Settings { BufferSeconds = 90 };
        SetupPresets.ApplyReplay(odd, 91);
        Check.Equal(90, odd.BufferSeconds, "a length that is not a choice is ignored");
        Check.False(SetupPresets.IsReplayChoice(90));
        Check.Equal("30 s", SetupPresets.ReplayLabel(30));
        Check.Equal("45 s", SetupPresets.ReplayLabel(45));
        Check.Equal("1 min", SetupPresets.ReplayLabel(60));
        Check.Equal("5 min", SetupPresets.ReplayLabel(300));
    }
}
