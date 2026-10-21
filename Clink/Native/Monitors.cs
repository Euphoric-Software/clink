using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;

namespace Clink.Native;

internal sealed record MonitorInfo(
    nint Handle,
    string DeviceName,      // \\.\DISPLAY1
    string FriendlyName,    // e.g. "AW2726DM"
    int Left, int Top, int Width, int Height,
    bool IsPrimary,
    bool IsHdr,
    float SdrWhiteNits,     // SDR white level reported by Windows (80 nits * level/1000)
    float MaxLuminance,
    string AdapterName)
{
    public string Label => $"{(string.IsNullOrWhiteSpace(FriendlyName) ? DeviceName.Replace(@"\\.\", "") : FriendlyName)} · {Width}×{Height}{(IsHdr ? " · HDR" : "")}";
    public string ShortName => DeviceName.Replace(@"\\.\", "");
}

internal static unsafe class Monitors
{
    [ThreadStatic] private static List<nint>? t_handles;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static BOOL EnumProc(HMONITOR monitor, HDC hdc, RECT* rect, LPARAM lparam)
    {
        t_handles!.Add((nint)monitor.Value);
        return true;
    }

    public static List<MonitorInfo> Enumerate()
    {
        t_handles = new List<nint>();
        PInvoke.EnumDisplayMonitors(HDC.Null, null, &EnumProc, default);
        var handles = t_handles;
        t_handles = null;

        var dxgi = QueryDxgiOutputs();
        var cfg = QueryDisplayConfig();

        var list = new List<MonitorInfo>();
        foreach (var h in handles)
        {
            MONITORINFOEXW mi = default;
            mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!PInvoke.GetMonitorInfo(new HMONITOR((void*)h), (MONITORINFO*)&mi)) continue;
            string device = mi.szDevice.ToString();
            var rc = mi.monitorInfo.rcMonitor;
            bool primary = (mi.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0;

            bool hdr = false; float maxLum = 0; string adapter = "";
            if (dxgi.TryGetValue(h, out var d))
            {
                hdr = d.ColorSpace == DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                maxLum = d.MaxLuminance;
                adapter = d.Adapter;
            }
            string friendly = ""; float sdrWhite = 80f; bool advColor = false;
            if (cfg.TryGetValue(device, out var c))
            {
                friendly = c.Friendly;
                sdrWhite = c.SdrWhiteNits;
                advColor = c.AdvancedColorEnabled;
            }
            // DXGI's colour space is authoritative; advanced-color is a hint when DXGI is unavailable.
            if (!dxgi.ContainsKey(h)) hdr = advColor;

            list.Add(new MonitorInfo(h, device, friendly, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, primary, hdr, sdrWhite, maxLum, adapter));
        }
        // Stable order: primary first, then by position.
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Left).ThenBy(m => m.Top).ToList();
    }

    private readonly record struct DxgiInfo(DXGI_COLOR_SPACE_TYPE ColorSpace, float MaxLuminance, string Adapter);

    private static Dictionary<nint, DxgiInfo> QueryDxgiOutputs()
    {
        var result = new Dictionary<nint, DxgiInfo>();
        IDXGIFactory1* factory = null;
        try
        {
            Guid iid = IDXGIFactory1.IID_Guid;
            void* pf;
            if (PInvoke.CreateDXGIFactory1(&iid, &pf).Failed) return result;
            factory = (IDXGIFactory1*)pf;
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1* adapter;
                if (factory->EnumAdapters1(a, &adapter).Failed) break;
                DXGI_ADAPTER_DESC1 ad;
                adapter->GetDesc1(&ad);
                string adapterName = ad.Description.ToString();
                for (uint o = 0; ; o++)
                {
                    IDXGIOutput* output;
                    if (adapter->EnumOutputs(o, &output).Failed) break;
                    IDXGIOutput6* out6 = ComUtil.TryQuery<IDXGIOutput6>(output);
                    if (out6 != null)
                    {
                        DXGI_OUTPUT_DESC1 d1;
                        if (out6->GetDesc1(&d1).Succeeded)
                            result[(nint)d1.Monitor.Value] = new DxgiInfo(d1.ColorSpace, d1.MaxLuminance, adapterName);
                        ComUtil.Release(ref out6);
                    }
                    else
                    {
                        DXGI_OUTPUT_DESC d0;
                        if (output->GetDesc(&d0).Succeeded)
                            result[(nint)d0.Monitor.Value] = new DxgiInfo(DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, 0, adapterName);
                    }
                    ComUtil.Release(ref output);
                }
                ComUtil.Release(ref adapter);
            }
        }
        catch { /* best effort */ }
        finally { ComUtil.Release(ref factory); }
        return result;
    }

    private readonly record struct CfgInfo(string Friendly, float SdrWhiteNits, bool AdvancedColorEnabled);

    private static Dictionary<string, CfgInfo> QueryDisplayConfig()
    {
        var result = new Dictionary<string, CfgInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
            if (PInvoke.GetDisplayConfigBufferSizes(flags, out uint numPaths, out uint numModes) != WIN32_ERROR.ERROR_SUCCESS) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            if (PInvoke.QueryDisplayConfig(flags, ref numPaths, paths, ref numModes, modes) != WIN32_ERROR.ERROR_SUCCESS) return result;

            for (int i = 0; i < numPaths; i++)
            {
                ref var p = ref paths[i];

                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                src.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                src.header.adapterId = p.sourceInfo.adapterId;
                src.header.id = p.sourceInfo.id;
                if (PInvoke.DisplayConfigGetDeviceInfo(ref src.header) != 0) continue;
                string gdiName = src.viewGdiDeviceName.ToString();

                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                tgt.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                tgt.header.adapterId = p.targetInfo.adapterId;
                tgt.header.id = p.targetInfo.id;
                string friendly = PInvoke.DisplayConfigGetDeviceInfo(ref tgt.header) == 0 ? tgt.monitorFriendlyDeviceName.ToString() : "";

                var sdr = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                sdr.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                sdr.header.size = (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL);
                sdr.header.adapterId = p.targetInfo.adapterId;
                sdr.header.id = p.targetInfo.id;
                float sdrNits = 80f;
                if (PInvoke.DisplayConfigGetDeviceInfo(ref sdr.header) == 0 && sdr.SDRWhiteLevel > 0)
                    sdrNits = sdr.SDRWhiteLevel * 80f / 1000f;

                var adv = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                adv.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                adv.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO);
                adv.header.adapterId = p.targetInfo.adapterId;
                adv.header.id = p.targetInfo.id;
                bool advEnabled = PInvoke.DisplayConfigGetDeviceInfo(ref adv.header) == 0 && adv.advancedColorEnabled;

                result[gdiName] = new CfgInfo(friendly, sdrNits, advEnabled);
            }
        }
        catch { /* best effort */ }
        return result;
    }
}
