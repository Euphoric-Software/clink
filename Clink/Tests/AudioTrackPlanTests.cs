using Clink.Audio;
using Clink.Encode;
using Clink.Engine;

namespace Clink.Tests;

/// <summary>
/// The audio tracks the engine builds (Engine/AudioTrackPlan.cs): track 1 is All (a hidden feeder of everything except Clink, plus the
/// Mic), the other tracks are optional alternates. Pure, plus one synthetic-packet check of an All track (no WASAPI).
/// </summary>
internal static unsafe class AudioTrackPlanTests
{
    private static readonly bool[] Both = { false, true };

    private static IEnumerable<(bool game, bool mic, bool voice, bool desktop)> Combinations()
    {
        foreach (bool g in Both) foreach (bool m in Both) foreach (bool v in Both) foreach (bool d in Both) yield return (g, m, v, d);
    }

    [UnitTest]
    public static void Every_combination_writes_All_first_then_the_enabled_tracks_in_order()
    {
        foreach (var (g, m, v, d) in Combinations())
        {
            var written = AudioTrackPlan.Plan(g, m, v, d).Where(s => s.Written).Select(s => s.Name).ToList();
            var expected = new List<string> { "All" };
            if (g) expected.Add("Game");
            if (m) expected.Add("Mic");
            if (v) expected.Add("Voice");
            if (d) expected.Add("Desktop");
            string what = $"game={g} mic={m} voice={v} desktop={d}";
            Check.Equal(expected.ToArray(), written.ToArray(), what);
            Check.Equal(1 + new[] { g, m, v, d }.Count(x => x), written.Count, what + ": count is 1 + flags");
            Check.Equal("All", AudioTrackPlan.Plan(g, m, v, d)[0].Name, what + ": All is the first spec");
        }
    }

    [UnitTest]
    public static void All_is_the_feeder_and_the_Mic_and_nothing_else()
    {
        foreach (var (g, m, v, d) in Combinations())
        {
            var plan = AudioTrackPlan.Plan(g, m, v, d);
            var all = plan.Single(s => s.Name == "All");
            Check.Equal(new[] { AudioTrackPlan.FeederName, "Mic" }, all.MixOf!.ToArray(), $"game={g} mic={m} voice={v} desktop={d}");
            Check.False(all.MixOf!.Any(n => n is "Game" or "Voice" or "Desktop"), "Game, Voice and Desktop never feed All");
            var feeder = plan.Single(s => s.Kind == AudioSourceKind.Feeder);
            Check.False(feeder.Written, "the feeder is never written");
            Check.Equal(1, plan.Count(s => s.Name == feeder.Name), "one feeder");
            Check.True(plan.Any(s => s.Kind == AudioSourceKind.Mic), "the Mic is captured for All even when its track is off");
            Check.Equal(m, plan.Single(s => s.Kind == AudioSourceKind.Mic).Written, "the Mic is written only when its track is on");
        }
    }

    [UnitTest]
    public static void Game_Voice_and_Desktop_are_captured_only_with_their_track()
    {
        var off = AudioTrackPlan.Plan(false, true, false, false);
        Check.Equal(new[] { AudioSourceKind.Feeder, AudioSourceKind.Mic }, off.Where(s => s.Kind != null).Select(s => s.Kind!.Value).ToArray());
        var on = AudioTrackPlan.Plan(true, true, true, true);
        Check.Equal(new[] { AudioSourceKind.Feeder, AudioSourceKind.Game, AudioSourceKind.Mic, AudioSourceKind.Voice, AudioSourceKind.Desktop },
            on.Where(s => s.Kind != null).Select(s => s.Kind!.Value).ToArray());
    }

    [UnitTest]
    public static void The_game_loopback_is_the_game_else_everything_except_the_voice_app_else_except_Clink()
    {
        Check.Equal((1234u, true), AudioTrackPlan.GameLoopback(1234, 0, 99), "a game: its process tree");
        Check.Equal((1234u, true), AudioTrackPlan.GameLoopback(1234, 555, 99), "a game and a voice app: still the game");
        Check.Equal((555u, false), AudioTrackPlan.GameLoopback(0, 555, 99), "no game, a voice app: everything except it");
        Check.Equal((99u, false), AudioTrackPlan.GameLoopback(0, 0, 99), "nothing running: everything except Clink");
    }

