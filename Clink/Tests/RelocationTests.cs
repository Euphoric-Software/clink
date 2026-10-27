using System.IO;
using System.Security.Cryptography;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Moving Clink.exe to its new home and removing the old copy, over an in-memory file system and a fake process seam.</summary>
internal static class RelocationTests
{
    private sealed class FakeChild : IChildProcess
    {
        private readonly bool _exits;
        public FakeChild(bool exits) => _exits = exits;
        public bool WaitForExit(int milliseconds) => _exits;
        public void Dispose() { }
    }

    private sealed class FakeSystem : ISelfInstallSystem
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ReleaseVersion> Versions = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Recycled = new();
        public readonly List<string> Deleted = new();
        public readonly List<(string Exe, string[] Args)> Started = new();
        public bool CorruptCopy, ThrowOnStart, ChildExits, ThrowOnRecycle, AbortRecycle;
        public int RecycleFailuresLeft, Sleeps, RecycleCalls;
        public readonly HashSet<string> Foreign = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<char, uint> Drives = new();
        public readonly List<string> ProgramFiles = new();
        public readonly List<nint?> Owners = new();
        public readonly List<string> ClearedReadOnly = new();
        public Func<string, string, Exception?>? MoveFault;
        public int MoveCalls;

        public void Put(string path, string content, string? version = null)
        {
            Files[path] = System.Text.Encoding.UTF8.GetBytes(content);
            if (version != null) Versions[path] = ReleaseVersion.Parse(version)!.Value;
        }
        public string Text(string path) => System.Text.Encoding.UTF8.GetString(Files[path]);

