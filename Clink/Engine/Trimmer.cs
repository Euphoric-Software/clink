using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Audio;
using Clink.Encode;
using Clink.Native;

namespace Clink.Engine;

/// <summary>How one audio track of the source should end up in the trimmed file.</summary>
/// <param name="Include">False drops the track from the export.</param>
/// <param name="Gain">Linear volume (1 = untouched, copied without re-encoding).</param>
internal readonly record struct TrackMix(bool Include, float Gain);

/// <summary>
/// Trims an MP4 made by Clink: copies compressed samples in [start, end] into a new file without re-encoding.
/// Audio tracks can be dropped or re-encoded with a gain (see <see cref="TrackMix"/>); the video is never re-encoded.
/// </summary>
internal static unsafe class Trimmer
{
    private const uint AnyStream = 0xFFFFFFFE;
    private const uint AllStreams = 0xFFFFFFFE;

    public static void Trim(string src, string dst, double startSec, double endSec, string[]? audioNames)
        => Trim(src, dst, startSec, endSec, audioNames, null);

    public static void Trim(string src, string dst, double startSec, double endSec, string[]? audioNames, TrackMix[]? mix)
    {
        Mf.EnsureStarted();
        // Round rather than truncate: a start snapped to a keyframe must equal that keyframe's tick exactly.
        long start = (long)Math.Round(startSec * 10_000_000), end = (long)Math.Round(endSec * 10_000_000);
        if (end <= start) throw new ArgumentException("End must be after start");

        IMFSourceReader* reader;
        PInvoke.MFCreateSourceReaderFromURL(src, null, &reader).Check("MFCreateSourceReaderFromURL");
        try
        {
            // Discover streams (native/compressed types).
            int videoIndex = -1;
            bool hevc = false; int w = 0, h = 0; byte[]? seqHeader = null;
            var audioStreams = new List<(uint index, AudioTrackDesc desc, float gain, bool reencode)>();
            int audioOrdinal = 0;
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                if (reader->GetNativeMediaType(i, 0, &t).Failed) break;
                var a = (IMFAttributes*)t;
                Guid major = Mf.GetGuid(a, PInvoke.MF_MT_MAJOR_TYPE);
                Guid sub = Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE);
                if (major == PInvoke.MFMediaType_Video && videoIndex < 0 && (sub == PInvoke.MFVideoFormat_H264 || sub == PInvoke.MFVideoFormat_HEVC))
                {
                    videoIndex = (int)i;
                    hevc = sub == PInvoke.MFVideoFormat_HEVC;
                    ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE);
                    w = (int)(size >> 32); h = (int)(size & 0xFFFFFFFF);
                    seqHeader = Mf.GetBlob(a, PInvoke.MF_MT_MPEG_SEQUENCE_HEADER);
                    reader->SetStreamSelection(i, true);
                }
                else if (major == PInvoke.MFMediaType_Audio && sub == PInvoke.MFAudioFormat_AAC)
                {
                    int n = audioOrdinal++;
                    TrackMix m = mix != null && n < mix.Length ? mix[n] : new TrackMix(true, 1f);
                    if (!m.Include)
                    {
                        reader->SetStreamSelection(i, false); // dropped from the export
                    }
                    else
                    {
                        bool reencode = Math.Abs(m.Gain - 1f) >= 0.001f;
                        var ud = Mf.GetBlob(a, PInvoke.MF_MT_USER_DATA);
                        byte[] asc = ud != null && ud.Length > 12 ? ud.AsSpan(12).ToArray() : new byte[] { 0x11, 0x90 };
                        var desc = new AudioTrackDesc
                        {
                            Name = audioNames != null && n < audioNames.Length ? audioNames[n] : $"Audio {n + 1}",
                            AudioSpecificConfig = asc,
                            SampleRate = reencode ? 48000 : (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000),
                            Channels = reencode ? 2 : (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2),
                            Bitrate = reencode ? 24000 * 8 : (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000) * 8,
                        };
                        audioStreams.Add((i, desc, m.Gain, reencode));
                        // Re-encoded tracks are decoded by their own reader later; the compressed samples are not needed here.
                        reader->SetStreamSelection(i, !reencode);
                    }
                }
                else reader->SetStreamSelection(i, false);
                t->Release();
            }
            if (videoIndex < 0) throw new InvalidOperationException("No H.264/HEVC video stream in file");

            // Read everything in range. Video: from the last keyframe at/before start; audio: frames overlapping [start,end].
            var video = new List<VideoSample>();
            var audio = audioStreams.Select(_ => new List<AacFrame>()).ToList();
            var ps = new ParameterSets();
            if (seqHeader != null) Nal.ToLengthPrefixed(seqHeader, hevc, ps);
            int ended = 0, selected = 1 + audioStreams.Count(s => !s.reencode);
            long frameDur = 213_333;
            var pending = new List<VideoSample>(); // frames since last keyframe before `start`
            while (ended < selected)
            {
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(AnyStream, 0, &idx, &flags, &ts, &sample).Check("ReadSample");
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) ended++;
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0) { if (sample != null) sample->Release(); break; }
                if (sample == null) continue;
                try
                {
                    if (ts > end + 10_000_000) continue;
                    byte[] data = Mf.ReadSampleBytes(sample);
                    if (idx == (uint)videoIndex)
                    {
                        bool key = Mf.GetU32((IMFAttributes*)sample, PInvoke.MFSampleExtension_CleanPoint) != 0 || Nal.ContainsKeyframe(data, hevc);
                        var vs = new VideoSample { Pts = ts, Keyframe = key, Data = data };
                        if (ts < start)
                        {
                            if (key) pending.Clear();
                            pending.Add(vs);
                        }
                        else if (ts <= end)
                        {
                            if (video.Count == 0)
                            {
                                // The first frame in range is the origin when it is itself a keyframe (what keyframe snapping
                                // produces); the GOP collected before it is only needed when it is not.
                                if (key) pending.Clear();
                                video.AddRange(pending);
                                pending.Clear();
                                if (video.Count == 0 && !key) continue;
                            }
                            video.Add(vs);
                        }
                    }
                    else
                    {
                        uint streamIdx = idx;
                        int ai = audioStreams.FindIndex(s => s.index == streamIdx);
                        if (ai < 0) continue;
                        // Keep everything up to `end`; the cut at the (keyframe-snapped) origin happens below once it is known,
                        // so audio starts together with the video instead of at the requested start.
                        if (ts <= end) audio[ai].Add(new AacFrame(ts, data));
                    }
                }
                finally { sample->Release(); }
            }
            if (video.Count == 0 || !video[0].Keyframe) throw new InvalidOperationException("Could not find a keyframe before the start time");

            // The clip starts exactly on the keyframe at/before the requested start (Media Foundation players ignore
            // edit lists, so we don't rely on them for video). Audio is cut to the same instant.
            long origin = video[0].Pts;
            var tracks = new List<(AudioTrackDesc desc, List<AacFrame> frames)>();
            for (int i = 0; i < audioStreams.Count; i++)
            {
                var (index, desc, gain, reencode) = audioStreams[i];
                if (!reencode)
                {
                    tracks.Add((desc, audio[i].Where(f => f.Pts + frameDur > origin).ToList()));
                }
                else
                {
                    var frames = ReencodeTrack(src, index, origin, end, gain, out byte[] asc);
                    desc.AudioSpecificConfig = asc;
                    tracks.Add((desc, frames));
                }
            }
            ClipWriter.Write(dst, video, hevc, w, h, seqHeader, tracks, origin, 0);
        }
        finally { reader->Release(); }
    }

    /// <summary>
    /// Decodes one audio stream from <paramref name="origin"/> to <paramref name="end"/>, applies a gain and re-encodes it to
    /// AAC. Output frame k gets pts origin + k * 1024 / 48 kHz so the track lines up with the passthrough ones.
    /// </summary>
    private static List<AacFrame> ReencodeTrack(string src, uint stream, long origin, long end, float gain, out byte[] asc)
    {
        const int Rate = 48000, Block = AacEncoder.FrameSamples;
        long frameDur = Block * 10_000_000L / Rate;
        var frames = new List<AacFrame>();
        IMFSourceReader* reader;
        PInvoke.MFCreateSourceReaderFromURL(src, null, &reader).Check("MFCreateSourceReaderFromURL(audio)");
        try
        {
            reader->SetStreamSelection(AllStreams, false);
            reader->SetStreamSelection(stream, true).Check("SetStreamSelection(audio)");
            IMFMediaType* want = Mf.CreateMediaType();
            try
            {
                var a = (IMFAttributes*)want;
                Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
                Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_Float);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, 8);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Rate * 8);
                Mf.SetU32(a, PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                reader->SetCurrentMediaType(stream, null, want).Check("SetCurrentMediaType(float 48k stereo)");
            }
            finally { want->Release(); }

            if (origin > 0)
            {
                PROPVARIANT pos = default;
                pos.vt = VARENUM.VT_I8;
                pos.hVal = origin;
                Guid timeFormat = Guid.Empty;
                reader->SetCurrentPosition(&timeFormat, &pos);
            }

            using var enc = new AacEncoder(Rate, 2, 24000);
            asc = enc.AudioSpecificConfig;
            long originFrame = (long)Math.Round(origin * (Rate / 10_000_000.0));
            long endFrame = (long)Math.Round(end * (Rate / 10_000_000.0));
            long next = originFrame;                  // next timeline frame the accumulator expects
            var acc = new float[Block * 2];
            int accN = 0;
            long inBlocks = 0, outIndex = 0;
            var pcm = new byte[Block * 4];

            void Flush()
            {
                for (int i = 0; i < Block * 2; i++)
                {
                    float x = acc[i] * gain;
                    x /= MathF.Max(1f, MathF.Abs(x)); // soft clip
                    int s = (int)MathF.Round(x * 32767f);
                    pcm[i * 2] = (byte)s; pcm[i * 2 + 1] = (byte)(s >> 8);
                }
                long inPts = origin + inBlocks++ * frameDur;
                enc.Encode(pcm, inPts, (_, data) =>
                {
                    long pts = origin + outIndex++ * frameDur;
                    if (pts <= end) frames.Add(new AacFrame(pts, data));
                });
                accN = 0;
            }

            void Feed(ReadOnlySpan<float> stereo)
            {
                while (stereo.Length >= 2)
                {
                    int n = Math.Min(stereo.Length / 2, Block - accN);
                    stereo.Slice(0, n * 2).CopyTo(acc.AsSpan(accN * 2));
                    accN += n;
                    stereo = stereo.Slice(n * 2);
                    if (accN == Block) Flush();
                }
            }

            void FeedSilence(long count)
            {
                Span<float> zero = stackalloc float[512 * 2];
                while (count > 0)
                {
                    int n = (int)Math.Min(512, count);
                    Feed(zero.Slice(0, n * 2));
                    count -= n;
                }
            }

            while (next < endFrame)
            {
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample(audio)");
                if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0)
                {
                    if (sample != null) sample->Release();
                    break;
                }
                if (sample == null) continue;
                try
                {
                    if (ts > end + 10_000_000) break;
                    byte[] raw = Mf.ReadSampleBytes(sample);
                    int n = raw.Length / 8;
                    if (n == 0) continue;
                    long sf = (long)Math.Round(ts * (Rate / 10_000_000.0));
                    ReadOnlySpan<float> stereo = MemoryMarshal.Cast<byte, float>(raw.AsSpan(0, n * 8));
                    if (sf + n <= next) continue;           // entirely before the cut
                    if (sf > next)
                    {
                        long gap = sf - next;
                        if (gap <= 48) { }                 // sub-millisecond jitter
                        else if (gap < Rate * 10) FeedSilence(gap);
                        else { /* hole: keep timeline but skip filling */ }
                        next = sf;
                    }
                    else if (sf < next)
                    {
                        int skip = (int)(next - sf);
                        stereo = stereo.Slice(skip * 2);
                        n -= skip;
                    }
                    Feed(stereo);
                    next += n;
                }
                finally { sample->Release(); }
            }
            // Pad the partial block and push the encoder's delay out so the tail is not lost.
            if (accN > 0) { acc.AsSpan(accN * 2).Clear(); Flush(); }
            for (int i = 0; i < 3; i++) FeedSilence(Block);
            return frames;
        }
        finally { reader->Release(); }
    }

    /// <summary>Keyframe timestamps (seconds) of the first video stream; used to snap the trim start.</summary>
    public static List<double> GetKeyframeTimes(string path)
    {
        Mf.EnsureStarted();
        var result = new List<double>();
        IMFSourceReader* reader;
        PInvoke.MFCreateSourceReaderFromURL(path, null, &reader).Check("MFCreateSourceReaderFromURL");
        try
        {
            uint videoIndex = uint.MaxValue;
            bool hevc = false;
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                if (reader->GetNativeMediaType(i, 0, &t).Failed) break;
                Guid major = Mf.GetGuid((IMFAttributes*)t, PInvoke.MF_MT_MAJOR_TYPE);
                Guid sub = Mf.GetGuid((IMFAttributes*)t, PInvoke.MF_MT_SUBTYPE);
                t->Release();
                bool isVideo = major == PInvoke.MFMediaType_Video && videoIndex == uint.MaxValue;
                if (isVideo) { videoIndex = i; hevc = sub == PInvoke.MFVideoFormat_HEVC; }
                reader->SetStreamSelection(i, isVideo);
            }
            if (videoIndex == uint.MaxValue) return result;
            while (true)
            {
                uint idx, flags; long ts; IMFSample* sample;
                if (reader->ReadSample(videoIndex, 0, &idx, &flags, &ts, &sample).Failed) break;
                if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0) { if (sample != null) sample->Release(); break; }
                if (sample == null) continue;
                try
                {
                    bool key = Mf.GetU32((IMFAttributes*)sample, PInvoke.MFSampleExtension_CleanPoint) != 0;
                    if (!key)
                    {
                        IMFMediaBuffer* buf;
                        if (sample->ConvertToContiguousBuffer(&buf).Succeeded)
                        {
                            byte* p; uint max, cur;
                            if (buf->Lock(&p, &max, &cur).Succeeded) { key = Nal.ContainsKeyframe(new ReadOnlySpan<byte>(p, (int)Math.Min(cur, 64)), hevc); buf->Unlock(); }
                            buf->Release();
                        }
                    }
                    if (key) result.Add(ts / 10_000_000.0);
                }
                finally { sample->Release(); }
            }
        }
        finally { reader->Release(); }
        return result;
    }
}
