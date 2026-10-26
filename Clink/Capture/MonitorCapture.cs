using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;
using Windows.Win32.System.WinRT.Direct3D11;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;
using Clink.Native;

namespace Clink.Capture;

/// <summary>Outcome of asking Windows to hide the yellow capture border.</summary>
internal enum CaptureBorderState { Hidden, UnsupportedOs, Denied, SetFailed, Unconfirmed }

/// <summary>
/// Captures one monitor with Windows.Graphics.Capture and keeps the most recent frame in a GPU texture.
/// Works with borderless and exclusive-fullscreen games. On HDR displays it captures FP16 scRGB so the
/// converter can tone-map properly instead of producing the usual washed-out SDR frame.
/// </summary>
internal sealed unsafe class MonitorCapture : IDisposable
{
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly D3DDevice _d3d;
    private readonly bool _hdr;
    private GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _pool;
    private GraphicsCaptureSession _session;
    private SizeInt32 _size;

    private ID3D11Texture2D* _latest;
    private uint _latestW, _latestH;
    private bool _hasFrame;
    private long _latestTime;
    private long _frameCount;
    private long _copyCount;
    private long _lastCopyTicks;
    private readonly long _minCopyInterval;

    public DirectXPixelFormat PixelFormat { get; }
    public bool IsHdr => _hdr;
    public long FramesReceived => Interlocked.Read(ref _frameCount);
    public long FramesCopied => Interlocked.Read(ref _copyCount);
    public bool HasFrame => Volatile.Read(ref _hasFrame);

    public MonitorCapture(D3DDevice d3d, nint hmonitor, bool hdr, bool captureCursor, uint targetFps)
    {
        _d3d = d3d;
        _hdr = hdr;
        PixelFormat = hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        // WGC delivers no faster than MinUpdateInterval (about 60 fps when it is not set). At exactly one encode period the
        // capture and pacer clocks drift past each other, and the pacer encodes a frame twice then skips one: a hitch on every
        // camera pan. 5x the encode rate (3.3 ms for 60 fps clips) lets a 240 Hz display hand over every frame (an interval of
        // exactly 1/240 s drops one in six to timing jitter) and a 360 Hz one most, so the pacer always samples a frame a few
        // ms old; nothing changes at 60 Hz. The copy throttle caps the cost of copying ~30 MB FP16 frames where WGC ignores
        // the interval; it sits a little under it so timestamp jitter never drops a frame WGC already paced.
        long minInterval = targetFps > 0 ? TimeSpan.TicksPerSecond / (targetFps * 5) : 0;
        _minCopyInterval = minInterval * 3 / 4;

        _item = CreateItemForMonitor(hmonitor);
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3d.WinRTDevice, PixelFormat, 2, _size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        TrySetBorderless(_session);
        try { _session.IsCursorCaptureEnabled = captureCursor; } catch { }
        TrySetMinUpdateInterval(_session, minInterval);
        _session.StartCapture();
    }

