using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Media.MediaFoundation;
using Clink.Encode;
using Clink.Engine;
using Clink.Native;

namespace Clink.Services;

/// <summary>One video codec choice for the UI.</summary>
/// <param name="Id">"h264" | "hevc" | "av1" (the Settings.Codec value).</param>
/// <param name="Name">"H.264" | "HEVC" | "AV1".</param>
/// <param name="Description">One line for the UI (from the spec).</param>
/// <param name="Supported">A hardware encoder MFT exists for it.</param>
/// <param name="EncoderName">Friendly name of the MFT that would be used, when supported.</param>
/// <param name="Reason">Why it is not supported ("not supported by this GPU"), when it is not.</param>
internal sealed record CodecInfo(string Id, string Name, string Description, bool Supported, string? EncoderName, string? Reason)
{
    public bool Experimental => Id == "av1";
}

/// <summary>
/// What this machine can do (hardware encoders, RAM) and the size arithmetic the Capture tab shows: per-minute / per-clip
/// estimates, Discord size targets, ring-buffer RAM. Codecs are probed once, lazily, through MFTEnumEx.
/// </summary>
internal static unsafe class Capabilities
{
    public const string NotSupportedReason = "not supported by this GPU";
    /// <summary>Each enabled audio track is AAC at this rate.</summary>
    public const int AudioKbps = 192;
    /// <summary>Bitrate range a size target may pick (the engine runs anything ≥ 0.1 Mbps; below 0.5 the picture is unusable).</summary>
    public const double MinTargetMbps = 0.5, MaxTargetMbps = 200;
    /// <summary>Share of a size limit the clip may use: the rest covers container overhead and rate-control overshoot.</summary>
    public const double TargetHeadroom = 0.95;

    private static readonly (string id, string name, string description)[] s_known =
    {
        ("h264", "H.264", "plays everywhere, incl. Discord inline and every browser; biggest files."),
        ("hevc", "HEVC", "~40 % smaller at the same quality; Discord/Chrome may not play it inline, fine for local/archive."),
        ("av1", "AV1", "smallest; needs RTX 40 / RX 7000 / Arc to encode and a recent player. Experimental."),
    };

    private static IReadOnlyList<CodecInfo>? s_codecs;
    private static readonly object s_gate = new();
    private static MonitorInfo? s_monitor;
    private static nint s_probedMonitor;

    /// <summary>
    /// H.264, HEVC, AV1 in that order. Probed on first use (a few ms, Media Foundation is started if needed) for the adapter that
    /// drives the captured monitor (<see cref="PreferMonitor"/>, or AppController's selected monitor), which is the encoder the
    /// engine will use on a multi-GPU machine; any adapter counts when that one has no encoder. Re-probed when the monitor changes.
    /// </summary>
    public static IReadOnlyList<CodecInfo> Codecs
    {
        get
        {
            lock (s_gate)
            {
                MonitorInfo? mon = s_monitor;
                if (mon == null) { try { mon = AppController.Instance?.SelectedMonitor; } catch { } }
                nint handle = mon?.Handle ?? 0;
                if (s_codecs == null || handle != s_probedMonitor)
                {
                    s_codecs = Probe(mon);
                    s_probedMonitor = handle;
                }
                return s_codecs;
            }
        }
    }

    /// <summary>Probe the encoders of the adapter that drives this monitor first; null = whatever AppController captures. Changing it re-probes.</summary>
    public static void PreferMonitor(MonitorInfo? monitor)
    {
        lock (s_gate) s_monitor = monitor;
    }

    public static CodecInfo? Codec(string? id)
    {
        string n = EngineConfig.NormalizeCodec(id);
        return Codecs.FirstOrDefault(c => c.Id == n);
    }

    public static bool IsSupported(string? id) => Codec(id)?.Supported ?? false;

    /// <summary>The codec to actually record with: the chosen one when its encoder exists, otherwise the first supported one (h264 as a last resort).</summary>
    public static string EffectiveCodec(string? id)
    {
        string n = EngineConfig.NormalizeCodec(id);
        if (IsSupported(n)) return n;
        return Codecs.FirstOrDefault(c => c.Supported)?.Id ?? "h264";
    }

    private static IReadOnlyList<CodecInfo> Probe(MonitorInfo? monitor)
    {
        LUID luid = default; bool haveLuid = false; string adapter = "any adapter";
        if (monitor != null)
        {
            try { haveLuid = TryGetAdapterLuid(monitor.Handle, out luid, out adapter); }
            catch (Exception ex) { Log.Warn("Codec probe: adapter lookup: " + ex.Message); }
        }
        var list = new List<CodecInfo>(s_known.Length);
        foreach (var (id, name, description) in s_known)
        {
            string? encoder = null, reason = null;
            try
            {
                Mf.EnsureStarted();
                encoder = FirstHardwareEncoder(VideoEncoder.SubtypeFor(id), haveLuid ? luid : null);
                if (encoder == null) reason = NotSupportedReason;
            }
            catch (Exception ex)
            {
                reason = NotSupportedReason;
                Log.Warn($"Codec probe {name}: {ex.Message}");
            }
            list.Add(new CodecInfo(id, name, description, encoder != null, encoder, reason));
            Log.Info($"Codec {name} ({adapter}): {(encoder != null ? encoder : reason)}{(id == "av1" && encoder != null ? " (experimental)" : "")}");
        }
        return list;
    }

