using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Clink.Engine;
using Clink.Native;

namespace Clink.Encode;

/// <summary>
/// A tiny CPU H.264 encoder over Media Foundation's software encoder MFT (synchronous, NV12 in from memory, Annex B out): only for the
/// documentation sample clips (<c>--dev-make-samples</c>), which have no GPU capture to encode. It produces the same kind of
/// <see cref="VideoSample"/> list the recording engine hands to <see cref="ClipWriter"/>. One keyframe per <c>gopSeconds</c>, no B-frames.
/// Call from an MTA thread (like every Media Foundation user in Clink).
/// </summary>
internal sealed unsafe class SoftwareH264Encoder : IDisposable
{
    private IMFTransform* _mft;
    private readonly long _frameDuration;
    private readonly int _frameBytes;
    private uint _outputSize;
    private bool _providesSamples;
    private readonly List<VideoSample> _samples = new();

    /// <summary>The encoder's parameter sets (Annex B), for the <c>sequenceHeader</c> of <c>ClipWriter.Write</c>; null until known.</summary>
    public byte[]? SequenceHeader { get; private set; }
    public string EncoderName { get; private set; } = "";

    public SoftwareH264Encoder(int width, int height, uint fps, uint bitrateBps, double gopSeconds = 1.0)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("the size must be even and positive");
        Mf.EnsureStarted();
        _frameDuration = 10_000_000L / fps;
        _frameBytes = width * height * 3 / 2;

