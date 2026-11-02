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

    /// <summary>The bitrates (Mbps) the Capture page's bitrate box offers, ascending. Every <see cref="DefaultBitrateMbps"/> is one of them.</summary>
    public static readonly IReadOnlyList<int> BitrateChoices = new[] { 4, 6, 8, 10, 12, 14, 16, 20, 25, 30, 40, 50, 60, 80, 100 };

    /// <summary>
    /// The bitrate a preset's resolution and frame rate start at for a codec, as percent of the preset's H.264 value: H.264 100,
    /// HEVC 65, AV1 50 (the same picture quality in fewer bits). Rounded to <see cref="BitrateChoices"/>: the nearest entry, a tie
    /// going to the higher one (so quality, not size, wins a doubt). Light 8 -> 6 / 4, Balanced 20 -> 14 / 10 (13 is 12 or 14: 14),
    /// High 40 -> 25 / 20. Null when the resolution and frame rate are none of the presets (Native, 1080p30, ...): there is no
    /// default. Unknown codecs count as H.264, like the engine does.
    /// Note: the app does not call this (nor Apply, PresetOf(Settings) or ApplyDefaults) and it still holds codec-aware defaults. The owner
    /// ruled (2026-11-01) that a codec change never changes the bitrate; anyone wiring these into a setup flow must keep that rule.
    /// </summary>
    public static int? DefaultBitrateMbps(string? resolution, int fps, string? codec)
    {
        foreach (var p in new[] { QualityPreset.Light, QualityPreset.Balanced, QualityPreset.High })
        {
            var v = ValuesOf(p)!.Value;
            if (!string.Equals(v.Resolution, resolution, StringComparison.OrdinalIgnoreCase) || v.Fps != fps) continue;
            int percent = Clink.Engine.EngineConfig.NormalizeCodec(codec) switch { "hevc" => 65, "av1" => 50, _ => 100 };
            return NearestChoice(v.BitrateMbps * percent);
        }
        return null;
    }

    // hundredths of a Mbps -> the nearest BitrateChoices entry; whole-number arithmetic so 13.00 is exactly a tie, ties go up
    private static int NearestChoice(int hundredths)
    {
        int best = BitrateChoices[0];
        foreach (int c in BitrateChoices)
            if (Math.Abs(c * 100 - hundredths) <= Math.Abs(best * 100 - hundredths)) best = c;
        return best;
    }

    /// <summary>
    /// The bitrate combo reported <paramref name="picked"/> (0 or less = nothing). Stores it; when it differs from the stored value
    /// and <paramref name="byUser"/> (the user changed the bitrate box, not some other box or a programmatic fill) it also marks the
    /// bitrate as chosen. A codec change never touches the bitrate either way. Returns whether the value changed.
    /// </summary>
    public static bool PickBitrate(Settings s, int picked, bool byUser)
    {
        if (picked <= 0 || picked == s.BitrateMbps) return false;
        s.BitrateMbps = picked;
        if (byUser) s.BitrateChosen = true;
        return true;
    }

    /// <summary>The bitrates the box lists for a stored value: <see cref="BitrateChoices"/>, plus the stored value (a hand edit, e.g. 13) in its place when it is not one of them.</summary>
    public static IReadOnlyList<int> BitrateItems(int stored)
    {
        if (stored <= 0 || BitrateChoices.Contains(stored)) return BitrateChoices;
        return BitrateChoices.Append(stored).OrderBy(x => x).ToArray();
    }

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

    /// <summary>The preset the settings are: its H.264 values, or its default bitrate for the settings' codec. Not called by the app; codec-aware (see <see cref="DefaultBitrateMbps"/>).</summary>
    public static QualityPreset PresetOf(Settings s)
    {
        var p = PresetOf(s.Resolution, s.Fps, s.BitrateMbps);
        if (p != QualityPreset.Custom) return p;
        if (DefaultBitrateMbps(s.Resolution, s.Fps, s.Codec) != s.BitrateMbps) return QualityPreset.Custom;
        foreach (var q in new[] { QualityPreset.Light, QualityPreset.Balanced, QualityPreset.High })
        {
            var v = ValuesOf(q)!.Value;
            if (string.Equals(v.Resolution, s.Resolution, StringComparison.OrdinalIgnoreCase) && v.Fps == s.Fps) return q;
        }
        return QualityPreset.Custom;
    }

    /// <summary>Writes a preset's resolution, frame rate and bitrate (the default for the settings' current codec) into the settings. Custom changes nothing. Not called by the app; codec-aware (see <see cref="DefaultBitrateMbps"/>): a codec change must never change the bitrate.</summary>
    public static void Apply(Settings s, QualityPreset preset)
    {
        if (ValuesOf(preset) is not { } v) return;
        s.Resolution = v.Resolution;
        s.Fps = v.Fps;
        s.BitrateMbps = DefaultBitrateMbps(v.Resolution, v.Fps, s.Codec) ?? v.BitrateMbps;
        s.BitrateChosen = false;   // a preset's bitrate is a default again
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

    /// <summary>What Skip leaves: the default quality preset and replay length, written explicitly (a fresh Settings already holds the same values, so on one it changes nothing). Not called by the app; goes through the codec-aware Apply (see <see cref="DefaultBitrateMbps"/>).</summary>
    public static void ApplyDefaults(Settings s)
    {
        Apply(s, DefaultQuality);
        s.BufferSeconds = DefaultReplaySeconds;
    }
}
