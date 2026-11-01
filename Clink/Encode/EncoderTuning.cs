using System.Text;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Encode;

// Slice 4 (encoder experiment), step 4a. A hidden settings key (Settings.EncoderExperiment, no UI) can ask the recording's encoder for
// variable bitrate and / or no low-latency mode. Everything here is pure except ComCodecApiPort: the ICodecAPI is reached through
// ICodecApiPort so a test can fake it. Absent / "off" must leave the recording exactly as it was (design invariant 1): the same
// ICodecAPI calls in the same order, none added.

/// <summary>What the hidden <c>EncoderExperiment</c> setting asks of the recording's encoder.</summary>
internal enum EncoderMode
{
    /// <summary>Today's setup: CBR, low latency on.</summary>
    Off,
    /// <summary>Peak-constrained VBR (mean = the bitrate, max = 1.5x), low latency on.</summary>
    Vbr,
    /// <summary>CBR, low latency off.</summary>
    LowLatencyOff,
    /// <summary>Peak-constrained VBR, low latency off.</summary>
    VbrLowLatencyOff,
    /// <summary>VBR, low latency off and B-frames. The B-frames are not built yet (step 3 / 4b): for now this applies as <see cref="VbrLowLatencyOff"/> with none.</summary>
    VbrBframes,
}

/// <summary>The requested live tuning. Null (rather than Off) is what the encoder gets when nothing is asked for.</summary>
/// <param name="ForceCbr">A size target is active: its clip size is only guaranteed by CBR, so a VBR mode is applied as CBR.</param>
/// <param name="BFrames">The B-picture count to ask for. 0 always, except for the capability probe test.</param>
internal sealed record LiveEncoderTuning(EncoderMode Mode, bool ForceCbr = false, uint BFrames = 0)
{
    public bool WantsVbr => Mode is EncoderMode.Vbr or EncoderMode.VbrLowLatencyOff or EncoderMode.VbrBframes;
    public bool LowLatency => Mode is EncoderMode.Off or EncoderMode.Vbr;
    public override string ToString() => $"{EncoderExperiments.Name(Mode)}{(ForceCbr && WantsVbr ? " (size target: CBR)" : "")}";
}

/// <summary>
/// What <see cref="EncoderExperiments.Apply"/> ended with. Read back and judged: the rate mode (so <c>Vbr</c>), the max bitrate (VBR only),
/// low latency (when it was asked off) and the B-picture count (<c>BFrames</c> is the value read back when it could be read, else the
/// request; <c>BFramesState</c> says which). Reported as requested, not verified: mean bitrate, GOP and quality-vs-speed.
/// </summary>
/// <param name="Vbr">Peak-constrained VBR is in effect (else CBR).</param>
/// <param name="Fallback">Why something asked for was not applied (several joined with "; "), or null.</param>
/// <param name="Report">The capability probe's lines (tuned encoders only), or null.</param>
/// <param name="BFramesState">What the B-picture count did: "ok" (set and read back), "unsupported", "rejected", "unverified" (set, not readable) or "reads back N". Empty without a tuning.</param>
internal sealed record AppliedTuning(EncoderMode Requested, bool Vbr, uint MeanBps, uint MaxBps, bool LowLatency, uint BFrames,
    uint QualityVsSpeed, uint Gop, string? Fallback, string? Report, string BFramesState = "")
{
    /// <summary>The highest bitrate the encoder may reach, in Mbps: what the ring is sized from.</summary>
    public double PeakMbps => MaxBps / 1_000_000.0;
    public string Describe() =>
        $"rate={(Vbr ? "PeakConstrainedVBR" : "CBR")} mean={MeanBps / 1e6:0.##} Mbps max={MaxBps / 1e6:0.##} Mbps lowLatency={(LowLatency ? "on" : "off")} bframes={BFrames} qvs={QualityVsSpeed} gop={Gop}";
}

/// <summary>The ICodecAPI calls the tuning needs, as HRESULTs, so the real COM object can be replaced by a fake.</summary>
internal interface ICodecApiPort
{
    /// <summary>ICodecAPI.IsSupported did not fail (S_FALSE counts, as the recording's setup always treated it).</summary>
    bool IsSupported(Guid key);
    int SetU32(Guid key, uint value);
    int SetBool(Guid key, bool value);
    int GetU32(Guid key, out uint value);
    int GetBool(Guid key, out bool value);
    /// <summary>One line: IsSupported, IsModifiable and GetParameterValues of the key.</summary>
    string Describe(Guid key);
}

