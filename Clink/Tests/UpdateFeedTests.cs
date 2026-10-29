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
            "installer":{"url":"https://cdn.example/clink/releases/Clink-Setup.exe","size":99,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
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
        Check.Contains(ParseProblem(Manifest("0.4.3-beta.1", "dev"), "dev"), "bad version", "not a canonical X.Y.Z / X.Y.Z-dev.N at all");
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

    // ------------------------------------------------------------------ devFeedUrl (two buckets)

    private const string DevBase = "https://dl-dev.example/clink-dev/";

    /// <summary>A dev manifest whose binaries (the installer included) sit under the dev base, except <paramref name="exeUrl"/> when given.</summary>
    private static string DevManifest(string version, string? exeUrl = null) =>
        Manifest(version, "dev", exeUrl: exeUrl ?? DevBase + $"releases/v{version}/Clink.exe")
            .Replace("https://cdn.example/clink/releases/Clink-Setup.exe", DevBase + "releases/Clink-Setup.exe");

    private static FeedTrust TwoBases(string[]? stable = null, string[]? dev = null, string? devUrl = DevBase)
    {
        var t = FeedTrust.Create(Base, stable ?? new[] { s_spareStableKey.Value }, dev ?? new[] { s_spareDevKey.Value }, null, devUrl);
        if (!t.On) throw new TestFailure("the test's trust is not on: " + string.Join("; ", t.Problems));
        return t;
    }

    [UnitTest]
    public static void DevFeedUrl_is_optional_and_validated_like_feedUrl()
    {
        string stable = SparePub(), dev = SparePub();
        var two = FeedTrust.Create(Base, new[] { stable }, new[] { dev }, null, DevBase);
        Check.Equal(FeedState.On, two.State);
        Check.Equal(Base, two.FeedUrl);
        Check.Equal(DevBase, two.DevFeedUrl);
        Check.Equal(Base, two.BaseFor("stable"));
        Check.Equal(DevBase, two.BaseFor("dev"));
        Check.Contains(two.Describe(), "devFeedUrl " + DevBase);

        foreach (string? none in new string?[] { null, "" })
        {
            var one = FeedTrust.Create(Base, new[] { stable }, new[] { dev }, null, none);
            Check.Equal(FeedState.On, one.State, $"devFeedUrl {(none ?? "null")}");
            Check.Equal(Base, one.DevFeedUrl, "absent or empty: the dev channel uses feedUrl");
            Check.Equal(Base, one.BaseFor("dev"));
            Check.False(one.Describe().Contains("devFeedUrl"), "nothing to say about it");
        }

        // The same validator, and a bad value is a misconfiguration (fail closed) exactly like a bad feedUrl.
        foreach (string bad in new[] { "http://dl-dev.example/clink-dev/", "https://dl-dev.example/clink-dev", "https://dl-dev.example/x/?a=b",
                                       "https://dl-dev.example/x/#f", "https://u:p@dl-dev.example/x/", "dl-dev.example/x/", "   ", "ftp://dl-dev.example/x/" })
        {
            var t = FeedTrust.Create(Base, new[] { stable }, new[] { dev }, null, bad);
            Check.Equal(FeedState.Misconfigured, t.State, $"'{bad}'");
            Check.Contains(string.Join("; ", t.Problems), "devFeedUrl is invalid", $"'{bad}'");
            Check.Null(t.FeedUrl, bad);
            Check.Null(t.DevFeedUrl, bad);
            Check.Equal(0, t.KeysFor("stable").Count + t.KeysFor("dev").Count, bad);
        }

        // devFeedUrl needs a feedUrl, with or without keys; it never turns the empty template on by itself.
        foreach (var keys in new[] { (new[] { stable }, new[] { dev }), (Array.Empty<string>(), Array.Empty<string>()) })
        {
            var t = FeedTrust.Create("", keys.Item1, keys.Item2, null, DevBase);
            Check.Equal(FeedState.Misconfigured, t.State);
            Check.Contains(string.Join("; ", t.Problems), "feedUrl is empty");
        }

        // feed.json: absent, null and "" all mean "use feedUrl"; a non-string is a problem.
        string Json(string extra) => $$"""{"schema":1,"feedUrl":"{{Base}}",{{extra}}"stableKeys":["{{stable}}"],"devKeys":["{{dev}}"]}""";
        Check.Equal(DevBase, FeedTrust.Parse(Bytes(Json($"\"devFeedUrl\":\"{DevBase}\","))).DevFeedUrl);
        foreach (string extra in new[] { "", "\"devFeedUrl\":\"\",", "\"devFeedUrl\":null," })
        {
            var t = FeedTrust.Parse(Bytes(Json(extra)));
            Check.Equal(FeedState.On, t.State, extra);
            Check.Equal(Base, t.DevFeedUrl, extra);
        }
        foreach (string extra in new[] { "\"devFeedUrl\":5,", "\"devFeedUrl\":[],", "\"devFeedUrl\":\"http://x.example/a/\"," })
        {
            var t = FeedTrust.Parse(Bytes(Json(extra)));
            Check.Equal(FeedState.Misconfigured, t.State, extra);
            Check.Null(t.DevFeedUrl, extra);
        }
        Check.Equal(FeedState.Misconfigured, FeedTrust.Parse(Bytes($$"""{"schema":1,"feedUrl":"","devFeedUrl":"{{DevBase}}","stableKeys":[],"devKeys":[]}""")).State);
        Check.Equal(FeedState.Off, FeedTrust.Parse(Bytes("""{"schema":1,"feedUrl":"","devFeedUrl":"","stableKeys":[],"devKeys":[]}""")).State, "the empty template with an empty devFeedUrl is still off");
    }

    [UnitTest]
    public static void Binary_urls_must_sit_under_the_manifests_own_base()
    {
        Check.True(UpdateFeed.IsUnder(Base + "releases/v1/Clink.exe", Base));
        Check.True(UpdateFeed.IsUnder("https://CDN.example/clink/releases/v1/Clink.exe", Base), "host case");
        foreach (string bad in new[]
                 {
                     "https://cdn.example/clink/", Base + "x?y=1", Base + "x#f", "http://cdn.example/clink/x.exe", "https://cdn.example:8443/clink/x.exe",
                     "https://evil.example/clink/x.exe", "https://cdn.example/clink-other/x.exe", "https://cdn.example/clinkx.exe", "https://cdn.example/x.exe",
                     "https://cdn.example/clink/../x.exe", "https://cdn.example/clink/%2e%2e/x.exe", "https://cdn.example/clink/a/..%2f..%2fx.exe",
                     "https://cdn.example/clink/a%5c..%5cx.exe", "https://user@cdn.example/clink/x.exe", "https://cdn.example.evil.example/clink/x.exe", "", "relative/x.exe",
                 })
            Check.False(UpdateFeed.IsUnder(bad, Base), $"'{bad}' is not under {Base}");
        Check.False(UpdateFeed.IsUnder(Base + "x.exe", DevBase), "the stable base is not the dev base");
    }

    [UnitTest]
    public static void Feed_dev_client_reads_stable_json_from_feedUrl_and_dev_json_from_the_dev_base()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = TwoBases(new[] { stablePub }, new[] { devPub });
        byte[] s = Bytes(Manifest("0.4.3")), d = Bytes(DevManifest("0.4.4-dev.1"));

        FakeNet Net()
        {
            var n = new FakeNet();
            n.Feed("stable", s, Sign(stableKey, s));
            n.Ok(DevBase + "dev.json", d);
            n.Ok(DevBase + "dev.json.sig", Bytes(Sign(devKey, d)));
            return n;
        }

        var net = Net();
        var r = Run(net, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.4-dev.1"), r.Info?.Version, r.Up.LastError);
        Check.Equal(DevBase + "releases/v0.4.4-dev.1/Clink.exe", r.Info!.ExeUrl);
        Check.True(net.Asked.Contains(Base + "stable.json") && net.Asked.Contains(Base + "stable.json.sig"), "stable.json under feedUrl");
        Check.True(net.Asked.Contains(DevBase + "dev.json") && net.Asked.Contains(DevBase + "dev.json.sig"), "dev.json under the dev base");
        Check.False(net.Asked.Contains(Base + "dev.json") || net.Asked.Contains(DevBase + "stable.json"), "neither file is looked for under the other base");
        Check.False(net.AskedGitHub);

        // A stable client never touches the dev base.
        var net2 = Net();
        Check.Equal(V("0.4.3"), Run(net2, trust, running: "0.4.2").Info?.Version);
        Check.False(net2.Asked.Any(u => u.StartsWith(DevBase, StringComparison.Ordinal)));

        // dev.json at the stable base only (the old single-base layout) is not found: the dev manifest is not used.
        var net3 = new FakeNet();
        net3.Feed("stable", s, Sign(stableKey, s));
        net3.Feed("dev", d, Sign(devKey, d));
        GitHub(net3, Release("v0.4.3"));
        var r3 = Run(net3, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.3"), r3.Info?.Version, "only stable.json is used: a dev.json under feedUrl is never read");
        Check.False(net3.Asked.Contains(Base + "dev.json"));

        // The keys stay per channel across the two bases: dev.json from the dev base signed with the stable key is refused.
        var net4 = Net();
        net4.Ok(DevBase + "dev.json.sig", Bytes(Sign(stableKey, d)));
        GitHub(net4, Release("v0.4.3"));
        var r4 = Run(net4, trust, dev: true, running: "0.4.3-dev.1");
        Check.Equal(V("0.4.3"), r4.Info?.Version, "only the stable.json is used, the forged dev.json is not");
        Check.Null(r4.Settings.FeedFloorDev);
    }

    [UnitTest]
    public static void Feed_refuses_a_manifest_whose_binaries_are_outside_its_base()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var two = TwoBases(new[] { stablePub }, new[] { devPub });
        var one = Trust(new[] { stablePub }, new[] { devPub });

        // stable.json naming a binary under the dev base, a foreign host, or a dot-dot escape.
        foreach (string exe in new[] { DevBase + "releases/v0.4.9/Clink.exe", "https://evil.example/clink/Clink.exe", Base + "../other/Clink.exe", "https://cdn.example/clink-evil/Clink.exe" })
        {
            var net = new FakeNet();
            byte[] m = Bytes(Manifest("0.4.9", exeUrl: exe));
            net.Feed("stable", m, Sign(stableKey, m));
            var r = Run(net, two);
            Check.Null(r.Info, exe);
            Check.True(net.AskedGitHub, exe + ": falls back to the signed GitHub path");
            Check.Null(r.Settings.FeedFloorStable, exe + ": raises no floor");
        }

        // dev.json under the dev base naming a binary under feedUrl (the stable base) is refused.
        var net2 = new FakeNet();
        byte[] dm = Bytes(DevManifest("0.4.9-dev.1", Base + "releases/v0.4.9-dev.1/Clink.exe"));
        net2.Ok(DevBase + "dev.json", dm);
        net2.Ok(DevBase + "dev.json.sig", Bytes(Sign(devKey, dm)));
        Check.Null(Run(net2, two, dev: true, running: "0.4.1").Info);
        Check.True(net2.AskedGitHub);
        // ...whereas with a single base that same manifest is fine.
        var net3 = new FakeNet();
        byte[] dm1 = Bytes(Manifest("0.4.9-dev.1", "dev")), sm1 = Bytes(Manifest("0.4.1"));
        net3.Feed("dev", dm1, Sign(devKey, dm1));
        net3.Feed("stable", sm1, Sign(stableKey, sm1));
        Check.Equal(V("0.4.9-dev.1"), Run(net3, one, dev: true, running: "0.4.1").Info?.Version);

        // The installer and the framework exe are binaries too.
        byte[] fxm = Bytes(Manifest("0.4.9").Replace("\"installer\":{\"url\":\"https://cdn.example/clink/releases/Clink-Setup.exe\"", "\"installer\":{\"url\":\"https://evil.example/Setup.exe\""));
        Check.Contains(Encoding.UTF8.GetString(fxm), "evil.example/Setup.exe");
        var net5 = new FakeNet();
        net5.Feed("stable", fxm, Sign(stableKey, fxm));
        Check.Null(Run(net5, one).Info, "the installer URL is outside the base");

        // The signed GitHub path does NOT apply the rule: it downloads GitHub's own ExeUrl and takes only the size and SHA-256 from the manifest,
        // so the manifest's URLs are never fetched (and a moved feed host must not strand old clients).
        var net6 = new FakeNet();
        byte[] gm = Bytes(Manifest("0.4.9", exeUrl: "https://evil.example/clink/Clink.exe"));
        GitHub(net6, Release("v0.4.9"));
        GitHubManifest(net6, "v0.4.9", gm, Sign(stableKey, gm));
        var r6 = Run(net6, one);
        Check.Equal(V("0.4.9"), r6.Info?.Version, r6.Up.LastError);
        Check.Equal(Gh + "v0.4.9/Clink.exe", r6.Info!.ExeUrl, "GitHub's ExeUrl, not the manifest's");
        Check.Equal(Sha, r6.Info.Sha256, "the manifest's hash");
        Check.False(r6.Info.FromFeed);
        // ...a dev release's manifest as well.
        var net7 = new FakeNet();
        byte[] gdm = Bytes(DevManifest("0.4.9-dev.1", Base + "releases/v0.4.9-dev.1/Clink.exe"));
        GitHub(net7, Release("v0.4.1"), Release("v0.4.9-dev.1", prerelease: true));
        GitHubManifest(net7, "v0.4.9-dev.1", gdm, Sign(devKey, gdm), "dev");
        var r7 = Run(net7, two, dev: true);
        Check.Equal(V("0.4.9-dev.1"), r7.Info?.Version, r7.Up.LastError);
        Check.Equal(Gh + "v0.4.9-dev.1/Clink.exe", r7.Info!.ExeUrl);
    }

    [UnitTest]
    public static void One_manifest_with_foreign_urls_is_accepted_on_the_github_path_and_refused_on_the_feed_path()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        byte[] m = Bytes(Manifest("0.4.9", exeUrl: "https://moved.example/other/Clink.exe"));

        var feed = new FakeNet();
        feed.Feed("stable", m, Sign(key, m));
        var viaFeed = Run(feed, trust);
        Check.Null(viaFeed.Info, "feed path: the binary is not under the feed's base");
        Check.True(feed.AskedGitHub);

        var gh = new FakeNet();
        GitHub(gh, Release("v0.4.9"));
        GitHubManifest(gh, "v0.4.9", m, Sign(key, m));
        var viaGitHub = Run(gh, trust);
        Check.Equal(V("0.4.9"), viaGitHub.Info?.Version, viaGitHub.Up.LastError);
        Check.Equal(Gh + "v0.4.9/Clink.exe", viaGitHub.Info!.ExeUrl);
    }

    // ------------------------------------------------------------------ NEW-2: a stale manifest cannot say "up to date"

    [UnitTest]
    public static void Feed_old_manifest_of_the_running_version_is_cross_checked_with_github()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        byte[] old = Bytes(Manifest("0.4.2", publishedDaysAgo: 15));

        // Nothing newer on GitHub: up to date, but only after GitHub was asked.
        var net = new FakeNet();
        net.Feed("stable", old, Sign(key, old));
        GitHub(net, Release("v0.4.2"));
        var r = Run(net, trust, running: "0.4.2");
        Check.Null(r.Info);
        Check.True(net.AskedGitHub, "a 15-day-old manifest does not end the check");
        Check.Null(r.Up.LastError);
        Check.False(r.Up.CheckFailed);
        Check.Null(r.Settings.FeedFloorStable, "the stale manifest is not what the answer rests on");

        // A newer signed release on GitHub (the feed was frozen): offered through the signed GitHub path.
        var net2 = new FakeNet();
        byte[] newer = Bytes(Manifest("0.4.9"));
        net2.Feed("stable", old, Sign(key, old));
        GitHub(net2, Release("v0.4.9"));
        GitHubManifest(net2, "v0.4.9", newer, Sign(key, newer));
        var r2 = Run(net2, trust, running: "0.4.2");
        Check.Equal(V("0.4.9"), r2.Info?.Version, r2.Up.LastError);
        Check.Equal(Gh + "v0.4.9/Clink.exe", r2.Info!.ExeUrl);
        Check.Equal("0.4.9", r2.Settings.FeedFloorStable);

        // A newer GitHub release with no valid signed manifest is not offered either, and GitHub naming something newer is never
        // "up to date": a failed check, not a soft one (round 2: it used to be softened).
        var net3 = new FakeNet();
        net3.Feed("stable", old, Sign(key, old));
        GitHub(net3, Release("v0.4.9", signedAssets: false));
        var r3 = Run(net3, trust, running: "0.4.2");
        Check.Null(r3.Info, "never offered");
        Check.Equal(Updater.GitHubRefusedNote, r3.Up.LastError);
        Check.True(r3.Up.CheckFailed);
        Check.Null(r3.Up.SoftNote);

        // GitHub cannot be reached: a soft result (see Stale_feed_manifest_soft_state for the clock), not a failed check.
        var net4 = new FakeNet();
        net4.Feed("stable", old, Sign(key, old));
        net4.Status(GitHubLatest, 503);
        var r4 = Run(net4, trust, running: "0.4.2");
        Check.Null(r4.Info);
        Check.Null(r4.Up.LastError);
        Check.False(r4.Up.CheckFailed);
        Check.Contains(r4.Up.SoftNote, "Could not confirm with GitHub");

        // A manifest under the limit still ends the check without GitHub; so does a manual check.
        foreach (int days in new[] { 0, 13 })
        {
            var net5 = new FakeNet();
            byte[] young = Bytes(Manifest("0.4.2", publishedDaysAgo: days));
            net5.Feed("stable", young, Sign(key, young));
            var r5 = Run(net5, trust, running: "0.4.2", manual: true);
            Check.Null(r5.Up.LastError, $"{days} days");
            Check.False(net5.AskedGitHub, $"{days} days old: up to date from the feed");
            Check.Equal("0.4.2", r5.Settings.FeedFloorStable);
        }
        Check.Equal(TimeSpan.FromDays(14), UpdateFeed.MaxUpToDateAge);

        // Dev channel: the dev manifest equal to the running dev build decides, not the (rarely re-signed) stable.json.
        var (devPub, devKey) = NewKey();
        using var dk = devKey;
        var devTrust = Trust(stable: new[] { pub }, dev: new[] { devPub });
        byte[] stableOld = Bytes(Manifest("0.4.1", publishedDaysAgo: 40)), devFresh = Bytes(Manifest("0.4.3-dev.2", "dev", publishedDaysAgo: 2));
        var net6 = new FakeNet();
        net6.Feed("stable", stableOld, Sign(key, stableOld));
        net6.Feed("dev", devFresh, Sign(devKey, devFresh));
        Check.False(Run(net6, devTrust, dev: true, running: "0.4.3-dev.2").Up.CheckFailed);
        Check.False(net6.AskedGitHub, "fresh dev.json equal to the running build: up to date");
        byte[] devOld = Bytes(Manifest("0.4.3-dev.2", "dev", publishedDaysAgo: 20));
        var net7 = new FakeNet();
        net7.Feed("stable", stableOld, Sign(key, stableOld));
        net7.Feed("dev", devOld, Sign(devKey, devOld));
        GitHub(net7, Release("v0.4.1"), Release("v0.4.3-dev.2", prerelease: true));
        Check.Null(Run(net7, devTrust, dev: true, running: "0.4.3-dev.2").Info);
        Check.True(net7.AskedGitHub, "a 20-day-old dev.json equal to the running build is cross-checked");
    }

    // ------------------------------------------------------------------ L2: GitHub's answer is capped

    /// <summary>A body without a Content-Length (chunked), like a server that does not say how much it will send.</summary>
    private sealed class UnsizedContent : HttpContent
    {
        private readonly byte[] _bytes;
        public UnsizedContent(byte[] bytes) { _bytes = bytes; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes, 0, _bytes.Length);
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    [UnitTest]
    public static void GitHub_answer_over_the_cap_fails_the_check()
    {
        byte[] huge = new byte[Updater.MaxApiBytes + 1];
        Array.Fill(huge, (byte)' ');
        foreach (var trust in new[] { FeedTrust.None, Trust() })
        foreach (bool dev in new[] { false, true })
        foreach (bool sized in new[] { true, false })
        {
            var net = new FakeNet();
            Func<HttpContent> make = () => sized ? new ByteArrayContent(huge) : new UnsizedContent(huge);
            net.Ok(GitHubLatest, make);
            net.Ok(GitHubList, make);
            string what = $"{(trust.On ? "feed on" : "feed off")}, dev {dev}, {(sized ? "declared" : "undeclared")} length";
            var r = Run(net, trust, dev: dev);
            Check.Null(r.Info, what);
            Check.True(r.Up.CheckFailed, what);
            Check.Contains(r.Up.LastError, "too large", what);
        }

        // A normal answer is well under the cap, and an answer of exactly the cap is read.
        Check.True(Updater.MaxApiBytes >= 1024 * 1024, "a wide margin over the real answers");
        var ok = new FakeNet();
        GitHub(ok, Release("v0.4.9", signedAssets: false));
        Check.Equal(V("0.4.9"), Run(ok, FeedTrust.None).Info?.Version);
    }

    [UnitTest]
    public static void Capped_reads_stop_at_the_limit()
    {
        string Read(byte[] body, int limit, bool sized)
        {
            using var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = sized ? new ByteArrayContent(body) : new UnsizedContent(body) };
            return OffUi(() => Updater.ReadCappedTextAsync(resp, limit, "the test answer", CancellationToken.None));
        }
        foreach (bool sized in new[] { true, false })
        {
            Check.Equal("0123456789", Read(Encoding.ASCII.GetBytes("0123456789"), 10, sized), "exactly the limit");
            Check.Equal("", Read(Array.Empty<byte>(), 10, sized));
            bool threw = false;
            try { Read(Encoding.ASCII.GetBytes("01234567890"), 10, sized); }
            catch (Exception ex) when (ex is InvalidDataException || ex.InnerException is InvalidDataException) { threw = true; }
            Check.True(threw, $"one byte over the limit ({(sized ? "declared" : "undeclared")}) fails closed");
        }
        Check.True(Updater.MaxShaFileBytes >= 4096 && Updater.MaxShaFileBytes <= Updater.MaxApiBytes);
    }

    // ------------------------------------------------------------------ repair round 1

    private static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [UnitTest]
    public static void Stale_feed_manifest_soft_state()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var t0 = DateTimeOffset.UtcNow;
        var now = t0;
        Updater.ClockOverride = () => now;
        try
        {
            byte[] stale = Bytes(Manifest("0.4.2", publishedDaysAgo: 20));
            string softText = SoftText("0.4.2", t0.AddDays(-20));
            FakeNet Net(bool github)
            {
                var n = new FakeNet();
                n.Feed("stable", stale, Sign(key, stale));
                if (github) GitHub(n, Release("v0.4.2")); else n.Status(GitHubLatest, 503);
                return n;
            }

            // stale + GitHub OK: confirms and sets both stamps for the running version (this channel's only)
            var net1 = Net(true);
            var r1 = Run(net1, trust, running: "0.4.2", settings: new Settings { FeedFloorStableSoftSince = Stamp("0.4.2", t0.AddDays(-2)) });
            Check.True(net1.AskedGitHub);
            Check.Null(r1.Up.LastError); Check.Null(r1.Up.SoftNote); Check.False(r1.Up.CheckFailed);
            Check.Equal(Stamp("0.4.2", t0), r1.Settings.FeedFloorStableGitHubConfirm);
            Check.Equal(Stamp("0.4.2", t0), r1.Settings.FeedFloorStableUpToDate);
            Check.Null(r1.Settings.FeedFloorStableSoftSince, "a real confirmation ends the unconfirmed streak");
            Check.Null(r1.Settings.FeedFloorDevGitHubConfirm); Check.Null(r1.Settings.FeedFloorDevUpToDate); Check.Null(r1.Settings.FeedFloorDevSoftSince);
            Check.Equal(t0.UtcDateTime, r1.Settings.LastUpdateCheck!.Value, "the fake clock is used for the check time too");

            // stale + GitHub confirmed 3 days ago: no GitHub call, still up to date
            var s2 = new Settings { FeedFloorStableGitHubConfirm = Stamp("0.4.2", t0.AddDays(-3)), FeedFloorStableUpToDate = Stamp("0.4.2", t0.AddDays(-3)) };
            var net2 = Net(false);
            var r2 = Run(net2, trust, running: "0.4.2", settings: s2);
            Check.False(net2.AskedGitHub, "within 7 days of the last GitHub answer GitHub is not asked");
            Check.Null(r2.Up.LastError); Check.Null(r2.Up.SoftNote); Check.False(r2.Up.CheckFailed);
            // ...but 8 days after it is
            now = t0.AddDays(5);
            var net2b = Net(true);
            Run(net2b, trust, running: "0.4.2", settings: s2);
            Check.True(net2b.AskedGitHub, "8 days after the last GitHub answer");
            now = t0;
            // the skip also needs the running version's UpToDate within MaxUnconfirmedStreak (round 2, defence in depth): a GitHubConfirm alone,
            // or with an UpToDate older than 14 days (only an edited settings.json has that), is not enough
            foreach (string? upToDate in new[] { null, Stamp("0.4.2", t0.AddDays(-15)) })
            {
                var net2c = Net(true);
                Run(net2c, trust, running: "0.4.2", settings: new Settings { FeedFloorStableGitHubConfirm = Stamp("0.4.2", t0.AddDays(-3)), FeedFloorStableUpToDate = upToDate });
                Check.True(net2c.AskedGitHub, $"UpToDate {upToDate ?? "absent"}");
            }
            Check.Equal(TimeSpan.FromDays(14), Updater.MaxUnconfirmedStreak);
            Check.Equal(TimeSpan.FromDays(7), Updater.GitHubConfirmValidity);

            // stale + GitHub 503 + last confirmation 10 days ago: soft result, not a failed check; the streak starts now
            var s3 = new Settings { FeedFloorStableUpToDate = Stamp("0.4.2", t0.AddDays(-10)), FeedFloorStableGitHubConfirm = Stamp("0.4.2", t0.AddDays(-10)) };
            var net3 = Net(false);
            var r3 = Run(net3, trust, running: "0.4.2", settings: s3);
            Check.True(net3.AskedGitHub);
            Check.Null(r3.Info);
            Check.Null(r3.Up.LastError);
            Check.False(r3.Up.CheckFailed);
            Check.Equal(softText, r3.Up.SoftNote);
            Check.Equal(Stamp("0.4.2", t0.AddDays(-10)), s3.FeedFloorStableUpToDate, "a soft result confirms nothing");
            Check.Equal(Stamp("0.4.2", t0), s3.FeedFloorStableSoftSince);

            // an unconfirmed streak that began 15 days ago: a real failure with a clear message (round 2: counted from SoftSince, not UpToDate)
            var s4 = new Settings { FeedFloorStableUpToDate = Stamp("0.4.2", t0.AddDays(-31)), FeedFloorStableGitHubConfirm = Stamp("0.4.2", t0.AddDays(-31)), FeedFloorStableSoftSince = Stamp("0.4.2", t0.AddDays(-15)) };
            var r4 = Run(Net(false), trust, running: "0.4.2", settings: s4);
            Check.True(r4.Up.CheckFailed);
            Check.Null(r4.Up.SoftNote);
            Check.Contains(r4.Up.LastError, "could not confirm that v0.4.2 is the latest");
            Check.Contains(r4.Up.LastError, "GitHub answered HTTP 503");
            Check.Contains(r4.Up.LastError, Day(t0.AddDays(-15)));
            Check.Equal(Stamp("0.4.2", t0.AddDays(-15)), s4.FeedFloorStableSoftSince, "a failure does not restart the streak");

            // a later GitHub success clears it
            var r5 = Run(Net(true), trust, running: "0.4.2", settings: s4);
            Check.False(r5.Up.CheckFailed); Check.Null(r5.Up.LastError); Check.Null(r5.Up.SoftNote);
            Check.Equal(Stamp("0.4.2", t0), s4.FeedFloorStableUpToDate);
            Check.Null(s4.FeedFloorStableSoftSince);
            now = t0.AddDays(8);   // GitHub's answer is older than 7 days again
            var r6 = Run(Net(false), trust, running: "0.4.2", settings: s4);
            now = t0;
            Check.False(r6.Up.CheckFailed, "and a new streak starts");
            Check.Equal(softText, r6.Up.SoftNote);
            Check.Equal(Stamp("0.4.2", t0.AddDays(8)), s4.FeedFloorStableSoftSince);

            // a network error is soft like a 503
            var net7 = Net(false);
            net7.Throw(GitHubLatest, () => new HttpRequestException("no route"));
            var r7 = Run(net7, trust, running: "0.4.2", settings: new Settings { FeedFloorStableUpToDate = Stamp("0.4.2", t0.AddDays(-1)) });
            Check.Equal(softText, r7.Up.SoftNote);
            Check.False(r7.Up.CheckFailed);

            // never confirmed at all: the 14 days start with the first unconfirmed check, then run out (round 2: 14 days, was 30)
            var s8 = new Settings();
            var r8 = Run(Net(false), trust, running: "0.4.2", settings: s8);
            Check.Equal(softText, r8.Up.SoftNote);
            Check.Equal(Stamp("0.4.2", t0), s8.FeedFloorStableSoftSince, "the streak starts now");
            Check.Null(s8.FeedFloorStableUpToDate, "and nothing is called confirmed");
            now = t0.AddDays(13);
            Check.False(Run(Net(false), trust, running: "0.4.2", settings: s8).Up.CheckFailed, "13 days later: still soft");
            now = t0.AddDays(15);
            var r9 = Run(Net(false), trust, running: "0.4.2", settings: s8);
            Check.True(r9.Up.CheckFailed, "15 days after the first unconfirmed check");
            now = t0;

            // a manifest under 14 days confirms the running version by itself (and asks nobody), and ends a streak
            byte[] young = Bytes(Manifest("0.4.2", publishedDaysAgo: 3));
            var net10 = new FakeNet();
            net10.Feed("stable", young, Sign(key, young));
            var r10 = Run(net10, trust, running: "0.4.2", settings: new Settings { FeedFloorStableSoftSince = Stamp("0.4.2", t0.AddDays(-5)) });
            Check.False(net10.AskedGitHub);
            Check.Equal(Stamp("0.4.2", t0), r10.Settings.FeedFloorStableUpToDate);
            Check.Null(r10.Settings.FeedFloorStableGitHubConfirm);
            Check.Null(r10.Settings.FeedFloorStableSoftSince);

            // a cancelled check is neither soft nor a confirmation
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var r11 = Run(Net(true), trust, running: "0.4.2", settings: new Settings(), ct: cts.Token);
            Check.Equal("cancelled", r11.Up.LastError);
            Check.Null(r11.Up.SoftNote);

            // dev channel: its own stamps
            var (devPub, devKey) = NewKey();
            using var dk = devKey;
            var devTrust = Trust(stable: new[] { pub }, dev: new[] { devPub });
            byte[] devOld = Bytes(Manifest("0.4.3-dev.2", "dev", publishedDaysAgo: 20)), stableOld = Bytes(Manifest("0.4.1", publishedDaysAgo: 40));
            var net12 = new FakeNet();
            net12.Feed("stable", stableOld, Sign(key, stableOld));
            net12.Feed("dev", devOld, Sign(devKey, devOld));
            GitHub(net12, Release("v0.4.1"), Release("v0.4.3-dev.2", prerelease: true));
            var r12 = Run(net12, devTrust, dev: true, running: "0.4.3-dev.2");
            Check.Null(r12.Up.LastError);
            Check.Equal(Stamp("0.4.3-dev.2", t0), r12.Settings.FeedFloorDevGitHubConfirm);
            Check.Equal(Stamp("0.4.3-dev.2", t0), r12.Settings.FeedFloorDevUpToDate);
            Check.Null(r12.Settings.FeedFloorStableGitHubConfirm); Check.Null(r12.Settings.FeedFloorStableUpToDate);
        }
        finally { Updater.ClockOverride = null; }
    }

    [UnitTest]
    public static void Stale_feed_state_is_saved_and_never_imported()
    {
        var at = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
        var s = new Settings { FeedFloorStableGitHubConfirm = Updater.FormatStamp(V("0.4.2"), at), FeedFloorDevUpToDate = Updater.FormatStamp(V("0.4.3-dev.1"), at.AddDays(1)), FeedFloorStableSoftSince = Updater.FormatStamp(V("0.4.2"), at) };
        Check.Equal("0.4.2@2026-10-20T08:00:00.0000000Z", s.FeedFloorStableGitHubConfirm);
        string json = s.ToJson();
        Check.Contains(json, "FeedFloorStableGitHubConfirm");
        Check.Contains(json, "FeedFloorStableSoftSince");
        Check.DoesNotContain(json, "FeedFloorDevGitHubConfirm", "left out while null");
        Check.DoesNotContain(json, "FeedFloorDevSoftSince", "left out while null");
        var back = Settings.FromJson(json);
        Check.Equal(s.FeedFloorStableGitHubConfirm, back.FeedFloorStableGitHubConfirm);
        Check.Equal(s.FeedFloorDevUpToDate, back.FeedFloorDevUpToDate);
        Check.Equal(s.FeedFloorStableSoftSince, back.FeedFloorStableSoftSince);
        Check.Equal(at, Updater.ParseStamp(back.FeedFloorStableGitHubConfirm, V("0.4.2"), at));
        Check.DoesNotContain(new Settings().ToJson(), "FeedFloor");
        foreach (string name in new[]
                 {
                     nameof(Settings.FeedFloorStableGitHubConfirm), nameof(Settings.FeedFloorDevGitHubConfirm), nameof(Settings.FeedFloorStableUpToDate),
                     nameof(Settings.FeedFloorDevUpToDate), nameof(Settings.FeedFloorStableSoftSince), nameof(Settings.FeedFloorDevSoftSince),
                 })
            Check.True(SettingsImport.IsOwnKey(name), name + " is this installation's own");
    }

    // ------------------------------------------------------------------ repair round 2

    /// <summary>A stamp as the updater stores it: "&lt;version&gt;@&lt;UTC round-trip&gt;".</summary>
    private static string Stamp(string version, DateTimeOffset t) => Updater.FormatStamp(V(version), t.UtcDateTime);

    /// <summary>The soft note for the stale feed manifest of <paramref name="version"/> published at <paramref name="published"/>.</summary>
    private static string SoftText(string version, DateTimeOffset published) =>
        $"Could not confirm with GitHub; the signed feed says v{version} (published {Day(published)}) is the latest";

    [UnitTest]
    public static void Stamps_are_tied_to_the_running_version_and_never_from_the_future()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var v = V("0.4.2");
        Check.Equal(now.AddDays(-3), Updater.ParseStamp(Updater.FormatStamp(v, now.AddDays(-3)), v, now));
        Check.Equal(now.AddHours(20), Updater.ParseStamp(Updater.FormatStamp(v, now.AddHours(20)), v, now), "under a day ahead: clock skew, still read");
        Check.Null(Updater.ParseStamp(Updater.FormatStamp(v, now.AddHours(25)), v, now), "more than a day ahead: absent");
        Check.Null(Updater.ParseStamp(Updater.FormatStamp(V("0.4.1"), now), v, now), "another version's stamp");
        Check.Null(Updater.ParseStamp(Updater.FormatStamp(V("0.4.2-dev.1"), now), v, now), "a dev build of the same number is another version");
        Check.Equal("0.4.2@2026-10-28T12:00:00.0000000Z", Updater.FormatStamp(v, DateTime.SpecifyKind(now, DateTimeKind.Unspecified)), "an unspecified kind is taken as UTC");
        foreach (string? bad in new string?[]
                 {
                     null, "", "@", "0.4.2", "0.4.2@", "@2026-10-28T12:00:00.0000000Z", "0.4.2@yesterday", "2026-10-28T12:00:00Z", "0.4.2@2026-10-28T12:00:00",
                     "0.4.2@2026-10-28T12:00:00.0000000+00:00", "0.4.2@2026-10-28T12:00:00.0000000", " 0.4.2@2026-10-28T12:00:00.0000000Z", "v0.4.2@2026-10-28T12:00:00.0000000Z",
                     "0.4.2@2026-10-28T12:00:00.0000000Z@x", "0.4.2@" + new string('9', 200),
                 })
            Check.Null(Updater.ParseStamp(bad, v, now), $"'{bad}' counts as absent");
    }

    /// <summary>The feed says v0.4.2 (the running version) in a 20-day-old manifest; GitHub is set up by the caller.</summary>
    private static FakeNet StaleEqualFeed(ECDsa key)
    {
        byte[] stale = Bytes(Manifest("0.4.2", publishedDaysAgo: 20));
        var n = new FakeNet();
        n.Feed("stable", stale, Sign(key, stale));
        return n;
    }

    [UnitTest]
    public static void A_newer_github_release_that_cannot_be_verified_is_a_failed_check_and_github_is_asked_again()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var t0 = DateTimeOffset.UtcNow;
        var now = t0;
        Updater.ClockOverride = () => now;
        try
        {
            // GitHub's latest is v0.4.3, but its stable.json asset cannot be downloaded (503) / it has no signed assets at all.
            foreach (bool assets in new[] { true, false })
            {
                string what = assets ? "stable.json asset 503" : "no signed assets";
                FakeNet Net()
                {
                    var n = StaleEqualFeed(key);
                    GitHub(n, Release("v0.4.3", signedAssets: assets));
                    if (assets) { n.Status(Gh + "v0.4.3/stable.json", 503); n.Status(Gh + "v0.4.3/stable.json.sig", 503); }
                    return n;
                }
                var settings = new Settings();
                var net = Net();
                var r = Run(net, trust, running: "0.4.2", settings: settings);
                Check.True(net.AskedGitHub, what);
                Check.Null(r.Info, what);
                Check.True(r.Up.CheckFailed, what);
                Check.Equal(Updater.GitHubRefusedNote, r.Up.LastError, what);
                Check.Null(r.Up.SoftNote, what + ": GitHub named a newer release, so nothing is soft");
                Check.Null(settings.FeedFloorStableGitHubConfirm, what + ": GitHub said a newer release exists, which confirms nothing");
                Check.Null(settings.FeedFloorStableUpToDate, what);
                Check.Null(settings.FeedFloorStableSoftSince, what + ": a real failure is no soft streak");
                // the next check (an hour later) asks GitHub again instead of calling the stale feed up to date
                now = t0.AddHours(1);
                var net2 = Net();
                var r2 = Run(net2, trust, running: "0.4.2", settings: settings);
                now = t0;
                Check.True(net2.AskedGitHub, what + ": asked again");
                Check.True(r2.Up.CheckFailed, what);
                Check.Null(r2.Up.SoftNote, what);
            }

            // a newer release whose manifest is for another tag is refused the same way (not softened)
            byte[] wrongTag = Bytes(Manifest("0.4.4"));
            var net3 = StaleEqualFeed(key);
            GitHub(net3, Release("v0.4.3"));
            GitHubManifest(net3, "v0.4.3", wrongTag, Sign(key, wrongTag));
            var r3 = Run(net3, trust, running: "0.4.2");
            Check.Equal(Updater.GitHubRefusedNote, r3.Up.LastError);
            Check.True(r3.Up.CheckFailed);
            Check.Null(r3.Up.SoftNote);
        }
        finally { Updater.ClockOverride = null; }
    }

    [UnitTest]
    public static void A_verified_newer_github_release_is_offered_again_on_the_next_check()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var t0 = DateTimeOffset.UtcNow;
        var now = t0;
        Updater.ClockOverride = () => now;
        try
        {
            byte[] m3 = Bytes(Manifest("0.4.3"));
            FakeNet Net()
            {
                var n = StaleEqualFeed(key);
                GitHub(n, Release("v0.4.3"));
                GitHubManifest(n, "v0.4.3", m3, Sign(key, m3));
                return n;
            }
            var settings = new Settings();
            var r = Run(Net(), trust, running: "0.4.2", settings: settings);
            Check.Equal(V("0.4.3"), r.Info?.Version, r.Up.LastError);
            Check.Null(settings.FeedFloorStableGitHubConfirm, "an offer is not 'nothing newer'");
            Check.Null(settings.FeedFloorStableUpToDate);
            // offered but not installed (AutoUpdate off, the download failed, the user said no): two days later GitHub is asked again
            now = t0.AddDays(2);
            var net2 = Net();
            var r2 = Run(net2, trust, running: "0.4.2", settings: settings);
            Check.True(net2.AskedGitHub, "within 7 days of the offer GitHub is still asked");
            Check.Equal(V("0.4.3"), r2.Info?.Version, "and offers it again");
        }
        finally { Updater.ClockOverride = null; }
    }

    [UnitTest]
    public static void Only_a_github_that_could_not_be_asked_is_soft()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var t0 = DateTimeOffset.UtcNow;
        Updater.ClockOverride = () => t0;
        try
        {
            string softText = SoftText("0.4.2", t0.AddDays(-20));
            // soft: GitHub down, rate-limited, unreachable or too slow
            var soft = new List<(string Name, Action<FakeNet> Setup)>
            {
                ("HTTP 500", n => n.Status(GitHubLatest, 500)), ("HTTP 503", n => n.Status(GitHubLatest, 503)),
                ("HTTP 429", n => n.Status(GitHubLatest, 429)), ("HTTP 403", n => n.Status(GitHubLatest, 403)),
                ("network error", n => n.Throw(GitHubLatest, () => new HttpRequestException("no route"))),
                ("I/O error", n => n.Throw(GitHubLatest, () => new IOException("connection reset"))),
                ("timeout", n => n.RoutesAsync[GitHubLatest] = StalledHeaders),
            };
            Updater.GitHubTimeoutOverride = TimeSpan.FromMilliseconds(300);
            foreach (var (name, setup) in soft)
            {
                var net = StaleEqualFeed(key);
                setup(net);
                var r = Run(net, trust, running: "0.4.2");
                Check.Null(r.Up.LastError, name);
                Check.False(r.Up.CheckFailed, name);
                Check.Equal(softText, r.Up.SoftNote, name);
            }
            // not soft: GitHub answered, and the answer is no confirmation
            var hard = new List<(string Name, Action<FakeNet> Setup, string Error)>
            {
                ("HTTP 404", n => n.Status(GitHubLatest, 404), "no releases found"),
                ("HTTP 401", n => n.Status(GitHubLatest, 401), "HTTP 401"),
                ("unusable release", n => n.Ok(GitHubLatest, Bytes("""{"tag_name":"latest","draft":false,"prerelease":false}""")), "not vX.Y.Z"),
                ("no exe", n => n.Ok(GitHubLatest, Bytes("""{"tag_name":"v0.4.3","draft":false,"prerelease":false,"assets":[]}""")), "no Clink.exe"),
                ("not JSON", n => n.Ok(GitHubLatest, Bytes("<html>captive portal</html>")), ""),
                ("too large", n => n.Ok(GitHubLatest, new byte[Updater.MaxApiBytes + 1]), "too large"),
            };
            foreach (var (name, setup, error) in hard)
            {
                var net = StaleEqualFeed(key);
                setup(net);
                var r = Run(net, trust, running: "0.4.2");
                Check.True(r.Up.CheckFailed, name);
                Check.Contains(r.Up.LastError, error, name);
                Check.Null(r.Up.SoftNote, name);
                Check.Null(r.Settings.FeedFloorStableSoftSince, name);
            }
            // a cancelled check is neither
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var rc = Run(StaleEqualFeed(key), trust, running: "0.4.2", ct: cts.Token);
            Check.Equal("cancelled", rc.Up.LastError);
            Check.Null(rc.Up.SoftNote);
        }
        finally { Updater.ClockOverride = null; Updater.GitHubTimeoutOverride = null; }
    }

    [UnitTest]
    public static void An_old_confirmation_does_not_make_the_first_unconfirmed_check_a_failure()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var t0 = DateTimeOffset.UtcNow;
        var now = t0;
        Updater.ClockOverride = () => now;
        try
        {
            FakeNet Down() { var n = StaleEqualFeed(key); n.Status(GitHubLatest, 503); return n; }
            // switched to the dev channel 60 days ago and back now (or Clink not used for two months): stable's stamps are old
            var settings = new Settings { FeedFloorStableUpToDate = Stamp("0.4.2", t0.AddDays(-60)), FeedFloorStableGitHubConfirm = Stamp("0.4.2", t0.AddDays(-60)) };
            var r1 = Run(Down(), trust, running: "0.4.2", settings: settings);
            Check.False(r1.Up.CheckFailed, "the first unconfirmed check is soft");
            Check.Equal(SoftText("0.4.2", t0.AddDays(-20)), r1.Up.SoftNote);
            Check.Equal(Stamp("0.4.2", t0), settings.FeedFloorStableSoftSince);
            // 14 days into the streak: still soft; a moment after: a failed check
            now = t0.AddDays(14);
            Check.False(Run(Down(), trust, running: "0.4.2", settings: settings).Up.CheckFailed, "14 days into the streak");
            now = t0.AddDays(14).AddHours(1);
            var r3 = Run(Down(), trust, running: "0.4.2", settings: settings);
            Check.True(r3.Up.CheckFailed, "over 14 days into the streak");
            Check.Contains(r3.Up.LastError, "since " + Day(t0));
            Check.Null(r3.Up.SoftNote);
        }
        finally { Updater.ClockOverride = null; }
    }

    [UnitTest]
    public static void Stamps_of_another_version_or_from_the_future_confirm_nothing()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var t0 = DateTimeOffset.UtcNow;
        Updater.ClockOverride = () => t0;
        try
        {
            FakeNet Up() { var n = StaleEqualFeed(key); GitHub(n, Release("v0.4.2")); return n; }
            FakeNet Down() { var n = StaleEqualFeed(key); n.Status(GitHubLatest, 503); return n; }

            // recorded for 0.4.1 (the version before the update), running 0.4.2: GitHub is asked
            var old = new Settings { FeedFloorStableGitHubConfirm = Stamp("0.4.1", t0.AddDays(-1)), FeedFloorStableUpToDate = Stamp("0.4.1", t0.AddDays(-1)) };
            var net1 = Up();
            var r1 = Run(net1, trust, running: "0.4.2", settings: old);
            Check.True(net1.AskedGitHub, "0.4.1's stamps do not count for 0.4.2");
            Check.Equal(Stamp("0.4.2", t0), old.FeedFloorStableGitHubConfirm, "replaced by the running version's own");
            // ...and 0.4.1's streak is not 0.4.2's: a fresh one starts, soft
            var oldStreak = new Settings { FeedFloorStableSoftSince = Stamp("0.4.1", t0.AddDays(-20)) };
            var r2 = Run(Down(), trust, running: "0.4.2", settings: oldStreak);
            Check.False(r2.Up.CheckFailed);
            Check.NotNull(r2.Up.SoftNote);
            Check.Equal(Stamp("0.4.2", t0), oldStreak.FeedFloorStableSoftSince);

            // stamps more than a day in the future: absent, GitHub is asked (they grant nothing)
            var future = new Settings { FeedFloorStableGitHubConfirm = Stamp("0.4.2", t0.AddDays(3)), FeedFloorStableUpToDate = Stamp("0.4.2", t0.AddDays(3)) };
            var net3 = Up();
            Run(net3, trust, running: "0.4.2", settings: future);
            Check.True(net3.AskedGitHub, "future stamps are not a confirmation");
            // a future streak start is absent too: the streak starts now (it neither fails nor extends anything)
            var futureStreak = new Settings { FeedFloorStableSoftSince = Stamp("0.4.2", t0.AddDays(30)) };
            var r4 = Run(Down(), trust, running: "0.4.2", settings: futureStreak);
            Check.False(r4.Up.CheckFailed);
            Check.Equal(Stamp("0.4.2", t0), futureStreak.FeedFloorStableSoftSince);
            // under a day ahead is clock skew: still counted
            var skew = new Settings { FeedFloorStableGitHubConfirm = Stamp("0.4.2", t0.AddHours(12)), FeedFloorStableUpToDate = Stamp("0.4.2", t0.AddHours(12)) };
            var net5 = Down();
            var r5 = Run(net5, trust, running: "0.4.2", settings: skew);
            Check.False(net5.AskedGitHub, "12 hours ahead counts");
            Check.False(r5.Up.CheckFailed);
            // unreadable stamps (round 1's bare dates among them) are absent
            var bad = new Settings { FeedFloorStableGitHubConfirm = "2026-10-20T08:00:00Z", FeedFloorStableUpToDate = "garbage" };
            var net6 = Up();
            Run(net6, trust, running: "0.4.2", settings: bad);
            Check.True(net6.AskedGitHub);
        }
        finally { Updater.ClockOverride = null; }
    }

    [UnitTest]
    public static void Feed_offers_download_through_the_feed_client_and_github_offers_through_the_github_client()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        byte[] exe = FakeExe();
        var seen = new List<(string Url, bool Feed)>();
        Updater.SendObserver = (url, feed) => { lock (seen) seen.Add((url, feed)); };

        // the feed offers v0.4.2: the manifest, its signature and the exe all go through the feed's own (no-redirect) client
        byte[] fm = Bytes(Manifest("0.4.2", size: exe.Length, sha: Hex(exe)));
        var net = new FakeNet();
        net.Feed("stable", fm, Sign(key, fm));
        const string feedExe = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";
        net.Ok(feedExe, exe);
        var r = Run(net, trust);
        Check.True(r.Info!.FromFeed);
        string path = OffUi(() => r.Up.DownloadAsync(r.Info, null, CancellationToken.None));
        Check.Equal(exe, File.ReadAllBytes(path));
        Check.Equal(new[] { (Base + "stable.json", true), (Base + "stable.json.sig", true), (feedExe, true) }, seen.ToArray());

        // the feed is down, GitHub offers v0.4.2 with a signed manifest: the API, the manifest assets and the exe go through GitHub's client
        lock (seen) seen.Clear();
        File.Delete(path);
        var net2 = new FakeNet();
        GitHub(net2, Release("v0.4.2", exeSize: exe.Length));
        GitHubManifest(net2, "v0.4.2", fm, Sign(key, fm));
        net2.Ok(Gh + "v0.4.2/Clink.exe", exe);
        var r2 = Run(net2, trust);
        Check.False(r2.Info!.FromFeed);
        string path2 = OffUi(() => r2.Up.DownloadAsync(r2.Info, null, CancellationToken.None));
        Check.Equal(exe, File.ReadAllBytes(path2));
        Check.True(seen.Where(s => s.Url.StartsWith(Base, StringComparison.Ordinal)).All(s => s.Feed), "the feed's own requests use the feed client");
        var github = seen.Where(s => !s.Url.StartsWith(Base, StringComparison.Ordinal)).ToArray();
        Check.Equal(new[] { (GitHubLatest, false), (Gh + "v0.4.2/stable.json", false), (Gh + "v0.4.2/stable.json.sig", false), (Gh + "v0.4.2/Clink.exe", false) }, github);
        Check.Equal(TimeSpan.FromSeconds(30), Updater.FeedClientTimeout, "the feed client has a timeout (connect + headers)");
    }

    [UnitTest]
    public static void A_download_server_that_never_answers_fails_in_time_and_releases_the_gate()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        byte[] exe = FakeExe();
        byte[] fm = Bytes(Manifest("0.4.2", size: exe.Length, sha: Hex(exe)));
        const string feedExe = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";
        Updater.FeedOverride = trust;
        Updater.BuiltInRepoOverride = BuiltIn;
        Updater.DownloadAnswerTimeoutOverride = TimeSpan.FromMilliseconds(300);
        Updater.DownloadReadTimeoutOverride = TimeSpan.FromMilliseconds(300);
        foreach (var (name, setup, error) in new (string, Action<FakeNet>, string)[]
                 {
                     ("headers never come", n => n.RoutesAsync[feedExe] = StalledHeaders, "did not answer within 0.3 s"),
                     ("body never comes", n => n.Ok(feedExe, () => new StreamContent(new StalledStream())), "stalled"),
                 })
        {
            var net = new FakeNet();
            net.Feed("stable", fm, Sign(key, fm));
            setup(net);
            Updater.HttpOverride = net.Send;
            var up = new Updater(new Settings { AutoUpdate = true }, V("0.4.1"));
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var check = Task.Run(() => up.CheckNowAsync());
                // bounded, so that a download that does hang fails this test instead of hanging the run
                Check.True(check.Wait(TimeSpan.FromSeconds(10)), $"{name}, attempt {attempt}: still running after {sw.Elapsed}");
                var info = check.Result;
                Check.Equal(V("0.4.2"), info?.Version, $"{name}, attempt {attempt}");
                Check.Contains(up.LastError, "downloading v0.4.2 failed", $"{name}, attempt {attempt}");
                Check.Contains(up.LastError, error, $"{name}, attempt {attempt}");
                Check.Equal(1, up._gate.CurrentCount, $"{name}, attempt {attempt}: the gate is free again");
                Check.False(up.IsDownloading, name);
                Check.Null(up.PendingPath, name);
            }
            Check.Equal(2, net.Asked.Count(u => u == feedExe), name + ": the second check was not blocked by the first");
        }
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
        r.Headers.Location = new Uri(to);
        return r;
    }

    [UnitTest]
    public static void Feed_redirects_are_not_followed()
    {
        Check.False(Updater.FeedHandler.AllowAutoRedirect, "the feed's own client never follows a redirect");
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        byte[] m = Bytes(Manifest("0.4.9"));
        const string elsewhere = "https://evil.example/stable.json";

        // the manifest redirects: unreachable, so the (signed) GitHub path is asked; the target is never requested
        var net = new FakeNet();
        net.Feed("stable", m, Sign(key, m));
        net.Routes[Base + "stable.json"] = _ => Redirect(elsewhere);
        net.Ok(elsewhere, m);
        var r = Run(net, trust);
        Check.Null(r.Info);
        Check.True(net.AskedGitHub);
        Check.False(net.Asked.Contains(elsewhere));
        // ...so does the signature
        var net2 = new FakeNet();
        net2.Feed("stable", m, Sign(key, m));
        net2.Routes[Base + "stable.json.sig"] = _ => Redirect("https://evil.example/stable.json.sig");
        Check.Null(Run(net2, trust).Info);
        Check.True(net2.AskedGitHub);

        // a feed binary that redirects fails the download
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        const string url = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";
        var net3 = new FakeNet();
        net3.Routes[url] = _ => Redirect("https://evil.example/Clink.exe");
        net3.Ok("https://evil.example/Clink.exe", exe);
        Updater.HttpOverride = net3.Send;
        var up = new Updater(new Settings(), V("0.4.1"));
        var info = new UpdateInfo(V("0.4.2"), "v0.4.2", url, exe.Length, "", "", "", Hex(exe), FromFeed: true);
        Check.Throws<HttpRequestException>(() => OffUi(() => up.DownloadAsync(info, null, CancellationToken.None)));
        Check.False(net3.Asked.Contains("https://evil.example/Clink.exe"));
        Check.True(UpdateFeed.ToUpdateInfo(UpdateFeed.ParseManifest(m, "stable", out _)!).FromFeed, "feed offers carry the flag");
    }

    [UnitTest]
    public static void Feed_notes_url_that_is_not_github_falls_back_to_the_latest_releases_page()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        const string latest = "https://github.com/o/r/releases/latest";
        foreach (var (notes, expected) in new[]
                 {
                     ("https://example.invalid/notes", latest), ("https://github.com/evil/repo/releases/latest", latest),
                     ("https://github.com/o/r/releases/download/v0.4.9/Clink.exe", latest), ("https://github.com/o/r/releases/latest?x=1", latest),
                     ("", latest), ("https://github.com/o/r/releases/tag/v0.4.9", "https://github.com/o/r/releases/tag/v0.4.9"),
                 })
        {
            byte[] m = Bytes(Manifest("0.4.9", notes: notes));
            var net = new FakeNet();
            net.Feed("stable", m, Sign(key, m));
            var r = Run(net, trust);
            Check.Equal(V("0.4.9"), r.Info?.Version, r.Up.LastError);
            Check.Equal(notes, r.Info!.HtmlUrl, "the feed path's HtmlUrl is the manifest's notesUrl");
            Check.Equal(expected, r.Up.ReleasePageFor(r.Info), $"notesUrl '{notes}'");
        }
        Check.Equal(latest, new Updater(new Settings(), V("0.4.1")).ReleasePageFor(null));
    }

    private sealed class LyingLengthContent : HttpContent
    {
        private readonly long _length;
        public LyingLengthContent(long length) { _length = length; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("the body must not be read");
        protected override bool TryComputeLength(out long length) { length = _length; return true; }
    }

    [UnitTest]
    public static void Unsigned_download_is_capped_by_the_listed_size_or_a_hard_ceiling()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        const string url = "https://gh.example/v0.4.2/Clink.exe";
        var settings = new Settings();
        var up = new Updater(settings, V("0.4.1"));
        string[] Left() => Directory.Exists(Updater.UpdatesDir) ? Directory.GetFiles(Updater.UpdatesDir) : Array.Empty<string>();
        UpdateInfo Info(long size) => new(V("0.4.2"), "v0.4.2", url, size, url + ".sha256", "", "");
        FakeNet Net(Func<HttpContent> exeBody)
        {
            var n = new FakeNet();
            n.Ok(url + ".sha256", Bytes(Hex(exe) + "  Clink.exe\n"));
            n.Ok(url, exeBody);
            return n;
        }

        // listed size known: a Content-Length above it is refused before the body is read
        var net = Net(() => new LyingLengthContent(exe.Length + 1));
        Updater.HttpOverride = net.Send;
        Check.Contains(Check.Throws<InvalidDataException>(() => OffUi(() => up.DownloadAsync(Info(exe.Length), null, CancellationToken.None))).Message, "size mismatch");
        Check.Equal(Array.Empty<string>(), Left());

        // ...and an undeclared body that runs past it is cut off
        var endless = new EndlessStream();
        var net2 = Net(() => new StreamContent(endless));
        Updater.HttpOverride = net2.Send;
        Check.Throws<InvalidDataException>(() => OffUi(() => up.DownloadAsync(Info(exe.Length), null, CancellationToken.None)));
        Check.True(endless.Served < 1 << 20, $"read {endless.Served} bytes");
        Check.Equal(Array.Empty<string>(), Left());

        // size unknown (0): the hard ceiling applies
        var net3 = Net(() => new LyingLengthContent(Updater.MaxExeBytes + 1));
        Updater.HttpOverride = net3.Send;
        Check.Throws<InvalidDataException>(() => OffUi(() => up.DownloadAsync(Info(0), null, CancellationToken.None)));
        Check.Equal(512L * 1024 * 1024, Updater.MaxExeBytes);
        Check.Equal(Array.Empty<string>(), Left());

        // the honest download still works, with and without a listed size
        foreach (long size in new[] { (long)exe.Length, 0L })
        {
            var ok = Net(() => new ByteArrayContent(exe));
            Updater.HttpOverride = ok.Send;
            string path = OffUi(() => up.DownloadAsync(Info(size), null, CancellationToken.None));
            Check.Equal(exe, File.ReadAllBytes(path), $"size {size}");
            File.Delete(path);
        }
    }

    [UnitTest]
    public static void Unsigned_sha_file_over_64_KiB_fails_the_download_before_the_exe_is_asked_for()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        const string url = "https://gh.example/v0.4.2/Clink.exe";
        var up = new Updater(new Settings(), V("0.4.1"));
        var info = new UpdateInfo(V("0.4.2"), "v0.4.2", url, 300, url + ".sha256", "", "");
        // The cap is 64 KiB, pinned: a body one byte over 64 KiB (not over whatever the constant says) fails, so raising the cap (or
        // reading the hash file with another limit) fails this test.
        Check.Equal(64 * 1024, Updater.MaxShaFileBytes);
        byte[] big = new byte[64 * 1024 + 1];
        Array.Fill(big, (byte)' ');
        foreach (bool sized in new[] { true, false })
        {
            var net = new FakeNet();
            net.Ok(url + ".sha256", () => sized ? new ByteArrayContent(big) : new UnsizedContent(big));
            net.Ok(url, FakeExe());
            Updater.HttpOverride = net.Send;
            var ex = Check.Throws<InvalidDataException>(() => OffUi(() => up.DownloadAsync(info, null, CancellationToken.None)), sized ? "declared" : "undeclared");
            Check.Contains(ex.Message, "too large");
            Check.False(net.Asked.Contains(url), "the exe is never requested");
        }

        // ...and a hash file of exactly MaxShaFileBytes is read (the hash first, padded with spaces): lowering the cap fails too
        byte[] exe = FakeExe();
        byte[] exact = new byte[Updater.MaxShaFileBytes];
        Array.Fill(exact, (byte)' ');
        Encoding.ASCII.GetBytes(Hex(exe)).CopyTo(exact, 0);
        foreach (bool sized in new[] { true, false })
        {
            var net = new FakeNet();
            net.Ok(url + ".sha256", () => sized ? new ByteArrayContent(exact) : new UnsizedContent(exact));
            net.Ok(url, exe);
            Updater.HttpOverride = net.Send;
            string path = OffUi(() => up.DownloadAsync(info with { ExeSize = exe.Length }, null, CancellationToken.None));
            Check.Equal(exe, File.ReadAllBytes(path), sized ? "declared" : "undeclared");
            File.Delete(path);
        }
    }

    [UnitTest]
    public static void IsUnder_edge_cases()
    {
        // an explicit default port is the same origin
        Check.True(UpdateFeed.IsUnder("https://cdn.example:443/clink/x.exe", Base));
        Check.True(UpdateFeed.IsUnder(Base + "x.exe", "https://cdn.example:443/clink/"));
        // an encoded letter in the base's own path never matches (fail closed)
        Check.False(UpdateFeed.IsUnder("https://cdn.example/%63link/x.exe", Base));
        // %2F in the rest stays one segment name (accepted); with dots it is the dot rule that refuses
        Check.True(UpdateFeed.IsUnder(Base + "a%2Fb.exe", Base));
        foreach (string bad in new[] { Base + "%2e%2e%2fx.exe", Base + "a%2F..%2Fx.exe", Base + "a%2f%2e%2e/x.exe", Base + "a%5Cb.exe", Base + "%2e/x.exe" })
            Check.False(UpdateFeed.IsUnder(bad, Base), bad);
        // empty segments
        Check.False(UpdateFeed.IsUnder(Base + "a//x.exe", Base));
        Check.False(UpdateFeed.IsUnder(Base + "/x.exe", Base));
        Check.False(UpdateFeed.IsUnder("https://cdn.example//clink/x.exe", Base));
        // trailing dot: a different host, either way round
        Check.False(UpdateFeed.IsUnder("https://cdn.example./clink/x.exe", Base));
        Check.False(UpdateFeed.IsUnder(Base + "x.exe", "https://cdn.example./clink/"));
        // IDN hosts are compared as their punycode form
        const string idnBase = "https://bücher.example/clink/";
        Check.True(UpdateFeed.IsUnder("https://bücher.example/clink/x.exe", idnBase));
        Check.True(UpdateFeed.IsUnder("https://BÜCHER.example/clink/x.exe", idnBase), "case");
        Check.True(UpdateFeed.IsUnder("https://xn--bcher-kva.example/clink/x.exe", idnBase), "same host, written as punycode");
        Check.False(UpdateFeed.IsUnder("https://bucher.example/clink/x.exe", idnBase));
        // dotless i (U+0131) and the Kelvin sign (U+212A) are not the ASCII letters they resemble
        Check.False(UpdateFeed.IsUnder("https://ınfo.example/clink/x.exe", "https://info.example/clink/"), "U+0131");
        Check.False(UpdateFeed.IsUnder("https://info.example/clink/x.exe", "https://ınfo.example/clink/"), "U+0131, the other way");
        // .NET's IDN mapping folds the Kelvin sign into an ASCII k, so "Kelvin.example" IS kelvin.example (the request goes to that very
        // host, which is the base's host: nothing escapes); what it can never do is stand in for a different name.
        Check.True(UpdateFeed.IsUnder("https://Kelvin.example/clink/x.exe", "https://kelvin.example/clink/"), "U+212A is folded to k: the same host");
        Check.False(UpdateFeed.IsUnder("https://Kelvin.example/clink/x.exe", "https://elvin.example/clink/"), "U+212A is not 'e' or nothing");
        Check.False(UpdateFeed.IsUnder("https://Kelvin.example/clink/x.exe", "https://kelvim.example/clink/"), "U+212A");
        Check.False(UpdateFeed.IsUnder("https://cdn.example/clink/x.exe", "https://cdn.example/clink/ı/"));
    }

    [UnitTest]
    public static void Nested_feed_bases_are_a_misconfiguration()
    {
        string stable = SparePub(), dev = SparePub();
        FeedTrust Make(string url, string devUrl) => FeedTrust.Create(url, new[] { stable }, new[] { dev }, null, devUrl);
        foreach (var (a, b) in new[]
                 {
                     (Base, Base + "dev/"), (Base + "stable/", Base), ("https://cdn.example/", Base), (Base, "https://CDN.example/clink/dev/"),
                 })
        {
            var t = Make(a, b);
            Check.Equal(FeedState.Misconfigured, t.State, $"{a} / {b}");
            Check.Contains(string.Join("; ", t.Problems), "nested", $"{a} / {b}");
            Check.Null(t.FeedUrl); Check.Null(t.DevFeedUrl);
        }
        // siblings, the very same base (also written differently) and an absent devFeedUrl are fine
        Check.Equal(FeedState.On, Make(Base, "https://cdn.example/clink-dev/").State, "a sibling whose name merely starts the same");
        Check.Equal(FeedState.On, Make(Base, DevBase).State);
        Check.Equal(FeedState.On, Make(Base, Base).State);
        Check.Equal(FeedState.On, Make(Base, "https://CDN.example:443/clink/").State, "the same base, written differently");
        Check.Equal(FeedState.On, Make(Base, "").State);
        // the same through feed.json
        string json = $$"""{"schema":1,"feedUrl":"{{Base}}","devFeedUrl":"{{Base}}dev/","stableKeys":["{{stable}}"],"devKeys":["{{dev}}"]}""";
        Check.Equal(FeedState.Misconfigured, FeedTrust.Parse(Bytes(json)).State);

        // only a devFeedUrl: the problem is the missing feedUrl, and it does not claim that keys are listed
        var only = FeedTrust.Create("", Array.Empty<string>(), Array.Empty<string>(), null, DevBase);
        Check.Equal(FeedState.Misconfigured, only.State);
        string text = string.Join("; ", only.Problems);
        Check.Contains(text, "devFeedUrl is set but feedUrl is empty");
        Check.DoesNotContain(text, "keys are listed");
        Check.Contains(string.Join("; ", Make("", DevBase).Problems), "keys are listed", "with keys it still says so");
    }

    private sealed class SlowBody : Stream
    {
        private readonly byte[] _bytes; private readonly TimeSpan _delay; private bool _done;
        public SlowBody(byte[] bytes, TimeSpan delay) { _bytes = bytes; _delay = delay; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(_delay, ct).ConfigureAwait(false);
            if (_done) return 0;
            _done = true;
            _bytes.CopyTo(buffer);
            return _bytes.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    [UnitTest]
    public static void GitHub_headers_and_body_share_one_total_timeout()
    {
        Updater.GitHubTimeoutOverride = TimeSpan.FromMilliseconds(600);
        try
        {
            byte[] body = Bytes(Release("v0.4.9", signedAssets: false));
            // each phase alone is quicker than the limit (350 ms of 600), together they are not
            var net = new FakeNet();
            net.RoutesAsync[GitHubLatest] = async ct =>
            {
                await Task.Delay(350, ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowBody(body, TimeSpan.FromMilliseconds(350))) };
            };
            var r = Run(net, FeedTrust.None);
            Check.Null(r.Info);
            Check.True(r.Up.CheckFailed);
            Check.Contains(r.Up.LastError, "did not answer");

            // and a quick answer is fine
            var ok = new FakeNet();
            GitHub(ok, Release("v0.4.9", signedAssets: false));
            Check.Equal(V("0.4.9"), Run(ok, FeedTrust.None).Info?.Version);
        }
        finally { Updater.GitHubTimeoutOverride = null; }
    }
}
