using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;
using Clink.Native;

namespace Clink.Audio;

/// <summary>Figures out which process is "the game" (the window filling the captured monitor) and where the voice app is.</summary>
internal static unsafe class GameProcessTracker
{
    // Never "the game": the shell, browsers, launchers, voice/chat, overlays, wallpaper engines, media players, dev tools and Clink itself.
    private static readonly HashSet<string> s_ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        // Clink, shell, system
        "clink", "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "lockapp", "logonui",
        "applicationframehost", "taskmgr", "systemsettings", "dwm", "sihost", "rundll32", "msedgewebview2", "widgets", "snippingtool",
        "screenclippinghost", "screensketch", "consent", "ctfmon", "securityhealthsystray", "windowsterminal", "openconsole", "conhost",
        "cmd", "powershell", "pwsh", "code", "devenv", "notepad", "notepad++", "mspaint", "winword", "excel", "powerpnt", "outlook", "onenote",
        "regedit", "mmc", "control", "calculator", "calculatorapp", "photos", "microsoft.photos", "7zfm", "winrar", "acrobat", "acrord32",
        "tabtip", "widgetboard", "microsoftstartfeedprovider", "gamebar", "powertoys",   // PowerToys.* modules: see IsIgnored
        // sync / cloud clients, streaming-desk tools, screenshot tools
        "onedrive", "googledrivefs", "dropbox", "wavelink", "elgato.wavelink", "streamdeck", "camerahub", "camera hub", "sharex", "greenshot",
        "lightshot", "thunderbird",
        // crash reporters and helpers that ship inside game installs (a game-library path alone would make them "the game")
        "blizzarderror", "crashmailer_64", "blizzardbrowser", "crashreportclient", "unitycrashhandler64", "unitycrashhandler32",
        "start_protected_game", "eaanticheat.gameservicelauncher", "epiconlineservicesuihelper", "epiconlineservicesuserhelper",
        "gameoverlayui", "gameoverlayui64",
        // browsers
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "iexplore", "chromium", "librewolf", "waterfox", "floorp", "zen",
        // launchers / stores
        "steam", "steamwebhelper", "steamservice", "steamerrorreporter", "epicgameslauncher", "epicwebhelper", "battle.net", "battle.net launcher",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler", "riot client", "leagueclient", "leagueclientux",
        "leagueclientuxrender", "vgtray", "eadesktop", "eabackgroundservice", "origin",
        "originwebhelperservice", "galaxyclient", "galaxyclient helper", "ubisoftconnect", "upc", "uplaywebcore", "xboxapp", "xboxpcapp",
        "xboxpcappft", "gamingservices", "gamelaunchhelper", "launcher", "rockstarservice", "socialclubhelper", "playnite.desktopapp",
        "playnite.fullscreenapp",
        // voice / chat
        "discord", "discordptb", "discordcanary", "teamspeak", "ts3client_win64", "slack", "mumble", "zoom", "skype", "telegram", "whatsapp",
        "signal", "teams", "ms-teams", "cpthost", "claude",
        // overlays, capture, GPU tools
        "nvidia overlay", "nvcontainer", "nvidia app", "nvidia share", "nvsphelper64", "nvdisplay.container", "radeonsoftware", "amdrsserv",
        "msiafterburner", "rtss", "obs64", "obs32", "streamlabs obs", "xsplit.core", "medal",
        // wallpaper engines (their window covers the monitor), media players
        "wallpaper32", "wallpaper64", "livelywpf", "lively", "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "mpv", "wmplayer", "video.ui",
        "microsoft.media.player", "potplayermini", "potplayermini64", "plex", "plex htpc", "netflix", "spotify", "kodi",
    };

    // Folder names (with separators) that mark a process image as living in a game library install.
    private static readonly string[] s_libraryFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\battle.net\", @"\blizzard\", @"\ubisoft game launcher\games\",
        @"\ea games\", @"\origin games\", @"\gog galaxy\games\", @"\xboxgames\", @"\rockstar games\",
        // Battle.net installs games to "<Program Files>\<Game>\_retail_\" (Overwatch, WoW, Call of Duty) and the like
        @"\_retail_\", @"\_classic_\", @"\_classic_era_\", @"\_ptr_\", @"\_beta_\",
    };

    public static readonly string[] VoiceAppNames = { "Discord", "DiscordPTB", "DiscordCanary", "TeamSpeak", "ts3client_win64", "Slack", "Mumble", "Zoom", "Skype" };

    /// <param name="InLibrary">The process image lives in a known game-library folder (so a windowed foreground app is still a game).</param>
    public readonly record struct Result(uint Pid, string Name, bool Fullscreen, bool InLibrary = false);

    /// <param name="Source">"fullscreen" (a window covers the captured monitor), "library" (installed in a game library folder, has a window) or "custom" (in the user's custom apps list).</param>
    public readonly record struct GameResult(uint Pid, string Name, string Source);

    [ThreadStatic] private static List<nint>? t_windows;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static BOOL EnumProc(HWND hwnd, LPARAM lparam)
    {
        t_windows!.Add((nint)hwnd.Value);
        return true;
    }

    /// <summary>Top-level windows in Z order (top first).</summary>
    private static List<nint> TopLevelWindows()
    {
        t_windows = new List<nint>(256);
        PInvoke.EnumWindows(&EnumProc, default);
        var windows = t_windows;
        t_windows = null;
        return windows;
    }

    public static bool IsIgnored(string processName) =>
        s_ignored.Contains(processName) || processName.StartsWith("PowerToys.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Looks for the topmost visible window that covers the monitor (fullscreen / borderless game). Falls back to the
    /// foreground window on that monitor when nothing covers it.
    /// </summary>
    public static Result? Detect(int monLeft, int monTop, int monRight, int monBottom, nint monitorHandle)
    {
        try
        {
            foreach (var h in TopLevelWindows()) // Z-order, top first
            {
                var hwnd = new HWND((void*)h);
                if (!CoversMonitor(hwnd, monLeft, monTop, monRight, monBottom)) continue;
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

    /// <summary>
    /// The three v2 signals, in this order (docs/SPEC-v2.md §5): a visible window covering the captured monitor; a process whose
    /// image lives in a known game-library folder and has a visible window; a process in the user's custom apps list (windowed or
    /// not). Returns null when no game is running. Safe to call from any thread; takes a few ms.
    /// </summary>
    public static GameResult? DetectGame(MonitorInfo monitor, IReadOnlyList<string>? customApps)
    {
        try
        {
            int l = monitor.Left, t = monitor.Top, r = monitor.Left + monitor.Width, b = monitor.Top + monitor.Height;
            var windows = TopLevelWindows();

            // 1. fullscreen / borderless on the captured monitor
            foreach (var h in windows)
            {
                var hwnd = new HWND((void*)h);
                if (!CoversMonitor(hwnd, l, t, r, b)) continue;
                var d = Describe(hwnd);
                if (d != null) return new GameResult(d.Value.Pid, d.Value.Name, "fullscreen");
            }

            // 2. a titled, visible top-level window of a process installed in a game library folder
            var seen = new HashSet<uint>();
            foreach (var h in windows)
            {
                var hwnd = new HWND((void*)h);
                if (!PInvoke.IsWindowVisible(hwnd) || IsCloaked(hwnd) || PInvoke.GetWindowTextLength(hwnd) == 0) continue;
                if (((WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0) continue;
                uint pid;
                PInvoke.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0 || pid == Environment.ProcessId || !seen.Add(pid)) continue;
                var info = ProcessInfo(pid);
                if (info != null && info.Value.InLibrary) return new GameResult(pid, info.Value.Name, "library");
            }

            // 3. custom apps always count
            if (customApps != null)
                foreach (var app in customApps)
                {
                    string n = NormalizeName(app);
                    if (n == "auto") continue;
                    var found = FindProcessByName(n);
                    if (found != null) return new GameResult(found.Value.pid, found.Value.name, "custom");
                }
        }
        catch (Exception ex) { Log.Warn("game detection: " + ex.Message); }
        return null;
    }

    private static bool CoversMonitor(HWND hwnd, int l, int t, int r, int b)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) return false;
        if (IsCloaked(hwnd)) return false;
        if (!PInvoke.GetWindowRect(hwnd, out RECT rc)) return false;
        if (rc.left > l || rc.top > t || rc.right < r || rc.bottom < b) return false;
        // A fullscreen or borderless game is a frameless window that takes input and shows in Alt-Tab. Overlays (FancyZones'
        // zone overlay, Shortcut Guide, the widgets feed, the pen canvas, NVIDIA / Game Bar) are tool, click-through or
        // no-activate windows; a maximized or snapped app has a resize frame or a caption, and with an auto-hide taskbar its
        // rect spans the monitor as well. (A frameless WS_POPUP shown maximized is still a borderless game: DirectX's templates do that.)
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((ex & (WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE)) != 0) return false;
        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        return (style & WINDOW_STYLE.WS_THICKFRAME) == 0 && (style & WINDOW_STYLE.WS_CAPTION) != WINDOW_STYLE.WS_CAPTION;
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
        var info = ProcessInfo(pid);   // null for an ignored process (browsers, launchers, voice apps, the shell)
        if (info == null) return null;
        return new Result(pid, info.Value.Name, false, info.Value.InLibrary);
    }

    // ---- per-process facts (name, image path) cached by pid: the poll runs every second and these do not change for one
    // process. Windows hands a freed pid to a new process within seconds, so every hit is checked against the process
    // creation time before it is trusted; an ignored process is cached too (as a "no") so it is not looked up every second. ----

    private readonly record struct ProcInfo(string Name, string? ImagePath, bool InLibrary, bool Ignored, long Created, DateTime Seen);
    private static readonly Dictionary<uint, ProcInfo> s_procCache = new();
    private static DateTime s_cacheSweep = DateTime.UtcNow;

    /// <summary>Process name (no .exe) and whether its image is in a game library folder; null when the process is gone or ignored.</summary>
    private static ProcInfo? ProcessInfo(uint pid)
    {
        lock (s_procCache)
        {
            var now = DateTime.UtcNow;
            if (now - s_cacheSweep > TimeSpan.FromSeconds(60))
            {
                s_cacheSweep = now;
                foreach (var stale in s_procCache.Where(kv => now - kv.Value.Seen > TimeSpan.FromSeconds(60)).Select(kv => kv.Key).ToList()) s_procCache.Remove(stale);
            }
            long created = CreationTime(pid);   // 0: gone, or not openable (then nothing cached is trusted either)
            if (s_procCache.TryGetValue(pid, out var cached))
            {
                if (created != 0 && cached.Created == created)
                {
                    s_procCache[pid] = cached with { Seen = now };
                    return cached.Ignored ? null : cached;
                }
                s_procCache.Remove(pid);   // another process has this pid now, or the one we knew is gone
            }
            string name;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { return null; }
            bool ignored = IsIgnored(name);
            string? path = ignored ? null : ImagePath(pid);
            var info = new ProcInfo(name, path, path != null && IsInLibrary(path), ignored, created, now);
            if (created != 0) s_procCache[pid] = info;
            return ignored ? null : info;
        }
    }

    /// <summary>Drops what is cached about a pid (the tracked game exited: its pid may be reused within seconds).</summary>
    public static void Forget(uint pid)
    {
        lock (s_procCache) s_procCache.Remove(pid);
    }

    /// <summary>Process creation time as a FILETIME value, or 0 when the process is gone or cannot be opened.</summary>
    private static long CreationTime(uint pid)
    {
        HANDLE h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsNull) return 0;
        try
        {
            if (!PInvoke.GetProcessTimes(h, out System.Runtime.InteropServices.ComTypes.FILETIME created, out _, out _, out _)) return 0;
            return ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        }
        catch { return 0; }
        finally { PInvoke.CloseHandle(h); }
    }

    /// <summary>Full image path of a process (works for elevated games too: only PROCESS_QUERY_LIMITED_INFORMATION is needed), or null.</summary>
    public static string? ImagePath(uint pid)
    {
        HANDLE h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsNull) return null;
        try
        {
            Span<char> buf = stackalloc char[1024];
            uint len = (uint)buf.Length;
            if (PInvoke.QueryFullProcessImageName(h, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buf, ref len) && len > 0) return new string(buf[..(int)len]);
            return null;
        }
        catch { return null; }
        finally { PInvoke.CloseHandle(h); }
    }

    public static bool IsInLibrary(string imagePath)
    {
        string p = imagePath.Replace('/', '\\');
        foreach (var folder in s_libraryFolders)
            if (p.Contains(folder, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsAlive(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; } catch { return false; }
    }

    /// <summary>
    /// Canonical form of a configured process name (settings / picker value): "auto" for blank or any-case "auto", otherwise
    /// trimmed with a trailing ".exe" dropped, so the UI, the config and the engine all agree on what a hand-edited value means.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4].TrimEnd();
        return n.Length == 0 || n.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : n;
    }

    public static bool IsAuto(string? name) => NormalizeName(name) == "auto";

    /// <summary>Finds the root process of the voice app (oldest process with that name) so include-tree captures all of it.</summary>
    public static (uint pid, string name)? FindVoiceApp(string? preferredName)
    {
        IEnumerable<string> names = IsAuto(preferredName) ? VoiceAppNames : new[] { NormalizeName(preferredName) };
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

    /// <summary>Running apps with a visible window, for the "which process is the game" / "add running app" pickers.</summary>
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
                    if (IsIgnored(name) || !seen.Add(name)) continue;
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
