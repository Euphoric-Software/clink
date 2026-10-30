using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clink.Services;

/// <summary>A started "Sign in with Copium": the code to show, the secret poll key, and where the user approves it.</summary>
internal sealed record ConnectStart(string Code, string PollKey, Uri ApproveUrl, TimeSpan ExpiresIn, TimeSpan Interval)
{
    // The generated ToString would print the secret poll key: leave it out.
    private bool PrintMembers(StringBuilder sb)
    {
        sb.Append("Code = ").Append(Code).Append(", ApproveUrl = ").Append(ApproveUrl).Append(", ExpiresIn = ").Append(ExpiresIn).Append(", Interval = ").Append(Interval);
        return true;
    }
}

/// <summary>An approved connection: the upload token and the username. ToString never shows the token.</summary>
internal sealed class ConnectResult
{
    public string Token { get; }
    public string Username { get; }
    public ConnectResult(string token, string username) { Token = token; Username = username; }
    public override string ToString() => $"ConnectResult {{ Token = <redacted>, Username = {Username} }}";
}

/// <summary>What /api/me says about the signed-in account.</summary>
internal sealed record CopiumAccount(string Username, bool Plus, long MaxVideoBytes, int MaxVideoSeconds, bool RulesAccepted);

internal enum ConnectFailure
{
    /// <summary>This Copium does not know the app "clink" yet (an older server).</summary>
    Unsupported,
    RateLimited,
    /// <summary>The code ran out (or the server forgot it) before it was approved.</summary>
    Expired,
    /// <summary>The user said no in the browser (or the token was refused).</summary>
    Denied,
    /// <summary>Offline, DNS, timeout, a server error.</summary>
    Network,
    /// <summary>An answer that is not what Copium sends.</summary>
    Unexpected,
    Cancelled,
}

/// <summary>A connect step that did not work. The message never holds a token or poll key.</summary>
internal sealed class ConnectException : Exception
{
    public ConnectFailure Failure { get; }
    public ConnectException(ConnectFailure failure, string? message = null, Exception? inner = null)
        : base(message ?? failure.ToString(), inner) { Failure = failure; }
}

