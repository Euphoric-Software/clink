using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Encode;

internal sealed record VideoEncoderConfig(uint Width, uint Height, uint Fps, uint BitrateBps, bool Hevc, float GopSeconds);

/// <summary>
/// Hardware H.264/HEVC encoder through Media Foundation (NVENC / AMF / QuickSync MFT), fed with NV12 textures
/// on the capture device so frames never leave the GPU. Async MFT: inputs are gated by NeedInput credits,
/// outputs are drained on a dedicated event thread.
/// </summary>
internal sealed unsafe class VideoEncoder : IDisposable
{
    public delegate void OutputHandler(long pts100ns, bool keyframe, ReadOnlySpan<byte> annexB);

    private readonly D3DDevice _d3d;
    private readonly VideoEncoderConfig _cfg;
    private IMFTransform* _mft;
    private IMFMediaEventGenerator* _gen;
    private IMFVideoSampleAllocatorEx* _alloc;
    private IMFMediaType* _inputType;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _lock = new();
    private int _credits;
    private bool _providesSamples;
    private uint _outputSize;
    private readonly long _frameDuration;

    public string EncoderName { get; private set; } = "";
    public long FramesIn, FramesOut, Dropped;
    public OutputHandler? OnOutput;
    public byte[]? SequenceHeader { get; private set; }
    public bool Hevc => _cfg.Hevc;

    public VideoEncoder(D3DDevice d3d, VideoEncoderConfig cfg)
    {
        Mf.EnsureStarted();
        _d3d = d3d;
        _cfg = cfg;
        _frameDuration = 10_000_000L / cfg.Fps;

        Guid outSub = cfg.Hevc ? PInvoke.MFVideoFormat_HEVC : PInvoke.MFVideoFormat_H264;
        _mft = FindHardwareEncoder(outSub, out string name);
        EncoderName = name;
        Log.Info($"Video encoder: {name} ({(cfg.Hevc ? "HEVC" : "H.264")}) {cfg.Width}x{cfg.Height}@{cfg.Fps} {cfg.BitrateBps / 1_000_000.0:0.#} Mbps");

        IMFAttributes* attrs;
        _mft->GetAttributes(&attrs).Check("IMFTransform.GetAttributes");
        try
        {
            if (Mf.GetU32(attrs, PInvoke.MF_TRANSFORM_ASYNC) == 0) throw new InvalidOperationException("Encoder is not an async MFT");
            if (Mf.GetU32(attrs, PInvoke.MF_SA_D3D11_AWARE) == 0) throw new InvalidOperationException("Encoder is not D3D11-aware");
            Mf.SetU32(attrs, PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            try { Mf.SetU32(attrs, PInvoke.MF_LOW_LATENCY, 1); } catch { }
        }
        finally { attrs->Release(); }

        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)_d3d.DeviceManager).Check("MFT_MESSAGE_SET_D3D_MANAGER");

