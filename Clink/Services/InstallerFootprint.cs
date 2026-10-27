using System.IO;
using Microsoft.Win32;

namespace Clink.Services;

/// <summary>What <see cref="InstallerFootprint"/> asks of the machine. Real: <see cref="InstallerProbe"/>; tests pass a fake (the real registry is never read by them).</summary>
internal interface IInstallerProbe
{
    /// <summary>True when the folder holds an Inno uninstaller (<c>unins*.exe</c>).</summary>
    bool HasUninstaller(string folder);
    /// <summary>The <c>InstallLocation</c> value of the Inno uninstall key of Clink (HKCU, <see cref="InstallerFootprint.InnoUninstallKey"/>); null when there is none.</summary>
    string? InnoInstallLocation();
}

/// <summary>Is a folder managed by the Inno Setup installer of Clink (so a copy placed there by hand would fight it)?</summary>
internal static class InstallerFootprint
{
    /// <summary>The installer's AppId (installer/Clink.iss).</summary>
    public const string InnoAppId = "{7D4A9E3B-5C21-4F6A-9B08-2E1C6A5D3F74}";
    /// <summary>Inno names the uninstall key <c>&lt;AppId&gt;_is1</c>.</summary>
    public const string InnoUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId + "_is1";

    /// <summary>True when <paramref name="folder"/> has an <c>unins*.exe</c> or is the <c>InstallLocation</c> of Clink's Inno uninstall key. Never throws.</summary>
    public static bool IsManaged(string? folder, IInstallerProbe probe)
    {
        string f = InstallLocation.Normalize(folder);
        if (f.Length == 0) return false;
        try { if (probe.HasUninstaller(f)) return true; } catch { }
        try
        {
            string recorded = InstallLocation.Normalize(probe.InnoInstallLocation());
            return recorded.Length > 0 && recorded.Equals(f, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>The real folder and the real registry (read only).</summary>
internal sealed class InstallerProbe : IInstallerProbe
{
    public bool HasUninstaller(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "unins*.exe").Any(); }
        catch { return false; }
    }

    public string? InnoInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallerFootprint.InnoUninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch { return null; }
    }
}
