using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Audio;

/// <param name="Problem">null for an active endpoint; otherwise why it cannot be used right now ("not connected", "disabled").</param>
internal sealed record AudioDeviceInfo(string Id, string Name, string? Problem = null);

/// <summary>Base for WASAPI capture sources that deliver 48 kHz stereo float packets with QPC timestamps.</summary>
internal abstract unsafe class WasapiSource : IDisposable
{
    public const int SampleRate = 48000;
    public delegate void PacketHandler(float* stereo, int frames, long qpc100ns, bool silent);

    protected IAudioClient* Client;
    protected IAudioCaptureClient* Capture;
    private bool _running;
    // Pinned: Convert hands a pointer into this buffer to the packet handler, which allocates (AAC frames) and so can trigger a GC.
    private float[] _conv = GC.AllocateArray<float>(4096 * 2, pinned: true);
    private int _srcChannels = 2;
    private bool _srcFloat = true;
    private int _srcBits = 32;
    private int _srcRate = SampleRate;
    private int _failures;
    private DateTime _nextRetry = DateTime.MinValue;

    public abstract string Name { get; }
    /// <summary>What is actually being captured, for the status line: the chosen device's name, the process; null when there is nothing to add to the track name.</summary>
    public string? Detail { get; protected set; }
    public bool IsRunning => _running;
    public string? LastError { get; protected set; }

    protected abstract IAudioClient* Activate();
    protected virtual uint ExtraStreamFlags => 0;
    protected virtual bool AllowAutoConvert => true;
    /// <summary>
    /// Longest wait between start attempts. A failed start on an absent endpoint costs well under a millisecond, so a re-plugged
    /// device (USB/Bluetooth headset, Wave Link restarting its virtual devices) is picked up within a few seconds.
    /// </summary>
    protected virtual int MaxRetrySeconds => 5;
    /// <summary>Called from <see cref="Pump"/> while running: true when the stream no longer records what it should (the default device changed).</summary>
    protected virtual bool IsStale() => false;
    private bool IsLoopback => (ExtraStreamFlags & PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK) != 0;