    [UnitTest]
    public static void The_feeder_leaves_process_loopback_only_when_the_system_lacks_it()
    {
        const int Timeout = unchecked((int)0x80131505), NotImpl = unchecked((int)0x80004001), NotSupported = unchecked((int)0x80070032);
        const int ServiceNotRunning = unchecked((int)0x88890010), AccessDenied = unchecked((int)0x80070005);
        // (osSupported, running, failures, hr) -> fall back
        Check.True(AudioTrackPlan.FallBackToEndpoint(false, false, 0, 0), "unsupported Windows: the endpoint from the start, nothing to wait for");
        Check.True(AudioTrackPlan.FallBackToEndpoint(false, true, 0, 0), "unsupported Windows, whatever else");
        Check.False(AudioTrackPlan.FallBackToEndpoint(true, false, 0, 0), "supported, nothing failed yet");
        foreach (int transient in new[] { Timeout, ServiceNotRunning, AccessDenied, 0 })
            foreach (int failures in new[] { 1, 2, 3, 50 })
                Check.False(AudioTrackPlan.FallBackToEndpoint(true, false, failures, transient), $"supported, transient 0x{(uint)transient:X8} x{failures}: keeps retrying process loopback");
        Check.False(AudioTrackPlan.FallBackToEndpoint(true, false, 1, NotImpl), "supported: one 'not implemented' is not yet enough");
        Check.True(AudioTrackPlan.FallBackToEndpoint(true, false, 2, NotImpl), "supported, but activation keeps saying not implemented");
        Check.True(AudioTrackPlan.FallBackToEndpoint(true, false, 7, NotSupported), "or not supported");
        Check.False(AudioTrackPlan.FallBackToEndpoint(true, true, 5, NotImpl), "running: nothing to fall back from");
        Check.True(AudioTrackPlan.IsNotSupportedError(NotImpl) && !AudioTrackPlan.IsNotSupportedError(Timeout));
    }

