using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.Win32;
using System.Windows.Media.Imaging;
using Clink.Services;

namespace Clink.Screenshots;

/// <summary>
/// Puts a screenshot on the clipboard: the encoded PNG (format "PNG", what Discord / browsers / Office take without losing
/// alpha) and a bitmap (<see cref="DataFormats.Bitmap"/>, which Windows also offers as DIB: Paint, Word, chat apps). UI thread only
/// (OLE needs STA, and never the keyboard hook's thread). One attempt, no retry loop of ours: another program holding the clipboard
/// open (CLIPBRD_E_CANT_OPEN) makes it fail, and the toast says "clipboard busy". When a window visibly holds it open, nothing is
/// tried at all; otherwise WPF's own retries (about a second at most) are the only wait the UI thread can see.
/// </summary>
internal static class ScreenshotClipboard
{
    /// <summary>The clipboard's change counter: it moves every time anything puts something on the clipboard. Read right after our copy, it tells later whether anyone has copied since.</summary>
    public static uint Sequence() => PInvoke.GetClipboardSequenceNumber();

    /// <summary>Puts text (the Copium link) on the clipboard, replacing whatever is there. UI thread. True when the clipboard took it; never throws.</summary>
    public static bool CopyText(string text)
    {
        try
        {
            if (!PInvoke.GetOpenClipboardWindow().IsNull)
            {
                Log.Warn("screenshot: clipboard busy (another window has it open): link not copied");
                return false;
            }
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("screenshot: clipboard (link): " + ex.Message);
            return false;
        }
    }

    /// <summary>Copies the bitmap (frozen, made once by the caller). True when the clipboard took it; false when it is busy (or anything else went wrong). Never throws.</summary>
    public static bool Copy(BitmapSource bitmap, byte[] png)
    {
        try
        {
            if (!PInvoke.GetOpenClipboardWindow().IsNull)
            {
                Log.Warn("screenshot: clipboard busy (another window has it open): not copied");
                return false;
            }
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(png), false);
            data.SetData(DataFormats.Bitmap, bitmap, true);   // the one bitmap the PNG was encoded from: no second copy here
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (ExternalException ex)   // COMException: CLIPBRD_E_CANT_OPEN and friends
        {
            Log.Warn($"screenshot: clipboard busy: 0x{ex.HResult:X8}: not copied");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("screenshot: clipboard: " + ex.Message);
            return false;
        }
    }
}
