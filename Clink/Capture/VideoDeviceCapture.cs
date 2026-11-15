using System.Runtime.InteropServices;
using Clink.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Clink.Capture;

/// <summary>Why a capture card cannot be recorded: unplugged (or never connected), held by another app, or blocked by Windows camera privacy.</summary>
internal enum CaptureCardProblem { NotConnected, InUse, Blocked }

/// <summary>A capture card failed for a reason the app acts on (<see cref="Problem"/>): a typed exception, so nothing reads the words of its message.</summary>
internal sealed class CaptureCardException : Exception
{
    public CaptureCardProblem Problem { get; }
    /// <summary>The card's name as Windows lists it.</summary>
    public string Card { get; }

    public CaptureCardException(CaptureCardProblem problem, string card, Exception? inner = null)
        : base(EnglishOf(problem, card) + (inner != null ? " (" + inner.Message + ")" : ""), inner)
    {
        Problem = problem;
        Card = card;
        if (inner != null) HResult = inner.HResult;
    }

    private static string EnglishOf(CaptureCardProblem problem, string card) => problem switch
    {
        CaptureCardProblem.InUse => $"Capture card {card} is in use by another app",
        CaptureCardProblem.Blocked => "Windows blocks the capture card: allow camera access for desktop apps in Windows privacy settings",
        _ => $"Capture card {card} is not connected",
    };

    /// <summary>
    /// What a failed Media Foundation call on a card means. <paramref name="listed"/>: whether Windows still lists the card (null: the
    /// listing failed, so unknown). An error that names no cause is "not connected" only when the card is no longer listed; otherwise
    /// it is a plain error (null), retried like any other.
    /// </summary>
    public static CaptureCardProblem? Classify(int hresult, bool? listed) => unchecked((uint)hresult) switch
    {
        0xC00D3EA2 => CaptureCardProblem.NotConnected,   // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED: unplugged while streaming
        0x8007048F => CaptureCardProblem.NotConnected,   // ERROR_DEVICE_NOT_CONNECTED: what some drivers return once the card is pulled
        0x8007001F => CaptureCardProblem.NotConnected,   // ERROR_GEN_FAILURE: a USB card pulled while a read waits
        0xC00D3EA3 => CaptureCardProblem.InUse,          // MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
        0xC00D3704 => CaptureCardProblem.InUse,          // MF_E_HW_MFT_FAILED_START_STREAMING
        0x800700AA => CaptureCardProblem.InUse,          // ERROR_BUSY
        0x80070020 => CaptureCardProblem.InUse,          // ERROR_SHARING_VIOLATION
        0x80070005 => CaptureCardProblem.Blocked,        // E_ACCESSDENIED: camera privacy is off for desktop apps
        _ => listed == false ? CaptureCardProblem.NotConnected : null,
    };
}

/// <summary>
/// Captures a capture card (any Media Foundation video capture device) and keeps the most recent frame in a GPU texture, like
/// <see cref="MonitorCapture"/>. A source reader on the engine's D3D device turns the card's frames (NV12, YUY2, P010, MJPG...) into BGRA
/// textures with the GPU video processor. The card's own rate drives the engine (<see cref="FrameArrived"/>): a card delivers exactly its
/// mode's rate and cannot be oversampled like a monitor. Frames are stamped with QPC time when they arrive. An HDR card is recorded as SDR
/// (<see cref="HdrIgnored"/>).
/// </summary>
internal sealed unsafe class VideoDeviceCapture : IVideoFrameSource
{
    private const uint FirstVideoStream = 0xFFFFFFFC;   // MF_SOURCE_READER_FIRST_VIDEO_STREAM
    private const uint AllStreams = 0xFFFFFFFE;         // MF_SOURCE_READER_ALL_STREAMS
    private const uint FlagError = 0x1, FlagEndOfStream = 0x2, FlagCurrentTypeChanged = 0x20;   // MF_SOURCE_READERF_*

    private readonly D3DDevice _d3d;
    private readonly VideoDevice _device;
    private IMFMediaSource* _source;
    private IMFSourceReader* _reader;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly AutoResetEvent _arrived = new(false);

