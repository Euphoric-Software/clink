using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Engine;

internal sealed record ProbeStream(int Index, bool IsVideo, string Subtype, int Width, int Height, int Channels, int SampleRate,
    long Count, long FirstTs, long LastTs, long MinDelta, long MaxDelta, long Keyframes, long Bytes, long OutOfOrder);

/// <summary>Reads an MP4 with the Media Foundation source reader to report stream timing and (optionally) decode the first frame.</summary>
internal static unsafe class MediaProbe
{
    private const uint AnyStream = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;
    private const uint FirstVideo = 0xFFFFFFFC;

    public static (long duration100ns, List<ProbeStream> streams) Probe(string path)
    {
        Mf.EnsureStarted();
        IMFSourceReader* reader;
        PInvoke.MFCreateSourceReaderFromURL(path, null, &reader).Check("MFCreateSourceReaderFromURL");
        try
        {
            var streams = new List<ProbeStream>();
            var acc = new Dictionary<uint, (ProbeStream s, long prev)>();
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                if (reader->GetNativeMediaType(i, 0, &t).Failed) break;
                var a = (IMFAttributes*)t;
                Guid major = Mf.GetGuid(a, PInvoke.MF_MT_MAJOR_TYPE);
                Guid sub = Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE);
                bool video = major == PInvoke.MFMediaType_Video;
                ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE);
                var ps = new ProbeStream((int)i, video, SubtypeName(sub), (int)(size >> 32), (int)(size & 0xFFFFFFFF),
                    (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS), (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND),
                    0, long.MinValue, long.MinValue, long.MaxValue, long.MinValue, 0, 0, 0);
                acc[i] = (ps, long.MinValue);
                t->Release();
                reader->SetStreamSelection(i, true);
            }

            long duration = 0;
            PROPVARIANT pv = default;
            Guid durKey = PInvoke.MF_PD_DURATION;
            if (reader->GetPresentationAttribute(MediaSource, &durKey, &pv).Succeeded && pv.vt == VARENUM.VT_UI8) duration = (long)pv.uhVal;
            PInvoke.PropVariantClear(ref pv);

            int ended = 0;
            while (ended < acc.Count)
            {
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(AnyStream, 0, &idx, &flags, &ts, &sample).Check("ReadSample");
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) { ended++; }
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0) { if (sample != null) sample->Release(); break; }
                if (sample == null) continue;
                try
                {
                    var (s, prev) = acc[idx];
                    long delta = prev == long.MinValue ? long.MinValue : ts - prev;
                    IMFMediaBuffer* buf;
                    uint len = 0;
                    if (sample->ConvertToContiguousBuffer(&buf).Succeeded) { buf->GetCurrentLength(&len); buf->Release(); }
                    bool key = Mf.GetU32((IMFAttributes*)sample, PInvoke.MFSampleExtension_CleanPoint) != 0;
                    s = s with
                    {
                        Count = s.Count + 1,
                        FirstTs = s.FirstTs == long.MinValue ? ts : s.FirstTs,
                        LastTs = ts,
                        MinDelta = delta == long.MinValue ? s.MinDelta : Math.Min(s.MinDelta, delta),
                        MaxDelta = delta == long.MinValue ? s.MaxDelta : Math.Max(s.MaxDelta, delta),
                        Keyframes = s.Keyframes + (key ? 1 : 0),
                        Bytes = s.Bytes + len,
                        OutOfOrder = s.OutOfOrder + (delta != long.MinValue && delta < 0 ? 1 : 0),
                    };
                    acc[idx] = (s, ts);
                }
                finally { sample->Release(); }
            }
            foreach (var kv in acc.OrderBy(k => k.Key)) streams.Add(kv.Value.s);
            return (duration, streams);
        }
        finally { reader->Release(); }
    }

    /// <summary>Decodes the video frame nearest <paramref name="at100ns"/> to BGRA bytes (top-down).</summary>
    public static (int w, int h, byte[] bgra)? DecodeFrame(string path, long at100ns)
    {
        Mf.EnsureStarted();
        IMFAttributes* attrs = Mf.CreateAttributes(2);
        Mf.SetU32(attrs, PInvoke.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
        IMFSourceReader* reader;
        HRESULT hr = PInvoke.MFCreateSourceReaderFromURL(path, attrs, &reader);
        attrs->Release();
        hr.Check("MFCreateSourceReaderFromURL");
        try
        {
            IMFMediaType* want = Mf.CreateMediaType();
            Mf.SetGuid((IMFAttributes*)want, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid((IMFAttributes*)want, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            hr = reader->SetCurrentMediaType(FirstVideo, null, want);
            want->Release();
            hr.Check("SetCurrentMediaType(RGB32)");

            IMFMediaType* cur;
            reader->GetCurrentMediaType(FirstVideo, &cur).Check("GetCurrentMediaType");
            ulong size = Mf.GetU64((IMFAttributes*)cur, PInvoke.MF_MT_FRAME_SIZE);
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            int stride = (int)Mf.GetU32((IMFAttributes*)cur, PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(w * 4));
            cur->Release();

            if (at100ns > 0)
            {
                PROPVARIANT pos = default;
                pos.vt = VARENUM.VT_I8;
                pos.hVal = at100ns;
                Guid timeFormat = Guid.Empty;
                reader->SetCurrentPosition(&timeFormat, &pos);
            }
            for (int tries = 0; tries < 200; tries++)
            {
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(FirstVideo, 0, &idx, &flags, &ts, &sample).Check("ReadSample");
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return null;
                if (sample == null) continue;
                try
                {
                    if (ts + 1 < at100ns - 20_000_000) continue; // keep decoding until we reach the target (seek lands on a keyframe)
                    byte[] raw = Mf.ReadSampleBytes(sample);
                    var bgra = new byte[w * h * 4];
                    int absStride = Math.Abs(stride);
                    bool bottomUp = stride < 0;
                    for (int y = 0; y < h; y++)
                    {
                        int srcRow = bottomUp ? (h - 1 - y) : y;
                        Buffer.BlockCopy(raw, srcRow * absStride, bgra, y * w * 4, w * 4);
                    }
                    return (w, h, bgra);
                }
                finally { sample->Release(); }
            }
            return null;
        }
        finally { reader->Release(); }
    }

    public static void SavePng(string pngPath, int w, int h, byte[] bgra)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(pngPath);
        enc.Save(fs);
    }

    public static string Report(string path)
    {
        var (dur, streams) = Probe(path);
        var sb = new StringBuilder();
        sb.AppendLine($"{Path.GetFileName(path)}: duration {dur / 10_000_000.0:0.000}s, {new FileInfo(path).Length / 1_048_576.0:0.0} MB");
        foreach (var s in streams)
        {
            string kind = s.IsVideo ? $"video {s.Subtype} {s.Width}x{s.Height}" : $"audio {s.Subtype} {s.Channels}ch {s.SampleRate}Hz";
            sb.AppendLine($"  #{s.Index} {kind}: {s.Count} samples, first={s.FirstTs / 10_000.0:0.0}ms last={s.LastTs / 10_000.0:0.0}ms " +
                          $"delta {s.MinDelta / 10_000.0:0.00}..{s.MaxDelta / 10_000.0:0.00}ms{(s.IsVideo ? $", {s.Keyframes} keyframes" : "")}, {s.Bytes / 1024} KB, outOfOrder={s.OutOfOrder}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The loudness of each audio track, in file order with its name: RMS and peak of the decoded audio in dBFS, on the clip's
    /// timeline (the edit lists applied). For checking by ear-less means that a volume really went into a track.
    /// </summary>
    public static string Levels(string path)
    {
        var streams = Trimmer.AudioStreamsOf(path);
        var info = Trimmer.ReadAudioTracks(path);
        if (info != null && info.Length != streams.Count) info = null;
        var sb = new StringBuilder();
        sb.AppendLine($"{Path.GetFileName(path)}: audio levels (dBFS of the decoded audio, RMS / peak)");
        var block = new float[2 * Trimmer.PcmTrackReader.Block];
        for (int n = 0; n < streams.Count; n++)
        {
            string name = info != null ? info[n].Name : $"Audio {n + 1}";
            using var reader = new Trimmer.PcmTrackReader(path, streams[n], info != null ? info[n].StartFrames : 0, 0, long.MaxValue / 4);
            double sum = 0, peak = 0;
            long frames = 0;
            int got;
            while ((got = reader.ReadBlock(block)) > 0)
            {
                for (int i = 0; i < got * 2; i++)
                {
                    double v = block[i];
                    sum += v * v;
                    peak = Math.Max(peak, Math.Abs(v));
                }
                frames += got;
            }
            double rms = frames > 0 ? Math.Sqrt(sum / (frames * 2)) : 0;
            sb.AppendLine($"  track {n + 1} {name,-9} {Db(rms),9} / {Db(peak),-9}  {frames / (double)Trimmer.PcmTrackReader.Rate:0.00} s");
        }
        return sb.ToString();
    }

    private static string Db(double linear) => linear < 1e-6 ? "silent" : $"{20 * Math.Log10(linear):0.0} dB";

    private static string SubtypeName(Guid g)
    {
        if (g == PInvoke.MFVideoFormat_H264) return "H264";
        if (g == PInvoke.MFVideoFormat_HEVC) return "HEVC";
        if (g == PInvoke.MFAudioFormat_AAC) return "AAC";
        if (g == PInvoke.MFAudioFormat_PCM) return "PCM";
        return g.ToString();
    }
}
