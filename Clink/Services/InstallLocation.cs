using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Clink.Services;

/// <summary>Where a Clink.exe sits, for the first-run setup (what to warn about, whether it is installed already). See <see cref="InstallLocation.Classify"/>.</summary>
internal enum LocationClass
{
    /// <summary>A UNC path or a drive that GetDriveType calls remote (a mapped network drive).</summary>
    Network,
    /// <summary>A removable (USB stick) or optical drive.</summary>
    Removable,
    /// <summary>Under %TEMP% or Windows\Temp, or in a folder a zip tool extracts to (Explorer's <c>Temp1_x.zip</c>, 7-Zip's <c>7zO...</c>, WinRAR's <c>Rar$EX...</c>).</summary>
    Temp,
    /// <summary>Under Program Files (x86 or x64).</summary>
    ProgramFiles,
    /// <summary>Under %LOCALAPPDATA%\Programs: where the per-user installer and "Install for me" put Clink; already installed.</summary>
    ProgramsFolder,
    /// <summary>Inside a cloud sync root (OneDrive, Dropbox, ...) or the file / its folder carries cloud-file attributes.</summary>
    Cloud,
    Downloads,
    Desktop,
    Documents,
    Other,
}

/// <summary>Reads the cloud sync roots the registry knows about. A seam: tests never read the real registry.</summary>
internal interface ICloudRootReader
{
    /// <summary>Every folder a cloud client has registered (OneDrive, sync-root manager roots of this user, Dropbox, Google Drive); never throws.</summary>
    IReadOnlyList<string> ReadRoots();
}

/// <summary>
/// Read only. OneDrive: HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder. Any sync provider:
/// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\*\UserSyncRoots (value names are SIDs; only this user's count).
/// Dropbox: %LOCALAPPDATA%\Dropbox\info.json and %APPDATA%\Dropbox\info.json. Google Drive: the DriveFS mount point
/// (HKCU\Software\Google\DriveFS\Share\MountPoint, a drive letter), a fixed volume labelled "Google Drive", and the mirror folder
/// %USERPROFILE%\My Drive.
/// </summary>
internal sealed class RegistryCloudRootReader : ICloudRootReader
{
    public IReadOnlyList<string> ReadRoots()
    {
        var roots = new List<string>();
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts != null)
                foreach (string name in accounts.GetSubKeyNames())
                {
                    try
                    {
                        using var k = accounts.OpenSubKey(name);
                        if (k?.GetValue("UserFolder") is string s && s.Length > 0) roots.Add(s);
                    }
                    catch { }
                }
        }
        catch { }
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            using var managers = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (managers != null)
                foreach (string name in managers.GetSubKeyNames())
                {
                    try
                    {
                        using var k = managers.OpenSubKey(name + @"\UserSyncRoots");
                        if (k == null) continue;
                        var values = new List<(string Name, string Value)>();
                        foreach (string v in k.GetValueNames())
                            if (k.GetValue(v) is string s) values.Add((v, s));
                        roots.AddRange(SelectUserSyncRoots(values, sid));
                    }
                    catch { }
                }
        }
        catch { }
        try
        {
            foreach (string? dir in new[] { Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetEnvironmentVariable("APPDATA") })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string info = Path.Combine(dir, "Dropbox", "info.json");
                try { if (File.Exists(info)) roots.AddRange(ParseDropboxInfo(File.ReadAllText(info))); } catch { }
            }
        }
        catch { }
        try
        {
            using var share = Registry.CurrentUser.OpenSubKey(@"Software\Google\DriveFS\Share");
            if (share?.GetValue("MountPoint") is string mount) { string? m = GoogleDriveMountRoot(mount); if (m != null) roots.AddRange(GoogleDriveFolders(m)); }
        }
        catch { }
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == System.IO.DriveType.Fixed && d.IsReady && string.Equals(d.VolumeLabel, "Google Drive", StringComparison.OrdinalIgnoreCase)) roots.AddRange(GoogleDriveFolders(d.RootDirectory.FullName));
                }
                catch { }
            }
        }
        catch { }
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0)
            {
                string mirror = Path.Combine(profile, "My Drive");
                if (Directory.Exists(mirror)) roots.Add(mirror);
            }
        }
        catch { }
        return roots;
    }

    /// <summary>The values of one UserSyncRoots key that belong to the user with this SID (the value names are SIDs). With no SID known, none.</summary>
    internal static IEnumerable<string> SelectUserSyncRoots(IEnumerable<(string Name, string Value)> values, string? currentUserSid)
    {
        if (string.IsNullOrEmpty(currentUserSid)) yield break;
        foreach (var (name, value) in values)
            if (value.Length > 0 && string.Equals(name, currentUserSid, StringComparison.OrdinalIgnoreCase)) yield return value;
    }

    /// <summary>The folders named in a Dropbox <c>info.json</c> (<c>{"personal":{"path":"..."},"business":{"path":"..."}}</c>); empty on anything else.</summary>
    internal static IReadOnlyList<string> ParseDropboxInfo(string? json)
    {
        var list = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
            foreach (var account in doc.RootElement.EnumerateObject())
                if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                    list.Add(p.GetString()!);
        }
        catch { }
        return list;
    }

    /// <summary>The cloud folders on a Google Drive virtual drive. The drive root itself is too broad a "sync root" (the classifier ignores drive roots), so its folders are named.</summary>
    internal static IReadOnlyList<string> GoogleDriveFolders(string driveRoot) => new[] { Path.Combine(driveRoot, "My Drive"), Path.Combine(driveRoot, "Shared drives"), Path.Combine(driveRoot, "Computers") };

    /// <summary>The DriveFS "MountPoint" value (a drive letter such as <c>G</c>, <c>G:</c> or <c>G:\</c>) as a root <c>G:\</c>; null for anything else.</summary>
    internal static string? GoogleDriveMountRoot(string? mountPoint)
    {
        string m = (mountPoint ?? "").Trim().TrimEnd('\\', ':');
        return m.Length == 1 && char.IsAsciiLetter(m[0]) ? char.ToUpperInvariant(m[0]) + @":\" : null;
    }
}
/// <summary>Everything <see cref="InstallLocation"/> looks at, injected. A null or empty folder is "not known" and matches nothing.</summary>
internal sealed record InstallEnvironment
{
    public string? Downloads { get; init; }
    public string? Desktop { get; init; }
    public string? Documents { get; init; }
    /// <summary>%LOCALAPPDATA%\Programs.</summary>
    public string? ProgramsFolder { get; init; }
    /// <summary>Program Files, Program Files (x86), ProgramW6432.</summary>
    public IReadOnlyList<string> ProgramFiles { get; init; } = Array.Empty<string>();
    /// <summary>%TEMP% and Windows\Temp.</summary>
    public IReadOnlyList<string> Temp { get; init; } = Array.Empty<string>();
    /// <summary>The OneDrive, OneDriveConsumer and OneDriveCommercial environment variables.</summary>
    public IReadOnlyList<string> CloudEnvRoots { get; init; } = Array.Empty<string>();
    public ICloudRootReader? CloudReader { get; init; }
    /// <summary>GetDriveType of a root such as <c>C:\</c>: 2 removable, 3 fixed, 4 remote, 5 optical.</summary>
    public Func<string, uint> DriveTypeOf { get; init; } = _ => 3;
    /// <summary>File attributes of a path (0 when it does not exist or cannot be read); looked at for the cloud-file bits.</summary>
    public Func<string, int> AttributesOf { get; init; } = _ => 0;

