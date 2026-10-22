using System.IO;
using System.Runtime.InteropServices;
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
/// some noise of its own, the Mix is their sum, the sources started at different moments), trimmed through <see cref="Trimmer"/> with
/// the plan <see cref="MixPlan"/> makes, then decoded and measured tone by tone. Needs the Windows AAC encoder and decoder
/// (Media Foundation), like the editor does; a machine without them skips these tests (and says so), unless CLINK_TESTS_NO_SKIP=1.
/// </summary>
internal static class MixRenderTests
{
    private static readonly string[] Names = { "Mix", "Game", "Mic", "Voice", "Desktop" };
    private static readonly float[] Unity = { 1, 1, 1, 1, 1 };

    private static string? s_whyNoAac;
    private static bool s_checkedAac;

    /// <summary>
    /// Media Foundation wants an MTA thread, like the editor's Task.Run; the test runner itself is on the UI thread. A machine that
    /// cannot make the clips at all (see <see cref="SkipReason"/>) skips the test; anything else that goes wrong fails it.
    /// </summary>
    internal static void OnMta(Action body)
    {
        Exception? failure = null;
        string? skip = null;
        var t = new Thread(() =>
        {
            try
            {
                if (!s_checkedAac)
                {
                    s_whyNoAac = WhyNoAac();
                    s_checkedAac = true;
                }
                if (s_whyNoAac != null) skip = s_whyNoAac;
                else body();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (failure != null) throw new TestFailure(failure is TestFailure ? failure.Message : failure.ToString());
        if (skip != null) Check.Skip(skip);
    }

    /// <summary>Why this machine cannot make the clips: null when it can. Anything but the two cases of <see cref="SkipReason"/> is thrown, and fails the test.</summary>
    private static string? WhyNoAac()
    {
        try
        {
            Mf.EnsureStarted();
            using var enc = new AacEncoder();
            return null;
        }
        catch (Exception ex) when (SkipReason(ex) is { } why) { return why; }
    }

    /// <summary>
    /// The only two reasons to skip: Media Foundation is not there (an N edition of Windows without the media pack) or its AAC encoder
    /// class is not registered (a stripped-down image). Any other failure of the encoder is a bug or a broken machine, and fails.
    /// </summary>
    internal static string? SkipReason(Exception ex)
    {
        const int ClassNotRegistered = unchecked((int)0x80040154);   // REGDB_E_CLASSNOTREG
        return ex switch
        {
            DllNotFoundException or EntryPointNotFoundException => "Media Foundation is not installed on this machine",
            COMException c when c.HResult == ClassNotRegistered => "the Media Foundation AAC encoder is not registered on this machine",
            COMException c when c.Message.StartsWith("MFStartup", StringComparison.Ordinal) => "Media Foundation cannot start on this machine",
            _ => null,
        };
    }

    internal static TrackMix[] PlanFor(string[] names, float[] gain, bool[]? include = null)
    {
        var roles = MixPlan.Roles(names, names.Length);
        return MixPlan.Export(roles, include ?? Enumerable.Repeat(true, names.Length).ToArray(), gain);
    }

    /// <summary>The tone of <paramref name="hz"/> in the track is <paramref name="share"/> times the level it was recorded at (0 = absent), give or take <paramref name="tolerance"/> of that level.</summary>
    private static void ExpectTone(float[] track, double hz, double share, double tolerance, string what)
        => Check.Near(share * SyntheticClip.Level, TestAudio.Amplitude(track, 48000, hz), tolerance * SyntheticClip.Level, $"{what}: level of the {hz:0} Hz tone");

    private const double Present = .07, Absent = .02;   // what counts as "at its level" and as "not there", as a share of the recorded level

    [UnitTest]
    public static void A_missing_encoder_or_media_foundation_is_a_skip_and_nothing_else_is()
    {
        Check.NotNull(SkipReason(new COMException("CoCreateInstance(AAC encoder) failed", unchecked((int)0x80040154))), "class not registered");
        Check.NotNull(SkipReason(new COMException("MFStartup failed: 0xC00D36FA", unchecked((int)0xC00D36FA))), "Media Foundation cannot start");
        Check.NotNull(SkipReason(new DllNotFoundException("mfplat.dll")), "no Media Foundation library");
        Check.NotNull(SkipReason(new EntryPointNotFoundException("MFStartup")), "no Media Foundation entry point");
        Check.Null(SkipReason(new COMException("access denied", unchecked((int)0x80070005))), "another COM failure is a failure");
        Check.Null(SkipReason(new COMException("CoCreateInstance(AAC encoder) failed", unchecked((int)0x80004005))), "so is an unspecified one");
        Check.Null(SkipReason(new InvalidOperationException("the encoder blew up")), "and any other exception");
        Check.Null(SkipReason(new NullReferenceException()), "even a bug in the test");
    }

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
            Check.True(Trimmer.ReadClipTracks(clip)!.Marked, "a recording says its edit lists are true");
            // The sources started at different moments, not on one frame grid: their places differ below a frame, as a recording's do.
            var phases = Trimmer.ReadAudioTracks(clip)!.Select(t => (int)(((t.StartFrames % 1024) + 1024) % 1024)).ToArray();
            Check.Equal(5, phases.Distinct().Count(), $"five different places inside a frame ({string.Join(", ", phases)})");
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
            // The game's noise is gone too, which it only is when the two tracks were lined up to the sample. Both on the clip's
            // timeline: the output starts at the clip's second 1.
            double residue = TestAudio.Correlation(TestAudio.Timeline(output, 0), TestAudio.Timeline(clip, 1), 48000, 2 * 48000, 3 * 48000, 2);
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
            Check.True(Trimmer.ReadClipTracks(output)!.Marked, "and the cut says its edit lists are true, as the recording did");
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
            // Both on the clip's timeline: the output starts at the clip's second 1, so its second 1 is the recording's second 2.
            var baked = TestAudio.Timeline(clip, 0);
            var rebuilt = TestAudio.Timeline(output, 0);
            int lag = TestAudio.BestLag(baked, 2 * 48000, rebuilt, 48000, 48000, 3000);
            double match = TestAudio.Correlation(baked, rebuilt, 48000, 2 * 48000, 48000, 0);
            Console.WriteLine($"  (rebuilt All audio vs the baked one: lag {lag} samples, correlation {match:0.000})");
            Check.True(Math.Abs(lag) <= 1, $"the rebuilt mix is in time with the baked one to the sample (lag {lag})");
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
            // Game alone at half volume (a plan of the trimmer's own, not the editor's): the Game track is rendered again, the Mix and
            // the rest are copied. The Game started at a different moment of the frame grid than the Mix, so a render that put its
            // sound where the file's first frame lies would be a few hundred samples off the copied Mix, which holds the same game.
            var plan = PlanFor(Names, Unity);
            plan[1] = new TrackMix(true, .5f);
            Trimmer.Trim(clip, output, 1, 5, Names, plan);
            ExpectTone(TestAudio.Decode(output, 1), SyntheticClip.GameHz, .5, Present, "Game at 50%");
            Check.True(TestAudio.IsSlice(TestAudio.Frames(clip, 0), TestAudio.Frames(output, 0)), "the Mix is copied");
            var mix = TestAudio.Timeline(output, 0);
            var game = TestAudio.Timeline(output, 1);
            int lag = TestAudio.BestLag(mix, 48000, game, 48000, 48000, 3000);
            double match = TestAudio.Correlation(mix, game, 48000, 48000, 48000, 0);
            Console.WriteLine($"  (rendered Game vs the copied Mix in the same file: lag {lag} samples, correlation {match:0.000})");
            Check.True(Math.Abs(lag) <= 1, $"the render adds no delay against the copied tracks (lag {lag})");
            // Against the Voice and Mic copies too: nothing in common with the Game, so their best lag proves nothing; the Mix is the check.
            Check.True(match > .3, $"and the game is in the Mix at that lag (correlation {match:0.000})");
        });
    }

