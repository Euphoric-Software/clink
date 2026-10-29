using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Clink.Services;

namespace Clink.Tests;

// The signed static update feed (UpdateFeed, FeedTrust) and the Updater's decisions around it. The client talks to its own feed and to
// nothing else (the source repository is not public), and these tests prove it: the HTTP layer is a fake (Updater.HttpOverride) that
// FAILS the test on any request to a host other than the configured feed hosts. No network.
internal static class UpdateFeedTests
{
    private const string Base = "https://cdn.example/clink/";
    private static readonly string Sha = new('a', 64);

    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure($"'{text}' is not a version");

    /// <summary>"days ago" on the updater's clock (the fake one when a test set it), so a manifest's `published` and a test's expected date never straddle midnight.</summary>
    private static string Ago(int days) => (Updater.ClockOverride?.Invoke() ?? DateTimeOffset.UtcNow).AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>A manifest. The default notesUrl is where the publisher puts it: <c>&lt;base&gt;releases/&lt;tag&gt;/notes.txt</c> (stable) or <c>&lt;base&gt;dev/&lt;tag&gt;/notes.txt</c> (dev).</summary>
    private static string Manifest(string version = "0.4.2", string channel = "stable", int schema = 1, string? tag = null,
        string? exeUrl = null, string? sha = null, long size = 1234, bool exe = true, string extra = "", string? notes = null,
        int publishedDaysAgo = 1, bool published = true)
    {
        exeUrl ??= $"https://cdn.example/clink/releases/v{version}/Clink.exe";
        notes ??= $"{Base}{(channel == "dev" ? "dev" : "releases")}/v{version}/notes.txt";
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

    /// <summary>
    /// URL to response; anything not listed is a 404. Records every URL asked for. A request to a host that is not one of
    /// <see cref="Hosts"/> (the configured feed hosts: <see cref="Run"/> sets them from the trust under test) throws a
    /// <see cref="TestFailure"/> and is recorded in <see cref="Stray"/>: the client must never contact anything else, GitHub least of all.
    /// </summary>
    private sealed class FakeNet
    {
        public readonly Dictionary<string, Func<CancellationToken, HttpResponseMessage>> Routes = new();
        /// <summary>Answers that take their time (a stalled server); looked up before <see cref="Routes"/>.</summary>
        public readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> RoutesAsync = new();
        public readonly List<string> Asked = new();
        /// <summary>The only hosts a request may go to (the hosts of <see cref="Base"/> and <see cref="DevBase"/> unless <see cref="Run"/> narrows them).</summary>
        public readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "cdn.example", "dl-dev.example" };
        /// <summary>Every request to another host (each also threw).</summary>
        public readonly List<string> Stray = new();

        public void Ok(string url, byte[] body) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        public void Ok(string url, Func<HttpContent> content) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content() };
        public void Status(string url, int code) => Routes[url] = _ => new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("error") };
        /// <summary>An error answer with a reason phrase of the server's choosing (which must never reach the user).</summary>
        public void Status(string url, int code, string reason) => Routes[url] = _ => new HttpResponseMessage((HttpStatusCode)code) { ReasonPhrase = reason, Content = new StringContent("error") };
        public void Throw(string url, Func<Exception> ex) => Routes[url] = _ => throw ex();

        public void Feed(string channel, byte[] manifest, string sig) => FeedAt(Base, channel, manifest, sig);

        public void FeedAt(string feedBase, string channel, byte[] manifest, string sig)
        {
            Ok(feedBase + channel + ".json", manifest);
            Ok(feedBase + channel + ".json.sig", Bytes(sig));
        }

        public Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
        {
            string url = req.RequestUri!.AbsoluteUri;
            lock (Asked) Asked.Add(url);
            if (!Hosts.Contains(req.RequestUri.Host))
            {
                lock (Stray) Stray.Add(url);
                TestRunner.RecordStray("a request to a host that is not a feed host: " + url);   // fails the test even where the updater swallows the throw
                throw new TestFailure($"the client contacted {req.RequestUri.Host}, which is not a configured feed host ({string.Join(", ", Hosts)}): {url}");
            }
            ct.ThrowIfCancellationRequested();
            if (RoutesAsync.TryGetValue(url, out var slow)) return slow(ct);
            if (Routes.TryGetValue(url, out var r)) return Task.FromResult(r(ct));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });
        }

        public bool AskedGitHub => Asked.Any(u => u.Contains("github", StringComparison.OrdinalIgnoreCase));
        public bool AskedFeed => Asked.Any(u => u.StartsWith(Base, StringComparison.Ordinal));
        public bool AskedSha256File => Asked.Any(u => u.EndsWith(".sha256", StringComparison.Ordinal));

        /// <summary>Narrows the allowed hosts to the feed hosts of <paramref name="trust"/> (none at all when it has no feed).</summary>
        public void AllowOnly(FeedTrust trust)
        {
            Hosts.Clear();
            foreach (string? u in new[] { trust.FeedUrl, trust.DevFeedUrl })
                if (u != null) Hosts.Add(new Uri(u).Host);
        }

        public void AssertNoStray() => Check.Equal(Array.Empty<string>(), Stray.ToArray(), "requests to a host that is not a feed host");
    }

    private sealed record Result(UpdateInfo? Info, Updater Up, Settings Settings);

    /// <summary>One Updater.CheckAsync (0.4.1 on the stable channel unless told) against the fake network, which is limited to the trust's own hosts; the runner resets the hooks after the test.</summary>
    private static Result Run(FakeNet net, FeedTrust trust, bool dev = false, string running = "0.4.1", bool manual = false, Settings? settings = null, CancellationToken ct = default)
    {
        settings ??= new Settings();
        settings.UseDevBuilds = dev;
        net.AllowOnly(trust);
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = trust;
        var up = new Updater(settings, V(running));
        var info = OffUi(() => up.CheckAsync(ct, manual));
        net.AssertNoStray();
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
        Check.Equal(1234L, info.ExeSize);
        Check.Equal("v0.4.2", info.Tag);
        Check.Equal(Base + "releases/v0.4.2/notes.txt", info.NotesUrl, "the manifest's notesUrl as signed (checked only when it is opened)");
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
        Check.Contains(ParseProblem(Manifest(published: false)), "published");
        Check.Contains(ParseProblem(Manifest().Replace(Ago(1), "yesterday")), "published");
        Check.Contains(ParseProblem("not json"), "JSON");
        Check.Contains(ParseProblem("[1]"), "object");
        Check.Contains(ParseProblem("""{"channel":"stable"}"""), "schema");

        // A notesUrl that is not https (or not even a URL) does not refuse the manifest: it only means the fallback page when opened.
        foreach (string notes in new[] { "http://example.invalid/n", "not a url", "" })
            Check.Null(ParseProblem(Manifest(notes: notes)), $"notesUrl '{notes}'");
    }

    [UnitTest]
    public static void Feed_ignores_unknown_fields()
    {
        string json = Manifest(extra: """ "future":{"a":[1,2]}, "flag":true, """).Replace("\"installer\"", "\"mystery\":{\"x\":1},\"installer\"");
        Check.Null(ParseProblem(json));
    }

    [UnitTest]
    public static void Feed_floor_reason_has_no_age_rule()
    {
        var m = UpdateFeed.ParseManifest(Bytes(Manifest("0.4.2")), "stable", out _)!;
        Check.Null(UpdateFeed.FloorReason(m, null));
        Check.Null(UpdateFeed.FloorReason(m, V("0.4.2")), "equal to the floor is fine");
        Check.Null(UpdateFeed.FloorReason(m, V("0.4.1")));
        Check.NotNull(UpdateFeed.FloorReason(m, V("0.4.3")), "below the floor: a replayed old manifest");
        Check.Contains(UpdateFeed.FloorReason(m, V("0.4.3")), "older than v0.4.3");

        // Neither a very old nor a future `published` makes a manifest unusable: it is parsed, and that is all.
        foreach (int daysAgo in new[] { 200, 4000, -3, -400 })
        {
            var old = UpdateFeed.ParseManifest(Bytes(Manifest("0.4.2", publishedDaysAgo: daysAgo)), "stable", out string? problem);
            Check.NotNull(old, $"published {daysAgo} days ago: {problem}");
            Check.Null(UpdateFeed.FloorReason(old!, null), $"published {daysAgo} days ago");
        }
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
        Check.Contains(empty.Describe(), "updates are off");
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
    /// feed.json was filled in): the embedded trust must be exactly that. With =on, CLINK_EXPECT_FEED_URL and CLINK_EXPECT_DEV_FEED_URL must
    /// both be given and equal (ordinally) the resolved stable and dev bases; with =off neither may be given. Nothing set (a local run): only
    /// that feed.json has no problems (a pass, not a skip, so CLINK_TESTS_NO_SKIP stays green locally).
    /// </summary>
    [UnitTest]
    public static void Embedded_feed_trust_matches_ci_expectation()
    {
        var embedded = FeedTrust.LoadEmbedded();
        Check.Equal(Array.Empty<string>(), embedded.Problems.ToArray(), "problems of the embedded feed.json");
        CheckCiExpectation(embedded, Environment.GetEnvironmentVariable("CLINK_EXPECT_FEED"),
            Environment.GetEnvironmentVariable("CLINK_EXPECT_FEED_URL"), Environment.GetEnvironmentVariable("CLINK_EXPECT_DEV_FEED_URL"));
    }

    /// <summary>
    /// The contract of <see cref="Embedded_feed_trust_matches_ci_expectation"/> for given variable values. Null and "" are alike "not
    /// given" (on Windows, <c>$env:X = ''</c> deletes the variable, so an empty value cannot be told from an absent one anyway).
    /// </summary>
    private static void CheckCiExpectation(FeedTrust embedded, string? expect, string? stableUrl, string? devUrl)
    {
        expect = string.IsNullOrWhiteSpace(expect) ? null : expect.Trim();
        stableUrl = string.IsNullOrEmpty(stableUrl) ? null : stableUrl;
        devUrl = string.IsNullOrEmpty(devUrl) ? null : devUrl;
        if (expect == null)
        {
            // No switch: a URL that is given is still compared (as before), nothing is required.
            if (stableUrl != null) ExpectBase("CLINK_EXPECT_FEED_URL", stableUrl, embedded.BaseFor("stable"));
            if (devUrl != null) ExpectBase("CLINK_EXPECT_DEV_FEED_URL", devUrl, embedded.BaseFor("dev"));
            return;
        }
        switch (expect.ToLowerInvariant())
        {
            case "off":
                if (stableUrl != null || devUrl != null)
                    throw new TestFailure($"CLINK_EXPECT_FEED=off, but a feed URL is expected (CLINK_EXPECT_FEED_URL \"{stableUrl}\", CLINK_EXPECT_DEV_FEED_URL \"{devUrl}\")");
                Check.Equal(FeedState.Off, embedded.State, $"CLINK_EXPECT_FEED=off: {embedded.Describe()}");
                break;
            case "on":
                if (stableUrl == null) throw new TestFailure("CLINK_EXPECT_FEED=on needs CLINK_EXPECT_FEED_URL");
                if (devUrl == null) throw new TestFailure("CLINK_EXPECT_FEED=on needs CLINK_EXPECT_DEV_FEED_URL");
                Check.Equal(FeedState.On, embedded.State, $"CLINK_EXPECT_FEED=on: {embedded.Describe()}");
                ExpectBase("CLINK_EXPECT_FEED_URL", stableUrl, embedded.BaseFor("stable"));
                ExpectBase("CLINK_EXPECT_DEV_FEED_URL", devUrl, embedded.BaseFor("dev"));
                break;
            default:
                throw new TestFailure($"CLINK_EXPECT_FEED must be \"off\" or \"on\", not \"{expect}\"");
        }
    }

    private static void ExpectBase(string variable, string expected, string? resolved)
    {
        if (!string.Equals(expected, resolved, StringComparison.Ordinal))
            throw new TestFailure($"{variable} is \"{expected}\" but the embedded feed.json resolves to \"{resolved ?? "(none)"}\"");
    }

    [UnitTest]
    public static void The_ci_feed_expectation_contract()
    {
        var off = FeedTrust.None;
        var on = Trust(url: Base);
        string stable = on.BaseFor("stable")!, dev = on.BaseFor("dev")!;
        bool Fails(FeedTrust t, string? e, string? s, string? d)
        {
            try { CheckCiExpectation(t, e, s, d); return false; }
            catch (TestFailure) { return true; }
        }
        // nothing given (a local run): a silent pass whatever the build; "" is the same as unset
        Check.False(Fails(off, null, null, null)); Check.False(Fails(on, null, null, null));
        Check.False(Fails(on, "", "", ""), "empty = not given");
        // on: both URLs, exactly
        Check.False(Fails(on, "on", stable, dev));
        Check.False(Fails(on, " ON ", stable, dev), "the switch is trimmed and case-insensitive");
        Check.True(Fails(on, "on", stable, null), "the dev URL is missing");
        Check.True(Fails(on, "on", null, dev), "the stable URL is missing");
        Check.True(Fails(on, "on", stable, ""), "an empty dev URL is a missing one");
        Check.True(Fails(on, "on", stable.ToUpperInvariant(), dev), "compared ordinally");
        Check.True(Fails(on, "on", stable, dev + "x/"));
        Check.True(Fails(off, "on", stable, dev), "the build has no feed");
        // off: no URL may be given
        Check.False(Fails(off, "off", null, null));
        Check.False(Fails(off, "off", "", ""));
        Check.True(Fails(off, "off", stable, null), "off with a URL");
        Check.True(Fails(off, "off", null, dev), "off with a dev URL");
        Check.True(Fails(on, "off", null, null), "the build has a feed");
        // anything else is a mistake in CI
        Check.True(Fails(on, "yes", stable, dev));
        // a URL without the switch is compared, nothing more
        Check.False(Fails(on, null, stable, null));
        Check.True(Fails(off, null, stable, null), "the build has no feed to match it");
    }

    // ------------------------------------------------------------------ feed off: no updates at all

    [UnitTest]
    public static void Feed_off_build_makes_no_http_requests_and_says_updates_arent_available()
    {
        var empty = FeedTrust.Parse(Bytes("""{"schema":1,"feedUrl":"","stableKeys":[],"devKeys":[]}"""));
        Check.Equal(FeedState.Off, empty.State);
        Check.Equal("Updates aren't available in this build", Updater.NoUpdatesNote, "the text the Updates card shows");
        var seen = new List<string>();
        Updater.SendObserver = url => { lock (seen) seen.Add(url); };

        foreach (bool dev in new[] { false, true })
        foreach (bool manual in new[] { false, true })
        {
            string what = $"dev {dev}, manual {manual}";
            var net = new FakeNet();   // no host is allowed at all
            // settings.json's legacy UpdateRepo key is ignored: it does not turn anything on
            var r = Run(net, empty, dev: dev, manual: manual, settings: new Settings { UpdateRepo = "me/fork", AutoUpdate = true });
            Check.Null(r.Info, what);
            Check.Equal(Updater.NoUpdatesNote, r.Up.LastError, what);
            Check.True(r.Up.CheckFailed, what);
            Check.False(r.Up.UpdatesEnabled, what);
            Check.Equal(0, net.Asked.Count, what + ": no request at all");
            Check.Null(r.Settings.FeedFloorStable, what);
            Check.Null(r.Settings.FeedFloorDev, what);
            Check.Null(r.Settings.FeedFloorFailingSince, what + ": nothing is tracked for a build that never checks");
            Check.Null(r.Settings.LastUpdateCheck, what + ": no check happened");
        }

        // The paths the app uses: a manual check and a background pass.
        var net2 = new FakeNet();
        net2.AllowOnly(empty);
        Updater.HttpOverride = net2.Send;
        Updater.FeedOverride = empty;
        using var up = new Updater(new Settings { AutoUpdate = true, UpdateRepo = "me/fork" }, V("0.4.1"));
        Check.False(up.UpdatesEnabled);
        Check.Null(up.LastError, "a build without a feed is not an error to report at start-up: the card has its own text");
        Check.Null(OffUi(() => up.CheckNowAsync()));
        Check.Equal(Updater.NoUpdatesNote, up.LastError);
        Check.Null(OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None)));
        Check.False(up.TryGetPendingUpdate(out _, out _), "nothing is installed either");
        Check.Throws<InvalidOperationException>(() => OffUi(() => up.DownloadAsync(
            new UpdateInfo(V("0.4.2"), "v0.4.2", Base + "releases/v0.4.2/Clink.exe", 300, Sha, ""), null, CancellationToken.None)), "not even a download");
        net2.AssertNoStray();
        Check.Equal(0, net2.Asked.Count, "no request by any path");
        Check.Equal(0, seen.Count, "nothing reached the send hook either");
    }

    [UnitTest]
    public static void Updates_are_enabled_only_with_a_valid_feed_and_outside_a_dev_instance()
    {
        Updater.FeedOverride = Trust();
        Check.True(new Updater(new Settings(), V("0.4.1")).UpdatesEnabled);
        Updater.FeedOverride = FeedTrust.None;
        Check.False(new Updater(new Settings(), V("0.4.1")).UpdatesEnabled, "feed off");
        Updater.FeedOverride = FeedTrust.Create(Base, new[] { SparePub() }, null);
        Check.False(new Updater(new Settings(), V("0.4.1")).UpdatesEnabled, "feed misconfigured");
        // the legacy key never enables or redirects anything
        Updater.FeedOverride = FeedTrust.None;
        Check.False(new Updater(new Settings { UpdateRepo = "owner/repo" }, V("0.4.1")).UpdatesEnabled);
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
                UpdateRepo = "me/fork", PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateSignedSha256 = Hex(exe), PendingUpdateFrom = "0.4.1",
            };
            var net = new FakeNet();
            byte[] m = Bytes(Manifest("0.4.9"));
            net.Feed("stable", m, Sign(key, m));
            var r = Run(net, trust, settings: settings);
            Check.Equal(FeedState.Misconfigured, trust.State, name);
            Check.Null(r.Info, name);
            Check.Equal(Array.Empty<string>(), net.Asked.ToArray(), name + ": nothing is asked");
            Check.Equal(Updater.MisconfiguredNote, r.Up.LastError, name);
            Check.True(r.Up.CheckFailed, name);
            Check.True(r.Up.LastError!.StartsWith("update configuration invalid", StringComparison.Ordinal), name);
            Check.False(r.Up.UpdatesEnabled, name + ": updates are off (the UI shows it, the loop does not start)");
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

    // ------------------------------------------------------------------ feed on: verified manifests

    [UnitTest]
    public static void Feed_verified_and_newer_is_offered_from_the_feed()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest());
        net.Feed("stable", manifest, Sign(key, manifest) + "\n");
        var r = Run(net, Trust(stable: new[] { pub }));
        Check.NotNull(r.Info);
        Check.Equal(V("0.4.2"), r.Info!.Version);
        Check.Equal(Sha, r.Info.Sha256);
        Check.Equal(Base + "releases/v0.4.2/notes.txt", r.Info.NotesUrl);
        Check.Equal(new[] { Base + "stable.json", Base + "stable.json.sig" }, net.Asked.ToArray(), "manifest, then signature; nothing else");
        Check.Null(r.Up.LastError);
        Check.False(r.Up.CheckFailed);
        Check.Equal("0.4.2", r.Settings.FeedFloorStable, "the floor is raised after verification");
        Check.Null(r.Settings.FeedFloorFailingSince);
        Check.NotNull(r.Settings.LastUpdateCheck);
    }

    [UnitTest]
    public static void Feed_same_version_is_up_to_date()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var net = new FakeNet();
        byte[] manifest = Bytes(Manifest("0.4.2"));
        net.Feed("stable", manifest, Sign(key, manifest));
        var r = Run(net, Trust(stable: new[] { pub }), running: "0.4.2");
        Check.Null(r.Info);
        Check.Null(r.Up.LastError);
        Check.False(r.Up.CheckFailed);
        Check.Equal("0.4.2", r.Settings.FeedFloorStable);
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
        Check.Equal("0.4.3", r.Settings.FeedFloorStable);
        Check.Equal("0.4.3-dev.2", r.Settings.FeedFloorDev);

        // dev.json is newer: the dev build.
        var net2 = new FakeNet();
        byte[] d2 = Bytes(Manifest("0.4.4-dev.1", "dev"));
        net2.Feed("stable", s, Sign(stableKey, s));
        net2.Feed("dev", d2, Sign(devKey, d2));
        Check.Equal(V("0.4.4-dev.1"), Run(net2, trust, dev: true, running: "0.4.3-dev.1").Info!.Version);

        // The stable channel never reads dev.json.
        var net4 = new FakeNet();
        net4.Feed("stable", s, Sign(stableKey, s));
        net4.Feed("dev", d2, Sign(devKey, d2));
        Check.Equal(V("0.4.3"), Run(net4, trust, running: "0.4.2").Info!.Version);
        Check.False(net4.Asked.Any(u => u.Contains("dev.json")));
    }

    [UnitTest]
    public static void Feed_dev_channel_a_failed_manifest_is_never_up_to_date()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        var (_, wrongKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        using var c = wrongKey;
        var trust = Trust(stable: new[] { stablePub }, dev: new[] { devPub });
        byte[] s = Bytes(Manifest("0.4.3")), d = Bytes(Manifest("0.4.4-dev.1", "dev"));

        // Nothing newer than the running dev build in what verified, and one manifest failed: the check fails (half the picture is no
        // "up to date"), with the failed manifest's host-only message. A manifest that did verify still raises its own floor.
        void Failed(string name, FakeNet net, string expectStart, string? stableFloor, string? devFloor, string running = "0.4.4-dev.1")
        {
            var r = Run(net, trust, dev: true, running: running);
            Check.Null(r.Info, name);
            Check.True(r.Up.CheckFailed, name + ": not 'up to date'");
            Check.True(r.Up.LastError!.StartsWith(expectStart, StringComparison.Ordinal), $"{name}: {r.Up.LastError}");
            Check.Contains(r.Up.LastError, "(cdn.example)", name);
            Check.Equal(stableFloor, r.Settings.FeedFloorStable, name + ": only a manifest that verified raises its floor");
            Check.Equal(devFloor, r.Settings.FeedFloorDev, name);
            Check.NotNull(r.Settings.FeedFloorFailingSince, name + ": a failed check");
            Check.False(net.AskedGitHub, name);
        }

        // stable verified + dev refused (signed with the wrong key)
        var net1 = new FakeNet();
        net1.Feed("stable", s, Sign(stableKey, s));
        net1.Feed("dev", d, Sign(wrongKey, d));
        Failed("dev refused", net1, "Couldn't verify the update server's answer", "0.4.3", null);

        // The keys are per channel: a stable.json signed with the dev key is refused.
        var net2 = new FakeNet();
        net2.Feed("stable", s, Sign(devKey, s));
        net2.Feed("dev", d, Sign(devKey, d));
        Failed("stable signed with the dev key", net2, "Couldn't verify the update server's answer", null, "0.4.4-dev.1");

        // stable verified + dev unreachable
        var net3 = new FakeNet();
        net3.Feed("stable", s, Sign(stableKey, s));
        net3.Status(Base + "dev.json", 503);
        Failed("dev unreachable", net3, "Couldn't reach the update server", "0.4.3", null);

        // dev verified + stable unreachable
        var net4 = new FakeNet();
        net4.Feed("dev", d, Sign(devKey, d));
        Failed("stable unreachable", net4, "Couldn't reach the update server", null, "0.4.4-dev.1");

        // A plain release in dev.json is refused (releases are published in stable.json only).
        var net5 = new FakeNet();
        byte[] plainDev = Bytes(Manifest("0.4.5", "dev"));
        net5.Feed("stable", s, Sign(stableKey, s));
        net5.Feed("dev", plainDev, Sign(devKey, plainDev));
        Failed("a plain release in dev.json", net5, "Couldn't verify the update server's answer", "0.4.3", null);

        // Both failed: the first one's message.
        var net6f = new FakeNet();
        net6f.Status(Base + "stable.json", 503);
        net6f.Feed("dev", d, Sign(wrongKey, d));
        Failed("both failed", net6f, "Couldn't reach the update server (cdn.example): HTTP 503", null, null);

        // The running version is what stable.json says (up to date there) but dev.json fails: still a failed check.
        var net7 = new FakeNet();
        net7.Feed("stable", s, Sign(stableKey, s));
        net7.Ok(Base + "dev.json", d);   // dev.json.sig is a 404
        Failed("stable up to date, dev signature missing", net7, "Couldn't verify the update server's answer", "0.4.3", null, running: "0.4.3");

        // Both verified, the running dev build is the newest: up to date, both floors raised.
        var net6 = new FakeNet();
        net6.Feed("stable", s, Sign(stableKey, s));
        net6.Feed("dev", d, Sign(devKey, d));
        var r6 = Run(net6, trust, dev: true, running: "0.4.4-dev.1");
        Check.Null(r6.Info);
        Check.Null(r6.Up.LastError);
        Check.Equal("0.4.3", r6.Settings.FeedFloorStable);
        Check.Equal("0.4.4-dev.1", r6.Settings.FeedFloorDev);
    }

    [UnitTest]
    public static void Feed_dev_channel_a_failed_manifest_does_not_hide_a_verified_offer()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = TwoBases(new[] { stablePub }, new[] { devPub });
        string devBase = trust.BaseFor("dev")!;
        byte[] s = Bytes(Manifest("0.4.9")), d = Bytes(DevManifest("0.4.4-dev.1"));

        // stable.json verifies 0.4.9, dev.json.sig is a 404: 0.4.9 is offered (it is newer than the running dev build whatever dev.json says).
        var net1 = new FakeNet();
        net1.Feed("stable", s, Sign(stableKey, s));
        net1.Ok(devBase + "dev.json", d);
        var r1 = Run(net1, trust, dev: true, running: "0.4.3-dev.5");
        Check.Equal(V("0.4.9"), r1.Info?.Version, r1.Up.LastError);
        Check.Null(r1.Up.LastError);
        Check.False(r1.Up.CheckFailed, "an offer is a working check");
        Check.Equal("0.4.9", r1.Settings.FeedFloorStable, "the verified manifest raises its floor");
        Check.Null(r1.Settings.FeedFloorDev, "the failed one does not");
        Check.True(net1.Asked.Contains(devBase + "dev.json.sig"), "dev.json was asked for, and failed");

        // dev.json verifies a newer dev build, stable.json is unreachable: the dev build is offered.
        var net2 = new FakeNet();
        net2.Status(Base + "stable.json", 503);
        net2.FeedAt(devBase, "dev", d, Sign(devKey, d));
        var r2 = Run(net2, trust, dev: true, running: "0.4.3-dev.5");
        Check.Equal(V("0.4.4-dev.1"), r2.Info?.Version, r2.Up.LastError);
        Check.Null(r2.Settings.FeedFloorStable);
        Check.Equal("0.4.4-dev.1", r2.Settings.FeedFloorDev);

        // A floor refusal on dev counts like a failed fetch: with a newer stable, stable is offered; the dev floor stays where it was.
        var net3 = new FakeNet();
        net3.Feed("stable", s, Sign(stableKey, s));
        net3.FeedAt(devBase, "dev", d, Sign(devKey, d));
        var r3 = Run(net3, trust, dev: true, running: "0.4.3-dev.5", settings: new Settings { FeedFloorDev = "0.4.4-dev.7" });
        Check.Equal(V("0.4.9"), r3.Info?.Version, r3.Up.LastError);
        Check.Equal("0.4.4-dev.7", r3.Settings.FeedFloorDev, "a floor never goes down");
        Check.Equal("0.4.9", r3.Settings.FeedFloorStable);

        // ...and with nothing newer it fails the check with the refusal, naming the dev host.
        var net4 = new FakeNet();
        byte[] old = Bytes(Manifest("0.4.2"));
        net4.Feed("stable", old, Sign(stableKey, old));
        net4.FeedAt(devBase, "dev", d, Sign(devKey, d));
        var r4 = Run(net4, trust, dev: true, running: "0.4.3-dev.5", settings: new Settings { FeedFloorDev = "0.4.4-dev.7" });
        Check.Null(r4.Info);
        Check.True(r4.Up.CheckFailed);
        Check.Equal($"The update server ({new Uri(devBase).Host}) offered an older version than before, so it was not used", r4.Up.LastError);
        Check.Equal("0.4.2", r4.Settings.FeedFloorStable, "the stable manifest verified");
    }

    [UnitTest]
    public static void Feed_leaving_a_dev_build_still_needs_a_manual_check()
    {
        var (pub, key) = NewKey();
        using var k = key;
        byte[] manifest = Bytes(Manifest("0.4.2"));

        var net = new FakeNet();
        net.Feed("stable", manifest, Sign(key, manifest));
        var manual = Run(net, Trust(stable: new[] { pub }), running: "0.4.3-dev.5", manual: true);
        Check.Equal(V("0.4.2"), manual.Info!.Version, "a manual check on the stable channel goes back to the release");
        Check.Equal(Sha, manual.Info.Sha256);

        var net2 = new FakeNet();
        net2.Feed("stable", manifest, Sign(key, manifest));
        var background = Run(net2, Trust(stable: new[] { pub }), running: "0.4.3-dev.5", manual: false);
        Check.Null(background.Info, "the background check does not");
        Check.Null(background.Up.LastError, "nothing failed: the release is older than the running dev build, so nothing newer exists");
        Check.False(background.Up.CheckFailed);
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
        Check.True(r2.Up.CheckFailed);
        Check.Equal("Couldn't verify the update server's answer (cdn.example)", r2.Up.LastError);
    }

    // ------------------------------------------------------------------ no freshness rule, floors refuse a replay

    [UnitTest]
    public static void An_old_manifest_of_the_running_version_is_up_to_date_not_a_failure()
    {
        // MaxAge is gone: a quiet period without a release must not break updates. A manifest published 200 days ago (or 10 years ago)
        // that names the running version says "up to date", on both channels, for a manual and a background check.
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = Trust(stable: new[] { stablePub }, dev: new[] { devPub });
        foreach (int days in new[] { 200, 3650 })
        foreach (bool dev in new[] { false, true })
        foreach (bool manual in new[] { false, true })
        {
            string what = $"published {days} days ago, dev {dev}, manual {manual}";
            var net = new FakeNet();
            byte[] s = Bytes(Manifest(dev ? "0.4.1" : "0.4.2", publishedDaysAgo: days));
            net.Feed("stable", s, Sign(stableKey, s));
            string running = "0.4.2";
            if (dev)
            {
                running = "0.4.3-dev.2";
                byte[] d = Bytes(Manifest(running, "dev", publishedDaysAgo: days));
                net.Feed("dev", d, Sign(devKey, d));
            }
            var r = Run(net, trust, dev: dev, running: running, manual: manual);
            Check.Null(r.Info, what);
            Check.Null(r.Up.LastError, what);
            Check.False(r.Up.CheckFailed, what);
            Check.Null(r.Settings.FeedFloorFailingSince, what);
            Check.Equal(dev ? "0.4.3-dev.2" : "0.4.2", dev ? r.Settings.FeedFloorDev : r.Settings.FeedFloorStable, what + ": the floor still goes up");
            Check.False(net.AskedGitHub, what + ": and nobody else is asked");
        }

        // An old manifest that is newer than the running version is simply offered, and one dated in the future is no reason to refuse it either.
        foreach (int daysAgo in new[] { 200, -30 })
        {
            var net = new FakeNet();
            byte[] m = Bytes(Manifest("0.4.2", publishedDaysAgo: daysAgo));
            net.Feed("stable", m, Sign(stableKey, m));
            var r = Run(net, trust);
            Check.Equal(V("0.4.2"), r.Info?.Version, $"published {daysAgo} days ago: {r.Up.LastError}");
        }
    }

    [UnitTest]
    public static void A_replayed_lower_version_is_refused_by_the_floor()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = Trust(stable: new[] { stablePub }, dev: new[] { devPub });

        // stable: the floor is 0.4.3 (seen before), the feed now serves a validly signed 0.4.2
        foreach (string running in new[] { "0.4.1", "0.4.2" })
        {
            var net = new FakeNet();
            byte[] old = Bytes(Manifest("0.4.2"));
            net.Feed("stable", old, Sign(stableKey, old));
            var r = Run(net, trust, running: running, settings: new Settings { FeedFloorStable = "0.4.3" });
            Check.Null(r.Info, running + ": the replay is not offered");
            Check.True(r.Up.CheckFailed, running + ": and it is no 'up to date' either");
            Check.Equal("The update server (cdn.example) offered an older version than before, so it was not used", r.Up.LastError, running);
            Check.Equal("0.4.3", r.Settings.FeedFloorStable, running + ": a floor never goes down");
        }

        // equal to the floor is fine
        var netEq = new FakeNet();
        byte[] eq = Bytes(Manifest("0.4.3"));
        netEq.Feed("stable", eq, Sign(stableKey, eq));
        Check.Equal(V("0.4.3"), Run(netEq, trust, settings: new Settings { FeedFloorStable = "0.4.3" }).Info?.Version);

        // dev channel: each channel has its own floor (the running dev build is ahead of the release, so only dev.json could offer anything)
        byte[] s = Bytes(Manifest("0.4.3")), dOld = Bytes(Manifest("0.4.4-dev.1", "dev"));
        var netDev = new FakeNet();
        netDev.Feed("stable", s, Sign(stableKey, s));
        netDev.Feed("dev", dOld, Sign(devKey, dOld));
        var rDev = Run(netDev, trust, dev: true, running: "0.4.4-dev.1", settings: new Settings { FeedFloorDev = "0.4.4-dev.5" });
        Check.Null(rDev.Info);
        Check.True(rDev.Up.CheckFailed);
        Check.Contains(rDev.Up.LastError, "older version than before");
        Check.Equal("0.4.4-dev.5", rDev.Settings.FeedFloorDev);
        Check.Equal("0.4.3", rDev.Settings.FeedFloorStable, "the stable manifest verified, so its floor goes up; the refused one raises nothing");

        // after a good check the floor is up: a later replay of the previous release fails
        var netA = new FakeNet();
        byte[] v3 = Bytes(Manifest("0.4.3")), v2 = Bytes(Manifest("0.4.2"));
        netA.Feed("stable", v3, Sign(stableKey, v3));
        var settings = new Settings();
        Check.Equal(V("0.4.3"), Run(netA, trust, settings: settings).Info?.Version);
        var netB = new FakeNet();
        netB.Feed("stable", v2, Sign(stableKey, v2));
        var rB = Run(netB, trust, settings: settings);
        Check.Null(rB.Info);
        Check.True(rB.Up.CheckFailed);
    }

    // ------------------------------------------------------------------ feed on: fetched but not trustworthy, or not reachable: the check fails

    [UnitTest]
    public static void Feed_every_fetch_failure_fails_the_check_with_a_plain_message()
    {
        var (pub, key) = NewKey();
        using var k1 = key;
        var (_, wrongKey) = NewKey();
        using var k2 = wrongKey;
        byte[] good = Bytes(Manifest());
        void Signed(FakeNet n, string json) { var b = Bytes(json); n.Feed("stable", b, Sign(key, b)); }

        const string Reach = "Couldn't reach the update server (cdn.example)";
        const string Verify = "Couldn't verify the update server's answer (cdn.example)";
        var cases = new List<(string Name, Action<FakeNet> Setup, string Expect)>
        {
            ("404", n => { }, Reach + ": HTTP 404"),
            ("HTTP 500", n => n.Status(Base + "stable.json", 500), Reach + ": HTTP 500"),
            ("connection error", n => n.Throw(Base + "stable.json", () => new HttpRequestException("SERVER-TEXT connection refused")), Reach),
            ("timeout", n => n.Throw(Base + "stable.json", () => new TaskCanceledException("SERVER-TEXT timed out")), Reach + ": no answer within 10 s"),
            ("signature HTTP 503", n => { n.Ok(Base + "stable.json", good); n.Status(Base + "stable.json.sig", 503); }, Reach + ": HTTP 503"),
            ("signature connection error", n => { n.Ok(Base + "stable.json", good); n.Throw(Base + "stable.json.sig", () => new IOException("SERVER-TEXT reset")); }, Reach),
            ("bad signature", n => n.Feed("stable", good, Sign(wrongKey, good)), Verify),
            ("flipped byte", n => { var b = (byte[])good.Clone(); b[12] ^= 1; n.Feed("stable", b, Sign(key, good)); }, Verify),
            ("missing signature", n => n.Ok(Base + "stable.json", good), Verify),
            ("signature gone (410)", n => { n.Ok(Base + "stable.json", good); n.Status(Base + "stable.json.sig", 410); }, Verify),
            ("garbage signature", n => n.Feed("stable", good, "%%%"), Verify),
            ("short signature", n => n.Feed("stable", good, Convert.ToBase64String(new byte[32])), Verify),
            ("oversize signature", n => n.Feed("stable", good, new string('A', UpdateFeed.MaxSignatureBytes + 1)), Verify),
            ("unparseable", n => Signed(n, "{ nope"), Verify),
            ("schema 2", n => Signed(n, Manifest(schema: 2)), Verify),
            ("wrong channel", n => Signed(n, Manifest(channel: "dev")), Verify),
            ("dev version on stable", n => Signed(n, Manifest("0.4.3-dev.7")), Verify),
            ("non-https exe", n => Signed(n, Manifest(exeUrl: "http://cdn.example/Clink.exe")), Verify),
            ("malformed exe url", n => Signed(n, Manifest(exeUrl: "https//cdn.example/Clink.exe")), Verify),
            ("exe outside the feed's base", n => Signed(n, Manifest(exeUrl: "https://evil.example/Clink.exe")), Verify),
            ("no exe asset", n => Signed(n, Manifest(exe: false)), Verify),
            ("bad sha", n => Signed(n, Manifest(sha: "XYZ")), Verify),
            ("size 0", n => Signed(n, Manifest(size: 0)), Verify),
            ("no published", n => Signed(n, Manifest(published: false)), Verify),
            ("tag mismatch", n => Signed(n, Manifest(tag: "v9.9.9")), Verify),
            ("UTF-16 BOM", n => { var b = new byte[] { 0xFF, 0xFE }.Concat(good).ToArray(); n.Feed("stable", b, Sign(key, b)); }, Verify),
        };
        foreach (var (name, setup, expect) in cases)
        {
            var net = new FakeNet();
            setup(net);
            var r = Run(net, Trust(stable: new[] { pub }));
            Check.Null(r.Info, name);
            Check.True(r.Up.CheckFailed, name);
            Check.True(r.Up.LastError!.StartsWith(expect, StringComparison.Ordinal), $"{name}: '{r.Up.LastError}' should start with '{expect}'");
            Check.DoesNotContain(r.Up.LastError, "SERVER-TEXT", name + ": nothing the server or the stack said");
            Check.False(net.AskedGitHub, name + ": there is no fallback");
            Check.Null(r.Settings.FeedFloorStable, name + ": no floor from a manifest that was not used");
            Check.NotNull(r.Settings.FeedFloorFailingSince, name + ": the failure streak starts");
        }
    }

    [UnitTest]
    public static void A_server_reason_phrase_never_reaches_the_user()
    {
        // F3: only "HTTP <code>" is ever recorded or shown, never the reason phrase the server chose.
        var (pub, key) = NewKey();
        using var k = key;
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        byte[] m = Bytes(Manifest("0.4.2", size: exe.Length, sha: Hex(exe)));
        const string exeUrl = Base + "releases/v0.4.2/Clink.exe";
        const string evil = "VISIT-EVIL";

        // the manifest and the signature
        foreach (string asset in new[] { "stable.json", "stable.json.sig" })
        {
            var net = new FakeNet();
            net.Feed("stable", m, Sign(key, m));
            net.Status(Base + asset, 503, evil);
            var r = Run(net, Trust(stable: new[] { pub }));
            Check.True(r.Up.CheckFailed, asset);
            Check.DoesNotContain(r.Up.LastError, evil, asset);
            Check.Contains(r.Up.LastError, "HTTP 503", asset);
            Check.Contains(r.Up.LastError, "cdn.example", asset);
        }

        // the exe download, through the same pass the app uses, up to the notice at the attempt cap
        var net2 = new FakeNet();
        net2.Feed("stable", m, Sign(key, m));
        net2.Status(exeUrl, 503, evil);
        net2.AllowOnly(Trust());
        Updater.HttpOverride = net2.Send;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        using var up = new Updater(new Settings { AutoUpdate = true }, V("0.4.1"));
        var notices = new List<UpdateNotice?>();
        up.CheckFinished += n => { lock (notices) notices.Add(n); };
        for (int pass = 1; pass <= 4; pass++)
        {
            Check.Equal(V("0.4.2"), OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None))?.Version, $"pass {pass}");
            Check.DoesNotContain(up.LastError, evil, $"pass {pass}");
            Check.Contains(up.LastError, "HTTP 503", $"pass {pass}");
            if (notices[^1] is { } notice)
            {
                Check.DoesNotContain(notice.Text, evil, $"pass {pass}: the tray text");
                Check.DoesNotContain(notice.Title, evil, $"pass {pass}");
                Check.Contains(notice.Text, "HTTP 503", $"pass {pass}");
            }
        }
        Check.NotNull(notices.FirstOrDefault(n => n != null), "the cap was reached and announced");
        Check.False(up.CheckFailed, "the check itself worked: it is the download that failed");
        net2.AssertNoStray();

        // the check result of a manual pass shows the same
        OffUi(() => up.CheckNowAsync());
        Check.DoesNotContain(up.LastError, evil);
        Check.Contains(up.LastError, "HTTP 503");
    }

    // ------------------------------------------------------------------ full cycles never leave the feed hosts

    [UnitTest]
    public static void Full_check_cycles_only_ever_talk_to_the_feed_hosts()
    {
        // Every cycle (offer + download, up to date, unreachable, bad signature, download failure) on both channels, with one feed base and
        // with two, manual and background. The fake network fails the test on a request to any other host; and every URL the updater
        // reports to its send hook is under one of the configured bases.
        var observed = new List<string>();
        Updater.SendObserver = url => { lock (observed) observed.Add(url); };
        foreach (bool twoBases in new[] { false, true })
        foreach (bool dev in new[] { false, true })
        foreach (bool manual in new[] { false, true })
        foreach (string scenario in new[] { "offer", "up to date", "unreachable", "bad signature", "download failure" })
        {
            string what = $"{(twoBases ? "two bases" : "one base")}, {(dev ? "dev" : "stable")} channel, {(manual ? "manual" : "background")}, {scenario}";
            using var dir = new TempDir();
            Updater.UpdatesDirOverride = dir.File("updates");
            var (stablePub, stableKey) = NewKey();
            var (devPub, devKey) = NewKey();
            var (_, wrongKey) = NewKey();
            using var a = stableKey;
            using var b = devKey;
            using var c = wrongKey;
            var trust = twoBases ? TwoBases(new[] { stablePub }, new[] { devPub }) : Trust(new[] { stablePub }, new[] { devPub });
            string stableBase = trust.BaseFor("stable")!, devBase = trust.BaseFor("dev")!;
            string feedBase = dev ? devBase : stableBase;
            byte[] exe = FakeExe();
            string version = dev ? "0.4.3-dev.2" : "0.4.2";
            string running = scenario == "up to date" ? version : dev ? "0.4.3-dev.1" : "0.4.1";
            string exeUrl = $"{feedBase}releases/v{version}/Clink.exe";
            string text = Manifest(version, dev ? "dev" : "stable", exeUrl: exeUrl, size: exe.Length, sha: Hex(exe)).Replace("https://cdn.example/clink/releases/Clink-Setup.exe", feedBase + "releases/Clink-Setup.exe");
            byte[] m = Bytes(text);
            var net = new FakeNet();
            if (scenario != "unreachable")
            {
                net.FeedAt(feedBase, dev ? "dev" : "stable", m, Sign(scenario == "bad signature" ? wrongKey : dev ? devKey : stableKey, m));
                if (dev)   // a dev client reads stable.json too (an old release: the dev build is the newest)
                {
                    byte[] s = Bytes(Manifest("0.4.1", exeUrl: $"{stableBase}releases/v0.4.1/Clink.exe").Replace("https://cdn.example/clink/releases/Clink-Setup.exe", stableBase + "releases/Clink-Setup.exe"));
                    net.FeedAt(stableBase, "stable", s, Sign(stableKey, s));
                }
            }
            if (scenario == "download failure") net.Status(exeUrl, 503); else net.Ok(exeUrl, exe);
            net.AllowOnly(trust);
            Updater.HttpOverride = net.Send;
            Updater.FeedOverride = trust;
            int before = observed.Count;
            using var up = new Updater(new Settings { AutoUpdate = true, UseDevBuilds = dev }, V(running));
            var info = OffUi(() => manual ? up.CheckNowAsync() : up.RunBackgroundPassAsync(CancellationToken.None));
            net.AssertNoStray();
            Check.False(net.AskedGitHub, what);
            Check.False(net.AskedSha256File, what);
            string[] sent;
            lock (observed) sent = observed.Skip(before).ToArray();
            Check.Equal(net.Asked.ToArray(), sent, what + ": the send hook saw exactly what the network was asked");
            foreach (string url in sent)
                Check.True(url.StartsWith(stableBase, StringComparison.Ordinal) || url.StartsWith(devBase, StringComparison.Ordinal), $"{what}: {url} is not under a configured feed base");

            switch (scenario)
            {
                case "offer":
                    Check.Equal(V(version), info?.Version, what + ": " + up.LastError);
                    Check.Null(up.LastError, what);
                    Check.Equal(Updater.FileFor(V(version)), up.PendingPath, what + ": downloaded and verified");
                    break;
                case "up to date":
                    Check.Null(info, what);
                    Check.Null(up.LastError, what);
                    Check.False(up.CheckFailed, what);
                    Check.Null(up.PendingPath, what);
                    break;
                case "unreachable":
                    Check.Null(info, what);
                    Check.True(up.CheckFailed, what);
                    Check.True(up.LastError!.StartsWith("Couldn't reach the update server (", StringComparison.Ordinal), what + ": " + up.LastError);
                    break;
                case "bad signature":
                    Check.Null(info, what);
                    Check.True(up.CheckFailed, what);
                    Check.True(up.LastError!.StartsWith("Couldn't verify the update server's answer (", StringComparison.Ordinal), what + ": " + up.LastError);
                    break;
                default:   // download failure
                    Check.Equal(V(version), info?.Version, what);
                    Check.False(up.CheckFailed, what + ": the check worked, the download did not");
                    Check.Equal($"downloading v{version} failed: HTTP 503", up.LastError, what);
                    Check.Null(up.PendingPath, what);
                    break;
            }
        }
    }

    [UnitTest]
    public static void A_manual_check_runs_the_same_feed_check_right_away()
    {
        var (pub, key) = NewKey();
        using var k = key;
        byte[] m = Bytes(Manifest("0.4.1"));
        var net = new FakeNet();
        net.Feed("stable", m, Sign(key, m));
        net.AllowOnly(Trust());
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        using var up = new Updater(new Settings(), V("0.4.1"));
        // a background pass just ran; the manual check does not wait for the 6 h interval and asks the very same two files
        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        var afterBackground = net.Asked.ToArray();
        Check.Equal(new[] { Base + "stable.json", Base + "stable.json.sig" }, afterBackground);
        Check.Null(OffUi(() => up.CheckNowAsync()));
        Check.Equal(afterBackground.Concat(afterBackground).ToArray(), net.Asked.ToArray(), "a manual check = the same requests, at once");
        Check.Null(up.LastError, "and its result is the status the footer reports");
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
        const string refused = "Couldn't verify the update server's answer (cdn.example)";

        var endless = new EndlessStream();
        var net = new FakeNet();
        net.Ok(Base + "stable.json", () => new StreamContent(endless));
        var r = Run(net, Trust(stable: new[] { pub }));
        Check.Null(r.Info);
        Check.True(r.Up.CheckFailed);
        Check.Equal(refused, r.Up.LastError);
        Check.True(endless.Served <= UpdateFeed.MaxManifestBytes + 1, $"read {endless.Served} bytes");
        Check.False(net.Asked.Contains(Base + "stable.json.sig"), "the size is checked before the signature is fetched");

        var announced = new AnnouncedContent(10L * 1024 * 1024);
        var net2 = new FakeNet();
        net2.Ok(Base + "stable.json", () => announced);
        var r2 = Run(net2, Trust(stable: new[] { pub }));
        Check.Null(r2.Info);
        Check.Equal(refused, r2.Up.LastError);
        Check.False(announced.Read, "a Content-Length over the cap is refused before the body is read");

        // The signature is capped too.
        byte[] manifest = Bytes(Manifest());
        var endlessSig = new EndlessStream();
        var net3 = new FakeNet();
        net3.Ok(Base + "stable.json", manifest);
        net3.Ok(Base + "stable.json.sig", () => new StreamContent(endlessSig));
        var r3 = Run(net3, Trust(stable: new[] { pub }));
        Check.Null(r3.Info);
        Check.Equal(refused, r3.Up.LastError);
        Check.True(endlessSig.Served <= UpdateFeed.MaxSignatureBytes + 1, $"read {endlessSig.Served} bytes of the signature");

        // At the cap is fine.
        string padded = Manifest() + new string(' ', UpdateFeed.MaxManifestBytes - Bytes(Manifest()).Length);
        byte[] full = Bytes(padded);
        Check.Equal(UpdateFeed.MaxManifestBytes, full.Length);
        var net4 = new FakeNet();
        net4.Feed("stable", full, Sign(key, full));
        Check.Equal(V("0.4.2"), Run(net4, Trust(stable: new[] { pub })).Info?.Version);
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
        Check.Equal("no answer within 0.3 s", ex.UserDetail, "the part that may be shown to the user");
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");

        sw.Restart();
        ex = Check.Throws<FeedUnreachableException>(() => UpdateFeed.FetchCappedAsync(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }),
            Base + "stable.json", 1000, false, CancellationToken.None, shortTime).GetAwaiter().GetResult(), "stalled body");
        Check.Contains(ex.Message, "no answer");
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");

        // An HTTP error: the user detail is the code alone (the reason phrase is the server's text).
        ex = Check.Throws<FeedUnreachableException>(() => UpdateFeed.FetchCappedAsync(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "VISIT-EVIL" }),
            Base + "stable.json", 1000, false, CancellationToken.None).GetAwaiter().GetResult(), "HTTP 503");
        Check.Equal("HTTP 503", ex.UserDetail);
        Check.DoesNotContain(ex.Message, "VISIT-EVIL");

        // The caller's cancellation still wins over the timeout: thrown as such, not as unreachable.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Check.Throws<OperationCanceledException>(() => UpdateFeed.FetchCappedAsync((_, ct) => StalledHeaders(ct), Base + "stable.json", 1000, false,
            cts.Token, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult());

        // In the updater: a stalled feed (header or body, manifest or signature) is unreachable: the check fails, in time.
        var (pub, key) = NewKey();
        using var k = key;
        byte[] good = Bytes(Manifest());
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
            Updater.FeedTimeoutOverride = shortTime;
            sw.Restart();
            var r = Run(net, Trust(stable: new[] { pub }));
            Check.Null(r.Info, name);
            Check.True(r.Up.CheckFailed, name);
            Check.Equal("Couldn't reach the update server (cdn.example): no answer within 0.3 s", r.Up.LastError, name);
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{name}: took {sw.Elapsed}");
        }
    }

    [UnitTest]
    public static void The_legacy_UpdateRepo_setting_is_ignored()
    {
        var (pub, key) = NewKey();
        using var k = key;
        byte[] m = Bytes(Manifest("0.4.2"));
        var net = new FakeNet();
        net.Feed("stable", m, Sign(key, m));
        net.Ok("https://api.github.com/repos/evil/x/releases/latest", Bytes("""{"tag_name":"v9.9.9"}"""));
        var r = Run(net, Trust(stable: new[] { pub }), settings: new Settings { UpdateRepo = "evil/x" });
        Check.Equal(V("0.4.2"), r.Info?.Version, r.Up.LastError);
        Check.Equal(new[] { Base + "stable.json", Base + "stable.json.sig" }, net.Asked.ToArray(), "the feed, and nothing else");
        Check.True(r.Up.UpdatesEnabled);

        // The key stays in settings.json (it round-trips, and an older Clink reads it) but means nothing here; a settings import keeps ours.
        var s = Settings.FromJson("""{"UpdateRepo":"owner/repo"}""");
        Check.Equal("owner/repo", s.UpdateRepo);
        Check.Contains(s.ToJson(), "\"UpdateRepo\": \"owner/repo\"");
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.UpdateRepo)));
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
        var r = Run(net, Trust(stable: new[] { pub }), ct: cts.Token);
        Check.Null(r.Info);
        Check.Equal("cancelled", r.Up.LastError);
        Check.Null(r.Settings.FeedFloorFailingSince, "shutting down is not a failed check");

        // Cancelled while the feed answers: the same.
        using var cts2 = new CancellationTokenSource();
        var net2 = new FakeNet();
        net2.Routes[Base + "stable.json"] = ct => { cts2.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("not reached"); };
        var r2 = Run(net2, Trust(stable: new[] { pub }), ct: cts2.Token);
        Check.Equal("cancelled", r2.Up.LastError);
        Check.Null(r2.Settings.FeedFloorFailingSince);

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
        using var up = new Updater(settings, V("0.4.1"));
        UpdateInfo Info(string sha, long size) => new(V("0.4.2"), "v0.4.2", url, size, sha, "");
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

        // An error status fails the download with "HTTP <code>" and nothing of the server's reason phrase.
        var netE = new FakeNet();
        netE.Status(url, 404, "VISIT-EVIL");
        Updater.HttpOverride = netE.Send;
        var httpEx = Check.Throws<HttpRequestException>(() => OffUi(() => up.DownloadAsync(Info(Hex(exe), exe.Length), null, CancellationToken.None)));
        Check.Equal("HTTP 404", httpEx.Message);
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
        Check.Equal(Hex(exe), settings.PendingUpdateSignedSha256!.ToLowerInvariant(), "recorded as a signed download, tied to its hash");
        Check.Contains(settings.ToJson(), "\"PendingUpdateSignedSha256\": \"" + settings.PendingUpdateSha256 + "\"");
        Check.DoesNotContain(settings.ToJson(), "\"PendingUpdateSigned\":", "the retired bool is never written");

        // There is no unsigned download any more: an offer without the signed hash is refused before any request is made.
        File.Delete(path);
        var net4 = new FakeNet();
        net4.Ok(url, exe);
        net4.Ok(url + ".sha256", Bytes(Hex(exe) + "  Clink.exe\n"));
        Updater.HttpOverride = net4.Send;
        Check.Throws<InvalidOperationException>(() => OffUi(() => up.DownloadAsync(new UpdateInfo(V("0.4.2"), "v0.4.2", url, exe.Length, "", ""), null, CancellationToken.None)));
        Check.Equal(0, net4.Asked.Count, "nothing was requested, no hash file either");

        // Caller cancellation propagates from the download too, and leaves nothing behind.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Check.Throws<OperationCanceledException>(() => OffUi(() => up.DownloadAsync(Info(Hex(exe), exe.Length), null, cts.Token)));
        Check.Equal(Array.Empty<string>(), Left().Where(f => f.EndsWith(".partial", StringComparison.Ordinal) || f.EndsWith(".exe", StringComparison.Ordinal)).ToArray());
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
        using var up = new Updater(settings, V("0.4.1"));
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
        settings.PendingUpdateSignedSha256 = null;
        var netU = new FakeNet();
        netU.Feed("stable", same, Sign(key, same));
        Updater.HttpOverride = netU.Send;
        using var upU = new Updater(settings, V("0.4.1"));
        OffUi(() => upU.CheckNowAsync());
        Check.True(netU.Asked.Contains(exeUrl), "an unsigned download is not reused while the feed is on");

        // Recorded as a signed download: adopted without a download.
        File.WriteAllBytes(file, exe);
        settings.PendingUpdateVersion = "0.4.2";
        settings.PendingUpdateSha256 = Convert.ToHexString(SHA256.HashData(exe));
        settings.PendingUpdateSignedSha256 = Convert.ToHexString(SHA256.HashData(exe));
        var net2 = new FakeNet();
        net2.Feed("stable", same, Sign(key, same));
        Updater.HttpOverride = net2.Send;
        using var up2 = new Updater(settings, V("0.4.1"));
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
        using var up = new Updater(settings, V("0.4.1"));
        Check.True(up.TrySetPending(newer, V("0.4.3"), "downloaded and verified", manual: false));   // made earlier in this run
        var info = OffUi(() => up.CheckNowAsync());
        Check.Equal(V("0.4.2"), info?.Version, up.LastError);
        Check.Equal(newer, up.PendingPath, "v0.4.3 stays the pending update");
        Check.False(net.Asked.Any(u => u.EndsWith("Clink.exe", StringComparison.Ordinal)), "the older offer is not downloaded");
    }

    [UnitTest]
    public static void Only_a_signed_download_is_installed_at_startup()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        byte[] exe = FakeExe();
        string file = Updater.FileFor(V("0.4.9"));
        Settings Recorded(string? signedSha) => new() { PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateSignedSha256 = signedSha, PendingUpdateFrom = "0.4.1" };

        // Feed off: this build has no updates at all, so nothing is installed (and nothing is thrown away either).
        File.WriteAllBytes(file, exe);
        Updater.FeedOverride = FeedTrust.None;
        var off = new Updater(Recorded(Hex(exe)), V("0.4.1"));
        Check.False(off.TryGetPendingUpdate(out _, out _));
        Check.Null(off.InstallProblem);
        Check.True(File.Exists(file));

        // Feed on: a download that was not made from a signed manifest is discarded, never installed.
        Updater.FeedOverride = Trust();
        var on = new Updater(Recorded(null), V("0.4.1"));
        Check.False(on.TryGetPendingUpdate(out _, out _));
        Check.Null(on.InstallProblem);
        Check.False(File.Exists(file), "the unsigned download is thrown away");

        // Feed on, signed download: installed.
        File.WriteAllBytes(file, exe);
        var signed = new Updater(Recorded(Hex(exe)), V("0.4.1"));
        bool found = signed.TryGetPendingUpdate(out string path, out _);
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
        using var up = new Updater(settings, V("0.4.1"));
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
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.PendingUpdateSignedSha256)));
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.FeedFloorDev)));
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.PendingUpdateSignedSha256)));

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

        // The failure streak stamp is this installation's own too (a FeedFloor* key), saved while set and left out while null.
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.FeedFloorFailingSince)));
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.FeedFloorFailingSince)));
        Check.DoesNotContain(new Settings().ToJson(), "FeedFloorFailingSince");
        string stamp = Updater.FormatStamp(V("0.4.2"), new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc));
        Check.Equal("0.4.2@2026-10-28T09:00:00.0000000Z", stamp);
        var failing = new Settings { FeedFloorFailingSince = stamp };
        Check.Equal(stamp, Settings.FromJson(failing.ToJson()).FeedFloorFailingSince);
        Check.Null(Settings.FromJson(SettingsImport.KeepOurOwn(failing.ToJson(), new Settings().ToJson(), keepOurToken: true)).FeedFloorFailingSince, "another copy's streak does not arrive");
        // the stamps and marks that were never shipped are gone: an old file that still has them loses them on load (not even kept as unknown keys)
        var legacy = Settings.FromJson("""
            {"FeedFloorStableUpToDate":"0.4.2@2026-10-28T09:00:00.0000000Z","FeedFloorDevGitHubConfirm":"x","FeedFloorStableSoftSince":"x",
             "FeedFloorDevSoftGrace":"x","PendingUpdateSigned":true,"PendingUpdateFxSigned":true,"SomethingNewer":1}
            """);
        Check.Null(legacy.FeedFloorFailingSince);
        Check.Equal(new[] { "SomethingNewer" }, legacy.Unknown?.Keys.ToArray(), "only a key a newer Clink may know is kept");
        foreach (string retired in new[] { "FeedFloorStableUpToDate", "FeedFloorDevGitHubConfirm", "FeedFloorStableSoftSince", "FeedFloorDevSoftGrace", "PendingUpdateSigned\"", "PendingUpdateFxSigned\"" })
            Check.DoesNotContain(legacy.ToJson(), retired, "not written back");

        // The stamp is only good for the version that wrote it, never from the future, and never unreadable.
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        Check.Equal(new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc), Updater.ParseStamp(stamp, V("0.4.2"), now));
        Check.Null(Updater.ParseStamp(stamp, V("0.4.3"), now), "another version's");
        Check.Null(Updater.ParseStamp(stamp, V("0.4.2"), now.AddDays(-2)), "more than a day ahead of now");
        foreach (string? bad in new string?[] { null, "", "nonsense", "0.4.2@", "0.4.2@yesterday", "@2026-10-28T09:00:00.0000000Z", "0.4.2@2026-10-28T09:00:00", new string('x', 200) })
            Check.Null(Updater.ParseStamp(bad, V("0.4.2"), now), $"'{bad}'");
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

        // dev.json at the stable base only (the old single-base layout) is not found: the dev channel's check fails, naming the dev host.
        // Running 0.4.3 here and below, so the verified stable 0.4.3 is no offer and the failed dev manifest decides the check.
        var net3 = new FakeNet();
        net3.Feed("stable", s, Sign(stableKey, s));
        net3.Feed("dev", d, Sign(devKey, d));
        var r3 = Run(net3, trust, dev: true, running: "0.4.3");
        Check.Null(r3.Info);
        Check.True(r3.Up.CheckFailed);
        Check.Equal("Couldn't reach the update server (dl-dev.example): HTTP 404", r3.Up.LastError);
        Check.False(net3.Asked.Contains(Base + "dev.json"), "a dev.json under feedUrl is never read");

        // The keys stay per channel across the two bases: dev.json from the dev base signed with the stable key is refused.
        var net4 = Net();
        net4.Ok(DevBase + "dev.json.sig", Bytes(Sign(stableKey, d)));
        var r4 = Run(net4, trust, dev: true, running: "0.4.3");
        Check.Null(r4.Info);
        Check.Equal("Couldn't verify the update server's answer (dl-dev.example)", r4.Up.LastError);
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
        const string refusedStable = "Couldn't verify the update server's answer (cdn.example)";

        // stable.json naming a binary under the dev base, a foreign host, or a dot-dot escape.
        foreach (string exe in new[] { DevBase + "releases/v0.4.9/Clink.exe", "https://evil.example/clink/Clink.exe", Base + "../other/Clink.exe", "https://cdn.example/clink-evil/Clink.exe" })
        {
            var net = new FakeNet();
            byte[] m = Bytes(Manifest("0.4.9", exeUrl: exe));
            net.Feed("stable", m, Sign(stableKey, m));
            var r = Run(net, two);
            Check.Null(r.Info, exe);
            Check.Equal(refusedStable, r.Up.LastError, exe);
            Check.Null(r.Settings.FeedFloorStable, exe + ": raises no floor");
        }

        // dev.json under the dev base naming a binary under feedUrl (the stable base) is refused.
        var net2 = new FakeNet();
        byte[] dm = Bytes(DevManifest("0.4.9-dev.1", Base + "releases/v0.4.9-dev.1/Clink.exe"));
        byte[] sm = Bytes(Manifest("0.4.1"));
        net2.Feed("stable", sm, Sign(stableKey, sm));
        net2.Ok(DevBase + "dev.json", dm);
        net2.Ok(DevBase + "dev.json.sig", Bytes(Sign(devKey, dm)));
        var r2 = Run(net2, two, dev: true, running: "0.4.1");
        Check.Null(r2.Info);
        Check.Equal("Couldn't verify the update server's answer (dl-dev.example)", r2.Up.LastError);
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
        byte[] fx2 = Bytes(WithFramework(Manifest("0.4.9"), "0.4.9").Replace("https://cdn.example/clink/releases/v0.4.9/Clink-framework.exe", "https://evil.example/Clink-framework.exe"));
        var net6 = new FakeNet();
        net6.Feed("stable", fx2, Sign(stableKey, fx2));
        Check.Null(Run(net6, one).Info, "the framework exe URL is outside the base");
    }

    // ------------------------------------------------------------------ release notes

    [UnitTest]
    public static void Release_notes_open_only_when_https_and_under_the_base_of_the_manifest_they_came_from()
    {
        var two = TwoBases();
        Updater.FeedOverride = two;
        const string fallback = "https://euphoric.software/";
        Check.Equal(fallback, UpdateFeed.FallbackNotesPage);
        var opened = new List<string>();
        Updater.BrowserOverride = url => opened.Add(url);
        var up = new Updater(new Settings(), V("0.4.1"));
        UpdateInfo Info(string version, string channel, string notes)
        {
            string escaped = System.Text.Json.JsonSerializer.Serialize(notes)[1..^1];   // whatever the manifest says is a JSON string, however odd
            var m = UpdateFeed.ParseManifest(Bytes(Manifest(version, channel, notes: escaped)), channel, out string? problem);
            return UpdateFeed.ToUpdateInfo(m ?? throw new TestFailure(problem ?? "manifest refused"));
        }

        void Expect(UpdateInfo info, string expected, string what)
        {
            Check.Equal(expected, up.ReleasePageFor(info), what);
            int before = opened.Count;
            Check.Null(up.OpenReleasePage(info), what);
            Check.Equal(before + 1, opened.Count, what + ": the browser is opened once");
            Check.Equal(expected, opened[^1], what + ": with exactly that address and nothing else");
        }

        // under the base (stable: <base>releases/<tag>/notes.txt): opened, as the normalised address
        Expect(Info("0.4.9", "stable", Base + "releases/v0.4.9/notes.txt"), Base + "releases/v0.4.9/notes.txt", "stable notes next to the binaries");
        Expect(Info("0.4.9", "stable", "https://CDN.example:443/clink/releases/v0.4.9/notes.txt"), Base + "releases/v0.4.9/notes.txt", "the normalised AbsoluteUri is what is opened");
        Expect(Info("0.4.9-dev.1", "dev", DevBase + "dev/v0.4.9-dev.1/notes.txt"), DevBase + "dev/v0.4.9-dev.1/notes.txt", "dev notes under the dev base");

        // elsewhere, or not https: the constant page
        foreach (string notes in new[]
                 {
                     "http://cdn.example/clink/releases/v0.4.9/notes.txt", "https://example.invalid/notes", "https://cdn.example/other/notes.txt", Base,
                     Base + "releases/v0.4.9/notes.txt?x=1", Base + "releases/v0.4.9/notes.txt#f", Base + "../x/notes.txt", "https://cdn.example/clink/%2e%2e/x/notes.txt",
                     "https://u@cdn.example/clink/releases/v0.4.9/notes.txt", "https://cdn.example:8443/clink/releases/v0.4.9/notes.txt",
                     DevBase + "dev/v0.4.9/notes.txt", "https://github.com/o/r/releases/latest", "https://cdn.example.evil.example/clink/releases/v0.4.9/notes.txt",
                     "", "   ", "javascript:alert(1)", "file:///C:/Windows/System32/calc.exe", "calc.exe", @"C:\Windows\System32\calc.exe",
                     Base + "releases/v0.4.9/notes.txt\r\nHost: x", Base + "releases/v0.4.9/notes.txt calc.exe", Base + new string('a', 3000),
                 })
            Expect(Info("0.4.9", "stable", notes), fallback, $"stable notesUrl '{notes.Replace("\r\n", "\\r\\n")}'");
        // a dev manifest's notes are checked against the DEV base, a stable one's against feedUrl
        Expect(Info("0.4.9-dev.1", "dev", Base + "dev/v0.4.9-dev.1/notes.txt"), fallback, "the stable base's address in a dev manifest");
        Expect(Info("0.4.9-dev.1", "dev", "http://dl-dev.example/clink-dev/dev/v0.4.9-dev.1/notes.txt"), fallback, "http in a dev manifest");

        // nothing to go by: the constant page; and a build without a feed has no base to vouch for any address
        Check.Equal(fallback, up.ReleasePageFor(null));
        Check.Null(up.OpenReleasePage(null));
        Check.Equal(fallback, opened[^1]);
        var good = Info("0.4.9", "stable", Base + "releases/v0.4.9/notes.txt");
        Updater.FeedOverride = FeedTrust.None;
        Check.Equal(fallback, up.ReleasePageFor(good));

        // a browser that cannot be started is reported, not thrown
        Updater.FeedOverride = two;
        Updater.BrowserOverride = _ => throw new InvalidOperationException("no browser");
        Check.Contains(up.OpenReleasePage(good), "Could not open the browser");
    }

    // ------------------------------------------------------------------ a feed that keeps failing

    [UnitTest]
    public static void Background_checks_failing_for_over_three_days_earn_one_notice_per_session()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var t0 = DateTimeOffset.UtcNow;
        var now = t0;
        Updater.ClockOverride = () => now;
        var net = new FakeNet();   // the feed is unreachable: a 404 for everything
        net.AllowOnly(trust);
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = trust;
        var settings = new Settings();
        using var up = new Updater(settings, V("0.4.1"));
        var notices = new List<UpdateNotice?>();
        up.CheckFinished += n => { lock (notices) notices.Add(n); };
        var gate = new UpdateNoticeGate();
        int shown = 0;
        void Pass(string what, bool manual = false)
        {
            int before = notices.Count;
            OffUi(() => manual ? up.CheckNowAsync() : up.RunBackgroundPassAsync(CancellationToken.None));
            Check.Equal(before + 1, notices.Count, what + ": the pass reports its end");
            Check.True(up.CheckFailed, what);
            if (gate.ShouldShow(notices[^1])) shown++;
        }

        Pass("the first failure");
        Check.Equal(Updater.FormatStamp(V("0.4.1"), t0.UtcDateTime), settings.FeedFloorFailingSince, "the streak starts at the first failure");
        Check.Null(notices[^1], "a plain failed check is no notification (the window shows it)");
        now = t0.AddDays(1); Pass("+1 d"); Check.Null(notices[^1]);
        now = t0.AddDays(3); Pass("+3 d"); Check.Null(notices[^1], "it takes MORE than 3 days");
        Check.Equal(0, shown);

        now = t0.AddDays(3).AddHours(1);
        Pass("+3 d 1 h");
        var notice = notices[^1];
        Check.NotNull(notice, "failing for 3 days and an hour");
        Check.Equal("checkfailed:stable", notice!.Key);
        Check.Contains(notice.Text, "cdn.example", "names the host");
        Check.Contains(notice.Text, "for 3 days", "and the duration");
        Check.DoesNotContain(notice.Text, "404", "and nothing else the check learnt");
        Check.True(notice.Text.Length <= 250, "fits a tray balloon");
        Check.Equal(1, shown);
        for (int i = 1; i <= 4; i++) { now = t0.AddDays(3).AddHours(1 + 6 * i); Pass($"+{6 * i} h later"); }
        Check.Equal(1, shown, "shown once per session, not at every pass");
        Check.True(notices.Count(n => n != null) > 1, "the later failed passes report the same problem; the gate shows it once");
        Check.Equal(Updater.FormatStamp(V("0.4.1"), t0.UtcDateTime), settings.FeedFloorFailingSince, "the streak is not restarted by a failure");

        // a manual check never produces a notice (its result is in the footer) and does not end the streak
        now = t0.AddDays(5);
        Pass("manual", manual: true);
        Check.Null(notices[^1]);
        Check.Equal(Updater.FormatStamp(V("0.4.1"), t0.UtcDateTime), settings.FeedFloorFailingSince);

        // any successful check clears it
        byte[] m = Bytes(Manifest("0.4.1"));
        net.Feed("stable", m, Sign(key, m));
        now = t0.AddDays(6);
        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        Check.False(up.CheckFailed, up.LastError);
        Check.Null(settings.FeedFloorFailingSince, "cleared");
        Check.Null(notices[^1]);
        Check.DoesNotContain(settings.ToJson(), "FeedFloorFailingSince", "left out of the file while null");

        // a later streak starts again from its own first failure; the session already told the user (same key)
        net.Routes.Clear();
        now = t0.AddDays(7);
        Pass("new streak");
        Check.Equal(Updater.FormatStamp(V("0.4.1"), t0.AddDays(7).UtcDateTime), settings.FeedFloorFailingSince);
        Check.Null(notices[^1]);
        now = t0.AddDays(10).AddHours(2);
        Pass("new streak, over 3 days");
        Check.Equal("checkfailed:stable", notices[^1]!.Key);
        Check.Equal(1, shown, "once per session: the key is the same");
    }

    [UnitTest]
    public static void The_updater_has_one_http_client_which_never_follows_a_redirect()
    {
        Check.False(Updater.FeedHandler.AllowAutoRedirect);
        Check.Equal(TimeSpan.FromSeconds(30), Updater.FeedClientTimeout, "the client has a timeout (connect + headers)");
        Check.Equal(TimeSpan.FromMinutes(15), Updater.FeedHandler.PooledConnectionLifetime);
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
            using var up = new Updater(new Settings { AutoUpdate = true }, V("0.4.1"));
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

    /// <summary>A body that keeps sending: <paramref name="chunk"/> bytes every <paramref name="delayMs"/> ms until all of <paramref name="data"/> is sent (a server that trickles).</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly int _delayMs, _chunk;
        private readonly byte[] _data;
        private int _pos;
        public TrickleStream(int delayMs, byte[] data, int chunk = 1) { _delayMs = delayMs; _data = data; _chunk = chunk; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= _data.Length) return 0;
            await Task.Delay(_delayMs, ct).ConfigureAwait(false);
            int n = Math.Min(Math.Min(_chunk, _data.Length - _pos), buffer.Length);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
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
    public static void A_trickling_download_fails_in_time_and_releases_the_gate()
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
        Updater.DownloadReadTimeoutOverride = TimeSpan.FromSeconds(5);   // never goes quiet: only the rate and the total limit can end it
        foreach (var (name, total, rate, window, error) in new (string, TimeSpan, long, TimeSpan, string)[]
                 {
                     ("under the minimum rate", TimeSpan.FromSeconds(30), 10_000, TimeSpan.FromMilliseconds(250), "too slow"),
                     ("over the total deadline", TimeSpan.FromMilliseconds(300), 0, TimeSpan.FromSeconds(30), "took too long"),
                 })
        {
            Updater.DownloadTotalTimeoutOverride = total;
            Updater.DownloadMinRateOverride = rate;
            Updater.DownloadRateWindowOverride = window;
            var net = new FakeNet();
            net.Feed("stable", fm, Sign(key, fm));
            net.Ok(feedExe, () => new StreamContent(new TrickleStream(20, exe)));   // 300 bytes at 50 B/s: 6 s if let be
            Updater.HttpOverride = net.Send;
            using var up = new Updater(new Settings { AutoUpdate = true }, V("0.4.1"));
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var check = Task.Run(() => up.CheckNowAsync());
                // 10 s: a bound for a hang only (each attempt ends in well under a second), with room for a slow CI runner
                Check.True(check.Wait(TimeSpan.FromSeconds(10)), $"{name}, attempt {attempt}: still running after {sw.Elapsed}");
                Check.Equal(V("0.4.2"), check.Result?.Version, $"{name}, attempt {attempt}");
                Check.Contains(up.LastError, "downloading v0.4.2 failed", $"{name}, attempt {attempt}");
                Check.Contains(up.LastError, error, $"{name}, attempt {attempt}");
                Check.Equal(1, up._gate.CurrentCount, $"{name}, attempt {attempt}: the gate is free again");
                Check.False(up.IsDownloading, name);
                Check.Null(up.PendingPath, name);
                Check.Equal(0, Directory.GetFiles(Updater.UpdatesDir, "*.partial").Length, name + ": no partial file is left");
            }
            Check.Equal(2, net.Asked.Count(u => u == feedExe), name + ": the next check ran");
        }
    }

    [UnitTest]
    public static void A_download_at_a_normal_pace_passes_the_rate_and_deadline_limits()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        const string url = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";
        Updater.DownloadTotalTimeoutOverride = TimeSpan.FromSeconds(10);
        // 10 B/s: the body below sends ~3.3 KB/s, so even a CI runner that stalls for a while does not fall under it (it was 300 B/s over 50 ms)
        Updater.DownloadMinRateOverride = 10;                               // bytes per second
        Updater.DownloadRateWindowOverride = TimeSpan.FromMilliseconds(50);
        using var up = new Updater(new Settings(), V("0.4.1"));
        var info = new UpdateInfo(V("0.4.2"), "v0.4.2", url, exe.Length, Hex(exe), "");
        foreach (var (name, body) in new (string, Func<HttpContent>)[]
                 {
                     ("all at once", () => new ByteArrayContent(exe)),
                     ("100 bytes every 30 ms (3.3 KB/s)", () => new StreamContent(new TrickleStream(30, exe, chunk: 100))),
                 })
        {
            var net = new FakeNet();
            net.Ok(url, body);
            Updater.HttpOverride = net.Send;
            string path = OffUi(() => up.DownloadAsync(info, null, CancellationToken.None));
            Check.Equal(Hex(exe), Hex(File.ReadAllBytes(path)).ToLowerInvariant(), name);
            File.Delete(path);
        }

        // the real limits: 10 minutes at least, else 64 KB/s; unchanged by the hooks' absence
        Updater.DownloadTotalTimeoutOverride = null;
        Check.Equal(TimeSpan.FromMinutes(10), Updater.DownloadDeadline(150L * 1024 * 1024 / 100));
        Check.Equal(TimeSpan.FromSeconds(40 * 60), Updater.DownloadDeadline(150L * 1024 * 1024));
        // a size over the hard ceiling (a lying Content-Length or listed size) buys no more than the ceiling's deadline (512 MB at 64 KB/s)
        Check.Equal(TimeSpan.FromSeconds(Updater.MaxExeBytes / (64 * 1024)), Updater.DownloadDeadline(Updater.MaxExeBytes));
        Check.Equal(Updater.DownloadDeadline(Updater.MaxExeBytes), Updater.DownloadDeadline(long.MaxValue));
        Check.Equal(Updater.DownloadDeadline(Updater.MaxExeBytes), Updater.DownloadDeadline(Updater.MaxExeBytes + 1));
    }

    [UnitTest]
    public static void The_rate_window_keeps_a_bounded_number_of_samples()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        // 6000 reads of one byte each, as fast as they come: one sample per read (the old queue) would be thousands within the window
        byte[] exe = FakeExe(6000);
        const string url = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";
        Updater.DownloadTotalTimeoutOverride = TimeSpan.FromSeconds(30);
        Updater.DownloadMinRateOverride = 0;
        Updater.DownloadRateWindowOverride = TimeSpan.FromSeconds(2);   // short, yet longer than the transfer: nothing ever leaves the window
        using var up = new Updater(new Settings(), V("0.4.1"));
        var info = new UpdateInfo(V("0.4.2"), "v0.4.2", url, exe.Length, Hex(exe), "");
        var net = new FakeNet();
        net.Ok(url, () => new StreamContent(new TrickleStream(0, exe, chunk: 1)));
        Updater.HttpOverride = net.Send;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string path = OffUi(() => up.DownloadAsync(info, null, CancellationToken.None));
        Check.Equal(exe, File.ReadAllBytes(path));
        // at most one sample per second of transfer (+1); the transfer takes well under the 30 s limit
        int bound = (int)Math.Ceiling(sw.Elapsed.TotalSeconds) + 1;
        Check.True(up.RateSamplesPeak <= bound, $"{up.RateSamplesPeak} samples held for a {sw.Elapsed.TotalSeconds:0.##} s transfer (at most {bound})");
    }

    [UnitTest]
    public static void Every_check_pass_reports_its_end_and_a_failed_background_pass_reaches_the_ui()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var net = new FakeNet();
        net.Status(Base + "stable.json", 503);
        net.AllowOnly(trust);
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = trust;
        using var up = new Updater(new Settings(), V("0.4.1"));
        var seen = new List<UpdateNotice?>();
        var gateFree = new List<int>();
        up.CheckFinished += n => { lock (seen) { seen.Add(n); gateFree.Add(up._gate.CurrentCount); } };
        Check.Null(OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None)));
        Check.True(up.CheckFailed);
        Check.Contains(up.LastError, "HTTP 503");
        Check.Equal(1, seen.Count, "a failed background pass reports its end");
        Check.Null(seen[0], "a plain failed check is no notification (the window shows it)");
        // a successful one, and a manual check, report too
        byte[] m = Bytes(Manifest("0.4.1"));
        net.Feed("stable", m, Sign(key, m));
        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        OffUi(() => up.CheckNowAsync());
        Check.Equal(3, seen.Count);
        Check.Equal(new[] { 1, 1, 1 }, gateFree.ToArray(), "raised after the gate is released (a handler may start the next check)");
        // a pass cut short by shutdown does not
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { OffUi(() => up.RunBackgroundPassAsync(cts.Token)); } catch (OperationCanceledException) { }
        Check.Equal(3, seen.Count, "not raised for a cancelled pass");
        net.AssertNoStray();
    }

    [UnitTest]
    public static void A_download_that_reaches_the_attempt_cap_is_announced_once_per_version()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        var (pub, key) = NewKey();
        using var k = key;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        byte[] exe = FakeExe();
        var net = new FakeNet();
        void Offer(string version)
        {
            byte[] fm = Bytes(Manifest(version, size: exe.Length, sha: Hex(exe)));
            net.Feed("stable", fm, Sign(key, fm));
            net.Status($"https://cdn.example/clink/releases/v{version}/Clink.exe", 503);
        }
        Offer("0.4.2");
        Updater.HttpOverride = net.Send;
        using var up = new Updater(new Settings { AutoUpdate = true }, V("0.4.1"));
        var notices = new List<UpdateNotice?>();
        up.CheckFinished += n => { lock (notices) notices.Add(n); };
        var gate = new UpdateNoticeGate();
        int shown = 0;
        for (int pass = 1; pass <= 5; pass++)   // five background passes (6 h apart in the app)
        {
            Check.Equal(V("0.4.2"), OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None))?.Version, $"pass {pass}");
            Check.Contains(up.LastError, "downloading v0.4.2 failed", $"pass {pass}");
            if (gate.ShouldShow(notices[^1])) shown++;
        }
        Check.Equal(5, notices.Count, "every pass reports");
        Check.Null(notices[0], "the first failure is not announced");
        Check.Null(notices[1]);
        Check.NotNull(notices[2], "the attempt that reaches the cap is");
        Check.Equal("download:0.4.2", notices[2]!.Key);
        Check.Contains(notices[2]!.Text, "Downloading v0.4.2 failed 3 times");
        Check.Contains(notices[2]!.Text, "503", "with why");
        Check.True(notices[2]!.Text.Length <= 250, "fits a tray balloon");
        Check.Equal("download:0.4.2", notices[4]!.Key, "the capped passes report the same problem...");
        Check.Equal(1, shown, "...which is shown once");
        Check.Equal(3, net.Asked.Count(u => u.EndsWith("/v0.4.2/Clink.exe", StringComparison.Ordinal)), "three attempts, then the cap");
        // another version is another problem
        Offer("0.4.3");
        for (int pass = 1; pass <= 4; pass++)
        {
            OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
            if (gate.ShouldShow(notices[^1])) shown++;
        }
        Check.Equal("download:0.4.3", notices[^1]!.Key);
        Check.Equal(2, shown, "once per version");
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

        // the manifest redirects: unreachable, so the check fails; the target is never requested
        var net = new FakeNet();
        net.Feed("stable", m, Sign(key, m));
        net.Routes[Base + "stable.json"] = _ => Redirect(elsewhere);
        net.Ok(elsewhere, m);
        var r = Run(net, trust);
        Check.Null(r.Info);
        Check.Equal("Couldn't reach the update server (cdn.example): HTTP 302", r.Up.LastError);
        Check.False(net.Asked.Contains(elsewhere));
        // ...so does the signature
        var net2 = new FakeNet();
        net2.Feed("stable", m, Sign(key, m));
        net2.Routes[Base + "stable.json.sig"] = _ => Redirect("https://evil.example/stable.json.sig");
        var r2 = Run(net2, trust);
        Check.Null(r2.Info);
        Check.Equal("Couldn't reach the update server (cdn.example): HTTP 302", r2.Up.LastError);
        Check.False(net2.Asked.Contains("https://evil.example/stable.json.sig"));

        // a feed binary that redirects fails the download
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        byte[] exe = FakeExe();
        const string url = "https://cdn.example/clink/releases/v0.4.2/Clink.exe";
        var net3 = new FakeNet();
        net3.Routes[url] = _ => Redirect("https://evil.example/Clink.exe");
        net3.Ok("https://evil.example/Clink.exe", exe);
        Updater.HttpOverride = net3.Send;
        using var up = new Updater(new Settings(), V("0.4.1"));
        var info = new UpdateInfo(V("0.4.2"), "v0.4.2", url, exe.Length, Hex(exe), "");
        var redirected = Check.Throws<HttpRequestException>(() => OffUi(() => up.DownloadAsync(info, null, CancellationToken.None)));
        Check.Equal("HTTP 302", redirected.Message);
        Check.False(net3.Asked.Contains("https://evil.example/Clink.exe"));
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

    // ------------------------------------------------------------------ repair round 1: the signed mark, error texts, strays, floors, install guard

    [UnitTest]
    public static void The_signed_mark_is_tied_to_the_hash_of_the_download()
    {
        // v0.4.1 and older keep keys they do not know and rewrite only their own PendingUpdate* keys: after a signed download A by this
        // version, an unsigned download B of theirs leaves PendingUpdateSha256 = hash(B) beside PendingUpdateSignedSha256 = hash(A).
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        Updater.IsElevatedOverride = false;
        byte[] fileA = FakeExe(300, 1), fileB = FakeExe(300, 2);
        string file = Updater.FileFor(V("0.4.9"));
        Settings Rec(string? signed) => new() { PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(fileB), PendingUpdateSignedSha256 = signed, PendingUpdateFrom = "0.4.1" };

        File.WriteAllBytes(file, fileB);
        var up = new Updater(Rec(Hex(fileA)), V("0.4.1"));
        Check.False(up.TryGetPendingUpdate(out _, out _), "the signed hash is another file's");
        Check.Null(up.InstallProblem, "thrown away quietly");
        Check.False(File.Exists(file), "the unsigned download is deleted");
        File.WriteAllBytes(file, fileB);
        var refused = Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(file));
        Check.Contains(refused.Reason, "signed manifest");
        Check.False(File.Exists(file), "PrepareApply discards it too");

        // A record that holds only the retired PendingUpdateSigned: true (a pre-release build's) is refused the same way.
        File.WriteAllBytes(file, fileB);
        var legacy = Settings.FromJson($$"""{"PendingUpdateVersion":"0.4.9","PendingUpdateSha256":"{{Hex(fileB)}}","PendingUpdateFrom":"0.4.1","PendingUpdateSigned":true}""");
        var upL = new Updater(legacy, V("0.4.1"));
        Check.False(upL.TryGetPendingUpdate(out _, out _), "the legacy bool counts as absent");
        Check.False(File.Exists(file));
        File.WriteAllBytes(file, fileB);
        Check.Throws<Updater.ApplyRefusedException>(() => upL.PrepareApply(file));
        Check.False(File.Exists(file));

        // Control: the signed hash is B's own.
        File.WriteAllBytes(file, fileB);
        var ok = new Updater(Rec(Hex(fileB)), V("0.4.1"));
        bool found = ok.TryGetPendingUpdate(out string path, out _);
        Check.True(found ? path == file : ok.InstallProblem != null, "found, or refused only because this runner cannot install");
        Check.True(File.Exists(file));
        Check.NotNull(ok.PrepareApply(file), "applied");
        // ...but not when the file itself changed since (signed == recorded, the file is another one).
        File.WriteAllBytes(file, fileA);
        Check.Throws<Updater.ApplyRefusedException>(() => ok.PrepareApply(file));
        Check.False(File.Exists(file));
    }

    [UnitTest]
    public static void A_download_failure_shows_a_fixed_text_never_the_exceptions_message()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        var (pub, key) = NewKey();
        using var k = key;
        byte[] exe = FakeExe();
        byte[] m = Bytes(Manifest("0.4.2", size: exe.Length, sha: Hex(exe)));
        const string exeUrl = Base + "releases/v0.4.2/Clink.exe";
        var net = new FakeNet();
        net.Feed("stable", m, Sign(key, m));
        net.Throw(exeUrl, () => new HttpRequestException("Received an invalid header line: 'EVIL'"));
        net.AllowOnly(Trust());
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        using var up = new Updater(new Settings { AutoUpdate = true }, V("0.4.1"));
        var notices = new List<UpdateNotice?>();
        up.CheckFinished += n => { lock (notices) notices.Add(n); };
        for (int pass = 1; pass <= 3; pass++)
        {
            Check.Equal(V("0.4.2"), OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None))?.Version, $"pass {pass}");
            Check.DoesNotContain(up.LastError, "EVIL", $"pass {pass}");
            Check.Equal("downloading v0.4.2 failed: network error (cdn.example)", up.LastError, $"pass {pass}");
        }
        var cap = notices[^1];
        Check.NotNull(cap, "the attempt cap is announced");
        Check.DoesNotContain(cap!.Text, "EVIL");
        Check.DoesNotContain(cap.Title, "EVIL");
        Check.Contains(cap.Text, "network error (cdn.example)");
        OffUi(() => up.CheckNowAsync());
        Check.DoesNotContain(up.LastError, "EVIL", "a manual retry too");

        // Every kind of failure has its own fixed text.
        Check.Equal("HTTP 503", Updater.DownloadErrorText(new HttpRequestException("EVIL", null, HttpStatusCode.ServiceUnavailable), exeUrl));
        Check.Equal("network error (cdn.example)", Updater.DownloadErrorText(new IOException("EVIL", new System.Net.Sockets.SocketException()), exeUrl));
        Check.Equal("network error (cdn.example)", Updater.DownloadErrorText(new HttpIOException(HttpRequestError.ResponseEnded, "EVIL"), exeUrl));
        Check.Equal("the download could not be saved in the updates folder", Updater.DownloadErrorText(new UnauthorizedAccessException("EVIL"), exeUrl));
        Check.Equal("unexpected error (details in the log)", Updater.DownloadErrorText(new FormatException("EVIL"), exeUrl));
        Check.Equal("no answer within 30 s", Updater.DownloadErrorText(new TaskCanceledException("EVIL"), exeUrl));

        // What DownloadAsync itself says: size, hash, not an executable.
        var plain = new Updater(new Settings(), V("0.4.1"));
        foreach (var (name, body, sha, expect) in new (string, byte[], string, string)[]
                 {
                     ("short body", exe[..200], Hex(exe), "size mismatch"),
                     ("wrong hash", exe, new string('0', 64), "SHA-256 mismatch"),
                     ("not an exe", Enumerable.Repeat((byte)7, exe.Length).ToArray(), Hex(Enumerable.Repeat((byte)7, exe.Length).ToArray()), "the downloaded file is not a Windows executable"),
                 })
        {
            var bodyNet = new FakeNet();
            bodyNet.Ok(exeUrl, body);
            Updater.HttpOverride = bodyNet.Send;
            var ex = Check.Throws<InvalidDataException>(() => OffUi(() => plain.DownloadAsync(new UpdateInfo(V("0.4.2"), "v0.4.2", exeUrl, exe.Length, sha, ""), null, CancellationToken.None)), name);
            Check.Equal(expect, Updater.DownloadErrorText(ex, exeUrl), name);
        }
    }

    /// <summary>Not a test: run by <see cref="The_runner_fails_a_test_whose_unfaked_request_or_browser_was_swallowed"/>. A check against the runner's default network, which the updater swallows.</summary>
    private static void StrayProbeRequest()
    {
        using var up = new Updater(new Settings(), V("0.4.1"));
        OffUi(() => up.CheckAsync(CancellationToken.None));
        Check.True(up.CheckFailed, "the updater swallowed the default fake's throw");
    }

    /// <summary>Not a test: the release page through the runner's default browser fake, which OpenReleasePage turns into a message.</summary>
    private static void StrayProbeBrowser()
    {
        using var up = new Updater(new Settings(), V("0.4.1"));
        Check.Contains(up.OpenReleasePage(null), "Could not open the browser");
    }

    /// <summary>Not a test: does nothing unfaked.</summary>
    private static void StrayProbeNothing() => Check.True(true);

    [UnitTest]
    public static void The_runner_fails_a_test_whose_unfaked_request_or_browser_was_swallowed()
    {
        System.Reflection.MethodInfo Probe(string name) => typeof(UpdateFeedTests).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var request = TestRunner.ExecuteWatched(Probe(nameof(StrayProbeRequest)), noSkip: false);
        Check.Equal(TestState.Failed, request.State, "a swallowed unfaked request fails the test");
        Check.Contains(request.Text, "a request nobody faked: " + Base + "stable.json");
        var browser = TestRunner.ExecuteWatched(Probe(nameof(StrayProbeBrowser)), noSkip: false);
        Check.Equal(TestState.Failed, browser.State, "a swallowed unfaked browser fails the test");
        Check.Contains(browser.Text, "a browser nobody faked: " + UpdateFeed.FallbackNotesPage);
        Check.Equal(TestState.Passed, TestRunner.ExecuteWatched(Probe(nameof(StrayProbeNothing)), noSkip: false).State, "and nothing else does");
        // The probes' strays do not count against this test (it would fail here otherwise): the list is per ExecuteWatched.
    }

    [UnitTest]
    public static void A_built_in_floor_refuses_an_older_manifest_on_a_fresh_install()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = Trust(stable: new[] { stablePub }, dev: new[] { devPub });

        // A dev build on the stable channel, no saved floor, built when 0.4.2 was out: a validly signed but replayed 0.3.0 is refused.
        Updater.FloorSeedOverride = ("0.4.2", null);
        var net = new FakeNet();
        byte[] old = Bytes(Manifest("0.3.0"));
        net.Feed("stable", old, Sign(stableKey, old));
        var r = Run(net, trust, running: "0.4.3-dev.5", manual: true);
        Check.Null(r.Info, "not offered");
        Check.True(r.Up.CheckFailed);
        Check.Equal("The update server (cdn.example) offered an older version than before, so it was not used", r.Up.LastError);
        Check.Null(r.Settings.FeedFloorStable, "the seed is not written to settings");

        // Control: the seed version itself is offered (a manual check may leave the dev build for it), and saved as the floor.
        var net2 = new FakeNet();
        byte[] same = Bytes(Manifest("0.4.2"));
        net2.Feed("stable", same, Sign(stableKey, same));
        var r2 = Run(net2, trust, running: "0.4.3-dev.5", manual: true);
        Check.Equal(V("0.4.2"), r2.Info?.Version, r2.Up.LastError);
        Check.Equal("0.4.2", r2.Settings.FeedFloorStable);

        // The higher of the saved floor and the seed counts.
        var net3 = new FakeNet();
        net3.Feed("stable", same, Sign(stableKey, same));
        Check.Null(Run(net3, trust, running: "0.4.1", settings: new Settings { FeedFloorStable = "0.4.5" }).Info, "the saved floor is higher");

        // The dev seed guards dev.json.
        Updater.FloorSeedOverride = (null, "0.4.4-dev.3");
        var net4 = new FakeNet();
        byte[] s = Bytes(Manifest("0.4.2")), d = Bytes(Manifest("0.4.4-dev.1", "dev"));
        net4.Feed("stable", s, Sign(stableKey, s));
        net4.Feed("dev", d, Sign(devKey, d));
        var r4 = Run(net4, trust, dev: true, running: "0.4.3-dev.5");
        Check.Null(r4.Info);
        Check.Contains(r4.Up.LastError, "older version than before");

        // Seeds are canonical versions of their channel; anything else is ignored (and logged at start).
        Check.Equal(V("0.4.2"), Updater.ParseSeed("0.4.2", "stable", out string? p));
        Check.Null(p);
        Check.Equal(V("0.4.3-dev.1"), Updater.ParseSeed("0.4.3-dev.1", "dev", out p));
        Check.Null(Updater.ParseSeed("", "stable", out p));
        Check.Null(p, "empty = none, no problem");
        foreach (var (text, channel) in new[] { ("v0.4.2", "stable"), ("0.4.3-dev.1", "stable"), ("0.4.3", "dev"), ("0.4", "stable"), ("latest", "dev") })
        {
            Check.Null(Updater.ParseSeed(text, channel, out p), $"{text} on {channel}");
            Check.NotNull(p, $"{text} on {channel}: says why");
        }
        Updater.FloorSeedOverride = ("garbage", "0.4.3");
        Check.Null(Updater.FloorSeed("stable"));
        Check.Null(Updater.FloorSeed("dev"));
        Check.NotNull(Updater.SeedProblem("stable"));

        // The contract with CI: whatever this exe was built with must be a valid seed (a local build has none at all).
        foreach (var attr in typeof(Updater).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>())
        {
            if (attr.Key == Updater.MinStableKey) Check.NotNull(Updater.ParseSeed(attr.Value, "stable", out _), "the embedded ClinkMinStable " + attr.Value);
            if (attr.Key == Updater.MinDevKey) Check.NotNull(Updater.ParseSeed(attr.Value, "dev", out _), "the embedded ClinkMinDev " + attr.Value);
        }
    }

    [UnitTest]
    public static void Nothing_is_applied_by_a_build_without_updates()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        Updater.IsElevatedOverride = false;
        byte[] exe = FakeExe();
        string file = Updater.FileFor(V("0.4.9"));
        var settings = new Settings { PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateSignedSha256 = Hex(exe), PendingUpdateFrom = "0.4.1" };
        foreach (var (name, trust) in new[] { ("feed off", FeedTrust.None), ("feed misconfigured", FeedTrust.Create(Base, new[] { SparePub() }, null)) })
        {
            File.WriteAllBytes(file, exe);
            Updater.FeedOverride = trust;
            var up = new Updater(settings, V("0.4.1"));
            var ex = Check.Throws<Updater.ApplyRefusedException>(() => up.PrepareApply(file), name);
            Check.False(ex.Deferred, name);
            Check.Throws<Updater.ApplyRefusedException>(() => up.LaunchApply(file, dir.File("Clink.exe"), Array.Empty<string>()), name + ": the start-up path");
            Check.True(File.Exists(file), name + ": nothing is touched");
            Check.False(File.Exists(file + ".failed") || File.Exists(file + ".timeout"), name + ": no marker");
        }
        // Control: with the feed on, the very same download passes.
        Updater.FeedOverride = Trust();
        Check.NotNull(new Updater(settings, V("0.4.1")).PrepareApply(file));
    }

    [UnitTest]
    public static void The_failing_notice_needs_two_failed_background_passes_in_this_session()
    {
        var (pub, key) = NewKey();
        using var k = key;
        var trust = Trust(stable: new[] { pub });
        var now = DateTimeOffset.UtcNow;
        Updater.ClockOverride = () => now;
        var net = new FakeNet();   // a 404 for everything
        net.AllowOnly(trust);
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = trust;
        // A new session (this Updater) of an installation whose checks have been failing for 10 days (asleep, offline, ...).
        var settings = new Settings { FeedFloorFailingSince = Updater.FormatStamp(V("0.4.1"), now.AddDays(-10).UtcDateTime) };
        using var up = new Updater(settings, V("0.4.1"));
        var notices = new List<UpdateNotice?>();
        up.CheckFinished += n => { lock (notices) notices.Add(n); };

        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        Check.True(up.CheckFailed);
        Check.Null(notices[^1], "the session's first failed background pass is no alarm, however old the streak");
        OffUi(() => up.CheckNowAsync());
        Check.Null(notices[^1], "a manual check neither notifies nor counts");
        now = now.AddHours(6);
        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        Check.NotNull(notices[^1], "the second one is");
        Check.Contains(notices[^1]!.Text, "for 10 days");

        // A success starts the count again.
        byte[] m = Bytes(Manifest("0.4.1"));
        net.Feed("stable", m, Sign(key, m));
        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        Check.False(up.CheckFailed, up.LastError);
        net.Routes.Clear();
        settings.FeedFloorFailingSince = Updater.FormatStamp(V("0.4.1"), now.AddDays(-5).UtcDateTime);
        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        Check.Null(notices[^1], "one failed pass after a success");
        OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
        Check.NotNull(notices[^1], "two");
    }

    [UnitTest]
    public static void The_failing_notice_names_the_host_that_failed()
    {
        var (stablePub, stableKey) = NewKey();
        var (devPub, devKey) = NewKey();
        using var a = stableKey;
        using var b = devKey;
        var trust = TwoBases(new[] { stablePub }, new[] { devPub });
        string devBase = trust.BaseFor("dev")!, devHost = new Uri(devBase).Host;
        var now = DateTimeOffset.UtcNow;
        Updater.ClockOverride = () => now;
        byte[] s = Bytes(Manifest("0.4.1")), d = Bytes(DevManifest("0.4.3-dev.1"));

        UpdateNotice? NoticeAfterTwoPasses(FakeNet net)
        {
            net.AllowOnly(trust);
            Updater.HttpOverride = net.Send;
            Updater.FeedOverride = trust;
            var settings = new Settings { UseDevBuilds = true, FeedFloorFailingSince = Updater.FormatStamp(V("0.4.3-dev.1"), now.AddDays(-5).UtcDateTime) };
            using var up = new Updater(settings, V("0.4.3-dev.1"));
            var notices = new List<UpdateNotice?>();
            up.CheckFinished += n => { lock (notices) notices.Add(n); };
            OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
            OffUi(() => up.RunBackgroundPassAsync(CancellationToken.None));
            Check.True(up.CheckFailed, up.LastError);
            net.AssertNoStray();
            return notices[^1];
        }

        // stable.json (cdn.example) is fine, dev.json (the dev host) fails: the dev host is named.
        var net1 = new FakeNet();
        net1.Feed("stable", s, Sign(stableKey, s));
        net1.Status(devBase + "dev.json", 503);
        var n1 = NoticeAfterTwoPasses(net1);
        Check.NotNull(n1);
        Check.Equal("checkfailed:dev", n1!.Key);
        Check.Contains(n1.Text, devHost);
        Check.DoesNotContain(n1.Text, "cdn.example");

        // The other way round: the stable host.
        var net2 = new FakeNet();
        net2.Status(Base + "stable.json", 503);
        net2.FeedAt(devBase, "dev", d, Sign(devKey, d));
        var n2 = NoticeAfterTwoPasses(net2);
        Check.Contains(n2!.Text, "cdn.example");
        Check.DoesNotContain(n2.Text, devHost);
    }

    [UnitTest]
    public static void A_legacy_failed_marker_beside_an_unsigned_download_is_cleaned_up_quietly()
    {
        using var dir = new TempDir();
        Updater.UpdatesDirOverride = dir.File("updates");
        Directory.CreateDirectory(Updater.UpdatesDir);
        byte[] exe = FakeExe();
        string file = Updater.FileFor(V("0.4.9"));
        void Lay() { File.WriteAllBytes(file, exe); File.WriteAllText(file + ".failed", "the update process did not finish\n2026-10-01 10:00:00 C:\\Clink.exe"); }

        // An older build's unsigned download (no signed hash) with its .failed marker: deleted with the marker, no balloon at start.
        Lay();
        var unsigned = new Settings { PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateFrom = "0.4.1" };
        var up = new Updater(unsigned, V("0.4.1"));
        Check.False(up.TryGetPendingUpdate(out _, out _));
        Check.Null(up.InstallProblem, "nothing to tell the user");
        Check.False(File.Exists(file), "the file is gone");
        Check.False(File.Exists(file + ".failed"), "and its marker");

        // A signed download with a .failed marker is still reported (and kept for a manual retry).
        Lay();
        var signed = new Settings { PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateSignedSha256 = Hex(exe), PendingUpdateFrom = "0.4.1" };
        var up2 = new Updater(signed, V("0.4.1"));
        Check.False(up2.TryGetPendingUpdate(out _, out _));
        Check.Contains(up2.InstallProblem, "could not be installed");
        Check.True(File.Exists(file));

        // The background check does not stop at an unsigned file's marker either: it is discarded and the signed one downloaded.
        Lay();
        var (pub, key) = NewKey();
        using var k = key;
        byte[] m = Bytes(Manifest("0.4.9", size: exe.Length, sha: Hex(exe)));
        var net = new FakeNet();
        net.Feed("stable", m, Sign(key, m));
        net.Ok(Base + "releases/v0.4.9/Clink.exe", exe);
        Updater.HttpOverride = net.Send;
        Updater.FeedOverride = Trust(stable: new[] { pub });
        var settings = new Settings { AutoUpdate = true, PendingUpdateVersion = "0.4.9", PendingUpdateSha256 = Hex(exe), PendingUpdateFrom = "0.4.1" };
        using var up3 = new Updater(settings, V("0.4.1"));
        OffUi(() => up3.RunBackgroundPassAsync(CancellationToken.None));
        Check.Equal(file, up3.PendingPath, up3.LastError);
        Check.Null(up3.LastError);
        Check.False(File.Exists(file + ".failed"), "the stale marker went with the file");
        Check.Equal(Hex(exe), settings.PendingUpdateSignedSha256!.ToLowerInvariant());
    }
}
