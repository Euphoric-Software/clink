using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// What the new copy does after the wizard moved Clink.exe (Run key, links, .bak, old copy), over a fake file system and a fake Run key; the links are
/// real .lnk files in a temp folder. Also the installer footprint, the elevation switch and the dev install environment. Nothing here touches the real
/// registry, the real Start menu or any file outside the temp folder.
/// </summary>
internal static class SetupContinuationTests
{
    private sealed class FakeRunKey : IRunKey
    {
        public string? Value;
        public int Reads, Writes;
        public bool ThrowOnWrite;
        public Action<string>? OnWrite;
        public string? Read() { Reads++; return Value; }
        public void Write(string value)
        {
            Writes++;
            if (ThrowOnWrite) throw new IOException("registry is read only");
            OnWrite?.Invoke(value);
            Value = value;
        }
    }

    private sealed class FakeSystem : ISelfInstallSystem
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Recycled = new();
        public readonly List<nint?> Owners = new();
        public Action<string>? OnRecycle;
        public int Sleeps;
        public int FileExistsCalls, SameFileCalls, Sha256Calls;
        public readonly HashSet<string> NotClink = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, uint> DriveType = _ => 3;

        public void Put(string path, string content) => Files[path] = content;
        private string Canon(string p) => Aliases.TryGetValue(p, out var c) ? c : p;

        /// <summary>Every path handed to FileExists / Sha256 / SameFile.</summary>
        public readonly List<string> Touched = new();

