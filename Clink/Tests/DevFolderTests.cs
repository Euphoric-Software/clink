using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// A junction (<c>mklink /J</c>, which needs no privilege) for the tests that check where a folder really is. A test that cannot
/// make one says so in the log and on the console and passes without checking (<see cref="Skipped"/>).
/// </summary>
internal static class TestJunction
{
    /// <summary>Makes <paramref name="link"/> a junction to <paramref name="target"/>. False, and why, when it cannot be done here.</summary>
    public static bool TryMake(string link, string target, out string why)
    {
        why = "";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } why = "mklink did not finish"; return false; }
            if (p.ExitCode == 0 && Resolves(link, target)) return true;
            // mklink (cmd.exe) cannot do paths past MAX_PATH where LongPathsEnabled is off, and can leave a link that points nowhere:
            // remove what it left and set the reparse point ourselves, on \\?\ paths.
            string mklinkSaid = $"mklink exited with {p.ExitCode}: {output.Trim()}";
            Remove(link);
            if (TryMakeDirect(link, target, out string direct) && Resolves(link, target)) return true;
            why = $"{mklinkSaid}; direct: {direct}";
            Remove(link);
            return false;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    /// <summary>The link is a junction whose target is <paramref name="target"/> and that opens (a junction that points nowhere is not one).</summary>
    private static bool Resolves(string link, string target)
    {
        try
        {
            var info = new DirectoryInfo(link);
            var resolved = info.ResolveLinkTarget(returnFinalTarget: false);
            if (resolved == null) return false;
            string a = Path.GetFullPath(resolved.FullName).TrimEnd('\\', '/'), b = Path.GetFullPath(target).TrimEnd('\\', '/');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) && Directory.Exists(link) && Directory.Exists(target);
        }
        catch (Exception) { return false; }
    }

    /// <summary>A junction made with FSCTL_SET_REPARSE_POINT on a \\?\ path of the (new, empty) folder <paramref name="link"/>.</summary>
    private static bool TryMakeDirect(string link, string target, out string why)
    {
        why = "";
        try
        {
            string full = Path.GetFullPath(target).TrimEnd('\\', '/');
            byte[] sub = System.Text.Encoding.Unicode.GetBytes(@"\??\" + full), print = System.Text.Encoding.Unicode.GetBytes(full);
            var buf = new byte[8 + 8 + sub.Length + 2 + print.Length + 2];
            BitConverter.GetBytes(0xA0000003u).CopyTo(buf, 0);                         // IO_REPARSE_TAG_MOUNT_POINT
            BitConverter.GetBytes((ushort)(buf.Length - 8)).CopyTo(buf, 4);            // ReparseDataLength
            BitConverter.GetBytes((ushort)0).CopyTo(buf, 8);                           // SubstituteNameOffset
            BitConverter.GetBytes((ushort)sub.Length).CopyTo(buf, 10);                 // SubstituteNameLength
            BitConverter.GetBytes((ushort)(sub.Length + 2)).CopyTo(buf, 12);           // PrintNameOffset
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buf, 14);               // PrintNameLength
            sub.CopyTo(buf, 16);
            print.CopyTo(buf, 16 + sub.Length + 2);
            Directory.CreateDirectory(link);
            nint h = CreateFileW(AppDataRedirect.ExtendedPath(link), 0x40000000, 7, 0, 3, 0x02000000 | 0x00200000, 0);   // GENERIC_WRITE, OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT
            if (h == -1) { why = "CreateFile failed, error " + Marshal.GetLastWin32Error(); return false; }
            try
            {
                if (!DeviceIoControl(h, 0x000900A4, buf, (uint)buf.Length, 0, 0, out _, 0)) { why = "FSCTL_SET_REPARSE_POINT failed, error " + Marshal.GetLastWin32Error(); return false; }
                return true;
            }
            finally { CloseHandle(h); }
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint device, uint code, byte[] inBuffer, uint inSize, nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>Removes the junction itself (not what it points to).</summary>
    public static void Remove(string link)
    {
        try { if (Directory.Exists(link)) Directory.Delete(link, recursive: false); } catch { }
    }

    /// <summary>
    /// Ends the test as skipped (<see cref="Check.Skip"/>: the runner prints SKIP and counts it, and CLINK_TESTS_NO_SKIP=1 turns it into a
    /// failure), because no junction could be made here. Never returns.
    /// </summary>
    public static void Skipped(string test, string why)
    {
        Log.Warn($"[unittest] {test}: no junction could be made here ({why}); not checked");
        Check.Skip($"no junction could be made here ({why}); not checked");
    }
}

/// <summary>
/// <c>--dev</c> command lines that must never become a normal start (a look-alike switch: a different dash, a slash, a stray space)
/// and data folders a dev instance must not live in (the real Clink's, a packaged app's private AppData, a drive root, the user
/// profile, a parent of the real settings, clips or archive, a folder of other people's files). Each is an <see cref="ArgumentException"/>
/// from <see cref="DevOptions.Parse"/>, which <c>App.RunNormal</c> turns into exit code 2 before the single-instance mutex is taken.
/// </summary>
internal static class DevFolderTests
{
    private static string Problem(params string[] args) => Check.Throws<ArgumentException>(() => DevOptions.Parse(args)).Message;

    // ------------------------------------------------------------------ look-alike switches

    [UnitTest]
    public static void A_switch_that_only_looks_like_a_dev_switch_is_an_error_not_a_normal_start()
    {
        // A different dash, one dash or three, a slash, a space, an invisible character or a quote in front: each would be skipped as
        // an argument Clink does not know, and the start would be a normal one (the real Clink's mutex, its pipe).
        string[] lookAlikes =
        {
            "-dev", "---dev", "/dev", "\u2013dev", "\u2014dev", "\u2212dev", "\u2010dev", "\u2011dev", "\u2012dev", "\u2015dev", "\uFF0Ddev", "\uFE63dev",
            " --dev", "--dev ", "\t--dev", "--DEV\t", "\u200B--dev", "\u00AD--dev", "\uFEFF--dev", "\"--dev\"", "'--dev'", "- -dev", "dev",
            "\u2013\u2013dev", "\u2014\u2014dev", "-\u2013dev", "/\u2014dev",
            "-dev-record", "/dev-hotkeys", "\u2014dev-token", "\u2013\u2013dev-accept-import", " --dev-record", "--dev-hotkeys ", "--dev-recordx", "--dev-hotkey",
            "\u2013dev=C:\\x", "/dev=C:\\x", "-dev=C:\\x", "--devx=C:\\x", " --dev=C:\\x",
            "-packages-root", "/packages-root", "\u2014packages-root", " --packages-root", "--packages-roots", "packages-root", "\u2013\u2013packages-root",
        };
        foreach (string a in lookAlikes)
        {
            string said = Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { a }), $"'{a}' alone").Message;
            Check.Contains(said, "not a dev switch", $"'{a}' alone");
            Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { a, "--dev", @"C:\scratch\x" }), $"'{a}' before a real --dev");
            Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", @"C:\scratch\x", a }), $"'{a}' after a real --dev");
            Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--show", "--minimized", a }), $"'{a}' among ordinary arguments");
        }
    }

    [UnitTest]
    public static void The_message_for_a_look_alike_shows_which_dash_it_was()
    {
        Check.Contains(Problem("\u2013dev"), "'\\u2013dev'");   // an en dash, written so that it can be told from a hyphen
        Check.Contains(Problem("\u2014dev-record"), "'\\u2014dev-record'");
        Check.Contains(Problem("\u200B--dev"), "'\\u200B--dev'");
        Check.Contains(Problem(" --dev"), "' --dev'");
        Check.Contains(Problem("-dev"), "'-dev'");
        Check.Contains(Problem("-dev"), "two plain hyphens");
    }

    [UnitTest]
    public static void A_dash_after_dev_is_another_switch_and_never_the_folder()
    {
        // "--dev -dev-record" used to make a folder called "-dev-record"; with an en dash for the hyphen it did the same.
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", "-dev-record" }));
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", "\u2013dev-record" }));
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", "/dev-record" }));
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", "dev-record" }), "a name that starts with dev is not taken as a folder either: write .\\dev-record");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", @"C:\scratch\x", "--packages-root", "\u2014packages-root" }));
        // other dashes are not dev switches, so they are not an error, but still not a folder
        string folder = Path.GetFullPath(DevOptions.DefaultFolder);
        Check.Equal(folder, DevOptions.Parse(new[] { "--dev", "-x" })!.Folder);
        Check.Equal(folder, DevOptions.Parse(new[] { "--dev", "\u2013x" })!.Folder);
        Check.Equal(folder, DevOptions.Parse(new[] { "--dev", "--dev-record" })!.Folder);
    }

    [UnitTest]
    public static void The_real_switches_and_the_other_arguments_are_not_taken_for_look_alikes()
    {
        using var dir = new TempDir();
        string folder = dir.File("one");
        var all = DevOptions.Parse(new[]
        {
            "--show", "--minimized", "--DEV", folder, "--Dev-Hotkeys", "--dev-record", "--dev-token", "--dev-accept-import",
            "--packages-root", dir.File("Packages"), "--no-update", "--after-pid", "1234",
        })!;
        Check.Equal(folder, all.Folder);
        Check.True(all.BindHotkeys && all.Record && all.AllowToken && all.AcceptImport);
        Check.Equal(dir.File("Packages"), all.PackagesRoot);
        Check.Equal(folder, DevOptions.Parse(new[] { "--dev=" + folder, "--clip" })!.Folder);
        Check.Equal(folder, DevOptions.Parse(new[] { "--dev=\"" + folder + "\"" })!.Folder);
        // arguments that have nothing to do with dev, in any dash or none, are no dev start and no error
        Check.Null(DevOptions.Parse(new[] { "--clip", "--minimized", "--no-update", "-x", "/x", "\u2013minimized", "--after-pid", "1234", "-v", "/?" }));
        Check.Null(DevOptions.Parse(new[] { "--trim-last", "--share-last", "--show", "C:\\a\\b.mp4", "d" }));
    }

    // ------------------------------------------------------------------ a data folder a dev instance must not live in

    [UnitTest]
    public static void A_dev_folder_inside_the_Packages_folder_is_refused()
    {
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        foreach (string path in new[]
        {
            packages,
            Path.Combine(packages, "Contoso.Notes_pzs8sxrjxfjjc"),
            Path.Combine(packages, "Contoso.Notes_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Clink-dev"),
            Path.Combine(packages, "Foo_abc", "LocalState", "dev"),
            packages.ToUpperInvariant() + @"\Foo_abc",
            Path.Combine(packages, "Foo_abc").Replace('\\', '/'),
            Path.Combine(packages, "Foo_abc", "..", "Bar_x"),
        })
        {
            Check.Contains(Problem("--dev", path), "Packages", path);
            Check.Contains(Problem("--dev=" + path), "Packages", path);
        }
        Check.Contains(Problem("--dev", Path.Combine(packages, "Foo_abc")), "keeps the private copies of AppData");
    }

    [UnitTest]
    public static void A_dev_folder_whose_final_path_is_a_packaged_apps_private_AppData_is_refused()
    {
        using var dir = new TempDir();
        // another profile, another drive, a folder under Temp that is shaped like a private copy: the shape is what Windows shows for a
        // folder that a packaged app made in its AppData, whoever's Packages folder it is
        // (On the drive of the Temp folder: a path on a drive that does not exist cannot be checked at all and is refused for that.)
        string drive = Path.GetPathRoot(dir.Path)!.TrimEnd('\\');
        foreach (string path in new[]
        {
            drive + @"\Users\Someone\AppData\Local\Packages\Fake_abc\LocalCache\Roaming\Clink-dev",
            drive + @"\x\AppData\Local\Packages\Foo_1\LocalCache\Local\dev",
            dir.File(@"AppData\Local\Packages\Fake_abc\LocalCache\Roaming\dev"),
            dir.File(@"appdata\local\packages\fake_abc\localcache\roaming\dev"),
        })
            Check.Contains(Problem("--dev", path), "private copy of AppData", path);
        Check.Contains(Problem("--dev", drive + @"\Users\Someone\AppData\Local\Packages\Fake_abc\LocalCache\Roaming\Clink-dev"), "Fake_abc");

        // what is not a packaged app's AppData is fine: LocalState and RoamingState are the app's own data, not the AppData redirect
        Check.NotNull(DevOptions.Parse(new[] { "--dev", dir.File(@"AppData\Local\Packages\Foo_1\LocalState\dev") }));
        Check.NotNull(DevOptions.Parse(new[] { "--dev", dir.File(@"Packages\Foo_1\LocalCache\Roaming\dev") }), "not in an AppData\\Local\\Packages");
    }

    [UnitTest]
    public static void A_dev_folder_that_holds_the_real_Clinks_settings_is_refused()
    {
        using var home = new TempDir();   // a made-up %APPDATA%
        string real = home.File(@"AppData\Roaming\Clink");
        Settings.RealDirOverride = real;
        Check.Contains(Problem("--dev", real), "real Clink's settings folder");
        foreach (string parent in new[] { home.File(@"AppData\Roaming"), home.File("AppData"), home.Path, home.Path + @"\", home.File(@"AppData\Roaming\Clink\..") })
            Check.Contains(Problem("--dev", parent), "holds the real Clink's settings folder", parent);
        Check.Contains(Problem("--dev=" + home.File("AppData")), "holds the real Clink's settings folder");
        Check.Contains(Problem("--dev", home.File("AppData").Replace('\\', '/').ToUpperInvariant()), "holds the real Clink's settings folder");

        // beside it, or inside it, is a folder of its own
        Check.Equal(real + "-dev", DevOptions.Parse(new[] { "--dev", real + "-dev" })!.Folder);
        Check.Equal(Path.Combine(real, "dev"), DevOptions.Parse(new[] { "--dev", Path.Combine(real, "dev") })!.Folder);
        Check.Equal(home.File("dev"), DevOptions.Parse(new[] { "--dev", home.File("dev") })!.Folder);
    }

    [UnitTest]
    public static void A_dev_folder_that_is_or_holds_the_real_clips_or_archive_is_refused()
    {
        using var home = new TempDir();
        string real = home.File(@"AppData\Roaming\Clink");
        Settings.RealDirOverride = real;
        Directory.CreateDirectory(real);
        var saved = Settings.FromJson("{}");
        saved.ClipsFolder = home.File(@"Videos\MyClips");
        saved.ArchiveEnabled = true;
        saved.ArchivePath = home.File(@"nas\archive");
        File.WriteAllText(Path.Combine(real, "settings.json"), saved.ToJson());

        Check.Contains(Problem("--dev", home.File(@"Videos\MyClips")), "the real clips folder");
        Check.Contains(Problem("--dev", home.File("Videos")), "the real clips folder");
        Check.Contains(Problem("--dev", home.File(@"nas\archive")), "the real archive folder");
        Check.Contains(Problem("--dev", home.File("nas")), "the real archive folder");
        Check.Contains(Problem("--dev", home.File(@"Videos\MyClips\")), "the real clips folder");
        Check.Contains(Problem("--dev", home.File(@"videos\myclips")), "the real clips folder", "the case does not matter");
        Check.Contains(Problem("--dev", home.File(@"Videos\x\..\MyClips")), "the real clips folder");

        // the default clips folder, whatever the real settings say
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (videos.Length > 0)
        {
            Check.Contains(Problem("--dev", Path.Combine(videos, "Clink")), "the default clips folder");
            Check.Contains(Problem("--dev", videos), "the default clips folder");
        }
        // and what Windows keeps for packaged apps, and the real Clink's local data (the updates)
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check.Contains(Problem("--dev", local), "holds");
        Check.Contains(Problem("--dev", Path.Combine(local, "Clink")), "the real Clink's local data folder");

        // folders of their own, near them
        Check.NotNull(DevOptions.Parse(new[] { "--dev", home.File(@"Videos\Other") }));
        Check.NotNull(DevOptions.Parse(new[] { "--dev", home.File(@"nas\other") }));
        Check.NotNull(DevOptions.Parse(new[] { "--dev", home.File(@"Videos\MyClips-dev") }));
        Check.NotNull(DevOptions.Parse(new[] { "--dev", home.File(@"Videos\MyClips\dev") }), "inside the real clips folder is not the real clips folder");
    }

    [UnitTest]
    public static void A_dev_folder_that_is_a_drive_root_or_the_user_profile_is_refused()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        Check.Contains(Problem("--dev", root), "drive's root");
        Check.Contains(Problem("--dev=" + root), "drive's root");
        Check.Contains(Problem("--dev", root.ToLowerInvariant()), "drive's root");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check.Contains(Problem("--dev", profile), "user profile folder");
        Check.Contains(Problem("--dev", profile + @"\"), "user profile folder");
        Check.Contains(Problem("--dev", profile.ToUpperInvariant()), "user profile folder");
        Check.Contains(Problem("--dev=" + profile.Replace('\\', '/')), "user profile folder");
        // the folder that holds every profile holds the real settings
        Check.Contains(Problem("--dev", Path.GetDirectoryName(profile)!), "holds");
        // a folder of its own inside one is fine
        Check.NotNull(DevOptions.Parse(new[] { "--dev", Path.Combine(profile, "clink-dev-unittest-nothing-here") }));
        Check.NotNull(DevOptions.Parse(new[] { "--dev", Path.Combine(root, "clink-dev-unittest-nothing-here") }));
    }

    [UnitTest]
    public static void A_dev_folder_is_new_empty_or_one_an_earlier_run_made_never_a_folder_of_other_files()
    {
        using var dir = new TempDir();
        Check.NotNull(DevOptions.Parse(new[] { "--dev", dir.File("not-there-yet") }), "a leaf folder Clink makes");
        Directory.CreateDirectory(dir.File("empty"));
        Check.NotNull(DevOptions.Parse(new[] { "--dev", dir.File("empty") }), "empty");

        // what an earlier dev run leaves: settings, the clip index, the log, their backups, clips (other files beside them do not matter)
        foreach (string name in new[] { "settings.json", "settings.json.bak", "settings.json.unreadable", "clips.json", "clips.json.bak", "clink.log", "clink.prev.log", "clink-cli.log", "settings.pre-import.json", "settings.pre-v3.json" })
        {
            string earlier = dir.File("earlier-" + name);
            Directory.CreateDirectory(earlier);
            File.WriteAllText(Path.Combine(earlier, "notes.txt"), "x");
            File.WriteAllText(Path.Combine(earlier, name), "x");
            Check.NotNull(DevOptions.Parse(new[] { "--dev", earlier }), name);
        }
        Directory.CreateDirectory(dir.File(@"with-clips\clips"));
        File.WriteAllText(dir.File(@"with-clips\a.txt"), "x");
        Check.NotNull(DevOptions.Parse(new[] { "--dev", dir.File("with-clips") }), "a clips folder of its own");

        // someone's own folder, and a folder full of folders
        Directory.CreateDirectory(dir.File("documents"));
        File.WriteAllText(dir.File(@"documents\report.docx"), "x");
        Check.Contains(Problem("--dev", dir.File("documents")), "other files and no Clink data");
        Check.Contains(Problem("--dev=" + dir.File("documents")), "use a new or an empty one");
        Directory.CreateDirectory(dir.File(@"just-a-subfolder\inner"));
        Check.Contains(Problem("--dev", dir.File("just-a-subfolder")), "other files and no Clink data");
        Check.Contains(Problem("--dev", dir.Path), "other files and no Clink data", "this folder holds all of the above and no Clink data of its own");
    }

    [UnitTest]
    public static void A_dev_folder_that_is_a_junction_is_judged_by_where_it_really_is()
    {
        using var dir = new TempDir();
        string privateCopy = dir.File(@"AppData\Local\Packages\Fake_abc\LocalCache\Roaming\Clink");   // what a packaged app's private AppData looks like
        string realFolder = dir.File("the-real-settings");
        string plain = dir.File("a-folder-of-its-own");
        Directory.CreateDirectory(privateCopy);
        Directory.CreateDirectory(realFolder);
        Directory.CreateDirectory(plain);
        Settings.RealDirOverride = realFolder;

        string toPackage = dir.File("looks-innocent"), toReal = dir.File("also-innocent"), toPlain = dir.File("fine");
        if (!TestJunction.TryMake(toPackage, privateCopy, out string why)) { TestJunction.Skipped(nameof(A_dev_folder_that_is_a_junction_is_judged_by_where_it_really_is), why); return; }
        try
        {
            if (!TestJunction.TryMake(toReal, realFolder, out why) || !TestJunction.TryMake(toPlain, plain, out why))
            {
                TestJunction.Skipped(nameof(A_dev_folder_that_is_a_junction_is_judged_by_where_it_really_is), why);
                return;
            }
            try
            {
                string said = Problem("--dev", toPackage);
                Check.Contains(said, "private copy of AppData");
                Check.Contains(said, "Fake_abc");
                Check.Contains(said, "really", "it says where the link goes");
                Check.Contains(Problem("--dev", Path.Combine(toPackage, "not-yet", "deeper")), "private copy of AppData", "a folder yet to be made inside the link");
                Check.Contains(Problem("--dev", toReal), "real Clink's settings folder");
                Check.Contains(Problem("--dev=" + Path.Combine(toReal, "..", "also-innocent")), "real Clink's settings folder");
                Check.Equal(toPlain, DevOptions.Parse(new[] { "--dev", toPlain })!.Folder, "a link to an ordinary empty folder is an ordinary folder");
            }
            finally { TestJunction.Remove(toReal); TestJunction.Remove(toPlain); }
        }
        finally { TestJunction.Remove(toPackage); }
    }
}
