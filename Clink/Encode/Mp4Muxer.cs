using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Clink.Encode;

internal sealed class VideoTrackDesc
{
    /// <summary>"h264" | "hevc" | "av1" (AV1 is experimental).</summary>
    public string Codec = "h264";
    /// <summary>Alias of <see cref="Codec"/> == "hevc"; setting it only switches between h264 and hevc.</summary>
    public bool Hevc { get => Codec == "hevc"; set { if (value) Codec = "hevc"; else if (Codec == "hevc") Codec = "h264"; } }
    public bool Av1 => Codec == "av1";
    public int Width, Height;
    public byte[] CodecConfig = Array.Empty<byte>(); // avcC, hvcC or av1C payload
    public int Timescale = 90000;

    /// <summary>Sample entry fourcc: avc1 / hvc1 / av01.</summary>
    public string SampleEntry => Av1 ? "av01" : Hevc ? "hvc1" : "avc1";
    /// <summary>Decoder configuration box: avcC / hvcC / av1C.</summary>
    public string ConfigBox => Av1 ? "av1C" : Hevc ? "hvcC" : "avcC";
}

internal sealed class AudioTrackDesc
{
    public string Name = "Audio";
    public byte[] AudioSpecificConfig = Array.Empty<byte>();
    public int SampleRate = 48000;
    public int Channels = 2;
    public int Bitrate = 192000;
    public int SamplesPerFrame = 1024;
}

/// <summary>
/// Minimal ISO BMFF writer: one video track (avc1/hvc1, or av01 for experimental AV1) + any number of AAC audio tracks, with edit lists so
/// every track starts exactly at the clip origin. Samples go to a temp file first; Finish() writes a
/// fast-start file (ftyp, moov, mdat).
/// </summary>
internal sealed class Mp4Muxer : IDisposable
{
    private sealed class Track
    {
        public bool IsVideo;
        public VideoTrackDesc? V;
        public AudioTrackDesc? A;
        public int Timescale;
        public readonly List<long> Dts = new();     // in track timescale, relative to first sample
        public readonly List<long> Pts = new();
        public readonly List<uint> Sizes = new();
        public readonly List<long> Offsets = new();  // offset within mdat payload
        public readonly List<int> KeyIndices = new();
        public long FirstPts100ns = long.MinValue;
        public long LastEnd100ns;
    }

    private readonly string _finalPath;
    private readonly string _tempPath;
    private readonly FileStream _temp;
    private readonly Track _video;
    private readonly List<Track> _audio = new();
    private readonly long _origin100ns;
    private long _payload;
    private bool _finished;

