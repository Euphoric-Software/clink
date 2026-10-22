using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Engine;

/// <summary>
/// The test seam of <see cref="ClipSource"/>, null in the app. The tests' emulation of a Media Foundation that applies the audio edit
/// lists (Tests/EditListEmulation.cs) installs itself here to see every reader <see cref="ClipSource"/> hands out.
/// </summary>
internal unsafe interface IClipSourceHook
{
    /// <summary>False keeps <see cref="ClipSource"/> from hiding this clip's audio edit lists: the reader is Media Foundation's own.</summary>
    bool HideEditLists(string path);

    /// <summary>
    /// The reader to hand out in place of <paramref name="reader"/> (which was opened with its audio edit lists hidden, or not: <paramref name="editListsHidden"/>).
    /// A wrapper takes the reader over; <paramref name="owner"/> then has to release it, and the stream it was read through, when disposed.
    /// </summary>
    IMFSourceReader* Wrap(string path, IMFSourceReader* reader, bool editListsHidden, ref IDisposable? owner);
}

/// <summary>What Media Foundation's own reader does with the audio edit lists of a Clink clip (see <see cref="ClipSource.Timeline"/>).</summary>
internal enum EditListHandling
{
    /// <summary>Not looked at: the check could not run.</summary>
    Unknown,
    /// <summary>It hands every track out from its first sample at 0, as Windows 11 does.</summary>
    Ignores,
    /// <summary>It moves every track by its edit list, as Windows Server 2025 does.</summary>
    Applies,
    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>The result of the timeline self-check (<see cref="ClipSource.Timeline"/>).</summary>
/// <param name="Passed">True: through <see cref="ClipSource"/> every track of the probe clip starts at 0, has its frames where they are and
/// is found again by a seek. False: the read was made and the numbers are not those, so tracks cannot be put on one timeline. Null: the
/// check could not run (Media Foundation or its AAC encoder is not there, a read failed or ended early, it did not finish), which is not
/// a reason to distrust anything and is not remembered: the next ask makes the check again.</param>
/// <param name="MediaFoundation">What Media Foundation's own reader makes of the same file.</param>
/// <param name="Detail">The timestamps that were read through <see cref="ClipSource"/>, or why the check could not run.</param>
/// <param name="OwnReading">The timestamps Media Foundation's own reader handed out (empty when it was not asked).</param>
internal sealed record TimelineCheck(bool? Passed, EditListHandling MediaFoundation, string Detail, string OwnReading = "")
{
    /// <summary>Clips may be put on one timeline: the check passed, or could not tell.</summary>
    public bool Trusted => Passed != false;
}

/// <summary>
/// Opens a Media Foundation source reader on a clip. Clink's muxer gives every audio track an edit list (a track that starts before
/// the clip origin skips its first samples, one that starts after it begins with an empty edit), and Clink reads the offsets from the
/// file itself and puts each track on the clip's timeline by them. Whether Media Foundation also applies the edit lists, though,
/// depends on the Windows: Windows 11 ignores them, Windows Server 2025 applies them, so on the latter every track would be moved
/// by its offset twice. So, for a file Clink wrote, the reader reads the file through an <see cref="EditListFreeStream"/> in which every
/// audio trak's 'edts' box reads as a 'free' box: Media Foundation then has no audio edit list to apply or to ignore, and every stream
/// comes out from its first sample at 0 on every Windows. The video trak's edit list (one edit from media time 0) is left alone, and a
/// file another application wrote is opened as Media Foundation does it. A Clink clip that cannot be read that way is not read at all,
/// unless Media Foundation is known to ignore the edit lists on this Windows (its own reader then stands in, and that is logged). The
/// reader is for compressed samples and decoded audio alike.
/// </summary>
internal static unsafe class ClipSource
{
    private const uint AllStreams = 0xFFFFFFFE;

    /// <summary>Set by the tests only (see <see cref="IClipSourceHook"/>).</summary>
    internal static IClipSourceHook? Hook { get; set; }

