namespace Clink.Services;

/// <summary>The quality choices of the first-run setup. Custom is "whatever the settings hold that is none of the others"; it is never applied.</summary>
internal enum QualityPreset { Light, Balanced, High, Custom }

/// <summary>Quality presets and replay lengths of the first-run setup, as plain values over <see cref="Settings"/>. Pure.</summary>
internal static class SetupPresets
{
    /// <summary>The preset a fresh <see cref="Settings"/> matches.</summary>
    public const QualityPreset DefaultQuality = QualityPreset.Balanced;

    /// <summary>The replay length (seconds) a fresh <see cref="Settings"/> has.</summary>
    public const int DefaultReplaySeconds = SettingsDefaults.BufferSeconds;

    /// <summary>The replay lengths on offer: 30 s, 45 s (default), 1, 2 and 5 minutes.</summary>
    public static readonly IReadOnlyList<int> ReplayChoices = new[] { 30, 45, 60, 120, 300 };

    /// <summary>The settings values of a preset; null for Custom.</summary>
    public static (string Resolution, int Fps, int BitrateMbps)? ValuesOf(QualityPreset preset) => preset switch
    {
        QualityPreset.Light => ("720p", 30, 8),
        QualityPreset.Balanced => ("1080p", 60, 20),
        QualityPreset.High => ("1440p", 60, 40),
        _ => null,
    };

    /// <summary>The preset the values are exactly, otherwise Custom.</summary>
    public static QualityPreset PresetOf(string? resolution, int fps, int bitrateMbps)
    {
        foreach (var p in new[] { QualityPreset.Light, QualityPreset.Balanced, QualityPreset.High })
        {
            var v = ValuesOf(p)!.Value;
            if (string.Equals(v.Resolution, resolution, StringComparison.OrdinalIgnoreCase) && v.Fps == fps && v.BitrateMbps == bitrateMbps) return p;
        }
        return QualityPreset.Custom;
    }

    public static QualityPreset PresetOf(Settings s) => PresetOf(s.Resolution, s.Fps, s.BitrateMbps);

    /// <summary>Writes a preset's resolution, frame rate and bitrate into the settings. Custom changes nothing.</summary>
    public static void Apply(Settings s, QualityPreset preset)
    {
        if (ValuesOf(preset) is not { } v) return;
        s.Resolution = v.Resolution;
        s.Fps = v.Fps;
        s.BitrateMbps = v.BitrateMbps;
    }

    /// <summary>True when the seconds are one of <see cref="ReplayChoices"/> (otherwise the setting is a custom length and the page keeps it).</summary>
    public static bool IsReplayChoice(int seconds) => ReplayChoices.Contains(seconds);

    /// <summary>Writes a replay length; a value that is not one of the choices is ignored.</summary>
    public static void ApplyReplay(Settings s, int seconds)
    {
        if (IsReplayChoice(seconds)) s.BufferSeconds = seconds;
    }

    /// <summary>"30 s", "45 s", "1 min", "2 min", "5 min".</summary>
    public static string ReplayLabel(int seconds) => seconds % 60 == 0 && seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} s";

    /// <summary>What Skip leaves: the default quality preset and replay length, written explicitly (a fresh Settings already holds the same values, so on one it changes nothing).</summary>
    public static void ApplyDefaults(Settings s)
    {
        Apply(s, DefaultQuality);
        s.BufferSeconds = DefaultReplaySeconds;
    }
}
