using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace Clink.Services;

/// <summary>Everything <see cref="Uninstaller"/> does to the machine, so tests can run it over fakes. Real: <see cref="UninstallSystem"/>.</summary>
internal interface IUninstallSystem
{
    /// <summary>Takes the single-instance mutex WITHOUT forwarding anything to a running Clink. False when a Clink is running.</summary>
    bool TryTakeSingleInstance();
    void ReleaseSingleInstance();
    /// <summary>Asks the user; the answer defaults to No. Never shown by tests.</summary>
    bool Confirm(string text);
    /// <summary>Tells the user something (refusals, the end).</summary>
    void Inform(string text);
    bool FileExists(string path);
    void DeleteFile(string path);
    /// <summary>Deletes a folder only when it has no entries; false when it was not empty (or not there).</summary>
    bool DeleteFolderIfEmpty(string folder);
    /// <summary>
    /// Removes <c>folder\Clink.lnk</c> when it points at <paramref name="exePath"/>; true when removed. Throws when the removal failed.
    /// Real: needs an STA thread (COM shell links), which the 4b wiring must provide for the whole verb.
    /// </summary>
    bool RemoveLinkTo(string folder, string exePath);
    /// <summary>The "Clink" value of HKCU\...\Run; null when there is none.</summary>
    string? ReadRunValue();
    void DeleteRunValue();
    string TempPath { get; }
    void SetCurrentDirectory(string folder);
    /// <summary>Sends a file to the Recycle Bin; throws when it cannot (the running exe cannot be).</summary>
    void Recycle(string path);
    /// <summary>
    /// Copies the running exe to <c>%TEMP%\clink-uninstall-&lt;guid&gt;\Clink.exe</c>, records <paramref name="exePath"/> next to it
    /// (<see cref="Uninstaller.CopyRecordName"/>) and returns the copy's path.
    /// </summary>
    string CopySelfToTemp(string exePath);
    /// <summary>The exe path <see cref="CopySelfToTemp"/> recorded next to the copy; null when there is none.</summary>
    string? ReadCopyTarget(string copyExe);
    /// <summary>SHA-256 of a file (hex). Throws when it cannot be read.</summary>
    string FileHash(string path);
    /// <summary>True when the path is on a fixed local drive (GetDriveType of its root; a local lookup, no network access). False when it cannot be told.</summary>
    bool IsFixedDrive(string path);
    /// <summary>Starts a process without waiting for it.</summary>
    void StartDetached(string exePath, IReadOnlyList<string> args);
    /// <summary>True when process <paramref name="pid"/> is gone (or never was there) within the time.</summary>
    bool WaitForProcessExit(int pid, int milliseconds);
}

internal enum UninstallOutcome
{
    /// <summary>Everything was removed (the exe recycled).</summary>
    Done,
    /// <summary>Not allowed: elevated or installer-managed.</summary>
    Refused,
    /// <summary>A Clink is running.</summary>
    Running,
    /// <summary>The user said no.</summary>
    Declined,
    /// <summary>The exe is locked: a copy in %TEMP% finishes the job once this process has ended (or the folder is left to the user).</summary>
    Deferred,
    /// <summary>The finishing step ran in the temp copy.</summary>
    Finished,
    /// <summary>The finishing step was given something it does not accept.</summary>
    BadFinish,
    /// <summary>An unexpected error.</summary>
    Failed,
}

internal sealed record UninstallResult(UninstallOutcome Outcome, int ExitCode, IReadOnlyList<string> Problems);

