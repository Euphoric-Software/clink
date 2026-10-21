using System.Diagnostics;
using System.IO;
using System.Text;

namespace Clink;

internal static class Log
{
    /// <summary>The running Clink's log. Only the instance that owns the single-instance mutex (and the updater) opens it.</summary>
    public const string MainFile = "clink.log";
    /// <summary>The command-line modes (--selftest / --probe / --trim) log here, so they never fight the running Clink for its file.</summary>
    public const string CliFile = "clink-cli.log";
    private const long RotateAtStart = 2_000_000, RotateWhileRunning = 4_000_000;

    private static readonly object s_gate = new();
    private static StreamWriter? s_writer;
    private static DateTime s_day;
    private static int s_lines;
    /// <summary>The file being written, or null when no log file could be opened.</summary>
    public static string? Path { get; private set; }
    public static event Action<string>? Line;

    public static void Init(string dir, string file = MainFile)
    {
        lock (s_gate)
        {
            try
            {
                s_writer?.Dispose();
                s_writer = null;
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, file);
                // Keep the log small: rotate at 2 MB here, and at 4 MB while running (see Write).
                if (File.Exists(path) && new FileInfo(path).Length > RotateAtStart) TryMove(path, PrevPath(path));
                // The updater's apply step may still hold clink.log for a moment after it started us: wait for it a little.
                StreamWriter? w = null;
                for (int i = 0; w == null; i++)
                {
                    try { w = Open(path); }
                    catch (IOException) when (file == MainFile && i < 15) { Thread.Sleep(100); }
                }
                s_writer = w;
                Path = path;
                s_day = DateTime.Now.Date;
                w.WriteLine($"==== Clink {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId} args: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))} ====");
                if (file == MainFile) DeleteStale(dir);
            }
            catch { s_writer = null; Path = null; }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);
    public static void Error(string msg, Exception ex) => Write("ERR ", msg + ": " + ex);

    private static int s_repeat;
    private static string? s_lastMsg;

    private static void Write(string level, string msg)
    {
        // Collapse floods of identical messages (a layout exception loop once produced gigabytes).
        if (msg == s_lastMsg) { if (++s_repeat > 20) return; }
        else { s_repeat = 0; s_lastMsg = msg; }
        if (msg.Length > 4000) msg = msg[..4000] + " …";
        var now = DateTime.Now;
        string line = $"{now:HH:mm:ss.fff} {level} {msg}";
        Debug.WriteLine(line);
        lock (s_gate)
        {
            try
            {
                if (s_writer != null)
                {
                    // Lines carry the time only: mark each new day (Clink runs for days in the tray).
                    if (now.Date != s_day) { s_day = now.Date; s_writer.WriteLine($"---- {now:yyyy-MM-dd} ----"); }
                    s_writer.WriteLine(line);
                    // A long-running session (or an error loop the flood guard does not catch) must not grow the file without bound.
                    if (++s_lines % 200 == 0 && s_writer.BaseStream.Length > RotateWhileRunning) Rotate();
                }
            }
            catch { }
        }
        try { Line?.Invoke(line); } catch { }
    }

    /// <summary>Moves the full log to the .prev file and starts a new one. Under s_gate.</summary>
    private static void Rotate()
    {
        string? path = Path;
        if (path == null || s_writer == null) return;
        s_writer.Dispose();
        s_writer = null;
        TryMove(path, PrevPath(path));
        try
        {
            s_writer = Open(path);
            s_writer.WriteLine($"==== Clink {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId} (log continued) ====");
        }
        catch { s_writer = null; Path = null; }
    }

    private static StreamWriter Open(string path) => new(path, append: true, Encoding.UTF8) { AutoFlush = true };

    /// <summary>clink.log → clink.prev.log, clink-cli.log → clink-cli.prev.log.</summary>
    private static string PrevPath(string path) => System.IO.Path.ChangeExtension(path, ".prev.log");

    private static void TryMove(string from, string to)
    {
        try { File.Move(from, to, true); } catch { }
    }

    /// <summary>Older versions fell back to clink.&lt;pid&gt;.log whenever the log was busy (every Stream Deck press once it passed 2 MB).</summary>
    private static void DeleteStale(string dir)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "clink.*.log"))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(f);   // "clink.12345"
                if (name.Length > 6 && name[6..].All(char.IsAsciiDigit)) { try { File.Delete(f); } catch { } }
            }
        }
        catch { }
    }
}
