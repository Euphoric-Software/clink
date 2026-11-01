using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Encode;

/// <param name="Codec">"h264" | "hevc" | "av1" (AV1 is experimental and needs a hardware AV1 encoder: RTX 40 / RX 7000 / Arc).</param>
internal sealed record VideoEncoderConfig(uint Width, uint Height, uint Fps, uint BitrateBps, string Codec, float GopSeconds)
{
    public VideoEncoderConfig(uint width, uint height, uint fps, uint bitrateBps, bool hevc, float gopSeconds)
        : this(width, height, fps, bitrateBps, hevc ? "hevc" : "h264", gopSeconds) { }
    public bool Hevc => Codec == "hevc";
    public bool Av1 => Codec == "av1";
    public string CodecName => Codec switch { "hevc" => "HEVC", "av1" => "AV1", _ => "H.264" };
}

/// <summary>
/// How a <see cref="VideoEncoder"/> takes its frames. <see cref="Live"/> (the default) is the recording's: a frame that arrives while four
/// are already waiting for the encoder drops the oldest, and the event thread runs AboveNormal. <see cref="Offline"/> is for a file
/// encode (the share-time transcode): <see cref="VideoEncoder.SubmitBlocking"/> waits for room instead of dropping,
/// <see cref="VideoEncoder.Drain"/> gets the last frames out, the event thread runs at the given priority and the encoder is set up
/// from <see cref="OfflineSettings"/> (not from the recording's settings, so a change to those never alters a share copy).
/// </summary>
/// <param name="OfflineSettings">Blocking only: the encoder settings (null = <see cref="OfflineEncoderSettings.Default"/>). Ignored by a live encoder.</param>
internal sealed record VideoEncoderOptions(bool Blocking, ThreadPriority EventThreadPriority, OfflineEncoderSettings? OfflineSettings = null)
{
    public static readonly VideoEncoderOptions Live = new(false, ThreadPriority.AboveNormal);
    public static VideoEncoderOptions Offline(ThreadPriority priority = ThreadPriority.BelowNormal, OfflineEncoderSettings? settings = null)
        => new(true, priority, settings ?? OfflineEncoderSettings.Default);
}

/// <summary>
/// What an offline encoder (a share copy) is set to, pinned here apart from the recording's own setup. The defaults are the values the
/// recording used when share copies were added: CBR (mean = max = the bitrate), no B-frames (the muxer writes pts as dts), low latency on
/// (the encoder hands each frame back as soon as it is done, which is what Drain and the frame accounting were verified with) and
/// quality-vs-speed 66.
/// </summary>
internal sealed record OfflineEncoderSettings(bool LowLatency = true, uint QualityVsSpeed = 66, uint BFrames = 0,
    eAVEncCommonRateControlMode RateControl = eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR)
{
    public static readonly OfflineEncoderSettings Default = new();
}

