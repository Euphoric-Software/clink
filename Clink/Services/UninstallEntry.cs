using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Clink.Services;

/// <summary>
/// Where the "Apps &amp; features" entry of a self-installed Clink lives. Values are strings (REG_SZ) or ints (REG_DWORD). Real:
/// <see cref="RegistryUninstallRegistry"/> (HKCU); a <c>--dev</c> instance uses <see cref="JsonFileUninstallRegistry"/> (a file in
/// the dev folder) so it never touches the real key. Tests use a fake.
/// </summary>
internal interface IUninstallRegistry
{
    /// <summary>The values of the entry; null when there is none.</summary>
    IReadOnlyDictionary<string, object>? Read();
    /// <summary>Creates the entry if needed and sets these values (others are kept).</summary>
    void Write(IReadOnlyDictionary<string, object> values);
    /// <summary>Removes the whole entry; true when something was removed.</summary>
    bool Delete();
}

/// <summary>
/// The per-user uninstall entry (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\EuphoricSoftware.Clink</c>) that makes a
/// copy Clink installed for itself ("Install for me" into Programs\Clink or Documents\Clink) show up in Settings &gt; Apps. Its
/// name differs from the Inno installer's key (<c>{AppId}_is1</c>) on purpose: a copy is managed by one or the other, never both.
/// The Inno script deletes this key at install time (<c>[Registry] ... Flags: deletekey</c>), so the installer cleans up a copy it takes
/// over; <c>uninsdeletekey</c> would be the flag for its own uninstall. Nothing here is written by an elevated
/// process (that would put a per-user entry under the admin account).
/// </summary>
internal static class UninstallEntry
{
    public const string KeyName = "EuphoricSoftware.Clink";
    public const string ParentPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string KeyPath = ParentPath + @"\" + KeyName;
    public const string DisplayNameValue = "Clink";
    public const string PublisherValue = "Euphoric Software";
    public const string AboutUrl = "https://github.com/Euphoric-Software/clink";
    public const string UninstallSwitch = "--uninstall";

