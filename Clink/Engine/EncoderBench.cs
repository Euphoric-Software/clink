using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Clink.Audio;
using Clink.Encode;
using Clink.Native;

namespace Clink.Engine;

// Slice 4 step 2: the hidden, dev-only encoder bench. `Clink.exe --encoder-bench <reference.mp4> <outFolder> [--codec h264|hevc]
// [--mbps 10.8,12,14.4,16,18,20,22.2] [--modes off,vbr,lowlatency-off,vbr-lowlatency-off] [--seconds N] [--keyframe-seconds S]` decodes one reference clip with Media Foundation and
// encodes the same frames once per (mode, bitrate) through the recording's own live encoder setup, writes a video-only MP4 and a JSON file
// per run, and a log, all in the out folder. tools\encoder-bench.ps1 then measures VMAF / SSIM / PSNR with ffmpeg and writes the report.
//
// Why the numbers mean what a recording would do: the encoder is a VideoEncoder with the LIVE options (VideoEncoderOptions.Live, with
// LiveTuning for every mode but off), so ConfigureCodecApiBeforeTypes, the media types, ConfigureCodecApi and EncoderExperiments.Apply (with
// read-back) run exactly as in Engine.VideoLoop; "off" passes the plain Live options, which is today's seven calls (plus the bench's one read after streaming starts). The one bench-only
// mode, "quality" (not an EncoderMode: the live setting never accepts it), is "off" with VideoEncoderOptions.QualityVsSpeedBeforeTypes = 100
// (null in the app): the MFT keeps a QualityVsSpeed only when it is set before the media types, so the recording's own 66 after them is
// not what runs. Otherwise nothing here adds an option to VideoEncoder: the frames are fed through the same TryAcquireInput / Submit the recording uses, and the only help is that
// this producer WAITS (VideoEncoder.HasRoomForSubmit) instead of calling Submit when it would drop the oldest frame, because a file reader,
// unlike a capture, can wait. The share-copy path (Offline options, OfflineEncoderSettings, ConfigureOfflineCodecApi) is not touched.
// This class never reads or writes Settings (no settings.json, no clips folder): the only files it writes are in the out folder.

/// <summary>A parsed <c>--encoder-bench</c> command line.</summary>
/// <param name="KeyframeSeconds">The GOP length in seconds: 1, as a recording's (EngineConfig.GopSeconds), unless --keyframe-seconds says otherwise.</param>
/// <param name="Quality">The bench-only "quality" mode is on the --modes list: CBR like "off", with QualityVsSpeed 100 set before the media types. Not an <see cref="EncoderMode"/> (the live setting never accepts it).</param>
internal sealed record BenchArgs(string Reference, string OutFolder, string Codec, IReadOnlyList<double> Mbps, IReadOnlyList<EncoderMode> Modes, double? Seconds, double KeyframeSeconds = 1.0, bool Quality = false);

/// <summary>The bitrate of an encoder's output in whole windows (1 s): what a VBR encoder does over time, which a file's mean hides.</summary>
/// <param name="PerSecondMbps">One value per complete window (the last, partial window is left out).</param>
internal sealed record BitrateProfile(double[] PerSecondMbps, double PeakMbps, double MeanMbps)
{
    public static readonly BitrateProfile Empty = new(Array.Empty<double>(), 0, 0);
    public int Seconds => PerSecondMbps.Length;
    public double PeakOverMean => MeanMbps > 0 ? PeakMbps / MeanMbps : 0;

    /// <summary>Sums the bytes of the samples per window counted from the earliest timestamp. Fewer than one complete window gives <see cref="Empty"/>.</summary>
    public static BitrateProfile Compute(IReadOnlyList<(long Pts, int Bytes)> samples, long window100ns = 10_000_000)
    {
        if (samples.Count == 0 || window100ns <= 0) return Empty;
        long first = long.MaxValue, last = long.MinValue;
        foreach (var s in samples) { first = Math.Min(first, s.Pts); last = Math.Max(last, s.Pts); }
        long windows = (last - first) / window100ns + 1;
        if (windows < 2) return Empty;   // nothing but the partial window
        var bytes = new long[windows];
        foreach (var s in samples) bytes[(s.Pts - first) / window100ns] += s.Bytes;
        double seconds = window100ns / 10_000_000.0;
        var mbps = new double[windows - 1];
        for (int i = 0; i < mbps.Length; i++) mbps[i] = bytes[i] * 8.0 / 1e6 / seconds;
        return new BitrateProfile(mbps, mbps.Max(), mbps.Average());
    }
}

/// <summary>Whether a VBR mode really ran as VBR.</summary>
/// <param name="Applicable">False for a CBR mode (nothing to judge).</param>
internal readonly record struct VbrVerdict(bool Applicable, bool TookEffect, string Note);

internal static class BenchChecks
{
    /// <summary>
    /// A VBR encode's busiest second has to be at least this much over its mean second (a CBR encoder's seconds stay within a few percent).
    /// The design's hard validity check: "applied" in the log does not prove VBR ran, the output's own profile does.
    /// </summary>
    public const double VbrPeakOverMeanMin = 1.05;
    public const string DidNotTakeEffect = "VBR did not take effect";

