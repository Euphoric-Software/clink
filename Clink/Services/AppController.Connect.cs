namespace Clink.Services;

// "Sign in with Copium": the controller's side of CopiumSignInSession (the rules and the state machine are there, WPF-free and tested).
// Here: the real browser opener and settings save, the UI-thread runner the session hands its settings writes to, the hop of its events to the
// UI thread (always the latest state, never a stale one), the toast, the welcome's answer.
internal sealed partial class AppController
{
    private CopiumSignInSession? _signIn;
    private long _signInShownVersion = -1;   // UI thread: the last state SignInChanged was raised with

    /// <summary>The one sign-in session (created on first use). Its <c>Start</c> is called only from a button click in the UI.</summary>
    internal CopiumSignInSession SignIn => _signIn ??= MakeSignIn();

    /// <summary>
    /// The sign-in state changed (Starting, Waiting with the code, SignedIn, Failed, SignedOut). Raised on the UI thread with the session's
    /// latest state at that moment: changes that come quickly are coalesced, a state is never raised twice, and an older one never after a newer one.
    /// </summary>
    public event Action<SignInState>? SignInChanged;

    /// <summary>A first sign-in finished and the welcome was not shown yet: show it (the main window forward, then the welcome). Raised on the UI thread, only while that sign-in is still the current state.</summary>
    public event Action? CopiumWelcomeNeeded;

    /// <summary>A Connect token of this app could not be revoked on Copium: tell the user to remove the connection of this name on copium.pics &gt; ShareX &amp; API. UI thread.</summary>
    public event Action<string>? CopiumRemoteCleanupNeeded;

    /// <summary>The app is exiting, disposed or restarting (a settings import): the UI ignores clicks that would reach the sign-in session.</summary>
    internal bool IsShuttingDown => _disposed || _exiting || _restarting;

    /// <summary>Test seam: opens the approve page instead of the default browser (a test must never start one).</summary>
    internal Action<Uri>? SignInOpenerOverride { get; set; }

    /// <summary>Test seam: what a finished sign-in does instead of the toast and the foreground grant (a test must show nothing and press no key).</summary>
    internal Action<SignInState>? SignedInOverride { get; set; }

    private CopiumSignInSession MakeSignIn()
    {
        var session = new CopiumSignInSession(Settings, uri => (SignInOpenerOverride ?? CopiumSignInSession.OpenInBrowser)(uri), () => Settings.Save(), RunOnUi);
        session.Changed += _ => _ui.BeginInvoke(() => ShowSignInState(session));
        session.Completed += state => _ui.BeginInvoke(() => { if (IsCurrentSignIn(state, session.Current, Settings.CloudSharingEnabled)) OnSignedIn(state); });
        session.WelcomeNeeded += state => _ui.BeginInvoke(() =>
        {
            if (!_disposed && IsCurrentSignIn(state, session.Current, Settings.CloudSharingEnabled) && !Settings.CopiumWelcomeShown) CopiumWelcomeNeeded?.Invoke();
        });
        session.RemoteCleanupNeeded += name => _ui.BeginInvoke(() => { if (!_disposed) CopiumRemoteCleanupNeeded?.Invoke(name); });
        return session;
    }

