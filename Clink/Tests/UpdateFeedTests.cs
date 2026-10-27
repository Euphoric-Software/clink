using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Clink.Services;

namespace Clink.Tests;

// The signed static update feed (UpdateFeed) and the Updater's use of it with a fall back to GitHub. No network: the HTTP layer is a fake.
internal static class UpdateFeedTests
{
    private const string Base = "https://cdn.example/clink/";
    private static readonly string Sha = new('a', 64);

    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure($"'{text}' is not a version");

    private static string Manifest(string version = "0.4.2", string channel = "stable", int schema = 1, string? tag = null,
        string exeUrl = "https://cdn.example/clink/releases/v0.4.2/Clink.exe", string? sha = null, bool exe = true, string extra = "", string notes = "https://example.invalid/notes")
    {
        string assets = (exe ? $$"""
            "exe":{"url":"{{exeUrl}}","size":1234,"sha256":"{{sha ?? Sha}}"},
            """ : "") + """
            "installer":{"url":"https://cdn.example/clink/releases/v0.4.2/Clink-0.4.2-setup.exe","size":99,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
            """;
        return $$"""
            {"schema":{{schema}},"channel":"{{channel}}","version":"{{version}}","tag":"{{tag ?? "v" + version}}","published":"2026-10-26T00:00:00Z",
             "notesUrl":"{{notes}}","minRuntime":"10.0",{{extra}}"assets":{ {{assets}} } }
            """;
    }

    private static byte[] Bytes(string s) => new UTF8Encoding(false).GetBytes(s);