/// <summary>
/// Hardware H.264/HEVC (and experimental AV1) encoder through Media Foundation (NVENC / AMF / QuickSync MFT), fed with NV12
/// textures on the capture device so frames never leave the GPU. Async MFT: inputs are gated by NeedInput credits,
/// outputs are drained on a dedicated event thread. Output samples are Annex B access units for H.264/HEVC and
/// temporal units (low-overhead OBU format, every OBU with obu_size) for AV1.
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
    private readonly VideoEncoderOptions _options;
    private readonly ManualResetEventSlim _drained = new(false);
    private volatile int _errorStatus;   // the last MEError's status (0 = none): what an offline wait gives up on
    private OfflineEncoderSettings OfflineSettings => _options.OfflineSettings ?? OfflineEncoderSettings.Default;

    public string EncoderName { get; private set; } = "";
    public long FramesIn, FramesOut, Dropped;
    /// <summary>Offline only (always 0 for the recording): ProcessInput / ProcessOutput calls that failed, so a file encode can judge
    /// what it lost (ShareTranscoder.JudgeEncoderLoss).</summary>
    public long InputFailures, OutputFailures;
    public OutputHandler? OnOutput;
    /// <summary>H.264/HEVC: Annex B parameter sets (MF_MT_MPEG_SEQUENCE_HEADER). AV1: the Sequence Header OBU (from the attribute or the first keyframe).</summary>
    public byte[]? SequenceHeader { get; private set; }
    public string Codec => _cfg.Codec;
    public bool Hevc => _cfg.Hevc;
    public bool IsAv1 => _cfg.Av1;
    private bool _av1Warned;

    public static Guid SubtypeFor(string codec) => codec switch
    {
        "hevc" => PInvoke.MFVideoFormat_HEVC,
        "av1" => PInvoke.MFVideoFormat_AV1,
        _ => PInvoke.MFVideoFormat_H264,
    };

    /// <summary>Hardware H.264 encoders (NVENC, AMF, QuickSync) stop at 4096 px per side; a 5120x1440 super-ultrawide needs HEVC or AV1.</summary>
    public const uint MaxH264Side = 4096;

    /// <summary>Whether a hardware H.264 encoder can take this output size (0 = unknown counts as fitting).</summary>
    public static bool H264Fits(uint width, uint height) => width <= MaxH264Side && height <= MaxH264Side;

    /// <param name="options">Null is <see cref="VideoEncoderOptions.Live"/> (the recording).</param>
    public VideoEncoder(D3DDevice d3d, VideoEncoderConfig cfg, VideoEncoderOptions? options = null)
    {
        Mf.EnsureStarted();
        _d3d = d3d;
        _cfg = cfg;
        _options = options ?? VideoEncoderOptions.Live;
        _frameDuration = 10_000_000L / cfg.Fps;

        // The MFT would only fail SetOutputType with "The input type is not supported for D3D device"; say what is wrong instead.
        if (!cfg.Hevc && !cfg.Av1 && !H264Fits(cfg.Width, cfg.Height))
            throw new InvalidOperationException($"H.264 encoders stop at {MaxH264Side} px per side and the output is {cfg.Width}x{cfg.Height}; pick HEVC or a lower resolution.");

        Guid outSub = SubtypeFor(cfg.Codec);
        _mft = FindHardwareEncoder(outSub, out string name);
        EncoderName = name;
        Log.Info($"Video encoder: {name} ({cfg.CodecName}{(cfg.Av1 ? ", experimental" : "")}) {cfg.Width}x{cfg.Height}@{cfg.Fps} {cfg.BitrateBps / 1_000_000.0:0.#} Mbps");

        try { Open(cfg, outSub); }
        catch (Exception ex)
        {
            // The engine only disposes an encoder whose constructor returned: shut the half-configured MFT down here, or its
            // hardware session (NVENC counts them per GPU) and the allocator's textures stay open until Clink exits.
            Dispose();
            throw new InvalidOperationException($"Could not open {name} for {cfg.CodecName} {cfg.Width}x{cfg.Height}@{cfg.Fps}: {ex.Message}", ex);
        }
    }

    /// <summary>Configures the activated MFT (media types, rate control, sample allocator), starts streaming and the event thread.</summary>
    private void Open(VideoEncoderConfig cfg, Guid outSub)
    {
        IMFAttributes* attrs;
        _mft->GetAttributes(&attrs).Check("IMFTransform.GetAttributes");
        try
        {
            if (Mf.GetU32(attrs, PInvoke.MF_TRANSFORM_ASYNC) == 0) throw new InvalidOperationException("Encoder is not an async MFT");
            if (Mf.GetU32(attrs, PInvoke.MF_SA_D3D11_AWARE) == 0) throw new InvalidOperationException("Encoder is not D3D11-aware");
            Mf.SetU32(attrs, PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            // (An offline encoder takes low latency from its own settings; a live one always sets it, as before.)
            if (!_options.Blocking || OfflineSettings.LowLatency) { try { Mf.SetU32(attrs, PInvoke.MF_LOW_LATENCY, 1); } catch { } }
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
            Guid profileKey = PInvoke.MF_MT_MPEG2_PROFILE;
            Mf.SetU32(a, profileKey, cfg.Av1 ? (uint)eAVEncAV1VProfile.eAVEncAV1VProfile_Main_420_8
                : cfg.Hevc ? (uint)eAVEncH265VProfile.eAVEncH265VProfile_Main_420_8 : (uint)eAVEncH264VProfile.eAVEncH264VProfile_High);
            HRESULT hrOut = _mft->SetOutputType(0, outType, 0);
            if (hrOut.Failed && cfg.Av1)
            {
                // Experimental: an AV1 MFT that does not take the profile attribute gets the type without it (Main is its default anyway).
                Log.Warn($"AV1 (experimental): SetOutputType with profile Main failed 0x{(uint)hrOut.Value:X8}, retrying without a profile");
                a->DeleteItem(&profileKey);
                hrOut = _mft->SetOutputType(0, outType, 0);
            }
            hrOut.Check("SetOutputType");
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
        _inputType = inType;   // owned from here on, so Dispose releases it when SetInputType fails
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

        if (_options.Blocking) ConfigureOfflineCodecApi(OfflineSettings);
        else ConfigureCodecApi();   // the recording

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

        _thread = new Thread(EventLoop) { IsBackground = true, Name = "Clink.Encoder", Priority = _options.EventThreadPriority };
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
            throw new InvalidOperationException(_cfg.Av1
                ? $"No hardware AV1 encoder found on {_d3d.AdapterName}. AV1 needs an RTX 40 / RX 7000 / Arc GPU; pick H.264 or HEVC."
                : $"No hardware {_cfg.CodecName} encoder found on {_d3d.AdapterName}. Update your GPU driver.");

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

    /// <summary>
    /// An offline encoder's rate control, GOP, B-frames, latency and speed, from its own pinned settings (<see cref="OfflineEncoderSettings"/>):
    /// on purpose not shared with <see cref="ConfigureCodecApi"/>, so trying a setting on the recording cannot change a share copy.
    /// </summary>
    private void ConfigureOfflineCodecApi(OfflineEncoderSettings s)
    {
        ICodecAPI* codec = ComUtil.TryQuery<ICodecAPI>(_mft);
        if (codec == null) return;
        try
        {
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonRateControlMode, (uint)s.RateControl);
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonMeanBitRate, _cfg.BitrateBps);
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonMaxBitRate, _cfg.BitrateBps);
            SetU32(codec, KnownGuids.CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(1, Math.Round(_cfg.Fps * _cfg.GopSeconds)));
            SetU32(codec, KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount, s.BFrames);
            SetBool(codec, KnownGuids.CODECAPI_AVLowLatencyMode, s.LowLatency);
            SetU32(codec, KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, s.QualityVsSpeed);
        }
        finally { codec->Release(); }

        static void SetU32(ICodecAPI* c, Guid key, uint value)
        {
            if (c->IsSupported(&key).Failed) return;
            VARIANT v = default; v.vt = VARENUM.VT_UI4; v.ulVal = value;
            HRESULT hr = c->SetValue(&key, &v);
            if (hr.Failed) Log.Warn($"ICodecAPI (offline) {key} = {value} not accepted (0x{(uint)hr.Value:X8})");
        }
        static void SetBool(ICodecAPI* c, Guid key, bool value)
        {
            VARIANT v = default; v.vt = VARENUM.VT_BOOL; v.boolVal = new VARIANT_BOOL((short)(value ? -1 : 0));
            HRESULT hr = c->SetValue(&key, &v);
            if (hr.Failed) Log.Warn($"ICodecAPI (offline) {key} = {value} not accepted (0x{(uint)hr.Value:X8})");
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

    // ------------------------------------------------------------------ offline use (VideoEncoderOptions.Offline only)

    /// <summary>How long an offline wait for the encoder may take before it counts as stalled.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    private void RequireOffline(string what)
    {
        if (!_options.Blocking) throw new InvalidOperationException($"VideoEncoder.{what} is for an offline encoder (VideoEncoderOptions.Offline)");
    }

    private void ThrowIfFailed()
    {
        int status = _errorStatus;
        if (status != 0) throw new COMException($"The encoder reported an error 0x{(uint)status:X8}", status);
        if (_stop) throw new ObjectDisposedException(nameof(VideoEncoder));
    }

    /// <summary>
    /// Offline: a free input sample from the pool, waiting (instead of failing) while the encoder still holds every one of them. The
    /// caller owns the sample and the texture reference (release the texture, hand the sample to <see cref="SubmitBlocking"/>).
    /// </summary>
    public IMFSample* AcquireInputBlocking(out ID3D11Texture2D* texture, out uint subresource, CancellationToken ct)
    {
        RequireOffline(nameof(AcquireInputBlocking));
        var since = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfFailed();
            if (TryAcquireInput(out IMFSample* sample, out texture, out subresource)) return sample;
            if (since.Elapsed > StallTimeout) throw new TimeoutException($"The encoder kept every input frame for {StallTimeout.TotalSeconds:0} s");
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Offline: hands a filled sample to the encoder like <see cref="Submit"/> (always consumes the caller's reference), but when four
    /// frames are already waiting it waits for the encoder to take one instead of dropping the oldest. Throws on cancellation, on an
    /// encoder error and when the encoder takes nothing for <see cref="StallTimeout"/>.
    /// </summary>
    public void SubmitBlocking(IMFSample* sample, long pts100ns, CancellationToken ct)
    {
        RequireOffline(nameof(SubmitBlocking));
        sample->SetSampleTime(pts100ns);
        sample->SetSampleDuration(_frameDuration);
        bool handed = false;
        try
        {
            var since = System.Diagnostics.Stopwatch.StartNew();
            lock (_lock)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    ThrowIfFailed();
                    if (_credits > 0) { handed = true; FeedLocked(sample); return; }
                    if (_pending.Count < MaxPending) { handed = true; _pending.Enqueue((nint)sample); return; }
                    if (since.Elapsed > StallTimeout) throw new TimeoutException($"The encoder took no frame for {StallTimeout.TotalSeconds:0} s");
                    Monitor.Wait(_lock, 20);
                }
            }
        }
        finally
        {
            if (!handed) sample->Release();
        }
    }

    /// <summary>
    /// Offline: the end of the input. Waits until every waiting frame is in the encoder, then sends END_OF_STREAM and COMMAND_DRAIN and
    /// waits for METransformDrainComplete, so every frame submitted has come out through <see cref="OnOutput"/> when this returns.
    /// If no drain-complete event comes for <see cref="StallTimeout"/> but FramesOut has reached FramesIn, it logs that and returns
    /// normally; it throws only when frames are still owed. Nothing may be submitted afterwards.
    /// </summary>
    public void Drain(CancellationToken ct)
    {
        RequireOffline(nameof(Drain));
        var since = System.Diagnostics.Stopwatch.StartNew();
        lock (_lock)
        {
            while (_pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                ThrowIfFailed();
                if (since.Elapsed > StallTimeout) throw new TimeoutException($"The encoder took no frame for {StallTimeout.TotalSeconds:0} s");
                Monitor.Wait(_lock, 20);
            }
            _drained.Reset();
            _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0).Check("NOTIFY_END_OF_STREAM");
            _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0).Check("COMMAND_DRAIN");
        }
        long lastOut = Interlocked.Read(ref FramesOut);
        since.Restart();
        while (!_drained.Wait(50, ct))
        {
            ThrowIfFailed();
            long now = Interlocked.Read(ref FramesOut);
            if (now != lastOut) { lastOut = now; since.Restart(); }
            if (since.Elapsed <= StallTimeout) continue;
            // Some drivers never send METransformDrainComplete. When every frame that went in has come out, nothing is missing, so
            // that is not a failure (the caller checks the counts again). Only a drain that still owes frames fails.
            long framesIn = Interlocked.Read(ref FramesIn);
            if (now >= framesIn)
            {
                Log.Warn($"{EncoderName}: no drain-complete event after {StallTimeout.TotalSeconds:0} s, but all {framesIn} frames came out; treating the drain as done");
                return;
            }
            throw new TimeoutException($"The encoder did not finish draining in {StallTimeout.TotalSeconds:0} s ({now} of {framesIn} frames out)");
        }
    }

    private void FeedLocked(IMFSample* sample)
    {
        HRESULT hr = _mft->ProcessInput(0, sample, 0);
        if (hr.Succeeded) { _credits--; Interlocked.Increment(ref FramesIn); }
        else { Log.Warn($"ProcessInput failed 0x{(uint)hr.Value:X8}"); Interlocked.Increment(ref Dropped); }
        if (hr.Failed && _options.Blocking) Interlocked.Increment(ref InputFailures);   // offline only: counted for the share copy's loss check
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
                            if (_options.Blocking) Monitor.PulseAll(_lock);   // an offline submit or drain may be waiting for room
                        }
                        break;
                    case Mf.METransformHaveOutput:
                        DrainOutput();
                        break;
                    case Mf.METransformDrainComplete:
                        // Only an offline Drain asks for one; the recording never sends COMMAND_DRAIN, so it never gets here.
                        _drained.Set();
                        break;
                    case Mf.MEError:
                        HRESULT status;
                        evt->GetStatus(&status);
                        Log.Error($"Encoder reported error 0x{(uint)status.Value:X8}");
                        if (_options.Blocking) _errorStatus = status.Value == 0 ? unchecked((int)0x80004005) : status.Value;
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
        if (hr.Failed && _options.Blocking) Interlocked.Increment(ref OutputFailures);   // offline only: counted for the share copy's loss check
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
                if (_cfg.Av1) key = InspectAv1(span, key);
                else if (!key) key = Nal.ContainsKeyframe(span, _cfg.Hevc);
                OnOutput?.Invoke(pts, key, span);
                buf->Unlock();
            }
            finally { buf->Release(); }
            Interlocked.Increment(ref FramesOut);
        }
        finally { sample->Release(); }
    }

    /// <summary>
    /// AV1 (experimental): checks that the output is a temporal unit in the low-overhead OBU format (parses every OBU header and
    /// obu_size; a malformed unit is logged once), remembers the Sequence Header OBU of keyframes, and derives the keyframe flag from
    /// the frame header when the MFT did not set MFSampleExtension_CleanPoint.
    /// </summary>
    private bool InspectAv1(ReadOnlySpan<byte> tu, bool cleanPoint)
    {
        var obus = new List<Av1.Obu>();
        if (!Av1.TryParse(tu, obus, out string? err))
        {
            if (!_av1Warned) { _av1Warned = true; Log.Warn($"AV1 (experimental): encoder output is not a valid OBU sequence: {err}; storing it as-is"); }
            return cleanPoint;
        }
        if (!_av1Warned)
        {
            foreach (var o in obus)
            {
                if (o.HasSize) continue;
                _av1Warned = true;
                Log.Warn("AV1 (experimental): encoder emits OBUs without obu_size (not low-overhead format); samples are rewritten with size fields");
                break;
            }
        }
        bool key = Av1.ContainsKeyframe(tu);
        if (key || SequenceHeader == null)
        {
            var sh = Av1.FindSequenceHeader(tu);
            if (sh != null && (SequenceHeader == null || !sh.AsSpan().SequenceEqual(SequenceHeader)))
            {
                SequenceHeader = sh;
                try
                {
                    var hdr = new List<Av1.Obu>();
                    Av1.TryParse(sh, hdr, out _);
                    Log.Info($"AV1 (experimental): sequence header {Av1.Describe(Av1.ParseSequenceHeader(sh.AsSpan(hdr[0].PayloadOffset, hdr[0].PayloadLength)))}, {obus.Count} OBUs in first temporal unit");
                }
                catch (Exception ex) { Log.Warn("AV1 (experimental): sequence header parse: " + ex.Message); }
            }
        }
        return key || cleanPoint;
    }

    private void RefreshSequenceHeader()
    {
        IMFMediaType* t;
        if (_mft->GetOutputCurrentType(0, &t).Failed) return;
        try
        {
            var blob = Mf.GetBlob((IMFAttributes*)t, PInvoke.MF_MT_MPEG_SEQUENCE_HEADER);
            if (blob is not { Length: > 0 }) return;
            if (_cfg.Av1)
            {
                // Only trust the attribute when it really is a Sequence Header OBU; otherwise the first keyframe provides it.
                var sh = Av1.FindSequenceHeader(blob);
                if (sh != null) SequenceHeader = sh;
                else Log.Info($"AV1 (experimental): MF_MT_MPEG_SEQUENCE_HEADER ({blob.Length} bytes) is not a Sequence Header OBU; using the in-band one");
            }
            else SequenceHeader = blob;
        }
        finally { t->Release(); }
    }

    public void Dispose()
    {
        if (_mft == null) return;   // already disposed (also by a failed constructor)
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