    public static VbrVerdict JudgeVbr(bool vbrRequested, bool vbrApplied, string? fallback, BitrateProfile profile)
    {
        if (!vbrRequested) return new VbrVerdict(false, true, "n/a (CBR mode)");
        if (!vbrApplied) return new VbrVerdict(true, false, $"{DidNotTakeEffect}: the encoder fell back to CBR ({fallback ?? "no reason given"})");
        if (profile.Seconds < 2) return new VbrVerdict(true, false, $"{DidNotTakeEffect}: not judged, only {profile.Seconds} complete second(s) of output (need 2)");
        if (!(profile.PeakMbps > profile.MeanMbps * VbrPeakOverMeanMin))
            return new VbrVerdict(true, false, $"{DidNotTakeEffect}: peak 1 s {profile.PeakMbps:0.##} Mbps is not above {VbrPeakOverMeanMin:0.##} x the mean {profile.MeanMbps:0.##} Mbps (x{profile.PeakOverMean:0.###})");
        return new VbrVerdict(true, true, $"ok: peak 1 s {profile.PeakMbps:0.##} Mbps = x{profile.PeakOverMean:0.###} the mean {profile.MeanMbps:0.##} Mbps");
    }
}

internal sealed class BenchAppliedInfo
{
    public string Rate { get; set; } = "";
    public double MeanMbps { get; set; }
    public double MaxMbps { get; set; }
    public bool LowLatency { get; set; }
    public uint BFrames { get; set; }
    public string BFramesState { get; set; } = "";
    public uint? QualityVsSpeed { get; set; }
    public uint Gop { get; set; }
    public string? Fallback { get; set; }
    public string? CapabilityReport { get; set; }
}

/// <summary>The QualityVsSpeed a run asked for, at which stage, and what the encoder said once streaming had begun.</summary>
internal sealed class BenchQvsInfo
{
    /// <summary>The value asked for (null: no ICodecAPI, so no call was made).</summary>
    public uint? Requested { get; set; }
    /// <summary>"before-types" (set before the media types, the only stage the encoder keeps it) | "after-types" (the recording's own call, which the NVENC MFT accepts but ignores) | "none".</summary>
    public string Stage { get; set; } = "";
    /// <summary>GetValue after BEGIN_STREAMING (when the encoder constructor returned); null = not readable.</summary>
    public uint? ReadBack { get; set; }
    /// <summary>Before-types only: SetValue's HRESULT.</summary>
    public string? SetResult { get; set; }
    public string Describe() => $"qvs requested {(Requested?.ToString() ?? "n/a")} at {Stage}, reads back {(ReadBack?.ToString() ?? "n/a")} after streaming started";
}

internal sealed class BenchRequestedInfo
{
    public bool Vbr { get; set; }
    public double MeanMbps { get; set; }
    public double MaxMbps { get; set; }
    public bool LowLatency { get; set; }
}

/// <summary>What the reference clip is: the bench re-encodes a decoded copy of it, so how good and how Clink-made it is matters to the result.</summary>
internal sealed class BenchReferenceInfo
{
    /// <summary>h264, hevc, av1 or other.</summary>
    public string Codec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public long FileBytes { get; set; }
    public double DurationSeconds { get; set; }
    /// <summary>File size x 8 / duration: audio included, so a little above the video's own rate. 0 = unknown.</summary>
    public double OverallMbps { get; set; }
    /// <summary>The file's brands include clk1: a Clink recording or trim (Clink records CBR).</summary>
    public bool ClinkBrand { get; set; }
}