    /// <summary>
    /// Opens a reader on the clip <paramref name="path"/>: its audio edit lists are hidden from Media Foundation when it is a file Clink
    /// wrote that has any, and every stream then starts at 0. A clip that cannot be read that way is not read at all, unless Media
    /// Foundation is known to ignore the edit lists on this Windows (see <see cref="OpenHidden"/>).
    /// </summary>
    /// <param name="attrs">The reader's attributes, or null.</param>
    /// <param name="owner">The stream the reader reads the file through, null for a plain reader. <b>Dispose it after releasing the reader</b>:
    /// that closes the file (the player's close, a trim that replaces its clip and the archive's delete all need that).</param>
    public static IMFSourceReader* Open(string path, IMFAttributes* attrs, out IDisposable? owner)
        => Open(path, attrs, out owner, hideEditLists: true);

    /// <param name="hideEditLists">False opens Media Foundation's own reader on the file, as it is (what the self-check compares with).</param>
    internal static IMFSourceReader* Open(string path, IMFAttributes* attrs, out IDisposable? owner, bool hideEditLists)
    {
        Mf.EnsureStarted();
        var hook = Hook;
        owner = null;
        IMFSourceReader* reader = null;
        if (hideEditLists && (hook?.HideEditLists(path) ?? true)) reader = OpenHidden(path, attrs, out owner);
        bool hidden = reader != null;
        if (reader == null) PInvoke.MFCreateSourceReaderFromURL(path, attrs, &reader).Check("MFCreateSourceReaderFromURL");
        return hook != null ? hook.Wrap(path, reader, hidden, ref owner) : reader;
    }

    /// <summary>
    /// The reader on <paramref name="path"/> with the audio edit lists hidden, or null for Media Foundation's own reader where that is as
    /// good: the file has no audio edit list to hide (it is not one Clink wrote, or there is none in it), it is not there (Media Foundation
    /// says so in its own words), or it could not be read with them hidden and Media Foundation ignores the edit lists on this Windows,
    /// so its own reader hands the tracks out from 0 as well (that is logged, every time). Anywhere else a clip that cannot be read with
    /// its edit lists hidden throws: Media Foundation's own reader would put every track where its edit list says, Clink would put it
    /// there a second time and the clip would look right, being marked as aligned.
    /// </summary>
    private static IMFSourceReader* OpenHidden(string path, IMFAttributes* attrs, out IDisposable? owner)
    {
        owner = null;
        EditListFreeStream? stream = null;
        try
        {
            stream = new EditListFreeStream(path);   // the one open of the file: what is parsed here is what is served
            var edits = stream.ReadTracks()?.AudioEditBoxes;
            if (edits is not { Length: > 0 })
            {
                stream.Dispose();
                return null;
            }
            stream.Hide(edits);
            var reader = ReaderOn(stream, attrs);
            owner = stream;
            return reader;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            stream?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            owner = null;
            string name = Path.GetFileName(path);
            if (MediaFoundationIgnoresEditLists())
            {
                Log.Warn($"media: {name} could not be read with its audio edit lists hidden ({ex.Message}); Media Foundation's own reader is used, which ignores them on this Windows");
                return null;
            }
            Log.Warn($"media: {name} could not be read with its audio edit lists hidden ({ex.Message}), and Media Foundation's own reader is not known to ignore them on this Windows: the clip is not read");
            throw;
        }
    }