/// <summary>What the uninstaller needs to know about this process. <see cref="Uninstaller.RealContext"/> builds the real one.</summary>
/// <param name="IsElevated">3b connects Services/Elevation.cs here.</param>
/// <param name="IsInstallerManaged">3b connects Services/InstallerFootprint.IsManaged here (takes the exe's folder).</param>
/// <param name="LinkFolders">Folders to look for <c>Clink.lnk</c> in (Start menu Programs, Desktop). In dev only folders inside the dev folder are given.</param>
/// <param name="DevFolder">Set in a <c>--dev</c> instance: the Run key is never touched.</param>
/// <param name="OwnFolders">The install choices (Programs\Clink, Documents\Clink): the only folders (besides the entry's InstallLocation) that may be removed.</param>
/// <param name="IsSingleFile">False when the running exe is not a single-file exe (a copy of it alone would not run). Null = true.</param>
/// <param name="DevSwitches">The <c>--dev ...</c> switches the temp copy needs to be the same dev instance; none for a normal run.</param>
internal sealed record UninstallContext(
    string ExePath,
    Func<bool> IsElevated,
    Func<string, bool> IsInstallerManaged,
    IUninstallSystem System,
    IUninstallRegistry Registry,
    IReadOnlyList<string> LinkFolders,
    string? DevFolder,
    int CurrentPid,
    IReadOnlyList<string>? OwnFolders = null,
    Func<bool>? IsSingleFile = null,
    IReadOnlyList<string>? DevSwitches = null);

/// <summary>
/// <c>Clink.exe --uninstall</c> (the UninstallString of <see cref="UninstallEntry"/>). Refuses when elevated or when the Inno installer
/// manages the folder; refuses when a Clink is running (the single-instance mutex is taken without forwarding anything); asks once
/// (default No); then removes, best effort and each step on its own: the Start menu / desktop links that point at this exe, the Run
/// value "Clink" if it points into this folder, <c>.bak/.new/.failed</c> next to the exe, and finally the exe itself (to the Recycle
/// Bin, after the working directory moved to %TEMP%), then the uninstall entry (only when its InstallLocation is this folder) and the
/// folder if empty and one of Clink's own install folders. Never removed: settings, clips, screenshots, the log, and the updates
/// folder (shared with portable copies).
/// A running exe cannot be recycled, so normally the user is told, and then a copy of it in %TEMP% is started with
/// <c>--uninstall-finish &lt;exe&gt; --after-pid &lt;pid&gt;</c> (the very last step); it removes the exe once this process has ended, and then
/// the entry and the folder. The entry stays until then. The copy accepts only the exe recorded next to it, only on a local drive, with the same
/// bytes as itself, never when elevated or installer-managed. A framework-dependent exe is not copied: the user is told to remove the folder by hand.
/// The verb must run on an STA thread (shortcut removal uses COM); real behaviour: VM test.
/// Exit codes: 0 done / declined / deferred, 1 refused, 2 a Clink is running, 3 bad arguments (also a refused finishing step), 4 failed (unexpected error).
/// </summary>
internal static class Uninstaller
{
    public const string Verb = "--uninstall";
    public const string FinishSwitch = "--uninstall-finish";
    public const string AfterPidSwitch = "--after-pid";
    public const string RunValueName = "Clink";
    public const string CopyFolderPrefix = "clink-uninstall-";
    public const string CopyRecordName = "target.txt";
    /// <summary>The single-instance mutex name without the dev suffix (<see cref="AppController.MutexName"/> is this plus the suffix).</summary>
    public const string MutexBase = "Clink.SingleInstance.6F1B";
    public const int FinishWaitMilliseconds = 30_000;

    public const string RefusalText = "Use Settings > Apps to remove Clink.";
    public const string RunningText = "Quit Clink first, then try again.";
    public const string ConfirmText = "Remove Clink from this PC? Your clips, screenshots and settings stay.";
    public const string DoneText = "Clink was removed. Your clips, screenshots and settings were kept.";
    public const string DeferredText = "Clink was removed. The last file is removed when you close this message. Your clips, screenshots and settings were kept.";

    public static string ManualText(string folder) =>
        $"Clink could not remove its own files. Delete the folder {folder} by hand, then remove Clink from Settings > Apps.";

