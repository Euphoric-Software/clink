using System.IO;
using System.Threading;

namespace Clink.Services;

/// <summary>
/// Sends one file to the Recycle Bin through the shell (<c>SHFileOperation</c> with <c>FOF_ALLOWUNDO</c> and
/// <c>FOF_WANTNUKEWARNING</c>): a file that cannot go to the Recycle Bin needs a second, explicit confirmation from Windows
/// (declining it leaves the file); otherwise no shell dialog of its own. Never a permanent delete. Shared by the screenshot and
/// the clip delete (each keeps its own test seam that defaults to <see cref="Recycle"/>).
/// </summary>
internal static class RecycleBin
{
    /// <summary>Test seam: the window the Recycle Bin call belongs to (the main window), 0 for none. Read on the UI thread.</summary>
    internal static Func<nint> OwnerHandle { get; set; } = () =>
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app == null) return 0;
            return app.Dispatcher.Invoke(() => app.MainWindow is { } w ? new System.Windows.Interop.WindowInteropHelper(w).Handle : 0);
        }
        catch { return 0; }
    };

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Never two shell operations at once (the first-run setup's old-copy cleanup takes the same lock).</summary>
    internal static readonly object Lock = new();

    /// <summary>
    /// Sends the file (full path) to the Recycle Bin on a dedicated STA thread (the shell wants one); throws when it cannot.
    /// <paramref name="owner"/> is the owner window handle for the shell (0 = the main window). No timeout: the shell may be
    /// waiting on its own dialog, and the caller is a pool thread the UI awaits.
    /// </summary>
    internal static void Recycle(string path, nint owner)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (owner == 0) owner = OwnerHandle();
        lock (Lock)
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { RecycleCore(full, owner); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "Clink.Recycle" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw new IOException(error.Message, error);
        }
    }

    /// <summary>
    /// The reparse tag of a path (WIN32_FIND_DATA.dwReserved0 from FindFirstFile, which never opens the file, so a cloud placeholder is
    /// not downloaded). 0 when the path has none or cannot be found (the caller only asks for paths that carry the reparse attribute,
    /// and a 0 tag is not a cloud tag, so an unreadable answer refuses).
    /// </summary>
    internal static unsafe uint ReparseTag(string path)
    {
        try
        {
            var h = Windows.Win32.PInvoke.FindFirstFile(path, out Windows.Win32.Storage.FileSystem.WIN32_FIND_DATAW data);
            if ((nint)h.Value == -1 || (nint)h.Value == 0) return 0;
            Windows.Win32.PInvoke.FindClose(h);
            return data.dwReserved0;
        }
        catch { return 0; }
    }
    private static unsafe void RecycleCore(string path, nint owner)
    {
        string from = path + "\0\0";   // a double-null terminated list of one
        fixed (char* pFrom = from)
        {
            var op = new Windows.Win32.UI.Shell.SHFILEOPSTRUCTW
            {
                hwnd = new Windows.Win32.Foundation.HWND(owner),
                wFunc = FO_DELETE,
                pFrom = pFrom,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING),
            };
            int rc = Windows.Win32.PInvoke.SHFileOperation(ref op);
            if (rc != 0 || op.fAnyOperationsAborted) throw new IOException("The file could not be moved to the Recycle Bin (code 0x" + rc.ToString("X") + ")");
        }
        if (File.Exists(path)) throw new IOException("The file is still there after the move to the Recycle Bin");
    }
}
