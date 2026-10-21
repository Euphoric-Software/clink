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

        using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = TryError(body) ?? $"HTTP {(int)resp.StatusCode}";
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403) msg = "Copium rejected the token (" + msg + ")";
            if ((int)resp.StatusCode == 413) msg = "Clip is larger than your Copium upload limit (" + msg + ")";
            if ((int)resp.StatusCode == 429) msg = "Too many uploads, wait a bit (" + msg + ")";
            throw new InvalidOperationException(msg);
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long size = root.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        string link = Get("link");
        if (string.IsNullOrEmpty(link)) link = Get("url");
        if (string.IsNullOrEmpty(link)) throw new InvalidOperationException("Copium answered without a link: " + body);
        return new CopiumUpload(Get("id"), link, Get("url"), Get("pageUrl"), Get("thumbUrl"), size);
    }

    private static string? TryError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString();
        }
        catch { }
        return body.Length > 200 ? body[..200] : body;
    }

    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner; private readonly IProgress<double>? _progress; private long _read;
        public ProgressStream(Stream inner, IProgress<double>? progress) { _inner = inner; _progress = progress; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { int n = _inner.Read(buffer, offset, count); Report(n); return n; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) { int n = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct); Report(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { int n = await _inner.ReadAsync(buffer, ct); Report(n); return n; }
        private void Report(int n) { _read += n; if (_inner.Length > 0) _progress?.Report(Math.Min(1.0, _read / (double)_inner.Length)); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