    /// <summary>A source reader on the stream: it is a COM object for as long as Media Foundation holds a reference to it.</summary>
    private static IMFSourceReader* ReaderOn(EditListFreeStream stream, IMFAttributes* attrs)
    {
        // The identity pointer is released once the byte stream (which the reader's source holds) has taken its own reference.
        nint identity = ClinkComWrappers.Instance.GetOrCreateComInterfaceForObject(stream, CreateComInterfaceFlags.None);
        Guid iid = IStream.IID_Guid;
        int hr = Marshal.QueryInterface(identity, in iid, out nint comStream);
        Marshal.Release(identity);
        Marshal.ThrowExceptionForHR(hr);
        IMFByteStream* bytes;
        try { PInvoke.MFCreateMFByteStreamOnStream((IStream*)comStream, &bytes).Check("MFCreateMFByteStreamOnStream"); }
        finally { Marshal.Release(comStream); }
        try
        {
            // Without a file name to go by, Media Foundation is told what the bytes are.
            Guid iidAttributes = IMFAttributes.IID_Guid;
            IMFAttributes* byteAttributes;
            if (((IUnknown*)bytes)->QueryInterface(&iidAttributes, (void**)&byteAttributes).Succeeded)
            {
                Guid contentType = PInvoke.MF_BYTESTREAM_CONTENT_TYPE;
                fixed (char* mp4 = "video/mp4") byteAttributes->SetString(&contentType, mp4);
                byteAttributes->Release();
            }
            IMFSourceReader* reader;
            PInvoke.MFCreateSourceReaderFromByteStream(bytes, attrs, &reader).Check("MFCreateSourceReaderFromByteStream");
            return reader;
        }
        finally { bytes->Release(); }
    }

    // ------------------------------------------------------------------ the timeline self-check

    private const int Samples = 3;
    /// <summary>Where the check seeks to, 100 ns: inside the second frame of a track (213,333 to 426,667 units), so that a seek that is moved by the Mix's 10 ms early start or by the Game's 25 ms late one lands in another frame.</summary>
    private const long SeekTo = 380_000;
    private const long SeekFinds = 213_333;
    private static readonly object s_gate = new();
    private static TimelineCheck? s_check;
    private static int s_handling = -1;   // what the check found Media Foundation's own reader to do (an EditListHandling), -1 while it is not known
    private static int s_probes;          // numbers the probe files, so that a check that overran and one that follows it never meet on one
    private static string? s_lastCouldNotRun;   // why the last check could not run (under s_gate), so that the log says it once
    [ThreadStatic] private static bool t_checking;   // this thread is the check's own

    /// <summary>Test seam: true or false stands in for the self-check's verdict, null lets the check decide.</summary>
    internal static bool? TrustOverride { get; set; }

    /// <summary>
    /// Whether clips may be put on one timeline here: the self-check passed (or could not run). When it did not, every source is left
    /// out of All audio (<see cref="ClipAlignment.Untrusted"/>), a plan that rebuilds or corrects the Mix is refused and no trim is
    /// marked as aligned. The check runs the first time this is asked, which is off the UI thread (<see cref="ClipAlignment.Read"/>).
    /// </summary>
    internal static bool TimelineTrusted => TrustOverride ?? Timeline.Trusted;

    /// <summary>
    /// The timeline self-check, once per process: a small probe clip whose two audio tracks start 10 ms before the clip origin (one
    /// edit with a media time) and 25 ms after it (an empty edit), the two forms the muxer writes, is read through <see cref="Open(string, IMFAttributes*, out IDisposable?)"/>.
    /// Both tracks must hand out their frames from 0, one 1024-sample frame (213,333 units, 100 ns) apart, and a seek into the second frame
    /// must find that frame. The same file is also read through Media Foundation's own reader and what it does with the edit lists is
    /// logged. It never throws. Only a read that was made has a verdict worth remembering: a check that could not run (or did not
    /// finish, or whose read ended early) is made again the next time it is asked for.
    /// </summary>
    internal static TimelineCheck Timeline
    {
        get
        {
            lock (s_gate)
            {
                if (s_check != null) return s_check;
                var check = RunTimelineCheck();
                if (check.Passed != null) s_check = check;
                return check;
            }
        }
    }

    /// <summary>Forgets the check (and what it found Media Foundation to do), so the next <see cref="Timeline"/> runs it again (for the tests).</summary>
    internal static void ResetTimelineCheck()
    {
        lock (s_gate)
        {
            s_check = null;
            Volatile.Write(ref s_handling, -1);
        }
    }

