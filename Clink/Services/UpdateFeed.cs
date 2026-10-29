using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clink.Services;

/// <summary>One downloadable file of a feed manifest.</summary>
internal sealed record FeedAsset(string Url, long Size, string Sha256);

/// <summary>
/// A validated feed manifest (schema 1, contract v2 assets): <see cref="Exe"/> is the self-contained portable <c>Clink.exe</c>,
/// <see cref="ExeFramework"/> the framework-dependent <c>Clink-framework.exe</c> (optional; a framework client falls back to
/// <see cref="Exe"/> without it), <see cref="Installer"/> is optional and unused by the self-updater.
/// </summary>
internal sealed record FeedManifest(string Channel, ReleaseVersion Version, string Tag, DateTimeOffset Published, string NotesUrl, string MinRuntime,
    FeedAsset Exe, FeedAsset? Installer, FeedAsset? ExeFramework);

/// <summary>What the embedded feed.json says about updates (<see cref="FeedTrust.State"/>).</summary>
internal enum FeedState
{
    /// <summary>feed.json is the empty template: no feed, so no updates at all (the client contacts nothing).</summary>
    Off,
    /// <summary>A valid feedUrl and non-empty, valid, disjoint stableKeys and devKeys: the signed feed, for both channels.</summary>
    On,
    /// <summary>feed.json names a feedUrl or a key (or cannot be read) but is not fully valid: no update checks at all, fail closed.</summary>
    Misconfigured,
}

/// <summary>
/// The trust anchors of the signed update feed, read from the embedded resource <see cref="ResourceName"/> (committed as
/// <c>Clink/Assets/feed.json</c>): <c>{"schema":1,"feedUrl":"https://host/path/","stableKeys":["&lt;Base64 DER SPKI&gt;", ...],"devKeys":[...]}</c>.
/// <para>All or nothing (<see cref="State"/>): the fully empty file is <see cref="FeedState.Off"/> (no updates at all); a valid
/// feedUrl with non-empty, all-valid stableKeys and devKeys that share no key is <see cref="FeedState.On"/> for both channels; anything
/// else that names a URL or a key, or a file that cannot be read, is <see cref="FeedState.Misconfigured"/>: the updater then checks
/// nothing at all, so a broken trust anchor can never silently drop a build back to an unsigned path.
/// What is wrong is listed in <see cref="Problems"/> (never with key bytes) for the loud startup log line (<see cref="Describe"/>).</para>
/// </summary>
internal sealed class FeedTrust
{
    /// <summary>LogicalName of the embedded resource (see Clink.csproj).</summary>
    public const string ResourceName = "feed.json";
    public const int Schema = 1;
    internal const string EcPublicKeyOid = "1.2.840.10045.2.1";
    internal const string P256Oid = "1.2.840.10045.3.1.7";

    public FeedState State { get; }
    /// <summary>The signed feed is used (both channels); settings.json's legacy UpdateRepo key is ignored.</summary>
    public bool On => State == FeedState.On;
    /// <summary>feed.json is broken: no update checks and no installs at all.</summary>
    public bool Misconfigured => State == FeedState.Misconfigured;
    /// <summary>The validated base URL (https, a host, no query or fragment, ends in '/') when <see cref="On"/>; null otherwise.</summary>
    public string? FeedUrl { get; }
    /// <summary>
    /// The dev channel's base URL when <see cref="On"/>: feed.json's optional <c>devFeedUrl</c> (validated exactly like <see cref="FeedUrl"/>),
    /// or <see cref="FeedUrl"/> itself when devFeedUrl is absent or ""; null otherwise. dev.json, dev.json.sig and the dev binaries live
    /// here, stable.json, stable.json.sig and the stable binaries under <see cref="FeedUrl"/>.
    /// </summary>
    public string? DevFeedUrl { get; }
    /// <summary>The stable channel's keys when <see cref="On"/>; empty otherwise.</summary>
    public IReadOnlyList<byte[]> StableKeys { get; }
    /// <summary>The dev channel's keys when <see cref="On"/>; empty otherwise.</summary>
    public IReadOnlyList<byte[]> DevKeys { get; }
    /// <summary>What is wrong ("stableKeys[1] is invalid (not an EC public key)"); empty exactly when the state is not <see cref="FeedState.Misconfigured"/>.</summary>
    public IReadOnlyList<string> Problems { get; }
    private readonly int _stableListed, _devListed, _stableValid, _devValid;
    private readonly bool _urlListed, _urlValid, _devUrlListed, _devUrlValid;