    private ID3D11Texture2D* _latest;
    private uint _latestW, _latestH;
    private DXGI_FORMAT _latestFormat;
    private uint _frameW, _frameH;   // the reader's output size (its textures may be padded beyond it)
    private bool _hasFrame;
    private long _latestTime;
    private long _frameCount, _copyCount;
    private volatile Exception? _fault;
    private bool _disposed;   // under the context lock: set by Dispose, so a reader that outlives it never touches the device (it may be gone)

    /// <summary>The card's mode that is recorded.</summary>
    public VideoMode Mode { get; }
    /// <summary>The card sends HDR (PQ): it is recorded as SDR, without tone mapping (the colours look flat).</summary>
    public bool HdrIgnored => Mode.Hdr;
    /// <summary>Always false: the reader hands over 8-bit BGRA, so the converter does no tone mapping.</summary>
    public bool IsHdr => false;
    public long FramesReceived => Interlocked.Read(ref _frameCount);
    public long FramesCopied => Interlocked.Read(ref _copyCount);
    public bool HasFrame => Volatile.Read(ref _hasFrame);
    public WaitHandle? FrameArrived => _arrived;
    public long LatestTime => Interlocked.Read(ref _latestTime);
    public Exception? Fault => _fault;

    /// <summary>
    /// Opens <paramref name="device"/> at the mode <see cref="VideoModePick.Pick"/> chooses for the preset box and <paramref name="wantedFps"/>,
    /// and starts reading. Throws <see cref="CaptureCardException"/> when the card is not connected, in use or blocked.
    /// </summary>
    public VideoDeviceCapture(D3DDevice d3d, VideoDevice device, uint maxWidth, uint maxHeight, double wantedFps)
    {
        _d3d = d3d;
        _device = device;
        try
        {
            _source = VideoDevices.CreateSource(device.Link);
            Mode = ChooseMode(_source, maxWidth, maxHeight, wantedFps, device.Name);
            Log.Info($"Capture card {device.Name}: recording {Mode}");
            if (Mode.Hdr) Log.Warn($"Capture card {device.Name} sends HDR: recorded as SDR (no tone mapping)");
            OpenReader();
        }
        catch (Exception ex)
        {
            Release();
            throw Translate(ex, "open");
        }
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "Clink.Card", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Sets the chosen native mode on the card itself (the documented way to pick a capture format), before the reader exists.</summary>
    private static VideoMode ChooseMode(IMFMediaSource* source, uint maxW, uint maxH, double wantedFps, string name)
    {
        IMFPresentationDescriptor* pd = null;
        IMFStreamDescriptor* sd = null;
        IMFMediaTypeHandler* handler = null;
        try
        {
            source->CreatePresentationDescriptor(&pd).Check("CreatePresentationDescriptor");
            BOOL selected;
            pd->GetStreamDescriptorByIndex(0, &selected, &sd).Check("GetStreamDescriptorByIndex");
            sd->GetMediaTypeHandler(&handler).Check("GetMediaTypeHandler");
            uint count;
            handler->GetMediaTypeCount(&count).Check("GetMediaTypeCount");
            var modes = new List<(VideoMode Mode, uint Index)>();
            for (uint i = 0; i < count; i++)
            {
                IMFMediaType* t;
                if (handler->GetMediaTypeByIndex(i, &t).Failed) continue;
                try { if (VideoDevices.ModeOf(t) is { } m) modes.Add((m, i)); }
                finally { ((IUnknown*)t)->Release(); }
            }
            var pick = VideoModePick.Pick(modes.Select(m => m.Mode).ToList(), maxW, maxH, wantedFps)
                ?? throw new InvalidOperationException($"Capture card {name} offers no video mode Clink can record ({modes.Count} modes)");
            uint index = modes.First(m => m.Mode == pick).Index;
            IMFMediaType* chosen;
            handler->GetMediaTypeByIndex(index, &chosen).Check("GetMediaTypeByIndex");
            try { handler->SetCurrentMediaType(chosen).Check("SetCurrentMediaType(card mode)"); }
            finally { ((IUnknown*)chosen)->Release(); }
            return pick;
        }
        finally
        {
            if (handler != null) ((IUnknown*)handler)->Release();
            if (sd != null) ((IUnknown*)sd)->Release();
            if (pd != null) ((IUnknown*)pd)->Release();
        }
    }

    private void OpenReader()
    {
        IMFAttributes* attrs = Mf.CreateAttributes(4);
        try
        {
            // The same pattern as the share transcoder's GPU decode: the engine's device, so the frames are textures the converter can read.
            Mf.SetUnknown(attrs, PInvoke.MF_SOURCE_READER_D3D_MANAGER, _d3d.DeviceManager);
            Mf.SetU32(attrs, PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            Mf.SetU32(attrs, PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            Mf.SetU32(attrs, PInvoke.MF_LOW_LATENCY, 1);
            IMFSourceReader* reader;
            PInvoke.MFCreateSourceReaderFromMediaSource(_source, attrs, &reader).Check("MFCreateSourceReaderFromMediaSource");
            _reader = reader;
        }
        finally { ((IUnknown*)attrs)->Release(); }

        _reader->SetStreamSelection(AllStreams, false);
        _reader->SetStreamSelection(FirstVideoStream, true).Check("SetStreamSelection(video)");
        IMFMediaType* want = Mf.CreateMediaType();
        try
        {
            var a = (IMFAttributes*)want;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            Mf.SetSize(a, PInvoke.MF_MT_FRAME_SIZE, Mode.Width, Mode.Height);
            Mf.SetU32(a, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            _reader->SetCurrentMediaType(FirstVideoStream, null, want).Check("SetCurrentMediaType(RGB32)");
        }
        finally { ((IUnknown*)want)->Release(); }
        ReadOutputSize();
    }

    private void ReadOutputSize()
    {
        IMFMediaType* cur;
        _reader->GetCurrentMediaType(FirstVideoStream, &cur).Check("GetCurrentMediaType");
        try
        {
            ulong size = Mf.GetU64((IMFAttributes*)cur, PInvoke.MF_MT_FRAME_SIZE);
            _frameW = (uint)(size >> 32);
            _frameH = (uint)size;
        }
        finally { ((IUnknown*)cur)->Release(); }
        if (_frameW == 0 || _frameH == 0) { _frameW = Mode.Width; _frameH = Mode.Height; }
    }

    private void ReadLoop()
    {
        try
        {
            while (!_stop)
            {
                uint index, flags;
                long time;
                IMFSample* sample = null;
                HRESULT hr = _reader->ReadSample(FirstVideoStream, 0, &index, &flags, &time, &sample);
                if (_stop) { if (sample != null) ((IUnknown*)sample)->Release(); break; }
                if (hr.Failed) { Fail(new COMException($"ReadSample failed: 0x{(uint)hr.Value:X8}", hr.Value)); break; }
                if ((flags & FlagError) != 0) { if (sample != null) ((IUnknown*)sample)->Release(); Fail(new COMException("ReadSample: stream error", unchecked((int)0x80004005))); break; }
                if ((flags & FlagEndOfStream) != 0) { if (sample != null) ((IUnknown*)sample)->Release(); Fail(new COMException("ReadSample: end of stream", unchecked((int)0xC00D3EA2))); break; }
                if ((flags & FlagCurrentTypeChanged) != 0)
                {
                    ReadOutputSize();
                    Log.Info($"Capture card {_device.Name}: the signal changed, frames are {_frameW}x{_frameH} now");
                }
                if (sample == null) continue;   // a gap in the stream (no signal for a moment)
                try { CopyFrame(sample, time); }
                finally { ((IUnknown*)sample)->Release(); }
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        if (_stop) return;
        var fault = Translate(ex, "read");
        Log.Warn($"Capture card {_device.Name} stopped: {fault.Message}");
        _fault = fault;
        try { _arrived.Set(); } catch (ObjectDisposedException) { }   // wake the engine at once
    }

    /// <summary>A failure as the app acts on it: <see cref="CaptureCardException"/> when its HRESULT (or the card's absence) says why.</summary>
    private Exception Translate(Exception ex, string what)
    {
        if (ex is CaptureCardException) return ex;
        bool? listed = null;
        try { listed = VideoDevices.IsConnected(_device.Link); } catch { }
        return CaptureCardException.Classify(ex.HResult, listed) is { } problem
            ? new CaptureCardException(problem, _device.Name, ex)
            : new InvalidOperationException($"Capture card {_device.Name}: {what} failed: {ex.Message}", ex);
    }

    private void CopyFrame(IMFSample* sample, long sampleTime)
    {
        long now = ComUtil.QpcNow100ns();
        bool first = Interlocked.Increment(ref _frameCount) == 1;
        IMFMediaBuffer* buf;
        sample->GetBufferByIndex(0, &buf).Check("GetBufferByIndex");
        try
        {
            IMFDXGIBuffer* dxgi = ComUtil.TryQuery<IMFDXGIBuffer>(buf);
            if (dxgi == null) throw new InvalidOperationException("the frames are not on the GPU (no Direct3D video processing for this card)");
            ID3D11Texture2D* src = null;
            try
            {
                Guid iid = ID3D11Texture2D.IID_Guid;
                void* p;
                dxgi->GetResource(&iid, &p).Check("IMFDXGIBuffer.GetResource");
                src = (ID3D11Texture2D*)p;
                uint sub;
                dxgi->GetSubresourceIndex(&sub).Check("GetSubresourceIndex");
                D3D11_TEXTURE2D_DESC desc;
                src->GetDesc(&desc);
                uint w = Math.Min(_frameW, desc.Width), h = Math.Min(_frameH, desc.Height);
                if (first) Log.Info($"Capture card {_device.Name}: first frame {w}x{h} (texture {desc.Width}x{desc.Height} format={desc.Format} array={desc.ArraySize}), sample time {sampleTime}, QPC {now}");
                var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = w, bottom = h, back = 1 };
                lock (_d3d.ContextLock)
                {
                    if (_disposed) return;   // the engine may have disposed its device already (a reader that did not stop in time)
                    EnsureLatest(w, h, desc.Format);
                    _d3d.Context->CopySubresourceRegion((ID3D11Resource*)_latest, 0, 0, 0, 0, (ID3D11Resource*)src, sub, &box);
                    _latestTime = now;
                    _hasFrame = true;
                }
                Interlocked.Increment(ref _copyCount);
            }
            finally
            {
                if (src != null) src->Release();
                dxgi->Release();
            }
        }
        finally { buf->Release(); }
        _arrived.Set();
    }

    private void EnsureLatest(uint w, uint h, DXGI_FORMAT format)
    {
        if (_latest != null && _latestW == w && _latestH == h && _latestFormat == format) return;
        ComUtil.Release(ref _latest);
        _latest = _d3d.CreateTexture(w, h, format, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        _latestW = w;
        _latestH = h;
        _latestFormat = format;
    }

    public bool WithLatest(Action<nint, uint, uint, long> use)
    {
        lock (_d3d.ContextLock)
        {
            if (!_hasFrame || _latest == null) return false;
            use((nint)_latest, _latestW, _latestH, _latestTime);
            return true;
        }
    }

    private void Release()
    {
        if (_reader != null) { ((IUnknown*)_reader)->Release(); _reader = null; }
        if (_source != null) { _source->Shutdown(); ((IUnknown*)_source)->Release(); _source = null; }
    }

    public void Dispose()
    {
        _stop = true;
        // Shutting the card down ends a ReadSample that waits for a frame (no signal), so the thread can be joined.
        try { if (_source != null) _source->Shutdown(); } catch { }
        bool joined = _thread == null || _thread.Join(3000);
        if (joined) Release();
        else Log.Warn($"Capture card {_device.Name}: the reader thread did not stop within 3 s (ReadSample still blocked after Shutdown); its reader and source are left to the process, and it copies no more frames");
        lock (_d3d.ContextLock) { _disposed = true; ComUtil.Release(ref _latest); }
        if (joined) _arrived.Dispose();   // (a reader still running may set it)
    }
}
