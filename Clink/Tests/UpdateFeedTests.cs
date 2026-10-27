using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Clink.Services;

namespace Clink.Tests;

// The signed static update feed (UpdateFeed, FeedTrust) and the Updater's decision table around it: feed, signed GitHub path,
// today's unsigned GitHub path when the feed is off. No network: the HTTP layer is a fake (Updater.HttpOverride).
internal static class UpdateFeedTests
{
    private const string Base = "https://cdn.example/clink/";
    private const string Gh = "https://gh.example/";
    private const string BuiltIn = "o/r";
    private const string GitHubLatest = "https://api.github.com/repos/o/r/releases/latest";
    private const string GitHubList = "https://api.github.com/repos/o/r/releases?per_page=30";
    private static readonly string Sha = new('a', 64);

    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure($"'{text}' is not a version");

    private static string Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Manifest(string version = "0.4.2", string channel = "stable", int schema = 1, string? tag = null,
        string? exeUrl = null, string? sha = null, long size = 1234, bool exe = true, string extra = "", string notes = "https://example.invalid/notes",
        int publishedDaysAgo = 1, bool published = true)
    {
        exeUrl ??= $"https://cdn.example/clink/releases/v{version}/Clink.exe";
        string assets = (exe ? $$"""
            "exe":{"url":"{{exeUrl}}","size":{{size}},"sha256":"{{sha ?? Sha}}"},
            """ : "") + """
            "installer":{"url":"https://cdn.example/clink/releases/Clink-setup.exe","size":99,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
            """;
        string pub = published ? $"\"published\":\"{Ago(publishedDaysAgo)}\"," : "";
        return $$"""
            {"schema":{{schema}},"channel":"{{channel}}","version":"{{version}}","tag":"{{tag ?? "v" + version}}",{{pub}}
             "notesUrl":"{{notes}}","minRuntime":"10.0",{{extra}}"assets":{ {{assets}} } }
            """;
    }

    private static byte[] Bytes(string s) => new UTF8Encoding(false).GetBytes(s);

    /// <summary>Runs an updater call on the thread pool, as the app does: the tests run on the UI thread, where a continuation that is not ConfigureAwait(false) (DownloadAsync's file streams) would wait for the blocked dispatcher.</summary>
    private static T OffUi<T>(Func<Task<T>> run) => Task.Run(run).GetAwaiter().GetResult();

