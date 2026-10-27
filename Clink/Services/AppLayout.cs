using System.IO;

namespace Clink.Services;

/// <summary>How this copy of Clink is deployed. "Portable" is simply the bare <c>Clink.exe</c> (<see cref="Standalone"/>): there is no marker file and no separate data folder.</summary>
internal enum AppMode
{
    /// <summary><c>--dev</c> instance: isolated by DevOptions, the layout changes nothing.</summary>
    Dev,
    /// <summary>The exe runs from <c>%LOCALAPPDATA%\Programs\Clink</c> (where the installer puts it): data in the usual places.</summary>
    Installed,
    /// <summary>A bare exe somewhere else (today's way of running Clink, the "portable" download): data in the usual places.</summary>
    Standalone,
}

/// <summary>
/// Where a Clink copy runs from, worked out from the exe path and %LOCALAPPDATA%. Pure: both are passed in, so every case is
/// unit-testable (<see cref="Resolve"/>). Nothing here reads the environment or touches the disk by itself except
/// <see cref="ForCurrentProcess"/>. Every mode keeps its settings in <c>%APPDATA%\Clink</c> and its updates in
/// <see cref="UpdatesDir"/> (<c>%LOCALAPPDATA%\Clink\updates</c>, today's folder: the base shared by both flavours; a Framework copy downloads
/// into its <c>fx</c> subfolder, see <c>Updater.UpdatesDirFor</c>); the mode only says how the copy was deployed.
/// </summary>
internal sealed record AppLayout(AppMode Mode, string? ExeDir, string? UpdatesDir)
{
    /// <summary>
    /// The layout for <paramref name="exePath"/>; <paramref name="localAppData"/> is %LOCALAPPDATA%. Precedence: Dev, then
    /// Installed (the exe's folder is <c>%LOCALAPPDATA%\Programs\Clink</c>, compared normalised and case-insensitively), else Standalone.
    /// An empty or relative <paramref name="localAppData"/> (no profile folder) is never resolved against the current directory:
    /// nothing is Installed then and <see cref="UpdatesDir"/> is null.
    /// </summary>
    public static AppLayout Resolve(string? exePath, bool isDev, string? localAppData)
    {
        string? local = string.IsNullOrWhiteSpace(localAppData) ? null : localAppData.Trim();
        if (local != null && !Path.IsPathFullyQualified(local)) local = null;
        string? todaysUpdates = local == null ? null : Path.Combine(local, "Clink", "updates");
        string? exeDir = ExeDirOf(exePath);
        if (isDev) return new AppLayout(AppMode.Dev, exeDir, todaysUpdates);
        if (exeDir == null || local == null) return new AppLayout(AppMode.Standalone, exeDir, todaysUpdates);
        string installedDir;
        try { installedDir = Normalize(Path.Combine(local, "Programs", "Clink")); }
        catch { return new AppLayout(AppMode.Standalone, exeDir, todaysUpdates); }
        var mode = string.Equals(exeDir, installedDir, StringComparison.OrdinalIgnoreCase) ? AppMode.Installed : AppMode.Standalone;
        return new AppLayout(mode, exeDir, todaysUpdates);
    }

    /// <summary><see cref="Resolve"/> for the running process and the real %LOCALAPPDATA%.</summary>
    public static AppLayout ForCurrentProcess(bool isDev) => Resolve(
        Environment.ProcessPath, isDev, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

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
}
