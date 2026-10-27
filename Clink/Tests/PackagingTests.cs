using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Clink.Services;

namespace Clink.Tests;

// The two flavours (Packaging): detection, which update asset each takes, per-packaging updates folders and pending-download
// isolation, and the preflight run of a downloaded exe. No network and no real process: HTTP and the preflight launcher are fakes.
// (The runner pins Updater.PackagingOverride to SelfContained, i.e. today's behaviour; these tests say Framework where they mean it.)
internal static class PackagingTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure($"'{text}' is not a version");

    private static T OffUi<T>(Func<Task<T>> run) => Task.Run(run).GetAwaiter().GetResult();

    private static byte[] FakeExe(byte fill) { var b = new byte[300]; Array.Fill(b, fill); b[0] = (byte)'M'; b[1] = (byte)'Z'; return b; }
    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // ------------------------------------------------------------------ detection

    [UnitTest]
    public static void Packaging_decision_table()
    {
        const string core = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0\System.Private.CoreLib.dll", deps = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0\Microsoft.NETCore.App.deps.json";
        var rows = new (string? Core, string? Deps, string? Label, Packaging Expect, bool Disagree, string Name)[]
        {
            (core, deps, null, Packaging.Framework, false, "framework runtime, no label"),
            (core, deps, "Framework", Packaging.Framework, false, "framework, label agrees"),
            (core, deps, "SelfContained", Packaging.SelfContained, true, "runtime framework, label self-contained: disagreement = self-contained"),
            ("", null, null, Packaging.SelfContained, false, "single-file self-contained, no label"),
            ("", null, "SelfContained", Packaging.SelfContained, false, "self-contained, label agrees"),
            ("", null, "Framework", Packaging.SelfContained, true, "runtime self-contained, label framework: disagreement"),
            (null, "", "SelfContained", Packaging.SelfContained, false, "null and empty count as missing"),
            ("", deps, null, Packaging.SelfContained, false, "bundled core library but a deps file: not a framework copy"),
            (core, null, null, Packaging.SelfContained, false, "core library on disk but no FX_DEPS_FILE: not a framework copy"),
            (core, "", "Framework", Packaging.SelfContained, true, "label says framework, runtime does not show a shared framework"),
            (core, deps, "garbage", Packaging.Framework, false, "an unknown label is ignored"),
            (core, deps, "1", Packaging.Framework, false, "a number is no label"),
            (core, deps, "framework", Packaging.Framework, false, "labels are exact"),
            (core, deps, " Framework ", Packaging.Framework, false, "a label is trimmed"),
        };
        foreach (var r in rows)
        {
            var d = PackagingInfo.Decide(r.Core, r.Deps, r.Label);
            Check.Equal(r.Expect, d.Packaging, r.Name);
            Check.Equal(r.Disagree, d.Disagreement != null, r.Name + ": disagreement");
        }
        var runtimeOnly = PackagingInfo.Decide(core, deps, "SelfContained");
        Check.Equal(Packaging.Framework, runtimeOnly.RuntimeSays, "the runtime's own verdict is kept for the log");
    }

    [UnitTest]
    public static void Packaging_of_this_process_matches_its_build_label()
    {
        var d = PackagingInfo.Current;
        Console.WriteLine($"packaging: {d.Packaging} (runtime says {d.RuntimeSays}, build label {d.BuildLabel ?? "none"}, disagreement {d.Disagreement ?? "none"})");
        Check.NotNull(d.BuildLabel, "Clink.csproj stamps the Packaging AssemblyMetadata");
        Check.Null(d.Disagreement);
        Check.Equal(d.RuntimeSays, d.Packaging);
        Check.Equal(d.BuildLabel, d.Packaging.ToString());
    }

    // ------------------------------------------------------------------ feed manifest assets

    private static string ManifestJson(string assets, string minRuntime = "10.0") =>
        $$"""
        {"schema":1,"channel":"stable","version":"0.4.2","tag":"v0.4.2","published":"{{DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}}","notesUrl":"https://example.invalid/n","minRuntime":"{{minRuntime}}","assets":{ {{assets}} } }
        """;

    private const string ExeAsset = "\"exe\":{\"url\":\"https://cdn.example/v0.4.2/Clink.exe\",\"size\":1234,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}";
    private const string FxAsset = "\"exeFramework\":{\"url\":\"https://cdn.example/v0.4.2/Clink-framework.exe\",\"size\":777,\"sha256\":\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\"}";

    private static FeedManifest Parse(string json) =>
        UpdateFeed.ParseManifest(Encoding.UTF8.GetBytes(json), "stable", out string? problem) ?? throw new TestFailure("manifest refused: " + problem);

    [UnitTest]
    public static void Packaging_manifest_reads_exeFramework_and_ignores_unknown_keys()
    {
        var m = Parse(ManifestJson(ExeAsset + "," + FxAsset + ""","portable":{"url":"http://not-even-https","size":0},"future":{}"""));
        Check.NotNull(m.ExeFramework);
        Check.Equal(777L, m.ExeFramework!.Size);
        Check.Null(m.Installer);
        Check.Null(Parse(ManifestJson(ExeAsset + ""","portable":{"url":"https://cdn.example/Clink.exe","size":5,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")).ExeFramework, "portable is not exeFramework");
        // A present exeFramework must be valid like any asset.
        var bad = UpdateFeed.ParseManifest(Encoding.UTF8.GetBytes(ManifestJson(ExeAsset + ""","exeFramework":{"url":"http://x/y.exe","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")), "stable", out string? why);
        Check.Null(bad);
        Check.Contains(why, "exeFramework");
    }

    [UnitTest]
    public static void Packaging_asset_selection_from_the_manifest()
    {
        var both = Parse(ManifestJson(ExeAsset + "," + FxAsset));
        var exeOnly = Parse(ManifestJson(ExeAsset));
        var ten = new[] { new Version(10, 0, 1) };
        var nine = new[] { new Version(9, 0, 8) };
        var none = Array.Empty<Version>();

        // SelfContained: always exe, whatever the runtimes or the manifest hold.
        foreach (var runtimes in new[] { ten, nine, none })
        {
            var sc = UpdateFeed.ToUpdateInfo(both, Packaging.SelfContained, runtimes);
            Check.Equal("https://cdn.example/v0.4.2/Clink.exe", sc.ExeUrl);
            Check.Equal(1234L, sc.ExeSize);
            Check.Equal(new string('a', 64), sc.Sha256);
            Check.Equal(Packaging.SelfContained, sc.Flavour);
        }
        Check.Equal("https://cdn.example/v0.4.2/Clink.exe", UpdateFeed.ToUpdateInfo(both).ExeUrl, "the default is today's behaviour");

        // Framework: exeFramework when the runtime is there.
        var fx = UpdateFeed.ToUpdateInfo(both, Packaging.Framework, ten);
        Check.Equal("https://cdn.example/v0.4.2/Clink-framework.exe", fx.ExeUrl);
        Check.Equal(777L, fx.ExeSize);
        Check.Equal(new string('e', 64), fx.Sha256);
        Check.Equal("", fx.ShaUrl);
        Check.Equal(Packaging.Framework, fx.Flavour);

        // ...exe when exeFramework is missing,
        var missing = UpdateFeed.ToUpdateInfo(exeOnly, Packaging.Framework, ten);
        Check.Equal("https://cdn.example/v0.4.2/Clink.exe", missing.ExeUrl);
        Check.Equal(Packaging.SelfContained, missing.Flavour);
        // ...or minRuntime is newer than every installed runtime (or none is found),
        foreach (var runtimes in new[] { nine, none })
        {
            var unmet = UpdateFeed.ToUpdateInfo(both, Packaging.Framework, runtimes);
            Check.Equal("https://cdn.example/v0.4.2/Clink.exe", unmet.ExeUrl);
            Check.Equal(Packaging.SelfContained, unmet.Flavour);
        }
        // minRuntime is judged against the newest runtime, not the first; and an unreadable or empty one asks for nothing.
        Check.Equal(Packaging.Framework, UpdateFeed.ToUpdateInfo(both, Packaging.Framework, new[] { new Version(9, 0, 8), new Version(10, 0, 0) }).Flavour);
        Check.Equal(Packaging.Framework, UpdateFeed.ToUpdateInfo(Parse(ManifestJson(ExeAsset + "," + FxAsset, minRuntime: "")), Packaging.Framework, none).Flavour);
        Check.Equal(Packaging.Framework, UpdateFeed.ToUpdateInfo(Parse(ManifestJson(ExeAsset + "," + FxAsset, minRuntime: "soon")), Packaging.Framework, none).Flavour);
        Check.Equal(Packaging.Framework, UpdateFeed.ToUpdateInfo(Parse(ManifestJson(ExeAsset + "," + FxAsset, minRuntime: "10.0.0-preview.7")), Packaging.Framework, ten).Flavour);
        Check.Equal(Packaging.SelfContained, UpdateFeed.ToUpdateInfo(Parse(ManifestJson(ExeAsset + "," + FxAsset, minRuntime: "10.0.2")), Packaging.Framework, ten).Flavour);
    }

    [UnitTest]
    public static void Packaging_installed_runtimes_are_injectable_and_never_throw()
    {
        DotNetRuntimes.Override = new[] { new Version(9, 0, 0) };
        var both = Parse(ManifestJson(ExeAsset + "," + FxAsset));
        Check.Equal(Packaging.SelfContained, UpdateFeed.ToUpdateInfo(both, Packaging.Framework).Flavour, "the injected runtimes decide");
        DotNetRuntimes.Override = null;
        Check.NotNull(DotNetRuntimes.InstalledDesktop());   // the real scan on this machine: a list, whatever it holds
        Check.Equal(new Version(10, 0, 0), DotNetRuntimes.ParseVersion("10.0.0-preview.2.25163.2"));
        Check.Equal(new Version(10, 0, 1), DotNetRuntimes.ParseVersion("10.0.1"));
        Check.Null(DotNetRuntimes.ParseVersion("x"));
    }

    private static string RuntimeRoot(TempDir dir, string name, params string[] versions)
    {
        string root = dir.File(name);
        Directory.CreateDirectory(root);
        foreach (string v in versions) Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App", v));
        return root;
    }

    [UnitTest]
    public static void Packaging_minimum_runtime_needs_the_same_major()
    {
        var rows = new (string? Min, Version[] Installed, bool Expect, string Name)[]
        {
            ("10.0.5", new[] { new Version(10, 0, 3), new Version(11, 0, 1) }, false, "10.0.3 + 11.0.1 vs 10.0.5: a higher major does not count"),
            ("10.0.5", new[] { new Version(10, 0, 5) }, true, "exactly the minimum"),
            ("10.0.5", new[] { new Version(10, 1, 0) }, true, "a higher minor of the same major"),
            ("10.0.5", new[] { new Version(11, 0, 1) }, false, "only a higher major"),
            ("10.0.5", new[] { new Version(9, 0, 9) }, false, "a lower major"),
            ("10.0.5", Array.Empty<Version>(), false, "nothing installed"),
            ("10.0.0", new[] { new Version(8, 0, 11), new Version(9, 0, 0) }, false, "only older majors"),
            ("", Array.Empty<Version>(), true, "no minimum asks for nothing"),
            ("soon", Array.Empty<Version>(), true, "an unreadable minimum asks for nothing"),
            (null, Array.Empty<Version>(), true, "a missing minimum asks for nothing"),
        };
        foreach (var r in rows) Check.Equal(r.Expect, DotNetRuntimes.MeetsMinimum(r.Min, r.Installed), r.Name);
    }

    [UnitTest]
    public static void Packaging_runtime_scan_over_given_roots()
    {
        using var dir = new TempDir();
        string old = RuntimeRoot(dir, "old", "8.0.11", "9.0.0");
        Check.False(DotNetRuntimes.MeetsMinimum("10.0.0", DotNetRuntimes.ScanDesktop(new[] { old }, null)), "only 8.0.11 and 9.0.0: .NET 10 is missing");

        string rc = RuntimeRoot(dir, "rc", "10.0.0-rc.2", "notaversion", "x.y");
        var found = DotNetRuntimes.ScanDesktop(new[] { rc }, null);
        Check.Equal(1, found.Count, "junk folders are skipped");
        Check.Equal(new Version(10, 0, 0), found[0]);
        Check.True(DotNetRuntimes.MeetsMinimum("10.0.0", found));

        Check.Equal(0, DotNetRuntimes.ScanDesktop(new[] { dir.File("missing"), "", "  " }, null).Count, "a missing or empty root is nothing");
        Check.Equal(0, DotNetRuntimes.ScanDesktop(Array.Empty<string>(), null).Count);

        Check.Equal(1, DotNetRuntimes.ScanDesktop(new[] { rc, rc.ToUpperInvariant(), rc }, null).Count, "the same root in another case is scanned once");

        var both = DotNetRuntimes.ScanDesktop(new[] { dir.File("missing"), old, rc }, null);
        Check.Equal(3, both.Count, "every root is read, a missing one in front included");

        var running = DotNetRuntimes.ScanDesktop(Array.Empty<string>(), new Version(10, 0, 5));
        Check.Equal(1, running.Count, "the running runtime counts with an empty scan");
        Check.Equal(new Version(10, 0, 5), running[0]);
        var first = DotNetRuntimes.ScanDesktop(new[] { old }, new Version(10, 0, 5));
        Check.Equal(new Version(10, 0, 5), first[0], "the running version comes first");
        Check.Equal(3, first.Count);
    }

    [UnitTest]
    public static void Packaging_runtime_scan_survives_an_unreadable_root()
    {
        using var dir = new TempDir();
        string good = RuntimeRoot(dir, "good", "10.0.1");
        string locked = RuntimeRoot(dir, "locked", "10.0.7");
        string lockedApp = Path.Combine(locked, "shared", "Microsoft.WindowsDesktop.App");
        var info = new DirectoryInfo(lockedApp);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            var found = DotNetRuntimes.ScanDesktop(new[] { locked, good }, null);
            Check.True(found.Contains(new Version(10, 0, 1)), "the readable root is still scanned after the unreadable one");
            Check.False(found.Contains(new Version(10, 0, 7)), "nothing is invented from the unreadable root");
        }
        finally
        {
            var undo = info.GetAccessControl();
            undo.RemoveAccessRule(deny);
            info.SetAccessControl(undo);
        }
    }

    [UnitTest]
    public static void Packaging_runtime_scan_ignores_DOTNET_ROOT()
    {
        using var dir = new TempDir();
        string tree = RuntimeRoot(dir, "envroot", "10.99.99");
        string? saved = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Environment.SetEnvironmentVariable("DOTNET_ROOT", tree);
        try
        {
            Check.False(DotNetRuntimes.DefaultRoots().Any(r => string.Equals(r.TrimEnd('\\'), tree, StringComparison.OrdinalIgnoreCase)), "DOTNET_ROOT is not a root");
            Check.False(DotNetRuntimes.ScanDesktop(DotNetRuntimes.DefaultRoots(), null).Contains(new Version(10, 99, 99)));
            DotNetRuntimes.Override = null;
            Check.False(DotNetRuntimes.InstalledDesktop().Contains(new Version(10, 99, 99)), "nor does the real scan read it");
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_ROOT", saved); }
    }

    // ------------------------------------------------------------------ GitHub assets (feed off)

    private static string ReleaseJson(bool fx = true, bool fxSha = true, bool exe = true, bool exeSha = true)
    {
        var assets = new List<string>();
        if (exe) assets.Add("""{"name":"Clink.exe","size":1000,"browser_download_url":"https://gh.example/Clink.exe"}""");
        if (exeSha) assets.Add("""{"name":"Clink.exe.sha256","size":64,"browser_download_url":"https://gh.example/Clink.exe.sha256"}""");
        if (fx) assets.Add("""{"name":"Clink-framework.exe","size":500,"browser_download_url":"https://gh.example/Clink-framework.exe"}""");
        if (fxSha) assets.Add("""{"name":"Clink-framework.exe.sha256","size":64,"browser_download_url":"https://gh.example/Clink-framework.exe.sha256"}""");
        return $$"""{"tag_name":"v0.4.2","draft":false,"prerelease":false,"html_url":"https://gh.example/r","body":"","assets":[{{string.Join(",", assets)}}]}""";
    }

    [UnitTest]
    public static void Packaging_github_asset_choice_without_the_feed()
    {
        // SelfContained: Clink.exe + Clink.exe.sha256 whatever else the release holds (v0.4.1 names unchanged).
        foreach (var sc in new[] { ReleaseJson(), ReleaseJson(fx: false, fxSha: false) })
        {
            var info = Updater.ParseRelease(sc, false, out string? problem);
            Check.NotNull(info, problem);
            Check.Equal("https://gh.example/Clink.exe", info!.ExeUrl);
            Check.Equal("https://gh.example/Clink.exe.sha256", info.ShaUrl);
            Check.Equal(1000L, info.ExeSize);
            Check.Equal(Packaging.SelfContained, info.Flavour);
            Check.Null(info.Sha256);
            var viaPackaging = Updater.ParseRelease(sc, false, Packaging.SelfContained, out _);
            Check.Equal(info.ExeUrl, viaPackaging!.ExeUrl);
        }

        // Framework: Clink-framework.exe + its .sha256.
        var fx = Updater.ParseRelease(ReleaseJson(), false, Packaging.Framework, out string? p2);
        Check.NotNull(fx, p2);
        Check.Equal("https://gh.example/Clink-framework.exe", fx!.ExeUrl);
        Check.Equal("https://gh.example/Clink-framework.exe.sha256", fx.ShaUrl);
        Check.Equal(500L, fx.ExeSize);
        Check.Equal(Packaging.Framework, fx.Flavour);

        // ...falling back to Clink.exe when the release lacks the framework asset (or its hash, which the unsigned path needs).
        foreach (var json in new[] { ReleaseJson(fx: false, fxSha: false), ReleaseJson(fxSha: false) })
        {
            var back = Updater.ParseRelease(json, false, Packaging.Framework, out string? p3);
            Check.NotNull(back, p3);
            Check.Equal("https://gh.example/Clink.exe", back!.ExeUrl);
            Check.Equal("https://gh.example/Clink.exe.sha256", back.ShaUrl);
            Check.Equal(Packaging.SelfContained, back.Flavour);
        }
        // A release with only the framework exe still serves a framework client, and nobody else.
        var onlyFx = ReleaseJson(exe: false, exeSha: false);
        Check.Equal("https://gh.example/Clink-framework.exe", Updater.ParseRelease(onlyFx, false, Packaging.Framework, out _)!.ExeUrl);
        Check.Null(Updater.ParseRelease(onlyFx, false, Packaging.SelfContained, out string? p4));
        Check.Contains(p4, "Clink.exe");
        // The list endpoint (dev channel) chooses the same way.
        Check.Equal("https://gh.example/Clink-framework.exe", Updater.PickRelease("[" + ReleaseJson() + "]", true, Packaging.Framework, out _)!.ExeUrl);
        Check.Equal("https://gh.example/Clink.exe", Updater.PickRelease("[" + ReleaseJson() + "]", true, out _)!.ExeUrl);
    }

    // ------------------------------------------------------------------ folders and pending isolation

    [UnitTest]
    public static void Packaging_updates_folders_are_per_packaging()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Updater.PackagingOverride = Packaging.SelfContained;
        Check.Equal(dir.File("updates"), Updater.UpdatesDir, "SelfContained keeps today's folder");
        Check.Equal(Path.Combine(dir.File("updates"), "Clink-1.2.3.exe"), Updater.FileFor(V("1.2.3")));
        Updater.PackagingOverride = Packaging.Framework;
        Check.Equal(Path.Combine(dir.File("updates"), "fx"), Updater.UpdatesDir);
        Check.Equal(Path.Combine(dir.File("updates"), "fx", "Clink-1.2.3.exe"), Updater.FileFor(V("1.2.3")));
        Check.Equal(dir.File("updates"), Updater.UpdatesDirFor(Packaging.SelfContained), "both are reachable by name");
    }

    [UnitTest]
    public static void Packaging_settings_key_is_per_installation()
    {
        foreach (string key in new[] { nameof(Settings.PendingUpdateFxVersion), nameof(Settings.PendingUpdateFxSha256), nameof(Settings.PendingUpdateFxSigned), nameof(Settings.PendingUpdateFxFrom) })
        {
            Check.True(SettingsImport.IsOwnKey(key), key + ": covered by the PendingUpdate* prefix, never imported");
            Check.True(SettingsImport.HasLabelOrIsVolatile(key), key);
            Check.DoesNotContain(new Settings().ToJson(), key, "left out while null");
        }
        var s = new Settings { PendingUpdateFxVersion = "1.2.3", PendingUpdateFxSha256 = new string('f', 64), PendingUpdateFxSigned = true, PendingUpdateFxFrom = "1.0.0" };
        Check.Contains(s.ToJson(), "\"PendingUpdateFxVersion\": \"1.2.3\"");
        var back = Settings.FromJson(s.ToJson());
        Check.Equal("1.2.3", back.PendingUpdateFxVersion);
        Check.Equal(new string('f', 64), back.PendingUpdateFxSha256);
        Check.Equal(true, back.PendingUpdateFxSigned);
        Check.Equal("1.0.0", back.PendingUpdateFxFrom);
        string merged = SettingsImport.KeepOurOwn(new Settings { PendingUpdateFxVersion = "0.0.1", PendingUpdateFxSha256 = new string('0', 64) }.ToJson(), s.ToJson(), keepOurToken: true);
        var kept = Settings.FromJson(merged);
        Check.Equal("1.2.3", kept.PendingUpdateFxVersion, "another copy's record is not taken over");
        Check.Equal(new string('f', 64), kept.PendingUpdateFxSha256);
        Check.Null(Settings.FromJson(SettingsImport.KeepOurOwn(s.ToJson(), new Settings().ToJson(), keepOurToken: true)).PendingUpdateFxVersion, "and a foreign Fx record does not arrive either");
    }

    /// <summary>A scratch updates folder holding a SelfContained download (root) and a Framework one (fx\), both v9.9.9.</summary>
    private sealed class TwoFlavours : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly byte[] ScExe = FakeExe(1), FxExe = FakeExe(2);
        public readonly string ScFile, FxFile;

        public TwoFlavours()
        {
            Updater.UpdatesDirOverride = Dir.File("updates");
            Updater.PackagingOverride = Packaging.SelfContained; ScFile = Updater.FileFor(V("9.9.9"));
            Updater.PackagingOverride = Packaging.Framework; FxFile = Updater.FileFor(V("9.9.9"));
            Directory.CreateDirectory(Path.GetDirectoryName(ScFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(FxFile)!);
            File.WriteAllBytes(ScFile, ScExe);
            File.WriteAllBytes(FxFile, FxExe);
        }

        /// <summary>Settings recording a verified v9.9.9 download: <paramref name="scRecord"/> in the legacy PendingUpdate* keys (root folder), <paramref name="fxRecord"/> in the PendingUpdateFx* keys (fx\); null = no such record.</summary>
        public Settings Settings(byte[]? scRecord, byte[]? fxRecord)
        {
            var s = Clink.Services.Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
            if (scRecord != null) { s.PendingUpdateVersion = "9.9.9"; s.PendingUpdateSha256 = Hex(scRecord); s.PendingUpdateFrom = "1.0.0"; }
            if (fxRecord != null) { s.PendingUpdateFxVersion = "9.9.9"; s.PendingUpdateFxSha256 = Hex(fxRecord); s.PendingUpdateFxFrom = "1.0.0"; }
            return s;
        }

        public void AssertUntouched(string why)
        {
            Check.Equal(ScExe, File.ReadAllBytes(ScFile), why + ": SelfContained download");
            Check.Equal(FxExe, File.ReadAllBytes(FxFile), why + ": Framework download");
        }

        public void Dispose() => Dir.Dispose();
    }

    /// <summary>Found, or refused only because this runner could not install at all (elevated, read-only folder): then InstallProblem says so.</summary>
    private static void Adopts(Updater up, string expectFile)
    {
        if (up.TryGetPendingUpdate(out string path, out _)) Check.Equal(expectFile, path);
        else Check.NotNull(up.InstallProblem, "refused only because this runner cannot install");
    }

    [UnitTest]
    public static void Packaging_each_flavour_adopts_its_own_record_and_keeps_the_others()
    {
        using var two = new TwoFlavours();
        var current = V("1.0.0");
        var settings = two.Settings(two.ScExe, two.FxExe);
        string before = settings.ToJson();

        Updater.PackagingOverride = Packaging.Framework;
        Adopts(new Updater(settings, current), two.FxFile);
        two.AssertUntouched("Framework copy scanned");
        Updater.PackagingOverride = Packaging.SelfContained;
        Adopts(new Updater(settings, current), two.ScFile);
        two.AssertUntouched("SelfContained copy scanned");
        Check.Equal(before, settings.ToJson(), "no record was rewritten");
    }

    [UnitTest]
    public static void Packaging_foreign_legacy_keys_leave_the_fx_download_and_record_alone()
    {
        // What v0.4.1 / dev.10 do to a shared settings.json: they keep the unknown PendingUpdateFx* keys and rewrite the legacy ones
        // for a download of their own (here a sha that matches nothing in this scratch folder).
        using var two = new TwoFlavours();
        var current = V("1.0.0");
        var settings = two.Settings(FakeExe(7), two.FxExe);
        string legacy = settings.PendingUpdateSha256!;

        Updater.PackagingOverride = Packaging.Framework;
        Adopts(new Updater(settings, current), two.FxFile);
        two.AssertUntouched("Framework copy scanned");
        Check.Equal(Hex(two.FxExe), settings.PendingUpdateFxSha256, "the Fx record is untouched");
        Check.Equal(legacy, settings.PendingUpdateSha256, "and so is the legacy one");

        // The SelfContained copy does find its own root file unverifiable and discards it; fx\ is none of its business.
        Updater.PackagingOverride = Packaging.SelfContained;
        Check.False(new Updater(settings, current).TryGetPendingUpdate(out _, out _));
        Check.False(File.Exists(two.ScFile), "the root file does not match the legacy record");
        Check.Equal(two.FxExe, File.ReadAllBytes(two.FxFile), "fx\\ untouched");
        Check.Equal(Hex(two.FxExe), settings.PendingUpdateFxSha256, "the Fx record is untouched");
        Check.Equal("9.9.9", settings.PendingUpdateFxVersion);
        Check.Equal("1.0.0", settings.PendingUpdateFxFrom);
    }

    [UnitTest]
    public static void Packaging_a_v0_4_1_settings_file_is_the_root_selfcontained_record()
    {
        using var two = new TwoFlavours();
        string json = $$"""{"UpdateRepo":"owner/repo","PendingUpdateVersion":"9.9.9","PendingUpdateSha256":"{{Hex(two.ScExe)}}","PendingUpdateFrom":"1.0.0"}""";
        var settings = Settings.FromJson(json);
        Check.Null(settings.PendingUpdateFxVersion);
        Check.Null(settings.PendingUpdateFxSha256);
        Check.DoesNotContain(settings.ToJson(), "PendingUpdateFx", "nothing Fx is written for a file that had none");

        Updater.PackagingOverride = Packaging.SelfContained;
        Adopts(new Updater(settings, V("1.0.0")), two.ScFile);
        Check.Equal(two.ScExe, File.ReadAllBytes(two.ScFile), "the root download is verified by the legacy keys");

        // A Framework copy has no record in it: it never borrows the legacy one (its fx\ file is unverifiable and goes), the root file stays.
        Updater.PackagingOverride = Packaging.Framework;
        Check.False(new Updater(settings, V("1.0.0")).TryGetPendingUpdate(out _, out _));
        Check.False(File.Exists(two.FxFile), "no Fx record: the unverifiable fx\\ file is discarded");
        Check.Equal(two.ScExe, File.ReadAllBytes(two.ScFile), "the root download is untouched");
        Check.Equal(Hex(two.ScExe), settings.PendingUpdateSha256, "the legacy record is untouched");
        Check.Null(settings.PendingUpdateFxSha256);
    }

    [UnitTest]
    public static void Packaging_cleanup_only_touches_its_own_folder()
    {
        using var two = new TwoFlavours();
        // Nothing here is newer than the running 99.0.0, so a copy's cleanup deletes what it finds in its own folder.
        string scOld = Path.Combine(Path.GetDirectoryName(two.ScFile)!, "Clink-0.0.1.exe"), fxOld = Path.Combine(Path.GetDirectoryName(two.FxFile)!, "Clink-0.0.1.exe");
        File.WriteAllBytes(scOld, two.ScExe);
        File.WriteAllBytes(fxOld, two.FxExe);
        var settings = two.Settings(two.ScExe, two.FxExe);

        Updater.PackagingOverride = Packaging.Framework;
        new Updater(settings, V("99.0.0")).CleanupUpdatesFolder();
        Check.False(File.Exists(two.FxFile) || File.Exists(fxOld), "the Framework copy cleaned its folder");
        Check.True(File.Exists(two.ScFile) && File.Exists(scOld), "and left the SelfContained downloads alone");
        Check.Equal(Hex(two.ScExe), settings.PendingUpdateSha256!.ToLowerInvariant());

        File.WriteAllBytes(two.FxFile, two.FxExe);
        Updater.PackagingOverride = Packaging.SelfContained;
        new Updater(settings, V("99.0.0")).CleanupUpdatesFolder();
        Check.False(File.Exists(two.ScFile) || File.Exists(scOld), "the SelfContained copy cleaned its folder");
        Check.True(File.Exists(two.FxFile), "and left the Framework download alone (it is in a subfolder)");
    }

    [UnitTest]
    public static void Packaging_cleanup_sweeps_applied_files_of_the_other_flavour_only()
    {
        // A Framework copy applied fx\Clink-9.9.9.exe (marker written by the apply), then fell back to the SelfContained exe:
        // that copy cleans the applied file up. An unmarked pending file in fx\ survives, and so does everything outside Clink-*.exe.
        using var two = new TwoFlavours();
        string fxDir = Path.GetDirectoryName(two.FxFile)!;
        string applied = two.FxFile, appliedMarker = applied + ".installed";
        string pending = Path.Combine(fxDir, "Clink-9.9.11.exe");
        string failedOnly = Path.Combine(fxDir, "Clink-9.9.12.exe");
        string stranger = Path.Combine(fxDir, "notes.txt");
        string nested = Path.Combine(fxDir, "sub", "Clink-9.9.13.exe");
        File.WriteAllBytes(appliedMarker, Array.Empty<byte>());
        File.WriteAllBytes(pending, two.FxExe);
        File.WriteAllBytes(failedOnly, two.FxExe); File.WriteAllText(failedOnly + ".failed", "boom");
        File.WriteAllText(stranger, "x");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllBytes(nested, two.FxExe); File.WriteAllBytes(nested + ".installed", Array.Empty<byte>());

        Updater.PackagingOverride = Packaging.SelfContained;   // the copy that fell back
        new Updater(two.Settings(two.ScExe, two.FxExe), V("1.0.0")).CleanupUpdatesFolder();
        Check.False(File.Exists(applied) || File.Exists(appliedMarker), "the applied file and its marker are gone");
        Check.True(File.Exists(pending), "an unmarked pending file survives");
        Check.True(File.Exists(failedOnly) && File.Exists(failedOnly + ".failed"), "a failed-only file is not an applied one");
        Check.True(File.Exists(stranger), "no other file is touched");
        Check.True(File.Exists(nested) && File.Exists(nested + ".installed"), "never recursive");
        Check.True(File.Exists(two.ScFile), "its own pending download is kept");

        // And the other way round: a Framework copy sweeps an applied file of the root folder, not an unmarked one.
        string rootDir = Path.GetDirectoryName(two.ScFile)!;
        File.WriteAllBytes(two.ScFile + ".installed", Array.Empty<byte>());
        string rootPending = Path.Combine(rootDir, "Clink-9.9.14.exe");
        File.WriteAllBytes(rootPending, two.ScExe);
        Updater.PackagingOverride = Packaging.Framework;
        new Updater(two.Settings(two.ScExe, two.FxExe), V("1.0.0")).CleanupUpdatesFolder();
        Check.False(File.Exists(two.ScFile) || File.Exists(two.ScFile + ".installed"), "the applied root file is gone");
        Check.True(File.Exists(rootPending), "the unmarked root file survives");
    }

    [UnitTest]
    public static void Packaging_a_download_goes_to_its_own_folder_and_leaves_the_other_alone()
    {
        using var two = new TwoFlavours();
        byte[] exe = FakeExe(5);
        string sha = Hex(exe);
        Updater.HttpOverride = (req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(req.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(sha) : exe),
        });
        var info = new UpdateInfo(V("9.9.10"), "v9.9.10", "https://gh.example/Clink-framework.exe", exe.Length, "https://gh.example/Clink-framework.exe.sha256", "", "", null, Packaging.Framework);

        var settings = two.Settings(two.ScExe, null);
        Updater.PackagingOverride = Packaging.Framework;
        var up = new Updater(settings, V("1.0.0"));
        string path = OffUi(() => up.DownloadAsync(info, null, CancellationToken.None));
        Check.Equal(Path.Combine(Updater.UpdatesDirFor(Packaging.Framework), "Clink-9.9.10.exe"), path);
        Check.Equal(exe, File.ReadAllBytes(path));
        Check.Equal("9.9.10", settings.PendingUpdateFxVersion, "a Framework download is recorded in the Fx keys");
        Check.Equal(sha, settings.PendingUpdateFxSha256!.ToLowerInvariant());
        Check.Equal("9.9.9", settings.PendingUpdateVersion, "the legacy record is not rewritten");
        Check.Equal(Hex(two.ScExe), settings.PendingUpdateSha256);
        Check.False(File.Exists(two.FxFile), "its own folder's older download was replaced, as always");
        Check.Equal(two.ScExe, File.ReadAllBytes(two.ScFile), "the SelfContained download was not deleted");

        // And the other way round: a SelfContained download records itself and does not touch fx\.
        Updater.PackagingOverride = Packaging.SelfContained;
        var scSettings = new Settings();
        var scUp = new Updater(scSettings, V("1.0.0"));
        string scPath = OffUi(() => scUp.DownloadAsync(info with { Flavour = Packaging.SelfContained }, null, CancellationToken.None));
        Check.Equal(Path.Combine(Updater.UpdatesDirFor(Packaging.SelfContained), "Clink-9.9.10.exe"), scPath);
        Check.Equal(sha, scSettings.PendingUpdateSha256!.ToLowerInvariant());
        Check.Equal("9.9.10", scSettings.PendingUpdateVersion);
        Check.Null(scSettings.PendingUpdateFxVersion, "the SelfContained download writes the legacy keys only");
        Check.True(File.Exists(path), "the Framework download is still there");
        Check.False(File.Exists(two.ScFile), "the SelfContained copy replaced its own older download");
    }

    [UnitTest]
    public static void Packaging_alternating_downloads_keep_both_records()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] body = FakeExe(1);
        Updater.HttpOverride = (req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(req.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(Hex(body)) : body),
        });
        var settings = Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
        string Download(Packaging p, string version, byte fill)
        {
            body = FakeExe(fill);
            Updater.PackagingOverride = p;
            var up = new Updater(settings, V("1.0.0"));
            var info = new UpdateInfo(V(version), "v" + version, "https://gh.example/Clink.exe", body.Length, "https://gh.example/Clink.exe.sha256", "", "", null, p);
            return OffUi(() => up.DownloadAsync(info, null, CancellationToken.None));
        }

        Download(Packaging.SelfContained, "9.9.10", 1);
        Download(Packaging.Framework, "9.9.11", 2);
        Download(Packaging.SelfContained, "9.9.12", 3);
        Download(Packaging.Framework, "9.9.13", 4);
        Download(Packaging.SelfContained, "9.9.14", 5);
        Check.Equal("9.9.14", settings.PendingUpdateVersion);
        Check.Equal(Hex(FakeExe(5)), settings.PendingUpdateSha256!.ToLowerInvariant());
        Check.Equal("9.9.13", settings.PendingUpdateFxVersion);
        Check.Equal(Hex(FakeExe(4)), settings.PendingUpdateFxSha256!.ToLowerInvariant());
        Check.True(File.Exists(Path.Combine(dir.File("updates"), "Clink-9.9.14.exe")) && File.Exists(Path.Combine(dir.File("updates"), "fx", "Clink-9.9.13.exe")), "both flavours' latest downloads are in place");
        // Each flavour still verifies its own file against its own record.
        Updater.PackagingOverride = Packaging.Framework;
        Adopts(new Updater(settings, V("1.0.0")), Path.Combine(dir.File("updates"), "fx", "Clink-9.9.13.exe"));
        Updater.PackagingOverride = Packaging.SelfContained;
        Adopts(new Updater(settings, V("1.0.0")), Path.Combine(dir.File("updates"), "Clink-9.9.14.exe"));
        Check.True(File.Exists(Path.Combine(dir.File("updates"), "fx", "Clink-9.9.13.exe")) && File.Exists(Path.Combine(dir.File("updates"), "Clink-9.9.14.exe")));
    }

    [UnitTest]
    public static void Packaging_checknow_leaves_the_other_flavours_download_and_record_alone()
    {
        // CheckNow as a Framework copy while a SelfContained download and record exist: the root file and the legacy keys stay as they are.
        using var two = new TwoFlavours();
        byte[] exe = FakeExe(9);
        string sha = Hex(exe);
        var calls = new List<string>();
        Updater.HttpOverride = (req, _) =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            calls.Add(url);
            if (url.EndsWith("/latest", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    """{"tag_name":"v9.9.9","draft":false,"prerelease":false,"html_url":"","body":"","assets":[{"name":"Clink.exe","size":300,"browser_download_url":"https://gh.example/Clink.exe"},{"name":"Clink.exe.sha256","size":64,"browser_download_url":"https://gh.example/Clink.exe.sha256"},{"name":"Clink-framework.exe","size":300,"browser_download_url":"https://gh.example/Clink-framework.exe"},{"name":"Clink-framework.exe.sha256","size":64,"browser_download_url":"https://gh.example/Clink-framework.exe.sha256"}]}""") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(url.EndsWith(".sha256", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(sha) : exe) });
        };
        Updater.FeedOverride = FeedTrust.None;
        Updater.BuiltInRepoOverride = "o/r";
        var settings = two.Settings(two.ScExe, null);
        settings.UseDevBuilds = false;
        settings.AutoUpdate = true;
        Updater.PackagingOverride = Packaging.Framework;
        var up = new Updater(settings, V("1.0.0"));
        var info = OffUi(() => up.CheckNowAsync());
        Check.NotNull(info, up.LastError);
        Check.Equal("https://gh.example/Clink-framework.exe", info!.ExeUrl, "a Framework copy asks for the framework exe");
        Check.Equal(two.ScExe, File.ReadAllBytes(two.ScFile), "the SelfContained download is untouched");
        Check.Equal(exe, File.ReadAllBytes(two.FxFile), "its own folder holds the fresh framework download");
        Check.Equal(sha, settings.PendingUpdateFxSha256!.ToLowerInvariant());
        Check.Equal(Hex(two.ScExe), settings.PendingUpdateSha256, "the legacy record is not rewritten");
    }

    // ------------------------------------------------------------------ preflight

    [UnitTest]
    public static void Packaging_preflight_outcomes()
    {
        TimeSpan? seenTimeout = null; string? seenExe = null;
        Func<Updater.PreflightResult, Func<string, TimeSpan, Updater.PreflightResult>> Fake = r => (exe, t) => { seenExe = exe; seenTimeout = t; return r; };
        Check.Null(Updater.PreflightProblem(@"C:\u\Clink-1.exe", TimeSpan.FromSeconds(15), Fake(new(0, false, null))), "exit 0 passes");
        Check.Equal(@"C:\u\Clink-1.exe", seenExe);
        Check.Equal(TimeSpan.FromSeconds(15), seenTimeout);
        Check.Contains(Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1), Fake(new(-2147450740, false, null))), "exit code -2147450740", "a failing exit code fails");
        Check.Contains(Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1), Fake(new(3, false, null))), "exit code 3");
        Check.Contains(Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1), Fake(new(null, true, null))), "did not finish");
        Check.Contains(Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1), Fake(new(null, false, "access denied"))), "access denied");
        Check.Equal("--preflight", Updater.PreflightVerb);
        Check.Equal(TimeSpan.FromSeconds(15), Updater.PreflightTimeout);
        // The default launcher is the test runner's fake (no process is started): exit 0.
        Check.Null(Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1)));
    }

    [UnitTest]
    public static void Packaging_a_failed_preflight_stops_the_apply_and_marks_the_download_failed()
    {
        using var two = new TwoFlavours();
        Updater.IsElevatedOverride = false;
        var up = new Updater(two.Settings(two.ScExe, two.FxExe), V("1.0.0"));
        string exe = two.ScFile, target = Path.Combine(two.Dir.Path, "Clink.exe");
        int launched = 0;
        // Only a timeout is left out here: that is not the file's fault (Packaging_a_preflight_timeout_does_not_condemn_the_download).
        foreach (var result in new[] { new Updater.PreflightResult(1, false, null), new Updater.PreflightResult(null, false, "blocked") })
        {
            launched = 0;
            Updater.PreflightOverride = (e, _) => { launched++; Check.Equal(exe, e); return result; };
            var ex = Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(exe, target, new[] { "--minimized" }));
            Check.Contains(ex.Message, "not applied");
            Check.Equal(1, launched);
            Check.True(File.Exists(exe + ".failed"), "marked failed, like a bad hash");
            Check.Contains(File.ReadAllText(exe + ".failed"), "preflight failed");
            Check.DoesNotContain(File.ReadAllText(exe + ".failed"), "not applied", "the marker stores the reason, not the prefixed sentence");
            File.Delete(exe + ".failed");
        }

        // A passing preflight goes on to start the apply process, which here cannot start (the file is no real exe): not the preflight's error.
        Updater.PreflightOverride = (_, _) => { launched++; return new(0, false, null); };
        launched = 0;
        try { up.LaunchApply(exe, target, Array.Empty<string>()); Check.Fail("the fake exe cannot have started"); }
        catch (TestFailure) { throw; }
        catch (Updater.ApplyRefusedException) { Check.Fail("the preflight passed: the apply process was to be started"); }
        catch (Exception ex) { Check.DoesNotContain(ex.Message, "not applied"); }
        Check.Equal(1, launched);

        // The split: PrepareApply runs the check once and StartApply (what the window calls back on its UI thread) never runs it again;
        // a Prepare that fails leaves no token, so there is nothing to Start.
        launched = 0;
        File.Delete(exe + ".failed");
        var token = up.PrepareApply(exe);
        Check.Equal(1, launched);
        try { Updater.StartApply(token, target, Array.Empty<string>()); } catch (Exception) { }
        Check.Equal(1, launched, "starting the apply does not run the start-up check a second time");
        File.Delete(exe + ".failed");
        Updater.PreflightOverride = (_, _) => { launched++; return new(1, false, null); };
        Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(exe), "a failed Prepare gives no token to Start");
    }

    [UnitTest]
    public static void Packaging_the_runtime_hint_belongs_to_the_framework_flavour()
    {
        Func<string, TimeSpan, Updater.PreflightResult> fail = (_, _) => new(-2147450740, false, null);
        Check.Contains(Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1), fail, Packaging.Framework), ".NET 10 Desktop Runtime");
        string? generic = Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1), fail, Packaging.SelfContained);
        Check.Contains(generic, "exit code -2147450740");
        Check.DoesNotContain(generic, "Desktop Runtime", "a self-contained exe needs no runtime");
        Check.DoesNotContain(Updater.PreflightProblem("x.exe", TimeSpan.FromSeconds(1), fail), "Desktop Runtime", "the default is the generic text");

        // And through PrepareApply, where the flavour comes from the file's folder.
        using var two = new TwoFlavours();
        Updater.IsElevatedOverride = false;
        var up = new Updater(two.Settings(two.ScExe, two.FxExe), V("1.0.0"));
        Updater.PreflightOverride = fail;
        Check.DoesNotContain(Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(two.ScFile)).Message, "Desktop Runtime");
        Check.Contains(Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(two.FxFile)).Message, ".NET 10 Desktop Runtime");
        Check.Contains(File.ReadAllText(two.FxFile + ".failed"), "Desktop Runtime");
    }

    [UnitTest]
    public static void Packaging_apply_verifies_the_file_at_the_moment_of_use()
    {
        // Download through the fake network, make it pending, change one byte, then do what "Update now" does: nothing may run.
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Updater.IsElevatedOverride = false;
        byte[] exe = FakeExe(5);
        Updater.HttpOverride = (req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(req.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(Hex(exe)) : exe),
        });
        int preflights = 0;
        Updater.PreflightOverride = (_, _) => { preflights++; return new(0, false, null); };
        var settings = Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
        var up = new Updater(settings, V("1.0.0"));
        var info = new UpdateInfo(V("9.9.10"), "v9.9.10", "https://gh.example/Clink.exe", exe.Length, "https://gh.example/Clink.exe.sha256", "", "");
        string path = OffUi(() => up.DownloadAsync(info, null, CancellationToken.None));
        Check.True(up.TrySetPending(path, V("9.9.10"), "downloaded and verified", manual: true));
        Check.Equal(path, up.PendingPath);

        var changed = (byte[])exe.Clone(); changed[100] ^= 0xFF;
        File.WriteAllBytes(path, changed);
        string target = dir.File("Clink.exe");
        var ex = Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(up.PendingPath!));
        Check.Contains(ex.Message, "no longer matches");
        Check.Equal(0, preflights, "the changed file was never run");
        Check.False(File.Exists(path), "and was thrown away");
        Check.Null(up.PendingPath, "no 'Update to v…' for a file that is gone");
        Check.False(File.Exists(target), "nothing was applied");

        // The same through LaunchApply, with the file changed after a successful Prepare of an earlier copy: also refused before any run.
        File.WriteAllBytes(path, changed);
        Check.Contains(Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(path, target, Array.Empty<string>())).Message, "no longer matches");
        Check.Equal(0, preflights);

        // An unchanged download passes verification and gets its single start-up check.
        File.WriteAllBytes(path, exe);
        var token = up.PrepareApply(path);
        Check.NotNull(token);
        Check.Equal(1, preflights);
    }

    [UnitTest]
    public static void Packaging_apply_verifies_against_the_record_of_the_files_own_folder()
    {
        using var two = new TwoFlavours();
        Updater.IsElevatedOverride = false;
        int preflights = 0;
        Updater.PreflightOverride = (_, _) => { preflights++; return new(0, false, null); };
        // A Framework record that does not describe fx\Clink-9.9.9.exe, a SelfContained one that describes the root file.
        var settings = two.Settings(two.ScExe, FakeExe(9));
        Updater.PackagingOverride = Packaging.SelfContained;   // this copy's own flavour must not decide
        var up = new Updater(settings, V("1.0.0"));
        Check.Contains(Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(two.FxFile)).Message, "no longer matches");
        Check.Equal(0, preflights);
        Check.False(File.Exists(two.FxFile), "the file that fails its own record is removed");
        Check.NotNull(up.PrepareApply(two.ScFile));
        Check.Equal(1, preflights);
        Check.Equal(two.ScExe, File.ReadAllBytes(two.ScFile));

        // A file outside the updates folders has no record: refused, never run.
        string stray = Path.Combine(two.Dir.Path, "Clink-9.9.9.exe");
        File.WriteAllBytes(stray, two.ScExe);
        Check.Contains(Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(stray)).Message, "updates folder");
        Check.Equal(1, preflights);
        Check.True(File.Exists(stray), "a stranger's file is not deleted");
    }

    [UnitTest]
    public static void Packaging_an_elevated_process_neither_prepares_nor_applies()
    {
        using var two = new TwoFlavours();
        int preflights = 0;
        Updater.PreflightOverride = (_, _) => { preflights++; return new(0, false, null); };
        Updater.IsElevatedOverride = true;
        var up = new Updater(two.Settings(two.ScExe, two.FxExe), V("1.0.0"));
        string target = Path.Combine(two.Dir.Path, "Clink.exe");
        Check.Contains(Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(two.ScFile)).Message, "administrator");
        Check.Contains(Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(two.ScFile, target, Array.Empty<string>())).Message, "administrator");
        Check.Equal(0, preflights, "no start-up check when elevated");
        Check.False(File.Exists(two.ScFile + ".failed"), "the file is not at fault: no marker");
        two.AssertUntouched("elevated");

        // --apply-update itself: refuses, returns non-zero, leaves the target alone and starts nothing.
        File.WriteAllText(target, "installed exe");
        int code = Updater.RunApplyUpdate(new[] { "--apply-update", target, "0", "--show" });
        Check.Equal(Updater.ElevatedExitCode, code);
        Check.Equal(3, code);
        Check.NotEqual(0, code);
        Check.Equal("installed exe", File.ReadAllText(target));
        Check.False(File.Exists(target + ".new") || File.Exists(target + ".bak"), "nothing staged, nothing backed up");
    }

    [UnitTest]
    public static void Packaging_a_preflight_timeout_does_not_condemn_the_download()
    {
        using var two = new TwoFlavours();
        Updater.IsElevatedOverride = false;
        var settings = two.Settings(two.ScExe, null);
        Updater.PackagingOverride = Packaging.SelfContained;
        var up = new Updater(settings, V("1.0.0"));
        string target = Path.Combine(two.Dir.Path, "Clink.exe");
        TimeSpan? seen = null;
        Updater.PreflightOverride = (_, t) => { seen = t; return new(null, true, null); };

        var ex = Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(two.ScFile));
        Check.True(ex.Deferred, "a timeout defers");
        Check.Equal(Updater.PreflightTimeout, seen, "the interactive timeout");
        Check.False(File.Exists(two.ScFile + ".failed"), "no failed marker");
        Check.Equal(two.ScExe, File.ReadAllBytes(two.ScFile), "the download stays");
        Check.Equal("9.9.9", settings.PendingUpdateVersion, "and so does its record");

        // At start-up the same file gets the longer timeout; still nothing is marked, and the text for the user says it is retried.
        Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(two.ScFile, target, Array.Empty<string>()));
        Check.Equal(Updater.StartupPreflightTimeout, seen, "the start-up timeout");
        Check.True(Updater.StartupPreflightTimeout > Updater.PreflightTimeout);
        up.ReportInstallFailure(ex, V("9.9.9"), two.ScFile);
        Check.Contains(up.InstallProblem, "tried again");
        Check.False(File.Exists(two.ScFile + ".failed"), "reporting it writes no marker either");
        Check.False(File.Exists(target), "nothing was applied");

        // Retry: the next start still finds the download, and a preflight that finishes in time goes through.
        Check.True(up.TryGetPendingUpdate(out string again, out _) || up.InstallProblem != null, "still offered (or refused only because this runner cannot install)");
        Updater.PreflightOverride = (_, _) => new(0, false, null);
        Check.NotNull(up.PrepareApply(two.ScFile));
    }

    [UnitTest]
    public static void Packaging_user_messages_fit_a_balloon_and_do_not_repeat_themselves()
    {
        using var two = new TwoFlavours();
        Updater.IsElevatedOverride = false;
        var up = new Updater(two.Settings(two.ScExe, null), V("1.0.0"));
        var longVersion = V("10.20.30-dev.123");
        string long400 = new string('x', 400);
        up.ReportInstallFailure(new Updater.ApplyRefusedException(long400, long400), longVersion, two.ScFile);
        Check.True(up.InstallProblem!.Length <= 250, $"refused: {up.InstallProblem.Length} characters");
        up.ReportInstallFailure(new Updater.ApplyRefusedException(long400, long400, deferred: true), longVersion, two.ScFile);
        Check.True(up.InstallProblem!.Length <= 250, $"deferred: {up.InstallProblem.Length} characters");
        up.ReportInstallFailure(new InvalidOperationException(long400), longVersion, two.ScFile);
        Check.True(up.InstallProblem!.Length <= 250, $"other failure: {up.InstallProblem.Length} characters");
        up.ReportInstallFailure(new InvalidOperationException("blocked by Application Control policy"), longVersion, two.ScFile);
        Check.True(up.InstallProblem!.Length <= 250, $"Smart App Control: {up.InstallProblem.Length} characters");
        File.Delete(two.ScFile + ".failed");

        // A preflight failure at start-up: one sentence, not "could not start: the downloaded exe was not applied: …" and not a second marker.
        Updater.PreflightOverride = (_, _) => new(1, false, null);
        var ex = Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(two.ScFile, @"C:\x\Clink.exe", Array.Empty<string>()));
        string marker = File.ReadAllText(two.ScFile + ".failed");
        up.ReportInstallFailure(ex, V("9.9.9"), two.ScFile);
        Check.Equal(marker, File.ReadAllText(two.ScFile + ".failed"), "the marker LaunchApply wrote is not overwritten");
        Check.DoesNotContain(up.InstallProblem, "could not start");
        Check.DoesNotContain(up.InstallProblem, "was not applied");
        Check.Contains(up.InstallProblem, "start-up check failed");
        Check.True(up.InstallProblem!.Length <= 250);
    }

    [UnitTest]
    public static void Packaging_check_verb_decides_exit_code_and_line()
    {
        const string core = @"C:\dotnet\System.Private.CoreLib.dll", deps = @"C:\dotnet\Microsoft.NETCore.App.deps.json";
        var sc = PackagingInfo.Decide("", null, "SelfContained");
        var fx = PackagingInfo.Decide(core, deps, "Framework");
        var split = PackagingInfo.Decide("", null, "Framework");   // label and runtime disagree
        var rows = new (PackagingDecision D, string? Core, string? Deps, string? Expected, int Code, string Text)[]
        {
            (sc, "", null, null, 0, "packaging=SelfContained build=SelfContained runtime=SelfContained corelib=bundled fx_deps=absent fx_version=10.0.1 fx_dir=\"\" result=ok"),
            (sc, "", null, "SelfContained", 0, "result=ok"),
            (sc, "", null, "selfcontained", 0, "case does not matter"),
            (sc, "", null, "Framework", PackagingCheck.ExitUnexpected, "result=unexpected (wanted Framework)"),
            (fx, core, deps, "Framework", 0, "packaging=Framework build=Framework runtime=Framework corelib=on-disk fx_deps=present fx_version=10.0.1 fx_dir=\"C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App\\10.0.1\" result=ok"),
            (fx, core, deps, "SelfContained", PackagingCheck.ExitUnexpected, "result=unexpected (wanted SelfContained)"),
            (fx, core, deps, null, 0, "result=ok"),
            (split, "", null, null, PackagingCheck.ExitDisagreement, "result=disagreement"),
            (split, "", null, "SelfContained", PackagingCheck.ExitDisagreement, "a disagreement fails even when the expected value matches"),
            (sc, "", null, "Both", PackagingCheck.ExitBadArgument, "result=bad-argument"),
            (sc, "", null, "1", PackagingCheck.ExitBadArgument, "a number is no packaging"),
            (PackagingInfo.Decide("", null, null), "", null, null, 0, "build=none"),
        };
        foreach (var r in rows)
        {
            string dir = ReferenceEquals(r.D, fx) ? @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.1" : "";
            int code = PackagingCheck.Evaluate(r.D, r.Core, r.Deps, "10.0.1", dir, r.Expected, out string line);
            Check.Equal(r.Code, code, r.Text);
            Check.DoesNotContain(line, "\n", "one line");
            if (r.Text.Contains('=')) Check.Contains(line, r.Text);
        }
        Check.Equal("--packaging-check", PackagingCheck.Verb);
        // This very process: label and runtime agree (the same facts Packaging_of_this_process_matches_its_build_label states).
        Check.Equal(0, PackagingCheck.Evaluate(PackagingInfo.Current, PackagingInfo.RuntimeSignals().CoreLibLocation, PackagingInfo.RuntimeSignals().FxDepsFile, Environment.Version.ToString(), "", PackagingInfo.Current.Packaging.ToString(), out _));
    }

    [UnitTest]
    public static void Packaging_check_line_shows_runtime_version_and_quoted_dir_in_order()
    {
        var fx = PackagingInfo.Decide(@"C:\dotnet\System.Private.CoreLib.dll", @"C:\dotnet\x.deps.json", "Framework");
        PackagingCheck.Evaluate(fx, @"C:\dotnet\System.Private.CoreLib.dll", @"C:\dotnet\x.deps.json", "10.0.7", @"D:\My Programs\dotnet\shared\App\10.0.7", null, out string line);
        int deps = line.IndexOf("fx_deps=present", StringComparison.Ordinal), ver = line.IndexOf(" fx_version=10.0.7 ", StringComparison.Ordinal);
        int dir = line.IndexOf(" fx_dir=\"D:\\My Programs\\dotnet\\shared\\App\\10.0.7\" ", StringComparison.Ordinal), result = line.IndexOf(" result=ok", StringComparison.Ordinal);
        Check.True(deps >= 0 && deps < ver && ver < dir && dir < result, "fx_deps, fx_version, fx_dir, result in that order: " + line);
        PackagingCheck.Evaluate(PackagingInfo.Decide("", null, "SelfContained"), "", null, "10.0.7", null, null, out string bundled);
        Check.Contains(bundled, " fx_dir=\"\" result=ok");
    }

    [UnitTest]
    public static void Packaging_an_elevated_apply_update_writes_nothing()
    {
        using var dir = new TempDir();
        string settingsDir = dir.File("settings");
        Settings.DirOverride = settingsDir;
        Updater.IsElevatedOverride = true;
        string target = dir.File("Clink.exe");
        File.WriteAllText(target, "installed exe");
        string[] before = Directory.GetFileSystemEntries(dir.Path, "*", SearchOption.AllDirectories);
        Check.Equal(Updater.ElevatedExitCode, Updater.RunApplyUpdate(new[] { "--apply-update", target, "0", "--show" }));
        Check.False(Directory.Exists(settingsDir), "no log folder or file was created");
        Check.Equal("installed exe", File.ReadAllText(target), "target untouched");
        Check.Equal(before.Length, Directory.GetFileSystemEntries(dir.Path, "*", SearchOption.AllDirectories).Length, "nothing was written anywhere");
    }

    [UnitTest]
    public static void Packaging_prepare_apply_refuses_while_the_gate_is_held()
    {
        using var two = new TwoFlavours();
        Updater.IsElevatedOverride = false;
        int preflights = 0;
        Updater.PreflightOverride = (_, _) => { preflights++; return new(0, false, null); };
        var up = new Updater(two.Settings(two.ScExe, two.FxExe), V("1.0.0"));
        // A file that would be thrown away if it were looked at: the busy gate must not even verify it.
        File.WriteAllBytes(two.ScFile, FakeExe(77));
        up._gate.Wait();
        try
        {
            var ex = Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(two.ScFile));
            Check.True(ex.Deferred, "busy defers");
            Check.Contains(ex.Reason, "try again");
            Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(two.ScFile, @"C:\x\Clink.exe", Array.Empty<string>()));
            Check.Equal(0, preflights, "no start-up check");
            Check.True(File.Exists(two.ScFile), "nothing deleted");
            Check.False(File.Exists(two.ScFile + ".failed") || File.Exists(two.ScFile + ".timeout") || File.Exists(two.ScFile + ".installed"), "no marker");
        }
        finally { up._gate.Release(); }
        Check.Equal(1, up._gate.CurrentCount, "PrepareApply took nothing it did not give back");
        File.WriteAllBytes(two.ScFile, two.ScExe);
        Check.NotNull(up.PrepareApply(two.ScFile));
        Check.Equal(1, up._gate.CurrentCount, "and released the gate after a run");
    }

    [UnitTest]
    public static void Packaging_startup_preflight_timeouts_are_capped()
    {
        using var two = new TwoFlavours();
        Updater.IsElevatedOverride = false;
        Updater.PackagingOverride = Packaging.SelfContained;
        var up = new Updater(two.Settings(two.ScExe, null), V("1.0.0"));
        string target = Path.Combine(two.Dir.Path, "Clink.exe"), file = two.ScFile;
        int runs = 0;
        Updater.PreflightOverride = (_, _) => { runs++; return new(null, true, null); };

        // A click (PrepareApply) never counts: the user is there to try again.
        Check.True(Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(file)).Deferred);
        Check.False(File.Exists(file + ".timeout"));

        for (int i = 1; i < Updater.MaxStartupTimeouts; i++)
        {
            Check.True(Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(file, target, Array.Empty<string>())).Deferred, $"start {i} defers");
            Check.Equal(i.ToString(), File.ReadLines(file + ".timeout").First().Trim(), $"count after start {i}");
            Check.False(File.Exists(file + ".failed"), $"not failed after {i}");
        }
        var last = Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(file, target, Array.Empty<string>()));
        Check.False(last.Deferred, "the last one is final");
        Check.True(File.Exists(file + ".failed"), "marked failed at the cap");
        Check.Contains(File.ReadAllText(file + ".failed"), "did not finish");
        Check.DoesNotContain(File.ReadAllText(file + ".failed"), "was not applied");
        Check.False(File.Exists(file + ".timeout"), "the counter is gone");
        up.ReportInstallFailure(last, V("9.9.9"), file);
        Check.Contains(up.InstallProblem, "Check now");
        Check.True(up.InstallProblem!.Length <= 250);
        Check.False(up.TryGetPendingUpdate(out _, out _), "start-up stops auto-applying it");
        Check.True(Updater.MaxStartupTimeouts >= 3);

        // A passing check clears a stale counter.
        File.Delete(file + ".failed");
        File.WriteAllText(file + ".timeout", "2\n");
        Updater.PreflightOverride = (_, _) => new(0, false, null);
        Check.NotNull(up.PrepareApply(file));
        Check.False(File.Exists(file + ".timeout"), "a successful check resets the count");

        // An orphaned counter (its file is gone) is swept with the other markers.
        File.Delete(file);
        File.WriteAllText(file + ".timeout", "1\n");
        up.CleanupUpdatesFolder();
        Check.False(File.Exists(file + ".timeout"), "orphaned counter swept");
    }

    [UnitTest]
    public static void Packaging_a_fresh_download_resets_the_timeout_count()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Updater.IsElevatedOverride = false;
        byte[] exe = FakeExe(5);
        Updater.HttpOverride = (req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(req.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(Hex(exe)) : exe),
        });
        var up = new Updater(Settings.FromJson("""{"UpdateRepo":"owner/repo"}"""), V("1.0.0"));
        string file = Updater.FileFor(V("9.9.10"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, FakeExe(6));
        File.WriteAllText(file + ".timeout", "2\n");
        var info = new UpdateInfo(V("9.9.10"), "v9.9.10", "https://gh.example/Clink.exe", exe.Length, "https://gh.example/Clink.exe.sha256", "", "");
        string path = OffUi(() => up.DownloadAsync(info, null, CancellationToken.None));
        Check.Equal(file, path);
        Check.False(File.Exists(file + ".timeout"), "the new download starts from zero");
    }

    [UnitTest]
    public static void Packaging_default_roots_use_the_registered_install_location()
    {
        var roots = DotNetRuntimes.DefaultRoots(() => @"D:\Tools\dotnet");
        Check.Equal(@"D:\Tools\dotnet", roots[0], "the registered location comes first");
        Check.True(DotNetRuntimes.DefaultRoots(() => null).All(r => r != @"D:\Tools\dotnet"));
        Check.True(DotNetRuntimes.DefaultRoots(() => "").All(r => r.Length > 0), "an empty value is no root");
        Check.NotNull(DotNetRuntimes.DefaultRoots(() => throw new InvalidOperationException("boom")), "a throwing reader is survived");
        _ = DotNetRuntimes.ReadInstallLocation();   // the real read (32-bit view, read only) never throws
    }

    [UnitTest]
    public static void Packaging_update_now_rechecks_after_the_await()
    {
        var v = V("9.9.9"); var cur = V("1.0.0");
        const string p = @"C:\u\Clink-9.9.9.exe";
        Check.True(AppController.StillTheUpdateToInstall(false, p, p, v, cur, false), "unchanged: go on");
        Check.True(AppController.StillTheUpdateToInstall(false, p.ToUpperInvariant(), p, v, cur, false), "path case does not matter");
        Check.False(AppController.StillTheUpdateToInstall(true, p, p, v, cur, false), "quitting");
        Check.False(AppController.StillTheUpdateToInstall(false, null, p, v, cur, false), "pending dropped");
        Check.False(AppController.StillTheUpdateToInstall(false, @"C:\u\Clink-9.9.10.exe", p, v, cur, false), "a different download became pending");
        Check.False(AppController.StillTheUpdateToInstall(false, p, p, V("0.9.0"), cur, false), "no longer newer than the running exe");
    }

    [UnitTest]
    public static void Packaging_a_failed_preflight_is_remembered_like_any_failed_install()
    {
        using var two = new TwoFlavours();
        Updater.PackagingOverride = Packaging.SelfContained;
        var settings = two.Settings(two.ScExe, null);
        var up = new Updater(settings, V("1.0.0"));
        // What App.xaml.cs does when LaunchApply throws: the marker is there (LaunchApply wrote it), the next scan skips the file.
        Updater.IsElevatedOverride = false;
        Updater.PreflightOverride = (_, _) => new(1, false, null);
        var ex = Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(two.ScFile, @"C:\x\Clink.exe", Array.Empty<string>()));
        up.ReportInstallFailure(ex, V("9.9.9"), two.ScFile);
        Check.Contains(up.InstallProblem, "9.9.9");
        Check.False(up.TryGetPendingUpdate(out _, out _), "not offered again at every start");
        Check.Contains(up.InstallProblem, "could not be installed");
        Check.Equal(two.ScExe, File.ReadAllBytes(two.ScFile), "the download stays for a manual retry");
    }
}
