using System.IO;
using Clink.Audio;
using Clink.Engine;
using Clink.Playback;
using static Clink.Tests.MixRenderTests;

namespace Clink.Tests;

/// <summary>
/// Where the tracks of a clip sit against each other, through everything that can happen to a clip: a cut, a render, a second cut, a
/// clip an older Clink trimmed (its edit lists lost the places), the preview. A source taken out of the Mix by adding it again with a
/// negative gain only cancels when the two are in line to the sample, so these tests measure the sound, not the bookkeeping.
/// </summary>
internal static class AlignmentTests
{
    private static readonly string[] FourTracks = { "Mix", "Game", "Mic", "Desktop" };
    private static readonly float[] GameOff = { 1, 0, 1, 1 };

    /// <summary>How much of <paramref name="x"/>'s <paramref name="hz"/> tone there is, as a share of <paramref name="level"/>.</summary>
    private static double Share(float[] x, double hz, double level) => TestAudio.Amplitude(x, 48000, hz) / level;

    /// <summary>
    /// Not a test: with CLINK_ALIGN_FILES set to clip paths (separated by ;) it prints, for each clip, whether it is marked, where its
    /// tracks sit and where each of its Game / Mic / Voice sources is against the Mix by correlation (the lag to add to the source's
    /// place, the strength of the peak, how much it stands out from its rival, and whether it counts as found), for looking at a real
    /// clip of an older Clink. The files are only read.
    /// </summary>
    [UnitTest]
    public static void Alignment_of_clips_is_reported_when_asked_for()
    {
        string? files = Environment.GetEnvironmentVariable("CLINK_ALIGN_FILES");
        if (string.IsNullOrWhiteSpace(files)) return;
        OnMta(() =>
        {
            foreach (string path in files.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var tracks = Trimmer.ReadClipTracks(path);
                if (tracks == null) { Console.WriteLine($"  {Path.GetFileName(path)}: not a Clink file"); continue; }
                var streams = Trimmer.AudioStreamsOf(path);
                var names = tracks.Audio.Select(t => t.Name).ToArray();
                var offs = tracks.Audio.Select(t => t.StartFrames).ToArray();
                Console.WriteLine($"  {Path.GetFileName(path)}: marked {tracks.Marked}; tracks {string.Join(", ", names.Select((n, i) => $"{n}@{offs[i]}"))}");
                for (int i = 1; i < names.Length; i++)
                {
                    if (names[i] is not ("Game" or "Mic" or "Voice")) continue;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var e = TrackAlignment.Measure(path, streams[0], offs[0], streams[i], offs[i]);
                    Console.WriteLine($"      {names[i],-6} against the Mix: reliable {e.Reliable} lag {e.Lag} peak {e.Peak:0.000} ratio {e.Ratio:0.00} \"{e.Why}\" ({sw.ElapsedMilliseconds} ms)");
                }
            }
        });
    }

    [UnitTest]
    public static void Every_track_is_read_from_its_first_sample_whatever_its_edit_list_says()
    {
        using var dir = new TempDir();
        string clip = dir.File("starts.mp4");
        OnMta(() =>
        {
            // Clink's own reader (TestAudio reads as the trimmer, the player and the check against the Mix do, through ClipSource): every
            // audio stream's first sample is at 0 and the track's offset puts it where it belongs, on every Windows. Media Foundation does not
            // hand the tracks out that way by itself. Windows Server 2025 applies the edit lists; Windows 11 ignores them, but drops the
            // start of the media when an edit after an empty one has a media time (a late start made up of a whole number of milliseconds
            // and a skip of the rest): the timestamps of the track's later packets come out early by the skip, and the track plays that many
            // samples short. So Clink hides the audio edit lists from Media Foundation (ClipSource), whatever forms the muxer writes them in.
            int[] starts = { -2000, -480, -37, 0, 1, 19, 47, 48, 49, 95, 480, 1023, 1024, 1200 };
            var tracks = starts.Select(s => ($"T{s}", s, 1f)).ToArray();
            SyntheticClip.WriteSame(clip, 3, tracks, out _);
            var offsets = TestAudio.Offsets(clip);
            long lengths0 = TestAudio.Decode(clip, 4).Length;
            for (int i = 0; i < starts.Length; i++)
            {
                var ts = TestAudio.Timestamps(clip, i);
                Check.Equal(0L, ts[0], $"track at {starts[i]}: first packet");
                Check.Near(213_333, ts[1], 1, $"track at {starts[i]} (read back at {offsets[i]}): second packet");
                Check.Near(426_667, ts[2], 1, $"track at {starts[i]}: third packet");
            }
        });
    }

