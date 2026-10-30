using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// "Sign in with Copium" (slice B): the sign-in session (state machine, cancellation, store / verify, sign out, revoke and cleanup) and the
/// welcome rules, through a mock HTTP handler; and the controller's wiring (UI-thread hop, latest-state coalescing, kill switch, restart).
/// Races are made deterministic with seams (<see cref="CopiumSignInSession.BeforeCommit"/>, held mock responses, idle waits), not sleeps.
/// </summary>
internal static class CopiumSignInTests
{
    private const string Token = "eup_abcdefghijklmnopqrstuvwxyz0123456789ABCD";
    private const string Manual = "eup_pasted_by_hand_0123456789abcdef";
    private const string PollKey = "pollkey-SECRET-0123456789abcdefghijklmnopqrstuvwxyz";
    private const string Code1 = "AB2C-DE9F", Code2 = "AB2C-DE9G";
    private const int Limit = 15_000;

    private sealed record Req(string Method, string Path, string? Bearer, string Body);

    private static Task InlineUi(Action action)
    {
        try { action(); return Task.CompletedTask; }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    /// <summary>One sign-in under test: settings, the opener and save seams, a routed mock for copium.pics, and a fast Delay.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly Settings S = Settings.FromJson("{}");
        public readonly List<Uri> Opened = new();
        public readonly List<SignInState> States = new();
        public readonly List<Req> Requests = new();
        public readonly List<string> Cleanups = new();
        public int Saves, CompletedCount, Welcomes, StartCalls;
        public volatile bool Approve, MeCancelled;
        public readonly ManualResetEventSlim MeEntered = new();
        public string ApprovedBody = $$"""{"token":"{{Token}}","username":"Jason","server":"https://copium.pics"}""";
        public Func<HttpResponseMessage>? Poll, Me, Delete;
        public Func<CancellationToken, Task<HttpResponseMessage>>? MeAsync;
        public Func<int, HttpResponseMessage>? StartReply;
        public readonly CopiumSignInSession Session;
        private readonly Func<TimeSpan, CancellationToken, Task> _oldDelay = CopiumConnect.Delay;

        /// <param name="prepare">Settings as they are when the session is made (it reads its resting state from them then).</param>
        public Rig(Func<Action, Task>? onUi = null, Action<Settings>? prepare = null)
        {
            S.CopiumConsent = true;
            prepare?.Invoke(S);
            Session = new CopiumSignInSession(S, uri => { lock (Opened) Opened.Add(uri); }, () => Interlocked.Increment(ref Saves), onUi)
            {
                IsDevInstance = () => false,
                DevKeepsToken = () => false,
            };
            Session.Changed += s => { lock (States) States.Add(s); };
            Session.Completed += _ => Interlocked.Increment(ref CompletedCount);
            Session.WelcomeNeeded += _ => Interlocked.Increment(ref Welcomes);
            Session.RemoteCleanupNeeded += name => { lock (Cleanups) Cleanups.Add(name); };
            CopiumClient.HttpOverride = new HttpClient(new Router(this));
            CopiumConnect.Delay = (_, ct) => Task.Delay(10, ct);
        }

        public int Count(string method, string path) { lock (Requests) return Requests.Count(r => r.Method == method && r.Path == path); }
        public int Deletes => Count("DELETE", "/api/connect/token");
        public int Polls => Count("POST", "/api/connect/poll");
        public int Total { get { lock (Requests) return Requests.Count; } }
        public Req Last(string method, string path) { lock (Requests) return Requests.Last(r => r.Method == method && r.Path == path); }
        public int OpenedCount { get { lock (Opened) return Opened.Count; } }

        public void WaitFor(Func<bool> done, string what)
        {
            var until = DateTime.UtcNow.AddMilliseconds(Limit);
            while (!done())
            {
                if (DateTime.UtcNow > until) Check.Fail("timed out waiting for " + what + " (state " + Session.Current + ")");
                Thread.Sleep(2);
            }
        }

        public void WaitWaiting() => WaitFor(() => Session.Current.Status == SignInStatus.Waiting, "the Waiting state");
        public void WaitOpened(int n) => WaitFor(() => OpenedCount >= n, n + " browser opening(s)");

        /// <summary>Every run and background revoke of the session has finished.</summary>
        public void Idle()
        {
            if (!Session.WhenIdleAsync().Wait(Limit)) Check.Fail("timed out waiting for the sign-in to go idle (state " + Session.Current + ")");
        }

        /// <summary>Holds /api/me until the request is cancelled (and says so).</summary>
        public void HoldMe() => MeAsync = async ct =>
        {
            MeEntered.Set();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { MeCancelled = true; throw; }
            return Json(HttpStatusCode.OK, MeJson());
        };

        public void Dispose()
        {
            Session.Shutdown();
            try { Session.WhenIdleAsync().Wait(Limit); } catch { }
            CopiumClient.HttpOverride = null;
            CopiumConnect.Delay = _oldDelay;
        }

        private async Task<HttpResponseMessage> RespondAsync(string path, CancellationToken ct)
        {
            switch (path)
            {
                case "/api/connect/start":
                {
                    int n = Interlocked.Increment(ref StartCalls);
                    return StartReply?.Invoke(n) ?? Json(HttpStatusCode.Created, StartJson(n == 1 ? Code1 : Code2));
                }
                case "/api/connect/poll":
                    return Poll?.Invoke() ?? (Approve ? Json(HttpStatusCode.OK, ApprovedBody) : Json(HttpStatusCode.Accepted, "{}"));
                case "/api/me":
                    if (MeAsync is { } held) return await held(ct);
                    return Me?.Invoke() ?? Json(HttpStatusCode.OK, MeJson());
                case "/api/connect/token":
                    return Delete?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return Json(HttpStatusCode.NotFound, "{}");
            }
        }

        private sealed class Router : HttpMessageHandler
        {
            private readonly Rig _rig;
            public Router(Rig rig) { _rig = rig; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
                string path = request.RequestUri!.AbsolutePath;
                Check.Equal("https://copium.pics", request.RequestUri.GetLeftPart(UriPartial.Authority), "only copium.pics is contacted");
                lock (_rig.Requests) _rig.Requests.Add(new Req(request.Method.Method, path, request.Headers.Authorization?.Parameter, body));
                return await _rig.RespondAsync(path, ct);
            }
        }
    }

    /// <summary>A point in the sign-in where the test lands an operation: the session waits there until <see cref="Release"/>.</summary>
    private sealed class Hold
    {
        private readonly ManualResetEventSlim _reached = new();
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Wait() { _reached.Set(); return _go.Task; }
        public void Reached() { if (!_reached.Wait(Limit)) Check.Fail("the sign-in never reached the hold"); }
        public void Release() => _go.TrySetResult();
    }