    /// <param name="origin100ns">Timeline position that becomes t=0 of the movie.</param>
    public Mp4Muxer(string path, VideoTrackDesc video, IEnumerable<AudioTrackDesc> audio, long origin100ns)
    {
        _finalPath = path;
        _tempPath = path + ".tmp";
        _temp = new FileStream(_tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        _video = new Track { IsVideo = true, V = video, Timescale = video.Timescale };
        foreach (var a in audio) _audio.Add(new Track { A = a, Timescale = a.SampleRate });
        _origin100ns = origin100ns;
    }

    public int AudioTrackCount => _audio.Count;

    /// <summary>Names of the audio tracks the file holds, in file order (a track that got no frames is left out of it).</summary>
    public string[] WrittenAudioTracks => _audio.Where(a => a.Sizes.Count > 0).Select(a => a.A!.Name).ToArray();

    public void WriteVideo(long pts100ns, long dts100ns, bool keyframe, ReadOnlySpan<byte> lengthPrefixed)
    {
        var t = _video;
        if (t.FirstPts100ns == long.MinValue) t.FirstPts100ns = dts100ns;
        long rel = dts100ns - t.FirstPts100ns;
        t.Dts.Add(ToScale(rel, t.Timescale));
        t.Pts.Add(ToScale(pts100ns - t.FirstPts100ns, t.Timescale));
        if (keyframe) t.KeyIndices.Add(t.Sizes.Count);
        Append(t, lengthPrefixed);
        t.LastEnd100ns = dts100ns;
    }

    public void WriteAudio(int track, long pts100ns, ReadOnlySpan<byte> aacFrame)
    {
        var t = _audio[track];
        if (t.FirstPts100ns == long.MinValue) t.FirstPts100ns = pts100ns;
        long rel = pts100ns - t.FirstPts100ns;
        long scaled = ToScale(rel, t.Timescale);
        t.Dts.Add(scaled);
        t.Pts.Add(scaled);
        Append(t, aacFrame);
        t.LastEnd100ns = pts100ns;
    }

    private void Append(Track t, ReadOnlySpan<byte> data)
    {
        t.Offsets.Add(_payload);
        t.Sizes.Add((uint)data.Length);
        _temp.Write(data);
        _payload += data.Length;
    }

    private static long ToScale(long v100ns, int timescale) => (long)Math.Round(v100ns * (double)timescale / 10_000_000.0);

    public void Finish()
    {
        if (_finished) return;
        _temp.Flush();

        var tracks = new List<Track> { _video };
        tracks.AddRange(_audio.Where(a => a.Sizes.Count > 0));

        // Fix the last sample duration of each track (needed for stts) and compute durations.
        const int movieTimescale = 1000;
        long movieDurationMs = 0;
        foreach (var t in tracks)
        {
            long end100ns = TrackEnd100ns(t);
            movieDurationMs = Math.Max(movieDurationMs, (end100ns - _origin100ns) / 10_000);
        }

        byte[] ftyp = BuildFtyp(_video.V!);
        // moov size depends on chunk offsets which depend on moov size: compute with a placeholder first, then rebuild with
        // real offsets. 32-bit offsets (stco) only when the whole file, moov included (several MB for a long clip), stays
        // under 4 GiB; co64 otherwise.
        byte[] moov = BuildMoov(tracks, movieTimescale, movieDurationMs, 0, false);
        bool use64 = ftyp.Length + moov.Length + 8L + _payload > uint.MaxValue;
        if (use64) moov = BuildMoov(tracks, movieTimescale, movieDurationMs, 0, true);
        long mdatHeaderSize = use64 ? 16 : 8;
        long mdatPayloadStart = ftyp.Length + moov.Length + mdatHeaderSize;
        moov = BuildMoov(tracks, movieTimescale, movieDurationMs, mdatPayloadStart, use64);
        if (ftyp.Length + moov.Length + mdatHeaderSize != mdatPayloadStart) throw new InvalidOperationException("moov size changed");

        // Written under a temporary name and renamed once complete, so a failure (disk full, access denied, the name taken)
        // never leaves a half-written clip under the real name.
        string part = _finalPath + ".part";
        try
        {
            using (var outFs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                outFs.Write(ftyp);
                outFs.Write(moov);
                Span<byte> hdr = stackalloc byte[16];
                if (use64)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
                    Encoding.ASCII.GetBytes("mdat", hdr.Slice(4));
                    BinaryPrimitives.WriteUInt64BigEndian(hdr.Slice(8), (ulong)(_payload + 16));
                    outFs.Write(hdr);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)(_payload + 8));
                    Encoding.ASCII.GetBytes("mdat", hdr.Slice(4));
                    outFs.Write(hdr.Slice(0, 8));
                }
                _temp.Position = 0;
                _temp.CopyTo(outFs);
            }
            File.Move(part, _finalPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        _finished = true;
        _temp.Dispose();
        try { File.Delete(_tempPath); } catch { }
    }

    private long TrackEnd100ns(Track t)
    {
        if (t.Sizes.Count == 0) return _origin100ns;
        long lastDur = t.IsVideo ? FrameDuration100ns(t) : t.A!.SamplesPerFrame * 10_000_000L / t.A.SampleRate;
        return t.LastEnd100ns + lastDur;
    }

    private static long FrameDuration100ns(Track t)
    {
        if (t.Dts.Count < 2) return 166_667;
        // median delta
        var deltas = new List<long>();
        for (int i = 1; i < Math.Min(t.Dts.Count, 200); i++) deltas.Add(t.Dts[i] - t.Dts[i - 1]);
        deltas.Sort();
        long d = deltas[deltas.Count / 2];
        return d * 10_000_000L / t.Timescale;
    }

    // ---------------------------------------------------------------- boxes

    private static byte[] BuildFtyp(VideoTrackDesc v)
    {
        var b = new Box("ftyp");
        b.Fourcc("isom"); b.U32(0x200);
        b.Fourcc("isom"); b.Fourcc("iso2"); b.Fourcc(v.SampleEntry); b.Fourcc("mp41");
        return b.ToArray();
    }

