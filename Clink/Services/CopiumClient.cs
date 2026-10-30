using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Clink.Services;

internal sealed record CopiumUpload(string Id, string Link, string Url, string PageUrl, string? ThumbUrl, long Size);

/// <summary>Copium refused an upload with an HTTP status; <see cref="StatusCode"/> lets a caller tell "this account / file" from "the picture type is not accepted" (400 / 415).</summary>
internal sealed class CopiumHttpException : InvalidOperationException
{
    public int StatusCode { get; }
    /// <summary>Copium's own error text from the answer (null when there was none): <see cref="CopiumClient.IsTypeRefusal"/> reads it.</summary>
    public string? ErrorText { get; }
    public CopiumHttpException(int statusCode, string message, string? errorText = null) : base(message) { StatusCode = statusCode; ErrorText = errorText; }
}

internal enum MediaKind { Video, Image }

/// <summary>Upload client for copium.pics (https://copium.pics/developers).</summary>
internal static class CopiumClient
{
    // No redirects: the bearer token must never follow a 3xx to another address (a 3xx is an error instead).
    private static readonly SocketsHttpHandler s_handler = new() { AllowAutoRedirect = false };
    private static readonly HttpClient s_http = new(s_handler) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Whether the real shared client follows redirects (it must not: the bearer token would go along).</summary>
    internal static bool FollowsRedirects => s_handler.AllowAutoRedirect;
    public const string UploadEndpoint = "https://v.copium.pics/api/upload";

    /// <summary>Test seam: sends the request through this client instead of the shared one (a mock handler).</summary>
    internal static HttpClient? HttpOverride { get; set; }

    /// <summary>The one client for everything sent to Copium (no redirects), or the test override.</summary>
    internal static HttpClient Http => HttpOverride ?? s_http;

    /// <summary>The MIME type of the file part: by extension (.mp4 video/mp4, .png image/png, .jpg/.jpeg image/jpeg, .gif, .webp), anything else application/octet-stream.</summary>
    internal static string ContentTypeFor(string path) => System.IO.Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>What kind of upload a file is: pictures by extension; everything else (clips, recordings) is a video.</summary>
    internal static MediaKind KindOf(string path) => ContentTypeFor(path).StartsWith("image/", StringComparison.Ordinal) ? MediaKind.Image : MediaKind.Video;

    /// <summary><c>copium.pics</c> or a subdomain of it: the only hosts the token may be sent to (an answer's links may point anywhere: see <see cref="IsHttpsUrl"/>).</summary>
    internal static bool IsCopiumHost(string? host) =>
        !string.IsNullOrEmpty(host) && (host.Equals("copium.pics", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".copium.pics", StringComparison.OrdinalIgnoreCase));