        public bool FileExists(string path) { FileExistsCalls++; Touched.Add(path); return Files.ContainsKey(path); }
        public string Sha256(string path) { Sha256Calls++; Touched.Add(path); return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Files[path]))); }
        public bool? SameFile(string a, string b) { SameFileCalls++; Touched.Add(a); Touched.Add(b); return Files.ContainsKey(a) && Files.ContainsKey(b) && string.Equals(Canon(a), Canon(b), StringComparison.OrdinalIgnoreCase); }
        public void Recycle(string path, nint? ownerHwnd)
        {
            OnRecycle?.Invoke(path);
            Owners.Add(ownerHwnd);
            Recycled.Add(path);
            Files.Remove(path);
        }
        public uint DriveTypeOf(string root) => DriveType(root);
        public IReadOnlyList<string> ProgramFilesFolders() => Array.Empty<string>();
        public void Sleep(int milliseconds) => Sleeps++;

        public bool IsReadOnly(string path) => false;
        public void ClearReadOnly(string path) { }
        public void CreateDirectory(string path) { }
        public void Copy(string from, string to) => throw new NotSupportedException();
        public void Move(string from, string to, bool overwrite) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException("the continuation never deletes for good");
        public ReleaseVersion? VersionOf(string exePath) => null;
        public bool LooksLikeClink(string exePath) => !NotClink.Contains(exePath);
        public void StripZoneIdentifier(string path) { }
        public IChildProcess Start(string exePath, IReadOnlyList<string> args) => throw new NotSupportedException();
    }

    /// <summary>The scene: Downloads\Clink.exe (the old copy) was moved to Programs\Clink\Clink.exe, which left a .bak next to it.</summary>
    private sealed class Scene : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeSystem Sys = new();
        public readonly FakeRunKey Run = new();
        public readonly FakeProbe Probe = new();
        public readonly string Root, Old, Exe, Bak, Hash, StartMenu, Desktop;

        public Scene(string? root = null)
        {
            Root = root ?? Dir.Path;
            Old = Path.Combine(Root, "Downloads", "Clink.exe");
            Exe = Path.Combine(Root, "Programs", "Clink", "Clink.exe");
            Bak = Exe + ".bak";
            StartMenu = Path.Combine(Root, "StartMenu");
            Desktop = Path.Combine(Root, "Desktop");
            Sys.Put(Old, "old bits");
            Sys.Put(Exe, "old bits");
            Sys.Put(Bak, "older install");
            Hash = Sys.Sha256(Old);
            Run.Value = $"\"{Old}\" --minimized";
        }

        public void LinkTo(string folder, string target) => Check.True(Shortcuts.Create(folder, target, notifyShell: false, previousExe: null).Ok);
        public string? LinkTarget(string folder) => Shortcuts.ReadTarget(Shortcuts.LinkPath(folder));

        public ContinuationReport Run_(string? devFolder = null, bool elevated = false, bool quiet = false, IReadOnlyList<string>? folders = null, nint? owner = 99, string? from = null)
            => SetupContinuation.RunAsync(from ?? Old, Hash, Exe, Sys, Run, Probe, devFolder, elevated, quiet, () => owner, folders ?? new[] { StartMenu, Desktop }, notifyShell: false).GetAwaiter().GetResult();

        public void Dispose() => Dir.Dispose();
    }

    // ------------------------------------------------------------------ the whole thing, in order

    [UnitTest]
    public static void The_continuation_retargets_the_run_key_then_the_links_then_recycles_the_backup_then_the_old_copy()
    {
        using var s = new Scene();
        s.LinkTo(s.StartMenu, s.Old);
        string other = Path.Combine(s.Root, "installer", "Clink.exe");
        s.LinkTo(s.Desktop, other);

        var seen = new List<string>();
        s.Run.OnWrite = _ => seen.Add("runkey, link still old: " + (s.LinkTarget(s.StartMenu) == s.Old));
        s.Sys.OnRecycle = path => seen.Add($"recycle {Path.GetFileName(path)} (link new: {s.LinkTarget(s.StartMenu) == s.Exe}, run key new: {s.Run.Value!.Contains(s.Exe)}, bak gone: {!s.Sys.Files.ContainsKey(s.Bak)})");

        var report = s.Run_();

        Check.Equal(RunKeyOutcome.Retargeted, report.RunKey);
        Check.Equal($"\"{s.Exe}\" --minimized", s.Run.Value);
        Check.Equal(s.Exe, s.LinkTarget(s.StartMenu), "the Start menu link points at the new exe");
        Check.Equal(other, s.LinkTarget(s.Desktop), "a link to another Clink.exe is not ours to move");
        Check.Equal(1, report.Links.Count);
        Check.Equal(ShortcutOutcome.Replaced, report.Links[0].Outcome);
        Check.Equal(BackupOutcome.Recycled, report.Backup);
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, report.Cleanup);
        Check.False(report.DryRun);
        Check.Equal(new[] { "runkey", "links", "backup", "cleanup" }, report.Steps);
        Check.Equal(new[] { s.Bak, s.Old }, s.Sys.Recycled.ToArray());
        Check.Equal(new[]
        {
            "runkey, link still old: True",
            "recycle Clink.exe.bak (link new: True, run key new: True, bak gone: False)",
            "recycle Clink.exe (link new: True, run key new: True, bak gone: True)",
        }, seen.ToArray());
        Check.True(s.Sys.Files.ContainsKey(s.Exe), "the new copy stays");
        Check.Equal(new nint?[] { 99, 99 }, s.Sys.Owners.ToArray());
    }

    // ------------------------------------------------------------------ the Run key

    [UnitTest]
    public static void The_run_key_is_retargeted_only_when_it_started_the_old_copy()
    {
        using var s = new Scene();
        s.Run.Value = @"""C:\Tools\Other.exe"" --minimized";
        Check.Equal(RunKeyOutcome.NotOurs, s.Run_().RunKey);
        Check.Equal(@"""C:\Tools\Other.exe"" --minimized", s.Run.Value);
        Check.Equal(0, s.Run.Writes);

        s.Run.Value = $"\"{Path.Combine(s.Root, "Else", "Clink.exe")}\" --minimized";   // another copy of Clink: not ours to move
        Check.Equal(RunKeyOutcome.NotOurs, s.Run_().RunKey);
        Check.Equal(0, s.Run.Writes);

        s.Run.Value = null;
        Check.Equal(RunKeyOutcome.NotPresent, s.Run_().RunKey);
        s.Run.Value = "  ";
        Check.Equal(RunKeyOutcome.NotPresent, s.Run_().RunKey);
        Check.Equal(0, s.Run.Writes);

        s.Run.Value = $"\"{s.Exe}\" --minimized";
        Check.Equal(RunKeyOutcome.AlreadyCurrent, s.Run_().RunKey);
        Check.Equal(0, s.Run.Writes);

        s.Run.Value = $"{s.Old.ToUpperInvariant()} --minimized";   // unquoted, another case
        Check.Equal(RunKeyOutcome.Retargeted, s.Run_().RunKey);
        Check.Equal($"\"{s.Exe}\" --minimized", s.Run.Value);
    }

    [UnitTest]
    public static void A_run_key_that_names_the_old_file_by_another_spelling_is_retargeted()
    {
        using var s = new Scene();
        string alias = Path.Combine(s.Root, "DOWNLO~1", "Clink.exe");
        s.Sys.Put(alias, "old bits");
        s.Sys.Aliases[alias] = s.Old;
        s.Run.Value = $"\"{alias}\" --minimized";
        Check.Equal(RunKeyOutcome.Retargeted, s.Run_().RunKey);
        Check.Equal($"\"{s.Exe}\" --minimized", s.Run.Value);
    }

    [UnitTest]
    public static void A_failing_run_key_does_not_stop_the_rest()
    {
        using var s = new Scene();
        s.Run.ThrowOnWrite = true;
        s.LinkTo(s.StartMenu, s.Old);
        var report = s.Run_();
        Check.Equal(RunKeyOutcome.Failed, report.RunKey);
        Check.Equal(s.Exe, s.LinkTarget(s.StartMenu));
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, report.Cleanup);
    }

    [UnitTest]
    public static void RetargetRunValue_is_pure_and_exact()
    {
        const string old = @"C:\Users\u\Downloads\Clink.exe", fresh = @"C:\Users\u\AppData\Local\Programs\Clink\Clink.exe";
        string want = $"\"{fresh}\" --minimized";
        Check.Equal(want, SetupContinuation.RetargetRunValue($"\"{old}\" --minimized", old, fresh));
        Check.Equal(want, SetupContinuation.RetargetRunValue($"\"{old}\"", old, fresh), "quoted, no arguments");
        Check.Equal(want, SetupContinuation.RetargetRunValue($"{old} --minimized", old, fresh), "unquoted, before ' --'");
        Check.Equal(want, SetupContinuation.RetargetRunValue(old, old, fresh), "just the path");
        Check.Equal(want, SetupContinuation.RetargetRunValue($"\"{old.ToUpperInvariant()}\" --minimized", old, fresh), "case");
        Check.Equal(want, SetupContinuation.RetargetRunValue($"\"{old.Replace('\\', '/')}\" --minimized", old, fresh), "slashes");
        Check.Equal(want, SetupContinuation.RetargetRunValue($"\"{@"C:\Users\u\Downloads\..\Downloads\Clink.exe"}\" --minimized", old, fresh), "dot segments");

        Check.Null(SetupContinuation.RetargetRunValue($"\"{old}x\" --minimized", old, fresh), "a different file");
        Check.Null(SetupContinuation.RetargetRunValue($"\"{@"C:\Users\u\Downloads\Clink (1).exe"}\" --minimized", old, fresh));
        Check.Null(SetupContinuation.RetargetRunValue($"\"{fresh}\" --minimized", old, fresh), "already the new one");
        Check.Null(SetupContinuation.RetargetRunValue(@"""C:\Tools\Other.exe"" --minimized", old, fresh));
        Check.Null(SetupContinuation.RetargetRunValue($"cmd /c \"{old}\"", old, fresh), "something that merely mentions the path");
        Check.Null(SetupContinuation.RetargetRunValue($"\"{old} --minimized", old, fresh), "unterminated quote");
        Check.Null(SetupContinuation.RetargetRunValue("", old, fresh));
        Check.Null(SetupContinuation.RetargetRunValue(null, old, fresh));
        Check.Null(SetupContinuation.RetargetRunValue($"\"{old}\" --minimized", "", fresh));

        Check.Null(SetupContinuation.RetargetRunValue(@"""C:\x\short.exe"" --minimized", old, fresh), "no file check given");
        Check.Equal(want, SetupContinuation.RetargetRunValue(@"""C:\x\short.exe"" --minimized", old, fresh, p => p == @"C:\x\short.exe"), "the same file under another name");
        Check.Null(SetupContinuation.RetargetRunValue(@"""C:\x\short.exe"" --minimized", old, fresh, _ => false));
        Check.Null(SetupContinuation.RetargetRunValue(@"""C:\x\short.exe"" --minimized", old, fresh, _ => throw new IOException()), "a failing check is a no");

        Check.Equal(@"C:\a b\c.exe", SetupContinuation.RunValueExe(@"""C:\a b\c.exe"" --minimized"));
        Check.Equal(@"C:\a b\c.exe", SetupContinuation.RunValueExe(@"C:\a b\c.exe --minimized"));
        Check.Null(SetupContinuation.RunValueExe(@"""unterminated"));
    }

    // ------------------------------------------------------------------ --dev

    [UnitTest]
    public static void A_dev_instance_never_touches_the_registry_and_only_its_own_folder()
    {
        using var outer = new TempDir();
        string dev = Path.Combine(outer.Path, "dev");
        using var s = new Scene(dev);
        string outsideFolder = Path.Combine(outer.Path, "RealStartMenu");
        s.LinkTo(s.StartMenu, s.Old);                 // inside the dev folder
        s.LinkTo(outsideFolder, s.Old);               // a link of the real Start menu that happens to point at the old path
        var report = s.Run_(devFolder: dev, folders: new[] { s.StartMenu, outsideFolder });

        Check.Equal(RunKeyOutcome.DevLogOnly, report.RunKey);
        Check.Equal(0, s.Run.Reads, "not even read");
        Check.Equal(0, s.Run.Writes);
        Check.Equal(s.Exe, s.LinkTarget(s.StartMenu), "dev links are retargeted");
        Check.Equal(s.Old, s.LinkTarget(outsideFolder), "a link outside the dev folder stays");
        Check.Equal(ShortcutOutcome.Refused, report.Links.Single(l => l.Path.StartsWith(outsideFolder, StringComparison.OrdinalIgnoreCase)).Outcome);
        Check.Equal(BackupOutcome.Recycled, report.Backup);
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, report.Cleanup);
    }

    [UnitTest]
    public static void A_dev_instance_recycles_nothing_outside_the_dev_folder()
    {
        using var outer = new TempDir();
        string dev = Path.Combine(outer.Path, "dev");
        using var s = new Scene(outer.Path);   // everything sits next to the dev folder, not in it
        var report = s.Run_(devFolder: dev);
        Check.Equal(BackupOutcome.Skipped, report.Backup);
        Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, report.Cleanup);
        Check.Equal(0, s.Sys.Recycled.Count);
        Check.True(s.Sys.Files.ContainsKey(s.Old) && s.Sys.Files.ContainsKey(s.Bak));
        Check.Equal(0, s.Run.Reads + s.Run.Writes);
    }

    // ------------------------------------------------------------------ quiet: dry run

    [UnitTest]
    public static void Under_quiet_mode_the_recycling_is_a_dry_run_that_only_logs()
    {
        using var outer = new TempDir();
        string dev = Path.Combine(outer.Path, "dev");
        using var s = new Scene(dev);
        var lines = new ConcurrentQueue<string>();
        void Collect(string line) => lines.Enqueue(line);
        Log.Line += Collect;
        ContinuationReport report;
        try { report = s.Run_(devFolder: dev, quiet: true); }
        finally { Log.Line -= Collect; }

        Check.True(report.DryRun);
        Check.Equal(BackupOutcome.DryRun, report.Backup);
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, report.Cleanup, "the flow ends as if it worked");
        Check.Equal(0, s.Sys.Recycled.Count, "the real system was never asked to recycle");
        Check.True(s.Sys.Files.ContainsKey(s.Old) && s.Sys.Files.ContainsKey(s.Bak), "nothing moved");
        string log = string.Join("\n", lines);
        Check.Contains(log, "quiet: would recycle " + s.Bak);
        Check.Contains(log, "quiet: would recycle " + s.Old);
    }

    [UnitTest]
    public static void The_dry_run_system_logs_instead_of_recycling_and_hides_the_file_afterwards()
    {
        var inner = new FakeSystem();
        inner.Put(@"C:\x\a.exe", "x");
        var dry = new DryRunRecycleSystem(inner);
        dry.Recycle(@"C:\x\a.exe", 5);
        Check.Equal(new[] { @"C:\x\a.exe" }, dry.WouldRecycle.ToArray());
        Check.Equal(0, inner.Recycled.Count);
        Check.False(dry.FileExists(@"C:\x\a.exe"), "looks gone to the caller");
        Check.True(inner.FileExists(@"C:\x\a.exe"), "is not gone");
        Check.False(dry.FileExists(@"C:\X\A.EXE"));
        Check.Equal(3u, dry.DriveTypeOf(@"C:\"));
    }

    // ------------------------------------------------------------------ elevated, bad record

    [UnitTest]
    public static void An_elevated_continuation_touches_nothing()
    {
        using var s = new Scene();
        s.LinkTo(s.StartMenu, s.Old);
        var report = s.Run_(elevated: true);
        Check.Equal(RunKeyOutcome.SkippedElevated, report.RunKey);
        Check.Equal(0, s.Run.Reads + s.Run.Writes);
        Check.Equal(s.Old, s.LinkTarget(s.StartMenu));
        Check.Equal(0, report.Links.Count);
        Check.Equal(BackupOutcome.Skipped, report.Backup);
        Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, report.Cleanup);
        Check.Equal(0, s.Sys.Recycled.Count);
    }

    [UnitTest]
    public static void Without_a_usable_move_record_nothing_happens()
    {
        using var s = new Scene();
        s.LinkTo(s.StartMenu, s.Old);
        foreach (var (from, sha) in new (string?, string?)[] { (null, s.Hash), (s.Old, null), (s.Old, "xyz"), ("Clink.exe", s.Hash), (s.Old + ":stream", s.Hash) })
        {
            var report = SetupContinuation.RunAsync(from, sha, s.Exe, s.Sys, s.Run, s.Probe, null, false, false, () => null, new[] { s.StartMenu }, notifyShell: false).GetAwaiter().GetResult();
            Check.Equal(RunKeyOutcome.NoRecord, report.RunKey, from + " / " + sha);
            Check.Equal(0, report.Links.Count);
            Check.Equal(s.Old, s.LinkTarget(s.StartMenu));
            Check.Equal(BackupOutcome.Skipped, report.Backup);
            Check.Null(report.Cleanup);
        }
        Check.Equal(0, s.Run.Reads + s.Run.Writes);
        Check.Equal(0, s.Sys.Recycled.Count, "nothing is recycled on a bad record");
        Check.True(s.Sys.Files.ContainsKey(s.Old));
    }

    [UnitTest]
    public static void A_missing_backup_and_missing_old_copy_are_fine()
    {
        using var s = new Scene();
        s.Sys.Files.Remove(s.Bak);
        s.Sys.Files.Remove(s.Old);
        s.Run.Value = null;
        var report = s.Run_();
        Check.Equal(BackupOutcome.NoBackup, report.Backup);
        Check.Equal(SelfInstall.CleanupOutcome.AlreadyGone, report.Cleanup);
        Check.Equal(0, s.Sys.Recycled.Count);
    }

    // ------------------------------------------------------------------ repair round 1: what the old copy is, and where

    private static bool Touches(FakeSystem sys, string prefix) => sys.Touched.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [UnitTest]
    public static void A_moved_from_path_on_a_share_or_mapped_drive_is_never_touched()
    {
        using var s = new Scene();
        s.Sys.DriveType = root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? 4u : 3u;   // Z: is a mapped network drive
        foreach (string remote in new[] { @"\\srv\share\Clink.exe", @"\\?\UNC\srv\share\Clink.exe", @"Z:\Clink.exe" })
        {
            s.Sys.Touched.Clear();
            int before = s.Sys.FileExistsCalls + s.Sys.SameFileCalls + s.Sys.Sha256Calls;
            var outcome = SelfInstall.CleanupOldCopy(remote, s.Hash, s.Exe, s.Sys, false);
            Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, outcome, remote);
            Check.Equal(before, s.Sys.FileExistsCalls + s.Sys.SameFileCalls + s.Sys.Sha256Calls, "zero FileExists / SameFile / Sha256 calls: " + remote);
            Check.Equal(0, s.Sys.Touched.Count);
            Check.Equal(0, s.Sys.Recycled.Count);
        }
    }

    [UnitTest]
    public static void A_non_clink_moved_from_file_is_never_recycled()
    {
        using var s = new Scene();
        s.Sys.NotClink.Add(s.Old);
        Check.Equal(SelfInstall.CleanupOutcome.NotAnExe, SelfInstall.CleanupOldCopy(s.Old, s.Hash, s.Exe, s.Sys, false));
        Check.Equal(0, s.Sys.Recycled.Count);
        Check.True(s.Sys.Files.ContainsKey(s.Old));
    }

    [UnitTest]
    public static void A_non_clink_moved_from_file_leaves_the_run_key_the_links_and_the_file_alone()
    {
        using var s = new Scene();
        s.Sys.NotClink.Add(s.Old);
        s.LinkTo(s.StartMenu, s.Old);
        var report = s.Run_();
        Check.Equal(RunKeyOutcome.NotOurs, report.RunKey);
        Check.Equal(0, s.Run.Reads + s.Run.Writes);
        Check.Equal(0, report.Links.Count);
        Check.Equal(s.Old, s.LinkTarget(s.StartMenu));
        Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, report.Cleanup);
        Check.False(s.Sys.Recycled.Contains(s.Old));
        Check.True(s.Sys.Files.ContainsKey(s.Old));
    }

    [UnitTest]
    public static void An_installer_managed_moved_from_folder_is_never_moved_from()
    {
        using var s = new Scene();
        s.Probe.WithUninstaller.Add(Path.GetDirectoryName(s.Old)!);
        s.LinkTo(s.StartMenu, s.Old);
        var report = s.Run_();
        Check.Equal(RunKeyOutcome.NotOurs, report.RunKey);
        Check.Equal(0, s.Run.Reads + s.Run.Writes);
        Check.Equal(0, report.Links.Count);
        Check.Equal(s.Old, s.LinkTarget(s.StartMenu));
        Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, report.Cleanup);
        Check.False(s.Sys.Recycled.Contains(s.Old));
        Check.True(s.Sys.Files.ContainsKey(s.Old));

        s.Probe.WithUninstaller.Clear();
        s.Probe.Recorded = Path.GetDirectoryName(s.Old);   // or the Inno uninstall key names that folder
        var byKey = s.Run_();
        Check.Equal(RunKeyOutcome.NotOurs, byKey.RunKey);
        Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, byKey.Cleanup);
        Check.False(s.Sys.Recycled.Contains(s.Old));
    }

    [UnitTest]
    public static void A_unc_moved_from_is_never_probed_and_only_an_exact_run_value_is_retargeted()
    {
        using var s = new Scene();
        const string unc = @"\\srv\share\Clink.exe";
        s.Sys.Touched.Clear();
        s.Run.Value = $"\"{unc}\" --minimized";
        var report = s.Run_(from: unc);
        Check.Equal(RunKeyOutcome.Retargeted, report.RunKey, "an exact string match needs no file check");
        Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, report.Cleanup);
        Check.False(Touches(s.Sys, @"\\srv"), "the share was never touched");
        Check.Equal(0, s.Sys.Recycled.Count(p => p == unc));
    }

    [UnitTest]
    public static void A_run_value_on_a_share_or_mapped_drive_is_not_ours_and_never_compared_by_file()
    {
        using var s = new Scene();
        s.Sys.DriveType = root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? 4u : 3u;
        foreach (string remote in new[] { @"\\srv\share\Clink.exe", @"\\?\UNC\srv\share\Clink.exe", @"Z:\apps\Clink.exe" })
        {
            s.Sys.Touched.Clear();
            s.Run.Value = $"\"{remote}\" --minimized";
            var report = s.Run_();
            Check.Equal(RunKeyOutcome.NotOurs, report.RunKey, remote);
            Check.False(Touches(s.Sys, @"\\srv") || Touches(s.Sys, @"\\?\UNC") || Touches(s.Sys, @"Z:"), "SameFile was not called with " + remote);
            Check.Equal(0, s.Run.Writes);
        }
    }

    [UnitTest]
    public static void A_backup_that_is_not_clink_is_not_recycled()
    {
        using var s = new Scene();
        s.Sys.NotClink.Add(s.Bak);
        var report = s.Run_();
        Check.Equal(BackupOutcome.Left, report.Backup);
        Check.False(s.Sys.Recycled.Contains(s.Bak));
        Check.True(s.Sys.Files.ContainsKey(s.Bak));
    }

    // ------------------------------------------------------------------ installer footprint

    private sealed class FakeProbe : IInstallerProbe
    {
        public HashSet<string> WithUninstaller = new(StringComparer.OrdinalIgnoreCase);
        public string? Recorded;
        public bool Throws;
        public bool HasUninstaller(string folder) { if (Throws) throw new IOException(); return WithUninstaller.Contains(folder); }
        public string? InnoInstallLocation() { if (Throws) throw new IOException(); return Recorded; }
    }

    [UnitTest]
    public static void A_folder_is_installer_managed_by_its_uninstaller_or_the_inno_key()
    {
        const string folder = @"C:\Users\u\AppData\Local\Programs\Clink";
        var none = new FakeProbe();
        Check.False(InstallerFootprint.IsManaged(folder, none));
        Check.False(InstallerFootprint.IsManaged(null, none));
        Check.False(InstallerFootprint.IsManaged("", none));

        var uninstaller = new FakeProbe { WithUninstaller = { folder } };
        Check.True(InstallerFootprint.IsManaged(folder, uninstaller));
        Check.True(InstallerFootprint.IsManaged(folder + @"\", uninstaller), "trailing separator");
        Check.False(InstallerFootprint.IsManaged(@"C:\Users\u\Documents\Clink", uninstaller));

        Check.True(InstallerFootprint.IsManaged(folder, new FakeProbe { Recorded = folder + @"\" }), "Inno records the folder with a trailing backslash");
        Check.True(InstallerFootprint.IsManaged(folder.ToUpperInvariant(), new FakeProbe { Recorded = folder }));
        Check.False(InstallerFootprint.IsManaged(folder, new FakeProbe { Recorded = @"C:\Elsewhere\Clink" }), "the installer put Clink somewhere else");
        Check.False(InstallerFootprint.IsManaged(folder, new FakeProbe { Throws = true }), "a registry or disk error is not a footprint");
        Check.Equal("{7D4A9E3B-5C21-4F6A-9B08-2E1C6A5D3F74}", InstallerFootprint.InnoAppId);
        Check.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7D4A9E3B-5C21-4F6A-9B08-2E1C6A5D3F74}_is1", InstallerFootprint.InnoUninstallKey);
    }

    [UnitTest]
    public static void The_real_probe_finds_an_uninstaller_in_a_temp_folder_only()
    {
        using var dir = new TempDir();
        var probe = new InstallerProbe();
        Check.False(probe.HasUninstaller(dir.Path));
        Check.False(probe.HasUninstaller(dir.File("missing")));
        File.WriteAllText(dir.File("Clink.exe"), "x");
        Check.False(probe.HasUninstaller(dir.Path), "Clink.exe is not an uninstaller");
        File.WriteAllText(dir.File("unins000.exe"), "x");
        Check.True(probe.HasUninstaller(dir.Path));
        Check.True(InstallerFootprint.IsManaged(dir.Path, new FakeProbe { WithUninstaller = { dir.Path } }));
    }

    // ------------------------------------------------------------------ elevation

    [UnitTest]
    public static void The_elevation_answer_can_be_forced_and_the_real_one_does_not_throw()
    {
        var before = Elevation.IsElevatedOverride;
        try
        {
            Elevation.IsElevatedOverride = true;
            Check.True(Elevation.IsElevated());
            Elevation.IsElevatedOverride = false;
            Check.False(Elevation.IsElevated());
            Elevation.IsElevatedOverride = null;
            bool real = Elevation.IsElevated();   // asks Windows (GetTokenInformation, TokenElevation); true or false, never an exception
            Check.Equal(real, Elevation.IsElevated(), "stable");
        }
        finally { Elevation.IsElevatedOverride = before; }
    }

    [UnitTest]
    public static void The_elevation_check_fails_closed_and_the_updater_shares_the_override()
    {
        var overrideBefore = Elevation.IsElevatedOverride;
        var tokenBefore = Elevation.TokenQueryOverride;
        var principalBefore = Elevation.PrincipalQueryOverride;
        try
        {
            Elevation.IsElevatedOverride = null;
            Elevation.TokenQueryOverride = () => false;
            Elevation.PrincipalQueryOverride = () => throw new InvalidOperationException("must not be asked");
            Check.False(Elevation.IsElevated(), "a readable token decides");
            Elevation.TokenQueryOverride = () => true;
            Check.True(Elevation.IsElevated());

            Elevation.TokenQueryOverride = () => throw new InvalidOperationException("token query failed");
            Elevation.PrincipalQueryOverride = () => false;
            Check.False(Elevation.IsElevated(), "token failure: the Administrators-group check answers");
            Elevation.PrincipalQueryOverride = () => true;
            Check.True(Elevation.IsElevated());
            Elevation.TokenQueryOverride = () => null;   // "could not be read" without an exception
            Elevation.PrincipalQueryOverride = () => false;
            Check.False(Elevation.IsElevated());

            Elevation.TokenQueryOverride = () => throw new InvalidOperationException("token query failed");
            Elevation.PrincipalQueryOverride = () => throw new InvalidOperationException("principal failed too");
            Check.True(Elevation.IsElevated(), "both failed: closed = elevated");

            Updater.IsElevatedOverride = false;
            Check.Equal((bool?)false, Elevation.IsElevatedOverride, "one override");
            Check.False(Elevation.IsElevated());
            Elevation.IsElevatedOverride = true;
            Check.Equal((bool?)true, Updater.IsElevatedOverride);
        }
        finally
        {
            Elevation.IsElevatedOverride = overrideBefore;
            Elevation.TokenQueryOverride = tokenBefore;
            Elevation.PrincipalQueryOverride = principalBefore;
        }
    }

    // ------------------------------------------------------------------ dev install environment

    [UnitTest]
    public static void The_dev_environment_puts_every_place_inside_the_dev_folder()
    {
        using var outer = new TempDir();
        string dev = Path.Combine(outer.Path, "dev");
        var env = InstallEnvironment.ForDev(dev);
        foreach (string? p in new[] { env.Downloads, env.Desktop, env.Documents, env.ProgramsFolder, env.StartMenu })
        {
            Check.NotNull(p);
            Check.True(InstallLocation.Under(InstallLocation.Normalize(p), dev), p);
        }
        Check.Equal(0, env.ProgramFiles.Count);
        Check.Equal(0, env.Temp.Count);
        Check.Equal(0, env.CloudEnvRoots.Count);
        Check.Null(env.CloudReader);
        Check.Equal(LocationClass.Downloads, InstallLocation.Classify(Path.Combine(env.Downloads!, "Clink.exe"), env));
        Check.Equal(LocationClass.Desktop, InstallLocation.Classify(Path.Combine(env.Desktop!, "Clink.exe"), env));
        Check.Equal(LocationClass.Documents, InstallLocation.Classify(Path.Combine(env.Documents!, "Clink", "Clink.exe"), env));
        Check.Equal(LocationClass.ProgramsFolder, InstallLocation.Classify(Path.Combine(env.ProgramsFolder!, "Clink", "Clink.exe"), env));
        Check.False(InstallLocation.IsDocumentsCloudSynced(env));
        Check.Equal(LocationClass.Other, InstallLocation.Classify(Path.Combine(Path.GetTempPath(), "x", "Clink.exe"), env), "the real Temp is not a dev-environment Temp");
        Check.Equal(LocationClass.Other, InstallLocation.Classify(@"C:\Program Files\X\Clink.exe", env), "no Program Files in a dev environment");
    }

    [UnitTest]
    public static void The_system_environment_knows_the_start_menu_folder()
    {
        var env = InstallEnvironment.FromSystem();
        Check.True(!string.IsNullOrEmpty(env.StartMenu), "the Programs folder of the Start menu");
    }
}
