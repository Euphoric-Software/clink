using System.Net;
using System.Net.Http;
using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

/// <summary>"Sign in with Copium" (slice A): the connect service through a mock HTTP handler, the token-name helpers, and the new settings keys.</summary>
internal static class CopiumConnectTests
{
    private const string Token = "eup_abcdefghijklmnopqrstuvwxyz0123456789ABCD";
    private const string PollKey = "pollkey-SECRET-0123456789abcdefghijklmnopqrstuvwxyz";
    private const string Code = "AB2C-DE9F";

    private sealed record Seen(HttpMethod Method, Uri? Uri, string? Auth, string? RequestedWith, string? UserAgent, string Body);

    private sealed class Mock : HttpMessageHandler
    {
        public Func<HttpRequestMessage, int, HttpResponseMessage> Respond = (_, _) => Reply(HttpStatusCode.OK, "{}");
        public readonly List<Seen> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            int n;
            lock (Requests)
            {
                Requests.Add(new Seen(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Requested-With", out var xs) ? string.Join(",", xs) : null,
                    request.Headers.UserAgent.ToString(), body));
                n = Requests.Count;
            }
            return Respond(request, n);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ReplyAfter(HttpStatusCode code, string body, TimeSpan? delta = null, DateTimeOffset? date = null)
    {
        var r = Reply(code, body);
        r.Headers.RetryAfter = delta != null ? new System.Net.Http.Headers.RetryConditionHeaderValue(delta.Value) : new System.Net.Http.Headers.RetryConditionHeaderValue(date!.Value);
        return r;
    }

    private const string HtmlChallenge = "<html><body>Just a moment...</body></html>";

    private static string StartJson(string code = Code, string approve = "https://copium.pics/connect?code=" + Code, string extra = "") =>
        $$"""{"code":"{{code}}","pollKey":"{{PollKey}}","approveUrl":"{{approve}}","expiresIn":600,"interval":2{{extra}}}""";

    private static string ApprovedJson(string token = Token) =>
        $$"""{"token":"{{token}}","username":"Jason","server":"https://copium.pics","uploadUrl":"https://v.copium.pics/api/upload","team":null}""";

    /// <summary>Runs <paramref name="work"/> with the mock as the shared client and a Delay that records and returns at once.</summary>
    private static (T? Result, ConnectException? Error, Mock Http, List<TimeSpan> Delays) Run<T>(Mock mock, Func<Task<T>> work, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var delays = new List<TimeSpan>();
        var oldDelay = CopiumConnect.Delay;
        CopiumClient.HttpOverride = new HttpClient(mock);
        CopiumConnect.Delay = delay ?? ((t, ct) => { lock (delays) delays.Add(t); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        try
        {
            var r = Task.Run(work).GetAwaiter().GetResult();
            return (r, null, mock, delays);
        }
        catch (ConnectException ex) { return (default, ex, mock, delays); }
        finally { CopiumClient.HttpOverride = null; CopiumConnect.Delay = oldDelay; }
    }

    private static ConnectStart Started(double expiresIn = 600, double interval = 2) =>
        new(Code, PollKey, new Uri("https://copium.pics/connect?code=" + Code), TimeSpan.FromSeconds(expiresIn), TimeSpan.FromSeconds(interval));

    private static Mock Replies(params (HttpStatusCode Code, string Body)[] steps) => new()
    {
        Respond = (_, n) => { var s = steps[Math.Min(n, steps.Length) - 1]; return Reply(s.Code, s.Body); },
    };

    // ------------------------------------------------------------------ start

    [UnitTest]
    public static void Start_sends_the_app_device_and_name_and_reads_the_answer()
    {
        var (start, error, http, _) = Run(Replies((HttpStatusCode.Created, StartJson())), () => CopiumConnect.StartAsync("MY-PC", "Gaming rig", CancellationToken.None));
        Check.Null(error);
        Check.Equal(Code, start!.Code);
        Check.Equal(PollKey, start.PollKey);
        Check.Equal("https://copium.pics/connect?code=" + Code, start.ApproveUrl.AbsoluteUri);
        Check.Equal(TimeSpan.FromSeconds(600), start.ExpiresIn);
        Check.Equal(TimeSpan.FromSeconds(2), start.Interval);
        var req = http.Requests.Single();
        Check.Equal("https://copium.pics/api/connect/start", req.Uri!.AbsoluteUri);
        Check.Equal(HttpMethod.Post, req.Method);
        using var body = JsonDocument.Parse(req.Body);
        Check.Equal("clink", body.RootElement.GetProperty("app").GetString());
        Check.Equal("MY-PC", body.RootElement.GetProperty("device").GetString());
        Check.Equal("Gaming rig", body.RootElement.GetProperty("name").GetString());
    }

    [UnitTest]
    public static void Start_failures_are_told_apart()
    {
        var (_, unknown, _, _) = Run(Replies((HttpStatusCode.BadRequest, """{"error":"Unknown app"}""")), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.Unsupported, unknown!.Failure, "an older Copium");
        var (_, limited, _, _) = Run(Replies(((HttpStatusCode)429, """{"error":"Too many tries"}""")), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.RateLimited, limited!.Failure);
        var (_, down, _, _) = Run(Replies((HttpStatusCode.BadGateway, "<html>")), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.Network, down!.Failure);
        var (_, other400, _, _) = Run(Replies((HttpStatusCode.BadRequest, """{"error":"Something else"}""")), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, other400!.Failure);
        var (_, junk, _, _) = Run(Replies((HttpStatusCode.Created, "not json")), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, junk!.Failure);
        var offline = new Mock { Respond = (_, _) => throw new HttpRequestException("no route") };
        var (_, net, _, _) = Run(offline, () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.Network, net!.Failure);
    }

    [UnitTest]
    public static void A_redirect_is_never_followed()
    {
        var mock = new Mock
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://evil.example/steal");
                return r;
            },
        };
        var (_, error, http, _) = Run(mock, () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, error!.Failure);
        Check.Equal(1, http.Requests.Count, "only copium.pics was asked");
        var poll = new Mock { Respond = mock.Respond };
        var (_, error2, http2, _) = Run(poll, () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, error2!.Failure);
        Check.Equal(1, http2.Requests.Count);
        var (_, error3, http3, _) = Run(new Mock { Respond = mock.Respond }, () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, error3!.Failure);
        Check.Equal(1, http3.Requests.Count, "the account read does not follow a redirect");
        var (revoked, error4, http4, _) = Run(new Mock { Respond = mock.Respond }, () => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None));
        Check.Null(error4);
        Check.Equal(false, revoked);
        Check.Equal(1, http4.Requests.Count, "the revoke does not follow a redirect");
        Check.False(CopiumClient.FollowsRedirects, "the real shared client never follows a redirect");
    }

