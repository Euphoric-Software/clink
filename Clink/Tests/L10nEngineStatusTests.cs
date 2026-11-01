using System.IO;
using System.Text.RegularExpressions;
using Clink.Audio;
using Clink.Engine;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// Localization slice S3c (capture engine and audio status): every status / detail / problem the engine and the audio code can produce has
/// a kind next to its English string. The helper (EngineStatusText) gives the same English as before under "en" and a catalog text under
/// the pseudo-locale; user data (app and device names, system error text) passes through as it is.
/// </summary>
internal static class L10nEngineStatusTests
{
    private const string Qps = "qps-ploc";

    private static void Marked(string? text, string what)
    {
        Check.True(PseudoLocale.IsPseudo(text), what + " carries the pseudo marker: " + text);
        Check.True(L10nTreeHarness.IsAllowed(L10nTreeHarness.StripFrames(text!)), what + " has text outside the catalog frames: " + text);
    }

    private static void NoMissing(string what) => Check.Equal(Array.Empty<string>(), L.Missing.OrderBy(k => k, StringComparer.Ordinal).ToArray(), what + ": keys missing from the catalog");

    // The English the code produced before the kinds existed, written out here independently of the helper under test.
    private static readonly (EngineStatusKind Kind, string English)[] OldStatus =
    {
        (EngineStatusKind.Stopped, "Stopped"), (EngineStatusKind.Starting, "Starting…"), (EngineStatusKind.Recording, "Recording"), (EngineStatusKind.Error, "Error"),
    };

    private static readonly (TrackKind Kind, string English)[] OldTracks =
    {
        (TrackKind.Mix, "Mix"), (TrackKind.Game, "Game"), (TrackKind.Mic, "Mic"), (TrackKind.Voice, "Voice"), (TrackKind.Desktop, "Desktop"),
    };

    /// <summary>Every detail kind with a typical argument and the old English text (built as the old code built it).</summary>
    private static readonly (AudioDetailKind Kind, string? Arg, string English, bool UserData)[] OldDetails =
    {
        (AudioDetailKind.Name, "USB Microphone", "USB Microphone", true),
        (AudioDetailKind.Everything, null, "everything", false),
        (AudioDetailKind.EverythingExcept, "Discord", "everything except " + "Discord", false),
        (AudioDetailKind.MixParts, null, "game + mic + voice, always track 1", false),
        (AudioDetailKind.WaitingForApp, null, "waiting for app", false),
        (AudioDetailKind.NotConnectedOrDisabled, null, "not connected or disabled", false),
        (AudioDetailKind.InUseExclusive, null, "in use by another app (exclusive mode)", false),
        (AudioDetailKind.AccessDenied, null, "access denied", false),
        (AudioDetailKind.MicBlocked, null, "blocked: allow microphone access for desktop apps in Windows privacy settings", false),
        (AudioDetailKind.NotOutputDevice, null, "not an output device", false),
        (AudioDetailKind.NotInputDevice, null, "not an input device", false),
        (AudioDetailKind.AudioServiceDown, null, "Windows Audio service not running", false),
        (AudioDetailKind.Other, "0x88890004", "0x88890004", true),
    };

    /// <summary>The audio line entry as the old MainWindow built it.</summary>
    private static string OldEntry(string name, bool running, string? detail) =>
        $"{(running ? "●" : "○")} {name}{(detail != null ? $" ({detail})" : "")}"; // (the old line also ran .Replace(' ', ' '), which changes nothing)

    // ------------------------------------------------------------------ the enums are covered

    [UnitTest]
    public static void Every_enum_value_of_the_engine_and_audio_kinds_has_an_expectation_here()
    {
        Check.Equal(Enum.GetValues<EngineStatusKind>().Length, OldStatus.Length, "engine status kinds");
        Check.Equal(Enum.GetValues<TrackKind>().Length, OldTracks.Length, "track kinds");
        Check.Equal(Enum.GetValues<AudioDetailKind>().Length, OldDetails.Length, "detail kinds");
        Check.Equal(2, Enum.GetValues<AudioProblemKind>().Length - 1, "problem kinds (besides None)");
        Check.Equal(2, Enum.GetValues<EngineErrorKind>().Length, "engine error kinds");
        foreach (var k in Enum.GetValues<EngineStatusKind>()) Check.True(OldStatus.Any(o => o.Kind == k), "status " + k);
        foreach (var k in Enum.GetValues<TrackKind>()) Check.True(OldTracks.Any(o => o.Kind == k), "track " + k);
        foreach (var k in Enum.GetValues<AudioDetailKind>()) Check.True(OldDetails.Any(o => o.Kind == k), "detail " + k);
    }