        public bool FileExists(string path) => Files.ContainsKey(path);
        public bool IsReadOnly(string path) => ReadOnly.Contains(path);
        public void CreateDirectory(string path) => Directories.Add(path);
        public string Sha256(string path) => Convert.ToHexString(SHA256.HashData(Files[path]));
        public void Copy(string from, string to) => Files[to] = CorruptCopy ? new byte[] { 1, 2, 3 } : Files[from].ToArray();
        public void Move(string from, string to, bool overwrite)
        {
            MoveCalls++;
            if (MoveFault?.Invoke(from, to) is { } fault) throw fault;
            if (!overwrite && Files.ContainsKey(to)) throw new IOException("exists: " + to);
            Files[to] = Files[from];
            Files.Remove(from);
            if (Versions.TryGetValue(from, out var v)) { Versions[to] = v; Versions.Remove(from); }
        }
        public int DeleteFailuresLeft;
        public bool DeleteAlwaysFails;
        public void Delete(string path)
        {
            if (DeleteAlwaysFails) throw new IOException("scanner holds it");
            if (DeleteFailuresLeft > 0) { DeleteFailuresLeft--; throw new IOException("scanner holds it"); }
            Files.Remove(path); Deleted.Add(path);
        }
        public void ClearReadOnly(string path) { ClearedReadOnly.Add(path); ReadOnly.Remove(path); }
        private string Canon(string p) => Aliases.TryGetValue(p, out var c) ? c : p;
        public bool SameFile(string a, string b) => Files.ContainsKey(a) && Files.ContainsKey(b) && string.Equals(Canon(a), Canon(b), StringComparison.OrdinalIgnoreCase);
        public bool LooksLikeClink(string exePath) => !Foreign.Contains(exePath);
        public uint DriveTypeOf(string root) => Drives.TryGetValue(char.ToUpperInvariant(root[0]), out var t) ? t : 3u;
        public IReadOnlyList<string> ProgramFilesFolders() => ProgramFiles;
        public ReleaseVersion? VersionOf(string exePath) => Versions.TryGetValue(exePath, out var v) ? v : null;
        public void StripZoneIdentifier(string path) { Stripped.Add(path); }
        public readonly List<string> Stripped = new();
        public IChildProcess Start(string exePath, IReadOnlyList<string> args)
        {
            if (ThrowOnStart) throw new IOException("cannot start");
            Started.Add((exePath, args.ToArray()));
            return new FakeChild(ChildExits);
        }
        public void Recycle(string path, nint? ownerHwnd)
        {
            RecycleCalls++;
            Owners.Add(ownerHwnd);
            if (AbortRecycle) throw new RecycleAbortedException("declined");
            if (ThrowOnRecycle) throw new IOException("busy");
            if (RecycleFailuresLeft > 0) { RecycleFailuresLeft--; throw new IOException("in use"); }
            Recycled.Add(path);
            Files.Remove(path);
        }
        public void Sleep(int milliseconds) => Sleeps++;
    }

    private const string Src = @"C:\Users\u\Downloads\Clink.exe";
    private const string Folder = @"C:\Users\u\AppData\Local\Programs\Clink";
    private const string Target = Folder + @"\Clink.exe";
    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("0.4.2")!.Value;

    private static RelocationPlan PlanOf(FakeSystem sys, bool elevated = false, string? src = null) => SelfInstall.Plan(src ?? Src, Folder, elevated, Current, sys);

    // ------------------------------------------------------------------ plan

    [UnitTest]
    public static void An_elevated_process_is_refused()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "exe");
        var plan = PlanOf(sys, elevated: true);
        Check.Equal(RelocationKind.Refused, plan.Kind);
        var r = SelfInstall.Execute(plan, 1, null, sys);
        Check.Equal(RelocationOutcome.Refused, r.Outcome);
        Check.Equal(0, sys.Started.Count);
        Check.False(sys.Files.ContainsKey(Target));
    }

    [UnitTest]
    public static void The_same_path_is_a_no_op_whatever_the_case_or_separators()
    {
        var sys = new FakeSystem();
        sys.Put(Target, "exe");
        Check.Equal(RelocationKind.NoOp, SelfInstall.Plan(Target, Folder, false, Current, sys).Kind);
        Check.Equal(RelocationKind.NoOp, SelfInstall.Plan(Target.ToUpperInvariant(), Folder + @"\", false, Current, sys).Kind);
        Check.Equal(RelocationOutcome.NothingToDo, SelfInstall.Execute(PlanOf(sys, src: Target), 1, null, sys).Outcome);
        Check.Equal(0, sys.Started.Count);
    }

    [UnitTest]
    public static void A_relative_path_is_refused()
    {
        var sys = new FakeSystem();
        Check.Equal(RelocationKind.Refused, SelfInstall.Plan("Clink.exe", Folder, false, Current, sys).Kind);
        Check.Equal(RelocationKind.Refused, SelfInstall.Plan(Src, "", false, Current, sys).Kind);
    }

    [UnitTest]
    public static void A_newer_or_equal_target_is_launched_instead_of_overwritten()
    {
        foreach (string v in new[] { "0.4.2", "0.5.0", "1.0.0" })
        {
            var sys = new FakeSystem();
            sys.Put(Src, "this one");
            sys.Put(Target, "existing", v);
            var plan = PlanOf(sys);
            Check.Equal(RelocationKind.LaunchExisting, plan.Kind, v);
            var r = SelfInstall.Execute(plan, 77, null, sys);
            Check.Equal(RelocationOutcome.LaunchedExisting, r.Outcome, v);
            Check.True(r.ShouldExit);
            Check.Equal("existing", sys.Text(Target), "not overwritten");
            Check.Equal(Target, sys.Started.Single().Exe);
            Check.Equal("--after-pid", sys.Started.Single().Args[0]);
            Check.False(sys.Files.ContainsKey(Target + ".new") || sys.Files.ContainsKey(Target + ".bak"));
        }
    }

    [UnitTest]
    public static void An_older_or_unversioned_target_is_replaced_with_a_backup_kept_until_the_child_lives()
    {
        foreach (string? v in new string?[] { "0.4.1", "0.3.0", "0.4.2-dev.5", null })
        {
            var sys = new FakeSystem();
            sys.Put(Src, "new bits");
            sys.Put(Target, "old bits", v);
            var plan = PlanOf(sys);
            Check.Equal(RelocationKind.Install, plan.Kind, v ?? "unknown");
            var r = SelfInstall.Execute(plan, 42, null, sys);
            Check.Equal(RelocationOutcome.Moved, r.Outcome, r.Error);
            Check.Equal("new bits", sys.Text(Target));
            Check.Equal("new bits", sys.Text(Src), "the source stays until the new copy removes it");
            Check.False(sys.Files.ContainsKey(Target + ".new"));
            Check.Equal("old bits", sys.Text(Target + ".bak"), "the backup stays until the next install (no shell call while the parent holds the mutex)");
            Check.Equal(0, sys.Recycled.Count);
            Check.False(sys.Deleted.Contains(Target + ".bak"), "never File.Delete on the backup");
            Check.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("new bits"))), r.Sha256);
        }
    }

    // ------------------------------------------------------------------ execute

    [UnitTest]
    public static void A_first_install_creates_the_folder_copies_verifies_and_starts_with_the_documented_arguments()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "exe bits");
        var r = SelfInstall.Execute(PlanOf(sys), 4321, null, sys);
        Check.Equal(RelocationOutcome.Moved, r.Outcome, r.Error);
        Check.True(r.ShouldExit);
        Check.True(sys.Directories.Contains(Folder));
        Check.Equal("exe bits", sys.Text(Target));
        var (exe, args) = sys.Started.Single();
        Check.Equal(Target, exe);
        Check.Equal(new[] { "--after-pid", "4321", "--setup-continue", "--setup-moved-from", Src, r.Sha256! }, args);
    }

    [UnitTest]
    public static void Dev_switches_are_passed_on()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "exe");
        SelfInstall.Execute(PlanOf(sys), 9, new[] { "--dev", @"C:\d" }, sys);
        var args = sys.Started.Single().Args;
        Check.Equal(new[] { "--dev", @"C:\d" }, args.TakeLast(2).ToArray());
        Check.Equal("--after-pid", args[0]);
    }

    [UnitTest]
    public static void A_hash_mismatch_removes_the_copy_and_changes_nothing_else()
    {
        var sys = new FakeSystem { CorruptCopy = true };
        sys.Put(Src, "exe bits");
        sys.Put(Target, "old", "0.1.0");
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.Contains(r.Error, "hash");
        Check.Equal("old", sys.Text(Target));
        Check.False(sys.Files.ContainsKey(Target + ".new"));
        Check.Equal(0, sys.Started.Count);
        Check.Equal("exe bits", sys.Text(Src));
    }

    [UnitTest]
    public static void A_read_only_target_is_not_touched()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "new");
        sys.Put(Target, "old", "0.1.0");
        sys.ReadOnly.Add(Target);
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.Contains(r.Error, "read-only");
        Check.Equal("old", sys.Text(Target));
        Check.False(sys.Files.ContainsKey(Target + ".new") || sys.Files.ContainsKey(Target + ".bak"));
        Check.Equal(0, sys.Started.Count);
    }

    [UnitTest]
    public static void A_launch_failure_rolls_back_a_first_install()
    {
        var sys = new FakeSystem { ThrowOnStart = true };
        sys.Put(Src, "exe");
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.False(r.ShouldExit);
        Check.False(sys.Files.ContainsKey(Target), "our copy is deleted");
        Check.False(sys.Files.ContainsKey(Target + ".new"));
        Check.Equal("exe", sys.Text(Src));
    }

    [UnitTest]
    public static void A_launch_failure_restores_the_previous_target()
    {
        var sys = new FakeSystem { ThrowOnStart = true };
        sys.Put(Src, "new");
        sys.Put(Target, "old", "0.1.0");
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.Equal("old", sys.Text(Target));
        Check.False(sys.Files.ContainsKey(Target + ".bak"));
        Check.False(sys.Files.ContainsKey(Target + ".new"));
    }

    [UnitTest]
    public static void A_child_that_ends_within_two_seconds_rolls_back()
    {
        var sys = new FakeSystem { ChildExits = true };
        sys.Put(Src, "new");
        sys.Put(Target, "old", "0.1.0");
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.Contains(r.Error, "ended");
        Check.Equal("old", sys.Text(Target));
        Check.False(sys.Files.ContainsKey(Target + ".bak"));

        var fresh = new FakeSystem { ChildExits = true };
        fresh.Put(Src, "new");
        Check.Equal(RelocationOutcome.Failed, SelfInstall.Execute(PlanOf(fresh), 1, null, fresh).Outcome);
        Check.False(fresh.Files.ContainsKey(Target));
    }

    [UnitTest]
    public static void The_mark_of_the_web_is_kept_unless_the_flag_is_set()
    {
        Check.False(SelfInstall.StripMarkOfTheWeb, "default: leave the Zone.Identifier stream alone");
        var sys = new FakeSystem();
        sys.Put(Src, "x");
        SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(0, sys.Stripped.Count);
        SelfInstall.StripMarkOfTheWeb = true;
        try
        {
            var on = new FakeSystem();
            on.Put(Src, "x");
            SelfInstall.Execute(PlanOf(on), 1, null, on);
            Check.Equal(new[] { Target }, on.Stripped.ToArray());
        }
        finally { SelfInstall.StripMarkOfTheWeb = false; }
    }

    [UnitTest]
    public static void The_default_target_is_the_per_user_programs_folder()
    {
        Check.Equal(@"C:\L\Programs\Clink", SelfInstall.DefaultTargetFolder(@"C:\L"));
    }

    // ------------------------------------------------------------------ cleanup of the old copy

    private static string HashOf(string s) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    [UnitTest]
    public static void The_old_copy_goes_to_the_recycle_bin_when_its_hash_still_matches()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "exe");
        var o = SelfInstall.CleanupOldCopy(Src, HashOf("exe").ToLowerInvariant(), Target, sys, elevated: false);
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, o);
        Check.Equal(new[] { Src }, sys.Recycled.ToArray());
        Check.Equal(0, sys.Deleted.Count, "never a permanent delete");
    }

    [UnitTest]
    public static void A_changed_file_is_not_deleted()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "someone replaced this");
        Check.Equal(SelfInstall.CleanupOutcome.HashMismatch, SelfInstall.CleanupOldCopy(Src, HashOf("exe"), Target, sys, elevated: false));
        Check.Equal(0, sys.Recycled.Count);
        Check.True(sys.Files.ContainsKey(Src));
    }

    [UnitTest]
    public static void Only_an_exe_that_is_not_the_running_one_is_ever_removed()
    {
        var sys = new FakeSystem();
        foreach (string p in new[] { @"C:\Users\u\Documents\notes.txt", @"C:\Users\u\Downloads\Clink.exe.bak", @"C:\Users\u\Downloads\Clink.dll", @"C:\Users\u\Downloads" })
        {
            sys.Put(p, "x");
            Check.Equal(SelfInstall.CleanupOutcome.NotAnExe, SelfInstall.CleanupOldCopy(p, HashOf("x"), Target, sys, elevated: false), p);
        }
        sys.Put(Target, "x");
        Check.Equal(SelfInstall.CleanupOutcome.IsSelf, SelfInstall.CleanupOldCopy(Target, HashOf("x"), Target, sys, elevated: false));
        Check.Equal(SelfInstall.CleanupOutcome.IsSelf, SelfInstall.CleanupOldCopy(Target.ToUpperInvariant(), HashOf("x"), Target, sys, elevated: false));
        Check.Equal(SelfInstall.CleanupOutcome.NotAnExe, SelfInstall.CleanupOldCopy("Clink.exe", HashOf("x"), Target, sys, elevated: false), "relative");
        Check.Equal(SelfInstall.CleanupOutcome.NotAnExe, SelfInstall.CleanupOldCopy(Src, null, Target, sys, elevated: false), "no hash");
        Check.Equal(SelfInstall.CleanupOutcome.NotAnExe, SelfInstall.CleanupOldCopy(null, HashOf("x"), Target, sys, elevated: false));
        Check.Equal(0, sys.Recycled.Count);
    }

    [UnitTest]
    public static void A_missing_old_copy_is_fine_and_a_busy_one_is_retried()
    {
        var gone = new FakeSystem();
        Check.Equal(SelfInstall.CleanupOutcome.AlreadyGone, SelfInstall.CleanupOldCopy(Src, HashOf("x"), Target, gone, elevated: false));

        var busy = new FakeSystem { RecycleFailuresLeft = 3 };
        busy.Put(Src, "x");
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, SelfInstall.CleanupOldCopy(Src, HashOf("x"), Target, busy, elevated: false, attempts: 8));
        Check.Equal(3, busy.Sleeps);

        var stuck = new FakeSystem { ThrowOnRecycle = true };
        stuck.Put(Src, "x");
        Check.Equal(SelfInstall.CleanupOutcome.Failed, SelfInstall.CleanupOldCopy(Src, HashOf("x"), Target, stuck, elevated: false, attempts: 3));
        Check.True(stuck.Files.ContainsKey(Src));
    }

    // ------------------------------------------------------------------ file identity

    private const string ShortSrc = @"C:\Users\u\APPDATA~1\Local\Programs\Clink\Clink.exe";

    [UnitTest]
    public static void Two_spellings_of_one_file_are_a_no_op_and_never_recycled()
    {
        var sys = new FakeSystem();
        sys.Put(Target, "exe");
        sys.Put(ShortSrc, "exe");
        sys.Aliases[ShortSrc] = Target;   // 8.3 name / junction / subst drive / hard link: one file, a different string
        var plan = SelfInstall.Plan(ShortSrc, Folder, false, Current, sys);
        Check.Equal(RelocationKind.NoOp, plan.Kind);
        Check.Equal(RelocationOutcome.NothingToDo, SelfInstall.Execute(plan, 1, null, sys).Outcome);
        Check.Equal(0, sys.Started.Count);

        Check.Equal(SelfInstall.CleanupOutcome.IsSelf, SelfInstall.CleanupOldCopy(ShortSrc, HashOf("exe"), Target, sys, elevated: false));
        Check.Equal(0, sys.RecycleCalls);
        Check.True(sys.Files.ContainsKey(ShortSrc) && sys.Files.ContainsKey(Target));

        // another file with the same bytes is not the same file
        var other = new FakeSystem();
        other.Put(Target, "exe");
        other.Put(Src, "exe");
        Check.Equal(RelocationKind.Install, SelfInstall.Plan(Src, Folder, false, Current, other).Kind);
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, SelfInstall.CleanupOldCopy(Src, HashOf("exe"), Target, other, elevated: false));
    }

    // ------------------------------------------------------------------ cleanup guards

    [UnitTest]
    public static void An_old_copy_the_recycle_bin_would_not_take_is_left_alone()
    {
        string[] places = { @"E:\usb\Clink.exe", @"F:\disc\Clink.exe", @"Z:\share\Clink.exe", @"\\nas\share\Clink.exe", @"\\?\UNC\nas\share\Clink.exe", @"C:\Program Files\X\Clink.exe", @"C:\Program Files (x86)\X\Clink.exe" };
        foreach (string p in places)
        {
            var sys = new FakeSystem();
            sys.Drives['E'] = 2; sys.Drives['F'] = 5; sys.Drives['Z'] = 4;
            sys.ProgramFiles.AddRange(new[] { @"C:\Program Files", @"C:\Program Files (x86)" });
            sys.Put(InstallLocation.Normalize(p), "exe");
            Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, SelfInstall.CleanupOldCopy(p, HashOf("exe"), Target, sys, elevated: false), p);
            Check.Equal(0, sys.RecycleCalls, p);
            Check.Equal(0, sys.Deleted.Count, p);
            Check.True(sys.Files.ContainsKey(InstallLocation.Normalize(p)), p);
        }
        var fixedDrive = new FakeSystem();
        fixedDrive.Put(Src, "exe");
        Check.Equal(SelfInstall.CleanupOutcome.Recycled, SelfInstall.CleanupOldCopy(Src, HashOf("exe"), Target, fixedDrive, elevated: false));
    }

    [UnitTest]
    public static void An_elevated_process_never_removes_the_old_copy()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "exe");
        Check.Equal(SelfInstall.CleanupOutcome.NotRecyclable, SelfInstall.CleanupOldCopy(Src, HashOf("exe"), Target, sys, elevated: true));
        Check.Equal(0, sys.RecycleCalls);
        Check.True(sys.Files.ContainsKey(Src));
    }

    [UnitTest]
    public static void A_path_with_an_alternate_stream_is_not_an_exe_to_remove()
    {
        var sys = new FakeSystem();
        foreach (string p in new[] { @"C:\x\a.exe:Zone.exe", @"C:\x\a.txt:s.exe", @"C:\x:y\a.exe" })
        {
            sys.Put(p, "x");
            Check.Equal(SelfInstall.CleanupOutcome.NotAnExe, SelfInstall.CleanupOldCopy(p, HashOf("x"), Target, sys, elevated: false), p);
        }
        Check.Equal(0, sys.RecycleCalls);
    }

    [UnitTest]
    public static void A_declined_shell_operation_is_final_and_the_owner_window_is_passed()
    {
        var aborted = new FakeSystem { AbortRecycle = true };
        aborted.Put(Src, "x");
        Check.Equal(SelfInstall.CleanupOutcome.Failed, SelfInstall.CleanupOldCopy(Src, HashOf("x"), Target, aborted, elevated: false, ownerHwnd: 77, attempts: 8));
        Check.Equal(1, aborted.RecycleCalls);
        Check.Equal(0, aborted.Sleeps);
        Check.Equal(new nint?[] { 77 }, aborted.Owners.ToArray());
        Check.True(aborted.Files.ContainsKey(Src));

        var plain = new FakeSystem();
        plain.Put(Src, "x");
        SelfInstall.CleanupOldCopy(Src, HashOf("x"), Target, plain, elevated: false);
        Check.Equal(new nint?[] { null }, plain.Owners.ToArray());
    }

    // ------------------------------------------------------------------ dev guard, foreign target

    [UnitTest]
    public static void A_dev_instance_refuses_a_target_outside_its_dev_folder()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "exe");
        string dev = @"C:\dev\data";
        var outside = SelfInstall.Plan(Src, Folder, false, Current, sys, devFolder: dev);
        Check.Equal(RelocationKind.Refused, outside.Kind);
        var r = SelfInstall.Execute(outside, 1, null, sys);
        Check.Equal(RelocationOutcome.Refused, r.Outcome);
        Check.Equal(0, sys.Started.Count);
        Check.False(sys.Files.ContainsKey(Target));
        Check.False(sys.Directories.Contains(Folder));

        Check.Equal(RelocationKind.Refused, SelfInstall.Plan(Src, @"C:\dev\data2\Clink", false, Current, sys, devFolder: dev).Kind, "a sibling with the same prefix");
        Check.Equal(RelocationKind.Install, SelfInstall.Plan(Src, dev + @"\Programs", false, Current, sys, devFolder: dev).Kind);
        Check.Equal(RelocationKind.Install, SelfInstall.Plan(Src, Folder, false, Current, sys, devFolder: null).Kind);
    }

    [UnitTest]
    public static void An_exe_at_the_target_that_is_not_clink_is_neither_overwritten_nor_launched()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "ours");
        sys.Put(Target, "someone else", "9.9.9");
        sys.Foreign.Add(Target);
        var plan = PlanOf(sys);
        Check.Equal(RelocationKind.Refused, plan.Kind);
        Check.Equal(RelocationOutcome.Refused, SelfInstall.Execute(plan, 1, null, sys).Outcome);
        Check.Equal("someone else", sys.Text(Target));
        Check.Equal(0, sys.Started.Count);

        // a plan made before the file changed: Execute checks again
        var stale = new RelocationPlan(RelocationKind.Install, Src, Target);
        var r = SelfInstall.Execute(stale, 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.Equal("someone else", sys.Text(Target));
        Check.False(sys.Files.ContainsKey(Target + ".new") || sys.Files.ContainsKey(Target + ".bak"));
    }

    // ------------------------------------------------------------------ swap robustness, rollback, backup

    [UnitTest]
    public static void A_locked_file_is_retried_briefly_and_a_read_only_copy_is_made_writable()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "exe");
        int fails = 3;
        sys.MoveFault = (from, to) => to == Target && fails-- > 0 ? new IOException("scanner has it open") : null;
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Moved, r.Outcome, r.Error);
        Check.Equal(3, sys.Sleeps);
        Check.Equal(new[] { Target + ".new" }, sys.ClearedReadOnly.ToArray());

        var denied = new FakeSystem();
        denied.Put(Src, "exe");
        int n = 1;
        denied.MoveFault = (from, to) => n-- > 0 ? new UnauthorizedAccessException("indexer") : null;
        Check.Equal(RelocationOutcome.Moved, SelfInstall.Execute(PlanOf(denied), 1, null, denied).Outcome);
        Check.Equal(1, denied.Sleeps);

        var forever = new FakeSystem();
        forever.Put(Src, "exe");
        forever.MoveFault = (from, to) => new IOException("locked");
        var f = SelfInstall.Execute(PlanOf(forever), 1, null, forever);
        Check.Equal(RelocationOutcome.Failed, f.Outcome);
        Check.Equal(SelfInstall.MoveAttempts, forever.MoveCalls);
        Check.Equal(SelfInstall.MoveAttempts - 1, forever.Sleeps);
        Check.False(forever.Files.ContainsKey(Target + ".new"), "the copy is cleaned up");
        Check.Equal("exe", forever.Text(Src));
    }

    [UnitTest]
    public static void A_failed_final_move_restores_the_previous_target()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "new");
        sys.Put(Target, "old", "0.1.0");
        sys.MoveFault = (from, to) => from == Target + ".new" && to == Target ? new IOException("av lock") : null;
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.Null(r.RollbackError);
        Check.Equal("old", sys.Text(Target));
        Check.False(sys.Files.ContainsKey(Target + ".bak") || sys.Files.ContainsKey(Target + ".new"));
        Check.Equal(0, sys.Started.Count);
    }

    [UnitTest]
    public static void A_rollback_that_does_not_fully_work_is_reported()
    {
        var sys = new FakeSystem { ThrowOnStart = true };
        sys.Put(Src, "new");
        sys.Put(Target, "old", "0.1.0");
        sys.MoveFault = (from, to) => from == Target + ".bak" ? new IOException("backup is locked") : null;
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.NotNull(r.RollbackError);
        Check.Contains(r.RollbackError, "backup is locked");
        Check.Contains(r.Error, "Rollback incomplete");
        Check.Contains(r.Error, "cannot start");
        Check.True(sys.Files.ContainsKey(Target + ".bak"), "the old copy is still safe in the backup");
        Check.False(r.ShouldExit);
    }

    [UnitTest]
    public static void A_backup_that_cannot_go_to_the_recycle_bin_is_left_not_deleted()
    {
        var sys = new FakeSystem();
        sys.Drives['C'] = 2;   // everything here counts as removable: the shell would delete for good
        sys.Put(Src, "new");
        sys.Put(Target, "old", "0.1.0");
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Moved, r.Outcome, r.Error);
        Check.Equal("old", sys.Text(Target + ".bak"));
        Check.Equal(0, sys.RecycleCalls);
        Check.False(sys.Deleted.Contains(Target + ".bak"));

        // an older .bak in the way that cannot be recycled stops the install before anything changes
        var stale = new FakeSystem();
        stale.Drives['C'] = 2;
        stale.Put(Src, "new");
        stale.Put(Target, "old", "0.1.0");
        stale.Put(Target + ".bak", "older");
        var f = SelfInstall.Execute(PlanOf(stale), 1, null, stale);
        Check.Equal(RelocationOutcome.Failed, f.Outcome);
        Check.Equal("old", stale.Text(Target));
        Check.Equal("older", stale.Text(Target + ".bak"));
        Check.False(stale.Files.ContainsKey(Target + ".new"));
        Check.Equal(0, stale.Started.Count);
    }

    [UnitTest]
    public static void A_stale_backup_goes_to_the_recycle_bin_before_a_new_one_is_made()
    {
        var sys = new FakeSystem();
        sys.Put(Src, "new");
        sys.Put(Target, "old", "0.1.0");
        sys.Put(Target + ".bak", "older");
        var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
        Check.Equal(RelocationOutcome.Moved, r.Outcome, r.Error);
        Check.Equal(1, sys.Recycled.Count(p => p == Target + ".bak"));   // only the stale one; the one made now stays until the next install
        Check.Equal("old", sys.Text(Target + ".bak"));
        Check.False(sys.Deleted.Contains(Target + ".bak"));
    }

    [UnitTest]
    public static void A_rollback_retries_removing_the_failed_copy_and_reports_when_it_cannot()
    {
        var flaky = new FakeSystem { ThrowOnStart = true, DeleteFailuresLeft = 2 };
        flaky.Put(Src, "new");
        flaky.Put(Target, "old", "0.1.0");
        var r = SelfInstall.Execute(PlanOf(flaky), 1, null, flaky);
        Check.Equal(RelocationOutcome.Failed, r.Outcome);
        Check.True(r.RollbackError == null, r.RollbackError);
        Check.Equal("old", flaky.Text(Target));
        Check.False(flaky.Files.ContainsKey(Target + ".bak"));

        var stuck = new FakeSystem { ThrowOnStart = true, DeleteAlwaysFails = true };
        stuck.Put(Src, "new");
        stuck.Put(Target, "old", "0.1.0");
        var f = SelfInstall.Execute(PlanOf(stuck), 1, null, stuck);
        Check.Equal(RelocationOutcome.Failed, f.Outcome);
        Check.NotNull(f.RollbackError);
        Check.Contains(f.RollbackError, "could not remove " + Target);
    }
    // ------------------------------------------------------------------ real disk (temp folder only)

    [UnitTest]
    public static void The_real_system_hashes_copies_and_swaps_inside_a_temp_folder()
    {
        using var dir = new TempDir();
        var sys = new SelfInstallSystem();
        string a = dir.File("a.exe"), b = dir.File("sub\\b.exe");
        File.WriteAllText(a, "hello");
        sys.CreateDirectory(Path.GetDirectoryName(b)!);
        sys.Copy(a, b);
        Check.Equal(sys.Sha256(a), sys.Sha256(b));
        Check.Equal(Convert.ToHexString(SHA256.HashData("hello"u8.ToArray())), sys.Sha256(a));
        string c = dir.File("c.exe");
        sys.Move(b, c, overwrite: false);
        Check.False(sys.FileExists(b));
        Check.True(sys.FileExists(c));
        File.SetAttributes(c, FileAttributes.ReadOnly);
        Check.True(sys.IsReadOnly(c));
        File.SetAttributes(c, FileAttributes.Normal);
        sys.StripZoneIdentifier(c);   // none there: no error
        string same = dir.File("sub\\..\\c.exe");
        Check.True(sys.SameFile(c, same), "one file, two spellings");
        Check.False(sys.SameFile(a, c), "two files with the same bytes");
        Check.False(sys.SameFile(a, dir.File("missing.exe")));
        sys.ClearReadOnly(c);
        File.SetAttributes(c, FileAttributes.ReadOnly);
        sys.ClearReadOnly(c);
        Check.False(sys.IsReadOnly(c));
        Check.False(sys.LooksLikeClink(c), "a text file is not Clink");
        _ = sys.DriveTypeOf(Path.GetPathRoot(dir.Path)!);   // read only: just must not throw
        Check.True(sys.ProgramFilesFolders().Count > 0);
        sys.Delete(c);
        Check.False(sys.FileExists(c));
    }
}