    /// <summary>Exit codes: 0 done / declined / deferred, 1 refused, 2 a Clink is running, 3 bad arguments, 4 failed (unexpected error).</summary>
    public const int ExitOk = 0, ExitRefused = 1, ExitRunning = 2, ExitBadArguments = 3, ExitFailed = 4;

    /// <summary>Set by the 3b wiring (Services/Elevation.cs); until then a plain administrator-role check.</summary>
    public static Func<bool> IsElevatedProvider { get; set; } = DefaultIsElevated;
    /// <summary>Set by the 3b wiring (Services/InstallerFootprint.cs); until then "an unins*.exe sits in the folder".</summary>
    public static Func<string, bool> IsInstallerManagedProvider { get; set; } = DefaultIsInstallerManaged;

    /// <summary>The verb: returns the process exit code. Must be called on an STA thread.</summary>
    public static int Run(string[] args)
    {
        UninstallContext ctx;
        try { ctx = RealContext(args); }
        catch (ArgumentException ex) { Log.Warn("uninstall: " + ex.Message); return ExitBadArguments; }
        catch (Exception ex) { Log.Error("uninstall", ex); return ExitFailed; }
        return Run(args, ctx).ExitCode;
    }

    public static UninstallResult Run(string[] args, UninstallContext ctx)
    {
        try { return RunCore(args, ctx); }
        catch (Exception ex)
        {
            Log.Error("uninstall", ex);
            try { ctx.System.Inform("Clink could not be removed: " + ex.Message); } catch { }
            return new UninstallResult(UninstallOutcome.Failed, ExitFailed, new[] { ex.Message });
        }
    }

