using Clink.Audio;
using Clink.Encode;

namespace Clink.Engine;

internal sealed record ClipResult(string Path, double DurationSeconds, long SizeBytes, string? Game);

/// <summary>Turns a replay-buffer snapshot into an MP4 file.</summary>
internal static class ClipWriter
{
    public static void Write(string path, List<VideoSample> video, bool hevc, int width, int height, byte[]? sequenceHeader,
        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long origin100ns, uint fps)
    {
        var ps = new ParameterSets();
        if (sequenceHeader != null) Nal.ToLengthPrefixed(sequenceHeader, hevc, ps);

        // Snap capture timestamps to the nominal frame grid so the file is clean constant-frame-rate
        // (the pacer jitters by a millisecond or two); keep timestamps strictly increasing.
        double period = fps > 0 ? 10_000_000.0 / fps : 0;
        var converted = new List<(long pts, bool key, byte[] data)>(video.Count);
        long prev = long.MinValue;
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
            converted.Add((q, v.Keyframe, Nal.ToLengthPrefixed(v.Data, hevc, ps)));
        }
        if (!ps.Complete(hevc)) throw new InvalidOperationException("Encoder did not provide SPS/PPS (no keyframe yet?)");

        var vdesc = new VideoTrackDesc
        {
            Hevc = hevc,
            Width = width,
            Height = height,
            CodecConfig = hevc ? Nal.BuildHvcC(ps.Vps!, ps.Sps!, ps.Pps!) : Nal.BuildAvcC(ps.Sps!, ps.Pps!),
        };

        using var mux = new Mp4Muxer(path, vdesc, audio.Select(a => a.desc), origin100ns);
        var ai = new int[audio.Count];
        foreach (var (pts, key, data) in converted)
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
        for (int t = 0; t < audio.Count; t++)
        {
            var frames = audio[t].frames;
            while (ai[t] < frames.Count) { mux.WriteAudio(t, frames[ai[t]].Pts, frames[ai[t]].Data); ai[t]++; }
        }
        mux.Finish();
    }
}