    private byte[] BuildMoov(List<Track> tracks, int movieTimescale, long movieDurationMs, long mdatPayloadStart, bool use64)
    {
        var moov = new Box("moov");
        // mvhd
        var mvhd = new Box("mvhd", version: 0, flags: 0);
        mvhd.U32(0); mvhd.U32(0); // creation/modification
        mvhd.U32((uint)movieTimescale);
        mvhd.U32((uint)movieDurationMs);
        mvhd.U32(0x00010000); // rate 1.0
        mvhd.U16(0x0100);     // volume
        mvhd.U16(0); mvhd.U32(0); mvhd.U32(0);
        WriteMatrix(mvhd);
        for (int i = 0; i < 6; i++) mvhd.U32(0);
        mvhd.U32((uint)(tracks.Count + 1)); // next track id
        moov.Child(mvhd);

        uint trackId = 1;
        bool firstAudio = true;
        foreach (var t in tracks)
        {
            // Only the first audio track (normally Mix) is enabled; the others are disabled alternates of it, as ffmpeg writes
            // them. Players that honour the flags (QuickTime, Safari, iOS) would otherwise play every audio track at once.
            bool enabled = t.IsVideo || firstAudio;
            if (!t.IsVideo) firstAudio = false;
            moov.Child(BuildTrak(t, trackId++, enabled, movieTimescale, movieDurationMs, mdatPayloadStart, use64));
        }
        return moov.ToArray();
    }

    private Box BuildTrak(Track t, uint trackId, bool enabled, int movieTimescale, long movieDurationMs, long mdatPayloadStart, bool use64)
    {
        var trak = new Box("trak");

        long firstPts100ns = t.FirstPts100ns;
        long trackEnd100ns = TrackEnd100ns(t);
        long mediaDuration = ToScale(trackEnd100ns - firstPts100ns, t.Timescale);

        // tkhd
        var tkhd = new Box("tkhd", 0, enabled ? 0x3u : 0x2u); // (enabled +) in movie
        tkhd.U32(0); tkhd.U32(0);
        tkhd.U32(trackId);
        tkhd.U32(0);
        tkhd.U32((uint)Math.Max(0, (trackEnd100ns - _origin100ns) / 10_000)); // duration in movie timescale (ms)
        tkhd.U32(0); tkhd.U32(0);
        tkhd.U16(0); tkhd.U16((ushort)(t.IsVideo ? 0 : 1)); // layer, alternate_group (the audio tracks are alternatives)
        tkhd.U16((ushort)(t.IsVideo ? 0 : 0x0100)); tkhd.U16(0);
        WriteMatrix(tkhd);
        tkhd.U32(t.IsVideo ? (uint)t.V!.Width << 16 : 0);
        tkhd.U32(t.IsVideo ? (uint)t.V!.Height << 16 : 0);
        trak.Child(tkhd);

        // edts/elst: align the track's first sample to the movie origin.
        var elst = new Box("elst", 0, 0);
        long startOffset100ns = firstPts100ns - _origin100ns; // >0: track starts late (empty edit); <0: skip into media
        if (startOffset100ns > 0)
        {
            elst.U32(2);
            elst.U32((uint)(startOffset100ns / 10_000)); elst.U32(0xFFFFFFFF); elst.U16(1); elst.U16(0); // empty edit
            elst.U32((uint)Math.Max(0, (trackEnd100ns - firstPts100ns) / 10_000)); elst.U32(0); elst.U16(1); elst.U16(0);
        }
        else
        {
            long mediaTime = ToScale(-startOffset100ns, t.Timescale);
            elst.U32(1);
            elst.U32((uint)Math.Max(0, (trackEnd100ns - _origin100ns) / 10_000)); elst.U32((uint)mediaTime); elst.U16(1); elst.U16(0);
        }
        var edts = new Box("edts"); edts.Child(elst); trak.Child(edts);

        // mdia
        var mdia = new Box("mdia");
        var mdhd = new Box("mdhd", 0, 0);
        mdhd.U32(0); mdhd.U32(0);
        mdhd.U32((uint)t.Timescale);
        mdhd.U32((uint)mediaDuration);
        mdhd.U16(0x55C4); // 'und'
        mdhd.U16(0);
        mdia.Child(mdhd);

        var hdlr = new Box("hdlr", 0, 0);
        hdlr.U32(0);
        hdlr.Fourcc(t.IsVideo ? "vide" : "soun");
        hdlr.U32(0); hdlr.U32(0); hdlr.U32(0);
        hdlr.Utf8Z(t.IsVideo ? "Video" : t.A!.Name);
        mdia.Child(hdlr);

        var minf = new Box("minf");
        if (t.IsVideo)
        {
            var vmhd = new Box("vmhd", 0, 1); vmhd.U16(0); vmhd.U16(0); vmhd.U16(0); vmhd.U16(0); minf.Child(vmhd);
        }
        else
        {
            var smhd = new Box("smhd", 0, 0); smhd.U16(0); smhd.U16(0); minf.Child(smhd);
        }
        var dinf = new Box("dinf");
        var dref = new Box("dref", 0, 0); dref.U32(1);
        var url = new Box("url ", 0, 1); dref.Child(url);
        dinf.Child(dref); minf.Child(dinf);

        minf.Child(BuildStbl(t, mdatPayloadStart, use64));
        mdia.Child(minf);
        trak.Child(mdia);
        return trak;
    }