    private static UninstallResult RunCore(string[] args, UninstallContext ctx)
    {
        var sys = ctx.System;
        string exe = InstallLocation.Normalize(ctx.ExePath);
        string folder = InstallLocation.Normalize(Path.GetDirectoryName(exe));
        if (folder.Length == 0) return new UninstallResult(UninstallOutcome.Refused, ExitBadArguments, new[] { "no exe path" });

        if (FinishTarget(args) is { } finish) return Finish(finish, AfterPid(args), ctx, exe);
        if (HasFinishSwitch(args))   // the switch with no usable value is never a normal uninstall
        {
            Log.Warn("uninstall finish refused: no target");
            return new UninstallResult(UninstallOutcome.BadFinish, ExitBadArguments, new[] { "no target" });
        }

        if (!string.IsNullOrWhiteSpace(ctx.DevFolder) && !InstallLocation.Under(exe, ctx.DevFolder))   // a --dev instance never touches the real install
        {
            Log.Warn("uninstall refused: a dev instance only removes an exe inside the dev folder");
            return new UninstallResult(UninstallOutcome.Refused, ExitRefused, new[] { "the exe is outside the dev folder" });
        }

        if (ctx.IsElevated() || ctx.IsInstallerManaged(folder))
        {
            sys.Inform(RefusalText);
            return new UninstallResult(UninstallOutcome.Refused, ExitRefused, Array.Empty<string>());
        }
        if (!sys.TryTakeSingleInstance())
        {
            sys.Inform(RunningText);
            return new UninstallResult(UninstallOutcome.Running, ExitRunning, Array.Empty<string>());
        }
        try
        {
            if (!sys.Confirm(ConfirmText)) return new UninstallResult(UninstallOutcome.Declined, ExitOk, Array.Empty<string>());

            var problems = new List<string>();
            void Step(string what, Action action)
            {
                try { action(); }
                catch (Exception ex) { problems.Add(what + ": " + ex.Message); Log.Warn($"uninstall: {what}: {ex.Message}"); }
            }

            foreach (string links in ctx.LinkFolders)
                Step("shortcut in " + links, () => { if (sys.RemoveLinkTo(links, exe)) Log.Info("uninstall: removed the link in " + links); });

            if (string.IsNullOrWhiteSpace(ctx.DevFolder))   // a --dev instance never touches the real Run key
                Step("startup entry", () =>
                {
                    if (RunValuePointsInto(sys.ReadRunValue(), folder)) { sys.DeleteRunValue(); Log.Info("uninstall: removed the Run value"); }
                });

            foreach (string suffix in new[] { ".bak", ".new", ".failed" })
                Step("file " + suffix, () => { if (sys.FileExists(exe + suffix)) sys.DeleteFile(exe + suffix); });

            // The updates folder (%LOCALAPPDATA%\Clink\updates) stays: portable copies share it.

            Step("working directory", () => sys.SetCurrentDirectory(sys.TempPath));

            bool own = IsOwnFolder(folder, ctx);   // judged before the entry goes: the entry's InstallLocation is one of the proofs
            bool singleFile = ctx.IsSingleFile?.Invoke() ?? true;
            bool recycled = false;
            if (singleFile)
            {
                try { sys.Recycle(exe); recycled = true; }
                catch (Exception ex) { Log.Info("uninstall: the exe cannot be recycled while it runs (" + ex.Message + "); a temp copy finishes"); }
            }
            else Log.Info("uninstall: not a single-file exe; the folder is left to the user");

            string Report(string ok) => problems.Count > 0
                ? "Clink was removed, but some items could not be removed:\n" + string.Join("\n", problems.Take(6))
                : ok;

            if (recycled)
            {
                Step("uninstall entry", () => UninstallEntry.DeleteIfOurs(folder, ctx.Registry));
                if (own) Step("folder", () => sys.DeleteFolderIfEmpty(folder));
                sys.Inform(Report(DoneText));
                return new UninstallResult(UninstallOutcome.Done, ExitOk, problems);
            }

            // The exe is still there (and so is the entry, until the copy has recycled it).
            string? copy = null;
            // The copy's finishing step accepts only a local fixed-drive path; judged here the same way, so a copy is never started in vain.
            bool acceptable = IsLocalDrivePath(exe) && sys.IsFixedDrive(exe);
            if (singleFile && !acceptable) Log.Info("uninstall: the exe is not on a local fixed drive; the folder is left to the user");
            if (singleFile && acceptable)
            {
                try { copy = sys.CopySelfToTemp(exe); }
                catch (Exception ex) { problems.Add("exe: " + ex.Message); Log.Warn("uninstall: copy to temp: " + ex.Message); }
            }
            if (copy == null)
            {
                sys.Inform((problems.Count > 0 ? Report("") + "\n" : "") + ManualText(folder));
                return new UninstallResult(UninstallOutcome.Deferred, ExitOk, problems);
            }

            sys.Inform(Report(DeferredText));   // before the start: the copy waits for this process to end
            try
            {
                var startArgs = new List<string> { Verb, FinishSwitch, exe, AfterPidSwitch, ctx.CurrentPid.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (ctx.DevSwitches != null) startArgs.AddRange(ctx.DevSwitches);
                sys.StartDetached(copy, startArgs);   // the very last step
            }
            catch (Exception ex)
            {
                problems.Add("exe: " + ex.Message);
                Log.Warn("uninstall: start of the temp copy: " + ex.Message);
                try { sys.Inform(ManualText(folder)); } catch { }
            }
            return new UninstallResult(UninstallOutcome.Deferred, ExitOk, problems);
        }
        finally { sys.ReleaseSingleInstance(); }
    }

    /// <summary>
    /// True when <paramref name="folder"/> may be removed: not a drive root, and one of Clink's own install folders or the InstallLocation
    /// of the uninstall entry. Anything else (an exe run from Downloads) leaves its folder alone.
    /// </summary>
    private static bool IsOwnFolder(string folder, UninstallContext ctx)
    {
        string f = InstallLocation.Normalize(folder);
        if (f.Length == 0) return false;
        string? root = Path.GetPathRoot(f);
        if (root != null && f.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        if (ctx.OwnFolders != null && ctx.OwnFolders.Any(o => InstallLocation.Normalize(o).Equals(f, StringComparison.OrdinalIgnoreCase))) return true;
        try { return UninstallEntry.IsOurs(folder, ctx.Registry); }
        catch { return false; }
    }

    /// <summary>True for <c>X:\...</c> (a local drive letter, fully qualified); false for relative, UNC and <c>\\?\</c> forms.</summary>
    internal static bool IsLocalDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && Path.IsPathFullyQualified(path);

    /// <summary>The temp copy's job: wait for the first process to end, recycle the exe, then the entry and the folder if empty.</summary>
    private static UninstallResult Finish(string target, int? pid, UninstallContext ctx, string self)
    {
        var sys = ctx.System;
        UninstallResult Bad(string why)
        {
            Log.Warn("uninstall finish refused: " + why);
            return new UninstallResult(UninstallOutcome.BadFinish, ExitBadArguments, new[] { why });
        }

        if (ctx.IsElevated()) return Bad("elevated");
        // Only a full path on a local drive, a file called Clink.exe, and only from a copy that lives in %TEMP%\clink-uninstall-*.
        // All of this is decided before the file system is touched.
        if (!IsLocalDrivePath(target)) return Bad("not a full path on a local drive");
        string exe = InstallLocation.Normalize(target);
        string folder = InstallLocation.Normalize(Path.GetDirectoryName(exe));
        if (!IsLocalDrivePath(exe) || folder.Length == 0) return Bad("not a full path on a local drive");
        if (!Path.GetFileName(exe).Equals(Shortcuts.ExeName, StringComparison.OrdinalIgnoreCase) || !IsUninstallCopy(self, sys.TempPath)
            || exe.Equals(self, StringComparison.OrdinalIgnoreCase))
            return Bad("not Clink.exe, or not run from a temp copy");
        if (!string.IsNullOrWhiteSpace(ctx.DevFolder) && !InstallLocation.Under(exe, ctx.DevFolder)) return Bad("outside the dev folder");

        // Order: the record, the drive, the installer check, the wait for the first process, then the file system (exists, hash), then the recycle.
        string? recorded = sys.ReadCopyTarget(self);
        if (recorded == null || !InstallLocation.Normalize(recorded).Equals(exe, StringComparison.OrdinalIgnoreCase))
            return Bad("not the exe this copy was made for");
        if (!sys.IsFixedDrive(exe)) return Bad("not on a fixed drive");
        if (ctx.IsInstallerManaged(folder)) return Bad("installer-managed folder");

        var problems = new List<string>();
        if (pid is int p && !sys.WaitForProcessExit(p, FinishWaitMilliseconds)) problems.Add("the first process did not end");
        if (!sys.FileExists(exe))
        {
            problems.Add("the exe is already gone");
            return new UninstallResult(UninstallOutcome.Finished, ExitOk, problems);
        }
        if (!string.Equals(sys.FileHash(exe), sys.FileHash(self), StringComparison.OrdinalIgnoreCase)) return Bad("not the same bytes as this copy");

        bool own = IsOwnFolder(folder, ctx);
        try
        {
            sys.Recycle(exe);
        }
        catch (Exception ex)
        {
            problems.Add("exe: " + ex.Message);
            Log.Warn("uninstall finish: " + ex.Message);
            try { sys.Inform(ManualText(folder)); } catch { }   // a valid request that failed: the user is told what to remove
            return new UninstallResult(UninstallOutcome.Finished, ExitOk, problems);
        }
        try { UninstallEntry.DeleteIfOurs(folder, ctx.Registry); }
        catch (Exception ex) { problems.Add("uninstall entry: " + ex.Message); Log.Warn("uninstall finish: entry: " + ex.Message); }
        if (own)
        {
            try { sys.DeleteFolderIfEmpty(folder); }
            catch (Exception ex) { problems.Add("folder: " + ex.Message); Log.Warn("uninstall finish: folder: " + ex.Message); }
        }
        return new UninstallResult(UninstallOutcome.Finished, ExitOk, problems);
    }

    /// <summary>True when <paramref name="exe"/> is <c>&lt;temp&gt;\clink-uninstall-*\Clink.exe</c>.</summary>
    public static bool IsUninstallCopy(string exe, string tempPath)
    {
        string dir = InstallLocation.Normalize(Path.GetDirectoryName(InstallLocation.Normalize(exe)));
        if (dir.Length == 0) return false;
        string parent = InstallLocation.Normalize(Path.GetDirectoryName(dir));
        return parent.Equals(InstallLocation.Normalize(tempPath), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(dir).StartsWith(CopyFolderPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? FinishTarget(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i].Equals(FinishSwitch, StringComparison.OrdinalIgnoreCase) && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) return list[i + 1];
        return null;
    }

    /// <summary>True when <c>--uninstall-finish</c> is present at all, whatever follows.</summary>
    private static bool HasFinishSwitch(IEnumerable<string> args) => args.Any(a => a.Equals(FinishSwitch, StringComparison.OrdinalIgnoreCase));

    private static int? AfterPid(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i].Equals(AfterPidSwitch, StringComparison.OrdinalIgnoreCase))
                return int.TryParse(list[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : null;
        return null;
    }

    /// <summary>
    /// True when a Run value (<c>"C:\path\Clink.exe" --minimized</c>, quoted or not) starts <c>Clink.exe</c> directly inside <paramref name="folder"/>.
    /// A value that points somewhere else (another copy, another program, an unexpanded <c>%VAR%</c> path) is not ours to remove.
    /// An unquoted value is cut at the first " --" only, so <c>C:\x\Clink.exe /min</c> (unquoted) is not recognised; a quoted exe is.
    /// </summary>
    public static bool RunValuePointsInto(string? value, string folder)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string v = value.Trim();
        string exe;
        if (v.StartsWith('"'))
        {
            int end = v.IndexOf('"', 1);
            if (end < 0) return false;
            exe = v[1..end];
        }
        else
        {
            int sw = v.IndexOf(" --", StringComparison.Ordinal);
            exe = sw >= 0 ? v[..sw] : v;
        }
        string normalized = InstallLocation.Normalize(exe);
        if (!Path.IsPathFullyQualified(normalized)) return false;
        if (!Path.GetFileName(normalized).Equals(Shortcuts.ExeName, StringComparison.OrdinalIgnoreCase)) return false;
        string dir = InstallLocation.Normalize(Path.GetDirectoryName(normalized));
        return dir.Equals(InstallLocation.Normalize(folder), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ real world

    /// <summary>
    /// The real context. Side-effect free (nothing is created, no mutex taken). <c>--dev &lt;folder&gt;</c> / <c>--dev-quiet</c> in <paramref name="args"/>
    /// (parsed by <see cref="DevOptions.Parse"/>, which throws <see cref="ArgumentException"/> for a bad dev command line) give the JSON
    /// registry in the dev folder, the dev mutex, link folders inside the dev folder and, with <c>--dev-quiet</c>, a system that only logs.
    /// </summary>
    public static UninstallContext RealContext(string[] args)
    {
        var dev = DevOptions.Parse(args);
        string exe = Environment.ProcessPath ?? "";
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var own = new List<string>();
        if (dev != null)   // a dev instance's install choices live inside the dev folder
        {
            own.Add(Path.Combine(dev.Folder, "Programs", "Clink"));
            own.Add(Path.Combine(dev.Folder, "Documents", "Clink"));
        }
        else
        {
            if (local.Length > 0) own.Add(SelfInstall.DefaultTargetFolder(local));
            if (docs.Length > 0) own.Add(Path.Combine(docs, "Clink"));
        }

        var links = new List<string>();
        if (dev == null)
        {
            foreach (var f in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
            {
                string p = Environment.GetFolderPath(f);
                if (p.Length > 0) links.Add(p);
            }
        }
        else
        {
            links.Add(Path.Combine(dev.Folder, "Programs"));
            links.Add(Path.Combine(dev.Folder, "Desktop"));
        }

        IUninstallSystem system = new UninstallSystem(dev?.InstanceSuffix ?? "", dev?.Folder);
        if (dev is { Quiet: true }) system = new DryRunUninstallSystem(system);
        IUninstallRegistry registry = dev == null ? new RegistryUninstallRegistry() : new JsonFileUninstallRegistry(dev.Folder);
        if (dev is { Quiet: true }) registry = new DryRunUninstallRegistry(registry);
        var devSwitches = dev == null ? Array.Empty<string>() : DevOptions.Switches(dev.Folder, false, false, false, null, dev.Quiet).ToArray();
        return new UninstallContext(exe, IsElevatedProvider, IsInstallerManagedProvider, system, registry, links, dev?.Folder, Environment.ProcessId,
            own, IsSingleFileExe, devSwitches);
    }

    /// <summary>Same test as the self-install: a single-file exe has no assembly file on disk.</summary>
    private static bool IsSingleFileExe()
    {
#pragma warning disable IL3000   // intentional: empty in a single-file bundle is exactly the signal
        return string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
    }

    private static bool DefaultIsElevated()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return true; }   // cannot tell: refuse
    }

    /// <summary>True when an unins*.exe sits in the folder; also true when that cannot be found out (refuse).</summary>
    internal static bool DefaultIsInstallerManaged(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "unins*.exe").Any(); }
        catch { return true; }
    }
}

/// <summary>The real machine. The whole verb must run on an STA thread (<see cref="RemoveLinkTo"/> uses COM shell links).</summary>
internal sealed class UninstallSystem : IUninstallSystem
{
    private readonly string _suffix;
    private readonly string? _devFolder;
    private Mutex? _mutex;

