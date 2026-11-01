using Clink.Audio;
using Clink.Engine;

namespace Clink.Services.Localization;

/// <summary>
/// The translated texts of the capture engine's and the audio's status (the main window's footer, the audio line, the device pickers).
/// The engine and the audio code keep their English strings (<c>Engine.Status</c>, <see cref="AudioTrackStatus.Detail"/>,
/// <see cref="AudioDeviceInfo.Problem"/>) for logs, --selftest and the controller; this turns the kinds they carry next to those strings
/// into the active language. User data (an app or device name) goes in as a placeholder value; the text of an exception or of Windows
/// (<see cref="AudioDetailKind.Other"/>, <see cref="EngineErrorKind.Other"/>) is shown as it is: it is not ours to translate, and a
/// template around it would add words to today's English.
/// Catalog keys: Locales/en/engine.json, and the track names of main.json (track|Game ...).
/// </summary>
internal static class EngineStatusText
{
    /// <summary>The name of a track ("Game", "Mic", "Mix", "Desktop", "Voice"). "Voice" is the same track as the checkbox "Voice chat", but the status line says "Voice" in English today, so it has its own key (track|Voice).</summary>
    public static string Track(TrackKind kind) => kind switch
    {
        TrackKind.Mix => L.T("track|Mix"),
        TrackKind.Game => L.T("track|Game"),
        TrackKind.Mic => L.T("track|Mic"),
        TrackKind.Voice => L.T("track|Voice"),
        TrackKind.Desktop => L.T("track|Desktop"),
        _ => AudioText.TrackName(kind),
    };

    /// <summary>The engine's status ("Starting…", "Stopped", "Recording", "Error").</summary>
    public static string Status(EngineStatusKind kind) => kind switch
    {
        EngineStatusKind.Starting => L.T("engine-status|Starting…"),
        EngineStatusKind.Recording => L.T("engine-status|Recording"),
        EngineStatusKind.Error => L.T("engine-status|Error"),
        _ => L.T("engine-status|Stopped"),
    };

    /// <summary>Why the engine failed: our own text for a lost GPU, the exception's own message otherwise.</summary>
    public static string Error(EngineError error) => error.Kind switch
    {
        EngineErrorKind.GpuReset => L.T("engine-error|The GPU was reset (graphics driver crash or TDR)"),
        _ => error.Arg ?? "",
    };

    /// <summary>Why an audio device cannot be used right now ("not connected", "disabled"); null for an active one.</summary>
    public static string? DeviceProblem(AudioProblemKind kind) => kind switch
    {
        AudioProblemKind.NotConnected => L.T("audio-problem|not connected"),
        AudioProblemKind.Disabled => L.T("audio-problem|disabled"),
        _ => null,
    };

    /// <summary>One detail of an audio track (what it captures, or why it does not).</summary>
    public static string Detail(AudioDetailKind kind, string? arg) => kind switch
    {
        AudioDetailKind.Everything => L.T("audio-detail|everything"),
        AudioDetailKind.EverythingExcept => L.T("audio-detail|everything except {app}", ("app", arg)),
        AudioDetailKind.MixParts => L.T("audio-detail|game + mic + voice, always track 1"),
        AudioDetailKind.WaitingForApp => L.T("audio-detail|waiting for app"),
        AudioDetailKind.NotConnectedOrDisabled => L.T("audio-detail|not connected or disabled"),
        AudioDetailKind.InUseExclusive => L.T("audio-detail|in use by another app (exclusive mode)"),
        AudioDetailKind.AccessDenied => L.T("audio-detail|access denied"),
        AudioDetailKind.MicBlocked => L.T("audio-detail|blocked: allow microphone access for desktop apps in Windows privacy settings"),
        AudioDetailKind.NotOutputDevice => L.T("audio-detail|not an output device"),
        AudioDetailKind.NotInputDevice => L.T("audio-detail|not an input device"),
        AudioDetailKind.AudioServiceDown => L.T("audio-detail|Windows Audio service not running"),
        _ => arg ?? "",   // Name (an app or device name) and Other (a system text): as they are
    };

    /// <summary>The detail of a track: its source's detail, with "mix only" for a track that is only part of the mix; null for none.</summary>
    public static string? Detail(AudioTrackStatus track)
    {
        string? detail = track.Info == null ? null : Detail(track.Info.Kind, track.Info.Arg);
        if (!track.MixOnly) return detail;
        return detail == null ? L.T("audio-detail|mix only") : L.T("audio-detail|{detail}, mix only", ("detail", detail));
    }

    /// <summary>One entry of the audio line: "● Game (Overwatch)", "○ Voice (waiting for app)".</summary>
    public static string AudioEntry(AudioTrackStatus track)
    {
        string name = Track(track.Kind);
        string? detail = Detail(track);
        return (track.Running ? "●" : "○") + " " + (detail == null ? name : L.T("audio-line|{name} ({detail})", ("name", name), ("detail", detail)));
    }

    /// <summary>The audio line of the main window: the entries side by side, or "No audio tracks" (the caller's text) when there are none.</summary>
    public static string AudioLine(IEnumerable<AudioTrackStatus> tracks) => string.Join("   ", tracks.Select(AudioEntry));
}