    // ------------------------------------------------------------------ English identity

    [UnitTest]
    public static void English_is_the_old_text_for_every_status_error_track_problem_and_detail()
    {
        using var scope = L.Scope("en");
        foreach (var (kind, english) in OldStatus)
        {
            Check.Equal(english, EngineText.Status(kind), "engine English " + kind);
            Check.Equal(english, EngineStatusText.Status(kind), "helper " + kind);
        }
        var gpu = new EngineError(EngineErrorKind.GpuReset);
        Check.Equal("The GPU was reset (graphics driver crash or TDR)", gpu.English, "GPU reset (engine English)");
        Check.Equal("The GPU was reset (graphics driver crash or TDR)", EngineStatusText.Error(gpu), "GPU reset (helper)");
        var other = new EngineError(EngineErrorKind.Other, "Access is denied. (0x80070005)");
        Check.Equal("Access is denied. (0x80070005)", other.English, "other error (engine English)");
        Check.Equal("Access is denied. (0x80070005)", EngineStatusText.Error(other), "other error (helper)");

        foreach (var (kind, english) in OldTracks)
        {
            Check.Equal(english, AudioText.TrackName(kind), "track English " + kind);
            Check.Equal(english, EngineStatusText.Track(kind), "track helper " + kind);
            Check.Equal(kind, AudioText.KindOf(english), "round trip " + kind);
        }

        Check.Equal("not connected", AudioText.Problem(AudioProblemKind.NotConnected), "problem English NotConnected");
        Check.Equal("disabled", AudioText.Problem(AudioProblemKind.Disabled), "problem English Disabled");
        Check.Null(AudioText.Problem(AudioProblemKind.None), "no problem");
        Check.Equal("not connected", EngineStatusText.DeviceProblem(AudioProblemKind.NotConnected), "problem helper NotConnected");
        Check.Equal("disabled", EngineStatusText.DeviceProblem(AudioProblemKind.Disabled), "problem helper Disabled");
        Check.Null(EngineStatusText.DeviceProblem(AudioProblemKind.None), "no problem (helper)");
        Check.Equal("not connected", new AudioDeviceInfo("id", "Headset", AudioProblemKind.NotConnected).Problem, "AudioDeviceInfo.Problem");
        Check.Null(new AudioDeviceInfo("id", "Headset").Problem, "an active endpoint has none");

        foreach (var (kind, arg, english, _) in OldDetails)
        {
            var d = new AudioDetail(kind, arg);
            Check.Equal(english, d.English, "detail English " + kind);
            Check.Equal(english, EngineStatusText.Detail(kind, arg), "detail helper " + kind);
        }
    }

    [UnitTest]
    public static void English_audio_entries_are_the_old_line_for_every_track_detail_and_mix_only_combination()
    {
        using var scope = L.Scope("en");
        int n = 0;
        foreach (var (track, trackEnglish) in OldTracks)
            foreach (bool running in new[] { true, false })
                foreach (bool mixOnly in new[] { false, true })
                {
                    // no detail
                    var none = new AudioTrackStatus(track, running, null, mixOnly);
                    string? noneDetail = mixOnly ? "mix only" : null;
                    Check.Equal(noneDetail, none.Detail, "Detail (English) of " + trackEnglish);
                    Check.Equal(trackEnglish, none.Name, "Name (English) of " + trackEnglish);
                    Check.Equal(OldEntry(trackEnglish, running, noneDetail), EngineStatusText.AudioEntry(none), "entry " + trackEnglish + " none mixOnly=" + mixOnly);
                    n++;
                    foreach (var (kind, arg, english, _) in OldDetails)
                    {
                        var s = new AudioTrackStatus(track, running, new AudioDetail(kind, arg), mixOnly);
                        string expected = mixOnly ? english + ", mix only" : english;   // as Engine.GetStats built it
                        Check.Equal(expected, s.Detail, "Detail (English) " + trackEnglish + "/" + kind + " mixOnly=" + mixOnly);
                        Check.Equal(kind, s.DetailKind, "DetailKind");
                        Check.Equal(arg, s.DetailArg, "DetailArg");
                        Check.Equal(OldEntry(trackEnglish, running, expected), EngineStatusText.AudioEntry(s), "entry " + trackEnglish + "/" + kind + " running=" + running + " mixOnly=" + mixOnly);
                        n++;
                    }
                }
        Check.True(n > 100, "combinations checked: " + n);

        // the whole line: entries side by side, three spaces apart
        var line = EngineStatusText.AudioLine(new[]
        {
            new AudioTrackStatus(TrackKind.Mix, true, AudioDetail.MixParts),
            new AudioTrackStatus(TrackKind.Game, true, AudioDetail.Named("Overwatch")),
            new AudioTrackStatus(TrackKind.Voice, false, AudioDetail.WaitingForApp, MixOnly: true),
        });
        Check.Equal("● Mix (game + mic + voice, always track 1)   ● Game (Overwatch)   ○ Voice (waiting for app, mix only)", line);
    }

