using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Clink.Services;

internal enum RelocationKind
{
    /// <summary>Already at the target path (the same file, whatever the spelling of the path).</summary>
    NoOp,
    /// <summary>Not allowed (elevated, bad input, outside the dev folder, a foreign exe at the target).</summary>
    Refused,
    /// <summary>Copy this exe to the target, then start it.</summary>
    Install,
    /// <summary>The target holds this version or a newer one: start that one, copy nothing.</summary>
    LaunchExisting,
}

internal sealed record RelocationPlan(RelocationKind Kind, string Source, string Target, string? Reason = null)
{
    public string NewPath => Target + ".new";
    public string BackupPath => Target + ".bak";
}

internal enum RelocationOutcome
{
    NothingToDo,
    Refused,
    /// <summary>Copied, swapped in and started; the caller exits now (the new copy waits for <c>--after-pid</c>).</summary>
    Moved,
    /// <summary>The existing newer copy was started; the caller exits now.</summary>
    LaunchedExisting,
    /// <summary>Something failed and everything was put back (unless <see cref="RelocationResult.RollbackError"/> says otherwise); the source is untouched.</summary>
    Failed,
}

/// <param name="RollbackError">Set when undoing a failed install did not fully work (a file could not be deleted or put back); also appended to <paramref name="Error"/>.</param>
internal sealed record RelocationResult(RelocationOutcome Outcome, string? Error = null, string? Sha256 = null, string? RollbackError = null)
{
    public bool ShouldExit => Outcome is RelocationOutcome.Moved or RelocationOutcome.LaunchedExisting;
}

/// <summary>A started child process.</summary>
internal interface IChildProcess : IDisposable
{
    /// <summary>True when the process ended within the time.</summary>
    bool WaitForExit(int milliseconds);
}

/// <summary>The user ended a shell operation (declined its warning): final, never retried.</summary>
internal sealed class RecycleAbortedException : IOException
{
    public RecycleAbortedException(string message) : base(message) { }
}

/// <summary>The disk and process access of <see cref="SelfInstall"/>. Real: <see cref="SelfInstallSystem"/>.</summary>
internal interface ISelfInstallSystem
{
    bool FileExists(string path);
    bool IsReadOnly(string path);
    void ClearReadOnly(string path);
    void CreateDirectory(string path);
    string Sha256(string path);
    void Copy(string from, string to);
    void Move(string from, string to, bool overwrite);
    void Delete(string path);
    /// <summary>True when the two paths are one file (volume serial + file index), however they are spelled (8.3 names, junctions, subst drives, hard links). False when either does not exist.</summary>
    bool SameFile(string a, string b);
    /// <summary>The version of an exe (its product version, else its file version); null when unknown.</summary>
    ReleaseVersion? VersionOf(string exePath);
    /// <summary>True when the exe's version resource says it is Clink (product name or original file name).</summary>
    bool LooksLikeClink(string exePath);
    /// <summary>Removes the Zone.Identifier alternate stream (mark of the web) of a file; no error when there is none.</summary>
    void StripZoneIdentifier(string path);
    IChildProcess Start(string exePath, IReadOnlyList<string> args);
    /// <summary>
    /// Sends one file to the Recycle Bin; throws when it cannot, <see cref="RecycleAbortedException"/> when the user ended the shell
    /// operation. <paramref name="ownerHwnd"/> is the owner window of any shell dialog (null = none).
    /// </summary>
    void Recycle(string path, nint? ownerHwnd);
    /// <summary>GetDriveType of a root such as <c>C:\</c>: 2 removable, 3 fixed, 4 remote, 5 optical.</summary>
    uint DriveTypeOf(string root);
    /// <summary>Program Files, Program Files (x86), ProgramW6432.</summary>
    IReadOnlyList<string> ProgramFilesFolders();
    void Sleep(int milliseconds);
}

/// <summary>
/// "Install for me": copies the running exe to its new home and starts it there. The steps, each undone if a later one fails:
/// hash the source; create the folder; copy to <c>Clink.exe.new</c> (read-only cleared); hash that and compare; an existing target
/// (which must be a Clink) becomes <c>Clink.exe.bak</c>; <c>.new</c> becomes the target (the moves are retried briefly: virus
/// scanners and the indexer hold new files); start it with
/// <c>--after-pid &lt;pid&gt; --setup-continue --setup-moved-from &lt;source&gt; &lt;sha256&gt;</c> (plus the dev switches of this start);
/// if starting throws or the child ends within 2 s, the target is deleted and the backup restored. The source is never touched here:
/// the new copy removes it later (<see cref="CleanupOldCopy"/>).
/// </summary>
internal static class SelfInstall
{
    public const string TargetFileName = "Clink.exe";
    public const int SurvivalMilliseconds = 2000;
    public const int MoveAttempts = 20;
    public const int MoveDelayMilliseconds = 250;