    [UnitTest]
    public static void Tracks_that_started_apart_are_lined_up_by_their_edit_lists_to_the_sample()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            // Five tracks that carry one sound: one starts 10 ms before the clip origin (its first samples are skipped), one a few
            // samples before it, one on it, one 0.4 ms after it and one 25 ms after it.
            var starts = new (string, int, float)[] { ("Mix", -480, 1), ("Game", 0, 1), ("Mic", 19, 1), ("Voice", -37, 1), ("Desktop", 1200, 1) };
            SyntheticClip.WriteSame(clip, 3, starts, out var firstFrames);
            var tracks = Trimmer.ReadAudioTracks(clip)!;
            Check.Equal(new[] { "Mix", "Game", "Mic", "Voice", "Desktop" }, tracks.Select(t => t.Name).ToArray());
            Console.WriteLine($"  (first frames at {string.Join(", ", firstFrames)} (100 ns); read back {string.Join(", ", tracks.Select(t => t.StartFrames))} frames)");
            Check.Equal(new long[] { -480, 0, 19, -37, 1200 }, tracks.Select(t => t.StartFrames).ToArray(), "every track starts where it was put, to the sample");
            for (int i = 0; i < tracks.Length; i++) Check.Near(firstFrames[i], tracks[i].StartOffset, 10, $"{tracks[i].Name}: 100 ns");
            // And the sound is where the places say: every track agrees with the first one on the clip's timeline.
            var timeline = Enumerable.Range(0, tracks.Length).Select(i => TestAudio.Timeline(clip, i)).ToArray();
            for (int i = 1; i < tracks.Length; i++)
            {
                int lag = TestAudio.BestLag(timeline[1], 40000, timeline[i], 40000, 30000, 4);
                Check.Equal(0, lag, $"{tracks[i].Name} is in line with Game");
            }
            Check.Null(Trimmer.ReadAudioTracks(dir.File("missing.mp4")));
        });
    }

    [UnitTest]
    public static void A_late_start_is_stored_to_the_sample_and_not_to_the_millisecond()
    {
        using var dir = new TempDir();
        string clip = dir.File("late.mp4");
        OnMta(() =>
        {
            // 19 samples is the 0.4 ms of the review: a millisecond-wide edit list rounded it to 0. The others sit on both sides of a
            // millisecond (48 samples) and of the frame.
            int[] late = { 1, 19, 47, 48, 49, 95, 480, 1023, 1024, 1200 };
            var tracks = new List<(string, int, float)> { ("Mix", 0, 1) };
            for (int i = 0; i < late.Length; i++) tracks.Add(($"Late {late[i]}", late[i], 1));
            SyntheticClip.WriteSame(clip, 3, tracks.ToArray(), out _);
            var read = Trimmer.ReadAudioTracks(clip)!;
            Check.Equal(0L, read[0].StartFrames, "the Mix starts on the origin");
            for (int i = 0; i < late.Length; i++) Check.Equal((long)late[i], read[i + 1].StartFrames, $"a track {late[i]} samples after the origin ({late[i] / 48.0:0.000} ms)");
        });
    }

    [UnitTest]
    public static void A_track_that_starts_late_is_silent_until_it_starts_and_one_that_started_early_is_cut_to_the_origin()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            SyntheticClip.WriteSame(clip, 3, new (string, int, float)[] { ("Mix", -480, 1), ("Game", 0, 1), ("Voice", 1200, 1) }, out _);
            var tracks = Trimmer.ReadAudioTracks(clip)!;
            foreach (int track in new[] { 0, 2 })
            {
                uint stream = TestAudio.StreamOf(clip, track);
                using var reader = new Trimmer.PcmTrackReader(clip, stream, tracks[track].StartFrames, 0, 3 * 48000L);
                var block = new float[2 * Trimmer.PcmTrackReader.Block];
                var first = new List<float>();
                while (first.Count < 4096 && reader.ReadBlock(block) > 0) for (int i = 0; i < block.Length; i += 2) first.Add(block[i]);
                // The start of the track: silence up to its offset (25 ms = 1200 samples for the late one), then the sound (which
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
    public static void The_track_reader_hands_out_each_track_as_the_decoder_has_it_on_the_timeline()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            var starts = new (string, int, float)[] { ("Mix", -480, 1), ("Game", 0, 1), ("Mic", 19, 1), ("Voice", -37, 1), ("Desktop", 1200, 1) };
            SyntheticClip.WriteSame(clip, 4, starts, out _);
            var offsets = TestAudio.Offsets(clip);
            for (int track = 0; track < starts.Length; track++)
            {
                var expected = TestAudio.Timeline(clip, track);
                // From the clip origin, and from the middle of the clip (a seek, and a first packet that starts before the block does).
                foreach (long origin in new long[] { 0, 30000 + track * 17 })
                {
                    using var reader = new Trimmer.PcmTrackReader(clip, TestAudio.StreamOf(clip, track), offsets[track], origin, 4 * 48000L);
                    var block = new float[2 * Trimmer.PcmTrackReader.Block];
                    var got = new List<float>();
                    while (got.Count < 20000 && reader.ReadBlock(block) > 0) for (int i = 0; i < Trimmer.PcmTrackReader.Block; i++) got.Add(block[i * 2]);
                    Check.True(got.Count >= 20000, $"{starts[track].Item1}: enough sound ({got.Count})");
                    // (After a seek the decoder has no packet before the first to overlap with, so its first two packets are not exact.)
                    double worst = 0;
                    for (int i = origin == 0 ? 0 : 2048; i < 20000; i++) worst = Math.Max(worst, Math.Abs(got[i] - (origin + i < expected.Length ? expected[origin + i] : 0f)));
                    Check.True(worst < 1e-6, $"{starts[track].Item1} from frame {origin}: sample for sample what the decoder has on the timeline (worst difference {worst})");
                }
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
    /// frequencies; CLINK_FIXTURE_NOVOICE=1 leaves the Voice track out, CLINK_FIXTURE_UNMARKED=1 writes the file the way a Clink before
    /// the mark did, CLINK_FIXTURE_NOISE and CLINK_FIXTURE_GAMELAG: see below) for measuring
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
        bool noVoice = Environment.GetEnvironmentVariable("CLINK_FIXTURE_NOVOICE") == "1", unmarked = Environment.GetEnvironmentVariable("CLINK_FIXTURE_UNMARKED") == "1";
        // CLINK_FIXTURE_NOISE=6 makes the sources mostly noise (a tone alone cannot be lined up with the Mix by correlation);
        // CLINK_FIXTURE_GAMELAG=400 writes the Game track's frames that many samples late, as a trim by an older Clink did.
        double noise = double.TryParse(Environment.GetEnvironmentVariable("CLINK_FIXTURE_NOISE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ns) ? ns : 1;
        long gameLag = long.TryParse(Environment.GetEnvironmentVariable("CLINK_FIXTURE_GAMELAG"), out long gl) ? gl : 0;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, starts != null ? $"tones-{seconds}s.mp4" : $"five-tracks-{seconds}s.mp4");
        OnMta(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            File.Delete(path);   // the muxer never overwrites
            if (starts != null) SyntheticClip.WriteTones(path, seconds, new[] { ("Mix", 440.0, starts[0]), ("Game", 880.0, starts[1]), ("Voice", 1320.0, starts[2]) }, out _, level);
            else SyntheticClip.Write(path, seconds, voiceTrack: !noVoice, level: level, hz: tones, noiseScale: noise, stampLag: new long[] { gameLag, 0, 0, 0 }, aligned: !unmarked);
            Console.WriteLine($"  (wrote {path}: {new FileInfo(path).Length / 1_048_576.0:0.0} MB in {sw.Elapsed.TotalSeconds:0.0} s)");
        });
    }
}