    private static void TrySetMinUpdateInterval(GraphicsCaptureSession session, long interval)
    {
        try
        {
            if (interval > 0 && Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                session.MinUpdateInterval = TimeSpan.FromTicks(interval);
        }
        catch (Exception ex) { Log.Warn("MinUpdateInterval: " + ex.Message); }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        HSTRING cls = ComUtil.CreateHString("Windows.Graphics.Capture.GraphicsCaptureItem");
        try
        {
            Guid iidInterop = IGraphicsCaptureItemInterop.IID_Guid;
            void* pInterop;
            PInvoke.RoGetActivationFactory(cls, &iidInterop, &pInterop).Check("RoGetActivationFactory(GraphicsCaptureItem)");
            var interop = (IGraphicsCaptureItemInterop*)pInterop;
            try
            {
                Guid iidItem = IID_GraphicsCaptureItem;
                void* pItem;
                interop->CreateForMonitor(new HMONITOR((void*)hmonitor), &iidItem, &pItem).Check("IGraphicsCaptureItemInterop.CreateForMonitor");
                var item = GraphicsCaptureItem.FromAbi((nint)pItem);
                ((IUnknown*)pItem)->Release();
                return item;
            }
            finally { interop->Release(); }
        }
        finally { PInvoke.WindowsDeleteString(cls); }
    }

    /// <summary>Whether the yellow capture border Windows draws around a captured monitor was hidden for this capture, and why not.</summary>
    public CaptureBorderState BorderState { get; private set; }
    /// <summary>The access status name or the exception message behind <see cref="BorderState"/>; empty when there is nothing to add.</summary>
    public string BorderDetail { get; private set; } = "";

    // The Borderless access request is made on every capture start (never cached: the user may flip the Windows switches at any time);
    // only the logging is de-duplicated per process.
    private static readonly object s_borderLogLock = new();
    private static string? s_borderLogged;

    /// <summary>The access status name ("Allowed", "DeniedBySystem", "DeniedByUser", "Timeout", "Error: ..."), or null when the API is absent.</summary>
    private static string? RequestBorderAccess()
    {
        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess")) return null;
            // Unpackaged desktop apps are granted this without a prompt; the call just needs to happen.
            var task = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
            return task.Wait(2000) ? task.Result.ToString() : "Timeout";
        }
        catch (AggregateException ae) when (ae.InnerException != null) { return "Error: " + ae.InnerException.Message; }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    /// <summary>
    /// The pure decision behind <see cref="BorderState"/>. <paramref name="accessStatus"/> is the Borderless access status name, or
    /// "Timeout" / "Error: ..." when the request did not complete, or null when it was not made. <paramref name="setException"/> is what
    /// setting IsBorderRequired threw (null: it worked or was not tried). A successful set proves nothing when access is denied
    /// (Windows ignores the value and draws the border), so only Allowed + a successful set is Hidden.
    /// </summary>
    internal static (CaptureBorderState State, string Detail) DecideBorder(bool propertyPresent, bool accessTypePresent, string? accessStatus, Exception? setException)
    {
        if (!propertyPresent) return (CaptureBorderState.UnsupportedOs, "");
        string status = accessTypePresent ? accessStatus ?? "" : "";
        // Denied wins over a thrown set: it is the actionable cause, and the set is attempted regardless.
        if (status is "DeniedBySystem" or "DeniedByUser" or "UserPromptRequired" or "NotDeclaredByApp") return (CaptureBorderState.Denied, status);
        if (setException != null) return (CaptureBorderState.SetFailed, setException.Message);
        if (!accessTypePresent) return (CaptureBorderState.Unconfirmed, "GraphicsCaptureAccess unavailable");
        if (status == "Allowed") return (CaptureBorderState.Hidden, status);
        if (status.StartsWith("Error: ", StringComparison.Ordinal)) return (CaptureBorderState.Unconfirmed, status.Substring(7));
        return (CaptureBorderState.Unconfirmed, status.Length > 0 ? status : "Unknown");
    }

    private void TrySetBorderless(GraphicsCaptureSession session)
    {
        bool propertyPresent = false, accessPresent = false;
        string? access = null;
        Exception? failure = null;
        try
        {
            propertyPresent = Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");
            if (propertyPresent)
            {
                access = RequestBorderAccess();
                accessPresent = access != null;
                session.IsBorderRequired = false;
            }
        }
        catch (Exception ex) { failure = ex; }
        (BorderState, BorderDetail) = DecideBorder(propertyPresent, accessPresent, access, failure);

        string line = BorderState + ": " + BorderDetail;
        lock (s_borderLogLock)
        {
            if (s_borderLogged == line) return;
            s_borderLogged = line;
        }
        if (BorderState == CaptureBorderState.Hidden) { Log.Info($"Capture border: hidden (access {BorderDetail})"); return; }
        string why = BorderState switch
        {
            CaptureBorderState.Denied => " - Settings > Privacy & security > Screenshot borders: turn on 'Screenshot border settings access', 'Let apps turn off the screenshot border' and 'Let desktop apps turn off the screenshot border'",
            CaptureBorderState.UnsupportedOs => " - needs Windows 11",
            CaptureBorderState.Unconfirmed => " - set succeeded, access unknown",
            _ => "",
        };
        Log.Warn($"Capture border: {BorderState}{(BorderDetail.Length > 0 ? " (" + BorderDetail + ")" : "")}{why}");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame == null) return;

            var contentSize = frame.ContentSize;
            bool first = Interlocked.Read(ref _frameCount) == 0;
            Interlocked.Increment(ref _frameCount);
            long now = frame.SystemRelativeTime.Ticks;
            bool wanted = first || _minCopyInterval == 0 || now - _lastCopyTicks >= _minCopyInterval;
            if (first) Log.Info($"Capture: first frame {contentSize.Width}x{contentSize.Height}, getting texture");
            if (wanted)
            {
                ID3D11Texture2D* tex = GetTexture(frame.Surface);
                try
                {
                    D3D11_TEXTURE2D_DESC desc;
                    tex->GetDesc(&desc);
                    if (first) Log.Info($"Capture: texture {desc.Width}x{desc.Height} format={desc.Format} array={desc.ArraySize}");
                    lock (_d3d.ContextLock)
                    {
                        EnsureLatest(desc.Width, desc.Height, desc.Format);
                        _d3d.Context->CopyResource((ID3D11Resource*)_latest, (ID3D11Resource*)tex);
                        _latestTime = now;
                        _hasFrame = true;
                    }
                    _lastCopyTicks = now;
                    Interlocked.Increment(ref _copyCount);
                }
                finally { tex->Release(); }
            }

            // Display mode changed (e.g. game switched resolution): recreate the pool at the new size.
            if (contentSize.Width != _size.Width || contentSize.Height != _size.Height)
            {
                _size = contentSize;
                sender.Recreate(_d3d.WinRTDevice, PixelFormat, 2, _size);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Capture frame error: {ex.Message}");
        }
        finally { frame?.Dispose(); }
    }

    private void EnsureLatest(uint w, uint h, DXGI_FORMAT format)
    {
        if (_latest != null && _latestW == w && _latestH == h) return;
        ComUtil.Release(ref _latest);
        _latest = _d3d.CreateTexture(w, h, format, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        _latestW = w;
        _latestH = h;
    }

    private static ID3D11Texture2D* GetTexture(IDirect3DSurface surface)
    {
        nint abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            IDirect3DDxgiInterfaceAccess* access = ComUtil.Query<IDirect3DDxgiInterfaceAccess>((void*)abi, "QI IDirect3DDxgiInterfaceAccess");
            try
            {
                Guid iid = ID3D11Texture2D.IID_Guid;
                void* p;
                access->GetInterface(&iid, &p).Check("IDirect3DDxgiInterfaceAccess.GetInterface");
                return (ID3D11Texture2D*)p;
            }
            finally { access->Release(); }
        }
        finally { Marshal.Release(abi); }
    }

    /// <summary>Runs <paramref name="use"/> with the latest frame texture while holding the context lock.</summary>
    public bool WithLatest(Action<nint, uint, uint> use)
    {
        lock (_d3d.ContextLock)
        {
            if (!_hasFrame || _latest == null) return false;
            use((nint)_latest, _latestW, _latestH);
            return true;
        }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool.FrameArrived -= OnFrameArrived; _pool?.Dispose(); } catch { }
        _session = null!;
        _pool = null!;
        _item = null!;
        lock (_d3d.ContextLock) { ComUtil.Release(ref _latest); }
    }
}
