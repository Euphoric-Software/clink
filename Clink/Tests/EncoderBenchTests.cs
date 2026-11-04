using System.IO;
using System.Text.Json;
using Clink.Encode;
using Clink.Engine;

namespace Clink.Tests;

/// <summary>
/// Slice 4 step 2: the hidden encoder bench (Engine/EncoderBench.cs). The command line, the per-second bitrate maths, the VBR validity
/// verdict and the guarantee that the bench only ever uses the recording's live options need no GPU. The one smoke test benches a
/// few seconds of a generated clip through the real hardware encoder and needs CLINK_TESTS_GPU=1; it decodes a file and encodes it: no
/// monitor is captured and no setting is read. The quality script's own maths is tested in tools\tests\encoder-bench.tests.ps1.
/// </summary>
internal static class EncoderBenchTests
{
    private static readonly string Verb = EncoderBench.Verb;

    private static string[] Args(TempDir dir, params string[] more)
    {
        string reference = dir.File("reference.mp4");
        if (!File.Exists(reference)) File.WriteAllBytes(reference, new byte[] { 0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
        return new[] { Verb, reference, dir.File("out") }.Concat(more).ToArray();
    }

    private static string Fails(string[] args)
    {
        Check.False(EncoderBench.TryParseArgs(args, out var parsed, out string error), "expected a refusal: " + string.Join(" ", args.Skip(1)));
        Check.Null(parsed);
        Check.True(error.Length > 0, "and a reason");
        return error;
    }

    // ------------------------------------------------------------------ the command line

    [UnitTest]
    public static void The_defaults_are_every_mode_at_the_preset_rates_and_their_0_9x_points()
    {
        using var dir = new TempDir();
        Check.True(EncoderBench.TryParseArgs(Args(dir), out var a, out string error), error);
        Check.Equal("h264", a!.Codec);
        Check.Equal(new[] { 10.8, 12.0, 14.4, 16.0, 18.0, 20.0, 22.2 }, a.Mbps.ToArray());
        Check.Equal(new[] { EncoderMode.Off, EncoderMode.Vbr, EncoderMode.LowLatencyOff, EncoderMode.VbrLowLatencyOff }, a.Modes.ToArray());
        Check.Null(a.Seconds);
        Check.Equal(1.0, a.KeyframeSeconds, "a recording's one-second GOP");
        // Each preset rate has its 0.9x point beside it, so that VBR at 90% of the bytes is measured directly.
        foreach (double preset in new[] { 12.0, 16.0, 20.0 })
            Check.True(a.Mbps.Any(m => Math.Abs(m - preset * 0.9) < 1e-9), $"{preset} has a 0.9x point");
        // The top of the curve is the highest preset / 0.9, so the highest VBR run's comparison size (its size / 0.9) stays on the curve.
        Check.True(Math.Abs(a.Mbps.Max() - 20.0 / 0.9) < 0.05, "22.2 tops the h264 curve");
        Check.Equal(Path.GetFullPath(dir.File("reference.mp4")), a.Reference);
        Check.Equal(Path.GetFullPath(dir.File("out")), a.OutFolder);

        Check.True(EncoderBench.TryParseArgs(Args(dir, "--codec", "HEVC"), out var h, out error), error);
        Check.Equal("hevc", h!.Codec);
        Check.Equal(new[] { 11.2, 12.6, 14.0, 15.6 }, h.Mbps.ToArray(), "HEVC's default 14 with a CBR curve around it");
        Check.True(Math.Abs(h.Mbps.Max() - 14.0 / 0.9) < 0.05, "15.6 tops the hevc curve");
    }

    [UnitTest]
    public static void Every_option_is_read()
    {
        using var dir = new TempDir();
        Check.True(EncoderBench.TryParseArgs(Args(dir, "--codec", "h264", "--mbps", "12, 16.5", "--modes", "vbr,OFF", "--seconds", "7.5"), out var a, out string error), error);
        Check.Equal(new[] { 12.0, 16.5 }, a!.Mbps.ToArray());
        Check.Equal(new[] { EncoderMode.Vbr, EncoderMode.Off }, a.Modes.ToArray(), "in the order given");
        Check.Equal(7.5, a.Seconds!.Value);
        Check.True(EncoderBench.TryParseArgs(Args(dir, "--SECONDS", "10", "--Codec", "hevc"), out var b, out error), error);
        Check.Equal(10.0, b!.Seconds!.Value);
        Check.Equal(1.0, b.KeyframeSeconds);
        Check.True(EncoderBench.TryParseArgs(Args(dir, "--keyframe-seconds", "2.5"), out var k, out error), error);
        Check.Equal(2.5, k!.KeyframeSeconds);
    }

    [UnitTest]
    public static void Bad_paths_are_refused_with_a_reason()
    {
        using var dir = new TempDir();
        Check.Contains(Fails(new[] { Verb }), "usage");
        Check.Contains(Fails(new[] { Verb, dir.File("reference.mp4") }), "usage");
        Check.Contains(Fails(new[] { Verb, dir.File("nope.mp4"), dir.File("out") }), "does not exist");
        Check.Contains(Fails(new[] { Verb, dir.Path, dir.File("out") }), "folder");
        Check.Contains(Fails(new[] { Verb, "", dir.File("out") }), "empty");
        Check.Contains(Fails(new[] { Verb, "   ", dir.File("out") }), "empty");
        Check.Contains(Fails(new[] { Verb, "bad\0name.mp4", dir.File("out") }), "path");
        string[] ok = Args(dir);
        Check.Contains(Fails(new[] { Verb, ok[1], "" }), "empty");
        Check.Contains(Fails(new[] { Verb, "--codec", "h264", "--mbps", "12" }), "come first");
        Check.Contains(Fails(new[] { Verb, ok[1], "--codec" }), "come first");
        // The out folder is not a file, and is not the reference's own folder (a clips folder must never be filled with bench output).
        Check.Contains(Fails(new[] { Verb, ok[1], ok[1] }), "is a file");
        Check.Contains(Fails(new[] { Verb, ok[1], dir.Path }), "own folder");
        Check.Contains(Fails(new[] { Verb, ok[1], dir.Path + Path.DirectorySeparatorChar }), "own folder");
        Check.Contains(Fails(new[] { Verb, ok[1], dir.Path.ToUpperInvariant() }), "own folder");
    }

    [UnitTest]
    public static void Bad_options_and_lists_are_refused_with_a_reason()
    {
        using var dir = new TempDir();
        Check.Contains(Fails(Args(dir, "--codec", "av1")), "h264 or hevc");
        Check.Contains(Fails(Args(dir, "--codec", "")), "h264 or hevc");
        Check.Contains(Fails(Args(dir, "--bitrate", "12")), "unknown option");
        Check.Contains(Fails(Args(dir, "--mbps")), "needs a value");
        Check.Contains(Fails(Args(dir, "--mbps", "12", "--mbps", "16")), "twice");
        foreach (string bad in new[] { "", "x", "12,,16", "12,", ",12", "12;16", "NaN", "Infinity", "0", "-5", "0.4", "201", "1e9" })
            Check.Contains(Fails(Args(dir, "--mbps", bad)), "--mbps", "mbps '" + bad + "'");
        Check.Contains(Fails(Args(dir, "--mbps", "12,12")), "twice");
        Check.Contains(Fails(Args(dir, "--mbps", "12,12.0000001")), "twice");   // both would be "12M" and overwrite each other
        foreach (string bad in new[] { "", "0", "0.24", "10.01", "NaN", "Infinity", "x" })
            Check.Contains(Fails(Args(dir, "--keyframe-seconds", bad)), "--keyframe-seconds", "keyframe '" + bad + "'");
        Check.Contains(Fails(Args(dir, "--keyframe-seconds", "1", "--keyframe-seconds", "2")), "twice");
        Check.Contains(Fails(Args(dir, "--mbps", string.Join(",", Enumerable.Range(10, 13)))), "at most 12");
        foreach (string bad in new[] { "", "cbr", "off,,vbr", "vbr,vbr", "off vbr", "vbr-lowlatency" })
            Check.Contains(Fails(Args(dir, "--modes", bad)), "--modes", "modes '" + bad + "'");
        Check.Contains(Fails(Args(dir, "--modes", "vbr,VBR")), "twice");
        string retired = Fails(Args(dir, "--modes", "off,vbr-bframes"));
        Check.Contains(retired, "retired");
        Check.Contains(retired, "vbr-lowlatency-off");
        foreach (string bad in new[] { "", "0", "-1", "x", "NaN", "Infinity", "3601" })
            Check.Contains(Fails(Args(dir, "--seconds", bad)), "--seconds", "seconds '" + bad + "'");
    }

    [UnitTest]
    public static void The_run_files_are_named_by_codec_bitrate_and_mode()
    {
        Check.Equal("h264_12M_off", EncoderBench.RunName("h264", 12, EncoderMode.Off));
        Check.Equal("hevc_14M_vbr-lowlatency-off", EncoderBench.RunName("hevc", 14, EncoderMode.VbrLowLatencyOff));
        Check.Equal("h264_12.5M_lowlatency-off", EncoderBench.RunName("h264", 12.5, EncoderMode.LowLatencyOff));
        Check.Equal("h264_16M_vbr", EncoderBench.RunName("h264", 16.0, EncoderMode.Vbr));
        Check.Equal("h264_10.8M_off", EncoderBench.RunName("h264", 10.8, EncoderMode.Off));
        Check.Equal("h264_14.4M_vbr", EncoderBench.RunName("h264", 14.4, EncoderMode.Vbr));
        // Bitrates that differ keep different names (12 and 12.001 used to be "12M" twice).
        Check.NotEqual(EncoderBench.RunName("h264", 12, EncoderMode.Off), EncoderBench.RunName("h264", 12.001, EncoderMode.Off));
        Check.Equal("h264_12.001M_off", EncoderBench.RunName("h264", 12.001, EncoderMode.Off));
        // The default rates are all distinct by name.
        foreach (string codec in new[] { "h264", "hevc" })
            Check.Equal(EncoderBench.DefaultMbps(codec).Count, EncoderBench.DefaultMbps(codec).Select(m => EncoderBench.RunName(codec, m, EncoderMode.Off)).Distinct().Count());
        // Safe for the script's ffmpeg arguments (it accepts letters, digits, '.', '_' and '-' only).
        foreach (var m in Enum.GetValues<EncoderMode>())
            Check.True(System.Text.RegularExpressions.Regex.IsMatch(EncoderBench.RunName("hevc", 12.5, m), "^[A-Za-z0-9._-]+$"));
    }

    // ------------------------------------------------------------------ the bench-only quality mode

    [UnitTest]
    public static void Quality_is_a_bench_only_mode_the_live_setting_does_not_know()
    {
        using var dir = new TempDir();
        Check.True(EncoderBench.TryParseArgs(Args(dir), out var d, out string error), error);
        Check.False(d!.Quality, "not in the default list");
        Check.True(EncoderBench.TryParseArgs(Args(dir, "--modes", "off,Quality"), out var a, out error), error);
        Check.Equal(new[] { EncoderMode.Off }, a!.Modes.ToArray(), "quality is not an EncoderMode");
        Check.True(a.Quality);
        Check.True(EncoderBench.TryParseArgs(Args(dir, "--modes", "QUALITY"), out var q, out error), error);
        Check.Equal(0, q!.Modes.Count);
        Check.True(q.Quality);
        Check.Equal(new[] { (EncoderMode.Off, false), (EncoderMode.Vbr, false), (EncoderMode.Off, true) }, EncoderBench.RunModes(a with { Modes = new[] { EncoderMode.Off, EncoderMode.Vbr } }).ToArray(), "the listed modes, then quality");
        Check.Contains(Fails(Args(dir, "--modes", "quality,off,quality")), "twice");
        Check.Contains(Fails(Args(dir, "--modes", "quality,")), "--modes");
        Check.Contains(Fails(Args(dir, "--modes", "qualit")), "quality");   // the list of modes in the message includes it
        // The live parser (the hidden setting) rejects it: unknown, so off with a warning, and no tuning.
        Check.NotNull(EncoderExperiments.Parse("quality").Warning);
        Check.Null(EncoderExperiments.Resolve("quality", false));
        Check.Equal(4, Enum.GetValues<EncoderMode>().Length, "no fifth encoder mode");
    }

    [UnitTest]
    public static void The_quality_run_has_its_own_name_and_options_cbr_like_off_with_qvs_100_before_the_types()
    {
        Check.Equal("h264_12M_quality", EncoderBench.QualityRunName("h264", 12));
        Check.Equal("hevc_12.6M_quality", EncoderBench.RunName("hevc", 12.6, EncoderMode.Off, quality: true));
        Check.Equal("h264_12M_off", EncoderBench.RunName("h264", 12, EncoderMode.Off, quality: false));
        foreach (string codec in new[] { "h264", "hevc" })
            foreach (double m in EncoderBench.DefaultMbps(codec))
            {
                var names = Enum.GetValues<EncoderMode>().Select(x => EncoderBench.RunName(codec, m, x)).Append(EncoderBench.QualityRunName(codec, m)).ToList();
                Check.Equal(names.Count, names.Distinct().Count(), "unique among the modes at one rate");
                Check.True(System.Text.RegularExpressions.Regex.IsMatch(EncoderBench.QualityRunName(codec, m), "^[A-Za-z0-9._-]+$"));
            }
        Check.Equal("quality", EncoderBench.ModeName(EncoderMode.Off, true));
        Check.Equal("off", EncoderBench.ModeName(EncoderMode.Off, false));

        var o = EncoderBench.OptionsFor(EncoderMode.Off, quality: true);
        Check.Equal(100u, o.QualityVsSpeedBeforeTypes!.Value);
        Check.Null(o.LiveTuning, "CBR exactly like off: no tuning, so no other early call and today's calls after the types");
        Check.False(o.Blocking);
        Check.Equal(VideoEncoderOptions.Live with { QualityVsSpeedBeforeTypes = 100 }, o);
        foreach (var mode in Enum.GetValues<EncoderMode>())
        {
            Check.Equal(EncoderBench.OptionsFor(mode), EncoderBench.OptionsFor(mode, quality: false));
            Check.Null(EncoderBench.OptionsFor(mode).QualityVsSpeedBeforeTypes, mode + ": no other mode sets it");
        }
        Check.Null(VideoEncoderOptions.Live.QualityVsSpeedBeforeTypes, "and the recording's options do not");
    }

    [UnitTest]
    public static void The_run_json_records_the_requested_qvs_its_stage_and_the_read_back()
    {
        var applied = new AppliedTuning(EncoderMode.Off, false, 12_000_000, 12_000_000, true, 0, 66, 60, null, null);
        var off = EncoderBench.DescribeQvs(EncoderBench.OptionsFor(EncoderMode.Off, false), applied, null, 33);
        Check.Equal(66u, off.Requested!.Value);
        Check.Equal("after-types", off.Stage);
        Check.Equal(33u, off.ReadBack!.Value);
        var none = EncoderBench.DescribeQvs(EncoderBench.OptionsFor(EncoderMode.Off, false), null, null, null);
        Check.Null(none.Requested);
        Check.Equal("none", none.Stage);
        Check.Null(none.ReadBack);

        var qApplied = applied with { QualityVsSpeed = 100 };
        var q = EncoderBench.DescribeQvs(EncoderBench.OptionsFor(EncoderMode.Off, true), qApplied, new QvsBeforeTypesResult(100, 0, 100), 100);
        Check.Equal(100u, q.Requested!.Value);
        Check.Equal("before-types", q.Stage);
        Check.Equal(100u, q.ReadBack!.Value);
        Check.Equal("0x00000000", q.SetResult);
        Check.Contains(q.Describe(), "requested 100 at before-types, reads back 100");

        var r = new BenchRunResult { Name = "h264_12M_quality", Mode = EncoderBench.QualityModeName, Codec = "h264", QualityVsSpeed = q, EncodeSpeedFps = 410.5 };
        using var doc = JsonDocument.Parse(EncoderBench.ToJson(r));
        var root = doc.RootElement;
        Check.Equal("quality", root.GetProperty("mode").GetString());
        var j = root.GetProperty("qualityVsSpeed");
        Check.Equal(100, j.GetProperty("requested").GetInt32());
        Check.Equal("before-types", j.GetProperty("stage").GetString());
        Check.Equal(100, j.GetProperty("readBack").GetInt32());
        Check.Equal(410.5, root.GetProperty("encodeSpeedFps").GetDouble());
        using var unread = JsonDocument.Parse(EncoderBench.ToJson(new BenchRunResult { QualityVsSpeed = new BenchQvsInfo { Requested = 100, Stage = "before-types" } }));
        Check.Equal(JsonValueKind.Null, unread.RootElement.GetProperty("qualityVsSpeed").GetProperty("readBack").ValueKind, "an unreadable value is null, not 0");
    }

    // ------------------------------------------------------------------ the per-second profile

    private static List<(long Pts, int Bytes)> Frames(int count, Func<int, int> bytes)
        => Enumerable.Range(0, count).Select(k => (k * 10_000_000L / 60, bytes(k))).ToList();

    [UnitTest]
    public static void The_profile_sums_whole_seconds_and_leaves_the_partial_one_out()
    {
        // 2.5 s of 60 fps at 10 000 bytes a frame: two whole seconds of 600 000 bytes = 4.8 Mbps each, and half a second left over.
        var p = BitrateProfile.Compute(Frames(150, _ => 10_000));
        Check.Equal(2, p.Seconds);
        Check.Near(4.8, p.PerSecondMbps[0], 1e-9);
        Check.Near(4.8, p.PerSecondMbps[1], 1e-9);
        Check.Near(4.8, p.PeakMbps, 1e-9);
        Check.Near(4.8, p.MeanMbps, 1e-9);
        Check.Near(1.0, p.PeakOverMean, 1e-9);

        // A busy second after a quiet one: 100 000 then 200 000 bytes (0.8 and 1.6 Mbps); the third second is partial.
        var v = BitrateProfile.Compute(Frames(150, k => k < 60 ? 100_000 / 60 + (k == 0 ? 100_000 % 60 : 0) : k < 120 ? 200_000 / 60 + (k == 60 ? 200_000 % 60 : 0) : 999_999));
        Check.Equal(2, v.Seconds);
        Check.Near(0.8, v.PerSecondMbps[0], 1e-9);
        Check.Near(1.6, v.PerSecondMbps[1], 1e-9);
        Check.Near(1.6, v.PeakMbps, 1e-9);
        Check.Near(1.2, v.MeanMbps, 1e-9);
        Check.Near(1.6 / 1.2, v.PeakOverMean, 1e-9);
    }

    [UnitTest]
    public static void The_profile_counts_from_the_earliest_timestamp_in_any_order_and_handles_little_data()
    {
        var late = Frames(150, _ => 10_000).Select(s => (s.Pts + 5_000_000_000L, s.Bytes)).Reverse().ToList();
        var p = BitrateProfile.Compute(late);
        Check.Equal(2, p.Seconds);
        Check.Near(4.8, p.PeakMbps, 1e-9);

        Check.Equal(0, BitrateProfile.Compute(new List<(long, int)>()).Seconds);
        Check.Equal(0, BitrateProfile.Compute(Frames(60, _ => 10_000)).Seconds, "under a second: nothing complete");
        Check.Equal(1, BitrateProfile.Compute(Frames(119, _ => 10_000)).Seconds, "119 frames reach 1.97 s: one whole second, the tail is the partial one");
        Check.Equal(0.0, BitrateProfile.Compute(Frames(10, _ => 1)).PeakMbps);
        Check.Equal(0, BitrateProfile.Compute(Frames(150, _ => 1), 0).Seconds, "no window, no profile");

        // A 0.5 s window: bytes * 8 / 0.5 s.
        var half = BitrateProfile.Compute(Frames(61, _ => 10_000), 5_000_000);
        Check.Equal(2, half.Seconds);
        Check.Near(4.8, half.PerSecondMbps[0], 1e-9);
    }

    // ------------------------------------------------------------------ the VBR validity verdict

    private static BitrateProfile Profile(params double[] mbps) => new(mbps, mbps.Length == 0 ? 0 : mbps.Max(), mbps.Length == 0 ? 0 : mbps.Average());

    [UnitTest]
    public static void A_cbr_mode_is_not_judged()
    {
        var v = BenchChecks.JudgeVbr(false, false, null, Profile(20, 20, 20));
        Check.False(v.Applicable);
        Check.True(v.TookEffect);
    }

    [UnitTest]
    public static void A_vbr_that_fell_back_did_not_take_effect_whatever_the_output_looks_like()
    {
        var v = BenchChecks.JudgeVbr(true, false, "rate mode VBR not applied (Differs), using CBR", Profile(10, 30, 10));
        Check.True(v.Applicable);
        Check.False(v.TookEffect);
        Check.Contains(v.Note, "VBR did not take effect");
        Check.Contains(v.Note, "using CBR");
    }

    [UnitTest]
    public static void A_vbr_needs_a_peak_second_above_the_mean_second()
    {
        var flat = BenchChecks.JudgeVbr(true, true, null, Profile(20, 20.4, 19.8, 20.2));
        Check.False(flat.TookEffect, "CBR-like seconds");
        Check.Contains(flat.Note, "VBR did not take effect");
        var varying = BenchChecks.JudgeVbr(true, true, null, Profile(10, 24, 12, 18));
        Check.True(varying.TookEffect);
        Check.True(varying.Note.StartsWith("ok"), varying.Note);

        // The boundary: strictly above 1.05 x the mean.
        Check.False(BenchChecks.JudgeVbr(true, true, null, new BitrateProfile(new[] { 1.9, 2.1 }, 2.1, 2.0)).TookEffect, "exactly 1.05 is not above it");
        Check.True(BenchChecks.JudgeVbr(true, true, null, new BitrateProfile(new[] { 1.9, 2.11 }, 2.11, 2.0)).TookEffect);

        var one = BenchChecks.JudgeVbr(true, true, null, Profile(30));
        Check.False(one.TookEffect, "one second cannot show a peak above a mean");
        Check.Contains(one.Note, "not judged");
        Check.False(BenchChecks.JudgeVbr(true, true, null, BitrateProfile.Empty).TookEffect);
    }

    // ------------------------------------------------------------------ the bench uses the recording's live options and nothing else

    [UnitTest]
    public static void The_bench_configures_the_encoder_with_the_live_options_of_a_recording()
    {
        foreach (var mode in Enum.GetValues<EncoderMode>())
        {
            var o = EncoderBench.OptionsFor(mode);
            Check.False(o.Blocking, mode + ": never the offline (share copy) encoder");
            Check.Null(o.OfflineSettings, mode + ": no offline settings");
            Check.Equal(VideoEncoderOptions.Live.EventThreadPriority, o.EventThreadPriority, mode + ": the live event thread");
            if (mode == EncoderMode.Off)
            {
                Check.Null(o.LiveTuning, "off: no tuning, so today's exact ICodecAPI calls (the plain live options)");
                Check.Equal(VideoEncoderOptions.Live, o);
            }
            else
            {
                Check.Equal(VideoEncoderOptions.Live with { LiveTuning = EncoderExperiments.Resolve(EncoderExperiments.Name(mode), false) }, o, mode + ": the tuning Engine builds from the setting");
                Check.False(o.LiveTuning!.ForceCbr, mode + ": no size target");
                Check.Equal(0u, o.LiveTuning.BFrames, mode + ": the recording's 0 B-frames");
            }
        }
    }

    [UnitTest]
    public static void A_bench_option_cannot_be_turned_into_a_share_or_offline_encoder()
    {
        var cfg = new VideoEncoderConfig(1920, 1080, 60, 16_000_000, "h264", 1f);
        foreach (var mode in Enum.GetValues<EncoderMode>().Where(m => m != EncoderMode.Off))
        {
            // The constructor refuses a blocking encoder with a tuning before it opens anything (so a null device is never touched).
            Check.Throws<ArgumentException>(() => { using var e = new VideoEncoder(null!, cfg, EncoderBench.OptionsFor(mode) with { Blocking = true }); }, mode.ToString());
        }
        // And the share copy's options carry no tuning and stay pinned: CBR, no B-frames, low latency on, quality-vs-speed 66.
        Check.Null(VideoEncoderOptions.Offline().LiveTuning);
        Check.True(VideoEncoderOptions.Offline().Blocking);
        var pinned = VideoEncoderOptions.Offline().OfflineSettings!;
        Check.True(pinned.LowLatency);
        Check.Equal(66u, pinned.QualityVsSpeed);
        Check.Equal(0u, pinned.BFrames);
        Check.Equal(Windows.Win32.Media.MediaFoundation.eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR, pinned.RateControl);
        Check.Equal(OfflineEncoderSettings.Default, pinned);
        // The options the recording gets when the flag is off are the ones the bench uses for "off".
        Check.Null(VideoEncoderOptions.Live.LiveTuning);
        Check.False(VideoEncoderOptions.Live.Blocking);
    }

    // ------------------------------------------------------------------ the result file

    [UnitTest]
    public static void A_run_result_is_written_as_camel_case_json_the_script_can_read()
    {
        var r = new BenchRunResult
        {
            Name = "h264_16M_vbr", ClinkVersion = "1.2.3+abc", FileMbps = 15.9, Mode = "vbr", Codec = "h264", RequestedMbps = 16, FramesIn = 600, FramesOut = 598, Dropped = 0, FramesHeldAtEnd = 2, MaxFramesInFlight = 3,
            FileSizeBytes = 123456, AchievedMbps = 15.5, PeakMbps1s = 20, MeanMbps1s = 16, PeakOverMean = 1.25, PerSecondMbps = new[] { 14.0, 20.0 },
            VbrApplicable = true, VbrTookEffect = true, VbrVerdict = "ok",
            DecodedFrames = 600, OutOfOrderOutputs = 0, KeyframeSeconds = 1, SecondsLimit = null, EncodeSpeedFps = 240.5, MaxWaitForRoomMs = 12.25,
            AdapterName = "Test GPU", AdapterLuid = "00000000:0000ABCD",
            ReferenceInfo = new BenchReferenceInfo { Codec = "h264", Width = 1920, Height = 1080, Fps = 60, FileBytes = 1000, DurationSeconds = 10, OverallMbps = 0.0008, ClinkBrand = true },
            Requested = new BenchRequestedInfo { Vbr = true, MeanMbps = 16, MaxMbps = 24, LowLatency = true },
            Applied = new BenchAppliedInfo { Rate = "PeakConstrainedVBR", MeanMbps = 16, MaxMbps = 24, LowLatency = true, Fallback = null },
        };
        using var doc = JsonDocument.Parse(EncoderBench.ToJson(r));
        var root = doc.RootElement;
        Check.Equal("vbr", root.GetProperty("mode").GetString());
        Check.Equal("1.2.3+abc", root.GetProperty("clinkVersion").GetString());
        Check.Equal(15.5, root.GetProperty("achievedMbps").GetDouble());
        Check.Equal(15.9, root.GetProperty("fileMbps").GetDouble());
        Check.Equal("h264", root.GetProperty("codec").GetString());
        Check.Equal(16.0, root.GetProperty("requestedMbps").GetDouble());
        Check.Equal(600L, root.GetProperty("framesIn").GetInt64());
        Check.Equal(598L, root.GetProperty("framesOut").GetInt64());
        Check.Equal(0L, root.GetProperty("dropped").GetInt64());
        Check.Equal(3L, root.GetProperty("maxFramesInFlight").GetInt64());
        Check.Equal(20.0, root.GetProperty("peakMbps1s").GetDouble());
        Check.Equal(2, root.GetProperty("perSecondMbps").GetArrayLength());
        Check.Equal("PeakConstrainedVBR", root.GetProperty("applied").GetProperty("rate").GetString());
        Check.Equal(24.0, root.GetProperty("requested").GetProperty("maxMbps").GetDouble());
        Check.True(root.GetProperty("vbrTookEffect").GetBoolean());
        Check.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        // What the script needs to judge the reference, the adapter, the alignment and the speed.
        var refInfo = root.GetProperty("referenceInfo");
        Check.Equal("h264", refInfo.GetProperty("codec").GetString());
        Check.Equal(1920, refInfo.GetProperty("width").GetInt32());
        Check.True(refInfo.GetProperty("clinkBrand").GetBoolean());
        Check.Equal("Test GPU", root.GetProperty("adapterName").GetString());
        Check.Equal("00000000:0000ABCD", root.GetProperty("adapterLuid").GetString());
        Check.Equal(600L, root.GetProperty("decodedFrames").GetInt64());
        Check.Equal(0, root.GetProperty("outOfOrderOutputs").GetInt32());
        Check.Equal(JsonValueKind.Null, root.GetProperty("secondsLimit").ValueKind);
        Check.Equal(1.0, root.GetProperty("keyframeSeconds").GetDouble());
        Check.Equal(240.5, root.GetProperty("encodeSpeedFps").GetDouble());
        Check.Equal(12.25, root.GetProperty("maxWaitForRoomMs").GetDouble());
    }

    [UnitTest]
    public static void A_run_deletes_what_an_earlier_run_of_the_same_name_left_but_nothing_else()
    {
        using var dir = new TempDir();
        string name = "h264_12M_vbr", other = "h264_12M_off";
        foreach (string ext in new[] { ".mp4", ".json", ".metrics.json", ".vmaf.json", ".ffmpeg.log" })
        {
            File.WriteAllText(dir.File(name + ext), "stale");
            File.WriteAllText(dir.File(other + ext), "keep");
        }
        string reference = dir.File("reference.mp4");
        File.WriteAllText(reference, "the reference");
        EncoderBench.DeleteStaleOutputs(dir.Path, name, reference);
        foreach (string ext in new[] { ".mp4", ".json", ".metrics.json", ".vmaf.json", ".ffmpeg.log" })
        {
            Check.False(File.Exists(dir.File(name + ext)), name + ext + " is gone");
            Check.True(File.Exists(dir.File(other + ext)), other + ext + " is kept");
        }
        EncoderBench.DeleteStaleOutputs(dir.Path, name, reference);   // nothing there: no error
        Check.Equal("the reference", File.ReadAllText(reference), "the reference is untouched");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, nint reserved);

    [UnitTest]
    public static void A_run_never_deletes_the_reference_and_refuses_when_it_is_an_output_name()
    {
        using var dir = new TempDir();
        string name = "h264_12M_off";
        // The reference IS an output name (same path, any case): refused, and still there afterwards.
        string clash = dir.File(name + ".mp4");
        File.WriteAllText(clash, "the reference");
        Check.Throws<InvalidOperationException>(() => EncoderBench.DeleteStaleOutputs(dir.Path, name, clash));
        Check.Throws<InvalidOperationException>(() => EncoderBench.DeleteStaleOutputs(dir.Path, name, dir.File(name.ToUpperInvariant() + ".MP4")));
        Check.True(File.Exists(clash) && File.ReadAllText(clash) == "the reference", "still there");
        Check.Contains(EncoderBench.ReferenceCollision(dir.Path, clash, new[] { "h264_16M_vbr", name }) ?? "", "nothing was deleted");
        Check.Null(EncoderBench.ReferenceCollision(dir.Path, clash, new[] { "h264_16M_vbr" }));

        // The reference is another name for an output file (a hard link): the same volume serial and file index, so refused, not deleted.
        string refLink = dir.File("somewhere.mp4"), output = dir.File("h264_20M_off.json");
        File.WriteAllText(refLink, "linked reference");
        if (!CreateHardLink(output, refLink, 0)) Check.Skip("hard links are not available here");
        Check.Throws<InvalidOperationException>(() => EncoderBench.DeleteStaleOutputs(dir.Path, "h264_20M_off", refLink));
        Check.True(File.Exists(output) && File.Exists(refLink), "both names are still there");
        Check.Contains(EncoderBench.ReferenceCollision(dir.Path, refLink, new[] { "h264_20M_off" }) ?? "", "reference");

        // The muxer's temp names of a run are outputs too: a reference called <run>.mp4.tmp / .part is refused, and a different name is not.
        foreach (string ext in new[] { ".mp4.tmp", ".mp4.part" })
        {
            string tmp = dir.File("h264_24M_off" + ext);
            File.WriteAllText(tmp, "the reference");
            Check.Contains(EncoderBench.ReferenceCollision(dir.Path, tmp, new[] { "h264_24M_off" }) ?? "", "nothing was deleted");
            Check.Null(EncoderBench.ReferenceCollision(dir.Path, tmp, new[] { "h264_26M_off" }));
        }
    }

    [UnitTest]
    public static void A_reference_that_is_the_out_folders_cli_log_is_refused_before_the_log_is_touched()
    {
        foreach (int size in new[] { 64, 2 * 1024 * 1024 + 1024 })
        {
            using var dir = new TempDir();
            string outDir = dir.File("out");
            Directory.CreateDirectory(outDir);
            // Same folder is refused while parsing (exit 2); the reference reaching the log by another name (a hard link) is what Run must catch.
            string reference = dir.File("reference.mp4"), log = Path.Combine(outDir, Log.CliFile);
            byte[] bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            File.WriteAllBytes(reference, bytes);
            if (!CreateHardLink(log, reference, 0)) Check.Skip("hard links are not available here");
            var said = new StringWriter();
            var oldOut = Console.Out;
            int code;
            try { Console.SetOut(said); code = EncoderBench.Run(new[] { Verb, reference, outDir }); }
            finally { Console.SetOut(oldOut); }
            Check.Equal(1, code, "refused, size " + size);
            Check.Contains(said.ToString(), "bench log");
            Check.True(File.ReadAllBytes(reference).AsSpan().SequenceEqual(bytes), "bytes unchanged, size " + size);
            Check.True(File.ReadAllBytes(log).AsSpan().SequenceEqual(bytes), "the log name still has the same bytes, size " + size);
            Check.Equal(1, Directory.GetFileSystemEntries(outDir).Length, "no rotated file or other output, size " + size);
        }
    }

    [UnitTest]
    public static void A_reference_reaching_the_cli_log_or_its_prev_file_through_a_junction_is_refused_and_survives()
    {
        foreach (string refName in new[] { Log.CliFile, Path.GetFileName(Log.PrevPathFor("x", Log.CliFile)) })
        {
            using var dir = new TempDir();
            string outDir = dir.File("out"), junction = dir.File("j");
            Directory.CreateDirectory(outDir);
            if (!TestJunction.TryMake(junction, outDir, out string why)) { TestJunction.Skipped(nameof(A_reference_reaching_the_cli_log_or_its_prev_file_through_a_junction_is_refused_and_survives), why); return; }
            try
            {
                // The reference is out\<refName> under another folder name; out\clink-cli.log is over 2 MB, so Log.Init would move it over the .prev file.
                byte[] reference = new byte[4096], oldLog = new byte[2 * 1024 * 1024 + 1024];
                new Random(1).NextBytes(reference);
                new Random(2).NextBytes(oldLog);
                string viaJunction = Path.Combine(junction, refName);
                if (refName == Log.CliFile) reference = oldLog;
                File.WriteAllBytes(viaJunction, reference);
                if (refName != Log.CliFile) File.WriteAllBytes(Path.Combine(outDir, Log.CliFile), oldLog);
                int before = Directory.GetFileSystemEntries(outDir).Length;
                var said = new StringWriter();
                var oldOut = Console.Out;
                int code;
                try { Console.SetOut(said); code = EncoderBench.Run(new[] { Verb, viaJunction, outDir }); }
                finally { Console.SetOut(oldOut); }
                Check.Equal(1, code, "refused, reference " + refName);
                Check.Contains(said.ToString(), "bench log");
                Check.True(File.ReadAllBytes(viaJunction).AsSpan().SequenceEqual(reference), "the reference is unchanged, " + refName);
                Check.Equal(before, Directory.GetFileSystemEntries(outDir).Length, "no rotated file or other output, " + refName);
                Check.True(File.ReadAllBytes(Path.Combine(outDir, Log.CliFile)).Length == oldLog.Length, "the log was not rotated, " + refName);
            }
            finally { TestJunction.Remove(junction); }
        }
    }

    [UnitTest]
    public static void The_reference_line_and_the_version_for_the_log_and_the_json()
    {
        var info = new BenchReferenceInfo { Codec = "h264", Width = 1920, Height = 1080, Fps = 60, OverallMbps = 48.34, ClinkBrand = false };
        string line = EncoderBench.DescribeReferenceLine(info, 3600);
        Check.Contains(line, "h264 1920x1080 60 fps");
        Check.Contains(line, "3600 frames");
        Check.Contains(line, "48.3 Mbps");
        Check.Contains(line, "clk1 no");
        Check.Contains(EncoderBench.DescribeReferenceLine(new BenchReferenceInfo { Codec = "hevc", ClinkBrand = true }, 0), "clk1 yes");
        Check.True(EncoderBench.ClinkVersionText().Length > 0, "the build is named");
    }

    // ------------------------------------------------------------------ GPU smoke (CLINK_TESTS_GPU=1)

    /// <summary>
    /// Benches 3 s of a generated 1280x720@60 clip with off and vbr through the real hardware H.264 encoder, with the real decoder. It
    /// writes only into the temp folder, reads no setting and captures no screen. The outputs must exist, the JSON must parse, no
    /// frame may be dropped and the MP4 must hold what came out of the encoder. (Not asserted: that VBR took effect; a synthetic
    /// pattern may not make it vary, which is what the verdict in the JSON is for.)
    /// </summary>
    [UnitTest]
    public static void The_bench_encodes_a_generated_clip_with_off_and_vbr_and_writes_mp4_and_json()
    {
        if (!ShareTranscoderTests.GpuTestsOn) { ShareTranscoderTests.NotRun(nameof(The_bench_encodes_a_generated_clip_with_off_and_vbr_and_writes_mp4_and_json)); return; }
        using var dir = new TempDir();
        ShareTranscoderTests.OnMta(() =>
        {
            string reference = ShareTranscoderTests.MakeClip(dir, "reference.mp4", 1280, 720, 60, 4.0);
            var args = new BenchArgs(reference, dir.File("bench"), "h264", new[] { 8.0 }, new[] { EncoderMode.Off, EncoderMode.Vbr }, 3.0);
            int code = EncoderBench.RunAll(args);
            foreach (var mode in args.Modes)
            {
                string name = EncoderBench.RunName("h264", 8, mode);
                string json = Path.Combine(args.OutFolder, name + ".json"), mp4 = Path.Combine(args.OutFolder, name + ".mp4");
                Check.True(File.Exists(json), name + ".json is there");
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var root = doc.RootElement;
                if (root.GetProperty("error").ValueKind != JsonValueKind.Null)
                {
                    string why = root.GetProperty("error").GetString() ?? "";
                    if (why.Contains("hardware", StringComparison.OrdinalIgnoreCase) || why.Contains("decoder", StringComparison.OrdinalIgnoreCase)) Check.Skip("CLINK_TESTS_GPU=1 but " + why);
                    Check.Fail(name + " failed: " + why);
                }
                Check.True(File.Exists(mp4) && new FileInfo(mp4).Length > 0, name + ".mp4 is there");
                Check.Equal(new FileInfo(mp4).Length, root.GetProperty("fileSizeBytes").GetInt64());
                Check.Equal(EncoderExperiments.Name(mode), root.GetProperty("mode").GetString());
                long framesIn = root.GetProperty("framesIn").GetInt64(), framesOut = root.GetProperty("framesOut").GetInt64();
                Check.True(framesIn >= 170 && framesIn <= 181, $"3 s of 60 fps went in ({framesIn})");
                Check.Equal(0L, root.GetProperty("dropped").GetInt64(), "no frame dropped");
                Check.True(framesOut <= framesIn && framesIn - framesOut <= 6, $"out {framesOut} of {framesIn}");
                Check.True(root.GetProperty("maxFramesInFlight").GetInt64() <= 24, "frames in flight stay inside the pool");
                Check.NotNull(root.GetProperty("applied").GetProperty("rate").GetString());
                Check.True(root.GetProperty("adapterName").GetString()!.Length > 0, "the adapter is named");
                Check.True(root.GetProperty("clinkVersion").GetString()!.Length > 0, "the build is recorded");
                Check.True(root.GetProperty("achievedMbps").GetDouble() > 0 && root.GetProperty("achievedMbps").GetDouble() <= root.GetProperty("fileMbps").GetDouble(), "video Mbps is the file's less its container");
                Check.Equal(1280, root.GetProperty("referenceInfo").GetProperty("width").GetInt32());
                Check.True(root.GetProperty("encodeSpeedFps").GetDouble() > 0, "the encode speed is recorded");
                Check.Equal(0, root.GetProperty("outOfOrderOutputs").GetInt32(), "outputs in order");
                var (_, streams) = MediaProbe.Probe(mp4, hideEditLists: true);
                var v = streams.Single(s => s.IsVideo);
                Check.Equal((int)framesOut, v.Count, "the MP4 holds every frame the encoder gave");
                Check.Equal((1280, 720), (v.Width, v.Height));
            }
            Check.Equal(0, code, "every run finished");
        });
    }

    /// <summary>
    /// Benches 3 s of a generated clip with off and the bench-only quality mode (QualityVsSpeed 100 before the media types) through the real
    /// hardware encoder: both runs finish, the JSON says what was requested, when, and what the encoder reads back after streaming began (100
    /// on NVIDIA, where the call before the types is kept), and the quality run's name, mode and speed are recorded.
    /// </summary>
    [UnitTest]
    public static void The_bench_encodes_a_generated_clip_with_the_quality_mode_and_records_the_qvs_read_back()
    {
        if (!ShareTranscoderTests.GpuTestsOn) { ShareTranscoderTests.NotRun(nameof(The_bench_encodes_a_generated_clip_with_the_quality_mode_and_records_the_qvs_read_back)); return; }
        using var dir = new TempDir();
        ShareTranscoderTests.OnMta(() =>
        {
            string reference = ShareTranscoderTests.MakeClip(dir, "reference.mp4", 1280, 720, 60, 4.0);
            var args = new BenchArgs(reference, dir.File("bench"), "h264", new[] { 8.0 }, new[] { EncoderMode.Off }, 3.0, Quality: true);
            int code = EncoderBench.RunAll(args);
            foreach (string name in new[] { EncoderBench.RunName("h264", 8, EncoderMode.Off), EncoderBench.QualityRunName("h264", 8) })
            {
                bool quality = name.EndsWith("_quality", StringComparison.Ordinal);
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(args.OutFolder, name + ".json")));
                var root = doc.RootElement;
                if (root.GetProperty("error").ValueKind != JsonValueKind.Null)
                {
                    string why = root.GetProperty("error").GetString() ?? "";
                    if (why.Contains("hardware", StringComparison.OrdinalIgnoreCase) || why.Contains("decoder", StringComparison.OrdinalIgnoreCase)) Check.Skip("CLINK_TESTS_GPU=1 but " + why);
                    Check.Fail(name + " failed: " + why);
                }
                Check.Equal(quality ? "quality" : "off", root.GetProperty("mode").GetString());
                var q = root.GetProperty("qualityVsSpeed");
                Check.Equal(quality ? "before-types" : "after-types", q.GetProperty("stage").GetString());
                Check.Equal(quality ? 100 : 66, q.GetProperty("requested").GetInt32());
                bool nvidia = root.GetProperty("encoder").GetString()!.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                if (quality && nvidia) Check.Equal(100, q.GetProperty("readBack").GetInt32(), "the value set before the types is kept");
                Check.Equal("CBR", root.GetProperty("applied").GetProperty("rate").GetString());
                Check.Equal(0L, root.GetProperty("dropped").GetInt64());
                Check.True(root.GetProperty("encodeSpeedFps").GetDouble() > 0, "the speed is recorded");
                Console.WriteLine($"[quality-smoke] {name}: {q.GetRawText().Replace("\n", " ")} {root.GetProperty("encodeSpeedFps").GetDouble():0.#} fps");
            }
            Check.Equal(0, code, "every run finished");
        });
    }
}
