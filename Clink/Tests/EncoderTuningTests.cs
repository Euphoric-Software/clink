using System.Text;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Clink.Encode;
using Clink.Engine;
using Clink.Native;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// Slice 4a: the hidden encoder experiment (EncoderTuning.cs). Everything runs against a fake ICodecApiPort, so no GPU is needed, except
/// the B-frame capability probe at the end, which needs CLINK_TESTS_GPU=1 (and an idle PC: it opens the hardware encoders).
/// </summary>
internal static unsafe class EncoderTuningTests
{
    private const eAVEncCommonRateControlMode CbrMode = eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR;
    private const eAVEncCommonRateControlMode VbrMode = eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_PeakConstrainedVBR;

    private static readonly Dictionary<Guid, string> Names = new()
    {
        [KnownGuids.CODECAPI_AVEncCommonRateControlMode] = "rate",
        [KnownGuids.CODECAPI_AVEncCommonMeanBitRate] = "mean",
        [KnownGuids.CODECAPI_AVEncCommonMaxBitRate] = "max",
        [KnownGuids.CODECAPI_AVEncMPVGOPSize] = "gop",
        [KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount] = "bframes",
        [KnownGuids.CODECAPI_AVLowLatencyMode] = "lowlatency",
        [KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed] = "qvs",
    };

    /// <summary>An ICodecAPI that remembers every call, in order, and can refuse or misreport values.</summary>
    private sealed class FakePort : ICodecApiPort
    {
        public readonly List<string> Calls = new();
        public readonly Dictionary<string, uint> Values = new();
        public readonly HashSet<string> Unsupported = new();
        public readonly Dictionary<string, int> SetFails = new();      // key name -> HRESULT SetValue returns
        public readonly Dictionary<string, uint> ReadsAs = new();      // key name -> what GetValue returns instead of the set value
        public readonly HashSet<string> GetFails = new();
        public bool LowLatencyReadsAs = true; public bool LowLatencyNeverReads;
        private static string K(Guid g) => Names.TryGetValue(g, out var n) ? n : g.ToString();

        public bool IsSupported(Guid key) { Calls.Add("supported " + K(key)); return !Unsupported.Contains(K(key)); }
        public int SetU32(Guid key, uint value)
        {
            string k = K(key);
            Calls.Add($"set {k}={value}");
            if (SetFails.TryGetValue(k, out int hr)) return hr;
            Values[k] = value;
            return 0;
        }
        public int SetBool(Guid key, bool value)
        {
            string k = K(key);
            Calls.Add($"setbool {k}={value}");
            if (SetFails.TryGetValue(k, out int hr)) return hr;
            LowLatencyReadsAs = value;
            return 0;
        }
        public int GetU32(Guid key, out uint value)
        {
            string k = K(key);
            Calls.Add("get " + k);
            value = 0;
            if (GetFails.Contains(k)) return unchecked((int)0x80004001);
            if (ReadsAs.TryGetValue(k, out uint other)) { value = other; return 0; }
            return Values.TryGetValue(k, out value) ? 0 : unchecked((int)0x80004001);
        }
        public int GetBool(Guid key, out bool value)
        {
            Calls.Add("getbool " + K(key));
            value = LowLatencyReadsAs;
            return LowLatencyNeverReads ? unchecked((int)0x80004001) : 0;
        }
        public string Describe(Guid key) { Calls.Add("describe " + K(key)); return "fake"; }
    }

    private static readonly VideoEncoderConfig Cfg = new(1920, 1080, 60, 20_000_000, "h264", 1f);

    private static FakePort Run(LiveEncoderTuning? tuning, out AppliedTuning applied, Action<FakePort>? setup = null)
    {
        var p = new FakePort();
        setup?.Invoke(p);
        applied = EncoderExperiments.Apply(p, Cfg, tuning);
        return p;
    }

    private static string Join(IEnumerable<string> calls) => string.Join(" | ", calls);

    // ------------------------------------------------------------------ the parser and the setting

    [UnitTest]
    public static void Parse_reads_the_modes_and_warns_about_unknown_ones()
    {
        Check.Equal((EncoderMode.Off, (string?)null), EncoderExperiments.Parse(null));
        Check.Equal((EncoderMode.Off, (string?)null), EncoderExperiments.Parse(""));
        Check.Equal((EncoderMode.Off, (string?)null), EncoderExperiments.Parse("   "));
        Check.Equal((EncoderMode.Off, (string?)null), EncoderExperiments.Parse("off"));
        Check.Equal((EncoderMode.Off, (string?)null), EncoderExperiments.Parse(" OFF "));
        Check.Equal((EncoderMode.Vbr, (string?)null), EncoderExperiments.Parse("vbr"));
        Check.Equal((EncoderMode.Vbr, (string?)null), EncoderExperiments.Parse("  VBR"));
        Check.Equal((EncoderMode.LowLatencyOff, (string?)null), EncoderExperiments.Parse("lowlatency-off"));
        Check.Equal((EncoderMode.VbrLowLatencyOff, (string?)null), EncoderExperiments.Parse("Vbr-LowLatency-Off"));
        foreach (string bad in new[] { "cbr", "vbr-bframes2", "true", "1", "vbr lowlatency-off", "x" })
        {
            var (mode, warning) = EncoderExperiments.Parse(bad);
            Check.Equal(EncoderMode.Off, mode, bad);
            Check.NotNull(warning, bad + ": says it was not understood");
            Check.Contains(warning!, bad.ToLowerInvariant());
        }
        Check.True(EncoderExperiments.Parse(new string('z', 500)).Warning!.Length < 120, "a long junk value does not flood the log");
        foreach (EncoderMode m in Enum.GetValues<EncoderMode>())
            Check.Equal(m, EncoderExperiments.Parse(EncoderExperiments.Name(m)).Mode, "every mode parses back from its name");
    }

    [UnitTest]
    public static void Resolve_gives_null_for_off_and_forces_cbr_under_a_size_target()
    {
        Check.Null(EncoderExperiments.Resolve(null, false));
        Check.Null(EncoderExperiments.Resolve("off", true));
        Check.Null(EncoderExperiments.Resolve("garbage", false), "an unknown value is off");
        var v = EncoderExperiments.Resolve("vbr", false)!;
        Check.Equal(EncoderMode.Vbr, v.Mode);
        Check.False(v.ForceCbr);
        Check.True(v.WantsVbr);
        Check.True(v.LowLatency, "vbr alone keeps low latency on");
        Check.True(EncoderExperiments.Resolve("vbr", true)!.ForceCbr, "a size target forces CBR");
        Check.False(EncoderExperiments.Resolve("lowlatency-off", false)!.WantsVbr);
        Check.False(EncoderExperiments.Resolve("lowlatency-off", false)!.LowLatency);
        Check.True(EncoderExperiments.Resolve("vbr-lowlatency-off", false)!.WantsVbr);
        Check.False(EncoderExperiments.Resolve("vbr-lowlatency-off", false)!.LowLatency);
        Check.Equal(0u, EncoderExperiments.Resolve("vbr-lowlatency-off", false)!.BFrames, "the live path always asks for 0 B-frames");
    }