    /// <summary>
    /// Media Foundation's own reader hands the tracks of a Clink clip out from 0 on this Windows (the check found it so, or finds it so
    /// now): a clip that cannot be read with its edit lists hidden reads the same without. The check's own thread cannot ask the check.
    /// </summary>
    private static bool MediaFoundationIgnoresEditLists()
    {
        int known = Volatile.Read(ref s_handling);
        if (known < 0 && !t_checking) known = (int)Timeline.MediaFoundation;
        return known == (int)EditListHandling.Ignores;
    }

    private static TimelineCheck RunTimelineCheck()
    {
        // Media Foundation wants an MTA thread, and the caller may be on any.
        TimelineCheck? result = null;
        var worker = new Thread(() => result = CheckProbe()) { IsBackground = true, Name = "Clink.TimelineCheck" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        // It takes a fraction of a second; the wait is bounded so that whoever asks (the player opening, a trim) can never hang on it.
        bool finished = worker.Join(TimeSpan.FromSeconds(30));
        var check = finished ? result ?? new TimelineCheck(null, EditListHandling.Unknown, "the check gave no result") : new TimelineCheck(null, EditListHandling.Unknown, "the check did not finish in 30 s");
        string windows = WindowsBuild();
        string how = check.MediaFoundation switch
        {
            EditListHandling.Applies => "Media Foundation applies MP4 audio edit lists on this PC",
            EditListHandling.Ignores => "Media Foundation ignores MP4 audio edit lists on this PC",
            EditListHandling.Other => $"Media Foundation reads the probe clip in a way Clink does not know ({check.OwnReading})",
            _ => "Media Foundation's treatment of MP4 audio edit lists is not known",
        };
        if (check.Passed == null)
        {
            // Made again at every ask while it cannot run: said once for as long as it is the same thing that stops it.
            if (check.Detail != s_lastCouldNotRun)
                Log.Warn($"media: the timeline self-check could not run ({check.Detail}); clips are taken as they are, and it is made again when it is next asked for [{windows}]");
            s_lastCouldNotRun = check.Detail;
            return check;
        }
        s_lastCouldNotRun = null;
        if (check.Passed == false) Log.Warn($"media: {how}; Clink reads its clips without them, but the timeline self-check FAILED ({check.Detail}): sources are kept out of All audio and no trim is marked as aligned [{windows}]");
        else Log.Info($"media: {how}; Clink reads its clips without them [{windows}]");
        return check;
    }

    private static TimelineCheck CheckProbe()
    {
        t_checking = true;
        string probe = Path.Combine(Path.GetTempPath(), $"clink-timeline-probe-{Environment.ProcessId}-{Interlocked.Increment(ref s_probes)}.mp4");
        var handling = EditListHandling.Unknown;
        string own = "";
        try
        {
            Mf.EnsureStarted();
            DeleteProbe(probe);   // the muxer never overwrites
            TimelineProbe.Write(probe);
            // What Media Foundation does by itself comes first: Open asks for it when a clip cannot be read with its edit lists hidden.
            try
            {
                var plain = ReadProbe(probe, hideEditLists: false, seek: false).First;
                handling = Classify(plain);
                own = Show(plain);
                if (handling != EditListHandling.Unknown) Volatile.Write(ref s_handling, (int)handling);
            }
            catch (Exception ex) { Log.Warn($"media: the probe clip could not be read by Media Foundation itself: {ex.Message}"); }
            // Through ClipSource. A read that fails or ends early throws: that is a check that could not run. Only a read that was made
            // and handed out other numbers fails the check.
            var through = ReadProbe(probe, hideEditLists: true, seek: true);
            bool passed = through.First.Length == 2 && Is(through.First[0], 0, 213_333, 426_667) && Is(through.First[1], 0, 213_333, 426_667)
                && Math.Abs(through.Seeked[0] - SeekFinds) <= 1 && Math.Abs(through.Seeked[1] - SeekFinds) <= 1;
            string detail = $"{Show(through.First)}; after a seek to {SeekTo}: {Show(through.Seeked)}"
                + (passed ? "" : $" (expected 0 213333 426667 for both, and {SeekFinds} after the seek)");
            return new TimelineCheck(passed, handling, detail, own);
        }
        catch (Exception ex)
        {
            return new TimelineCheck(null, handling, ex.Message, own);
        }
        finally
        {
            t_checking = false;
            DeleteProbe(probe);
        }
    }

    private static void DeleteProbe(string probe)
    {
        foreach (string f in new[] { probe, probe + ".tmp", probe + ".part" })
        {
            try { File.Delete(f); } catch { }
        }
    }

    /// <param name="First">The first few compressed timestamps (100 ns) of each audio stream, in file order.</param>
    /// <param name="Seeked">The timestamp of the first frame of each audio stream after a seek to <see cref="SeekTo"/> (when it was asked for).</param>
    private readonly record struct ProbeReading(long[][] First, long[] Seeked);

    /// <summary>
    /// The probe clip as <see cref="Open(string, IMFAttributes*, out IDisposable?, bool)"/> reads it. Throws when a read fails or the
    /// stream ends before what was asked for.
    /// </summary>
    private static ProbeReading ReadProbe(string path, bool hideEditLists, bool seek)
    {
        List<uint> streams;
        IMFSourceReader* lister = Open(path, null, out var listerOwner, hideEditLists);
        try { streams = Trimmer.AudioStreamsOf(lister); }
        finally
        {
            lister->Release();
            listerOwner?.Dispose();
        }
        // A reader of its own for each stream, as every reader of Clink has (a reader does not hand out a second stream after the first was deselected).
        var first = new long[streams.Count][];
        var seeked = new long[seek ? streams.Count : 0];
        for (int n = 0; n < streams.Count; n++)
        {
            IMFSourceReader* reader = Open(path, null, out var owner, hideEditLists);
            try
            {
                reader->SetStreamSelection(AllStreams, false);
                reader->SetStreamSelection(streams[n], true).Check("SetStreamSelection");
                first[n] = new long[Samples];
                for (int k = 0; k < Samples; k++) first[n][k] = NextTimestamp(reader, streams[n]);
                if (seek)
                {
                    PROPVARIANT position = default;
                    position.vt = VARENUM.VT_I8;
                    position.hVal = SeekTo;
                    Guid timeFormat = Guid.Empty;
                    reader->SetCurrentPosition(&timeFormat, &position).Check("SetCurrentPosition");
                    seeked[n] = NextTimestamp(reader, streams[n]);
                }
            }
            finally
            {
                reader->Release();
                owner?.Dispose();
            }
        }
        return new ProbeReading(first, seeked);
    }

    /// <summary>The timestamp of the next sample of <paramref name="stream"/>.</summary>
    private static long NextTimestamp(IMFSourceReader* reader, uint stream)
    {
        for (int empty = 0; empty < 100; empty++)
        {
            uint idx, flags; long ts; IMFSample* sample;
            reader->ReadSample(stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample");
            if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0)
            {
                if (sample != null) sample->Release();
                throw new InvalidOperationException("the probe clip ended early or could not be read");
            }
            if (sample == null) continue;
            sample->Release();
            return ts;
        }
        throw new InvalidOperationException("the probe clip handed out no sample");
    }

    /// <summary>"Mix 0 213333 426667 / Game 0 213333 426667".</summary>
    private static string Show(long[][] timestamps)
        => string.Join(" / ", timestamps.Select((t, i) => $"{(i == 0 ? "Mix" : "Game")} {string.Join(" ", t)}"));

    /// <summary>"Mix 213333 / Game 213333".</summary>
    private static string Show(long[] timestamps)
        => string.Join(" / ", timestamps.Select((t, i) => $"{(i == 0 ? "Mix" : "Game")} {t}"));

    /// <summary>The first timestamps are these, give or take one unit (a frame is 213,333.33 units long).</summary>
    private static bool Is(long[] got, params long[] want)
    {
        if (got.Length < want.Length) return false;
        for (int i = 0; i < want.Length; i++)
            if (Math.Abs(got[i] - want[i]) > 1) return false;
        return true;
    }

    /// <summary>What Media Foundation's own reader did with the probe clip: the Mix starts 10 ms early and the Game 25 ms late.</summary>
    private static EditListHandling Classify(long[][] own)
    {
        if (own.Length != 2) return EditListHandling.Other;
        if (Is(own[0], 0, 213_333, 426_667) && Is(own[1], 0, 213_333, 426_667)) return EditListHandling.Ignores;
        if (Is(own[0], 0, 113_333, 326_667) && Is(own[1], 250_000, 463_333, 676_667)) return EditListHandling.Applies;
        return EditListHandling.Other;
    }

    /// <summary>"Windows 10.0.26100.33438 24H2", for the log.</summary>
    internal static string WindowsBuild()
    {
        var v = Environment.OSVersion.Version;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            object? ubr = key?.GetValue("UBR");
            string? display = key?.GetValue("DisplayVersion") as string;
            return $"Windows {v.Major}.{v.Minor}.{v.Build}{(ubr != null ? "." + ubr : "")}{(string.IsNullOrEmpty(display) ? "" : " " + display)}";
        }
        catch { return $"Windows {v.Major}.{v.Minor}.{v.Build}"; }
    }
}

/// <summary>
/// A clip file as an IStream (read only) in which the type of every audio trak's 'edts' box reads as 'free': an MPEG-4 file whose
/// audio tracks have no edit list. The file is opened once, here; <see cref="ReadTracks"/> parses it through the same handle that
/// serves it, so the offsets to hide (<see cref="Hide"/>) are those of the very bytes read later, whatever happens to the name
/// meanwhile. Its COM side is <see cref="ClinkComWrappers"/>; Media Foundation calls it from its own threads through
/// <c>MFCreateMFByteStreamOnStream</c>, so every call takes the lock. <see cref="Dispose"/> closes the file; a call after that fails.
/// </summary>
internal sealed unsafe class EditListFreeStream : IStream.Interface, IDisposable
{
    private static readonly byte[] Free = "free"u8.ToArray();
    private static readonly HRESULT E_NOTIMPL = (HRESULT)unchecked((int)0x80004001);
    private static readonly HRESULT STG_E_INVALIDFUNCTION = (HRESULT)unchecked((int)0x80030001);
    private static readonly HRESULT STG_E_ACCESSDENIED = (HRESULT)unchecked((int)0x80030005);
    private static readonly HRESULT STG_E_INVALIDPOINTER = (HRESULT)unchecked((int)0x80030009);
    private static readonly HRESULT COR_E_OBJECTDISPOSED = (HRESULT)unchecked((int)0x80131622);

