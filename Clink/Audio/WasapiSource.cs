using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Audio;

internal sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Base for WASAPI capture sources that deliver 48 kHz stereo float packets with QPC timestamps.</summary>
internal abstract unsafe class WasapiSource : IDisposable
{
    public const int SampleRate = 48000;
    public delegate void PacketHandler(float* stereo, int frames, long qpc100ns, bool silent);

    protected IAudioClient* Client;
    protected IAudioCaptureClient* Capture;
    private bool _running;
    private float[] _conv = new float[4096 * 2];
    private int _srcChannels = 2;
    private bool _srcFloat = true;
    private int _srcBits = 32;
    private int _srcRate = SampleRate;
    private int _failures;
    private DateTime _nextRetry = DateTime.MinValue;

    public abstract string Name { get; }
    public bool IsRunning => _running;
    public string? LastError { get; protected set; }

    protected abstract IAudioClient* Activate();
    protected virtual uint ExtraStreamFlags => 0;
    protected virtual bool AllowAutoConvert => true;

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
            LastError = ex.Message;
            _failures++;
            _nextRetry = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(30, 2 * _failures));
            if (_failures <= 2) Log.Warn($"Audio source '{Name}' failed to start: {ex.Message}");
            Stop();
            return false;
        }
    }

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
            LastError = ex.Message;
            Log.Warn($"Audio source '{Name}' error: {ex.Message}");
            Stop();
            return false;
        }
    }

    private void Fail(HRESULT hr)
    {
        LastError = $"0x{(uint)hr.Value:X8}";
        if (hr == HRESULT.AUDCLNT_E_DEVICE_INVALIDATED) Log.Info($"Audio source '{Name}': device invalidated, will retry");
        else Log.Warn($"Audio source '{Name}' capture failed {LastError}");
        Stop();
        _nextRetry = DateTime.UtcNow + TimeSpan.FromSeconds(2);
    }

    private float* Convert(byte* data, int frames, bool silent)
    {
        if (_conv.Length < frames * 2) _conv = new float[frames * 2 * 2];
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

    public static List<AudioDeviceInfo> ListDevices(bool capture)
    {
        var list = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator* en = null;
        IMMDeviceCollection* col = null;
        try
        {
            en = CreateEnumerator();
            en->EnumAudioEndpoints(capture ? EDataFlow.eCapture : EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, &col).Check("EnumAudioEndpoints");
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
                    string name = idStr;
                    Windows.Win32.UI.Shell.PropertiesSystem.IPropertyStore* store;
                    if (dev->OpenPropertyStore(STGM.STGM_READ, &store).Succeeded)
                    {
                        PROPVARIANT pv = default;
                        PROPERTYKEY key = PInvoke.PKEY_Device_FriendlyName;
                        if (store->GetValue(&key, &pv).Succeeded && pv.vt == VARENUM.VT_LPWSTR) name = pv.pwszVal.ToString();
                        PInvoke.PropVariantClear(ref pv);
                        store->Release();
                    }
                    list.Add(new AudioDeviceInfo(idStr, name));
                }
                finally { dev->Release(); }
            }
        }
        catch (Exception ex) { Log.Warn("ListDevices: " + ex.Message); }
        finally { ComUtil.Release(ref col); ComUtil.Release(ref en); }
        return list;
    }
}

/// <summary>System loopback (what you hear) or a microphone/line input.</summary>
internal sealed unsafe class EndpointSource : WasapiSource
{
    private readonly bool _loopback;
    private readonly string? _deviceId;
    public override string Name { get; }

    public EndpointSource(bool loopback, string? deviceId, string name)
    {
        _loopback = loopback;
        _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        Name = name;
    }

    protected override uint ExtraStreamFlags => _loopback ? PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK : 0;

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
                en->GetDefaultAudioEndpoint(_loopback ? EDataFlow.eRender : EDataFlow.eCapture, ERole.eConsole, &dev).Check("GetDefaultAudioEndpoint");
            }
            Guid iid = IAudioClient.IID_Guid;
            void* p;
            dev->Activate(&iid, CLSCTX.CLSCTX_ALL, null, &p).Check("IMMDevice.Activate(IAudioClient)");
            return (IAudioClient*)p;
        }
        finally { ComUtil.Release(ref dev); en->Release(); }
    }
}

/// <summary>Audio of a single process tree (include) or everything except it (exclude). Windows 10 2004+.</summary>
internal sealed unsafe class ProcessSource : WasapiSource
{
    public uint ProcessId { get; }
    public bool Include { get; }
    public override string Name { get; }

    public ProcessSource(uint pid, bool include, string name)
    {
        ProcessId = pid;
        Include = include;
        Name = name;
    }

    protected override uint ExtraStreamFlags => PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK;
    protected override bool AllowAutoConvert => false;

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