        // Output type
        IMFMediaType* outType = Mf.CreateMediaType();
        try
        {
            var a = (IMFAttributes*)outType;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, outSub);
            Mf.SetU32(a, PInvoke.MF_MT_AVG_BITRATE, cfg.BitrateBps);
            Mf.SetSize(a, PInvoke.MF_MT_FRAME_SIZE, cfg.Width, cfg.Height);
            Mf.SetRatio(a, PInvoke.MF_MT_FRAME_RATE, cfg.Fps, 1);
            Mf.SetU32(a, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            Mf.SetRatio(a, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.SetU32(a, PInvoke.MF_MT_MPEG2_PROFILE, cfg.Hevc ? (uint)eAVEncH265VProfile.eAVEncH265VProfile_Main_420_8 : (uint)eAVEncH264VProfile.eAVEncH264VProfile_High);
            _mft->SetOutputType(0, outType, 0).Check("SetOutputType");
        }
        finally { outType->Release(); }

        // Input type: NV12 at the same size/rate.
        IMFMediaType* inType = null;
        for (uint i = 0; ; i++)
        {
            IMFMediaType* t;
            HRESULT hr = _mft->GetInputAvailableType(0, i, &t);
            if (hr.Failed) break;
            if (Mf.GetGuid((IMFAttributes*)t, PInvoke.MF_MT_SUBTYPE) == PInvoke.MFVideoFormat_NV12) { inType = t; break; }
            t->Release();
        }
        if (inType == null)
        {
            inType = Mf.CreateMediaType();
            Mf.SetGuid((IMFAttributes*)inType, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid((IMFAttributes*)inType, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
        }
        {
            var a = (IMFAttributes*)inType;
            Mf.SetSize(a, PInvoke.MF_MT_FRAME_SIZE, cfg.Width, cfg.Height);
            Mf.SetRatio(a, PInvoke.MF_MT_FRAME_RATE, cfg.Fps, 1);
            Mf.SetU32(a, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            Mf.SetRatio(a, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.SetU32(a, PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MFNominalRange.MFNominalRange_16_235);
            Mf.SetU32(a, PInvoke.MF_MT_YUV_MATRIX, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709);
            Mf.SetU32(a, PInvoke.MF_MT_VIDEO_PRIMARIES, (uint)MFVideoPrimaries.MFVideoPrimaries_BT709);
            Mf.SetU32(a, PInvoke.MF_MT_TRANSFER_FUNCTION, (uint)MFVideoTransferFunction.MFVideoTransFunc_709);
            _mft->SetInputType(0, inType, 0).Check("SetInputType(NV12)");
        }
        _inputType = inType;

        ConfigureCodecApi();

        MFT_OUTPUT_STREAM_INFO info;
        _mft->GetOutputStreamInfo(0, &info).Check("GetOutputStreamInfo");
        _providesSamples = (info.dwFlags & (uint)(_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | _MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outputSize = info.cbSize > 0 ? info.cbSize : 4u << 20;

        // Sample allocator bound to our device: NV12 textures the compute shader can write (UAV) and the encoder can read.
        Guid iidAlloc = IMFVideoSampleAllocatorEx.IID_Guid;
        void* pAlloc;
        PInvoke.MFCreateVideoSampleAllocatorEx(&iidAlloc, &pAlloc).Check("MFCreateVideoSampleAllocatorEx");
        _alloc = (IMFVideoSampleAllocatorEx*)pAlloc;
        _alloc->SetDirectXManager((IUnknown*)_d3d.DeviceManager).Check("SetDirectXManager");
        IMFAttributes* allocAttrs = Mf.CreateAttributes(4);
        try
        {
            Mf.SetU32(allocAttrs, PInvoke.MF_SA_D3D11_BINDFLAGS, (uint)(D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE));
            Mf.SetU32(allocAttrs, PInvoke.MF_SA_D3D11_USAGE, (uint)D3D11_USAGE.D3D11_USAGE_DEFAULT);
            Mf.SetU32(allocAttrs, PInvoke.MF_SA_BUFFERS_PER_SAMPLE, 1);
            _alloc->InitializeSampleAllocatorEx(6, 24, allocAttrs, inType).Check("InitializeSampleAllocatorEx");
        }
        finally { allocAttrs->Release(); }

        _gen = ComUtil.Query<IMFMediaEventGenerator>(_mft, "QI IMFMediaEventGenerator");

        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0).Check("NOTIFY_BEGIN_STREAMING");
        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0).Check("NOTIFY_START_OF_STREAM");

        _thread = new Thread(EventLoop) { IsBackground = true, Name = "Clink.Encoder", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private IMFTransform* FindHardwareEncoder(Guid outSub, out string name)
    {
        var inInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 };
        var outInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = outSub };
        var flags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;

        IMFActivate** list = null;
        uint count = 0;

        // Prefer the encoder on the adapter that owns the capture device.
        IMFAttributes* enumAttrs = Mf.CreateAttributes(1);
        try
        {
            LUID luid = _d3d.AdapterLuid;
            Mf.SetBlob(enumAttrs, PInvoke.MFT_ENUM_ADAPTER_LUID, new ReadOnlySpan<byte>(&luid, sizeof(LUID)));
            HRESULT hr = PInvoke.MFTEnum2(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, &inInfo, &outInfo, enumAttrs, &list, &count);
            if (hr.Failed || count == 0)
            {
                if (list != null) { PInvoke.CoTaskMemFree(list); list = null; }
                PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, &inInfo, &outInfo, &list, &count).Check("MFTEnumEx");
            }
        }
        finally { enumAttrs->Release(); }

        if (count == 0 || list == null)
            throw new InvalidOperationException($"No hardware {(outSub == PInvoke.MFVideoFormat_HEVC ? "HEVC" : "H.264")} encoder found on {_d3d.AdapterName}. Update your GPU driver.");

        try
        {
            Exception? last = null;
            for (uint i = 0; i < count; i++)
            {
                IMFActivate* act = list[i];
                string friendly = GetString((IMFAttributes*)act, PInvoke.MFT_FRIENDLY_NAME_Attribute) ?? "Hardware encoder";
                try
                {
                    Guid iid = IMFTransform.IID_Guid;
                    void* p;
                    act->ActivateObject(&iid, &p).Check("ActivateObject");
                    name = friendly;
                    return (IMFTransform*)p;
                }
                catch (Exception ex) { last = ex; Log.Warn($"Encoder '{friendly}' failed to activate: {ex.Message}"); }
            }
            throw new InvalidOperationException("No usable hardware encoder", last);
        }
        finally
        {
            for (uint i = 0; i < count; i++) list[i]->Release();
            PInvoke.CoTaskMemFree(list);
        }
    }

    private static string? GetString(IMFAttributes* a, Guid key)
    {
        PWSTR s; uint len;
        if (a->GetAllocatedString(&key, &s, &len).Failed) return null;
        string r = s.ToString();
        PInvoke.CoTaskMemFree(s.Value);
        return r;
    }

    private void ConfigureCodecApi()
    {
        ICodecAPI* codec = ComUtil.TryQuery<ICodecAPI>(_mft);
        if (codec == null) return;
        try
        {
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonRateControlMode, (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR);
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonMeanBitRate, _cfg.BitrateBps);
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonMaxBitRate, _cfg.BitrateBps);
            SetU32(codec, KnownGuids.CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(1, Math.Round(_cfg.Fps * _cfg.GopSeconds)));
            SetU32(codec, KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount, 0);
            SetBool(codec, KnownGuids.CODECAPI_AVLowLatencyMode, true);
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, 66);
        }
        finally { codec->Release(); }

        static void SetU32(ICodecAPI* c, Guid key, uint value)
        {
            if (c->IsSupported(&key).Failed) return; // e.g. NVENC MFT has no B-frame count property
            VARIANT v = default; v.vt = VARENUM.VT_UI4; v.ulVal = value;
            HRESULT hr = c->SetValue(&key, &v);
            if (hr.Failed) Log.Warn($"ICodecAPI {key} = {value} not accepted (0x{(uint)hr.Value:X8})");
        }
        static void SetBool(ICodecAPI* c, Guid key, bool value)
        {
            VARIANT v = default; v.vt = VARENUM.VT_BOOL; v.boolVal = new VARIANT_BOOL((short)(value ? -1 : 0));
            HRESULT hr = c->SetValue(&key, &v);
            if (hr.Failed) Log.Warn($"ICodecAPI {key} = {value} not accepted (0x{(uint)hr.Value:X8})");
        }
    }

