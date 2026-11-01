using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Audio;
using Clink.Encode;
using Clink.Native;

namespace Clink.Engine;

/// <summary>What a smaller copy is made at. The caller (the share-fit planner) has already kept the aspect, made the size even and not
/// larger than the source, and picked the bitrate. H.264 only for now.</summary>
/// <param name="Fps">Output frames per second; frames are picked from the source by timestamp (a 59.94, 120 or 144 fps source to 30 or 60).
/// A value above the source's rate keeps every frame and the output says the source's rate.</param>
/// <param name="VideoBitrateBps">CBR video bitrate (the Mix track is copied as it is, on top).</param>
internal sealed record ShareTranscodeRequest(int Width, int Height, int Fps, long VideoBitrateBps, float GopSeconds = 2f)
{
    /// <summary>The adapter to decode and encode on (the capture adapter, so the recording's encoder and this one share a GPU); null or not
    /// found = the first hardware adapter.</summary>
    public LUID? Adapter { get; init; }
}

/// <param name="Duration">The video's length: first frame to the end of the last one.</param>
/// <param name="AudioTracks">The copy's audio track names: the Mix, or none for a clip without audio (the copy is then video only).</param>
/// <param name="Encoder">The hardware encoder's name; empty for a stream copy (<see cref="ShareTranscoder.RemuxMixOnlyAsync"/>).</param>
internal sealed record ShareTranscodeResult(string Path, long SizeBytes, TimeSpan Duration, int VideoFrames, int Width, int Height, int Fps,
    string[] AudioTracks, string Encoder)
{
    /// <summary>Re-encode only: frames the hardware encoder did not take (ProcessInput failed), a few of which a copy survives (each is a
    /// gap in time, not damage); 0 for a stream copy.</summary>
    public int DroppedInputFrames { get; init; }

    /// <summary>Re-encode only (0 for a stream copy): the rate of the frames that went into the encoder, measured from their timestamps
    /// ((submitted - 1) / (last - first)). It differs from <see cref="Fps"/> when the source's header understates or overstates its
    /// real rate (a 30 fps header on 60 fps content keeps every frame, so the bitrate was planned for half the frames per second).</summary>
    public double MeasuredFps { get; init; }
}

/// <summary>Why a smaller copy could not be made (<see cref="ShareTranscodeException"/>).</summary>
internal enum ShareTranscodeFailure
{
    /// <summary>The file is not a video this can read or copy (not an MP4 Media Foundation opens, no video, AV1 for a stream copy, audio that is not AAC).</summary>
    Unsupported,
    /// <summary>Windows has no decoder for the clip's video (HEVC / AV1 without the Store extension: MF_E_TOPO_CODEC_NOT_FOUND).</summary>
    DecoderMissing,
    /// <summary>No hardware H.264 encoder could be opened. There is no software fallback.</summary>
    NoHardwareEncoder,
    /// <summary>The output's disk is full.</summary>
    DiskFull,
    /// <summary>The GPU was reset or went away while the copy was made.</summary>
    GpuLost,
    /// <summary>Anything else (the message says what).</summary>
    Failed,
}

/// <summary>A smaller copy could not be made. <see cref="Exception.Message"/> is a short reason the UI can show after
/// "Couldn't make a smaller copy: "; the inner exception has the details (and is logged).</summary>
internal sealed class ShareTranscodeException : Exception
{
    public ShareTranscodeFailure Kind { get; }

    public ShareTranscodeException(ShareTranscodeFailure kind, string reason, Exception? inner = null) : base(reason, inner) { Kind = kind; }
}

/// <summary>
/// Makes the smaller copy of a clip that sharing needs (Discord's and Copium's size limits), without touching the original:
/// <list type="bullet">
/// <item><see cref="TranscodeAsync"/>: the video decoded on the GPU (a source reader with a D3D manager on a dedicated low-priority
/// device, hardware transforms and advanced video processing, NV12 out at the target size in BT.709 limited range), each kept frame copied
/// into the hardware encoder's own input texture and encoded as H.264 CBR with no B-frames and a 2 s GOP, then muxed by
/// <see cref="ClipMuxWriter"/> (fast start, the clk1 mark and the track name kept) with the Mix track copied as it is.</item>
/// <item><see cref="RemuxMixOnlyAsync"/>: the video copied as it is with the Mix track only (what is enough when only the other audio
/// tracks make the clip too big).</item>
/// </list>
/// A clip without audio gives a video-only copy (no audio track, <see cref="ShareTranscodeResult.AudioTracks"/> empty).
/// Both run on a thread of their own at BelowNormal priority (MTA, for Media Foundation), report progress from 0 to 1 and stop promptly
/// when cancelled. A bad request (an empty or unusable path, the original as the output, an odd or upscaled size) is an
/// <see cref="ArgumentException"/>: thrown at once for what the arguments alone show, else the task's fault before anything is written.
/// An existing file at the output path is replaced: it is deleted before any work (a copy that is in use fails the call, Failed), so
/// when a call fails or is cancelled afterwards only the muxer's .tmp and .part are removed, never a file at the output path (the muxer
/// renames its .part to the output name as its very last step).
/// </summary>
internal static unsafe class ShareTranscoder
{
    private const uint MediaSource = 0xFFFFFFFF;
    private const uint AllStreams = 0xFFFFFFFE;
    private const long AacFrame100ns = 213_333;   // 1024 samples at 48 kHz

    private static readonly HRESULT MF_E_TOPO_CODEC_NOT_FOUND = (HRESULT)unchecked((int)0xC00D5212);
    private static readonly HRESULT MF_E_UNSUPPORTED_BYTESTREAM_TYPE = (HRESULT)unchecked((int)0xC00D36C4);
    private static readonly HRESULT MF_E_INVALID_FILE_FORMAT = (HRESULT)unchecked((int)0xC00D3E8C);
    private static readonly HRESULT MF_E_INVALIDMEDIATYPE = (HRESULT)unchecked((int)0xC00D36B4);
    private const int ErrorDiskFull = unchecked((int)0x80070070), ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const string GpuLostReason = "the graphics driver was reset";

    /// <summary>Test seam: read the source without the GPU (no D3D manager, DXVA off), so the CPU upload path is what copies the frames.</summary>
    internal static bool ForceSoftwareDecode { get; set; }

    /// <summary>Frames that went through the CPU upload path (not a GPU copy), over the process: for the tests and the log.</summary>
    internal static long UploadedFrames;

    /// <summary>Of <see cref="UploadedFrames"/>, those read through IMF2DBuffer.Lock2D at the buffer's own pitch (the rest: a contiguous copy).</summary>
    internal static long UploadedFrames2D;