        var inInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 };
        var outInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 };
        IMFActivate** list = null;
        uint count = 0;
        PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
            &inInfo, &outInfo, &list, &count).Check("MFTEnumEx");
        if (count == 0 || list == null) throw new InvalidOperationException("No software H.264 encoder found.");
        try
        {
            Guid iid = IMFTransform.IID_Guid;
            void* p;
            list[0]->ActivateObject(&iid, &p).Check("ActivateObject");
            _mft = (IMFTransform*)p;
            EncoderName = "Media Foundation software H.264 encoder";
        }
        finally
        {
            for (uint i = 0; i < count; i++) list[i]->Release();
            PInvoke.CoTaskMemFree(list);
        }

        try { Open(width, height, fps, bitrateBps, gopSeconds); }
        catch { Dispose(); throw; }
    }

    private void Open(int width, int height, uint fps, uint bitrateBps, double gopSeconds)
    {
        IMFMediaType* outType = Mf.CreateMediaType();
        try
        {
            var a = (IMFAttributes*)outType;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
            Mf.SetU32(a, PInvoke.MF_MT_AVG_BITRATE, bitrateBps);
            Mf.SetSize(a, PInvoke.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            Mf.SetRatio(a, PInvoke.MF_MT_FRAME_RATE, fps, 1);
            Mf.SetU32(a, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            Mf.SetRatio(a, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.SetU32(a, PInvoke.MF_MT_MPEG2_PROFILE, (uint)eAVEncH264VProfile.eAVEncH264VProfile_Main);
            _mft->SetOutputType(0, outType, 0).Check("SetOutputType");
        }
        finally { outType->Release(); }

        IMFMediaType* inType = Mf.CreateMediaType();
        try
        {
            var a = (IMFAttributes*)inType;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
            Mf.SetSize(a, PInvoke.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            Mf.SetRatio(a, PInvoke.MF_MT_FRAME_RATE, fps, 1);
            Mf.SetU32(a, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            Mf.SetRatio(a, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            Mf.SetU32(a, PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MFNominalRange.MFNominalRange_16_235);
            Mf.SetU32(a, PInvoke.MF_MT_YUV_MATRIX, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709);
            Mf.SetU32(a, PInvoke.MF_MT_VIDEO_PRIMARIES, (uint)MFVideoPrimaries.MFVideoPrimaries_BT709);
            Mf.SetU32(a, PInvoke.MF_MT_TRANSFER_FUNCTION, (uint)MFVideoTransferFunction.MFVideoTransFunc_709);
            _mft->SetInputType(0, inType, 0).Check("SetInputType(NV12)");
        }
        finally { inType->Release(); }

        ICodecAPI* codec = ComUtil.TryQuery<ICodecAPI>(_mft);
        if (codec != null)
        {
            try
            {
                SetU32(codec, KnownGuids.CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(1, Math.Round(fps * gopSeconds)));
                SetU32(codec, KnownGuids.CODECAPI_AVEncMPVDefaultBPictureCount, 0);
                SetU32(codec, KnownGuids.CODECAPI_AVEncCommonQualityVsSpeed, 100);   // best quality: it is a few seconds of video, not a live capture
                SetBool(codec, KnownGuids.CODECAPI_AVLowLatencyMode, true);
            }
            finally { codec->Release(); }
        }

        MFT_OUTPUT_STREAM_INFO info;
        _mft->GetOutputStreamInfo(0, &info).Check("GetOutputStreamInfo");
        _providesSamples = (info.dwFlags & (uint)(_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | _MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outputSize = info.cbSize > 0 ? info.cbSize : 4u << 20;

        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0).Check("NOTIFY_BEGIN_STREAMING");
        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0).Check("NOTIFY_START_OF_STREAM");
        RefreshSequenceHeader();

        static void SetU32(ICodecAPI* c, Guid key, uint value)
        {
            if (c->IsSupported(&key).Failed) return;
            VARIANT v = default; v.vt = VARENUM.VT_UI4; v.ulVal = value;
            HRESULT hr = c->SetValue(&key, &v);
            if (hr.Failed) Log.Warn($"ICodecAPI {key} = {value} not accepted (0x{(uint)hr.Value:X8})");
        }
        static void SetBool(ICodecAPI* c, Guid key, bool value)
        {
            if (c->IsSupported(&key).Failed) return;
            VARIANT v = default; v.vt = VARENUM.VT_BOOL; v.boolVal = new VARIANT_BOOL((short)(value ? -1 : 0));
            HRESULT hr = c->SetValue(&key, &v);
            if (hr.Failed) Log.Warn($"ICodecAPI {key} = {value} not accepted (0x{(uint)hr.Value:X8})");
        }
    }

    /// <summary>Encodes one NV12 frame (width*height*3/2 bytes) shown at <paramref name="pts100ns"/>, and collects what the encoder has ready.</summary>
    public void Encode(ReadOnlySpan<byte> nv12, long pts100ns)
    {
        if (nv12.Length != _frameBytes) throw new ArgumentException($"an NV12 frame is {_frameBytes} bytes, got {nv12.Length}");
        IMFSample* sample = Mf.CreateMemorySample(nv12, pts100ns, _frameDuration);
        try
        {
            HRESULT hr = _mft->ProcessInput(0, sample, 0);
            if (hr == HRESULT.MF_E_NOTACCEPTING)
            {
                Drain();
                hr = _mft->ProcessInput(0, sample, 0);
            }
            hr.Check("ProcessInput");
        }
        finally { sample->Release(); }
        Drain();
    }

    /// <summary>Flushes the encoder and returns every sample, in order, the first one a keyframe.</summary>
    public List<VideoSample> Finish()
    {
        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0);
        Drain();
        if (SequenceHeader == null) RefreshSequenceHeader();
        return _samples;
    }

    private const int MaxFormatChanges = 3;                      // MF_E_TRANSFORM_STREAM_CHANGE answers per Drain before giving up
    private const uint OutputNoSample = 0x300;                     // MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE in dwStatus

    private void Drain()
    {
        int formatChanges = 0;
        while (true)
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
            if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // The output type changed: take the first type the encoder offers now, set it, and ask again (a bounded number of times).
                if (ob.pSample != null) ob.pSample->Release();
                if (++formatChanges > MaxFormatChanges) throw new InvalidOperationException($"the software H.264 encoder changed its output type more than {MaxFormatChanges} times in a row");
                IMFMediaType* offered;
                _mft->GetOutputAvailableType(0, 0, &offered).Check("GetOutputAvailableType");
                try { _mft->SetOutputType(0, offered, 0).Check("SetOutputType (after a stream change)"); }
                finally { offered->Release(); }
                // the buffer size and who allocates the samples may have changed with the type
                MFT_OUTPUT_STREAM_INFO changed;
                _mft->GetOutputStreamInfo(0, &changed).Check("GetOutputStreamInfo (after a stream change)");
                _providesSamples = (changed.dwFlags & (uint)(_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | _MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
                _outputSize = changed.cbSize > 0 ? changed.cbSize : 4u << 20;
                RefreshSequenceHeader();
                continue;
            }
            if (hr.Failed) { if (ob.pSample != null) ob.pSample->Release(); hr.Check("ProcessOutput"); return; }
            IMFSample* sample = ob.pSample;
            if (sample == null) return;
            try
            {
                if ((ob.dwStatus & OutputNoSample) == OutputNoSample) continue;   // the encoder said there is no sample after all
                if (SequenceHeader == null) RefreshSequenceHeader();
                long pts;
                if (sample->GetSampleTime(&pts).Failed) continue;
                byte[] data = Mf.ReadSampleBytes(sample);
                if (data.Length == 0) continue;
                bool key = Mf.GetU32((IMFAttributes*)sample, PInvoke.MFSampleExtension_CleanPoint) != 0 || Nal.ContainsKeyframe(data, hevc: false);
                _samples.Add(new VideoSample { Pts = pts, Keyframe = key, Data = data });
            }
            finally { sample->Release(); }
        }
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
        if (_mft == null) return;
        try
        {
            _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        }
        catch { }
        ComUtil.Release(ref _mft);
    }
}