    private readonly object _gate = new();
    private readonly string _name;
    private readonly long _length;
    private long[] _patch = Array.Empty<long>();
    private SafeFileHandle? _file;
    private long _pos;

    /// <summary>Opens the clip, with nothing hidden yet.</summary>
    public EditListFreeStream(string path)
    {
        _name = Path.GetFileName(path);
        // As Media Foundation's own reader opens a file: others may read it, rename it or delete it, nobody may write to it, and the
        // cache is left to do its own read-ahead (a RandomAccess hint would switch it off, which a clip on a share would feel in every read).
        _file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.None);
        try { _length = RandomAccess.GetLength(_file); }
        catch { _file.Dispose(); throw; }
    }

    /// <param name="typeOffsets">The file offsets of the four type bytes that read as 'free' (<see cref="ClipTracks.AudioEditBoxes"/>).</param>
    public EditListFreeStream(string path, long[] typeOffsets) : this(path) => Hide(typeOffsets);

    /// <summary>The file's length.</summary>
    public long Length => _length;

    /// <summary>
    /// The audio tracks of the clip this stream serves, parsed from its own handle (<see cref="Trimmer.ReadClipTracks(SafeFileHandle, string)"/>;
    /// reads at their own offsets, so the position of the stream is not touched). Null: not a file Clink wrote, or not one that can be parsed.
    /// </summary>
    public ClipTracks? ReadTracks()
    {
        lock (_gate)
            return _file == null ? null : Trimmer.ReadClipTracks(_file, _name);
    }