    private static readonly Regex s_sha256 = new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The copy keeps the Zone.Identifier stream (mark of the web) a browser put on the download, so SmartScreen treats the moved exe like
    /// the original and nothing about its origin is hidden. Set only to strip it; off for now (owner decision pending).
    /// </summary>
    public static bool StripMarkOfTheWeb { get; set; } = false;

    /// <summary>Where "Install for me" puts Clink: %LOCALAPPDATA%\Programs\Clink, the folder of the per-user installer.</summary>
    public static string DefaultTargetFolder(string localAppData) => Path.Combine(localAppData, "Programs", "Clink");

    /// <summary>
    /// What to do. <paramref name="devFolder"/>: the data folder of a <c>--dev</c> instance; when given, a target outside it is refused
    /// (a dev run must never touch the real install).
    /// </summary>
    public static RelocationPlan Plan(string sourceExe, string targetFolder, bool elevated, ReleaseVersion currentVersion, ISelfInstallSystem sys, string? devFolder = null)
    {
        string source = InstallLocation.Normalize(sourceExe);
        if (source.Length == 0 || string.IsNullOrWhiteSpace(targetFolder) || !Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(targetFolder))
            return new RelocationPlan(RelocationKind.Refused, source, "", "The paths are not full paths.");
        string target = Path.Combine(InstallLocation.Normalize(targetFolder), TargetFileName);
        if (elevated) return new RelocationPlan(RelocationKind.Refused, source, target, "Clink is running as administrator; it will not install itself from an elevated process.");
        if (!string.IsNullOrWhiteSpace(devFolder) && !InstallLocation.Under(InstallLocation.Normalize(target), devFolder))
            return new RelocationPlan(RelocationKind.Refused, source, target, "A --dev instance only installs inside its own dev folder.");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || (sys.FileExists(target) && sys.SameFile(source, target)))
            return new RelocationPlan(RelocationKind.NoOp, source, target, "Already there.");
        if (sys.FileExists(target))
        {
            if (!sys.LooksLikeClink(target))
                return new RelocationPlan(RelocationKind.Refused, source, target, "The file at the target is not Clink; it is left alone.");
            if (sys.VersionOf(target) is { } existing && existing >= currentVersion)
                return new RelocationPlan(RelocationKind.LaunchExisting, source, target, $"The target holds {existing}, this is {currentVersion}.");
        }
        return new RelocationPlan(RelocationKind.Install, source, target);
    }

    /// <summary>The command line of the new copy.</summary>
    public static IReadOnlyList<string> LaunchArguments(int currentPid, string source, string sha256, IReadOnlyList<string>? devArgs = null)
    {
        var args = new List<string> { "--after-pid", currentPid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--setup-continue", "--setup-moved-from", source, sha256 };
        if (devArgs != null) args.AddRange(devArgs);
        return args;
    }

    /// <summary>A moved-from record that can be acted on: a full path without stray colons (alternate streams) and a SHA-256 in hex.</summary>
    public static bool IsValidMoveRecord(string? movedFrom, string? sha256)
    {
        string src = InstallLocation.Normalize(movedFrom);
        return src.Length > 0 && Path.IsPathFullyQualified(src) && src.IndexOf(':', 2) < 0 && !string.IsNullOrWhiteSpace(sha256) && s_sha256.IsMatch(sha256.Trim());
    }

    /// <summary>
    /// Runs the plan. Blocking (hashing, copying, up to 2 s of waiting for the child, retries): the caller runs it OFF the UI thread.
    /// Caller contract (slice 3, AppController): when <see cref="RelocationResult.ShouldExit"/> is true, release the single-instance
    /// mutex only after this returned (the child has started by then; the child waits for <c>--after-pid</c>), then exit. The restart
    /// path of the new copy must strip <c>--setup-continue</c> and <c>--setup-moved-from &lt;path&gt; &lt;sha&gt;</c> from the arguments it
    /// restarts with, or every restart would ask to remove the old copy and show the wizard again.
    /// A backup of the replaced copy (<c>Clink.exe.bak</c>) stays after success until the next install recycles it.
    /// <paramref name="ownerHwnd"/>: owner window for a Recycle Bin dialog (null = none).
    /// </summary>
    public static RelocationResult Execute(RelocationPlan plan, int currentPid, IReadOnlyList<string>? devArgs, ISelfInstallSystem sys, nint? ownerHwnd = null)
    {
        switch (plan.Kind)
        {
            case RelocationKind.NoOp: return new RelocationResult(RelocationOutcome.NothingToDo);
            case RelocationKind.Refused: return new RelocationResult(RelocationOutcome.Refused, plan.Reason);
            case RelocationKind.LaunchExisting:
                try
                {
                    string hash = sys.Sha256(plan.Source);
                    using var child = sys.Start(plan.Target, LaunchArguments(currentPid, plan.Source, hash, devArgs));
                    if (child.WaitForExit(SurvivalMilliseconds)) return new RelocationResult(RelocationOutcome.Failed, "The installed Clink ended right after it started.");
                    return new RelocationResult(RelocationOutcome.LaunchedExisting, null, hash);
                }
                catch (Exception ex) { return new RelocationResult(RelocationOutcome.Failed, ex.Message); }
        }

        string newPath = plan.NewPath, backup = plan.BackupPath, target = plan.Target;
        bool backedUp = false, swapped = false;
        try
        {
            if (!sys.FileExists(plan.Source)) return new RelocationResult(RelocationOutcome.Failed, "The source file is gone.");
            bool targetExists = sys.FileExists(target);
            if (targetExists && !sys.LooksLikeClink(target)) return new RelocationResult(RelocationOutcome.Failed, "The file in the target folder is not Clink; it is left alone.");
            if (targetExists && sys.IsReadOnly(target)) return new RelocationResult(RelocationOutcome.Failed, "The existing Clink.exe in the target folder is read-only.");
            string hash = sys.Sha256(plan.Source);

            sys.CreateDirectory(Path.GetDirectoryName(target)!);
            if (sys.FileExists(newPath)) sys.Delete(newPath);
            sys.Copy(plan.Source, newPath);
            sys.ClearReadOnly(newPath);   // a copy of a read-only download is read-only; the swap and a later update need it writable
            if (!string.Equals(sys.Sha256(newPath), hash, StringComparison.OrdinalIgnoreCase))
            {
                sys.Delete(newPath);
                return new RelocationResult(RelocationOutcome.Failed, "The copy does not match the original (hash mismatch).");
            }

            if (targetExists)
            {
                if (sys.FileExists(backup) && !TryRecycle(sys, backup, ownerHwnd))
                {
                    Rollback(plan, sys, false, false);
                    return new RelocationResult(RelocationOutcome.Failed, "An older Clink.exe.bak is in the way and cannot be moved to the Recycle Bin.");
                }
                MoveWithRetry(sys, target, backup, overwrite: false);
                backedUp = true;
            }
            MoveWithRetry(sys, newPath, target, overwrite: false);
            swapped = true;
            if (StripMarkOfTheWeb) { try { sys.StripZoneIdentifier(target); } catch { } }

            using (var child = sys.Start(target, LaunchArguments(currentPid, plan.Source, hash, devArgs)))
            {
                if (child.WaitForExit(SurvivalMilliseconds))
                    return Failed("The new copy ended right after it started.", Rollback(plan, sys, swapped, backedUp));
            }
            // No recycle here: the shell can block on a prompt while the parent still holds the single-instance mutex, and the child gives up after its
            // --after-pid wait. The .bak stays; the pre-step above recycles it on the next install.
            return new RelocationResult(RelocationOutcome.Moved, null, hash);
        }
        catch (Exception ex)
        {
            return Failed(ex.Message, Rollback(plan, sys, swapped, backedUp));
        }
    }

    private static RelocationResult Failed(string error, string? rollbackError) =>
        new(RelocationOutcome.Failed, rollbackError == null ? error : error + " (Rollback incomplete: " + rollbackError + ")", null, rollbackError);

    /// <summary>File.Move retried for about 5 s on sharing / access errors (virus scanner, indexer); anything else throws at once.</summary>
    private static void MoveWithRetry(ISelfInstallSystem sys, string from, string to, bool overwrite)
    {
        for (int i = 0; ; i++)
        {
            try { sys.Move(from, to, overwrite); return; }
            catch (Exception ex) when (i < MoveAttempts - 1 && (ex is UnauthorizedAccessException || (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException)))
            {
                sys.Sleep(MoveDelayMilliseconds);
            }
        }
    }

    /// <summary>File.Delete retried like <see cref="MoveWithRetry"/> (a virus scanner may hold the file right after a failed launch).</summary>
    private static void DeleteWithRetry(ISelfInstallSystem sys, string path)
    {
        for (int i = 0; ; i++)
        {
            try { sys.Delete(path); return; }
            catch (Exception ex) when (i < MoveAttempts - 1 && (ex is UnauthorizedAccessException || (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException)))
            {
                sys.Sleep(MoveDelayMilliseconds);
            }
        }
    }

    /// <summary>Undoes what was done. Returns what could not be undone (null = all fine); never throws.</summary>
    private static string? Rollback(RelocationPlan plan, ISelfInstallSystem sys, bool swapped, bool backedUp)
    {
        var problems = new List<string>();
        try { if (sys.FileExists(plan.NewPath)) sys.Delete(plan.NewPath); } catch (Exception ex) { problems.Add("could not remove " + plan.NewPath + ": " + ex.Message); }
        if (swapped) { try { if (sys.FileExists(plan.Target)) DeleteWithRetry(sys, plan.Target); } catch (Exception ex) { problems.Add("could not remove " + plan.Target + ": " + ex.Message); } }
        if (backedUp)
        {
            try { if (sys.FileExists(plan.BackupPath)) MoveWithRetry(sys, plan.BackupPath, plan.Target, overwrite: true); }
            catch (Exception ex) { problems.Add("could not restore " + plan.BackupPath + " to " + plan.Target + ": " + ex.Message); }
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // ------------------------------------------------------------------ recycling

    /// <summary>
    /// A file the Recycle Bin really takes: on a local fixed drive (not UNC, network, removable or optical) and not under Program Files.
    /// Anywhere else the shell deletes for good or asks the user, which a background cleanup must not do.
    /// </summary>
    public static bool IsRecyclableLocation(string normalizedPath, ISelfInstallSystem sys)
    {
        try
        {
            if (InstallLocation.IsUnc(normalizedPath)) return false;
            string root = Path.GetPathRoot(normalizedPath) ?? "";
            if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\') return false;
            if (sys.DriveTypeOf(root) != 3) return false;
            foreach (string pf in sys.ProgramFilesFolders())
                if (InstallLocation.Under(normalizedPath, pf)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Sends our own leftover (.bak) to the Recycle Bin if that is safe; true when it is gone. Never throws, never deletes for good.</summary>
    private static bool TryRecycle(ISelfInstallSystem sys, string path, nint? ownerHwnd)
    {
        try
        {
            if (!IsRecyclableLocation(InstallLocation.Normalize(path), sys)) return false;
            sys.Recycle(path, ownerHwnd);
            return !sys.FileExists(path);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ old copy

    public enum CleanupOutcome
    {
        Recycled, AlreadyGone, NotAnExe, IsSelf, HashMismatch, Failed,
        /// <summary>The old copy is on a removable / network drive or under Program Files, or this process is elevated: the Recycle Bin would not take it (or it must not be touched); left alone.</summary>
        NotRecyclable,
    }

    /// <summary>
    /// Run by the new copy: sends the copy it was moved from to the Recycle Bin. Only a file whose name ends in .exe, that is not the
    /// running exe (compared by file identity too), that sits on a local fixed drive outside Program Files, and whose SHA-256 is still
    /// the one passed on the command line. Never when <paramref name="elevated"/>. Anything else is left alone. Retries (the old
    /// process may still be exiting) except after the user ended the shell operation. Serialised with the screenshot library's
    /// Recycle Bin calls. Never throws.
    /// </summary>
    public static CleanupOutcome CleanupOldCopy(string? movedFrom, string? expectedSha256, string currentExe, ISelfInstallSystem sys, bool elevated,
        nint? ownerHwnd = null, int attempts = 8, int delayMs = 500)
    {
        try
        {
            string src = InstallLocation.Normalize(movedFrom);
            if (src.Length == 0 || !Path.IsPathFullyQualified(src) || src.IndexOf(':', 2) >= 0 || string.IsNullOrWhiteSpace(expectedSha256)) return CleanupOutcome.NotAnExe;
            if (!src.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return CleanupOutcome.NotAnExe;
            if (elevated) return CleanupOutcome.NotRecyclable;
            if (src.Equals(InstallLocation.Normalize(currentExe), StringComparison.OrdinalIgnoreCase) || sys.SameFile(src, currentExe)) return CleanupOutcome.IsSelf;

            for (int i = 0; i < Math.Max(1, attempts); i++)
            {
                if (i > 0) sys.Sleep(delayMs);
                try
                {
                    if (!sys.FileExists(src)) return CleanupOutcome.AlreadyGone;   // a directory of that name is not a file
                    if (!IsRecyclableLocation(src, sys)) return CleanupOutcome.NotRecyclable;
                    if (!string.Equals(sys.Sha256(src), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase)) return CleanupOutcome.HashMismatch;
                    sys.Recycle(src, ownerHwnd);
                    if (!sys.FileExists(src)) return CleanupOutcome.Recycled;
                }
                catch (RecycleAbortedException) { return CleanupOutcome.Failed; }   // the user said no: final
                catch (IOException) { }                   // still open: the old process is exiting
                catch (UnauthorizedAccessException) { }
            }
            return CleanupOutcome.Failed;
        }
        catch { return CleanupOutcome.Failed; }
    }
}

/// <summary>The real disk, processes and Recycle Bin.</summary>
internal sealed class SelfInstallSystem : ISelfInstallSystem
{
    private sealed class Child : IChildProcess
    {
        private readonly Process _p;
        public Child(Process p) => _p = p;
        public bool WaitForExit(int milliseconds) => _p.WaitForExit(milliseconds);
        public void Dispose() => _p.Dispose();
    }

    public bool FileExists(string path) => File.Exists(path);
    public bool IsReadOnly(string path) => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
    public void ClearReadOnly(string path)
    {
        var a = File.GetAttributes(path);
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
    }
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public void Copy(string from, string to) => File.Copy(from, to, overwrite: true);
    public void Move(string from, string to, bool overwrite) => File.Move(from, to, overwrite);
    public void Delete(string path) => File.Delete(path);

    /// <summary>Volume serial + file index of both files. When one cannot be opened though both exist, the answer is "same" (the safe one for the callers: nothing is installed over it or removed).</summary>
    public unsafe bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            Windows.Win32.Storage.FileSystem.BY_HANDLE_FILE_INFORMATION ia, ib;
            if (!Windows.Win32.PInvoke.GetFileInformationByHandle(new Windows.Win32.Foundation.HANDLE(fa.SafeFileHandle.DangerousGetHandle()), &ia)) return true;
            if (!Windows.Win32.PInvoke.GetFileInformationByHandle(new Windows.Win32.Foundation.HANDLE(fb.SafeFileHandle.DangerousGetHandle()), &ib)) return true;
            return ia.dwVolumeSerialNumber == ib.dwVolumeSerialNumber && ia.nFileIndexHigh == ib.nFileIndexHigh && ia.nFileIndexLow == ib.nFileIndexLow;
        }
        catch { return true; }
    }

    /// <summary>ProductVersion (the informational version), else FileVersion: the same fallback the updater uses for an exe's embedded version.</summary>
    public ReleaseVersion? VersionOf(string exePath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            return ReleaseVersion.Parse(fvi.ProductVersion) ?? ReleaseVersion.Parse(fvi.FileVersion);
        }
        catch { return null; }
    }

    public bool LooksLikeClink(string exePath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            return string.Equals(fvi.ProductName?.Trim(), "Clink", StringComparison.OrdinalIgnoreCase)
                || (fvi.OriginalFilename?.Trim().StartsWith("Clink.", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch { return false; }
    }

    public void StripZoneIdentifier(string path) => File.Delete(path + ":Zone.Identifier");

    public IChildProcess Start(string exePath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath) ?? "" };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return new Child(Process.Start(psi) ?? throw new IOException("The new copy could not be started."));
    }

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

    public uint DriveTypeOf(string root) => Screenshots.ScreenshotService.DriveTypeOf(root);

    public IReadOnlyList<string> ProgramFilesFolders() =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") ?? "" }
            .Where(s => s.Length > 0).ToArray();

    public void Recycle(string path, nint? ownerHwnd)
    {
        string full = Path.GetFullPath(path);
        nint owner = ownerHwnd ?? 0;
        lock (Screenshots.ScreenshotService.s_recycleLock)   // never two shell operations at once (the library's delete takes the same lock)
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { RecycleCore(full, owner); } catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "Clink.RecycleOldCopy" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error is RecycleAbortedException) throw error;
            if (error != null) throw new IOException(error.Message, error);
        }
    }

    private static unsafe void RecycleCore(string path, nint owner)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;
        string from = path + "\0\0";
        fixed (char* pFrom = from)
        {
            var op = new Windows.Win32.UI.Shell.SHFILEOPSTRUCTW
            {
                hwnd = new Windows.Win32.Foundation.HWND(owner),
                wFunc = FO_DELETE,
                pFrom = pFrom,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING),
            };
            int rc = Windows.Win32.PInvoke.SHFileOperation(ref op);
            if (op.fAnyOperationsAborted) throw new RecycleAbortedException("The Recycle Bin operation was ended (code 0x" + rc.ToString("X") + ")");
            if (rc != 0) throw new IOException("The file could not be moved to the Recycle Bin (code 0x" + rc.ToString("X") + ")");
        }
    }
}
