using System.IO;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Engine;

/// <summary>What a clip is, from its header: what the share-fit planner (<c>ShareFit.Plan</c>) needs and the library does not know.</summary>
/// <param name="DurationSeconds">MF_PD_DURATION (a clip the library scanned from its folder has a duration of 0).</param>
/// <param name="Codec">"h264" | "hevc" | "av1", or "" for any other video.</param>
/// <param name="Fps">MF_MT_FRAME_RATE, else the first sample's delta; 0 when neither is known.</param>
/// <param name="AudioTracks">The number of audio streams.</param>
/// <param name="KeyframeSeconds">The keyframe interval a trim of this clip can start early by: <c>Settings.KeyframeSeconds</c>, clamped to 0.25 .. 5 (the engine's GOP clamp).</param>
internal sealed record ShareProbeResult(double DurationSeconds, string Codec, int Width, int Height, double Fps, int AudioTracks, double KeyframeSeconds)
{
    /// <summary>The rung name for this size and rate, as the UI says it: "720p60" (the short side, so a portrait clip works).</summary>
    public string Label => ShareProbe.LabelFor(Width, Height, Fps);
}

/// <summary>
/// A header-only look at a clip before a smaller copy is planned: the length, video codec, frame size and rate, and how many audio
/// tracks it has. The file is opened as Clink reads clips everywhere (<see cref="ClipSource.Open(string, IMFAttributes*, out IDisposable?, bool)"/>
/// with the edit lists hidden) and nothing is decoded; only without a frame rate in the header are the first two samples read.
/// </summary>
internal static unsafe class ShareProbe
{
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;

    /// <summary>The probe for <paramref name="path"/>.</summary>
    /// <param name="keyframeSeconds">The current <c>Settings.KeyframeSeconds</c> (clamped here; NaN = 1).</param>
    /// <exception cref="ShareTranscodeException">Not a video Clink can read, or no video in it.</exception>
    public static ShareProbeResult Read(string path, double keyframeSeconds)
    {
        if (!File.Exists(path)) throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip file is not there");
        try { return ReadCore(path, keyframeSeconds); }
        catch (Exception ex) when (ex is not ShareTranscodeException and not OperationCanceledException)
        {
            throw ShareTranscoder.Classify(ex);
        }
    }

    private static ShareProbeResult ReadCore(string path, double keyframeSeconds)
    {
        IMFSourceReader* reader = ClipSource.Open(path, null, out var owner, hideEditLists: true);
        try
        {
            uint video = uint.MaxValue;
            Guid subtype = default;
            int width = 0, height = 0, audio = 0;
            double fps = 0;
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                if (reader->GetNativeMediaType(i, 0, &t).Failed) break;
                try
                {
                    var a = (IMFAttributes*)t;
                    Guid major = Mf.GetGuid(a, PInvoke.MF_MT_MAJOR_TYPE);
                    if (major == PInvoke.MFMediaType_Audio) audio++;
                    else if (major == PInvoke.MFMediaType_Video && video == uint.MaxValue)
                    {
                        video = i;
                        subtype = Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE);
                        ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE);
                        width = (int)(size >> 32);
                        height = (int)(size & 0xFFFFFFFF);
                        ulong rate = Mf.GetU64(a, PInvoke.MF_MT_FRAME_RATE);
                        fps = RateOf((uint)(rate >> 32), (uint)rate);
                    }
                }
                finally { t->Release(); }
            }
            if (video == uint.MaxValue) throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip has no video");

            long duration = 0;
            PROPVARIANT pv = default;
            Guid durKey = PInvoke.MF_PD_DURATION;
            if (reader->GetPresentationAttribute(MediaSource, &durKey, &pv).Succeeded && pv.vt == VARENUM.VT_UI8) duration = (long)pv.uhVal;
            PInvoke.PropVariantClear(ref pv);

            if (fps <= 0) fps = FirstSampleRate(reader, video);

            string codec = subtype == PInvoke.MFVideoFormat_H264 ? "h264" : subtype == PInvoke.MFVideoFormat_HEVC ? "hevc" : subtype == PInvoke.MFVideoFormat_AV1 ? "av1" : "";
            return new ShareProbeResult(duration / 10_000_000.0, codec, width, height, fps, audio, ClampKeyframe(keyframeSeconds));
        }
        finally
        {
            reader->Release();
            owner?.Dispose();   // after the reader: that closes the clip file
        }
    }

    /// <summary>The frame rate from the first two video samples' times (the header gave none), or 0.</summary>
    private static double FirstSampleRate(IMFSourceReader* reader, uint video)
    {
        reader->SetStreamSelection(AllStreams, false);
        reader->SetStreamSelection(video, true);
        long first = long.MinValue;
        for (int reads = 0; reads < 8; reads++)
        {
            uint idx, flags; long ts; IMFSample* sample;
            if (reader->ReadSample(video, 0, &idx, &flags, &ts, &sample).Failed) return 0;
            if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM)) != 0)
            {
                if (sample != null) sample->Release();
                return 0;
            }
            if (sample == null) continue;
            sample->Release();
            if (first == long.MinValue) { first = ts; continue; }
            return FromDelta(ts - first);
        }
        return 0;
    }

    /// <summary>A frame rate from a numerator and denominator, 0 when either is missing or the rate is absurd.</summary>
    internal static double RateOf(uint numerator, uint denominator)
        => numerator > 0 && denominator > 0 && numerator / (double)denominator <= 1000 ? numerator / (double)denominator : 0;

    /// <summary>The frame rate that a gap of <paramref name="delta100ns"/> between two frames means (0 for no gap or one above a second).</summary>
    internal static double FromDelta(long delta100ns)
        => delta100ns > 0 && delta100ns <= 10_000_000 ? 10_000_000.0 / delta100ns : 0;

    /// <summary><c>Settings.KeyframeSeconds</c> clamped like the engine's GOP (0.25 .. 5); NaN is 1.</summary>
    internal static double ClampKeyframe(double seconds) => double.IsNaN(seconds) ? 1.0 : Math.Clamp(seconds, 0.25, 5.0);

    /// <summary>"720p60": the short side and the rate rounded (59.94 reads 60).</summary>
    internal static string LabelFor(int width, int height, double fps)
        => $"{Math.Min(width, height)}p{(int)Math.Round(fps, MidpointRounding.AwayFromZero)}";
}