    /// <summary>Gets a free NV12 sample from the pool. Returns false (frame should be dropped) when the encoder is backed up.</summary>
    public bool TryAcquireInput(out IMFSample* sample, out ID3D11Texture2D* texture, out uint subresource)
    {
        sample = null; texture = null; subresource = 0;
        IMFSample* s;
        if (_alloc->AllocateSample(&s).Failed) return false;
        IMFMediaBuffer* buf;
        if (s->GetBufferByIndex(0, &buf).Failed) { s->Release(); return false; }
        try
        {
            IMFDXGIBuffer* dxgi = ComUtil.TryQuery<IMFDXGIBuffer>(buf);
            if (dxgi == null) { s->Release(); return false; }
            try
            {
                Guid iid = ID3D11Texture2D.IID_Guid;
                void* p;
                dxgi->GetResource(&iid, &p).Check("IMFDXGIBuffer.GetResource");
                uint sub;
                dxgi->GetSubresourceIndex(&sub);
                texture = (ID3D11Texture2D*)p;
                subresource = sub;
            }
            finally { dxgi->Release(); }
        }
        finally { buf->Release(); }
        sample = s;
        return true;
    }

    private readonly Queue<nint> _pending = new();
    private const int MaxPending = 4;

    /// <summary>Hands a filled sample to the encoder (always consumes the caller's reference).</summary>
    public void Submit(IMFSample* sample, long pts100ns)
    {
        sample->SetSampleTime(pts100ns);
        sample->SetSampleDuration(_frameDuration);
        lock (_lock)
        {
            if (_stop) { sample->Release(); Interlocked.Increment(ref Dropped); return; }
            if (_credits > 0)
            {
                FeedLocked(sample);
                return;
            }
            // Encoder hasn't asked for input yet: keep a short queue so we don't drop on event latency.
            if (_pending.Count >= MaxPending)
            {
                var oldest = (IMFSample*)_pending.Dequeue();
                oldest->Release();
                Interlocked.Increment(ref Dropped);
            }
            _pending.Enqueue((nint)sample);
        }
    }

