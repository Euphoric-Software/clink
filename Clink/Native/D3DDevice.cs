using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;
using WinRT;

namespace Clink.Native;

/// <summary>D3D11 device created on the adapter that drives a given monitor, plus the WinRT and MF views of it.</summary>
internal sealed unsafe class D3DDevice : IDisposable
{
    public ID3D11Device* Device;
    public ID3D11DeviceContext* Context;
    public IDXGIDevice* DxgiDevice;
    public IMFDXGIDeviceManager* DeviceManager;
    public uint ResetToken;
    public LUID AdapterLuid;
    public string AdapterName = "";
    public Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice WinRTDevice = null!;

    /// <summary>Serializes immediate-context use between the capture callback, the pacer and the encoder.</summary>
    public readonly object ContextLock = new();

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint hProcess, int priority);

    public static D3DDevice CreateForMonitor(nint hmonitor)
    {
        IDXGIFactory1* factory = null;
        IDXGIAdapter1* chosen = null;
        try
        {
            Guid iidFactory = IDXGIFactory1.IID_Guid;
            void* pf;
            PInvoke.CreateDXGIFactory1(&iidFactory, &pf).Check("CreateDXGIFactory1");
            factory = (IDXGIFactory1*)pf;

            string chosenName = "";
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1* adapter;
                if (factory->EnumAdapters1(a, &adapter).Failed) break;
                DXGI_ADAPTER_DESC1 ad;
                adapter->GetDesc1(&ad);
                bool match = false;
                for (uint o = 0; ; o++)
                {
                    IDXGIOutput* output;
                    if (adapter->EnumOutputs(o, &output).Failed) break;
                    DXGI_OUTPUT_DESC od;
                    output->GetDesc(&od);
                    ComUtil.Release(ref output);
                    if ((nint)od.Monitor.Value == hmonitor) { match = true; break; }
                }
                if (match && chosen == null)
                {
                    chosen = adapter;
                    chosenName = ad.Description.ToString();
                }
                else
                {
                    ComUtil.Release(ref adapter);
                }
            }

            if (chosen == null)
            {
                // Fall back to the first hardware adapter.
                for (uint a = 0; ; a++)
                {
                    IDXGIAdapter1* adapter;
                    if (factory->EnumAdapters1(a, &adapter).Failed) break;
                    DXGI_ADAPTER_DESC1 ad;
                    adapter->GetDesc1(&ad);
                    if ((ad.Flags & DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
                    {
                        chosen = adapter;
                        chosenName = ad.Description.ToString();
                        break;
                    }
                    ComUtil.Release(ref adapter);
                }
            }
            if (chosen == null) throw new InvalidOperationException("No DXGI adapter found.");

            var dev = new D3DDevice { AdapterName = chosenName };
            DXGI_ADAPTER_DESC1 desc;
            chosen->GetDesc1(&desc);
            dev.AdapterLuid = desc.AdapterLuid;

            var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
            ID3D11Device* device;
            ID3D11DeviceContext* context;
            D3D_FEATURE_LEVEL got;
            var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
            HRESULT hr = PInvoke.D3D11CreateDevice((IDXGIAdapter*)chosen, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.Null, flags,
                levels, 2, PInvoke.D3D11_SDK_VERSION, &device, &got, &context);
            if (hr.Failed)
            {
                // Some drivers reject VIDEO_SUPPORT; retry without it.
                flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
                PInvoke.D3D11CreateDevice((IDXGIAdapter*)chosen, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.Null, flags,
                    levels, 2, PInvoke.D3D11_SDK_VERSION, &device, &got, &context).Check("D3D11CreateDevice");
            }
            dev.Device = device;
            dev.Context = context;

            // Media Foundation requires multithread protection on a device shared through the DXGI device manager.
            ID3D11Multithread* mt = ComUtil.TryQuery<ID3D11Multithread>(context);
            if (mt != null)
            {
                mt->SetMultithreadProtected(true);
                ComUtil.Release(ref mt);
            }

            dev.DxgiDevice = ComUtil.Query<IDXGIDevice>(device, "QI IDXGIDevice");
            // Ask for a higher GPU scheduling priority so a busy game doesn't starve capture/encode. Both calls may be
            // refused without elevation; that's fine.
            try { dev.DxgiDevice->SetGPUThreadPriority(7); } catch { }
            try
            {
                int status = D3DKMTSetProcessSchedulingPriorityClass(System.Diagnostics.Process.GetCurrentProcess().Handle, 4 /* HIGH */);
                Log.Info($"GPU scheduling priority HIGH: 0x{(uint)status:X8}");
            }
            catch (Exception ex) { Log.Info("GPU scheduling priority not available: " + ex.Message); }

            global::Windows.Win32.System.WinRT.IInspectable* inspectable;
            PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dev.DxgiDevice, &inspectable).Check("CreateDirect3D11DeviceFromDXGIDevice");
            dev.WinRTDevice = MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi((nint)inspectable);
            ((IUnknown*)inspectable)->Release();

            IMFDXGIDeviceManager* mgr;
            PInvoke.MFCreateDXGIDeviceManager(out uint token, &mgr).Check("MFCreateDXGIDeviceManager");
            mgr->ResetDevice((IUnknown*)device, token).Check("IMFDXGIDeviceManager.ResetDevice");
            dev.DeviceManager = mgr;
            dev.ResetToken = token;
            return dev;
        }
        finally
        {
            ComUtil.Release(ref chosen);
            ComUtil.Release(ref factory);
        }
    }

    public ID3D11Texture2D* CreateTexture(uint width, uint height, Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT format, D3D11_BIND_FLAG bind, D3D11_USAGE usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, D3D11_CPU_ACCESS_FLAG cpu = 0)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpu,
            MiscFlags = 0,
        };
        ID3D11Texture2D* tex;
        Device->CreateTexture2D(&desc, null, &tex).Check("CreateTexture2D");
        return tex;
    }

    public void Dispose()
    {
        ComUtil.Release(ref DeviceManager);
        WinRTDevice?.Dispose();
        WinRTDevice = null!;
        ComUtil.Release(ref DxgiDevice);
        ComUtil.Release(ref Context);
        ComUtil.Release(ref Device);
    }
}