    private FeedTrust(FeedState state, string? feedUrl, string? devFeedUrl, List<byte[]> stable, List<byte[]> dev, List<string> problems,
        int stableListed, int devListed, bool urlListed, bool urlValid, bool devUrlListed = false, bool devUrlValid = false)
    {
        State = state;
        bool on = state == FeedState.On;
        FeedUrl = on ? feedUrl : null;
        DevFeedUrl = on ? devFeedUrl ?? feedUrl : null;
        _devUrlListed = devUrlListed; _devUrlValid = devUrlValid;
        StableKeys = on ? stable : new List<byte[]>();
        DevKeys = on ? dev : new List<byte[]>();
        Problems = problems;
        _stableListed = stableListed; _devListed = devListed; _stableValid = stable.Count; _devValid = dev.Count;
        _urlListed = urlListed; _urlValid = urlValid;
    }

    /// <summary>No feed at all (no updates): what the empty feed.json gives.</summary>
    public static FeedTrust None => new(FeedState.Off, null, null, new(), new(), new(), 0, 0, false, false);

    /// <summary>The keys that may sign a manifest of <paramref name="channel"/> ("stable" or "dev"); any one of them verifying is enough (rotation).</summary>
    public IReadOnlyList<byte[]> KeysFor(string channel) => channel == "dev" ? DevKeys : StableKeys;

    /// <summary>The base URL (ends in '/') under which <paramref name="channel"/>'s manifest, signature and binaries live; null unless <see cref="On"/>.</summary>
    public string? BaseFor(string channel) => channel == "dev" ? DevFeedUrl : FeedUrl;

    /// <summary>From the parts (the unit tests, and <see cref="Parse"/>): every entry is validated and the state decided (see the class remarks).</summary>
    public static FeedTrust Create(string? feedUrl, IEnumerable<string?>? stableKeys, IEnumerable<string?>? devKeys, List<string>? problems = null, string? devFeedUrl = null)
    {
        problems ??= new();
        string? url = null;
        bool urlListed = !string.IsNullOrWhiteSpace(feedUrl);
        if (urlListed)
        {
            url = ValidFeedUrl(feedUrl, out string? why);
            if (url == null) problems.Add($"feedUrl is invalid ({why})");
        }
        // devFeedUrl: absent or "" = the dev channel uses feedUrl; anything else (a blank string too) must pass the very same validator.
        string? devUrl = null;
        bool devUrlListed = !string.IsNullOrEmpty(devFeedUrl);
        if (devUrlListed)
        {
            devUrl = ValidFeedUrl(devFeedUrl, out string? why);
            if (devUrl == null) problems.Add($"devFeedUrl is invalid ({why})");
            if (!urlListed) problems.Add("devFeedUrl is set but feedUrl is empty");
        }
        // Two different bases must be disjoint: with one inside the other, a binary under the inner base would pass the outer channel's
        // "under my base" check too. Compared on the normalised absolute form (both end in '/'), ordinally.
        if (url != null && devUrl != null && !string.Equals(url, devUrl, StringComparison.Ordinal)
            && (devUrl.StartsWith(url, StringComparison.Ordinal) || url.StartsWith(devUrl, StringComparison.Ordinal)))
            problems.Add("feedUrl and devFeedUrl are nested (one is inside the other)");
        var stable = Keys(stableKeys, "stableKeys", problems, out int stableListed);
        var dev = Keys(devKeys, "devKeys", problems, out int devListed);
        if (urlListed || devUrlListed || stableListed > 0 || devListed > 0)
        {
            // Something is configured: then everything must be, for both channels together.
            if (!urlListed && (stableListed > 0 || devListed > 0)) problems.Add("feedUrl is empty but keys are listed");
            if (stableListed == 0) problems.Add("stableKeys is empty (the feed needs keys for both channels)");
            if (devListed == 0) problems.Add("devKeys is empty (the feed needs keys for both channels)");
            if (stable.Any(s => dev.Any(d => s.AsSpan().SequenceEqual(d)))) problems.Add("the same key is listed in stableKeys and devKeys");
        }
        var state = problems.Count > 0 ? FeedState.Misconfigured : urlListed ? FeedState.On : FeedState.Off;
        return new FeedTrust(state, url, devUrlListed ? devUrl : null, stable, dev, problems, stableListed, devListed, urlListed, url != null, devUrlListed, devUrl != null);

        static List<byte[]> Keys(IEnumerable<string?>? list, string name, List<string> problems, out int listed)
        {
            var valid = new List<byte[]>();
            listed = 0;
            foreach (string? text in list ?? Array.Empty<string?>())
            {
                if (ParseP256Key(text, out string? why) is { } der) valid.Add(der);
                else problems.Add($"{name}[{listed}] is invalid ({why})");
                listed++;
            }
            return valid;
        }
    }

