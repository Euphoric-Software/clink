using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;

namespace Clink.Audio;

/// <summary>Figures out which process is "the game" (the window filling the captured monitor) and where the voice app is.</summary>
internal static unsafe class GameProcessTracker
{
    private static readonly HashSet<string> s_ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "clink", "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
        "lockapp", "applicationframehost", "nvidia overlay", "nvcontainer", "steam", "steamwebhelper", "obs64",
        "discord", "discordptb", "discordcanary", "teamspeak", "ts3client_win64", "slack", "mumble", "zoom",
        "skype", "telegram", "epicgameslauncher", "battle.net", "eadesktop", "galaxyclient", "ubisoftconnect",
        "taskmgr", "systemsettings", "dwm", "sihost", "rundll32", "msedgewebview2", "widgets", "claude",
    };

    public static readonly string[] VoiceAppNames = { "Discord", "DiscordPTB", "DiscordCanary", "TeamSpeak", "ts3client_win64", "Slack", "Mumble", "Zoom", "Skype" };

    public readonly record struct Result(uint Pid, string Name, bool Fullscreen);

    [ThreadStatic] private static List<nint>? t_windows;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static BOOL EnumProc(HWND hwnd, LPARAM lparam)
    {
        t_windows!.Add((nint)hwnd.Value);
        return true;
    }

    /// <summary>
    /// Looks for the topmost visible window that covers the monitor (fullscreen / borderless game). Falls back to the
    /// foreground window on that monitor when nothing covers it.
    /// </summary>
    public static Result? Detect(int monLeft, int monTop, int monRight, int monBottom, nint monitorHandle)
    {
        try
        {
            t_windows = new List<nint>(256);
            PInvoke.EnumWindows(&EnumProc, default);
            var windows = t_windows;
            t_windows = null;

            foreach (var h in windows) // Z-order, top first
            {
                var hwnd = new HWND((void*)h);
                if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) continue;
                if (IsCloaked(hwnd)) continue;
                if (!PInvoke.GetWindowRect(hwnd, out RECT rc)) continue;
                bool covers = rc.left <= monLeft && rc.top <= monTop && rc.right >= monRight && rc.bottom >= monBottom;
                if (!covers) continue;
                var r = Describe(hwnd);
                if (r == null) continue;
                return r.Value with { Fullscreen = true };
            }

            HWND fg = PInvoke.GetForegroundWindow();
            if (fg.IsNull) return null;
            IntPtr monOfWindow = PInvoke.MonitorFromWindow(fg, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            if (monOfWindow != monitorHandle) return null;
            var d = Describe(fg);
            return d == null ? null : d.Value with { Fullscreen = false };
        }
        catch { return null; }
    }

    private static bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, 4).Succeeded && cloaked != 0;
    }

    private static Result? Describe(HWND hwnd)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { return null; }
        if (s_ignored.Contains(name)) return null;
        return new Result(pid, name, false);
    }

    public static bool IsAlive(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; } catch { return false; }
    }

    /// <summary>Finds the root process of the voice app (oldest process with that name) so include-tree captures all of it.</summary>
    public static (uint pid, string name)? FindVoiceApp(string? preferredName)
    {
        IEnumerable<string> names = string.IsNullOrWhiteSpace(preferredName) || preferredName == "auto"
            ? VoiceAppNames
            : new[] { preferredName };
        foreach (var n in names)
        {
            var found = FindProcessByName(n);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Oldest process with this name (".exe" optional) — the root of its tree for include-tree loopback.</summary>
    public static (uint pid, string name)? FindProcessByName(string name)
    {
        string n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (n.Length == 0) return null;
        Process[] procs;
        try { procs = Process.GetProcessesByName(n); } catch { return null; }
        if (procs.Length == 0) return null;
        Process? root = null;
        DateTime best = DateTime.MaxValue;
        foreach (var p in procs)
        {
            try { if (p.StartTime < best) { best = p.StartTime; root = p; } } catch { root ??= p; }
        }
        uint pid = (uint)(root ?? procs[0]).Id;
        foreach (var p in procs) p.Dispose();
        return (pid, n);
    }

    public readonly record struct Candidate(string Name, string Title, uint Pid);

    /// <summary>Running apps with a visible window, for the "which process is the game" picker.</summary>
    public static List<Candidate> ListCandidates()
    {
        var result = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    if (p.Id == Environment.ProcessId) continue;
                    string name = p.ProcessName;
                    if (s_ignored.Contains(name) || !seen.Add(name)) continue;
                    result.Add(new Candidate(name, p.MainWindowTitle, (uint)p.Id));
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