    /// <summary>From now on the four bytes at each of these file offsets read as 'free'.</summary>
    public void Hide(long[] typeOffsets)
    {
        lock (_gate) _patch = (long[])typeOffsets.Clone();
    }

    public HRESULT Read(void* pv, uint cb, uint* pcbRead)
    {
        lock (_gate)
        {
            var file = _file;
            if (file == null) return COR_E_OBJECTDISPOSED;
            var buffer = new Span<byte>(pv, (int)Math.Min(cb, (uint)int.MaxValue));
            int total = 0;
            while (total < buffer.Length)
            {
                int got = RandomAccess.Read(file, buffer.Slice(total), _pos + total);
                if (got <= 0) break;
                total += got;
            }
            HideEditBoxes(buffer.Slice(0, total), _pos);
            _pos += total;
            if (pcbRead != null) *pcbRead = (uint)total;
            return default;   // S_OK, also for a read that came up short at the end (as a memory stream does)
        }
    }

    /// <summary><paramref name="bytes"/> are the file's bytes from <paramref name="at"/> on: every type of an edit box that lies in them (wholly or in part) becomes 'free'.</summary>
    private void HideEditBoxes(Span<byte> bytes, long at)
    {
        foreach (long type in _patch)
        {
            long from = Math.Max(type, at), to = Math.Min(type + Free.Length, at + bytes.Length);
            for (long x = from; x < to; x++) bytes[(int)(x - at)] = Free[(int)(x - type)];
        }
    }