    /// <param name="instanceSuffix">The dev mutex suffix (<see cref="DevOptions.InstanceSuffix"/>); "" for a normal run.</param>
    /// <param name="devFolder">A dev instance: shortcuts outside it are never touched.</param>
    public UninstallSystem(string instanceSuffix = "", string? devFolder = null) { _suffix = instanceSuffix; _devFolder = devFolder; }

    public bool TryTakeSingleInstance()
    {
        try
        {
            var m = new Mutex(true, Uninstaller.MutexBase + _suffix, out bool createdNew);
            if (!createdNew) { m.Dispose(); return false; }
            _mutex = m;
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }   // an elevated Clink owns it: running all the same
    }

    public void ReleaseSingleInstance()
    {
        var m = _mutex;
        _mutex = null;
        if (m == null) return;
        try { m.ReleaseMutex(); } catch { }
        try { m.Dispose(); } catch { }
    }

    public bool Confirm(string text) =>
        System.Windows.MessageBox.Show(text, "Clink", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    public void Inform(string text) =>
        System.Windows.MessageBox.Show(text, "Clink", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    public bool FileExists(string path) => File.Exists(path);
    public void DeleteFile(string path) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }

    public bool DeleteFolderIfEmpty(string folder)
    {
        if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) return false;
        Directory.Delete(folder, recursive: false);
        return true;
    }