    private Box BuildStbl(Track t, long mdatPayloadStart, bool use64)
    {
        var stbl = new Box("stbl");

        // stsd
        var stsd = new Box("stsd", 0, 0); stsd.U32(1);
        if (t.IsVideo)
        {
            var v = t.V!;
            var entry = new Box(v.SampleEntry);         // avc1 / hvc1 / av01 VisualSampleEntry
            entry.Zero(6); entry.U16(1);                // reserved, data_reference_index
            entry.U16(0); entry.U16(0); entry.Zero(12); // pre_defined/reserved
            entry.U16((ushort)v.Width); entry.U16((ushort)v.Height);
            entry.U32(0x00480000); entry.U32(0x00480000);
            entry.U32(0);
            entry.U16(1);                               // frame_count
            var name = new byte[32]; var nm = Encoding.ASCII.GetBytes("Clink"); name[0] = (byte)nm.Length; nm.CopyTo(name, 1); entry.Bytes(name);
            entry.U16(0x0018); entry.I16(-1);
            var cfg = new Box(v.ConfigBox); cfg.Bytes(v.CodecConfig); entry.Child(cfg);
            var colr = new Box("colr"); colr.Fourcc("nclx"); colr.U16(1); colr.U16(1); colr.U16(1); colr.U8(0); entry.Child(colr);
            var pasp = new Box("pasp"); pasp.U32(1); pasp.U32(1); entry.Child(pasp);
            stsd.Child(entry);
        }
        else
        {
            var a = t.A!;
            var entry = new Box("mp4a");
            entry.Zero(6); entry.U16(1);
            entry.U32(0); entry.U32(0);                 // reserved
            entry.U16((ushort)a.Channels); entry.U16(16);
            entry.U16(0); entry.U16(0);
            entry.U32((uint)a.SampleRate << 16);
            entry.Child(BuildEsds(a));
            stsd.Child(entry);
        }
        stbl.Child(stsd);

        // stts (run-length of deltas)
        int n = t.Sizes.Count;
        var stts = new Box("stts", 0, 0);
        var runs = new List<(long delta, uint count)>();
        if (n > 0)
        {
            long defaultDelta = t.IsVideo ? ToScale(FrameDuration100ns(t), t.Timescale) : t.A!.SamplesPerFrame;
            for (int i = 0; i < n; i++)
            {
                long delta = i + 1 < n ? t.Dts[i + 1] - t.Dts[i] : defaultDelta;
                if (delta <= 0) delta = 1;
                if (runs.Count > 0 && runs[^1].delta == delta) runs[^1] = (delta, runs[^1].count + 1);
                else runs.Add((delta, 1));
            }
        }
        stts.U32((uint)runs.Count);
        foreach (var (delta, count) in runs) { stts.U32(count); stts.U32((uint)delta); }
        stbl.Child(stts);

        if (t.IsVideo)
        {
            var stss = new Box("stss", 0, 0);
            stss.U32((uint)t.KeyIndices.Count);
            foreach (var k in t.KeyIndices) stss.U32((uint)(k + 1));
            stbl.Child(stss);

            bool needCtts = false;
            for (int i = 0; i < n; i++) if (t.Pts[i] != t.Dts[i]) { needCtts = true; break; }
            if (needCtts)
            {
                var ctts = new Box("ctts", 1, 0);
                var cruns = new List<(long off, uint count)>();
                for (int i = 0; i < n; i++)
                {
                    long off = t.Pts[i] - t.Dts[i];
                    if (cruns.Count > 0 && cruns[^1].off == off) cruns[^1] = (off, cruns[^1].count + 1);
                    else cruns.Add((off, 1));
                }
                ctts.U32((uint)cruns.Count);
                foreach (var (off, count) in cruns) { ctts.U32(count); ctts.I32((int)off); }
                stbl.Child(ctts);
            }
        }

        // stsc: one sample per chunk
        var stsc = new Box("stsc", 0, 0);
        stsc.U32(1); stsc.U32(1); stsc.U32(1); stsc.U32(1);
        stbl.Child(stsc);

        var stsz = new Box("stsz", 0, 0);
        stsz.U32(0); stsz.U32((uint)n);
        foreach (var s in t.Sizes) stsz.U32(s);
        stbl.Child(stsz);

        if (use64)
        {
            var co64 = new Box("co64", 0, 0);
            co64.U32((uint)n);
            foreach (var o in t.Offsets) co64.U64((ulong)(mdatPayloadStart + o));
            stbl.Child(co64);
        }
        else
        {
            var stco = new Box("stco", 0, 0);
            stco.U32((uint)n);
            foreach (var o in t.Offsets) stco.U32((uint)(mdatPayloadStart + o));
            stbl.Child(stco);
        }
        return stbl;
    }