    /// <summary>The real machine.</summary>
    public static InstallEnvironment FromSystem()
    {
        static string Env(string n) => Environment.GetEnvironmentVariable(n) ?? "";
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new InstallEnvironment
        {
            Downloads = KnownFolderPaths.Downloads(),
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ProgramsFolder = local.Length > 0 ? Path.Combine(local, "Programs") : null,
            ProgramFiles = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Env("ProgramW6432") }
                .Where(s => s.Length > 0).ToArray(),
            Temp = new[] { Path.GetTempPath(), windows.Length > 0 ? Path.Combine(windows, "Temp") : "" }.Where(s => s.Length > 0).ToArray(),
            CloudEnvRoots = new[] { Env("OneDrive"), Env("OneDriveConsumer"), Env("OneDriveCommercial") }.Where(s => s.Length > 0).ToArray(),
            CloudReader = new RegistryCloudRootReader(),
            DriveTypeOf = root => Windows.Win32.PInvoke.GetDriveType(root),
            AttributesOf = p =>
            {
                try { return (int)File.GetAttributes(p); } catch { return 0; }
            },
        };
    }
}

/// <summary>The Downloads folder has no Environment.SpecialFolder; the shell knows it.</summary>
internal static unsafe class KnownFolderPaths
{
    private static readonly Guid s_downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder (follows a redirect); null when the shell cannot say.</summary>
    public static string? Downloads()
    {
        try
        {
            Guid id = s_downloads;
            Windows.Win32.PInvoke.SHGetKnownFolderPath(in id, Windows.Win32.UI.Shell.KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, out Windows.Win32.Foundation.PWSTR p).ThrowOnFailure();
            try { return new string(p.Value); }
            finally { Windows.Win32.PInvoke.CoTaskMemFree(p.Value); }
        }
        catch
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return profile.Length > 0 ? Path.Combine(profile, "Downloads") : null;
        }
    }
}