    /// <summary>An absolute https address on <see cref="IsCopiumHost"/>, no user info.</summary>
    internal static bool IsCopiumHttps(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !IsCopiumHost(u.Host) || !string.IsNullOrEmpty(u.UserInfo)) return false;
        uri = u;
        return true;
    }

    /// <summary>An absolute https address (what a link read from a file or an answer must be before the shell opens it).</summary>
    internal static bool IsHttpsUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host) || !string.IsNullOrEmpty(u.UserInfo)) return false;   // (no "https://copium.pics@elsewhere" look-alike)
        uri = u;
        return true;
    }

    /// <summary>The override's address when <paramref name="imageOverride"/> is one that may be used (<see cref="IsCopiumHttps"/>); null when it is blank or not allowed. No logging.</summary>
    internal static Uri? OverrideUri(string? imageOverride) => IsCopiumHttps(imageOverride, out var uri) ? uri : null;

    /// <summary>
    /// Where an upload goes: for an image, <paramref name="imageOverride"/> when it is an https URL on copium.pics or a subdomain
    /// (the token goes with the request, so no other host is ever allowed; anything else is ignored with a warning); otherwise
    /// <see cref="UploadEndpoint"/>. A clip never uses the override.
    /// </summary>
    internal static string EndpointFor(MediaKind kind, string? imageOverride)
    {
        if (kind != MediaKind.Image || string.IsNullOrWhiteSpace(imageOverride)) return UploadEndpoint;
        if (OverrideUri(imageOverride) is { } uri) return uri.AbsoluteUri;
        Log.Warn("copium: CopiumImageUploadUrl ignored: it must be an https:// address on copium.pics");
        return UploadEndpoint;
    }

    /// <summary>The longest answer read (a JSON answer is a few hundred bytes; anything bigger is cut).</summary>
    internal const int MaxBodyBytes = 64 * 1024;
    /// <summary>How long the answer's body may take after its headers came. A test shortens it.</summary>
    internal static TimeSpan BodyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Reads at most <see cref="MaxBodyBytes"/> of the answer within <see cref="BodyTimeout"/> (a server that sends headers and then stalls must not hold the upload queue).</summary>
    internal static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(BodyTimeout);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[MaxBodyBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
            }
            return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Copium did not finish its answer within {BodyTimeout.TotalSeconds:0} s");
        }
    }

    /// <summary>
    /// A refusal of the picture itself, as opposed to this account or this one file: HTTP 415, or a 400 whose error text speaks of
    /// the type / format / mime / extension / unsupported. Only that stops the automatic uploads for the session.
    /// </summary>
    internal static bool IsTypeRefusal(int code, string? errorText) =>
        code == 415 || (code == 400 && !string.IsNullOrEmpty(errorText) && s_typeWords.IsMatch(errorText));

    // whole words only: "type" alone (a "token type" error) or "format" inside another word must not pause the uploads
    private static readonly System.Text.RegularExpressions.Regex s_typeWords = new(@"\b(file type|mime|unsupported|extension|image format)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex s_authWords = new(@"\b(token|auth\w*)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The account's token was refused: HTTP 401, or a 403 with no error text at all (a bare refusal is the usual shape of a bad
    /// key) or whose error text speaks of the token / authorization (not a bare "key": a storage or cache key is no token). Only that pauses the automatic uploads; any other 403
    /// (a blocked file, a quota: it says so) is a plain refusal of that one upload.
    /// </summary>
    internal static bool IsTokenRejection(int code, string? errorText) =>
        code == 401 || (code == 403 && (string.IsNullOrWhiteSpace(errorText) || s_authWords.IsMatch(errorText)));

    /// <summary>Uploads a clip or a picture. Returns the share info; <c>Link</c> is "the one to paste" (raw file unless the account has custom embeds on).</summary>
    /// <param name="imageEndpoint">The hidden image-host override (<see cref="Settings.CopiumImageUploadUrl"/>); used for pictures only (<see cref="EndpointFor"/>).</param>
    public static async Task<CopiumUpload> UploadAsync(string token, string path, string? title, IProgress<double>? progress, CancellationToken ct, string? imageEndpoint = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("No Copium token set. Create one under ShareX & API on copium.pics and paste it in Clink.");

        var kind = KindOf(path);
        string noun = kind == MediaKind.Image ? "Screenshot" : "Clip";
        string endpoint = EndpointFor(kind, imageEndpoint);
        if (!string.Equals(endpoint, UploadEndpoint, StringComparison.Ordinal))
            Log.Info("copium: uploading the picture to " + new Uri(endpoint).Host + " (CopiumImageUploadUrl)");   // the host only: no path, no token
        var http = Http;

        using var content = new MultipartFormDataContent();
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var stream = new ProgressStream(fs, progress);
        var fileContent = new StreamContent(stream, 1 << 16);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(path));
        content.Add(fileContent, "file", Path.GetFileName(path));
        if (!string.IsNullOrWhiteSpace(title)) content.Add(new StringContent(title), "title");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        req.Content = content;

        int code;
        string body;
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            code = (int)resp.StatusCode;
            body = await ReadBodyAsync(resp.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is IOException) && !ct.IsCancellationRequested)
        {
            // HttpClient wraps a failed read of the clip itself (an archive share that went away mid-upload) like a network error.
            if (stream.ReadError != null) throw new IOException($"Could not read the {noun.ToLowerInvariant()}: " + stream.ReadError.Message, ex);
            // Offline / DNS / refused before anything was sent; or the connection dropped while the file was going up, which is
            // also how a proxy or CDN body limit answers (it closes the connection early, so its 413 never arrives).
            throw new InvalidOperationException(ex is HttpRequestException && ex.InnerException is not IOException
                ? "Can't reach Copium - check your connection"
                : "Connection to Copium dropped during the upload (network, or the file is over your upload limit)", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Upload timed out after {http.Timeout.TotalMinutes:0} min - the connection is too slow or stalled", ex);
        }
        // Redirects are never followed (the token would go with them): a 3xx is an error.
        if (code >= 300 && code <= 399) throw new InvalidOperationException($"Copium sent an unexpected redirect (HTTP {code})");
        if (code < 200 || code > 299) throw new CopiumHttpException(code, ErrorMessage(code, body, noun), TryError(body));
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            // A captive portal or a proxy page instead of Copium's JSON.
            Log.Warn($"copium: HTTP {code} answer is not JSON: {Cap(body)}");
            throw new InvalidOperationException($"Copium sent an unexpected answer (HTTP {code})", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Copium sent an unexpected answer (HTTP {code})");
            string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long size = root.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long n) ? n : 0;
            string link = Get("link");
            if (string.IsNullOrEmpty(link)) link = Get("url");
            if (string.IsNullOrEmpty(link))
            {
                Log.Warn($"copium: HTTP {code} answer without a link: {Cap(body)}");
                throw new InvalidOperationException($"Copium sent an unexpected answer (HTTP {code}, no link)");
            }
            // The link is only copied and opened by the shell, so any absolute https address will do (a custom embed or CDN host);
            // only the destination of the token is pinned (EndpointFor). Not file:, not http:, not relative.
            if (!IsHttpsUrl(link, out _))
            {
                Log.Warn($"copium: HTTP {code} answer with a link that is not an https address: {Cap(link)}");
                throw new InvalidOperationException($"Copium sent an unexpected answer (HTTP {code}, bad link)");
            }
            string Safe(string value) => IsHttpsUrl(value, out _) ? value : "";
            return new CopiumUpload(Get("id"), link, Safe(Get("url")), Safe(Get("pageUrl")), Safe(Get("thumbUrl")) is { Length: > 0 } th ? th : null, size);
        }
    }

    /// <summary>One sentence for a failed upload: what the status means, with Copium's own error text (or the status code) in brackets.</summary>
    /// <param name="noun">"Clip" (the default) or "Screenshot": the word for what was uploaded. For a screenshot 400 / 415 means the picture type is not accepted.</param>
    internal static string ErrorMessage(int code, string body, string noun = "Clip")
    {
        string? text = TryError(body);
        string msg = text ?? $"HTTP {code}";
        if (noun != "Clip" && IsTypeRefusal(code, text)) return "Copium did not accept the image (" + msg + ")";
        return code switch
        {
            _ when IsTokenRejection(code, text) => "Copium rejected the token (" + msg + ")",
            413 => noun + " is larger than your Copium upload limit (" + msg + ")",
            429 => "Too many uploads, wait a bit (" + msg + ")",
            >= 500 => "Copium is having trouble (" + msg + "), try again later",
            _ => "Copium refused the upload (" + msg + ")",
        };
    }

    /// <summary>
    /// Copium's own error text from a JSON body (<c>{"error": "…"}</c>, <c>{"message": "…"}</c> or <c>{"error": {"message": "…"}}</c>),
    /// capped; null for an empty body or one that is not JSON (a proxy's or CDN's HTML error page), so the status code speaks instead.
    /// </summary>
    internal static string? TryError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
            foreach (string name in new[] { "error", "message" })
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is string s && s.Trim().Length > 0)
                    return Cap(s.Trim());
        }
        catch (JsonException) { }
        return null;
    }

    private static string Cap(string s) => s.Length > 120 ? s[..119] + "…" : s;

    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner; private readonly IProgress<double>? _progress; private long _read;
        public ProgressStream(Stream inner, IProgress<double>? progress) { _inner = inner; _progress = progress; }
        /// <summary>The clip file itself failed to read (set before the error goes up into HttpClient, which reports it as a network error).</summary>
        public IOException? ReadError { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n;
            try { n = _inner.Read(buffer, offset, count); } catch (IOException ex) { ReadError = ex; throw; }
            Report(n); return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n;
            try { n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false); } catch (IOException ex) { ReadError = ex; throw; }
            Report(n); return n;
        }
        private void Report(int n) { _read += n; if (_inner.Length > 0) _progress?.Report(Math.Min(1.0, _read / (double)_inner.Length)); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