/// <summary>
/// A five-track clip the way the recording writes one: <see cref="AudioTrack"/> encodes the sources and mixes the Mix from them, and
/// <see cref="ClipWriter"/> muxes. Every source is a pure tone (Game 440 Hz, Mic 880 Hz, Voice 1320 Hz, Desktop 220 Hz) and a little
/// noise of its own, so what a track holds can be measured and two tracks can be lined up by correlation. The recording has been
/// running for a second when the clip starts and its sources began at different moments, so the tracks' frame grids sit differently
/// against the clip's start, as in a real recording (all of them starting on the origin hid every alignment mistake). The video is a
/// gray 16x16 picture, once a second, which nothing here decodes.
/// </summary>
internal static unsafe class SyntheticClip
{
    public const double GameHz = 440, MicHz = 880, VoiceHz = 1320, DesktopHz = 220;
    private const int Rate = AudioTrack.Rate, PacketFrames = 960;   // 20 ms packets, as the capture delivers
    public const float Level = .2f;                                 // peak of every tone; the three sources plus noise stay under the Mix's soft clip (0.8)
    private const float Noise = .08f;                               // peak-to-peak of the white noise on every source
    private const long Origin = 1_000_000_000;                     // an arbitrary clock reading, as a recording has
    private const long PreRoll = 10_000_000;                       // the capture started a second before the clip does