    /// <summary>The feed.json text (a UTF-8 BOM is allowed). Anything unreadable is <see cref="FeedState.Misconfigured"/> with the reason in <see cref="Problems"/>.</summary>
    public static FeedTrust Parse(byte[] json)
    {
        var problems = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(UpdateFeed.WithoutBom(json));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Broken($"{ResourceName} is not a JSON object");
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int sv) || sv != Schema)
                return Broken($"{ResourceName} has an unsupported schema");
            string? url = null;
            if (root.TryGetProperty("feedUrl", out var u))
            {
                if (u.ValueKind == JsonValueKind.String) url = u.GetString();
                else if (u.ValueKind != JsonValueKind.Null) problems.Add("feedUrl is not a string");
            }
            string? devUrl = null;   // devFeedUrl is optional: absent, null and "" all mean "the dev channel uses feedUrl"
            if (root.TryGetProperty("devFeedUrl", out var du))
            {
                if (du.ValueKind == JsonValueKind.String) devUrl = du.GetString();
                else if (du.ValueKind != JsonValueKind.Null) problems.Add("devFeedUrl is not a string");
            }
            return Create(url, Strings(root, "stableKeys", problems), Strings(root, "devKeys", problems), problems, devUrl);
        }
        catch (JsonException) { return Broken($"{ResourceName} is not valid JSON"); }

        static List<string?> Strings(JsonElement root, string name, List<string> problems)
        {
            var items = new List<string?>();
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind == JsonValueKind.Null) return items;
            if (arr.ValueKind != JsonValueKind.Array) { problems.Add($"{name} is not a list"); return items; }
            foreach (var e in arr.EnumerateArray()) items.Add(e.ValueKind == JsonValueKind.String ? e.GetString() : null);   // a non-string is an invalid entry
            return items;
        }
    }

    /// <summary>The embedded <see cref="ResourceName"/> of this exe (missing or unreadable = <see cref="FeedState.Misconfigured"/>).</summary>
    public static FeedTrust LoadEmbedded()
    {
        try
        {
            using var s = typeof(FeedTrust).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) return Broken($"the {ResourceName} resource is missing");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Parse(ms.ToArray());
        }
        catch (Exception ex) { return Broken($"{ResourceName} could not be read ({ex.GetType().Name})"); }
    }

    private static FeedTrust Broken(string problem) => new(FeedState.Misconfigured, null, null, new(), new(), new() { problem }, 0, 0, false, false);

    /// <summary>The startup log line: the state, URL validity, key counts and validity, and what is wrong. Never key bytes.</summary>
    public string Describe()
    {
        string url = _urlValid ? FeedUrl ?? "valid" : _urlListed ? "invalid" : "empty";
        string devUrl = _devUrlListed ? _devUrlValid ? $"; devFeedUrl {DevFeedUrl ?? "valid"}" : "; devFeedUrl invalid" : "";
        string counts = $"feedUrl {url}{devUrl}; stableKeys {_stableValid} valid of {_stableListed}; devKeys {_devValid} valid of {_devListed}";
        return State switch
        {
            FeedState.Off => $"no signed update feed in this build ({ResourceName} is empty): updates are off",
            FeedState.On => $"signed update feed ({ResourceName}): {counts}; on for both channels",
            _ => $"update configuration invalid ({ResourceName}): {string.Join("; ", Problems)}; {counts}. "
                 + "No update checks at all and no downloaded update is installed",
        };
    }

    /// <summary>
    /// <paramref name="text"/> as a feed base URL: absolute https with a host, no user info, query or fragment, and a path that
    /// ends in '/'. Null with <paramref name="why"/> otherwise (a missing '/' is not added: the URL is taken as written or not at all).
    /// </summary>
    internal static string? ValidFeedUrl(string? text, out string? why)
    {
        why = null;
        string t = (text ?? "").Trim();
        if (t.Length == 0) { why = "empty"; return null; }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) { why = "not an absolute URL"; return null; }
        if (u.Scheme != Uri.UriSchemeHttps) { why = "not https"; return null; }
        if (string.IsNullOrEmpty(u.Host)) { why = "no host"; return null; }
        if (t.Contains('?') || t.Contains('#') || u.Query.Length > 0 || u.Fragment.Length > 0) { why = "has a query or fragment"; return null; }
        if (u.UserInfo.Length > 0) { why = "has user info"; return null; }
        if (!t.EndsWith('/') || !u.AbsolutePath.EndsWith('/')) { why = "must end with '/'"; return null; }
        return u.AbsoluteUri;
    }

    /// <summary>
    /// The DER of <paramref name="base64"/> when it is a SubjectPublicKeyInfo of an EC key on the P-256 curve (algorithm id-ecPublicKey,
    /// named curve OID 1.2.840.10045.3.1.7, an uncompressed point that the platform accepts); null with <paramref name="why"/> otherwise.
    /// The curve is checked by its OID, not by the key size: brainpoolP256r1 is 256 bits too.
    /// </summary>
    internal static byte[]? ParseP256Key(string? base64, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(base64)) { why = "empty or not a string"; return null; }
        byte[] der;
        try { der = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { why = "not Base64"; return null; }
        try
        {
            var outer = new AsnReader(der, AsnEncodingRules.DER);
            var spki = outer.ReadSequence();
            outer.ThrowIfNotEmpty();
            var alg = spki.ReadSequence();
            if (alg.ReadObjectIdentifier() != EcPublicKeyOid) { why = "not an EC public key"; return null; }
            if (!alg.HasData || alg.PeekTag() != Asn1Tag.ObjectIdentifier) { why = "an EC key without a named curve"; return null; }
            string curve = alg.ReadObjectIdentifier();
            alg.ThrowIfNotEmpty();
            if (curve != P256Oid) { why = $"curve {curve} is not P-256"; return null; }
            byte[] point = spki.ReadBitString(out int unusedBits);
            spki.ThrowIfNotEmpty();
            if (unusedBits != 0 || point.Length != 65 || point[0] != 0x04) { why = "not an uncompressed P-256 point"; return null; }
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(der, out int read);
            if (read != der.Length) { why = "trailing bytes"; return null; }
            return der;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            why = "not a valid SubjectPublicKeyInfo";
            return null;
        }
    }
}