    /// <summary>The approval is read and held just before the commit decision.</summary>
    private static Hold HoldAtCommit(Rig r)
    {
        var hold = new Hold();
        r.Session.BeforeCommit = hold.Wait;
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Approve = true;
        hold.Reached();
        return hold;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string StartJson(string code) =>
        $$"""{"code":"{{code}}","pollKey":"{{PollKey}}","approveUrl":"https://evil.example/connect?code={{code}}","expiresIn":900,"interval":2}""";

    private static string MeJson(bool plus = true, string name = "Jason") =>
        $$"""{"username":"{{name}}","premium":{"active":{{(plus ? "true" : "false")}}},"limits":{"maxVideoBytes":2000000000,"maxVideoSeconds":3600},"rulesAccepted":true}""";

    private static void SignInAndApprove(Rig r, string name = "Gaming rig")
    {
        r.Session.Start(name);
        r.WaitWaiting();
        r.Approve = true;
        r.Idle();
    }

    // ------------------------------------------------------------------ happy path

    [UnitTest]
    public static void Signing_in_waits_opens_the_page_once_then_stores_the_token_and_reads_the_account()
    {
        using var r = new Rig();
        r.S.CopiumPlus = false;
        r.Session.Start("Gaming rig");
        r.WaitWaiting();
        r.WaitOpened(1);
        var waiting = r.Session.Current;
        Check.Equal(Code1, waiting.Code);
        Check.Equal("https://copium.pics/connect?code=" + Code1, waiting.ApproveUrl!.AbsoluteUri, "built locally, not the server's approveUrl");
        Check.False(waiting.Retrying);
        Check.False(waiting.HasToken);
        Check.Equal(1, r.OpenedCount, "the browser was opened once");
        Check.Equal("https://copium.pics/connect?code=" + Code1, r.Opened[0].AbsoluteUri);
        Check.False(r.S.CopiumToken != null, "nothing stored before the approval");
        Check.Equal("Gaming rig", r.S.CopiumTokenName, "the name is kept for next time");

        r.Approve = true;
        r.Idle();
        var done = r.Session.Current;
        Check.Equal(SignInStatus.SignedIn, done.Status);
        Check.Equal("Jason", done.Username);
        Check.True(done.Plus, "Copium+ from the account");
        Check.True(done.Verified);
        Check.True(done.HasToken);
        Check.True(done.LinkedViaConnect);
        Check.Null(done.RemoteCleanupName);
        Check.False(r.Session.IsRunning);
        Check.Equal(Token, r.S.CopiumToken);
        Check.Equal("Jason", r.S.CopiumAccountName);
        Check.True(r.S.CopiumLinkedViaConnect);
        Check.True(r.S.CopiumPlus, "the I-have-Copium+ setting follows premium");
        Check.True(r.Saves >= 2, "settings were saved");
        Check.Equal(1, r.CompletedCount);
        Check.Equal(1, r.Welcomes, "first sign-in: the welcome is needed");
        Check.Equal("Gaming rig", JsonDocument.Parse(r.Last("POST", "/api/connect/start").Body).RootElement.GetProperty("name").GetString());
        Check.Equal(Token, r.Last("GET", "/api/me").Bearer);
        Check.Equal(1, r.OpenedCount, "still once");
        Check.Equal(0, r.Deletes);
        lock (r.States)
        {
            Check.Equal(r.States.Count, r.States.Select(s => s.Version).Distinct().Count(), "every state has its own version");
            Check.Equal(done.Version, r.States.Max(s => s.Version), "the current state is the newest");
        }
    }

    [UnitTest]
    public static void The_welcome_is_asked_for_only_when_it_was_not_shown()
    {
        using var r = new Rig();
        r.S.CopiumWelcomeShown = true;
        SignInAndApprove(r);
        Check.Equal(SignInStatus.SignedIn, r.Session.Current.Status);
        Check.Equal(1, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
    }

    [UnitTest]
    public static void Plus_off_in_the_account_clears_the_setting()
    {
        using var r = new Rig();
        r.S.CopiumPlus = true;
        r.Me = () => Json(HttpStatusCode.OK, MeJson(plus: false));
        SignInAndApprove(r);
        Check.False(r.S.CopiumPlus);
        Check.False(r.Session.Current.Plus);
    }

    [UnitTest]
    public static void An_empty_username_from_the_poll_is_filled_from_the_account()
    {
        using var r = new Rig();
        r.ApprovedBody = $$"""{"token":"{{Token}}","username":"","server":"https://copium.pics"}""";
        r.Me = () => Json(HttpStatusCode.OK, MeJson(name: "FromMe"));
        SignInAndApprove(r);
        Check.Equal("FromMe", r.S.CopiumAccountName);
        Check.Equal("FromMe", r.Session.Current.Username);
        Check.True(r.Session.Current.Verified);
    }

    [UnitTest]
    public static void A_failing_account_read_keeps_the_token_unverified()
    {
        foreach (var bad in new Func<HttpResponseMessage>[] { () => Json(HttpStatusCode.InternalServerError, "{}"), () => Json(HttpStatusCode.Unauthorized, """{"error":"nope"}"""), () => Json(HttpStatusCode.OK, "not json") })
        {
            using var r = new Rig();
            r.Me = bad;
            SignInAndApprove(r);
            Check.Equal(SignInStatus.SignedIn, r.Session.Current.Status);
            Check.False(r.Session.Current.Verified);
            Check.True(r.Session.Current.AccountCheckFailed, "a failed check is remembered for the note");
            Check.Equal(Token, r.S.CopiumToken, "an /api/me failure never removes the token");
            Check.Equal("Jason", r.S.CopiumAccountName, "the name from the approval");
            Check.True(r.S.CopiumLinkedViaConnect);
            Check.Equal(0, r.Deletes);
            Check.Equal(1, r.CompletedCount);
        }
    }

    [UnitTest]
    public static void An_unexpected_error_after_the_token_is_stored_rests_signed_in_unverified_not_failed()
    {
        int calls = 0;   // the session's UI hops: 1 the browser, 2 the commit, 3 the account
        using var r = new Rig(onUi: a => Interlocked.Increment(ref calls) == 3 ? Task.FromException(new InvalidOperationException("the UI went away")) : InlineUi(a));
        r.S.CopiumWelcomeShown = true;
        SignInAndApprove(r);
        Check.Equal(3, calls);
        var now = r.Session.Current;
        Check.Equal(SignInStatus.SignedIn, now.Status, now.ToString());
        Check.Null(now.Failure);
        Check.False(now.Verified);
        Check.True(now.LinkedViaConnect);
        Check.False(r.Session.IsRunning, "the run is over, not stuck committed");
        Check.Equal(Token, r.S.CopiumToken);
        Check.Equal(0, r.Deletes, "a stored token is never revoked by an error");
    }

    [UnitTest]
    public static void Polling_that_backs_off_shows_retrying_and_clears_it_again()
    {
        using var r = new Rig();
        int polls = 0;
        r.Poll = () =>
        {
            int n = Interlocked.Increment(ref polls);
            if (n <= 2) return Json(HttpStatusCode.InternalServerError, "<html>");
            if (n == 3) return Json(HttpStatusCode.Accepted, "{}");
            return r.Approve ? Json(HttpStatusCode.OK, r.ApprovedBody) : Json(HttpStatusCode.Accepted, "{}");
        };
        r.Session.Start("PC");
        r.WaitFor(() => { lock (r.States) return r.States.Any(s => s.Retrying) && polls >= 3 && !r.Session.Current.Retrying && r.Session.Current.Status == SignInStatus.Waiting; }, "retrying and then not");
        r.Approve = true;
        r.Idle();
        Check.Equal(SignInStatus.SignedIn, r.Session.Current.Status);
    }

    // ------------------------------------------------------------------ refusals and unsupported

    [UnitTest]
    public static void Start_is_refused_without_a_request_when_cloud_sharing_is_off()
    {
        using var r = new Rig();
        r.S.CloudSharingEnabled = false;
        r.Session.Start("PC");
        Check.Equal(SignInStatus.Failed, r.Session.Current.Status);
        Check.Equal(SignInFailure.CloudSharingOff, r.Session.Current.Failure);
        Check.False(r.Session.IsRunning);
        r.Idle();
        Check.Equal(0, r.Total, "no request");
        Check.Equal(0, r.OpenedCount);
    }

    [UnitTest]
    public static void A_dev_instance_without_dev_token_refuses_and_with_it_names_the_token_dev()
    {
        using (var r = new Rig())
        {
            r.Session.IsDevInstance = () => true;
            r.Session.DevKeepsToken = () => false;
            r.Session.Start("My PC");
            Check.Equal(SignInFailure.DevInstanceNeedsToken, r.Session.Current.Failure);
            Check.Contains(r.Session.Current.Message, "--dev-token");
            r.Idle();
            Check.Equal(0, r.Total, "no request");
        }
        foreach (var (given, expected) in new[] { ("My PC", "My PC-dev"), ("PC-dev", "PC-dev"), ("PC-DEV", "PC-DEV"), (new string('x', 60), new string('x', 36) + "-dev"), ("", null) })
        {
            using var r = new Rig();
            r.Session.IsDevInstance = () => true;
            r.Session.DevKeepsToken = () => true;
            r.Session.Start(given);
            r.WaitWaiting();
            string sent = JsonDocument.Parse(r.Last("POST", "/api/connect/start").Body).RootElement.GetProperty("name").GetString()!;
            Check.True(sent.EndsWith("-dev", StringComparison.OrdinalIgnoreCase), "'" + given + "' -> '" + sent + "'");
            Check.True(sent.Length <= 40);
            if (expected != null) Check.Equal(expected, sent);
            Check.Equal(sent, r.S.CopiumTokenName);
        }
        using (var r = new Rig())   // a normal instance is not suffixed
        {
            r.Session.Start("My PC");
            r.WaitWaiting();
            Check.Equal("My PC", r.S.CopiumTokenName);
        }
    }

    [UnitTest]
    public static void Start_while_signed_in_via_connect_is_refused_without_a_request_but_a_pasted_token_does_not_block()
    {
        using (var r = Linked(viaConnect: true))
        {
            r.Session.Start("Another name");
            var now = r.Session.Current;
            Check.Equal(SignInStatus.Failed, now.Status);
            Check.Equal(SignInFailure.AlreadySignedIn, now.Failure);
            Check.Contains(now.Message, "sign out first");
            Check.False(r.Session.IsRunning);
            r.Idle();
            Check.Equal(0, r.Total, "no request");
            Check.Equal(0, r.OpenedCount);
            Check.Equal(Token, r.S.CopiumToken, "the token is untouched");
            Check.Equal("Gaming rig", r.S.CopiumTokenName, "a refused start does not rename the linked token");
        }
        using (var r = Linked(viaConnect: false))
        {
            r.Session.Start("PC");
            r.WaitWaiting();
            Check.Equal(1, r.Count("POST", "/api/connect/start"), "a pasted token is no sign-in: allowed");
        }
    }

    [UnitTest]
    public static void A_failed_state_still_carries_the_resting_account()
    {
        using (var r = Linked(viaConnect: true))
        {
            r.Session.Start("PC");
            var failed = r.Session.Current;
            Check.Equal(SignInStatus.Failed, failed.Status);
            Check.True(failed.HasToken);
            Check.True(failed.LinkedViaConnect);
            Check.Equal("Jason", failed.Username);
        }
        using (var r = Linked(viaConnect: false))
        {
            r.StartReply = _ => Json(HttpStatusCode.BadRequest, """{"error":"Unknown app"}""");
            r.Session.Start("PC");
            r.Idle();
            var failed = r.Session.Current;
            Check.Equal(SignInFailure.Unsupported, failed.Failure);
            Check.True(failed.HasToken, "the pasted token is still there");
            Check.False(failed.LinkedViaConnect);
            Check.Null(failed.Username);
        }
    }

    [UnitTest]
    public static void An_older_copium_is_reported_as_unsupported()
    {
        using var r = new Rig();
        r.StartReply = _ => Json(HttpStatusCode.BadRequest, """{"error":"Unknown app"}""");
        r.Session.Start("PC");
        r.Idle();
        Check.Equal(SignInStatus.Failed, r.Session.Current.Status);
        Check.Equal(SignInFailure.Unsupported, r.Session.Current.Failure);
        Check.False(r.Session.Current.HasToken);
        Check.Equal(0, r.OpenedCount, "no browser");
        Check.Null(r.S.CopiumToken);
    }

    [UnitTest]
    public static void Other_failures_end_in_failed_with_their_reason()
    {
        using var r = new Rig();
        r.Poll = () => Json(HttpStatusCode.Gone, """{"error":"expired"}""");
        r.Session.Start("PC");
        r.Idle();
        Check.Equal(SignInFailure.Expired, r.Session.Current.Failure);
        r.Poll = () => Json(HttpStatusCode.Forbidden, """{"error":"cancelled"}""");
        r.Session.Start("PC");
        r.Idle();
        Check.Equal(SignInFailure.Denied, r.Session.Current.Failure);
    }

    // ------------------------------------------------------------------ cancellation

    [UnitTest]
    public static void Cancel_while_waiting_returns_to_signed_out_and_stops_polling()
    {
        using var r = new Rig();
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Session.Cancel();
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.False(r.Session.IsRunning);
        Check.False(r.Session.OpenApprovePageAgain(), "nothing to reopen");
        r.Idle();   // the run itself has ended: nothing polls any more
        int polls = r.Polls;
        r.Approve = true;   // an approval in the browser now is never seen: no poll asks for it
        r.Idle();
        Check.Equal(polls, r.Polls, "no poll after the cancel was answered");
        Check.Equal(0, r.Deletes, "no token was ever received, so there is nothing to revoke");
        Check.Null(r.S.CopiumToken);
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.Equal(0, r.CompletedCount);
    }

    [UnitTest]
    public static void An_approval_read_just_before_a_cancel_is_revoked_exactly_once_and_not_stored()
    {
        using var r = new Rig();
        var hold = HoldAtCommit(r);
        r.Session.Cancel();
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        hold.Release();
        r.Idle();
        Check.Null(r.S.CopiumToken, "not stored");
        Check.Equal(1, r.Deletes, "revoked exactly once");
        Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer, "the token that was approved");
        Check.Equal(0, r.Count("GET", "/api/me"));
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.Null(r.Session.Current.RemoteCleanupName, "the revoke worked");
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
    }

    [UnitTest]
    public static void Cancel_with_an_earlier_pasted_token_keeps_it()
    {
        using var r = Linked(viaConnect: false);
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Session.Cancel();
        var now = r.Session.Current;
        Check.Equal(SignInStatus.SignedOut, now.Status, "a pasted token is not a sign-in");
        Check.True(now.HasToken);
        Check.Equal(Token, r.S.CopiumToken, "Cancel never removes a token");
        Check.False(r.S.CopiumLinkedViaConnect);
    }

    [UnitTest]
    public static void Turning_the_kill_switch_off_mid_wait_stops_it()
    {
        using var r = new Rig();
        r.Session.Start("PC");
        r.WaitWaiting();
        r.S.CloudSharingEnabled = false;
        r.Session.StopForCloudSharingOff();   // what AppController.SetCloudSharing(false) does
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.False(r.Session.IsRunning);
        r.Idle();
        int total = r.Total;
        r.Approve = true;
        r.Idle();
        Check.Equal(total, r.Total, "nothing more is sent");
        Check.Null(r.S.CopiumToken);
    }

    [UnitTest]
    public static void An_approval_that_arrives_with_the_kill_switch_off_is_revoked_not_stored()
    {
        using var r = new Rig();
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Poll = () => { r.S.CloudSharingEnabled = false; return Json(HttpStatusCode.OK, r.ApprovedBody); };   // off between the poll and the store
        r.Idle();
        Check.Equal(1, r.Deletes, "the intended exception to the kill switch: one revoke of the unstored token");
        Check.Null(r.S.CopiumToken, "not stored");
        Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer, "the new token was revoked");
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
        Check.Equal(0, r.Count("GET", "/api/me"));
    }