    [UnitTest]
    public static void English_footer_lines_are_unchanged_with_and_without_the_kinds()
    {
        using var scope = L.Scope("en");
        // the string-only seam (what existing callers use)
        Check.Equal("x", MainWindow.IdleFooterLine(RecordingState.Recording, null, "x"));
        Check.Equal("boom", MainWindow.IdleFooterLine(RecordingState.Error, "boom", "x"));
        Check.Equal("Error", MainWindow.IdleFooterLine(RecordingState.Error, null, "x"));
        // with the kinds, every state gives the same English the strings gave
        foreach (var (kind, english) in OldStatus)
            Check.Equal(english, MainWindow.IdleFooterLine(RecordingState.Recording, null, english, null, kind), "status " + kind);
        var gpu = new EngineError(EngineErrorKind.GpuReset);
        Check.Equal(gpu.English, MainWindow.IdleFooterLine(RecordingState.Error, gpu.English, "Error", gpu, EngineStatusKind.Error), "GPU error");
        var other = new EngineError(EngineErrorKind.Other, "boom");
        Check.Equal("boom", MainWindow.IdleFooterLine(RecordingState.Error, "boom", "Error", other, EngineStatusKind.Error), "other error");
    }

    // ------------------------------------------------------------------ the pseudo-locale marks every catalog text

    [UnitTest]
    public static void Under_the_pseudo_locale_every_status_text_is_from_the_catalog_and_user_data_passes_through()
    {
        using var scope = L.Scope(Qps);
        L.ClearMissing();
        foreach (var (kind, _) in OldStatus) Marked(EngineStatusText.Status(kind), "status " + kind);
        Marked(EngineStatusText.Error(new EngineError(EngineErrorKind.GpuReset)), "GPU reset");
        // an exception's own message is not ours: as it is
        Check.Equal("Access is denied.", EngineStatusText.Error(new EngineError(EngineErrorKind.Other, "Access is denied.")), "other engine error");
        foreach (var (kind, _) in OldTracks) Marked(EngineStatusText.Track(kind), "track " + kind);
        Marked(EngineStatusText.DeviceProblem(AudioProblemKind.NotConnected), "problem not connected");
        Marked(EngineStatusText.DeviceProblem(AudioProblemKind.Disabled), "problem disabled");
        foreach (var (kind, arg, _, userData) in OldDetails)
        {
            string text = EngineStatusText.Detail(kind, arg);
            if (userData) Check.Equal(arg!, text, "user data " + kind);   // a device name / a system text: shown as it is
            else
            {
                Marked(text, "detail " + kind);
                if (arg != null) Check.True(text.Contains(arg), "the app name is in the text: " + text);
            }
        }
        // the composed pieces
        Marked(EngineStatusText.Detail(new AudioTrackStatus(TrackKind.Mic, true, null, MixOnly: true))!, "mix only");
        Marked(EngineStatusText.Detail(new AudioTrackStatus(TrackKind.Mic, true, AudioDetail.Named("USB Microphone"), MixOnly: true))!, "name, mix only");
        foreach (var (track, _) in OldTracks)
            foreach (bool running in new[] { true, false })
            {
                Marked(EngineStatusText.AudioEntry(new AudioTrackStatus(track, running)), "entry (no detail) " + track);
                Marked(EngineStatusText.AudioEntry(new AudioTrackStatus(track, running, AudioDetail.Named("USB Microphone"))), "entry (device) " + track);
                Marked(EngineStatusText.AudioEntry(new AudioTrackStatus(track, running, AudioDetail.EverythingExcept("Discord"), true)), "entry (except, mix only) " + track);
            }
        NoMissing("engine status texts");
    }

