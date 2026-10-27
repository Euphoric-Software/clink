using System.IO;
using Microsoft.Win32;

namespace Clink.Services;

/// <summary>The "Clink" value of the per-user Run key. A seam: tests never touch the real registry. Real: <see cref="RegistryRunKey"/>.</summary>
internal interface IRunKey
{
    /// <summary>The value, or null when there is none.</summary>
    string? Read();
    void Write(string value);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "Clink" (the one <see cref="AppController.SetRunAtStartup"/> writes).</summary>
internal sealed class RegistryRunKey : IRunKey
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Clink";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public void Write(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true) ?? throw new IOException("The Run key could not be opened.");
        key.SetValue(ValueName, value);
    }
}

internal enum RunKeyOutcome
{
    /// <summary>No "Clink" value: start-up was not on.</summary>
    NotPresent,
    /// <summary>The value pointed at the old copy (or the same file) and now points at the new exe.</summary>
    Retargeted,
    /// <summary>The value already points at the running exe.</summary>
    AlreadyCurrent,
    /// <summary>The value starts something else (another copy of Clink, another program): left alone.</summary>
    NotOurs,
    /// <summary>A --dev instance: the registry is never touched, the log says what would have happened.</summary>
    DevLogOnly,
    /// <summary>Elevated: this is not the user's own Run key; left alone.</summary>
    SkippedElevated,
    /// <summary>No usable moved-from record.</summary>
    NoRecord,
    Failed,
}

internal enum BackupOutcome
{
    NoBackup,
    Recycled,
    /// <summary>Quiet mode: logged "would recycle", nothing moved.</summary>
    DryRun,
    /// <summary>Could not go to the Recycle Bin (removable / network / Program Files, or the shell failed): left in place.</summary>
    Left,
    /// <summary>Elevated, or a --dev instance and the file is outside its dev folder.</summary>
    Skipped,
}

/// <param name="Links">What happened to each Start-menu / desktop link that pointed at the old copy.</param>
/// <param name="Cleanup">What happened to the old copy; null when nothing was tried.</param>
/// <param name="DryRun">Quiet mode: the recycling steps only logged.</param>
/// <param name="Steps">The steps taken, in order: <c>runkey</c>, <c>links</c>, <c>backup</c>, <c>cleanup</c>.</param>
internal sealed record ContinuationReport(RunKeyOutcome RunKey, IReadOnlyList<ShortcutResult> Links, BackupOutcome Backup, SelfInstall.CleanupOutcome? Cleanup, bool DryRun, IReadOnlyList<string> Steps);

/// <summary>
/// Quiet mode (<c>--dev --dev-quiet</c>): the shell's Recycle Bin operation runs on its own thread outside the quiet window hook, so it could pop
/// a dialog. This wrapper hands every call to the real system except <see cref="Recycle"/>, which only logs "quiet: would recycle ..." and makes the
/// file look gone (so the caller's flow ends as if it worked).
/// </summary>
internal sealed class DryRunRecycleSystem : ISelfInstallSystem
{
    private readonly ISelfInstallSystem _inner;
    private readonly HashSet<string> _gone = new(StringComparer.OrdinalIgnoreCase);
    public List<string> WouldRecycle { get; } = new();

    public DryRunRecycleSystem(ISelfInstallSystem inner) => _inner = inner;

    public void Recycle(string path, nint? ownerHwnd)
    {
        WouldRecycle.Add(path);
        _gone.Add(InstallLocation.Normalize(path));
        Log.Info("quiet: would recycle " + path);
    }

    public bool FileExists(string path) => !_gone.Contains(InstallLocation.Normalize(path)) && _inner.FileExists(path);
    public bool IsReadOnly(string path) => _inner.IsReadOnly(path);
    public void ClearReadOnly(string path) => _inner.ClearReadOnly(path);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public string Sha256(string path) => _inner.Sha256(path);
    public void Copy(string from, string to) => _inner.Copy(from, to);
    public void Move(string from, string to, bool overwrite) => _inner.Move(from, to, overwrite);
    public void Delete(string path) => _inner.Delete(path);
    public bool? SameFile(string a, string b) => _inner.SameFile(a, b);
    public ReleaseVersion? VersionOf(string exePath) => _inner.VersionOf(exePath);
    public bool LooksLikeClink(string exePath) => _inner.LooksLikeClink(exePath);
    public void StripZoneIdentifier(string path) => _inner.StripZoneIdentifier(path);
    public IChildProcess Start(string exePath, IReadOnlyList<string> args) => _inner.Start(exePath, args);
    public uint DriveTypeOf(string root) => _inner.DriveTypeOf(root);
    public IReadOnlyList<string> ProgramFilesFolders() => _inner.ProgramFilesFolders();
    public void Sleep(int milliseconds) => _inner.Sleep(milliseconds);
}

