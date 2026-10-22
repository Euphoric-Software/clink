using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Audio;
using Clink.Encode;
using Clink.Native;

namespace Clink.Engine;

/// <summary>One input of a rendered track: an audio track of the source clip and the volume it is summed in at.</summary>
/// <param name="Track">The source track's position among the file's audio tracks, in file order (0 = the first, normally the Mix).</param>
/// <param name="Gain">Linear volume of this input; negative values subtract it.</param>
internal readonly record struct MixInput(int Track, float Gain);

/// <summary>How one audio track of the source should end up in the trimmed file.</summary>
/// <param name="Include">False drops the track from the export.</param>
/// <param name="Gain">Linear volume (1 = untouched, copied without re-encoding). When <paramref name="Sum"/> is set it is the master
/// volume applied to the sum.</param>
/// <param name="Sum">When set, the track is not copied but rendered: the listed source tracks summed at their gains, then the master
/// <paramref name="Gain"/> and the soft clip (see <see cref="MixPlan"/>: a Mix rebuilt from its sources). An empty list renders silence.
/// A source that is not itself included still feeds the sum.</param>
internal readonly record struct TrackMix(bool Include, float Gain, MixInput[]? Sum = null);

/// <summary>An audio track as the file describes it (see <see cref="Trimmer.ReadAudioTracks"/>).</summary>
/// <param name="Name">The track's name in the file (Mix, Game, Mic…).</param>
/// <param name="StartOffset">Where the track's first sample sits on the clip's timeline, 100 ns: negative when the file starts the
/// track before the clip origin, positive when after it.</param>
internal readonly record struct AudioTrackInfo(string Name, long StartOffset)
{
    /// <summary><see cref="StartOffset"/> in 48 kHz frames: the muxer writes it to the sample.</summary>
    public long StartFrames => (long)Math.Round(StartOffset * (ClipAlignment.Rate / 10_000_000.0), MidpointRounding.AwayFromZero);
}

/// <summary>The audio tracks of a file Clink wrote (see <see cref="Trimmer.ReadClipTracks"/>).</summary>
/// <param name="Marked">The file says its edit lists are alignment-true (<see cref="Mp4Muxer.AlignedBrand"/>).</param>
/// <param name="Audio">The audio tracks in file order.</param>
internal sealed record ClipTracks(bool Marked, AudioTrackInfo[] Audio);

/// <summary>
/// Trims an MP4 made by Clink: copies compressed samples in [start, end] into a new file without re-encoding.
/// Audio tracks can be dropped or re-encoded with a gain (see <see cref="TrackMix"/>); the video is never re-encoded.
/// </summary>
internal static unsafe class Trimmer
{
    private const uint AnyStream = 0xFFFFFFFE;
    private const uint AllStreams = 0xFFFFFFFE;

    /// <summary>What a trim does with one audio track it keeps.</summary>
    private sealed class PlannedTrack
    {
        /// <summary>The source reader's stream number of the track.</summary>
        public uint Stream;
        public AudioTrackDesc Desc = null!;
        /// <summary>The volume (the master volume of a track that is a sum).</summary>
        public float Gain;
        /// <summary>Rendered again (a volume change, or a sum of tracks) instead of copied.</summary>
        public bool Reencode;
        /// <summary>The render is limited with the recording's soft knee (<see cref="AudioTrack.SoftKnee"/>): a Mix put together from its sources.</summary>
        public bool Knee;
        /// <summary>What a render is made of: the reader's stream, the volume and the track's offset in frames on the clip's timeline.</summary>
        public (uint stream, float gain, long offset)[] Inputs = Array.Empty<(uint, float, long)>();
        /// <summary>Where the track's own time 0 sits on the clip's timeline, 100 ns: what a copied track's frames are stamped later by.</summary>
        public long Shift;
    }

    public static string[] Trim(string src, string dst, double startSec, double endSec, string[]? audioNames)
        => Trim(src, dst, startSec, endSec, audioNames, null);

