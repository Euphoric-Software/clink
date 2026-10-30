using System.Diagnostics;

namespace Clink.Services;

internal enum SignInStatus
{
    /// <summary>No connected account (a hand-pasted token is not a "sign-in": <see cref="SignInState.HasToken"/> says whether one is stored).</summary>
    SignedOut,
    /// <summary>Asking Copium for a code, or finishing after the approval (reading the account).</summary>
    Starting,
    /// <summary>The code is up and the browser was opened; polling for the approval.</summary>
    Waiting,
    SignedIn,
    Failed,
}

/// <summary>Why a sign-in ended in <see cref="SignInStatus.Failed"/>. The first six are a <see cref="ConnectFailure"/> from <see cref="CopiumConnect"/>.</summary>
internal enum SignInFailure
{
    /// <summary>An older Copium: fall back to the token box.</summary>
    Unsupported,
    RateLimited,
    Expired,
    Denied,
    Network,
    Unexpected,
    /// <summary>The cloud kill switch is off: nothing is sent to Copium.</summary>
    CloudSharingOff,
    /// <summary>A --dev instance without --dev-token drops its token at the next start, so signing in there would be pointless.</summary>
    DevInstanceNeedsToken,
    /// <summary>
    /// Already signed in with "Sign in with Copium": sign out first. (Copium deletes the old same-name app token when a new one is approved, so a
    /// second sign-in would leave this PC holding a dead token.) A pasted token does not refuse.
    /// </summary>
    AlreadySignedIn,
    /// <summary>The approval came, but the token could not be stored on this PC (DPAPI): it was revoked again and nothing is signed in.</summary>
    CouldNotSave,
}

/// <summary>
/// What the sign-in is doing right now. Never holds the poll key or the token.
/// <list type="bullet">
/// <item><see cref="Version"/>: increases with every new state (assigned under the session's lock), so a listener can tell the latest from a stale one.</item>
/// <item><see cref="Code"/> / <see cref="ApproveUrl"/> only in <see cref="SignInStatus.Waiting"/>; <see cref="Retrying"/> while polling backs off because Copium cannot be reached.</item>
/// <item>The resting account, in every status (so a Failed state still says who is signed in): <see cref="HasToken"/> (a token is stored, pasted or not,
/// decrypted at snapshot time), <see cref="LinkedViaConnect"/> (that token came from signing in), and for a linked token <see cref="Username"/>,
/// <see cref="Plus"/> and <see cref="Verified"/> (/api/me confirmed it in this process; false = kept but not checked).</item>
/// <item><see cref="Failure"/> / <see cref="Message"/> in <see cref="SignInStatus.Failed"/>.</item>
/// <item><see cref="RemoteCleanupName"/>: a Connect token of this app could not be revoked on Copium (offline, an error, an older Copium, a token scope
/// without the sign-out route): the user removes the connection of this name on copium.pics &gt; ShareX &amp; API. Stays until <see cref="CopiumSignInSession.DismissRemoteCleanup"/>.</item>
/// </list>
/// </summary>
internal sealed record SignInState(
    SignInStatus Status,
    long Version = 0,
    string? Code = null,
    Uri? ApproveUrl = null,
    bool Retrying = false,
    bool HasToken = false,
    bool LinkedViaConnect = false,
    string? Username = null,
    bool Plus = false,
    bool Verified = false,
    SignInFailure? Failure = null,
    string? Message = null,
    string? RemoteCleanupName = null,
    bool RemoteCleanupGuess = false,
    long RunId = 0,
    bool AccountCheckFailed = false)
{
    // RemoteCleanupGuess: RemoteCleanupName is the default token name, not one this PC knows it used ("probably named ...").
    // AccountCheckFailed: an /api/me check of the stored Connect token failed in this process (not merely "not checked yet"); cleared when the token changes, is checked, or sharing stops.
    // RunId: set on the SignedIn state a sign-in run ends with and kept by later changes that do not end the state (a cleanup flag or its dismissal),
    // so a queued Completed / WelcomeNeeded can tell "still this sign-in" without comparing Version. 0 = not from a run.
}

internal enum SignOutResult
{
    /// <summary>Copium dropped the token (or had dropped it already).</summary>
    Revoked,
    /// <summary>This Copium cannot revoke: the user removes the app on copium.pics.</summary>
    NotSupportedDisconnectOnWebsite,
    /// <summary>The revoke did not work (offline, error, 10 s, a token scope without the route); the token is gone from this PC anyway.</summary>
    FailedButSignedOutLocally,
    /// <summary>The token was pasted by hand: nothing was asked of Copium; the user can remove it on copium.pics -> ShareX &amp; API.</summary>
    Manual,
    /// <summary>There was no token.</summary>
    NotSignedIn,
}