    /// <summary>Starts the source if it is not running; failures are rate limited.</summary>
    public bool EnsureStarted()
    {
        if (_running) return true;
        if (DateTime.UtcNow < _nextRetry) return false;
        try
        {
            Start();
            _failures = 0;
            return true;
        }
        catch (Exception ex)
        {
            LastError = Explain(ex.HResult) ?? ex.Message;
            _failures++;
            _nextRetry = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(MaxRetrySeconds, 2 * _failures));
            if (_failures <= 2) Log.Warn($"Audio source '{Name}' failed to start: {ex.Message}");
            Stop();
            return false;
        }
    }

    /// <summary>What a failure means in plain words for the status line ("○ Mic (not connected or disabled)"), or null when there is no better text than the raw error.</summary>
    private string? Explain(int hr) => (uint)hr switch
    {
        0x88890004 or 0x80070490 => "not connected or disabled",                   // AUDCLNT_E_DEVICE_INVALIDATED, E_NOTFOUND
        0x8889000A => "in use by another app (exclusive mode)",                       // AUDCLNT_E_DEVICE_IN_USE
        0x80070005 => IsLoopback ? "access denied"
            : "blocked: allow microphone access for desktop apps in Windows privacy settings",   // E_ACCESSDENIED
        0x88890003 => IsLoopback ? "not an output device" : "not an input device", // AUDCLNT_E_WRONG_ENDPOINT_TYPE
        0x88890010 => "Windows Audio service not running",                            // AUDCLNT_E_SERVICE_NOT_RUNNING
        _ => null,
    };

    private void Start()
    {
        Client = Activate();
        if (Client == null) throw new InvalidOperationException("no audio client");

        var fmt = MakeFloatStereo48k();
        uint flags = ExtraStreamFlags;
        if (AllowAutoConvert) flags |= PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        HRESULT hr = Client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000, 0, (WAVEFORMATEX*)&fmt, null);
        if (hr.Succeeded)
        {
            _srcChannels = 2; _srcFloat = true; _srcBits = 32; _srcRate = SampleRate;
        }
        else
        {
            // Fall back to the device's mix format and convert ourselves.
            ComUtil.Release(ref Client);
            Client = Activate();
            WAVEFORMATEX* mix;
            Client->GetMixFormat(&mix).Check("GetMixFormat");
            try
            {
                ReadFormat(mix);
                Client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, ExtraStreamFlags, 2_000_000, 0, mix, null).Check($"IAudioClient.Initialize ({Name})");
            }
            finally { PInvoke.CoTaskMemFree(mix); }
            if (_srcRate != SampleRate) throw new NotSupportedException($"Device sample rate {_srcRate} Hz not supported without auto-conversion");
        }
        Guid iid = IAudioCaptureClient.IID_Guid;
        void* pc;
        Client->GetService(&iid, &pc).Check("GetService(IAudioCaptureClient)");
        Capture = (IAudioCaptureClient*)pc;
        Client->Start().Check("IAudioClient.Start");
        _running = true;
        Log.Info($"Audio source '{Name}' started ({_srcChannels}ch {(_srcFloat ? "float" : _srcBits + "-bit")} {_srcRate} Hz)");
    }

    private void ReadFormat(WAVEFORMATEX* f)
    {
        _srcChannels = f->nChannels;
        _srcBits = f->wBitsPerSample;
        _srcRate = (int)f->nSamplesPerSec;
        if (f->wFormatTag == (ushort)PInvoke.WAVE_FORMAT_EXTENSIBLE && f->cbSize >= 22)
        {
            var ext = (WAVEFORMATEXTENSIBLE*)f;
            _srcFloat = ext->SubFormat == KnownGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        }
        else _srcFloat = f->wFormatTag == (ushort)PInvoke.WAVE_FORMAT_IEEE_FLOAT;
    }

    protected static WAVEFORMATEXTENSIBLE MakeFloatStereo48k()
    {
        var f = new WAVEFORMATEXTENSIBLE();
        f.Format.wFormatTag = (ushort)PInvoke.WAVE_FORMAT_EXTENSIBLE;
        f.Format.nChannels = 2;
        f.Format.nSamplesPerSec = SampleRate;
        f.Format.wBitsPerSample = 32;
        f.Format.nBlockAlign = 8;
        f.Format.nAvgBytesPerSec = SampleRate * 8;
        f.Format.cbSize = 22;
        f.Samples.wValidBitsPerSample = 32;
        f.dwChannelMask = 3;
        f.SubFormat = KnownGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        return f;
    }

    /// <summary>Drains all available packets. Returns false if the device went away (source is stopped).</summary>
    public bool Pump(PacketHandler handler)
    {
        if (!_running) return false;
        try
        {
            if (IsStale())
            {
                // Reopen right away (EnsureStarted on the next loop pass); the track fills the few ms in between with silence.
                Stop();
                _nextRetry = DateTime.MinValue;
                return false;
            }
            while (true)
            {
                uint packet;
                HRESULT hr = Capture->GetNextPacketSize(&packet);
                if (hr.Failed) { Fail(hr); return false; }
                if (packet == 0) return true;
                byte* data; uint frames, flags; ulong devPos, qpc;
                hr = Capture->GetBuffer(&data, &frames, &flags, &devPos, &qpc);
                if (hr.Failed) { Fail(hr); return false; }
                if (frames == 0) { Capture->ReleaseBuffer(0); return true; }
                bool silent = (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                long ts = (long)qpc;
                if (ts <= 0) ts = ComUtil.QpcNow100ns() - frames * 10_000_000L / SampleRate;
                float* stereo = Convert(data, (int)frames, silent);
                handler(stereo, (int)frames, ts, silent);
                Capture->ReleaseBuffer(frames);
            }
        }
        catch (Exception ex)
        {
            LastError = Explain(ex.HResult) ?? ex.Message;
            Log.Warn($"Audio source '{Name}' error: {ex.Message}");
            Stop();
            return false;
        }
    }

    private void Fail(HRESULT hr)
    {
        LastError = Explain(hr.Value) ?? $"0x{(uint)hr.Value:X8}";
        if (hr == HRESULT.AUDCLNT_E_DEVICE_INVALIDATED) Log.Info($"Audio source '{Name}': device invalidated, will retry");
        else Log.Warn($"Audio source '{Name}' capture failed 0x{(uint)hr.Value:X8}");
        Stop();
        _nextRetry = DateTime.UtcNow + TimeSpan.FromSeconds(2);
    }

    private float* Convert(byte* data, int frames, bool silent)
    {
        if (_conv.Length < frames * 2) _conv = GC.AllocateArray<float>(frames * 2 * 2, pinned: true);
        fixed (float* dst = _conv)
        {
            if (silent) { new Span<float>(dst, frames * 2).Clear(); return dst; }
            if (_srcFloat && _srcChannels == 2 && _srcBits == 32)
            {
                Buffer.MemoryCopy(data, dst, frames * 8, frames * 8);
                return dst;
            }
            int ch = _srcChannels;
            for (int i = 0; i < frames; i++)
            {
                float l, r;
                if (_srcFloat)
                {
                    float* s = (float*)data + i * ch;
                    l = s[0]; r = ch > 1 ? s[1] : l;
                }
                else if (_srcBits == 16)
                {
                    short* s = (short*)data + i * ch;
                    l = s[0] / 32768f; r = ch > 1 ? s[1] / 32768f : l;
                }
                else if (_srcBits == 32)
                {
                    int* s = (int*)data + i * ch;
                    l = s[0] / 2147483648f; r = ch > 1 ? s[1] / 2147483648f : l;
                }
                else if (_srcBits == 24)
                {
                    byte* s = data + i * ch * 3;
                    int li = (s[0] << 8) | (s[1] << 16) | (s[2] << 24);
                    int ri = ch > 1 ? (s[3] << 8) | (s[4] << 16) | (s[5] << 24) : li;
                    l = li / 2147483648f; r = ri / 2147483648f;
                }
                else { l = r = 0; }
                dst[i * 2] = l; dst[i * 2 + 1] = r;
            }
            return dst;
        }
    }

    public void Stop()
    {
        _running = false;
        if (Client != null) { try { Client->Stop(); } catch { } }
        ComUtil.Release(ref Capture);
        ComUtil.Release(ref Client);
    }

    public virtual void Dispose() => Stop();

    // ------------------------------------------------------------------ device enumeration

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    protected static IMMDeviceEnumerator* CreateEnumerator()
    {
        Guid clsid = CLSID_MMDeviceEnumerator;
        Guid iid = IMMDeviceEnumerator.IID_Guid;
        void* p;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_ALL, &iid, &p).Check("CoCreateInstance(MMDeviceEnumerator)");
        return (IMMDeviceEnumerator*)p;
    }

    /// <summary>
    /// Render or capture endpoints. Active ones only by default; with <paramref name="includeInactive"/> also the unplugged and
    /// disabled ones Windows still knows (so a saved device that is not connected right now can be shown by name), flagged
    /// through <see cref="AudioDeviceInfo.Problem"/>.
    /// </summary>
    public static List<AudioDeviceInfo> ListDevices(bool capture, bool includeInactive = false)
    {
        var list = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator* en = null;
        IMMDeviceCollection* col = null;
        try
        {
            en = CreateEnumerator();
            var states = DEVICE_STATE.DEVICE_STATE_ACTIVE;
            if (includeInactive) states |= DEVICE_STATE.DEVICE_STATE_UNPLUGGED | DEVICE_STATE.DEVICE_STATE_DISABLED;
            en->EnumAudioEndpoints(capture ? EDataFlow.eCapture : EDataFlow.eRender, states, &col).Check("EnumAudioEndpoints");
            uint n;
            col->GetCount(&n);
            for (uint i = 0; i < n; i++)
            {
                IMMDevice* dev;
                if (col->Item(i, &dev).Failed) continue;
                try
                {
                    PWSTR id;
                    dev->GetId(&id);
                    string idStr = id.ToString();
                    PInvoke.CoTaskMemFree(id.Value);
                    var state = DEVICE_STATE.DEVICE_STATE_ACTIVE;
                    if (includeInactive && dev->GetState(out DEVICE_STATE st).Succeeded) state = st;
                    string? problem = state switch
                    {
                        DEVICE_STATE.DEVICE_STATE_ACTIVE => null,
                        DEVICE_STATE.DEVICE_STATE_DISABLED => "disabled",
                        _ => "not connected",
                    };
                    list.Add(new AudioDeviceInfo(idStr, FriendlyName(dev) ?? idStr, problem));
                }
                finally { dev->Release(); }
            }
        }
        catch (Exception ex) { Log.Warn("ListDevices: " + ex.Message); }
        finally { ComUtil.Release(ref col); ComUtil.Release(ref en); }
        return list;
    }

    /// <summary>The endpoint's name as Sound settings shows it ("Headset Earphone (Arctis Nova Pro)"), or null if it has none.</summary>
    protected static string? FriendlyName(IMMDevice* dev)
    {
        Windows.Win32.UI.Shell.PropertiesSystem.IPropertyStore* store;
        if (dev->OpenPropertyStore(STGM.STGM_READ, &store).Failed) return null;
        try
        {
            PROPVARIANT pv = default;
            PROPERTYKEY key = PInvoke.PKEY_Device_FriendlyName;
            string? name = store->GetValue(&key, &pv).Succeeded && pv.vt == VARENUM.VT_LPWSTR ? pv.pwszVal.ToString() : null;
            PInvoke.PropVariantClear(ref pv);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        finally { store->Release(); }
    }
}

