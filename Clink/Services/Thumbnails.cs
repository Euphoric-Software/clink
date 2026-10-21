using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Clink.Native;

namespace Clink.Services;

/// <summary>Video thumbnails through the Windows shell (same ones Explorer shows).</summary>
internal static unsafe class Thumbnails
{
    public static BitmapSource? Get(string path, int width, int height)
    {
        try
        {
            Guid iid = IShellItemImageFactory.IID_Guid;
            void* p;
            fixed (char* ps = path)
            {
                if (PInvoke.SHCreateItemFromParsingName(new PCWSTR(ps), null, &iid, &p).Failed) return null;
            }
            var factory = (IShellItemImageFactory*)p;
            try
            {
                HBITMAP hbm;
                HRESULT hr = factory->GetImage(new SIZE(width, height), SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK, &hbm);
                if (hr.Failed || hbm.IsNull) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap((nint)hbm.Value, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { PInvoke.DeleteObject(new HGDIOBJ(hbm.Value)); }
            }
            finally { factory->Release(); }
        }
        catch (Exception ex)
        {
            Log.Warn("thumbnail: " + ex.Message);
            return null;
        }
    }
}
