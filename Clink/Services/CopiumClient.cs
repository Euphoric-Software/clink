using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Clink.Services;

internal sealed record CopiumUpload(string Id, string Link, string Url, string PageUrl, string? ThumbUrl, long Size);

/// <summary>Upload client for copium.pics (https://copium.pics/developers).</summary>
internal static class CopiumClient
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(30) };
    public const string UploadEndpoint = "https://v.copium.pics/api/upload";

    /// <summary>Uploads a clip. Returns the share info; <c>Link</c> is "the one to paste" (raw file unless the account has custom embeds on).</summary>
    public static async Task<CopiumUpload> UploadAsync(string token, string path, string? title, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("No Copium token set. Create one under ShareX & API on copium.pics and paste it in Clink.");

        using var content = new MultipartFormDataContent();
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var stream = new ProgressStream(fs, progress);
        var fileContent = new StreamContent(stream, 1 << 16);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Add(fileContent, "file", Path.GetFileName(path));
        if (!string.IsNullOrWhiteSpace(title)) content.Add(new StringContent(title), "title");

        using var req = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        req.Content = content;

        int code;
        string body;
        try
        {
            using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            code = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is IOException) && !ct.IsCancellationRequested)
        {
            // HttpClient wraps a failed read of the clip itself (an archive share that went away mid-upload) like a network error.
            if (stream.ReadError != null) throw new IOException("Could not read the clip: " + stream.ReadError.Message, ex);
            // Offline / DNS / refused before anything was sent; or the connection dropped while the file was going up, which is
            // also how a proxy or CDN body limit answers (it closes the connection early, so its 413 never arrives).
            throw new InvalidOperationException(ex is HttpRequestException && ex.InnerException is not IOException
                ? "Can't reach Copium - check your connection"
                : "Connection to Copium dropped during the upload (network, or the file is over your upload limit)", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Upload timed out after {s_http.Timeout.TotalMinutes:0} min - the connection is too slow or stalled", ex);
        }
        if (code < 200 || code > 299) throw new InvalidOperationException(ErrorMessage(code, body));
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
            return new CopiumUpload(Get("id"), link, Get("url"), Get("pageUrl"), Get("thumbUrl"), size);
        }
    }

    /// <summary>One sentence for a failed upload: what the status means, with Copium's own error text (or the status code) in brackets.</summary>
    internal static string ErrorMessage(int code, string body)
    {
        string msg = TryError(body) ?? $"HTTP {code}";
        return code switch
        {
            401 or 403 => "Copium rejected the token (" + msg + ")",
            413 => "Clip is larger than your Copium upload limit (" + msg + ")",
            429 => "Too many uploads, wait a bit (" + msg + ")",
            >= 500 => "Copium is having trouble (" + msg + "), try again later",
            _ => "Copium refused the upload (" + msg + ")",
        };
    }

    /// <summary>
    /// Copium's own error text from a JSON body (<c>{"error": "…"}</c>, <c>{"message": "…"}</c> or <c>{"error": {"message": "…"}}</c>),
    /// capped; null for an empty body or one that is not JSON (a proxy's or CDN's HTML error page), so the status code speaks instead.
    /// </summary>
    private static string? TryError(string body)
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