    [UnitTest]
    public static void Under_the_pseudo_locale_the_footer_follows_the_kinds_not_the_english_strings()
    {
        using var scope = L.Scope(Qps);
        L.ClearMissing();
        foreach (var (kind, english) in OldStatus)
            Marked(MainWindow.IdleFooterLine(RecordingState.Recording, null, english, null, kind), "footer status " + kind);
        Marked(MainWindow.IdleFooterLine(RecordingState.Error, "x", "Error", new EngineError(EngineErrorKind.GpuReset), EngineStatusKind.Error), "footer GPU error");
        Check.Equal("boom", MainWindow.IdleFooterLine(RecordingState.Error, "boom", "Error", new EngineError(EngineErrorKind.Other, "boom"), EngineStatusKind.Error), "footer other error");
        NoMissing("footer");
    }

    // ------------------------------------------------------------------ the kinds cannot drift from the strings

    [UnitTest]
    public static void A_fresh_engine_reports_its_status_by_kind_and_text_alike()
    {
        using var engine = new Clink.Engine.Engine();
        Check.Equal(EngineStatusKind.Stopped, engine.StatusKind);
        Check.Equal("Stopped", engine.Status);
        Check.Null(engine.Error, "no error");
        Check.Null(engine.ErrorInfo, "no error info");
    }

    [UnitTest]
    public static void The_engine_and_audio_sources_set_a_text_only_through_a_kind()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;
        string engine = File.ReadAllText(Path.Combine(root, "Engine", "Engine.cs"));
        string wasapi = File.ReadAllText(Path.Combine(root, "Audio", "WasapiSource.cs"));

        // Status / Error / Detail / LastError / Problem are get-only: the engine and the sources set kinds. What can still leak is new raw English
        // through the passthrough kinds (they are shown as they are, so they are only for exception messages, system text and user data).
        Check.False(Regex.IsMatch(engine, @"SetStatus\(\s*"""), "SetStatus takes a kind, not a string");
        Check.False(Regex.IsMatch(engine, @"new AudioTrackStatus\([^)]*"""), "AudioTrackStatus is made from kinds");
        var raw = new Regex(@"(AudioDetail\.(Other|Named)\(|AudioDetailKind\.Other\s*,|EngineErrorKind\.Other\s*,)\s*\$?""(?!0x\{)");
        foreach (var (file, text) in new[] { ("Engine.cs", engine), ("WasapiSource.cs", wasapi) })
            foreach (Match m in raw.Matches(text))
                Check.True(false, file + " passes an English literal through a passthrough kind: " + m.Value + " (add a kind and a catalog key; only the HRESULT form $\"0x{ is allowed)");