    public HRESULT Write(void* pv, uint cb, uint* pcbWritten)
    {
        if (pcbWritten != null) *pcbWritten = 0;
        return STG_E_ACCESSDENIED;
    }

    public HRESULT Seek(long dlibMove, SeekOrigin dwOrigin, ulong* plibNewPosition)
    {
        lock (_gate)
        {
            if (_file == null) return COR_E_OBJECTDISPOSED;
            long from = dwOrigin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _pos, SeekOrigin.End => _length, _ => -1 };
            if (from < 0) return STG_E_INVALIDFUNCTION;
            long to = from + dlibMove;
            if (to < 0) return STG_E_INVALIDFUNCTION;   // before the start
            _pos = to;
            if (plibNewPosition != null) *plibNewPosition = (ulong)to;
            return default;
        }
    }

    public HRESULT Stat(STATSTG* pstatstg, uint grfStatFlag)
    {
        if (pstatstg == null) return STG_E_INVALIDPOINTER;
        *pstatstg = default;
        pstatstg->type = (uint)STGTY.STGTY_STREAM;
        pstatstg->cbSize = (ulong)_length;
        pstatstg->grfMode = STGM.STGM_READ;
        return default;
    }

    public HRESULT SetSize(ulong libNewSize) => E_NOTIMPL;
    public HRESULT CopyTo(IStream* pstm, ulong cb, ulong* pcbRead, ulong* pcbWritten) => E_NOTIMPL;
    public HRESULT Commit(uint grfCommitFlags) => default;
    public HRESULT Revert() => default;
    public HRESULT LockRegion(ulong libOffset, ulong cb, uint dwLockType) => STG_E_INVALIDFUNCTION;
    public HRESULT UnlockRegion(ulong libOffset, ulong cb, uint dwLockType) => STG_E_INVALIDFUNCTION;
    public HRESULT Clone(IStream** ppstm) => E_NOTIMPL;

    /// <summary>Closes the file. Call it after the reader that reads through this stream has been released.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
