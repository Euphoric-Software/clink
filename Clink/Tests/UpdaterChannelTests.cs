using System.IO;
using System.Text;
using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

// The update channels: release versus dev builds (X.Y.Z-dev.N builds published from the dev branch), version order and the names of the
// files in the updates folder. No network.
internal static class UpdaterChannelTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure($"'{text}' is not a version");

    [UnitTest]
    public static void Versions_parse_and_order()
    {
        var v = V("v0.3.1-dev.10+abc");
        Check.Equal(new Version(0, 3, 1), v.Number);
        Check.Equal("dev.10", v.Prerelease);
        Check.Equal(10, v.DevBuild);
        Check.True(v.IsDevBuild);
        Check.True(v.IsPrerelease);
        Check.False(V("0.3.1-beta.1").IsDevBuild);
        Check.False(V("0.3.1").IsPrerelease);
        Check.Equal(new Version(1, 2, 0), V("1.2").Number, "normalized to three parts");
        Check.Null(ReleaseVersion.Parse("nonsense"));
        Check.Null(ReleaseVersion.Parse(null));

        Check.True(V("0.3.1-dev.10") > V("0.3.1-dev.9"), "numeric, not text, order");
        Check.True(V("0.3.1") > V("0.3.1-dev.99"));
        Check.True(V("0.3.1-dev.99") > V("0.3.0"));
        Check.True(V("0.3.0") < V("0.3.1-dev.1"));
        Check.True(V("0.2.0") > V("0.2.0-beta.1"));
        Check.True(V("0.3.1-dev.5") >= V("0.3.1-dev.5") && V("0.3.1-dev.5") <= V("0.3.1-dev.5"));

        Check.Equal("0.3.1", V("v0.3.1+sha").ToString());
        Check.Equal("0.3.1-dev.57", V("0.3.1-dev.57+abc").ToString());
        Check.Equal(V("0.3.1-dev.57"), V(V("0.3.1-dev.57").ToString()), "round-trips");
    }

    [UnitTest]
    public static void ShouldInstall_follows_the_channel()
    {
        // Stable channel, running a release.
        Check.True(Updater.ShouldInstall(V("0.3.1"), V("0.3.0"), false, false));
        Check.False(Updater.ShouldInstall(V("0.3.0"), V("0.3.0"), false, false));
        Check.False(Updater.ShouldInstall(V("0.2.6"), V("0.3.0"), false, true));
        Check.False(Updater.ShouldInstall(V("0.3.1-dev.5"), V("0.3.0"), false, true), "a dev build is never installed on the stable channel");
        Check.True(Updater.ShouldInstall(V("0.2.0"), V("0.2.0-beta.1"), false, false), "a release replaces a beta of its own number");

        // Dev channel.
        Check.True(Updater.ShouldInstall(V("0.3.1-dev.5"), V("0.3.0"), true, false));
        Check.True(Updater.ShouldInstall(V("0.3.1-dev.6"), V("0.3.1-dev.5"), true, false));
        Check.True(Updater.ShouldInstall(V("0.3.1"), V("0.3.1-dev.9"), true, false));
        Check.False(Updater.ShouldInstall(V("0.3.1-dev.5"), V("0.3.1-dev.5"), true, true));
        Check.False(Updater.ShouldInstall(V("0.3.1-dev.4"), V("0.3.1-dev.5"), true, true));
        Check.False(Updater.ShouldInstall(V("0.3.0"), V("0.3.1-dev.5"), true, true), "the dev channel never goes down to an older release");

        // Stable channel, running a dev build: going back is deliberate.
        Check.False(Updater.ShouldInstall(V("0.3.0"), V("0.3.1-dev.5"), false, false));
        Check.True(Updater.ShouldInstall(V("0.3.0"), V("0.3.1-dev.5"), false, true));
        Check.True(Updater.ShouldInstall(V("0.3.1"), V("0.3.1-dev.5"), false, false));
        Check.True(Updater.ShouldInstall(V("0.3.1"), V("0.3.1-dev.5"), false, true));
        Check.False(Updater.ShouldInstall(V("0.3.1-dev.9"), V("0.3.1-dev.5"), false, true));
    }

    [UnitTest]
    public static void File_names_round_trip_with_a_prerelease()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.Path;
        var v = V("0.3.1-dev.57");
        string file = Updater.FileFor(v);
        Check.Equal("Clink-0.3.1-dev.57.exe", Path.GetFileName(file));
        Check.Equal(v, Updater.VersionFromFile(file));
        Check.Equal("Clink-0.3.1.exe", Path.GetFileName(Updater.FileFor(V("0.3.1"))));
        Check.Equal(V("0.3.1"), Updater.VersionFromFile(@"C:\x\Clink-0.3.1.exe"));
        Check.Null(Updater.VersionFromFile(@"C:\x\Other-0.3.1.exe"));
    }

    [UnitTest]
    public static void A_downloaded_dev_build_is_only_picked_up_on_the_dev_channel()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        var v = V("9.9.9-dev.5");
        string file = Updater.FileFor(v);
        File.WriteAllBytes(file, new byte[] { (byte)'M', (byte)'Z', 0, 0, 0, 0 });
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));

        var settings = new Settings();
        settings.PendingUpdateVersion = v.ToString();
        settings.PendingUpdateSha256 = sha;
        settings.PendingUpdateSignedSha256 = sha;   // only a download made from a signed manifest (its hash) is ever installed
        var up = new Updater(settings);

        Check.False(up.TryGetPendingUpdate(out _, out _), "stable channel: a dev build in the folder is ignored");
        Check.Null(up.InstallProblem);

        settings.UseDevBuilds = true;
        bool found = up.TryGetPendingUpdate(out string path, out ReleaseVersion version);
        if (found)
        {
            Check.Equal(file, path);
            Check.Equal(v, version);
        }
        else Check.NotNull(up.InstallProblem, "refused only because this runner cannot install (elevated or read-only folder)");
    }

    [UnitTest]
    public static void Dev_suffixes_are_strict_and_a_default_version_does_not_throw()
    {
        Check.False(V("0.3.1-dev.07").IsDevBuild, "a leading zero is not a dev build number");
        Check.True(V("0.3.1-dev.0").IsDevBuild);
        Check.False(V("0.3.1-dev.07") == V("0.3.1-dev.7"));
        Check.False(V("0.3.1-dev.07").CompareTo(V("0.3.1-dev.7")) == 0, "equality and ordering agree");
        var none = default(ReleaseVersion);
        Check.Equal("0.0.0", none.ToString());
        Check.Equal(0, none.CompareTo(default));
        Check.True(none < V("0.0.1"));
        Check.True(V("0.0.1") > none);
    }

    [UnitTest]
    public static void An_exe_must_report_the_dev_suffix_its_tag_promises()
    {
        Check.NotNull(Updater.EmbeddedVersionProblem(V("0.3.1"), V("0.3.1-dev.57")), "a release exe in a dev build's place");
        Check.NotNull(Updater.EmbeddedVersionProblem(V("0.3.1-dev.57"), V("0.3.1")));
        Check.NotNull(Updater.EmbeddedVersionProblem(V("0.3.1-dev.56"), V("0.3.1-dev.57")));
        Check.Null(Updater.EmbeddedVersionProblem(V("0.3.1-dev.57+abc"), V("0.3.1-dev.57")));
        Check.Null(Updater.EmbeddedVersionProblem(V("0.3.1"), V("0.3.1")));
    }

    [UnitTest]
    public static void The_channel_and_the_download_origin_are_never_imported()
    {
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.UseDevBuilds)));
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.PendingUpdateFrom)));
    }

    [UnitTest]
    public static void A_download_finished_after_the_channel_switched_is_not_offered()
    {
        var settings = new Settings();
        using var up = new Updater(settings, V("0.3.0"));
        int ready = 0, cleared = 0;
        up.UpdateReady += _ => ready++;
        up.PendingCleared += () => cleared++;
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        string path = Updater.FileFor(V("0.3.1-dev.6"));
        File.WriteAllBytes(path, new byte[] { (byte)'M', (byte)'Z', 0, 0, 0, 0 });

        // Dev Build was ticked when the check started and unticked before the download finished.
        Check.False(up.TrySetPending(path, V("0.3.1-dev.6"), "downloaded and verified", manual: true));
        Check.Null(up.PendingVersion);
        Check.Null(up.PendingPath);
        Check.Equal(0, ready);

        settings.UseDevBuilds = true;
        Check.True(up.TrySetPending(path, V("0.3.1-dev.6"), "downloaded and verified", manual: false));
        Check.Equal(V("0.3.1-dev.6"), up.PendingVersion);
        Check.Equal(1, ready);
        Check.False(up.DropStalePending(), "still wanted on the dev channel");
        Check.Equal(0, cleared);

        // Unticked after the download finished: the pending dev build is dropped (Update to v... can no longer install it).
        settings.UseDevBuilds = false;
        Check.True(up.DropStalePending());
        Check.Null(up.PendingVersion);
        Check.Null(up.PendingPath);
        Check.Equal(1, cleared);
        Check.False(up.DropStalePending(), "nothing left to drop");
        Check.Equal(1, cleared);
    }

    [UnitTest]
    public static void Leaving_a_dev_build_needs_a_manual_check()
    {
        var settings = new Settings();
        using var up = new Updater(settings, V("0.3.1-dev.5"));
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        string path = Updater.FileFor(V("0.3.0"));
        File.WriteAllBytes(path, new byte[] { (byte)'M', (byte)'Z', 0, 0, 0, 0 });
        Check.False(up.TrySetPending(path, V("0.3.0"), "downloaded and verified", manual: false), "the background loop never goes back to an older release");
        Check.Null(up.PendingVersion);
        Check.Null(settings.PendingUpdateFrom, "nothing accepted, nothing recorded");
        Check.True(up.TrySetPending(path, V("0.3.0"), "downloaded and verified", manual: true));
        Check.Equal("0.3.1-dev.5", settings.PendingUpdateFrom, "accepted by this exe, so the next start may install it");
        Check.False(up.DropStalePending(), "a deliberate switch back stays installable");
        settings.UseDevBuilds = true;
        Check.True(up.DropStalePending(), "ticking Dev Build again drops it");
        Check.Null(up.PendingVersion);

        // A pending entry whose file is gone (a later download's cleanup deleted it) is dropped, not left as a dead "ready" update.
        settings.UseDevBuilds = false;
        Check.True(up.TrySetPending(path, V("0.3.0"), "downloaded and verified", manual: true));
        int cleared = 0;
        up.PendingCleared += () => cleared++;
        File.Delete(path);
        Check.True(up.DropStalePending(), "the file is missing");
        Check.Null(up.PendingVersion);
        Check.Equal(1, cleared);
    }

    [UnitTest]
    public static void At_startup_only_this_exes_own_download_takes_a_dev_build_back_to_a_release()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        var target = V("0.3.0");
        string file = Updater.FileFor(target);
        File.WriteAllBytes(file, new byte[] { (byte)'M', (byte)'Z', 0, 0, 0, 0 });
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));

        var settings = new Settings();
        settings.PendingUpdateVersion = target.ToString();
        settings.PendingUpdateSha256 = sha;
        settings.PendingUpdateSignedSha256 = sha;
        settings.PendingUpdateFrom = "0.2.9";   // left by an older exe, before this dev build was installed by hand
        using var up = new Updater(settings, V("0.3.1-dev.5"));

        Check.False(up.TryGetPendingUpdate(out _, out _), "a release downloaded by another exe does not replace a newer dev build");
        Check.Null(up.InstallProblem);
        Check.True(File.Exists(file), "the scan does not delete it");

        settings.PendingUpdateFrom = null;
        Check.False(up.TryGetPendingUpdate(out _, out _), "no record of who downloaded it");
        Check.Null(up.InstallProblem);

        settings.PendingUpdateFrom = "0.3.1-dev.5";   // this exe switched back to releases (manual check / Dev Build unticked)
        bool found = up.TryGetPendingUpdate(out string path, out ReleaseVersion version);
        if (found)
        {
            Check.Equal(file, path);
            Check.Equal(target, version);
        }
        else Check.NotNull(up.InstallProblem, "refused only because this runner cannot install (elevated or read-only folder)");

        settings.UseDevBuilds = true;
        Check.False(up.TryGetPendingUpdate(out _, out _), "the dev channel never goes down to an older release");
        Check.Null(up.InstallProblem);
    }

    // ------------------------------------------------------------------ M1: only the version shapes the workflows produce

    [UnitTest]
    public static void Canonical_versions_are_X_Y_Z_or_X_Y_Z_dev_N_and_nothing_else()
    {
        Check.Equal(V("0.4.2"), ReleaseVersion.ParseCanonical("0.4.2"));
        Check.Equal(V("10.20.30"), ReleaseVersion.ParseCanonical("10.20.30"));
        Check.Equal(V("0.4.3-dev.7"), ReleaseVersion.ParseCanonical("0.4.3-dev.7"));
        Check.Equal(V("0.4.3-dev.0"), ReleaseVersion.ParseCanonical("0.4.3-dev.0"));
        foreach (string? bad in new string?[]
                 {
                     null, "", " ", "v9.9.9", "V9.9.9", "9.9", "9", "9.9.9.9", "9.9.9-beta", "9.9.9-beta.1", "9.9.9-rc.1", "9.9.9-dev", "9.9.9-dev.",
                     "9.9.9-dev.x", "9.9.9-dev.07", "9.9.9-Dev.1", "9.9.9-dev.1.2", "9.9.9+abc", "9.9.9-dev.1+abc", "09.9.9", "9.09.9", "9.9.09",
                     "-1.0.0", "1.0.0 ", " 1.0.0", "1.0.0\n", "1.0.0٠", "١0.0.0", "99999999999.0.0", "1.0.0-dev.99999999999", "1..0", "1.0.0-",
                     "latest", "../1.0.0", "1.0.0/../x",
                 })
            Check.Null(ReleaseVersion.ParseCanonical(bad), $"'{bad}' is not a canonical version");
        Check.Null(ReleaseVersion.ParseCanonical("v1.2.3"), "the file/manifest form has no v");
        Check.Null(ReleaseVersion.ParseCanonical("1.2"));
        Check.Equal(V("1.2.3-dev.4"), ReleaseVersion.ParseCanonical("1.2.3-dev.4"));
    }

    [UnitTest]
    public static void A_pre_release_that_is_not_a_dev_build_is_never_installed()
    {
        // Even a signed manifest cannot make the updater install anything but a release or an X.Y.Z-dev.N build.
        Check.False(Updater.ShouldInstall(V("9.9.9-beta"), V("0.4.1"), false, true));
        Check.False(Updater.ShouldInstall(V("9.9.9-beta"), V("0.4.1"), true, true));
        Check.False(Updater.ShouldInstall(V("9.9.9-rc.1"), V("0.4.1-dev.3"), false, true));
        Check.True(Updater.ShouldInstall(V("9.9.9"), V("0.4.1"), false, false));
        Check.True(Updater.ShouldInstall(V("9.9.9-dev.1"), V("0.4.1"), true, false));
    }

    [UnitTest]
    public static void Only_canonical_versions_become_update_files()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Check.Equal("Clink-1.2.3-dev.4.exe", Path.GetFileName(Updater.FileFor(V("1.2.3-dev.4"))));
        Check.Equal(Path.GetFullPath(dir.File("updates")), Path.GetDirectoryName(Path.GetFullPath(Updater.FileFor(V("1.2.3")))));

        // A version with any other suffix (only reachable by a bug) does not become a file name, least of all one with separators.
        foreach (string suffix in new[] { "beta.1", "dev.07", @"..\..\x", "a/b", "x:y", "dev.1+meta" })
        {
            var weird = new ReleaseVersion(new Version(1, 2, 3), suffix);
            bool threw = false;
            try { Updater.FileFor(weird); } catch (ArgumentException) { threw = true; }
            Check.True(threw, $"suffix '{suffix}'");
        }

        foreach (string name in new[] { "Clink-1.2.exe", "Clink-v1.2.3.exe", "Clink-1.2.3-beta.1.exe", "Clink-1.2.3+x.exe", "Clink-1.2.3.exe.failed", "Clink-01.2.3.exe", "Clink-.exe", "Clink-1.2.3-dev.07.exe" })
            Check.Null(Updater.VersionFromFile(@"C:\x\" + name), name);
        Check.Equal(V("1.2.3"), Updater.VersionFromFile(@"C:\x\Clink-1.2.3.exe"));
        Check.Equal(V("1.2.3-dev.4"), Updater.VersionFromFile(@"C:\x\Clink-1.2.3-dev.4.exe"));
    }

    // ------------------------------------------------------------------ an older copy still running (v0.4.1 users upgrade by hand)

    [UnitTest]
    public static void A_plain_start_names_an_older_copy_still_running_from_another_exe()
    {
        const string Own = @"C:\Users\Me\AppData\Local\Programs\Clink\Clink.exe", Old = @"C:\Users\Me\Downloads\Clink.exe";
        var own = V("0.4.2");
        const int Pid = 100, Session = 1;
        AppController.RunningClink Other(string path, string? version, int session = Session, int pid = 200) => new(pid, session, path, version);
        string? Ask(IEnumerable<AppController.RunningClink> running, string? command = null, bool dev = false, ReleaseVersion? ownVersion = null) =>
            AppController.OlderRunningCopy(running, Own, ownVersion ?? own, Pid, Session, command, dev);

        // an older copy elsewhere: the message, with its version and path
        string? msg = Ask(new[] { Other(Old, "0.4.1+abc123") });
        Check.Equal(@"An older Clink (v0.4.1) is still running from C:\Users\Me\Downloads\Clink.exe. Right-click its tray icon, choose Quit, then start Clink again.", msg);
        Check.NotNull(Ask(new[] { Other(Old, "0.4.2-dev.9") }), "a dev build of this version is older too");
        Check.NotNull(Ask(new[] { Other(Old, "garbage"), Other(Old, "0.3.0", pid: 300) }), "the one that reads as older is found");

        // no message
        Check.Null(Ask(new[] { Other(Old, "0.4.2") }), "the same version");
        Check.Null(Ask(new[] { Other(Old, "0.4.3") }), "a newer one");
        Check.Null(Ask(new[] { Other(Own, "0.4.1") }), "the same exe (path)");
        Check.Null(Ask(new[] { Other(Own.ToUpperInvariant(), "0.4.1") }), "the same exe, other case");
        Check.Null(Ask(new[] { Other(Old, "0.4.1") }, command: "clip"), "a forwarded command (--clip, ...)");
        Check.Null(Ask(new[] { Other(Old, "0.4.1") }, dev: true), "a --dev start");
        Check.Null(Ask(new[] { Other(Old, "0.4.1", session: 2) }), "another Windows session");
        Check.Null(Ask(new[] { Other(Old, "0.4.1", pid: Pid) }), "this process itself");
        Check.Null(Ask(new[] { Other(Old, null) }), "an unreadable version");
        Check.Null(Ask(new[] { new AppController.RunningClink(200, Session, null, "0.4.1") }), "an unreadable path");
        Check.Null(Ask(Array.Empty<AppController.RunningClink>()), "nothing else running");
        Check.Null(AppController.OlderRunningCopy(new[] { Other(Old, "0.4.1") }, Own, null, Pid, Session, null, false), "this exe's own version unreadable");
    }
}