        // every track the engine creates takes its name from AudioText.TrackName, so GetStats can always map it back to a kind
        int tracks = Regex.Matches(engine, @"new AudioTrack\(").Count;
        Check.Equal(Enum.GetValues<TrackKind>().Length, tracks, "the engine creates one track per kind");
        Check.Equal(tracks, Regex.Matches(engine, @"new AudioTrack\(AudioText\.TrackName\(TrackKind\.").Count, "every new AudioTrack( takes AudioText.TrackName(TrackKind.X)");
        foreach (var k in Enum.GetValues<TrackKind>())
        {
            Check.True(AudioText.TryKindOf(AudioText.TrackName(k), out var back) && back == k, "track name round trip " + k);
            Check.True(Regex.IsMatch(engine, @"new AudioTrack\(AudioText\.TrackName\(TrackKind\." + k + @"\)"), "the engine creates the " + k + " track");
        }
        Check.False(AudioText.TryKindOf("Everything", out _), "an unknown name is not a kind");
        Check.Throws<ArgumentException>(() => AudioText.KindOf("Everything"), "KindOf of an unknown name is a bug, not a silent default");
    }
    [UnitTest]
    public static void A_track_status_is_made_from_kinds_so_its_English_follows_them()
    {
        var s = new AudioTrackStatus(TrackKind.Game, true, AudioDetail.Named("Overwatch"));
        Check.Equal("Game", s.Name);
        Check.Equal("Overwatch", s.Detail);
        var s2 = s with { Kind = TrackKind.Mic, MixOnly = true };
        Check.Equal("Mic", s2.Name);
        Check.Equal("Overwatch, mix only", s2.Detail);
        Check.Null(new AudioTrackStatus(TrackKind.Desktop, false, AudioDetail.Named(null)).Detail, "a missing name is no detail");
        Check.Equal("", new AudioTrackStatus(TrackKind.Desktop, false, AudioDetail.Named("")).Detail, "an empty name is an empty detail, as before");
        Check.Equal("everything except ", AudioDetail.EverythingExcept(null).English, "no voice-app name, as before");
    }

    // ------------------------------------------------------------------ the sources and the catalog agree

    private static readonly string[] Sources = { @"Services\Localization\StatusText.cs" };

    [UnitTest]
    public static void Every_key_the_status_helper_asks_for_is_in_the_catalog_and_every_engine_json_key_is_used()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;
        var engineKeys = L10nSourceScan.AreaKeys(root, "engine");
        var main = L10nSourceScan.AreaKeys(root, "main");
        var core = L10nSourceScan.AreaKeys(root, "core");
        var hits = L10nSourceScan.CSharp(root, Sources, new[] { L10nSourceScan.T });
        Check.True(hits.Count >= 25, "found the keys of StatusText.cs: " + hits.Count);
        var problems = hits.Where(h => !engineKeys.Contains(h.Key) && !main.Contains(h.Key) && !core.Contains(h.Key)).Select(h => h.File + ": " + h.Key).ToList();
        Check.Equal(0, problems.Count, "keys missing from engine.json / main.json / core.json: " + string.Join(" | ", problems));
        var used = hits.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        Check.Equal(Array.Empty<string>(), engineKeys.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray(), "engine.json keys nothing asks for");
        // the track names of the checkboxes are reused, not copied (a key in two files must not differ, and none is added twice)
        foreach (string k in new[] { "track|Mix", "track|Game", "track|Mic", "track|Desktop", "track|Voice" })   // (track|Voice: the status line's own word for the voice-chat track; the checkbox says "Voice chat". One copy, in main.json)
        {
            Check.True(main.Contains(k) && !engineKeys.Contains(k), k + " lives in main.json only");
            Check.True(used.Contains(k), k + " is used");
        }

        // each key's English is the text the code shows: the part after the context (or the key itself)
        var english = L.Catalogs.English.Entries;
        foreach (string k in engineKeys)
            Check.Equal(L.EnglishOf(k), english[k].Primary, "the English text of " + k);
    }

    [UnitTest]
    public static void The_main_window_shows_the_engine_and_audio_status_only_through_the_helper()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;
        string code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        Check.True(code.Contains("EngineStatusText.AudioLine(st.Audio)"), "the audio line");
        Check.True(Regex.Matches(code, @"EngineStatusText\.DeviceProblem\(known\.ProblemKind\)").Count == 2, "both device-problem texts (the mic / output combo and the Desktop combo)");
        Check.True(code.Contains("IdleFooterLine(state, eng.Error, eng.Status, eng.ErrorInfo, eng.StatusKind)"), "the footer gets the kinds");
        Check.True(code.Contains("EngineStatusText.Error(errorInfo)") && code.Contains("EngineStatusText.Status(kind)"), "the footer translates through the helper");
        Check.False(Regex.IsMatch(code, @"\b(d|known)\.Problem\b"), "no logic on, and no display of, the English device problem: ProblemKind is used");
        Check.True(code.Contains("d.ProblemKind == AudioProblemKind.None") && code.Contains("known.ProblemKind != AudioProblemKind.None"), "the active-device tests use the kind");
        // eng.Status / eng.Error (English) appear only as the arguments of the one footer call, which picks the translated text by kind
        string stripped = code.Replace("IdleFooterLine(state, eng.Error, eng.Status, eng.ErrorInfo, eng.StatusKind)", "");
        Check.False(Regex.IsMatch(stripped, @"\beng\.(Status|Error)\b(?!Kind|Info)"), "the engine's English Status / Error are not shown: " + Regex.Match(stripped, @"[^\n]*\beng\.(Status|Error)\b(?!Kind|Info)[^\n]*").Value.Trim());
        Check.False(Regex.IsMatch(code, @"\ba\.(Name|Detail)\b"), "no audio track is shown as the English strings");
    }
}
