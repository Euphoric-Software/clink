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
