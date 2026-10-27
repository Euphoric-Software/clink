using System.IO;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The uninstall entry and the --uninstall logic, over fakes: nothing here touches the real Uninstall key, the Run key, shortcuts or files outside temp folders.</summary>
internal static class UninstallTests
{
    private const string Folder = @"C:\Fake\Programs\Clink";
    private const string Exe = Folder + @"\Clink.exe";
    private const string Temp = @"C:\Fake\Temp\";

    private sealed class FakeRegistry : IUninstallRegistry
    {
        public Dictionary<string, object>? Values;
        public int Deletes;
        public bool ThrowOnDelete;
        public IReadOnlyDictionary<string, object>? Read() => Values;
        public void Write(IReadOnlyDictionary<string, object> values)
        {
            Values ??= new Dictionary<string, object>();
            foreach (var (k, v) in values) Values[k] = v;
        }
        public bool Delete()
        {
            if (ThrowOnDelete) throw new IOException("registry is locked");
            Deletes++;
            bool had = Values != null;
            Values = null;
            return had;
        }
    }

    private sealed class FakeSystem : IUninstallSystem
    {
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase) { Folder };
        public readonly List<string> Calls = new();
        public readonly List<string> Informed = new();
        public readonly List<string> Deleted = new();
        public readonly List<string> Recycled = new();
        public readonly List<(string Exe, string[] Args)> Started = new();
        public readonly HashSet<string> Links = new(StringComparer.OrdinalIgnoreCase);   // "folder|exe" pairs that exist
        public string? RunValue;
        public bool Running, Answer, RecycleFails, Mutex, CopyFails, StartFails, ThrowOnConfirm, LinkFails;
        public int Confirms, FileExistsCalls;
        public string Cwd = @"C:\Fake\Programs\Clink";
        public int? WaitedFor;
        public readonly Dictionary<string, string> Hashes = new(StringComparer.OrdinalIgnoreCase);        // path -> hash; default "same"
        public readonly Dictionary<string, string> CopyTargets = new(StringComparer.OrdinalIgnoreCase);   // copy exe -> recorded target

        public bool TryTakeSingleInstance() { Calls.Add("mutex"); if (Running) return false; Mutex = true; return true; }
        public void ReleaseSingleInstance() { Calls.Add("release"); Mutex = false; }
        public bool Confirm(string text)
        {
            Confirms++; Calls.Add("confirm"); Check.Equal(Uninstaller.ConfirmText, text);
            if (ThrowOnConfirm) throw new InvalidOperationException("boom");
            return Answer;
        }
        public void Inform(string text) { Informed.Add(text); Calls.Add("inform"); }
        public bool FileExists(string path) { FileExistsCalls++; return Files.Contains(path); }
        public bool Remote;   // IsFixedDrive answers false
        public string FileHash(string path) { Calls.Add("hash"); return Hashes.TryGetValue(path, out var h) ? h : "same"; }
        public bool IsFixedDrive(string path) => !Remote;
        public string? ReadCopyTarget(string copyExe) => CopyTargets.TryGetValue(copyExe, out var t) ? t : null;
        public void DeleteFile(string path) { Files.Remove(path); Deleted.Add(path); Calls.Add("delete " + path); }
        public bool DeleteFolderIfEmpty(string folder)
        {
            string prefix = folder.TrimEnd('\\') + "\\";
            if (Files.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
            Calls.Add("rmdir " + folder);
            return Folders.Remove(folder);
        }
        public bool RemoveLinkTo(string folder, string exePath)
        {
            Calls.Add("link " + folder);
            if (LinkFails) throw new IOException("link is locked");
            return Links.Remove(folder + "|" + exePath);
        }
        public string? ReadRunValue() => RunValue;
        public void DeleteRunValue() { RunValue = null; Calls.Add("run-delete"); }
        public string TempPath => Temp;
        public void SetCurrentDirectory(string folder) { Cwd = folder; Calls.Add("cd " + folder); }
        public void Recycle(string path)
        {
            Calls.Add("recycle " + path + " (cwd " + Cwd + ")");
            if (RecycleFails) throw new IOException("in use");
            Recycled.Add(path);
            Files.Remove(path);
        }
        public string CopySelfToTemp(string exePath)
        {
            Calls.Add("copy-self");
            if (CopyFails) throw new IOException("disk full");
            string copy = Temp + Uninstaller.CopyFolderPrefix + "abc\\Clink.exe";
            CopyTargets[copy] = exePath;
            return copy;
        }
        public void StartDetached(string exePath, IReadOnlyList<string> args)
        {
            Calls.Add("start");
            if (StartFails) throw new IOException("cannot start");
            Started.Add((exePath, args.ToArray()));
        }
        public bool WaitForProcessExit(int pid, int milliseconds) { WaitedFor = pid; Calls.Add("wait"); return true; }
    }

    private static UninstallContext Context(FakeSystem sys, FakeRegistry reg, bool elevated = false, bool managed = false, string? dev = null,
        string exe = Exe, bool singleFile = true, string[]? own = null, Func<string, bool>? managedCheck = null, params string[] linkFolders) =>
        new(exe, () => elevated, managedCheck ?? (_ => managed), sys, reg, linkFolders, dev, 4242, own ?? new[] { Folder }, () => singleFile);

    private const string TempCopy = Temp + "clink-uninstall-abc\\Clink.exe";
    private static readonly string[] FinishArgs = { "--uninstall", "--uninstall-finish", Exe, "--after-pid", "4242" };

    /// <summary>A system set up for the finishing step: the installed exe, and the copy's record naming it.</summary>
    private static FakeSystem ForFinish()
    {
        var sys = Installed();
        sys.Files.Remove(Exe + ".bak"); sys.Files.Remove(Exe + ".new"); sys.Files.Remove(Exe + ".failed");
        sys.CopyTargets[TempCopy] = Exe;
        return sys;
    }

    private static FakeSystem Installed()
    {
        var sys = new FakeSystem { Answer = true };
        sys.Files.Add(Exe);
        sys.Files.Add(Exe + ".bak"); sys.Files.Add(Exe + ".new"); sys.Files.Add(Exe + ".failed");
        sys.RunValue = "\"" + Exe + "\" --minimized";
        sys.Links.Add(@"C:\Fake\StartMenu|" + Exe);
        return sys;
    }