/// <summary>
/// "Sign in with Copium" for one running Clink: the state machine around <see cref="CopiumConnect"/>. No WPF: the UI thread is reached only
/// through the injected <c>onUi</c> runner.
/// <list type="bullet">
/// <item>The code, poll key and approve address come only from a <see cref="CopiumConnect.StartAsync"/> this object started in <see cref="Start"/>,
/// which only a user click calls. The poll key lives in a local of the running task and nowhere else.</item>
/// <item>The browser is opened only with a validated https://copium.pics/connect address, on the UI thread, and only while that sign-in is still current.</item>
/// <item>A token is stored only when nothing took the sign-in first (cancel, kill switch, a hand-saved token, exit / restart); otherwise it is revoked again.</item>
/// <item>A failed /api/me never removes a token; anything that goes wrong after the token is stored rests as SignedIn (unverified).</item>
/// <item>With cloud sharing off nothing is sent, with one intended exception: revoking this app's own Connect token (an approval nobody stored,
/// a sign-out, a Connect token replaced by hand). It only deletes a token on the account, which protects the account.</item>
/// </list>
/// <para><b>Threading contract.</b></para>
/// <list type="number">
/// <item>The public operations (<see cref="Start"/>, <see cref="Cancel"/>, <see cref="SaveManualToken"/>, <see cref="StopForCloudSharingOff"/>,
/// <see cref="SignOutAsync"/>, <see cref="Shutdown"/>, <see cref="OpenApprovePageAgain"/>, <see cref="DismissRemoteCleanup"/>) are called on the UI
/// thread in production (button clicks, the kill switch, exit / restart). The work in between (HTTP, polling) runs on pool threads.</item>
/// <item>Every write of the token settings (<see cref="Settings.SetCopiumToken"/>, the account name, <see cref="Settings.CopiumPlus"/>) happens on the
/// UI thread: the operations above run there, and the background work hands its two writes (the commit after the approval, the account after
/// /api/me) to <c>onUi</c> and awaits them asynchronously. So those writes are serialised with the token box, the kill switch and Settings.Save,
/// which also run there, and the three token fields are never torn.</item>
/// <item><c>_gate</c> guards the session's own fields (the current run, the state and its version, the approve address, verified, the cleanup name).
/// It is held only for short sections that never wait: never across an await, never while calling <c>onUi</c>, the opener, save, an event
/// or <see cref="CancellationTokenSource.Cancel()"/>. Token writes are also made under it, so the commit and a competing operation are serialised
/// even where <c>onUi</c> runs inline (tests).</item>
/// <item>No deadlock: the worker never holds <c>_gate</c> while it waits for the UI (it awaits the <c>onUi</c> task with no lock), and the UI thread
/// never waits for the worker (it takes <c>_gate</c> only briefly and never blocks on a session task). Cancelling a run may run the run's
/// continuation inline on the caller's thread; that is why Cancel is always called after leaving the lock, and why the production <c>onUi</c>
/// runs an action inline when it is already on the UI thread.</item>
/// <item>Events are raised on whatever thread made the change, after the lock is released. A listener that shows them should read
/// <see cref="Current"/> at the time it runs (the latest state) rather than trust the order its calls arrive in; <see cref="Completed"/> and
/// <see cref="WelcomeNeeded"/> carry the state they belong to, so a listener can check it is still current (same <see cref="SignInState.Version"/>).</item>
/// </list>
/// </summary>
internal sealed class CopiumSignInSession
{
    private static readonly TimeSpan RevokeLimit = TimeSpan.FromSeconds(10);

    private sealed class Run
    {
        public readonly CancellationTokenSource Cts = new();
        /// <summary>The token name this sign-in asked Copium for (what the user removes on the website when a revoke did not work).</summary>
        public readonly string Name;
        /// <summary>The token is stored: a user Cancel no longer undoes it (sign out, the kill switch, a hand-saved token and exit still stop the run).</summary>
        public bool Committed;
        public readonly long Id;
        public Run(string name, long id) { Name = name; Id = id; }
    }

    private readonly Settings _settings;
    private readonly Action<Uri> _openBrowser;
    private readonly Action _save;
    private readonly Func<Action, Task> _onUi;
    private readonly object _gate = new();
    private Run? _run;
    private SignInState _state;
    private long _version;
    private Uri? _approve;      // the validated address of the current Waiting state
    private bool _verified;     // /api/me confirmed the stored token in this process
    private bool _checkFailed;  // an /api/me check actually failed in this process (never set by "not checked yet")
    private string? _cleanup;   // a Connect token name to remove on copium.pics by hand
    private bool _cleanupGuess; // ... and that name is the default, not one this PC used
    private long _runIds;
    private readonly List<Task> _work = new();