    private static (string Pub, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static string Sign(ECDsa key, byte[] manifest) => Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256));

    private static string? ParseProblem(string json, string channel = "stable")
    {
        var m = UpdateFeed.ParseManifest(Bytes(json), channel, out string? problem);
        if (m != null) Check.Null(problem);
        else Check.NotNull(problem, "a rejection says why");
        return m == null ? problem : null;
    }

    // ------------------------------------------------------------------ manifest

    [UnitTest]
    public static void Feed_valid_manifest_maps_to_update_info()
    {
        var m = UpdateFeed.ParseManifest(Bytes(Manifest()), "stable", out string? problem);
        Check.Null(problem);
        Check.NotNull(m);
        Check.Equal(V("0.4.2"), m!.Version);
        Check.Equal("v0.4.2", m.Tag);
        Check.Equal(1234L, m.Exe.Size);
        Check.NotNull(m.Installer);
        Check.Null(m.Portable);
        var info = UpdateFeed.ToUpdateInfo(m);
        Check.Equal("https://cdn.example/clink/releases/v0.4.2/Clink.exe", info.ExeUrl);
        Check.Equal(Sha, info.Sha256);
        Check.Equal("", info.ShaUrl);
        Check.Equal(1234L, info.ExeSize);
        Check.Equal("v0.4.2", info.Tag);
    }

    [UnitTest]
    public static void Feed_dev_manifest_accepts_dev_builds_and_releases()
    {
        Check.Null(ParseProblem(Manifest("0.4.3-dev.7", "dev"), "dev"));
        Check.Null(ParseProblem(Manifest("0.4.2", "dev"), "dev"));
        Check.Contains(ParseProblem(Manifest("0.4.3-beta.1", "dev"), "dev"), "not a dev build");
    }

    [UnitTest]
    public static void Feed_rejects_bad_manifests()
    {
        Check.Contains(ParseProblem(Manifest(schema: 2)), "schema");
        Check.Contains(ParseProblem(Manifest(channel: "dev")), "channel");
        Check.Contains(ParseProblem(Manifest("0.4.3-dev.7", "stable")), "pre-release");
        Check.Contains(ParseProblem(Manifest(exeUrl: "http://cdn.example/Clink.exe")), "https");
        Check.Contains(ParseProblem(Manifest(exeUrl: "ftp://cdn.example/Clink.exe")), "https");
        Check.Contains(ParseProblem(Manifest(exe: false)), "no exe asset");
        Check.Contains(ParseProblem(Manifest(sha: new string('A', 64))), "sha256");
        Check.Contains(ParseProblem(Manifest(sha: new string('a', 63))), "sha256");
        Check.Contains(ParseProblem(Manifest(sha: new string('g', 64))), "sha256");
        Check.Contains(ParseProblem(Manifest(tag: "v0.4.1")), "tag");
        Check.Contains(ParseProblem(Manifest("v0.4.2", tag: "vv0.4.2")), "version");
        Check.Contains(ParseProblem(Manifest(notes: "http://example.invalid/n")), "notesUrl");
        Check.Contains(ParseProblem("not json"), "JSON");
        Check.Contains(ParseProblem("[1]"), "object");
        Check.Contains(ParseProblem("""{"channel":"stable"}"""), "schema");
    }

    [UnitTest]
    public static void Feed_ignores_unknown_fields()
    {
        string json = Manifest(extra: """ "future":{"a":[1,2]}, "flag":true, """).Replace("\"installer\"", "\"mystery\":{\"x\":1},\"installer\"");
        Check.Null(ParseProblem(json));
    }

    // ------------------------------------------------------------------ signature

    [UnitTest]
    public static void Feed_signature_verifies_with_the_matching_key_only()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        byte[] manifest = Bytes(Manifest());
        string sig = Sign(key, manifest);
        Check.True(UpdateFeed.VerifySignature(manifest, sig, pub, out string? p), p);

        byte[] flipped = (byte[])manifest.Clone();
        flipped[10] ^= 0x01;
        Check.False(UpdateFeed.VerifySignature(flipped, sig, pub, out p), "one flipped byte");
        Check.NotNull(p);

        var (otherPub, otherKey) = NewKey();
        using var k2 = otherKey;
        Check.False(UpdateFeed.VerifySignature(manifest, sig, otherPub, out _), "wrong key");
        Check.False(UpdateFeed.VerifySignature(manifest, "", pub, out _), "no signature");
        Check.False(UpdateFeed.VerifySignature(manifest, "%%%not base64", pub, out _));
        Check.False(UpdateFeed.VerifySignature(manifest, Convert.ToBase64String(new byte[10]), pub, out _), "wrong length");
        Check.False(UpdateFeed.VerifySignature(manifest, sig, "garbage", out _), "bad public key");

        // A DER-encoded signature is not the contract's P1363 form.
        string der = Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Check.False(UpdateFeed.VerifySignature(manifest, der, pub, out _));
    }

    [UnitTest]
    public static void Feed_signature_tolerates_whitespace_and_newlines()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        byte[] manifest = Bytes(Manifest());
        string sig = Sign(key, manifest);
        Check.True(UpdateFeed.VerifySignature(manifest, sig + "\n", pub, out _));
        Check.True(UpdateFeed.VerifySignature(manifest, "  \r\n" + sig + "\r\n", pub, out _));
        Check.True(UpdateFeed.VerifySignature(manifest, sig[..40] + "\r\n" + sig[40..], pub, out _), "wrapped base64");
    }

    // ------------------------------------------------------------------ fetching and the fall back to GitHub

    /// <summary>A fake network: URL to (status, body); anything not listed is a 404. Records the URLs asked for.</summary>
    private sealed class FakeNet
    {
        public readonly Dictionary<string, (int Status, byte[] Body)> Routes = new();
        public readonly List<string> Asked = new();

        public void Feed(string channel, byte[] manifest, string sig)
        {
            Routes[Base + channel + ".json"] = (200, manifest);
            Routes[Base + channel + ".json.sig"] = (200, Bytes(sig));
        }

        public Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
        {
            string url = req.RequestUri!.ToString();
            Asked.Add(url);
            if (Routes.TryGetValue(url, out var r))
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)r.Status) { Content = new ByteArrayContent(r.Body) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });
        }

        public Task<byte[]> Fetch(string url, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = Send(req, ct).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)resp.StatusCode);
            return Task.FromResult(resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        }
    }

    [UnitTest]
    public static void Feed_fetch_verifies_before_parsing()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest());
        net.Feed("stable", manifest, Sign(key, manifest) + "\n");
        var (info, problem) = UpdateFeed.TryGetAsync(net.Fetch, Base, pub, dev: false, CancellationToken.None).GetAwaiter().GetResult();
        Check.Null(problem);
        Check.Equal(V("0.4.2"), info!.Version);
        Check.Equal(new[] { Base + "stable.json", Base + "stable.json.sig" }, net.Asked.ToArray());

        // The same bytes with a bad signature are not even parsed: a schema-2 manifest signed wrongly reports the signature.
        var net2 = new FakeNet();
        net2.Feed("stable", Bytes(Manifest(schema: 2)), Sign(key, manifest));
        (info, problem) = UpdateFeed.TryGetAsync(net2.Fetch, Base, pub, false, CancellationToken.None).GetAwaiter().GetResult();
        Check.Null(info);
        Check.Contains(problem, "signature");
    }

    private const string GitHubLatest = "https://api.github.com/repos/o/r/releases/latest";

    private static string GitHubRelease(string tag) =>
        $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false,"html_url":"https://example.invalid/{{tag}}","body":"","assets":[{"name":"Clink.exe","size":1000,"browser_download_url":"https://example.invalid/Clink.exe"},{"name":"Clink.exe.sha256","size":64,"browser_download_url":"https://example.invalid/Clink.exe.sha256"}]}""";

    /// <summary>One Updater.CheckAsync (0.4.1 on the stable channel unless told) against the fake network; always restores the static test hooks.</summary>
    private static (UpdateInfo? Info, FakeNet Net) Check_(FakeNet net, string pub, bool dev = false, string running = "0.4.1", bool withFeed = true)
    {
        var settings = new Settings { UpdateRepo = "o/r", UseDevBuilds = dev };
        net.Routes[GitHubLatest] = (200, Bytes(GitHubRelease("v0.4.9")));
        net.Routes["https://api.github.com/repos/o/r/releases?per_page=30"] = (200, Bytes("[" + GitHubRelease("v0.4.9") + "]"));
        try
        {
            Updater.HttpOverride = net.Send;
            Updater.FeedOverride = withFeed ? (Base, pub) : null;
            using var updater = new Updater(settings, V(running));
            var info = updater.CheckAsync(CancellationToken.None, manual: false).GetAwaiter().GetResult();
            return (info, net);
        }
        finally { Updater.HttpOverride = null; Updater.FeedOverride = null; }
    }

    [UnitTest]
    public static void Feed_updater_uses_a_verified_feed_and_skips_github()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest());
        net.Feed("stable", manifest, Sign(key, manifest));
        var (info, _) = Check_(net, pub);
        Check.NotNull(info);
        Check.Equal(V("0.4.2"), info!.Version, "the feed's version, not GitHub's 0.4.9");
        Check.Equal(Sha, info.Sha256);
        Check.False(net.Asked.Any(u => u.Contains("api.github.com")), "GitHub is not asked");
    }

    [UnitTest]
    public static void Feed_updater_falls_back_to_github_on_every_failure_class()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        var (_, wrongKey) = NewKey();
        using var k2 = wrongKey;
        byte[] good = Bytes(Manifest());

        var cases = new List<(string Name, Action<FakeNet> Setup, bool Dev, string Running)>
        {
            ("404", n => { }, false, "0.4.1"),
            ("network error", n => n.Routes[Base + "stable.json"] = (500, Bytes("boom")), false, "0.4.1"),
            ("missing signature", n => n.Routes[Base + "stable.json"] = (200, good), false, "0.4.1"),
            ("bad signature", n => n.Feed("stable", good, Sign(wrongKey, good)), false, "0.4.1"),
            ("flipped byte", n => { var b = (byte[])good.Clone(); b[12] ^= 1; n.Feed("stable", b, Sign(key, good)); }, false, "0.4.1"),
            ("unparseable", n => { var b = Bytes("{ nope"); n.Feed("stable", b, Sign(key, b)); }, false, "0.4.1"),
            ("schema 2", n => { var b = Bytes(Manifest(schema: 2)); n.Feed("stable", b, Sign(key, b)); }, false, "0.4.1"),
            ("wrong channel", n => { var b = Bytes(Manifest(channel: "dev")); n.Feed("stable", b, Sign(key, b)); }, false, "0.4.1"),
            ("dev version on stable", n => { var b = Bytes(Manifest("0.4.3-dev.7")); n.Feed("stable", b, Sign(key, b)); }, false, "0.4.1"),
            ("non-https asset", n => { var b = Bytes(Manifest(exeUrl: "http://cdn.example/Clink.exe")); n.Feed("stable", b, Sign(key, b)); }, false, "0.4.1"),
            ("no exe asset", n => { var b = Bytes(Manifest(exe: false)); n.Feed("stable", b, Sign(key, b)); }, false, "0.4.1"),
            ("bad sha format", n => { var b = Bytes(Manifest(sha: "XYZ")); n.Feed("stable", b, Sign(key, b)); }, false, "0.4.1"),
            ("version not newer", n => n.Feed("stable", good, Sign(key, good)), false, "0.4.2"),
            ("dev channel, wrong channel file", n => { var b = Bytes(Manifest()); n.Feed("dev", b, Sign(key, b)); }, true, "0.4.1"),
        };
        foreach (var (name, setup, dev, running) in cases)
        {
            var net = new FakeNet();
            setup(net);
            var (info, _) = Check_(net, pub, dev, running);
            Check.NotNull(info, name + ": GitHub answered");
            Check.Equal(V("0.4.9"), info!.Version, name + ": GitHub's release");
            Check.Null(info.Sha256);
            Check.Equal("https://example.invalid/Clink.exe.sha256", info.ShaUrl, name);
            Check.True(net.Asked.Any(u => u.Contains("api.github.com")), name + ": GitHub was asked");
        }
    }

    [UnitTest]
    public static void Feed_updater_ignores_the_feed_unless_both_settings_exist()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest());
        net.Feed("stable", manifest, Sign(key, manifest));
        var (info, _) = Check_(net, pub, withFeed: false);
        Check.Equal(V("0.4.9"), info!.Version);
        Check.False(net.Asked.Any(u => u.StartsWith(Base)), "the feed is not contacted");

        Updater.FeedOverride = (Base, "  ");
        try { Check.Null(Updater.FeedConfig, "empty key"); }
        finally { Updater.FeedOverride = null; }
        Updater.FeedOverride = ("", pub);
        try { Check.Null(Updater.FeedConfig, "empty url"); }
        finally { Updater.FeedOverride = null; }
        Updater.FeedOverride = ("http://cdn.example/clink/", pub);
        try { Check.Null(Updater.FeedConfig, "non-https feed"); }
        finally { Updater.FeedOverride = null; }
        Updater.FeedOverride = ("https://cdn.example/clink", pub);
        try { Check.Equal(Base, Updater.FeedConfig!.Value.BaseUrl, "a trailing slash is added"); }
        finally { Updater.FeedOverride = null; }
    }
}