/// <summary>
/// What the new copy does after the wizard moved Clink.exe to its new home (started with <c>--setup-continue</c>), in this order:
/// (1) the Run key value "Clink" is pointed at the new exe, but only when it pointed at the old copy; (2) Start-menu / desktop links to the
/// old copy are pointed at the new exe; (3) the <c>Clink.exe.bak</c> the move left next to the new exe goes to the Recycle Bin (the updater would
/// delete it for good later); (4) the old copy goes to the Recycle Bin (<see cref="SelfInstall.CleanupOldCopy"/>, after a hash check).
/// A <c>--dev</c> instance never touches the registry (it logs) and only the dev folder's files and links. Under quiet mode the recycling is a dry run.
/// Nothing here throws.
/// </summary>
internal static class SetupContinuation
{
    /// <summary>
    /// Steps 1 and 2 run on the calling thread, which must be the UI thread (the shell-link COM objects need its apartment); steps 3 and 4
    /// (Recycle Bin, retries) run on a worker. <paramref name="owner"/> is read once, up front, on the calling thread (the owner window of shell dialogs).
    /// <paramref name="linkFolders"/>: the Start menu Programs and Desktop folders (a --dev instance passes only folders inside its dev folder).
    /// </summary>
    public static async Task<ContinuationReport> RunAsync(string? movedFrom, string? sha256, string currentExe, ISelfInstallSystem sys, IRunKey runKey, IInstallerProbe installerProbe,
        string? devFolder, bool elevated, bool quiet, Func<nint?> owner, IReadOnlyList<string>? linkFolders = null, bool notifyShell = true)
    {
        var steps = new List<string>();
        bool dev = !string.IsNullOrWhiteSpace(devFolder);
        string from = InstallLocation.Normalize(movedFrom), exe = InstallLocation.Normalize(currentExe);
        bool haveRecord = SelfInstall.IsValidMoveRecord(movedFrom, sha256);
        nint? hwnd = null;
        try { hwnd = owner(); } catch { }

        // What the moved-from file is, looked at only on a local fixed drive (a UNC or mapped path is never probed): the wizard never moves FROM an
        // installer-managed folder, and a record that names something that exists and is not Clink is not ours to rewrite shortcuts for.
        bool fromLocal = haveRecord && SelfInstall.IsLocalFixed(from, sys);
        bool fromManaged = false, fromNotClink = false;
        if (fromLocal)
        {
            try { fromManaged = InstallerFootprint.IsManaged(Path.GetDirectoryName(from), installerProbe); } catch { fromManaged = false; }
            try { fromNotClink = !fromManaged && sys.FileExists(from) && !sys.LooksLikeClink(from); } catch { fromNotClink = false; }
        }
        bool foreignFrom = fromManaged || fromNotClink;
        if (foreignFrom) Log.Warn("setup: the moved-from file " + from + (fromManaged ? " is in an installer-managed folder" : " is not Clink") + "; the Run key, links and old copy are left alone");

        // (1) the Run key
        steps.Add("runkey");
        RunKeyOutcome runOutcome;
        try
        {
            if (!haveRecord) runOutcome = RunKeyOutcome.NoRecord;
            else if (dev) { runOutcome = RunKeyOutcome.DevLogOnly; Log.Info("setup: dev instance, the Run key is left alone (would point it at " + exe + " if it started " + from + ")"); }
            else if (elevated) runOutcome = RunKeyOutcome.SkippedElevated;
            else if (foreignFrom) runOutcome = RunKeyOutcome.NotOurs;
            else runOutcome = RetargetRunKey(runKey, from, exe, sys, fromLocal);
        }
        catch (Exception ex) { Log.Warn("setup: Run key: " + ex.Message); runOutcome = RunKeyOutcome.Failed; }

        // (2) the links
        steps.Add("links");
        var links = new List<ShortcutResult>();
        if (haveRecord && !elevated && !foreignFrom && linkFolders != null)
        {
            foreach (string folder in linkFolders)
            {
                try
                {
                    string link = Shortcuts.LinkPath(folder);
                    if (!File.Exists(link)) continue;
                    string? target = Shortcuts.ReadTarget(link);
                    if (target == null || !InstallLocation.Normalize(Environment.ExpandEnvironmentVariables(target)).Equals(from, StringComparison.OrdinalIgnoreCase)) continue;
                    var r = Shortcuts.Create(folder, exe, "Clink", notifyShell, dev ? devFolder : null, previousExe: from);
                    links.Add(r);
                    Log.Info($"setup: link {r.Path}: {r.Outcome}{(r.Detail == null ? "" : " (" + r.Detail + ")")}");
                }
                catch (Exception ex) { Log.Warn("setup: link in " + folder + ": " + ex.Message); }
            }
        }

        // (3) and (4), off the UI thread
        var recycleSys = quiet ? new DryRunRecycleSystem(sys) : sys;
        var (backup, cleanup) = await Task.Run(() =>
        {
            if (!haveRecord) return (BackupOutcome.Skipped, (SelfInstall.CleanupOutcome?)null);   // not a real continuation: nothing is removed
            steps.Add("backup");
            var b = RecycleBackup(exe, recycleSys, hwnd, devFolder, elevated, quiet);
            steps.Add("cleanup");
            SelfInstall.CleanupOutcome c;
            if (foreignFrom) { Log.Info("setup: old copy " + from + ": left alone"); return (b, (SelfInstall.CleanupOutcome?)SelfInstall.CleanupOutcome.NotRecyclable); }
            try { c = SelfInstall.CleanupOldCopy(movedFrom, sha256, exe, recycleSys, elevated, hwnd, devFolder: dev ? devFolder : null); }
            catch (Exception ex) { Log.Warn("setup: cleanup: " + ex.Message); c = SelfInstall.CleanupOutcome.Failed; }
            Log.Info("setup: old copy " + from + ": " + c);
            return (b, (SelfInstall.CleanupOutcome?)c);
        }).ConfigureAwait(false);

        return new ContinuationReport(runOutcome, links, backup, cleanup, quiet, steps);
    }