    /// <summary>The values of a fresh entry for <paramref name="exePath"/>. <paramref name="sizeBytes"/> is the exe's size (EstimatedSize is in KB).</summary>
    public static Dictionary<string, object> Build(string exePath, string version, long sizeBytes)
    {
        string exe = InstallLocation.Normalize(exePath);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DisplayName"] = DisplayNameValue,
            ["DisplayVersion"] = version,
            ["Publisher"] = PublisherValue,
            ["DisplayIcon"] = exe + ",0",
            ["InstallLocation"] = Path.GetDirectoryName(exe) ?? "",
            ["UninstallString"] = "\"" + exe + "\" " + UninstallSwitch,
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
            ["EstimatedSize"] = (int)Math.Clamp((sizeBytes + 1023) / 1024, 0, int.MaxValue),
            ["URLInfoAbout"] = AboutUrl,
        };
    }

    /// <summary>
    /// True when Clink may add the entry for this exe: not elevated, the exe sits in one of <paramref name="ownFolders"/> (the Programs\Clink
    /// and Documents\Clink of the install choices) and that folder is not managed by the Inno installer.
    /// </summary>
    public static bool ShouldWrite(string exePath, IEnumerable<string> ownFolders, bool elevated, Func<string, bool> isInstallerManaged)
    {
        if (elevated) return false;
        string folder = InstallLocation.Normalize(Path.GetDirectoryName(InstallLocation.Normalize(exePath)));
        if (folder.Length == 0) return false;
        if (!ownFolders.Any(f => InstallLocation.Normalize(f).Equals(folder, StringComparison.OrdinalIgnoreCase))) return false;
        return !isInstallerManaged(folder);
    }

    /// <summary>True when the entry's InstallLocation (a string) is <paramref name="folder"/> (same normalisation: case, slashes, trailing backslash).</summary>
    private static bool LocationMatches(IReadOnlyDictionary<string, object> values, string folder)
    {
        if (folder.Length == 0 || !values.TryGetValue("InstallLocation", out var loc) || loc is not string location) return false;
        string normalized = InstallLocation.Normalize(location);
        return normalized.Length > 0 && normalized.Equals(InstallLocation.Normalize(folder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when an entry exists and its InstallLocation is <paramref name="folder"/>. Throws when the registry cannot be read.</summary>
    public static bool IsOurs(string folder, IUninstallRegistry registry) =>
        registry.Read() is { } current && LocationMatches(current, folder);

    /// <summary>Deletes the entry only when its InstallLocation is <paramref name="folder"/>; an entry for another folder is kept. True when deleted.</summary>
    public static bool DeleteIfOurs(string folder, IUninstallRegistry registry) => IsOurs(folder, registry) && registry.Delete();

    /// <summary>Writes the entry (after the continuation of a move). True when written. Never throws.</summary>
    public static bool Write(string exePath, string version, long sizeBytes, IUninstallRegistry registry, bool elevated,
        IEnumerable<string> ownFolders, Func<string, bool> isInstallerManaged)
    {
        try
        {
            if (!ShouldWrite(exePath, ownFolders, elevated, isInstallerManaged)) return false;
            registry.Write(Build(exePath, version, sizeBytes));
            Log.Info("uninstall entry written for " + exePath);
            return true;
        }
        catch (Exception ex) { Log.Warn("uninstall entry: " + ex.Message); return false; }
    }

    /// <summary>
    /// At a normal start: when the entry exists and its InstallLocation is this exe's folder, brings DisplayVersion (and the size) up
    /// to date. An entry for another folder, or none, is left alone. Skipped when elevated. True when something was rewritten.
    /// </summary>
    public static bool RefreshIfOurs(string exePath, string version, long sizeBytes, IUninstallRegistry registry, bool elevated)
    {
        if (elevated) return false;
        try
        {
            var current = registry.Read();
            string folder = InstallLocation.Normalize(Path.GetDirectoryName(InstallLocation.Normalize(exePath)));
            if (current == null || !LocationMatches(current, folder)) return false;
            var fresh = Build(exePath, version, sizeBytes);
            if (current.TryGetValue("DisplayVersion", out var v) && v is string old && old == version
                && current.TryGetValue("EstimatedSize", out var s) && s is int size && size.Equals(fresh["EstimatedSize"])) return false;
            registry.Write(new Dictionary<string, object> { ["DisplayVersion"] = version, ["EstimatedSize"] = fresh["EstimatedSize"] });
            return true;
        }
        catch (Exception ex) { Log.Warn("uninstall entry refresh: " + ex.Message); return false; }
    }
}

/// <summary>The real HKCU entry. Only ever touches <see cref="UninstallEntry.KeyPath"/>.</summary>
internal sealed class RegistryUninstallRegistry : IUninstallRegistry
{
    public IReadOnlyDictionary<string, object>? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallEntry.KeyPath);
        if (key == null) return null;
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string name in key.GetValueNames())
            if (key.GetValue(name) is string or int) result[name] = key.GetValue(name)!;
        return result;
    }

    public void Write(IReadOnlyDictionary<string, object> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallEntry.KeyPath);
        foreach (var (name, value) in values)
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    public bool Delete()
    {
        using var parent = Registry.CurrentUser.OpenSubKey(UninstallEntry.ParentPath, writable: true);
        if (parent == null || parent.OpenSubKey(UninstallEntry.KeyName) is not { } existing) return false;
        existing.Dispose();
        parent.DeleteSubKeyTree(UninstallEntry.KeyName, throwOnMissingSubKey: false);
        return true;
    }
}

/// <summary><c>--dev-quiet</c>: reads go to the wrapped registry, Write and Delete are only logged.</summary>
internal sealed class DryRunUninstallRegistry : IUninstallRegistry
{
    private readonly IUninstallRegistry _real;
    public DryRunUninstallRegistry(IUninstallRegistry real) => _real = real;
    public IReadOnlyDictionary<string, object>? Read() => _real.Read();
    public void Write(IReadOnlyDictionary<string, object> values) => Log.Info("uninstall entry (dry run): write " + string.Join(", ", values.Keys));
    public bool Delete() { Log.Info("uninstall entry (dry run): delete"); return true; }
}

/// <summary>A <c>--dev</c> instance's stand-in for the entry: <c>uninstall-entry.json</c> in the dev folder.</summary>
internal sealed class JsonFileUninstallRegistry : IUninstallRegistry
{
    public const string FileName = "uninstall-entry.json";
    private readonly string _file;

    public JsonFileUninstallRegistry(string devFolder) => _file = Path.Combine(devFolder, FileName);

    public IReadOnlyDictionary<string, object>? Read()
    {
        if (!File.Exists(_file)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(_file));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String) result[p.Name] = p.Value.GetString() ?? "";
            else if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n)) result[p.Name] = n;
        }
        return result;
    }

    public void Write(IReadOnlyDictionary<string, object> values)
    {
        var merged = new Dictionary<string, object>(Read() ?? new Dictionary<string, object>(), StringComparer.Ordinal);
        foreach (var (name, value) in values) merged[name] = value;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool Delete()
    {
        if (!File.Exists(_file)) return false;
        File.Delete(_file);
        return true;
    }
}
