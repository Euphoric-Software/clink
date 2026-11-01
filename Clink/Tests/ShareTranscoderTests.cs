using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Win32.Foundation;
using Clink.Encode;
using Clink.Engine;
using Clink.Native;

namespace Clink.Tests;

/// <summary>
/// The share-time smaller copy (<see cref="ShareTranscoder"/>), on synthetic clips made like the documentation samples: H.264 from
/// Media Foundation's software encoder (a moving pattern), AAC tracks from its AAC encoder, muxed by the recording's own writer (so the
/// file is a Clink clip: clk1 mark, named tracks, edit lists). The stream copy, the refusals and the frame picking need no GPU and always
/// run (a machine without Media Foundation's encoders skips, as <see cref="MixRenderTests"/> do). The re-encode needs a hardware H.264
/// encoder and decoder and uses the GPU, so it only runs with CLINK_TESTS_GPU=1 (which a developer sets when no game is running);
/// without it those tests pass without doing anything and say so on the console ("not run"), so a CI runner without a GPU stays green.
/// With CLINK_TESTS_GPU=1 a machine that has no hardware encoder or decoder skips them (a failure under CLINK_TESTS_NO_SKIP=1: the
/// variable says there is one).
/// </summary>
internal static class ShareTranscoderTests
{
    private const string GpuVariable = "CLINK_TESTS_GPU";
    internal static bool GpuTestsOn => Environment.GetEnvironmentVariable(GpuVariable) is "1" or "true";

    /// <summary>A GPU test that was not run: on the console (where PASS is read), so a pass is not taken for a check.</summary>
    internal static void NotRun(string test) => Console.WriteLine($"      {test}: NOT RUN, set {GpuVariable}=1 to run it (uses the GPU)");