    [UnitTest]
    public static void The_approve_address_is_always_built_locally()
    {
        string own = "https://copium.pics/connect?code=" + Code;
        foreach (var bad in new[] { "https://evil.example/connect?code=" + Code, "http://copium.pics/connect?code=" + Code, "https://copium.pics.evil.example/connect",
                                    "https://copium.pics/login?next=x", "https://copium.pics@evil.example/connect", "https://v.copium.pics/connect?code=" + Code,
                                    "https://copium.pics:8443/connect", "javascript:alert(1)", "/connect?code=" + Code, "", "https://copium.pics/connect/../x" })
        {
            var (start, error, _, _) = Run(Replies((HttpStatusCode.Created, StartJson(approve: bad))), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
            Check.Null(error, bad);
            Check.Equal(own, start!.ApproveUrl.AbsoluteUri, $"'{bad}' is rebuilt");
        }
        foreach (var odd in new[] { "https://copium.pics/connect?code=" + Code + "&x=1", "https://copium.pics/connect?code=ZZZZ-ZZZZ", "https://copium.pics/connect?code=" + Code + "&next=https://evil.example" })
        {
            var (good, _, _, _) = Run(Replies((HttpStatusCode.Created, StartJson(approve: odd))), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
            Check.Equal(own, good!.ApproveUrl.AbsoluteUri, $"the server's address '{odd}' is ignored: always built here from the validated code");
        }
    }

    [UnitTest]
    public static void A_malformed_code_is_refused()
    {
        foreach (var bad in new[] { "", "ABCD1234", "AAAA-BBBO", "AAAA-BBB1", "ABCD-EFG", "ABCD-EFGHI", "AB D-EFGH", "ABCD-EF\\nH" })
        {
            var (_, error, _, _) = Run(Replies((HttpStatusCode.Created, StartJson(code: bad))), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
            Check.Equal(ConnectFailure.Unexpected, error?.Failure, $"'{bad}'");
        }
        var (_, noKey, _, _) = Run(Replies((HttpStatusCode.Created, $$"""{"code":"{{Code}}","approveUrl":"x"}""")), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, noKey!.Failure, "no poll key");
    }

    [UnitTest]
    public static void The_server_interval_and_lifetime_are_bounded()
    {
        foreach (var (given, want) in new[] { (0.0, 2.0), (1.0, 2.0), (5.0, 5.0), (60.0, 10.0), (-3.0, 2.0) })
        {
            var (s, _, _, _) = Run(Replies((HttpStatusCode.Created, StartJson(extra: "").Replace("\"interval\":2", $"\"interval\":{given.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
            Check.Equal(TimeSpan.FromSeconds(want), s!.Interval, $"interval {given}");
        }
        var (late, _, _, _) = Run(Replies((HttpStatusCode.Created, StartJson().Replace("\"expiresIn\":600", "\"expiresIn\":999999"))), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
        Check.True(late!.ExpiresIn <= TimeSpan.FromMinutes(15), "never longer than 15 minutes");
    }

    // ------------------------------------------------------------------ wait

    [UnitTest]
    public static void Waiting_polls_until_the_user_approves()
    {
        var mock = Replies((HttpStatusCode.Accepted, """{"status":"pending"}"""), (HttpStatusCode.Accepted, """{"status":"pending"}"""), (HttpStatusCode.OK, ApprovedJson()));
        var (r, error, http, delays) = Run(mock, () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(error);
        Check.Equal(Token, r!.Token);
        Check.Equal("Jason", r!.Username);
        Check.Equal(3, http.Requests.Count);
        Check.Equal(new[] { 2.0, 2.0, 2.0 }, delays.Select(d => d.TotalSeconds).ToArray(), "at the server's interval");
        using var body = JsonDocument.Parse(http.Requests[0].Body);
        Check.Equal(PollKey, body.RootElement.GetProperty("pollKey").GetString());
        Check.Equal("https://copium.pics/api/connect/poll", http.Requests[0].Uri!.AbsoluteUri);
    }

    [UnitTest]
    public static void Waiting_ends_on_expired_or_denied()
    {
        var (_, expired, _, _) = Run(Replies((HttpStatusCode.Gone, """{"error":"expired"}""")), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Equal(ConnectFailure.Expired, expired!.Failure);
        var (_, denied, _, _) = Run(Replies((HttpStatusCode.Forbidden, """{"error":"cancelled"}""")), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Equal(ConnectFailure.Denied, denied!.Failure);
        var (_, odd, http, _) = Run(Replies((HttpStatusCode.NotFound, "{}")), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, odd!.Failure);
        Check.Equal(1, http.Requests.Count, "no retry on an answer that is not understood");
    }

    [UnitTest]
    public static void A_403_that_is_not_copiums_json_is_a_passing_problem_not_a_denial()
    {
        var challenge = new Mock { Respond = (_, n) => n == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(HtmlChallenge, System.Text.Encoding.UTF8, "text/html") } : Reply(HttpStatusCode.OK, ApprovedJson()) };
        var (r, error, http, delays) = Run(challenge, () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(error);
        Check.Equal(Token, r!.Token);
        Check.Equal(2, http.Requests.Count);
        Check.Equal(new[] { 2.0, 4.0 }, delays.Select(d => d.TotalSeconds).ToArray(), "backs off like a server error");
        var (_, empty, _, _) = Run(Replies((HttpStatusCode.Forbidden, ""), (HttpStatusCode.OK, ApprovedJson())), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(empty, "a bare 403 is not a denial either");

        var (_, html, _, _) = Run(Replies((HttpStatusCode.Forbidden, HtmlChallenge)), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(ConnectFailure.Network, html!.Failure, "a challenge page on /api/me");
        var (_, json, _, _) = Run(Replies((HttpStatusCode.Forbidden, """{"error":"Sign in on the website to do this"}""")), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(ConnectFailure.Denied, json!.Failure, "Copium's own JSON refusal");
        var (_, none, _, _) = Run(Replies((HttpStatusCode.Unauthorized, HtmlChallenge)), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(ConnectFailure.Denied, none!.Failure, "401 is always a denial");
    }

    [UnitTest]
    public static void Retry_After_on_a_429_is_honoured_within_bounds()
    {
        Func<HttpResponseMessage, Mock> once = first => new Mock { Respond = (_, n) => n == 1 ? first : Reply(HttpStatusCode.OK, ApprovedJson()) };
        var (_, e1, _, d1) = Run(once(ReplyAfter((HttpStatusCode)429, "{}", delta: TimeSpan.FromSeconds(30))), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(e1);
        Check.Equal(new[] { 2.0, 30.0 }, d1.Select(d => d.TotalSeconds).ToArray(), "waits as long as the server asks");
        var (_, e2, _, d2) = Run(once(ReplyAfter((HttpStatusCode)429, "{}", delta: TimeSpan.FromSeconds(1))), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(e2);
        Check.Equal(new[] { 2.0, 4.0 }, d2.Select(d => d.TotalSeconds).ToArray(), "never less than the back-off");
        var (_, e3, _, d3) = Run(once(ReplyAfter((HttpStatusCode)429, "{}", delta: TimeSpan.FromHours(5))), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(e3);
        Check.Equal(new[] { 2.0, 60.0 }, d3.Select(d => d.TotalSeconds).ToArray(), "never more than 60 s");
        var (_, e4, _, d4) = Run(once(ReplyAfter((HttpStatusCode)429, "{}", date: DateTimeOffset.UtcNow.AddSeconds(25))), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(e4);
        Check.True(d4.Count == 2 && d4[1].TotalSeconds > 20 && d4[1].TotalSeconds <= 26, "an HTTP-date works too");
        var (_, e5, _, d5) = Run(once(ReplyAfter((HttpStatusCode)429, "{}", date: DateTimeOffset.UtcNow.AddMinutes(-5))), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(e5);
        Check.Equal(new[] { 2.0, 4.0 }, d5.Select(d => d.TotalSeconds).ToArray(), "a date in the past is the plain back-off");
        // the overall deadline still wins
        var (_, e6, http6, d6) = Run(new Mock { Respond = (_, _) => ReplyAfter((HttpStatusCode)429, "{}", delta: TimeSpan.FromSeconds(60)) }, () => CopiumConnect.WaitAsync(Started(expiresIn: 10), CancellationToken.None));
        Check.Equal(ConnectFailure.Expired, e6!.Failure);
        Check.True(d6.Sum(d => d.TotalSeconds) <= 10.0, "the pauses never run past the deadline");
    }

    [UnitTest]
    public static void A_network_blip_or_server_trouble_backs_off_and_goes_on()
    {
        var offline = new Mock();
        offline.Respond = (_, n) => n switch
        {
            1 => throw new HttpRequestException("blip"),
            2 => Reply(HttpStatusCode.InternalServerError, "oops"),
            3 => Reply((HttpStatusCode)429, "{}"),
            4 => Reply(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => Reply(HttpStatusCode.OK, ApprovedJson()),
        };
        var (r, error, http, delays) = Run(offline, () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(error);
        Check.Equal(Token, r!.Token);
        Check.Equal(5, http.Requests.Count);
        Check.Equal(new[] { 2.0, 4.0, 8.0, 15.0, 15.0 }, delays.Select(d => d.TotalSeconds).ToArray(), "doubling, capped at 15 s");

        // a good answer ends the back-off
        var mixed = new Mock();
        mixed.Respond = (_, n) => n switch
        {
            1 => Reply(HttpStatusCode.BadGateway, ""),
            2 => Reply(HttpStatusCode.Accepted, "{}"),
            3 => Reply(HttpStatusCode.Accepted, "{}"),
            _ => Reply(HttpStatusCode.OK, ApprovedJson()),
        };
        var (_, e2, _, d2) = Run(mixed, () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Null(e2);
        Check.Equal(new[] { 2.0, 4.0, 2.0, 2.0 }, d2.Select(d => d.TotalSeconds).ToArray());
    }

    [UnitTest]
    public static void Cancelling_stops_the_requests()
    {
        using var cts = new CancellationTokenSource();
        var mock = Replies((HttpStatusCode.Accepted, "{}"));
        var (_, error, http, _) = Run(mock, () => CopiumConnect.WaitAsync(Started(), cts.Token), (t, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Check.Equal(ConnectFailure.Cancelled, error!.Failure);
        Check.Equal(0, http.Requests.Count, "cancelled while waiting: nothing was sent");

        using var already = new CancellationTokenSource();
        already.Cancel();
        var (_, e2, h2, _) = Run(Replies((HttpStatusCode.Accepted, "{}")), () => CopiumConnect.WaitAsync(Started(), already.Token));
        Check.Equal(ConnectFailure.Cancelled, e2!.Failure);
        Check.Equal(0, h2.Requests.Count);
        var (_, e3, h3, _) = Run(Replies((HttpStatusCode.Created, StartJson())), () => CopiumConnect.StartAsync("PC", "n", already.Token));
        Check.Equal(ConnectFailure.Cancelled, e3!.Failure);
        Check.Equal(0, h3.Requests.Count);

        // cancelled during a request: no further request follows
        using var mid = new CancellationTokenSource();
        var during = new Mock { Respond = (_, _) => { mid.Cancel(); throw new OperationCanceledException(mid.Token); } };
        var (_, e4, h4, _) = Run(during, () => CopiumConnect.WaitAsync(Started(), mid.Token));
        Check.Equal(ConnectFailure.Cancelled, e4!.Failure);
        Check.Equal(1, h4.Requests.Count);
    }

    [UnitTest]
    public static void The_deadline_is_the_shorter_of_the_code_lifetime_and_fifteen_minutes()
    {
        var (_, error, http, delays) = Run(Replies((HttpStatusCode.Accepted, "{}")), () => CopiumConnect.WaitAsync(Started(expiresIn: 10), CancellationToken.None));
        Check.Equal(ConnectFailure.Expired, error!.Failure);
        Check.Equal(5, delays.Count);
        Check.Equal(4, http.Requests.Count, "no request once the time is up");

        var (_, e2, _, d2) = Run(Replies((HttpStatusCode.Accepted, "{}")), () => CopiumConnect.WaitAsync(Started(expiresIn: 7200, interval: 10), CancellationToken.None));
        Check.Equal(ConnectFailure.Expired, e2!.Failure);
        Check.Equal(900.0, d2.Sum(d => d.TotalSeconds), "capped at 15 minutes");
    }

    [UnitTest]
    public static void A_token_at_the_length_limits_is_accepted()
    {
        foreach (var ok in new[] { "eup_" + new string('a', 20), "eup_" + new string('b', 200) })
        {
            var (r, error, _, _) = Run(Replies((HttpStatusCode.OK, ApprovedJson(ok))), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
            Check.Null(error);
            Check.Equal(ok, r!.Token);
        }
    }

    [UnitTest]
    public static void A_token_of_the_wrong_shape_is_refused()
    {
        foreach (var bad in new[] { "", "eup_short", "abc_abcdefghijklmnopqrstuvwxyz0123456789", "eup_abcdefghijklmnopqrstuvwxyz 0123456789", "eup_abcdefghijklmnopqrstuvwxyz!!!!!!", "eup_" + new string('a', 201) })
        {
            var (_, error, _, _) = Run(Replies((HttpStatusCode.OK, ApprovedJson(bad))), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
            Check.Equal(ConnectFailure.Unexpected, error?.Failure, $"'{bad}'");
        }
        var (_, none, _, _) = Run(Replies((HttpStatusCode.OK, """{"username":"x"}""")), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, none!.Failure, "no token at all");
    }

    // ------------------------------------------------------------------ account, revoke, headers, logs

    [UnitTest]
    public static void The_account_is_read_from_me()
    {
        const string full = """{"username":"Jason","premium":{"active":true,"source":"stripe"},"limits":{"maxFileBytes":1,"maxVideoBytes":2147483648,"maxVideoSeconds":3600},"rulesAccepted":true}""";
        var (a, e, http, _) = Run(Replies((HttpStatusCode.OK, full)), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Null(e);
        Check.Equal(new CopiumAccount("Jason", true, 2147483648L, 3600, true), a);
        Check.Equal(HttpMethod.Get, http.Requests.Single().Method);
        Check.Equal("https://copium.pics/api/me", http.Requests.Single().Uri!.AbsoluteUri);

        var (b, _, _, _) = Run(Replies((HttpStatusCode.OK, """{"username":"Free","premium":{"active":false},"limits":{"maxVideoBytes":209715200,"maxVideoSeconds":600},"rulesAccepted":false}""")), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(new CopiumAccount("Free", false, 209715200L, 600, false), b);

        var (c, _, _, _) = Run(Replies((HttpStatusCode.OK, """{"username":"Bare"}""")), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(new CopiumAccount("Bare", false, 0, 0, false), c, "missing parts are the cautious values (not Plus, rules not accepted)");

        var (_, noName, _, _) = Run(Replies((HttpStatusCode.OK, """{"premium":{"active":true}}""")), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(ConnectFailure.Unexpected, noName!.Failure);
        var (_, rejected, _, _) = Run(Replies((HttpStatusCode.Unauthorized, "{}")), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        Check.Equal(ConnectFailure.Denied, rejected!.Failure);
    }

    [UnitTest]
    public static void Revoking_says_done_unsupported_or_failed()
    {
        var (done, _, http, _) = Run(Replies((HttpStatusCode.NoContent, "")), () => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None));
        Check.Equal(true, done);
        var req = http.Requests.Single();
        Check.Equal(HttpMethod.Delete, req.Method);
        Check.Equal("https://copium.pics/api/connect/token", req.Uri!.AbsoluteUri);
        Check.Equal("Bearer " + Token, req.Auth);
        var (none, _, _, _) = Run(Replies((HttpStatusCode.NotFound, "{}")), () => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None));
        Check.Null(none, "a server without self-revoke");
        var (failed, _, _, _) = Run(Replies((HttpStatusCode.InternalServerError, "{}")), () => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None));
        Check.Equal(false, failed);
        foreach (var (code, want) in new (HttpStatusCode, bool?)[] { (HttpStatusCode.OK, true), (HttpStatusCode.NoContent, true), (HttpStatusCode.Unauthorized, true), (HttpStatusCode.NotFound, null),
                                                                     (HttpStatusCode.InternalServerError, false), (HttpStatusCode.Found, false), (HttpStatusCode.Forbidden, false), (HttpStatusCode.BadGateway, false) })
        {
            var (got, err, _, _) = Run(Replies((code, code == HttpStatusCode.Forbidden ? HtmlChallenge : "{}")), () => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None));
            Check.Null(err);
            Check.Equal(want, got, code.ToString());
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var (_, cancelErr, _, _) = Run(Replies((HttpStatusCode.NoContent, "")), () => CopiumConnect.RevokeSelfAsync(Token, cancelled.Token));
        Check.Equal(ConnectFailure.Cancelled, cancelErr!.Failure, "cancelling is thrown, not answered");
        var down = new Mock { Respond = (_, _) => throw new HttpRequestException("offline") };
        var (offline, e, _, _) = Run(down, () => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None));
        Check.Null(e);
        Check.Equal(false, offline);
    }

    [UnitTest]
    public static void Only_copium_pics_is_contacted_with_the_marker_headers_and_the_user_agent()
    {
        var mock = new Mock
        {
            Respond = (r, _) => r.RequestUri!.AbsolutePath switch
            {
                "/api/connect/start" => Reply(HttpStatusCode.Created, StartJson()),
                "/api/connect/poll" => Reply(HttpStatusCode.OK, ApprovedJson()),
                "/api/me" => Reply(HttpStatusCode.OK, """{"username":"Jason"}"""),
                _ => Reply(HttpStatusCode.NoContent, ""),
            },
        };
        var (_, error, http, _) = Run(mock, async () =>
        {
            var s = await CopiumConnect.StartAsync("PC", "n", CancellationToken.None);
            await CopiumConnect.WaitAsync(s, CancellationToken.None);
            await CopiumConnect.GetAccountAsync(Token, CancellationToken.None);
            await CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None);
            return 0;
        });
        Check.Null(error);
        Check.Equal(4, http.Requests.Count);
        foreach (var r in http.Requests)
        {
            Check.Equal("https", r.Uri!.Scheme);
            Check.Equal("copium.pics", r.Uri.Host);
            Check.Equal("fetch", r.RequestedWith, r.Uri.AbsolutePath);
            Check.True(r.UserAgent!.StartsWith("Clink/", StringComparison.Ordinal), r.Uri.AbsolutePath + ": " + r.UserAgent);
        }
        Check.Null(http.Requests[0].Auth, "start and poll carry no token");
        Check.Null(http.Requests[1].Auth);
        Check.Equal("Bearer " + Token, http.Requests[2].Auth);
    }

    [UnitTest]
    public static void The_token_and_poll_key_are_never_logged()
    {
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        Log.Line += Collect;
        try
        {
            var bad = new Mock { Respond = (_, n) => n == 1 ? Reply(HttpStatusCode.OK, ApprovedJson("eup_short")) : Reply(HttpStatusCode.OK, "{}") };
            Run(bad, () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
            Run(Replies((HttpStatusCode.Created, StartJson())), () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None));
            Run(Replies((HttpStatusCode.OK, ApprovedJson())), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
            Run(Replies((HttpStatusCode.Unauthorized, $$"""{"error":"bad {{Token}}"}""")), () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
            Run(Replies((HttpStatusCode.InternalServerError, "{}")), () => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None));
            Run(new Mock { Respond = (_, _) => throw new HttpRequestException(Token + PollKey) }, () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None));
        }
        finally { Log.Line -= Collect; }
        Check.True(lines.Count > 0, "something was logged");
        foreach (var l in lines)
        {
            Check.False(l.Contains(Token) || l.Contains("eup_") || l.Contains(PollKey) || l.Contains("Bearer"), "a log line held a secret");
        }
    }

    // ------------------------------------------------------------------ names

    [UnitTest]
    public static void Token_names_are_cleaned_and_default_to_the_pc_name()
    {
        Check.Equal("Clink-MY-PC", CopiumConnect.DefaultTokenName("MY-PC", false));
        Check.Equal("Clink-MY-PC-dev", CopiumConnect.DefaultTokenName("MY-PC", true));
        Check.Equal("Clink-PC", CopiumConnect.DefaultTokenName("", false), "no machine name");
        Check.Equal("Clink-PC-dev", CopiumConnect.DefaultTokenName("  \u0007 ", true));
        var longDev = CopiumConnect.DefaultTokenName(new string('X', 80), true);
        Check.Equal(40, longDev.Length);
        Check.True(longDev.EndsWith("-dev", StringComparison.Ordinal), "the -dev marker survives a long PC name");
        string current = CopiumConnect.DefaultTokenName();
        Check.True(current.StartsWith("Clink-", StringComparison.Ordinal) && current.Length <= CopiumConnect.MaxTokenNameLength, "the default for this PC is a Clink- name within the limit");

        Check.Equal("Gaming rig", CopiumConnect.SanitizeTokenName("  Gaming rig  "));
        Check.Equal("a b c", CopiumConnect.SanitizeTokenName("a \t\r\n  b  c"), "whitespace collapses");
        Check.Equal("ab", CopiumConnect.SanitizeTokenName("a\u0000\u0007b"), "control characters are dropped");
        Check.Equal("ab", CopiumConnect.SanitizeTokenName("a\u200Bb\u202E"), "format characters (zero width, bidi) are dropped");
        Check.Equal("Büro-PC é中", CopiumConnect.SanitizeTokenName("Büro-PC é中"), "ordinary Unicode stays");
        Check.Equal(CopiumConnect.DefaultTokenName(), CopiumConnect.SanitizeTokenName(null));
        Check.Equal(CopiumConnect.DefaultTokenName(), CopiumConnect.SanitizeTokenName(""));
        Check.Equal(CopiumConnect.DefaultTokenName(), CopiumConnect.SanitizeTokenName(" \t \u200B\u0001 "), "nothing left: the default");
        Check.Equal(new string('n', 40), CopiumConnect.SanitizeTokenName(new string('n', 100)), "cut at 40");
        // a pair of surrogates is never split by the cut
        string emoji = char.ConvertFromUtf32(0x1F600);
        string cut = CopiumConnect.SanitizeTokenName(new string('n', 39) + emoji);
        Check.Equal(new string('n', 39), cut);
        Check.Equal(40, CopiumConnect.SanitizeTokenName(new string('n', 38) + emoji + "zz").Length);
    }

    // ------------------------------------------------------------------ settings

    private static readonly string[] s_newKeys = { nameof(Settings.CopiumAccountName), nameof(Settings.CopiumLinkedViaConnect), nameof(Settings.CopiumWelcomeShown), nameof(Settings.CopiumTokenName) };

    [UnitTest]
    public static void The_new_settings_start_empty_and_survive_a_round_trip()
    {
        var fresh = Settings.FromJson("{}");
        Check.Null(fresh.CopiumAccountName);
        Check.False(fresh.CopiumLinkedViaConnect);
        Check.False(fresh.CopiumWelcomeShown);
        Check.Null(fresh.CopiumTokenName);
        Check.False(fresh.ScreenshotUpload, "the automatic upload stays off by default");
        var s = Settings.FromJson("{}");
        s.CopiumToken = Token;
        s.CopiumAccountName = "Jason";
        s.CopiumLinkedViaConnect = true;
        s.CopiumWelcomeShown = true;
        s.CopiumTokenName = "Gaming rig";
        var back = Settings.FromJson(s.ToJson());
        Check.Equal(Token, back.CopiumToken);
        Check.Equal("Jason", back.CopiumAccountName);
        Check.True(back.CopiumLinkedViaConnect);
        Check.True(back.CopiumWelcomeShown);
        Check.Equal("Gaming rig", back.CopiumTokenName);
        foreach (var key in s_newKeys) Check.True(SettingsImport.HasLabelOrIsVolatile(key), key + " is named or ignored by the import offer");
    }

    [UnitTest]
    public static void Normalizing_clears_the_account_name_and_link_when_there_is_no_token()
    {
        var noToken = Settings.FromJson("""{ "CopiumAccountName": "Jason", "CopiumLinkedViaConnect": true, "CopiumWelcomeShown": true, "CopiumTokenName": "Gaming rig" }""");
        Check.Null(noToken.CopiumAccountName);
        Check.False(noToken.CopiumLinkedViaConnect);
        Check.True(noToken.CopiumWelcomeShown, "per installation: not tied to the token");
        Check.Equal("Gaming rig", noToken.CopiumTokenName);

        var withToken = Settings.FromJson("{}");
        withToken.CopiumToken = Token;
        withToken.CopiumAccountName = "Jason";
        withToken.CopiumLinkedViaConnect = true;
        var kept = Settings.FromJson(withToken.ToJson());
        Check.Equal("Jason", kept.CopiumAccountName);
        Check.True(kept.CopiumLinkedViaConnect);
        // a token that cannot be decrypted here still counts as a token
        var foreign = Settings.FromJson("""{ "CopiumTokenProtected": "AAAA-not-ours", "CopiumAccountName": "Jason", "CopiumLinkedViaConnect": true }""");
        Check.Equal("Jason", foreign.CopiumAccountName);
        Check.True(foreign.CopiumLinkedViaConnect);
        Check.Null(Settings.FromJson("""{ "CopiumAccountName": "   " }""").CopiumAccountName, "blank is none");
    }

    [UnitTest]
    public static void An_import_takes_the_account_name_with_the_token_and_keeps_ours_when_the_token_is_kept()
    {
        string theirs = """{ "CopiumTokenProtected": "THEIRS", "CopiumAccountName": "Them", "CopiumLinkedViaConnect": true, "CopiumWelcomeShown": true, "CopiumTokenName": "Their PC", "Fps": 120 }""";
        string ours = """{ "CopiumTokenProtected": "OURS", "CopiumAccountName": "Me", "CopiumLinkedViaConnect": false, "CopiumWelcomeShown": false, "CopiumTokenName": "My PC" }""";

        // the import takes their token: the name and the link come with it; the installation's own keys stay ours
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, ours, keepOurToken: false)))
        {
            var r = doc.RootElement;
            Check.Equal("THEIRS", r.GetProperty("CopiumTokenProtected").GetString());
            Check.Equal("Them", r.GetProperty("CopiumAccountName").GetString());
            Check.True(r.GetProperty("CopiumLinkedViaConnect").GetBoolean());
            Check.False(r.GetProperty("CopiumWelcomeShown").GetBoolean(), "ours");
            Check.Equal("My PC", r.GetProperty("CopiumTokenName").GetString(), "ours");
            Check.Equal(120, r.GetProperty("Fps").GetInt32());
        }
        // their token cannot be decrypted here: ours stays, with its name and link
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, ours, keepOurToken: true)))
        {
            var r = doc.RootElement;
            Check.Equal("OURS", r.GetProperty("CopiumTokenProtected").GetString());
            Check.Equal("Me", r.GetProperty("CopiumAccountName").GetString());
            Check.False(r.GetProperty("CopiumLinkedViaConnect").GetBoolean());
        }
        // we have no token of our own: nothing of theirs describing it is left behind
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, "{}", keepOurToken: true)))
        {
            Check.False(doc.RootElement.TryGetProperty("CopiumTokenProtected", out _));
            Check.False(doc.RootElement.TryGetProperty("CopiumAccountName", out _));
            Check.False(doc.RootElement.TryGetProperty("CopiumLinkedViaConnect", out _));
        }
        // the per-installation keys are never imported, even with no settings of ours
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, null, keepOurToken: false)))
        {
            Check.False(doc.RootElement.TryGetProperty("CopiumWelcomeShown", out _));
            Check.False(doc.RootElement.TryGetProperty("CopiumTokenName", out _));
        }
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.CopiumWelcomeShown)));
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.CopiumTokenName)));
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.CopiumAccountName)), "follows the token");
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.CopiumLinkedViaConnect)), "follows the token");
    }

    [UnitTest]
    public static void An_import_that_keeps_our_token_keeps_our_copium_plus_with_it()
    {
        string theirs = """{ "CopiumTokenProtected": "THEIRS", "CopiumPlus": true, "Fps": 120 }""";
        string ours = """{ "CopiumTokenProtected": "OURS", "CopiumPlus": false }""";
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, ours, keepOurToken: true)))
        {
            Check.Equal("OURS", doc.RootElement.GetProperty("CopiumTokenProtected").GetString());
            Check.False(doc.RootElement.GetProperty("CopiumPlus").GetBoolean(), "their Copium+ does not ride in on our token");
            Check.Equal(120, doc.RootElement.GetProperty("Fps").GetInt32());
        }
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn("""{ "CopiumPlus": false }""", """{ "CopiumTokenProtected": "OURS", "CopiumPlus": true }""", keepOurToken: true)))
            Check.True(doc.RootElement.GetProperty("CopiumPlus").GetBoolean(), "ours stays");
        // their token is taken: their flag comes with it
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, ours, keepOurToken: false)))
            Check.True(doc.RootElement.GetProperty("CopiumPlus").GetBoolean());
        // we have no flag of our own: theirs is not left behind
        using (var doc = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, """{ "CopiumTokenProtected": "OURS" }""", keepOurToken: true)))
            Check.False(doc.RootElement.TryGetProperty("CopiumPlus", out _));
    }

    [UnitTest]
    public static void A_difference_only_in_the_new_keys_is_no_difference_to_offer()
    {
        var a = Settings.FromJson("{}");
        var b = Settings.FromJson("{}");
        b.CopiumWelcomeShown = true;
        b.CopiumTokenName = "Other";
        Check.Equal(0, SettingsImport.Differences(a.ToJson(), b.ToJson()).Count);
        a.CopiumToken = Token;
        b.CopiumToken = Token;
        a.CopiumAccountName = "Me";
        b.CopiumAccountName = "Them";
        b.CopiumLinkedViaConnect = true;
        Check.Equal(0, SettingsImport.Differences(a.ToJson(), b.ToJson()).Count, "same token: the display cache alone offers nothing");
        b.CopiumToken = Token + "X";
        Check.Equal(new[] { "Copium token" }, SettingsImport.Differences(a.ToJson(), b.ToJson()).ToArray());
    }

    [UnitTest]
    public static void An_unusable_incoming_token_keeps_our_copium_plus_so_it_is_no_difference_to_offer()
    {
        var ours = Settings.FromJson("{}");
        ours.CopiumToken = Token;
        ours.CopiumLinkedViaConnect = true;
        ours.CopiumPlus = true;
        var theirs = Settings.FromJson("{}");
        theirs.CopiumTokenProtected = Convert.ToBase64String(new byte[] { 1, 2, 3 });   // protected for somebody else
        theirs.CopiumPlus = false;
        Check.True(SettingsImport.IsTokenUnusable(theirs));
        Check.Equal(0, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).Count, "our token stays, so does its Copium+ flag");
        // a real difference elsewhere is offered, without a Copium+ line
        theirs.Codec = ours.Codec == "hevc" ? "h264" : "hevc";
        var labels = SettingsImport.Differences(ours.ToJson(), theirs.ToJson());
        Check.False(labels.Contains("Copium+"));
        Check.True(labels.Count > 0);
    }

    // ------------------------------------------------------------------ secrets in ToString, token follows account

    [UnitTest]
    public static void The_printed_forms_never_hold_the_poll_key_or_the_token()
    {
        string start = Started().ToString();
        Check.False(start.Contains(PollKey) || start.Contains("SECRET"), "ConnectStart.ToString");
        Check.Contains(start, Code);
        var (r, _, _, _) = Run(Replies((HttpStatusCode.OK, ApprovedJson())), () => CopiumConnect.WaitAsync(Started(), CancellationToken.None));
        string shown = r!.ToString();
        Check.False(shown.Contains(Token) || shown.Contains("eup_"), "ConnectResult.ToString");
        Check.Contains(shown, "Jason");
        Check.False($"{Started()} {r}".Contains(PollKey), "interpolation too");
    }

    [UnitTest]
    public static void The_account_name_and_link_follow_the_token_everywhere()
    {
        var s = Settings.FromJson("{}");
        s.SetCopiumToken(Token, "Jason", viaConnect: true);
        Check.Equal(Token, s.CopiumToken);
        Check.Equal("Jason", s.CopiumAccountName);
        Check.True(s.CopiumLinkedViaConnect);
        // a pasted token replaces a connected one: the name and the link go
        s.SetCopiumToken("eup_pasted_by_hand_0123456789");
        Check.Equal("eup_pasted_by_hand_0123456789", s.CopiumToken);
        Check.Null(s.CopiumAccountName);
        Check.False(s.CopiumLinkedViaConnect);
        s.SetCopiumToken(Token, "  Jason  ", viaConnect: true);
        Check.Equal("Jason", s.CopiumAccountName, "trimmed");
        // clearing clears all three
        s.SetCopiumToken(null);
        Check.Null(s.CopiumTokenProtected);
        Check.Null(s.CopiumAccountName);
        Check.False(s.CopiumLinkedViaConnect);
        s.SetCopiumToken(Token, "Jason", true);
        s.SetCopiumToken("");
        Check.Null(s.CopiumToken);
        Check.Null(s.CopiumAccountName);
        Check.False(s.CopiumLinkedViaConnect);

        // the --dev guard
        using var dir = new TempDir();
        var dev = DevOptions.Parse(new[] { "--dev", dir.Path })!;
        var dev1 = Settings.FromJson("{}");
        dev1.SetCopiumToken(Token, "Jason", true);
        Check.Contains(string.Join("; ", dev.Isolate(dev1, freshStore: false)), "no Copium token");
        Check.Null(dev1.CopiumTokenProtected);
        Check.Null(dev1.CopiumAccountName);
        Check.False(dev1.CopiumLinkedViaConnect);
        var kept = Settings.FromJson("{}");
        kept.SetCopiumToken(Token, "Jason", true);
        DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-token" })!.Isolate(kept, freshStore: false);
        Check.Equal("Jason", kept.CopiumAccountName, "--dev-token keeps the token and its name");
    }
}