    /// <summary>A request the caller should not have made (a bad path, an odd size, an upscale, the original as the output). The only
    /// <see cref="ArgumentException"/> that reaches the caller as one: any other from inside the pipeline is a <see cref="ShareTranscodeException"/>.</summary>
    private sealed class ShareRequestException : ArgumentException
    {
        public ShareRequestException(string message, string paramName, Exception? inner = null) : base(message, paramName, inner) { }
    }

    // ------------------------------------------------------------------ entry points

    /// <summary>Re-encodes <paramref name="sourcePath"/> into <paramref name="outputPath"/> as <paramref name="req"/> says, with the Mix track copied.</summary>
    /// <exception cref="ArgumentException">The request is wrong (thrown at once when the arguments alone show it).</exception>
    /// <exception cref="ShareTranscodeException">It could not be done; the message is the reason to show.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (nothing is left behind).</exception>
    public static Task<ShareTranscodeResult> TranscodeAsync(string sourcePath, string outputPath, ShareTranscodeRequest req, IProgress<double>? progress, CancellationToken ct)
    {
        var (src, dst) = ValidatePaths(sourcePath, outputPath);
        ValidateRequest(req);
        return OnWorker("Clink.ShareTranscode", src, dst, () => Transcode(src, dst, req, progress, ct), ct);
    }

    /// <summary>
    /// Copies the video of <paramref name="sourcePath"/> as it is, with only the Mix track (by name; without one, the first audio track), into
    /// <paramref name="outputPath"/>. H.264 and HEVC only, like <see cref="Trimmer"/>: an AV1 clip is refused (Unsupported) and needs
    /// <see cref="TranscodeAsync"/>.
    /// </summary>
    public static Task<ShareTranscodeResult> RemuxMixOnlyAsync(string sourcePath, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var (src, dst) = ValidatePaths(sourcePath, outputPath);
        return OnWorker("Clink.ShareRemux", src, dst, () => RemuxMixOnly(src, dst, progress, ct), ct);
    }

