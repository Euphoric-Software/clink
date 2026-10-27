using System.IO;

namespace Clink.Services;

/// <summary>How this copy of Clink is deployed, which decides where its data lives.</summary>
internal enum AppMode
{
    /// <summary><c>--dev</c> instance: isolated by DevOptions, the layout changes nothing.</summary>
    Dev,
    /// <summary>The file <c>Clink.portable</c> sits beside the exe: all data lives in <c>&lt;exeDir&gt;\ClinkData</c>.</summary>
    Portable,
    /// <summary>The exe runs from <c>%LOCALAPPDATA%\Programs\Clink</c> (where the installer puts it): data in the usual places.</summary>
    Installed,
    /// <summary>A bare exe somewhere else (today's way of running Clink): data in the usual places.</summary>
    Standalone,
}

/// <summary>
/// Where a Clink copy keeps its data, worked out from the exe path and a few known folders. Pure: the exe path, the known folders
/// and the file-exists check are passed in, so every case is unit-testable (<see cref="Resolve"/>). Nothing here reads the
/// environment or touches the disk by itself, except <see cref="IsDataDirWritable"/> with its default probe.
/// <para>
/// What <c>App.xaml.cs</c> does with it (wiring slice), before <c>Settings.Load()</c>:
/// <code>
/// var layout = AppLayout.ForCurrentProcess(isDev: AppController.IsDev);
/// if (layout.DataDir != null)
/// {
///     if (!layout.IsDataDirWritable()) { /* tell the user, exit or fall back */ }
///     Settings.DirOverride = layout.DataDir;
///     Settings.PortableRoot = layout.DataDir;
///     Settings.DefaultClipsFolderOverride = layout.DefaultClipsFolder;
///     Updater.UpdatesDirOverride = layout.UpdatesDir;
/// }
/// </code>
/// Dev, Installed and Standalone have a null <see cref="DataDir"/> and today's folders in the other properties.
/// </para>
/// </summary>
internal sealed record AppLayout(AppMode Mode, string? ExeDir, string? DataDir, string UpdatesDir, string DefaultClipsFolder)
{
    /// <summary>Name of the marker file beside the exe that switches on portable mode.</summary>
    public const string PortableMarker = "Clink.portable";
    /// <summary>Data folder name beside the exe in portable mode.</summary>
    public const string PortableDataFolder = "ClinkData";

    public bool IsPortable => Mode == AppMode.Portable;

    /// <summary>
    /// The layout for <paramref name="exePath"/>. <paramref name="localAppData"/> and <paramref name="myVideos"/> are the
    /// known folders (%LOCALAPPDATA%, the Videos folder); <paramref name="fileExists"/> answers whether a file exists.
    /// Precedence: Dev, then Portable (marker present), then Installed, else Standalone.
    /// </summary>
    public static AppLayout Resolve(string? exePath, bool isDev, string localAppData, string myVideos, Func<string, bool> fileExists)
    {
        string todaysUpdates = Path.Combine(localAppData, "Clink", "updates");
        string todaysClips = Path.Combine(myVideos, "Clink");
        string? exeDir = ExeDirOf(exePath);
        if (isDev) return new AppLayout(AppMode.Dev, exeDir, null, todaysUpdates, todaysClips);
        if (exeDir == null) return new AppLayout(AppMode.Standalone, null, null, todaysUpdates, todaysClips);

        if (SafeExists(fileExists, Path.Combine(exeDir, PortableMarker)))
        {
            string data = Path.Combine(exeDir, PortableDataFolder);
            return new AppLayout(AppMode.Portable, exeDir, data, Path.Combine(data, "updates"), Path.Combine(data, "Clips"));
        }
        string installedDir = Normalize(Path.Combine(localAppData, "Programs", "Clink"));
        var mode = string.Equals(exeDir, installedDir, StringComparison.OrdinalIgnoreCase) ? AppMode.Installed : AppMode.Standalone;
        return new AppLayout(mode, exeDir, null, todaysUpdates, todaysClips);
    }

    /// <summary><see cref="Resolve"/> for the running process with the real known folders and the real disk.</summary>
    public static AppLayout ForCurrentProcess(bool isDev) => Resolve(
        Environment.ProcessPath, isDev,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        File.Exists);

    /// <summary>
    /// True when there is no data folder to write to (not portable) or <paramref name="probe"/> says it can be written (the
    /// default probe creates the folder and a scratch file in it). A portable copy on a read-only stick or in Program Files fails here.
    /// </summary>
    public bool IsDataDirWritable(Func<string, bool>? probe = null)
    {
        if (DataDir == null) return true;
        try { return (probe ?? ProbeWritable)(DataDir); }
        catch { return false; }
    }

    private static bool ProbeWritable(string dir)
    {
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $".write-test.{Environment.ProcessId}");
        using (new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return true;
    }

    private static bool SafeExists(Func<string, bool> fileExists, string path)
    {
        try { return fileExists(path); }
        catch { return false; }
    }

    private static string? ExeDirOf(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(exePath.Trim()));
            return string.IsNullOrEmpty(dir) ? null : Normalize(dir);
        }
        catch { return null; }
    }

    /// <summary>Full path without trailing separators (a drive root keeps its backslash).</summary>
    internal static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    // ------------------------------------------------------------------ portable paths in settings.json

    /// <summary>
    /// The text to save for <paramref name="path"/> when the data root is <paramref name="root"/>: a path inside the root becomes
    /// relative to it ("Clips", "Shots\2026"; "." for the root itself), so it survives a changed drive letter; anything else, and
    /// every path when <paramref name="root"/> is null, is returned unchanged.
    /// </summary>
    internal static string? ToStoredPath(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return path;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return path;
            string full = Normalize(path), r = Normalize(root);
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase)) return ".";
            string prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full[prefix.Length..] : path;
        }
        catch { return path; }
    }

    /// <summary>
    /// The inverse of <see cref="ToStoredPath"/>: a relative <paramref name="stored"/> is resolved against <paramref name="root"/>;
    /// an absolute one, and anything when <paramref name="root"/> is null, comes back unchanged. A relative path that climbs out
    /// of the root ("..\x") is not resolved either (returned unchanged, which Settings then discards as not a full path).
    /// </summary>
    internal static string? FromStoredPath(string? stored, string? root)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(root)) return stored;
        try
        {
            string s = stored.Trim();
            if (Path.IsPathRooted(s)) return stored;
            string r = Normalize(root);
            string full = Normalize(Path.Combine(r, s));
            bool inside = string.Equals(full, r, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return inside ? full : stored;
        }
        catch { return stored; }
    }
}
