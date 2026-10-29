using System.IO;
using System.Text;
using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

// The update channels: release versus dev builds (X.Y.Z-dev.N pre-releases published from the dev branch). No network: GitHub's
// answers are fake JSON.
internal static class UpdaterChannelTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure($"'{text}' is not a version");

    private static string Entry(string tag, bool prerelease = false, bool draft = false, bool sha = true)
    {
        var assets = new StringBuilder("""{"name":"Clink.exe","size":1000,"browser_download_url":"https://example.invalid/Clink.exe"}""");
        if (sha) assets.Append(""",{"name":"Clink.exe.sha256","size":64,"browser_download_url":"https://example.invalid/Clink.exe.sha256"}""");
        return $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://example.invalid/{{tag}}","body":"","assets":[{{assets}}]}""";
    }

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
    public static void PickRelease_takes_the_newest_usable_entry()
    {
        string list = "[" + string.Join(",",
            Entry("v0.3.0"),
            Entry("v0.3.1-dev.9", prerelease: true),
            Entry("v0.3.1-dev.10", prerelease: true),
            Entry("v0.3.1-dev.11", prerelease: true, draft: true),
            Entry("v0.3.1-dev.12", prerelease: true, sha: false),
            Entry("v0.4.0-beta.1", prerelease: true),
            Entry("v0.3.1-dev.13", prerelease: false)) + "]";

        var dev = Updater.PickRelease(list, true, out string? problem);
        Check.NotNull(dev);
        Check.Null(problem);
        Check.Equal("v0.3.1-dev.10", dev!.Tag);
        Check.Equal(V("0.3.1-dev.10"), dev.Version);

        string list2 = "[" + string.Join(",", Entry("v0.3.1-dev.3", prerelease: true), Entry("v0.3.0"), Entry("v0.3.1")) + "]";
        Check.Equal("v0.3.1", Updater.PickRelease(list2, true, out _)!.Tag, "a release beats the dev builds of its number whatever the list order");

        Check.Null(Updater.PickRelease("[]", true, out problem));
        Check.Equal("no usable release or dev build found", problem);
        Check.Null(Updater.PickRelease("""[{"tag_name":"v0.4.0-beta.1","prerelease":true}]""", true, out problem));
        Check.NotNull(problem);
    }

    [UnitTest]
    public static void ParseRelease_applies_the_channel_rules()
    {
        string devEntry = Entry("v0.3.1-dev.57", prerelease: true);
        Check.Null(Updater.ParseRelease(devEntry, false, out _), "the stable channel rejects a pre-release");
        Check.Equal("v0.3.1-dev.57", Updater.ParseRelease(devEntry, true, out _)!.Tag);
        Check.Null(Updater.ParseRelease(Entry("v0.3.1-dev.57", prerelease: false), true, out _), "a dev tag not flagged pre-release is skipped");
        Check.Null(Updater.ParseRelease(Entry("v0.4.0-beta.1", prerelease: true), true, out _), "betas are skipped");
        Check.Null(Updater.ParseRelease(Entry("v0.3.1", draft: true), false, out _));
        var release = Updater.ParseRelease(Entry("v0.3.1"), false, out string? problem);
        Check.NotNull(release);
        Check.Null(problem);
        Check.Null(Updater.ParseRelease(Entry("v0.3.1", sha: false), false, out problem), "a release without a hash is refused");
        Check.Contains(problem, "sha256");
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

        var settings = Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
        settings.PendingUpdateVersion = v.ToString();
        settings.PendingUpdateSha256 = sha;
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
    public static void PickRelease_says_why_nothing_was_usable()
    {
        Updater.PickRelease("[" + Entry("v0.3.1-dev.12", prerelease: true, sha: false) + "]", true, out string? problem);
        Check.Contains(problem, "no usable release or dev build found");
        Check.Contains(problem, "sha256");
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
        var settings = Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
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
        var settings = Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
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

        var settings = Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
        settings.PendingUpdateVersion = target.ToString();
        settings.PendingUpdateSha256 = sha;
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

    // ------------------------------------------------------------------ M1: only the tag shapes the workflows produce

    [UnitTest]
    public static void Release_tags_are_vX_Y_Z_or_vX_Y_Z_dev_N_and_nothing_else()
    {
        Check.Equal(V("0.4.2"), ReleaseVersion.ParseTag("v0.4.2"));
        Check.Equal(V("10.20.30"), ReleaseVersion.ParseTag("v10.20.30"));
        Check.Equal(V("0.4.3-dev.7"), ReleaseVersion.ParseTag("v0.4.3-dev.7"));
        Check.Equal(V("0.4.3-dev.0"), ReleaseVersion.ParseTag("v0.4.3-dev.0"));
        foreach (string? bad in new string?[]
                 {
                     null, "", " ", "9.9.9", "V9.9.9", "v9.9", "v9", "v9.9.9.9", "v9.9.9-beta", "v9.9.9-beta.1", "v9.9.9-rc.1", "v9.9.9-dev", "v9.9.9-dev.",
                     "v9.9.9-dev.x", "v9.9.9-dev.07", "v9.9.9-Dev.1", "v9.9.9-dev.1.2", "v9.9.9+abc", "v9.9.9-dev.1+abc", "v09.9.9", "v9.09.9", "v9.9.09",
                     "v-1.0.0", "v1.0.0 ", " v1.0.0", "v1.0.0\n", "v1.0.0\u0660", "v\u0661.0.0", "v99999999999.0.0", "v1.0.0-dev.99999999999", "vv1.0.0", "v1..0", "v1.0.0-",
                     "latest", "../v1.0.0", "v1.0.0/../x",
                 })
            Check.Null(ReleaseVersion.ParseTag(bad), $"'{bad}' is not a release tag");
        Check.Null(ReleaseVersion.ParseCanonical("v1.2.3"), "the file/manifest form has no v");
        Check.Null(ReleaseVersion.ParseCanonical("1.2"));
        Check.Equal(V("1.2.3-dev.4"), ReleaseVersion.ParseCanonical("1.2.3-dev.4"));
    }

    [UnitTest]
    public static void Odd_release_tags_are_refused_on_both_channels()
    {
        foreach (bool dev in new[] { false, true })
        {
            foreach (string tag in new[] { "9.9.9", "V9.9.9", "v9.9", "v9.9.9-beta", "v9.9.9+x", "v09.9.9", "v9.9.9-dev.07" })
            {
                foreach (bool prerelease in new[] { false, true })
                {
                    string entry = Entry(tag, prerelease);
                    Check.Null(Updater.ParseRelease(entry, dev, out string? problem), $"{tag} (pre-release {prerelease}, dev channel {dev})");
                    if (!prerelease || dev) Check.NotNull(problem, $"{tag}: says why");
                    Check.Null(Updater.PickRelease("[" + entry + "]", dev, out _), $"{tag} picked from a list");
                }
            }
            // The flag and the tag must agree on every channel, not only on dev.
            Check.Null(Updater.ParseRelease(Entry("v9.9.9-dev.1", prerelease: false), dev, out _), $"dev tag without the pre-release flag (dev channel {dev})");
            Check.Null(Updater.ParseRelease(Entry("v9.9.9", prerelease: true), dev, out _), $"release tag flagged pre-release (dev channel {dev})");
        }
        Check.NotNull(Updater.ParseRelease(Entry("v9.9.9"), false, out _));
        Check.NotNull(Updater.ParseRelease(Entry("v9.9.9"), true, out _));
        Check.NotNull(Updater.ParseRelease(Entry("v9.9.9-dev.3", prerelease: true), true, out _));

        // A hostile release marked "latest" with a non-dev pre-release tag is not installed even if everything else accepted it.
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

    // ------------------------------------------------------------------ L5: only this repo's github.com release pages are opened

    [UnitTest]
    public static void A_release_page_is_opened_only_when_it_is_this_repos_on_github_com()
    {
        const string repo = "Euphoric-Software/Clink";
        foreach (string good in new[]
                 {
                     "https://github.com/Euphoric-Software/Clink/releases/latest", "https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.2",
                     "https://github.com/euphoric-software/clink/releases/tag/v0.4.2-dev.3", "https://GITHUB.com/Euphoric-Software/Clink/releases/latest",
                     "https://github.com:443/Euphoric-Software/Clink/releases/tag/v1.0.0",
                 })
            Check.True(Updater.IsOwnReleasePage(good, repo), good);
        // What is opened is the normalised address, not the raw string.
        Check.Equal("https://github.com/Euphoric-Software/Clink/releases/latest", Updater.OwnReleasePage("https://GITHUB.com:443/Euphoric-Software/Clink/releases/latest", repo));
        Check.Equal("https://github.com/Euphoric-Software/Clink/releases/tag/v1.0.0", Updater.OwnReleasePage("https://github.com:443/Euphoric-Software/Clink/releases/tag/v1.0.0", repo));
        Check.Null(Updater.OwnReleasePage("https://evil.example/x", repo));
        // .NET decodes an escaped unreserved character, so what is opened is the plain, normalised address
        Check.Equal("https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.2", Updater.OwnReleasePage("https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.%32", repo));

        // Only releases/latest and releases/tag/<canonical tag>: not downloads, not an arbitrary tag text, no query, no fragment.
        foreach (string bad in new[]
                 {
                     "https://github.com/Euphoric-Software/Clink/releases/download/v0.4.2/Clink.exe", "https://github.com/Euphoric-Software/Clink/releases/latest?a=1",
                     "https://github.com/Euphoric-Software/Clink/releases/latest#b", "https://github.com/Euphoric-Software/Clink/releases/latest?", "https://github.com/Euphoric-Software/Clink/releases/latest#",
                     "https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.2?a", "https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.2#b",
                     "https://github.com/Euphoric-Software/Clink/releases/tag/", "https://github.com/Euphoric-Software/Clink/releases/tag/0.4.2",
                     "https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.2-beta.1", "https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.2/extra",
                     "https://github.com/Euphoric-Software/Clink/releases/tag/v0.4.%2F2", "https://github.com/Euphoric-Software/Clink/releases/latest/",
                     "https://github.com/Euphoric-Software/Clink/releases/Latest", "https://github.com/Euphoric-Software/Clink/releases/",
                 })
            Check.False(Updater.IsOwnReleasePage(bad, repo), $"'{bad}'");

        foreach (string? bad in new string?[]
                 {
                     null, "", " ", "http://github.com/Euphoric-Software/Clink/releases/latest", "ftp://github.com/Euphoric-Software/Clink/releases/latest",
                     "file:///C:/Windows/System32/calc.exe", "calc.exe", @"C:\Windows\System32\calc.exe", "javascript:alert(1)", "//github.com/Euphoric-Software/Clink/releases/latest",
                     "https://evil.example/Euphoric-Software/Clink/releases/latest", "https://github.com.evil.example/Euphoric-Software/Clink/releases/latest",
                     "https://evilgithub.com/Euphoric-Software/Clink/releases/latest", "https://gist.github.com/Euphoric-Software/Clink/releases/latest",
                     "https://www.github.com/Euphoric-Software/Clink/releases/latest", "https://github.com@evil.example/Euphoric-Software/Clink/releases/latest",
                     "https://evil.example@github.com/Euphoric-Software/Clink/releases/latest", "https://github.com:8443/Euphoric-Software/Clink/releases/latest",
                     "https://github.com/other/repo/releases/latest", "https://github.com/Euphoric-Software/Clink-evil/releases/latest",
                     "https://github.com/Euphoric-Software/Clink", "https://github.com/Euphoric-Software/Clink/", "https://github.com/Euphoric-Software/Clink/issues/1",
                     "https://github.com/Euphoric-Software/Clink/releases", "https://github.com/Euphoric-Software/Clink/releases/../../../evil/x/releases/latest",
                     "https://github.com/Euphoric-Software/Clink/releases/%2e%2e/%2e%2e/evil", "https://github.com/Euphoric-Software/Clink/releases/..%2f..%2fx",
                     @"https://github.com/Euphoric-Software/Clink/releases/..\x", "https://github.com/Euphoric-Software/Clink/releases/latest\r\nHost: x",
                     "https://github.com/Euphoric-Software/Clink/releases/latest calc.exe", "https://github.com\\@evil.example/Euphoric-Software/Clink/releases/latest",
                 })
            Check.False(Updater.IsOwnReleasePage(bad, repo), $"'{bad}'");

        Check.False(Updater.IsOwnReleasePage("https://github.com/Euphoric-Software/Clink/releases/latest", null), "no repo, no page");
        Check.False(Updater.IsOwnReleasePage("https://github.com/Euphoric-Software/Clink/releases/latest", ""));
        Check.False(Updater.IsOwnReleasePage("https://github.com/Euphoric-Software/Clink/releases/latest", "not a repo"));
        Check.False(Updater.IsOwnReleasePage("https://github.com/a/b/releases/latest", "../b"));
        Check.True(Updater.IsOwnReleasePage("https://github.com/a/b/releases/latest", "a/b/"));
        Check.False(Updater.IsOwnReleasePage("https://github.com/a/b/releases/latest", "a/c"));
    }
}