    private void FeedLocked(IMFSample* sample)
    {
        HRESULT hr = _mft->ProcessInput(0, sample, 0);
        if (hr.Succeeded) { _credits--; Interlocked.Increment(ref FramesIn); }
        else { Log.Warn($"ProcessInput failed 0x{(uint)hr.Value:X8}"); Interlocked.Increment(ref Dropped); }
        sample->Release();
    }

    private void EventLoop()
    {
        while (!_stop)
        {
            IMFMediaEvent* evt;
            HRESULT hr = _gen->GetEvent(0, &evt);
            if (hr.Failed)
            {
                if (_stop) break;
                Thread.Sleep(5);
                continue;
            }
            try
            {
                uint type;
                evt->GetType(&type);
                switch (type)
                {
                    case Mf.METransformNeedInput:
                        lock (_lock)
                        {
                            _credits++;
                            if (_pending.Count > 0) FeedLocked((IMFSample*)_pending.Dequeue());
                        }
                        break;
                    case Mf.METransformHaveOutput:
                        DrainOutput();
                        break;
                    case Mf.MEError:
                        HRESULT status;
                        evt->GetStatus(&status);
                        Log.Error($"Encoder reported error 0x{(uint)status.Value:X8}");
                        break;
                }
            }
            catch (Exception ex) { Log.Error("Encoder event loop", ex); }
            finally { evt->Release(); }
        }
    }

    private void DrainOutput()
    {
        MFT_OUTPUT_DATA_BUFFER ob = default;
        ob.dwStreamID = 0;
        if (!_providesSamples)
        {
            IMFMediaBuffer* buf;
            PInvoke.MFCreateMemoryBuffer(_outputSize, &buf).Check("MFCreateMemoryBuffer");
            IMFSample* s;
            PInvoke.MFCreateSample(&s).Check("MFCreateSample");
            s->AddBuffer(buf);
            buf->Release();
            ob.pSample = s;
        }
        uint status;
        HRESULT hr = _mft->ProcessOutput(0, 1, &ob, &status);
        if (ob.pEvents != null) ((IUnknown*)ob.pEvents)->Release();
        if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT) { if (ob.pSample != null) ob.pSample->Release(); return; }
        if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE) { if (ob.pSample != null) ob.pSample->Release(); RefreshSequenceHeader(); return; }
        if (hr.Failed) { if (ob.pSample != null) ob.pSample->Release(); Log.Warn($"ProcessOutput failed 0x{(uint)hr.Value:X8}"); return; }
        IMFSample* sample = ob.pSample;
        if (sample == null) return;
        try
        {
            if (SequenceHeader == null) RefreshSequenceHeader();
            long pts;
            sample->GetSampleTime(&pts);
            bool key = Mf.GetU32((IMFAttributes*)sample, PInvoke.MFSampleExtension_CleanPoint) != 0;
            IMFMediaBuffer* buf;
            sample->ConvertToContiguousBuffer(&buf).Check("ConvertToContiguousBuffer");
            try
            {
                byte* p; uint max, cur;
                buf->Lock(&p, &max, &cur).Check("Lock");
                var span = new ReadOnlySpan<byte>(p, (int)cur);
                if (!key) key = Nal.ContainsKeyframe(span, _cfg.Hevc);
                OnOutput?.Invoke(pts, key, span);
                buf->Unlock();
            }
            finally { buf->Release(); }
            Interlocked.Increment(ref FramesOut);
        }
        finally { sample->Release(); }
    }

    private void RefreshSequenceHeader()
    {
        IMFMediaType* t;
        if (_mft->GetOutputCurrentType(0, &t).Failed) return;
        try
        {
            var blob = Mf.GetBlob((IMFAttributes*)t, PInvoke.MF_MT_MPEG_SEQUENCE_HEADER);
            if (blob is { Length: > 0 }) SequenceHeader = blob;
        }
        finally { t->Release(); }
    }

    public void Dispose()
    {
        _stop = true;
        try
        {
            lock (_lock)
            {
                _credits = 0;
                while (_pending.Count > 0) ((IMFSample*)_pending.Dequeue())->Release();
                _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
                _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            }
            IMFShutdown* sd = ComUtil.TryQuery<IMFShutdown>(_mft);
            if (sd != null) { sd->Shutdown(); sd->Release(); }
        }
        catch { }
        _thread?.Join(2000);
        ComUtil.Release(ref _gen);
        if (_alloc != null) { _alloc->UninitializeSampleAllocator(); ComUtil.Release(ref _alloc); }
        ComUtil.Release(ref _inputType);
        ComUtil.Release(ref _mft);
    }
}