/// <summary>One run's result: the JSON file tools\encoder-bench.ps1 reads. Property names are written camelCase.</summary>
internal sealed class BenchRunResult
{
    public string Name { get; set; } = "";
    /// <summary>The Clink build that made the run (assembly informational version): the script warns when one group of runs mixes builds.</summary>
    public string ClinkVersion { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Codec { get; set; } = "";
    public double RequestedMbps { get; set; }
    public string Reference { get; set; } = "";
    public BenchReferenceInfo? ReferenceInfo { get; set; }
    public string AdapterName { get; set; } = "";
    /// <summary>HighPart:LowPart in hex.</summary>
    public string AdapterLuid { get; set; } = "";
    /// <summary>The --seconds limit, or null for the whole clip (only then can the frame count be compared with the reference's packet count).</summary>
    public double? SecondsLimit { get; set; }
    public double KeyframeSeconds { get; set; }
    public string OutputFile { get; set; } = "";
    public string Encoder { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public uint Fps { get; set; }
    public double SourceFps { get; set; }
    public BenchRequestedInfo? Requested { get; set; }
    public BenchAppliedInfo? Applied { get; set; }
    /// <summary>Requested QualityVsSpeed, the stage it was set at, and the read-back after streaming started (the script makes a "quality" run INVALID unless that reads 100).</summary>
    public BenchQvsInfo? QualityVsSpeed { get; set; }
    public long DecodedFrames { get; set; }
    public long FramesIn { get; set; }
    public long FramesOut { get; set; }
    /// <summary>Always 0 by design: this producer waits for room instead of letting the encoder drop. A value above 0 would mean the bench itself broke, and makes the run INVALID in the script.</summary>
    public long Dropped { get; set; }
    /// <summary>Frames the (undrained, like a recording's) encoder still held when the wait for output ended: FramesIn - FramesOut.</summary>
    public long FramesHeldAtEnd { get; set; }
    public long MaxFramesInFlight { get; set; }
    public int OutOfOrderOutputs { get; set; }
    public double DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    /// <summary>Video bits / (FramesOut / source fps): the encoder's own output rate, without the container. The x-axis of the script's CBR curve and the size every run is compared at.</summary>
    public double AchievedMbps { get; set; }
    /// <summary>File bytes x 8 / the same duration: the MP4 including its container overhead (information only).</summary>
    public double FileMbps { get; set; }
    public int ProfileSeconds { get; set; }
    public double PeakMbps1s { get; set; }
    public double MeanMbps1s { get; set; }
    public double PeakOverMean { get; set; }
    public double[] PerSecondMbps { get; set; } = Array.Empty<double>();
    public bool VbrApplicable { get; set; }
    public bool VbrTookEffect { get; set; }
    public string VbrVerdict { get; set; } = "";
    public double EncodeWallSeconds { get; set; }
    /// <summary>From the first frame read to the last Submit (decode, copy and encode together).</summary>
    public double FeedSeconds { get; set; }
    /// <summary>Frames submitted / FeedSeconds. Below the source fps the encoder (or the decoder) could not keep up in real time.</summary>
    public double EncodeSpeedFps { get; set; }
    /// <summary>
    /// The longest this feeder waited for the encoder to have room for a frame or an input buffer, in ms. Expected: the bench feeds faster
    /// than real time and sleeps with Thread.Sleep(1) (about 15.6 ms on a default Windows timer), so this says nothing about a live
    /// recording. The latency evidence is FramesHeldAtEnd; MaxFramesInFlight saturates when feeding faster than real time.
    /// </summary>
    public double MaxWaitForRoomMs { get; set; }
    public string? Error { get; set; }
}

internal static unsafe class EncoderBench
{
    public const string Verb = "--encoder-bench";
    public const string Usage = "usage: Clink.exe --encoder-bench <reference.mp4> <outFolder> [--codec h264|hevc] [--mbps 10.8,12,14.4,16,18,20,22.2] [--modes off,vbr,lowlatency-off,vbr-lowlatency-off,quality] [--seconds N] [--keyframe-seconds S]";

    /// <summary>The GOP length of a recording by default (EngineConfig.GopSeconds), and the default of --keyframe-seconds.</summary>
    public const double LiveGopSeconds = 1.0;
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OutputSettle = TimeSpan.FromSeconds(2);
    private static readonly EncoderMode[] AllModes = { EncoderMode.Off, EncoderMode.Vbr, EncoderMode.LowLatencyOff, EncoderMode.VbrLowLatencyOff };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The bitrates a codec is benched at by default (1080p60: the H.264 presets' 12 / 16 / 20, and 14 for HEVC; these are bench rates only, not app defaults), each
    /// with a point at 0.9x so that "VBR at 90% of the bytes" is measured directly: the CBR ("off") runs form the curve the others are judged
    /// against, so they need to span the sizes the VBR runs end up at. The top point (22.2 = 20 / 0.9, 15.6 = 14 / 0.9 rounded) keeps the
    /// highest preset's VBR run, compared at its size / 0.9, inside the curve.
    /// </summary>
    public static IReadOnlyList<double> DefaultMbps(string codec) => codec == "hevc" ? new[] { 11.2, 12.6, 14.0, 15.6 } : new[] { 10.8, 12.0, 14.4, 16.0, 18.0, 20.0, 22.2 };

    // ------------------------------------------------------------------ the options of one run

    /// <summary>
    /// The encoder options a bench run uses: the recording's own. Off is the plain <see cref="VideoEncoderOptions.Live"/> (what Engine passes,
    /// as null, when the flag is off: today's ICodecAPI calls); every other mode is Live with the same LiveTuning Engine builds
    /// (<see cref="EncoderExperiments.Resolve"/>, no size target). Never a blocking / offline option.
    /// </summary>
    public static VideoEncoderOptions OptionsFor(EncoderMode mode)
        => mode == EncoderMode.Off ? VideoEncoderOptions.Live : VideoEncoderOptions.Live with { LiveTuning = new LiveEncoderTuning(mode) };

    /// <summary>The bench-only mode that is not an <see cref="EncoderMode"/>: CBR exactly like off, plus QualityVsSpeed 100 before the media types. The live setting never accepts this name.</summary>
    public const string QualityModeName = "quality";
    /// <summary>The preset the quality mode asks for (100 = best quality; the MFT keeps a value only when it is set before the media types).</summary>
    public const uint QualityModeVsSpeed = 100;

    /// <summary>The options of a bench run: <see cref="OptionsFor(EncoderMode)"/>, or for the quality mode the plain live options (no tuning, so no other early call) with QualityVsSpeed set before the media types.</summary>
    public static VideoEncoderOptions OptionsFor(EncoderMode mode, bool quality)
        => quality ? VideoEncoderOptions.Live with { QualityVsSpeedBeforeTypes = QualityModeVsSpeed } : OptionsFor(mode);

    /// <summary>The runs of one bitrate, in order: the listed encoder modes, then "quality" when it was asked for.</summary>
    internal static IReadOnlyList<(EncoderMode Mode, bool Quality)> RunModes(BenchArgs a)
    {
        var list = a.Modes.Select(m => (m, false)).ToList();
        if (a.Quality) list.Add((EncoderMode.Off, true));
        return list;
    }

    internal static string ModeName(EncoderMode mode, bool quality) => quality ? QualityModeName : EncoderExperiments.Name(mode);

    /// <summary>What the run's JSON and log say about QualityVsSpeed. <paramref name="applied"/> is null without an ICodecAPI (nothing was set).</summary>
    internal static BenchQvsInfo DescribeQvs(VideoEncoderOptions options, AppliedTuning? applied, QvsBeforeTypesResult? before, uint? readBack)
    {
        if (options.QualityVsSpeedBeforeTypes is { } asked)
            return new BenchQvsInfo { Requested = asked, Stage = "before-types", ReadBack = readBack, SetResult = before == null ? "not called" : $"0x{(uint)before.SetHr:X8}" };
        // The recording's own call, after the media types (it reports 66 whatever the encoder keeps).
        return new BenchQvsInfo { Requested = applied?.QualityVsSpeed, Stage = applied == null ? "none" : "after-types", ReadBack = readBack };
    }

    /// <summary>A run's JSON (camelCase, indented): what tools\encoder-bench.ps1 reads.</summary>
    internal static string ToJson(BenchRunResult r) => JsonSerializer.Serialize(r, Json);

    /// <summary>The file name (no extension) of a run's outputs. The bitrate keeps its decimals (10.8M, 12.001M) so that two bitrates never share a name.</summary>
    public static string RunName(string codec, double mbps, EncoderMode mode)
        => $"{codec}_{MbpsText(mbps)}M_{EncoderExperiments.Name(mode)}";

    /// <summary>The name of the quality run at a bitrate, e.g. h264_12M_quality (never the same as an encoder mode's).</summary>
    public static string QualityRunName(string codec, double mbps) => $"{codec}_{MbpsText(mbps)}M_{QualityModeName}";

    internal static string RunName(string codec, double mbps, EncoderMode mode, bool quality) => quality ? QualityRunName(codec, mbps) : RunName(codec, mbps, mode);

    internal static string MbpsText(double mbps) => mbps.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The muxer's temporary names for a run's MP4 while it is written; a reference with one of them would be overwritten.</summary>
    private static readonly string[] MuxerTempExtensions = { ".mp4.tmp", ".mp4.part" };
    private static readonly string[] OutputExtensions = { ".mp4", ".json", ".metrics.json", ".vmaf.json", ".ffmpeg.log" };

    /// <summary>
    /// Whether <paramref name="candidate"/> is, or might be, the reference: the same full path, or (when it exists) the same file by volume
    /// serial and file index (a hard link, a junction, a subst drive), or not established. A reference is never deleted on a guess.
    /// </summary>
    private static bool MayBeReference(string candidate, string reference)
    {
        if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(reference), StringComparison.OrdinalIgnoreCase)) return true;
        if (!File.Exists(candidate) || !File.Exists(reference)) return false;
        return ShareTranscoder.SameFile(candidate, reference) != false;
    }

    /// <summary>A message when one of the files the runs of <paramref name="names"/> write or delete in <paramref name="outFolder"/> is the reference, otherwise null.</summary>
    internal static string? ReferenceCollision(string outFolder, string reference, IEnumerable<string> names)
    {
        foreach (string name in names)
            foreach (string ext in OutputExtensions.Concat(MuxerTempExtensions))
            {
                string p = Path.Combine(outFolder, name + ext);
                if (MayBeReference(p, reference)) return $"the reference clip {reference} is (or may be) the bench output {p}: choose another out folder; nothing was deleted";
            }
        return null;
    }

    /// <summary>
    /// Deletes what an earlier run of the same name left (MP4, JSON, the script's metrics cache, VMAF log and ffmpeg log), so nothing stale is
    /// measured or reported. Never the reference: a candidate that is the reference file (by path or by file identity) is refused.
    /// </summary>
    internal static void DeleteStaleOutputs(string outFolder, string name, string reference)
    {
        foreach (string ext in OutputExtensions)
        {
            string p = Path.Combine(outFolder, name + ext);
            if (MayBeReference(p, reference)) throw new InvalidOperationException($"{p} is (or may be) the reference clip, so it is not deleted");
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // ------------------------------------------------------------------ the command line

    /// <param name="args">The whole command line, <see cref="Verb"/> first.</param>
    public static bool TryParseArgs(string[] args, out BenchArgs? parsed, out string error)
    {
        parsed = null;
        error = Usage;
        if (args.Length < 3 || !args[0].Equals(Verb, StringComparison.OrdinalIgnoreCase)) return false;
        if (args[1].StartsWith("--", StringComparison.Ordinal) || args[2].StartsWith("--", StringComparison.Ordinal))
        { error = "the reference clip and the out folder come first. " + Usage; return false; }

        string reference, outFolder;
        try
        {
            if (string.IsNullOrWhiteSpace(args[1])) { error = "the reference clip path is empty"; return false; }
            if (string.IsNullOrWhiteSpace(args[2])) { error = "the out folder path is empty"; return false; }
            reference = Path.GetFullPath(args[1]);
            outFolder = Path.GetFullPath(args[2]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { error = "not a usable path: " + ex.Message; return false; }
        if (Directory.Exists(reference)) { error = $"the reference clip is a folder, not a file: {reference}"; return false; }
        if (!File.Exists(reference)) { error = $"the reference clip does not exist: {reference}"; return false; }
        if (File.Exists(outFolder)) { error = $"the out folder is a file: {outFolder}"; return false; }
        string? refDir = Path.GetDirectoryName(reference);
        if (refDir != null && string.Equals(Path.TrimEndingDirectorySeparator(refDir), Path.TrimEndingDirectorySeparator(outFolder), StringComparison.OrdinalIgnoreCase))
        { error = "the out folder must not be the reference clip's own folder (use a scratch folder)"; return false; }

        string codec = "h264";
        string? mbpsText = null, modesText = null, secondsText = null, keyframeText = null;
        var seen = new HashSet<string>();
        for (int i = 3; i < args.Length; i += 2)
        {
            string opt = args[i].ToLowerInvariant();
            if (opt is not ("--codec" or "--mbps" or "--modes" or "--seconds" or "--keyframe-seconds")) { error = $"unknown option '{args[i]}'. " + Usage; return false; }
            if (!seen.Add(opt)) { error = $"{opt} is given twice"; return false; }
            if (i + 1 >= args.Length) { error = $"{opt} needs a value"; return false; }
            string v = args[i + 1];
            switch (opt)
            {
                case "--codec": codec = v.Trim().ToLowerInvariant(); break;
                case "--mbps": mbpsText = v; break;
                case "--modes": modesText = v; break;
                case "--keyframe-seconds": keyframeText = v; break;
                default: secondsText = v; break;
            }
        }
        if (codec is not ("h264" or "hevc")) { error = $"--codec must be h264 or hevc, not '{codec}'"; return false; }

        IReadOnlyList<double> mbps = DefaultMbps(codec);
        if (mbpsText != null && !TryParseMbps(mbpsText, out mbps, out error)) return false;
        IReadOnlyList<EncoderMode> modes = AllModes;
        bool quality = false;
        if (modesText != null && !TryParseModes(modesText, out modes, out quality, out error)) return false;
        double? seconds = null;
        if (secondsText != null)
        {
            if (!double.TryParse(secondsText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) || !double.IsFinite(s) || s <= 0 || s > 3600)
            { error = $"--seconds must be a number above 0 and at most 3600, not '{secondsText}'"; return false; }
            seconds = s;
        }
        double keyframe = LiveGopSeconds;
        if (keyframeText != null
            && (!double.TryParse(keyframeText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out keyframe) || !double.IsFinite(keyframe) || keyframe < 0.25 || keyframe > 10))
        { error = $"--keyframe-seconds must be a number from 0.25 to 10 (a recording uses 1), not '{keyframeText}'"; return false; }
        parsed = new BenchArgs(reference, outFolder, codec, mbps, modes, seconds, keyframe, quality);
        error = "";
        return true;
    }

    public static bool TryParseMbps(string text, out IReadOnlyList<double> list, out string error)
    {
        list = Array.Empty<double>();
        error = "";
        var items = new List<double>();
        var names = new HashSet<string>();
        foreach (string part in text.Split(','))
        {
            string p = part.Trim();
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            { error = $"--mbps: '{p}' is not a number (a comma-separated list such as 12,16,20)"; return false; }
            if (v < 0.5 || v > 200) { error = $"--mbps: {p} is outside 0.5 to 200"; return false; }
            // Refused by the name the files get (12 and 12.0000001 would both be "12M" and overwrite each other).
            if (!names.Add(MbpsText(v))) { error = $"--mbps: {p} is listed twice (both would be named {MbpsText(v)}M)"; return false; }
            items.Add(v);
        }
        if (items.Count > 12) { error = "--mbps: at most 12 bitrates"; return false; }
        list = items;
        return true;
    }

    /// <summary>The bench's mode list: the live setting's modes (<see cref="EncoderExperiments.Parse"/>) and, bench only, "quality" (any case), which comes back as <paramref name="quality"/> and never as an <see cref="EncoderMode"/>.</summary>
    public static bool TryParseModes(string text, out IReadOnlyList<EncoderMode> list, out bool quality, out string error)
    {
        list = Array.Empty<EncoderMode>();
        quality = false;
        error = "";
        var items = new List<EncoderMode>();
        foreach (string part in text.Split(','))
        {
            string p = part.Trim();
            if (p.Equals(QualityModeName, StringComparison.OrdinalIgnoreCase))
            {
                if (quality) { error = $"--modes: {p} is listed twice"; return false; }
                quality = true;
                continue;
            }
            var (mode, warning) = EncoderExperiments.Parse(p);
            if (p.Length == 0 || warning != null)
            { error = $"--modes: '{p}' is not a mode (off, vbr, lowlatency-off, vbr-lowlatency-off, quality)" + (warning != null && p.Length > 0 ? ": " + warning : ""); return false; }
            if (items.Contains(mode)) { error = $"--modes: {p} is listed twice"; return false; }
            items.Add(mode);
        }
        list = items;
        return true;
    }

    // ------------------------------------------------------------------ the run

    /// <summary>Runs the whole bench. 0 = every run finished, 1 = a run failed (its JSON says why), 2 = the command line was wrong.</summary>
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var a, out string error))
        {
            Console.WriteLine("encoder-bench: " + error);
            return 2;
        }
        Action<string>? echo = null;
        // Log.Init appends to the out folder's clink-cli.log and, past 2 MB, moves it over clink-cli.prev.log: a reference that is (or may be) either file is refused first.
        foreach (string logPath in new[] { Path.Combine(a!.OutFolder, Log.CliFile), Log.PrevPathFor(a.OutFolder, Log.CliFile) })
        {
            if (!MayBeReference(logPath, a.Reference)) continue;
            Console.WriteLine($"encoder-bench: the reference clip {a.Reference} is (or may be) the bench log {logPath}: choose another out folder; nothing was changed");
            return 1;
        }
        try
        {
            Directory.CreateDirectory(a.OutFolder);
            Log.Init(a.OutFolder, Log.CliFile);   // the out folder's own log: never the real Clink folder
            echo = Console.WriteLine;
            Log.Line += echo;
            return RunAll(a);
        }
        catch (Exception ex)
        {
            Console.WriteLine("encoder-bench: failed: " + ex.Message);
            try { Log.Error("[encoder-bench] failed", ex); } catch { }
            return 1;
        }
        finally { if (echo != null) Log.Line -= echo; }
    }

    /// <summary>Every (bitrate, mode) run of <paramref name="a"/>, one JSON (and MP4) each in the out folder. Does not touch the log's file (the caller set it up). Returns the exit code of <see cref="Run"/>.</summary>
    internal static int RunAll(BenchArgs a)
    {
        var previousPriority = ProcessPriorityClass.Normal;
        bool lowered = false;
        try
        {
            // The bench is a batch job that may share a PC with a game: below normal costs it no frames, since the feeder waits for the encoder.
            try
            {
                using var self = Process.GetCurrentProcess();
                previousPriority = self.PriorityClass;
                if (previousPriority is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal or ProcessPriorityClass.High or ProcessPriorityClass.RealTime)
                {
                    self.PriorityClass = ProcessPriorityClass.BelowNormal;
                    lowered = true;
                    Log.Info($"[encoder-bench] priority BelowNormal (was {previousPriority})");
                }
                else Log.Info($"[encoder-bench] priority left at {previousPriority}");
            }
            catch (Exception ex) { Log.Warn("[encoder-bench] could not lower the process priority: " + ex.Message); }

            Directory.CreateDirectory(a.OutFolder);
            var runModes = RunModes(a);
            string? collision = ReferenceCollision(a.OutFolder, a.Reference, a.Mbps.SelectMany(m => runModes.Select(rm => RunName(a.Codec, m, rm.Mode, rm.Quality))));
            if (collision != null)
            {
                Log.Error("[encoder-bench] refused: " + collision);
                return 1;
            }
            Log.Info($"[encoder-bench] reference {a.Reference}, out {a.OutFolder}, {a.Codec}, Mbps {string.Join(",", a.Mbps.Select(MbpsText))}, modes {string.Join(",", runModes.Select(rm => ModeName(rm.Mode, rm.Quality)))}, GOP {a.KeyframeSeconds:0.##} s" +
                     (a.Seconds is { } s ? $", first {s:0.###} s" : ", whole clip"));
            int failed = 0;
            using var d3d = D3DDevice.CreateForAdapter(null, lowPriority: false);
            string luid = $"{d3d.AdapterLuid.HighPart:X8}:{d3d.AdapterLuid.LowPart:X8}";
            Log.Info($"[encoder-bench] adapter {d3d.AdapterName} (LUID {luid})");
            bool referenceLogged = false;
            foreach (double mbps in a.Mbps)
            {
                foreach (var (mode, quality) in runModes)
                {
                    var r = RunOne(d3d, a, mode, quality, mbps, ref referenceLogged);
                    r.AdapterName = d3d.AdapterName;
                    r.AdapterLuid = luid;
                    if (r.Error != null) failed++;
                    File.WriteAllText(Path.Combine(a.OutFolder, r.Name + ".json"), ToJson(r));
                }
            }
            Log.Info($"[encoder-bench] done, {failed} of {a.Mbps.Count * runModes.Count} run(s) failed. Next: tools\\encoder-bench.ps1 -OutFolder \"{a.OutFolder}\" -Reference \"{a.Reference}\"");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("[encoder-bench] failed", ex);
            return 1;
        }
        finally
        {
            if (lowered)
            {
                try
                {
                    using var self = Process.GetCurrentProcess();
                    self.PriorityClass = previousPriority;
                    Log.Info($"[encoder-bench] priority restored to {previousPriority}");
                }
                catch { }
            }
        }
    }
    /// <summary>What the reference is, for the JSON and the report: its codec, size, frame rate, overall bitrate (file size over duration) and whether Clink wrote it (clk1).</summary>
    private static BenchReferenceInfo DescribeReference(string path, ShareTranscoder.SourceVideo video)
    {
        var info = new BenchReferenceInfo
        {
            Codec = video.CopyCodec ?? (video.Subtype == PInvoke.MFVideoFormat_AV1 ? "av1" : "other"),
            Width = video.Width, Height = video.Height, Fps = video.Fps,
            DurationSeconds = video.Duration100ns / 1e7,
        };
        try { info.FileBytes = new FileInfo(path).Length; } catch (IOException) { }
        if (info.FileBytes > 0 && info.DurationSeconds > 0) info.OverallMbps = info.FileBytes * 8.0 / 1e6 / info.DurationSeconds;
        info.ClinkBrand = Trimmer.ReadClipTracks(path)?.Marked ?? false;
        return info;
    }

    /// <summary>The Clink build that runs the bench: the assembly's informational version, else its version.</summary>
    internal static string ClinkVersionText()
    {
        var asm = typeof(EncoderBench).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>One line for the log: what the reference is (codec, size, frame rate, frames, bitrate, Clink brand).</summary>
    internal static string DescribeReferenceLine(BenchReferenceInfo i, long expectedFrames)
        => $"[encoder-bench] reference {i.Codec} {i.Width}x{i.Height} {i.Fps:0.###} fps, {(expectedFrames > 0 ? "~" + expectedFrames : "?")} frames, {i.OverallMbps:0.0} Mbps overall, clk1 {(i.ClinkBrand ? "yes" : "no")}";

    private static BenchRunResult RunOne(D3DDevice d3d, BenchArgs a, EncoderMode mode, bool quality, double mbps, ref bool referenceLogged)
    {
        string name = RunName(a.Codec, mbps, mode, quality);
        string mp4 = Path.Combine(a.OutFolder, name + ".mp4");
        var r = new BenchRunResult
        {
            ClinkVersion = ClinkVersionText(),
            Name = name, Mode = ModeName(mode, quality), Codec = a.Codec, RequestedMbps = mbps, Reference = a.Reference, OutputFile = name + ".mp4",
            SecondsLimit = a.Seconds, KeyframeSeconds = a.KeyframeSeconds,
        };
        var wall = Stopwatch.StartNew();
        IMFSourceReader* reader = null;
        IDisposable? owner = null;
        VideoEncoder? enc = null;
        ClipMuxWriter? mux = null;
        ID3D11Texture2D* staging = null;
        Log.Info($"[encoder-bench] run {name}");
        try
        {
            DeleteStaleOutputs(a.OutFolder, name, a.Reference);   // the MP4 and everything measured from an earlier run of this name
            IMFAttributes* attrs = Mf.CreateAttributes(4);
            try
            {
                Mf.SetUnknown(attrs, PInvoke.MF_SOURCE_READER_D3D_MANAGER, d3d.DeviceManager);
                Mf.SetU32(attrs, PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                Mf.SetU32(attrs, PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                reader = ClipSource.Open(a.Reference, attrs, out owner);
            }
            finally { attrs->Release(); }

            var video = ShareTranscoder.FindVideo(reader);
            if (video.Fps <= 0) throw new InvalidOperationException("the reference clip does not say its frame rate");
            if (video.Width % 2 != 0 || video.Height % 2 != 0) throw new InvalidOperationException($"the reference is {video.Width}x{video.Height}; the encoder needs even sizes");
            int w = video.Width, h = video.Height;
            uint fps = (uint)Math.Max(1, Math.Round(video.Fps));
            int stride = ShareTranscoder.SetDecodedType(reader, video, w, h);
            r.Width = w; r.Height = h; r.Fps = fps; r.SourceFps = video.Fps;
            r.ReferenceInfo = DescribeReference(a.Reference, video);
            if (!referenceLogged) { referenceLogged = true; Log.Info(DescribeReferenceLine(r.ReferenceInfo, video.ExpectedFrames)); }

            uint bitrate = (uint)Math.Round(mbps * 1_000_000);
            var cfg = new VideoEncoderConfig((uint)w, (uint)h, fps, bitrate, a.Codec, (float)a.KeyframeSeconds);
            var options = OptionsFor(mode, quality);
            enc = new VideoEncoder(d3d, cfg, options);
            r.Encoder = enc.EncoderName;
            var applied = enc.Applied;
            // (The constructor returns once streaming has begun: this is the read-back "after streaming starts". Read-only.)
            r.QualityVsSpeed = DescribeQvs(options, applied, enc.QvsBeforeTypes, enc.ReadQualityVsSpeed());
            var tuning = options.LiveTuning;
            bool wantVbr = tuning?.WantsVbr ?? false;
            r.Requested = new BenchRequestedInfo
            {
                Vbr = wantVbr, MeanMbps = mbps, MaxMbps = wantVbr ? mbps * EncoderExperiments.VbrPeakFactor : mbps, LowLatency = tuning?.LowLatency ?? true,
            };

            var outputs = new ConcurrentQueue<(long pts, bool key, byte[] data)>();
            enc.OnOutput = (pts, key, annexB) => outputs.Enqueue((pts, key, annexB.ToArray()));
            var samples = new List<(long Pts, int Bytes)>();
            long origin = long.MinValue, lastOut = long.MinValue, payload = 0, decoded = 0, submitted = 0, maxInFlight = 0;
            int outOfOrder = 0;
            uint gridFps = ShareTranscoder.MuxGridFps(video.Fps, (int)fps);
            var encoder = enc;

            void Flush()
            {
                while (outputs.TryDequeue(out var o))
                {
                    mux ??= new ClipMuxWriter(ClipOutput.FastStart(mp4), new ClipVideoConfig(a.Codec, w, h, gridFps, encoder.SequenceHeader), Array.Empty<AudioTrackDesc>(), origin, false);
                    mux.AddVideo(o.pts, o.key, o.data);
                    samples.Add((o.pts, o.data.Length));
                    payload += o.data.Length;
                    if (o.pts <= lastOut) outOfOrder++;
                    lastOut = Math.Max(lastOut, o.pts);
                }
            }

            long limit = a.Seconds is { } sec ? (long)(sec * 10_000_000) : long.MaxValue;
            double maxWaitMs = 0;
            var feed = Stopwatch.StartNew();
            var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = (uint)w, bottom = (uint)h, back = 1 };
            {
                while (true)
                {
                    uint idx, flags; long ts; IMFSample* sample;
                    reader->ReadSample(video.Stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample(decoded video)");
                    if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                    {
                        if (sample != null) sample->Release();
                        throw new InvalidOperationException("The reference could not be decoded");
                    }
                    if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) stride = ShareTranscoder.CheckDecodedType(reader, video.Stream, w, h);
                    bool end = (flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                    if (sample != null)
                    {
                        try
                        {
                            if (origin == long.MinValue) origin = ts;
                            if (ts - origin >= limit) end = true;
                            else
                            {
                                decoded++;
                                // A live encoder drops the oldest waiting frame when it has no credit and four waiting; a file reader can wait instead
                                // (and does, constantly, when it feeds faster than real time: the wait is not a statement about a recording).
                                var since = Stopwatch.StartNew();
                                while (!encoder.HasRoomForSubmit)
                                {
                                    Flush();
                                    if (since.Elapsed > Stall) throw new TimeoutException($"the encoder took no frame for {Stall.TotalSeconds:0} s");
                                    Thread.Sleep(1);
                                }
                                maxWaitMs = Math.Max(maxWaitMs, since.Elapsed.TotalMilliseconds);
                                IMFSample* input; ID3D11Texture2D* dstTex; uint dstSub;
                                since.Restart();
                                while (!encoder.TryAcquireInput(out input, out dstTex, out dstSub))
                                {
                                    Flush();
                                    if (since.Elapsed > Stall) throw new TimeoutException($"the encoder kept every input frame for {Stall.TotalSeconds:0} s");
                                    Thread.Sleep(1);
                                }
                                maxWaitMs = Math.Max(maxWaitMs, since.Elapsed.TotalMilliseconds);
                                try { ShareTranscoder.CopyFrame(d3d, sample, dstTex, dstSub, w, h, stride, &box, ref staging); }
                                catch { input->Release(); throw; }
                                finally { dstTex->Release(); }
                                encoder.Submit(input, ts);   // (room was checked above and only this thread submits: it cannot drop)
                                submitted++;
                                maxInFlight = Math.Max(maxInFlight, encoder.FramesInFlight);
                            }
                        }
                        finally { sample->Release(); }
                    }
                    Flush();
                    if (end) break;
                }
            }
            feed.Stop();
            if (submitted == 0) throw new InvalidOperationException("the reference gave no video frames");
            r.FeedSeconds = feed.Elapsed.TotalSeconds;
            r.EncodeSpeedFps = r.FeedSeconds > 0 ? submitted / r.FeedSeconds : 0;
            r.MaxWaitForRoomMs = maxWaitMs;

            // A recording's encoder is not drained either: wait for the output to settle (low latency off holds a few frames back).
            var settle = Stopwatch.StartNew();
            long seen = Interlocked.Read(ref enc.FramesOut);
            while (Interlocked.Read(ref enc.FramesOut) < Interlocked.Read(ref enc.FramesIn) && settle.Elapsed < OutputSettle)
            {
                Flush();
                long now = Interlocked.Read(ref enc.FramesOut);
                if (now != seen) { seen = now; settle.Restart(); }
                Thread.Sleep(5);
            }
            Flush();
            r.FramesIn = Interlocked.Read(ref enc.FramesIn);
            r.FramesOut = Interlocked.Read(ref enc.FramesOut);
            r.Dropped = Interlocked.Read(ref enc.Dropped);
            r.FramesHeldAtEnd = r.FramesIn - r.FramesOut;
            r.MaxFramesInFlight = maxInFlight;
            r.DecodedFrames = decoded;
            r.OutOfOrderOutputs = outOfOrder;
            if (mux == null) throw new InvalidOperationException("the hardware encoder gave no output");
            var done = mux.Finish();
            r.FileSizeBytes = done.SizeBytes;
            // The output's own length: the frames that came out, not the ones submitted (an undrained encoder still holds a few at the end).
            r.DurationSeconds = Math.Max(1, r.FramesOut) / video.Fps;
            r.AchievedMbps = payload * 8.0 / 1e6 / r.DurationSeconds;   // the video bits only: the axis every comparison uses
            r.FileMbps = done.SizeBytes * 8.0 / 1e6 / r.DurationSeconds;

            var profile = BitrateProfile.Compute(samples);
            r.ProfileSeconds = profile.Seconds;
            r.PeakMbps1s = profile.PeakMbps;
            r.MeanMbps1s = profile.MeanMbps;
            r.PeakOverMean = profile.PeakOverMean;
            r.PerSecondMbps = profile.PerSecondMbps;
            if (applied != null)
            {
                r.Applied = new BenchAppliedInfo
                {
                    Rate = applied.Vbr ? "PeakConstrainedVBR" : "CBR", MeanMbps = applied.MeanBps / 1e6, MaxMbps = applied.MaxBps / 1e6, LowLatency = applied.LowLatency,
                    BFrames = applied.BFrames, BFramesState = applied.BFramesState, QualityVsSpeed = applied.QualityVsSpeed, Gop = applied.Gop,
                    Fallback = applied.Fallback, CapabilityReport = applied.Report,
                };
            }
            var verdict = BenchChecks.JudgeVbr(wantVbr, applied?.Vbr ?? false, applied?.Fallback, profile);
            r.VbrApplicable = verdict.Applicable;
            r.VbrTookEffect = verdict.TookEffect;
            r.VbrVerdict = verdict.Note;
            r.EncodeWallSeconds = wall.Elapsed.TotalSeconds;
            Log.Info($"[encoder-bench] {name}: {r.FramesIn} in, {r.FramesOut} out, dropped {r.Dropped}, held {r.FramesHeldAtEnd}, max in flight {r.MaxFramesInFlight}, {r.EncodeSpeedFps:0.#} fps fed (longest feeder wait {r.MaxWaitForRoomMs:0} ms: expected when feeding faster than real time), out of order {r.OutOfOrderOutputs}, {r.FileSizeBytes / 1048576.0:0.00} MB = {r.AchievedMbps:0.##} Mbps video ({r.FileMbps:0.##} Mbps file), " +
                     $"peak 1 s {r.PeakMbps1s:0.##} Mbps (mean {r.MeanMbps1s:0.##}), applied: {applied?.Describe() ?? "no ICodecAPI"}{(applied?.Fallback != null ? "; fallback: " + applied.Fallback : "")}; {r.QualityVsSpeed?.Describe()}; {r.VbrVerdict}; {r.EncodeWallSeconds:0.0} s");
        }
        catch (Exception ex)
        {
            r.Error = ex.GetType().Name + ": " + ex.Message;
            r.EncodeWallSeconds = wall.Elapsed.TotalSeconds;
            Log.Error($"[encoder-bench] {name} failed", ex);
            try { mux?.Discard(); } catch { }
            mux = null;
        }
        finally
        {
            enc?.Dispose();   // first: it holds the device
            mux?.Dispose();
            if (reader != null) reader->Release();
            owner?.Dispose();
            ComUtil.Release(ref staging);
        }
        return r;
    }
}