    [UnitTest]
    public static void The_kill_switch_at_the_commit_point_revokes_the_approval_once()
    {
        using var r = new Rig();
        var hold = HoldAtCommit(r);
        r.S.CloudSharingEnabled = false;
        r.Session.StopForCloudSharingOff();
        hold.Release();
        r.Idle();
        Check.Null(r.S.CopiumToken);
        Check.Equal(1, r.Deletes);
        Check.Equal(0, r.Count("GET", "/api/me"));
        Check.Equal(0, r.CompletedCount);
    }

    [UnitTest]
    public static void Sharing_off_after_the_commit_sends_no_account_read_and_after_the_read_fires_no_completion()
    {
        foreach (int hop in new[] { 2, 3 })   // the session's UI hops: 1 the browser, 2 the commit, 3 the account
        {
            int calls = 0;
            Rig? rig = null;
            using var r = rig = new Rig(onUi: a =>
            {
                var t = InlineUi(a);
                if (Interlocked.Increment(ref calls) == hop) rig!.S.CloudSharingEnabled = false;   // off right after that hop, without the kill-switch call
                return t;
            });
            SignInAndApprove(r);
            Check.Equal(Token, r.S.CopiumToken, "stored (" + hop + ")");
            Check.Equal(hop == 2 ? 0 : 1, r.Count("GET", "/api/me"), "the account is read only while sharing is on (" + hop + ")");
            Check.Equal(0, r.CompletedCount, "no completion with sharing off (" + hop + ")");
            Check.Equal(0, r.Welcomes);
            var now = r.Session.Current;
            Check.Equal(SignInStatus.SignedIn, now.Status);
            Check.False(r.Session.IsRunning);
            if (hop == 2) Check.False(now.Verified);
        }
    }

    [UnitTest]
    public static void A_cancel_that_lands_while_the_approving_poll_answer_is_in_flight_drops_the_answer_unread()
    {
        using var r = new Rig();
        r.Session.Start("PC");
        r.WaitWaiting();
        // The cancel lands while the poll's 200 is on its way back: the request is cancelled, so the answer (and the token in it) is never
        // read. Exactly: nothing stored, nothing revoked (the token Copium issued in that answer stays on the account: the known residual,
        // removable on the website), no account read, no completion.
        int answered = 0;
        r.Poll = () => { Interlocked.Increment(ref answered); r.Session.Cancel(); return Json(HttpStatusCode.OK, r.ApprovedBody); };
        r.WaitFor(() => Volatile.Read(ref answered) >= 1, "the poll");
        r.Idle();
        Check.Equal(1, answered, "one approving answer, then no more polls");
        Check.Null(r.S.CopiumToken, "not stored");
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.Equal(0, r.Deletes, "the answer was never read, so no token to revoke");
        Check.Equal(0, r.Count("GET", "/api/me"));
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
    }

    [UnitTest]
    public static void Saving_a_token_by_hand_mid_wait_stops_it_and_keeps_the_hand_saved_token()
    {
        using var r = new Rig();
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Session.SaveManualToken(Manual);   // TokenSave_Click
        var now = r.Session.Current;
        Check.Equal(SignInStatus.SignedOut, now.Status, "a pasted token is not a sign-in");
        Check.True(now.HasToken);
        Check.False(r.Session.IsRunning);
        r.Approve = true;
        r.Idle();
        Check.Equal(Manual, r.S.CopiumToken);
        Check.False(r.S.CopiumLinkedViaConnect);
        Check.Equal(0, r.Deletes, "nothing was approved and the old token was not a Connect one");
        Check.Equal(0, r.CompletedCount);
    }