    [UnitTest]
    public static void Tracks_keep_their_places_through_a_cut_a_render_and_a_second_cut()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4"), cut = dir.File("cut.mp4"), rendered = dir.File("rendered.mp4"), again = dir.File("again.mp4");
        OnMta(() =>
        {
            // One sound on five tracks that start at five places between the frames of one another's grids.
            var starts = new (string, int, float)[] { ("Mix", -480, 1), ("Game", 0, 1), ("Mic", 19, 1), ("Voice", -37, 1), ("Desktop", 1200, 1) };
            SyntheticClip.WriteSame(clip, 8, starts, out _);
            var offsets = TestAudio.Offsets(clip);
            Check.Equal(new long[] { -480, 0, 19, -37, 1200 }, offsets);
            Check.True(Trimmer.ReadClipTracks(clip)!.Marked);

            // A plain cut from the keyframe at second 1: the frames are copied, the places are kept.
            Trimmer.Trim(clip, cut, 1, 6, null, null);
            var cutOffsets = TestAudio.Offsets(cut);
            Check.True(Trimmer.ReadClipTracks(cut)!.Marked, "the cut says its places are true");
            for (int i = 0; i < 5; i++)
                Check.Equal(0L, ((cutOffsets[i] + 48000 - offsets[i]) % 1024 + 1024) % 1024, $"track {i + 1} lost only whole frames at its start (it was at {offsets[i]}, now at {cutOffsets[i]})");
            AssertInLine(clip, cut, 48000, "after a plain cut");

            // One track rendered again at half volume (the others are copied): the render is on the timeline, not on the first frame.
            var plan = Enumerable.Repeat(new TrackMix(true, 1f), 5).ToArray();
            plan[1] = new TrackMix(true, .5f);
            plan[4] = new TrackMix(true, 2f);
            Trimmer.Trim(clip, rendered, 1, 6, null, plan);
            Check.True(Trimmer.ReadClipTracks(rendered)!.Marked);
            AssertInLine(clip, rendered, 48000, "after a render");
            double half = TestAudio.Rms(TestAudio.Timeline(rendered, 1), 40000, 30000) / TestAudio.Rms(TestAudio.Timeline(rendered, 3), 40000, 30000);
            Check.Near(.5, half, .06, "the rendered Game is half as loud as the copied Voice");

            // The cut of a cut: the places went through the file once already.
            Trimmer.Trim(rendered, again, 1, 4, null, null);
            Check.True(Trimmer.ReadClipTracks(again)!.Marked);
            AssertInLine(rendered, again, 48000, "after a second cut");
        });
    }

    /// <summary>Every track of <paramref name="cut"/> has the sound of the same track of <paramref name="whole"/> at the same moment (the cut starts <paramref name="shift"/> samples into it), and the tracks of the cut are in line with each other.</summary>
    private static void AssertInLine(string whole, string cut, int shift, string when)
    {
        int tracks = TestAudio.TrackCount(cut);
        var before = Enumerable.Range(0, tracks).Select(i => TestAudio.Timeline(whole, i)).ToArray();
        var after = Enumerable.Range(0, tracks).Select(i => TestAudio.Timeline(cut, i)).ToArray();
        for (int i = 0; i < tracks; i++)
        {
            int lag = TestAudio.BestLag(before[i], shift + 30000, after[i], 30000, 30000, 4);
            Check.Equal(0, lag, $"{when}: track {i + 1} against the same moment of the whole clip");
            int between = TestAudio.BestLag(after[1], 30000, after[i], 30000, 30000, 4);
            Check.Equal(0, between, $"{when}: track {i + 1} against Game");
        }
    }

    [UnitTest]
    public static void Delta_mode_removes_the_game_from_a_clip_that_was_trimmed_first()
    {
        using var dir = new TempDir();
        string clip = dir.File("four.mp4"), first = dir.File("first.mp4"), second = dir.File("second.mp4");
        OnMta(() =>
        {
            // No Voice track (the default setting): the Mix still holds the voice, and the game comes out of it again by adding the
            // Game track with a negative gain. That only works when the two tracks are in line, also after the clip was cut once.
            SyntheticClip.Write(clip, 10, voiceTrack: false);
            Trimmer.Trim(clip, first, 1, 9, FourTracks, null);
            var plan = PlanFor(FourTracks, GameOff);
            Check.Equal(new[] { new MixInput(0, 1f), new MixInput(1, -1f) }, plan[0].Sum!);
            Trimmer.Trim(first, second, 1, 6, FourTracks, plan);

            var mix = TestAudio.Decode(second, 0);
            double trace = Share(mix, SyntheticClip.GameHz, SyntheticClip.Level);
            double residue = TestAudio.Correlation(TestAudio.Timeline(second, 0), TestAudio.Timeline(first, 1), 48000, 2 * 48000, 3 * 48000, 2);
            Console.WriteLine($"  (the game left in All audio after a cut and a delta: its tone at {trace:P1} of what it was, noise correlation {residue:0.000})");
            Check.True(trace < .03, $"the game's tone is gone (left: {trace:P1})");
            Check.True(residue < .15, $"and so is its noise (correlation {residue:0.000})");
            ExpectShare(mix, SyntheticClip.MicHz, 1, "the mic stays");
            ExpectShare(mix, SyntheticClip.VoiceHz, 1, "the voice, which has no track of its own, stays");
            Check.True(Trimmer.ReadClipTracks(second)!.Marked);
        });
    }

    private static void ExpectShare(float[] track, double hz, double share, string what)
        => Check.Near(share, Share(track, hz, SyntheticClip.Level), .07, $"{what}: level of the {hz:0} Hz tone");

    [UnitTest]
    public static void A_clip_trimmed_by_an_older_Clink_is_lined_up_again_before_a_source_is_taken_out_of_it()
    {
        using var dir = new TempDir();
        string truth = dir.File("truth.mp4"), old = dir.File("old.mp4"), output = dir.File("out.mp4"), plain = dir.File("plain.mp4");
        OnMta(() =>
        {
            // Sources that are mostly noise (a tone alone repeats itself and cannot be lined up by correlation, which is the next test).
            // "old" is the same recording whose Game frames were written 400 samples (8 ms) late and without the mark: what a trim by
            // a Clink before the mark did to the places.
            SyntheticClip.Write(truth, 8, voiceTrack: false, level: .1f, noiseScale: 6);
            SyntheticClip.Write(old, 8, voiceTrack: false, level: .1f, noiseScale: 6, stampLag: new long[] { 400, 0, 0, 0 }, aligned: false);
            var right = ClipAlignment.Read(truth);
            Check.True(right.Marked, "the recording is marked, so its places are trusted as they are");
            Check.False(right.Calibrated, "and not looked at");
            var rawOld = TestAudio.Offsets(old);
            var found = ClipAlignment.Read(old);
            Console.WriteLine($"  (older clip: file says {string.Join(", ", rawOld)}; lined up {string.Join(", ", found.OffsetFrames)}; recorded at {string.Join(", ", right.OffsetFrames)})");
            Check.False(found.Marked, "no mark");
            Check.True(found.Calibrated, "the sources were checked against the Mix");
            Check.Equal(0, found.Unlined.Length, "and all of them could be lined up");
            Check.True(found.Aligned);
            Check.Equal(right.OffsetFrames[0], found.OffsetFrames[0], "the Mix stays where the file has it");
            Check.Equal(right.OffsetFrames[1], found.OffsetFrames[1], "Game is back where it was recorded");
            Check.Near(right.OffsetFrames[2], found.OffsetFrames[2], 1, "Mic is where it was recorded");

            // The game comes out of the Mix, which it only does when the two are in line.
            Trimmer.Trim(old, output, 1, 5, FourTracks, PlanFor(FourTracks, GameOff));
            double residue = TestAudio.Correlation(TestAudio.Timeline(output, 0), TestAudio.Timeline(truth, 1), 48000, 2 * 48000, 3 * 48000, 2);
            double before = TestAudio.Correlation(TestAudio.Timeline(truth, 0), TestAudio.Timeline(truth, 1), 48000, 3 * 48000, 3 * 48000, 2);
            Console.WriteLine($"  (game noise in the Mix: {before:0.000} before, {residue:0.000} after the delta)");
            Check.True(before > .3, $"the game is in the Mix to begin with ({before:0.000})");
            Check.True(residue < .15, $"the game's noise is cancelled out (correlation {residue:0.000})");
            Check.True(Trimmer.ReadClipTracks(output)!.Marked, "the cut writes the places it found, and says so");

            // A plain cut of the older clip writes the lined-up places too: a clip repaired once is not checked again.
            Trimmer.Trim(old, plain, 1, 5, FourTracks, null);
            Check.True(Trimmer.ReadClipTracks(plain)!.Marked);
            var cut = TestAudio.Offsets(plain);
            long gameAgainstMix = cut[1] - cut[0], recorded = right.OffsetFrames[1] - right.OffsetFrames[0];
            Check.Equal(0L, ((gameAgainstMix - recorded) % 1024 + 1024) % 1024, $"Game sits against the Mix as it was recorded, up to whole frames ({gameAgainstMix} against {recorded})");
        });
    }

    [UnitTest]
    public static void A_source_that_cannot_be_found_in_the_Mix_is_left_out_of_it_and_the_clip_says_why()
    {
        using var dir = new TempDir();
        string old = dir.File("old.mp4"), output = dir.File("out.mp4"), refused = dir.File("refused.mp4");
        OnMta(() =>
        {
            // Pure tones: the Mix holds the game, but a tone correlates with the Mix at every period, so no lag is clear.
            SyntheticClip.Write(old, 8, voiceTrack: false, stampLag: new long[] { 400, 0, 0, 0 }, aligned: false);
            var found = ClipAlignment.Read(old);
            Console.WriteLine($"  (tones only: unlined tracks {string.Join(", ", found.Unlined)})");
            Check.False(found.Marked);
            Check.True(found.Unlined.Contains(1), "Game could not be lined up");
            Check.False(found.Aligned, "so nothing is vouched for");

            // The editor's plan without it: no source is left to change All audio with, so Game's slider is for its own track.
            var roles = MixPlan.WithoutTracks(MixPlan.Roles(FourTracks, 4), found.Unlined);
            Check.Equal(TrackRole.Other, roles[1]);
            var plan = MixPlan.Export(roles, new[] { true, true, true, true }, GameOff);
            Check.Null(plan[0].Sum, "All audio is copied");
            Trimmer.Trim(old, output, 1, 5, FourTracks, plan);
            Check.True(TestAudio.IsSlice(TestAudio.Frames(old, 0), TestAudio.Frames(output, 0)), "the Mix is the very same frames");
            Check.True(TestAudio.Rms(TestAudio.Decode(output, 1)) < .002, "Game's own track is silent, as its slider said");
            Check.False(Trimmer.ReadClipTracks(output)!.Marked, "and the cut does not say its places are true");

            // The plan made without that knowledge would add the game to the Mix a few hundred samples off: refused.
            var raw = MixPlan.Export(MixPlan.Roles(FourTracks, 4), new[] { true, true, true, true }, GameOff);
            Check.NotNull(raw[0].Sum, "the unchecked plan subtracts the game");
            var ex = Check.Throws<InvalidOperationException>(() => Trimmer.Trim(old, refused, 1, 5, FourTracks, raw));
            Check.Contains(ex.Message, "Recorded or trimmed by an older Clink");
            Check.False(File.Exists(refused), "nothing was written");
        });
    }

    [UnitTest]
    public static void A_clip_with_all_its_sources_is_rebuilt_whatever_its_places_are()
    {
        using var dir = new TempDir();
        string old = dir.File("old.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            // The Mix is not used at all when all three sources are there, so an older clip needs no checking; its sources are
            // far apart in the file and still give the same sound.
            string[] names = { "Mix", "Game", "Mic", "Voice", "Desktop" };
            SyntheticClip.Write(old, 6, stampLag: new long[] { 400, -300, 150, 0 }, aligned: false);
            var found = ClipAlignment.Read(old);
            Check.False(found.Marked);
            Check.False(found.Calibrated, "nothing to check");
            Check.Equal(0, found.Unlined.Length);
            Trimmer.Trim(old, output, 1, 5, names, PlanFor(names, new float[] { 1, 0, 1, 1, 1 }));
            var mix = TestAudio.Decode(output, 0);
            Check.True(Share(mix, SyntheticClip.GameHz, SyntheticClip.Level) < .02, "no game in All audio");
            ExpectShare(mix, SyntheticClip.MicHz, 1, "All audio");
            Check.False(Trimmer.ReadClipTracks(output)!.Marked, "the places of an older clip are not vouched for");
        });
    }

    [UnitTest]
    public static void The_lag_of_a_source_in_the_Mix_is_found_to_the_sample_and_a_tone_is_refused()
    {
        using var dir = new TempDir();
        string noisy = dir.File("noisy.mp4"), tones = dir.File("tones.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(noisy, 8, voiceTrack: false, level: .1f, noiseScale: 6);
            var offsets = TestAudio.Offsets(noisy);
            uint mix = TestAudio.StreamOf(noisy, 0);
            double weakestPeak = 1, weakestRatio = 999;
            foreach (int source in new[] { 1, 2 })
            {
                uint stream = TestAudio.StreamOf(noisy, source);
                foreach (int wrong in new[] { -9000, -1100, -37, -1, 0, 1, 384, 2487, 9000 })
                {
                    var e = TrackAlignment.Measure(noisy, mix, offsets[0], stream, offsets[source] + wrong);
                    Check.True(e.Reliable, $"track {source + 1} placed {wrong} off is found ({e.Why})");
                    Check.Equal((long)-wrong, e.Lag, $"track {source + 1} placed {wrong} samples off");
                    weakestPeak = Math.Min(weakestPeak, e.Peak);
                    weakestRatio = Math.Min(weakestRatio, e.Ratio);
                }
            }
            Console.WriteLine($"  (lags from -9000 to 9000 found to the sample; the weakest peak {weakestPeak:0.000}, the least clear {weakestRatio:0.00}x its rival)");
            var beyond = TrackAlignment.Measure(noisy, mix, offsets[0], TestAudio.StreamOf(noisy, 1), offsets[1] + 20000);
            Check.False(beyond.Reliable, "a lag outside the 0.3 s searched is not invented");

            SyntheticClip.Write(tones, 8, voiceTrack: false);
            var toneOffsets = TestAudio.Offsets(tones);
            var tone = TrackAlignment.Measure(tones, TestAudio.StreamOf(tones, 0), toneOffsets[0], TestAudio.StreamOf(tones, 1), toneOffsets[1] + 400);
            Console.WriteLine($"  (a pure tone: peak {tone.Peak:0.000}, {tone.Ratio:0.00}x its rival, \"{tone.Why}\")");
            Check.False(tone.Reliable, "a pure tone repeats itself: no lag is clear");
        });
    }

    [UnitTest]
    public static void A_source_with_nothing_in_it_needs_no_lining_up()
    {
        using var dir = new TempDir();
        string clip = dir.File("quiet.mp4");
        OnMta(() =>
        {
            // The Game track is silent all through (a game that makes no sound): there is nothing to take out of the Mix.
            SyntheticClip.WriteSame(clip, 6, new (string, int, float)[] { ("Mix", -480, 1), ("Game", 0, 0) }, out _, aligned: false);
            var offsets = TestAudio.Offsets(clip);
            var e = TrackAlignment.Measure(clip, TestAudio.StreamOf(clip, 0), offsets[0], TestAudio.StreamOf(clip, 1), offsets[1]);
            Check.True(e.Reliable, "a silent source is lined up trivially");
            Check.Equal("silent", e.Why);
            var found = ClipAlignment.Read(clip);
            Check.True(found.Calibrated);
            Check.Equal(0, found.Unlined.Length);
        });
    }

    [UnitTest]
    public static void Rendered_frames_are_stamped_at_their_exact_places_however_long_the_render_is()
    {
        // 1875 frames are 40 s: stamping each at a whole number of truncated 213,333 units puts the last one 3 samples early.
        Check.Equal(0L, Trimmer.FramePts(0, 0));
        Check.Equal(213_333L, Trimmer.FramePts(0, 1));
        Check.Equal(640_000L, Trimmer.FramePts(0, 3));
        Check.Equal(400_000_000L, Trimmer.FramePts(0, 1875));
        Check.Equal(1_000_000_000L + 400_000_000L, Trimmer.FramePts(1_000_000_000L, 1875));

        using var dir = new TempDir();
        string clip = dir.File("long.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            SyntheticClip.WriteSame(clip, 42, new (string, int, float)[] { ("Mix", 0, 1), ("Game", 0, 1) }, out _);
            var plan = new[] { new TrackMix(true, 1f), new TrackMix(true, .5f) };
            Trimmer.Trim(clip, output, 0, 41, null, plan);
            var times = TestAudio.Timestamps(output, 1);
            Check.True(times.Count > 1900, $"a long render ({times.Count} frames)");
            long worst = 0;
            for (int k = 0; k < times.Count; k++) worst = Math.Max(worst, Math.Abs(times[k] - Trimmer.FramePts(times[0], k)));
            Console.WriteLine($"  (the rendered track's {times.Count} frames are at most {worst} units (100 ns) off their places)");
            Check.True(worst <= 2, $"no drift over {times.Count} frames (worst {worst} units)");
        });
    }

    [UnitTest]
    public static void A_packet_stamped_early_or_late_by_under_a_millisecond_follows_the_one_before_it()
    {
        using var dir = new TempDir();
        string steady = dir.File("steady.mp4"), jittery = dir.File("jittery.mp4");
        OnMta(() =>
        {
            var track = new (string, int, float)[] { ("Game", 0, 1) };
            SyntheticClip.WriteSame(steady, 5, track, out _);
            SyntheticClip.WriteSame(jittery, 5, track, out _, jitter: 19);   // every second frame 19 samples (0.4 ms) late
            long Frames(string path)
            {
                using var reader = new Trimmer.PcmTrackReader(path, TestAudio.StreamOf(path, 0), 0, 0, 100 * 48000L);
                var block = new float[2 * Trimmer.PcmTrackReader.Block];
                long total = 0;
                int got;
                while ((got = reader.ReadBlock(block)) > 0) total += got;
                return total;
            }
            long a = Frames(steady), b = Frames(jittery);
            Console.WriteLine($"  (a steady stream hands out {a} frames, one whose timestamps jitter {b})");
            Check.Equal(a, b, "no frame was cut out or put in for the jitter, in either direction");
        });
    }

    [UnitTest]
    public static void The_player_puts_every_track_on_the_timeline_by_its_place_to_the_sample()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            var starts = new (string, int, float)[] { ("Mix", -480, 1), ("Game", 0, 1), ("Mic", 19, 1), ("Voice", -37, 1), ("Desktop", 1200, 1) };
            SyntheticClip.WriteSame(clip, 4, starts, out _);
            using var player = new ClipPlayer(clip, 0, headless: true);
            Check.Equal(5, player.TrackCount);
            Check.Equal(new long[] { -480, 0, 19, -37, 1200 }, player.TrackOffsets, "the places the file's edit lists give");
            for (int track = 0; track < 5; track++)
            {
                // Each track alone from the clip's second 1: what the player mixes is what the decoder gives on the clip's timeline.
                for (int j = 0; j < 5; j++)
                {
                    player.Gain[j] = 1f;
                    player.Muted[j] = j != track;
                }
                var heard = player.RenderAudio(1.0, 24000);
                var expected = TestAudio.Timeline(clip, track);
                double worst = 0;
                for (int i = 1000; i < 23000; i++) worst = Math.Max(worst, Math.Abs(heard[i * 2] - expected[48000 + i]));
                Check.True(worst < 1e-5, $"{starts[track].Item1}: what the player plays is the track on the timeline, sample for sample (worst difference {worst})");
            }
        });
    }

    [UnitTest]
    public static void The_preview_of_a_clip_that_was_cut_takes_the_game_out_of_All_audio_like_the_export_does()
    {
        using var dir = new TempDir();
        string clip = dir.File("four.mp4"), cut = dir.File("cut.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 8, voiceTrack: false);
            Trimmer.Trim(clip, cut, 1, 7, FourTracks, null);
            using var player = new ClipPlayer(cut, 0, headless: true);
            var roles = MixPlan.Roles(player.TrackNames, player.TrackCount);
            var gains = MixPlan.PreviewGains(roles, new[] { true, true, true, true }, GameOff, 0);
            Check.Equal(new[] { 1f, -1f, 0f, 0f }, gains, "the Mix and minus the Game");
            for (int i = 0; i < gains.Length; i++)
            {
                player.Gain[i] = gains[i];
                player.Muted[i] = gains[i] == 0f;
            }
            var heard = player.RenderAudio(1.0, 3 * 48000);
            var left = new float[heard.Length / 2];
            for (int i = 0; i < left.Length; i++) left[i] = heard[i * 2];
            double game = Share(left, SyntheticClip.GameHz, SyntheticClip.Level);
            Console.WriteLine($"  (the preview of the cut clip: game at {game:P1}, mic at {Share(left, SyntheticClip.MicHz, SyntheticClip.Level):P0}, voice at {Share(left, SyntheticClip.VoiceHz, SyntheticClip.Level):P0})");
            Check.True(game < .03, $"the game is gone from the preview ({game:P1})");
            ExpectShare(left, SyntheticClip.MicHz, 1, "preview");
            ExpectShare(left, SyntheticClip.VoiceHz, 1, "preview");
        });
    }

    [UnitTest]
    public static void A_Mix_put_together_from_its_sources_follows_the_soft_knee_of_the_recording()
    {
        using var dir = new TempDir();
        string clip = dir.File("loud.mp4"), output = dir.File("out.mp4");
        OnMta(() =>
        {
            // The Game is a full-scale square wave and nothing else makes a sound: the recorded Mix is its soft-knee limited self
            // (+-0.95), a flat top where the plain clip (+-1) and the knee differ by 5%.
            string[] five = { "Mix", "Game", "Mic", "Voice", "Desktop" };
            SyntheticClip.Write(clip, 6, level: 1f, noiseScale: 0, wave: (s, n) => s == 0 ? (n % 109 < 55 ? 1f : -1f) : 0f);
            var plan = PlanFor(five, new float[] { 1, 1, 1, 1, 1 });
            plan[0] = new TrackMix(true, 1f, new[] { new MixInput(1, 1f), new MixInput(2, 1f), new MixInput(3, 1f) });
            Trimmer.Trim(clip, output, 1, 5, five, plan);
            double baked = TestAudio.Rms(TestAudio.Timeline(clip, 0), 2 * 48000, 2 * 48000);
            double rebuilt = TestAudio.Rms(TestAudio.Timeline(output, 0), 48000, 2 * 48000);
            double game = TestAudio.Rms(TestAudio.Timeline(clip, 1), 2 * 48000, 2 * 48000);
            Console.WriteLine($"  (RMS of the square wave: Game {game:0.000}, recorded Mix {baked:0.000}, rebuilt Mix {rebuilt:0.000}; the plain clip would give about {Math.Min(game, 1):0.000})");
            Check.True(baked < game - .02, $"the recorded Mix is limited by the knee ({baked:0.000} against {game:0.000})");
            Check.Near(baked, rebuilt, .02, "the rebuilt Mix has the recorded one's loud peaks");
        });
    }

    [UnitTest]
    public static void The_preview_limits_a_mix_of_sources_with_the_knee_and_one_track_with_the_plain_clip()
    {
        using var dir = new TempDir();
        string clip = dir.File("loud.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 4, level: 1f, noiseScale: 0, wave: (s, n) => s == 0 ? (n % 109 < 55 ? 1f : -1f) : 0f);
            using var player = new ClipPlayer(clip, 0, headless: true);
            for (int j = 0; j < player.TrackCount; j++)
            {
                player.Gain[j] = 1f;   // the Game alone at its own level: a full-scale square wave
                player.Muted[j] = j != 1;
            }
            var track = TestAudio.Timeline(clip, 1);
            double apart = 0;
            foreach (bool knee in new[] { false, true })
            {
                player.SoftKnee = knee;
                var heard = player.RenderAudio(1.0, 24000);
                double worst = 0;
                for (int i = 1000; i < 23000; i++)
                {
                    float x = track[48000 + i];
                    float expected = knee ? AudioTrack.SoftKnee(x) : x / MathF.Max(1f, MathF.Abs(x));
                    worst = Math.Max(worst, Math.Abs(heard[i * 2] - expected));
                    apart = Math.Max(apart, Math.Abs(AudioTrack.SoftKnee(x) - x / MathF.Max(1f, MathF.Abs(x))));
                }
                Check.True(worst < 1e-5, $"{(knee ? "the recording's soft knee" : "the plain clip")} is what limits the preview (worst difference {worst})");
            }
            Check.True(apart > .03, $"the two curves differ on this sound, so the check means something ({apart:0.000})");
        });
    }
}
