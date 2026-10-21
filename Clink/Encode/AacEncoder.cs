using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Clink.Native;

namespace Clink.Encode;

/// <summary>Microsoft AAC-LC encoder MFT (synchronous). 16-bit PCM in, raw AAC frames out.</summary>
internal sealed unsafe class AacEncoder : IDisposable
{
    private static readonly Guid CLSID_AACMFTEncoder = new("93AF0C51-2275-45D2-A35B-F2BA21CAED00");

    private IMFTransform* _mft;
    private uint _outSize;
    public int SampleRate { get; }
    public int Channels { get; }
    public int BytesPerSecond { get; }
    public byte[] AudioSpecificConfig { get; private set; } = new byte[] { 0x11, 0x90 };
    public const int FrameSamples = 1024;

    public AacEncoder(int sampleRate = 48000, int channels = 2, int bytesPerSecond = 24000)
    {
        Mf.EnsureStarted();
        SampleRate = sampleRate;
        Channels = channels;
        BytesPerSecond = bytesPerSecond;

        Guid clsid = CLSID_AACMFTEncoder;
        Guid iid = IMFTransform.IID_Guid;
        void* p;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &p).Check("CoCreateInstance(AAC encoder)");
        _mft = (IMFTransform*)p;

        IMFMediaType* inType = Mf.CreateMediaType();
        IMFMediaType* outType = Mf.CreateMediaType();
        try
        {
            var a = (IMFAttributes*)inType;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_PCM);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(2 * channels));
            Mf.SetU32(a, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(2 * channels * sampleRate));
            Mf.SetU32(a, PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

            var o = (IMFAttributes*)outType;
            Mf.SetGuid(o, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            Mf.SetGuid(o, PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_AAC);
            Mf.SetU32(o, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            Mf.SetU32(o, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            Mf.SetU32(o, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            Mf.SetU32(o, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)bytesPerSecond);
            Mf.SetU32(o, PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0);
            Mf.SetU32(o, PInvoke.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);

            HRESULT hr = _mft->SetInputType(0, inType, 0);
            if (hr.Succeeded) hr = _mft->SetOutputType(0, outType, 0);
            if (hr.Failed)
            {
                _mft->SetOutputType(0, outType, 0).Check("AAC SetOutputType");
                _mft->SetInputType(0, inType, 0).Check("AAC SetInputType");
            }
        }
        finally { inType->Release(); outType->Release(); }

        IMFMediaType* cur;
        if (_mft->GetOutputCurrentType(0, &cur).Succeeded)
        {
            try
            {
                var ud = Mf.GetBlob((IMFAttributes*)cur, PInvoke.MF_MT_USER_DATA);
                if (ud != null && ud.Length > 12) AudioSpecificConfig = ud.AsSpan(12).ToArray();
            }
            finally { cur->Release(); }
        }

        MFT_OUTPUT_STREAM_INFO info;
        _mft->GetOutputStreamInfo(0, &info).Check("AAC GetOutputStreamInfo");
        _outSize = info.cbSize > 0 ? info.cbSize : 8192;

        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    /// <summary>Feeds interleaved 16-bit PCM and emits any complete AAC frames (with the encoder's own timestamps).</summary>
    public void Encode(ReadOnlySpan<byte> pcm16, long pts100ns, Action<long, byte[]> onFrame)
    {
        long dur = (long)pcm16.Length / (2 * Channels) * 10_000_000L / SampleRate;
        IMFSample* input = Mf.CreateMemorySample(pcm16, pts100ns, dur);
        try
        {
            HRESULT hr = _mft->ProcessInput(0, input, 0);
            if (hr.Failed) { Log.Warn($"AAC ProcessInput failed 0x{(uint)hr.Value:X8}"); return; }
        }
        finally { input->Release(); }
        Drain(onFrame);
    }

    private void Drain(Action<long, byte[]> onFrame)
    {
        while (true)
        {
            IMFMediaBuffer* buf;
            PInvoke.MFCreateMemoryBuffer(_outSize, &buf).Check("MFCreateMemoryBuffer");
            IMFSample* s;
            PInvoke.MFCreateSample(&s).Check("MFCreateSample");
            s->AddBuffer(buf);
            buf->Release();
            MFT_OUTPUT_DATA_BUFFER ob = default;
            ob.pSample = s;
            uint status;
            HRESULT hr = _mft->ProcessOutput(0, 1, &ob, &status);
            if (ob.pEvents != null) ((IUnknown*)ob.pEvents)->Release();
            if (hr.Failed) { s->Release(); break; }
            long t;
            s->GetSampleTime(&t);
            byte[] data = Mf.ReadSampleBytes(s);
            s->Release();
            if (data.Length > 0) onFrame(t, data);
        }
    }

    public void Dispose()
    {
        ComUtil.Release(ref _mft);
    }
}