    private static RunKeyOutcome RetargetRunKey(IRunKey runKey, string from, string exe, ISelfInstallSystem sys, bool fromLocal)
    {
        string? value = runKey.Read();
        if (string.IsNullOrWhiteSpace(value)) return RunKeyOutcome.NotPresent;
        string? valueExe = RunValueExe(value);
        if (valueExe != null && InstallLocation.Normalize(valueExe).Equals(exe, StringComparison.OrdinalIgnoreCase)) return RunKeyOutcome.AlreadyCurrent;
        // file identity only when both paths are on a local fixed drive (a UNC or mapped path is never opened); an exact string match needs no check
        string? updated = RetargetRunValue(value, from, exe, p => fromLocal && SelfInstall.IsLocalFixed(p, sys) && sys.SameFile(p, from) == true);
        if (updated == null) return RunKeyOutcome.NotOurs;
        runKey.Write(updated);
        Log.Info("setup: Run key now starts " + exe);
        return RunKeyOutcome.Retargeted;
    }

    private static BackupOutcome RecycleBackup(string exe, ISelfInstallSystem sys, nint? hwnd, string? devFolder, bool elevated, bool quiet)
    {
        try
        {
            string bak = exe + ".bak";
            if (!sys.FileExists(bak)) return BackupOutcome.NoBackup;
            if (elevated) return BackupOutcome.Skipped;
            if (!string.IsNullOrWhiteSpace(devFolder) && !InstallLocation.Under(bak, devFolder)) return BackupOutcome.Skipped;
            if (!SelfInstall.IsRecyclableLocation(InstallLocation.Normalize(bak), sys) || !sys.LooksLikeClink(bak)) return BackupOutcome.Left;   // a file that is not Clink is never recycled, whatever its name
            if (!SelfInstall.TryRecycle(sys, bak, hwnd)) return BackupOutcome.Left;
            return quiet ? BackupOutcome.DryRun : BackupOutcome.Recycled;
        }
        catch (Exception ex) { Log.Warn("setup: backup: " + ex.Message); return BackupOutcome.Left; }
    }

    /// <summary>
    /// The exe a Run value starts: the quoted part, or everything before the first " --" (or the whole value). Null for a blank value or an
    /// unterminated quote.
    /// </summary>
    public static string? RunValueExe(string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v[0] == '"')
        {
            int end = v.IndexOf('"', 1);
            return end > 1 ? v[1..end] : null;
        }
        int sw = v.IndexOf(" --", StringComparison.Ordinal);
        string exe = (sw < 0 ? v : v[..sw]).Trim();
        return exe.Length == 0 ? null : exe;
    }

    /// <summary>
    /// The new Run value (<c>"&lt;newExe&gt;" --minimized</c>) when <paramref name="value"/> starts <paramref name="movedFrom"/> (same path, whatever the
    /// case and spelling; or, through <paramref name="sameFile"/>, the same file); null when it starts anything else or the new exe already.
    /// </summary>
    public static string? RetargetRunValue(string? value, string movedFrom, string newExe, Func<string, bool>? sameFile = null)
    {
        string? exe = RunValueExe(value);
        if (exe == null) return null;
        string run = InstallLocation.Normalize(exe), old = InstallLocation.Normalize(movedFrom), fresh = InstallLocation.Normalize(newExe);
        if (run.Length == 0 || old.Length == 0 || fresh.Length == 0) return null;
        if (run.Equals(fresh, StringComparison.OrdinalIgnoreCase)) return null;
        bool mine = run.Equals(old, StringComparison.OrdinalIgnoreCase);
        if (!mine && sameFile != null && Path.IsPathFullyQualified(run)) { try { mine = sameFile(run); } catch { mine = false; } }
        return mine ? $"\"{fresh}\" --minimized" : null;
    }
}
