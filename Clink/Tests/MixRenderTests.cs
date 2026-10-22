using System.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Clink.Audio;
using Clink.Encode;
using Clink.Engine;
using Clink.Native;

namespace Clink.Tests;

/// <summary>
/// The trim export of "All audio" end to end: a synthetic five-track clip made by the recording code (every source is a pure tone and
/// some noise of its own, the Mix is their sum), trimmed through <see cref="Trimmer"/> with the plan <see cref="MixPlan"/> makes,
/// then decoded and measured tone by tone. Needs the Windows AAC encoder and decoder (Media Foundation), like the editor does.
/// </summary>
internal static class MixRenderTests
{
    private static readonly string[] Names = { "Mix", "Game", "Mic", "Voice", "Desktop" };
    private static readonly float[] Unity = { 1, 1, 1, 1, 1 };

    private static bool? s_hasAac;

    /// <summary>
    /// Media Foundation wants an MTA thread, like the editor's Task.Run; the test runner itself is on the UI thread. A machine
    /// without the Windows AAC encoder (a stripped-down server image) cannot make the clips: the test says so and passes.
    /// </summary>
    private static void OnMta(Action body)
    {
        Exception? failure = null;
        bool skipped = false;
        var t = new Thread(() =>
        {
            try
            {
                s_hasAac ??= HasAacEncoder();
                if (s_hasAac == false) skipped = true; else body();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (failure != null) throw new TestFailure(failure is TestFailure ? failure.Message : failure.ToString());
        if (skipped) Console.WriteLine("  (skipped: this machine has no Media Foundation AAC encoder)");
    }

    private static bool HasAacEncoder()
    {
        try
        {
            Mf.EnsureStarted();
            using var enc = new AacEncoder();
            return true;
        }
        catch (Exception) { return false; }
    }

    private static TrackMix[] PlanFor(string[] names, float[] gain, bool[]? include = null)
    {
        var roles = MixPlan.Roles(names, names.Length);
        return MixPlan.Export(roles, include ?? Enumerable.Repeat(true, names.Length).ToArray(), gain);
    }

    /// <summary>The tone of <paramref name="hz"/> in the track is <paramref name="share"/> times the level it was recorded at (0 = absent), give or take <paramref name="tolerance"/> of that level.</summary>
    private static void ExpectTone(float[] track, double hz, double share, double tolerance, string what)
        => Check.Near(share * SyntheticClip.Level, TestAudio.Amplitude(track, 48000, hz), tolerance * SyntheticClip.Level, $"{what}: level of the {hz:0} Hz tone");

    private const double Present = .07, Absent = .02;   // what counts as "at its level" and as "not there", as a share of the recorded level

    [UnitTest]
    public static void The_synthetic_clip_holds_one_tone_per_source_and_their_sum_in_the_Mix()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4");
        OnMta(() =>
        {
            var written = SyntheticClip.Write(clip, 6);
            Check.Equal(Names, written);
            Check.Equal(Names, Trimmer.ReadAudioTrackNames(clip)!);
            var mix = TestAudio.Decode(clip, 0);
            ExpectTone(mix, SyntheticClip.GameHz, 1, Present, "Mix");
            ExpectTone(mix, SyntheticClip.MicHz, 1, Present, "Mix");
            ExpectTone(mix, SyntheticClip.VoiceHz, 1, Present, "Mix");
            ExpectTone(mix, SyntheticClip.DesktopHz, 0, Absent, "Mix has no Desktop in it");
            var game = TestAudio.Decode(clip, 1);
            ExpectTone(game, SyntheticClip.GameHz, 1, Present, "Game");
            ExpectTone(game, SyntheticClip.MicHz, 0, Absent, "Game");
            var desktop = TestAudio.Decode(clip, 4);
            ExpectTone(desktop, SyntheticClip.DesktopHz, 1, Present, "Desktop");
            ExpectTone(desktop, SyntheticClip.GameHz, 0, Absent, "Desktop");
        });
    }

    [UnitTest]
    public static void Game_at_zero_leaves_no_game_in_All_audio_and_the_rest_untouched()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            var plan = PlanFor(Names, new float[] { 1, 0, 1, 1, 1 });
            Check.NotNull(plan[0].Sum, "All audio is rebuilt");
            var kept = Trimmer.Trim(clip, output, 1, 5, Names, plan);
            Check.Equal(Names, kept, "all five tracks stay, in the same order");

            var mix = TestAudio.Decode(output, 0);
            ExpectTone(mix, SyntheticClip.GameHz, 0, Absent, "All audio without the game");
            ExpectTone(mix, SyntheticClip.MicHz, 1, Present, "All audio");
            ExpectTone(mix, SyntheticClip.VoiceHz, 1, Present, "All audio");
            // Game's own track is still saved at its own volume, which is also 0.
            ExpectTone(TestAudio.Decode(output, 1), SyntheticClip.GameHz, 0, Absent, "Game's own track");
            // Everything else is copied: a track at 100% is the very same AAC frames.
            ExpectTone(TestAudio.Decode(output, 2), SyntheticClip.MicHz, 1, Present, "Mic's own track");
            ExpectTone(TestAudio.Decode(output, 4), SyntheticClip.DesktopHz, 1, Present, "Desktop");
            Check.True(TestAudio.IsSlice(TestAudio.Frames(clip, 2), TestAudio.Frames(output, 2)), "Mic's frames are copied unchanged");
            Check.Near(4.0, TestAudio.Seconds(mix), .15, "length of All audio");
        });
    }

    [UnitTest]
    public static void Turning_Game_down_scales_it_in_All_audio_and_in_its_own_track()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            Trimmer.Trim(clip, output, 1, 5, Names, PlanFor(Names, new float[] { 1, .5f, 1, 1, 1 }));
            var mix = TestAudio.Decode(output, 0);
            ExpectTone(mix, SyntheticClip.GameHz, .5, Present, "All audio");
            ExpectTone(mix, SyntheticClip.MicHz, 1, Present, "All audio");
            ExpectTone(mix, SyntheticClip.VoiceHz, 1, Present, "All audio");
            ExpectTone(TestAudio.Decode(output, 1), SyntheticClip.GameHz, .5, Present, "Game's own track");
        });
    }

    [UnitTest]
    public static void The_Mix_volume_is_the_master_of_the_rebuilt_All_audio()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4"), second = dir.File("out2.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            // Only the Mix's own volume moved: the same single-track volume change as ever, no rebuild.
            Trimmer.Trim(clip, output, 1, 5, Names, PlanFor(Names, new float[] { .5f, 1, 1, 1, 1 }));
            var mix = TestAudio.Decode(output, 0);
            foreach (double hz in new[] { SyntheticClip.GameHz, SyntheticClip.MicHz, SyntheticClip.VoiceHz }) ExpectTone(mix, hz, .5, Present, "All audio at 50%");

            Trimmer.Trim(clip, second, 1, 5, Names, PlanFor(Names, new float[] { .5f, 0, 1, 2, 1 }));
            var both = TestAudio.Decode(second, 0);
            ExpectTone(both, SyntheticClip.GameHz, 0, Absent, "master 50%, Game 0");
            ExpectTone(both, SyntheticClip.MicHz, .5, Present, "master 50%, Mic 100%");
            ExpectTone(both, SyntheticClip.VoiceHz, 1, Present, "master 50%, Voice 200%");
        });
    }

    [UnitTest]
    public static void Without_a_Voice_track_the_game_is_taken_out_of_the_baked_mix()
    {
        using var dir = new TempDir();
        string clip = dir.File("four.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            // TrackVoice is off by default: the Mix still holds the voice, but there is no Voice track to rebuild it from.
            var written = SyntheticClip.Write(clip, 6, voiceTrack: false);
            string[] names = { "Mix", "Game", "Mic", "Desktop" };
            Check.Equal(names, written);
            var plan = PlanFor(names, new float[] { 1, 0, 1, 1 });
            Check.Equal(new[] { new MixInput(0, 1f), new MixInput(1, -1f) }, plan[0].Sum!);
            Trimmer.Trim(clip, output, 1, 5, names, plan);
            var mix = TestAudio.Decode(output, 0);
            // The mix and the game were coded apart, so a trace of the game remains; it must be far below what the game was (a
            // single sample of misalignment would leave 6% of a 440 Hz tone).
            ExpectTone(mix, SyntheticClip.GameHz, 0, .03, "baked mix minus the game");
            ExpectTone(mix, SyntheticClip.MicHz, 1, Present, "baked mix minus the game");
            ExpectTone(mix, SyntheticClip.VoiceHz, 1, Present, "the voice, which has no track of its own, stays");
            // The game's noise is gone too, which it only is when the two tracks were lined up to the sample.
            // The output starts at the clip's second 1: its second 2 is second 3 of the recorded game track.
            var game = TestAudio.Decode(clip, 1);
            double residue = TestAudio.Correlation(mix, game, 48000, 2 * 48000, 3 * 48000, 3000);
            double trace = TestAudio.Amplitude(mix, 48000, SyntheticClip.GameHz) / SyntheticClip.Level;
            Console.WriteLine($"  (what is left of the game in the baked mix minus the game: its tone at {trace:P1} of what it was ({20 * Math.Log10(Math.Max(trace, 1e-6)):0.0} dB), noise correlation {residue:0.000})");
            Check.True(residue < .15, $"the game's noise is cancelled out (correlation {residue:0.000})");
        });
    }

    [UnitTest]
    public static void With_every_source_at_100_percent_the_Mix_is_copied_frame_for_frame()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            var plan = PlanFor(Names, Unity);
            foreach (var track in plan) Check.Null(track.Sum);
            Trimmer.Trim(clip, output, 1, 5, Names, plan);
            for (int i = 0; i < 5; i++)
                Check.True(TestAudio.IsSlice(TestAudio.Frames(clip, i), TestAudio.Frames(output, i)), $"track {i + 1} is copied, not encoded again");
        });
    }

    [UnitTest]
    public static void A_Mix_rebuilt_from_untouched_sources_sounds_like_the_baked_one_and_stays_in_time()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            // Forced render at 100%: not what the plan does for an untouched mix, but it must give back the same sound, in time.
            var plan = PlanFor(Names, Unity);
            plan[0] = new TrackMix(true, 1f, new[] { new MixInput(1, 1f), new MixInput(2, 1f), new MixInput(3, 1f) });
            Trimmer.Trim(clip, output, 1, 5, Names, plan);
            var baked = TestAudio.Decode(clip, 0);
            var rebuilt = TestAudio.Decode(output, 0);
            // The baked Mix starts 384 samples before the clip (its edit list skips them); the output starts at the keyframe at 1.0 s.
            int bakedAt = 2 * 48000 + 384, rebuiltAt = 48000;
            int lag = TestAudio.BestLag(baked, bakedAt, rebuilt, rebuiltAt, 48000, 3000);
            double match = TestAudio.Correlation(baked, rebuilt, 48000, bakedAt, rebuiltAt, 0);
            Console.WriteLine($"  (rebuilt All audio vs the baked one: lag {lag} samples, correlation {match:0.000})");
            Check.True(Math.Abs(lag) <= 2, $"the rebuilt mix is in time with the baked one (lag {lag})");
            Check.True(match > .95, $"and sounds like it (correlation {match:0.000})");
            foreach (double hz in new[] { SyntheticClip.GameHz, SyntheticClip.MicHz, SyntheticClip.VoiceHz }) ExpectTone(rebuilt, hz, 1, Present, "rebuilt All audio");
        });
    }

    [UnitTest]
    public static void A_source_that_is_not_saved_on_its_own_still_comes_out_of_All_audio()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            var include = new[] { true, false, true, true, false };   // no Game track, no Desktop track
            var plan = PlanFor(Names, new float[] { 1, 0, 1, 1, 1 }, include);
            var kept = Trimmer.Trim(clip, output, 1, 5, Names, plan);
            Check.Equal(new[] { "Mix", "Mic", "Voice" }, kept);
            var mix = TestAudio.Decode(output, 0);
            ExpectTone(mix, SyntheticClip.GameHz, 0, Absent, "All audio");
            ExpectTone(mix, SyntheticClip.MicHz, 1, Present, "All audio");
            Check.Equal(3, TestAudio.TrackCount(output));
        });
    }

    [UnitTest]
    public static void All_sources_at_zero_still_save_All_audio_as_silence()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            var plan = PlanFor(Names, new float[] { 1, 0, 0, 0, 1 });
            Check.Equal(0, plan[0].Sum!.Length);
            var kept = Trimmer.Trim(clip, output, 1, 5, Names, plan);
            Check.Equal(Names, kept, "the silent All audio is still track 1");
            var mix = TestAudio.Decode(output, 0);
            Check.True(TestAudio.Rms(mix) < .002, "digital silence, give or take the encoder");
            Check.Near(4.0, TestAudio.Seconds(mix), .15, "length of All audio");
        });
    }

    [UnitTest]
    public static void A_single_track_volume_change_stays_in_time_with_the_copied_tracks()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            // Desktop is nobody's source: its volume applies to its own track and the Mix stays a copy.
            var plan = PlanFor(Names, new float[] { 1, 1, 1, 1, .5f });
            Check.Null(plan[0].Sum);
            Trimmer.Trim(clip, output, 1, 5, Names, plan);
            var desktop = TestAudio.Decode(output, 4);
            ExpectTone(desktop, SyntheticClip.DesktopHz, .5, Present, "Desktop at 50%");
            Check.True(TestAudio.IsSlice(TestAudio.Frames(clip, 0), TestAudio.Frames(output, 0)), "the Mix is copied");
            int lag = TestAudio.BestLag(TestAudio.Decode(clip, 4), 2 * 48000, desktop, 48000, 48000, 3000);
            Console.WriteLine($"  (rendered Desktop vs the recorded one: lag {lag} samples)");
            Check.True(Math.Abs(lag) <= 2, $"the render adds no delay (lag {lag})");
        });
    }

    [UnitTest]
    public static void Tracks_that_started_apart_are_lined_up_by_their_edit_lists()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            // Three tracks: one starts 10 ms before the clip origin (its first frames are skipped), one on it, one 25 ms after it.
            SyntheticClip.WriteTones(clip, 3, new[] { ("Mix", 440.0, -.010), ("Game", 880.0, 0.0), ("Voice", 1320.0, .025) }, out var firstFrames);
            var tracks = Trimmer.ReadAudioTracks(clip)!;
            Check.Equal(new[] { "Mix", "Game", "Voice" }, tracks.Select(t => t.Name).ToArray());
            Console.WriteLine($"  (first frames at {string.Join(", ", firstFrames)} (100 ns); read back {string.Join(", ", tracks.Select(t => t.StartOffset))})");
            // The skipped part is whole samples; the late start is stored in milliseconds.
            Check.Near(firstFrames[0], tracks[0].StartOffset, 10, "starts before the origin");
            Check.Near(firstFrames[1], tracks[1].StartOffset, 10, "starts on the origin");
            Check.Near(firstFrames[2], tracks[2].StartOffset, 10_000, "starts after the origin");
            Check.True(tracks[0].StartOffset < -50_000 && tracks[2].StartOffset > 100_000, "the early one is negative, the late one positive");
            Check.Null(Trimmer.ReadAudioTracks(dir.File("missing.mp4")));
        });
    }

    [UnitTest]
    public static void A_track_that_starts_late_is_silent_until_it_starts_and_one_that_started_early_is_cut_to_the_origin()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            SyntheticClip.WriteTones(clip, 3, new[] { ("Mix", 440.0, -.010), ("Game", 880.0, 0.0), ("Voice", 1320.0, .025) }, out _);
            var tracks = Trimmer.ReadAudioTracks(clip)!;
            foreach (int track in new[] { 0, 2 })
            {
                uint stream = TestAudio.StreamOf(clip, track);
                using var reader = new Trimmer.PcmTrackReader(clip, stream, tracks[track].StartOffset, 0, 3 * 10_000_000L);
                var block = new float[2 * Trimmer.PcmTrackReader.Block];
                var first = new List<float>();
                while (first.Count < 4096 && reader.ReadBlock(block) > 0) for (int i = 0; i < block.Length; i += 2) first.Add(block[i]);
                // The start of the track: silence up to its offset (25 ms = 1200 samples for the late one), then the tone (which
                // fades in over the first AAC frame, as every decoded stream does).
                int silent = 0;
                while (silent < first.Count && Math.Abs(first[silent]) < 1e-4) silent++;
                Console.WriteLine($"  (track {track + 1}: {silent} silent samples at the start)");
                if (track == 2) Check.True(silent >= 1150 && silent < 1200 + 1024 + 256, $"the late track starts 1200 samples in (silent for {silent})");
                else Check.True(silent < 300, $"the early track has its sound from the clip origin on (silent for {silent})");
            }
        });
    }

    [UnitTest]
    public static void A_plan_that_names_a_track_the_clip_does_not_have_is_refused()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 4);
            var plan = PlanFor(Names, Unity);
            plan[0] = new TrackMix(true, 1f, new[] { new MixInput(7, 1f) });
            var ex = Check.Throws<ArgumentException>(() => Trimmer.Trim(clip, dir.File("out.mp4"), 0, 3, Names, plan));
            Check.Contains(ex.Message, "audio track 8");
            Check.False(File.Exists(dir.File("out.mp4")), "nothing was written");
        });
    }

    /// <summary>
    /// Not a test: with CLINK_FIXTURE_DIR and CLINK_FIXTURE_SECONDS set it writes a long synthetic five-track clip there
    /// (five-tracks-&lt;seconds&gt;s.mp4; CLINK_FIXTURE_LEVEL, default 0.2, sets the peak of its tones and CLINK_FIXTURE_HZ=g,m,v,d their
    /// frequencies) for measuring
    /// `Clink.exe --trim ... --gain Game=0` on a long clip, which needs a process of its own to read the memory peak from.
    /// </summary>
    [UnitTest]
    public static void A_long_fixture_is_written_when_asked_for()
    {
        string? dir = Environment.GetEnvironmentVariable("CLINK_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !int.TryParse(Environment.GetEnvironmentVariable("CLINK_FIXTURE_SECONDS"), out int seconds) || seconds < 2) return;
        if (!float.TryParse(Environment.GetEnvironmentVariable("CLINK_FIXTURE_LEVEL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float level)) level = SyntheticClip.Level;
        double[]? tones = Environment.GetEnvironmentVariable("CLINK_FIXTURE_HZ")?.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (tones != null && tones.Length != 4) throw new TestFailure("CLINK_FIXTURE_HZ is four frequencies: game, mic, voice, desktop");
        // CLINK_FIXTURE_STARTS=a,b,c: instead of the recording's five tracks, Mix / Game / Voice tones that start a, b and c seconds
        // from the clip origin (negative: before it), for looking at how tracks that started apart play.
        double[]? starts = Environment.GetEnvironmentVariable("CLINK_FIXTURE_STARTS")?.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (starts != null && starts.Length != 3) throw new TestFailure("CLINK_FIXTURE_STARTS is three start times: mix, game, voice");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, starts != null ? $"tones-{seconds}s.mp4" : $"five-tracks-{seconds}s.mp4");
        OnMta(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            File.Delete(path);   // the muxer never overwrites
            if (starts != null) SyntheticClip.WriteTones(path, seconds, new[] { ("Mix", 440.0, starts[0]), ("Game", 880.0, starts[1]), ("Voice", 1320.0, starts[2]) }, out _, level);
            else SyntheticClip.Write(path, seconds, level: level, hz: tones);
            Console.WriteLine($"  (wrote {path}: {new FileInfo(path).Length / 1_048_576.0:0.0} MB in {sw.Elapsed.TotalSeconds:0.0} s)");
        });
    }
}

/// <summary>
/// A five-track clip the way the recording writes one: <see cref="AudioTrack"/> encodes the sources and mixes the Mix from them, and
/// <see cref="ClipWriter"/> muxes. Every source is a pure tone (Game 440 Hz, Mic 880 Hz, Voice 1320 Hz, Desktop 220 Hz) and a little
/// noise of its own, so what a track holds can be measured and two tracks can be lined up by correlation. The video is a gray 16x16
/// picture, once a second, which nothing here decodes.
/// </summary>
internal static unsafe class SyntheticClip
{
    public const double GameHz = 440, MicHz = 880, VoiceHz = 1320, DesktopHz = 220;
    private const int Rate = AudioTrack.Rate, PacketFrames = 960;   // 20 ms packets, as the capture delivers
    public const float Level = .2f;                                 // peak of every tone; the three sources plus noise stay under the Mix's soft clip (0.8)
    private const float Noise = .08f;                               // peak-to-peak of the white noise on every source
    private const long Origin = 1_000_000_000;                     // an arbitrary clock reading, as a recording has

    // Annex B: SPS (baseline, 16x16), PPS, and an IDR slice with one I_PCM macroblock of mid-gray.
    private static readonly byte[] Idr = BuildIdr();

    private static byte[] BuildIdr()
    {
        var au = new List<byte>();
        byte[] sc = { 0, 0, 0, 1 };
        au.AddRange(sc); au.AddRange(new byte[] { 0x67, 0x42, 0xC0, 0x0A, 0xDA, 0x79 });
        au.AddRange(sc); au.AddRange(new byte[] { 0x68, 0xCE, 0x38, 0x80 });
        au.AddRange(sc); au.AddRange(new byte[] { 0x65, 0x88, 0x84, 0x86, 0x80 });
        au.AddRange(Enumerable.Repeat((byte)0x80, 384));    // 256 luma + 2 x 64 chroma samples
        au.Add(0x80);                                        // rbsp trailing bits
        return au.ToArray();
    }

    private static List<VideoSample> Video(double seconds)
    {
        var video = new List<VideoSample>();
        for (long k = 0; Origin + k * 10_000_000 <= Origin + (long)(seconds * 10_000_000); k++)
            video.Add(new VideoSample { Pts = Origin + k * 10_000_000, Keyframe = true, Data = Idr });
        return video;
    }

    /// <param name="voiceTrack">False leaves the Voice track out of the file (the default setting does): the Mix still holds it.</param>
    /// <param name="level">Peak of every tone (the noise follows); the tests measure against <see cref="Level"/>.</param>
    /// <param name="hz">The tones of Game, Mic, Voice and Desktop; the defaults are the constants above (a clip made to be played and
    /// recorded back picks tones far above what music has energy in, so they can be told from it).</param>
    /// <returns>The names of the tracks written, in file order.</returns>
    public static string[] Write(string path, double seconds, bool voiceTrack = true, float level = Level, double[]? hz = null)
    {
        hz ??= new[] { GameHz, MicHz, VoiceHz, DesktopHz };
        Mf.EnsureStarted();
        var game = new AudioTrack("Game");
        var mic = new AudioTrack("Mic");
        var voice = new AudioTrack("Voice");
        var desktop = new AudioTrack("Desktop");
        var mix = new AudioTrack("Mix", new[] { game, mic, voice });
        var all = new[] { mix, game, mic, voice, desktop };
        try
        {
            var tones = new (AudioTrack track, double hz)[] { (game, hz[0]), (mic, hz[1]), (voice, hz[2]), (desktop, hz[3]) };
            var seeds = new uint[] { 0x9E3779B9, 0x85EBCA6B, 0xC2B2AE35, 0x27D4EB2F };   // one noise sequence per source
            var packet = new float[PacketFrames * 2];
            long packets = (long)Math.Ceiling((seconds + 1) * 50);   // a second more than the clip, so the mix has caught up with its end
            for (long p = 0; p < packets; p++)
            {
                long qpc = Origin + p * 200_000;
                for (int s = 0; s < tones.Length; s++)
                {
                    var (track, toneHz) = tones[s];
                    for (int i = 0; i < PacketFrames; i++)
                    {
                        seeds[s] = seeds[s] * 1664525u + 1013904223u;   // LCG
                        float noise = ((seeds[s] >> 8) / (float)(1 << 24) - .5f) * Noise * (level / Level);
                        float v = level * (float)Math.Sin(2 * Math.PI * toneHz * ((p * PacketFrames + i) % Rate) / Rate) + noise;
                        packet[i * 2] = v; packet[i * 2 + 1] = v;
                    }
                    fixed (float* f = packet) track.OnPacket(f, PacketFrames, qpc, false);
                }
                if (p % 5 == 4)
                {
                    // The engine's audio loop every 100 ms: sources first, then the mix up to 300 ms behind now.
                    long until = qpc + 200_000 - 3_000_000;
                    foreach (var t in all) t.PadSilenceTo(until);
                    foreach (var t in all) t.MixUpTo(until);
                }
            }

            long end = Origin + (long)(seconds * 10_000_000);
            var audio = new List<(AudioTrackDesc desc, List<AacFrame> frames)>();
            foreach (var t in all)
            {
                if (t == voice && !voiceTrack) continue;
                audio.Add((new AudioTrackDesc { Name = t.Name, AudioSpecificConfig = t.AudioSpecificConfig, Bitrate = t.Bitrate, SampleRate = Rate, Channels = 2 },
                    t.Snapshot(Origin, end)));
            }
            return ClipWriter.Write(path, Video(seconds), "h264", 16, 16, null, audio, Origin, 0);
        }
        finally { foreach (var t in all) t.Dispose(); }
    }

    /// <summary>
    /// A clip of plain tracks, each a tone that starts <c>start</c> seconds after the clip origin (negative: before it, so the
    /// muxer skips that much of the track), for the edit-list tests. Names are as given, in file order.
    /// </summary>
    /// <param name="firstFrames">Where each track's first frame in the file starts relative to the origin, 100 ns: what its edit list should say.</param>
    public static string[] WriteTones(string path, double seconds, (string name, double hz, double start)[] tracks, out long[] firstFrames, float level = Level)
    {
        Mf.EnsureStarted();
        var audio = new List<(AudioTrackDesc desc, List<AacFrame> frames)>();
        var firsts = new List<long>();
        long end = Origin + (long)(seconds * 10_000_000);
        foreach (var (name, hz, start) in tracks)
        {
            using var enc = new AacEncoder(Rate, 2, 24000);
            var frames = new List<AacFrame>();
            long first = Origin + (long)(start * 10_000_000);
            long block = AacEncoder.FrameSamples * 10_000_000L / Rate;
            var pcm = new byte[AacEncoder.FrameSamples * 4];
            for (long k = 0; first + k * block <= end; k++)
            {
                for (int i = 0; i < AacEncoder.FrameSamples; i++)
                {
                    long n = k * AacEncoder.FrameSamples + i;
                    short s = (short)Math.Round(level * 32767 * Math.Sin(2 * Math.PI * hz * (n % Rate) / Rate));
                    pcm[i * 4] = pcm[i * 4 + 2] = (byte)s;
                    pcm[i * 4 + 1] = pcm[i * 4 + 3] = (byte)(s >> 8);
                }
                enc.Encode(pcm, first + k * block, (pts, data) => frames.Add(new AacFrame(pts, data)));
            }
            // The muxer's snapshot rule: whole frames that end after the origin.
            frames = frames.Where(f => f.Pts + block > Origin).ToList();
            firsts.Add(frames[0].Pts - Origin);
            audio.Add((new AudioTrackDesc { Name = name, AudioSpecificConfig = enc.AudioSpecificConfig, Bitrate = enc.BytesPerSecond * 8, SampleRate = Rate, Channels = 2 }, frames));
        }
        firstFrames = firsts.ToArray();
        return ClipWriter.Write(path, Video(seconds), "h264", 16, 16, null, audio, Origin, 0);
    }
}

/// <summary>Reads the audio of a clip back for the tests: decoded to floats on the file's timeline, or as the compressed frames.</summary>
internal static unsafe class TestAudio
{
    private const uint AllStreams = 0xFFFFFFFE;

    public static int TrackCount(string path) => Trimmer.AudioStreamsOf(path).Count;

    /// <summary>The reader's stream number of audio track <paramref name="track"/> (file order).</summary>
    public static uint StreamOf(string path, int track) => Trimmer.AudioStreamsOf(path)[track];

    private static IMFSourceReader* Open(string path, uint stream, bool decoded)
    {
        IMFSourceReader* reader;
        PInvoke.MFCreateSourceReaderFromURL(path, null, &reader).Check("MFCreateSourceReaderFromURL");
        reader->SetStreamSelection(AllStreams, false);
        reader->SetStreamSelection(stream, true);
        if (decoded)
        {
            IMFMediaType* want = Mf.CreateMediaType();
            var a = (IMFAttributes*)want;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_Float);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, 8);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 48000 * 8);
            Mf.SetU32(a, PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            HRESULT hr = reader->SetCurrentMediaType(stream, null, want);
            want->Release();
            hr.Check("SetCurrentMediaType(float)");
        }
        return reader;
    }

    /// <summary>The left channel of audio track <paramref name="track"/> (file order) at 48 kHz, each packet at its own timestamp (the track's own, from 0: the edit list is not applied).</summary>
    public static float[] Decode(string path, int track)
    {
        uint stream = StreamOf(path, track);
        IMFSourceReader* reader = Open(path, stream, decoded: true);
        try
        {
            var left = new List<float>();
            while (true)
            {
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample");
                if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0)
                {
                    if (sample != null) sample->Release();
                    break;
                }
                if (sample == null) continue;
                try
                {
                    byte[] raw = Mf.ReadSampleBytes(sample);
                    int n = raw.Length / 8;
                    long at = (long)Math.Round(ts * (48000 / 10_000_000.0));
                    while (left.Count < at) left.Add(0f);
                    for (int i = (int)Math.Max(0, left.Count - at); i < n; i++) left.Add(BitConverter.ToSingle(raw, i * 8));
                }
                finally { sample->Release(); }
            }
            return left.ToArray();
        }
        finally { reader->Release(); }
    }

    /// <summary>The compressed AAC frames of audio track <paramref name="track"/>, as stored.</summary>
    public static List<byte[]> Frames(string path, int track)
    {
        uint stream = StreamOf(path, track);
        IMFSourceReader* reader = Open(path, stream, decoded: false);
        try
        {
            var frames = new List<byte[]>();
            while (true)
            {
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample");
                if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0)
                {
                    if (sample != null) sample->Release();
                    break;
                }
                if (sample == null) continue;
                try { frames.Add(Mf.ReadSampleBytes(sample)); }
                finally { sample->Release(); }
            }
            return frames;
        }
        finally { reader->Release(); }
    }

    /// <summary>True when <paramref name="part"/> is a run of whole frames taken out of <paramref name="whole"/>, byte for byte (a trim of a copied track).</summary>
    public static bool IsSlice(List<byte[]> whole, List<byte[]> part)
    {
        if (part.Count == 0 || part.Count > whole.Count) return false;
        for (int start = 0; start + part.Count <= whole.Count; start++)
        {
            bool same = true;
            for (int i = 0; i < part.Count && same; i++) same = whole[start + i].AsSpan().SequenceEqual(part[i]);
            if (same) return true;
        }
        return false;
    }

    /// <summary>Peak amplitude of the sine of <paramref name="hz"/> in one second from the middle of <paramref name="x"/> (a whole number of cycles for a whole number of Hz).</summary>
    public static double Amplitude(float[] x, int rate, double hz)
    {
        int n = rate;
        int start = Math.Max(0, x.Length / 2 - n / 2);
        n = Math.Min(n, x.Length - start);
        if (n <= 0) return 0;
        double re = 0, im = 0, w = 2 * Math.PI * hz / rate;
        for (int i = 0; i < n; i++)
        {
            re += x[start + i] * Math.Cos(w * (start + i));
            im += x[start + i] * Math.Sin(w * (start + i));
        }
        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    public static double Rms(float[] x)
    {
        if (x.Length == 0) return 0;
        double sum = 0;
        foreach (float v in x) sum += (double)v * v;
        return Math.Sqrt(sum / x.Length);
    }

    public static double Seconds(float[] x) => x.Length / 48000.0;

    /// <summary>
    /// The shift (in samples) at which <paramref name="b"/> best lines up with <paramref name="a"/>: compares <paramref name="length"/>
    /// samples of a from <paramref name="aStart"/> with b from bStart + lag, for lags from -range to +range. Positive: b is later.
    /// </summary>
    public static int BestLag(float[] a, int aStart, float[] b, int bStart, int length, int range)
    {
        int best = 0;
        double bestScore = double.MinValue;
        for (int lag = -range; lag <= range; lag++)
        {
            double score = Dot(a, aStart, b, bStart + lag, length);
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        return best;
    }

    /// <summary>
    /// Normalized correlation (1 = the same signal, 0 = nothing in common) of <paramref name="length"/> samples of a from aStart with b
    /// from bStart; with a range, the largest over the lags -range..range.
    /// </summary>
    public static double Correlation(float[] a, float[] b, int length, int aStart, int bStart, int range)
    {
        double best = 0;
        for (int lag = -range; lag <= range; lag++)
        {
            double dot = Dot(a, aStart, b, bStart + lag, length);
            double na = Dot(a, aStart, a, aStart, length), nb = Dot(b, bStart + lag, b, bStart + lag, length);
            if (na > 0 && nb > 0) best = Math.Max(best, Math.Abs(dot) / Math.Sqrt(na * nb));
        }
        return best;
    }

    private static double Dot(float[] a, int aStart, float[] b, int bStart, int length)
    {
        double dot = 0;
        for (int i = 0; i < length; i += 2)
        {
            int ai = aStart + i, bi = bStart + i;
            if (ai < 0 || bi < 0 || ai >= a.Length || bi >= b.Length) continue;
            dot += (double)a[ai] * b[bi];
        }
        return dot;
    }
}