/// <summary>Reads the hidden setting; applies a tuning through an <see cref="ICodecApiPort"/>; sizes the ring.</summary>
internal static class EncoderExperiments
{
    /// <summary>The VBR peak over the mean.</summary>
    public const double VbrPeakFactor = 1.5;
    public const uint QualityVsSpeed = 66;

    public static string Name(EncoderMode m) => m switch
    {
        EncoderMode.Vbr => "vbr",
        EncoderMode.LowLatencyOff => "lowlatency-off",
        EncoderMode.VbrLowLatencyOff => "vbr-lowlatency-off",
        EncoderMode.VbrBframes => "vbr-bframes",
        _ => "off",
    };

    /// <summary>Case and surrounding spaces are ignored. Null, empty and "off" are Off without a warning; anything else unknown is Off with one.</summary>
    public static (EncoderMode Mode, string? Warning) Parse(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        switch (v)
        {
            case "": case "off": return (EncoderMode.Off, null);
            case "vbr": return (EncoderMode.Vbr, null);
            case "lowlatency-off": return (EncoderMode.LowLatencyOff, null);
            case "vbr-lowlatency-off": return (EncoderMode.VbrLowLatencyOff, null);
            case "vbr-bframes": return (EncoderMode.VbrBframes, null);
            default:
                string shown = v.Length > 40 ? v[..40] + "..." : v;
                return (EncoderMode.Off, $"Unknown encoder experiment '{shown}'; using off");
        }
    }

    /// <summary>The tuning to hand the engine for this setting: null for Off (the recording is then set up exactly as before). Logs a parse warning.</summary>
    public static LiveEncoderTuning? Resolve(string? setting, bool sizeTargetActive)
    {
        var (mode, warning) = Parse(setting);
        if (warning != null) Log.Warn(warning);
        return mode == EncoderMode.Off ? null : new LiveEncoderTuning(mode, ForceCbr: sizeTargetActive);
    }

    /// <summary>
    /// The bitrate the ring is sized from: the applied peak under VBR (so the replay length still holds at the peak), else the configured
    /// bitrate exactly as before.
    /// </summary>
    public static double RingMbps(double configuredMbps, AppliedTuning? applied) => applied is { Vbr: true } ? applied.PeakMbps : configuredMbps;

    // ------------------------------------------------------------------ apply

    private enum Outcome { Ok, Unsupported, Rejected, Differs, Unverified }

    private static readonly Dictionary<Guid, string> s_names = new()
    {
        [KnownGuids.CODECAPI_AVEncCommonRateControlMode] = "RateControlMode",
        [KnownGuids.CODECAPI_AVEncCommonMeanBitRate] = "MeanBitRate",
        [KnownGuids.CODECAPI_AVEncCommonMaxBitRate] = "MaxBitRate",
        [KnownGuids.CODECAPI_AVEncMPVGOPSize] = "GOPSize",
        [KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount] = "BPictureCount",
        [KnownGuids.CODECAPI_AVLowLatencyMode] = "LowLatencyMode",
        [KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed] = "QualityVsSpeed",
    };
    private static string N(Guid key) => s_names.TryGetValue(key, out var n) ? n : key.ToString();

    private static readonly HashSet<string> s_probeLogged = new();   // guarded by itself (more than one encoder can open at a time)

