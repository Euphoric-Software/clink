using System.Diagnostics;
using System.IO;
using System.Text;

namespace Clink;

internal static class Log
{
    private static readonly object s_gate = new();
    private static StreamWriter? s_writer;
    public static string? Path { get; private set; }
    public static event Action<string>? Line;

    public static void Init(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, "clink.log");
            // Keep the log small: rotate at 2 MB.
            if (File.Exists(Path) && new FileInfo(Path).Length > 2_000_000)
            {
                try { File.Move(Path, System.IO.Path.Combine(dir, "clink.prev.log"), true); }
                catch { try { File.Delete(Path); } catch { Path = System.IO.Path.Combine(dir, $"clink.{Environment.ProcessId}.log"); } }
            }
            s_writer = new StreamWriter(Path, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch { s_writer = null; }
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
        string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
        Debug.WriteLine(line);
        lock (s_gate)
        {
            try { s_writer?.WriteLine(line); } catch { }
        }
        try { Line?.Invoke(line); } catch { }
    }
}