/// <summary>System loopback (what you hear) or a microphone/line input.</summary>
internal sealed unsafe class EndpointSource : WasapiSource
{
    private readonly bool _loopback;
    private readonly string? _deviceId;
    private string? _defaultId;        // with no device chosen: the endpoint "default" resolved to when the stream was opened
    private long _nextDefaultCheck;    // Environment.TickCount64
    public override string Name { get; }

    public EndpointSource(bool loopback, string? deviceId, string name)
    {
        _loopback = loopback;
        _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        Name = name;
    }

    protected override uint ExtraStreamFlags => _loopback ? PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK : 0;
    private EDataFlow Flow => _loopback ? EDataFlow.eRender : EDataFlow.eCapture;

    protected override IAudioClient* Activate()
    {
        IMMDeviceEnumerator* en = CreateEnumerator();
        IMMDevice* dev = null;
        try
        {
            if (_deviceId != null)
            {
                fixed (char* id = _deviceId) en->GetDevice(new PCWSTR(id), &dev).Check("GetDevice");
            }
            else
            {
                en->GetDefaultAudioEndpoint(Flow, ERole.eConsole, &dev).Check("GetDefaultAudioEndpoint");
                _defaultId = EndpointId(dev);
            }
            Detail = FriendlyName(dev);   // confirms in the status line which device the pick (or the Windows default) resolved to
            Guid iid = IAudioClient.IID_Guid;
            void* p;
            dev->Activate(&iid, CLSCTX.CLSCTX_ALL, null, &p).Check("IMMDevice.Activate(IAudioClient)");
            return (IAudioClient*)p;
        }
        finally { ComUtil.Release(ref dev); en->Release(); }
    }