    /// <summary>When the capture of Game, Mic, Voice and Desktop began after the capture clock started, seconds: none on the sample grid of another, none on a frame.</summary>
    public static readonly double[] DefaultDelays = { 0.0, 0.00371, 0.00793, 0.01181 };

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
    /// <param name="delays">When each of Game, Mic, Voice and Desktop began, seconds after the capture clock (<see cref="DefaultDelays"/>).</param>
    /// <param name="noiseScale">The noise of every source as a multiple of the usual (0 = none); a source that is mostly noise can be lined up with the Mix by correlation, a pure tone cannot.</param>
    /// <param name="stampLag">Frames to add to the timestamps of Game, Mic, Voice and Desktop when the clip is muxed: a recording whose tracks were written with the wrong places (what a trim by a Clink before the mark did).</param>
    /// <param name="aligned">False writes the file without the mark, as a Clink before it did.</param>
    /// <param name="wave">The shape of source <c>s</c> (0 Game, 1 Mic, 2 Voice, 3 Desktop) at sample <c>n</c> of the second, -1..1 (before <paramref name="level"/>); a sine of <paramref name="hz"/> by default.</param>
    /// <returns>The names of the tracks written, in file order.</returns>
    public static string[] Write(string path, double seconds, bool voiceTrack = true, float level = Level, double[]? hz = null,
        double[]? delays = null, double noiseScale = 1, long[]? stampLag = null, bool aligned = true, Func<int, long, float>? wave = null)
    {
        hz ??= new[] { GameHz, MicHz, VoiceHz, DesktopHz };
        delays ??= DefaultDelays;
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
            long start = Origin - PreRoll;
            long packets = (long)Math.Ceiling((seconds + 2) * 50);   // the second before the clip, the clip, and a second more so the mix has caught up with its end
            for (long p = 0; p < packets; p++)
            {
                long tick = start + p * 200_000;
                for (int s = 0; s < tones.Length; s++)
                {
                    var (track, toneHz) = tones[s];
                    long qpc = tick + (long)Math.Round(delays[s] * 10_000_000);
                    for (int i = 0; i < PacketFrames; i++)
                    {
                        seeds[s] = seeds[s] * 1664525u + 1013904223u;   // LCG
                        float noise = ((seeds[s] >> 8) / (float)(1 << 24) - .5f) * Noise * (level / Level) * (float)noiseScale;
                        long n = (p * PacketFrames + i) % Rate;
                        float tone = wave != null ? wave(s, n) : (float)Math.Sin(2 * Math.PI * toneHz * n / Rate);
                        float v = level * tone + noise;
                        packet[i * 2] = v; packet[i * 2 + 1] = v;
                    }
                    fixed (float* f = packet) track.OnPacket(f, PacketFrames, qpc, false);
                }
                if (p % 5 == 4)
                {
                    // The engine's audio loop every 100 ms: sources first, then the mix up to 300 ms behind now.
                    long until = tick + 200_000 - 3_000_000;
                    foreach (var t in all) t.PadSilenceTo(until);
                    foreach (var t in all) t.MixUpTo(until);
                }
            }

            long end = Origin + (long)(seconds * 10_000_000);
            var audio = new List<(AudioTrackDesc desc, List<AacFrame> frames)>();
            foreach (var t in all)
            {
                if (t == voice && !voiceTrack) continue;
                var frames = t.Snapshot(Origin, end);
                int source = Array.IndexOf(tones.Select(x => x.track).ToArray(), t);
                if (stampLag != null && source >= 0 && stampLag[source] != 0)
                {
                    long units = (long)Math.Round(stampLag[source] * 10_000_000.0 / Rate);
                    frames = frames.Select(f => new AacFrame(f.Pts + units, f.Data)).ToList();
                }
                audio.Add((new AudioTrackDesc { Name = t.Name, AudioSpecificConfig = t.AudioSpecificConfig, Bitrate = t.Bitrate, SampleRate = Rate, Channels = 2 }, frames));
            }
            return ClipWriter.Write(path, Video(seconds), "h264", 16, 16, null, audio, Origin, 0, aligned);
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

    /// <summary>
    /// The sound of <see cref="Signal"/>: a function of the sample's place on the clip's timeline.
    /// </summary>
    public static float Signal(long m)
    {
        uint h = unchecked((uint)m * 2654435761u);
        h ^= h >> 15; h = unchecked(h * 2246822519u); h ^= h >> 13; h = unchecked(h * 3266489917u); h ^= h >> 16;   // an integer hash: white noise that depends on nothing but m
        float noise = h / (float)uint.MaxValue * 2 - 1;
        return .2f * (float)Math.Sin(2 * Math.PI * 440 * (((m % Rate) + Rate) % Rate) / Rate) + .15f * noise;
    }

    /// <summary>
    /// A clip of tracks that carry one and the same sound (<see cref="Signal"/> of the sample's place on the clip's timeline), each from
    /// its own sample <c>start</c> (negative: before the clip origin, so the muxer skips that much of it) at its own <c>gain</c>: whatever
    /// is done to the clip, two tracks that are in line have the same sound at the same index of their timelines, and the lag
    /// between them says how far a track was moved. The tracks' places lie between the frames of one another's grids.
    /// </summary>
    /// <param name="jitter">Samples by which every second frame's timestamp is late (the jitter a stream with imprecise timestamps has).</param>
    /// <param name="firstFrames">Where each track's first frame starts relative to the origin, 100 ns.</param>
    public static string[] WriteSame(string path, double seconds, (string name, int start, float gain)[] tracks, out long[] firstFrames, int jitter = 0, bool aligned = true)
    {
        Mf.EnsureStarted();
        var audio = new List<(AudioTrackDesc desc, List<AacFrame> frames)>();
        var firsts = new List<long>();
        long end = Origin + (long)(seconds * 10_000_000);
        double unit = 10_000_000.0 / Rate;
        foreach (var (name, start, gain) in tracks)
        {
            using var enc = new AacEncoder(Rate, 2, 24000);
            var frames = new List<AacFrame>();
            long first = Origin + (long)Math.Round(start * unit);
            var pcm = new byte[AacEncoder.FrameSamples * 4];
            int k = 0;
            for (int input = 0; first + (long)Math.Round(input * AacEncoder.FrameSamples * unit) <= end; input++)
            {
                for (int i = 0; i < AacEncoder.FrameSamples; i++)
                {
                    short s = (short)Math.Round(gain * Signal(start + (long)input * AacEncoder.FrameSamples + i) * 32767);
                    pcm[i * 4] = pcm[i * 4 + 2] = (byte)s;
                    pcm[i * 4 + 1] = pcm[i * 4 + 3] = (byte)(s >> 8);
                }
                // The frames are stamped by their number, exactly; the encoder's own timestamps are not needed.
                enc.Encode(pcm, first + (long)Math.Round(input * AacEncoder.FrameSamples * unit), (_, data) => frames.Add(new AacFrame(first + (long)Math.Round(k++ * AacEncoder.FrameSamples * unit), data)));
            }
            // The muxer's snapshot rule: whole frames that end after the origin.
            long block = (long)Math.Round(AacEncoder.FrameSamples * unit);
            frames = frames.Where(f => f.Pts + block > Origin).ToList();
            firsts.Add(frames[0].Pts - Origin);
            if (jitter != 0)
            {
                long late = (long)Math.Round(jitter * unit);
                frames = frames.Select((f, i) => i % 2 == 1 ? new AacFrame(f.Pts + late, f.Data) : f).ToList();
            }
            audio.Add((new AudioTrackDesc { Name = name, AudioSpecificConfig = enc.AudioSpecificConfig, Bitrate = enc.BytesPerSecond * 8, SampleRate = Rate, Channels = 2 }, frames));
        }
        firstFrames = firsts.ToArray();
        return ClipWriter.Write(path, Video(seconds), "h264", 16, 16, null, audio, Origin, 0, aligned);
    }
}

/// <summary>Reads the audio of a clip back for the tests: decoded to floats on the file's timeline, or as the compressed frames.</summary>
internal static unsafe class TestAudio
{
    private const uint AllStreams = 0xFFFFFFFE;