    [UnitTest]
    public static void The_retired_vbr_bframes_mode_is_off_with_a_warning_that_says_so_and_the_value_is_kept()
    {
        var (mode, warning) = EncoderExperiments.Parse("vbr-bframes");
        Check.Equal(EncoderMode.Off, mode);
        Check.NotNull(warning);
        Check.Contains(warning!, "vbr-bframes");
        Check.Contains(warning!, "retired");
        Check.Contains(warning!, "vbr-lowlatency-off");
        Check.Equal(EncoderMode.Off, EncoderExperiments.Parse("  VBR-BFRAMES ").Mode, "any case, spaces ignored");
        Check.Null(EncoderExperiments.Resolve("vbr-bframes", false), "no tuning is handed to the engine: the recording is set up as before");
        Check.False(Enum.GetNames<EncoderMode>().Any(n => n.Contains("Bframes", StringComparison.OrdinalIgnoreCase)), "no mode is left for it");
        // The stored value is not rewritten (an unknown value is kept as it is, as for any other).
        var kept = Settings.FromJson(new Settings { EncoderExperiment = "vbr-bframes" }.ToJson());
        Check.Equal("vbr-bframes", kept.EncoderExperiment);
        Check.Equal("vbr-bframes", Settings.FromJson(kept.ToJson()).EncoderExperiment, "and survives another save");
    }

    [UnitTest]
    public static void The_setting_is_absent_by_default_round_trips_and_keeps_an_unknown_value()
    {
        var def = new Settings();
        Check.Null(def.EncoderExperiment);
        Check.DoesNotContain(def.ToJson(), "EncoderExperiment", "never written by default");
        var s = new Settings { EncoderExperiment = "vbr-lowlatency-off" };
        Check.Contains(s.ToJson(), "\"EncoderExperiment\": \"vbr-lowlatency-off\"");
        Check.Equal("vbr-lowlatency-off", Settings.FromJson(s.ToJson()).EncoderExperiment);
        var odd = Settings.FromJson(new Settings { EncoderExperiment = "from-the-future" }.ToJson());
        Check.Equal("from-the-future", odd.EncoderExperiment, "an unknown value is kept as it is");
        Check.Equal(EncoderMode.Off, EncoderExperiments.Parse(odd.EncoderExperiment).Mode);
        Check.Equal("from-the-future", Settings.FromJson(odd.ToJson()).EncoderExperiment, "and survives another save");
    }

