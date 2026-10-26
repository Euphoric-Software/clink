using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Clink.Native;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Sources;

/// <summary>
/// Copies a monitor from the screen device context with BitBlt into a 32-bit DIB section. Synchronous (a few ms per 4K monitor)
/// and safe on any thread. The process is per-monitor DPI aware, so the screen DC and <see cref="MonitorInfo"/> are both physical
/// pixels: no conversion. DWM composes everything (layered windows too, CAPTUREBLT) into what GDI reads, windows that exclude
/// themselves from capture (Clink's own toasts) are left out. Limits: no cursor; on an HDR monitor Windows hands GDI an SDR
/// conversion (bright HDR highlights clip); an exclusive-fullscreen game gives black or a stale frame (the caller checks
/// <see cref="BgraImage.LooksBlack"/>).
/// </summary>
internal sealed unsafe class GdiScreenshotSource : IScreenshotSource
{
    public string Name => "gdi";

    public bool Handles(MonitorInfo monitor) => monitor.Width > 0 && monitor.Height > 0;

    public FrozenMonitor Capture(MonitorInfo monitor)
    {
        int w = monitor.Width, h = monitor.Height;
        if (w <= 0 || h <= 0 || (long)w * h > BgraImage.MaxPixels) throw new ScreenshotCaptureException($"{monitor.ShortName} has an unusable size ({w} x {h})");
        var sw = Stopwatch.StartNew();
        HDC screen = default, mem = default;
        HBITMAP dib = default;
        HGDIOBJ old = default;
        try
        {
            screen = PInvoke.GetDC(HWND.Null);
            if (screen.IsNull) throw new ScreenshotCaptureException("The screen could not be read (GetDC failed)");
            mem = PInvoke.CreateCompatibleDC(screen);
            if (mem.IsNull) throw new ScreenshotCaptureException("The screen could not be read (CreateCompatibleDC failed)");

            BITMAPINFO bmi = default;
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;   // negative: top-down rows, the layout of BgraImage
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;   // BI_RGB
            void* bits = null;
            dib = PInvoke.CreateDIBSection(mem, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (dib.IsNull || bits == null) throw new ScreenshotCaptureException($"The screen copy could not be allocated ({w} x {h})");

            old = PInvoke.SelectObject(mem, dib);
            if (old.IsNull) throw new ScreenshotCaptureException($"The screen copy of {monitor.ShortName} could not be prepared (SelectObject failed)");   // BitBlt would copy into nothing: an all-black picture
            if (!PInvoke.BitBlt(mem, 0, 0, w, h, screen, monitor.Left, monitor.Top, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
                throw new ScreenshotCaptureException($"The screen of {monitor.ShortName} could not be copied (BitBlt failed, error {Marshal.GetLastWin32Error()})");
            PInvoke.GdiFlush();

            var pixels = new byte[w * h * 4];
            Marshal.Copy((nint)bits, pixels, 0, pixels.Length);
            var image = new BgraImage(w, h, pixels).ForceOpaque();   // GDI leaves alpha at 0: a transparent PNG otherwise
            Log.Info($"screenshot: {monitor.ShortName} {w}x{h} at {monitor.Left},{monitor.Top}{(monitor.IsHdr ? " HDR" : "")} copied by gdi in {sw.ElapsedMilliseconds} ms");
            return new FrozenMonitor(monitor, image, Name);
        }
        finally
        {
            if (!mem.IsNull && !old.IsNull) PInvoke.SelectObject(mem, old);
            if (!dib.IsNull) PInvoke.DeleteObject(dib);
            if (!mem.IsNull) PInvoke.DeleteDC(mem);
            if (!screen.IsNull) PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }
}