/// <summary>
/// Pure classifier for where the running Clink.exe sits. Precedence, first match wins:
/// Network, Removable, Temp, ProgramFiles, ProgramsFolder (= installed), Cloud, Downloads, Desktop, Documents, Other.
/// So an exe in a OneDrive-redirected Downloads, Desktop or Documents folder is <see cref="LocationClass.Cloud"/>.
/// Paths are compared case-insensitively, without trailing separators, without a <c>\\?\</c> prefix.
/// </summary>
internal static class InstallLocation
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | PINNED | UNPINNED. (0x40000, RECALL_ON_OPEN, is left out: it also marks non-cloud placeholder files.)</summary>
    public const int CloudAttributeMask = 0x400000 | 0x80000 | 0x100000;

    private static readonly Regex s_zipTemp = new(@"^(Temp\d+_.*|.*\.zip|7z[OE][0-9A-Za-z]+|Rar\$.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex s_duplicateName = new(@"^.*\s?\(\d+\)\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static LocationClass Classify(string? exePath, InstallEnvironment env)
    {
        string p = Normalize(exePath);
        if (p.Length == 0) return LocationClass.Other;

        uint dt = DriveType(p, env);
        if (IsUnc(p) || dt == 4) return LocationClass.Network;
        if (dt == 2 || dt == 5) return LocationClass.Removable;
        if (IsTemp(p, env)) return LocationClass.Temp;
        if (UnderAny(p, env.ProgramFiles)) return LocationClass.ProgramFiles;
        if (Under(p, env.ProgramsFolder)) return LocationClass.ProgramsFolder;
        if (IsCloud(p, env)) return LocationClass.Cloud;
        if (Under(p, env.Downloads)) return LocationClass.Downloads;
        if (Under(p, env.Desktop)) return LocationClass.Desktop;
        if (Under(p, env.Documents)) return LocationClass.Documents;
        return LocationClass.Other;
    }

    /// <summary>The Documents folder lives in a cloud sync root (or carries cloud attributes): "Documents\Clink" would be uploaded and could be evicted.</summary>
    public static bool IsDocumentsCloudSynced(InstallEnvironment env)
    {
        string d = Normalize(env.Documents);
        if (d.Length == 0) return false;
        return IsCloud(d, env);
    }

    /// <summary>"Clink (1).exe" or "Clink(1).exe": a browser's name for a second download of the same file.</summary>
    public static bool IsBrowserDuplicateName(string? path)
        => !string.IsNullOrEmpty(path) && s_duplicateName.IsMatch(Path.GetFileName(Normalize(path)));

    /// <summary>The cloud sync roots, normalised and without duplicates: the environment's plus the reader's.</summary>
    public static IReadOnlyList<string> CloudRoots(InstallEnvironment env) => s_rootCache.GetValue(env, ComputeCloudRoots);

    /// <summary>Read once per environment (the registry and disk are not asked again for every question).</summary>
    private static readonly ConditionalWeakTable<InstallEnvironment, IReadOnlyList<string>> s_rootCache = new();

    private static IReadOnlyList<string> ComputeCloudRoots(InstallEnvironment env)
    {
        var all = new List<string>(env.CloudEnvRoots);
        try { if (env.CloudReader != null) all.AddRange(env.CloudReader.ReadRoots()); } catch { }
        return all.Select(Normalize).Where(s => s.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsCloud(string normalized, InstallEnvironment env)
    {
        foreach (string root in CloudRoots(env))
            if (Under(normalized, root)) return true;
        try
        {
            if ((env.AttributesOf(normalized) & CloudAttributeMask) != 0) return true;
            string? dir = Path.GetDirectoryName(normalized);
            if (!string.IsNullOrEmpty(dir) && (env.AttributesOf(dir) & CloudAttributeMask) != 0) return true;
        }
        catch { }
        return false;
    }

    private static bool IsTemp(string p, InstallEnvironment env)
    {
        if (UnderAny(p, env.Temp)) return true;
        string? dir = Path.GetDirectoryName(p);
        while (!string.IsNullOrEmpty(dir))
        {
            string name = Path.GetFileName(dir);
            if (name.Length > 0 && s_zipTemp.IsMatch(name)) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private static uint DriveType(string p, InstallEnvironment env)
    {
        if (IsUnc(p)) return 4;
        string root = Path.GetPathRoot(p) ?? "";
        if (root.Length < 2 || root[1] != ':') return 0;
        try { return env.DriveTypeOf(root.Length == 2 ? root + "\\" : root); } catch { return 0; }
    }

    // ------------------------------------------------------------------ paths

    public static bool IsUnc(string normalized) => normalized.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// Forward slashes become backslashes, <c>\\?\C:\x</c> becomes <c>C:\x</c>, <c>\\?\UNC\srv\share</c> becomes <c>\\srv\share</c>,
    /// dot segments are resolved, trailing separators dropped (except a drive root's). No file system access. Empty for blank input.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
        try { if (Path.IsPathFullyQualified(p)) p = Path.GetFullPath(p); } catch { }
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }

    /// <summary>The same folder or a path inside it (never a sibling that merely starts with the same letters).</summary>
    public static bool Under(string normalizedPath, string? folder)
    {
        string f = Normalize(folder);
        if (f.Length == 0) return false;
        if (normalizedPath.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = f.EndsWith('\\') ? f : f + "\\";
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool UnderAny(string normalizedPath, IReadOnlyList<string> folders)
    {
        foreach (string f in folders)
            if (Under(normalizedPath, f)) return true;
        return false;
    }
}