    /// <summary>
    /// "Default" follows Windows: a stream keeps recording the endpoint it was opened on when the default moves (volume flyout,
    /// a headset connecting), so every 2 s the current default is compared with the one in use and the source reopens on a change.
    /// </summary>
    protected override bool IsStale()
    {
        if (_deviceId != null || _defaultId == null) return false;
        long now = Environment.TickCount64;
        if (now < _nextDefaultCheck) return false;
        _nextDefaultCheck = now + 2000;
        IMMDeviceEnumerator* en = null;
        IMMDevice* dev = null;
        try
        {
            en = CreateEnumerator();
            // No default at all right now: keep the stream; losing its device is reported by the stream itself.
            if (en->GetDefaultAudioEndpoint(Flow, ERole.eConsole, &dev).Failed) return false;
            string? id = EndpointId(dev);
            if (id == null || id == _defaultId) return false;
            Log.Info($"Audio source '{Name}': default {(_loopback ? "output" : "input")} is now {FriendlyName(dev) ?? id}, reopening");
            return true;
        }
        catch { return false; }
        finally { ComUtil.Release(ref dev); ComUtil.Release(ref en); }
    }

    private static string? EndpointId(IMMDevice* dev)
    {
        PWSTR id;
        if (dev->GetId(&id).Failed) return null;
        string s = id.ToString();
        PInvoke.CoTaskMemFree(id.Value);
        return s;
    }
}