/// <summary>
/// Reaching the feed failed (network error, HTTP error status, timeout): the check fails, there is no fallback. <see cref="UserDetail"/>
/// is the short safe part that may be shown to the user ("HTTP 503", "no answer within 10 s": built here, never a server's text such as
/// a reason phrase); <see cref="Exception.Message"/> is for the log.
/// </summary>
internal sealed class FeedUnreachableException : Exception
{
    public string? UserDetail { get; }
    public FeedUnreachableException(string message, Exception? inner = null, string? userDetail = null) : base(message, inner) { UserDetail = userDetail; }
}

/// <summary>Something was fetched but cannot be trusted (missing or bad signature, too large, invalid manifest): it is not used and the check fails (no fallback).</summary>
internal sealed class FeedRejectedException : Exception
{
    public FeedRejectedException(string message) : base(message) { }
}

/// <summary>
/// The signed static update feed (schema 1): <c>&lt;feedUrl&gt;stable.json</c> / <c>dev.json</c> plus <c>&lt;channel&gt;.json.sig</c>, the
/// Base64 of an ECDSA P-256 / SHA-256 signature (IEEE P1363, 64 bytes) over the exact manifest bytes (a UTF-8 BOM included, if the
/// file has one). Nothing from a manifest is used before its signature has verified. The newest valid signed manifest is trusted as it
/// is: there is no age limit (<c>published</c> is parsed but never decides anything), and the updater's version floors refuse a replay.
/// </summary>
internal static class UpdateFeed
{
    internal const int Schema = 1;
    internal const int MaxManifestBytes = 256 * 1024;
    internal const int MaxSignatureBytes = 4 * 1024;
    /// <summary>Per feed request, headers and body (a linked token: the caller's cancellation still wins). The default of the timeout parameters below.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex s_sha = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_isoStart = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly byte[] s_bom = { 0xEF, 0xBB, 0xBF };
    /// <summary>A percent-escape of an unreserved character (ALPHA, DIGIT, '-', '.', '_', '~'), which URI normalisation turns back into the plain character.</summary>
    private static readonly Regex s_escapedUnreserved = new("%(?:3[0-9]|[46][1-9A-Fa-f]|[57][0-9Aa]|2[Dd]|2[Ee]|5[Ff]|7[Ee])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A GET that returns the response with its headers read (the body still streaming). Injected so that tests need no network.</summary>
    internal delegate Task<HttpResponseMessage> Get(string url, CancellationToken ct);

    /// <summary>"stable" or "dev".</summary>
    internal static string ChannelName(bool dev) => dev ? "dev" : "stable";

    /// <summary><paramref name="bytes"/> without a leading UTF-8 BOM (the signature still covers the raw bytes).</summary>
    internal static ReadOnlyMemory<byte> WithoutBom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(s_bom) ? bytes.AsMemory(s_bom.Length) : bytes.AsMemory();

    /// <summary>
    /// GETs <paramref name="url"/> and returns at most <paramref name="limit"/> bytes. A Content-Length over the limit is refused before
    /// the body is read, and at most limit+1 bytes are ever read. <see cref="FeedUnreachableException"/> for a network error, an HTTP
    /// error status (except 404/410 with <paramref name="missingIsRejection"/>) or no answer within <paramref name="requestTimeout"/>
    /// (<see cref="RequestTimeout"/> when null; headers and body together); <see cref="FeedRejectedException"/> for a body that is too
    /// large or a missing file; a cancelled <paramref name="ct"/> throws as such.
    /// </summary>
    internal static async Task<byte[]> FetchCappedAsync(Get get, string url, int limit, bool missingIsRejection, CancellationToken ct, TimeSpan? requestTimeout = null)
    {
        TimeSpan limitTime = requestTimeout ?? RequestTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limitTime);
        string name = NameOf(url);
        try
        {
            using var resp = await get(url, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                if (missingIsRejection && code is 404 or 410) throw new FeedRejectedException($"{name} is missing (HTTP {code})");
                throw new FeedUnreachableException($"HTTP {code} for {name}", null, $"HTTP {code}");   // the code only: the reason phrase is the server's text
            }
            if (resp.Content.Headers.ContentLength is long declared && declared > limit)
                throw new FeedRejectedException($"{name} is too large ({declared} bytes, at most {limit})");
            var body = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buf = new byte[limit + 1];
            int total = 0;
            await using (body.ConfigureAwait(false))
            {
                while (total < buf.Length)
                {
                    int n = await body.ReadAsync(buf.AsMemory(total), timeout.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    total += n;
                }
            }
            if (total > limit) throw new FeedRejectedException($"{name} is too large (more than {limit} bytes)");
            return buf.AsSpan(0, total).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            string within = limitTime.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
            throw new FeedUnreachableException($"no answer for {name} within {within} s", ex, $"no answer within {within} s");
        }
        catch (Exception ex) when (ex is not FeedRejectedException and not FeedUnreachableException)
        {
            throw new FeedUnreachableException($"{name}: {ex.Message.ReplaceLineEndings(" ").Trim()}", ex);
        }
    }

    /// <summary>
    /// Fetches a manifest (capped, checked before the signature is even asked for), then its signature (capped), verifies it with any
    /// of <paramref name="keys"/> and validates it for <paramref name="channel"/>. Throws <see cref="FeedUnreachableException"/> or
    /// <see cref="FeedRejectedException"/> (see <see cref="FetchCappedAsync"/>); a missing signature is a rejection.
    /// </summary>
    internal static async Task<FeedManifest> FetchVerifiedAsync(Get get, string manifestUrl, string signatureUrl, string channel, IReadOnlyList<byte[]> keys,
        CancellationToken ct, TimeSpan? requestTimeout = null, string? assetsBase = null)
    {
        byte[] manifest = await FetchCappedAsync(get, manifestUrl, MaxManifestBytes, missingIsRejection: false, ct, requestTimeout).ConfigureAwait(false);
        byte[] sig = await FetchCappedAsync(get, signatureUrl, MaxSignatureBytes, missingIsRejection: true, ct, requestTimeout).ConfigureAwait(false);
        if (!VerifyAny(manifest, Encoding.UTF8.GetString(sig), keys, out string? problem)) throw new FeedRejectedException(problem ?? "bad signature");
        var m = ParseManifest(manifest, channel, out problem) ?? throw new FeedRejectedException(problem ?? "invalid manifest");
        if (assetsBase != null && AssetsOutside(m, assetsBase) is { } outside) throw new FeedRejectedException(outside);
        return m;
    }

    /// <summary>
    /// Why a verified manifest's binaries do not all sit under <paramref name="assetsBase"/> (the base of the feed the manifest belongs
    /// to: feedUrl for stable.json, the dev base for dev.json), or null. The URL must start with the base (scheme, host, port and path
    /// prefix, compared on the normalised form), with no query or fragment, and its rest must not hold a backslash (a literal or an
    /// encoded %5C), a "." / ".." segment (also when encoded, e.g. %2e%2e) or an empty segment ("//"). An encoded slash (%2F) in the rest is NOT rejected: it
    /// stays inside the base's path as one segment name, and with ".." it is caught by the dot rule.
    /// </summary>
    internal static string? AssetsOutside(FeedManifest m, string assetsBase)
    {
        foreach (var (name, asset) in new (string, FeedAsset?)[] { ("exe", m.Exe), ("installer", m.Installer), ("exeFramework", m.ExeFramework) })
            if (asset != null && !IsUnder(asset.Url, assetsBase)) return $"the {name} asset URL is not under {assetsBase}";
        return null;
    }

    /// <summary>True when <paramref name="url"/> is an https URL strictly inside <paramref name="assetsBase"/> (ends in '/').</summary>
    internal static bool IsUnder(string url, string assetsBase)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Uri.TryCreate(assetsBase, UriKind.Absolute, out var b)) return false;
        // .NET quietly decodes an escaped unreserved character ("%63link" is "clink"): a URL that relies on that is not one a publisher writes. Fail closed.
        if (s_escapedUnreserved.IsMatch(url)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || url.Contains('?') || url.Contains('#')) return false;
        // Hosts compared as their IDN (punycode) form, lower-cased, ordinally: a trailing dot or a look-alike Unicode letter is a different host.
        if (!string.Equals(u.Scheme, b.Scheme, StringComparison.Ordinal) || u.Port != b.Port
            || !string.Equals(u.IdnHost.ToLowerInvariant(), b.IdnHost.ToLowerInvariant(), StringComparison.Ordinal)) return false;
        string basePath = b.AbsolutePath;
        if (!basePath.EndsWith('/')) basePath += "/";
        if (!u.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal) || u.AbsolutePath.Length == basePath.Length) return false;
        string rest = Uri.UnescapeDataString(u.AbsolutePath[basePath.Length..]);
        // Empty segments ("//") are refused too (fail closed): a published binary URL never has one.
        return !rest.Contains('\\') && !rest.Split('/').Any(s => s is ".." or "." or "");
    }

    /// <summary>
    /// True if <paramref name="signatureText"/> (Base64; a BOM / U+FEFF and all whitespace anywhere are ignored) is a valid ECDSA
    /// P-256 / SHA-256 signature (IEEE P1363) of exactly <paramref name="manifest"/> under ANY of <paramref name="keys"/> (DER SPKI,
    /// validated by <see cref="FeedTrust.ParseP256Key"/>). False with a reason otherwise.
    /// </summary>
    internal static bool VerifyAny(byte[] manifest, string? signatureText, IReadOnlyList<byte[]> keys, out string? problem)
    {
        problem = null;
        var clean = new StringBuilder();
        foreach (char c in signatureText ?? "") if (c != '﻿' && !char.IsWhiteSpace(c)) clean.Append(c);
        if (clean.Length == 0) { problem = "the signature is empty"; return false; }
        byte[] sig;
        try { sig = Convert.FromBase64String(clean.ToString()); }
        catch (FormatException) { problem = "the signature is not Base64"; return false; }
        if (sig.Length != 64) { problem = "the signature has the wrong length"; return false; }
        if (keys.Count == 0) { problem = "no trusted key for this channel"; return false; }
        foreach (byte[] spki in keys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);
                if (key.VerifyData(manifest, sig, HashAlgorithmName.SHA256)) return true;
            }
            catch (CryptographicException) { }
        }
        problem = "the signature does not match any trusted key";
        return false;
    }

    /// <summary>
    /// Validates manifest bytes (UTF-8 JSON, a leading BOM allowed) for <paramref name="channel"/>: schema 1, the channel it says, a
    /// version in canonical form ("0.4.2", "0.4.3-dev.7", no "v") of that channel (stable: a plain X.Y.Z; dev: an X.Y.Z-dev.N, a
    /// plain release in dev.json is refused: releases are published in stable.json only), tag "v"+version, an ISO 8601
    /// <c>published</c> time, an https exe asset with a size &gt; 0 and a lowercase SHA-256, https for every other asset URL (notesUrl is kept as signed and checked only when opened). Unknown fields
    /// are ignored. Does NOT check the signature (see <see cref="VerifyAny"/>).
    /// </summary>
    internal static FeedManifest? ParseManifest(byte[] json, string channel, out string? problem)
    {
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(WithoutBom(json));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problem = "manifest is not a JSON object"; return null; }
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int sv) || sv != Schema)
            { problem = "unsupported manifest schema"; return null; }
            string ch = Str(root, "channel");
            if (ch != channel) { problem = $"manifest is for channel '{Clip(ch)}', expected '{channel}'"; return null; }
            string versionText = Str(root, "version");
            var version = ReleaseVersion.ParseCanonical(versionText);
            if (version == null) { problem = $"bad version '{Clip(versionText)}' (expected X.Y.Z or X.Y.Z-dev.N)"; return null; }
            if (channel == "stable" && version.Value.IsPrerelease) { problem = $"version {versionText} is a pre-release but the channel is stable"; return null; }
            if (channel == "dev" && !version.Value.IsDevBuild) { problem = $"version {versionText} is not a dev build (X.Y.Z-dev.N) but the channel is dev"; return null; }
            string tag = Str(root, "tag");
            if (tag != "v" + versionText) { problem = $"tag '{Clip(tag)}' does not match version {versionText}"; return null; }
            string publishedText = Str(root, "published");
            if (!s_isoStart.IsMatch(publishedText)
                || !DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var published))
            { problem = "published is missing or not an ISO 8601 time"; return null; }
            string notes = Str(root, "notesUrl");   // kept as signed; only opened after NotesPage has checked it (a bad one means the fallback page, never a refused manifest)
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object) { problem = "manifest has no assets"; return null; }
            if (!assets.TryGetProperty("exe", out var exeEl) || exeEl.ValueKind != JsonValueKind.Object) { problem = "manifest has no exe asset"; return null; }
            var exe = Asset(exeEl, "exe", out problem);
            if (exe == null) return null;
            FeedAsset? installer = null, exeFramework = null;
            if (assets.TryGetProperty("installer", out var inst) && inst.ValueKind == JsonValueKind.Object) { installer = Asset(inst, "installer", out problem); if (installer == null) return null; }
            if (assets.TryGetProperty("exeFramework", out var fx) && fx.ValueKind == JsonValueKind.Object) { exeFramework = Asset(fx, "exeFramework", out problem); if (exeFramework == null) return null; }
            return new FeedManifest(ch, version.Value, tag, published, notes, Str(root, "minRuntime"), exe, installer, exeFramework);
        }
        catch (JsonException) { problem = "manifest is not valid JSON"; return null; }

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static FeedAsset? Asset(JsonElement e, string name, out string? why)
        {
            why = null;
            string url = Str(e, "url"), sha = Str(e, "sha256");
            if (!IsHttps(url)) { why = $"{name} asset URL is missing or not https"; return null; }
            if (!s_sha.IsMatch(sha)) { why = $"{name} asset sha256 is not 64 lowercase hex digits"; return null; }
            if (!e.TryGetProperty("size", out var sz) || sz.ValueKind != JsonValueKind.Number || !sz.TryGetInt64(out long size) || size <= 0) { why = $"{name} asset size is missing or invalid"; return null; }
            return new FeedAsset(url, size, sha);
        }
    }

    /// <summary>The floor check: why <paramref name="m"/> is below <paramref name="floor"/> (the highest version verified before on its channel: a replayed older manifest), or null. There is no age rule.</summary>
    internal static string? FloorReason(FeedManifest m, ReleaseVersion? floor) =>
        floor is { } f && m.Version < f ? $"v{m.Version} is older than v{f}, verified before" : null;

    /// <summary>An absolute https URL with a host and no user info.</summary>
    internal static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host) && u.UserInfo.Length == 0;

    /// <summary>
    /// The asset a client of <paramref name="packaging"/> takes from <paramref name="m"/>, and the flavour that asset is.
    /// SelfContained always takes <see cref="FeedManifest.Exe"/>. Framework takes <see cref="FeedManifest.ExeFramework"/>, unless the
    /// manifest has none or its minRuntime is newer than every installed Microsoft.WindowsDesktop.App runtime
    /// (<paramref name="installedRuntimes"/>; <see cref="DotNetRuntimes.InstalledDesktop"/> when null): then <see cref="FeedManifest.Exe"/>
    /// (self-contained, it needs no runtime).
    /// </summary>
    internal static (FeedAsset Asset, Packaging Flavour) PickAsset(FeedManifest m, Packaging packaging, IEnumerable<Version>? installedRuntimes = null)
    {
        if (packaging != Packaging.Framework || m.ExeFramework == null) return (m.Exe, Packaging.SelfContained);
        return DotNetRuntimes.MeetsMinimum(m.MinRuntime, installedRuntimes ?? DotNetRuntimes.InstalledDesktop())
            ? (m.ExeFramework, Packaging.Framework)
            : (m.Exe, Packaging.SelfContained);
    }

    /// <summary>Where the release notes go when a manifest's <c>notesUrl</c> is missing, not https or not under its feed's base.</summary>
    internal const string FallbackNotesPage = "https://euphoric.software/";

    /// <summary>
    /// <paramref name="notesUrl"/> (a signed manifest's <c>notesUrl</c>) as the address to open, or null: only an https URL strictly under
    /// <paramref name="feedBase"/>, the base of the very feed that manifest came from (<see cref="IsUnder"/>), passes. The result is the
    /// normalised <see cref="Uri.AbsoluteUri"/>, which is what the shell is given (what was checked is what is opened). Null feedBase: null.
    /// </summary>
    internal static string? NotesPage(string? notesUrl, string? feedBase)
    {
        if (string.IsNullOrWhiteSpace(notesUrl) || feedBase == null || notesUrl.Length > 2048 || notesUrl.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return null;
        if (!IsUnder(notesUrl, feedBase)) return null;
        return Uri.TryCreate(notesUrl, UriKind.Absolute, out var u) ? u.AbsoluteUri : null;
    }

    /// <summary>The updater's view of a feed manifest: the asset <see cref="PickAsset"/> chooses, <see cref="UpdateInfo.Sha256"/> carries the signed hash, <see cref="UpdateInfo.NotesUrl"/> the manifest's notes address as signed (checked only when opened).</summary>
    internal static UpdateInfo ToUpdateInfo(FeedManifest m, Packaging packaging = Packaging.SelfContained, IEnumerable<Version>? installedRuntimes = null)
    {
        var (a, flavour) = PickAsset(m, packaging, installedRuntimes);
        return new(m.Version, m.Tag, a.Url, a.Size, a.Sha256, m.NotesUrl, flavour);
    }

    private static string NameOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Segments.LastOrDefault() is { Length: > 0 } last && last != "/" ? Uri.UnescapeDataString(last) : u.Host : "the feed";

    private static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
