using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Clink.Native;

namespace Clink.Playback;

/// <summary>
/// Shared-mode WASAPI render of float stereo 48 kHz on the default output device. <see cref="Write"/> blocks until the
/// endpoint buffer has room, which is what paces the player's mixing loop; <see cref="PlayedSeconds"/> is the audio clock.
/// </summary>
internal sealed unsafe class AudioOutput : IDisposable
{
    public const int SampleRate = 48000;
    private const int FrameBytes = 8; // 2 channels x float

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly object _gate = new();
    private IAudioClient* _client;
    private IAudioRenderClient* _render;
    private readonly uint _bufferFrames;
    private long _framesSubmitted;
    private bool _started;
    private bool _disposed;

    /// <summary>Frames the endpoint buffer can hold.</summary>
    public int BufferFrames => (int)_bufferFrames;
    public bool IsStarted { get { lock (_gate) return _started; } }

    public AudioOutput()
    {
        IMMDeviceEnumerator* en = null;
        IMMDevice* dev = null;
        try
        {
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iidEn = IMMDeviceEnumerator.IID_Guid;
            void* pEn;
            PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_ALL, &iidEn, &pEn).Check("CoCreateInstance(MMDeviceEnumerator)");
            en = (IMMDeviceEnumerator*)pEn;
            en->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &dev).Check("GetDefaultAudioEndpoint(eRender)");

            Guid iidClient = IAudioClient.IID_Guid;
            void* pClient;
            dev->Activate(&iidClient, CLSCTX.CLSCTX_ALL, null, &pClient).Check("IMMDevice.Activate(IAudioClient)");
            _client = (IAudioClient*)pClient;

            var fmt = MakeFloatStereo48k();
            uint flags = PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            _client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000, 0, (WAVEFORMATEX*)&fmt, null).Check("IAudioClient.Initialize (render)");

            uint frames;
            _client->GetBufferSize(&frames).Check("IAudioClient.GetBufferSize");
            _bufferFrames = frames;

            Guid iidRender = IAudioRenderClient.IID_Guid;
            void* pRender;
            _client->GetService(&iidRender, &pRender).Check("GetService(IAudioRenderClient)");
            _render = (IAudioRenderClient*)pRender;
            Log.Info($"Audio output ready ({_bufferFrames} frame buffer)");
        }
        catch
        {
            ComUtil.Release(ref _render);
            ComUtil.Release(ref _client);
            throw;
        }
        finally
        {
            ComUtil.Release(ref dev);
            ComUtil.Release(ref en);
        }
    }

    private static WAVEFORMATEXTENSIBLE MakeFloatStereo48k()
    {
        var f = new WAVEFORMATEXTENSIBLE();
        f.Format.wFormatTag = (ushort)PInvoke.WAVE_FORMAT_EXTENSIBLE;
        f.Format.nChannels = 2;
        f.Format.nSamplesPerSec = SampleRate;
        f.Format.wBitsPerSample = 32;
        f.Format.nBlockAlign = FrameBytes;
        f.Format.nAvgBytesPerSec = SampleRate * FrameBytes;
        f.Format.cbSize = 22;
        f.Samples.wValidBitsPerSample = 32;
        f.dwChannelMask = 3;
        f.SubFormat = KnownGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        return f;
    }

    /// <summary>
    /// Queues interleaved stereo float frames, blocking (in short sleeps) until the endpoint buffer has room for all of them.
    /// Returns early (dropping the rest) if the output is stopped while waiting, so a paused stream never deadlocks the caller.
    /// </summary>
    public void Write(ReadOnlySpan<float> interleavedStereo)
    {
        int remaining = interleavedStereo.Length / 2;
        int offset = 0;
        int idleSpins = 0;
        while (remaining > 0)
        {
            int written;
            lock (_gate)
            {
                if (_disposed) return;
                uint padding;
                if (_client->GetCurrentPadding(&padding).Failed) return;
                int room = (int)_bufferFrames - (int)padding;
                if (room <= 0)
                {
                    // Nothing drains while stopped: give up instead of spinning forever (the caller re-sends after a seek anyway).
                    if (!_started && ++idleSpins > 50) return;
                    written = 0;
                }
                else
                {
                    int n = Math.Min(room, remaining);
                    byte* data;
                    if (_render->GetBuffer((uint)n, &data).Failed) return;
                    interleavedStereo.Slice(offset * 2, n * 2).CopyTo(new Span<float>(data, n * 2));
                    _render->ReleaseBuffer((uint)n, 0);
                    _framesSubmitted += n;
                    written = n;
                }
            }
            if (written == 0) { Thread.Sleep(3); continue; }
            idleSpins = 0;
            offset += written;
            remaining -= written;
        }
    }

    /// <summary>Frames queued but not yet played (what <see cref="PlayedSeconds"/> subtracts).</summary>
    public int PendingFrames
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return 0;
                uint padding;
                return _client->GetCurrentPadding(&padding).Succeeded ? (int)padding : 0;
            }
        }
    }

    /// <summary>Seconds of audio actually rendered since the last <see cref="Reset"/>.</summary>
    public double PlayedSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return 0;
                uint padding = 0;
                _client->GetCurrentPadding(&padding);
                long played = _framesSubmitted - padding;
                return played <= 0 ? 0 : played / (double)SampleRate;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            HRESULT hr = _client->Start();
            if (hr.Failed) { Log.Warn($"Audio output start failed 0x{(uint)hr.Value:X8}"); return; }
            _started = true;
            PInvoke.timeBeginPeriod(1); // make the short pacing sleeps in Write accurate while rendering
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_disposed || !_started) return;
        _client->Stop();
        _started = false;
        PInvoke.timeEndPeriod(1);
    }

    /// <summary>Stops (if needed), drops everything queued and zeroes the clock.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked();
            _client->Reset();
            _framesSubmitted = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { StopLocked(); } catch { }
            _disposed = true;
            ComUtil.Release(ref _render);
            ComUtil.Release(ref _client);
        }
    }
}