    /// <param name="audioNames">Names of the source's audio tracks in file order; null (or a count that does not match the file)
    /// reads them from the file.</param>
    /// <param name="mix">Per source audio track, in file order (the order <see cref="AudioInFileOrder"/> gives).</param>
    /// <returns>The names of the audio tracks written to <paramref name="dst"/>, in file order.</returns>
    /// <exception cref="InvalidOperationException">A track of <paramref name="mix"/> adds a source to the Mix or takes it out of it
    /// (<see cref="MixInput"/> 0 with others) although the source could not be lined up with the Mix (<see cref="ClipAlignment.Unlined"/>:
    /// a clip an older Clink trimmed). The tracks of a recording and of a trim by this Clink are always in line.</exception>
    public static string[] Trim(string src, string dst, double startSec, double endSec, string[]? audioNames, TrackMix[]? mix)
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
            var audioIdx = new List<uint>();
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
                else if (major == PInvoke.MFMediaType_Audio && sub == PInvoke.MFAudioFormat_AAC) audioIdx.Add(i);
                else reader->SetStreamSelection(i, false);
                t->Release();
            }
            if (videoIndex < 0) throw new InvalidOperationException("No H.264/HEVC video stream in file");

            // n is the track's position in the file, so the output keeps the source order (the mix stays track 1) and the
            // names and mix entries line up with the editor's rows.
            AudioInFileOrder(audioIdx, (uint)videoIndex);
            string[]? names = audioNames != null && audioNames.Length == audioIdx.Count ? audioNames : ReadAudioTrackNames(src);
            if (names != null && names.Length != audioIdx.Count) names = null;
            // Where each audio track's first sample sits on the clip's timeline, to the sample, and whether the file's edit lists can
            // be trusted (ClipAlignment; for an older clip with a Mix to correct this checks its sources against it, which takes a
            // moment unless the editor did it already). A file Clink did not write has no offsets: its tracks start together.
            var align = ClipAlignment.Read(src);
            bool placed = align.OffsetFrames.Length == audioIdx.Count;
            long OffsetOf(int track) => placed ? align.OffsetFrames[track] : 0;
            var audioStreams = new List<PlannedTrack>();
            for (int n = 0; n < audioIdx.Count; n++)
            {
                uint i = audioIdx[n];
                TrackMix m = mix != null && n < mix.Length ? mix[n] : new TrackMix(true, 1f);
                if (!m.Include)
                {
                    reader->SetStreamSelection(i, false); // dropped from the export
                    continue;
                }
                // A source that could not be lined up with the Mix is neither added to it nor taken out of it (the editor offers no
                // such slider; this is for a plan made by anything else).
                if (m.Sum != null && m.Sum.Any(s => s.Track == 0) && m.Sum.Any(s => Array.IndexOf(align.Unlined, s.Track) >= 0))
                    throw new InvalidOperationException(ClipAlignment.OlderClinkNote);
                IMFMediaType* t;
                reader->GetNativeMediaType(i, 0, &t).Check("GetNativeMediaType(audio)");
                try
                {
                    var a = (IMFAttributes*)t;
                    bool reencode = m.Sum != null || Math.Abs(m.Gain - 1f) >= 0.001f;
                    // A re-encoded track is rendered from its inputs: the source tracks of a Mix rebuilt from them (file order here,
                    // this reader's stream numbers there), or, for a plain volume change, from itself.
                    var inputs = !reencode ? Array.Empty<(uint, float, long)>()
                        : m.Sum != null ? m.Sum.Select(s => (StreamOf(audioIdx, s.Track), s.Gain, OffsetOf(s.Track))).ToArray()
                        : new[] { (i, 1f, OffsetOf(n)) };
                    var ud = Mf.GetBlob(a, PInvoke.MF_MT_USER_DATA);
                    byte[] asc = ud != null && ud.Length > 12 ? ud.AsSpan(12).ToArray() : new byte[] { 0x11, 0x90 };
                    var desc = new AudioTrackDesc
                    {
                        Name = names != null && !string.IsNullOrWhiteSpace(names[n]) ? names[n] : $"Audio {n + 1}",
                        AudioSpecificConfig = asc,
                        SampleRate = reencode ? 48000 : (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000),
                        Channels = reencode ? 2 : (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2),
                        Bitrate = reencode ? 24000 * 8 : (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000) * 8,
                    };
                    audioStreams.Add(new PlannedTrack
                    {
                        Stream = i, Desc = desc, Gain = m.Gain, Inputs = inputs, Reencode = reencode,
                        // A Mix put together from its sources is limited with the curve the recording's Mix was; one track at another volume keeps the plain clip.
                        Knee = m.Sum != null,
                        Shift = ClipAlignment.To100ns(OffsetOf(n)),
                    });
                    // Re-encoded tracks are decoded by their own reader later; the compressed samples are not needed here.
                    reader->SetStreamSelection(i, !reencode);
                }
                finally { t->Release(); }
            }

            // Read everything in range. Video: from the last keyframe at/before start; audio: frames overlapping [start,end].
            var video = new List<VideoSample>();
            var audio = audioStreams.Select(_ => new List<AacFrame>()).ToList();
            var ps = new ParameterSets();
            if (seqHeader != null) Nal.ToLengthPrefixed(seqHeader, hevc, ps);
            int ended = 0, selected = 1 + audioStreams.Count(s => !s.Reencode);
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
                    // An audio frame is stamped with where it sits on the clip's timeline: the stream's own time (Media Foundation
                    // starts every track at 0, whatever the edit list says) plus the track's offset, so the tracks keep their places
                    // against each other. The cut keeps what the frame-by-frame rule always kept, measured in the stream's own time
                    // from an offset of 0 up (a track that starts before the origin keeps its frames just as it did).
                    uint streamIdx = idx;   // a copy: idx has its address taken above, which a lambda may not capture
                    int track = streamIdx == (uint)videoIndex ? -1 : audioStreams.FindIndex(s => s.Stream == streamIdx);
                    long shift = track >= 0 ? audioStreams[track].Shift : 0;
                    long lead = Math.Max(0, shift);
                    if (ts + lead > end + 10_000_000) continue;
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
                        if (track < 0) continue;
                        // Keep everything up to `end`; the cut at the (keyframe-snapped) origin happens below once it is known,
                        // so audio starts together with the video instead of at the requested start.
                        if (ts + lead <= end) audio[track].Add(new AacFrame(ts + shift, data));
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
                var plan = audioStreams[i];
                if (!plan.Reencode)
                {
                    // The frames are the track's own, stamped where they sit; the head is cut by the same rule as ever (the frame's
                    // stream time plus a lead of at least 0 ends after the origin), so the same frames stay as in a cut that
                    // did not know the offsets; the muxer's edit list then puts them where the stamps say.
                    long slack = Math.Max(0, plan.Shift) - plan.Shift;
                    tracks.Add((plan.Desc, audio[i].Where(f => f.Pts + slack + frameDur > origin).ToList()));
                }
                else
                {
                    var frames = RenderTrack(src, plan.Inputs, plan.Gain, origin, end, plan.Knee, out byte[] asc);
                    plan.Desc.AudioSpecificConfig = asc;
                    tracks.Add((plan.Desc, frames));
                }
            }
            // The names the muxer really wrote (it leaves out a track with no frames in range), as for a clip save. The file says its
            // tracks are in their true places when the one it was cut from did, or when its sources were lined up with the Mix here.
            return ClipWriter.Write(dst, video, hevc, w, h, seqHeader, tracks, origin, 0, placed && align.Aligned);
        }
        finally { reader->Release(); }
    }

    /// <summary>
    /// Puts audio stream indices (ascending, as the source reader numbers them) into file order. Media Foundation's MPEG-4
    /// source lists the traks of a Clink file last-first: the video, which Clink always writes as trak 1, comes out with the
    /// highest index and the mix (the first audio trak) right before it. Index order would reverse every track.
    /// </summary>
    internal static void AudioInFileOrder(List<uint> audio, uint videoIndex)
    {
        if (audio.Count > 1 && videoIndex != uint.MaxValue && videoIndex > audio[^1]) audio.Reverse();
    }

    /// <summary>The source reader's stream numbers of the file's audio tracks, in file order (what <see cref="TrackMix"/> and <see cref="MixInput.Track"/> count by).</summary>
    internal static List<uint> AudioStreamsOf(string path)
    {
        Mf.EnsureStarted();
        IMFSourceReader* reader;
        PInvoke.MFCreateSourceReaderFromURL(path, null, &reader).Check("MFCreateSourceReaderFromURL");
        try
        {
            var audio = new List<uint>();
            uint video = uint.MaxValue;
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                if (reader->GetNativeMediaType(i, 0, &t).Failed) break;
                Guid major = Mf.GetGuid((IMFAttributes*)t, PInvoke.MF_MT_MAJOR_TYPE);
                t->Release();
                if (major == PInvoke.MFMediaType_Video && video == uint.MaxValue) video = i;
                else if (major == PInvoke.MFMediaType_Audio) audio.Add(i);
            }
            AudioInFileOrder(audio, video);
            return audio;
        }
        finally { reader->Release(); }
    }

    /// <summary>
    /// Names of the audio tracks as the file itself has them (the 'hdlr' names Clink writes: Mix, Game, Mic…), in file
    /// order; null when the file cannot be parsed or was not written by Clink. Only box headers are read, so a long clip's
    /// sample tables are skipped.
    /// </summary>
    internal static string[]? ReadAudioTrackNames(string path) => ReadAudioTracks(path)?.Select(t => t.Name).ToArray();

    /// <summary>
    /// The audio tracks as the file itself has them, in file order: the 'hdlr' names (see <see cref="ReadAudioTrackNames"/>) and
    /// where each track's first sample sits on the clip's timeline, from its edit list. Media Foundation hands every track's
    /// samples out from 0 and does not apply the edit list, but Clink's muxer starts a track before the clip origin (a negative
    /// offset: the first frames are skipped) or after it (a positive one), so tracks that are mixed together must be moved by
    /// these offsets to line up. Null when the file cannot be parsed or was not written by Clink.
    /// </summary>
    internal static AudioTrackInfo[]? ReadAudioTracks(string path) => ReadClipTracks(path)?.Audio;

    /// <summary>
    /// <see cref="ReadAudioTracks"/> with the file's mark: whether the brands of its 'ftyp' include <see cref="Mp4Muxer.AlignedBrand"/>,
    /// which a Clink that writes every track's place to the sample puts there (a recording, and a trim that kept the places). Null when
    /// the file cannot be parsed or was not written by Clink.
    /// </summary>
    internal static ClipTracks? ReadClipTracks(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            var top = Boxes(fs, 0, fs.Length);
            var moov = top.FirstOrDefault(b => b.type == "moov");
            if (moov.type == null) return null;
            bool marked = false;
            var ftyp = top.FirstOrDefault(b => b.type == "ftyp");
            if (ftyp.type != null && ftyp.end - ftyp.start >= 8 && ftyp.end - ftyp.start <= 1024)
            {
                // major brand, minor version, then the compatible brands, four bytes each.
                var brands = new byte[(int)(ftyp.end - ftyp.start)];
                fs.Position = ftyp.start;
                fs.ReadExactly(brands);
                string aligned = Mp4Muxer.AlignedBrand;
                for (int at = 8; at + 4 <= brands.Length && !marked; at += 4) marked = Encoding.ASCII.GetString(brands, at, 4) == aligned;
            }
            var moovBoxes = Boxes(fs, moov.start, moov.end);
            var mvhd = moovBoxes.FirstOrDefault(b => b.type == "mvhd");
            long movieScale = mvhd.type == null ? 1000 : ReadU32(fs, mvhd.start + (ReadU8(fs, mvhd.start) == 1 ? 20 : 12));
            var tracks = new List<AudioTrackInfo>();
            bool clink = false;
            foreach (var trak in moovBoxes.Where(b => b.type == "trak").ToList())
            {
                var parts = Boxes(fs, trak.start, trak.end);
                var mdia = parts.FirstOrDefault(b => b.type == "mdia");
                if (mdia.type == null) return null;
                var mdiaParts = Boxes(fs, mdia.start, mdia.end);
                var hdlr = mdiaParts.FirstOrDefault(b => b.type == "hdlr");
                if (hdlr.type == null || hdlr.end - hdlr.start < 24) return null;
                var buf = new byte[(int)Math.Min(hdlr.end - hdlr.start, 512)];
                fs.Position = hdlr.start;
                fs.ReadExactly(buf);
                // version/flags, pre_defined, handler type, 3 reserved words, then the zero-terminated UTF-8 name.
                string type = Encoding.ASCII.GetString(buf, 8, 4);
                int z = Array.IndexOf(buf, (byte)0, 24);
                string name = Encoding.UTF8.GetString(buf, 24, (z < 0 ? buf.Length : z) - 24).Trim();
                if (type == "vide") clink |= name == "Video";
                else if (type == "soun") tracks.Add(new AudioTrackInfo(name, StartOffset(fs, parts, mdiaParts, movieScale)));
            }
            // Only Clink's muxer names the video trak exactly "Video". Other writers use generic names (ffmpeg/OBS:
            // VideoHandler, SoundHandler on every track) or Pascal strings, so their files fall back to Audio 1, Audio 2…
            return clink ? new ClipTracks(marked, tracks.ToArray()) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Track names of {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Where the track's first sample sits on the movie's timeline, in 100 ns: an empty edit in front of the media delays it, and a
    /// media time on the first real edit is the part of the media before the clip origin, which is skipped. 0 without an edit list.
    /// </summary>
    private static long StartOffset(FileStream fs, List<(string type, long start, long end)> trak, List<(string type, long start, long end)> mdia, long movieScale)
    {
        var edts = trak.FirstOrDefault(b => b.type == "edts");
        var mdhd = mdia.FirstOrDefault(b => b.type == "mdhd");
        if (edts.type == null || mdhd.type == null) return 0;
        var elst = Boxes(fs, edts.start, edts.end).FirstOrDefault(b => b.type == "elst");
        if (elst.type == null) return 0;
        long mediaScale = ReadU32(fs, mdhd.start + (ReadU8(fs, mdhd.start) == 1 ? 20 : 12));
        if (mediaScale <= 0 || movieScale <= 0) return 0;
        bool v1 = ReadU8(fs, elst.start) == 1;
        long count = Math.Min(ReadU32(fs, elst.start + 4), 8);
        long offset = 0;
        for (int i = 0; i < count; i++)
        {
            long at = elst.start + 8 + i * (v1 ? 20 : 12);
            long duration, mediaTime;
            if (v1) { duration = ReadI64(fs, at); mediaTime = ReadI64(fs, at + 8); }
            else { duration = ReadU32(fs, at); mediaTime = (int)ReadU32(fs, at + 4); }
            if (mediaTime < 0) offset += duration * 10_000_000 / movieScale;   // an empty edit: the track starts later
            else return offset - mediaTime * 10_000_000 / mediaScale;          // the media from here on; what is before it is skipped
        }
        return offset;
    }

    private static int ReadU8(FileStream fs, long at)
    {
        fs.Position = at;
        return fs.ReadByte();
    }

    private static long ReadU32(FileStream fs, long at)
    {
        Span<byte> b = stackalloc byte[4];
        fs.Position = at;
        fs.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static long ReadI64(FileStream fs, long at)
    {
        Span<byte> b = stackalloc byte[8];
        fs.Position = at;
        fs.ReadExactly(b);
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }

    /// <summary>The boxes directly inside [from, to): type and payload range. Stops at the first malformed header.</summary>
    private static List<(string type, long start, long end)> Boxes(FileStream fs, long from, long to)
    {
        var list = new List<(string, long, long)>();
        Span<byte> h = stackalloc byte[16];
        for (long o = from; o + 8 <= to;)
        {
            fs.Position = o;
            fs.ReadExactly(h.Slice(0, 8));
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            int hdr = 8;
            if (size == 1)
            {
                if (o + 16 > to) break;
                fs.ReadExactly(h.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(h.Slice(8));
                hdr = 16;
            }
            else if (size == 0) size = to - o;
            if (size < hdr || size > to - o) break;
            list.Add((Encoding.ASCII.GetString(h.Slice(4, 4)), o + hdr, o + size));
            o += size;
        }
        return list;
    }

    /// <summary>The reader's stream number of the file's <paramref name="track"/>-th audio track (file order).</summary>
    private static uint StreamOf(List<uint> audioIdx, int track)
    {
        if (track < 0 || track >= audioIdx.Count)
            throw new ArgumentException($"The mix uses audio track {track + 1}, but the clip has {audioIdx.Count}");
        return audioIdx[track];
    }

    /// <summary>
    /// The pts of the <paramref name="k"/>-th AAC frame of a track rendered from <paramref name="origin"/>: its exact place, rounded to
    /// 100 ns once. A frame is 213,333.33 units long, so stamping k whole truncated frames would put the track a sample early for
    /// every 600 frames (13 s) and, read back, make a long render look as if it had holes in it.
    /// </summary>
    internal static long FramePts(long origin, long k) => origin + (long)Math.Round(k * (double)AacEncoder.FrameSamples * 10_000_000 / PcmTrackReader.Rate);

    /// <summary>
    /// Renders one output track from <paramref name="origin"/> to <paramref name="end"/> as AAC, a block at a time: each input is
    /// decoded to 48 kHz stereo float by its own <see cref="PcmTrackReader"/>, the blocks are summed at their gains, multiplied by
    /// <paramref name="master"/>, soft-clipped and encoded. Only the current block of each input is ever held, however long the
    /// clip, so a Mix rebuilt from three sources costs a few blocks of memory and not three decoded tracks. A single input at
    /// gain 1 with the volume as master is the plain volume change. Output frame k gets pts origin + round(k * 1024 / 48 kHz) so the
    /// track lines up with the passthrough ones however long it is (<see cref="FramePts"/>). The sum is limited with the recording's
    /// soft knee (<paramref name="knee"/>, a Mix put together from its sources) or, for one track at another volume, simply held to
    /// full scale. No inputs render silence for the length of the selection.
    /// </summary>
    private static List<AacFrame> RenderTrack(string src, (uint stream, float gain, long offset)[] inputs, float master, long origin, long end, bool knee, out byte[] asc)
    {
        const int Rate = PcmTrackReader.Rate, Block = AacEncoder.FrameSamples;
        var frames = new List<AacFrame>();
        var readers = new List<(PcmTrackReader reader, float gain)>();
        try
        {
            long originFrame = (long)Math.Round(origin * (Rate / 10_000_000.0));
            long endFrame = (long)Math.Round(end * (Rate / 10_000_000.0));
            // The same stream listed twice would only be decoded twice: one reader at the summed gain.
            foreach (var same in inputs.GroupBy(x => x.stream))
                readers.Add((new PcmTrackReader(src, same.Key, same.First().offset, originFrame, endFrame), same.Sum(x => x.gain)));
            using var enc = new AacEncoder(Rate, 2, 24000);
            asc = enc.AudioSpecificConfig;
            var acc = new float[Block * 2];
            var block = new float[Block * 2];
            var pcm = new byte[Block * 4];
            long inBlocks = 0, outIndex = 0;

            void Encode()
            {
                for (int i = 0; i < Block * 2; i++)
                {
                    float x = acc[i] * master;
                    x = knee ? AudioTrack.SoftKnee(x) : x / MathF.Max(1f, MathF.Abs(x));
                    int s = (int)MathF.Round(x * 32767f);
                    pcm[i * 2] = (byte)s; pcm[i * 2 + 1] = (byte)(s >> 8);
                }
                enc.Encode(pcm, FramePts(origin, inBlocks++), (_, data) =>
                {
                    long pts = FramePts(origin, outIndex++);
                    if (pts <= end) frames.Add(new AacFrame(pts, data));
                });
            }

            // One block past the end stays real audio, not silence: the encoder looks a frame ahead, so the last frame kept (it starts
            // at or before the end) would otherwise be coded against a cut.
            for (long at = originFrame; at < endFrame + Block; at += Block)
            {
                Array.Clear(acc);
                bool any = readers.Count == 0;   // nothing to sum is silence, not the end
                foreach (var (reader, gain) in readers)
                {
                    if (reader.ReadBlock(block) == 0) continue;   // this input ended before the block
                    any = true;
                    for (int i = 0; i < acc.Length; i++) acc[i] += gain * block[i];
                }
                if (!any) break;   // every input ended: the track is as long as its longest input
                Encode();
            }
            // Push the encoder's delay out so the tail is not lost.
            Array.Clear(acc);
            for (int i = 0; i < 3; i++) Encode();
            return frames;
        }
        finally { foreach (var (reader, _) in readers) reader.Dispose(); }
    }

    /// <summary>
    /// One audio stream of the clip as 48 kHz stereo float on the clip's timeline, handed out from frame <c>originFrame</c> in blocks of
    /// 1024 frames. It has a source reader of its own (any number of them can read one file at once, as the player does) and holds
    /// only the packet being handed out. The timeline is repaired as the trimmer always did: frames before the cut are dropped, a
    /// packet that starts before what was already delivered loses its first frames, a gap of more than 48 frames (1 ms) is filled
    /// with silence, a hole of ten seconds or more is closed instead of filled. A packet whose timestamp is within 48 frames of where
    /// the one before it ended, early or late, just follows it: that is jitter in the timestamps and not a hole or a repeat, and
    /// dropping or inserting frames for it would put a click into the sound. The first packet is placed to the frame, so a stream that
    /// starts a few samples after the origin starts that many samples in.
    /// The stream's own timestamps start at 0 whatever the file says; the track's offset (<see cref="ReadAudioTracks"/>) puts it
    /// on the clip's timeline, so readers of tracks that were recorded together line up to the sample.
    /// </summary>
    internal sealed class PcmTrackReader : IDisposable
    {
        public const int Rate = 48000, Block = AacEncoder.FrameSamples;
        /// <summary>Timestamps this many frames (1 ms) off where the packet before ended are jitter.</summary>
        private const int Jitter = 48;
        private IMFSourceReader* _reader;
        private readonly uint _stream;
        private readonly long _offset, _end;                // frames
        private long _next;                                 // the timeline frame (48 kHz) the next decoded frame lands on
        private float[] _pcm = new float[Block * 2];        // the packet being handed out, interleaved stereo
        private int _pcmPos, _pcmCount;                     // frames of it still to hand out
        private long _silence;                              // frames of silence to hand out before it
        private bool _eos, _running;                        // the stream ended; a packet has been taken (the first one is placed to the frame)

        /// <param name="stream">The source reader's stream number (not the file order).</param>
        /// <param name="offsetFrames">Where the stream's first sample sits on the clip's timeline, 48 kHz frames (<see cref="AudioTrackInfo.StartFrames"/>).</param>
        /// <param name="originFrame">The timeline frame the first block starts at.</param>
        /// <param name="endFrame">The end of what will be read, as a timeline frame; a packet that starts a second after it ends the stream.</param>
        public PcmTrackReader(string src, uint stream, long offsetFrames, long originFrame, long endFrame)
        {
            Mf.EnsureStarted();
            _stream = stream;
            _offset = offsetFrames;
            _end = endFrame;
            IMFSourceReader* reader;
            PInvoke.MFCreateSourceReaderFromURL(src, null, &reader).Check("MFCreateSourceReaderFromURL(audio)");
            _reader = reader;
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

                long seekFrames = originFrame - offsetFrames;   // the stream's own position of the clip origin; before its first sample, nothing to skip
                if (seekFrames > 0)
                {
                    PROPVARIANT pos = default;
                    pos.vt = VARENUM.VT_I8;
                    pos.hVal = (long)Math.Round(seekFrames * (10_000_000.0 / Rate));
                    Guid timeFormat = Guid.Empty;
                    reader->SetCurrentPosition(&timeFormat, &pos);
                }
                _next = originFrame;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Fills <paramref name="dst"/> (1024 interleaved stereo frames) with the next block of the timeline: the stream's audio,
        /// silence where it has a gap, zeros after its end. Returns how many frames at the start of the block came from the stream
        /// (1024, fewer in the block it ends in, 0 once it has ended).
        /// </summary>
        public int ReadBlock(Span<float> dst)
        {
            int got = 0;
            while (got < Block)
            {
                if (_silence > 0)
                {
                    int n = (int)Math.Min(_silence, Block - got);
                    dst.Slice(got * 2, n * 2).Clear();
                    _silence -= n;
                    got += n;
                }
                else if (_pcmCount > 0)
                {
                    int n = Math.Min(_pcmCount, Block - got);
                    _pcm.AsSpan(_pcmPos * 2, n * 2).CopyTo(dst.Slice(got * 2));
                    _pcmPos += n;
                    _pcmCount -= n;
                    got += n;
                }
                else if (_eos || !Fill()) break;
            }
            if (got < Block) dst.Slice(got * 2, (Block - got) * 2).Clear();
            return got;
        }

        /// <summary>Reads the next decoded packet and lines it up with the timeline; false at the end of the stream.</summary>
        private bool Fill()
        {
            while (true)
            {
                uint idx, flags; long ts; IMFSample* sample;
                _reader->ReadSample(_stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample(audio)");
                if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0)
                {
                    if (sample != null) sample->Release();
                    _eos = true;
                    return false;
                }
                if (sample == null) continue;
                try
                {
                    long sf = (long)Math.Round(ts * (Rate / 10_000_000.0)) + _offset;   // the packet's first frame on the clip's timeline
                    if (sf > _end + Rate) { _eos = true; return false; }
                    byte[] raw = Mf.ReadSampleBytes(sample);
                    int n = raw.Length / 8;
                    if (n == 0) continue;
                    long gap = sf - _next;
                    if (gap <= -n) continue;                 // entirely before the cut, or a repeat of what was handed out
                    int skip = 0;
                    if (_running && Math.Abs(gap) <= Jitter) { }   // follows the packet before it
                    else if (gap > 0)
                    {
                        if (gap < Rate * 10) _silence += gap;      // a hole of ten seconds or more is closed, not filled
                        _next = sf;
                    }
                    else if (gap < 0) skip = (int)-gap;
                    n -= skip;
                    if (_pcm.Length < n * 2) _pcm = new float[n * 2];
                    MemoryMarshal.Cast<byte, float>(raw.AsSpan(skip * 8, n * 8)).CopyTo(_pcm);
                    _pcmPos = 0;
                    _pcmCount = n;
                    _next += n;
                    _running = true;
                    return true;
                }
                finally { sample->Release(); }
            }
        }

        public void Dispose()
        {
            ComUtil.Release(ref _reader);
        }
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