/// <summary>Audio of a single process tree (include) or everything except it (exclude). Windows 10 2004+.</summary>
internal sealed unsafe class ProcessSource : WasapiSource
{
    public uint ProcessId { get; }
    public bool Include { get; }
    public override string Name { get; }

    public ProcessSource(uint pid, bool include, string name, string? detail = null)
    {
        ProcessId = pid;
        Include = include;
        Name = name;
        Detail = detail;
    }

    protected override uint ExtraStreamFlags => PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK;
    protected override bool AllowAutoConvert => false;
    // Activation can wait up to 3 s on the audio thread, and a new game/voice pid gets a new source anyway: keep the long backoff.
    protected override int MaxRetrySeconds => 30;

    private sealed class Handler : IActivateAudioInterfaceCompletionHandler.Interface
    {
        public readonly ManualResetEventSlim Done = new();
        public HRESULT ActivateCompleted(IActivateAudioInterfaceAsyncOperation* op) { Done.Set(); return default; }
    }

    protected override IAudioClient* Activate()
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        };
        p.ProcessLoopbackParams.TargetProcessId = ProcessId;
        p.ProcessLoopbackParams.ProcessLoopbackMode = Include
            ? PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            : PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;

        PROPVARIANT pv = default;
        pv.vt = VARENUM.VT_BLOB;
        pv.blob.cbSize = (uint)sizeof(AUDIOCLIENT_ACTIVATION_PARAMS);
        pv.blob.pBlobData = (byte*)&p;

        var handler = new Handler();
        Log.Info($"Process loopback: activating for pid {ProcessId} ({(Include ? "include" : "exclude")})");
        nint identity = ClinkComWrappers.Instance.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
        // The returned pointer is the IUnknown identity; the API must get the completion-handler vtable.
        Guid handlerIid = IActivateAudioInterfaceCompletionHandler.IID_Guid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(identity, in handlerIid, out nint ccw));
        Marshal.Release(identity);
        IActivateAudioInterfaceAsyncOperation* op = null;
        try
        {
            Guid iid = IAudioClient.IID_Guid;
            PInvoke.ActivateAudioInterfaceAsync(PInvoke.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, in iid, pv,
                (IActivateAudioInterfaceCompletionHandler*)ccw, &op).Check("ActivateAudioInterfaceAsync");
            Log.Info("Process loopback: async call returned, waiting for completion");
            if (!handler.Done.Wait(3000)) throw new TimeoutException("Process loopback activation timed out");
            HRESULT result; IUnknown* unk;
            op->GetActivateResult(&result, &unk).Check("GetActivateResult");
            result.Check("Process loopback activation");
            Log.Info("Process loopback: activated");
            return (IAudioClient*)unk;
        }
        finally
        {
            ComUtil.Release(ref op);
            Marshal.Release(ccw);
        }
    }
}