    /// <summary>
    /// Sets the recording's rate control, bitrates, GOP, B-frames, low latency and speed. With <paramref name="tuning"/> null this is
    /// today's setup and nothing else: IsSupported then SetValue for each (low latency without IsSupported), in the same order, and no
    /// read-back. With a tuning each value is set, read back and judged; a mode that did not take falls back (VBR to CBR, low latency
    /// to on) and says why. Logs one line with what was asked and what applied, and the capability probe once per codec.
    /// </summary>
    public static AppliedTuning Apply(ICodecApiPort port, VideoEncoderConfig cfg, LiveEncoderTuning? tuning)
    {
        uint gop = (uint)Math.Max(1, Math.Round(cfg.Fps * cfg.GopSeconds));
        uint bitrate = cfg.BitrateBps;
        if (tuning == null)
        {
            // Today's calls, in today's order (ConfigureCodecApi before slice 4). Do not add a call here.
            SetUnchecked(port, KnownGuids.CODECAPI_AVEncCommonRateControlMode, (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR);
            SetUnchecked(port, KnownGuids.CODECAPI_AVEncCommonMeanBitRate, bitrate);
            SetUnchecked(port, KnownGuids.CODECAPI_AVEncCommonMaxBitRate, bitrate);
            SetUnchecked(port, KnownGuids.CODECAPI_AVEncMPVGOPSize, gop);
            SetUnchecked(port, KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount, 0);
            int hr = port.SetBool(KnownGuids.CODECAPI_AVLowLatencyMode, true);
            if (hr < 0) Log.Warn($"ICodecAPI {KnownGuids.CODECAPI_AVLowLatencyMode} = True not accepted (0x{(uint)hr:X8})");
            SetUnchecked(port, KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, QualityVsSpeed);
            return new AppliedTuning(EncoderMode.Off, false, bitrate, bitrate, true, 0, QualityVsSpeed, gop, null, null);
        }

        var fallbacks = new List<string>();
        string report = ProbeReport(port, cfg);
        bool firstProbe;
        lock (s_probeLogged) firstProbe = s_probeLogged.Add(cfg.Codec);
        if (firstProbe) Log.Info($"Encoder capability probe ({cfg.CodecName}):\n" + report);

        bool vbr = tuning.WantsVbr;
        if (vbr && tuning.ForceCbr)
        {
            vbr = false;
            Log.Info($"Encoder experiment {Name(tuning.Mode)}: a size target is set, so the encoder stays CBR");
        }
        uint peak = PeakOf(bitrate);

        const uint Cbr = (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR;
        const uint Pcvbr = (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_PeakConstrainedVBR;
        var rate = Checked(port, KnownGuids.CODECAPI_AVEncCommonRateControlMode, vbr ? Pcvbr : Cbr);
        if (vbr && rate != Outcome.Ok)
        {
            fallbacks.Add($"rate mode VBR not applied ({rate}), using CBR");
            vbr = false;
            Checked(port, KnownGuids.CODECAPI_AVEncCommonRateControlMode, Cbr);
        }
        Checked(port, KnownGuids.CODECAPI_AVEncCommonMeanBitRate, bitrate);
        var max = Checked(port, KnownGuids.CODECAPI_AVEncCommonMaxBitRate, vbr ? peak : bitrate);
        if (vbr && max is Outcome.Unsupported or Outcome.Rejected or Outcome.Differs)
        {
            fallbacks.Add($"max bitrate not accepted ({max}), using CBR");
            vbr = false;
            Checked(port, KnownGuids.CODECAPI_AVEncCommonRateControlMode, Cbr);
            Checked(port, KnownGuids.CODECAPI_AVEncCommonMaxBitRate, bitrate);
        }
        Checked(port, KnownGuids.CODECAPI_AVEncMPVGOPSize, gop);
        var bf = Checked(port, KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount, tuning.BFrames, out uint bfBack);
        uint bfApplied = bf is Outcome.Ok or Outcome.Differs ? bfBack : tuning.BFrames;   // what the encoder says, when it could be asked
        string bfState = bf == Outcome.Differs ? $"reads back {bfBack}" : bf == Outcome.Ok ? "ok" : bf.ToString().ToLowerInvariant();

        // Low latency: the MF attribute was left unset by the encoder when the mode wants it off; this is the ICodecAPI half. It stays
        // on unless the encoder says (reads back) that it is off.
        bool wantLl = tuning.LowLatency;
        bool appliedLl = true;
        int sb = port.SetBool(KnownGuids.CODECAPI_AVLowLatencyMode, wantLl);
        if (sb < 0) Log.Warn($"ICodecAPI LowLatencyMode = {wantLl} not accepted (0x{(uint)sb:X8})");
        if (!wantLl)
        {
            if (sb < 0) fallbacks.Add("low latency stays on (not accepted)");
            else if (port.GetBool(KnownGuids.CODECAPI_AVLowLatencyMode, out bool llNow) < 0) fallbacks.Add("low latency stays on (could not be read back)");
            else if (llNow) fallbacks.Add("low latency stays on (reads back on)");
            else appliedLl = false;
        }
        Checked(port, KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, QualityVsSpeed);

        if (tuning.Mode == EncoderMode.VbrBframes)
        {
            fallbacks.Add("B-frames not built yet");
        }

        string? fb = fallbacks.Count == 0 ? null : string.Join("; ", fallbacks);
        var applied = new AppliedTuning(tuning.Mode, vbr, bitrate, vbr ? peak : bitrate, appliedLl, bfApplied, QualityVsSpeed, gop, fb, report, bfState);
        Log.Info($"Encoder experiment: requested {tuning}; applied {applied.Describe()}" + (fb != null ? $"; fallback: {fb}" : ""));
        return applied;
    }

    /// <summary>
    /// Before the media types are set (tuned encoders only; Off makes no call here): asks for the rate mode, the mean and max bitrate and
    /// low latency once, so an encoder that fixes its session when the output type is set starts from them. Nothing is read back and
    /// nothing falls back here: a failure is logged and ignored, and <see cref="Apply"/> after the types still sets, reads back and judges.
    /// Never throws.
    /// </summary>
    public static void ApplyBeforeTypes(ICodecApiPort port, VideoEncoderConfig cfg, LiveEncoderTuning tuning)
    {
        try
        {
            bool vbr = tuning.WantsVbr && !tuning.ForceCbr;
            uint bitrate = cfg.BitrateBps;
            PreSet(port, KnownGuids.CODECAPI_AVEncCommonRateControlMode, vbr ? (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_PeakConstrainedVBR : (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR);
            PreSet(port, KnownGuids.CODECAPI_AVEncCommonMeanBitRate, bitrate);
            PreSet(port, KnownGuids.CODECAPI_AVEncCommonMaxBitRate, vbr ? PeakOf(bitrate) : bitrate);
            int hr = port.SetBool(KnownGuids.CODECAPI_AVLowLatencyMode, tuning.LowLatency);
            if (hr < 0) Log.Info($"ICodecAPI LowLatencyMode = {tuning.LowLatency} before the media types: not accepted (0x{(uint)hr:X8}); it is set again after them");
        }
        catch (Exception ex) { Log.Warn("Encoder experiment: setting before the media types failed: " + ex.Message); }
    }

    private static void PreSet(ICodecApiPort port, Guid key, uint value)
    {
        int hr = port.SetU32(key, value);
        if (hr < 0) Log.Info($"ICodecAPI {N(key)} = {value} before the media types: not accepted (0x{(uint)hr:X8}); it is set again after them");
    }

    private static uint PeakOf(uint bitrate) => (uint)Math.Min(uint.MaxValue, Math.Round(bitrate * VbrPeakFactor));

    /// <summary>Today's SetU32: skip a key the encoder does not support, set, log a failure. No read-back.</summary>
    private static void SetUnchecked(ICodecApiPort port, Guid key, uint value)
    {
        if (!port.IsSupported(key)) return;   // e.g. NVENC MFT has no B-frame count property
        int hr = port.SetU32(key, value);
        if (hr < 0) Log.Warn($"ICodecAPI {key} = {value} not accepted (0x{(uint)hr:X8})");
    }

    /// <summary>IsSupported, then SetValue (its HRESULT), then GetValue to read it back.</summary>
    private static Outcome Checked(ICodecApiPort port, Guid key, uint value) => Checked(port, key, value, out _);

    private static Outcome Checked(ICodecApiPort port, Guid key, uint value, out uint back)
    {
        back = 0;
        if (!port.IsSupported(key)) { Log.Info($"ICodecAPI {N(key)} is not supported"); return Outcome.Unsupported; }
        int hr = port.SetU32(key, value);
        if (hr < 0) { Log.Warn($"ICodecAPI {N(key)} = {value} not accepted (0x{(uint)hr:X8})"); return Outcome.Rejected; }
        if (port.GetU32(key, out back) < 0) return Outcome.Unverified;
        if (back != value) { Log.Warn($"ICodecAPI {N(key)} = {value} reads back as {back}"); return Outcome.Differs; }
        return Outcome.Ok;
    }

    private static string ProbeReport(ICodecApiPort port, VideoEncoderConfig cfg)
    {
        var sb = new StringBuilder();
        foreach (var key in new[] { KnownGuids.CODECAPI_AVEncCommonRateControlMode, KnownGuids.CODECAPI_AVEncCommonMaxBitRate,
                     KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount, KnownGuids.CODECAPI_AVLowLatencyMode })
        {
            string line;
            try { line = port.Describe(key); }
            catch (Exception ex) { line = "probe failed: " + ex.Message; }
            sb.Append("  ").Append(N(key)).Append(": ").AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 4a's order guard: output timestamps must strictly increase (this step writes pts as dts). The first regression latches: later
/// samples are refused too, and the engine reopens the encoder without the experiment.
/// </summary>
internal sealed class OutputOrderGuard
{
    private long _last;
    private bool _any;
    private volatile bool _tripped;
    public bool Tripped => _tripped;
    public string? Detail { get; private set; }

    /// <summary>True when <paramref name="pts"/> is later than the last accepted one. False once tripped, for every sample after the first regression too.</summary>
    public bool Accept(long pts)
    {
        if (_tripped) return false;
        if (_any && pts <= _last)
        {
            Detail = $"pts {pts} after {_last}";
            _tripped = true;
            return false;
        }
        _last = pts;
        _any = true;
        return true;
    }
}

/// <summary>The real ICodecAPI behind <see cref="ICodecApiPort"/>. Does not own the pointer.</summary>
internal sealed unsafe class ComCodecApiPort : ICodecApiPort
{
    private readonly ICodecAPI* _c;
    public ComCodecApiPort(ICodecAPI* codec) => _c = codec;

    public bool IsSupported(Guid key) => !_c->IsSupported(&key).Failed;

    public int SetU32(Guid key, uint value)
    {
        VARIANT v = default; v.vt = VARENUM.VT_UI4; v.ulVal = value;
        return _c->SetValue(&key, &v).Value;
    }

    public int SetBool(Guid key, bool value)
    {
        VARIANT v = default; v.vt = VARENUM.VT_BOOL; v.boolVal = new VARIANT_BOOL((short)(value ? -1 : 0));
        return _c->SetValue(&key, &v).Value;
    }

    public int GetU32(Guid key, out uint value)
    {
        VARIANT v = default;
        HRESULT hr = _c->GetValue(&key, &v);
        value = 0;
        if (hr.Failed) return hr.Value;
        try
        {
            if (!TryNumber(v, out ulong n)) return unchecked((int)0x80004005);
            value = (uint)n;
            return hr.Value;
        }
        finally { Clear(&v); }
    }

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int VariantClear(VARIANT* pvarg);

    /// <summary>Releases what a VARIANT the encoder filled in owns (a BSTR, an interface); a number needs nothing.</summary>
    private static void Clear(VARIANT* v) { try { VariantClear(v); } catch { } }

    public int GetBool(Guid key, out bool value)
    {
        VARIANT v = default;
        HRESULT hr = _c->GetValue(&key, &v);
        value = false;
        if (hr.Failed) return hr.Value;
        try
        {
            if (v.vt == VARENUM.VT_BOOL) { value = v.boolVal.Value != 0; return hr.Value; }
            if (!TryNumber(v, out ulong n)) return unchecked((int)0x80004005);
            value = n != 0;
            return hr.Value;
        }
        finally { Clear(&v); }
    }

    private static bool TryNumber(VARIANT v, out ulong n)
    {
        n = 0;
        switch (v.vt)
        {
            case VARENUM.VT_UI4: n = v.ulVal; return true;
            case VARENUM.VT_I4: n = unchecked((uint)v.lVal); return true;
            case VARENUM.VT_UI8: n = v.ullVal; return true;
            case VARENUM.VT_I8: n = unchecked((ulong)v.llVal); return true;
            case VARENUM.VT_BOOL: n = v.boolVal.Value != 0 ? 1UL : 0; return true;
            default: return false;
        }
    }

    private static string Show(VARIANT v) => v.vt switch
    {
        VARENUM.VT_UI4 => v.ulVal.ToString(),
        VARENUM.VT_I4 => v.lVal.ToString(),
        VARENUM.VT_UI8 => v.ullVal.ToString(),
        VARENUM.VT_I8 => v.llVal.ToString(),
        VARENUM.VT_BOOL => (v.boolVal.Value != 0).ToString(),
        VARENUM.VT_EMPTY => "-",
        _ => "vt" + (int)v.vt,
    };

    public string Describe(Guid key)
    {
        HRESULT s = _c->IsSupported(&key);
        HRESULT m = _c->IsModifiable(&key);
        VARIANT min = default, max = default, step = default;
        HRESULT p = _c->GetParameterRange(&key, &min, &max, &step);
        string range;
        try { range = p.Failed ? $"unavailable (0x{(uint)p.Value:X8})" : $"{Show(min)}..{Show(max)} step {Show(step)}"; }
        finally { if (p.Succeeded) { Clear(&min); Clear(&max); Clear(&step); } }
        // The list of allowed values (GetParameterValues): only some properties have one.
        VARIANT* list = null; uint count = 0;
        HRESULT lv = _c->GetParameterValues(&key, &list, &count);
        string values;
        if (lv.Failed || list == null) values = $"unavailable (0x{(uint)lv.Value:X8})";
        else
        {
            try
            {
                var sb = new StringBuilder();
                for (uint i = 0; i < count && i < 8; i++) sb.Append(i > 0 ? "," : "").Append(Show(list[i]));
                if (count > 8) sb.Append(",... (").Append(count).Append(')');
                values = sb.ToString();
            }
            finally
            {
                for (uint i = 0; i < count; i++) Clear(&list[i]);   // every element, not only the ones shown
                Windows.Win32.PInvoke.CoTaskMemFree(list);
            }
        }
        return $"IsSupported=0x{(uint)s.Value:X8} IsModifiable=0x{(uint)m.Value:X8} range={range} values={values}";
    }
}
