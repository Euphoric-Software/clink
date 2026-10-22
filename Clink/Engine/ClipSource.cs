using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
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
/// <param name="Passed">True: through <see cref="ClipSource"/> every track of the probe clip starts at 0 and has its frames where they are. False:
/// it does not, so tracks cannot be put on one timeline. Null: the check could not run (Media Foundation or its AAC encoder is not
/// there), which is not a reason to distrust anything.</param>
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
/// file another application wrote is opened as Media Foundation does it. The reader is for compressed samples and decoded audio alike.
/// </summary>
internal static unsafe class ClipSource
{
    private const uint AllStreams = 0xFFFFFFFE;
    private static bool s_fallbackLogged;

    /// <summary>Set by the tests only (see <see cref="IClipSourceHook"/>).</summary>
    internal static IClipSourceHook? Hook { get; set; }

    /// <summary>
    /// Opens a reader on the clip <paramref name="path"/>: its audio edit lists are hidden from Media Foundation when it is a file Clink
    /// wrote that has any, and every stream then starts at 0.
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
        if (hideEditLists && (hook?.HideEditLists(path) ?? true))
        {
            var edits = Trimmer.ReadClipTracks(path)?.AudioEditBoxes;
            if (edits is { Length: > 0 })
            {
                try { reader = OpenHidden(path, attrs, edits, out owner); }
                catch (Exception ex)
                {
                    // Never worse than before: the clip opens through Media Foundation's own reader. The timeline self-check reads through
                    // this method, so on a Windows where that reader moves the tracks it says so, and nothing is mixed.
                    owner = null;
                    reader = null;
                    if (!s_fallbackLogged)
                    {
                        s_fallbackLogged = true;
                        Log.Warn($"media: {Path.GetFileName(path)} could not be read with its audio edit lists hidden ({ex.Message}); Media Foundation's own reader is used");
                    }
                }
            }
        }
        bool hidden = reader != null;
        if (reader == null) PInvoke.MFCreateSourceReaderFromURL(path, attrs, &reader).Check("MFCreateSourceReaderFromURL");
        return hook != null ? hook.Wrap(path, reader, hidden, ref owner) : reader;
    }

    private static IMFSourceReader* OpenHidden(string path, IMFAttributes* attrs, long[] editBoxes, out IDisposable? owner)
    {
        var stream = new EditListFreeStream(path, editBoxes);
        try
        {
            // The stream is a COM object for as long as Media Foundation holds a reference to it (the byte stream, which the reader's
            // source holds): the identity pointer is released once the byte stream has taken its own.
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
                owner = stream;
                return reader;
            }
            finally { bytes->Release(); }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ the timeline self-check

    private const int Samples = 3;
    private static readonly object s_gate = new();
    private static TimelineCheck? s_check;

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
    /// Both tracks must hand out their frames from 0, one 1024-sample frame (213,333 units, 100 ns) apart. The same file is then read through
    /// Media Foundation's own reader and what it does with the edit lists is logged. It never throws.
    /// </summary>
    internal static TimelineCheck Timeline
    {
        get
        {
            lock (s_gate) return s_check ??= RunTimelineCheck();
        }
    }

    /// <summary>Forgets the check, so the next <see cref="Timeline"/> runs it again (for the tests).</summary>
    internal static void ResetTimelineCheck()
    {
        lock (s_gate) s_check = null;
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
        if (check.Passed == false) Log.Warn($"media: {how}; Clink reads its clips without them, but the timeline self-check FAILED ({check.Detail}): sources are kept out of All audio and no trim is marked as aligned [{windows}]");
        else if (check.Passed == null) Log.Warn($"media: the timeline self-check could not run ({check.Detail}); clips are taken as they are [{windows}]");
        else Log.Info($"media: {how}; Clink reads its clips without them [{windows}]");
        return check;
    }

    private static TimelineCheck CheckProbe()
    {
        string probe = Path.Combine(Path.GetTempPath(), $"clink-timeline-probe-{Environment.ProcessId}.mp4");
        try
        {
            Mf.EnsureStarted();
            DeleteProbe(probe);   // the muxer never overwrites
            TimelineProbe.Write(probe);
            var through = FirstTimestamps(probe, hideEditLists: true);
            bool passed = through.Length == 2 && Is(through[0], 0, 213_333, 426_667) && Is(through[1], 0, 213_333, 426_667);
            var handling = EditListHandling.Unknown;
            string own = "";
            try
            {
                var plain = FirstTimestamps(probe, hideEditLists: false);
                handling = Classify(plain);
                own = Show(plain);
            }
            catch (Exception ex) { Log.Warn($"media: the probe clip could not be read by Media Foundation itself: {ex.Message}"); }
            return new TimelineCheck(passed, handling, Show(through) + (passed ? "" : " (expected 0 213333 426667 for both)"), own);
        }
        catch (Exception ex)
        {
            return new TimelineCheck(null, EditListHandling.Unknown, ex.Message);
        }
        finally { DeleteProbe(probe); }
    }

    private static void DeleteProbe(string probe)
    {
        foreach (string f in new[] { probe, probe + ".tmp", probe + ".part" })
        {
            try { File.Delete(f); } catch { }
        }
    }

    /// <summary>The first few compressed timestamps (100 ns) of each audio stream of a clip, in file order, as <see cref="Open(string, IMFAttributes*, out IDisposable?, bool)"/> reads it.</summary>
    private static long[][] FirstTimestamps(string path, bool hideEditLists)
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
        var all = new long[streams.Count][];
        for (int n = 0; n < streams.Count; n++)
        {
            IMFSourceReader* reader = Open(path, null, out var owner, hideEditLists);
            try
            {
                reader->SetStreamSelection(AllStreams, false);
                reader->SetStreamSelection(streams[n], true).Check("SetStreamSelection");
                var times = new List<long>();
                while (times.Count < Samples)
                {
                    uint idx, flags; long ts; IMFSample* sample;
                    reader->ReadSample(streams[n], 0, &idx, &flags, &ts, &sample).Check("ReadSample");
                    if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0)
                    {
                        if (sample != null) sample->Release();
                        break;
                    }
                    if (sample == null) continue;
                    times.Add(ts);
                    sample->Release();
                }
                all[n] = times.ToArray();
            }
            finally
            {
                reader->Release();
                owner?.Dispose();
            }
        }
        return all;
    }

    /// <summary>"Mix 0 213333 426667 / Game 0 213333 426667".</summary>
    private static string Show(long[][] timestamps)
        => string.Join(" / ", timestamps.Select((t, i) => $"{(i == 0 ? "Mix" : "Game")} {string.Join(" ", t)}"));

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
/// audio tracks have no edit list. Its COM side is <see cref="ClinkComWrappers"/>; Media Foundation calls it from its own threads
/// through <c>MFCreateMFByteStreamOnStream</c>, so every call takes the lock. <see cref="Dispose"/> closes the file; a call after that fails.
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
    private readonly long[] _patch;
    private readonly long _length;
    private SafeFileHandle? _file;
    private long _pos;

    /// <param name="path">The clip.</param>
    /// <param name="typeOffsets">The file offsets of the four type bytes that read as 'free' (<see cref="ClipTracks.AudioEditBoxes"/>).</param>
    public EditListFreeStream(string path, long[] typeOffsets)
    {
        // As Media Foundation's own reader opens a file: others may read it, rename it or delete it, nobody may write to it, and the
        // cache is left to do its own read-ahead (a RandomAccess hint would switch it off, which a clip on a share would feel in every read).
        _file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.None);
        _patch = (long[])typeOffsets.Clone();
        try { _length = RandomAccess.GetLength(_file); }
        catch { _file.Dispose(); throw; }
    }

    /// <summary>The file's length.</summary>
    public long Length => _length;

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
