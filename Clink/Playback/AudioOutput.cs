using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Clink.Native;

namespace Clink.Playback;

/// <summary>
/// Shared-mode WASAPI render of float stereo 48 kHz on the default output device. <see cref="Write"/> blocks until the
/// endpoint buffer has drained below a fill level, which is what paces the player's mixing loop; <see cref="PlayedSeconds"/>
/// is the audio clock. A device failure (unplugged, disabled, invalidated) latches <see cref="Failed"/>; the owner then
/// drops the output and carries on with its own clock instead of spinning on calls that never block.
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
    private readonly double _periodSec;      // the engine drains the client buffer once per period
    private readonly double _latencySec;     // engine -> endpoint delay the device reports
    private long _framesSubmitted;
    private long _lastPlayed, _lastPlayedTs; // padding-derived play position and the timestamp it last moved
    private double _stoppedSec;              // clock value frozen while stopped
    private bool _started;
    private volatile bool _failed;
    private bool _disposed;

    /// <summary>Frames the endpoint buffer can hold.</summary>
    public int BufferFrames => (int)_bufferFrames;
    /// <summary>
    /// Frames worth keeping queued: enough to ride out the player's per-iteration video work, short enough that a mixer
    /// change (mute, volume) is heard a few tens of milliseconds after it is made rather than a whole device buffer later.
    /// </summary>
    public int TargetFrames { get; }
    public bool IsStarted { get { lock (_gate) return _started; } }
    /// <summary>True once a device call failed (device removed or invalidated); nothing will play on this instance again.</summary>
    public bool Failed => _failed;

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

            long defPeriod = 0, minPeriod = 0;
            _periodSec = _client->GetDevicePeriod(&defPeriod, &minPeriod).Succeeded && defPeriod > 0 ? defPeriod / 10_000_000.0 : 0.01;
            long latency = 0;
            _latencySec = _client->GetStreamLatency(&latency).Succeeded ? Math.Clamp(latency / 10_000_000.0, 0, 0.05) : 0;
            TargetFrames = (int)Math.Min(_bufferFrames, Math.Max(SampleRate * 80 / 1000, (long)(4 * _periodSec * SampleRate)));

            Guid iidRender = IAudioRenderClient.IID_Guid;
            void* pRender;
            _client->GetService(&iidRender, &pRender).Check("GetService(IAudioRenderClient)");
            _render = (IAudioRenderClient*)pRender;
            Log.Info($"Audio output ready ({_bufferFrames} frame buffer, {_periodSec * 1000:0.#} ms period, {_latencySec * 1000:0.#} ms latency, {TargetFrames} frames queued)");
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
    /// Queues interleaved stereo float frames, blocking (in short sleeps) until the endpoint buffer has drained enough for
    /// all of them to fit under <paramref name="fillLimit"/> queued frames (the whole buffer when 0). Returns early (dropping
    /// the rest) if the output is stopped while waiting, so a paused stream never deadlocks the caller. Returns false once
    /// the device has failed; nothing is written then and the caller should stop using this instance.
    /// </summary>
    public bool Write(ReadOnlySpan<float> interleavedStereo, int fillLimit = 0)
    {
        int limit = fillLimit <= 0 ? (int)_bufferFrames : Math.Min(fillLimit, (int)_bufferFrames);
        int remaining = interleavedStereo.Length / 2;
        int offset = 0;
        while (remaining > 0)
        {
            int written;
            lock (_gate)
            {
                if (_disposed || _failed) return !_failed;
                uint padding;
                HRESULT hr = _client->GetCurrentPadding(&padding);
                if (hr.Failed) { FailLocked(hr, "GetCurrentPadding"); return false; }
                int room = limit - (int)padding;
                if (room <= 0)
                {
                    // Nothing drains while stopped: give up right away (the caller re-sends after a seek anyway). Waiting here
                    // would only hold up ClipPlayer.Close, which stops the stream to release a Write blocked on a full buffer.
                    if (!_started) return true;
                    written = 0;
                }
                else
                {
                    int n = Math.Min(room, remaining);
                    byte* data;
                    hr = _render->GetBuffer((uint)n, &data);
                    if (hr.Failed) { FailLocked(hr, "GetBuffer"); return false; }
                    interleavedStereo.Slice(offset * 2, n * 2).CopyTo(new Span<float>(data, n * 2));
                    _render->ReleaseBuffer((uint)n, 0);
                    _framesSubmitted += n;
                    written = n;
                }
            }
            if (written == 0) { Thread.Sleep(3); continue; }
            offset += written;
            remaining -= written;
        }
        return true;
    }

    /// <summary>Frames queued but not yet consumed by the engine (0 once the device has failed).</summary>
    public int PendingFrames
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || _failed) return 0;
                uint padding;
                HRESULT hr = _client->GetCurrentPadding(&padding);
                if (hr.Failed) { FailLocked(hr, "GetCurrentPadding"); return 0; }
                return (int)padding;
            }
        }
    }

    /// <summary>
    /// Seconds of audio heard since the last <see cref="Reset"/>. The engine only takes data out of the buffer once per
    /// period (10 ms by default), so the padding-derived position is interpolated between those steps, and the device's
    /// own output latency is subtracted so video is placed on the instant the sound actually leaves the endpoint.
    /// Frozen while stopped.
    /// </summary>
    public double PlayedSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || !_started) return _stoppedSec;
                return LiveSecondsLocked();
            }
        }
    }

    private double LiveSecondsLocked()
    {
        long now = Stopwatch.GetTimestamp();
        if (!_failed)
        {
            uint padding;
            HRESULT hr = _client->GetCurrentPadding(&padding);
            if (hr.Failed) FailLocked(hr, "GetCurrentPadding");
            else
            {
                long played = _framesSubmitted - padding;
                if (played != _lastPlayed) { _lastPlayed = played; _lastPlayedTs = now; }
            }
        }
        double sec = Math.Max(0, _lastPlayed) / (double)SampleRate;
        if (_lastPlayed > 0 && !_failed)
        {
            sec += Math.Min(_periodSec, (now - _lastPlayedTs) / (double)Stopwatch.Frequency);
            sec -= _latencySec;
        }
        return sec < 0 ? 0 : sec;
    }

    private void FailLocked(HRESULT hr, string what)
    {
        if (_failed) return;
        _failed = true;
        Log.Warn($"Audio output {what} failed 0x{(uint)hr.Value:X8}: the device is gone, the preview continues without sound");
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started || _failed) return;
            HRESULT hr = _client->Start();
            if (hr.Failed) { FailLocked(hr, "Start"); return; }
            _started = true;
            _lastPlayedTs = Stopwatch.GetTimestamp();
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
        _stoppedSec = LiveSecondsLocked();
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
            if (!_failed)
            {
                HRESULT hr = _client->Reset();
                if (hr.Failed) FailLocked(hr, "Reset");
            }
            _framesSubmitted = 0;
            _lastPlayed = 0;
            _lastPlayedTs = 0;
            _stoppedSec = 0;
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
