// Native helpers for tools/screenshots/capture.ps1 (compiled by Add-Type). Nothing here sends input, activates a window or looks at the screen:
// windows are found by process id, drawn with PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT) and resized through UI Automation (SetWindowPos with NOACTIVATE | NOZORDER | NOMOVE only as a fallback). C# 5 only (Windows PowerShell 5.1 Add-Type).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class ShotWindow
{
    public IntPtr Handle;
    public string Title = "";
    public string ClassName = "";
    public bool Visible;
    public int Width, Height;
    public override string ToString() { return string.Format("0x{0:X} '{1}' [{2}] {3}x{4}{5}", Handle.ToInt64(), Title, ClassName, Width, Height, Visible ? "" : " hidden"); }
}

public static class ShotNative
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    /// <summary>Every top-level window of the process (visible or not, cloaked ones included).</summary>
    public static List<ShotWindow> Windows(uint pid)
    {
        UsePhysicalPixels();
        var list = new List<ShotWindow>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var t = new StringBuilder(256); var c = new StringBuilder(256);
            GetWindowText(h, t, 256); GetClassName(h, c, 256);
            RECT r; GetWindowRect(h, out r);
            list.Add(new ShotWindow { Handle = h, Title = t.ToString(), ClassName = c.ToString(), Visible = IsWindowVisible(h), Width = r.Right - r.Left, Height = r.Bottom - r.Top });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    /// <summary>Makes the calling thread per-monitor DPI aware (v2), so window sizes and UI Automation rectangles are physical pixels.</summary>
    public static void UsePhysicalPixels() { SetThreadDpiAwarenessContext((IntPtr)(-4)); }

    public static double Scale(IntPtr hwnd) { UsePhysicalPixels(); uint d = GetDpiForWindow(hwnd); return d == 0 ? 1.0 : d / 96.0; }

    /// <summary>Fallback when UI Automation cannot resize: sets the size in physical pixels, without moving, raising or activating the window.</summary>
    public static bool Resize(IntPtr hwnd, int width, int height) { UsePhysicalPixels(); return SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE); }

    /// <summary>
    /// Draws the window with PrintWindow(PW_RENDERFULLCONTENT = 2) and saves it as a PNG; returns a hash of the pixels (to tell when a window has settled).
    /// The window's transparent corners come out as transparent pixels (the picture is drawn twice, on black and on white, to recover the alpha).
    /// </summary>
    public static ulong Capture(IntPtr hwnd, string path, out int width, out int height)
    {
        UsePhysicalPixels();
        RECT r; GetWindowRect(hwnd, out r);
        width = r.Right - r.Left; height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("the window has no size");
        using (var onBlack = Render(hwnd, width, height, Color.Black))
        using (var onWhite = Render(hwnd, width, height, Color.White))
        using (var result = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            var a = onBlack.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var b = onWhite.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var o = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            ulong hash = 1469598103934665603UL;
            try
            {
                var rowA = new byte[a.Stride]; var rowB = new byte[b.Stride]; var rowO = new byte[o.Stride];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(a.Scan0 + y * a.Stride, rowA, 0, a.Stride);
                    Marshal.Copy(b.Scan0 + y * b.Stride, rowB, 0, b.Stride);
                    for (int x = 0; x < width; x++)
                    {
                        int i = x * 4;
                        // a pixel the window covers is the same on both backgrounds; one it does not cover shows the background (black vs white)
                        int diff = Math.Max(rowB[i] - rowA[i], Math.Max(rowB[i + 1] - rowA[i + 1], rowB[i + 2] - rowA[i + 2]));
                        int alpha = 255 - Math.Max(0, Math.Min(255, diff));
                        if (alpha >= 250) { rowO[i] = rowA[i]; rowO[i + 1] = rowA[i + 1]; rowO[i + 2] = rowA[i + 2]; rowO[i + 3] = 255; }
                        else if (alpha <= 0) { rowO[i] = rowO[i + 1] = rowO[i + 2] = rowO[i + 3] = 0; }
                        else
                        {
                            // colour = what the window drew on black, un-premultiplied
                            rowO[i] = (byte)Math.Min(255, rowA[i] * 255 / alpha); rowO[i + 1] = (byte)Math.Min(255, rowA[i + 1] * 255 / alpha); rowO[i + 2] = (byte)Math.Min(255, rowA[i + 2] * 255 / alpha); rowO[i + 3] = (byte)alpha;
                        }
                        hash = (hash ^ rowO[i]) * 1099511628211UL; hash = (hash ^ rowO[i + 1]) * 1099511628211UL; hash = (hash ^ rowO[i + 2]) * 1099511628211UL; hash = (hash ^ rowO[i + 3]) * 1099511628211UL;
                    }
                    Marshal.Copy(rowO, 0, o.Scan0 + y * o.Stride, o.Stride);
                }
            }
            finally { onBlack.UnlockBits(a); onWhite.UnlockBits(b); result.UnlockBits(o); }
            result.Save(path, ImageFormat.Png);
            return hash;
        }
    }

    private static Bitmap Render(IntPtr hwnd, int width, int height, Color background)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(background);
            IntPtr hdc = g.GetHdc();
            try { if (!PrintWindow(hwnd, hdc, 2)) throw new InvalidOperationException("PrintWindow failed"); }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

    /// <summary>True when the picture has nothing in it: every pixel is fully transparent or black (a window PrintWindow could not draw).</summary>
    public static bool IsBlank(string pngPath)
    {
        using (var bmp = new Bitmap(pngPath))
        using (var argb = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(argb)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            var d = argb.LockBits(new Rectangle(0, 0, argb.Width, argb.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[d.Stride];
                for (int y = 0; y < argb.Height; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                    for (int x = 0; x < argb.Width; x++)
                    {
                        int i = x * 4;
                        if (row[i + 3] != 0 && (row[i] != 0 || row[i + 1] != 0 || row[i + 2] != 0)) return false;
                    }
                }
                return true;
            }
            finally { argb.UnlockBits(d); }
        }
    }

    // ------------------------------------------------------------------ final paths (junctions and symlinks resolved)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// The path with every junction and symbolic link resolved: the deepest EXISTING ancestor is opened (no access rights, only to ask for its
    /// final name) and the part that does not exist yet is appended unchanged. Throws when the existing ancestor cannot be opened.
    /// </summary>
    public static string FinalPath(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string existing = full;
        var rest = new List<string>();
        while (!System.IO.Directory.Exists(existing))
        {
            string parent = System.IO.Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent)) throw new System.IO.IOException("no existing folder above " + full);
            rest.Insert(0, System.IO.Path.GetFileName(existing));
            existing = parent;
        }
        const uint ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
        IntPtr h = CreateFile(existing, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new System.IO.IOException("cannot open " + existing + " (error " + Marshal.GetLastWin32Error() + ")");
        string resolved;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
            if (n == 0 || n >= sb.Capacity) throw new System.IO.IOException("cannot resolve " + existing + " (error " + Marshal.GetLastWin32Error() + ")");
            resolved = sb.ToString();
        }
        finally { CloseHandle(h); }
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) resolved = @"\\" + resolved.Substring(8);
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved.Substring(4);
        foreach (string part in rest) resolved = resolved.TrimEnd('\\') + "\\" + part;
        return resolved;
    }

    // ------------------------------------------------------------------ foreground watch

    /// <summary>
    /// Samples the foreground window every ~100 ms. When it belongs to a watched process continuously for 200 ms or more, the watcher
    /// stops that process itself (only that one, and only if it is still running), sets Tripped and writes a Note. Reads only otherwise;
    /// it never changes the foreground.
    /// </summary>
    public static class Watch
    {
        private static Thread _thread;
        private static volatile bool _stop;
        private static readonly List<System.Diagnostics.Process> Procs = new List<System.Diagnostics.Process>();
        public static volatile bool Tripped;
        public static int Samples, OwnSamples, LongestOwnMs;
        public static string Note = "";

        public static void Add(System.Diagnostics.Process p) { lock (Procs) Procs.Add(p); }

        public static void Start()
        {
            _stop = false; Tripped = false; Samples = 0; OwnSamples = 0; LongestOwnMs = 0; Note = "";
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "foreground watch";
            _thread.Start();
        }

        public static void Stop() { _stop = true; if (_thread != null) _thread.Join(1500); }

        private static System.Diagnostics.Process Match(uint pid)
        {
            if (pid == 0) return null;
            lock (Procs)
            {
                foreach (var p in Procs)
                {
                    try { if ((uint)p.Id == pid && !p.HasExited) return p; } catch (Exception) { }
                }
            }
            return null;
        }

        private static void Loop()
        {
            var run = new System.Diagnostics.Stopwatch();
            uint runPid = 0;
            while (!_stop)
            {
                IntPtr fg = GetForegroundWindow();
                uint pid = 0;
                if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
                var own = Match(pid);
                Samples++;
                if (own != null)
                {
                    OwnSamples++;
                    if (!run.IsRunning || runPid != pid) { runPid = pid; run.Reset(); run.Start(); }
                    int held = (int)run.ElapsedMilliseconds;
                    if (held > LongestOwnMs) LongestOwnMs = held;
                    if (held >= 200 && !Tripped)
                    {
                        Tripped = true;
                        Note = "pid " + pid + " held the foreground for " + held + " ms; it was stopped";
                        try { if (!own.HasExited) own.Kill(); } catch (Exception) { }
                    }
                }
                else { run.Stop(); run.Reset(); runPid = 0; }
                Thread.Sleep(100);
            }
        }
    }
}