    // ------------------------------------------------------------------ the entry

    [UnitTest]
    public static void The_uninstall_entry_has_the_listed_values()
    {
        var v = UninstallEntry.Build(Exe, "0.4.2", 5 * 1024 * 1024 + 1);
        Check.Equal("Clink", (string)v["DisplayName"]);
        Check.Equal("0.4.2", (string)v["DisplayVersion"]);
        Check.Equal("Euphoric Software", (string)v["Publisher"]);
        Check.Equal(Exe + ",0", (string)v["DisplayIcon"]);
        Check.Equal(Folder, (string)v["InstallLocation"]);
        Check.Equal("\"" + Exe + "\" --uninstall", (string)v["UninstallString"]);
        Check.Equal(1, (int)v["NoModify"]);
        Check.Equal(1, (int)v["NoRepair"]);
        Check.Equal(5121, (int)v["EstimatedSize"], "KB, rounded up");
        Check.True(((string)v["URLInfoAbout"]).StartsWith("https://"));
        Check.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\EuphoricSoftware.Clink", UninstallEntry.KeyPath);
    }

    [UnitTest]
    public static void The_entry_is_written_only_for_our_own_unmanaged_folders_and_never_elevated()
    {
        string[] own = { Folder, @"C:\Fake\Documents\Clink" };
        var reg = new FakeRegistry();
        Check.False(UninstallEntry.Write(Exe, "1.0.0", 10, reg, elevated: true, own, _ => false), "elevated");
        Check.False(UninstallEntry.Write(Exe, "1.0.0", 10, reg, false, own, _ => true), "installer-managed");
        Check.False(UninstallEntry.Write(@"C:\Fake\Downloads\Clink.exe", "1.0.0", 10, reg, false, own, _ => false), "a folder that is not an install choice");
        Check.Null(reg.Values, "nothing written so far");
        Check.True(UninstallEntry.Write(Exe, "1.0.0", 10, reg, false, own, _ => false), "Programs\\Clink");
        Check.Equal(Folder, (string)reg.Values!["InstallLocation"]);
        var docs = new FakeRegistry();
        Check.True(UninstallEntry.Write(@"C:\Fake\Documents\Clink\Clink.exe", "1.0.0", 10, docs, false, own, _ => false), "Documents\\Clink");
    }

    [UnitTest]
    public static void A_failing_registry_does_not_throw_out_of_Write()
    {
        var reg = new ThrowingRegistry();
        Check.False(UninstallEntry.Write(Exe, "1.0.0", 10, reg, false, new[] { Folder }, _ => false));
    }

    private sealed class ThrowingRegistry : IUninstallRegistry
    {
        public IReadOnlyDictionary<string, object>? Read() => throw new IOException("no");
        public void Write(IReadOnlyDictionary<string, object> values) => throw new IOException("no");
        public bool Delete() => throw new IOException("no");
    }

    [UnitTest]
    public static void RefreshIfOurs_updates_the_version_only_when_the_install_location_is_this_folder()
    {
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1.0.0", 1024) };
        Check.True(UninstallEntry.RefreshIfOurs(Exe, "1.1.0", 1024, reg, false));
        Check.Equal("1.1.0", (string)reg.Values!["DisplayVersion"]);
        Check.Equal("\"" + Exe + "\" --uninstall", (string)reg.Values["UninstallString"], "the rest is kept");
        Check.False(UninstallEntry.RefreshIfOurs(Exe, "1.1.0", 1024, reg, false), "already current");

        Check.False(UninstallEntry.RefreshIfOurs(@"C:\Fake\Downloads\Clink.exe", "9.9.9", 1024, reg, false), "another folder's copy");
        Check.Equal("1.1.0", (string)reg.Values["DisplayVersion"]);
        Check.False(UninstallEntry.RefreshIfOurs(Exe, "2.0.0", 1024, reg, elevated: true), "elevated");
        Check.Equal("1.1.0", (string)reg.Values["DisplayVersion"]);
        Check.False(UninstallEntry.RefreshIfOurs(Exe, "2.0.0", 1024, new FakeRegistry(), false), "no entry: none is created");
    }

    [UnitTest]
    public static void The_dev_entry_is_a_json_file_in_the_dev_folder()
    {
        using var dir = new TempDir();
        var reg = new JsonFileUninstallRegistry(dir.Path);
        Check.Null(reg.Read());
        reg.Write(UninstallEntry.Build(Exe, "1.0.0", 2048));
        Check.True(File.Exists(dir.File(JsonFileUninstallRegistry.FileName)));
        var back = reg.Read()!;
        Check.Equal("1.0.0", (string)back["DisplayVersion"]);
        Check.Equal(2, (int)back["EstimatedSize"]);
        reg.Write(new Dictionary<string, object> { ["DisplayVersion"] = "1.2.0" });
        Check.Equal("1.2.0", (string)reg.Read()!["DisplayVersion"]);
        Check.Equal("Clink", (string)reg.Read()!["DisplayName"], "merged, not replaced");
        Check.True(reg.Delete());
        Check.False(reg.Delete());
        Check.Null(reg.Read());
    }

