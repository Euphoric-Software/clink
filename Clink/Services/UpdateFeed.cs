using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clink.Services;

/// <summary>One downloadable file of a feed manifest.</summary>
internal sealed record FeedAsset(string Url, long Size, string Sha256);

/// <summary>A validated feed manifest (contract v1). <see cref="Installer"/> and <see cref="Portable"/> are optional and unused by the self-updater.</summary>
internal sealed record FeedManifest(string Channel, ReleaseVersion Version, string Tag, string NotesUrl, string MinRuntime, FeedAsset Exe, FeedAsset? Installer, FeedAsset? Portable);

/// <summary>
/// The signed static update feed (contract v1): <c>&lt;base&gt;stable.json</c> / <c>dev.json</c> plus <c>&lt;channel&gt;.json.sig</c>, the Base64 of an
/// ECDSA P-256 / SHA-256 signature (IEEE P1363, 64 bytes) over the exact manifest bytes. The public key is baked into the exe
/// (Base64 DER SubjectPublicKeyInfo). Nothing from a manifest is used before its signature has verified.
/// </summary>
internal static class UpdateFeed
{
    internal const int Schema = 1;
    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxSignatureBytes = 4 * 1024;
    private static readonly Regex s_sha = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Fetches a URL and returns the body; throws on a network error or a non-success status. Injected so tests need no network.</summary>
    internal delegate Task<byte[]> Fetch(string url, CancellationToken ct);

    /// <summary>"stable" or "dev".</summary>
    internal static string ChannelName(bool dev) => dev ? "dev" : "stable";

    /// <summary>
    /// Downloads, verifies and validates the manifest of a channel and maps it to an <see cref="UpdateInfo"/> (with the inline
    /// SHA-256, so no <c>Clink.exe.sha256</c> is fetched). Null with <paramref name="problem"/> on any failure; never throws except
    /// for a cancelled <paramref name="ct"/>.
    /// </summary>
    internal static async Task<(UpdateInfo? Info, string? Problem)> TryGetAsync(Fetch fetch, string baseUrl, string publicKey, bool dev, CancellationToken ct)
    {
        string channel = ChannelName(dev);
        try
        {
            string manifestUrl = baseUrl + channel + ".json";
            byte[] manifest = await fetch(manifestUrl, ct).ConfigureAwait(false);
            byte[] sig = await fetch(manifestUrl + ".sig", ct).ConfigureAwait(false);
            if (manifest.Length > MaxManifestBytes) return (null, "manifest is too large");
            if (sig.Length > MaxSignatureBytes) return (null, "signature file is too large");
            if (!VerifySignature(manifest, Encoding.UTF8.GetString(sig), publicKey, out string? sigProblem)) return (null, sigProblem);
            var parsed = ParseManifest(manifest, channel, out string? problem);
            if (parsed == null) return (null, problem);
            return (ToUpdateInfo(parsed), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, ex.Message.ReplaceLineEndings(" ").Trim()); }
    }

    /// <summary>
    /// True if <paramref name="signatureText"/> (Base64, surrounding or embedded whitespace ignored) is a valid ECDSA P-256 / SHA-256
    /// signature of exactly <paramref name="manifest"/> under <paramref name="publicKeyBase64"/>. False with a reason otherwise.
    /// </summary>
    internal static bool VerifySignature(byte[] manifest, string signatureText, string publicKeyBase64, out string? problem)
    {
        problem = null;
        byte[] sig;
        try { sig = Convert.FromBase64String(Regex.Replace(signatureText ?? "", @"\s+", "")); }
        catch (FormatException) { problem = "signature is not Base64"; return false; }
        if (sig.Length != 64) { problem = "signature has the wrong length"; return false; }
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String((publicKeyBase64 ?? "").Trim()), out _);
            if (key.KeySize != 256) { problem = "the built-in public key is not a P-256 key"; return false; }
            if (!key.VerifyData(manifest, sig, HashAlgorithmName.SHA256)) { problem = "signature does not match the manifest"; return false; }
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            problem = "signature check failed: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Validates manifest bytes (UTF-8 JSON) for <paramref name="channel"/>: schema 1, the channel it says, a version in the
    /// canonical form ("0.4.2", "0.4.3-dev.7", no "v") allowed on that channel, tag "v"+version, an https exe asset with a size and a
    /// lowercase SHA-256, https for every other URL. Unknown fields are ignored. Does NOT check the signature (see <see cref="VerifySignature"/>).
    /// </summary>
    internal static FeedManifest? ParseManifest(byte[] json, string channel, out string? problem)
    {
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problem = "manifest is not a JSON object"; return null; }
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int sv) || sv != Schema)
            { problem = $"unsupported manifest schema {(root.TryGetProperty("schema", out var raw) ? raw.ToString() : "(none)")}"; return null; }
            string ch = Str(root, "channel");
            if (ch != channel) { problem = $"manifest is for channel '{ch}', expected '{channel}'"; return null; }
            string versionText = Str(root, "version");
            var version = ReleaseVersion.Parse(versionText);
            if (version == null || version.Value.ToString() != versionText) { problem = $"bad version '{versionText}'"; return null; }
            if (channel == "stable" && version.Value.IsPrerelease) { problem = $"version {versionText} is a pre-release but the channel is stable"; return null; }
            if (channel == "dev" && version.Value.IsPrerelease && !version.Value.IsDevBuild) { problem = $"version {versionText} is not a dev build"; return null; }
            string tag = Str(root, "tag");
            if (tag != "v" + versionText) { problem = $"tag '{tag}' does not match version {versionText}"; return null; }
            string notes = Str(root, "notesUrl");
            if (notes.Length > 0 && !IsHttps(notes)) { problem = "notesUrl is not https"; return null; }
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object) { problem = "manifest has no assets"; return null; }
            if (!assets.TryGetProperty("exe", out var exeEl) || exeEl.ValueKind != JsonValueKind.Object) { problem = "manifest has no exe asset"; return null; }
            var exe = Asset(exeEl, "exe", out problem);
            if (exe == null) return null;
            FeedAsset? installer = null, portable = null;
            if (assets.TryGetProperty("installer", out var inst) && inst.ValueKind == JsonValueKind.Object) { installer = Asset(inst, "installer", out problem); if (installer == null) return null; }
            if (assets.TryGetProperty("portable", out var port) && port.ValueKind == JsonValueKind.Object) { portable = Asset(port, "portable", out problem); if (portable == null) return null; }
            return new FeedManifest(ch, version.Value, tag, notes, Str(root, "minRuntime"), exe, installer, portable);
        }
        catch (JsonException ex) { problem = "manifest is not valid JSON: " + ex.Message; return null; }

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

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);

    /// <summary>The updater's view of a manifest: ShaUrl is empty and <see cref="UpdateInfo.Sha256"/> carries the hash.</summary>
    internal static UpdateInfo ToUpdateInfo(FeedManifest m) =>
        new(m.Version, m.Tag, m.Exe.Url, m.Exe.Size, "", m.NotesUrl, "", m.Exe.Sha256);
}