    [UnitTest]
    public static void A_hand_saved_token_while_the_approval_is_held_wins_and_the_approval_is_revoked_once()
    {
        using var r = new Rig();
        var hold = HoldAtCommit(r);   // the poll's 200 is in: the token is read, the commit not decided yet
        r.Session.SaveManualToken(Manual);
        hold.Release();
        r.Idle();
        Check.Equal(Manual, r.S.CopiumToken, "the pasted token is kept");
        Check.False(r.S.CopiumLinkedViaConnect);
        Check.Null(r.S.CopiumAccountName);
        Check.Equal(1, r.Deletes, "exactly one revoke");
        Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer, "of the Connect token, never the pasted one");
        Check.Equal(0, r.Count("GET", "/api/me"));
        var now = r.Session.Current;
        Check.Equal(SignInStatus.SignedOut, now.Status);
        Check.True(now.HasToken);
        Check.False(now.LinkedViaConnect);
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
    }

    [UnitTest]
    public static void A_hand_saved_token_while_the_account_is_read_replaces_and_revokes_the_connect_token()
    {
        using var r = new Rig();
        r.HoldMe();
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Approve = true;
        Check.True(r.MeEntered.Wait(Limit), "the account read started");
        Check.Equal(Token, r.S.CopiumToken, "committed");
        r.Session.SaveManualToken(Manual);   // even a committed run is stopped
        r.Idle();
        Check.True(r.MeCancelled, "the account read was cancelled");
        Check.Equal(Manual, r.S.CopiumToken);
        Check.Null(r.S.CopiumAccountName);
        Check.False(r.S.CopiumLinkedViaConnect);
        Check.Equal(1, r.Deletes, "the replaced Connect token is this app's own: revoked once");
        Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer);
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status, "not stuck on Starting");
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
    }

    [UnitTest]
    public static void A_second_start_cancels_the_first()
    {
        using var r = new Rig();
        r.S.CopiumWelcomeShown = true;
        r.Session.Start("First");
        r.WaitWaiting();
        r.WaitOpened(1);
        Check.Equal(Code1, r.Session.Current.Code);
        r.Session.Start("Second");
        r.WaitFor(() => r.Session.Current.Status == SignInStatus.Waiting && r.Session.Current.Code == Code2, "the second code");
        r.WaitOpened(2);
        Check.Equal(2, r.StartCalls);
        Check.Equal(2, r.OpenedCount);
        Check.Equal("https://copium.pics/connect?code=" + Code2, r.Opened[1].AbsoluteUri);
        Check.Equal("Second", r.S.CopiumTokenName);
        r.Approve = true;
        r.Idle();
        Check.Equal(SignInStatus.SignedIn, r.Session.Current.Status);
        Check.Equal(1, r.CompletedCount, "one sign-in, not two");
        Check.Equal(Token, r.S.CopiumToken);
        Check.Equal(2, r.OpenedCount, "the first never opened again");
        lock (r.States)
        {
            int second = r.States.FindIndex(s => s.Code == Code2);
            Check.False(r.States.Skip(second).Any(s => s.Code == Code1), "the first never came back");
        }
    }

    [UnitTest]
    public static void Shutdown_stops_the_wait_without_touching_the_token()
    {
        using var r = Linked(viaConnect: false, token: Manual);
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Session.Shutdown();
        Check.False(r.Session.IsRunning);
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status, "no stale code left on screen");
        r.Idle();
        int total = r.Total;
        r.Approve = true;
        r.Idle();
        Check.Equal(total, r.Total);
        Check.Equal(Manual, r.S.CopiumToken);
        Check.Equal(0, r.CompletedCount);
    }

    [UnitTest]
    public static void Shutdown_at_the_commit_point_stores_nothing_and_revokes_once()
    {
        using var r = new Rig();
        var hold = HoldAtCommit(r);
        r.Session.Shutdown();   // exit, or the restart of a settings import
        hold.Release();
        r.Idle();
        Check.Null(r.S.CopiumToken);
        Check.Equal(1, r.Deletes);
        Check.Equal(0, r.CompletedCount);
    }

    // ------------------------------------------------------------------ a hand-saved token replacing a Connect one; cleanup on the website

    [UnitTest]
    public static void Replacing_or_clearing_a_connected_token_by_hand_revokes_it_once_and_a_pasted_one_never()
    {
        foreach (string? next in new[] { Manual, null, "" })
        {
            using var r = Linked(viaConnect: true);
            Check.Equal(SignInStatus.SignedIn, r.Session.Current.Status);
            long before = r.Session.Current.Version;
            r.Session.SaveManualToken(next);
            var now = r.Session.Current;
            Check.True(now.Version > before, "the resting state is published even with no sign-in running");
            Check.Equal(SignInStatus.SignedOut, now.Status, "no stale SignedIn after the paste");
            Check.Equal(!string.IsNullOrEmpty(next), now.HasToken);
            r.Idle();
            Check.Equal(1, r.Deletes, "the old Connect token, once ('" + next + "')");
            Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer);
            Check.Equal(string.IsNullOrEmpty(next) ? null : next, r.S.CopiumToken);
            Check.True(r.Saves >= 1);
        }
        foreach (string? next in new[] { Manual, null })
        {
            using var r = Linked(viaConnect: false);
            r.Session.SaveManualToken(next);
            r.Idle();
            Check.Equal(0, r.Total, "a pasted token is not ours to revoke ('" + next + "')");
            Check.Equal(next, r.S.CopiumToken);
        }
        using (var r = Linked(viaConnect: true))
        {
            r.Session.SaveManualToken(Token);   // the same token again: unchanged (still linked, name kept), not revoked
            r.Idle();
            Check.Equal(0, r.Deletes);
            Check.Equal(Token, r.S.CopiumToken);
            Check.True(r.S.CopiumLinkedViaConnect);
        }
    }

    [UnitTest]
    public static void A_revoke_that_does_not_work_asks_for_cleanup_on_the_website()
    {
        var failing = new (string What, Func<HttpResponseMessage> Reply)[]
        {
            ("500", () => Json(HttpStatusCode.InternalServerError, "{}")),
            ("404 older Copium", () => Json(HttpStatusCode.NotFound, """{"error":"Not found"}""")),
            ("403 token scope", () => Json(HttpStatusCode.Forbidden, """{"error":"This app connection can't do that"}""")),
            ("offline", () => throw new HttpRequestException("no route")),
        };
        foreach (var (what, reply) in failing)
        {
            using var r = Linked(viaConnect: true);
            r.Delete = reply;
            r.Session.SaveManualToken(Manual);
            r.Idle();
            Check.Equal(1, r.Deletes, what);
            Check.Equal("Gaming rig", r.Session.Current.RemoteCleanupName, what);
            lock (r.Cleanups) Check.Equal(new[] { "Gaming rig" }, r.Cleanups.ToArray(), what);
            Check.Equal(Manual, r.S.CopiumToken, what);
            r.Session.DismissRemoteCleanup();
            Check.Null(r.Session.Current.RemoteCleanupName, what);
        }
        var gone = new (string What, Func<HttpResponseMessage> Reply)[]
        {
            ("204", () => new HttpResponseMessage(HttpStatusCode.NoContent)),
            ("401", () => Json(HttpStatusCode.Unauthorized, "{}")),
            ("403 signedOut", () => Json(HttpStatusCode.Forbidden, """{"error":"You left the server","signedOut":true}""")),
            ("403 banned", () => Json(HttpStatusCode.Forbidden, """{"error":"Your account has been suspended","banned":true}""")),
        };
        foreach (var (what, reply) in gone)
        {
            using var r = Linked(viaConnect: true);
            r.Delete = reply;
            r.Session.SaveManualToken(Manual);
            r.Idle();
            Check.Equal(1, r.Deletes, what);
            Check.Null(r.Session.Current.RemoteCleanupName, what + " counts as revoked");
            lock (r.Cleanups) Check.Equal(0, r.Cleanups.Count, what);
        }
    }

    [UnitTest]
    public static void An_unstored_approval_whose_revoke_fails_asks_for_cleanup_under_the_sign_in_name()
    {
        using var r = new Rig();
        r.Delete = () => Json(HttpStatusCode.NotFound, "{}");
        var hold = HoldAtCommit(r);
        r.Session.Cancel();
        hold.Release();
        r.Idle();
        Check.Equal(1, r.Deletes);
        Check.Equal("PC", r.Session.Current.RemoteCleanupName);
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
    }

    [UnitTest]
    public static void Copium_revoke_answers_403_by_what_the_server_says()
    {
        bool? Revoke(HttpStatusCode code, string body)
        {
            var old = CopiumClient.HttpOverride;
            CopiumClient.HttpOverride = new HttpClient(new FuncHandler(_ => Json(code, body)));
            try { return Task.Run(() => CopiumConnect.RevokeSelfAsync(Token, CancellationToken.None)).GetAwaiter().GetResult(); }
            finally { CopiumClient.HttpOverride = old; }
        }
        Check.Equal(true, Revoke(HttpStatusCode.Forbidden, """{"error":"You are no longer a member","signedOut":true}"""), "membership lost: access already revoked");
        Check.Equal(true, Revoke(HttpStatusCode.Forbidden, """{"error":"Your account has been suspended","banned":true}"""), "banned: access already revoked");
        Check.Equal(false, Revoke(HttpStatusCode.Forbidden, """{"error":"This app connection can't do that"}"""), "a token scope without the route");
        Check.Equal(false, Revoke(HttpStatusCode.Forbidden, """{"error":"x","signedOut":false,"banned":false}"""));
        Check.Equal(false, Revoke(HttpStatusCode.Forbidden, """{"error":"x","signedOut":"true"}"""), "only a JSON true counts");
        Check.Equal(false, Revoke(HttpStatusCode.Forbidden, """[{"signedOut":true}]"""));
        Check.Equal(false, Revoke(HttpStatusCode.Forbidden, "<html>signedOut: true</html>"), "a challenge page");
        Check.Equal(false, Revoke(HttpStatusCode.Forbidden, ""));
        Check.Equal(false, Revoke(HttpStatusCode.InternalServerError, """{"signedOut":true}"""), "only on a 403");
    }

    // ------------------------------------------------------------------ the browser address

    [UnitTest]
    public static void The_page_can_be_opened_again_and_only_the_connect_page_ever_reaches_the_opener()
    {
        using var r = new Rig();
        r.Session.Start("PC");   // the server's approveUrl is https://evil.example/...: it is ignored
        r.WaitWaiting();
        r.WaitOpened(1);
        Check.True(r.Session.OpenApprovePageAgain());
        Check.True(r.Session.OpenApprovePageAgain());
        Check.Equal(3, r.OpenedCount);
        lock (r.Opened)
        {
            Check.True(r.Opened.All(u => u.AbsoluteUri == "https://copium.pics/connect?code=" + Code1), string.Join(" ", r.Opened.Select(u => u.AbsoluteUri)));
            Check.True(r.Opened.All(CopiumSignInSession.IsApproveUri));
        }

        Check.True(CopiumSignInSession.IsApproveUri(new Uri("https://copium.pics/connect?code=AB2C-DE9F")));
        Check.True(CopiumSignInSession.IsApproveUri(new Uri("https://COPIUM.pics/connect")));
        foreach (string bad in new[]
        {
            "http://copium.pics/connect?code=AB2C-DE9F", "https://evil.example/connect?code=AB2C-DE9F", "https://v.copium.pics/connect", "https://copium.pics.evil.example/connect",
            "https://copium.pics@evil.example/connect", "https://user:pw@copium.pics/connect", "https://copium.pics:8443/connect", "https://copium.pics/", "https://copium.pics/connect/x",
            "https://copium.pics/other?x=/connect", "file:///C:/Windows/System32/calc.exe", "ms-msdt:/id", "javascript:alert(1)",
        })
            Check.False(CopiumSignInSession.IsApproveUri(new Uri(bad)), bad);
        Check.False(CopiumSignInSession.IsApproveUri(null));
        Check.False(CopiumSignInSession.IsApproveUri(new Uri("/connect", UriKind.Relative)));
    }

    [UnitTest]
    public static void The_browser_opens_on_the_ui_runner_and_not_after_a_cancel()
    {
        // A UI thread the test drives by hand: the session's hops queue here until the test runs them.
        var queue = new List<(Action Action, TaskCompletionSource Done)>();
        Task Queue(Action a)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (queue) queue.Add((a, done));
            return done.Task;
        }
        void RunQueued()
        {
            (Action Action, TaskCompletionSource Done)[] items;
            lock (queue) { items = queue.ToArray(); queue.Clear(); }
            foreach (var (a, done) in items) { a(); done.SetResult(); }
        }
        using var r = new Rig(onUi: Queue);
        r.Session.Start("PC");
        r.WaitWaiting();
        r.WaitFor(() => { lock (queue) return queue.Count == 1; }, "the opener queued for the UI");
        Check.Equal(0, r.OpenedCount, "not opened from the worker");
        r.Session.Cancel();   // lands before the UI gets to the opener
        RunQueued();
        r.Idle();
        Check.Equal(0, r.OpenedCount, "the browser is not opened for a cancelled sign-in");
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.Equal(0, r.Polls);

        // and the normal case through the same runner
        r.Session.Start("PC");
        r.WaitWaiting();
        r.WaitFor(() => { lock (queue) return queue.Count == 1; }, "the opener queued again");
        RunQueued();
        Check.Equal(1, r.OpenedCount);
    }

    [UnitTest]
    public static void A_failing_browser_does_not_stop_the_sign_in()
    {
        using var r = new Rig();
        var s = new CopiumSignInSession(r.S, _ => throw new InvalidOperationException("no browser"), () => { }) { IsDevInstance = () => false, DevKeepsToken = () => false };
        s.Start("PC");
        r.WaitFor(() => s.Current.Status == SignInStatus.Waiting, "waiting");
        Check.True(s.OpenApprovePageAgain());   // still only a logged failure
        Check.Equal(SignInStatus.Waiting, s.Current.Status);
        s.Shutdown();
        Check.True(s.WhenIdleAsync().Wait(Limit));
    }

    // ------------------------------------------------------------------ sign out

    /// <summary>A rig whose settings already hold a token (from signing in, or pasted), under the token name "Gaming rig".</summary>
    private static Rig Linked(bool viaConnect, string token = Token) => new(prepare: s =>
    {
        s.SetCopiumToken(token, viaConnect ? "Jason" : null, viaConnect);
        s.CopiumTokenName = "Gaming rig";
        s.CopiumConsent = true;
        s.ScreenshotUpload = true;
    });

    [UnitTest]
    public static void Signing_out_of_a_connected_account_revokes_and_clears_but_keeps_consent()
    {
        using var r = Linked(true);
        var session = new CopiumSignInSession(r.S, _ => { }, () => r.Saves++) { IsDevInstance = () => false, DevKeepsToken = () => false };
        Check.Equal(SignInStatus.SignedIn, session.Current.Status, "a connected token is the resting SignedIn state");
        var result = session.SignOutAsync().GetAwaiter().GetResult();
        Check.Equal(SignOutResult.Revoked, result);
        Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer);
        Check.Null(r.S.CopiumToken);
        Check.Null(r.S.CopiumAccountName);
        Check.False(r.S.CopiumLinkedViaConnect);
        Check.True(r.S.CopiumConsent, "D8: consent stays");
        Check.True(r.S.ScreenshotUpload);
        Check.Equal(SignInStatus.SignedOut, session.Current.Status);
        Check.Null(session.Current.RemoteCleanupName);
        Check.True(r.Saves >= 1);
    }

    [UnitTest]
    public static void Signing_out_where_copium_cannot_revoke_still_clears_the_token_and_asks_for_cleanup()
    {
        using var r = Linked(true);
        r.Delete = () => Json(HttpStatusCode.NotFound, """{"error":"Not found"}""");
        Check.Equal(SignOutResult.NotSupportedDisconnectOnWebsite, r.Session.SignOutAsync().GetAwaiter().GetResult());
        Check.Null(r.S.CopiumToken);
        Check.True(r.S.CopiumConsent);
        Check.Equal("Gaming rig", r.Session.Current.RemoteCleanupName);
    }

    [UnitTest]
    public static void Signing_out_with_a_token_copium_already_dropped_counts_as_revoked()
    {
        foreach (var reply in new Func<HttpResponseMessage>[] { () => Json(HttpStatusCode.Unauthorized, "{}"), () => Json(HttpStatusCode.Forbidden, """{"error":"gone","signedOut":true}""") })
        {
            using var r = Linked(true);
            r.Delete = reply;
            Check.Equal(SignOutResult.Revoked, r.Session.SignOutAsync().GetAwaiter().GetResult());
            Check.Null(r.S.CopiumToken);
            Check.Null(r.Session.Current.RemoteCleanupName);
        }
    }

    [UnitTest]
    public static void Signing_out_offline_or_on_an_error_still_clears_the_token()
    {
        foreach (var reply in new Func<HttpResponseMessage>[] { () => throw new HttpRequestException("no route"), () => Json(HttpStatusCode.InternalServerError, "{}"), () => Json(HttpStatusCode.Forbidden, """{"error":"This app connection can't do that"}""") })
        {
            using var r = Linked(true);
            r.Delete = reply;
            Check.Equal(SignOutResult.FailedButSignedOutLocally, r.Session.SignOutAsync().GetAwaiter().GetResult());
            Check.Null(r.S.CopiumToken);
            Check.True(r.S.CopiumConsent);
            Check.Equal("Gaming rig", r.Session.Current.RemoteCleanupName);
        }
    }

    [UnitTest]
    public static void Signing_out_of_a_pasted_token_asks_copium_nothing()
    {
        using var r = Linked(false);
        Check.Equal(SignOutResult.Manual, r.Session.SignOutAsync().GetAwaiter().GetResult());
        Check.Equal(0, r.Total, "no request");
        Check.Null(r.S.CopiumToken);
        Check.True(r.S.CopiumConsent);
    }

    [UnitTest]
    public static void Signing_out_with_no_token_does_nothing_and_stops_a_wait()
    {
        using var r = new Rig();
        Check.Equal(SignOutResult.NotSignedIn, r.Session.SignOutAsync().GetAwaiter().GetResult());
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Session.SignOutAsync().GetAwaiter().GetResult();
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        r.Idle();
        int total = r.Total;
        r.Approve = true;
        r.Idle();
        Check.Equal(total, r.Total);
        Check.Null(r.S.CopiumToken);
    }

    [UnitTest]
    public static void Signing_out_while_the_account_is_read_leaves_no_signed_in_behind()
    {
        using var r = new Rig();
        r.HoldMe();
        r.Session.Start("PC");
        r.WaitWaiting();
        r.Approve = true;
        Check.True(r.MeEntered.Wait(Limit), "the account read started");
        Check.Equal(SignOutResult.Revoked, r.Session.SignOutAsync().GetAwaiter().GetResult());
        long signedOut = r.Session.Current.Version;
        r.Idle();
        Check.True(r.MeCancelled, "the account read was cancelled");
        Check.Equal(SignInStatus.SignedOut, r.Session.Current.Status);
        Check.Equal(signedOut, r.Session.Current.Version, "nothing came after the sign-out");
        lock (r.States) Check.False(r.States.Any(s => s.Version > signedOut), "no state after the sign-out");
        lock (r.States) Check.False(r.States.Any(s => s.Status == SignInStatus.SignedIn), "never SignedIn");
        Check.Null(r.S.CopiumToken);
        Check.Equal(1, r.Deletes, "the sign-out revoked the new token once");
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
    }

    // ------------------------------------------------------------------ the poll key stays secret

    [UnitTest]
    public static void The_poll_key_is_in_no_state_and_no_printed_form()
    {
        using var r = new Rig();
        SignInAndApprove(r, "PC");
        r.Session.SignOutAsync().GetAwaiter().GetResult();
        r.StartReply = _ => Json(HttpStatusCode.BadRequest, """{"error":"Unknown app"}""");
        r.Session.Start("PC");
        r.Idle();
        List<SignInState> all;
        lock (r.States) all = r.States.ToList();
        Check.True(all.Count >= 4);
        foreach (var s in all)
        {
            Check.False(s.ToString().Contains(PollKey) || s.ToString().Contains("SECRET"), s.ToString());
            Check.False(s.ToString().Contains(Token), "no token either");
            foreach (var p in typeof(SignInState).GetProperties())
                Check.False((p.GetValue(s)?.ToString() ?? "").Contains("SECRET"), p.Name);
        }
        Check.False(typeof(SignInState).GetProperties().Any(p => p.Name.Contains("Poll", StringComparison.OrdinalIgnoreCase)), "no poll key member");
        Check.False(typeof(SignInState).GetProperties().Any(p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) && p.Name != nameof(SignInState.HasToken)), "no token member");
    }

    // ------------------------------------------------------------------ the deadline edge (slice A carry-over)

    [UnitTest]
    public static void A_deadline_reached_between_the_checks_is_expired_not_a_hang_or_a_negative_wait()
    {
        using var r = new Rig();
        var pauses = new List<TimeSpan>();
        CopiumConnect.Delay = (t, ct) =>
        {
            if (t < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(t), "negative delay");
            pauses.Add(t);
            Thread.Sleep(t + TimeSpan.FromMilliseconds(150));   // the deadline passes during the pause
            return Task.CompletedTask;
        };
        var start = new ConnectStart(Code1, PollKey, new Uri("https://copium.pics/connect?code=" + Code1), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
        var ex = Check.Throws<ConnectException>(() => Task.Run(() => CopiumConnect.WaitAsync(start, CancellationToken.None)).GetAwaiter().GetResult());
        Check.Equal(ConnectFailure.Expired, ex.Failure);
        Check.True(pauses.Count >= 1 && pauses.All(p => p > TimeSpan.Zero && p <= TimeSpan.FromMilliseconds(250)), "the pause was clamped to what was left: " + string.Join(",", pauses));
        Check.Equal(0, r.Polls, "no poll after the deadline");

        // a deadline that is gone before the first check
        pauses.Clear();
        var gone = start with { ExpiresIn = TimeSpan.FromTicks(1) };
        var ex2 = Check.Throws<ConnectException>(() => Task.Run(() => CopiumConnect.WaitAsync(gone, CancellationToken.None)).GetAwaiter().GetResult());
        Check.Equal(ConnectFailure.Expired, ex2.Failure);
        Check.Equal(0, pauses.Count);
    }

    // ------------------------------------------------------------------ welcome

    private static JsonObject WithoutWelcomeKeys(Settings s)
    {
        var node = JsonNode.Parse(s.ToJson())!.AsObject();
        foreach (string k in new[] { nameof(Settings.CopiumConsent), nameof(Settings.ScreenshotUpload), nameof(Settings.CopiumWelcomeShown) }) node.Remove(k);
        return node;
    }

    [UnitTest]
    public static void The_welcome_box_starts_ticked_until_sharing_was_agreed_to()
    {
        var fresh = Settings.FromJson("{}");
        Check.False(fresh.ScreenshotUpload, "the setting itself stays off by default");
        Check.True(ConsentGate.WelcomeInitialAutoUpload(fresh), "D1: pre-ticked");
        var declined = Settings.FromJson("{}");
        declined.CopiumConsentDontAsk = true;
        Check.True(ConsentGate.WelcomeInitialAutoUpload(declined));
        var agreedOff = Settings.FromJson("{}");
        agreedOff.CopiumConsent = true;
        agreedOff.ScreenshotUpload = false;
        Check.False(ConsentGate.WelcomeInitialAutoUpload(agreedOff), "consent given: the user's own choice");
        agreedOff.ScreenshotUpload = true;
        Check.True(ConsentGate.WelcomeInitialAutoUpload(agreedOff));
    }

    [UnitTest]
    public static void Start_sharing_gives_consent_and_sets_the_auto_upload_as_the_box_says()
    {
        foreach (bool box in new[] { true, false })
        {
            var s = Settings.FromJson("{}");
            s.ScreenshotUpload = !box;   // whatever it was
            var before = WithoutWelcomeKeys(s).ToJsonString();
            ConsentGate.ApplyWelcome(s, startSharing: true, autoUpload: box);
            Check.True(s.CopiumConsent);
            Check.Equal(box, s.ScreenshotUpload);
            Check.True(s.CopiumWelcomeShown);
            Check.Equal(before, WithoutWelcomeKeys(s).ToJsonString(), "nothing else changed: no clip or video setting");
        }
    }

    [UnitTest]
    public static void Not_now_changes_no_consent_and_still_counts_the_welcome_as_shown()
    {
        var s = Settings.FromJson("{}");
        var before = WithoutWelcomeKeys(s).ToJsonString();
        ConsentGate.ApplyWelcome(s, startSharing: false, autoUpload: true);   // the box is ignored on Not now
        Check.False(s.CopiumConsent);
        Check.False(s.ScreenshotUpload);
        Check.True(s.CopiumWelcomeShown);
        Check.Equal(before, WithoutWelcomeKeys(s).ToJsonString());

        var agreed = Settings.FromJson("{}");
        agreed.CopiumConsent = true;
        agreed.ScreenshotUpload = true;
        ConsentGate.ApplyWelcome(agreed, startSharing: false, autoUpload: false);
        Check.True(agreed.CopiumConsent, "Not now never takes consent back");
        Check.True(agreed.ScreenshotUpload);
        Check.True(agreed.CopiumWelcomeShown);
    }

    // ------------------------------------------------------------------ the controller's wiring

    /// <summary>A controller whose UI thread is this test thread (its dispatcher runs only while the test pumps), with a mock copium.pics.</summary>
    private sealed class AppRig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Settings S;
        public readonly AppController App;
        public readonly List<Uri> Opened = new();
        public readonly List<SignInState> Shown = new();
        public readonly List<Req> Requests = new();
        public int Toasts, Welcomes, Completed;
        public volatile bool Approve, MeCancelled;
        public bool HoldMe;
        public readonly ManualResetEventSlim MeEntered = new();
        private readonly Func<TimeSpan, CancellationToken, Task> _oldDelay = CopiumConnect.Delay;

        public AppRig(Func<TempDir, Settings>? load = null, Action<AppController>? setup = null)
        {
            Settings.DirOverride = Dir.Path;
            S = load?.Invoke(Dir) ?? Settings.FromJson("{}");
            S.CopiumConsent = true;
            App = new AppController(S, new Updater(S));
            setup?.Invoke(App);
            App.SignInOpenerOverride = uri => { lock (Opened) Opened.Add(uri); };   // never a real browser in a test
            App.SignedInOverride = _ => Toasts++;                                  // never a real toast or key press
            App.SignInChanged += s => Shown.Add(s);
            App.CopiumWelcomeNeeded += () => Welcomes++;
            App.SignIn.Completed += _ => Interlocked.Increment(ref Completed);
            CopiumClient.HttpOverride = new HttpClient(new AsyncHandler(RespondAsync));
            CopiumConnect.Delay = (_, ct) => Task.Delay(10, ct);
        }

        public int Count(string method, string path) { lock (Requests) return Requests.Count(r => r.Method == method && r.Path == path); }
        public int Total { get { lock (Requests) return Requests.Count; } }
        public int OpenedCount { get { lock (Opened) return Opened.Count; } }

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string path = req.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add(new Req(req.Method.Method, path, req.Headers.Authorization?.Parameter, ""));
            switch (path)
            {
                case "/api/connect/start": return Json(HttpStatusCode.Created, StartJson(Code1));
                case "/api/connect/poll": return Approve ? Json(HttpStatusCode.OK, $$"""{"token":"{{Token}}","username":"Jason"}""") : Json(HttpStatusCode.Accepted, "{}");
                case "/api/me":
                    if (HoldMe)
                    {
                        MeEntered.Set();
                        try { await Task.Delay(Timeout.Infinite, ct); }
                        catch (OperationCanceledException) { MeCancelled = true; throw; }
                    }
                    return Json(HttpStatusCode.OK, MeJson());
                case "/api/connect/token": return new HttpResponseMessage(HttpStatusCode.NoContent);
                default: return Json(HttpStatusCode.NotFound, "{}");
            }
        }

        /// <summary>Waits without running the UI thread (so whatever the session hands to it stays queued).</summary>
        public static void Spin(Func<bool> done, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!done())
            {
                if (sw.ElapsedMilliseconds > Limit) Check.Fail("timed out waiting for " + what);
                Thread.Sleep(2);
            }
        }

        public static void Pump(Func<bool> done, string what) => ScreenshotUploadTests.Pump(done, what, Limit);

        /// <summary>Runs the UI thread until the session is idle, then until everything it queued at normal priority has run.</summary>
        public void Settle()
        {
            var idle = App.SignIn.WhenIdleAsync();
            Pump(() => idle.IsCompleted, "the sign-in to go idle (state " + App.SignIn.Current + ")");
            bool drained = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => drained = true));   // after every Normal item already queued
            Pump(() => drained, "the UI queue to drain");
        }

        public void Dispose()
        {
            try { App.SignIn.Shutdown(); Settle(); } catch { }
            App.Dispose();
            CopiumClient.HttpOverride = null;
            CopiumConnect.Delay = _oldDelay;
            Dir.Dispose();
        }
    }

    [UnitTest]
    public static void The_controller_welcome_answer_counts_not_now_as_one_nudge_and_saves()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        using var app = new AppController(settings, new Updater(settings));
        Check.True(app.WelcomeInitialAutoUpload);
        app.ApplyWelcome(startSharing: false, autoUpload: true);
        Check.False(settings.CopiumConsent);
        Check.True(settings.CopiumWelcomeShown);
        Check.Equal(1, settings.CopiumConsentPromptCount, "one proactive showing");
        var saved = Settings.FromJson(File.ReadAllText(Path.Combine(dir.Path, "settings.json")));
        Check.True(saved.CopiumWelcomeShown, "saved");

        var second = Settings.FromJson("{}");
        using var app2 = new AppController(second, new Updater(second));
        app2.ApplyWelcome(startSharing: true, autoUpload: true);
        Check.True(second.CopiumConsent);
        Check.True(second.ScreenshotUpload);
        Check.Equal(0, second.CopiumConsentPromptCount, "Start sharing is not a nudge");
    }

    [UnitTest]
    public static void The_controller_signs_in_on_the_ui_thread_and_raises_the_toast_and_welcome_once()
    {
        using var a = new AppRig();
        a.App.SignIn.Start("PC");
        AppRig.Pump(() => a.OpenedCount == 1, "the browser");
        a.Approve = true;
        a.Settle();
        var now = a.App.SignIn.Current;
        Check.Equal(SignInStatus.SignedIn, now.Status);
        Check.True(now.Verified);
        Check.Equal(Token, a.S.CopiumToken);
        Check.Equal(1, a.Completed);
        Check.Equal(1, a.Toasts, "the sign-in toast");
        Check.Equal(1, a.Welcomes, "the welcome");
        Check.Equal(now, a.Shown.Last(), "the UI ends on the current state");
        for (int i = 1; i < a.Shown.Count; i++) Check.True(a.Shown[i].Version > a.Shown[i - 1].Version, "never an older state after a newer one");
    }

    [UnitTest]
    public static void The_controller_shows_only_the_latest_state_and_opens_no_browser_after_a_cancel()
    {
        using var a = new AppRig();
        a.App.SignIn.Start("PC");
        AppRig.Spin(() => a.App.SignIn.Current.Status == SignInStatus.Waiting, "the Waiting state");   // the UI thread has not run yet
        a.App.SignIn.Cancel();
        a.Settle();
        Check.Equal(0, a.OpenedCount, "the opener ran on the UI thread after the cancel and found the sign-in gone");
        Check.Equal(0, a.Count("POST", "/api/connect/poll"));
        Check.True(a.Shown.Count >= 1);
        Check.Equal(SignInStatus.SignedOut, a.Shown.Last().Status, "the UI's last state is SignedOut, not a stuck code");
        Check.False(a.Shown.Any(s => s.Status == SignInStatus.Waiting), "the stale Waiting was coalesced away");
        Check.Equal(a.App.SignIn.Current, a.Shown.Last());
    }

    [UnitTest]
    public static void The_controller_kill_switch_during_the_account_read_stops_it_with_no_toast_or_welcome()
    {
        using var a = new AppRig();
        a.HoldMe = true;
        a.App.SignIn.Start("PC");
        AppRig.Pump(() => a.OpenedCount == 1, "the browser");
        a.Approve = true;
        AppRig.Pump(() => a.MeEntered.IsSet, "the account read");
        int sent = a.Total;
        Check.False(a.App.SetCloudSharing(false));
        a.Settle();
        Check.True(a.MeCancelled, "the /api/me request was cancelled");
        Check.Equal(sent, a.Total, "no further request");
        Check.Equal(0, a.Completed);
        Check.Equal(0, a.Toasts);
        Check.Equal(0, a.Welcomes);
        Check.Equal(Token, a.S.CopiumToken, "the token is kept");
        var now = a.App.SignIn.Current;
        Check.Equal(SignInStatus.SignedIn, now.Status);
        Check.True(now.LinkedViaConnect);
        Check.False(now.Verified, "unverified");
        Check.False(a.App.SignIn.IsRunning);
        Check.Equal(now, a.Shown.Last());

        a.App.SignIn.Start("PC");   // refused while the switch is off
        Check.Equal(SignInFailure.CloudSharingOff, a.App.SignIn.Current.Failure);
    }

    [UnitTest]
    public static void The_controller_kill_switch_and_a_hand_saved_token_stop_a_waiting_sign_in()
    {
        using var a = new AppRig();
        a.App.SignIn.Start("PC");
        AppRig.Pump(() => a.OpenedCount == 1, "the browser");
        Check.False(a.App.SetCloudSharing(false));   // the kill switch
        Check.Equal(SignInStatus.SignedOut, a.App.SignIn.Current.Status);
        a.App.SignIn.Start("PC");
        Check.Equal(SignInFailure.CloudSharingOff, a.App.SignIn.Current.Failure, "refused while the switch is off");
        a.Settle();

        a.S.CloudSharingEnabled = true;
        a.App.SignIn.Start("PC");
        AppRig.Pump(() => a.OpenedCount == 2, "the browser again");
        a.App.SaveManualCopiumToken(Manual);   // TokenSave_Click
        Check.Equal(SignInStatus.SignedOut, a.App.SignIn.Current.Status);
        Check.True(a.App.SignIn.Current.HasToken);
        a.Approve = true;
        a.Settle();
        Check.Equal(Manual, a.S.CopiumToken);
        Check.False(a.S.CopiumLinkedViaConnect);
        Check.Equal(0, a.Toasts);
        var saved = Settings.FromJson(File.ReadAllText(a.Dir.File("settings.json")));
        Check.Equal(Manual, saved.CopiumToken, "saved by the session");
    }

    [UnitTest]
    public static void An_import_restart_stops_the_sign_in_and_a_late_approval_is_revoked_not_stored()
    {
        using var root = new TempDir();
        string folder = root.File(Path.Combine("Packages", "Foo_abc", "LocalCache", "Roaming", "Clink"));
        Directory.CreateDirectory(folder);
        var theirs = Settings.FromJson("{}");
        theirs.ClickSound = false;
        theirs.Codec = "hevc";
        File.WriteAllText(Path.Combine(folder, "settings.json"), theirs.ToJson());
        int exits = 0, started = 0;
        using var a = new AppRig(
            load: dir =>
            {
                var ours = Settings.FromJson("{}");
                ours.ClickSound = false;   // a refused clip must not beep in the middle of a test run
                File.WriteAllText(dir.File("settings.json"), ours.ToJson());
                ClipLibrary.IndexPathOverride = dir.File("clips.json");
                SettingsImport.PackagesRoot = root.File("Packages");
                return Settings.Load();
            },
            setup: app =>
            {
                app.CommandLine = new[] { "--minimized", "--no-update" };
                app.ExitForRestart = () => exits++;
                app.StartProcess = _ => started++;
            });
        var hold = new Hold();
        a.App.SignIn.BeforeCommit = hold.Wait;
        a.App.SignIn.Start("PC");
        AppRig.Pump(() => a.OpenedCount == 1, "the browser");
        a.Approve = true;
        AppRig.Spin(() => a.Count("POST", "/api/connect/poll") >= 1, "the approving poll");
        hold.Reached();   // the token is read; the commit is not decided

        a.App.ImportForeignSettings(SettingsImport.FindForeignCopies(root.Path).Single());
        Check.Equal(1, started, "the new Clink was started");
        Check.Equal(1, exits);
        Check.False(a.App.SignIn.IsRunning, "the restart stopped the sign-in");

        hold.Release();
        a.Settle();
        Check.Null(a.S.CopiumToken, "not stored");
        Check.Equal(1, a.Count("DELETE", "/api/connect/token"), "revoked exactly once");
        Check.Equal(0, a.Count("GET", "/api/me"));
        Check.Equal(0, a.Completed);
        Check.Equal(0, a.Toasts);
    }

    [UnitTest]
    public static void A_queued_sign_in_event_acts_only_while_it_is_still_the_current_state()
    {
        var signedIn = new SignInState(SignInStatus.SignedIn, Version: 7, HasToken: true, LinkedViaConnect: true, Username: "Jason");
        Check.True(AppController.IsCurrentSignIn(signedIn, signedIn, cloudSharing: true));
        Check.False(AppController.IsCurrentSignIn(signedIn, signedIn, cloudSharing: false), "the kill switch went off");
        Check.False(AppController.IsCurrentSignIn(signedIn, signedIn with { Version = 8 }, cloudSharing: true), "something happened since (even another SignedIn)");
        Check.False(AppController.IsCurrentSignIn(signedIn, new SignInState(SignInStatus.SignedOut, Version: 8), cloudSharing: true), "signed out since");
        Check.False(AppController.IsCurrentSignIn(signedIn, signedIn with { Status = SignInStatus.Starting }, cloudSharing: true));
    }

    [UnitTest]
    public static void A_queued_sign_in_event_survives_a_cleanup_bump_of_the_same_sign_in_run()
    {
        var signedIn = new SignInState(SignInStatus.SignedIn, Version: 7, HasToken: true, LinkedViaConnect: true, Username: "Jason", RunId: 3);
        Check.True(AppController.IsCurrentSignIn(signedIn, signedIn with { Version = 9, RemoteCleanupName = "PC" }, cloudSharing: true), "a cleanup flag is not a new sign-in");
        Check.True(AppController.IsCurrentSignIn(signedIn, signedIn with { Version = 10, RemoteCleanupName = null }, cloudSharing: true), "nor is its dismissal");
        Check.False(AppController.IsCurrentSignIn(signedIn, signedIn with { Version = 9, RunId = 4 }, cloudSharing: true), "another run's SignedIn");
        Check.False(AppController.IsCurrentSignIn(signedIn, signedIn with { Version = 9, RunId = 0 }, cloudSharing: true), "a token saved by hand since");
        Check.False(AppController.IsCurrentSignIn(signedIn, signedIn with { Version = 9, Status = SignInStatus.SignedOut }, cloudSharing: true));
    }

    [UnitTest]
    public static void An_unrelated_version_bump_between_signed_in_and_the_event_does_not_drop_completed_or_the_welcome()
    {
        using var r = Linked(true);
        r.Delete = () => Json(HttpStatusCode.NotFound, "{}");
        Check.Equal(SignOutResult.NotSupportedDisconnectOnWebsite, r.Session.SignOutAsync().GetAwaiter().GetResult());
        Check.True(r.Session.Current.RemoteCleanupName != null, "a cleanup note is pending");
        bool bumped = false;
        r.Session.Changed += s =>
        {
            if (s.Status == SignInStatus.SignedIn && !bumped) { bumped = true; r.Session.DismissRemoteCleanup(); }   // lands between SignedIn and the event
        };
        SignInAndApprove(r);
        Check.True(bumped);
        Check.Equal(SignInStatus.SignedIn, r.Session.Current.Status);
        Check.True(r.Session.Current.RunId != 0);
        Check.Equal(1, r.CompletedCount);
        Check.Equal(1, r.Welcomes);
        r.Session.SaveManualToken(Manual);
        Check.Equal(0L, r.Session.Current.RunId, "a token by hand ends the run's state");
    }

    [UnitTest]
    public static void Saving_the_stored_connect_token_again_keeps_it_linked_and_revokes_nothing()
    {
        using var r = Linked(true);
        r.Session.SaveManualToken("  " + Token + " ");
        r.Idle();
        Check.Equal(Token, r.S.CopiumToken);
        Check.True(r.S.CopiumLinkedViaConnect);
        Check.Equal("Jason", r.S.CopiumAccountName);
        Check.Equal(0, r.Deletes);
        Check.Equal(SignInStatus.SignedIn, r.Session.Current.Status);
        r.Session.SaveManualToken(Manual);   // a different token still replaces and revokes it
        r.Idle();
        Check.False(r.S.CopiumLinkedViaConnect);
        Check.Equal(1, r.Deletes);
    }

    [UnitTest]
    public static void A_token_that_cannot_be_stored_is_revoked_and_the_sign_in_rests_signed_out_with_a_message()
    {
        using var r = new Rig();
        r.Session.AfterStore = () => r.S.CopiumTokenProtected = null;   // what Settings.Protect does when DPAPI fails
        SignInAndApprove(r);
        var now = r.Session.Current;
        Check.Equal(SignInStatus.Failed, now.Status);
        Check.Equal(SignInFailure.CouldNotSave, now.Failure);
        Check.Equal("Couldn't save the sign-in on this PC.", now.Message);
        Check.False(now.HasToken);
        Check.Null(r.S.CopiumToken);
        Check.Null(r.S.CopiumAccountName);
        Check.False(r.S.CopiumLinkedViaConnect, "no partial flag");
        Check.Equal(1, r.Deletes, "revoked once");
        Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer);
        Check.Equal(0, r.Count("GET", "/api/me"));
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);
        Check.False(r.Session.IsRunning);
    }

    [UnitTest]
    public static void A_pasted_token_survives_a_failed_write_of_the_new_token()
    {
        using var r = new Rig();
        r.S.SetCopiumToken(Manual);   // a pasted token: no name, not via Connect
        string? before = r.S.CopiumTokenProtected;
        r.Session.AfterStore = () => r.S.CopiumTokenProtected = null;   // Settings.Protect gave null
        SignInAndApprove(r);
        Check.Equal(SignInFailure.CouldNotSave, r.Session.Current.Failure);
        Check.Equal(Manual, r.S.CopiumToken, "the pasted token is still there");
        Check.Equal(before, r.S.CopiumTokenProtected);
        Check.Null(r.S.CopiumAccountName);
        Check.False(r.S.CopiumLinkedViaConnect);
        Check.True(r.Session.Current.HasToken);
        Check.Equal(1, r.Deletes, "the new token was revoked once");
        Check.Equal(Token, r.Last("DELETE", "/api/connect/token").Bearer);
    }

    [UnitTest]
    public static void The_account_check_note_is_for_a_failed_check_only()
    {
        using (var r = new Rig())
        {
            r.S.SetCopiumToken(Token, "Jason", viaConnect: true);   // signed in earlier, nothing checked in this process
            var rest = new CopiumSignInSession(r.S, _ => { }, () => { });
            Check.False(rest.Current.Verified);
            Check.False(rest.Current.AccountCheckFailed, "not checked yet is not a failure");
            rest.SaveManualToken(Token);   // the same token again
            Check.False(rest.Current.AccountCheckFailed);
            Check.False(rest.Current.Verified);
        }
        using (var r = new Rig())
        {
            r.Me = () => Json(HttpStatusCode.InternalServerError, "{}");
            SignInAndApprove(r);
            Check.True(r.Session.Current.AccountCheckFailed);
            r.Session.SaveManualToken(Token);   // same token: nothing new was checked, nothing new failed
            Check.True(r.S.CopiumLinkedViaConnect);
            r.Session.SaveManualToken(Manual);   // another token: the failure was about the old one
            r.Idle();
            Check.False(r.Session.Current.AccountCheckFailed);
        }
        using (var r = new Rig())
        {
            SignInAndApprove(r);
            Check.True(r.Session.Current.Verified);
            Check.False(r.Session.Current.AccountCheckFailed);
        }
    }

    [UnitTest]
    public static void A_token_that_cannot_be_stored_and_cannot_be_revoked_asks_for_cleanup()
    {
        using var r = new Rig();
        r.Delete = () => Json(HttpStatusCode.InternalServerError, "{}");
        r.Session.AfterStore = () => r.S.CopiumTokenProtected = null;
        SignInAndApprove(r, "Gaming rig");
        Check.Equal(SignInFailure.CouldNotSave, r.Session.Current.Failure);
        Check.Equal("Gaming rig", r.Session.Current.RemoteCleanupName);
        Check.Equal(1, r.Cleanups.Count);
    }

    [UnitTest]
    public static void The_consent_pop_up_offers_the_auto_upload_box_for_screenshot_contexts_only()
    {
        Check.True(ConsentGate.OffersAutoUpload(ConsentContext.ScreenshotUploadManual));
        Check.True(ConsentGate.OffersAutoUpload(ConsentContext.Proactive));
        Check.False(ConsentGate.OffersAutoUpload(ConsentContext.ClipShare));
        Check.False(ConsentGate.OffersAutoUpload(ConsentContext.TokenSaved));
        Check.False(Enum.GetNames<ConsentContext>().Contains("Linked"), "the unused Linked context is gone (the welcome applies consent itself)");
    }

    // ------------------------------------------------------------------ the startup account refresh (slice D)

    [UnitTest]
    public static void The_startup_refresh_updates_name_plus_and_verified_and_keeps_the_token()
    {
        using var r = Linked(viaConnect: true);
        r.S.CopiumPlus = false;
        r.Me = () => Json(HttpStatusCode.OK, MeJson(plus: true, name: "Renamed"));
        Check.False(r.Session.Current.Verified);
        Check.True(r.Session.RefreshAccountAsync(CancellationToken.None).Wait(Limit));
        Check.Equal(1, r.Count("GET", "/api/me"));
        Check.Equal(Token, r.Last("GET", "/api/me").Bearer);
        Check.Equal("Renamed", r.S.CopiumAccountName);
        Check.True(r.S.CopiumPlus);
        Check.True(r.Session.Current.Verified);
        Check.False(r.Session.Current.AccountCheckFailed);
        Check.Equal("Renamed", r.Session.Current.Username);
        Check.Equal(Token, r.S.CopiumToken);
        Check.True(r.S.CopiumLinkedViaConnect);
        Check.True(r.Saves > 0, "saved");
    }

    [UnitTest]
    public static void A_failed_startup_refresh_only_sets_the_check_note_and_never_touches_the_token()
    {
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests, HttpStatusCode.OK })
        {
            using var r = Linked(viaConnect: true);
            r.S.CopiumPlus = true;
            r.Me = () => Json(code, code == HttpStatusCode.OK ? "not json" : "{}");
            string? protectedBefore = r.S.CopiumTokenProtected;
            Check.False(r.Session.RefreshAccountAsync(CancellationToken.None).Result, code.ToString());
            Check.True(r.Session.Current.AccountCheckFailed, code + ": the note");
            Check.False(r.Session.Current.Verified);
            Check.Equal(Token, r.S.CopiumToken, code + ": the token stays");
            Check.Equal(protectedBefore, r.S.CopiumTokenProtected);
            Check.True(r.S.CopiumLinkedViaConnect);
            Check.Equal("Jason", r.S.CopiumAccountName);
            Check.True(r.S.CopiumPlus, "Copium+ unchanged on a failure");
            Check.Equal(0, r.Deletes, "nothing is revoked");
        }
        using var net = Linked(viaConnect: true);
        net.Me = () => throw new HttpRequestException("offline");
        Check.False(net.Session.RefreshAccountAsync(CancellationToken.None).Result);
        Check.True(net.Session.Current.AccountCheckFailed);
        Check.Equal(Token, net.S.CopiumToken);
    }

    [UnitTest]
    public static void The_startup_refresh_sends_nothing_without_a_connect_token_or_with_sharing_off_or_in_a_dev_instance()
    {
        using (var pasted = Linked(viaConnect: false))
        {
            Check.False(pasted.Session.RefreshAccountAsync(CancellationToken.None).Result);
            Check.Equal(0, pasted.Total, "a pasted token is not refreshed");
        }
        using (var none = new Rig())
        {
            Check.False(none.Session.RefreshAccountAsync(CancellationToken.None).Result);
            Check.Equal(0, none.Total);
        }
        using (var off = Linked(viaConnect: true))
        {
            off.S.CloudSharingEnabled = false;
            Check.False(off.Session.RefreshAccountAsync(CancellationToken.None).Result);
            Check.Equal(0, off.Total, "no request when sharing is off");
        }
        using (var dev = Linked(viaConnect: true))
        {
            dev.Session.IsDevInstance = () => true;
            dev.Session.DevKeepsToken = () => false;
            Check.False(dev.Session.RefreshAccountAsync(CancellationToken.None).Result);
            Check.Equal(0, dev.Total, "a dev instance without --dev-token");
            dev.Session.DevKeepsToken = () => true;
            Check.True(dev.Session.RefreshAccountAsync(CancellationToken.None).Result);
            Check.Equal(1, dev.Total, "with --dev-token it reads");
        }
    }

    [UnitTest]
    public static void The_startup_refresh_is_cancelled_by_the_kill_switch_exit_and_sign_out_without_changing_anything()
    {
        foreach (int how in new[] { 0, 1, 2 })
        {
            using var r = Linked(viaConnect: true);
            r.S.CopiumPlus = false;
            r.HoldMe();
            var task = Task.Run(() => r.Session.RefreshAccountAsync(CancellationToken.None));   // (as in production: off the test thread's context)
            r.WaitFor(() => r.MeEntered.IsSet, "the /api/me request");
            if (how == 0) { r.S.CloudSharingEnabled = false; r.Session.StopForCloudSharingOff(); }
            else if (how == 1) r.Session.Shutdown();
            else r.Session.SignOutAsync().Wait(Limit);
            Check.True(task.Wait(Limit), "the refresh ended");
            Check.False(task.Result);
            Check.True(r.MeCancelled, "the request was cancelled");
            Check.False(r.S.CopiumPlus, "nothing from the late answer was applied");
            Check.False(r.Session.Current.AccountCheckFailed, "a cancel is not a failed check");
            if (how != 2) Check.Equal(Token, r.S.CopiumToken, "the token stays");
        }
        using var caller = Linked(viaConnect: true);
        caller.HoldMe();
        using var cts = new CancellationTokenSource();
        var t = Task.Run(() => caller.Session.RefreshAccountAsync(cts.Token));
        caller.WaitFor(() => caller.MeEntered.IsSet, "the /api/me request");
        cts.Cancel();
        Check.True(t.Wait(Limit));
        Check.False(t.Result);
        Check.False(caller.Session.Current.AccountCheckFailed);
    }

    [UnitTest]
    public static void A_startup_refresh_answer_for_a_token_replaced_meanwhile_is_dropped()
    {
        using var r = Linked(viaConnect: true);
        r.HoldMe();
        var task = Task.Run(() => r.Session.RefreshAccountAsync(CancellationToken.None));   // (as in production: off the test thread's context)
        r.WaitFor(() => r.MeEntered.IsSet, "the /api/me request");
        r.Session.SaveManualToken(Manual);
        Check.True(task.Wait(Limit));
        Check.False(task.Result);
        Check.Equal(Manual, r.S.CopiumToken);
        Check.False(r.S.CopiumLinkedViaConnect);
        Check.False(r.Session.Current.Verified);
    }

    [UnitTest]
    public static void The_controller_schedules_the_startup_refresh_only_for_a_connect_token_with_sharing_on()
    {
        Settings Prepared(Action<Settings> tweak) { var s = Settings.FromJson("{}"); s.SetCopiumToken(Token, "Old", viaConnect: true); tweak(s); return s; }

        using (var a = new AppRig(load: _ => Prepared(s => s.CopiumPlus = false), setup: app => app.StartupAccountRefreshDelay = TimeSpan.FromMilliseconds(20)))
        {
            a.App.ScheduleAccountRefresh();
            Check.NotNull(a.App.StartupAccountRefresh);
            var task = a.App.StartupAccountRefresh!;
            AppRig.Pump(() => task.IsCompleted, "the startup refresh");
            a.Settle();
            Check.Equal(1, a.Count("GET", "/api/me"));
            Check.Equal("Jason", a.S.CopiumAccountName);
            Check.True(a.S.CopiumPlus);
            Check.True(a.App.SignIn.Current.Verified);
            a.App.ScheduleAccountRefresh();
            Check.True(ReferenceEquals(task, a.App.StartupAccountRefresh), "once per launch");
        }
        using (var off = new AppRig(load: _ => Prepared(s => s.CloudSharingEnabled = false)))
        {
            off.App.ScheduleAccountRefresh();
            Check.Null(off.App.StartupAccountRefresh);
            Check.Equal(0, off.Total);
        }
        using (var pasted = new AppRig(load: _ => { var s = Settings.FromJson("{}"); s.SetCopiumToken(Manual); return s; }))
        {
            pasted.App.ScheduleAccountRefresh();
            Check.Null(pasted.App.StartupAccountRefresh);
        }
        using (var exiting = new AppRig(load: _ => Prepared(_ => { }), setup: app => app.StartupAccountRefreshDelay = TimeSpan.FromMinutes(5)))
        {
            exiting.App.ScheduleAccountRefresh();
            var task = exiting.App.StartupAccountRefresh!;
            exiting.App.Dispose();   // exit during the wait: cancelled, no request
            AppRig.Pump(() => task.IsCompleted, "the refresh to end at exit");
            Check.Equal(0, exiting.Total);
        }
    }

    [UnitTest]
    public static void A_refresh_raises_no_completed_or_welcome_and_keeps_a_shown_failure()
    {
        using var r = Linked(viaConnect: true);
        r.Session.Start("PC");   // signed in via Connect already: refused, shown as Failed
        var failed = r.Session.Current;
        Check.Equal(SignInStatus.Failed, failed.Status);
        Check.True(failed.Failure != null);
        r.Idle();
        Check.True(r.Session.RefreshAccountAsync(CancellationToken.None).Wait(Limit));
        var after = r.Session.Current;
        Check.Equal(SignInStatus.Failed, after.Status);
        Check.Equal(failed.Failure, after.Failure);
        Check.Equal(failed.Message, after.Message);
        Check.True(after.Verified);
        Check.Equal(0, r.CompletedCount);
        Check.Equal(0, r.Welcomes);

        using var plain = Linked(viaConnect: true);
        Check.True(plain.Session.RefreshAccountAsync(CancellationToken.None).Wait(Limit));
        Check.Equal(SignInStatus.SignedIn, plain.Session.Current.Status);
        Check.Equal(0, plain.CompletedCount, "a refresh is not a sign-in");
        Check.Equal(0, plain.Welcomes);
    }

    [UnitTest]
    public static void A_refresh_that_runs_over_its_limit_is_a_failed_check_and_keeps_the_token()
    {
        using var r = Linked(viaConnect: true);
        r.Session.RefreshLimitOverride = TimeSpan.FromMilliseconds(80);
        r.HoldMe();
        r.S.CopiumPlus = true;
        string? protectedBefore = r.S.CopiumTokenProtected;
        var task = Task.Run(() => r.Session.RefreshAccountAsync(CancellationToken.None));
        Check.True(task.Wait(Limit), "the refresh ended by its own limit");
        Check.False(task.Result);
        Check.True(r.MeCancelled, "the request was cancelled");
        Check.True(r.Session.Current.AccountCheckFailed, "our own limit is a failed check");
        Check.Equal(Token, r.S.CopiumToken);
        Check.Equal(protectedBefore, r.S.CopiumTokenProtected);
        Check.True(r.S.CopiumLinkedViaConnect);
        Check.True(r.S.CopiumPlus);
        Check.Equal(0, r.Deletes, "nothing is revoked");
    }

    [UnitTest]
    public static void A_refresh_sends_nothing_while_a_sign_in_run_is_active()
    {
        using var r = Linked(viaConnect: false);
        r.Session.Start("PC");   // a pasted token does not block a sign-in
        r.WaitWaiting();
        r.S.CopiumLinkedViaConnect = true;   // the other gates now pass: only the active run holds the refresh back
        int before = r.Count("GET", "/api/me");
        Check.False(r.Session.RefreshAccountAsync(CancellationToken.None).Result);
        Check.Equal(before, r.Count("GET", "/api/me"), "no /api/me request during a run");
        r.Session.Cancel();
        r.Idle();
    }

    private static Settings LinkedSettings() { var s = Settings.FromJson("{}"); s.SetCopiumToken(Token, "Old", viaConnect: true); return s; }

    [UnitTest]
    public static void The_account_is_read_again_every_day_and_not_with_sharing_off_or_after_exit()
    {
        using var gate = new SemaphoreSlim(0);
        int calls = 0;
        using var a = new AppRig(load: _ => LinkedSettings(), setup: app =>
        {
            app.StartupAccountRefreshDelay = TimeSpan.Zero;
            app.RefreshDelay = async (_, ct) => { if (Interlocked.Increment(ref calls) > 1) await gate.WaitAsync(ct); };   // the first wait is the startup one
        });
        a.App.ScheduleAccountRefresh();
        var startup = a.App.StartupAccountRefresh!;
        AppRig.Pump(() => startup.IsCompleted, "the startup refresh");
        Check.Equal(1, a.Count("GET", "/api/me"));
        Check.Equal(0, Volatile.Read(ref a.App.DailyRefreshes));
        gate.Release();
        AppRig.Pump(() => a.Count("GET", "/api/me") == 2, "the first daily refresh");
        gate.Release();
        AppRig.Pump(() => a.Count("GET", "/api/me") == 3, "the second daily refresh");
        a.S.CloudSharingEnabled = false;   // the kill switch: the next turn sends nothing
        gate.Release();
        AppRig.Pump(() => Volatile.Read(ref a.App.DailyRefreshes) == 3, "the turn with sharing off");
        Check.Equal(3, a.Count("GET", "/api/me"), "nothing is sent with sharing off");
        var loop = a.App.DailyAccountRefresh!;
        a.App.Dispose();   // exit: the loop ends and no further request goes out
        AppRig.Pump(() => loop.IsCompleted, "the daily loop to end at exit");
        gate.Release();
        Check.Equal(3, a.Count("GET", "/api/me"));
    }

    [UnitTest]
    public static void The_daily_loop_exists_without_a_token_and_serves_a_later_sign_in()
    {
        using var gate = new SemaphoreSlim(0);
        using var a = new AppRig(load: _ => Settings.FromJson("{}"), setup: app =>
        {
            app.StartupAccountRefreshDelay = TimeSpan.Zero;
            app.RefreshDelay = async (_, ct) => await gate.WaitAsync(ct);
        });
        a.App.ScheduleAccountRefresh();
        Check.Null(a.App.StartupAccountRefresh);   // the one-shot keeps its launch gate
        var loop = a.App.DailyAccountRefresh;
        Check.NotNull(loop);
        gate.Release();
        AppRig.Pump(() => Volatile.Read(ref a.App.DailyRefreshes) == 1, "the turn without a token");
        Check.Equal(0, a.Total);
        a.S.SetCopiumToken(Token, "Old", viaConnect: true);   // signed in after launch
        gate.Release();
        AppRig.Pump(() => a.Count("GET", "/api/me") == 1, "the turn after the sign-in");
        Check.Equal(1, a.Count("GET", "/api/me"));
        a.S.CloudSharingEnabled = false;
        gate.Release();
        AppRig.Pump(() => Volatile.Read(ref a.App.DailyRefreshes) == 3, "the turn with sharing off");
        Check.Equal(1, a.Count("GET", "/api/me"), "nothing is sent with sharing off");
        a.App.Dispose();
        AppRig.Pump(() => loop!.IsCompleted, "the daily loop to end at exit");
        gate.Release();
        Check.Equal(1, a.Count("GET", "/api/me"));
    }

    [UnitTest]
    public static void A_too_large_answer_triggers_one_refresh_per_ten_minutes()
    {
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        using var a = new AppRig(load: _ => LinkedSettings(), setup: app => app.UtcNowForRefresh = () => now);
        void Fire() { a.App.OnUploadTooLarge(); a.Settle(); }
        Fire();
        var first = a.App.TooLargeRefresh;
        Check.NotNull(first);
        AppRig.Pump(() => first!.IsCompleted, "the refresh after a 413");
        Check.Equal(1, a.Count("GET", "/api/me"));
        Check.Equal("Jason", a.S.CopiumAccountName);
        now = now.AddMinutes(9);
        Fire(); Fire();
        Check.True(ReferenceEquals(first, a.App.TooLargeRefresh), "inside the window: no second refresh");
        Check.Equal(1, a.Count("GET", "/api/me"));
        now = now.AddMinutes(2);
        Fire();
        var second = a.App.TooLargeRefresh!;
        Check.False(ReferenceEquals(first, second));
        AppRig.Pump(() => second.IsCompleted, "the next refresh");
        Check.Equal(2, a.Count("GET", "/api/me"));
        Check.Equal(Token, a.S.CopiumToken);
    }

    [UnitTest]
    public static void The_client_raises_the_too_large_event_for_a_413_only_and_the_upload_still_fails()
    {
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "shot.png");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        int raised = 0;
        Action handler = () => Interlocked.Increment(ref raised);
        var old = CopiumClient.HttpOverride;
        CopiumClient.UploadTooLarge += handler;
        try
        {
            // (only the 413 counts: the total stays 1 after the other two)
            foreach (var (code, expected) in new[] { (HttpStatusCode.RequestEntityTooLarge, 1), (HttpStatusCode.TooManyRequests, 1), (HttpStatusCode.InternalServerError, 1) })
            {
                CopiumClient.HttpOverride = new HttpClient(new FuncHandler(_ => Json(code, "{}")));
                var t = CopiumClient.UploadAsync(Token, file, "t", null, CancellationToken.None);
                Check.True(((IAsyncResult)t).AsyncWaitHandle.WaitOne(Limit));
                Check.True(t.IsFaulted, code + " fails the upload as before");
                Check.Equal(expected, raised, code.ToString());
            }
        }
        finally { CopiumClient.UploadTooLarge -= handler; CopiumClient.HttpOverride = old; }
    }

    [UnitTest]
    public static void An_upload_names_clink_in_the_client_field_and_the_user_agent()
    {
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "shot.png");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        string? body = null, agent = null;
        var handler = new FuncHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            agent = req.Headers.UserAgent.ToString();
            return Json(HttpStatusCode.OK, """{"id":"a1","link":"https://copium.pics/a1","url":"https://copium.pics/a1.png"}""");
        });
        var old = CopiumClient.HttpOverride;
        CopiumClient.HttpOverride = new HttpClient(handler);
        try { CopiumClient.UploadAsync(Token, file, "t", null, CancellationToken.None).Wait(Limit); }
        finally { CopiumClient.HttpOverride = old; }
        Check.Contains(body!, "name=client");
        Check.Contains(body!, "\r\n\r\nclink\r\n");
        Check.Contains(body!, "name=title");
        Check.True(System.Text.RegularExpressions.Regex.IsMatch(agent!, @"^Clink/\d+\.\d+\.\d+$"), "User-Agent: " + agent);
        Check.Equal("Clink/" + CopiumConnect.AppVersion(), agent);
    }

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> f) { _f = f; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_f(request));
    }

    private sealed class AsyncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _f;
        public AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) { _f = f; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _f(request, ct);
    }
}