    [UnitTest]
    public static void Process_loopback_support_follows_the_Windows_build()
    {
        Check.Equal(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348), AudioTrackPlan.ProcessLoopbackSupported());
    }

    [UnitTest]
    public static void The_All_status_follows_the_feeder_and_not_the_Mic()
    {
        Check.Equal((true, "everything you hear + mic"), AudioTrackPlan.AllStatus(true, null, false));
        Check.Equal((true, "system loopback (includes Clink sounds)"), AudioTrackPlan.AllStatus(true, null, true));
        Check.Equal((false, "starting"), AudioTrackPlan.AllStatus(false, null, false), "activating");
        Check.Equal((false, "access denied"), AudioTrackPlan.AllStatus(false, "access denied", false), "a dead feeder shows why");
        Check.Equal((true, "everything you hear + mic"), AudioTrackPlan.AllStatus(true, "old error", false), "running ignores a stale error");
    }

    [UnitTest]
    public static void A_feeder_can_never_be_enabled_for_writing()
    {
        MixRenderTests.OnMta(() =>
        {
            using var feeder = new AudioTrack("Everything", null, feeder: true);
            Check.False(feeder.Enabled, "off by default");
            feeder.Enabled = true;
            Check.False(feeder.Enabled, "and the setter does not turn it on");
            using var plain = new AudioTrack("Mic");
            Check.True(plain.Enabled);
            plain.Enabled = false;
            Check.False(plain.Enabled);
            using var mixOnly = new AudioTrack("All", new[] { feeder, plain }, feeder: true);
            Check.False(mixOnly.IsFeeder, "a mix is never a feeder");
        });
    }

    [UnitTest]
    public static void The_engine_wiring_builds_the_same_tracks_for_every_combination()
    {
        MixRenderTests.OnMta(() =>
        {
            foreach (var (g, m, v, d) in Combinations())
            {
                string what = $"game={g} mic={m} voice={v} desktop={d}";
                var tracks = new List<AudioTrack>();
                var asked = new List<AudioSourceKind>();
                try
                {
                    var built = AudioTrackPlan.Build(AudioTrackPlan.Plan(g, m, v, d), kind => { asked.Add(kind); return null; }, tracks);
                    var expected = new List<string> { "All", AudioTrackPlan.FeederName };
                    if (g) expected.Add("Game");
                    expected.Add("Mic");
                    if (v) expected.Add("Voice");
                    if (d) expected.Add("Desktop");
                    Check.Equal(expected.ToArray(), tracks.Select(t => t.Name).ToArray(), what + ": names and order");
                    Check.Equal(1, tracks.Count(t => t.IsFeeder), what + ": one feeder");
                    Check.True(tracks[1].IsFeeder && ReferenceEquals(tracks[1], built.Feeder), what + ": it is the second track");
                    Check.True(tracks.Where(t => t.IsFeeder).All(t => !t.Enabled), what + ": the feeder is never enabled");
                    Check.True(tracks[0].IsMix && ReferenceEquals(tracks[0], built.All) && tracks[0].Enabled, what + ": All is first, a mix, written");
                    Check.Equal(new[] { AudioTrackPlan.FeederName, "Mic" }, tracks[0].MixOf!.Select(t => t.Name).ToArray(), what + ": All mixes the feeder and the Mic only");
                    var written = tracks.Where(t => t.Enabled).Select(t => t.Name).ToList();
                    Check.Equal(1 + new[] { g, m, v, d }.Count(x => x), written.Count, what + ": written is 1 + flags");
                    Check.Equal(m, tracks.Single(t => t.Name == "Mic").Enabled, what + ": Mic written only with its flag");
                    Check.Equal(g, built.Game != null, what + ": Game track only with its flag");
                    Check.Equal(v, built.Voice != null, what + ": Voice track only with its flag");
                    Check.Equal(g, asked.Contains(AudioSourceKind.Game), what + ": no Game source is made without its track");
                    Check.Equal(v, asked.Contains(AudioSourceKind.Voice), what + ": no Voice source is made without its track");
                    Check.Equal(d, asked.Contains(AudioSourceKind.Desktop), what + ": no Desktop source is made without its track");
                    Check.True(asked.Contains(AudioSourceKind.Feeder) && asked.Contains(AudioSourceKind.Mic), what + ": feeder and Mic always get a source");
                }
                finally { foreach (var t in tracks) t.Dispose(); }
            }
        });
    }

    [UnitTest]
    public static void A_failing_source_leaves_the_tracks_built_so_far_for_the_engine_to_dispose()
    {
        MixRenderTests.OnMta(() =>
        {
            var tracks = new List<AudioTrack>();
            try
            {
                Check.Throws<InvalidOperationException>(() => AudioTrackPlan.Build(AudioTrackPlan.Plan(true, true, false, false),
                    k => k == AudioSourceKind.Mic ? throw new InvalidOperationException("no mic") : null, tracks));
                Check.Equal(new[] { AudioTrackPlan.FeederName, "Game", "Mic" }, tracks.Select(t => t.Name).ToArray());
            }
            finally { foreach (var t in tracks) t.Dispose(); }
        });
    }
    [UnitTest]
    public static void A_feeder_track_keeps_its_PCM_but_encodes_nothing()
    {
        using var feeder = new AudioTrack("Everything", null, feeder: true);
        Check.True(feeder.IsFeeder);
        var packet = new float[960 * 2];
        for (int i = 0; i < 960; i++) packet[i * 2] = packet[i * 2 + 1] = .25f;
        long t = 1_000_000_000;
        for (int p = 0; p < 100; p++, t += 200_000) fixed (float* f = packet) feeder.OnPacket(f, 960, t, false);
        Check.Equal(0, feeder.FrameCount, "no AAC frame is made");
        var pcm = new float[1024 * 2];
        feeder.ReadPcm(1_000_000_000 + 5_000_000, 1024, pcm);
        Check.Near(.25, pcm[100], .0001, "the PCM history is there for the mix");
    }

    [UnitTest]
    public static void An_All_track_mixing_a_feeder_and_a_mic_carries_both_tones()
    {
        MixRenderTests.OnMta(() =>
        {
            const int Rate = AudioTrack.Rate, Frames = 960;
            const double FeederHz = 440, MicHz = 880;
            const float Level = .2f;
            const long Origin = 1_000_000_000;
            var feeder = new AudioTrack("Everything", null, feeder: true);
            var mic = new AudioTrack("Mic");
            var all = new AudioTrack("All", new[] { feeder, mic });
            var driven = new[] { all, feeder, mic };
            try
            {
                var fp = new float[Frames * 2];
                var mp = new float[Frames * 2];
                for (int p = 0; p < 400; p++)   // 8 s
                {
                    long tick = Origin + p * 200_000L;
                    for (int i = 0; i < Frames; i++)
                    {
                        long n = ((long)p * Frames + i) % Rate;
                        fp[i * 2] = fp[i * 2 + 1] = Level * (float)Math.Sin(2 * Math.PI * FeederHz * n / Rate);
                        mp[i * 2] = mp[i * 2 + 1] = Level * (float)Math.Sin(2 * Math.PI * MicHz * n / Rate);
                    }
                    fixed (float* f = fp) feeder.OnPacket(f, Frames, tick, false);
                    fixed (float* f = mp) mic.OnPacket(f, Frames, tick, false);
                    if (p % 5 == 4)
                    {
                        long until = tick + 200_000 - 3_000_000;
                        foreach (var t in driven) t.PadSilenceTo(until);
                        foreach (var t in driven) t.MixUpTo(until);
                    }
                }
                Check.Equal(0, feeder.FrameCount, "the feeder encodes nothing");
                var frames = all.Snapshot(Origin, Origin + 80_000_000);
                Check.True(frames.Count > 100, "All has frames");
                using var dir = new TempDir();
                string clip = dir.File("all.mp4");
                var video = new List<VideoSample>();
                for (long k = 0; k <= 7; k++) video.Add(new VideoSample { Pts = Origin + k * 10_000_000, Keyframe = true, Data = TimelineProbe.Idr });
                var desc = new AudioTrackDesc { Name = "All", AudioSpecificConfig = all.AudioSpecificConfig, Bitrate = all.Bitrate, SampleRate = Rate, Channels = 2 };
                ClipWriter.Write(clip, video, "h264", 16, 16, null, new List<(AudioTrackDesc, List<AacFrame>)> { (desc, frames) }, Origin, 0, true);
                var pcm = TestAudio.Decode(clip, 0);
                Check.Near(Level, TestAudio.Amplitude(pcm, Rate, FeederHz), .07 * Level, "the feeder's tone is in All");
                Check.Near(Level, TestAudio.Amplitude(pcm, Rate, MicHz), .07 * Level, "the Mic's tone is in All");
                Check.Near(0, TestAudio.Amplitude(pcm, Rate, 1320), .02 * Level, "and nothing else");
            }
            finally { foreach (var t in driven) t.Dispose(); }
        });
    }
}