    /// <summary>
    /// The session's UI-thread runner: inline when already there, else queued (the task ends when it ran; cancelled when the dispatcher is gone,
    /// so the session knows the action never ran). The session never holds its lock while it waits for this.
    /// </summary>
    private Task RunOnUi(Action action)
    {
        if (_ui.CheckAccess())
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception ex) { return Task.FromException(ex); }
        }
        if (_ui.HasShutdownStarted) return Task.FromCanceled(new CancellationToken(canceled: true));
        return _ui.InvokeAsync(action).Task;
    }

    /// <summary>UI thread: raises <see cref="SignInChanged"/> with the latest state, once per version.</summary>
    private void ShowSignInState(CopiumSignInSession session)
    {
        if (_disposed) return;
        var now = session.Current;
        if (now.Version <= _signInShownVersion) return;
        _signInShownVersion = now.Version;
        SignInChanged?.Invoke(now);
    }

    /// <summary>A sign-in event still belongs to what is on screen: sharing is on and nothing changed since it was raised (no sign out, kill switch, new start, token by hand).</summary>
    internal static bool IsCurrentSignIn(SignInState raised, SignInState now, bool cloudSharing) =>
        cloudSharing && now.Status == SignInStatus.SignedIn
        && (raised.RunId != 0 ? now.RunId == raised.RunId   // the same sign-in run: an unrelated bump (cleanup flag / dismissal) does not drop it
                              : now.Version == raised.Version);

    private void OnSignedIn(SignInState state)
    {
        if (_disposed || !Settings.CloudSharingEnabled) return;
        if (SignedInOverride is { } over) { over(state); return; }
        string name = state.Username ?? "";
        // "connected", not "can share": sharing may not be agreed to yet (the welcome asks)
        try { Toasts.ShowNotice(name.Length == 0 ? "Signed in to Copium" : "Signed in to Copium as " + name, "Your Copium account is connected"); }   // the name is plain text
        catch (Exception ex) { Log.Warn("sign-in toast: " + ex.Message); }
        try { Hotkeys.GrantForeground(); }   // best effort: the approval happened in the browser, the window may be allowed forward
        catch (Exception ex) { Log.Warn("sign-in foreground: " + ex.Message); }
    }

    /// <summary>
    /// The token box's Save (UI thread): the pasted token (null / empty clears it) through the session, which stops any sign-in first (even one
    /// finishing), saves, and revokes a Connect token it replaces or one that is still approved.
    /// </summary>
    internal void SaveManualCopiumToken(string? token) => SignIn.SaveManualToken(token);

    // ------------------------------------------------------------------ the startup account refresh

    /// <summary>How long after startup the account is read once (test seam).</summary>
    internal TimeSpan StartupAccountRefreshDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>The scheduled startup refresh (test seam: await it).</summary>
    internal Task? StartupAccountRefresh { get; private set; }

    /// <summary>
    /// Once per launch, a few seconds after startup: refreshes the account name, Copium+ and verified from /api/me for a token that came from
    /// "Sign in with Copium". Nothing is scheduled with cloud sharing off, with no Connect token, or in a --dev instance without --dev-token.
    /// Runs in the background, cancelled by exit. A failure only sets the card's "couldn't check" note; the token is never removed. UI thread.
    /// </summary>
    internal void ScheduleAccountRefresh()
    {
        if (DailyAccountRefresh != null || IsShuttingDown) return;
        var session = SignIn;   // created here, on the UI thread
        var ct = _cts.Token;
        var delay = StartupAccountRefreshDelay;
        var wait = RefreshDelay;
        var daily = DailyAccountRefreshInterval;
        var startup = StartupAccountRefresh;
        if (startup == null && CanRefreshAccount())   // the one-shot keeps its launch gate
        {
            startup = StartupAccountRefresh = Task.Run(async () =>
            {
                try
                {
                    await wait(delay, ct).ConfigureAwait(false);
                    if (!IsShuttingDown) await session.RefreshAccountAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn("startup account check: " + ex.GetType().Name); }
            });
        }
        // Then once a day while Clink runs, started at every launch: a user who signs in later is covered too. The gates (sharing on,
        // a Connect token, not a --dev instance without --dev-token) are checked on each turn and the turn is skipped when they fail;
        // RefreshAccountAsync also skips while a sign-in runs. A failed check is tried again at the next turn.
        DailyAccountRefresh = Task.Run(async () =>
        {
            try
            {
                if (startup != null) await startup.ConfigureAwait(false);
                while (!IsShuttingDown)
                {
                    await wait(daily, ct).ConfigureAwait(false);
                    if (IsShuttingDown) break;
                    Interlocked.Increment(ref DailyRefreshes);
                    if (!CanRefreshAccount()) continue;
                    await session.RefreshAccountAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("daily account check: " + ex.GetType().Name); }
        });
    }

    /// <summary>The loop of the daily refreshes (test seam: it ends at exit / restart).</summary>
    internal Task? DailyAccountRefresh { get; private set; }

    /// <summary>Test seam: the wait between refreshes (the real clock in production).</summary>
    internal Func<TimeSpan, CancellationToken, Task> RefreshDelay { get; set; } = (d, ct) => Task.Delay(d, ct);

    /// <summary>How long after a refresh the next one goes out while Clink runs (test seam).</summary>
    internal TimeSpan DailyAccountRefreshInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How many daily turns started (test seam).</summary>
    internal int DailyRefreshes;   // (Interlocked)

    /// <summary>Not before this long after the last 413-triggered refresh (test seam).</summary>
    internal TimeSpan TooLargeRefreshDebounce { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Test seam: the clock of the 413 debounce.</summary>
    internal Func<DateTime> UtcNowForRefresh { get; set; } = () => DateTime.UtcNow;

    private DateTime _lastTooLargeRefresh = DateTime.MinValue;

    /// <summary>The last 413-triggered refresh (test seam: await it).</summary>
    internal Task? TooLargeRefresh { get; private set; }

    /// <summary>Same gates as the startup refresh: a Connect token, sharing on, not a --dev instance without --dev-token.</summary>
    private bool CanRefreshAccount() =>
        Settings.CopiumLinkedViaConnect && !string.IsNullOrWhiteSpace(Settings.CopiumToken) && Settings.CloudSharingEnabled && (!IsDev || DevToken);

    /// <summary>
    /// Copium answered an upload with 413 (raised on any thread): the plan may have changed, so the account is read once, at most once per
    /// 10 minutes. Never blocks or retries the upload; a failure only sets the card's note (the token is kept).
    /// </summary>
    internal void OnUploadTooLarge()
    {
        if (IsShuttingDown || _ui.HasShutdownStarted) return;
        _ui.BeginInvoke(() =>
        {
            if (IsShuttingDown || !CanRefreshAccount()) return;
            var now = UtcNowForRefresh();
            if (now - _lastTooLargeRefresh < TooLargeRefreshDebounce) return;
            _lastTooLargeRefresh = now;
            var session = SignIn;
            var ct = _cts.Token;
            TooLargeRefresh = Task.Run(async () =>
            {
                try { await session.RefreshAccountAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn("account check after 413: " + ex.GetType().Name); }
            });
        });
    }

    // ------------------------------------------------------------------ the welcome

    /// <summary>The welcome's box when it opens (D1: ticked until sharing was agreed to).</summary>
    internal bool WelcomeInitialAutoUpload => ConsentGate.WelcomeInitialAutoUpload(Settings);

    /// <summary>
    /// The welcome's answer (UI thread). "Start sharing": consent and the screenshot auto-upload as the box says. "Not now": no consent, counts as
    /// one proactive showing of the Copium nudge. Saved either way; the welcome never shows again.
    /// </summary>
    internal void ApplyWelcome(bool startSharing, bool autoUpload)
    {
        ConsentGate.ApplyWelcome(Settings, startSharing, autoUpload);
        if (!startSharing) Clink.Screenshots.CopiumNudge.MarkPrompted(Settings, DateTime.UtcNow);   // saves too
        try { Settings.Save(); }
        catch (Exception ex) { Log.Warn("welcome: settings not saved: " + ex.Message); }
        Log.Info(startSharing ? "welcome: sharing started" + (autoUpload ? " (automatic screenshot uploads on)" : "") : "welcome: not now");
        Consent.NotifyChanged();
        RaiseStateChanged();
    }
}