    private static (string Pub, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static string Sign(ECDsa key, byte[] manifest) => Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256));

    private static byte[] Der(string pub) => Convert.FromBase64String(pub);

    private static readonly Lazy<string> s_spareStableKey = new(SparePub), s_spareDevKey = new(SparePub);

    private static string SparePub()
    {
        var (pub, key) = NewKey();
        key.Dispose();
        return pub;
    }

    /// <summary>
    /// A trust that is On (both channels need keys): the list not given gets a spare key of its own, which signs nothing. A test of a
    /// broken feed.json uses <see cref="FeedTrust.Create"/> or <see cref="FeedTrust.Parse"/> directly.
    /// </summary>
    private static FeedTrust Trust(string[]? stable = null, string[]? dev = null, string? url = Base)
    {
        var t = FeedTrust.Create(url, stable ?? new[] { s_spareStableKey.Value }, dev ?? new[] { s_spareDevKey.Value });
        if (!t.On) throw new TestFailure("the test's trust is not on: " + string.Join("; ", t.Problems));
        return t;
    }

    private static string? ParseProblem(string json, string channel = "stable")
    {
        var m = UpdateFeed.ParseManifest(Bytes(json), channel, out string? problem);
        if (m != null) Check.Null(problem);
        else Check.NotNull(problem, "a rejection says why");
        return m == null ? problem : null;
    }

    // ------------------------------------------------------------------ a fake network

    /// <summary>URL to response; anything not listed is a 404. Records every URL asked for.</summary>
    private sealed class FakeNet
    {
        public readonly Dictionary<string, Func<CancellationToken, HttpResponseMessage>> Routes = new();
        /// <summary>Answers that take their time (a stalled server); looked up before <see cref="Routes"/>.</summary>
        public readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> RoutesAsync = new();
        public readonly List<string> Asked = new();

        public void Ok(string url, byte[] body) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        public void Ok(string url, Func<HttpContent> content) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content() };
        public void Status(string url, int code) => Routes[url] = _ => new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("error") };
        public void Throw(string url, Func<Exception> ex) => Routes[url] = _ => throw ex();

        public void Feed(string channel, byte[] manifest, string sig)
        {
            Ok(Base + channel + ".json", manifest);
            Ok(Base + channel + ".json.sig", Bytes(sig));
        }

        public Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
        {
            string url = req.RequestUri!.AbsoluteUri;
            lock (Asked) Asked.Add(url);
            ct.ThrowIfCancellationRequested();
            if (RoutesAsync.TryGetValue(url, out var slow)) return slow(ct);
            if (Routes.TryGetValue(url, out var r)) return Task.FromResult(r(ct));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });
        }

        public bool AskedGitHub => Asked.Any(u => u.StartsWith("https://api.github.com/", StringComparison.Ordinal));
        public bool AskedFeed => Asked.Any(u => u.StartsWith(Base, StringComparison.Ordinal));
        public bool AskedSha256File => Asked.Any(u => u.EndsWith(".sha256", StringComparison.Ordinal));
    }

    /// <summary>A GitHub release object: Clink.exe (and Clink.exe.sha256), plus the signed &lt;channel&gt;.json / .sig assets unless told otherwise.</summary>
    private static string Release(string tag, bool prerelease = false, bool signedAssets = true, bool shaAsset = true, long exeSize = 1234)
    {
        string channel = prerelease ? "dev" : "stable";
        var assets = new List<string> { $$"""{"name":"Clink.exe","size":{{exeSize}},"browser_download_url":"{{Gh}}{{tag}}/Clink.exe"}""" };
        if (shaAsset) assets.Add($$"""{"name":"Clink.exe.sha256","size":64,"browser_download_url":"{{Gh}}{{tag}}/Clink.exe.sha256"}""");
        if (signedAssets)
        {
            assets.Add($$"""{"name":"{{channel}}.json","size":500,"browser_download_url":"{{Gh}}{{tag}}/{{channel}}.json"}""");
            assets.Add($$"""{"name":"{{channel}}.json.sig","size":88,"browser_download_url":"{{Gh}}{{tag}}/{{channel}}.json.sig"}""");
        }
        return $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://github.com/o/r/releases/tag/{{tag}}","body":"","assets":[{{string.Join(",", assets)}}]}""";
    }

    /// <summary>GitHub answers with <paramref name="release"/> on both the latest and the list endpoint.</summary>
    private static void GitHub(FakeNet net, params string[] releases)
    {
        net.Ok(GitHubLatest, Bytes(releases[0]));
        net.Ok(GitHubList, Bytes("[" + string.Join(",", releases) + "]"));
    }

    /// <summary>The signed manifest assets of the GitHub release <paramref name="tag"/>.</summary>
    private static void GitHubManifest(FakeNet net, string tag, byte[] manifest, string sig, string channel = "stable")
    {
        net.Ok($"{Gh}{tag}/{channel}.json", manifest);
        net.Ok($"{Gh}{tag}/{channel}.json.sig", Bytes(sig));
    }

    private sealed record Result(UpdateInfo? Info, Updater Up, Settings Settings);

    /// <summary>One Updater.CheckAsync (0.4.1 on the stable channel unless told) against the fake network; the runner resets the hooks after the test.</summary>
    private static Result Run(FakeNet net, FeedTrust trust, bool dev = false, string running = "0.4.1", bool manual = false, Settings? settings = null, CancellationToken ct = default)
    {
        settings ??= new Settings();
        settings.UseDevBuilds = dev;
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = trust;
        Updater.BuiltInRepoOverride = BuiltIn;
        var up = new Updater(settings, V(running));
        var info = OffUi(() => up.CheckAsync(ct, manual));
        return new Result(info, up, settings);
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
        Check.Null(m.ExeFramework);
        Check.True(DateTimeOffset.UtcNow - m.Published < TimeSpan.FromDays(2), "published is read");
        var info = UpdateFeed.ToUpdateInfo(m);
        Check.Equal("https://cdn.example/clink/releases/v0.4.2/Clink.exe", info.ExeUrl);
        Check.Equal(Sha, info.Sha256);
        Check.Equal("", info.ShaUrl);
        Check.Equal(1234L, info.ExeSize);
        Check.Equal("v0.4.2", info.Tag);
    }

    [UnitTest]
    public static void Feed_dev_manifest_must_carry_a_dev_build()
    {
        Check.Null(ParseProblem(Manifest("0.4.3-dev.7", "dev"), "dev"));
        Check.Contains(ParseProblem(Manifest("0.4.2", "dev"), "dev"), "not a dev build", "a plain release belongs in stable.json");
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
        Check.Contains(ParseProblem(Manifest(exeUrl: "https://user:pw@cdn.example/Clink.exe")), "https");
        Check.Contains(ParseProblem(Manifest(exeUrl: "not a url")), "https");
        Check.Contains(ParseProblem(Manifest(exe: false)), "no exe asset");
        Check.Contains(ParseProblem(Manifest(sha: new string('A', 64))), "sha256");
        Check.Contains(ParseProblem(Manifest(sha: new string('a', 63))), "sha256");
        Check.Contains(ParseProblem(Manifest(sha: new string('g', 64))), "sha256");
        Check.Contains(ParseProblem(Manifest(size: 0)), "size");
        Check.Contains(ParseProblem(Manifest(size: -5)), "size");
        Check.Contains(ParseProblem(Manifest(tag: "v0.4.1")), "tag");
        Check.Contains(ParseProblem(Manifest("v0.4.2", tag: "vv0.4.2")), "version");
        Check.Contains(ParseProblem(Manifest(notes: "http://example.invalid/n")), "notesUrl");
        Check.Contains(ParseProblem(Manifest(published: false)), "published");
        Check.Contains(ParseProblem(Manifest().Replace(Ago(1), "yesterday")), "published");
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

    [UnitTest]
    public static void Feed_stale_reason_uses_the_floor_and_the_age()
    {
        var m = UpdateFeed.ParseManifest(Bytes(Manifest("0.4.2")), "stable", out _)!;
        var now = DateTimeOffset.UtcNow;
        Check.Null(UpdateFeed.StaleReason(m, null, now));
        Check.Null(UpdateFeed.StaleReason(m, V("0.4.2"), now), "equal to the floor is fine");
        Check.NotNull(UpdateFeed.StaleReason(m, V("0.4.3"), now), "below the floor");
        Check.NotNull(UpdateFeed.StaleReason(m, null, now.AddDays(90)), "91 days after publishing");
        Check.Null(UpdateFeed.StaleReason(m, null, now.AddDays(88)));

        // published in the future: up to a day is clock skew, more is stale
        var future = UpdateFeed.ParseManifest(Bytes(Manifest("0.4.2", publishedDaysAgo: -3)), "stable", out _)!;
        Check.Contains(UpdateFeed.StaleReason(future, null, now), "future");
        Check.Null(UpdateFeed.StaleReason(future, null, now.AddDays(2.5)), "half a day ahead is fine");
        Check.NotNull(UpdateFeed.StaleReason(future, null, now.AddDays(1.5)), "a day and a half ahead");
        Check.Null(UpdateFeed.FloorReason(future, null));
        Check.NotNull(UpdateFeed.FloorReason(m, V("0.4.3")));
        Check.Null(UpdateFeed.FloorReason(m, V("0.4.2")));
    }

    // ------------------------------------------------------------------ signature

    [UnitTest]
    public static void Feed_signature_verifies_with_a_trusted_key_only()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        byte[] manifest = Bytes(Manifest());
        string sig = Sign(key, manifest);
        var keys = new[] { Der(pub) };
        Check.True(UpdateFeed.VerifyAny(manifest, sig, keys, out string? p), p);

        byte[] flipped = (byte[])manifest.Clone();
        flipped[10] ^= 0x01;
        Check.False(UpdateFeed.VerifyAny(flipped, sig, keys, out p), "one flipped byte");
        Check.NotNull(p);

        var (otherPub, otherKey) = NewKey();
        using var k2 = otherKey;
        Check.False(UpdateFeed.VerifyAny(manifest, sig, new[] { Der(otherPub) }, out _), "wrong key");
        Check.False(UpdateFeed.VerifyAny(manifest, sig, Array.Empty<byte[]>(), out p), "no key");
        Check.Contains(p, "no trusted key");
        Check.False(UpdateFeed.VerifyAny(manifest, "", keys, out _), "no signature");
        Check.False(UpdateFeed.VerifyAny(manifest, "%%%not base64", keys, out _));
        Check.False(UpdateFeed.VerifyAny(manifest, Convert.ToBase64String(new byte[10]), keys, out _), "wrong length");

        // A DER-encoded signature is not the contract's P1363 form.
        string der = Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Check.False(UpdateFeed.VerifyAny(manifest, der, keys, out _));
    }

    [UnitTest]
    public static void Feed_signature_tolerates_whitespace_newlines_and_a_bom()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        byte[] manifest = Bytes(Manifest());
        string sig = Sign(key, manifest);
        var keys = new[] { Der(pub) };
        Check.True(UpdateFeed.VerifyAny(manifest, sig + "\n", keys, out _));
        Check.True(UpdateFeed.VerifyAny(manifest, "  \r\n" + sig + "\r\n", keys, out _));
        Check.True(UpdateFeed.VerifyAny(manifest, sig[..40] + "\r\n" + sig[40..], keys, out _), "wrapped base64");
        Check.True(UpdateFeed.VerifyAny(manifest, "﻿" + sig + "\t\n", keys, out _), "BOM and a tab");
    }

    [UnitTest]
    public static void Feed_key_rotation_any_listed_key_verifies()
    {
        var (pub1, key1) = NewKey();
        var (pub2, key2) = NewKey();
        using var a = key1;
        using var b = key2;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest());
        net.Feed("stable", manifest, Sign(key2, manifest));   // signed with the second key of {k1, k2}
        var r = Run(net, Trust(stable: new[] { pub1, pub2 }));
        Check.NotNull(r.Info);
        Check.Equal(V("0.4.2"), r.Info!.Version);
        Check.False(net.AskedGitHub);
    }

    // ------------------------------------------------------------------ trust anchors (feed.json)

    private static string BrainpoolLookalike()
    {
        // A 256-bit EC key whose curve is brainpoolP256r1, not P-256: the key size alone would not tell.
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = k.ExportParameters(false).Q;
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(FeedTrust.EcPublicKeyOid);
                w.WriteObjectIdentifier("1.3.36.3.3.2.8.1.1.7");
            }
            w.WriteBitString(new byte[] { 0x04 }.Concat(q.X!).Concat(q.Y!).ToArray());
        }
        return Convert.ToBase64String(w.Encode());
    }

    [UnitTest]
    public static void Feed_trust_takes_only_valid_urls()
    {
        Check.Equal(Base, FeedTrust.ValidFeedUrl(Base, out _));
        Check.Equal(Base, FeedTrust.ValidFeedUrl("  " + Base + " ", out _), "surrounding blanks");
        foreach (string bad in new[] { "", "http://cdn.example/clink/", "https://cdn.example/clink", "https://cdn.example/clink/?x=1", "https://cdn.example/clink/#f",
                     "https://cdn.example/clink/?", "https://u:p@cdn.example/clink/", "ftp://cdn.example/clink/", "/clink/", "cdn.example/clink/", "https:///clink/" })
            Check.Null(FeedTrust.ValidFeedUrl(bad, out string? why), $"'{bad}' ({why})");
    }

    [UnitTest]
    public static void Feed_trust_ignores_invalid_and_non_p256_keys()
    {
        var (pub, key) = NewKey();
        using var k = key;
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        string p384Pub = Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo());
        string rsaPub = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        string brainpool = BrainpoolLookalike();

        Check.NotNull(FeedTrust.ParseP256Key(pub, out _));
        Check.Null(FeedTrust.ParseP256Key(p384Pub, out string? why), "P-384");
        Check.Contains(why, "not P-256");
        Check.Null(FeedTrust.ParseP256Key(brainpool, out why), "brainpoolP256r1 is 256 bits but not P-256");
        Check.Contains(why, "not P-256");
        Check.Null(FeedTrust.ParseP256Key(rsaPub, out why), "RSA");
        Check.Contains(why, "not an EC public key");
        Check.Null(FeedTrust.ParseP256Key("not base64!", out _));
        Check.Null(FeedTrust.ParseP256Key(Convert.ToBase64String(new byte[] { 1, 2, 3 }), out _));
        Check.Null(FeedTrust.ParseP256Key(Convert.ToBase64String(Der(pub).Concat(new byte[] { 0 }).ToArray()), out _), "trailing bytes");

        // One invalid key among valid ones is not "ignored": the whole configuration is invalid (fail closed).
        string dev = SparePub();
        var trust = FeedTrust.Create(Base, new[] { p384Pub, pub, brainpool, rsaPub, "garbage" }, new[] { dev });
        Check.Equal(FeedState.Misconfigured, trust.State);
        Check.Equal(0, trust.StableKeys.Count, "no key of a broken configuration is usable");
        Check.Equal(0, trust.DevKeys.Count);
        Check.Null(trust.FeedUrl);
        Check.Equal(4, trust.Problems.Count);
        string line = trust.Describe();
        Check.Contains(line, "update configuration invalid");
        Check.Contains(line, "stableKeys 1 valid of 5");
        foreach (string text in new[] { pub, p384Pub, rsaPub, brainpool, dev }) Check.DoesNotContain(line, text[..24], "never key bytes in the log line");

        var good = FeedTrust.Create(Base, new[] { pub }, new[] { dev });
        Check.Equal(FeedState.On, good.State);
        Check.Equal(1, good.StableKeys.Count);
        Check.Equal(Base, good.FeedUrl);
        Check.Contains(good.Describe(), "on for both channels");
        foreach (string text in new[] { pub, dev }) Check.DoesNotContain(good.Describe(), text[..24]);
    }

    [UnitTest]
    public static void Feed_trust_is_all_or_nothing()
    {
        var (pub, key) = NewKey();
        using var k = key;
        string dev = SparePub(), dev2 = SparePub();
        var cases = new List<(string Name, string? Url, string?[]? Stable, string?[]? Dev, string Expect)>
        {
            ("invalid feedUrl (http)", "http://cdn.example/clink/", new[] { pub }, new[] { dev }, "feedUrl is invalid"),
            ("invalid feedUrl (no slash)", "https://cdn.example/clink", new[] { pub }, new[] { dev }, "feedUrl is invalid"),
            ("keys without a feedUrl", "", new[] { pub }, new[] { dev }, "feedUrl is empty"),
            ("a feedUrl without keys", Base, null, null, "stableKeys is empty"),
            ("stable keys only", Base, new[] { pub }, null, "devKeys is empty"),
            ("dev keys only", Base, null, new[] { dev }, "stableKeys is empty"),
            ("one dev key invalid", Base, new[] { pub }, new[] { dev, "garbage" }, "devKeys[1] is invalid"),
            ("an empty entry", Base, new[] { pub }, new[] { dev, "" }, "devKeys[1] is invalid"),
            ("the same key on both channels", Base, new[] { pub }, new[] { dev2, pub }, "same key"),
            ("the same key, re-encoded", Base, new[] { pub }, new[] { " " + pub + " " }, "same key"),
        };
        foreach (var (name, url, stable, devKeys, expect) in cases)
        {
            var t = FeedTrust.Create(url, stable, devKeys);
            Check.Equal(FeedState.Misconfigured, t.State, name);
            Check.True(t.Misconfigured && !t.On, name);
            Check.Contains(string.Join("; ", t.Problems), expect, name);
            Check.Null(t.FeedUrl, name);
            Check.Equal(0, t.KeysFor("stable").Count + t.KeysFor("dev").Count, name + ": nothing usable");
        }
        Check.Equal(FeedState.Off, FeedTrust.Create("", null, null).State, "fully empty: off");
        Check.Equal(FeedState.Off, FeedTrust.Create("  ", Array.Empty<string>(), Array.Empty<string>()).State, "blank: off");
        Check.Equal(0, FeedTrust.Create(null, null, null).Problems.Count);
        Check.Equal(FeedState.Off, FeedTrust.None.State);
        Check.Equal(FeedState.On, FeedTrust.Create(Base, new[] { pub, dev2 }, new[] { dev }).State, "rotation: several keys per channel");
    }

    [UnitTest]
    public static void Feed_trust_reads_feed_json()
    {
        var (pub, key) = NewKey();
        using var k = key;
        string dev = SparePub();
        var empty = FeedTrust.Parse(Bytes("""{"schema":1,"feedUrl":"","stableKeys":[],"devKeys":[]}"""));
        Check.Equal(FeedState.Off, empty.State);
        Check.Equal(0, empty.Problems.Count, "the shipped empty file is not a problem");
        Check.Contains(empty.Describe(), "GitHub only");
        Check.Equal(FeedState.Off, FeedTrust.Parse(Bytes("""{"schema":1}""")).State, "absent fields are empty");

        var full = FeedTrust.Parse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes($$"""{"schema":1,"feedUrl":"{{Base}}","stableKeys":["{{pub}}"],"devKeys":["{{dev}}"]}""")).ToArray());
        Check.Equal(FeedState.On, full.State, "a BOM is fine");
        Check.Equal(Base, full.FeedUrl);
        Check.Equal(1, full.DevKeys.Count);

        foreach (var (name, json) in new[]
                 {
                     ("a non-string key entry", $$"""{"schema":1,"feedUrl":"{{Base}}","stableKeys":["{{pub}}"],"devKeys":[42,"{{dev}}"]}"""),
                     ("unknown schema", $$"""{"schema":2,"feedUrl":"{{Base}}","stableKeys":["{{pub}}"],"devKeys":["{{dev}}"]}"""),
                     ("no schema", """{"feedUrl":"","stableKeys":[],"devKeys":[]}"""),
                     ("keys not a list", $$"""{"schema":1,"feedUrl":"{{Base}}","stableKeys":"{{pub}}","devKeys":["{{dev}}"]}"""),
                     ("feedUrl not a string", """{"schema":1,"feedUrl":5,"stableKeys":[],"devKeys":[]}"""),
                     ("not json", "not json"),
                     ("not an object", "[1]"),
                 })
        {
            var t = FeedTrust.Parse(Bytes(json));
            Check.Equal(FeedState.Misconfigured, t.State, name);
            Check.NotEqual(0, t.Problems.Count, name);
        }
    }

    [UnitTest]
    public static void Embedded_feed_json_has_no_problems()
    {
        // The resource is embedded in this exe and, whatever CI wrote into it, it is a valid configuration (empty or fully on).
        var embedded = FeedTrust.LoadEmbedded();
        Check.Equal(Array.Empty<string>(), embedded.Problems.ToArray(), "problems of the embedded feed.json");
        Check.NotEqual(FeedState.Misconfigured, embedded.State);
    }

    /// <summary>
    /// CI runs this one test against the built exe with CLINK_EXPECT_FEED=off (a build without a feed) or =on (a release build whose
    /// feed.json was filled in): the embedded trust must be exactly that. Unset: only that it has no problems.
    /// </summary>
    [UnitTest]
    public static void Embedded_feed_trust_matches_ci_expectation()
    {
        var embedded = FeedTrust.LoadEmbedded();
        Check.Equal(Array.Empty<string>(), embedded.Problems.ToArray(), "problems of the embedded feed.json");
        string? expect = Environment.GetEnvironmentVariable("CLINK_EXPECT_FEED")?.Trim();
        if (string.IsNullOrEmpty(expect)) return;
        var want = expect.ToLowerInvariant() switch
        {
            "off" => FeedState.Off,
            "on" => FeedState.On,
            _ => throw new TestFailure($"CLINK_EXPECT_FEED must be \"off\" or \"on\", not \"{expect}\""),
        };
        Check.Equal(want, embedded.State, $"CLINK_EXPECT_FEED={expect}: {embedded.Describe()}");
    }

    // ------------------------------------------------------------------ feed off: today's behaviour

    [UnitTest]
    public static void Feed_off_with_the_empty_feed_json_is_todays_github_path()
    {
        var empty = FeedTrust.Parse(Bytes("""{"schema":1,"feedUrl":"","stableKeys":[],"devKeys":[]}"""));
        var net = new FakeNet();
        GitHub(net, Release("v0.4.9", signedAssets: false));
        var r = Run(net, empty);
        Check.NotNull(r.Info);
        Check.Equal(V("0.4.9"), r.Info!.Version);
        Check.Null(r.Info.Sha256, "the hash comes from Clink.exe.sha256, as before");
        Check.Equal(Gh + "v0.4.9/Clink.exe.sha256", r.Info.ShaUrl);
        Check.Equal(new[] { GitHubLatest }, net.Asked.ToArray(), "one GitHub request, no feed");
        Check.Null(r.Settings.FeedFloorStable);

        // Still refused without Clink.exe.sha256 (fail closed, unchanged).
        var net2 = new FakeNet();
        GitHub(net2, Release("v0.4.9", shaAsset: false));
        var r2 = Run(net2, empty);
        Check.Null(r2.Info);
        Check.Contains(r2.Up.LastError, "sha256");

        // settings.json's UpdateRepo works as before.
        var net3 = new FakeNet();
        net3.Ok("https://api.github.com/repos/me/fork/releases/latest", Bytes(Release("v0.4.9", signedAssets: false)));
        var r3 = Run(net3, empty, settings: new Settings { UpdateRepo = "me/fork" });
        Check.Equal(V("0.4.9"), r3.Info!.Version);
        Check.Equal("me/fork", r3.Up.Repo);
        Check.True(r3.Up.RepoOverridden);
    }

    // ------------------------------------------------------------------ feed.json broken: fail closed

    [UnitTest]
    public static void Feed_misconfigured_means_no_checks_and_no_installs()
    {
        var (pub, key) = NewKey();
        using var k = key;
        string dev = SparePub();
        var broken = new List<(string Name, FeedTrust Trust)>
        {
            ("keys with an invalid URL", FeedTrust.Create("http://cdn.example/clink/", new[] { pub }, new[] { dev })),
            ("one invalid key", FeedTrust.Create(Base, new[] { pub, "garbage" }, new[] { dev })),
            ("stable keys only", FeedTrust.Create(Base, new[] { pub }, null)),
            ("the same key on both channels", FeedTrust.Create(Base, new[] { pub }, new[] { pub })),
            ("unreadable feed.json", FeedTrust.Parse(Bytes("{ nope"))),
        };
        foreach (var (name, trust) in broken)
        {
            using var dir = new TempDir();
            Updater.UpdatesDirOverride = dir.File("updates");
            Directory.CreateDirectory(Updater.UpdatesDir);
            byte[] exe = FakeExe();
            File.WriteAllBytes(Updater.FileFor(V("0.4.9")), exe);   // a download recorded as verified and signed: still not installed
            var settings = new Settings
            {
                UpdateRepo = "me/fork", PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateSigned = true, PendingUpdateFrom = "0.4.1",
            };
            var net = new FakeNet();
            GitHub(net, Release("v0.4.9", signedAssets: false));
            net.Ok("https://api.github.com/repos/me/fork/releases/latest", Bytes(Release("v0.4.9", signedAssets: false)));
            byte[] m = Bytes(Manifest("0.4.9"));
            net.Feed("stable", m, Sign(key, m));
            var r = Run(net, trust, settings: settings);
            Check.Equal(FeedState.Misconfigured, trust.State, name);
            Check.Null(r.Info, name);
            Check.Equal(Array.Empty<string>(), net.Asked.ToArray(), name + ": neither the feed nor GitHub is asked");
            Check.Equal(Updater.MisconfiguredNote, r.Up.LastError, name);
            Check.True(r.Up.CheckFailed, name);
            Check.True(r.Up.LastError!.StartsWith("update configuration invalid", StringComparison.Ordinal), name);
            Check.Null(r.Up.Repo, name + ": updates are off (the UI shows it, the loop does not start)");
            Check.False(r.Up.RepoOverridden, name);
            Check.False(r.Up.TryGetPendingUpdate(out _, out _), name + ": nothing is installed at startup");
            Check.Null(r.Settings.FeedFloorStable, name);

            // The note is there from the start, before any check.
            var fresh = new Updater(new Settings(), V("0.4.1"));
            Check.Equal(Updater.MisconfiguredNote, fresh.LastError, name);
            Check.True(fresh.CheckFailed, name);
            Check.Null(OffUi(() => fresh.CheckNowAsync()), name);
            Check.Equal(Array.Empty<string>(), net.Asked.ToArray(), name);
        }
    }

    // ------------------------------------------------------------------ feed on: verified and fresh

    [UnitTest]
    public static void Feed_verified_and_newer_is_offered_from_the_feed()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest());
        net.Feed("stable", manifest, Sign(key, manifest) + "\n");
        GitHub(net, Release("v0.4.9"));
        var r = Run(net, Trust(stable: new[] { pub }));
        Check.NotNull(r.Info);
        Check.Equal(V("0.4.2"), r.Info!.Version, "the feed's version, not GitHub's 0.4.9");
        Check.Equal(Sha, r.Info.Sha256);
        Check.Equal("", r.Info.ShaUrl);
        Check.Equal(new[] { Base + "stable.json", Base + "stable.json.sig" }, net.Asked.ToArray(), "manifest, then signature; nothing else");
        Check.Null(r.Up.LastError);
        Check.Equal("0.4.2", r.Settings.FeedFloorStable, "the floor is raised after verification");
    }

    [UnitTest]
    public static void Feed_same_version_is_up_to_date_without_asking_github()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest("0.4.2"));
        net.Feed("stable", manifest, Sign(key, manifest));
        GitHub(net, Release("v0.4.9"));
        var r = Run(net, Trust(stable: new[] { pub }), running: "0.4.2");
        Check.Null(r.Info);
        Check.Null(r.Up.LastError);
        Check.False(r.Up.CheckFailed);
        Check.False(net.AskedGitHub);
    }

    [UnitTest]
    public static void Feed_dev_channel_gets_a_newer_stable_and_a_newer_dev_build()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = Trust(stable: new[] { stablePub }, dev: new[] { devPub });

        // stable.json is newer than dev.json: a dev user gets the release.
        var net = new FakeNet();
        byte[] s = Bytes(Manifest("0.4.3")), d = Bytes(Manifest("0.4.3-dev.2", "dev"));
        net.Feed("stable", s, Sign(stableKey, s));
        net.Feed("dev", d, Sign(devKey, d));
        var r = Run(net, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.3"), r.Info!.Version);
        Check.False(net.AskedGitHub);
        Check.Equal("0.4.3", r.Settings.FeedFloorStable);
        Check.Equal("0.4.3-dev.2", r.Settings.FeedFloorDev);

        // dev.json is newer: the dev build.
        var net2 = new FakeNet();
        byte[] d2 = Bytes(Manifest("0.4.4-dev.1", "dev"));
        net2.Feed("stable", s, Sign(stableKey, s));
        net2.Feed("dev", d2, Sign(devKey, d2));
        Check.Equal(V("0.4.4-dev.1"), Run(net2, trust, dev: true, running: "0.4.3-dev.1").Info!.Version);

        // The keys are per channel: a stable.json signed with the dev key is refused; the valid dev.json is still used.
        var net3 = new FakeNet();
        net3.Feed("stable", s, Sign(devKey, s));
        net3.Feed("dev", d2, Sign(devKey, d2));
        GitHub(net3, Release("v0.4.9"));
        var r3 = Run(net3, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.4-dev.1"), r3.Info?.Version, r3.Up.LastError);
        Check.False(net3.AskedGitHub);
        Check.Null(r3.Settings.FeedFloorStable, "the refused stable.json raises nothing");
        Check.Equal("0.4.4-dev.1", r3.Settings.FeedFloorDev);

        // The stable channel never reads dev.json.
        var net4 = new FakeNet();
        net4.Feed("stable", s, Sign(stableKey, s));
        net4.Feed("dev", d2, Sign(devKey, d2));
        Check.Equal(V("0.4.3"), Run(net4, trust, running: "0.4.2").Info!.Version);
        Check.False(net4.Asked.Any(u => u.Contains("dev.json")));
    }

    [UnitTest]
    public static void Feed_dev_channel_partial_outcomes()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        var (_, wrongKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        using var c = wrongKey;
        var trust = Trust(stable: new[] { stablePub }, dev: new[] { devPub });
        byte[] s = Bytes(Manifest("0.4.3")), d = Bytes(Manifest("0.4.4-dev.1", "dev"));
        byte[] gh = Bytes(Manifest("0.4.9"));
        void WithGitHub(FakeNet n) { GitHub(n, Release("v0.4.9")); GitHubManifest(n, "v0.4.9", gh, Sign(stableKey, gh)); }

        // stable verified + dev refused: the newer stable is offered; only its floor goes up.
        var net = new FakeNet();
        net.Feed("stable", s, Sign(stableKey, s));
        net.Feed("dev", d, Sign(wrongKey, d));
        WithGitHub(net);
        var r = Run(net, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.3"), r.Info?.Version, r.Up.LastError);
        Check.Equal(Sha, r.Info!.Sha256);
        Check.False(net.AskedGitHub, "a refused dev.json does not block the valid stable.json");
        Check.Equal("0.4.3", r.Settings.FeedFloorStable);
        Check.Null(r.Settings.FeedFloorDev, "nothing from the refused dev.json");

        // stable verified + dev unreachable: the same.
        var net2 = new FakeNet();
        net2.Feed("stable", s, Sign(stableKey, s));
        net2.Status(Base + "dev.json", 503);
        var r2 = Run(net2, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.3"), r2.Info?.Version, r2.Up.LastError);
        Check.Equal("0.4.3", r2.Settings.FeedFloorStable);
        Check.Null(r2.Settings.FeedFloorDev);

        // stable verified but not newer + dev unreachable: half the picture cannot say "up to date": the signed GitHub path decides.
        var net3 = new FakeNet();
        net3.Feed("stable", s, Sign(stableKey, s));
        WithGitHub(net3);
        var r3 = Run(net3, trust, dev: true, running: "0.4.3");
        Check.True(net3.AskedGitHub);
        Check.Equal(V("0.4.9"), r3.Info?.Version, r3.Up.LastError);
        Check.Equal(Sha, r3.Info!.Sha256);
        Check.Equal("0.4.9", r3.Settings.FeedFloorStable, "raised by the GitHub manifest that was offered, not by the unused 0.4.3");
        Check.Null(r3.Settings.FeedFloorDev);

        // The same, but GitHub has nothing newer: up to date from GitHub, and the unused 0.4.3 raises no floor.
        var net4 = new FakeNet();
        net4.Feed("stable", s, Sign(stableKey, s));
        net4.Feed("dev", d, Sign(wrongKey, d));
        GitHub(net4, Release("v0.4.3"));
        var r4 = Run(net4, trust, dev: true, running: "0.4.3");
        Check.Null(r4.Info);
        Check.Null(r4.Up.LastError);
        Check.True(net4.AskedGitHub);
        Check.Null(r4.Settings.FeedFloorStable, "a verified manifest that was not used raises nothing");

        // dev verified + stable refused (bad version in it): the newer dev build is offered.
        var net5 = new FakeNet();
        byte[] badStable = Bytes(Manifest("0.4.3-dev.9"));
        net5.Feed("stable", badStable, Sign(stableKey, badStable));
        net5.Feed("dev", d, Sign(devKey, d));
        var r5 = Run(net5, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.4-dev.1"), r5.Info?.Version, r5.Up.LastError);
        Check.Equal("0.4.4-dev.1", r5.Settings.FeedFloorDev);
        Check.Null(r5.Settings.FeedFloorStable);

        // Both verified and fresh, the running dev build is the newest: up to date, both floors raised, no GitHub.
        var net6 = new FakeNet();
        net6.Feed("stable", s, Sign(stableKey, s));
        net6.Feed("dev", d, Sign(devKey, d));
        var r6 = Run(net6, trust, dev: true, running: "0.4.4-dev.1");
        Check.Null(r6.Info);
        Check.Null(r6.Up.LastError);
        Check.False(net6.AskedGitHub);
        Check.Equal("0.4.3", r6.Settings.FeedFloorStable);
        Check.Equal("0.4.4-dev.1", r6.Settings.FeedFloorDev);

        // A plain release in dev.json is refused (N10); stable.json's release is still offered.
        var net7 = new FakeNet();
        byte[] plainDev = Bytes(Manifest("0.4.5", "dev"));
        net7.Feed("stable", s, Sign(stableKey, s));
        net7.Feed("dev", plainDev, Sign(devKey, plainDev));
        var r7 = Run(net7, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.3"), r7.Info?.Version, r7.Up.LastError);
        Check.Null(r7.Settings.FeedFloorDev);
    }

    [UnitTest]
    public static void Feed_leaving_a_dev_build_still_needs_a_manual_check()
    {
        var (pub, key) = NewKey();
        using var k = key;
        byte[] manifest = Bytes(Manifest("0.4.2"));

        var net = new FakeNet();
        net.Feed("stable", manifest, Sign(key, manifest));
        GitHub(net, Release("v0.4.2"));
        var manual = Run(net, Trust(stable: new[] { pub }), running: "0.4.3-dev.5", manual: true);
        Check.Equal(V("0.4.2"), manual.Info!.Version, "a manual check on the stable channel goes back to the release");
        Check.Equal(Sha, manual.Info.Sha256);
        Check.False(net.AskedGitHub);

        var net2 = new FakeNet();
        net2.Feed("stable", manifest, Sign(key, manifest));
        GitHub(net2, Release("v0.4.2"));
        var background = Run(net2, Trust(stable: new[] { pub }), running: "0.4.3-dev.5", manual: false);
        Check.Null(background.Info, "the background check does not");
        Check.Null(background.Up.LastError, "nothing failed");
        Check.True(net2.AskedGitHub, "older than the running exe counts as stale: GitHub decides, as before");
    }

    [UnitTest]
    public static void Feed_bom_in_manifest_and_signature_is_accepted()
    {
        var (pub, key) = NewKey();
        using var k = key;
        byte[] manifest = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Manifest())).ToArray();
        var net = new FakeNet();
        net.Ok(Base + "stable.json", manifest);
        net.Ok(Base + "stable.json.sig", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Sign(key, manifest) + "\r\n")).ToArray());
        var r = Run(net, Trust(stable: new[] { pub }));
        Check.Equal(V("0.4.2"), r.Info?.Version, r.Up.LastError);

        // The signature covers the raw bytes: one made over the JSON without the BOM does not verify the file with it.
        var net2 = new FakeNet();
        net2.Feed("stable", manifest, Sign(key, Bytes(Manifest())));
        var r2 = Run(net2, Trust(stable: new[] { pub }));
        Check.Null(r2.Info);
        Check.True(net2.AskedGitHub, "refused: the signed GitHub path is asked");
        Check.Equal("no releases found for o/r", r2.Up.LastError);
    }

    // ------------------------------------------------------------------ feed on: fetched but not trustworthy -> signed GitHub path

    [UnitTest]
    public static void Feed_refused_manifest_falls_back_to_the_signed_github_path()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        var (_, wrongKey) = NewKey();
        using var k2 = wrongKey;
        byte[] good = Bytes(Manifest());
        void Signed(FakeNet n, string json) { var b = Bytes(json); n.Feed("stable", b, Sign(key, b)); }

        var cases = new List<(string Name, Action<FakeNet> Setup)>
        {
            ("bad signature", n => n.Feed("stable", good, Sign(wrongKey, good))),
            ("flipped byte", n => { var b = (byte[])good.Clone(); b[12] ^= 1; n.Feed("stable", b, Sign(key, good)); }),
            ("missing signature", n => n.Ok(Base + "stable.json", good)),
            ("signature gone (410)", n => { n.Ok(Base + "stable.json", good); n.Status(Base + "stable.json.sig", 410); }),
            ("garbage signature", n => n.Feed("stable", good, "%%%")),
            ("short signature", n => n.Feed("stable", good, Convert.ToBase64String(new byte[32]))),
            ("oversize signature", n => n.Feed("stable", good, new string('A', UpdateFeed.MaxSignatureBytes + 1))),
            ("unparseable", n => Signed(n, "{ nope")),
            ("schema 2", n => Signed(n, Manifest(schema: 2))),
            ("wrong channel", n => Signed(n, Manifest(channel: "dev"))),
            ("dev version on stable", n => Signed(n, Manifest("0.4.3-dev.7"))),
            ("non-https exe", n => Signed(n, Manifest(exeUrl: "http://cdn.example/Clink.exe"))),
            ("malformed exe url", n => Signed(n, Manifest(exeUrl: "https//cdn.example/Clink.exe"))),
            ("no exe asset", n => Signed(n, Manifest(exe: false))),
            ("bad sha", n => Signed(n, Manifest(sha: "XYZ"))),
            ("size 0", n => Signed(n, Manifest(size: 0))),
            ("no published", n => Signed(n, Manifest(published: false))),
            ("tag mismatch", n => Signed(n, Manifest(tag: "v9.9.9"))),
            ("UTF-16 BOM", n => { var b = new byte[] { 0xFF, 0xFE }.Concat(good).ToArray(); n.Feed("stable", b, Sign(key, b)); }),
        };
        foreach (var (name, setup) in cases)
        {
            var net = new FakeNet();
            setup(net);
            GitHub(net, Release("v0.4.9"));
            GitHubManifest(net, "v0.4.9", Bytes(Manifest("0.4.9")), Sign(key, Bytes(Manifest("0.4.9"))));
            var r = Run(net, Trust(stable: new[] { pub }));
            Check.True(net.AskedGitHub, name + ": GitHub is asked");
            Check.Equal(V("0.4.9"), r.Info?.Version, name + ": GitHub's release, with its signed manifest; " + r.Up.LastError);
            Check.Equal(Sha, r.Info!.Sha256, name + ": the hash comes from the signed GitHub manifest");
            Check.Equal("", r.Info.ShaUrl, name);
            Check.False(net.AskedSha256File, name + ": never the unsigned path");
            Check.Null(r.Up.LastError, name);
            Check.Equal("0.4.9", r.Settings.FeedFloorStable, name + ": only the used GitHub manifest raises the floor");

            // GitHub without a signed manifest: nothing at all (never Clink.exe.sha256).
            var net2 = new FakeNet();
            setup(net2);
            GitHub(net2, Release("v0.4.9", signedAssets: false));
            var r2 = Run(net2, Trust(stable: new[] { pub }));
            Check.Null(r2.Info, name);
            Check.Equal(Updater.GitHubRefusedNote, r2.Up.LastError, name);
            Check.True(r2.Up.CheckFailed, name);
            Check.False(net2.AskedSha256File, name);
            Check.Null(r2.Settings.FeedFloorStable, name + ": no floor from a refused manifest");
        }
    }

    // ------------------------------------------------------------------ capped reads

    /// <summary>A body that never ends; counts what was read from it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Served;
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)' ', offset, count); Served += count; return count; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that announces a Content-Length and must never be read.</summary>
    private sealed class AnnouncedContent : HttpContent
    {
        public bool Read;
        public AnnouncedContent(long length) { Headers.ContentLength = length; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Read = true; throw new InvalidOperationException("the body must not be read"); }
        protected override Task<Stream> CreateContentReadStreamAsync() { Read = true; throw new InvalidOperationException("the body must not be read"); }
        protected override bool TryComputeLength(out long length) { length = Headers.ContentLength ?? 0; return true; }
    }

    [UnitTest]
    public static void Feed_oversize_manifest_is_refused_without_reading_the_whole_body()
    {
        var (pub, key) = NewKey();
        using var k = key;

        var endless = new EndlessStream();
        var net = new FakeNet();
        net.Ok(Base + "stable.json", () => new StreamContent(endless));
        var r = Run(net, Trust(stable: new[] { pub }));   // GitHub has no releases: the fall back ends there
        Check.Null(r.Info);
        Check.Equal("no releases found for o/r", r.Up.LastError);
        Check.True(endless.Served <= UpdateFeed.MaxManifestBytes + 1, $"read {endless.Served} bytes");
        Check.False(net.Asked.Contains(Base + "stable.json.sig"), "the size is checked before the signature is fetched");
        Check.True(net.AskedGitHub, "refused: the signed GitHub path");

        var announced = new AnnouncedContent(10L * 1024 * 1024);
        var net2 = new FakeNet();
        net2.Ok(Base + "stable.json", () => announced);
        var r2 = Run(net2, Trust(stable: new[] { pub }));
        Check.Null(r2.Info);
        Check.Equal("no releases found for o/r", r2.Up.LastError);
        Check.False(announced.Read, "a Content-Length over the cap is refused before the body is read");

        // The signature is capped too.
        byte[] manifest = Bytes(Manifest());
        var endlessSig = new EndlessStream();
        var net3 = new FakeNet();
        net3.Ok(Base + "stable.json", manifest);
        net3.Ok(Base + "stable.json.sig", () => new StreamContent(endlessSig));
        var r3 = Run(net3, Trust(stable: new[] { pub }));
        Check.Null(r3.Info);
        Check.Equal("no releases found for o/r", r3.Up.LastError);
        Check.True(endlessSig.Served <= UpdateFeed.MaxSignatureBytes + 1, $"read {endlessSig.Served} bytes of the signature");

        // At the cap is fine.
        string padded = Manifest() + new string(' ', UpdateFeed.MaxManifestBytes - Bytes(Manifest()).Length);
        byte[] full = Bytes(padded);
        Check.Equal(UpdateFeed.MaxManifestBytes, full.Length);
        var net4 = new FakeNet();
        net4.Feed("stable", full, Sign(key, full));
        Check.Equal(V("0.4.2"), Run(net4, Trust(stable: new[] { pub })).Info?.Version);
    }

    // ------------------------------------------------------------------ feed on: unreachable or stale -> signed GitHub path

    [UnitTest]
    public static void Feed_unreachable_falls_back_to_the_signed_github_path()
    {
        var (pub, key) = NewKey();
        using var k = key;
        byte[] good = Bytes(Manifest());
        var cases = new List<(string Name, Action<FakeNet> Setup)>
        {
            ("404", n => { }),
            ("HTTP 500", n => n.Status(Base + "stable.json", 500)),
            ("connection error", n => n.Throw(Base + "stable.json", () => new HttpRequestException("connection refused"))),
            ("timeout", n => n.Throw(Base + "stable.json", () => new TaskCanceledException("timed out"))),
            ("signature HTTP 503", n => { n.Ok(Base + "stable.json", good); n.Status(Base + "stable.json.sig", 503); }),
            ("signature connection error", n => { n.Ok(Base + "stable.json", good); n.Throw(Base + "stable.json.sig", () => new IOException("reset")); }),
        };
        foreach (var (name, setup) in cases)
        {
            var net = new FakeNet();
            setup(net);
            GitHub(net, Release("v0.4.9"));
            byte[] ghManifest = Bytes(Manifest("0.4.9"));
            GitHubManifest(net, "v0.4.9", ghManifest, Sign(key, ghManifest));
            var r = Run(net, Trust(stable: new[] { pub }));
            Check.NotNull(r.Info, name + ": GitHub's release is offered");
            Check.Equal(V("0.4.9"), r.Info!.Version, name);
            Check.Equal(Gh + "v0.4.9/Clink.exe", r.Info.ExeUrl, name + ": the exe comes from the GitHub release");
            Check.Equal(Sha, r.Info.Sha256, name + ": the hash comes from the signed manifest");
            Check.Equal(1234L, r.Info.ExeSize, name);
            Check.Equal("", r.Info.ShaUrl, name);
            Check.False(net.AskedSha256File, name + ": Clink.exe.sha256 is not used");
            Check.Null(r.Up.LastError, name);
            Check.Equal("0.4.9", r.Settings.FeedFloorStable, name + ": a verified GitHub manifest raises the floor");
        }
    }

    [UnitTest]
    public static void Feed_stale_manifest_falls_back_to_github()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var cases = new List<(string Name, string Manifest, string Running, string? Floor)>
        {
            ("below the floor", Manifest("0.4.2"), "0.4.1", "0.4.3"),
            ("published over 90 days ago", Manifest("0.4.2", publishedDaysAgo: 91), "0.4.1", null),
            ("published 3 days in the future", Manifest("0.4.2", publishedDaysAgo: -3), "0.4.1", null),
            ("older than the running exe", Manifest("0.4.2"), "0.4.3", null),
        };
        foreach (var (name, json, running, floor) in cases)
        {
            var net = new FakeNet();
            byte[] m = Bytes(json);
            net.Feed("stable", m, Sign(key, m));   // GitHub has no releases: the check ends there
            var settings = new Settings { FeedFloorStable = floor };
            var r = Run(net, Trust(stable: new[] { pub }), running: running, settings: settings);
            Check.Null(r.Info, name);
            Check.True(net.AskedGitHub, name + ": GitHub is asked");
            Check.Equal("no releases found for o/r", r.Up.LastError, name);
            Check.Equal(floor, r.Settings.FeedFloorStable, name + ": a stale manifest raises no floor");
        }

        // A stale manifest still raises nothing above what it is, and a floor never goes down.
        var net2 = new FakeNet();
        byte[] m2 = Bytes(Manifest("0.4.2"));
        net2.Feed("stable", m2, Sign(key, m2));
        var r2 = Run(net2, Trust(stable: new[] { pub }), settings: new Settings { FeedFloorStable = "0.4.3" });
        Check.Equal("0.4.3", r2.Settings.FeedFloorStable);
    }

    [UnitTest]
    public static void Feed_github_release_without_valid_signed_manifest_is_refused()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        var (_, wrongKey) = NewKey();
        using var k2 = wrongKey;
        byte[] m9 = Bytes(Manifest("0.4.9"));
        var cases = new List<(string Name, Action<FakeNet> Setup)>
        {
            ("no signed assets", n => GitHub(n, Release("v0.4.9", signedAssets: false))),
            ("signature asset missing", n => { GitHub(n, Release("v0.4.9")); n.Ok(Gh + "v0.4.9/stable.json", m9); }),
            ("wrong key", n => { GitHub(n, Release("v0.4.9")); GitHubManifest(n, "v0.4.9", m9, Sign(wrongKey, m9)); }),
            ("manifest of another tag", n => { GitHub(n, Release("v0.4.9")); var m8 = Bytes(Manifest("0.4.8")); GitHubManifest(n, "v0.4.9", m8, Sign(key, m8)); }),
            ("exe size differs", n => { GitHub(n, Release("v0.4.9", exeSize: 999)); GitHubManifest(n, "v0.4.9", m9, Sign(key, m9)); }),
            ("manifest unreachable", n => { GitHub(n, Release("v0.4.9")); n.Status(Gh + "v0.4.9/stable.json", 500); }),
            ("below the floor (a replayed release)", n => { GitHub(n, Release("v0.4.9")); GitHubManifest(n, "v0.4.9", m9, Sign(key, m9)); }),
        };
        foreach (var (name, setup) in cases)
        {
            var net = new FakeNet();
            setup(net);   // and the feed is unreachable (404)
            string? floor = name.StartsWith("below", StringComparison.Ordinal) ? "0.5.0" : null;
            var r = Run(net, Trust(stable: new[] { pub }), settings: new Settings { FeedFloorStable = floor });
            Check.Null(r.Info, name + ": refused");
            Check.Equal(Updater.GitHubRefusedNote, r.Up.LastError, name);
            Check.True(r.Up.CheckFailed, name);
            Check.False(net.AskedSha256File, name + ": no fall back to Clink.exe.sha256");
            Check.False(net.Asked.Any(u => u.EndsWith("Clink.exe", StringComparison.Ordinal)), name + ": nothing downloaded");
            Check.Equal(floor, r.Settings.FeedFloorStable, name + ": a manifest that is not offered raises no floor (the size mismatch included)");
        }

        // The floor holds on the GitHub path: equal to it is fine.
        var ok = new FakeNet();
        GitHub(ok, Release("v0.4.9"));
        GitHubManifest(ok, "v0.4.9", m9, Sign(key, m9));
        var r2 = Run(ok, Trust(stable: new[] { pub }), settings: new Settings { FeedFloorStable = "0.4.9" });
        Check.Equal(V("0.4.9"), r2.Info?.Version, r2.Up.LastError);
    }

    // ------------------------------------------------------------------ timeouts

    /// <summary>A body that never sends a byte (until the read is cancelled).</summary>
    private sealed class StalledStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("synchronous read of a stalled body");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A GET whose headers never come (until it is cancelled).</summary>
    private static async Task<HttpResponseMessage> StalledHeaders(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        throw new InvalidOperationException("not reached");
    }

    [UnitTest]
    public static void Feed_request_timeout_covers_a_stalled_header_and_a_stalled_body()
    {
        var shortTime = TimeSpan.FromMilliseconds(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Check.Throws<FeedUnreachableException>(() => UpdateFeed.FetchCappedAsync((_, ct) => StalledHeaders(ct), Base + "stable.json", 1000, false,
            CancellationToken.None, shortTime).GetAwaiter().GetResult(), "stalled header");
        Check.Contains(ex.Message, "no answer for stable.json within 0.3 s");
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");

        sw.Restart();
        ex = Check.Throws<FeedUnreachableException>(() => UpdateFeed.FetchCappedAsync(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }),
            Base + "stable.json", 1000, false, CancellationToken.None, shortTime).GetAwaiter().GetResult(), "stalled body");
        Check.Contains(ex.Message, "no answer");
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");

        // The caller's cancellation still wins over the timeout: thrown as such, not as unreachable.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Check.Throws<OperationCanceledException>(() => UpdateFeed.FetchCappedAsync((_, ct) => StalledHeaders(ct), Base + "stable.json", 1000, false,
            cts.Token, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult());

        // In the updater: a stalled feed (header or body, manifest or signature) is unreachable, so the signed GitHub path is asked.
        var (pub, key) = NewKey();
        using var k = key;
        byte[] good = Bytes(Manifest());
        byte[] m9 = Bytes(Manifest("0.4.9"));
        var cases = new List<(string Name, Action<FakeNet> Setup)>
        {
            ("manifest header", n => n.RoutesAsync[Base + "stable.json"] = StalledHeaders),
            ("manifest body", n => n.Ok(Base + "stable.json", () => new StreamContent(new StalledStream()))),
            ("signature body", n => { n.Ok(Base + "stable.json", good); n.Ok(Base + "stable.json.sig", () => new StreamContent(new StalledStream())); }),
        };
        foreach (var (name, setup) in cases)
        {
            var net = new FakeNet();
            setup(net);
            GitHub(net, Release("v0.4.9"));
            GitHubManifest(net, "v0.4.9", m9, Sign(key, m9));
            Updater.FeedTimeoutOverride = shortTime;
            sw.Restart();
            var r = Run(net, Trust(stable: new[] { pub }));
            Check.Equal(V("0.4.9"), r.Info?.Version, name + ": " + r.Up.LastError);
            Check.Equal(Sha, r.Info!.Sha256, name);
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{name}: took {sw.Elapsed}");
        }
    }

    [UnitTest]
    public static void Feed_signed_github_path_on_the_dev_channel_uses_dev_json_and_the_dev_keys()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = Trust(stable: new[] { stablePub }, dev: new[] { devPub });
        byte[] dm = Bytes(Manifest("0.4.5-dev.3", "dev"));

        var net = new FakeNet();   // feed unreachable
        GitHub(net, Release("v0.4.4"), Release("v0.4.5-dev.3", prerelease: true));
        GitHubManifest(net, "v0.4.5-dev.3", dm, Sign(devKey, dm), "dev");
        var r = Run(net, trust, dev: true, running: "0.4.4");
        Check.Equal(V("0.4.5-dev.3"), r.Info?.Version, r.Up.LastError);
        Check.Equal(Gh + "v0.4.5-dev.3/Clink.exe", r.Info!.ExeUrl);
        Check.Equal("0.4.5-dev.3", r.Settings.FeedFloorDev);

        var net2 = new FakeNet();
        GitHub(net2, Release("v0.4.4"), Release("v0.4.5-dev.3", prerelease: true));
        GitHubManifest(net2, "v0.4.5-dev.3", dm, Sign(stableKey, dm), "dev");
        var r2 = Run(net2, trust, dev: true, running: "0.4.4");
        Check.Null(r2.Info, "a dev manifest signed with the stable key is refused");
        Check.Equal(Updater.GitHubRefusedNote, r2.Up.LastError);
    }

    [UnitTest]
    public static void Feed_on_ignores_update_repo_from_settings()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var net = new FakeNet();   // feed unreachable: GitHub is asked
        GitHub(net, Release("v0.4.9"));
        byte[] m9 = Bytes(Manifest("0.4.9"));
        GitHubManifest(net, "v0.4.9", m9, Sign(key, m9));
        net.Ok("https://api.github.com/repos/evil/x/releases/latest", Bytes(Release("v9.9.9", signedAssets: false)));
        var settings = new Settings { UpdateRepo = "evil/x" };
        var r = Run(net, Trust(stable: new[] { pub }), settings: settings);
        Check.Equal(V("0.4.9"), r.Info?.Version, r.Up.LastError);
        Check.False(net.Asked.Any(u => u.Contains("evil", StringComparison.Ordinal)), "only the built-in repository is asked");
        Check.True(net.Asked.Contains(GitHubLatest));
        Check.Equal(BuiltIn, r.Up.Repo);
        Check.False(r.Up.RepoOverridden);

        // With the feed off, the setting works as it always did.
        Updater.FeedOverride = FeedTrust.None;
        var plain = new Updater(settings, V("0.4.1"));
        Check.Equal("evil/x", plain.Repo);
        Check.True(plain.RepoOverridden);
    }

    // ------------------------------------------------------------------ cancellation

    [UnitTest]
    public static void Feed_caller_cancellation_propagates()
    {
        var (pub, key) = NewKey();
        using var k = key;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var net = new FakeNet();
        GitHub(net, Release("v0.4.9"));
        var r = Run(net, Trust(stable: new[] { pub }), ct: cts.Token);
        Check.Null(r.Info);
        Check.Equal("cancelled", r.Up.LastError);
        Check.False(net.AskedGitHub, "a cancelled check is not a reason to ask GitHub");

        // Cancelled while the feed answers: the same, not a fall back.
        using var cts2 = new CancellationTokenSource();
        var net2 = new FakeNet();
        net2.Routes[Base + "stable.json"] = ct => { cts2.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("not reached"); };
        GitHub(net2, Release("v0.4.9"));
        var r2 = Run(net2, Trust(stable: new[] { pub }), ct: cts2.Token);
        Check.Equal("cancelled", r2.Up.LastError);
        Check.False(net2.AskedGitHub);

        // FetchCappedAsync itself throws the caller's cancellation as such.
        using var cts3 = new CancellationTokenSource();
        cts3.Cancel();
        Check.Throws<OperationCanceledException>(() => UpdateFeed.FetchCappedAsync((_, c) => { c.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage()); },
            Base + "stable.json", 10, false, cts3.Token).GetAwaiter().GetResult());
    }

    // ------------------------------------------------------------------ download with a signed hash

    private static byte[] FakeExe(int length = 300, byte fill = 7)
    {
        var b = new byte[length];
        Array.Fill(b, fill);
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        return b;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [UnitTest]
    public static void Feed_download_uses_the_signed_hash_and_size_only()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        const string url = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";
        var settings = new Settings();
        var up = new Updater(settings, V("0.4.1"));
        UpdateInfo Info(string sha, long size) => new(V("0.4.2"), "v0.4.2", url, size, "", "", "", sha);
        string[] Left() => Directory.Exists(Updater.UpdatesDir) ? Directory.GetFiles(Updater.UpdatesDir) : Array.Empty<string>();

        var cases = new List<(string Name, byte[] Body, string Sha, long Size)>
        {
            ("wrong hash", exe, new string('0', 64), exe.Length),
            ("body longer than the signed size", exe.Concat(new byte[10]).ToArray(), Hex(exe), exe.Length),
            ("body shorter than the signed size", exe[..200], Hex(exe), exe.Length),
        };
        foreach (var (name, body, sha, size) in cases)
        {
            var net = new FakeNet();
            net.Ok(url, body);
            Updater.HttpOverride = net.Send;
            Check.Throws<InvalidDataException>(() => OffUi(() => up.DownloadAsync(Info(sha, size), null, CancellationToken.None)), name);
            Check.Equal(Array.Empty<string>(), Left(), name + ": no .partial and no final file");
            Check.False(net.AskedSha256File, name);
            Check.Null(settings.PendingUpdateSha256, name);
        }

        // A body without a Content-Length that runs past the signed size is cut off.
        var endless = new EndlessStream();
        var net2 = new FakeNet();
        net2.Ok(url, () => new StreamContent(endless));
        Updater.HttpOverride = net2.Send;
        Check.Throws<InvalidDataException>(() => OffUi(() => up.DownloadAsync(Info(Hex(exe), exe.Length), null, CancellationToken.None)));
        Check.True(endless.Served < 1 << 20, $"read {endless.Served} bytes");
        Check.Equal(Array.Empty<string>(), Left());

        // The good one.
        var net3 = new FakeNet();
        net3.Ok(url, exe);
        Updater.HttpOverride = net3.Send;
        string path = OffUi(() => up.DownloadAsync(Info(Hex(exe), exe.Length), null, CancellationToken.None));
        Check.Equal(Updater.FileFor(V("0.4.2")), path);
        Check.Equal(exe, File.ReadAllBytes(path));
        Check.Equal(new[] { url }, net3.Asked.ToArray(), "only the exe: no hash file");
        Check.Equal(Hex(exe), settings.PendingUpdateSha256!.ToLowerInvariant());
        Check.Equal(true, settings.PendingUpdateSigned, "recorded as a signed download");
        Check.Contains(settings.ToJson(), "\"PendingUpdateSigned\": true");

        // An unsigned download (feed off: Clink.exe.sha256) clears the mark.
        File.Delete(path);
        var net4 = new FakeNet();
        net4.Ok(url, exe);
        net4.Ok(url + ".sha256", Bytes(Hex(exe) + "  Clink.exe\n"));
        Updater.HttpOverride = net4.Send;
        OffUi(() => up.DownloadAsync(new UpdateInfo(V("0.4.2"), "v0.4.2", url, exe.Length, url + ".sha256", "", ""), null, CancellationToken.None));
        Check.Null(settings.PendingUpdateSigned);
        Check.DoesNotContain(settings.ToJson(), "PendingUpdateSigned", "left out of the file while null");
        path = Updater.FileFor(V("0.4.2"));

        // Caller cancellation propagates from the download too, and leaves nothing behind.
        File.Delete(path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Check.Throws<OperationCanceledException>(() => OffUi(() => up.DownloadAsync(Info(Hex(exe), exe.Length), null, cts.Token)));
        Check.Equal(Array.Empty<string>(), Left().Where(f => f.EndsWith(".partial", StringComparison.Ordinal) || f.EndsWith(".exe", StringComparison.Ordinal)).ToArray());
    }

    [UnitTest]
    public static void Feed_github_manifest_hash_must_match_the_downloaded_exe()
    {
        var (pub, key) = NewKey();
        using var k = key;
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        var net = new FakeNet();   // feed unreachable
        GitHub(net, Release("v0.4.9", exeSize: exe.Length));
        byte[] m9 = Bytes(Manifest("0.4.9", size: exe.Length, sha: new string('c', 64)));   // signed, but not this exe's hash
        GitHubManifest(net, "v0.4.9", m9, Sign(key, m9));
        net.Ok(Gh + "v0.4.9/Clink.exe", exe);
        var r = Run(net, Trust(stable: new[] { pub }));
        Check.NotNull(r.Info, r.Up.LastError);
        var ex = Check.Throws<InvalidDataException>(() => OffUi(() => r.Up.DownloadAsync(r.Info!, null, CancellationToken.None)));
        Check.Contains(ex.Message, "SHA-256");
        Check.False(net.AskedSha256File, "Clink.exe.sha256 is never asked for");
        Check.Equal(Array.Empty<string>(), Directory.GetFiles(Updater.UpdatesDir));
    }

    [UnitTest]
    public static void Feed_existing_download_is_reused_only_with_the_signed_hash()
    {
        var (pub, key) = NewKey();
        using var k = key;
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        byte[] exe = FakeExe();
        string file = Updater.FileFor(V("0.4.2"));
        File.WriteAllBytes(file, exe);
        const string exeUrl = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";

        // Recorded as verified (hash of the file), but the signed manifest describes another file: not reused.
        var settings = new Settings { PendingUpdateVersion = "0.4.2", PendingUpdateSha256 = Convert.ToHexString(SHA256.HashData(exe)) };
        var net = new FakeNet();
        byte[] other = Bytes(Manifest("0.4.2", size: exe.Length, sha: new string('d', 64)));
        net.Feed("stable", other, Sign(key, other));
        net.Ok(exeUrl, exe);   // what the server sends does not match the manifest either
        settings.UseDevBuilds = false;
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        Updater.BuiltInRepoOverride = BuiltIn;
        var up = new Updater(settings, V("0.4.1"));
        OffUi(() => up.CheckNowAsync());
        Check.Null(up.PendingPath, "not adopted");
        Check.True(net.Asked.Contains(exeUrl), "downloaded again instead");
        Check.False(File.Exists(file), "the old file was discarded and the new download failed its hash");
        Check.Contains(up.LastError, "downloading v0.4.2 failed");

        // The same file with the signed hash recorded, but recorded by an unsigned download (Clink.exe.sha256): not adopted either.
        byte[] same = Bytes(Manifest("0.4.2", size: exe.Length, sha: Hex(exe)));
        File.WriteAllBytes(file, exe);
        settings.PendingUpdateVersion = "0.4.2";
        settings.PendingUpdateSha256 = Convert.ToHexString(SHA256.HashData(exe));
        settings.PendingUpdateSigned = null;
        var netU = new FakeNet();
        netU.Feed("stable", same, Sign(key, same));
        Updater.HttpOverride = netU.Send;
        var upU = new Updater(settings, V("0.4.1"));
        OffUi(() => upU.CheckNowAsync());
        Check.True(netU.Asked.Contains(exeUrl), "an unsigned download is not reused while the feed is on");

        // Recorded as a signed download: adopted without a download.
        File.WriteAllBytes(file, exe);
        settings.PendingUpdateVersion = "0.4.2";
        settings.PendingUpdateSha256 = Convert.ToHexString(SHA256.HashData(exe));
        settings.PendingUpdateSigned = true;
        var net2 = new FakeNet();
        net2.Feed("stable", same, Sign(key, same));
        Updater.HttpOverride = net2.Send;
        var up2 = new Updater(settings, V("0.4.1"));
        OffUi(() => up2.CheckNowAsync());
        Check.Equal(file, up2.PendingPath);
        Check.False(net2.Asked.Contains(exeUrl), "reused");

        // The pending download of this run is not reused either once the signed hash differs (the shortcut path).
        var net3 = new FakeNet();
        byte[] changed = Bytes(Manifest("0.4.2", size: exe.Length, sha: new string('e', 64)));
        net3.Feed("stable", changed, Sign(key, changed));
        net3.Ok(exeUrl, exe);
        Updater.HttpOverride = net3.Send;
        OffUi(() => up2.CheckNowAsync());
        Check.True(net3.Asked.Contains(exeUrl), "the pending file is not taken for a manifest that describes another one");
        Check.False(File.Exists(file));
    }

    [UnitTest]
    public static void Feed_a_pending_download_newer_than_the_signed_offer_still_wins()
    {
        var (pub, key) = NewKey();
        using var k = key;
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        string newer = Updater.FileFor(V("0.4.3"));
        File.WriteAllBytes(newer, FakeExe());
        var settings = new Settings();
        var net = new FakeNet();
        byte[] m = Bytes(Manifest("0.4.2"));
        net.Feed("stable", m, Sign(key, m));
        settings.UseDevBuilds = false;
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        Updater.BuiltInRepoOverride = BuiltIn;
        var up = new Updater(settings, V("0.4.1"));
        Check.True(up.TrySetPending(newer, V("0.4.3"), "downloaded and verified", manual: false));   // made earlier in this run
        var info = OffUi(() => up.CheckNowAsync());
        Check.Equal(V("0.4.2"), info?.Version, up.LastError);
        Check.Equal(newer, up.PendingPath, "v0.4.3 stays the pending update");
        Check.False(net.Asked.Any(u => u.EndsWith("Clink.exe", StringComparison.Ordinal)), "the older offer is not downloaded");
    }

    [UnitTest]
    public static void Feed_on_installs_only_a_signed_download_at_startup()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        byte[] exe = FakeExe();
        string file = Updater.FileFor(V("0.4.9"));
        Updater.BuiltInRepoOverride = BuiltIn;
        Settings Recorded(bool? signed) => new() { PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateSigned = signed, PendingUpdateFrom = "0.4.1" };

        // Feed off: a download checked against Clink.exe.sha256 is installed as before.
        File.WriteAllBytes(file, exe);
        Updater.FeedOverride = FeedTrust.None;
        var off = new Updater(Recorded(null), V("0.4.1"));
        bool found = off.TryGetPendingUpdate(out string path, out _);
        Check.True(found ? path == file : off.InstallProblem != null, "found, or refused only because this runner cannot install");
        Check.True(File.Exists(file));

        // Feed on: the same unsigned download is discarded, never installed.
        Updater.FeedOverride = Trust();
        var on = new Updater(Recorded(null), V("0.4.1"));
        Check.False(on.TryGetPendingUpdate(out _, out _));
        Check.Null(on.InstallProblem);
        Check.False(File.Exists(file), "the unsigned download is thrown away");

        // Feed on, signed download: installed.
        File.WriteAllBytes(file, exe);
        var signed = new Updater(Recorded(true), V("0.4.1"));
        found = signed.TryGetPendingUpdate(out path, out _);
        Check.True(found ? path == file : signed.InstallProblem != null, "found, or refused only because this runner cannot install");
        Check.True(File.Exists(file));
    }

    [UnitTest]
    public static void Feed_check_and_download_need_no_ui_thread()
    {
        // m3: every await in the check and the download is ConfigureAwait(false), so blocking the test (UI) thread on them cannot
        // deadlock. A deadlock shows as a timeout here instead of a hung run.
        var (pub, key) = NewKey();
        using var k = key;
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        byte[] m = Bytes(Manifest("0.4.2", size: exe.Length, sha: Hex(exe)));
        var net = new FakeNet();
        net.Feed("stable", m, Sign(key, m));
        net.Ok("https://cdn.example/clink/releases/v0.4.2/Clink.exe", () => new StreamContent(new MemoryStream(exe)));
        var settings = new Settings { UseDevBuilds = false };
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        Updater.BuiltInRepoOverride = BuiltIn;
        var up = new Updater(settings, V("0.4.1"));
        var check = up.CheckAsync(CancellationToken.None);
        Check.True(check.Wait(TimeSpan.FromSeconds(20)), "the check finishes while this thread is blocked");
        Check.NotNull(check.Result, up.LastError);
        var download = up.DownloadAsync(check.Result!, null, CancellationToken.None);
        Check.True(download.Wait(TimeSpan.FromSeconds(20)), "the download finishes while this thread is blocked");
        Check.Equal(Updater.FileFor(V("0.4.2")), download.Result);
    }

    // ------------------------------------------------------------------ settings

    [UnitTest]
    public static void Feed_floors_are_saved_read_and_never_imported()
    {
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.FeedFloorStable)));
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.FeedFloorDev)));
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.PendingUpdateSigned)));
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.FeedFloorDev)));
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.PendingUpdateSigned)));

        var s = new Settings { FeedFloorStable = "0.4.2", FeedFloorDev = "0.4.3-dev.1" };
        string json = s.ToJson();
        Check.Contains(json, "\"FeedFloorStable\": \"0.4.2\"");
        var back = Settings.FromJson(json);
        Check.Equal("0.4.2", back.FeedFloorStable);
        Check.Equal("0.4.3-dev.1", back.FeedFloorDev);
        Check.True(back.Unknown == null || !back.Unknown.ContainsKey(nameof(Settings.FeedFloorStable)), "read as a setting, not kept as an unknown key");
        string again = back.ToJson();
        Check.Equal(1, again.Split("FeedFloorStable").Length - 1, "written once");

        // Another copy's floors are not taken over.
        string theirs = new Settings { FeedFloorStable = "0.1.0" }.ToJson();
        string merged = SettingsImport.KeepOurOwn(theirs, json, keepOurToken: true);
        Check.Equal("0.4.2", Settings.FromJson(merged).FeedFloorStable);
        Check.Null(Settings.FromJson(SettingsImport.KeepOurOwn(theirs, new Settings().ToJson(), keepOurToken: true)).FeedFloorStable, "left out when we have none");
    }

    // ------------------------------------------------------------------ two flavours (Packaging)

    private static readonly string FxSha = new('e', 64);

    /// <summary>Adds the framework exe (777 bytes, <see cref="FxSha"/>) to a manifest made by <see cref="Manifest"/>.</summary>
    private static string WithFramework(string manifest, string version) =>
        manifest.Replace("\"installer\":", $"\"exeFramework\":{{\"url\":\"https://cdn.example/clink/releases/v{version}/Clink-framework.exe\",\"size\":777,\"sha256\":\"{FxSha}\"}},\"installer\":");

    [UnitTest]
    public static void Feed_a_framework_client_takes_exeFramework_unless_it_cannot_use_it()
    {
        var (pub, key) = NewKey();
        using var k = key;
        byte[] m = Bytes(WithFramework(Manifest(), "0.4.2"));   // minRuntime "10.0"
        var trust = Trust(stable: new[] { pub });
        FakeNet Net() { var n = new FakeNet(); n.Feed("stable", m, Sign(key, m)); return n; }

        DotNetRuntimes.Override = new[] { new Version(10, 0, 1) };
        Updater.PackagingOverride = Packaging.Framework;
        var r = Run(Net(), trust);
        Check.NotNull(r.Info, r.Up.LastError);
        Check.Equal("https://cdn.example/clink/releases/v0.4.2/Clink-framework.exe", r.Info!.ExeUrl);
        Check.Equal(777L, r.Info.ExeSize);
        Check.Equal(FxSha, r.Info.Sha256);
        Check.Equal(Packaging.Framework, r.Info.Flavour);

        DotNetRuntimes.Override = new[] { new Version(9, 0, 9) };   // minRuntime 10.0 is not installed: the self-contained exe
        r = Run(Net(), trust);
        Check.Equal("https://cdn.example/clink/releases/v0.4.2/Clink.exe", r.Info!.ExeUrl);
        Check.Equal(Sha, r.Info.Sha256);
        Check.Equal(Packaging.SelfContained, r.Info.Flavour);

        DotNetRuntimes.Override = new[] { new Version(10, 0, 1) };
        Updater.PackagingOverride = Packaging.SelfContained;       // a portable copy never takes the framework exe
        r = Run(Net(), trust);
        Check.Equal("https://cdn.example/clink/releases/v0.4.2/Clink.exe", r.Info!.ExeUrl);
        Check.Equal(Packaging.SelfContained, r.Info.Flavour);

        byte[] plain = Bytes(Manifest());                           // a manifest without exeFramework
        var net = new FakeNet(); net.Feed("stable", plain, Sign(key, plain));
        Updater.PackagingOverride = Packaging.Framework;
        r = Run(net, trust);
        Check.Equal("https://cdn.example/clink/releases/v0.4.2/Clink.exe", r.Info!.ExeUrl, "falls back to exe");
    }

    [UnitTest]
    public static void Feed_signed_github_path_picks_the_asset_by_packaging()
    {
        var (pub, key) = NewKey();
        using var k = key;
        string fxAsset = $$"""{"name":"Clink-framework.exe","size":777,"browser_download_url":"{{Gh}}v0.4.9/Clink-framework.exe"},""";
        string release = Release("v0.4.9", exeSize: 1234).Replace("\"assets\":[", "\"assets\":[" + fxAsset);
        byte[] withFx = Bytes(WithFramework(Manifest("0.4.9", size: 1234), "0.4.9"));
        byte[] withoutFx = Bytes(Manifest("0.4.9", size: 1234));

        FakeNet Net(byte[] manifest) { var n = new FakeNet(); GitHub(n, release); GitHubManifest(n, "v0.4.9", manifest, Sign(key, manifest)); return n; }   // the feed itself is unreachable
        var trust = Trust(stable: new[] { pub });
        DotNetRuntimes.Override = new[] { new Version(10, 0, 1) };

        Updater.PackagingOverride = Packaging.Framework;
        var r = Run(Net(withFx), trust);
        Check.NotNull(r.Info, r.Up.LastError);
        Check.Equal(Gh + "v0.4.9/Clink-framework.exe", r.Info!.ExeUrl);
        Check.Equal(777L, r.Info.ExeSize);
        Check.Equal(FxSha, r.Info.Sha256);
        Check.Equal(Packaging.Framework, r.Info.Flavour);

        r = Run(Net(withoutFx), trust);
        Check.NotNull(r.Info, "the manifest has no exeFramework: " + r.Up.LastError);
        Check.Equal(Gh + "v0.4.9/Clink.exe", r.Info!.ExeUrl, "falls back to Clink.exe");
        Check.Equal(1234L, r.Info.ExeSize);
        Check.Equal(Sha, r.Info.Sha256);
        Check.Equal(Packaging.SelfContained, r.Info.Flavour);

        DotNetRuntimes.Override = new[] { new Version(9, 0, 9) };
        r = Run(Net(withFx), trust);
        Check.Equal(Gh + "v0.4.9/Clink.exe", r.Info!.ExeUrl, "minRuntime unmet: Clink.exe");

        DotNetRuntimes.Override = new[] { new Version(10, 0, 1) };
        Updater.PackagingOverride = Packaging.SelfContained;
        r = Run(Net(withFx), trust);
        Check.Equal(Gh + "v0.4.9/Clink.exe", r.Info!.ExeUrl, "a SelfContained copy: unchanged");
        Check.Equal(Sha, r.Info.Sha256);

        // A framework asset whose size GitHub and the manifest disagree on is refused, like Clink.exe's.
        byte[] wrongSize = Bytes(WithFramework(Manifest("0.4.9", size: 1234), "0.4.9").Replace("\"size\":777", "\"size\":778"));
        Updater.PackagingOverride = Packaging.Framework;
        r = Run(Net(wrongSize), trust);
        Check.Null(r.Info);
        Check.Equal(Updater.GitHubRefusedNote, r.Up.LastError);
    }

    [UnitTest]
    public static void Feed_signed_github_fallback_exe_must_be_https_too()
    {
        var (pub, key) = NewKey();
        using var k = key;
        // https Clink-framework.exe, but Clink.exe (the fallback when the manifest has no exeFramework) is an http URL.
        string fxAsset = $$"""{"name":"Clink-framework.exe","size":777,"browser_download_url":"{{Gh}}v0.4.9/Clink-framework.exe"},""";
        string release = Release("v0.4.9", exeSize: 1234).Replace("\"assets\":[", "\"assets\":[" + fxAsset).Replace($"\"{Gh}v0.4.9/Clink.exe\"", "\"http://insecure.example/Clink.exe\"");
        Check.Contains(release, "http://insecure.example/Clink.exe");
        byte[] withoutFx = Bytes(Manifest("0.4.9", size: 1234));
        var net = new FakeNet(); GitHub(net, release); GitHubManifest(net, "v0.4.9", withoutFx, Sign(key, withoutFx));
        net.Ok("http://insecure.example/Clink.exe", new byte[] { (byte)'M', (byte)'Z' });
        DotNetRuntimes.Override = new[] { new Version(10, 0, 1) };
        Updater.PackagingOverride = Packaging.Framework;
        var r = Run(net, Trust(stable: new[] { pub }), settings: new Settings { AutoUpdate = true });
        Check.Null(r.Info, "no offer");
        Check.Equal(Updater.GitHubRefusedNote, r.Up.LastError);
        Check.False(net.Asked.Any(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)), "nothing was requested over http");
    }

    [UnitTest]
    public static void Feed_unsigned_github_assets_must_be_https()
    {
        FakeNet Net(string release) { var n = new FakeNet(); GitHub(n, release); return n; }
        string plain = Release("v0.4.9", signedAssets: false);
        // Sanity: the untouched release is offered.
        Check.NotNull(Run(Net(plain), FeedTrust.None).Info, "the https release is offered");

        foreach (string asset in new[] { "Clink.exe.sha256", "Clink.exe" })
        {
            string bad = plain.Replace($"\"{Gh}v0.4.9/{asset}\"", $"\"http://insecure.example/{asset}\"");
            Check.Contains(bad, "http://insecure.example/" + asset);
            var net = Net(bad);
            var r = Run(net, FeedTrust.None);
            Check.Null(r.Info, $"http {asset}: no offer");
            Check.False(net.Asked.Any(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)), $"http {asset}: nothing requested over http");
        }

        // A framework copy: the http .sha256 of Clink-framework.exe is refused as well.
        string fxAssets = $$"""{"name":"Clink-framework.exe","size":777,"browser_download_url":"{{Gh}}v0.4.9/Clink-framework.exe"},{"name":"Clink-framework.exe.sha256","size":64,"browser_download_url":"http://insecure.example/Clink-framework.exe.sha256"},""";
        string fxRelease = plain.Replace("\"assets\":[", "\"assets\":[" + fxAssets);
        Updater.PackagingOverride = Packaging.Framework;
        var fxNet = Net(fxRelease);
        Check.Null(Run(fxNet, FeedTrust.None).Info, "http framework .sha256: no offer");
        Check.False(fxNet.Asked.Any(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)));
    }
}
