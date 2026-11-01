using System.IO;
using System.Windows;

namespace Clink.Services;

/// <summary>
/// Test mode only (<c>Clink.exe --unittest</c> turns it on first thing): refuses any write under the true real settings folder,
/// <c>%APPDATA%\Clink</c>, the true local folder <c>%LOCALAPPDATA%\Clink</c> and the true default clips folder (<c>Videos\Clink</c>, which
/// holds the default screenshots folder), whatever the overrides say. A write that would land there is a test (or code a test started,
/// e.g. a save that fires after the test) that bypassed its scratch folder: it is not performed, it throws and logs with the stack
/// trace, and the runner fails the test it ran in (or the run, when none was). The same goes for a launch (<see cref="CheckLaunch"/>)
/// and for a window that gets shown (<see cref="SweepWindows"/>, the Loaded handler).
/// Outside test mode <see cref="Active"/> is false, so everything here returns at once and nothing changes.
/// </summary>
internal static class TestGuard
{
    /// <summary>True only in a <c>--unittest</c> run.</summary>
    internal static bool Active { get; private set; }

    /// <summary>The test that is running, when the runner says (for the message).</summary>
    internal static string? CurrentTest { get; set; }

    private static readonly List<string> s_violations = new();

    /// <summary>Where the settings, the clip index and the updates point from <see cref="Enable"/> until the runner gives a test its own scratch folder; null outside test mode.</summary>
    internal static string? StartupScratch { get; private set; }