    /// <summary>LUID and name of the DXGI adapter whose output is this monitor (what <c>D3DDevice.CreateForMonitor</c> picks).</summary>
    private static bool TryGetAdapterLuid(nint hmonitor, out LUID luid, out string name)
    {
        luid = default; name = "";
        Guid iidFactory = IDXGIFactory1.IID_Guid;
        void* pf;
        PInvoke.CreateDXGIFactory1(&iidFactory, &pf).Check("CreateDXGIFactory1");
        var factory = (IDXGIFactory1*)pf;
        try
        {
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1* adapter;
                if (factory->EnumAdapters1(a, &adapter).Failed) break;
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IDXGIOutput* output;
                        if (adapter->EnumOutputs(o, &output).Failed) break;
                        DXGI_OUTPUT_DESC od;
                        output->GetDesc(&od);
                        output->Release();
                        if ((nint)od.Monitor.Value != hmonitor) continue;
                        DXGI_ADAPTER_DESC1 ad;
                        adapter->GetDesc1(&ad);
                        luid = ad.AdapterLuid;
                        name = ad.Description.ToString();
                        return true;
                    }
                }
                finally { adapter->Release(); }
            }
            return false;
        }
        finally { factory->Release(); }
    }

    /// <summary>
    /// Friendly name of the first hardware encoder MFT that takes NV12 and produces <paramref name="outSub"/>: on the given adapter
    /// when one is passed and has one (MFTEnum2 + MFT_ENUM_ADAPTER_LUID, as the engine does), otherwise on any adapter.
    /// </summary>
    private static string? FirstHardwareEncoder(Guid outSub, LUID? adapterLuid)
    {
        var inInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_NV12 };
        var outInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = outSub };
        var flags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;
        IMFActivate** list = null;
        uint count = 0;
        if (adapterLuid is LUID luid)
        {
            IMFAttributes* enumAttrs = Mf.CreateAttributes(1);
            try
            {
                Mf.SetBlob(enumAttrs, PInvoke.MFT_ENUM_ADAPTER_LUID, new ReadOnlySpan<byte>(&luid, sizeof(LUID)));
                HRESULT hr = PInvoke.MFTEnum2(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, &inInfo, &outInfo, enumAttrs, &list, &count);
                if (hr.Failed || count == 0)
                {
                    if (list != null) { PInvoke.CoTaskMemFree(list); list = null; }
                    count = 0;
                }
            }
            finally { enumAttrs->Release(); }
        }
        if (list == null)
            PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, &inInfo, &outInfo, &list, &count).Check("MFTEnumEx");
        if (list == null) return null;
        try
        {
            if (count == 0) return null;
            PWSTR s; uint len;
            Guid key = PInvoke.MFT_FRIENDLY_NAME_Attribute;
            string name = "Hardware encoder";
            if (((IMFAttributes*)list[0])->GetAllocatedString(&key, &s, &len).Succeeded)
            {
                name = s.ToString();
                PInvoke.CoTaskMemFree(s.Value);
            }
            return name;
        }
        finally
        {
            for (uint i = 0; i < count; i++) list[i]->Release();
            PInvoke.CoTaskMemFree(list);
        }
    }

    // ------------------------------------------------------------------ size arithmetic

    /// <summary>File size of a clip: (video bitrate + audio tracks × 192 kbps) × seconds / 8.</summary>
    public static long EstimateBytes(double bitrateMbps, int audioTracks, double seconds)
    {
        double bps = Math.Max(0, bitrateMbps) * 1_000_000 + Math.Max(0, audioTracks) * AudioKbps * 1000.0;
        return (long)Math.Round(bps * Math.Max(0, seconds) / 8);
    }

    /// <summary>The video bitrate at which a clip of <paramref name="seconds"/> with <paramref name="audioTracks"/> comes out at <paramref name="targetBytes"/> (may be ≤ 0 when the audio alone is bigger).</summary>
    public static double BitrateMbpsFor(long targetBytes, int audioTracks, double seconds)
    {
        if (seconds <= 0) return 0;
        double videoBps = targetBytes * 8.0 / seconds - Math.Max(0, audioTracks) * AudioKbps * 1000.0;
        return videoBps / 1_000_000;
    }

    /// <summary>RAM the replay buffer takes for this length and bitrate (the engine's own formula, before the RAM clamp).</summary>
    public static long RingBytesFor(double bitrateMbps, int seconds) => RingStore.BytesFor(bitrateMbps, seconds);

    /// <summary>Replay length a ring of this many bytes holds at the bitrate (inverse of <see cref="RingBytesFor"/>).</summary>
    public static int RingSecondsFor(double bitrateMbps, long ringBytes) => RingStore.SecondsFor(bitrateMbps, ringBytes);

    /// <summary>Installed physical RAM.</summary>
    public static long PhysicalRamBytes() => RingStore.PhysicalRamBytes();

    /// <summary>The most the ring may take (half of physical RAM); the engine clamps to this and the UI warns above it.</summary>
    public static long MaxRingBytes() => RingStore.MaxBytes();

    /// <summary>Byte limit of a size target, or 0 for "none" / unknown. Discord limits are decimal MB (10 MB = 10 000 000 bytes), the safe reading.</summary>
    public static long SizeTargetBytes(string? target) => (target ?? "").Trim().ToLowerInvariant() switch
    {
        "discord10" => 10_000_000,
        "discord50" => 50_000_000,
        "discord500" => 500_000_000,
        _ => 0,
    };

    public static string SizeTargetName(string? target) => (target ?? "").Trim().ToLowerInvariant() switch
    {
        "discord10" => "Discord (10 MB)",
        "discord50" => "Discord Nitro Basic (50 MB)",
        "discord500" => "Discord Nitro (500 MB)",
        _ => "None",
    };

    /// <summary>
    /// The bitrate to record with. SizeTarget "none": the manual Settings.BitrateMbps and no note. Otherwise the largest bitrate
    /// (0.5–200 Mbps, one decimal) at which a clip of <paramref name="seconds"/> with <paramref name="audioTracks"/> stays under the
    /// limit (with 5 % headroom), and a one-line note for the read-only display. When even the minimum does not fit the note says so.
    /// </summary>
    /// <summary>
    /// The bitrate to encode at: the chosen one, lowered (never raised) when a size target is set so that a clip of
    /// <paramref name="seconds"/> fits the limit. The note says what happened, or is null when nothing was changed.
    /// </summary>
    public static (double bitrateMbps, string? note) ResolveBitrate(Settings s, int audioTracks, int seconds)
    {
        double chosen = Math.Max(MinTargetMbps, s.BitrateMbps);
        long limit = SizeTargetBytes(s.SizeTarget);
        if (limit <= 0) return (chosen, null);
        seconds = Math.Max(1, seconds);
        double cap = BitrateMbpsFor((long)(limit * TargetHeadroom), audioTracks, seconds);
        string len = FormatDuration(seconds), lim = FormatBytes(limit);
        if (cap < MinTargetMbps)
            return (MinTargetMbps, $"a {len} clip does not fit in {lim} even at {MinTargetMbps:0.#} Mbps; shorten the replay length");
        cap = Math.Min(MaxTargetMbps, Math.Floor(cap * 10) / 10);
        if (cap >= chosen) return (chosen, $"a {len} clip at {chosen:0.#} Mbps fits {lim}");
        return (cap, $"lowered to {cap:0.#} Mbps so a {len} clip fits {lim}");
    }

    /// <summary>"820 kB", "9.5 MB", "190 MB", "1.2 GB" (decimal units, like the Discord limits).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1_000_000) return $"{bytes / 1_000.0:0} kB";
        if (bytes < 10_000_000) return $"{bytes / 1_000_000.0:0.#} MB";
        if (bytes < 1_000_000_000) return $"{bytes / 1_000_000.0:0} MB";
        if (bytes < 10_000_000_000) return $"{bytes / 1_000_000_000.0:0.#} GB";
        return $"{bytes / 1_000_000_000.0:0} GB";
    }

    /// <summary>"15 s", "1:30", "10:00", "1 h", "1:05:00".</summary>
    public static string FormatDuration(int seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60) return $"{seconds} s";
        if (seconds < 3600) return $"{seconds / 60}:{seconds % 60:00}";
        if (seconds % 3600 == 0) return $"{seconds / 3600} h";
        return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    /// <summary>"≈ 190 MB per minute · ≈ 380 MB per 2:00 clip" (plus the 1 h figure when the length is an hour or more).</summary>
    public static string DescribeEstimate(double bitrateMbps, int audioTracks, int clipSeconds)
    {
        string perMinute = FormatBytes(EstimateBytes(bitrateMbps, audioTracks, 60));
        string perClip = FormatBytes(EstimateBytes(bitrateMbps, audioTracks, clipSeconds));
        string s = $"≈ {perMinute} per minute · ≈ {perClip} per {FormatDuration(clipSeconds)} clip";
        if (clipSeconds >= 3600 && clipSeconds != 3600) s += $" · ≈ {FormatBytes(EstimateBytes(bitrateMbps, audioTracks, 3600))} per hour";
        return s;
    }

    /// <summary>"≈ 450 MB of RAM" and whether that is over half of the machine's RAM (the UI turns red and warns).</summary>
    public static (string text, bool tooMuch) DescribeRing(double bitrateMbps, int seconds)
    {
        long bytes = RingBytesFor(bitrateMbps, seconds);
        bool tooMuch = bytes > MaxRingBytes();
        string text = $"≈ {FormatBytes(bytes)} of RAM for {FormatDuration(seconds)} at {bitrateMbps:0.#} Mbps";
        if (tooMuch) text += $" (more than half of the {FormatBytes(PhysicalRamBytes())} installed; the buffer will be shortened)";
        return (text, tooMuch);
    }
}