    public bool RemoveLinkTo(string folder, string exePath)
    {
        var r = Shortcuts.Remove(folder, exePath, devFolder: _devFolder);
        if (r.Outcome is ShortcutOutcome.Failed or ShortcutOutcome.Refused) throw new IOException($"{r.Path}: {r.Outcome} {r.Detail}".TrimEnd());
        return r.Outcome == ShortcutOutcome.Removed;
    }

    public string? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(Uninstaller.RunValueName) as string;
    }

    public void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue(Uninstaller.RunValueName, false);
    }

    public string TempPath => Path.GetTempPath();
    public void SetCurrentDirectory(string folder) => Directory.SetCurrentDirectory(folder);
    public void Recycle(string path) => new SelfInstallSystem().Recycle(path, null);

    public bool IsFixedDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && root.Length >= 3 && root[1] == ':' && Screenshots.ScreenshotService.DriveTypeOf(root) == 3;   // GetDriveType: local lookup
        }
        catch { return false; }
    }

    public string CopySelfToTemp(string exePath) =>
        CopySelfTo(exePath, Path.Combine(Path.GetTempPath(), Uninstaller.CopyFolderPrefix + Guid.NewGuid().ToString("N")));

    /// <summary>
    /// Copies the exe into <paramref name="dir"/>, which must not exist yet (create-new: an existing folder, e.g. one planted by another
    /// process, is refused). The folder gets an owner-only ACL that does not inherit from %TEMP%: the current user and SYSTEM, full control.
    /// </summary>
    internal static string CopySelfTo(string exePath, string dir)
    {
        if (Directory.Exists(dir) || File.Exists(dir)) throw new IOException("the copy folder already exists: " + dir);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using (var id = WindowsIdentity.GetCurrent())
            if (id.User != null)
                security.AddAccessRule(new FileSystemAccessRule(id.User, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).Create(security);
        string copy = Path.Combine(dir, Shortcuts.ExeName);
        File.Copy(exePath, copy);
        File.WriteAllText(Path.Combine(dir, Uninstaller.CopyRecordName), InstallLocation.Normalize(exePath));
        return copy;
    }

    public string? ReadCopyTarget(string copyExe)
    {
        try
        {
            string dir = Path.GetDirectoryName(copyExe) ?? "";
            string file = Path.Combine(dir, Uninstaller.CopyRecordName);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch { return null; }
    }

    public string FileHash(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public void StartDetached(string exePath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath) ?? "" };
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    public bool WaitForProcessExit(int pid, int milliseconds)
    {
        try { using var p = Process.GetProcessById(pid); return p.WaitForExit(milliseconds); }
        catch (ArgumentException) { return true; }   // not running
        catch { return true; }
    }
}

/// <summary>
/// <c>--dev --dev-quiet</c>: nothing on screen and nothing changed on the machine; every action is only logged. Reads (file exists, Run
/// value, hash, the mutex) go to the real system so the flow takes the same branches.
/// </summary>
internal sealed class DryRunUninstallSystem : IUninstallSystem
{
    private readonly IUninstallSystem _real;
    public DryRunUninstallSystem(IUninstallSystem real) => _real = real;

    private static void Say(string what) => Log.Info("uninstall (dry run): " + what);

    public bool TryTakeSingleInstance() => _real.TryTakeSingleInstance();
    public void ReleaseSingleInstance() => _real.ReleaseSingleInstance();
    public bool Confirm(string text) { Say("confirm: " + text); return true; }
    public void Inform(string text) => Say("inform: " + text);
    public bool FileExists(string path) => _real.FileExists(path);
    public void DeleteFile(string path) => Say("delete " + path);
    public bool DeleteFolderIfEmpty(string folder) { Say("remove folder " + folder); return false; }
    public bool RemoveLinkTo(string folder, string exePath) { Say("remove link in " + folder); return false; }
    public string? ReadRunValue() => _real.ReadRunValue();
    public void DeleteRunValue() => Say("delete the Run value");
    public string TempPath => _real.TempPath;
    public void SetCurrentDirectory(string folder) => Say("cd " + folder);
    public void Recycle(string path) => Say("recycle " + path);
    public string CopySelfToTemp(string exePath) { Say("copy self"); return Path.Combine(_real.TempPath, Uninstaller.CopyFolderPrefix + "dryrun", Shortcuts.ExeName); }
    public string? ReadCopyTarget(string copyExe) => _real.ReadCopyTarget(copyExe);
    public string FileHash(string path) => _real.FileHash(path);
    public bool IsFixedDrive(string path) => _real.IsFixedDrive(path);
    public void StartDetached(string exePath, IReadOnlyList<string> args) => Say("start " + exePath + " " + string.Join(" ", args));
    public bool WaitForProcessExit(int pid, int milliseconds) => true;
}