    public static int TrackCount(string path) => Trimmer.AudioStreamsOf(path).Count;

    /// <summary>Where each audio track's first sample sits on the clip's timeline, in frames (its edit list).</summary>
    public static long[] Offsets(string path) => Trimmer.ReadAudioTracks(path)!.Select(t => t.StartFrames).ToArray();

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

    /// <summary>
    /// The left channel of the track on the clip's timeline: index 0 is the clip origin, a track that starts late has silence in front
    /// of it and one that started early has lost what lies before the origin (the track's edit list applied, to the sample).
    /// </summary>
    public static float[] Timeline(string path, int track)
    {
        var x = Decode(path, track);
        long offset = Trimmer.ReadAudioTracks(path)![track].StartFrames;
        if (offset >= 0)
        {
            var y = new float[offset + x.Length];
            Array.Copy(x, 0, y, offset, x.Length);
            return y;
        }
        int skip = (int)Math.Min(-offset, x.Length);
        var z = new float[x.Length - skip];
        Array.Copy(x, skip, z, 0, z.Length);
        return z;
    }

    /// <summary>The timestamps (100 ns) of the compressed frames of audio track <paramref name="track"/>, as the file stores them.</summary>
    public static List<long> Timestamps(string path, int track)
    {
        uint stream = StreamOf(path, track);
        IMFSourceReader* reader = Open(path, stream, decoded: false);
        try
        {
            var times = new List<long>();
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
                times.Add(ts);
                sample->Release();
            }
            return times;
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

    public static double Rms(float[] x) => Rms(x, 0, x.Length);

    /// <summary>RMS of <paramref name="length"/> samples from <paramref name="start"/> (what lies beyond the end counts as nothing).</summary>
    public static double Rms(float[] x, int start, int length)
    {
        int n = Math.Min(length, x.Length - start);
        if (n <= 0) return 0;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += (double)x[start + i] * x[start + i];
        return Math.Sqrt(sum / n);
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