    private static Task<ShareTranscodeResult> OnWorker(string name, string sourcePath, string outputPath, Func<ShareTranscodeResult> body, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ShareTranscodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Nothing may escape this thread (an unhandled exception on it ends the process): every handler is guarded, and the outer
            // catch is the last word.
            try
            {
                try
                {
                    Mf.EnsureStarted();
                    tcs.TrySetResult(body());
                }
                catch (OperationCanceledException)
                {
                    DeleteTemporaries(outputPath);
                    tcs.TrySetCanceled(ct);
                }
                catch (ShareRequestException ex)
                {
                    // Not a reason for the user: the caller should not have asked.
                    DeleteTemporaries(outputPath);
                    tcs.TrySetException(ex);
                }
                catch (Exception ex)
                {
                    DeleteTemporaries(outputPath);
                    ShareTranscodeException typed;
                    try { typed = Classify(ex); }
                    catch (Exception) { typed = new ShareTranscodeException(ShareTranscodeFailure.Failed, "something went wrong while encoding", ex); }
                    try { Log.Warn($"share copy of {Path.GetFileName(sourcePath)} failed ({typed.Kind}): {typed.Message}; {ex.GetType().Name}: {ex.Message}"); }
                    catch (Exception) { }
                    tcs.TrySetException(typed);
                }
            }
            catch (Exception last)
            {
                try { tcs.TrySetException(new ShareTranscodeException(ShareTranscodeFailure.Failed, "something went wrong while encoding", last)); }
                catch (Exception) { }
            }
        }) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>The typed failure for whatever went wrong (a <see cref="ShareTranscodeException"/> stays as it is).</summary>
    internal static ShareTranscodeException Classify(Exception ex)
    {
        if (ex is ShareTranscodeException typed) return typed;
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is IOException && (e.HResult == ErrorDiskFull || e.HResult == ErrorHandleDiskFull))
                return new ShareTranscodeException(ShareTranscodeFailure.DiskFull, "the disk is full", ex);
            if (e.HResult == MF_E_TOPO_CODEC_NOT_FOUND.Value)
                return new ShareTranscodeException(ShareTranscodeFailure.DecoderMissing, "Windows has no decoder for this clip's video", ex);
            if (e.HResult == MF_E_UNSUPPORTED_BYTESTREAM_TYPE.Value || e.HResult == MF_E_INVALID_FILE_FORMAT.Value)
                return new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the file is not a video Clink can read", ex);
            if (e.HResult is unchecked((int)0x887A0005) or unchecked((int)0x887A0006) or unchecked((int)0x887A0007) or unchecked((int)0x887A0020))
                return new ShareTranscodeException(ShareTranscodeFailure.GpuLost, GpuLostReason, ex);
        }
        if (ex is TimeoutException) return new ShareTranscodeException(ShareTranscodeFailure.Failed, "the hardware encoder stopped responding", ex);
        if (ex is FileNotFoundException or DirectoryNotFoundException) return new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip file is not there", ex);
        if (ex is UnauthorizedAccessException) return new ShareTranscodeException(ShareTranscodeFailure.Failed, "the copy could not be written (access denied)", ex);
        if (ex is IOException) return new ShareTranscodeException(ShareTranscodeFailure.Failed, "the copy could not be written", ex);
        return new ShareTranscodeException(ShareTranscodeFailure.Failed, ex is COMException c ? $"Media Foundation failed (0x{(uint)c.HResult:X8})" : "something went wrong while encoding", ex);
    }

    /// <summary>
    /// After a TDR the call that fails says E_FAIL, a timeout or a Media Foundation error, not DXGI_ERROR_DEVICE_REMOVED: when the device
    /// says it was removed (<paramref name="removedReason"/> failed), that is the reason, as the recording engine does. Null when the
    /// device is fine, and for a cancellation or a bad request (those stay what they are).
    /// </summary>
    internal static ShareTranscodeException? AsGpuLost(Exception ex, HRESULT removedReason)
    {
        if (!removedReason.Failed || ex is OperationCanceledException or ShareRequestException) return null;
        if (ex is ShareTranscodeException { Kind: ShareTranscodeFailure.GpuLost } already) return already;
        return new ShareTranscodeException(ShareTranscodeFailure.GpuLost, GpuLostReason,
            new InvalidOperationException($"GPU device removed (0x{(uint)removedReason.Value:X8})", ex));
    }

    // ------------------------------------------------------------------ the request

    private static (string src, string dst) ValidatePaths(string? sourcePath, string? outputPath)
    {
        string src = FullPath(sourcePath, nameof(sourcePath)), dst = FullPath(outputPath, nameof(outputPath));
        if (dst.EndsWith('\\') || dst.EndsWith('/') || string.IsNullOrEmpty(Path.GetFileName(dst)))
            throw new ShareRequestException("The output must be a file, not a folder", nameof(outputPath));
        if (SamePathString(src, dst)) throw new ShareRequestException("The copy cannot be written over the original", nameof(outputPath));
        return (src, dst);
    }

    private static string FullPath(string? path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ShareRequestException("A file path is needed", paramName);
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            throw new ShareRequestException($"Not a usable file path: {ex.Message}", paramName, ex);
        }
    }

    private static bool SamePathString(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static void ValidateRequest(ShareTranscodeRequest? req)
    {
        if (req == null) throw new ShareRequestException("A request is needed", nameof(req));
        if (req.Width <= 0 || req.Height <= 0 || (req.Width & 1) != 0 || (req.Height & 1) != 0)
            throw new ShareRequestException($"The output size must be even and positive, not {req.Width}x{req.Height}", nameof(req));
        if (req.Fps <= 0 || req.Fps > 1000 || req.VideoBitrateBps <= 0 || req.VideoBitrateBps > uint.MaxValue || !(req.GopSeconds > 0) || req.GopSeconds > 60)
            throw new ShareRequestException("The frame rate, bitrate and GOP must be positive (a GOP of at most 60 s)", nameof(req));
        if (!VideoEncoder.H264Fits((uint)req.Width, (uint)req.Height))
            throw new ShareRequestException($"H.264 stops at {VideoEncoder.MaxH264Side} px per side", nameof(req));
    }

    /// <summary>Best effort, never throws: what the muxer writes beside the output (its fast-start temporary and the file it renames at the end). Never the output itself.</summary>
    private static void DeleteTemporaries(string outputPath)
    {
        foreach (string suffix in new[] { ".tmp", ".part" })
        {
            try { File.Delete(outputPath + suffix); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Before any work: the output is not the source (by name, by final path and by file identity, so a hard link, a junction or a mapped
    /// drive spelling of the original is refused), the folder exists, and an earlier file at the output path is gone (the muxer never
    /// overwrites). One that cannot be deleted (open in a player, a share window, Discord) fails the call before anything is written.
    /// </summary>
    private static void PrepareOutput(string sourcePath, string outputPath)
    {
        if (SamePathString(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath)))
            throw new ShareRequestException("The copy cannot be written over the original", nameof(outputPath));
        if (!File.Exists(sourcePath)) throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip file is not there");
        if (Directory.Exists(outputPath)) throw new ShareRequestException("The output must be a file, not a folder", nameof(outputPath));
        if (File.Exists(outputPath))
        {
            bool? same = SameFile(sourcePath, outputPath);
            if (same == true) throw new ShareRequestException("The copy cannot be written over the original (the output path is another name for it)", nameof(outputPath));
            // Not established (the earlier file is open without sharing, or cannot be read): it is not deleted on a guess.
            if (same == null) throw new ShareTranscodeException(ShareTranscodeFailure.Failed, "the earlier copy is in use");
        }
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DeleteTemporaries(outputPath);
        Exception? why = null;
        try { File.Delete(outputPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { why = ex; }
        if (File.Exists(outputPath)) throw new ShareTranscodeException(ShareTranscodeFailure.Failed, "the earlier copy is in use", why);
    }

    /// <summary>
    /// True when the two existing paths are one file: the same final path, or the same volume serial and file index (a hard link). False
    /// when they are two files; null when that could not be established (one of them could not be opened).
    /// </summary>
    internal static bool? SameFile(string a, string b)
    {
        try
        {
            using SafeFileHandle ha = File.OpenHandle(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using SafeFileHandle hb = File.OpenHandle(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string? fa = FinalPath(ha), fb = FinalPath(hb);
            if (fa != null && fb != null && string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)) return true;
            BY_HANDLE_FILE_INFORMATION ia, ib;
            if (!PInvoke.GetFileInformationByHandle(new HANDLE(ha.DangerousGetHandle()), &ia) || !PInvoke.GetFileInformationByHandle(new HANDLE(hb.DangerousGetHandle()), &ib))
                return fa != null && fb != null ? false : null;
            // Some network file systems report no file index (0): there the final paths decide.
            bool noIds = ia.nFileIndexHigh == 0 && ia.nFileIndexLow == 0 && ib.nFileIndexHigh == 0 && ib.nFileIndexLow == 0;
            if (noIds) return fa != null && fb != null ? false : null;
            return ia.dwVolumeSerialNumber == ib.dwVolumeSerialNumber && ia.nFileIndexHigh == ib.nFileIndexHigh && ia.nFileIndexLow == ib.nFileIndexLow;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Where the open file really is (junctions, symbolic links and subst drives resolved), or null.</summary>
    private static string? FinalPath(SafeFileHandle handle)
    {
        var buf = new char[512];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint n;
            fixed (char* p = buf) n = GetFinalPathNameByHandle(handle.DangerousGetHandle(), p, (uint)buf.Length, 0);   // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
            if (n == 0) return null;
            if (n < buf.Length) return new string(buf, 0, (int)n);
            buf = new char[n + 1];
        }
        return null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(nint file, char* path, uint length, uint flags);

    // ------------------------------------------------------------------ what the source holds

    private sealed class SourceVideo
    {
        public uint Stream;
        public Guid Subtype;
        public int Width, Height;
        public double Fps;
        public long Duration100ns;
        public byte[]? SequenceHeader;

        public string? CopyCodec => Subtype == PInvoke.MFVideoFormat_H264 ? "h264" : Subtype == PInvoke.MFVideoFormat_HEVC ? "hevc" : null;
        public string CodecName => Subtype == PInvoke.MFVideoFormat_H264 ? "H.264" : Subtype == PInvoke.MFVideoFormat_HEVC ? "HEVC" : Subtype == PInvoke.MFVideoFormat_AV1 ? "AV1" : "this";
        public long ExpectedFrames => Fps > 0 && Duration100ns > 0 ? Math.Max(1, (long)Math.Round(Duration100ns * Fps / 10_000_000)) : 0;
    }

    /// <summary>The first video stream (selected, every other stream deselected) and the presentation's duration.</summary>
    private static SourceVideo FindVideo(IMFSourceReader* reader)
    {
        reader->SetStreamSelection(AllStreams, false);
        SourceVideo? found = null;
        for (uint i = 0; found == null; i++)
        {
            IMFMediaType* t;
            if (reader->GetNativeMediaType(i, 0, &t).Failed) break;
            try
            {
                var a = (IMFAttributes*)t;
                if (Mf.GetGuid(a, PInvoke.MF_MT_MAJOR_TYPE) != PInvoke.MFMediaType_Video) continue;
                ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE);
                ulong rate = Mf.GetU64(a, PInvoke.MF_MT_FRAME_RATE);
                uint num = (uint)(rate >> 32), den = (uint)rate;
                found = new SourceVideo
                {
                    Stream = i,
                    Subtype = Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE),
                    Width = (int)(size >> 32), Height = (int)(size & 0xFFFFFFFF),
                    Fps = num > 0 && den > 0 ? num / (double)den : 0,
                    SequenceHeader = Mf.GetBlob(a, PInvoke.MF_MT_MPEG_SEQUENCE_HEADER),
                };
            }
            finally { t->Release(); }
        }
        if (found == null) throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip has no video");
        reader->SetStreamSelection(found.Stream, true).Check("SetStreamSelection(video)");
        PROPVARIANT pv = default;
        Guid durKey = PInvoke.MF_PD_DURATION;
        if (reader->GetPresentationAttribute(MediaSource, &durKey, &pv).Succeeded && pv.vt == VARENUM.VT_UI8) found.Duration100ns = (long)pv.uhVal;
        PInvoke.PropVariantClear(ref pv);
        return found;
    }

    /// <summary>The audio track a copy keeps: the source reader's stream, how it is described in the file, where it sits on the clip's
    /// timeline and whether the copy may say its edit lists are true (as a trim does: <see cref="Trimmer"/>).</summary>
    private sealed record MixTrack(uint Stream, AudioTrackDesc Desc, long Shift100ns, bool Aligned);

    /// <summary>The Mix (by name; else the first audio track in file order), or null for a clip without audio (the copy is then video only).</summary>
    private static MixTrack? FindMix(string src, IMFSourceReader* reader)
    {
        var streams = Trimmer.AudioStreamsOf(reader);
        if (streams.Count == 0) return null;
        string[]? names = Trimmer.ReadAudioTrackNames(src);
        if (names != null && names.Length != streams.Count) names = null;
        int n = names == null ? 0 : Math.Max(0, Array.FindIndex(names, s => string.Equals(s, "Mix", StringComparison.OrdinalIgnoreCase)));
        uint stream = streams[n];
        IMFMediaType* t;
        reader->GetNativeMediaType(stream, 0, &t).Check("GetNativeMediaType(audio)");
        AudioTrackDesc desc;
        try
        {
            var a = (IMFAttributes*)t;
            if (Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE) != PInvoke.MFAudioFormat_AAC)
                throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip's audio is not AAC");
            var ud = Mf.GetBlob(a, PInvoke.MF_MT_USER_DATA);
            desc = new AudioTrackDesc
            {
                // The first audio track of a file Clink did not write is its whole sound: that is what the Mix is.
                Name = names != null && !string.IsNullOrWhiteSpace(names[n]) ? names[n] : "Mix",
                AudioSpecificConfig = ud != null && ud.Length > 12 ? ud.AsSpan(12).ToArray() : new byte[] { 0x11, 0x90 },
                SampleRate = (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000),
                Channels = (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2),
                Bitrate = (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000) * 8,
            };
        }
        finally { t->Release(); }
        // The track's place on the clip's timeline, from the file's edit lists (ClipSource hides them from Media Foundation, so the stream
        // starts at 0 on every Windows), and whether they can be trusted: the same rule as a trim.
        var align = ClipAlignment.Read(src);
        bool placed = align.OffsetFrames.Length == streams.Count;
        long shift = placed ? ClipAlignment.To100ns(align.OffsetFrames[n]) : 0;
        return new MixTrack(stream, desc, shift, placed && align.Aligned);
    }

    /// <summary>
    /// The Mix's compressed frames from a reader of their own, handed to the muxer up to each video sample's time, so the copy is written
    /// interleaved and nothing is held in memory. Frames are stamped where they sit on the clip's timeline (stream time + the track's
    /// offset) and the head is cut by the trimmer's rule against the copy's origin.
    /// </summary>
    private sealed class MixFeeder : IDisposable
    {
        private IMFSourceReader* _reader;
        private IDisposable? _owner;
        private readonly uint _stream;
        private readonly long _shift, _lead;
        private bool _eos;
        private AacFrame? _next;
        private long _nextRaw;
        public int Written;

        public MixFeeder(string src, MixTrack mix)
        {
            _stream = mix.Stream;
            _shift = mix.Shift100ns;
            _lead = Math.Max(0, mix.Shift100ns);
            IMFSourceReader* reader = ClipSource.Open(src, null, out _owner);
            _reader = reader;
            try
            {
                reader->SetStreamSelection(AllStreams, false);
                reader->SetStreamSelection(_stream, true).Check("SetStreamSelection(Mix)");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private bool Peek()
        {
            while (_next == null && !_eos)
            {
                uint idx, flags; long ts; IMFSample* sample;
                _reader->ReadSample(_stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample(Mix)");
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample != null) sample->Release();
                    throw new InvalidOperationException("The Mix track could not be read");
                }
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) _eos = true;
                if (sample == null) continue;
                try
                {
                    _next = new AacFrame(ts + _shift, Mf.ReadSampleBytes(sample));
                    _nextRaw = ts;
                }
                finally { sample->Release(); }
            }
            return _next != null;
        }

        /// <summary>Writes the frames due by <paramref name="upTo"/> (all of them for long.MaxValue) as track 0.</summary>
        public void WriteUpTo(ClipMuxWriter mux, long upTo, long origin)
        {
            while (Peek() && _next!.Value.Pts <= upTo)
            {
                var f = _next.Value;
                _next = null;
                if (_nextRaw + _lead + AacFrame100ns <= origin) continue;   // ends before the copy starts (Trimmer's head cut)
                mux.AddAudio(0, f);
                Written++;
            }
        }

        public void Dispose()
        {
            ComUtil.Release(ref _reader);
            _owner?.Dispose();   // after the reader: that closes the clip file
            _owner = null;
        }
    }

    private sealed class ProgressReporter
    {
        private readonly IProgress<double>? _progress;
        private readonly long _expected;
        private double _last = -1;

        public ProgressReporter(IProgress<double>? progress, long expected) { _progress = progress; _expected = expected; }

        public void Frame(long done)
        {
            if (_progress == null || _expected <= 0) return;
            double p = Math.Min(0.99, done / (double)_expected);
            if (p - _last < 0.005) return;
            _last = p;
            _progress.Report(p);
        }

        /// <summary>After the file is complete: a caller's progress handler that throws here does not turn a finished copy into a failure.</summary>
        public void Done()
        {
            try { _progress?.Report(1.0); }
            catch (Exception ex) { Log.Warn($"share copy: the progress handler failed at the end: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private static void DiscardQuietly(ClipMuxWriter? mux)
    {
        try { mux?.Discard(); }
        catch (Exception ex) { Log.Warn($"share copy: discarding the unfinished copy: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ the stream copy

    internal static ShareTranscodeResult RemuxMixOnly(string src, string dst, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();   // first: a call cancelled before it starts touches nothing
        PrepareOutput(src, dst);
        ct.ThrowIfCancellationRequested();
        IMFSourceReader* reader = null;
        IDisposable? owner = null;
        MixFeeder? feeder = null;
        ClipMuxWriter? mux = null;
        try
        {
            reader = ClipSource.Open(src, null, out owner);
            var mix = FindMix(src, reader);
            var video = FindVideo(reader);
            string codec = video.CopyCodec ?? throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported,
                video.Subtype == PInvoke.MFVideoFormat_AV1 ? "an AV1 clip cannot be copied without re-encoding it" : "only H.264 and HEVC clips can be copied without re-encoding them");
            bool hevc = codec == "hevc";
            var report = new ProgressReporter(progress, video.ExpectedFrames);
            if (mix != null) feeder = new MixFeeder(src, mix);
            long origin = 0, frames = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(video.Stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample(video)");
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample != null) sample->Release();
                    throw new InvalidOperationException("The video could not be read");
                }
                bool end = (flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                if (sample != null)
                {
                    try
                    {
                        byte[] data = Mf.ReadSampleBytes(sample);
                        bool key = Mf.GetU32((IMFAttributes*)sample, PInvoke.MFSampleExtension_CleanPoint) != 0 || Nal.ContainsKeyframe(data, hevc);
                        frames++;
                        if (mux == null && key)   // a copy starts on a keyframe
                        {
                            origin = ts;
                            mux = new ClipMuxWriter(ClipOutput.FastStart(dst), new ClipVideoConfig(codec, video.Width, video.Height, 0, video.SequenceHeader),
                                mix != null ? new[] { mix.Desc } : Array.Empty<AudioTrackDesc>(), origin, mix?.Aligned ?? false);
                        }
                        if (mux != null)
                        {
                            feeder?.WriteUpTo(mux, mux.NextVideoPts(ts), origin);
                            mux.AddVideo(ts, key, data);
                        }
                        report.Frame(frames);
                    }
                    finally { sample->Release(); }
                }
                if (end) break;
            }
            if (mux == null) throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip has no keyframe to start from");
            feeder?.WriteUpTo(mux, long.MaxValue, origin);
            ct.ThrowIfCancellationRequested();
            var done = mux.Finish();   // the last step that can fail: from here the copy is complete under its name
            report.Done();
            int fps = (int)Math.Round(video.Fps);
            return new ShareTranscodeResult(done.Path, done.SizeBytes, FrameSpan(done, fps), done.VideoSamples, video.Width, video.Height, fps, done.AudioTracks, "");
        }
        catch
        {
            DiscardQuietly(mux);
            throw;
        }
        finally
        {
            mux?.Dispose();
            feeder?.Dispose();
            if (reader != null) reader->Release();
            owner?.Dispose();
        }
    }

    /// <summary>The video's length: first sample to the last one's start, plus one frame.</summary>
    private static TimeSpan FrameSpan(ClipMuxResult done, int fps)
        => TimeSpan.FromTicks(done.Duration100ns + (fps > 0 && done.VideoSamples > 0 ? 10_000_000L / fps : 0));

    // ------------------------------------------------------------------ the re-encode

    /// <summary>
    /// True when the source's rate is at or below the output's, within 2% (<paramref name="sourceFps"/> &lt;= <paramref name="fps"/> * 1.02):
    /// then no frame is dropped. 59.94 against 60, 29.97 against 30, a 60.02 header on a clip that really runs at 59.9, a clip that
    /// runs at 60.3, and the same rate all count (slotting such a clip would drop frames at slot boundaries). Unknown (0) does not count.
    /// </summary>
    internal static bool KeepsEveryFrame(double sourceFps, int fps) => sourceFps > 0 && sourceFps <= fps * 1.02;

    /// <summary>
    /// Whether a frame at <paramref name="ts"/> is kept for an output of <paramref name="fps"/> that started at <paramref name="first"/>.
    /// A source at or below the output's rate (<see cref="KeepsEveryFrame"/>) keeps every frame from <paramref name="first"/> on. Slotting
    /// such a source would drop frames: a 59.94 source drifts against the 60 grid by about one slot every 1000 frames, so 1 ms rounding or
    /// a 2 ms jitter would put two frames in one slot. A faster source (or one of unknown rate) keeps the first frame in each slot of the
    /// output's frame grid, given the output slot of the last frame kept. Each frame counts half a source frame early (half an output
    /// frame when the source's rate is unknown), so a frame stamped a little before its slot still opens it. A rate twice the output's
    /// then keeps every other frame, on a 1 ms timescale too.
    /// </summary>
    internal static bool Keep(long ts, long first, int fps, double sourceFps, ref long lastSlot)
    {
        if (ts < first) return false;
        if (KeepsEveryFrame(sourceFps, fps)) return true;
        long slack = sourceFps > 0 ? (long)(5_000_000 / sourceFps) : 5_000_000 / fps;
        long slot = (ts - first + slack) * fps / 10_000_000;
        if (slot <= lastSlot) return false;
        lastSlot = slot;
        return true;
    }

    /// <summary>
    /// The frame rate the muxer snaps the copy's timestamps to, or 0 to keep them. Only a copy that drops frames (a source faster than
    /// the output: one kept frame per output slot) is snapped, to give a clean constant rate. A copy that keeps every frame
    /// (<see cref="KeepsEveryFrame"/>: the same rate, 59.94 or 29.97 to 60 or 30, a lower rate) keeps the source's own times. On the
    /// integer grid those frames would slide by a slot every few seconds, and a 60 fps header on a 59.9 fps or jittered clip could round two
    /// frames onto one grid point. An unknown source rate keeps the times too.
    /// </summary>
    internal static uint MuxGridFps(double sourceFps, int fps) => sourceFps > 0 && !KeepsEveryFrame(sourceFps, fps) ? (uint)fps : 0;

    /// <summary>
    /// Whether the encoder's frame counts show real damage. Returns the failure to throw, or null when the copy is good. The copy fails
    /// when fewer frames came out of the encoder than went in: one missing in the middle of a GOP corrupts every later P-frame. It also
    /// fails when more than max(2, 1% of <paramref name="submitted"/>) frames never went in (ProcessInput failed). A few input-side
    /// losses, or ProcessOutput failures with every frame still out, only give a <paramref name="warning"/> (the counts) and keep the copy.
    /// </summary>
    /// <param name="droppedInput">Frames that were submitted but never went in.</param>
    internal static ShareTranscodeException? JudgeEncoderLoss(string encoder, long sourceFrames, long submitted, long framesIn, long framesOut,
        long inputFailures, long outputFailures, out long droppedInput, out string? warning)
    {
        droppedInput = Math.Max(inputFailures, Math.Max(0, submitted - framesIn));
        double allowed = Math.Max(2, submitted * 0.01);
        string counts = $"{encoder}: {framesOut} of {framesIn} frames out, {submitted} submitted, {droppedInput} never went in " +
                        $"({inputFailures} ProcessInput and {outputFailures} ProcessOutput failures, {sourceFrames} source frames)";
        warning = null;
        if (framesOut < framesIn)
            return new ShareTranscodeException(ShareTranscodeFailure.Failed, "the hardware encoder lost frames",
                new InvalidOperationException(counts + ": frames went into the encoder and did not come out"));
        if (droppedInput > allowed)
            return new ShareTranscodeException(ShareTranscodeFailure.Failed, "the hardware encoder lost frames",
                new InvalidOperationException(counts + $": more than {allowed:0.##} frames never went in"));
        if (droppedInput > 0 || outputFailures > 0 || framesOut > framesIn) warning = counts;
        return null;
    }

    /// <summary>The rate of <paramref name="submitted"/> frames stamped from <paramref name="first"/> to <paramref name="last"/> (100 ns): the
    /// intervals between them over the time they span; 0 when fewer than two frames or no time.</summary>
    internal static double MeasuredRate(long submitted, long first, long last)
        => submitted >= 2 && last > first ? (submitted - 1) * 10_000_000.0 / (last - first) : 0;

    internal static ShareTranscodeResult Transcode(string src, string dst, ShareTranscodeRequest req, IProgress<double>? progress, CancellationToken ct)
    {
        ValidateRequest(req);
        ct.ThrowIfCancellationRequested();   // first: a call cancelled before it starts touches nothing
        PrepareOutput(src, dst);
        ct.ThrowIfCancellationRequested();

        D3DDevice? d3d = null;
        IMFSourceReader* reader = null;
        IDisposable? owner = null;
        VideoEncoder? encoder = null;
        MixFeeder? feeder = null;
        ClipMuxWriter? mux = null;
        ID3D11Texture2D* staging = null;
        var outputs = new ConcurrentQueue<(long pts, bool key, byte[] data)>();
        long uploadedBefore = Interlocked.Read(ref UploadedFrames);
        try
        {
            // Which Mix and where it sits come from a plain reader (no decoding): the video reader below hands out decoded frames only.
            MixTrack? mix;
            {
                IMFSourceReader* plain = ClipSource.Open(src, null, out var plainOwner);
                try { mix = FindMix(src, plain); }
                finally
                {
                    plain->Release();
                    plainOwner?.Dispose();
                }
            }

            bool gpuDecode = !ForceSoftwareDecode;
            d3d = D3DDevice.CreateForAdapter(req.Adapter, lowPriority: true);
            IMFAttributes* attrs = Mf.CreateAttributes(4);
            try
            {
                if (gpuDecode) Mf.SetUnknown(attrs, PInvoke.MF_SOURCE_READER_D3D_MANAGER, d3d.DeviceManager);
                else Mf.SetU32(attrs, PInvoke.MF_SOURCE_READER_DISABLE_DXVA, 1);
                Mf.SetU32(attrs, PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, gpuDecode ? 1u : 0u);
                Mf.SetU32(attrs, PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                reader = ClipSource.Open(src, attrs, out owner);
            }
            finally { attrs->Release(); }

            var video = FindVideo(reader);
            if (req.Width > video.Width || req.Height > video.Height)
                throw new ShareRequestException($"A smaller copy is never larger than the clip ({req.Width}x{req.Height} asked, the clip is {video.Width}x{video.Height})", nameof(req));
            int fps = video.Fps > 0 ? Math.Min(req.Fps, (int)Math.Ceiling(video.Fps - 0.01)) : req.Fps;
            int stride = SetDecodedType(reader, video, req.Width, req.Height);

            try
            {
                encoder = new VideoEncoder(d3d, new VideoEncoderConfig((uint)req.Width, (uint)req.Height, (uint)fps, (uint)req.VideoBitrateBps, "h264", req.GopSeconds),
                    VideoEncoderOptions.Offline(ThreadPriority.BelowNormal));
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                throw new ShareTranscodeException(ShareTranscodeFailure.NoHardwareEncoder, $"no hardware H.264 encoder could be opened on {d3d.AdapterName}", ex);
            }
            encoder.OnOutput = (pts, key, annexB) => outputs.Enqueue((pts, key, annexB.ToArray()));

            var report = new ProgressReporter(progress, video.ExpectedFrames);
            long origin = long.MinValue, lastSlot = -1, decoded = 0;
            int submitted = 0;
            long firstKept = 0, lastKept = 0;
            uint gridFps = MuxGridFps(video.Fps, fps);
            var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = (uint)req.Width, bottom = (uint)req.Height, back = 1 };

            void Flush()
            {
                while (outputs.TryDequeue(out var o))
                {
                    if (mux == null)
                    {
                        mux = new ClipMuxWriter(ClipOutput.FastStart(dst), new ClipVideoConfig("h264", req.Width, req.Height, gridFps, encoder!.SequenceHeader),
                            mix != null ? new[] { mix.Desc } : Array.Empty<AudioTrackDesc>(), origin, mix?.Aligned ?? false);
                        if (mix != null) feeder = new MixFeeder(src, mix);
                    }
                    feeder?.WriteUpTo(mux, mux.NextVideoPts(o.pts), origin);
                    mux.AddVideo(o.pts, o.key, o.data);
                }
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint idx, flags; long ts; IMFSample* sample;
                reader->ReadSample(video.Stream, 0, &idx, &flags, &ts, &sample).Check("ReadSample(decoded video)");
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample != null) sample->Release();
                    throw new InvalidOperationException("The video could not be decoded");
                }
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) stride = CheckDecodedType(reader, video.Stream, req.Width, req.Height);
                bool end = (flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                if (sample != null)
                {
                    try
                    {
                        decoded++;
                        if (origin == long.MinValue) origin = ts;
                        if (Keep(ts, origin, fps, video.Fps, ref lastSlot))
                        {
                            IMFSample* input = encoder.AcquireInputBlocking(out ID3D11Texture2D* dstTex, out uint dstSub, ct);
                            try
                            {
                                CopyFrame(d3d, sample, dstTex, dstSub, req.Width, req.Height, stride, &box, ref staging);
                            }
                            catch
                            {
                                input->Release();
                                throw;
                            }
                            finally { dstTex->Release(); }
                            encoder.SubmitBlocking(input, ts, ct);
                            if (submitted == 0) firstKept = ts;
                            lastKept = ts;
                            submitted++;
                        }
                        report.Frame(decoded);
                    }
                    finally { sample->Release(); }
                }
                Flush();
                if (end) break;
            }
            if (submitted == 0) throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip has no video frames");
            encoder.Drain(ct);
            Flush();
            // Every frame that went into the encoder has to come out (0 B-frames, no frame skipping): a hole inside a GOP breaks the rest of
            // it. A few frames that never went in only leave a gap in time (JudgeEncoderLoss).
            long framesIn = Interlocked.Read(ref encoder.FramesIn), framesOut = Interlocked.Read(ref encoder.FramesOut);
            long inFailed = Interlocked.Read(ref encoder.InputFailures), outFailed = Interlocked.Read(ref encoder.OutputFailures);
            double measuredFps = MeasuredRate(submitted, firstKept, lastKept);
            if (JudgeEncoderLoss(encoder.EncoderName, decoded, submitted, framesIn, framesOut, inFailed, outFailed, out long droppedInput, out string? lossWarning) is { } loss)
                throw loss;
            if (lossWarning != null) Log.Warn($"share copy of {Path.GetFileName(src)}: the encoder lost a few frames, the copy is kept: {lossWarning}");
            if (mux == null) throw new ShareTranscodeException(ShareTranscodeFailure.Failed, "the hardware encoder gave no output");
            feeder?.WriteUpTo(mux, long.MaxValue, origin);
            ct.ThrowIfCancellationRequested();
            var done = mux.Finish();   // the last step that can fail: from here the copy is complete under its name
            report.Done();
            Log.Info($"share copy: {Path.GetFileName(src)} -> {req.Width}x{req.Height}@{fps} {req.VideoBitrateBps / 1e6:0.##} Mbps ({encoder.EncoderName}{(gpuDecode ? "" : ", CPU decode")}{(Interlocked.Read(ref UploadedFrames) > uploadedBefore ? ", frames uploaded from system memory" : "")}{(gridFps == 0 ? ", source timestamps kept" : "")}): {decoded} decoded, kept {measuredFps:0.##} fps, {done.VideoSamples} encoded{(droppedInput > 0 ? $", {droppedInput} dropped at the encoder's input" : "")}, {done.SizeBytes / 1048576.0:0.0} MB");
            return new ShareTranscodeResult(done.Path, done.SizeBytes, FrameSpan(done, fps), done.VideoSamples, req.Width, req.Height, fps, done.AudioTracks, encoder.EncoderName)
            {
                DroppedInputFrames = (int)Math.Min(int.MaxValue, droppedInput),
                MeasuredFps = measuredFps,
            };
        }
        catch (Exception ex)
        {
            DiscardQuietly(mux);
            HRESULT removed = default;
            try { if (d3d != null) removed = d3d.RemovedReason; } catch (Exception) { }
            if (AsGpuLost(ex, removed) is { } lost && !ReferenceEquals(lost, ex))
            {
                Log.Warn($"share copy: GPU device removed (0x{(uint)removed.Value:X8}) during the copy; {ex.GetType().Name}: {ex.Message}");
                throw lost;
            }
            throw;
        }
        finally
        {
            encoder?.Dispose();   // first: it holds the device; an undrained encoder is shut down without its last frames
            mux?.Dispose();
            feeder?.Dispose();
            if (reader != null) reader->Release();
            owner?.Dispose();
            ComUtil.Release(ref staging);
            d3d?.Dispose();
        }
    }

    /// <summary>Asks the reader for NV12 at the output size in BT.709 limited range (the video processor scales on the GPU); MF_E_TOPO_CODEC_NOT_FOUND = no decoder.</summary>
    /// <returns>The decoded frames' default stride in bytes (0 = not given).</returns>
    private static int SetDecodedType(IMFSourceReader* reader, SourceVideo video, int width, int height)
    {
        IMFMediaType* want = Mf.CreateMediaType();
        HRESULT hr;
        try
        {
            var a = (IMFAttributes*)want;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
            Mf.SetSize(a, PInvoke.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            Mf.SetU32(a, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            Mf.SetRatio(a, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            // Explicit, or the processor may pick BT.601 for a small output.
            Mf.SetU32(a, PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MFNominalRange.MFNominalRange_16_235);
            Mf.SetU32(a, PInvoke.MF_MT_YUV_MATRIX, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709);
            Mf.SetU32(a, PInvoke.MF_MT_VIDEO_PRIMARIES, (uint)MFVideoPrimaries.MFVideoPrimaries_BT709);
            Mf.SetU32(a, PInvoke.MF_MT_TRANSFER_FUNCTION, (uint)MFVideoTransferFunction.MFVideoTransFunc_709);
            hr = reader->SetCurrentMediaType(video.Stream, null, want);
        }
        finally { want->Release(); }
        if (hr == MF_E_TOPO_CODEC_NOT_FOUND)
            throw new ShareTranscodeException(ShareTranscodeFailure.DecoderMissing, $"Windows has no {video.CodecName} video decoder (the Microsoft Store has it as a video extension)",
                new COMException("SetCurrentMediaType(NV12)", hr.Value));
        if (hr == MF_E_INVALIDMEDIATYPE)
            throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, $"the video cannot be scaled to {width}x{height}", new COMException("SetCurrentMediaType(NV12)", hr.Value));
        hr.Check("SetCurrentMediaType(NV12)");
        return CheckDecodedType(reader, video.Stream, width, height);
    }

    /// <returns>The decoded frames' default stride in bytes (MF_MT_DEFAULT_STRIDE; 0 = not given or bottom-up).</returns>
    private static int CheckDecodedType(IMFSourceReader* reader, uint stream, int width, int height)
    {
        IMFMediaType* cur;
        reader->GetCurrentMediaType(stream, &cur).Check("GetCurrentMediaType");
        try
        {
            var a = (IMFAttributes*)cur;
            ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE);
            if (Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE) != PInvoke.MFVideoFormat_NV12 || (int)(size >> 32) != width || (int)(size & 0xFFFFFFFF) != height)
                throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, $"the video decoder does not give {width}x{height} NV12 frames");
            int stride = (int)Mf.GetU32(a, PInvoke.MF_MT_DEFAULT_STRIDE, 0);   // a signed value stored as UINT32
            return stride > 0 ? stride : 0;
        }
        finally { cur->Release(); }
    }

    /// <summary>
    /// Copies the decoded frame into the encoder's input texture: a GPU copy of the processor's texture (which may be larger than the
    /// frame: decoders pad), or (a frame in system memory: the reader decoded on the CPU) an upload through a staging texture.
    /// </summary>
    private static void CopyFrame(D3DDevice d3d, IMFSample* sample, ID3D11Texture2D* dst, uint dstSub, int width, int height, int defaultStride, D3D11_BOX* box, ref ID3D11Texture2D* staging)
    {
        IMFMediaBuffer* buf;
        sample->GetBufferByIndex(0, &buf).Check("GetBufferByIndex");
        try
        {
            IMFDXGIBuffer* dxgi = ComUtil.TryQuery<IMFDXGIBuffer>(buf);
            if (dxgi != null)
            {
                ID3D11Texture2D* src = null;
                try
                {
                    Guid iid = ID3D11Texture2D.IID_Guid;
                    void* p;
                    dxgi->GetResource(&iid, &p).Check("IMFDXGIBuffer.GetResource");
                    src = (ID3D11Texture2D*)p;
                    uint srcSub;
                    dxgi->GetSubresourceIndex(&srcSub).Check("GetSubresourceIndex");
                    D3D11_TEXTURE2D_DESC desc;
                    src->GetDesc(&desc);
                    if (desc.Format != DXGI_FORMAT.DXGI_FORMAT_NV12 || desc.Width < (uint)width || desc.Height < (uint)height)
                        throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, $"the video decoder gave {desc.Width}x{desc.Height} frames in format {(int)desc.Format}, not {width}x{height} NV12");
                    lock (d3d.ContextLock)
                        d3d.Context->CopySubresourceRegion((ID3D11Resource*)dst, dstSub, 0, 0, 0, (ID3D11Resource*)src, srcSub, box);
                }
                finally
                {
                    if (src != null) src->Release();
                    dxgi->Release();
                }
                return;
            }
        }
        finally { buf->Release(); }
        Upload(d3d, sample, dst, dstSub, width, height, defaultStride, box, ref staging);
    }

    // IMF2DBuffer (not in the generated bindings): IUnknown, then Lock2D(BYTE** scanline0, LONG* pitch), Unlock2D().
    private static readonly Guid IID_IMF2DBuffer = new("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB");
    private const int Lock2DSlot = 3, Unlock2DSlot = 4, GetContiguousLengthSlot = 7;   // 5 GetScanline0AndPitch, 6 IsContiguousFormat, 7 GetContiguousLength(DWORD*)

    /// <summary>
    /// How many rows the Y plane of a 2D NV12 buffer has, so the UV plane starts at scan0 + pitch * rows. The buffer's contiguous length
    /// is stride * rows * 3 / 2 for the stride it was sized with (the pitch, or the width when packed); rows is the frame's
    /// <paramref name="height"/> or a few more (a padded buffer: 1088 for 1080). 0 when the numbers do not add up (an odd length, rows
    /// below the height or implausibly many): the caller then uses the contiguous copy.
    /// </summary>
    internal static int Nv12LumaRows(long contiguousLength, int pitch, int width, int height)
    {
        if (contiguousLength <= 0 || pitch < width || width <= 0 || height <= 0) return 0;
        foreach (int stride in pitch == width ? new[] { pitch } : new[] { pitch, width })
        {
            long twice = contiguousLength * 2, per = 3L * stride;
            if (twice % per != 0) continue;
            long rows = twice / per;
            if (rows >= height && rows <= height + 64) return (int)rows;
        }
        return 0;
    }

    /// <summary>
    /// A frame in system memory into the encoder's texture. The rows are read at the buffer's own pitch: a 2D buffer's (IMF2DBuffer.Lock2D)
    /// when it has one, else the media type's default stride, else (neither said) the contiguous frame's length over its rows.
    /// </summary>
    private static void Upload(D3DDevice d3d, IMFSample* sample, ID3D11Texture2D* dst, uint dstSub, int width, int height, int defaultStride, D3D11_BOX* box, ref ID3D11Texture2D* staging)
    {
        if (staging == null)
            staging = d3d.CreateTexture((uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_NV12, 0, D3D11_USAGE.D3D11_USAGE_STAGING, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE);

        uint count;
        sample->GetBufferCount(&count).Check("GetBufferCount");
        if (count == 1)
        {
            IMFMediaBuffer* one;
            sample->GetBufferByIndex(0, &one).Check("GetBufferByIndex");
            try
            {
                Guid iid = IID_IMF2DBuffer;
                void* b2d = null;
                if (one->QueryInterface(&iid, &b2d).Succeeded && b2d != null)
                {
                    try
                    {
                        void** vtbl = *(void***)b2d;
                        byte* scan0; int pitch;
                        if (((delegate* unmanaged[Stdcall]<void*, byte**, int*, int>)vtbl[Lock2DSlot])(b2d, &scan0, &pitch) >= 0)
                        {
                            try
                            {
                                // NV12 in a 2D buffer: the Y plane, then the interleaved UV plane at half height, both at the pitch.
                                // The UV plane starts after the Y plane's rows, which may be more than the frame's height (a padded
                                // buffer): the rows come from the buffer's contiguous length, and a buffer whose numbers do not add up
                                // is read through the contiguous copy below.
                                if (pitch >= width)
                                {
                                    uint contiguous = 0;
                                    int hr = ((delegate* unmanaged[Stdcall]<void*, uint*, int>)vtbl[GetContiguousLengthSlot])(b2d, &contiguous);
                                    int rows = hr >= 0 ? Nv12LumaRows(contiguous, pitch, width, height) : 0;
                                    if (rows > 0)
                                    {
                                        CopyPlanes(d3d, scan0, pitch, scan0 + (long)pitch * rows, width, height, dst, dstSub, box, staging);
                                        Interlocked.Increment(ref UploadedFrames2D);
                                        return;
                                    }
                                }
                            }
                            finally { ((delegate* unmanaged[Stdcall]<void*, int>)vtbl[Unlock2DSlot])(b2d); }
                        }
                    }
                    finally { ((IUnknown*)b2d)->Release(); }
                }
            }
            finally { one->Release(); }
        }

        IMFMediaBuffer* buf;
        sample->ConvertToContiguousBuffer(&buf).Check("ConvertToContiguousBuffer");
        try
        {
            byte* p; uint max, cur;
            buf->Lock(&p, &max, &cur).Check("Lock");
            try
            {
                // A contiguous NV12 frame: the Y plane, then the interleaved UV plane at half height, both with the same stride.
                int stride = defaultStride >= width ? defaultStride : (int)(cur / (uint)(height * 3 / 2));
                if (stride < width || (long)stride * (height * 3 / 2) > cur) throw new InvalidOperationException($"a {cur}-byte frame is too small for {width}x{height} NV12 at a stride of {stride}");
                CopyPlanes(d3d, p, stride, p + (long)stride * height, width, height, dst, dstSub, box, staging);
            }
            finally { buf->Unlock(); }
        }
        finally { buf->Release(); }
    }

    private static void CopyPlanes(D3DDevice d3d, byte* y, long pitch, byte* uv, int width, int height, ID3D11Texture2D* dst, uint dstSub, D3D11_BOX* box, ID3D11Texture2D* staging)
    {
        lock (d3d.ContextLock)
        {
            D3D11_MAPPED_SUBRESOURCE map;
            d3d.Context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_WRITE, 0, &map).Check("Map(staging)");
            try
            {
                byte* to = (byte*)map.pData;
                for (int r = 0; r < height; r++)
                    Buffer.MemoryCopy(y + r * pitch, to + (long)r * map.RowPitch, map.RowPitch, width);
                byte* toUv = to + (long)height * map.RowPitch;
                for (int r = 0; r < height / 2; r++)
                    Buffer.MemoryCopy(uv + r * pitch, toUv + (long)r * map.RowPitch, map.RowPitch, width);
            }
            finally { d3d.Context->Unmap((ID3D11Resource*)staging, 0); }
            d3d.Context->CopySubresourceRegion((ID3D11Resource*)dst, dstSub, 0, 0, 0, (ID3D11Resource*)staging, 0, box);
        }
        Interlocked.Increment(ref UploadedFrames);
    }
}