    /// <summary>Runs <paramref name="body"/> on an MTA thread (Media Foundation); a missing Media Foundation encoder is a skip, a hardware one too.</summary>
    internal static void OnMta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (failure == null) return;
        if (failure is TestSkipped or TestFailure) ExceptionDispatch(failure);
        if (MixRenderTests.SkipReason(failure) is { } why) Check.Skip(why);
        if (failure is InvalidOperationException { Message: "No software H.264 encoder found." }) Check.Skip("Media Foundation has no software H.264 encoder on this machine");
        if (failure is ShareTranscodeException { Kind: ShareTranscodeFailure.NoHardwareEncoder or ShareTranscodeFailure.DecoderMissing } hw) Check.Skip($"{GpuVariable}=1 but {hw.Message}");
        throw new TestFailure(failure.ToString());
    }

    private static void ExceptionDispatch(Exception ex) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

    /// <summary>An IProgress that calls back on the reporting thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class Direct : IProgress<double>
    {
        private readonly Action<double> _on;
        public readonly List<double> Seen = new();
        public Direct(Action<double>? on = null) => _on = on ?? (_ => { });
        public void Report(double value) { lock (Seen) Seen.Add(value); _on(value); }
    }

    // ------------------------------------------------------------------ the synthetic clip

    private const long Origin = 1_000_000_000L;

    /// <summary>
    /// A Clink clip of <paramref name="seconds"/> at <paramref name="width"/>x<paramref name="height"/>@<paramref name="fps"/> with one AAC
    /// track per name (the first starts 10 ms before the video, as a recording's Mix does, the others later; none = a clip without audio),
    /// a keyframe every 2 s.
    /// </summary>
    internal static string MakeClip(TempDir dir, string name, int width, int height, uint fps, double seconds, params string[] tracks)
    {
        string path = dir.File(name);
        int frames = (int)Math.Round(seconds * fps);
        var nv12 = new byte[width * height * 3 / 2];
        List<VideoSample> video;
        byte[]? seq;
        using (var enc = new SoftwareH264Encoder(width, height, fps, 4_000_000, gopSeconds: 2.0))
        {
            for (int f = 0; f < frames; f++)
            {
                Pattern(nv12, width, height, f);
                enc.Encode(nv12, Origin + (long)Math.Round(f * 10_000_000.0 / fps));
            }
            video = enc.Finish();
            seq = enc.SequenceHeader;
        }
        var audio = new List<(AudioTrackDesc desc, List<Audio.AacFrame> frames)>();
        long end = Origin + (long)(seconds * 10_000_000);
        long block = AacEncoder.FrameSamples * 10_000_000L / 48000;
        for (int i = 0; i < tracks.Length; i++)
        {
            using var enc = new AacEncoder(48000, 2, 24000);
            var list = new List<Audio.AacFrame>();
            long first = Origin + (i == 0 ? -100_000 : 250_000 * i);
            var pcm = new byte[AacEncoder.FrameSamples * 4];
            double hz = 440 * (i + 1);
            for (long k = 0; first + k * block <= end; k++)
            {
                for (int s = 0; s < AacEncoder.FrameSamples; s++)
                {
                    long n = k * AacEncoder.FrameSamples + s;
                    short v = (short)Math.Round(0.2 * 32767 * Math.Sin(2 * Math.PI * hz * (n % 48000) / 48000));
                    pcm[s * 4] = pcm[s * 4 + 2] = (byte)v;
                    pcm[s * 4 + 1] = pcm[s * 4 + 3] = (byte)(v >> 8);
                }
                enc.Encode(pcm, first + k * block, (pts, data) => list.Add(new Audio.AacFrame(pts, data)));
            }
            list = list.Where(f => f.Pts + block > Origin).ToList();
            audio.Add((new AudioTrackDesc { Name = tracks[i], AudioSpecificConfig = enc.AudioSpecificConfig, Bitrate = enc.BytesPerSecond * 8, SampleRate = 48000, Channels = 2 }, list));
        }
        ClipWriter.Write(path, video, "h264", width, height, seq, audio, Origin, fps);
        return path;
    }

    /// <summary>A moving diagonal pattern with a sliding box: enough motion and detail that the encoder has something to spend bits on.</summary>
    private static void Pattern(byte[] nv12, int width, int height, int f)
    {
        int bx = (f * 7) % Math.Max(1, width - 64), by = (f * 3) % Math.Max(1, height - 64);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                bool box = x >= bx && x < bx + 64 && y >= by && y < by + 64;
                nv12[row + x] = box ? (byte)235 : (byte)(16 + (((x + 2 * f) ^ (y + f)) & 0x7F) + ((x * y) & 0x3F));
            }
        }
        int uv = width * height;
        for (int y = 0; y < height / 2; y++)
            for (int x = 0; x < width / 2; x++)
            {
                nv12[uv + y * width + 2 * x] = (byte)(128 + ((x + f) & 31) - 16);
                nv12[uv + y * width + 2 * x + 1] = (byte)(128 + ((y - f) & 31) - 16);
            }
    }

    private static (string hash, DateTime written, long length) Fingerprint(string path)
    {
        using var fs = File.OpenRead(path);
        return (Convert.ToHexString(SHA256.HashData(fs)), File.GetLastWriteTimeUtc(path), new FileInfo(path).Length);
    }

    private static string[] Leftovers(string folder, string except)
        => Directory.GetFiles(folder).Where(f => !string.Equals(f, except, StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToArray()!;

    /// <summary>How many of <paramref name="frames"/> source frames at <paramref name="srcFps"/> (stamped as the synthetic clips are:
    /// round(k / fps) in 100 ns) the transcoder keeps for <paramref name="outFps"/>: the same rule it uses, so a count is exact.</summary>
    private static int ExpectedKept(long frames, double srcFps, int outFps)
        => Kept(Enumerable.Range(0, checked((int)frames)).Select(k => (long)Math.Round(k * 10_000_000.0 / srcFps)), srcFps, outFps);

    /// <summary>Applies <see cref="ShareTranscoder.Keep"/> to <paramref name="stamps"/> with the first one as the origin, as the transcoder does.</summary>
    private static int Kept(IEnumerable<long> stamps, double srcFps, int outFps)
    {
        long last = -1, first = long.MinValue;
        int kept = 0;
        foreach (long ts in stamps)
        {
            if (first == long.MinValue) first = ts;
            if (ShareTranscoder.Keep(ts, first, outFps, srcFps, ref last)) kept++;
        }
        return kept;
    }

    // ------------------------------------------------------------------ pure

    [UnitTest]
    public static void Frames_are_picked_by_timestamp_for_a_lower_frame_rate()
    {
        Check.Equal(300, ExpectedKept(600, 60, 30), "60 -> 30 keeps every other frame");
        Check.Equal(600, ExpectedKept(600, 60, 60), "60 -> 60 keeps all");
        Check.Equal(300, ExpectedKept(300, 30, 60), "a lower source rate keeps every frame");
        Check.Equal(300, ExpectedKept(1440, 144, 30), "144 -> 30: one per 30 fps slot over 10 s");
        Check.Equal(600, ExpectedKept(1440, 144, 60), "144 -> 60");
        Check.Equal(300, ExpectedKept(1200, 120, 30), "120 -> 30");
        // 600 frames at 59.94 span 10.01 s: 301 slots of 1/30 s each get one frame.
        Check.Equal(301, ExpectedKept(600, 59.94, 30), "59.94 -> 30 keeps one frame per 30 fps slot");
        Check.Equal(600, ExpectedKept(600, 59.94, 60), "59.94 -> 60 keeps every frame");
        Check.Equal(600, ExpectedKept(600, 60_000.0 / 1001, 60), "60000/1001 -> 60 keeps every frame");

        // A remux on a 1 ms timescale (Game Bar, phones, most editors): 60 fps stamped 0, 17, 33, 50, 67 ms...
        var ms = Enumerable.Range(0, 600).Select(k => (long)Math.Round(k * 1000.0 / 60) * 10_000).ToArray();
        Check.Equal(600, Kept(ms, 60, 60), "60 -> 60 on a 1 ms timescale keeps all 600");
        Check.Equal(300, Kept(ms, 60, 30), "60 -> 30 on a 1 ms timescale keeps every other frame");
        var ms30 = Enumerable.Range(0, 300).Select(k => (long)Math.Round(k * 1000.0 / 30) * 10_000).ToArray();
        Check.Equal(300, Kept(ms30, 30, 30), "30 -> 30 on a 1 ms timescale keeps all");

        // A variable-rate capture: each frame up to 2 ms early or late (worst case: alternating, and a fixed pattern).
        var alternating = Enumerable.Range(0, 600).Select(k => (long)Math.Round(k * 10_000_000.0 / 60) + (k % 2 == 0 ? 20_000 : -20_000)).ToArray();
        Check.Equal(600, Kept(alternating, 60, 60), "60 -> 60 with +-2 ms jitter keeps all");
        Check.Equal(300, Kept(alternating, 60, 30), "60 -> 30 with +-2 ms jitter keeps every other frame");
        var pattern = Enumerable.Range(0, 600).Select(k => (long)Math.Round(k * 10_000_000.0 / 60) + ((k * 7) % 5 - 2) * 10_000).ToArray();
        Check.Equal(600, Kept(pattern, 60, 60), "60 -> 60 with 0..2 ms jitter keeps all");
        Check.Equal(300, Kept(pattern, 60, 30), "60 -> 30 with 0..2 ms jitter keeps every other frame");
        var early = Enumerable.Range(0, 600).Select(k => (long)Math.Round(k * 10_000_000.0 / 60) - (k == 0 ? 0 : 20_000)).ToArray();
        Check.Equal(600, Kept(early, 60, 60), "every frame after the first 2 ms early still opens its slot");

        long l = -1;
        Check.False(ShareTranscoder.Keep(-1, 0, 30, 60, ref l), "a frame before the first is not kept");
        l = -1;
        Check.True(ShareTranscoder.Keep(0, 0, 30, 0, ref l), "an unknown source rate still keeps the first frame");
    }

    [UnitTest]
    public static void The_copy_is_snapped_to_the_frame_grid_only_when_it_drops_frames()
    {
        Check.Equal(30u, ShareTranscoder.MuxGridFps(60, 30), "60 -> 30: one kept frame per slot");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(60, 60), "60 -> 60 keeps every frame, so it keeps the source's times");
        Check.Equal(30u, ShareTranscoder.MuxGridFps(144, 30));
        Check.Equal(30u, ShareTranscoder.MuxGridFps(60_000.0 / 1001, 30), "59.94 -> 30 fills every slot");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(60_000.0 / 1001, 60), "59.94 -> 60 keeps its own times (no slot skipped every 17 s)");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(30_000.0 / 1001, 30), "29.97 -> 30 too");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(30, 60), "a slower source too");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(60.005, 60), "a rate a hair above the output's counts as the same");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(60.02, 60), "a rate within 2% above the output's counts as the same");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(61.2, 60), "exactly 2% above still keeps every frame");
        Check.Equal(60u, ShareTranscoder.MuxGridFps(61.3, 60), "a rate more than 2% above the output's drops frames, so it is snapped");
        Check.Equal(60u, ShareTranscoder.MuxGridFps(72, 60), "72 -> 60 drops frames");
        Check.Equal(0u, ShareTranscoder.MuxGridFps(0, 30), "an unknown rate keeps the times");
    }

    /// <summary>What the muxer makes of <paramref name="stamps"/> for a copy snapped to <paramref name="gridFps"/> (0 = not snapped):
    /// the real <see cref="ClipMuxWriter"/> timestamp rule (its private Snap, then _prev as AddVideo sets it). Nothing is written.</summary>
    private static long[] Muxed(IReadOnlyList<long> stamps, uint gridFps, long origin)
    {
        var snap = typeof(ClipMuxWriter).GetMethod("Snap", BindingFlags.NonPublic | BindingFlags.Instance);
        var prev = typeof(ClipMuxWriter).GetField("_prev", BindingFlags.NonPublic | BindingFlags.Instance);
        Check.NotNull(snap, "ClipMuxWriter.Snap (the test drives the muxer's own rule)");
        Check.NotNull(prev, "ClipMuxWriter._prev");
        using var w = new ClipMuxWriter(ClipOutput.FastStart(Path.Combine(Path.GetTempPath(), "clink-mux-rule-never-written.mp4")),
            new ClipVideoConfig("h264", 64, 64, gridFps), Array.Empty<AudioTrackDesc>(), origin);
        var outStamps = new long[stamps.Count];
        for (int i = 0; i < stamps.Count; i++)
        {
            long q = (long)snap!.Invoke(w, new object[] { stamps[i] })!;
            prev!.SetValue(w, q);
            outStamps[i] = q;
        }
        return outStamps;
    }

    /// <summary>The stamps <see cref="ShareTranscoder.Keep"/> keeps, with the first one as the origin, as the transcoder does.</summary>
    private static List<long> KeptStamps(IReadOnlyList<long> stamps, double srcFps, int outFps)
    {
        long last = -1, first = stamps[0];
        var kept = new List<long>();
        foreach (long ts in stamps)
            if (ShareTranscoder.Keep(ts, first, outFps, srcFps, ref last)) kept.Add(ts);
        return kept;
    }

    private static bool StrictlyIncreasing(long[] s)
    {
        for (int i = 1; i < s.Length; i++)
            if (s[i] <= s[i - 1]) return false;
        return true;
    }

    [UnitTest]
    public static void A_source_at_or_just_below_the_output_rate_keeps_every_one_of_6000_frames()
    {
        // 6000 frames each: a 59.94 source drifts a slot against the 60 grid about every 1000 frames, and the first frame the old
        // slotting dropped was number 473.
        const int N = 6000;
        double ntsc60 = 60_000.0 / 1001, ntsc30 = 30_000.0 / 1001;
        var rng = new Random(20261101);   // fixed seed: the same jitter on every run
        var rng2 = new Random(1337);
        var rng3 = new Random(60_02);
        var rng4 = new Random(60_3);
        var cases = new (string what, long[] stamps, double headerFps, int outFps)[]
        {
            ("59.94 -> 60 on a 1 ms timescale", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 1000.0 / ntsc60) * 10_000).ToArray(), ntsc60, 60),
            ("29.97 -> 30 on a 1 ms timescale", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 1000.0 / ntsc30) * 10_000).ToArray(), ntsc30, 30),
            ("59.94 -> 60 with +-2 ms alternating jitter", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 10_000_000.0 / ntsc60) + (k % 2 == 0 ? 20_000 : -20_000)).ToArray(), ntsc60, 60),
            ("59.94 -> 60 with random +-2 ms jitter", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 10_000_000.0 / ntsc60) + rng2.Next(-20_000, 20_001)).ToArray(), ntsc60, 60),
            ("a 60 fps header on a 59.9 fps clip with random +-2 ms jitter", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 10_000_000.0 / 59.9) + rng.Next(-20_000, 20_001)).ToArray(), 60, 60),
            ("a 60.02 header on a 59.9 fps clip with random +-2 ms jitter", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 10_000_000.0 / 59.9) + rng3.Next(-20_000, 20_001)).ToArray(), 60.02, 60),
            ("a 60.3 fps clip with random +-2 ms jitter", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 10_000_000.0 / 60.3) + rng4.Next(-20_000, 20_001)).ToArray(), 60.3, 60),
            ("60 -> 60 on a 1 ms timescale", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 1000.0 / 60) * 10_000).ToArray(), 60, 60),
            ("30 -> 60 (a slower source)", Enumerable.Range(0, N).Select(k => (long)Math.Round(k * 10_000_000.0 / 30)).ToArray(), 30, 60),
        };
        foreach (var (what, raw, headerFps, outFps) in cases)
        {
            var stamps = raw.Select(t => Origin + t).ToArray();
            Check.True(ShareTranscoder.KeepsEveryFrame(headerFps, outFps), what + ": counts as keeping every frame");
            var kept = KeptStamps(stamps, headerFps, outFps);
            Check.Equal(N, kept.Count, what + ": every frame is kept");
            uint grid = ShareTranscoder.MuxGridFps(headerFps, outFps);
            Check.Equal(0u, grid, what + ": not snapped to the grid");
            long[] muxed = Muxed(kept, grid, stamps[0]);
            Check.True(StrictlyIncreasing(muxed), what + ": the muxed timestamps are strictly increasing");
            Check.Equal(stamps, muxed, what + ": the muxer keeps the source's own times");
        }
    }

    [UnitTest]
    public static void A_faster_source_keeps_exactly_one_frame_per_output_slot_over_6000_frames()
    {
        // The ideal count: the output slots the source spans, ceil(6000 * out / src).
        const int N = 6000;
        foreach (var (src, outFps, ideal) in new (double, int, int)[] { (60, 30, 3000), (144, 60, 2500), (165, 60, 2182), (90, 30, 2000), (75, 30, 2400), (50, 30, 3600) })
        {
            Check.Equal(ideal, (int)Math.Ceiling(N * outFps / src - 1e-9), $"{src} -> {outFps}: the ideal count");
            Check.False(ShareTranscoder.KeepsEveryFrame(src, outFps), $"{src} -> {outFps} drops frames");
            var exact = Enumerable.Range(0, N).Select(k => Origin + (long)Math.Round(k * 10_000_000.0 / src)).ToArray();
            var ms = Enumerable.Range(0, N).Select(k => Origin + (long)Math.Round(k * 1000.0 / src) * 10_000).ToArray();
            foreach (var (what, stamps) in new[] { ("exact", exact), ("1 ms timescale", ms) })
            {
                var kept = KeptStamps(stamps, src, outFps);
                Check.Equal(ideal, kept.Count, $"{src} -> {outFps} ({what})");
                uint grid = ShareTranscoder.MuxGridFps(src, outFps);
                Check.Equal((uint)outFps, grid, $"{src} -> {outFps} ({what}): snapped to the output's grid as before");
                Check.True(StrictlyIncreasing(Muxed(kept, grid, stamps[0])), $"{src} -> {outFps} ({what}): the muxed timestamps are strictly increasing");
            }
        }
    }

    [UnitTest]
    public static void Encoder_losses_fail_the_copy_only_when_frames_are_really_damaged()
    {
        ShareTranscodeException? Judge(long src, long sub, long fin, long fout, long inFail, long outFail, out long dropped, out string? warning)
            => ShareTranscoder.JudgeEncoderLoss("Test MFT", src, sub, fin, fout, inFail, outFail, out dropped, out warning);

        Check.Null(Judge(1000, 500, 500, 500, 0, 0, out long d, out string? w), "nothing lost");
        Check.Equal(0L, d);
        Check.Null(w, "no warning when nothing was lost");

        // Fewer out than in: a frame missing inside a GOP corrupts every P-frame after it.
        var e = Judge(1000, 500, 500, 499, 0, 0, out _, out _);
        Check.NotNull(e, "one frame in and not out");
        Check.Equal(ShareTranscodeFailure.Failed, e!.Kind);
        Check.Equal("the hardware encoder lost frames", e.Message);
        Check.NotNull(Judge(1000, 500, 500, 498, 0, 2, out _, out _), "ProcessOutput failures that lost frames");

        // ProcessOutput failed but every frame still came out: only a warning.
        Check.Null(Judge(1000, 500, 500, 500, 0, 3, out d, out w), "spurious ProcessOutput failures");
        Check.NotNull(w, "they are logged");
        Check.True(w!.Contains("3 ProcessOutput"), w);
        Check.Equal(0L, d);

        // A few frames that never went in: a gap in time, not damage. The allowance is 1% of the SUBMITTED frames (here 1000 submitted of
        // 5000 decoded: 10), not of the decoded ones.
        Check.Null(Judge(5000, 1000, 990, 990, 10, 0, out d, out w), "10 of 1000 submitted never went in");
        Check.Equal(10L, d, "the dropped-input count");
        Check.NotNull(w);
        var many = Judge(5000, 1000, 989, 989, 11, 0, out d, out _);
        Check.NotNull(many, "11 of 1000 submitted is more than 1%");
        Check.Equal("the hardware encoder lost frames", many!.Message);
        Check.Equal(11L, d);
        // 1000 decoded but only 500 submitted (a 60 -> 30 copy): 5 allowed, not 10.
        Check.Null(Judge(1000, 500, 495, 495, 5, 0, out _, out _), "5 of 500 submitted");
        Check.NotNull(Judge(1000, 500, 494, 494, 6, 0, out _, out _), "6 of 500 submitted is more than 1% of what was submitted");

        // A short clip still allows 2.
        Check.Null(Judge(60, 60, 58, 58, 2, 0, out d, out _), "2 of 60");
        Check.NotNull(Judge(60, 60, 57, 57, 3, 0, out _, out _), "3 of 60");

        // Frames that went missing on the way in count even when ProcessInput did not report them.
        Check.NotNull(Judge(100, 100, 97, 97, 0, 0, out d, out _), "3 submitted frames never went in");
        Check.Equal(3L, d);
        // Both kinds at once: the out-side loss decides.
        Check.NotNull(Judge(1000, 500, 499, 498, 1, 0, out _, out _), "one never went in, and one went in and did not come out");
    }

    [UnitTest]
    public static void The_UV_plane_of_a_padded_2D_buffer_is_found_from_its_contiguous_length()
    {
        // Unpadded: pitch 1920 (or a wider pitch), 1080 rows -> length = pitch * 1080 * 3 / 2.
        Check.Equal(1080, ShareTranscoder.Nv12LumaRows(1920L * 1080 * 3 / 2, 1920, 1920, 1080), "no padding");
        Check.Equal(1080, ShareTranscoder.Nv12LumaRows(2048L * 1080 * 3 / 2, 2048, 1920, 1080), "a wider pitch, no padded rows");
        // Padded to 1088 rows.
        Check.Equal(1088, ShareTranscoder.Nv12LumaRows(1920L * 1088 * 3 / 2, 1920, 1920, 1080), "1080 padded to 1088");
        Check.Equal(1088, ShareTranscoder.Nv12LumaRows(2048L * 1088 * 3 / 2, 2048, 1920, 1080), "padded rows at a wider pitch");
        // The length of a packed copy (the width, not the pitch) still works.
        Check.Equal(1080, ShareTranscoder.Nv12LumaRows(1920L * 1080 * 3 / 2, 2048, 1920, 1080), "packed length at a wider pitch");
        Check.Equal(1088, ShareTranscoder.Nv12LumaRows(1920L * 1088 * 3 / 2, 2048, 1920, 1080), "packed and padded");
        Check.Equal(720, ShareTranscoder.Nv12LumaRows(1280L * 720 * 3 / 2, 1280, 1280, 720));
        // The numbers do not add up: use the contiguous copy.
        Check.Equal(0, ShareTranscoder.Nv12LumaRows(0, 1920, 1920, 1080), "no length");
        Check.Equal(0, ShareTranscoder.Nv12LumaRows(1920L * 1080 * 3 / 2 - 2, 1920, 1920, 1080), "not a whole number of rows");
        Check.Equal(0, ShareTranscoder.Nv12LumaRows(1920L * 1000 * 3 / 2, 1920, 1920, 1080), "fewer rows than the frame has");
        Check.Equal(0, ShareTranscoder.Nv12LumaRows(1920L * 1300 * 3 / 2, 1920, 1920, 1080), "implausibly many rows");
        Check.Equal(0, ShareTranscoder.Nv12LumaRows(1920L * 1080 * 3 / 2, 1000, 1920, 1080), "a pitch below the width");
    }

    [UnitTest]
    public static void The_kept_rate_is_measured_from_the_submitted_frames()
    {
        Check.True(Math.Abs(ShareTranscoder.MeasuredRate(601, 0, 100_000_000) - 60) < 1e-9, "601 frames over 10 s = 60 fps");
        Check.True(Math.Abs(ShareTranscoder.MeasuredRate(301, 5, 100_000_005) - 30) < 1e-9);
        Check.Equal(0.0, ShareTranscoder.MeasuredRate(1, 0, 0), "one frame has no rate");
        Check.Equal(0.0, ShareTranscoder.MeasuredRate(0, 0, 0));
        Check.Equal(0.0, ShareTranscoder.MeasuredRate(10, 5, 5), "no time");
    }

    [UnitTest]
    public static void The_recording_encoder_keeps_its_live_options_by_default()
    {
        Check.False(VideoEncoderOptions.Live.Blocking, "the recording drops a frame rather than wait");
        Check.Equal(ThreadPriority.AboveNormal, VideoEncoderOptions.Live.EventThreadPriority);
        Check.Null(VideoEncoderOptions.Live.OfflineSettings, "the recording is set up by its own code, not from offline settings");
        var off = VideoEncoderOptions.Offline();
        Check.True(off.Blocking);
        Check.Equal(ThreadPriority.BelowNormal, off.EventThreadPriority, "a share copy stays behind the recording");
        // A share copy's encoder settings are pinned apart from the recording's.
        var s = off.OfflineSettings!;
        Check.NotNull(s);
        Check.Equal(Windows.Win32.Media.MediaFoundation.eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR, s.RateControl, "CBR");
        Check.Equal(0u, s.BFrames, "no B-frames (the muxer writes pts as dts)");
        Check.True(s.LowLatency, "low latency, as the drain was verified with");
        Check.Equal(66u, s.QualityVsSpeed);
        Check.Equal(OfflineEncoderSettings.Default, s);
    }

    [UnitTest]
    public static void Failures_carry_a_short_reason_of_their_kind()
    {
        var full = ShareTranscoder.Classify(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));
        Check.Equal(ShareTranscodeFailure.DiskFull, full.Kind);
        Check.Equal("the disk is full", full.Message);
        Check.Equal(ShareTranscodeFailure.DiskFull, ShareTranscoder.Classify(new InvalidOperationException("mux", new IOException("full", unchecked((int)0x80070027)))).Kind, "found inside");
        Check.Equal(ShareTranscodeFailure.DecoderMissing, ShareTranscoder.Classify(new COMException("SetCurrentMediaType", unchecked((int)0xC00D5212))).Kind, "MF_E_TOPO_CODEC_NOT_FOUND");
        Check.Equal(ShareTranscodeFailure.Unsupported, ShareTranscoder.Classify(new COMException("open", unchecked((int)0xC00D36C4))).Kind, "MF_E_UNSUPPORTED_BYTESTREAM_TYPE");
        Check.Equal(ShareTranscodeFailure.GpuLost, ShareTranscoder.Classify(new COMException("copy", unchecked((int)0x887A0005))).Kind, "DXGI_ERROR_DEVICE_REMOVED");
        var typed = new ShareTranscodeException(ShareTranscodeFailure.NoHardwareEncoder, "no hardware H.264 encoder");
        Check.True(ReferenceEquals(typed, ShareTranscoder.Classify(typed)), "a typed failure stays as it is");
        Check.True(ShareTranscoder.Classify(new Exception("x")).Message.Length is > 0 and < 80, "anything else still has a short reason");
        // An ArgumentException from inside the pipeline is a failure like any other, not a bad request.
        Check.Equal(ShareTranscodeFailure.Failed, ShareTranscoder.Classify(new ArgumentOutOfRangeException("track")).Kind, "an argument error inside is Failed");
    }

    [UnitTest]
    public static void A_failure_after_the_GPU_was_reset_says_so()
    {
        var removed = (HRESULT)unchecked((int)0x887A0005);   // DXGI_ERROR_DEVICE_REMOVED, as GetDeviceRemovedReason gives it
        var fine = default(HRESULT);
        // After a TDR the failing call says E_FAIL, a timeout or a Media Foundation error.
        foreach (var ex in new Exception[] { new COMException("ProcessInput", unchecked((int)0x80004005)), new TimeoutException("stalled"), new COMException("ReadSample", unchecked((int)0xC00D36B2)),
                     new ShareTranscodeException(ShareTranscodeFailure.NoHardwareEncoder, "x") })
        {
            var lost = ShareTranscoder.AsGpuLost(ex, removed);
            Check.NotNull(lost, ex.GetType().Name);
            Check.Equal(ShareTranscodeFailure.GpuLost, lost!.Kind, ex.Message);
            Check.Equal("the graphics driver was reset", lost.Message);
            Check.Null(ShareTranscoder.AsGpuLost(ex, fine), "a device that is fine changes nothing");
        }
        Check.Null(ShareTranscoder.AsGpuLost(new OperationCanceledException(), removed), "a cancellation stays one");
    }

    // ------------------------------------------------------------------ no GPU: refusals and the stream copy

    [UnitTest]
    public static void Empty_or_unusable_paths_and_requests_are_refused_at_once()
    {
        using var dir = new TempDir();
        string src = dir.File("clip.mp4");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        string output = dir.File("copy.mp4");
        var req = new ShareTranscodeRequest(640, 360, 30, 600_000);
        // Thrown by the call itself (not a faulted task, never an exception on the worker thread, which would end the process).
        foreach (var (s, o, what) in new (string?, string?, string)[] { ("", output, "empty source"), (src, "", "empty output"), (null, output, "null source"),
                     (src, null, "null output"), ("   ", output, "blank source"), (src, "a\0b.mp4", "a NUL in the output"), (src, dir.Path + "\\", "a folder as the output"), (src, src, "the original") })
        {
            Check.Throws<ArgumentException>(() => { _ = ShareTranscoder.TranscodeAsync(s!, o!, req, null, default); }, "transcode: " + what);
            Check.Throws<ArgumentException>(() => { _ = ShareTranscoder.RemuxMixOnlyAsync(s!, o!, null, default); }, "remux: " + what);
        }
        Check.Throws<ArgumentException>(() => { _ = ShareTranscoder.TranscodeAsync(src, output, null!, null, default); }, "no request");
        foreach (var bad in new[] { req with { Width = 641 }, req with { Height = 0 }, req with { Fps = 0 }, req with { VideoBitrateBps = 0 }, req with { GopSeconds = float.NaN },
                     req with { GopSeconds = 0 }, req with { Width = 5000 } })
            Check.Throws<ArgumentException>(() => { _ = ShareTranscoder.TranscodeAsync(src, output, bad, null, default); }, $"{bad}");
        // A folder at the output path is a bad request too (from the worker: only a look at the disk shows it).
        Directory.CreateDirectory(dir.File("folder.mp4"));
        Check.Throws<ArgumentException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, dir.File("folder.mp4"), null, default).GetAwaiter().GetResult(), "a folder named like the copy");
        Check.True(Directory.Exists(dir.File("folder.mp4")), "and it is left alone");
        Check.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(src), "the original is untouched");
        Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, src), "nothing written");
    }

    [UnitTest]
    public static void A_file_that_is_not_a_video_gives_the_typed_reason_and_leaves_nothing()
    {
        using var dir = new TempDir();
        string bad = dir.File("not a clip.mp4");
        File.WriteAllBytes(bad, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        string output = dir.File("copy.mp4");
        OnMta(() =>
        {
            var req = new ShareTranscodeRequest(640, 360, 30, 600_000);
            var e1 = Check.Throws<ShareTranscodeException>(() => ShareTranscoder.TranscodeAsync(bad, output, req, null, default).GetAwaiter().GetResult(), "transcode");
            Check.Equal(ShareTranscodeFailure.Unsupported, e1.Kind, e1.Message);
            var e2 = Check.Throws<ShareTranscodeException>(() => ShareTranscoder.RemuxMixOnlyAsync(bad, output, null, default).GetAwaiter().GetResult(), "remux");
            Check.Equal(ShareTranscodeFailure.Unsupported, e2.Kind, e2.Message);
            var e3 = Check.Throws<ShareTranscodeException>(() => ShareTranscoder.RemuxMixOnlyAsync(dir.File("gone.mp4"), output, null, default).GetAwaiter().GetResult(), "missing");
            Check.Equal(ShareTranscodeFailure.Unsupported, e3.Kind);
            Check.Throws<ArgumentException>(() => ShareTranscoder.RemuxMixOnlyAsync(bad, bad, null, default).GetAwaiter().GetResult(), "the original is never the output");
            Check.Throws<ArgumentException>(() => ShareTranscoder.TranscodeAsync(bad, output, req with { Width = 641 }, null, default).GetAwaiter().GetResult(), "an odd size");
            Check.True(File.Exists(bad), "the original is still there");
            Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, bad), "nothing written");
        });
    }

    /// <summary>Makes <paramref name="link"/> a hard link to <paramref name="target"/> (mklink /H: no privilege, NTFS, one volume). False, and why, when it cannot.</summary>
    private static bool TryMakeHardLink(string link, string target, out string why)
    {
        why = "";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "/c", "mklink", "/H", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string said = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } why = "mklink did not finish"; return false; }
            if (p.ExitCode == 0 && File.Exists(link)) return true;
            why = $"mklink exited with {p.ExitCode}: {said.Trim()}";
            return false;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    [UnitTest]
    public static void Another_name_for_the_original_is_never_the_output()
    {
        using var dir = new TempDir();
        string src = dir.File("clip.mp4");
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(src, bytes);
        var req = new ShareTranscodeRequest(640, 360, 30, 600_000);
        int checkedAliases = 0;
        var notChecked = new List<string>();

        // A hard link: another name, the same file (volume serial and file index).
        string hard = dir.File("alias.mp4");
        if (TryMakeHardLink(hard, src, out string whyHard))
        {
            checkedAliases++;
            Check.Equal(true, ShareTranscoder.SameFile(src, hard), "a hard link is the same file");
            Check.Throws<ArgumentException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, hard, null, default).GetAwaiter().GetResult(), "remux onto a hard link of the original");
            Check.Throws<ArgumentException>(() => ShareTranscoder.TranscodeAsync(src, hard, req, null, default).GetAwaiter().GetResult(), "transcode onto a hard link of the original");
            Check.True(File.Exists(hard), "the link is not deleted");
            Check.Equal(bytes, File.ReadAllBytes(src), "the original is untouched");
        }
        else notChecked.Add("hard link: " + whyHard);

        // A junction: the original's folder under another name.
        string junction = dir.File("other folder");
        try
        {
            if (TestJunction.TryMake(junction, dir.Path, out string whyJunction))
            {
                checkedAliases++;
                string viaJunction = Path.Combine(junction, "clip.mp4");
                Check.Equal(true, ShareTranscoder.SameFile(src, viaJunction), "the original through a junction is the same file");
                Check.Throws<ArgumentException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, viaJunction, null, default).GetAwaiter().GetResult(), "remux onto the original through a junction");
                Check.Throws<ArgumentException>(() => ShareTranscoder.TranscodeAsync(src, viaJunction, req, null, default).GetAwaiter().GetResult(), "transcode onto the original through a junction");
                Check.Equal(bytes, File.ReadAllBytes(src), "the original is untouched");
            }
            else notChecked.Add("junction: " + whyJunction);
        }
        finally { TestJunction.Remove(junction); }

        // Two different files are two files.
        string other = dir.File("other.mp4");
        File.WriteAllBytes(other, bytes);
        Check.Equal(false, ShareTranscoder.SameFile(src, other), "a copy with the same bytes is another file");

        foreach (string n in notChecked)
        {
            Console.WriteLine($"      {nameof(Another_name_for_the_original_is_never_the_output)}: {n}; not checked");
            Log.Warn($"[unittest] {nameof(Another_name_for_the_original_is_never_the_output)}: {n}; not checked");
        }
        if (checkedAliases == 0) TestJunction.Skipped(nameof(Another_name_for_the_original_is_never_the_output), string.Join("; ", notChecked));
    }

    [UnitTest]
    public static void An_earlier_copy_that_is_in_use_fails_the_call_before_any_work()
    {
        using var dir = new TempDir();
        string src = dir.File("clip.mp4");
        File.WriteAllBytes(src, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        string output = dir.File("copy.mp4");
        File.WriteAllText(output, "earlier");
        var req = new ShareTranscodeRequest(640, 360, 30, 600_000);
        // Open without sharing (a player that locks it), and open for reading only (Discord reading it while it uploads): neither can be deleted.
        foreach (var share in new[] { FileShare.None, FileShare.ReadWrite })
        {
            using (var held = new FileStream(output, FileMode.Open, FileAccess.Read, share))
            {
                File.WriteAllText(output + ".part", "stale part");   // left by a crash: removed
                var e1 = Check.Throws<ShareTranscodeException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, output, null, default).GetAwaiter().GetResult(), $"remux ({share})");
                Check.Equal(ShareTranscodeFailure.Failed, e1.Kind);
                Check.Equal("the earlier copy is in use", e1.Message);
                var e2 = Check.Throws<ShareTranscodeException>(() => ShareTranscoder.TranscodeAsync(src, output, req, null, default).GetAwaiter().GetResult(), $"transcode ({share})");
                Check.Equal("the earlier copy is in use", e2.Message);
                Check.False(File.Exists(output + ".part"), "the muxer's leftovers are removed");
            }
            Check.Equal("earlier", File.ReadAllText(output), "the earlier copy is left as it was");
        }
        Check.Equal(new[] { "copy.mp4" }, Leftovers(dir.Path, src));
    }

    [UnitTest]
    public static void The_stream_copy_keeps_the_video_as_it_is_and_only_the_Mix()
    {
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "src.mp4", 640, 360, 60, 2.0, "Mix", "Game", "Mic");
            var before = Fingerprint(src);
            var (srcDur, srcStreams) = MediaProbe.Probe(src, hideEditLists: true);
            var srcVideo = srcStreams.Single(s => s.IsVideo);
            Check.Equal(3, srcStreams.Count(s => !s.IsVideo), "the source has three audio tracks");
            uint mixStream = Trimmer.AudioStreamsOf(src)[0];   // the Mix is the first audio track in file order
            var srcMix = srcStreams.Single(s => s.Index == (int)mixStream);

            string output = dir.File("copy.mp4");
            var progress = new Direct();
            var r = ShareTranscoder.RemuxMixOnlyAsync(src, output, progress, default).GetAwaiter().GetResult();
            Check.Equal(output, r.Path);
            Check.Equal(new FileInfo(output).Length, r.SizeBytes);
            Check.Equal(new[] { "Mix" }, r.AudioTracks);
            Check.Equal(new[] { "Mix" }, Trimmer.ReadAudioTrackNames(output)!, "one audio track, named Mix");
            Check.True(Trimmer.ReadClipTracks(output)!.Marked, "the copy keeps the clk1 mark");
            var (dur, streams) = MediaProbe.Probe(output, hideEditLists: true);
            var v = streams.Single(s => s.IsVideo);
            Check.Equal(srcVideo.Count, v.Count, "every video sample is copied");
            Check.Equal(srcVideo.Bytes, v.Bytes, "as it is");
            Check.Equal(1, streams.Count(s => !s.IsVideo), "one audio stream");
            var a = streams.Single(s => !s.IsVideo);
            Check.Equal(srcMix.Bytes, a.Bytes, "the Mix is copied whole");
            Check.Near(srcDur / 1e7, dur / 1e7, 0.05, "the same length");
            Check.Equal(srcVideo.Count, r.VideoFrames);
            Check.Equal((640, 360, 60), (r.Width, r.Height, r.Fps));
            Check.True(progress.Seen.Count > 5 && progress.Seen[^1] == 1.0 && progress.Seen.Zip(progress.Seen.Skip(1), (x, y) => y >= x).All(b => b), "progress rises to 1");
            Check.Equal(before, Fingerprint(src), "the original is untouched");
            Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, src).Where(f => f != "copy.mp4").ToArray(), "no .tmp or .part left");

            // A clip without a track named Mix keeps its first audio track.
            string other = MakeClip(dir, "other.mp4", 320, 180, 30, 1.0, "Game", "Mic");
            var r2 = ShareTranscoder.RemuxMixOnlyAsync(other, dir.File("other copy.mp4"), null, default).GetAwaiter().GetResult();
            Check.Equal(new[] { "Game" }, r2.AudioTracks, "the first track in file order");
        });
    }

    [UnitTest]
    public static void The_stream_copy_of_a_clip_without_audio_is_video_only()
    {
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "silent.mp4", 320, 180, 30, 1.0);
            var before = Fingerprint(src);
            var (_, srcStreams) = MediaProbe.Probe(src, hideEditLists: true);
            Check.Equal(0, srcStreams.Count(s => !s.IsVideo), "the source has no audio");
            var srcVideo = srcStreams.Single(s => s.IsVideo);
            var r = ShareTranscoder.RemuxMixOnlyAsync(src, dir.File("copy.mp4"), null, default).GetAwaiter().GetResult();
            Check.Equal(Array.Empty<string>(), r.AudioTracks, "no audio track");
            var (_, streams) = MediaProbe.Probe(r.Path, hideEditLists: true);
            Check.Equal(0, streams.Count(s => !s.IsVideo), "video only");
            Check.Equal(srcVideo.Count, streams.Single(s => s.IsVideo).Count, "every video sample");
            Check.Equal(srcVideo.Bytes, streams.Single(s => s.IsVideo).Bytes, "as it is");
            Check.Equal(before, Fingerprint(src), "the original is untouched");
            Check.Equal(new[] { "copy.mp4" }, Leftovers(dir.Path, src), "no .tmp or .part left");
        });
    }

    [UnitTest]
    public static void Cancelling_a_stream_copy_leaves_no_file_behind()
    {
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "src.mp4", 640, 360, 60, 2.0, "Mix", "Game");
            var before = Fingerprint(src);
            string output = dir.File("copy.mp4");
            using var cts = new CancellationTokenSource();
            var progress = new Direct(p => { if (p >= 0.3) cts.Cancel(); });
            Check.Throws<OperationCanceledException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, output, progress, cts.Token).GetAwaiter().GetResult(), "cancelled half way");
            Check.True(progress.Seen.Count > 0 && progress.Seen.Max() < 1, "it stopped before the end");
            Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, src), "no copy, .tmp or .part");
            Check.Throws<OperationCanceledException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, output, null, cts.Token).GetAwaiter().GetResult(), "cancelled before it starts");
            Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, src));

            // An earlier copy at the output path: a call cancelled before it starts touches nothing; one that starts replaces it (it is
            // deleted before any work), so a call cancelled half way leaves neither the earlier copy nor a new one.
            File.WriteAllText(output, "stale");
            Check.Throws<OperationCanceledException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, output, null, cts.Token).GetAwaiter().GetResult());
            Check.Equal("stale", File.ReadAllText(output), "not touched by a call cancelled before it started");
            using var cts2 = new CancellationTokenSource();
            var progress2 = new Direct(p => { if (p >= 0.3) cts2.Cancel(); });
            Check.Throws<OperationCanceledException>(() => ShareTranscoder.RemuxMixOnlyAsync(src, output, progress2, cts2.Token).GetAwaiter().GetResult(), "cancelled half way over an earlier copy");
            Check.False(File.Exists(output), "the earlier copy was replaced and no new one is left");
            Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, src), "no .tmp or .part");
            Check.Equal(before, Fingerprint(src), "the original is untouched");
        });
    }

    [UnitTest]
    public static void A_progress_handler_that_throws_at_the_end_does_not_fail_a_finished_stream_copy()
    {
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "src.mp4", 320, 180, 30, 1.0, "Mix");
            string output = dir.File("copy.mp4");
            var progress = new Direct(p => { if (p >= 1.0) throw new InvalidOperationException("the handler failed at 1.0"); });
            var r = ShareTranscoder.RemuxMixOnlyAsync(src, output, progress, default).GetAwaiter().GetResult();
            Check.Equal(1.0, progress.Seen[^1], "the handler was called at 1.0 (and threw)");
            Check.Equal(output, r.Path, "a successful result");
            Check.True(File.Exists(output), "the finished copy is left in place");
            Check.Equal(new FileInfo(output).Length, r.SizeBytes);
            Check.True(r.VideoFrames > 0, "with its video");
            Check.Equal(new[] { "copy.mp4" }, Leftovers(dir.Path, src), "no .tmp or .part left");
        });
    }

    // ------------------------------------------------------------------ GPU (CLINK_TESTS_GPU=1)

    [UnitTest]
    public static void The_re_encode_makes_a_smaller_H264_copy_with_only_the_Mix()
    {
        if (!GpuTestsOn) { NotRun(nameof(The_re_encode_makes_a_smaller_H264_copy_with_only_the_Mix)); return; }
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "src.mp4", 1280, 720, 60, 5.0, "Mix", "Game");
            var before = Fingerprint(src);
            var (srcDur, srcStreams) = MediaProbe.Probe(src, hideEditLists: true);
            int expected = ExpectedKept(srcStreams.Single(s => s.IsVideo).Count, 60, 30);
            string output = dir.File("small.mp4");
            var req = new ShareTranscodeRequest(640, 360, 30, 600_000);
            var progress = new Direct();
            long uploaded = Interlocked.Read(ref ShareTranscoder.UploadedFrames);
            var sw = Stopwatch.StartNew();
            var r = ShareTranscoder.TranscodeAsync(src, output, req, progress, default).GetAwaiter().GetResult();
            sw.Stop();
            Check.Equal(uploaded, Interlocked.Read(ref ShareTranscoder.UploadedFrames), "decoded and scaled on the GPU: no frame went through system memory");
            Console.WriteLine($"      transcode 5 s 1280x720@60 -> 640x360@30 0.6 Mbps on {r.Encoder}: {sw.ElapsedMilliseconds} ms ({5000.0 / Math.Max(1, sw.ElapsedMilliseconds):0.0}x real time), {r.SizeBytes / 1024} KB");

            Check.True(File.Exists(output), "the copy is there");
            Check.Equal(new FileInfo(output).Length, r.SizeBytes);
            long audioBytes = 24_000 * 5;
            long videoBudget = 600_000 / 8 * 5;
            Check.True(r.SizeBytes > audioBytes + videoBudget / 10 && r.SizeBytes < audioBytes + videoBudget * 2 + 64 * 1024,
                $"size {r.SizeBytes} B is in a sane band around {audioBytes + videoBudget} B");
            Check.Equal(new[] { "Mix" }, Trimmer.ReadAudioTrackNames(output)!, "exactly one audio track, named Mix");
            Check.True(Trimmer.ReadClipTracks(output)!.Marked, "the clk1 mark is kept");
            var (dur, streams) = MediaProbe.Probe(output, hideEditLists: true);
            var v = streams.Single(s => s.IsVideo);
            Check.Equal("H264", v.Subtype);
            Check.Equal((640, 360), (v.Width, v.Height));
            Check.True(Math.Abs(dur - srcDur) <= 2 * 10_000_000, $"duration {dur / 1e7:0.000} s within one GOP of {srcDur / 1e7:0.000} s");
            Check.Equal(150, expected, "every other frame of 300");
            Check.Equal(expected, v.Count, "30 fps over 5 s: every kept frame is in the copy");
            Check.Near(333_333, v.MinDelta, 200, "frames 1/30 s apart (min)");
            Check.Near(333_333, v.MaxDelta, 200, "frames 1/30 s apart (max)");
            Check.True(v.Keyframes >= 3, $"a keyframe every 2 s ({v.Keyframes})");
            Check.Equal(1, streams.Count(s => !s.IsVideo), "one audio stream");
            var srcMixBytes = srcStreams.Where(s => !s.IsVideo).Max(s => s.Bytes);   // the Mix starts first, so it is the longest
            Check.Equal(srcMixBytes, streams.Single(s => !s.IsVideo).Bytes, "the Mix is copied, not re-encoded");
            Check.Equal((640, 360, 30), (r.Width, r.Height, r.Fps));
            Check.Equal(expected, r.VideoFrames);
            Check.Near(5.0, r.Duration.TotalSeconds, 0.1);
            Check.True(progress.Seen.Count > 5 && progress.Seen[^1] == 1.0, "progress reported up to 1");
            Check.Equal(before, Fingerprint(src), "the original is untouched");
            Check.Equal(new[] { "small.mp4" }, Leftovers(dir.Path, src), "no .tmp or .part left");
        });
    }

    [UnitTest]
    public static void The_re_encode_at_the_same_size_copies_the_decoded_frames_as_they_are()
    {
        if (!GpuTestsOn) { NotRun(nameof(The_re_encode_at_the_same_size_copies_the_decoded_frames_as_they_are)); return; }
        using var dir = new TempDir();
        OnMta(() =>
        {
            // No scaling: the processor may hand over the decoder's own (padded) texture; the copy takes the frame's rectangle.
            string src = MakeClip(dir, "src.mp4", 1280, 720, 60, 1.0, "Mix");
            var (_, srcStreams) = MediaProbe.Probe(src, hideEditLists: true);
            int expected = ExpectedKept(srcStreams.Single(s => s.IsVideo).Count, 60, 60);
            long uploaded = Interlocked.Read(ref ShareTranscoder.UploadedFrames);
            var r = ShareTranscoder.TranscodeAsync(src, dir.File("same.mp4"), new ShareTranscodeRequest(1280, 720, 60, 3_000_000), null, default).GetAwaiter().GetResult();
            Check.Equal(uploaded, Interlocked.Read(ref ShareTranscoder.UploadedFrames), "a GPU copy, no upload");
            var (_, streams) = MediaProbe.Probe(r.Path, hideEditLists: true);
            var v = streams.Single(s => s.IsVideo);
            Check.Equal((1280, 720), (v.Width, v.Height));
            Check.Equal(60, expected);
            Check.Equal(expected, v.Count, "every frame");
            Check.Equal(expected, r.VideoFrames);
            Check.Near(166_667, v.MinDelta, 200, "frames 1/60 s apart (min)");
            Check.Near(166_667, v.MaxDelta, 200, "frames 1/60 s apart (max)");
            Check.Equal(new[] { "Mix" }, r.AudioTracks);
            Check.Equal((1280, 720, 60), (r.Width, r.Height, r.Fps));
        });
    }

    [UnitTest]
    public static void The_re_encode_of_a_clip_without_audio_is_video_only()
    {
        if (!GpuTestsOn) { NotRun(nameof(The_re_encode_of_a_clip_without_audio_is_video_only)); return; }
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "silent.mp4", 640, 360, 30, 1.0);
            var (_, srcStreams) = MediaProbe.Probe(src, hideEditLists: true);
            int expected = ExpectedKept(srcStreams.Single(s => s.IsVideo).Count, 30, 30);
            var r = ShareTranscoder.TranscodeAsync(src, dir.File("small.mp4"), new ShareTranscodeRequest(320, 180, 30, 400_000), null, default).GetAwaiter().GetResult();
            Check.Equal(Array.Empty<string>(), r.AudioTracks, "no audio track");
            var (_, streams) = MediaProbe.Probe(r.Path, hideEditLists: true);
            Check.Equal(0, streams.Count(s => !s.IsVideo), "video only");
            Check.Equal(30, expected);
            Check.Equal(expected, streams.Single(s => s.IsVideo).Count, "every frame");
            Check.Equal(new[] { "small.mp4" }, Leftovers(dir.Path, src), "no .tmp or .part left");
        });
    }

    [UnitTest]
    public static void The_re_encode_also_takes_frames_decoded_on_the_CPU()
    {
        if (!GpuTestsOn) { NotRun(nameof(The_re_encode_also_takes_frames_decoded_on_the_CPU)); return; }
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "src.mp4", 640, 360, 30, 1.0, "Mix");
            var (_, srcStreams) = MediaProbe.Probe(src, hideEditLists: true);
            int expected = ExpectedKept(srcStreams.Single(s => s.IsVideo).Count, 30, 30);
            ShareTranscoder.ForceSoftwareDecode = true;
            try
            {
                long uploaded = Interlocked.Read(ref ShareTranscoder.UploadedFrames), uploaded2D = Interlocked.Read(ref ShareTranscoder.UploadedFrames2D);
                var r = ShareTranscoder.TranscodeAsync(src, dir.File("cpu.mp4"), new ShareTranscodeRequest(320, 180, 30, 400_000), null, default).GetAwaiter().GetResult();
                long via2D = Interlocked.Read(ref ShareTranscoder.UploadedFrames2D) - uploaded2D;
                Console.WriteLine($"      CPU decode: {via2D} of {Interlocked.Read(ref ShareTranscoder.UploadedFrames) - uploaded} frames read at the 2D buffer's pitch (the rest from a contiguous copy)");
                Check.True(via2D > 0, $"the IMF2DBuffer.Lock2D upload path was used ({via2D} frames)");
                var (_, streams) = MediaProbe.Probe(r.Path, hideEditLists: true);
                var v = streams.Single(s => s.IsVideo);
                Check.Equal((320, 180), (v.Width, v.Height));
                Check.Equal(30, expected);
                Check.Equal(expected, v.Count, "every frame of the second");
                Check.Equal((long)expected, Interlocked.Read(ref ShareTranscoder.UploadedFrames) - uploaded, "the frames came through the CPU upload");
            }
            finally { ShareTranscoder.ForceSoftwareDecode = false; }
        });
    }

    [UnitTest]
    public static void Cancelling_a_re_encode_stops_it_and_leaves_no_file_behind()
    {
        if (!GpuTestsOn) { NotRun(nameof(Cancelling_a_re_encode_stops_it_and_leaves_no_file_behind)); return; }
        using var dir = new TempDir();
        OnMta(() =>
        {
            string src = MakeClip(dir, "src.mp4", 1280, 720, 60, 3.0, "Mix", "Game");
            var before = Fingerprint(src);
            string output = dir.File("small.mp4");
            using var cts = new CancellationTokenSource();
            var progress = new Direct(p => { if (p >= 0.3) cts.Cancel(); });
            var sw = Stopwatch.StartNew();
            Check.Throws<OperationCanceledException>(() => ShareTranscoder.TranscodeAsync(src, output, new ShareTranscodeRequest(640, 360, 30, 600_000), progress, cts.Token).GetAwaiter().GetResult());
            Check.True(sw.ElapsedMilliseconds < 10_000, $"stopped promptly ({sw.ElapsedMilliseconds} ms)");
            Check.True(progress.Seen.Max() < 1, "before the end");
            Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, src), "no copy, .tmp or .part");
            Check.Equal(before, Fingerprint(src), "the original is untouched");
            // An upscale is refused before anything is written.
            Check.Throws<ArgumentException>(() => ShareTranscoder.TranscodeAsync(src, output, new ShareTranscodeRequest(1920, 1080, 30, 600_000), null, default).GetAwaiter().GetResult(), "never upscaled");
            Check.Equal(Array.Empty<string>(), Leftovers(dir.Path, src));
        });
    }
}
