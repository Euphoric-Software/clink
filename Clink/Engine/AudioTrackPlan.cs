using Clink.Audio;

namespace Clink.Engine;

/// <summary>What a planned audio track captures.</summary>
internal enum AudioSourceKind { Feeder, Game, Mic, Voice, Desktop }

/// <param name="Name">The track's name (the names of <see cref="MixPlan"/>); the feeder's is internal and never reaches a file.</param>
/// <param name="Written">The track goes into clips and recordings. The feeder never does; the Mic is captured even when it is not written, because it feeds All.</param>
/// <param name="MixOf">For a mix, the names of the specs it is summed from; null for a captured track.</param>
internal sealed record AudioTrackSpec(string Name, AudioSourceKind? Kind, bool Written, IReadOnlyList<string>? MixOf);

/// <summary>
/// Which audio tracks the engine builds (pure, no WASAPI). Track 1 is <c>All</c>: a hidden feeder that captures everything except
/// Clink itself, plus the Mic. Game, Voice and Desktop no longer feed it; each is captured only when its own track is written.
/// </summary>
internal static class AudioTrackPlan
{
    /// <summary>Internal name of the feeder track (never written, never shown).</summary>
    public const string FeederName = "Everything";

    /// <summary>Failed process-loopback starts, with an error that says the OS lacks it, after which the feeder switches to endpoint loopback.</summary>
    public const int FallbackFailures = 2;

    /// <summary>First Windows build with process loopback capture (Windows 11 and Server 2022; Windows 10 has the API but not the capture).</summary>
    public const int ProcessLoopbackBuild = 20348;

    /// <summary>This Windows can capture the audio of a process tree (or everything except it).</summary>
    public static bool ProcessLoopbackSupported() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, ProcessLoopbackBuild);

    /// <summary>The specs in engine order: All first, then the feeder, then the optional tracks in written order (Game, Mic, Voice, Desktop).</summary>
    public static IReadOnlyList<AudioTrackSpec> Plan(bool game, bool mic, bool voice, bool desktop)
    {
        var list = new List<AudioTrackSpec>
        {
            new(MixPlan.AllName, null, true, new[] { FeederName, MixPlan.MicName }),
            new(FeederName, AudioSourceKind.Feeder, false, null),
        };
        if (game) list.Add(new(MixPlan.GameName, AudioSourceKind.Game, true, null));
        list.Add(new(MixPlan.MicName, AudioSourceKind.Mic, mic, null));   // always captured: it feeds All
        if (voice) list.Add(new(MixPlan.VoiceName, AudioSourceKind.Voice, true, null));
        if (desktop) list.Add(new(MixPlan.DesktopName, AudioSourceKind.Desktop, true, null));
        return list;
    }

    /// <summary>The Game track's process source: the game's tree; with no game everything except the voice app, or except Clink itself (keeps its click sound out of the clip).</summary>
    public static (uint Pid, bool Include) GameLoopback(uint gamePid, uint voicePid, uint selfPid)
        => gamePid != 0 ? (gamePid, true) : (voicePid != 0 ? voicePid : selfPid, false);

    /// <summary>
    /// Whether the feeder should give up process loopback for the endpoint loopback (which still holds Clink's own sounds). On an
    /// unsupported Windows always; on a supported one only when activation keeps saying "not implemented/supported". Transient
    /// failures (a 3 s activation timeout, the audio service restarting after a resume) keep retrying process loopback.
    /// </summary>
    public static bool FallBackToEndpoint(bool osSupported, bool running, int failures, int lastHResult)
        => !osSupported || (!running && failures >= FallbackFailures && IsNotSupportedError(lastHResult));

    /// <summary>An activation error that means this system cannot do it, rather than something that may pass.</summary>
    public static bool IsNotSupportedError(int hr) => (uint)hr is
        0x80004001      // E_NOTIMPL
        or 0x80004002   // E_NOINTERFACE
        or 0x80040154   // REGDB_E_CLASSNOTREG
        or 0x80070032   // HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)
        or 0x80070078;  // HRESULT_FROM_WIN32(ERROR_CALL_NOT_IMPLEMENTED)

    /// <summary>The All row of the status: running means the feeder is, whatever the mic does; otherwise why not.</summary>
    public static (bool Running, string Detail) AllStatus(bool feederRunning, string? feederError, bool endpointFallback)
        => feederRunning
            ? (true, endpointFallback ? "system loopback (includes Clink sounds)" : "everything you hear + mic")
            : (false, feederError ?? "starting");

    /// <summary>The tracks of a plan, built.</summary>
    internal sealed record Built(AudioTrack All, AudioTrack Feeder, AudioTrack? Game, AudioTrack? Voice);

    /// <summary>
    /// Builds the tracks of a plan: All first, then the feeder and the rest in plan order, appended to <paramref name="into"/> as each
    /// is made (a failing <paramref name="source"/> leaves the earlier ones there to be disposed). <paramref name="source"/> is asked
    /// for a source only for tracks the plan has; null leaves the track without one.
    /// </summary>
    public static Built Build(IReadOnlyList<AudioTrackSpec> plan, Func<AudioSourceKind, WasapiSource?> source, List<AudioTrack> into)
    {
        var byName = new Dictionary<string, AudioTrack>();
        AudioTrackSpec? mixSpec = null;
        AudioTrack? feeder = null, game = null, voice = null;
        foreach (var spec in plan)
        {
            if (spec.MixOf != null) { mixSpec = spec; continue; }   // built below, once its sources exist
            var track = new AudioTrack(spec.Name, null, spec.Kind == AudioSourceKind.Feeder) { Enabled = spec.Written };
            byName[spec.Name] = track;
            into.Add(track);
            if (spec.Kind == AudioSourceKind.Feeder) feeder = track;
            else if (spec.Kind == AudioSourceKind.Game) game = track;
            else if (spec.Kind == AudioSourceKind.Voice) voice = track;
            track.Source = source(spec.Kind!.Value);
        }
        var all = new AudioTrack(mixSpec!.Name, mixSpec.MixOf!.Select(n => byName[n]).ToList()) { Enabled = mixSpec.Written };
        into.Insert(0, all);   // always track 1
        return new Built(all, feeder!, game, voice);
    }
}