    private static Box BuildEsds(AudioTrackDesc a)
    {
        var esds = new Box("esds", 0, 0);
        byte[] asc = a.AudioSpecificConfig;
        // DecoderSpecificInfo
        var dsi = new List<byte> { 0x05 }; dsi.AddRange(DescLen(asc.Length)); dsi.AddRange(asc);
        // DecoderConfigDescriptor
        var dcd = new List<byte> { 0x04 };
        var dcdBody = new List<byte> { 0x40, 0x15, 0, 0x06, 0x00 }; // objectType AAC, streamType audio, bufferSize 1536
        dcdBody.AddRange(BE32((uint)Math.Max(a.Bitrate, 128000)));
        dcdBody.AddRange(BE32((uint)a.Bitrate));
        dcdBody.AddRange(dsi);
        dcd.AddRange(DescLen(dcdBody.Count)); dcd.AddRange(dcdBody);
        // SLConfigDescriptor
        var sl = new byte[] { 0x06, 0x01, 0x02 };
        // ES_Descriptor
        var esBody = new List<byte> { 0, 1, 0 }; // ES_ID = 1, flags
        esBody.AddRange(dcd); esBody.AddRange(sl);
        var es = new List<byte> { 0x03 }; es.AddRange(DescLen(esBody.Count)); es.AddRange(esBody);
        esds.Bytes(es.ToArray());
        return esds;

        static IEnumerable<byte> DescLen(int len) => new[] { (byte)(0x80 | ((len >> 21) & 0x7F)), (byte)(0x80 | ((len >> 14) & 0x7F)), (byte)(0x80 | ((len >> 7) & 0x7F)), (byte)(len & 0x7F) };
        static IEnumerable<byte> BE32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static void WriteMatrix(Box b)
    {
        b.U32(0x00010000); b.U32(0); b.U32(0);
        b.U32(0); b.U32(0x00010000); b.U32(0);
        b.U32(0); b.U32(0); b.U32(0x40000000);
    }

    public void Dispose()
    {
        try { _temp.Dispose(); } catch { }
        try { File.Delete(_tempPath); } catch { }
    }

    /// <summary>Big-endian box builder.</summary>
    private sealed class Box
    {
        private readonly MemoryStream _ms = new();
        private readonly string _type;
        public Box(string type) { _type = type; }
        public Box(string type, byte version, uint flags) : this(type) { U8(version); U8((byte)(flags >> 16)); U8((byte)(flags >> 8)); U8((byte)flags); }
        public void U8(byte v) => _ms.WriteByte(v);
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _ms.Write(b); }
        public void I16(short v) => U16((ushort)v);
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _ms.Write(b); }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _ms.Write(b); }
        public void Fourcc(string s) => _ms.Write(Encoding.ASCII.GetBytes(s));
        public void Bytes(byte[] b) => _ms.Write(b);
        public void Zero(int n) { for (int i = 0; i < n; i++) _ms.WriteByte(0); }
        public void Utf8Z(string s) { _ms.Write(Encoding.UTF8.GetBytes(s)); _ms.WriteByte(0); }
        public void Child(Box b) => _ms.Write(b.ToArray());
        public byte[] ToArray()
        {
            var body = _ms.ToArray();
            var o = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(o, (uint)o.Length);
            Encoding.ASCII.GetBytes(_type, o.AsSpan(4, 4));
            body.CopyTo(o, 8);
            return o;
        }
    }
}