    /// <summary>Test seam: is this a --dev instance? (default <see cref="AppController.IsDev"/>).</summary>
    internal Func<bool> IsDevInstance { get; set; } = () => AppController.IsDev;
    /// <summary>Test seam: does this --dev instance keep its token (<see cref="AppController.DevToken"/>)?</summary>
    internal Func<bool> DevKeepsToken { get; set; } = () => AppController.DevToken;
    /// <summary>Test seam: awaited after the approval was read and before the commit decision (so a test can land an operation exactly there).</summary>
    internal Func<Task>? BeforeCommit { get; set; }
    /// <summary>Test seam: runs right after the commit stored the token (under the lock, UI thread); a test can simulate a failed DPAPI write here.</summary>
    internal Action? AfterStore { get; set; }

    /// <param name="openBrowser">Opens a validated approve address (production: <see cref="OpenInBrowser"/>). Called from <paramref name="onUi"/>.</param>
    /// <param name="save">Writes the settings file (production: <c>settings.Save</c>).</param>
    /// <param name="onUi">Runs an action on the UI thread; the task ends when it has run (faulted / cancelled when it threw or could not run).
    /// Null: run it inline (tests).</param>
    public CopiumSignInSession(Settings settings, Action<Uri> openBrowser, Action save, Func<Action, Task>? onUi = null)
    {
        _settings = settings;
        _openBrowser = openBrowser;
        _save = save;
        _onUi = onUi ?? Inline;
        _state = RestingLocked();
    }

    private static Task Inline(Action action)
    {
        try { action(); return Task.CompletedTask; }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    /// <summary>Every state change (the new state). Any thread; read <see cref="Current"/> when showing it.</summary>
    public event Action<SignInState>? Changed;
    /// <summary>A sign-in finished and the token is stored (after the /api/me check, verified or not). Once per sign-in, with the SignedIn state it produced. Any thread.</summary>
    public event Action<SignInState>? Completed;
    /// <summary>After <see cref="Completed"/>, when <see cref="Settings.CopiumWelcomeShown"/> is still false: the UI shows the welcome. Same state. Any thread.</summary>
    public event Action<SignInState>? WelcomeNeeded;
    /// <summary>A Connect token of this app could not be revoked: the name to remove on copium.pics &gt; ShareX &amp; API (also in <see cref="SignInState.RemoteCleanupName"/>). Any thread.</summary>
    public event Action<string>? RemoteCleanupNeeded;

    public SignInState Current { get { lock (_gate) return _state; } }

    /// <summary>A sign-in is running (Starting or Waiting, or finishing after the approval).</summary>
    public bool IsRunning { get { lock (_gate) return _run != null; } }

    /// <summary>Test seam: completes when every sign-in run and background revoke started so far (and any they started) has finished.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_work) pending = _work.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }
    }

    private void Track(Task task)
    {
        lock (_work)
        {
            _work.RemoveAll(t => t.IsCompleted);
            _work.Add(task);
        }
    }

    // ------------------------------------------------------------------ the browser