/// <summary>
/// "Sign in with Copium" (server/app-connect.js): start a connection, wait for the user to approve it in the browser, read the
/// account (/api/me) and, when the server can, revoke the token again. No WPF. Only https://copium.pics is contacted here (uploads
/// stay on <see cref="CopiumClient.UploadEndpoint"/>); the HTTP client is <see cref="CopiumClient.Http"/> (no redirects).
/// The token, the poll key and the Authorization header are never logged: only outcome categories.
/// </summary>
internal static class CopiumConnect
{
    public const string BaseUrl = "https://copium.pics";
    private const string AppId = "clink";
    /// <summary>Copium's token names are cut at 40 characters (createAppToken).</summary>
    internal const int MaxTokenNameLength = 40;
    /// <summary>Copium cleans the device to 30 characters of word characters, space, dot, apostrophe and hyphen.</summary>
    private const int MaxDeviceLength = 30;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2), MaxInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15), MaxWait = TimeSpan.FromMinutes(15), MaxRetryAfter = TimeSpan.FromSeconds(60);

    // server/app-connect.js CODE_CHARS: no I, O, 0 or 1; the code is XXXX-XXXX.
    private static readonly Regex s_code = new("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", RegexOptions.CultureInvariant);
    private static readonly Regex s_token = new("^eup_[A-Za-z0-9_-]{20,200}$", RegexOptions.CultureInvariant);

    /// <summary>Test seam: how the poll loop waits (the default is <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

    // ------------------------------------------------------------------ names

    /// <summary>"Clink-&lt;PC name&gt;", plus "-dev" for a --dev instance.</summary>
    internal static string DefaultTokenName() => DefaultTokenName(Environment.MachineName, AppController.IsDev);

    internal static string DefaultTokenName(string? machine, bool dev)
    {
        string suffix = dev ? "-dev" : "";
        string name = Clean(machine ?? "", MaxTokenNameLength - "Clink-".Length - suffix.Length);
        return "Clink-" + (name.Length == 0 ? "PC" : name) + suffix;
    }

    /// <summary>The user's token name made safe: trimmed, no control or format characters, one space between words, at most 40 characters; the default when nothing is left.</summary>
    internal static string SanitizeTokenName(string? name)
    {
        string clean = Clean(name ?? "", MaxTokenNameLength);
        return clean.Length == 0 ? DefaultTokenName() : clean;
    }

    /// <summary>Drops control / format / unassigned characters, collapses whitespace to single spaces, trims, cuts at <paramref name="max"/> characters without splitting a surrogate pair or a combined letter.</summary>
    private static string Clean(string s, int max)
    {
        var sb = new StringBuilder();
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            var cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        string text = sb.ToString();
        if (text.Length <= max) return text;
        var cut = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string el = (string)e.Current;
            if (cut.Length + el.Length > max) break;
            cut.Append(el);
        }
        return cut.ToString().TrimEnd();
    }

    /// <summary>The device as Copium would clean it anyway (word characters, space . ' -), at most 30 characters.</summary>
    private static string DeviceName(string? device)
    {
        var sb = new StringBuilder();
        foreach (char c in device ?? "") sb.Append(char.IsLetterOrDigit(c) || c is '_' or ' ' or '.' or '\'' or '-' ? c : ' ');
        return Clean(sb.ToString(), MaxDeviceLength);
    }

    private static string AppVersion()
    {
        var v = typeof(CopiumConnect).Assembly.GetName().Version;
        return v == null ? "0" : v.ToString(3);
    }

    // ------------------------------------------------------------------ start

    /// <summary>POST /api/connect/start: the code to show and where to approve it.</summary>
    public static async Task<ConnectStart> StartAsync(string device, string tokenName, CancellationToken ct)
    {
        var body = new { app = AppId, device = DeviceName(device), name = SanitizeTokenName(tokenName) };
        var (code, text, _) = await SendAsync(HttpMethod.Post, "/api/connect/start", body, null, ct).ConfigureAwait(false);
        if (code == 400 && (CopiumClient.TryError(text) ?? "").Contains("unknown app", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("copium: connect start: this Copium does not know the app yet");
            throw new ConnectException(ConnectFailure.Unsupported, "This version of Copium cannot connect Clink yet");
        }
        if (code == 429) throw Fail(ConnectFailure.RateLimited, "start", "Too many tries, wait a few minutes");
        if (code >= 500) throw Fail(ConnectFailure.Network, "start", "Copium is having trouble, try again later");
        if (code != 201) throw Fail(ConnectFailure.Unexpected, "start", $"Copium sent an unexpected answer (HTTP {code})");

        using var doc = ParseObject(text, "start");
        var root = doc.RootElement;
        string? c = Str(root, "code")?.Trim().ToUpperInvariant();
        string? pollKey = Str(root, "pollKey");
        if (c == null || !s_code.IsMatch(c) || string.IsNullOrEmpty(pollKey) || pollKey.Length > 200)
            throw Fail(ConnectFailure.Unexpected, "start", "Copium sent an unexpected answer (code)");

        // The server's approveUrl is ignored: the page is always built here from the validated code.
        Uri approve = new($"{BaseUrl}/connect?code={c}");
        double expires = Num(root, "expiresIn") ?? 600, interval = Num(root, "interval") ?? 2;
        return new ConnectStart(c, pollKey, approve,
            TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(expires) ? expires : 600, 1, MaxWait.TotalSeconds)),
            ClampInterval(interval));
    }



    private static TimeSpan ClampInterval(double seconds) =>
        double.IsFinite(seconds) ? TimeSpan.FromSeconds(Math.Clamp(seconds, MinInterval.TotalSeconds, MaxInterval.TotalSeconds)) : MinInterval;

    // ------------------------------------------------------------------ wait

    /// <summary>Polls until the user approves in the browser. Returns the upload token and the username.</summary>
    public static async Task<ConnectResult> WaitAsync(ConnectStart start, CancellationToken ct)
    {
        TimeSpan interval = ClampInterval(start.Interval.TotalSeconds);
        TimeSpan deadline = start.ExpiresIn < MaxWait ? start.ExpiresIn : MaxWait;
        long t0 = Stopwatch.GetTimestamp();
        TimeSpan waited = TimeSpan.Zero, backoff = TimeSpan.Zero;
        // (the time spent is the larger of the clock and the delays asked for, so a test with a fake Delay still reaches the deadline)
        TimeSpan Elapsed() { var real = Stopwatch.GetElapsedTime(t0); return real > waited ? real : waited; }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Elapsed() >= deadline) throw Fail(ConnectFailure.Expired, "wait", "The code ran out - press Sign in again");
                TimeSpan pause = backoff > TimeSpan.Zero ? backoff : interval;
                TimeSpan remaining = deadline - Elapsed();
                if (pause > remaining) pause = remaining;   // the overall deadline wins over a long back-off
                await Delay(pause, ct).ConfigureAwait(false);
                waited += pause;
                ct.ThrowIfCancellationRequested();
                if (Elapsed() >= deadline) throw Fail(ConnectFailure.Expired, "wait", "The code ran out - press Sign in again");

                int code; string text; TimeSpan? retryAfter = null;
                try { (code, text, retryAfter) = await SendAsync(HttpMethod.Post, "/api/connect/poll", new { pollKey = start.PollKey }, null, ct).ConfigureAwait(false); }
                catch (ConnectException ex) when (ex.Failure == ConnectFailure.Network) { code = -1; text = ""; }

                if (code == 202) { backoff = TimeSpan.Zero; continue; }
                if (code == 200) return ReadApproval(text);
                if (code == 410) throw Fail(ConnectFailure.Expired, "wait", "That connection expired - press Sign in again");
                // Copium's own refusal is a JSON { "error": ... }; a 403 that is not (a Cloudflare / WAF challenge page) is a passing problem.
                if (code == 403 && CopiumClient.TryError(text) != null) throw Fail(ConnectFailure.Denied, "wait", "The connection was cancelled in the browser");
                if (code == -1 || code == 429 || code == 403 || code >= 500)
                {
                    TimeSpan next = backoff == TimeSpan.Zero ? interval * 2 : backoff * 2;
                    next = next < MaxBackoff ? next : MaxBackoff;
                    if (code == 429 && retryAfter is { } ra) next = ra < next ? next : ra < MaxRetryAfter ? ra : MaxRetryAfter;   // Retry-After: at least the back-off, at most 60 s
                    backoff = next;
                    continue;
                }
                throw Fail(ConnectFailure.Unexpected, "wait", $"Copium sent an unexpected answer (HTTP {code})");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ConnectException(ConnectFailure.Cancelled);
        }
    }

    private static ConnectResult ReadApproval(string text)
    {
        using var doc = ParseObject(text, "wait");
        string? token = Str(doc.RootElement, "token");
        if (token == null || !s_token.IsMatch(token)) throw Fail(ConnectFailure.Unexpected, "wait", "Copium sent an unexpected answer (token)");
        string name = Clean(Str(doc.RootElement, "username") ?? "", 100);
        Log.Info("copium: connect: approved");
        return new ConnectResult(token, name);
    }

    // ------------------------------------------------------------------ account, revoke

    /// <summary>GET /api/me with the token.</summary>
    public static async Task<CopiumAccount> GetAccountAsync(string token, CancellationToken ct)
    {
        var (code, text, _) = await SendAsync(HttpMethod.Get, "/api/me", null, token, ct).ConfigureAwait(false);
        if (code == 401 || (code == 403 && CopiumClient.TryError(text) != null)) throw Fail(ConnectFailure.Denied, "account", "Copium does not accept this token");
        if (code == 403) throw Fail(ConnectFailure.Network, "account", "Copium could not be reached properly, try again later");   // not Copium's JSON: a WAF / challenge page
        if (code == 429) throw Fail(ConnectFailure.RateLimited, "account", "Too many tries, wait a bit");
        if (code >= 500) throw Fail(ConnectFailure.Network, "account", "Copium is having trouble, try again later");
        if (code != 200) throw Fail(ConnectFailure.Unexpected, "account", $"Copium sent an unexpected answer (HTTP {code})");

        using var doc = ParseObject(text, "account");
        var root = doc.RootElement;
        string name = Clean(Str(root, "username") ?? "", 100);
        if (name.Length == 0) throw Fail(ConnectFailure.Unexpected, "account", "Copium sent an unexpected answer (no username)");
        bool plus = root.TryGetProperty("premium", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
        long bytes = 0; int seconds = 0;
        if (root.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object)
        {
            if (Num(l, "maxVideoBytes") is { } b && b >= 0 && b < long.MaxValue) bytes = (long)b;
            if (Num(l, "maxVideoSeconds") is { } s && s >= 0 && s < int.MaxValue) seconds = (int)s;
        }
        bool rules = root.TryGetProperty("rulesAccepted", out var r) && r.ValueKind == JsonValueKind.True;
        return new CopiumAccount(name, plus, bytes, seconds, rules);
    }

    /// <summary>
    /// DELETE /api/connect/token: asks Copium to drop this token. true = done (any 2xx) or already gone (401: revoked / replaced, so signed out anyway),
    /// null = this Copium cannot do that (404), false = anything else (3xx, 403, 5xx, network). Throws <see cref="ConnectException"/> (Cancelled) when cancelled.
    /// </summary>
    public static async Task<bool?> RevokeSelfAsync(string token, CancellationToken ct)
    {
        try
        {
            var (code, _, _) = await SendAsync(HttpMethod.Delete, "/api/connect/token", null, token, ct).ConfigureAwait(false);
            if (code >= 200 && code <= 299) { Log.Info("copium: connect: token revoked"); return true; }
            if (code == 401) { Log.Info("copium: connect: token was already revoked"); return true; }
            if (code == 404) { Log.Info("copium: connect: this Copium has no self-revoke"); return null; }
            Log.Warn($"copium: connect: revoke failed (HTTP {code})");
            return false;
        }
        catch (ConnectException ex) when (ex.Failure != ConnectFailure.Cancelled)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ plumbing

    private static ConnectException Fail(ConnectFailure failure, string step, string message)
    {
        Log.Warn($"copium: connect {step}: {failure}");
        return new ConnectException(failure, message);
    }

    private static JsonDocument ParseObject(string text, string step)
    {
        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        throw Fail(ConnectFailure.Unexpected, step, "Copium sent an unexpected answer");
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    /// <summary>One request to https://copium.pics with the usual headers and its own 20 s limit. A network problem is a <see cref="ConnectFailure.Network"/> exception; a redirect is never followed (Unexpected).</summary>
    private static async Task<(int Code, string Body, TimeSpan? RetryAfter)> SendAsync(HttpMethod method, string path, object? json, string? bearer, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) throw new ConnectException(ConnectFailure.Cancelled);
        using var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "fetch");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Clink", AppVersion()));
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Trim());
        if (json != null) req.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            using var resp = await CopiumClient.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code >= 300 && code <= 399) throw Fail(ConnectFailure.Unexpected, "request", $"Copium sent an unexpected redirect (HTTP {code})");
            string body = await CopiumClient.ReadBodyAsync(resp.Content, cts.Token).ConfigureAwait(false);
            TimeSpan? retryAfter = null;
            var ra = resp.Headers.RetryAfter;
            if (ra?.Delta is { } delta) retryAfter = delta;
            else if (ra?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;
            return (code, body, retryAfter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ConnectException(ConnectFailure.Cancelled);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
        {
            Log.Warn("copium: connect: network problem (" + ex.GetType().Name + ")");
            throw new ConnectException(ConnectFailure.Network, "Can't reach Copium - check your connection", ex);
        }
    }
}