    /// <summary>
    /// Turns test mode on and, before anything else can run, points the settings folder, the "real" settings folder, the clip index, the
    /// updates folder and the default clips folder at a per-run scratch folder under the temp folder (made lazily, by whoever writes
    /// there). Work that runs between the per-test resets, or after the last one, therefore lands in scratch and never in the user's
    /// folders. Also puts a backstop on every WPF window that becomes visible.
    /// </summary>
    internal static void Enable()
    {
        Active = true;
        string scratch = Path.Combine(Path.GetTempPath(), "clink-unittest", "startup-" + Guid.NewGuid().ToString("N"));
        StartupScratch = scratch;
        Settings.DirOverride = scratch;
        Settings.RealDirOverride = Path.Combine(scratch, "RealClink");
        Settings.DefaultClipsFolderOverride = Path.Combine(scratch, "Clips");
        ClipLibrary.IndexPathOverride = Path.Combine(scratch, "clips.json");
        Updater.UpdatesDirOverride = Path.Combine(scratch, "updates");
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) OnWindowShown(w); }));
    }

    /// <summary>The folder a user's Clink really keeps its files in, ignoring every override.</summary>
    internal static string TrueRealDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink");

    /// <summary>Whether <paramref name="path"/> is the true real folder or inside it (a path computation; touches nothing).</summary>
    internal static bool IsUnderTrueRealDir(string path, string? trueRealDir = null)
    {
        string root = Path.GetFullPath(trueRealDir ?? TrueRealDir).TrimEnd('\\', '/');
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other folder a user's Clink keeps files in, <c>%LOCALAPPDATA%\Clink</c> (the updates), ignoring every override.</summary>
    internal static string TrueLocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink");

    /// <summary>The default clips folder, <c>Videos\Clink</c> (the default screenshots folder, <c>Screenshots</c>, is inside it), ignoring every override. The archive has no default folder: it is off until the user picks one.</summary>
    internal static string TrueClipsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink");

    /// <summary>Call before writing, deleting or moving <paramref name="path"/>: in test mode, throws (after recording) when it is under a true user folder (%APPDATA%\Clink, %LOCALAPPDATA%\Clink or Videos\Clink).</summary>
    internal static void Check(string path)
    {
        if (!Active) return;
        string what;
        if (IsUnderTrueRealDir(path)) what = "a write to the real settings folder";
        else if (IsUnderTrueRealDir(path, TrueLocalDir)) what = "a write to the real local Clink folder (updates)";
        else if (IsUnderTrueRealDir(path, TrueClipsDir)) what = "a write to the real clips or screenshots folder";
        else return;
        Refuse(what + " was refused: " + path, "write to the real settings folder");
    }

    /// <summary>
    /// Call before anything that would run a program, open a document, URL or folder, or put a dialog or balloon on the owner's screen:
    /// in test mode, records a violation and throws, so the test fails loudly and nothing happens. Does nothing outside test mode.
    /// A test that means to cover such a path replaces the seam in front of it (e.g. <see cref="Updater.LaunchOverride"/>).
    /// </summary>
    internal static void CheckLaunch(string what)
    {
        if (!Active) return;
        Refuse($"{what} was refused: nothing may be launched or shown from a test; give it a test seam", "launch from a test");
    }

    /// <summary><see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> for the product code: in test mode refused (see <see cref="CheckLaunch"/>).</summary>
    internal static System.Diagnostics.Process? Start(System.Diagnostics.ProcessStartInfo psi)
    {
        if (Active) CheckLaunch($"starting {psi.FileName} {string.Join(' ', psi.ArgumentList)}{psi.Arguments}");
        return System.Diagnostics.Process.Start(psi);
    }

    /// <summary><see cref="System.Diagnostics.Process.Start(string, string)"/> for the product code: in test mode refused (see <see cref="CheckLaunch"/>).</summary>
    internal static System.Diagnostics.Process? Start(string fileName, string arguments)
    {
        if (Active) CheckLaunch($"starting {fileName} {arguments}");
        return System.Diagnostics.Process.Start(fileName, arguments);
    }

    // ---- windows: a WPF window that becomes visible during a test is on the owner's screen.

    /// <summary>Windows a test may legitimately show (a test that needs a real window handle adds its window here and removes it when done). Empty by default.</summary>
    internal static readonly List<Window> AllowedWindows = new();

    private static bool IsShown(Window w)
    {
        try { return w.IsVisible || new System.Windows.Interop.WindowInteropHelper(w).Handle != 0; }
        catch { return false; }
    }

    private static void OnWindowShown(Window w)
    {
        if (!Active || AllowedWindows.Contains(w) || s_handled.TryGetValue(w, out _)) return;
        s_handled.Add(w, s_handled);   // once only: a window that survives Close() is not recorded again by every later sweep
        Record($"a window was shown from a test: {w.GetType().Name} \"{w.Title}\"", "window shown from a test");
        try { w.Close(); } catch { }
        try { if (IsShown(w)) w.Visibility = Visibility.Collapsed; } catch { try { w.Hide(); } catch { } }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> s_handled = new();

    /// <summary>Records and closes every window that is visible or has a window handle (an offscreen window that was only measured has neither), unless it is allowed. The runner calls it after each test.</summary>
    internal static void SweepWindows()
    {
        if (!Active) return;
        var app = Application.Current;
        if (app == null || !app.Dispatcher.CheckAccess()) return;
        foreach (Window w in app.Windows.Cast<Window>().ToArray())
        {
            if (AllowedWindows.Contains(w) || s_handled.TryGetValue(w, out _) || !IsShown(w)) continue;
            OnWindowShown(w);
        }
    }

    private static string Record(string what, string exceptionText)
    {
        string message = $"test guard: {what} (test: {CurrentTest ?? "none running"})\n{Environment.StackTrace}";
        lock (s_violations) s_violations.Add(message);
        Log.Error("[unittest] " + message, new InvalidOperationException(exceptionText));
        return message;
    }

    private static void Refuse(string what, string exceptionText) => throw new InvalidOperationException(Record(what, exceptionText));

    /// <summary>How many refusals are recorded and not yet taken (does not take them).</summary>
    internal static int ViolationCount { get { lock (s_violations) return s_violations.Count; } }

    /// <summary>Returns and forgets what was refused since the last call.</summary>
    internal static string[] TakeViolations()
    {
        lock (s_violations)
        {
            var all = s_violations.ToArray();
            s_violations.Clear();
            return all;
        }
    }

    /// <summary>Puts refusals back (the runner does, for the test that was running when a nested run took them).</summary>
    internal static void AddViolations(IEnumerable<string> violations)
    {
        lock (s_violations) s_violations.InsertRange(0, violations);
    }
}
