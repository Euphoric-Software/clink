using Clink.Audio;
using Clink.Encode;

namespace Clink.Engine;

/// <param name="AudioTracks">Names of the audio tracks in the file, in file order (what the library and the trim editor label them with).</param>
internal sealed record ClipResult(string Path, double DurationSeconds, long SizeBytes, string? Game, string[] AudioTracks);

/// <summary>
/// Turns a replay-buffer snapshot into an MP4 file. Samples are streamed: each one is converted and handed to the muxer in
/// turn and its snapshot bytes are released right after, so a long clip (the replay length goes up to an hour) costs one
/// copy of itself in managed memory, not the snapshot plus a converted twin of it.
/// </summary>
internal static class ClipWriter
{
    /// <summary>H.264 / HEVC only (kept for callers that still think in terms of a bool).</summary>
    public static string[] Write(string path, IEnumerable<VideoSample> video, bool hevc, int width, int height, byte[]? sequenceHeader,
        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long origin100ns, uint fps)
        => Write(path, video, hevc ? "hevc" : "h264", width, height, sequenceHeader, audio, origin100ns, fps);

    /// <param name="video">Video samples in order, starting on a keyframe. Each sample's <see cref="VideoSample.Data"/> is released once written.</param>
    /// <param name="codec">"h264" | "hevc" | "av1" (experimental). Video samples are Annex B access units for H.264/HEVC, temporal units for AV1.</param>
    /// <param name="sequenceHeader">Encoder-provided parameter sets (Annex B) or AV1 Sequence Header OBU; may be null when the samples carry them in-band.</param>
    /// <returns>Names of the audio tracks written, in file order (a track with no frames is left out of the file).</returns>
    public static string[] Write(string path, IEnumerable<VideoSample> video, string codec, int width, int height, byte[]? sequenceHeader,
        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long origin100ns, uint fps)
    {
        bool av1 = codec == "av1", hevc = codec == "hevc";
        var ps = new ParameterSets();
        byte[]? seqObu = null;
        if (av1) { if (sequenceHeader != null) seqObu = Av1.FindSequenceHeader(sequenceHeader); }
        else if (sequenceHeader != null) Nal.ToLengthPrefixed(sequenceHeader, hevc, ps);

        // Snap capture timestamps to the nominal frame grid so the file is clean constant-frame-rate
        // (the pacer jitters by a millisecond or two); keep timestamps strictly increasing (see SnapToGrid).
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
                if (fps > 0) q = SnapToGrid(v.Pts, prev, origin100ns, period);
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
            return mux.WrittenAudioTracks;
        }
        finally { mux?.Dispose(); }
    }

    /// <summary>
    /// Puts a frame on the nearest slot of the exact frame grid (origin + round(k * period)), after <paramref name="prev"/>.
    /// When that slot is taken, the frame moves to the next free slot if that is at most 3/4 of a period after its real time:
    /// that is jitter around a half-slot boundary (the pacer steps by a whole number of 100 ns units, so over a long clip its
    /// frames drift against the exact grid, and its catch-up reset can land on any phase), and staying on the upper slot keeps
    /// the file one stts run. A free slot further ahead than that means a catch-up burst after a late wake: the frame keeps its
    /// real time, so the frames after it land on their own slots instead of all running late against the audio. Either way a
    /// frame ends up at most 3/4 of a period from when it was captured.
    /// </summary>
    private static long SnapToGrid(long pts, long prev, long origin100ns, double period)
    {
        long k = (long)Math.Round((pts - origin100ns) / period);
        long q = origin100ns + (long)Math.Round(k * period);
        if (q > prev) return q;
        k = (long)Math.Floor((prev - origin100ns) / period) + 1;   // first slot after prev
        long next = origin100ns + (long)Math.Round(k * period);
        if (next <= prev) next = origin100ns + (long)Math.Round((k + 1) * period);
        return next - pts <= 0.75 * period ? next : Math.Max(prev + 1, pts);
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
