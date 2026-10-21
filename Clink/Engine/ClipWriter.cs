using Clink.Audio;
using Clink.Encode;

namespace Clink.Engine;

internal sealed record ClipResult(string Path, double DurationSeconds, long SizeBytes, string? Game);

/// <summary>
/// Turns a replay-buffer snapshot into an MP4 file. Samples are streamed: each one is converted and handed to the muxer in
/// turn and its snapshot bytes are released right after, so a long clip (the replay length goes up to an hour) costs one
/// copy of itself in managed memory, not the snapshot plus a converted twin of it.
/// </summary>
internal static class ClipWriter
{
    /// <summary>H.264 / HEVC only (kept for callers that still think in terms of a bool).</summary>
    public static void Write(string path, IEnumerable<VideoSample> video, bool hevc, int width, int height, byte[]? sequenceHeader,
        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long origin100ns, uint fps)
        => Write(path, video, hevc ? "hevc" : "h264", width, height, sequenceHeader, audio, origin100ns, fps);

    /// <param name="video">Video samples in order, starting on a keyframe. Each sample's <see cref="VideoSample.Data"/> is released once written.</param>
    /// <param name="codec">"h264" | "hevc" | "av1" (experimental). Video samples are Annex B access units for H.264/HEVC, temporal units for AV1.</param>
    /// <param name="sequenceHeader">Encoder-provided parameter sets (Annex B) or AV1 Sequence Header OBU; may be null when the samples carry them in-band.</param>
    public static void Write(string path, IEnumerable<VideoSample> video, string codec, int width, int height, byte[]? sequenceHeader,
        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long origin100ns, uint fps)
    {
        bool av1 = codec == "av1", hevc = codec == "hevc";
        var ps = new ParameterSets();
        byte[]? seqObu = null;
        if (av1) { if (sequenceHeader != null) seqObu = Av1.FindSequenceHeader(sequenceHeader); }
        else if (sequenceHeader != null) Nal.ToLengthPrefixed(sequenceHeader, hevc, ps);

        // Snap capture timestamps to the nominal frame grid so the file is clean constant-frame-rate
        // (the pacer jitters by a millisecond or two); keep timestamps strictly increasing.
        double period = fps > 0 ? 10_000_000.0 / fps : 0;
        long prev = long.MinValue;
        Mp4Muxer? mux = null;
        var ai = new int[audio.Count];
        // Converted samples waiting for the parameter sets (normally none: the first sample is a keyframe that carries them, or the encoder handed them over).
        List<(long pts, bool key, byte[] data)>? pending = null;
        int count = 0;
        try
        {
            foreach (var v in video)
            {
                long q = v.Pts;
                if (fps > 0)
                {
                    q = origin100ns + (long)Math.Round((v.Pts - origin100ns) / period) * (long)Math.Round(period);
                    if (q <= prev) q = prev + (long)Math.Round(period);
                }
                else if (q <= prev) q = prev + 1;
                prev = q;
                byte[] data;
                if (av1)
                {
                    data = Av1.ToSample(v.Data);
                    seqObu ??= Av1.FindSequenceHeader(v.Data);
                }
                else data = Nal.ToLengthPrefixed(v.Data, hevc, ps);
                v.Data = Array.Empty<byte>();   // the snapshot's copy is done with
                count++;

                if (mux == null)
                {
                    bool ready = av1 ? seqObu != null : ps.Complete(hevc);
                    if (!ready) { (pending ??= new()).Add((q, v.Keyframe, data)); continue; }
                    mux = new Mp4Muxer(path, new VideoTrackDesc
                    {
                        Codec = codec,
                        Width = width,
                        Height = height,
                        CodecConfig = av1 ? Av1.BuildAv1C(seqObu!) : hevc ? Nal.BuildHvcC(ps.Vps!, ps.Sps!, ps.Pps!) : Nal.BuildAvcC(ps.Sps!, ps.Pps!),
                    }, audio.Select(a => a.desc), origin100ns);
                    if (pending != null)
                    {
                        foreach (var p in pending) WriteSample(mux, audio, ai, p.pts, p.key, p.data);
                        pending = null;
                    }
                }
                WriteSample(mux, audio, ai, q, v.Keyframe, data);
            }
            if (mux == null)
            {
                if (count == 0) throw new InvalidOperationException("Nothing recorded yet, wait a moment and try again.");
                throw new InvalidOperationException(av1 ? "AV1 encoder did not provide a Sequence Header OBU (no keyframe yet?)" : "Encoder did not provide SPS/PPS (no keyframe yet?)");
            }
            for (int t = 0; t < audio.Count; t++)
            {
                var frames = audio[t].frames;
                while (ai[t] < frames.Count) { mux.WriteAudio(t, frames[ai[t]].Pts, frames[ai[t]].Data); ai[t]++; }
            }
            mux.Finish();
        }
        finally { mux?.Dispose(); }
    }

    /// <summary>Writes the audio frames due up to this video sample, then the sample.</summary>
    private static void WriteSample(Mp4Muxer mux, List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, int[] ai, long pts, bool key, byte[] data)
    {
        for (int t = 0; t < audio.Count; t++)
        {
            var frames = audio[t].frames;
            while (ai[t] < frames.Count && frames[ai[t]].Pts <= pts)
            {
                mux.WriteAudio(t, frames[ai[t]].Pts, frames[ai[t]].Data);
                ai[t]++;
            }
        }
        mux.WriteVideo(pts, pts, key, data);
    }
}
