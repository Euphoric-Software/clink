using Clink.Audio;
using Clink.Encode;
using Clink.Native;

namespace Clink.Engine;

/// <summary>
/// The clip <see cref="ClipSource"/>'s timeline self-check reads: a 16x16 gray picture and two silent audio tracks written by the
/// recording's own muxer, one starting 10 ms before the clip origin (its edit list is one edit that skips the first 480 samples) and
/// one 25 ms after it (an empty edit, then the media), the two forms of edit list the muxer writes. Read back with the edit lists
/// hidden from Media Foundation, both have to hand out their first frame at 0 and the next ones one frame (1024 samples) apart.
/// </summary>
internal static class TimelineProbe
{
    /// <summary>Where the two tracks start, in samples (48 kHz) from the clip origin: the Mix 10 ms before it, the Game 25 ms after it.</summary>
    public const int MixStart = -480, GameStart = 1200;

    private const int Rate = 48000, Frames = 24;     // 0.5 s of each track
    private const long Origin = 1_000_000_000;       // an arbitrary clock reading, as a recording has

    /// <summary>A mid-gray 16x16 H.264 picture as an Annex B access unit (SPS: baseline, PPS, and an IDR slice with one I_PCM macroblock).</summary>
    public static readonly byte[] Idr = BuildIdr();

    private static byte[] BuildIdr()
    {
        var au = new List<byte>();
        byte[] sc = { 0, 0, 0, 1 };
        au.AddRange(sc); au.AddRange(new byte[] { 0x67, 0x42, 0xC0, 0x0A, 0xDA, 0x79 });
        au.AddRange(sc); au.AddRange(new byte[] { 0x68, 0xCE, 0x38, 0x80 });
        au.AddRange(sc); au.AddRange(new byte[] { 0x65, 0x88, 0x84, 0x86, 0x80 });
        au.AddRange(Enumerable.Repeat((byte)0x80, 384));    // 256 luma + 2 x 64 chroma samples
        au.Add(0x80);                                        // rbsp trailing bits
        return au.ToArray();
    }

    /// <summary>Writes the probe clip to <paramref name="path"/> (which must not exist). Needs the AAC encoder, like a recording does.</summary>
    public static void Write(string path)
    {
        Mf.EnsureStarted();
        var audio = new List<(AudioTrackDesc desc, List<AacFrame> frames)>();
        double unit = 10_000_000.0 / Rate;
        foreach (var (name, start) in new[] { ("Mix", MixStart), ("Game", GameStart) })
        {
            using var enc = new AacEncoder(Rate, 2, 24000);
            var frames = new List<AacFrame>();
            long first = Origin + (long)Math.Round(start * unit);
            var silence = new byte[AacEncoder.FrameSamples * 4];
            int k = 0;
            // The frames are stamped by their number, exactly (the encoder's own timestamps are not needed).
            for (int input = 0; input < Frames + 4; input++)
                enc.Encode(silence, first, (_, data) => { if (k < Frames) frames.Add(new AacFrame(first + (long)Math.Round(k++ * AacEncoder.FrameSamples * unit), data)); });
            audio.Add((new AudioTrackDesc { Name = name, AudioSpecificConfig = enc.AudioSpecificConfig, Bitrate = enc.BytesPerSecond * 8, SampleRate = Rate, Channels = 2 }, frames));
        }
        var video = new[] { new VideoSample { Pts = Origin, Keyframe = true, Data = (byte[])Idr.Clone() } };
        ClipWriter.Write(path, video, "h264", 16, 16, null, audio, Origin, 0);
    }
}