    /// <summary>Only the page Copium's approval lives on: https, copium.pics, port 443, no user info, path /connect.</summary>
    internal static bool IsApproveUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } u
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, "copium.pics", StringComparison.OrdinalIgnoreCase)
        && u.IsDefaultPort
        && string.IsNullOrEmpty(u.UserInfo)
        && u.AbsolutePath == "/connect";

    /// <summary>The production opener: the default browser through the shell, the absolute address only, validated again here. UI thread (STA).</summary>
    internal static void OpenInBrowser(Uri uri)
    {
        if (!IsApproveUri(uri)) { Log.Warn("copium sign-in: refused to open an address that is not the approve page"); return; }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    private void Open(Uri uri)
    {
        if (!IsApproveUri(uri)) return;   // (nothing else ever reaches the opener)
        try { _openBrowser(uri); }
        catch (Exception ex) { Log.Warn("copium sign-in: the browser did not open (" + ex.GetType().Name + ")"); }   // the code is on screen; "Open the page again" still works
    }

    /// <summary>Opens the same validated approve address again while the sign-in is waiting. False when it is not waiting. UI thread.</summary>
    public bool OpenApprovePageAgain()
    {
        Uri? uri;
        lock (_gate) uri = _state.Status == SignInStatus.Waiting ? _approve : null;
        if (uri == null) return false;
        Open(uri);
        return true;
    }

    /// <summary>On the UI thread: opens the page only if <paramref name="run"/> is still the current sign-in and still waiting at that moment.</summary>
    private async Task OpenIfCurrentAsync(Run run)
    {
        try
        {
            await _onUi(() =>
            {
                Uri? uri;
                lock (_gate) uri = _run == run && _state.Status == SignInStatus.Waiting ? _approve : null;
                if (uri != null) Open(uri);   // UI thread: a cancel cannot land between the check and the open
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn("copium sign-in: the browser was not opened (" + ex.GetType().Name + ")"); }
    }

    // ------------------------------------------------------------------ start

    /// <summary>
    /// Starts a sign-in under this token name. Call it only from the user's click (UI thread). A running sign-in is cancelled first. Returns at once;
    /// follow <see cref="Changed"/>. Refused (Failed, no request) when cloud sharing is off, in a --dev instance that would lose the token, or when
    /// already signed in via Connect (<see cref="SignInFailure.AlreadySignedIn"/>: sign out first; a pasted token does not refuse).
    /// </summary>
    public void Start(string? tokenName)
    {
        SignInState first;
        Run? old = null, run = null;
        lock (_gate)
        {
            SignInFailure? refusal = null;
            string message = "";
            if (!_settings.CloudSharingEnabled) { refusal = SignInFailure.CloudSharingOff; message = ConsentGate.CloudOffText; }
            else if (IsDevInstance() && !DevKeepsToken()) { refusal = SignInFailure.DevInstanceNeedsToken; message = "Not available in a test instance without --dev-token"; }
            else if (LinkedLocked()) { refusal = SignInFailure.AlreadySignedIn; message = "Already signed in to Copium - sign out first to use another account"; }

            if (refusal is { } why)
            {
                // A finishing sign-in (its token stored already) is left to finish; one that is still waiting stops.
                if (_run is { Committed: false }) old = TakeRunLocked();
                _approve = null;
                Log.Info("copium sign-in: refused (" + why + ")");
                first = PublishLocked(MakeLocked(SignInStatus.Failed, failure: why, message: message));
            }
            else
            {
                old = TakeRunLocked();
                string name = CopiumConnect.SanitizeTokenName(tokenName);
                // A test instance can never replace the real PC's token: its name always ends in -dev.
                if (IsDevInstance()) name = CopiumConnect.WithSuffix(name, "-dev");
                _settings.CopiumTokenName = name;
                run = _run = new Run(name, ++_runIds);
                first = PublishLocked(MakeLocked(SignInStatus.Starting));
            }
        }
        CancelRun(old);
        if (run != null) Save();
        Raise(first);
        if (run != null) Track(Task.Run(() => RunAsync(run)));
    }

    private async Task RunAsync(Run run)
    {
        CancellationToken ct = run.Cts.Token;
        try
        {
            ConnectStart start = await CopiumConnect.StartAsync(Environment.MachineName, run.Name, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!IsApproveUri(start.ApproveUrl)) { EndBadly(run, SignInFailure.Unexpected, "Copium sent an unexpected answer"); return; }
            // (the poll key stays in 'start', a local of this task)
            if (!Set(run, () => MakeLocked(SignInStatus.Waiting, start.Code, start.ApproveUrl), approve: start.ApproveUrl)) return;
            await OpenIfCurrentAsync(run).ConfigureAwait(false);

            ConnectResult result = await CopiumConnect.WaitAsync(start, ct, retrying => SetRetrying(run, retrying)).ConfigureAwait(false);
            await CompleteAsync(run, result).ConfigureAwait(false);
        }
        catch (ConnectException ex) when (ex.Failure == ConnectFailure.Cancelled) { }   // whoever cancelled set the state
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ConnectException ex) { EndBadly(run, Map(ex.Failure), ex.Message); }
        catch (Exception ex)
        {
            Log.Warn("copium sign-in: unexpected " + ex.GetType().Name);
            EndBadly(run, SignInFailure.Unexpected, "Something went wrong signing in");
        }
        finally
        {
            // Whatever path ended the run without saying so (the UI went away mid-way): never leave a run state behind.
            Set(run, RestingLocked, end: true);
        }
    }

    private static SignInFailure Map(ConnectFailure f) => f switch
    {
        ConnectFailure.Unsupported => SignInFailure.Unsupported,
        ConnectFailure.RateLimited => SignInFailure.RateLimited,
        ConnectFailure.Expired => SignInFailure.Expired,
        ConnectFailure.Denied => SignInFailure.Denied,
        ConnectFailure.Network => SignInFailure.Network,
        _ => SignInFailure.Unexpected,
    };

    /// <summary>The run ends on a failure: Failed, unless its token is stored already, which rests as SignedIn (unverified), never Failed.</summary>
    private void EndBadly(Run run, SignInFailure failure, string message) =>
        Set(run, () => run.Committed ? RestingLocked() : MakeLocked(SignInStatus.Failed, failure: failure, message: message), end: true);

    private void SetRetrying(Run run, bool retrying)
    {
        SignInState next;
        lock (_gate)
        {
            if (_run != run || _state.Status != SignInStatus.Waiting || _state.Retrying == retrying) return;
            next = PublishLocked(_state with { Retrying = retrying });
        }
        Raise(next);
    }

    /// <summary>
    /// Sets the state (built under the lock) if <paramref name="run"/> is still the current sign-in; <paramref name="end"/>: the run is over
    /// (no longer current). False when it was taken (cancelled, replaced, signed out, ...) meanwhile.
    /// </summary>
    private bool Set(Run run, Func<SignInState> make, Uri? approve = null, bool end = false)
    {
        SignInState next;
        lock (_gate)
        {
            if (_run != run) return false;
            if (end) _run = null;
            _approve = approve;
            next = PublishLocked(make());
        }
        Raise(next);
        return true;
    }

    // ------------------------------------------------------------------ approved

    private async Task CompleteAsync(Run run, ConnectResult result)
    {
        if (BeforeCommit is { } hook)
        {
            try { await hook().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("copium sign-in: test hook failed (" + ex.GetType().Name + ")"); }
        }

        // The commit, on the UI thread: decided and done under one lock, so a cancel, a hand-saved token, the kill switch or exit either came
        // first (not stored: revoked below) or finds a stored token (and deals with it as a stored token).
        bool stored = false, unsavable = false;
        try
        {
            await _onUi(() =>
            {
                SignInState now;
                lock (_gate)
                {
                    if (_run != run || run.Cts.IsCancellationRequested || !_settings.CloudSharingEnabled) return;
                    // What was stored before (a pasted token, say) must survive a failed write: snapshot it as it is.
                    string? prevProtected = _settings.CopiumTokenProtected, prevName = _settings.CopiumAccountName;
                    bool prevLinked = _settings.CopiumLinkedViaConnect;
                    _settings.SetCopiumToken(result.Token, result.Username, viaConnect: true);
                    try { AfterStore?.Invoke(); } catch { }
                    if (!string.Equals(_settings.CopiumToken, result.Token, StringComparison.Ordinal))
                    {
                        // DPAPI could not protect it (Settings.Protect gave null): the new token is not stored. Put the old one back exactly; revoked below.
                        _settings.CopiumTokenProtected = prevProtected;
                        _settings.CopiumAccountName = prevName;
                        _settings.CopiumLinkedViaConnect = prevLinked;
                        unsavable = true;
                        return;
                    }
                    run.Committed = true;
                    _verified = false;
                    _checkFailed = false;
                    stored = true;
                    now = PublishLocked(MakeLocked(SignInStatus.Starting));
                }
                Save();
                Raise(now);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { if (!stored) Log.Warn("copium sign-in: the token could not be handed to the UI thread (" + ex.GetType().Name + ")"); }

        if (!stored)
        {
            if (!unsavable) Log.Info("copium sign-in: approved after it was stopped; not stored, revoking");
            if (unsavable)
            {
                Log.Warn("copium sign-in: the token could not be protected for storage; revoking");
                Save();
                Set(run, () => MakeLocked(SignInStatus.Failed, failure: SignInFailure.CouldNotSave, message: "Couldn't save the sign-in on this PC."), end: true);
            }
            else Set(run, RestingLocked, end: true);   // (when nothing else set the state: the UI went away)
            // Intended exception to the kill switch: this request may go out with cloud sharing off. It only deletes the token Copium just
            // issued, which nobody stored; left alone it would stay valid on the account, unknown to this PC. It protects the account.
            await RevokeOrFlagAsync(result.Token, run.Name).ConfigureAwait(false);
            return;
        }

        // Copium deleted the old token of this name when the user approved: read the account with the new one. A failure keeps the token.
        // Nothing more goes to Copium once the run was taken or sharing went off.
        if (RestIfStoppedOrSharingOff(run)) return;
        CopiumAccount? account = null;
        bool checkFailed = false;
        try { account = await CopiumConnect.GetAccountAsync(result.Token, run.Cts.Token).ConfigureAwait(false); }
        catch (ConnectException ex) when (ex.Failure == ConnectFailure.Cancelled) { return; }   // signed out, kill switch, a token by hand, exit: whoever did that set the state
        catch (ConnectException) { checkFailed = true; Log.Warn("copium sign-in: signed in, but the account could not be read; token kept"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { checkFailed = true; Log.Warn("copium sign-in: signed in, but reading the account failed (" + ex.GetType().Name + "); token kept"); }

        // The account, on the UI thread; the run ends here.
        SignInState? done = null;
        await _onUi(() =>
        {
            lock (_gate)
            {
                if (_run != run) return;
                _run = null;
                _approve = null;
                bool ours = string.Equals(_settings.CopiumToken, result.Token, StringComparison.Ordinal);
                if (ours && account != null && _settings.CloudSharingEnabled)
                {
                    if (string.IsNullOrWhiteSpace(_settings.CopiumAccountName)) _settings.CopiumAccountName = account.Username;
                    _settings.CopiumPlus = account.Plus;   // the same setting as the "I have Copium+" box
                    _verified = true;
                    _checkFailed = false;
                }
                else if (ours && checkFailed) _checkFailed = true;
                done = PublishLocked(RestingLocked() with { RunId = run.Id });
            }
            Save();
            Raise(done);
        }).ConfigureAwait(false);   // (a UI that went away: RunAsync's finally rests the state)

        if (done is not { Status: SignInStatus.SignedIn }) return;
        bool fire, welcome;
        lock (_gate)
        {
            // still this sign-in's SignedIn (not signed out, a new start, a token by hand): a cleanup flag or its dismissal does not end it
            fire = _state.Status == SignInStatus.SignedIn && _state.RunId == done.RunId && _settings.CloudSharingEnabled;
            welcome = !_settings.CopiumWelcomeShown;
        }
        if (!fire) return;
        Fire(Completed, done);
        if (welcome) Fire(WelcomeNeeded, done);
    }

    /// <summary>True (stop) when the run was taken meanwhile, or when cloud sharing went off (then the run rests: the token stays, unverified).</summary>
    private bool RestIfStoppedOrSharingOff(Run run)
    {
        SignInState now;
        lock (_gate)
        {
            if (_run != run) return true;
            if (_settings.CloudSharingEnabled) return false;
            _run = null;
            _approve = null;
            _verified = false;
            _checkFailed = false;
            now = PublishLocked(RestingLocked());
        }
        Raise(now);
        return true;
    }

    // ------------------------------------------------------------------ revoke

    /// <summary>Asks Copium to drop this token: true revoked, null this Copium cannot, false it did not work (an error, offline, the 10 s limit). Never throws.</summary>
    private static async Task<bool?> RevokeAsync(string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(RevokeLimit);
            return await CopiumConnect.RevokeSelfAsync(token, cts.Token).ConfigureAwait(false);
        }
        catch (ConnectException) { return false; }   // Cancelled: the limit
        catch (Exception ex)
        {
            Log.Warn("copium sign-in: revoke failed (" + ex.GetType().Name + ")");
            return false;
        }
    }

    /// <summary>Revokes a Connect token of this app; when that does not work, tells the UI which connection to remove on copium.pics.</summary>
    private async Task<bool?> RevokeOrFlagAsync(string token, string? name)
    {
        bool? done = await RevokeAsync(token).ConfigureAwait(false);
        if (done != true) FlagCleanup(name);
        return done;
    }

    private void FlagCleanup(string? name)
    {
        bool guess = string.IsNullOrWhiteSpace(name);
        string label = guess ? CopiumConnect.DefaultTokenName() : name!;
        SignInState now;
        lock (_gate)
        {
            _cleanup = label;
            _cleanupGuess = guess;
            now = PublishLocked(_state with { RemoteCleanupName = label, RemoteCleanupGuess = guess });
        }
        Log.Info("copium sign-in: a connection could not be removed on Copium; the user is asked to remove it there");
        Raise(now);
        Fire(RemoteCleanupNeeded, label);
    }

    /// <summary>The UI showed (or the user dismissed) the "remove it on copium.pics" note: <see cref="SignInState.RemoteCleanupName"/> goes back to null.</summary>
    public void DismissRemoteCleanup()
    {
        SignInState now;
        lock (_gate)
        {
            if (_cleanup == null) return;
            _cleanup = null;
            _cleanupGuess = false;
            now = PublishLocked(_state with { RemoteCleanupName = null, RemoteCleanupGuess = false });
        }
        Raise(now);
    }

    // ------------------------------------------------------------------ the account refresh at startup

    private CancellationTokenSource? _refresh;   // the running startup refresh (guarded by _gate)
    private static readonly TimeSpan RefreshLimit = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: the bound of one account read (30 s in production).</summary>
    internal TimeSpan RefreshLimitOverride { get; set; } = RefreshLimit;

    private void CancelRefresh()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _refresh; _refresh = null; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Reads /api/me once with the stored Connect token (the startup refresh: a stale name or a changed Copium+ plan). Nothing is sent unless
    /// the token came from "Sign in with Copium", cloud sharing is on, no sign-in is running and this is not a --dev instance without
    /// --dev-token. Bounded (30 s) and cancelled by exit, the kill switch, sign out and a token saved by hand. On success the account name,
    /// Copium+ and verified are written on the UI thread (<c>onUi</c>, no lock across the await); on any failure only
    /// <see cref="SignInState.AccountCheckFailed"/> is set. The token is NEVER removed or changed here, whatever the answer (a 401 included). Returns true when the account was read and applied.
    /// </summary>
    public async Task<bool> RefreshAccountAsync(CancellationToken ct)
    {
        string? token;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_run != null || _refresh != null || !LinkedLocked() || !_settings.CloudSharingEnabled) return false;
            if (IsDevInstance() && !DevKeepsToken()) return false;
            token = _settings.CopiumToken!;
            cts = _refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        bool checkFailed = false;
        CopiumAccount? account = null;
        try
        {
            cts.CancelAfter(RefreshLimitOverride);
            account = await CopiumConnect.GetAccountAsync(token, cts.Token).ConfigureAwait(false);
        }
        // Cancelled by exit / the kill switch / sign out (whoever did that set the state): stop quietly. Cancelled by our own 30 s limit: a failed check.
        catch (ConnectException ex) when (ex.Failure == ConnectFailure.Cancelled && (ct.IsCancellationRequested || RefreshTaken(cts))) { return Finish(cts); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || RefreshTaken(cts)) { return Finish(cts); }
        catch (ConnectException) { checkFailed = true; Log.Warn("copium: the startup account check failed; token kept"); }
        catch (Exception ex) { checkFailed = true; Log.Warn("copium: the startup account check failed (" + ex.GetType().Name + "); token kept"); }

        bool applied = false;
        try
        {
            await _onUi(() =>
            {
                SignInState now;
                lock (_gate)
                {
                    if (_refresh != cts) return;   // taken meanwhile (sign out, kill switch, a token by hand, exit)
                    _refresh = null;
                    if (_run != null || !LinkedLocked() || !_settings.CloudSharingEnabled || !string.Equals(_settings.CopiumToken, token, StringComparison.Ordinal)) return;
                    if (account != null)
                    {
                        _settings.CopiumAccountName = account.Username;
                        _settings.CopiumPlus = account.Plus;
                        _verified = true;
                        _checkFailed = false;
                        applied = true;
                    }
                    else if (checkFailed) _checkFailed = true;
                    // A shown failure (e.g. AlreadySignedIn) keeps its reason and message: a refresh only updates the account part.
                    var rest = _state.Status == SignInStatus.Failed ? MakeLocked(SignInStatus.Failed, failure: _state.Failure, message: _state.Message) : RestingLocked();
                    now = PublishLocked(rest with { RunId = _state.RunId });
                }
                Save();
                Raise(now);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn("copium: the startup account check could not be applied (" + ex.GetType().Name + ")"); }
        return Finish(cts, applied);
    }

    private bool RefreshTaken(CancellationTokenSource cts) { lock (_gate) return _refresh != cts; }

    private bool Finish(CancellationTokenSource cts, bool applied = false)
    {
        lock (_gate) { if (_refresh == cts) _refresh = null; }
        cts.Dispose();
        return applied;
    }

    // ------------------------------------------------------------------ cancel and the other stops

    /// <summary>
    /// The user's Cancel: stops a sign-in that is starting or waiting. The state goes back to the resting one (Cancel never removes a token).
    /// A sign-in whose token is stored already (finishing) is left alone: sign out undoes it.
    /// </summary>
    public void Cancel()
    {
        Run? run;
        SignInState now;
        lock (_gate)
        {
            if (_run == null || _run.Committed) return;
            run = TakeRunLocked();
            now = PublishLocked(RestingLocked());
        }
        CancelRun(run);
        Raise(now);
    }

    /// <summary>
    /// The cloud kill switch went off (call after <see cref="Settings.CloudSharingEnabled"/> is false; UI thread): any sign-in stops, even one whose
    /// token is stored and whose /api/me read is under way (it is cancelled; no Completed, toast or welcome follows). A stored token is kept
    /// (unverified). An approval that still arrives is not stored and is revoked (see <see cref="CompleteAsync"/>).
    /// </summary>
    public void StopForCloudSharingOff()
    {
        Run? run;
        SignInState now;
        lock (_gate)
        {
            run = TakeRunLocked();
            _verified = false;
            _checkFailed = false;
            now = PublishLocked(RestingLocked());
        }
        CancelRun(run);
        CancelRefresh();
        Raise(now);
    }

    /// <summary>
    /// The token box (UI thread): stops any sign-in (even a finishing one), stores this pasted token (null / empty clears it; never a sign-in),
    /// saves, and publishes the resting state. An approval that still arrives is not stored and is revoked. When the token being replaced or
    /// cleared came from "Sign in with Copium", it is this app's own: it is revoked on Copium too (best effort, in the background).
    /// </summary>
    public void SaveManualToken(string? token)
    {
        token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        Run? run;
        string? oldToken, oldName;
        bool oldConnect;
        SignInState now;
        lock (_gate)
        {
            run = TakeRunLocked();
            oldToken = _settings.CopiumToken;
            oldConnect = _settings.CopiumLinkedViaConnect && !string.IsNullOrWhiteSpace(oldToken);
            oldName = _settings.CopiumTokenName;
            // The stored token again (unchanged): leave it and its flags alone (a Connect token must not be demoted to a pasted one without a revoke).
            if (!string.Equals(oldToken, token, StringComparison.Ordinal)) _settings.SetCopiumToken(token);   // a pasted token: no account name, not via Connect
            _verified = false;
            _checkFailed = false;
            now = PublishLocked(RestingLocked());
        }
        CancelRun(run);
        CancelRefresh();
        Save();
        Raise(now);
        if (oldConnect && !string.Equals(oldToken, token, StringComparison.Ordinal))
            Track(Task.Run(() => RevokeOrFlagAsync(oldToken!, oldName)));
    }

    /// <summary>
    /// Exit, or the restart of a settings import (UI thread): every sign-in stops at once, no network wait. An approval that still arrives is
    /// not stored and is revoked (bounded, in the background, as far as the process lives). The token is not touched.
    /// </summary>
    public void Shutdown()
    {
        Run? run;
        SignInState? now = null;
        lock (_gate)
        {
            run = TakeRunLocked();
            if (run != null) now = PublishLocked(RestingLocked());   // (a failed restart must not show a stale code)
        }
        CancelRun(run);
        CancelRefresh();
        if (now != null) Raise(now);
    }

    /// <summary>Takes the current run out of play (it is no longer current: whatever it finds later is discarded). Cancel it with <see cref="CancelRun"/> after leaving the lock.</summary>
    private Run? TakeRunLocked()
    {
        var run = _run;
        _run = null;
        _approve = null;
        return run;
    }

    private static void CancelRun(Run? run)
    {
        if (run == null) return;
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ states

    /// <summary>Gives the state the next version and makes it current. Under the lock.</summary>
    private SignInState PublishLocked(SignInState next) => _state = next with { Version = ++_version };

    /// <summary>The stored token came from "Sign in with Copium".</summary>
    private bool LinkedLocked() => _settings.CopiumLinkedViaConnect && !string.IsNullOrWhiteSpace(_settings.CopiumToken);

    /// <summary>A state with the resting account filled in from the settings as they are now. Under the lock.</summary>
    private SignInState MakeLocked(SignInStatus status, string? code = null, Uri? approve = null, SignInFailure? failure = null, string? message = null)
    {
        bool has = !string.IsNullOrWhiteSpace(_settings.CopiumToken);
        bool linked = has && _settings.CopiumLinkedViaConnect;
        return new SignInState(status, Code: code, ApproveUrl: approve, HasToken: has, LinkedViaConnect: linked,
            Username: linked ? _settings.CopiumAccountName ?? "" : null, Plus: linked && _settings.CopiumPlus, Verified: linked && _verified, AccountCheckFailed: linked && !_verified && _checkFailed,
            Failure: failure, Message: message, RemoteCleanupName: _cleanup, RemoteCleanupGuess: _cleanup != null && _cleanupGuess);
    }

    /// <summary>The state with no sign-in running: SignedIn for a token this app got by signing in, SignedOut otherwise.</summary>
    private SignInState RestingLocked() => MakeLocked(LinkedLocked() ? SignInStatus.SignedIn : SignInStatus.SignedOut);

    // ------------------------------------------------------------------ sign out

    /// <summary>
    /// Stops any sign-in, removes the token from this PC at once (consent is kept) and, for a token this app got by signing in, asks Copium to
    /// revoke it (best effort, at most ~10 s; when that does not work <see cref="SignInState.RemoteCleanupName"/> says what to remove on the
    /// website). Always ends signed out. UI thread.
    /// </summary>
    public async Task<SignOutResult> SignOutAsync()
    {
        Run? run;
        string? token, name;
        bool viaConnect;
        SignInState now;
        lock (_gate)
        {
            run = TakeRunLocked();
            token = _settings.CopiumToken;
            viaConnect = _settings.CopiumLinkedViaConnect;
            name = _settings.CopiumTokenName;
            if (!string.IsNullOrWhiteSpace(token)) _settings.SetCopiumToken(null);
            _verified = false;
            _checkFailed = false;
            now = PublishLocked(RestingLocked());
        }
        CancelRun(run);
        CancelRefresh();
        Save();
        Raise(now);
        if (string.IsNullOrWhiteSpace(token)) return SignOutResult.NotSignedIn;
        if (!viaConnect) return SignOutResult.Manual;

        return await RevokeOrFlagAsync(token, name).ConfigureAwait(false) switch
        {
            true => SignOutResult.Revoked,
            null => SignOutResult.NotSupportedDisconnectOnWebsite,
            false => SignOutResult.FailedButSignedOutLocally,
        };
    }

    // ------------------------------------------------------------------ plumbing

    private void Save()
    {
        try { _save(); }
        catch (Exception ex) { Log.Warn("copium sign-in: settings not saved: " + ex.Message); }
    }

    private void Raise(SignInState state) => Fire(Changed, state);

    private static void Fire<T>(Action<T>? handler, T value)
    {
        try { handler?.Invoke(value); }
        catch (Exception ex) { Log.Warn("copium sign-in: a listener failed: " + ex.Message); }
    }
}