    [UnitTest]
    public static void The_key_name_matches_the_installer_script_when_it_is_in_the_checkout()
    {
        string? script = FindInstallerScript();
        if (script == null) return;   // dev: the installer branch's script is not here yet (pass, not skip)
        string text = File.ReadAllText(script);
        var line = System.Text.RegularExpressions.Regex.Match(text,
            @"^[^;\r\n]*Subkey:\s*""?" + System.Text.RegularExpressions.Regex.Escape(UninstallEntry.KeyPath) + @"""?[^\r\n]*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Check.True(line.Success, "a [Registry] line for our key");
        var flags = System.Text.RegularExpressions.Regex.Match(line.Value, @"Flags:\s*([^;""\r\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Check.True(flags.Success, "that line has Flags");
        var tokens = flags.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        Check.True(tokens.Contains("deletekey", StringComparer.OrdinalIgnoreCase), "Flags contain deletekey (install-time cleanup)");
        Check.False(tokens.Contains("uninsdeletekey", StringComparer.OrdinalIgnoreCase), "not uninsdeletekey");
    }

    [UnitTest]
    public static void RefreshIfOurs_matches_the_install_location_however_it_is_spelled()
    {
        foreach (string spelled in new[] { Folder.ToUpperInvariant(), Folder + "\\", Folder.Replace('\\', '/'), "\"" + Folder + "\"" })
        {
            var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1.0.0", 1024) };
            reg.Values["InstallLocation"] = spelled;
            Check.True(UninstallEntry.RefreshIfOurs(Exe, "1.1.0", 1024, reg, false), "location spelled " + spelled);
            Check.Equal("1.1.0", (string)reg.Values["DisplayVersion"]);
        }
        var wrongType = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1.0.0", 1024) };
        wrongType.Values["InstallLocation"] = 42;
        Check.False(UninstallEntry.RefreshIfOurs(Exe, "1.1.0", 1024, wrongType, false), "a non-string InstallLocation");
        Check.Equal("1.0.0", (string)wrongType.Values["DisplayVersion"]);
        Check.False(UninstallEntry.DeleteIfOurs(Folder, wrongType), "a non-string InstallLocation is not ours either");
        Check.NotNull(wrongType.Values);
    }

    [UnitTest]
    public static void ShouldWrite_is_false_below_the_install_folder()
    {
        Check.False(UninstallEntry.ShouldWrite(Folder + @"\sub\Clink.exe", new[] { Folder }, false, _ => false));
        Check.True(UninstallEntry.ShouldWrite(Exe, new[] { Folder }, false, _ => false));
    }

    private static string? FindInstallerScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "installer", "Clink.iss");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "Clink", "Clink.csproj"))) return candidate;
        }
        return null;
    }

    // ------------------------------------------------------------------ refusals

    [UnitTest]
    public static void Elevated_and_installer_managed_copies_are_refused_before_anything_else()
    {
        foreach (bool elevated in new[] { true, false })
        {
            var sys = Installed();
            var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
            var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg, elevated: elevated, managed: !elevated, linkFolders: @"C:\Fake\StartMenu"));
            Check.Equal(UninstallOutcome.Refused, r.Outcome);
            Check.Equal(Uninstaller.ExitRefused, r.ExitCode);
            Check.Equal(new[] { "Use Settings > Apps to remove Clink." }, sys.Informed.ToArray());
            Check.Equal(0, sys.Confirms, "no question");
            Check.False(sys.Calls.Contains("mutex"), "the mutex is not even taken");
            Check.Equal(0, sys.Deleted.Count + sys.Recycled.Count);
            Check.NotNull(reg.Values, "the entry stays");
            Check.NotNull(sys.RunValue);
        }
    }

    [UnitTest]
    public static void A_running_clink_stops_the_uninstall_without_a_question()
    {
        var sys = Installed();
        sys.Running = true;
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg));
        Check.Equal(UninstallOutcome.Running, r.Outcome);
        Check.Equal(Uninstaller.ExitRunning, r.ExitCode);
        Check.Equal(new[] { "Quit Clink first, then try again." }, sys.Informed.ToArray());
        Check.Equal(0, sys.Confirms);
        Check.Equal(0, sys.Deleted.Count + sys.Recycled.Count);
        Check.NotNull(reg.Values);
    }

    [UnitTest]
    public static void Saying_no_changes_nothing_and_releases_the_mutex()
    {
        var sys = Installed();
        sys.Answer = false;
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg, linkFolders: @"C:\Fake\StartMenu"));
        Check.Equal(UninstallOutcome.Declined, r.Outcome);
        Check.Equal(0, r.ExitCode);
        Check.Equal(1, sys.Confirms);
        Check.False(sys.Mutex, "mutex released");
        Check.Equal(0, sys.Deleted.Count + sys.Recycled.Count);
        Check.Equal(1, sys.Links.Count);
        Check.NotNull(reg.Values);
        Check.NotNull(sys.RunValue);
        Check.Equal(0, sys.Informed.Count);
    }

    [UnitTest]
    public static void The_confirmation_text_is_the_agreed_one()
    {
        Check.Equal("Remove Clink from this PC? Your clips, screenshots and settings stay.", Uninstaller.ConfirmText);
    }

    // ------------------------------------------------------------------ removal

    [UnitTest]
    public static void A_confirmed_uninstall_removes_the_list_and_the_exe_after_leaving_the_folder()
    {
        var sys = Installed();
        // Things that must survive: settings and clips live elsewhere, but even files beside the exe that are not ours stay.
        sys.Files.Add(@"C:\Fake\Roaming\Clink\settings.json");
        sys.Files.Add(@"C:\Fake\Videos\Clink\clip.mp4");
        sys.Files.Add(@"C:\Fake\LocalAppData\Clink\updates\Clink-9.exe");
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg, linkFolders: new[] { @"C:\Fake\StartMenu", @"C:\Fake\Desktop" }));

        Check.Equal(UninstallOutcome.Done, r.Outcome);
        Check.Equal(0, r.ExitCode);
        Check.Equal(0, r.Problems.Count);
        Check.Equal(0, sys.Links.Count, "the Start menu link went");
        Check.Null(sys.RunValue, "the Run value pointed into this folder");
        Check.Null(reg.Values, "the uninstall entry went");
        Check.Equal(new[] { Exe + ".bak", Exe + ".new", Exe + ".failed" }, sys.Deleted.ToArray());
        Check.Equal(new[] { Exe }, sys.Recycled.ToArray());
        Check.False(sys.Folders.Contains(Folder), "the empty folder went");
        Check.True(sys.Files.Contains(@"C:\Fake\Roaming\Clink\settings.json"), "settings stay");
        Check.True(sys.Files.Contains(@"C:\Fake\Videos\Clink\clip.mp4"), "clips stay");
        Check.True(sys.Files.Contains(@"C:\Fake\LocalAppData\Clink\updates\Clink-9.exe"), "the updates folder stays");
        Check.Equal(new[] { Uninstaller.DoneText }, sys.Informed.ToArray());
        Check.False(sys.Mutex, "mutex released");

        int cd = sys.Calls.FindIndex(c => c.StartsWith("cd "));
        int rec = sys.Calls.FindIndex(c => c.StartsWith("recycle "));
        Check.True(cd >= 0 && cd < rec, "the working directory moves first");
        Check.Equal("cd " + Temp, sys.Calls[cd]);
        Check.Contains(sys.Calls[rec], "(cwd " + Temp + ")");
    }

    [UnitTest]
    public static void The_folder_stays_when_something_else_is_in_it()
    {
        var sys = Installed();
        sys.Files.Add(Folder + @"\notes.txt");
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, new FakeRegistry()));
        Check.Equal(UninstallOutcome.Done, r.Outcome);
        Check.True(sys.Folders.Contains(Folder));
        Check.True(sys.Files.Contains(Folder + @"\notes.txt"));
    }

    [UnitTest]
    public static void The_Run_value_is_removed_only_when_it_points_into_this_folder()
    {
        foreach (var (value, removed) in new[]
        {
            ("\"" + Exe + "\" --minimized", true),
            (Exe + " --minimized", true),
            (Exe, true),
            ("\"" + Folder.ToUpperInvariant() + "\\CLINK.EXE\" --minimized", true),
            ("\"C:\\Fake\\Downloads\\Clink.exe\" --minimized", false),
            ("\"C:\\Fake\\Programs\\Clink2\\Clink.exe\"", false),
            ("\"" + Folder + "\\sub\\Clink.exe\"", false),
            ("", false),
            ("\"unterminated", false),
            ("Clink.exe --minimized", false),
            ("\"" + Folder + "\\Other.exe\" --minimized", false),
            (Folder + "\\Other.exe", false),
            ("\"" + Folder + "\\Clink.exe\" /min", true),     // quoted: only the exe counts
            (Folder + "\\Clink.exe /min", false),             // unquoted: cut at " --" only, so this is not a Clink.exe path
            ("%LOCALAPPDATA%\\Programs\\Clink\\Clink.exe", false),
            ("\"%LOCALAPPDATA%\\Programs\\Clink\\Clink.exe\" --minimized", false),
            ("  \"" + Exe + "\" --minimized  \t", true),
            (Exe + "   ", true),
        })
        {
            var sys = Installed();
            sys.RunValue = value;
            Uninstaller.Run(new[] { "--uninstall" }, Context(sys, new FakeRegistry()));
            Check.Equal(removed, sys.RunValue == null, "Run value: " + value);
            if (!removed) Check.Equal(value, sys.RunValue, "untouched: " + value);
        }
    }

    [UnitTest]
    public static void A_dev_uninstall_never_touches_the_Run_value()
    {
        var sys = Installed();
        string before = sys.RunValue!;
        Uninstaller.Run(new[] { "--uninstall" }, Context(sys, new FakeRegistry(), dev: @"C:\Fake\Programs"));
        Check.Equal(before, sys.RunValue);
        Check.False(sys.Calls.Contains("run-delete"));
    }

    [UnitTest]
    public static void A_failing_step_does_not_stop_the_others()
    {
        var sys = Installed();
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1), ThrowOnDelete = true };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg, linkFolders: @"C:\Fake\StartMenu"));
        Check.Equal(UninstallOutcome.Done, r.Outcome);
        Check.Equal(1, r.Problems.Count);
        Check.Contains(r.Problems[0], "uninstall entry");
        Check.Equal(0, sys.Links.Count);
        Check.Null(sys.RunValue);
        Check.Equal(new[] { Exe }, sys.Recycled.ToArray());
        Check.Contains(sys.Informed[0], "could not be removed");
    }

    [UnitTest]
    public static void A_running_exe_is_finished_by_a_copy_in_temp()
    {
        var sys = Installed();
        sys.RecycleFails = true;
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg));
        Check.Equal(UninstallOutcome.Deferred, r.Outcome);
        Check.Equal(0, r.ExitCode);
        Check.Equal(1, sys.Started.Count);
        Check.Equal(TempCopy, sys.Started[0].Exe);
        Check.Equal(FinishArgs, sys.Started[0].Args);
        Check.True(sys.Files.Contains(Exe), "left for the copy");
        Check.True(sys.Folders.Contains(Folder));
        Check.NotNull(reg.Values, "the entry stays until the copy has recycled the exe");
        Check.Equal(new[] { Uninstaller.DeferredText }, sys.Informed.ToArray());
    }

    [UnitTest]
    public static void The_user_is_told_before_the_copy_is_started_and_the_start_is_the_last_step()
    {
        var sys = Installed();
        sys.RecycleFails = true;
        Uninstaller.Run(new[] { "--uninstall" }, Context(sys, new FakeRegistry()));
        int inform = sys.Calls.IndexOf("inform"), start = sys.Calls.IndexOf("start");
        Check.True(inform >= 0 && inform < start, "inform, then start");
        Check.Equal("start", sys.Calls[^2], "only the mutex release comes after the start");
        Check.Equal("release", sys.Calls[^1]);
    }

    [UnitTest]
    public static void A_failing_copy_or_start_keeps_the_entry_and_tells_the_user_what_to_remove()
    {
        foreach (bool copyFails in new[] { true, false })
        {
            var sys = Installed();
            sys.RecycleFails = true;
            sys.CopyFails = copyFails;
            sys.StartFails = !copyFails;
            var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
            var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg));
            Check.Equal(UninstallOutcome.Deferred, r.Outcome);
            Check.NotNull(reg.Values, "the entry stays");
            Check.Equal(1, r.Problems.Count);
            Check.Contains(r.Problems[0], "exe");
            Check.Contains(sys.Informed[^1], Uninstaller.ManualText(Folder));
            Check.True(sys.Files.Contains(Exe));
            Check.True(sys.Folders.Contains(Folder));
        }
    }

    [UnitTest]
    public static void A_framework_dependent_exe_is_not_copied_and_the_user_removes_the_folder_by_hand()
    {
        var sys = Installed();
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg, singleFile: false));
        Check.Equal(UninstallOutcome.Deferred, r.Outcome);
        Check.False(sys.Calls.Contains("copy-self"));
        Check.False(sys.Calls.Contains("start"));
        Check.Equal(0, sys.Recycled.Count);
        Check.NotNull(reg.Values, "entry kept");
        Check.Equal(new[] { Uninstaller.ManualText(Folder) }, sys.Informed.ToArray());
    }

    [UnitTest]
    public static void An_entry_for_another_folder_is_kept()
    {
        var sys = Installed();
        var reg = new FakeRegistry { Values = UninstallEntry.Build(@"C:\Fake\Documents\Clink\Clink.exe", "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg));
        Check.Equal(UninstallOutcome.Done, r.Outcome);
        Check.NotNull(reg.Values, "not ours");
        Check.Equal(0, reg.Deletes);
    }

    [UnitTest]
    public static void A_copy_run_from_a_foreign_folder_leaves_the_folder_alone()
    {
        string downloads = @"C:\Fake\Downloads";
        string dExe = downloads + @"\Clink.exe";
        var sys = Installed();
        sys.Files.Add(dExe);
        sys.Folders.Add(downloads);
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };   // the entry names Programs\Clink, not Downloads
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg, exe: dExe));
        Check.Equal(UninstallOutcome.Done, r.Outcome);
        Check.Equal(new[] { dExe }, sys.Recycled.ToArray());
        Check.False(sys.Calls.Contains("rmdir " + downloads), "Downloads stays");
        Check.True(sys.Folders.Contains(downloads));
        Check.NotNull(reg.Values, "the entry is for another folder");

        // The entry's InstallLocation makes a folder ours; a drive root never is.
        var viaEntry = Installed();
        viaEntry.Files.Add(dExe);
        viaEntry.Folders.Add(downloads);
        Uninstaller.Run(new[] { "--uninstall" }, Context(viaEntry, new FakeRegistry { Values = UninstallEntry.Build(dExe, "1", 1) }, exe: dExe));
        Check.True(viaEntry.Calls.Contains("rmdir " + downloads), "named by the entry");

        var root = Installed();
        root.Files.Add(@"C:\Clink.exe");
        root.Folders.Add(@"C:\");
        Uninstaller.Run(new[] { "--uninstall" }, Context(root, new FakeRegistry { Values = UninstallEntry.Build(@"C:\Clink.exe", "1", 1) }, exe: @"C:\Clink.exe", own: new[] { @"C:\" }));
        Check.False(root.Calls.Any(c => c.StartsWith("rmdir ")), "never a drive root");
    }

    [UnitTest]
    public static void An_unexpected_error_has_its_own_exit_code()
    {
        var sys = Installed();
        sys.ThrowOnConfirm = true;
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, new FakeRegistry()));
        Check.Equal(UninstallOutcome.Failed, r.Outcome);
        Check.Equal(Uninstaller.ExitFailed, r.ExitCode);
        Check.NotEqual(Uninstaller.ExitBadArguments, r.ExitCode);
        Check.False(sys.Mutex, "mutex released");
    }

    [UnitTest]
    public static void A_failed_link_removal_is_reported()
    {
        var sys = Installed();
        sys.LinkFails = true;
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, new FakeRegistry(), linkFolders: @"C:\Fake\StartMenu"));
        Check.Equal(1, r.Problems.Count);
        Check.Contains(r.Problems[0], "shortcut");
    }

    [UnitTest]
    public static void The_temp_copy_recycles_the_exe_then_removes_the_entry_and_the_empty_folder()
    {
        var sys = ForFinish();
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(FinishArgs, Context(sys, reg, exe: TempCopy));
        Check.Equal(UninstallOutcome.Finished, r.Outcome);
        Check.Equal(0, r.Problems.Count);
        Check.Equal((int?)4242, sys.WaitedFor);
        Check.Equal(new[] { Exe }, sys.Recycled.ToArray());
        Check.Null(reg.Values, "the entry goes after the recycle");
        Check.False(sys.Folders.Contains(Folder));
        Check.Equal(0, sys.Confirms, "already confirmed by the first process");
        Check.False(sys.Calls.Contains("mutex"));
    }

    [UnitTest]
    public static void The_finishing_step_refuses_a_bad_request_before_touching_anything()
    {
        // Elevated, installer-managed, relative, UNC and \\?\ targets, a hash mismatch, a different recorded target.
        void Refused(string label, FakeSystem sys, string target, bool elevated = false, bool managed = false, bool fileSystemUntouched = false)
        {
            var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
            var r = Uninstaller.Run(new[] { "--uninstall", "--uninstall-finish", target, "--after-pid", "4242" }, Context(sys, reg, elevated: elevated, managed: managed, exe: TempCopy));
            Check.Equal(UninstallOutcome.BadFinish, r.Outcome, label);
            Check.Equal(0, sys.Recycled.Count, label + ": no recycle");
            Check.False(sys.Calls.Any(c => c.StartsWith("rmdir ")), label + ": no rmdir");
            Check.NotNull(reg.Values, label + ": entry kept");
            if (fileSystemUntouched) Check.Equal(0, sys.FileExistsCalls, label + ": FileExists never called");
        }
        Refused("elevated", ForFinish(), Exe, elevated: true, fileSystemUntouched: true);
        Refused("installer-managed", ForFinish(), Exe, managed: true);
        Refused("relative", ForFinish(), @"Programs\Clink\Clink.exe", fileSystemUntouched: true);
        Refused("drive-relative", ForFinish(), @"C:Clink.exe", fileSystemUntouched: true);
        Refused("UNC", ForFinish(), @"\\server\share\Clink\Clink.exe", fileSystemUntouched: true);
        Refused("long UNC", ForFinish(), @"\\?\UNC\server\share\Clink\Clink.exe", fileSystemUntouched: true);
        Refused("device path", ForFinish(), @"\\?\C:\Fake\Programs\Clink\Clink.exe", fileSystemUntouched: true);

        var mismatch = ForFinish();
        mismatch.Hashes[TempCopy] = "other bytes";
        Refused("hash mismatch", mismatch, Exe);

        var unrecorded = ForFinish();
        unrecorded.CopyTargets[TempCopy] = @"C:\Fake\Other\Clink.exe";
        unrecorded.Files.Add(@"C:\Fake\Other\Clink.exe");
        Refused("target not the recorded one", unrecorded, Exe);

        var noRecord = ForFinish();
        noRecord.CopyTargets.Clear();
        Refused("no record", noRecord, Exe);
    }

    [UnitTest]
    public static void The_finishing_step_with_a_missing_target_removes_nothing()
    {
        var sys = ForFinish();
        sys.Files.Remove(Exe);
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(FinishArgs, Context(sys, reg, exe: TempCopy));
        Check.Equal(UninstallOutcome.Finished, r.Outcome);
        Check.Equal(1, r.Problems.Count);
        Check.Equal(0, sys.Recycled.Count);
        Check.False(sys.Calls.Any(c => c.StartsWith("rmdir ")), "no rmdir");
        Check.NotNull(reg.Values);
    }

    [UnitTest]
    public static void A_failed_recycle_in_the_finishing_step_keeps_the_entry_and_the_folder()
    {
        var sys = ForFinish();
        sys.RecycleFails = true;
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(FinishArgs, Context(sys, reg, exe: TempCopy));
        Check.Equal(1, r.Problems.Count);
        Check.NotNull(reg.Values);
        Check.False(sys.Calls.Any(c => c.StartsWith("rmdir ")));
    }

    [UnitTest]
    public static void The_finishing_step_leaves_a_folder_that_is_not_ours_and_an_entry_for_another_folder()
    {
        var sys = ForFinish();
        var reg = new FakeRegistry { Values = UninstallEntry.Build(@"C:\Fake\Documents\Clink\Clink.exe", "1", 1) };
        var r = Uninstaller.Run(FinishArgs, Context(sys, reg, exe: TempCopy, own: new[] { @"C:\Fake\Other\Clink" }));
        Check.Equal(new[] { Exe }, sys.Recycled.ToArray());
        Check.NotNull(reg.Values, "entry for another folder stays");
        Check.False(sys.Calls.Any(c => c.StartsWith("rmdir ")), "folder is not an install folder");
        Check.Equal(0, r.Problems.Count);
    }

    [UnitTest]
    public static void The_finishing_step_accepts_only_Clink_exe_and_only_from_a_temp_copy()
    {
        var args = new[] { "--uninstall", "--uninstall-finish", Exe };
        var notTemp = Installed();
        Check.Equal(UninstallOutcome.BadFinish, Uninstaller.Run(args, Context(notTemp, new FakeRegistry(), exe: @"C:\Fake\Downloads\Clink.exe")).Outcome, "not a temp copy");
        Check.Equal(0, notTemp.Recycled.Count);

        var wrongName = Installed();
        wrongName.Files.Add(Folder + @"\Other.exe");
        var r = Uninstaller.Run(new[] { "--uninstall", "--uninstall-finish", Folder + @"\Other.exe" },
            Context(wrongName, new FakeRegistry(), exe: Temp + "clink-uninstall-abc\\Clink.exe"));
        Check.Equal(UninstallOutcome.BadFinish, r.Outcome, "not called Clink.exe");
        Check.Equal(0, wrongName.Recycled.Count);

        var elsewhere = Installed();
        Check.Equal(UninstallOutcome.BadFinish, Uninstaller.Run(args, Context(elsewhere, new FakeRegistry(), exe: Temp + "other-folder\\Clink.exe")).Outcome, "temp, but not a clink-uninstall folder");
        Check.Equal(0, elsewhere.Recycled.Count);
    }

    // ------------------------------------------------------------------ round 2

    private const string DevFolder = @"C:\Fake\Dev";

    [UnitTest]
    public static void A_dev_instance_refuses_an_exe_outside_the_dev_folder_and_works_inside_it()
    {
        var sys = Installed();   // the real-looking install: C:\Fake\Programs\Clink
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg, dev: DevFolder));
        Check.Equal(UninstallOutcome.Refused, r.Outcome);
        Check.Equal(0, sys.Recycled.Count + sys.Deleted.Count);
        Check.False(sys.Calls.Any(c => c.StartsWith("rmdir ")), "no rmdir");
        Check.Equal(0, sys.Informed.Count + sys.Confirms, "no message, no question");
        Check.NotNull(reg.Values);
        Check.NotNull(sys.RunValue);

        string devFolder = DevFolder + @"\Programs\Clink", devExe = devFolder + @"\Clink.exe";
        var inside = new FakeSystem { Answer = true };
        inside.Files.Add(devExe);
        inside.Folders.Add(devFolder);
        var devReg = new FakeRegistry { Values = UninstallEntry.Build(devExe, "1", 1) };
        var ok = Uninstaller.Run(new[] { "--uninstall" }, Context(inside, devReg, dev: DevFolder, exe: devExe, own: new[] { devFolder }));
        Check.Equal(UninstallOutcome.Done, ok.Outcome);
        Check.Equal(new[] { devExe }, inside.Recycled.ToArray());
        Check.True(inside.Calls.Contains("rmdir " + devFolder));
        Check.Null(devReg.Values);

        // The finishing step: a target outside the dev folder is refused too.
        var fin = ForFinish();
        var finReg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var bad = Uninstaller.Run(FinishArgs, Context(fin, finReg, dev: DevFolder, exe: TempCopy));
        Check.Equal(UninstallOutcome.BadFinish, bad.Outcome);
        Check.Equal(0, fin.Recycled.Count);
        Check.NotNull(finReg.Values);
    }

    [UnitTest]
    public static void A_dev_quiet_registry_reads_the_real_file_and_only_logs_changes()
    {
        using var dir = new TempDir();
        var json = new JsonFileUninstallRegistry(dir.Path);
        json.Write(UninstallEntry.Build(Exe, "1.0.0", 1024));
        var dry = new DryRunUninstallRegistry(json);
        Check.Equal("1.0.0", (string)dry.Read()!["DisplayVersion"]);
        dry.Write(new Dictionary<string, object> { ["DisplayVersion"] = "9.9.9" });
        Check.Equal("1.0.0", (string)json.Read()!["DisplayVersion"], "write only logged");
        dry.Delete();
        Check.True(File.Exists(dir.File(JsonFileUninstallRegistry.FileName)), "the entry file stays");

        string devDir = dir.File("dev");   // a new folder: --dev refuses a folder that holds other files
        var ctx = Uninstaller.RealContext(new[] { "--dev", devDir, "--dev-quiet" });
        Check.True(ctx.Registry is DryRunUninstallRegistry);
        new JsonFileUninstallRegistry(devDir).Write(UninstallEntry.Build(Exe, "1.0.0", 1024));
        Check.True(UninstallEntry.DeleteIfOurs(Folder, ctx.Registry), "read the real entry");
        Check.True(File.Exists(Path.Combine(devDir, JsonFileUninstallRegistry.FileName)), "a dry run keeps the entry file");
        Check.True(ctx.OwnFolders!.All(f => InstallLocation.Under(InstallLocation.Normalize(f), devDir)), "dev own folders live in the dev folder");
    }

    [UnitTest]
    public static void An_exe_on_a_remote_drive_is_left_to_the_user_without_a_temp_copy()
    {
        var sys = Installed();
        sys.RecycleFails = true;
        sys.Remote = true;
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(new[] { "--uninstall" }, Context(sys, reg));
        Check.Equal(UninstallOutcome.Deferred, r.Outcome);
        Check.False(sys.Calls.Contains("copy-self"));
        Check.Equal(0, sys.Started.Count);
        Check.NotNull(reg.Values, "entry kept");
        Check.Equal(new[] { Uninstaller.ManualText(Folder) }, sys.Informed.ToArray());

        var unc = Installed();
        unc.RecycleFails = true;
        string uncExe = @"\\server\share\Clink\Clink.exe";
        unc.Files.Add(uncExe);
        var r2 = Uninstaller.Run(new[] { "--uninstall" }, Context(unc, new FakeRegistry(), exe: uncExe));
        Check.Equal(UninstallOutcome.Deferred, r2.Outcome);
        Check.Equal(0, unc.Started.Count);
        Check.False(unc.Calls.Contains("copy-self"));
        Check.Contains(unc.Informed[^1], "by hand");
    }

    [UnitTest]
    public static void The_finishing_step_refuses_a_remote_drive_before_the_file_system_and_informs_after_a_failed_recycle()
    {
        var remote = ForFinish();
        remote.Remote = true;
        var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
        var r = Uninstaller.Run(FinishArgs, Context(remote, reg, exe: TempCopy));
        Check.Equal(UninstallOutcome.BadFinish, r.Outcome);
        Check.Equal(0, remote.FileExistsCalls, "FileExists never called");
        Check.Equal(0, remote.Informed.Count, "a malformed request is not announced");

        var fails = ForFinish();
        fails.RecycleFails = true;
        var r2 = Uninstaller.Run(FinishArgs, Context(fails, new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) }, exe: TempCopy));
        Check.Equal(UninstallOutcome.Finished, r2.Outcome);
        Check.Equal(new[] { Uninstaller.ManualText(Folder) }, fails.Informed.ToArray());
    }

    [UnitTest]
    public static void The_finishing_step_checks_in_the_agreed_order()
    {
        int installerChecks = 0;
        var mismatch = ForFinish();
        mismatch.CopyTargets[TempCopy] = @"C:\Fake\Other\Clink.exe";
        var r = Uninstaller.Run(FinishArgs, Context(mismatch, new FakeRegistry(), exe: TempCopy, managedCheck: _ => { installerChecks++; return false; }));
        Check.Equal(UninstallOutcome.BadFinish, r.Outcome);
        Check.Equal(0, installerChecks, "a record mismatch never reaches the installer check");

        var sys = ForFinish();
        var ok = Uninstaller.Run(FinishArgs, Context(sys, new FakeRegistry(), exe: TempCopy, managedCheck: _ => { installerChecks++; return false; }));
        Check.Equal(UninstallOutcome.Finished, ok.Outcome);
        Check.Equal(1, installerChecks);
        int wait = sys.Calls.IndexOf("wait"), hash = sys.Calls.IndexOf("hash"), rec = sys.Calls.FindIndex(c => c.StartsWith("recycle "));
        Check.True(wait >= 0 && wait < hash && hash < rec, "wait, then hash, then recycle");
    }

    [UnitTest]
    public static void The_finish_switch_without_a_value_is_never_a_normal_uninstall()
    {
        foreach (var args in new[]
        {
            new[] { "--uninstall", "--uninstall-finish" },
            new[] { "--uninstall", "--uninstall-finish", "--after-pid", "4242" },
            new[] { "--uninstall", "--uninstall-finish", "--dev" },
        })
        {
            var sys = Installed();
            var reg = new FakeRegistry { Values = UninstallEntry.Build(Exe, "1", 1) };
            var r = Uninstaller.Run(args, Context(sys, reg));
            Check.Equal(UninstallOutcome.BadFinish, r.Outcome, string.Join(" ", args));
            Check.Equal(Uninstaller.ExitBadArguments, r.ExitCode);
            Check.Equal(0, sys.Confirms);
            Check.False(sys.Calls.Contains("mutex"));
            Check.Equal(0, sys.Recycled.Count + sys.Deleted.Count);
            Check.NotNull(reg.Values);
        }
    }

    [UnitTest]
    public static void The_real_copy_is_create_new_with_an_owner_only_folder()
    {
        using var dir = new TempDir();
        string exe = dir.File("src.exe");
        File.WriteAllText(exe, "bytes");
        string existing = dir.File("clink-uninstall-exists");
        Directory.CreateDirectory(existing);
        Check.Throws<IOException>(() => UninstallSystem.CopySelfTo(exe, existing));
        Check.False(File.Exists(Path.Combine(existing, "Clink.exe")), "nothing copied into an existing folder");

        string fresh = dir.File("clink-uninstall-fresh");
        string copy = UninstallSystem.CopySelfTo(exe, fresh);
        Check.Equal("bytes", File.ReadAllText(copy));
        Check.Equal(InstallLocation.Normalize(exe), new UninstallSystem().ReadCopyTarget(copy));
        Check.True(new DirectoryInfo(fresh).GetAccessControl().AreAccessRulesProtected, "does not inherit from %TEMP%");
    }

    [UnitTest]
    public static void RunValuePointsInto_reads_quoted_and_unquoted_values()
    {
        Check.True(Uninstaller.RunValuePointsInto("\"C:\\a b\\Clink.exe\" --minimized", @"C:\a b"));
        Check.True(Uninstaller.RunValuePointsInto(@"C:\a\Clink.exe --minimized", @"C:\a\"));
        Check.False(Uninstaller.RunValuePointsInto(null, @"C:\a"));
        Check.False(Uninstaller.RunValuePointsInto(@"C:\ab\Clink.exe", @"C:\a"), "a sibling with the same start");
        Check.False(Uninstaller.RunValuePointsInto(@"C:\a\Other.exe", @"C:\a"), "another program in the folder");
    }

    // ------------------------------------------------------------------ the real world (only fakes and temp folders)

    [UnitTest]
    public static void The_real_context_uses_the_registered_providers()
    {
        var elevated = Uninstaller.IsElevatedProvider;
        var managed = Uninstaller.IsInstallerManagedProvider;
        Func<bool> myElevated = () => false;
        Func<string, bool> myManaged = _ => false;
        try
        {
            Uninstaller.IsElevatedProvider = myElevated;
            Uninstaller.IsInstallerManagedProvider = myManaged;
            var ctx = Uninstaller.RealContext(Array.Empty<string>());
            Check.True(ReferenceEquals(myElevated, ctx.IsElevated), "IsElevatedProvider");
            Check.True(ReferenceEquals(myManaged, ctx.IsInstallerManaged), "IsInstallerManagedProvider");
            Check.True(ctx.System is UninstallSystem);
            Check.True(ctx.Registry is RegistryUninstallRegistry);
            Check.Null(ctx.DevFolder);
        }
        finally { Uninstaller.IsElevatedProvider = elevated; Uninstaller.IsInstallerManagedProvider = managed; }
    }

    [UnitTest]
    public static void The_real_context_honours_the_dev_switches_without_side_effects()
    {
        using var dir = new TempDir();
        var ctx = Uninstaller.RealContext(new[] { "--dev", dir.Path });
        Check.True(ctx.Registry is JsonFileUninstallRegistry, "the JSON registry, never HKCU");
        Check.Equal(dir.Path, ctx.DevFolder);
        Check.True(ctx.System is UninstallSystem, "plain dev: the real system");
        Check.True(ctx.LinkFolders.All(f => InstallLocation.Under(InstallLocation.Normalize(f), dir.Path)), "link folders inside the dev folder");
        Check.Equal(new[] { "--dev", dir.Path }, ctx.DevSwitches!.ToArray());
        Check.False(Directory.EnumerateFileSystemEntries(dir.Path).Any(), "construction creates nothing");

        var quiet = Uninstaller.RealContext(new[] { "--dev", dir.Path, "--dev-quiet" });
        Check.True(quiet.System is DryRunUninstallSystem, "dev-quiet only logs");
        Check.Equal(new[] { "--dev", dir.Path, "--dev-quiet" }, quiet.DevSwitches!.ToArray());
        Check.False(Directory.EnumerateFileSystemEntries(dir.Path).Any());

        Check.Throws<ArgumentException>(() => Uninstaller.RealContext(new[] { "--dev-quiet" }));
    }

    [UnitTest]
    public static void The_mutex_name_is_the_single_instance_name_plus_the_dev_suffix()
    {
        Check.Equal(AppController.MutexName, Uninstaller.MutexBase + AppController.InstanceSuffix);
    }

    [UnitTest]
    public static void A_dry_run_system_changes_nothing()
    {
        using var dir = new TempDir();
        string file = dir.File("Clink.exe");
        File.WriteAllText(file, "x");
        var dry = new DryRunUninstallSystem(new UninstallSystem());
        Check.True(dry.Confirm("?"));
        dry.Inform("hi");
        dry.DeleteFile(file);
        dry.Recycle(file);
        Check.False(dry.DeleteFolderIfEmpty(dir.Path));
        dry.StartDetached(file, new[] { "--x" });
        Check.True(File.Exists(file), "still there");
        Check.True(dry.FileExists(file));
    }

    [UnitTest]
    public static void The_real_system_deletes_a_read_only_file_inside_a_temp_folder()
    {
        using var dir = new TempDir();
        string file = dir.File("Clink.exe.bak");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        new UninstallSystem().DeleteFile(file);
        Check.False(File.Exists(file));
    }

    [UnitTest]
    public static void The_real_system_removes_only_an_empty_folder()
    {
        using var dir = new TempDir();
        var sys = new UninstallSystem();
        string empty = dir.File("empty");
        Directory.CreateDirectory(empty);
        Check.True(sys.DeleteFolderIfEmpty(empty));
        Check.False(Directory.Exists(empty));
        Check.False(sys.DeleteFolderIfEmpty(empty), "missing folder");

        string withSub = dir.File("withsub");
        Directory.CreateDirectory(Path.Combine(withSub, "sub"));
        Check.False(sys.DeleteFolderIfEmpty(withSub), "a subfolder counts");
        Check.True(Directory.Exists(Path.Combine(withSub, "sub")));

        string withFile = dir.File("withfile");
        Directory.CreateDirectory(withFile);
        File.WriteAllText(Path.Combine(withFile, "a.txt"), "x");
        Check.False(sys.DeleteFolderIfEmpty(withFile));
        Check.True(File.Exists(Path.Combine(withFile, "a.txt")));
    }

    [UnitTest]
    public static void The_real_system_hashes_files_and_reads_the_copy_record()
    {
        using var dir = new TempDir();
        var sys = new UninstallSystem();
        string a = dir.File("a.bin"), b = dir.File("b.bin"), c = dir.File("c.bin");
        File.WriteAllText(a, "same"); File.WriteAllText(b, "same"); File.WriteAllText(c, "different");
        Check.Equal(sys.FileHash(a), sys.FileHash(b));
        Check.NotEqual(sys.FileHash(a), sys.FileHash(c));
        Check.Equal(64, sys.FileHash(a).Length);
        Check.Null(sys.ReadCopyTarget(a), "no record");
        File.WriteAllText(dir.File(Uninstaller.CopyRecordName), Exe + "\r\n");
        Check.Equal(Exe, sys.ReadCopyTarget(a));
    }

    [UnitTest]
    public static void The_default_installer_check_refuses_when_it_cannot_look()
    {
        using var dir = new TempDir();
        Check.False(Uninstaller.DefaultIsInstallerManaged(dir.Path), "an empty folder");
        File.WriteAllText(dir.File("unins000.exe"), "x");
        Check.True(Uninstaller.DefaultIsInstallerManaged(dir.Path), "an unins*.exe");
        Check.True(Uninstaller.DefaultIsInstallerManaged(dir.File("missing")), "cannot enumerate: refuse");
    }
}