    [UnitTest]
    public static void The_setting_is_this_machines_own_and_is_never_imported()
    {
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.EncoderExperiment)));
        Check.True(SettingsImport.IsOwnKey("encoderexperiment"), "any case");
        string merged = SettingsImport.KeepOurOwn(new Settings { EncoderExperiment = "vbr" }.ToJson(), new Settings { EncoderExperiment = "lowlatency-off" }.ToJson(), keepOurToken: true);   // (theirs, ours)
        Check.Equal("lowlatency-off", Settings.FromJson(merged).EncoderExperiment, "ours stays");
        Check.Null(Settings.FromJson(SettingsImport.KeepOurOwn(new Settings { EncoderExperiment = "vbr" }.ToJson(), new Settings().ToJson(), keepOurToken: true)).EncoderExperiment,
            "another copy's experiment does not arrive");
    }

    // ------------------------------------------------------------------ Off = today

    [UnitTest]
    public static void Off_makes_exactly_todays_codec_api_calls_in_todays_order()
    {
        // Written out by hand from ConfigureCodecApi as it was before slice 4: IsSupported then SetValue for each, low latency without
        // IsSupported, no read-back, no probe.
        string[] today =
        {
            "supported rate", $"set rate={(uint)CbrMode}",
            "supported mean", "set mean=20000000",
            "supported max", "set max=20000000",
            "supported gop", "set gop=60",
            "supported bframes", "set bframes=0",
            "setbool lowlatency=True",
            "supported qvs", "set qvs=66",
        };
        Check.Equal(today, Run(null, out var applied).Calls.ToArray(), "no tuning");
        Check.Equal(today, Run(null, out _).Calls.ToArray(), "and again: the same");
        Check.False(applied.Vbr);
        Check.Equal(20_000_000u, applied.MeanBps);
        Check.Equal(20_000_000u, applied.MaxBps);
        Check.True(applied.LowLatency);
        Check.Equal(0u, applied.BFrames);
        Check.Equal(66u, applied.QualityVsSpeed);
        Check.Equal(60u, applied.Gop);
        Check.Null(applied.Fallback);
        Check.Null(applied.Report);

        // An unsupported key is skipped, as before (NVENC has no B-frame count).
        var nvenc = Run(null, out _, p => p.Unsupported.Add("bframes"));
        Check.False(nvenc.Calls.Contains("set bframes=0"));
        Check.True(nvenc.Calls.Contains("supported bframes"));

        // A refused value is only logged, as before: the next call is still made.
        var refused = Run(null, out var ap2, p => p.SetFails["rate"] = unchecked((int)0x80004005));
        Check.Equal(today.Length, refused.Calls.Count);
        Check.True(ap2.LowLatency);
    }

    [UnitTest]
    public static void Off_uses_cbr_for_a_fractional_gop_and_rounds_like_before()
    {
        var cfg = new VideoEncoderConfig(1280, 720, 30, 5_000_000, "hevc", 0.25f);
        var p = new FakePort();
        EncoderExperiments.Apply(p, cfg, null);
        Check.True(p.Calls.Contains("set gop=8"), "round(30 * 0.25) = 8");
        Check.True(p.Calls.Contains("set mean=5000000"));
    }

    // ------------------------------------------------------------------ QualityVsSpeed before the media types (bench only)

    [UnitTest]
    public static void With_no_qvs_before_the_types_the_calls_are_todays_and_the_option_is_null_in_the_app()
    {
        Check.Null(VideoEncoderOptions.Live.QualityVsSpeedBeforeTypes, "the recording's options never carry it");
        Check.Null(VideoEncoderOptions.Offline().QualityVsSpeedBeforeTypes);
        string[] today =
        {
            "supported rate", $"set rate={(uint)CbrMode}", "supported mean", "set mean=20000000", "supported max", "set max=20000000",
            "supported gop", "set gop=60", "supported bframes", "set bframes=0", "setbool lowlatency=True", "supported qvs", "set qvs=66",
        };
        var p = new FakePort();
        var applied = EncoderExperiments.Apply(p, Cfg, null, null);   // the argument VideoEncoder passes from the null option
        Check.Equal(today, p.Calls.ToArray(), "an explicit null changes nothing");
        Check.Equal(66u, applied.QualityVsSpeed);
        Check.Equal(Join(Run(null, out _).Calls), Join(p.Calls), "the same as the 3-argument form");
    }

    [UnitTest]
    public static void The_early_codec_api_step_runs_only_for_a_tuning_or_a_qvs_before_the_types()
    {
        Check.False(VideoEncoder.NeedsCodecApiBeforeTypes(VideoEncoderOptions.Live), "Live");
        Check.False(VideoEncoder.NeedsCodecApiBeforeTypes(VideoEncoderOptions.Offline(ThreadPriority.BelowNormal)), "the share transcoder's Offline");
        Check.False(VideoEncoder.NeedsCodecApiBeforeTypes(VideoEncoderOptions.Offline()), "Offline()");
        // What Engine builds with the experiment off: no tuning, so no options at all, and the encoder then uses Live.
        LiveEncoderTuning? none = EncoderExperiments.Resolve(null, false);
        VideoEncoderOptions? engineOff = none == null ? null : VideoEncoderOptions.Live with { LiveTuning = none };
        Check.Null(engineOff);
        Check.False(VideoEncoder.NeedsCodecApiBeforeTypes(engineOff ?? VideoEncoderOptions.Live), "Engine, experiment off");

        Check.True(VideoEncoder.NeedsCodecApiBeforeTypes(VideoEncoderOptions.Live with { LiveTuning = new LiveEncoderTuning(EncoderMode.Vbr) }), "a tuning");
        Check.True(VideoEncoder.NeedsCodecApiBeforeTypes(VideoEncoderOptions.Live with { QualityVsSpeedBeforeTypes = 100 }), "the QvS field");
        Check.True(VideoEncoder.NeedsCodecApiBeforeTypes(VideoEncoderOptions.Live with { QualityVsSpeedBeforeTypes = 0 }), "the QvS field, even 0");
        Check.False(VideoEncoder.NeedsCodecApiBeforeTypes(VideoEncoderOptions.Offline() with { LiveTuning = new LiveEncoderTuning(EncoderMode.Vbr) }), "an offline (blocking) encoder never");
    }

    [UnitTest]
    public static void A_qvs_set_before_the_types_is_the_only_difference_and_is_not_overridden_afterwards()
    {
        var p = new FakePort();
        var applied = EncoderExperiments.Apply(p, Cfg, null, 100, new QvsBeforeTypesResult(100, 0, 100));
        Check.Equal(new[]
        {
            "supported rate", $"set rate={(uint)CbrMode}", "supported mean", "set mean=20000000", "supported max", "set max=20000000",
            "supported gop", "set gop=60", "supported bframes", "set bframes=0", "setbool lowlatency=True",
        }, p.Calls.ToArray(), "CBR exactly like off, and no QualityVsSpeed call after the types");
        Check.Equal((uint?)100u, applied.QualityVsSpeed, "reported as what the early call read back");
        Check.Null(EncoderExperiments.Apply(new FakePort(), Cfg, null, 100, new QvsBeforeTypesResult(100, unchecked((int)0x80004005), null)).QualityVsSpeed, "an early set that failed is not reported as applied");
        Check.Null(EncoderExperiments.Apply(new FakePort(), Cfg, null, 100, new QvsBeforeTypesResult(100, 0, null)).QualityVsSpeed, "unreadable: unknown");
        Check.Null(EncoderExperiments.Apply(new FakePort(), Cfg, null, 100, null).QualityVsSpeed, "no early result: unknown");
        Check.Equal((uint?)33u, EncoderExperiments.Apply(new FakePort(), Cfg, null, 100, new QvsBeforeTypesResult(100, 0, 33)).QualityVsSpeed, "what the encoder kept");
        Check.False(applied.Vbr);

        var tuned = new FakePort();
        EncoderExperiments.Apply(tuned, Cfg, new LiveEncoderTuning(EncoderMode.Vbr), 100);
        Check.False(tuned.Calls.Any(c => c.Contains("qvs")), "a tuning does not bring the call back: " + Join(tuned.Calls));

        var before = new FakePort();
        var r = EncoderExperiments.ApplyQualityVsSpeedBeforeTypes(before, 100);
        Check.Equal(new[] { "set qvs=100", "get qvs" }, before.Calls.ToArray(), "one set and one read-back, nothing else");
        Check.Equal(100u, r.Requested);
        Check.Equal(0, r.SetHr);
        Check.Equal(100u, r.ReadBack!.Value);

        var refused = new FakePort(); refused.SetFails["qvs"] = unchecked((int)0x80004005);
        var rr = EncoderExperiments.ApplyQualityVsSpeedBeforeTypes(refused, 100);
        Check.True(rr.SetHr < 0, "the failure is in the result");
        Check.Null(rr.ReadBack, "and nothing was set to read");
        var ignored = new FakePort(); ignored.ReadsAs["qvs"] = 33;
        Check.Equal(33u, EncoderExperiments.ApplyQualityVsSpeedBeforeTypes(ignored, 100).ReadBack!.Value, "a swallowed value shows as the read-back");
    }

    [UnitTest]
    public static void The_option_is_refused_by_an_offline_encoder_and_the_live_setting_never_accepts_quality()
    {
        var cfg = new VideoEncoderConfig(1920, 1080, 60, 16_000_000, "h264", 1f);
        Check.Throws<ArgumentException>(() => { using var e = new VideoEncoder(null!, cfg, VideoEncoderOptions.Offline() with { QualityVsSpeedBeforeTypes = 100 }); });
        var (mode, warning) = EncoderExperiments.Parse("quality");
        Check.Equal(EncoderMode.Off, mode);
        Check.NotNull(warning, "the live parser does not know it");
        Check.Null(EncoderExperiments.Resolve("quality", false), "the recording stays on today's setup");
        Check.False(Enum.GetNames<EncoderMode>().Any(n => n.Contains("Quality", StringComparison.OrdinalIgnoreCase)), "no EncoderMode for it");
    }

    // ------------------------------------------------------------------ the modes

    [UnitTest]
    public static void Vbr_sets_peak_constrained_vbr_with_mean_the_target_and_max_one_and_a_half_times()
    {
        var p = Run(new LiveEncoderTuning(EncoderMode.Vbr), out var a, null);
        Check.Equal((uint)VbrMode, p.Values["rate"]);
        Check.Equal(20_000_000u, p.Values["mean"]);
        Check.Equal(30_000_000u, p.Values["max"]);
        Check.Equal(0u, p.Values["bframes"]);
        Check.Equal(66u, p.Values["qvs"]);
        Check.True(a.Vbr);
        Check.Equal(20_000_000u, a.MeanBps);
        Check.Equal(30_000_000u, a.MaxBps);
        Check.Equal(30.0, a.PeakMbps);
        Check.True(a.LowLatency, "vbr alone keeps low latency on");
        Check.Equal(0u, a.BFrames);
        Check.Null(a.Fallback);
        Check.True(p.Calls.Contains("get rate") && p.Calls.Contains("get max"), "values are read back");
        Check.True(p.Calls.Contains("setbool lowlatency=True"));
        Check.NotNull(a.Report);
        Check.Equal(4, p.Calls.Count(c => c.StartsWith("describe ")), "the capability probe asks for the four keys");
        int rate = p.Calls.IndexOf($"set rate={(uint)VbrMode}"), mean = p.Calls.IndexOf("set mean=20000000"), max = p.Calls.IndexOf("set max=30000000");
        Check.True(rate >= 0 && rate < mean && mean < max, "rate, mean, max, like the recording's own order");
    }

    [UnitTest]
    public static void Lowlatency_off_stays_cbr_and_asks_for_low_latency_off()
    {
        var p = Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff), out var a, null);
        Check.Equal((uint)CbrMode, p.Values["rate"]);
        Check.Equal(20_000_000u, p.Values["max"]);
        Check.True(p.Calls.Contains("setbool lowlatency=False"));
        Check.False(p.Calls.Contains("setbool lowlatency=True"));
        Check.False(a.LowLatency);
        Check.False(a.Vbr);
        Check.Equal(20_000_000u, a.MaxBps);
        Check.Null(a.Fallback);
    }

    [UnitTest]
    public static void Vbr_lowlatency_off_applies_both()
    {
        var p = Run(new LiveEncoderTuning(EncoderMode.VbrLowLatencyOff), out var a, null);
        Check.Equal((uint)VbrMode, p.Values["rate"]);
        Check.Equal(30_000_000u, p.Values["max"]);
        Check.True(p.Calls.Contains("setbool lowlatency=False"));
        Check.True(a.Vbr);
        Check.False(a.LowLatency);
        Check.Null(a.Fallback);
    }

    [UnitTest]
    public static void Every_mode_asks_for_zero_bframes_and_none_reports_a_bframe_fallback()
    {
        foreach (EncoderMode mode in Enum.GetValues<EncoderMode>().Where(m => m != EncoderMode.Off))
        {
            var p = Run(new LiveEncoderTuning(mode), out var a, null);
            Check.Equal(0u, p.Values["bframes"], mode.ToString());
            Check.Equal(0u, a.BFrames, mode.ToString());
            Check.Null(a.Fallback, mode + ": nothing about B-frames is reported");
        }
    }

    [UnitTest]
    public static void A_size_target_forces_cbr()
    {
        foreach (var mode in new[] { EncoderMode.Vbr, EncoderMode.VbrLowLatencyOff })
        {
            var p = Run(new LiveEncoderTuning(mode, ForceCbr: true), out var a, null);
            Check.Equal((uint)CbrMode, p.Values["rate"], mode.ToString());
            Check.False(p.Calls.Contains($"set rate={(uint)VbrMode}"), mode + ": VBR is never even tried");
            Check.Equal(20_000_000u, p.Values["max"], mode.ToString());
            Check.False(a.Vbr, mode.ToString());
            Check.Equal(20_000_000u, a.MaxBps, mode + ": the ring is sized for the CBR rate");
        }
        // Low latency still follows the mode under a size target.
        var ll = Run(new LiveEncoderTuning(EncoderMode.VbrLowLatencyOff, ForceCbr: true), out var la, null);
        Check.False(la.LowLatency);
        Check.True(ll.Calls.Contains("setbool lowlatency=False"));
        Check.Equal("vbr (size target: CBR)", new LiveEncoderTuning(EncoderMode.Vbr, ForceCbr: true).ToString());
    }

    // ------------------------------------------------------------------ fallbacks

    [UnitTest]
    public static void Fallback_when_the_rate_mode_is_refused_unsupported_or_reads_back_different()
    {
        var cases = new (string name, Action<FakePort> setup)[]
        {
            ("set refused", p => p.SetFails["rate"] = unchecked((int)0x80004005)),
            ("not supported", p => p.Unsupported.Add("rate")),
            ("reads back CBR", p => p.ReadsAs["rate"] = (uint)CbrMode),
            ("cannot be read back", p => p.GetFails.Add("rate")),
        };
        foreach (var (name, setup) in cases)
        {
            var p = Run(new LiveEncoderTuning(EncoderMode.Vbr), out var a, setup);
            Check.False(a.Vbr, name);
            Check.Equal(20_000_000u, a.MaxBps, name + ": the ring follows the CBR rate");
            Check.Contains(a.Fallback ?? "", "rate mode", name);
            Check.Contains(a.Fallback ?? "", "CBR", name);
            Check.False(p.Calls.Contains("set max=30000000"), name + ": no peak is asked for once VBR is off");
            Check.True(p.Calls.Contains("set max=20000000"), name);
        }
        // The fallback sets CBR again explicitly (when the encoder takes sets at all).
        var again = Run(new LiveEncoderTuning(EncoderMode.Vbr), out _, p => p.ReadsAs["rate"] = (uint)CbrMode);
        Check.True(again.Calls.Count(c => c == $"set rate={(uint)CbrMode}") == 1);
    }

    [UnitTest]
    public static void Fallback_to_cbr_when_the_max_bitrate_is_rejected()
    {
        var cases = new (string name, Action<FakePort> setup)[]
        {
            ("set refused", p => p.SetFails["max"] = unchecked((int)0x80070057)),
            ("not supported", p => p.Unsupported.Add("max")),
            ("reads back different", p => p.ReadsAs["max"] = 20_000_000),
        };
        foreach (var (name, setup) in cases)
        {
            var p = Run(new LiveEncoderTuning(EncoderMode.VbrLowLatencyOff), out var a, setup);
            Check.False(a.Vbr, name);
            Check.Equal(20_000_000u, a.MaxBps, name);
            Check.Contains(a.Fallback ?? "", "max bitrate", name);
            Check.True(p.Calls.Contains($"set rate={(uint)CbrMode}"), name + ": back to CBR");
            Check.False(a.LowLatency, name + ": low latency off is a separate setting and still applies");
        }
        // A max that cannot be read back is accepted (it was set without complaint).
        Run(new LiveEncoderTuning(EncoderMode.Vbr), out var u, p => p.GetFails.Add("max"));
        Check.True(u.Vbr);
        Check.Equal(30_000_000u, u.MaxBps);
    }

    [UnitTest]
    public static void Low_latency_stays_on_when_it_cannot_be_turned_off()
    {
        Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff), out var a, p => p.SetFails["lowlatency"] = unchecked((int)0x80004005));
        Check.True(a.LowLatency, "set refused: applied is on");
        Check.Contains(a.Fallback ?? "", "low latency stays on");

        // Accepted but it reads back on (the set was ignored).
        var ignored = new FakePort();
        var ap = EncoderExperiments.Apply(new IgnoringLowLatency(ignored), Cfg, new LiveEncoderTuning(EncoderMode.VbrLowLatencyOff));
        Check.True(ap.LowLatency, "reads back on: applied is on");
        Check.True(ap.Vbr, "the VBR half is not affected");
        Check.Contains(ap.Fallback ?? "", "reads back on");

        Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff), out var ur, p => p.LowLatencyNeverReads = true);
        Check.True(ur.LowLatency, "not verifiable: reported on");

        var honest = Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff), out var ok, null);
        Check.False(ok.LowLatency);
        Check.True(honest.Calls.Contains("getbool lowlatency"));
    }

    /// <summary>Takes SetBool but keeps reporting low latency on.</summary>
    private sealed class IgnoringLowLatency : ICodecApiPort
    {
        private readonly FakePort _inner;
        public IgnoringLowLatency(FakePort inner) => _inner = inner;
        public bool IsSupported(Guid key) => _inner.IsSupported(key);
        public int SetU32(Guid key, uint value) => _inner.SetU32(key, value);
        public int SetBool(Guid key, bool value) => 0;
        public int GetU32(Guid key, out uint value) => _inner.GetU32(key, out value);
        public int GetBool(Guid key, out bool value) { value = true; return 0; }
        public string Describe(Guid key) => "fake";
    }

    [UnitTest]
    public static void Bframes_state_is_reported_for_the_probe()
    {
        var ok = Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff, BFrames: 2), out var a, null);
        Check.Equal("ok", a.BFramesState);
        Check.Equal(2u, ok.Values["bframes"]);
        Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff, BFrames: 2), out var b, p => p.Unsupported.Add("bframes"));
        Check.Equal("unsupported", b.BFramesState);
        Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff, BFrames: 2), out var c, p => p.ReadsAs["bframes"] = 0);
        Check.Equal("reads back 0", c.BFramesState);
        Run(new LiveEncoderTuning(EncoderMode.LowLatencyOff, BFrames: 2), out var d, p => p.SetFails["bframes"] = -1);
        Check.Equal("rejected", d.BFramesState);
        Check.Equal(2u, a.BFrames);
        Check.Equal(0u, c.BFrames, "what the encoder reads back, not what was asked");
        Check.Equal(2u, d.BFrames, "not readable (rejected): the request");
    }

    // ------------------------------------------------------------------ before the media types

    private static readonly uint VbrU = (uint)VbrMode, CbrU = (uint)CbrMode;

    [UnitTest]
    public static void A_tuned_mode_sets_rate_bitrates_and_low_latency_before_the_types_then_runs_the_full_setup_after()
    {
        var p = new FakePort();
        var tuning = new LiveEncoderTuning(EncoderMode.VbrLowLatencyOff);
        EncoderExperiments.ApplyBeforeTypes(p, Cfg, tuning);   // (VideoEncoder.Open: before SetOutputType)
        string[] pre = { $"set rate={VbrU}", "set mean=20000000", "set max=30000000", "setbool lowlatency=False" };
        Check.Equal(pre, p.Calls.ToArray(), "exactly these four, in this order, no read-back");
        int before = p.Calls.Count;
        var applied = EncoderExperiments.Apply(p, Cfg, tuning);   // (after SetInputType)
        Check.Equal(pre, p.Calls.Take(4).ToArray(), "the pre-type calls came first");
        Check.True(p.Calls[before].StartsWith("describe "), "then the post-type setup begins");
        int postRate = p.Calls.IndexOf($"set rate={VbrU}", before), postGet = p.Calls.IndexOf("get rate", before);
        Check.True(postRate > before && postGet > postRate, "and sets and reads the rate mode again");
        Check.True(p.Calls.IndexOf("getbool lowlatency", before) > 0, "and reads low latency back");
        Check.True(applied.Vbr && !applied.LowLatency && applied.Fallback == null);

        var cbr = new FakePort();
        EncoderExperiments.ApplyBeforeTypes(cbr, Cfg, new LiveEncoderTuning(EncoderMode.LowLatencyOff));
        Check.Equal(new[] { $"set rate={CbrU}", "set mean=20000000", "set max=20000000", "setbool lowlatency=False" }, cbr.Calls.ToArray(), "CBR mode: max = mean");

        var vbrOn = new FakePort();
        EncoderExperiments.ApplyBeforeTypes(vbrOn, Cfg, new LiveEncoderTuning(EncoderMode.Vbr));
        Check.Equal("setbool lowlatency=True", vbrOn.Calls[3], "low latency stays on for vbr");

        var sized = new FakePort();
        EncoderExperiments.ApplyBeforeTypes(sized, Cfg, new LiveEncoderTuning(EncoderMode.Vbr, ForceCbr: true));
        Check.Equal(new[] { $"set rate={CbrU}", "set mean=20000000", "set max=20000000", "setbool lowlatency=True" }, sized.Calls.ToArray(), "a size target: CBR before the types too");
    }

    [UnitTest]
    public static void Off_makes_no_calls_before_the_types()
    {
        // Covers only Resolve/Apply for Off. The !Blocking && LiveTuning gate in VideoEncoder.Open is checked by review (the Engine cannot run without a GPU).
        // VideoEncoder.Open calls ApplyBeforeTypes only for a non-null LiveTuning (Resolve gives null for Off), and Apply(null) starts
        // with today's first call and nothing else.
        Check.Null(EncoderExperiments.Resolve("off", false));
        Check.Null(EncoderExperiments.Resolve(null, false));
        Check.Equal("supported rate", Run(null, out _).Calls[0]);
        Check.False(Run(null, out _).Calls.Any(c => c.StartsWith("get") || c.StartsWith("describe")), "and no read-back or probe");
    }

    private sealed class ThrowingPort : ICodecApiPort
    {
        public bool IsSupported(Guid key) => throw new InvalidOperationException("no codec api");
        public int SetU32(Guid key, uint value) => throw new InvalidOperationException("no codec api");
        public int SetBool(Guid key, bool value) => throw new InvalidOperationException("no codec api");
        public int GetU32(Guid key, out uint value) => throw new InvalidOperationException("no codec api");
        public int GetBool(Guid key, out bool value) => throw new InvalidOperationException("no codec api");
        public string Describe(Guid key) => throw new InvalidOperationException("no codec api");
    }

    [UnitTest]
    public static void A_failed_set_before_the_types_does_not_change_the_fallback_decisions_after_them()
    {
        var tuning = new LiveEncoderTuning(EncoderMode.VbrLowLatencyOff);
        var clean = new FakePort();
        var cleanApplied = EncoderExperiments.Apply(clean, Cfg, tuning);

        var p = new FakePort();
        foreach (string k in new[] { "rate", "mean", "max", "lowlatency" }) p.SetFails[k] = unchecked((int)0x80004005);
        EncoderExperiments.ApplyBeforeTypes(p, Cfg, tuning);
        Check.Equal(4, p.Calls.Count, "each was tried, none stopped the rest");
        p.SetFails.Clear();
        var applied = EncoderExperiments.Apply(p, Cfg, tuning);
        Check.Equal(cleanApplied.Describe(), applied.Describe(), "same result as with no pre-type call");
        Check.Equal(cleanApplied.Fallback, applied.Fallback);
        Check.True(applied.Vbr && !applied.LowLatency);

        // A rate mode that only takes after the types, and then reads back CBR: the post-type read-back still falls back.
        var q = new FakePort();
        EncoderExperiments.ApplyBeforeTypes(q, Cfg, new LiveEncoderTuning(EncoderMode.Vbr));
        q.ReadsAs["rate"] = CbrU;
        var fb = EncoderExperiments.Apply(q, Cfg, new LiveEncoderTuning(EncoderMode.Vbr));
        Check.False(fb.Vbr, "the read-back after the types decides, whatever the early set said");
        Check.Contains(fb.Fallback ?? "", "rate mode");

        // No ICodecAPI at all: nothing escapes.
        EncoderExperiments.ApplyBeforeTypes(new ThrowingPort(), Cfg, tuning);
    }

    [UnitTest]
    public static void A_save_that_spans_an_encoder_reopen_is_refused()
    {
        // Covers only the pure helper. Where SaveClip reads the generation (before _encoder) is checked by review (the Engine cannot run without a GPU).
        Engine.Engine.ThrowIfEncoderRestarted(3, 3);   // same generation: fine
        bool threw = false;
        try { Engine.Engine.ThrowIfEncoderRestarted(3, 4); }
        catch (InvalidOperationException ex) { threw = true; Check.Contains(ex.Message, "encoder restarted"); Check.Contains(ex.Message, "try again"); Check.Equal("The encoder restarted, try again", Clink.Services.Localization.UserText.Of(ex), "the user text (English here) rides on the exception"); }
        Check.True(threw, "a changed generation throws the kind of exception the save's callers already show");
    }

    // ------------------------------------------------------------------ the ring

    [UnitTest]
    public static void The_ring_is_sized_from_the_applied_peak_under_vbr()
    {
        Run(new LiveEncoderTuning(EncoderMode.Vbr), out var vbr, null);
        Run(null, out var off, null);
        Check.Equal(20.0, EncoderExperiments.RingMbps(20.0, off), "Off: the configured bitrate, exactly");
        Check.Equal(20.0, EncoderExperiments.RingMbps(20.0, null), "no ICodecAPI: the configured bitrate");
        Check.Equal(30.0, EncoderExperiments.RingMbps(20.0, vbr), "VBR: the peak");
        long mean = RingStore.BytesFor(20.0, 45), peak = RingStore.BytesFor(EncoderExperiments.RingMbps(20.0, vbr), 45);
        Check.True(peak > mean, "bigger");
        Check.True(Math.Abs(peak / (double)mean - 1.5) < 0.02, "about 1.5x: " + peak / (double)mean);
        Check.True(RingStore.SecondsFor(30.0, peak) >= 45, "the replay length holds at the peak");
        Check.True(RingStore.SecondsFor(20.0, mean) >= 45);
        // A VBR that fell back to CBR sizes from the CBR rate.
        Run(new LiveEncoderTuning(EncoderMode.Vbr), out var fb, p => p.SetFails["rate"] = -1);
        Check.Equal(20.0, EncoderExperiments.RingMbps(20.0, fb));
    }

    // ------------------------------------------------------------------ the offline (share) path stays pinned

    [UnitTest]
    public static void A_share_copy_never_takes_a_tuning_and_stays_cbr_with_no_bframes_and_low_latency()
    {
        Check.Null(VideoEncoderOptions.Offline().LiveTuning);
        Check.Null(VideoEncoderOptions.Offline(ThreadPriority.Normal, new OfflineEncoderSettings()).LiveTuning);
        Check.Null(VideoEncoderOptions.Live.LiveTuning, "the default live options are today's");
        var pinned = OfflineEncoderSettings.Default;
        Check.True(pinned.LowLatency);
        Check.Equal(66u, pinned.QualityVsSpeed);
        Check.Equal(0u, pinned.BFrames);
        Check.Equal(CbrMode, pinned.RateControl);
        Check.Equal(CbrMode, VideoEncoderOptions.Offline().OfflineSettings!.RateControl);
        Check.True(VideoEncoderOptions.Offline().Blocking);

        var tuned = new VideoEncoderOptions(true, ThreadPriority.BelowNormal, null, new LiveEncoderTuning(EncoderMode.Vbr));
        bool threw = false;
        try { _ = new VideoEncoder(null!, Cfg, tuned); }
        catch (ArgumentException) { threw = true; }
        Check.True(threw, "Blocking plus a live tuning is refused before anything opens");

        var live = VideoEncoderOptions.Live with { LiveTuning = new LiveEncoderTuning(EncoderMode.Vbr) };
        Check.False(live.Blocking);
        Check.NotNull(live.LiveTuning);
    }

    // ------------------------------------------------------------------ the order guard

    [UnitTest]
    public static void The_order_guard_accepts_increasing_pts_and_latches_on_the_first_regression()
    {
        var g = new OutputOrderGuard();
        Check.True(g.Accept(100));
        Check.True(g.Accept(101));
        Check.True(g.Accept(5_000_000));
        Check.False(g.Tripped);
        Check.False(g.Accept(4_000_000), "a regression");
        Check.True(g.Tripped);
        Check.Contains(g.Detail ?? "", "4000000");
        Check.False(g.Accept(9_000_000), "latched: later samples are refused too");
        Check.True(g.Tripped);

        var equal = new OutputOrderGuard();
        Check.True(equal.Accept(10));
        Check.False(equal.Accept(10), "strictly increasing: equal counts as a regression");
        Check.True(equal.Tripped);

        var first = new OutputOrderGuard();
        Check.True(first.Accept(long.MinValue + 1), "any first value");
        Check.True(first.Accept(long.MinValue + 2));
    }

    // ------------------------------------------------------------------ the B-frame capability probe (CLINK_TESTS_GPU=1, run it on an idle PC)

    private sealed class ProbeResult
    {
        public readonly object Gate = new();
        public bool Regressed; public long Last = long.MinValue; public int Outputs; public int MaxInFlight;
        public string FirstRegression = "";
    }

    private static void Say(string s) { Console.WriteLine(s); Log.Info("[bframes-probe] " + s); }

    /// <summary>
    /// Design risk 1: does the hardware H.264 / HEVC encoder take a B-picture count with low latency off, and does its output come out
    /// reordered (pts going backwards) with decode timestamps? Prints one `BFRAMES &lt;codec&gt;: ...` line per codec for B-frames = 2, and one
    /// `BFRAMES &lt;codec&gt; control(bframes=0): ...` line for B-frames = 0 (NVENC may reorder anyway). A measurement: it fails only when
    /// something throws, and skips when no codec opens. Run: Clink.exe --unittest The_bframe_capability_probe, with CLINK_TESTS_GPU=1.
    /// </summary>
    [UnitTest]
    public static void The_bframe_capability_probe_for_the_hardware_encoders()
    {
        if (!ShareTranscoderTests.GpuTestsOn) { ShareTranscoderTests.NotRun(nameof(The_bframe_capability_probe_for_the_hardware_encoders)); return; }
        ShareTranscoderTests.OnMta(() =>
        {
            using var d3d = D3DDevice.CreateForAdapter(null, lowPriority: false);
            Say($"adapter: {d3d.AdapterName}");
            int opened = 0;
            foreach (string codec in new[] { "h264", "hevc" })
            {
                foreach (uint bframes in new uint[] { 2, 0 })
                {
                    string label = bframes == 2 ? $"BFRAMES {codec}" : $"BFRAMES {codec} control(bframes=0)";
                    var cfg = new VideoEncoderConfig(1280, 720, 60, 8_000_000, codec, 1f);
                    var tuning = new LiveEncoderTuning(EncoderMode.LowLatencyOff, BFrames: bframes);
                    VideoEncoder enc;
                    try { enc = new VideoEncoder(d3d, cfg, VideoEncoderOptions.Live with { LiveTuning = tuning }); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                    {
                        Say($"{label}: no encoder ({ex.Message})");
                        break;
                    }
                    using (enc)
                    {
                        opened++;
                        var applied = enc.Applied;
                        if (bframes == 2 && applied?.Report != null) Say($"{codec} {enc.EncoderName} capabilities:\n{applied.Report}");
                        var res = new ProbeResult();
                        enc.OnOutput = (pts, key, data) =>
                        {
                            lock (res.Gate)
                            {
                                if (pts <= res.Last && !res.Regressed) { res.Regressed = true; res.FirstRegression = $"{pts} after {res.Last}"; }
                                res.Last = Math.Max(res.Last, pts);
                                res.Outputs++;
                            }
                        };
                        FeedSynthetic(d3d, enc, cfg, 60, res);
                        // Give the encoder time to hand back what it holds (a live encoder is not drained).
                        for (int i = 0; i < 60 && enc.FramesInFlight > 0; i++) Thread.Sleep(50);
                        string supported = applied == null ? "no-icodecapi" : applied.BFramesState == "unsupported" ? "no" : applied.BFramesState == "rejected" ? "rejected" : "yes";
                        string readback = applied?.BFramesState switch { null => "n/a", "ok" => bframes.ToString(), var s => s };
                        bool dts = enc.OutputsWithDts > 0;
                        lock (res.Gate)
                        {
                            Say($"{label}: supported={supported}, readback={readback}, reordered={(res.Regressed ? "yes (" + res.FirstRegression + ")" : "no")}, dts={(dts ? "yes" : "no")}" +
                                $" [lowlatency={(applied?.LowLatency == true ? "on" : "off")}, out={res.Outputs}/{enc.FramesIn}, dropped={enc.Dropped}, maxInFlight={res.MaxInFlight}, dtsSamples={enc.OutputsWithDts}]");
                        }
                    }
                }
            }
            if (opened == 0) Check.Skip("CLINK_TESTS_GPU=1 but no hardware H.264 or HEVC encoder opened");
        });
    }

    // ------------------------------------------------------------------ the QualityVsSpeed probe (CLINK_TESTS_GPU=1, run it on an idle PC)

    private static void SayQvs(string s) { Console.WriteLine(s); Log.Info("[qvs-probe] " + s); }

    /// <summary>
    /// Does the hardware MFT keep a QualityVsSpeed other than the 66 the recording sets (a bench logged "66 reads back as 33")? Per codec:
    /// the property's support / range / values, then for each value a SetValue (HRESULT) and the GetValue read-back. The values are set
    /// on an opened live encoder, i.e. after the media types, as the recording does. A measurement: fails only when something throws.
    /// Run: Clink.exe --unittest The_qualityvsspeed_probe, with CLINK_TESTS_GPU=1.
    /// </summary>
    [UnitTest]
    public static void The_qualityvsspeed_probe_for_the_hardware_encoders()
    {
        if (!ShareTranscoderTests.GpuTestsOn) { ShareTranscoderTests.NotRun(nameof(The_qualityvsspeed_probe_for_the_hardware_encoders)); return; }
        ShareTranscoderTests.OnMta(() =>
        {
            using var d3d = D3DDevice.CreateForAdapter(null, lowPriority: false);
            SayQvs($"adapter: {d3d.AdapterName}");
            int opened = 0;
            foreach (string codec in new[] { "h264", "hevc" })
            {
                var cfg = new VideoEncoderConfig(1920, 1080, 60, 12_000_000, codec, 1f);
                VideoEncoder enc;
                try { enc = new VideoEncoder(d3d, cfg, VideoEncoderOptions.Live); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    SayQvs($"{codec}: no encoder ({ex.Message})");
                    continue;
                }
                using (enc)
                {
                    opened++;
                    SayQvs($"{codec}: {enc.EncoderName}; the recording's own setup applied qvs={enc.Applied?.QualityVsSpeed.ToString() ?? "n/a"}");
                    enc.WithCodecApi(port =>
                    {
                        SayQvs($"{codec} QualityVsSpeed: {port.Describe(KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed)}");
                        var sequence = new uint[] { 0, 1, 25, 33, 34, 50, 66, 67, 75, 99, 100, 66, 100 };   // the last two repeat 66 and 100 in the other order
                        foreach (uint v in sequence)
                        {
                            int set = port.SetU32(KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, v);
                            int get = port.GetU32(KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, out uint back);
                            SayQvs($"{codec} set {v}: SetValue=0x{(uint)set:X8} GetValue=0x{(uint)get:X8} readback={(get < 0 ? "n/a" : back.ToString())}");
                        }
                    });
                }
            }
            if (opened == 0) Check.Skip("CLINK_TESTS_GPU=1 but no hardware H.264 or HEVC encoder opened");
        });
    }

    private static string QvsRead(ICodecApiPort port)
    {
        int get = port.GetU32(KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, out uint back);
        return get < 0 ? $"n/a(0x{(uint)get:X8})" : back.ToString();
    }

    /// <summary>
    /// One encoder (1080p60, 12 Mbps CBR, low latency on) with QualityVsSpeed set to <paramref name="value"/> BEFORE the media types and
    /// kept (VideoEncoderOptions.QualityVsSpeedBeforeTypes: the setup after the types makes no QualityVsSpeed call of its own), or, with
    /// <paramref name="value"/> null, today's exact live path (<see cref="VideoEncoderOptions.Live"/>: QualityVsSpeed 66 after the types).
    /// Logs the read-back at each stage, encodes 120 synthetic frames and returns the output byte count and SHA-256 of the bitstream;
    /// null when no encoder opens.
    /// </summary>
    private static (long Bytes, string Hash)? QvsBeforeTypesRun(D3DDevice d3d, string codec, uint? value)
    {
        var cfg = new VideoEncoderConfig(1920, 1080, 60, 12_000_000, codec, 1f);
        string tag = value == null ? $"{codec} today's path (66 after the types)" : $"{codec} before-types qvs={value}";
        var opts = VideoEncoderOptions.Live with { QualityVsSpeedBeforeTypes = value };
        VideoEncoder enc;
        try { enc = new VideoEncoder(d3d, cfg, opts); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            SayQvs($"{codec}: no encoder ({ex.Message})");
            return null;
        }
        using (enc)
        using (var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            long bytes = 0;
            if (enc.QvsBeforeTypes is { } before) SayQvs($"{tag}: stage=immediately-after-set SetValue=0x{(uint)before.SetHr:X8} readback={before.ReadBack?.ToString() ?? "n/a"}");
            var res = new ProbeResult();
            enc.OnOutput = (pts, key, data) => { lock (res.Gate) { sha.AppendData(data); bytes += data.Length; } };
            enc.WithCodecApi(p => SayQvs($"{tag}: stage=after-BEGIN_STREAMING readback={QvsRead(p)} (applied.qvs field says {enc.Applied?.QualityVsSpeed.ToString() ?? "n/a"} = the recording's constant, not the encoder's)"));
            FeedSynthetic(d3d, enc, cfg, 1, res);
            enc.WithCodecApi(p => SayQvs($"{tag}: stage=after-first-ProcessInput readback={QvsRead(p)}"));
            FeedSynthetic(d3d, enc, cfg, 119, res);
            for (int i = 0; i < 60 && enc.FramesInFlight > 0; i++) Thread.Sleep(50);
            Thread.Sleep(200);
            enc.WithCodecApi(p => SayQvs($"{tag}: stage=after-120-frames readback={QvsRead(p)}"));
            string hash;
            long total;
            lock (res.Gate) { hash = Convert.ToHexString(sha.GetCurrentHash()); total = bytes; }
            SayQvs($"{tag}: encoded in={enc.FramesIn} dropped={enc.Dropped} bytes={total} sha256={hash}");
            return (total, hash);
        }
    }

    /// <summary>
    /// Does QualityVsSpeed set BEFORE the media types stick, does it change the bitstream, and which of those does today's recording path
    /// (66 after the types) actually produce? Per codec a fresh encoder for today's exact live path (twice: determinism) and for 0, 33, 66
    /// and 100 before the types; the read-back at the stages, the byte count and the SHA-256 of 120 synthetic frames each. The last line
    /// per codec names the value(s) today's bitstream equals ("TODAY MATCHES"). "No QualityVsSpeed call at all" is not tried: the option
    /// is a value, so the seam has no "skip" (33 is the MFT's default read-back, so a match with 33 says the same).
    /// A measurement: fails only when something throws. Run: Clink.exe --unittest The_qualityvsspeed_probe_set_before, with CLINK_TESTS_GPU=1.
    /// </summary>
    [UnitTest]
    public static void The_qualityvsspeed_probe_set_before_the_media_types()
    {
        if (!ShareTranscoderTests.GpuTestsOn) { ShareTranscoderTests.NotRun(nameof(The_qualityvsspeed_probe_set_before_the_media_types)); return; }
        ShareTranscoderTests.OnMta(() =>
        {
            using var d3d = D3DDevice.CreateForAdapter(null, lowPriority: false);
            SayQvs($"adapter: {d3d.AdapterName}");
            int opened = 0;
            foreach (string codec in new[] { "h264", "hevc" })
            {
                var results = new List<(string Label, uint? Value, (long Bytes, string Hash) R)>();
                foreach (uint? v in new uint?[] { null, 0, 33, 66, 100, null })
                {
                    var r = QvsBeforeTypesRun(d3d, codec, v);
                    if (r == null) break;
                    results.Add((v == null ? (results.Count == 0 ? "today's path" : "today's path (repeat)") : $"before-types qvs={v}", v, r.Value));
                }
                if (results.Count == 0) continue;
                opened++;
                foreach (var (label, _, r) in results) SayQvs($"{codec} SUMMARY {label}: bytes={r.Bytes} sha256={r.Hash}");
                if (results.Count == 6)
                {
                    SayQvs($"{codec} SUMMARY today's path vs its repeat: {(results[0].R.Hash == results[5].R.Hash ? "identical (deterministic)" : "differ")}");
                    var matches = results.Skip(1).Take(4).Where(x => x.R.Hash == results[0].R.Hash).Select(x => x.Value!.Value.ToString()).ToList();
                    SayQvs($"{codec} TODAY MATCHES: {(matches.Count == 0 ? "none of the before-types values 0, 33, 66, 100" : "before-types qvs " + string.Join(", ", matches))}");
                    SayQvs($"{codec} SUMMARY 0 vs 100: {(results[1].R.Hash == results[4].R.Hash ? "IDENTICAL" : "differ")}");
                }
            }
            if (opened == 0) Check.Skip("CLINK_TESTS_GPU=1 but no hardware H.264 or HEVC encoder opened");
        });
    }

    /// <summary>Feeds <paramref name="frames"/> moving NV12 frames to a live encoder at about 60 fps through a CPU-written staging texture.</summary>
    private static void FeedSynthetic(D3DDevice d3d, VideoEncoder enc, VideoEncoderConfig cfg, int frames, ProbeResult res)
    {
        int w = (int)cfg.Width, h = (int)cfg.Height;
        ID3D11Texture2D* staging = d3d.CreateTexture(cfg.Width, cfg.Height, Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_NV12, 0,
            D3D11_USAGE.D3D11_USAGE_STAGING, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE);
        try
        {
            long frameTicks = 10_000_000L / cfg.Fps, t0 = ComUtil.QpcNow100ns();
            var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = cfg.Width, bottom = cfg.Height, back = 1 };
            for (int f = 0; f < frames; f++)
            {
                IMFSample* sample = null; ID3D11Texture2D* tex = null; uint sub = 0;
                for (int tries = 0; tries < 500; tries++)
                {
                    if (enc.TryAcquireInput(out sample, out tex, out sub)) break;
                    sample = null;
                    Thread.Sleep(2);
                }
                if (sample == null) throw new TimeoutException("the encoder gave no input sample");
                try
                {
                    lock (d3d.ContextLock)
                    {
                        D3D11_MAPPED_SUBRESOURCE map;
                        d3d.Context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_WRITE, 0, &map).Check("Map(staging)");
                        try
                        {
                            byte* y = (byte*)map.pData;
                            for (int r = 0; r < h; r++)
                                for (int x = 0; x < w; x++)
                                    y[(long)r * map.RowPitch + x] = (byte)(16 + (((x * 3 + r * 2 + f * 9) ^ (r + f)) & 0x7F) + ((x ^ r) & 0x3F));
                            byte* uv = y + (long)h * map.RowPitch;
                            for (int r = 0; r < h / 2; r++)
                                for (int x = 0; x < w; x++)
                                    uv[(long)r * map.RowPitch + x] = (byte)(128 + (((x >> 1) + f) & 31) - 16);
                        }
                        finally { d3d.Context->Unmap((ID3D11Resource*)staging, 0); }
                        d3d.Context->CopySubresourceRegion((ID3D11Resource*)tex, sub, 0, 0, 0, (ID3D11Resource*)staging, 0, &box);
                    }
                }
                catch { tex->Release(); sample->Release(); throw; }
                tex->Release();
                enc.Submit(sample, t0 + f * frameTicks);
                lock (res.Gate) res.MaxInFlight = (int)Math.Max(res.MaxInFlight, enc.FramesInFlight);
                Thread.Sleep((int)(frameTicks / 10_000));
            }
        }
        finally { staging->Release(); }
    }
}
