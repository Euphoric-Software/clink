using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>Which body the App tab's Copium card shows.</summary>
internal enum CopiumPanel
{
    /// <summary>No token: "Sign in with Copium", the name box, and the "Use an API token instead" link.</summary>
    SignedOut,
    /// <summary>A sign-in is running before the approval: the start panel shows the contacting state, and the Waiting panel shows the code.</summary>
    Waiting,
    /// <summary>The approval came and the token is stored; the account is being read.</summary>
    Finishing,
    /// <summary>Signed in with "Sign in with Copium": the account line and Sign out.</summary>
    SignedIn,
    /// <summary>A token pasted by hand: the token box, and Sign in with Copium still offered.</summary>
    PastedToken,
}

/// <summary>
/// What the App tab's Copium card shows for one <see cref="SignInState"/>: the panel, every text and the enabled / visible flag of every control.
/// WPF-free and pure; every user-facing string of the card lives here. The window only copies these values onto its controls.
/// Texts that carry a name from the account or the token are plain text (the window sets Text, never markup); the name is wrapped in Unicode
/// isolates so a right-to-left name cannot reorder the words around it.
/// </summary>
internal sealed class CopiumCardModel
{
    // ---- the strings
    // Properties, not constants: each is the text in the active language (the English text is the catalog key).
    internal static string SignInLabel => L.T("Sign in with Copium");
    internal static string SignInHint => L.T("Opens copium.pics in your browser. Approve there and you're done.");
    internal static string NameLabel => L.T("Name on Copium");
    internal static string UseTokenLink => L.T("Use an API token instead");
    internal static string CodeCheckHint => L.T("Check that this code matches the one on the Copium page.");
    internal static string OpenAgainLabel => L.T("Open the page again");
    internal static string CancelLabel => L.T("Cancel");
    internal static string SignOutLabel => L.T("Sign out");
    internal static string StartingStatus => L.T("Contacting Copium…");
    internal static string WaitingStatus => L.T("Waiting for you to approve in your browser…");
    internal static string RetryingStatus => L.T("Can't reach Copium right now. Still trying…");
    internal static string FinishingStatus => L.T("Finishing sign-in…");
    internal static string UnverifiedNote => L.T("Couldn't check your account just now.");
    internal static string PlusFromAccountNote => L.T("From your Copium account");
    internal static string DevNeedsTokenReason => L.T("Not available in a test instance without --dev-token");
    internal static string CleanupOpenLabel => L.T("Open copium.pics");
    internal static string CleanupDismissLabel => L.T("Dismiss");
    internal static string NoTokenShareHint => L.T("Without a token, Share opens a window you can drag the clip from (into Discord, a browser or a folder).");
    internal static string SignOutBusyStatus => L.T("Signing out…");
    /// <summary>Where the "Open copium.pics" button of the cleanup notice goes (the ShareX &amp; API page, where the connections are listed).</summary>
    internal const string CleanupUrl = "https://copium.pics/sharex";

    private const string IsolateStart = "\u2068", IsolateEnd = "\u2069";
    internal static string Iso(string s) => IsolateStart + s + IsolateEnd;

    // ---- the panel
    public CopiumPanel Panel { get; init; }

    // ---- signed out / pasted token: the sign-in start
    public bool SignInVisible { get; init; }
    public bool SignInEnabled { get; init; }
    /// <summary>Why Sign in is disabled (shown instead of <see cref="SignInHintText"/>); null when it is enabled or the card is busy.</summary>
    public string? SignInDisabledReason { get; init; }
    public bool SignInHintVisible { get; init; }
    public string SignInHintText { get; init; } = "";
    public bool NameBoxVisible { get; init; }
    public bool NameBoxEnabled { get; init; }
    /// <summary>The name box's text when the card is (re)filled: the saved token name, else the default.</summary>
    public string NameBoxText { get; init; } = "";
    /// <summary>"Use an API token instead": only while signed out (a pasted token shows its box at once).</summary>
    public bool UseTokenLinkVisible { get; init; }
    /// <summary>The token box, Save and "Get a token" are shown (a pasted token). A signed-out card shows them too once the link is used or when <see cref="AutoExpandToken"/>.</summary>
    public bool TokenSectionVisible { get; init; }
    /// <summary>Sign in with Copium is not available on this Copium yet: open the token section on its own.</summary>
    public bool AutoExpandToken { get; init; }
    /// <summary>The token box shows the stored token (only a pasted one; never a Connect token).</summary>
    public bool TokenBoxPrefill { get; init; }

    // ---- waiting / finishing
    public bool CodeVisible { get; init; }
    public string? CodeText { get; init; }
    public bool CodeHintVisible { get; init; }
    public bool OpenAgainVisible { get; init; }
    public bool CancelVisible { get; init; }
    public bool CancelEnabled { get; init; }
    /// <summary>The progress line (read out politely by screen readers): contacting, waiting, retrying, finishing. Null when there is none.</summary>
    public string? StatusText { get; init; }

    // ---- signed in
    public bool SignedInVisible { get; init; }
    public string? SignedInText { get; init; }
    /// <summary>Shown only when an account check actually failed in this process (<see cref="SignInState.AccountCheckFailed"/>), never merely because nothing checked yet.</summary>
    public bool UnverifiedNoteVisible { get; init; }
    public bool SignOutVisible { get; init; }
    public bool SignOutEnabled { get; init; }

    // ---- the Copium+ box
    public bool PlusCheckEnabled { get; init; } = true;
    public bool PlusCheckValueFromAccount { get; init; }
    public bool PlusCheckValue { get; init; }
    public string? PlusNote { get; init; }

    /// <summary>The "Without a token, Share opens a window" advice: only while no token is stored.</summary>
    public bool NoTokenHintVisible { get; init; }

    // ---- a failure line
    public string? FailureText { get; init; }
    /// <summary>A failure line is on screen (the notice right under the Sign in button).</summary>
    // Kept apart from MessageIsWarning: this is derived from the text, that one is the look (set by the factory) and may differ for non-failure lines.
    public bool MessageVisible => FailureText != null;
    /// <summary>The notice uses the warning look: true for every failure line. The sign-out result texts and the "Signing out…" line are neutral (the window shows them with this false).</summary>
    public bool MessageIsWarning { get; init; }

    /// <summary>The start request is in flight: the start panel stays, Sign in is disabled and shows the contacting line, and a compact Cancel sits beside it.</summary>
    public bool Contacting { get; init; }

    // ---- the "remove it on copium.pics" notice
    public bool CleanupVisible { get; init; }
    public string? CleanupText { get; init; }

    /// <summary>
    /// The card for <paramref name="state"/>. <paramref name="devKeepsToken"/> only matters for a dev instance. <paramref name="defaultTokenName"/> is
    /// <see cref="CopiumConnect.DefaultTokenName()"/> (a parameter so the model stays pure); <paramref name="savedTokenName"/> is the name used last.
    /// </summary>
    public static CopiumCardModel From(SignInState state, bool cloudSharingOn, bool isDev, bool devKeepsToken, string defaultTokenName, string? savedTokenName)
    {
        bool linked = state.LinkedViaConnect && state.HasToken;
        // The start request is in flight (no code yet, no Connect token): the start panel stays put, so nothing jumps while Copium answers.
        bool contacting = state.Status == SignInStatus.Starting && !linked;
        CopiumPanel panel =
            state.Status == SignInStatus.Starting && linked ? CopiumPanel.Finishing
            : contacting ? (state.HasToken ? CopiumPanel.PastedToken : CopiumPanel.SignedOut)
            : state.Status == SignInStatus.Waiting ? CopiumPanel.Waiting
            : linked ? CopiumPanel.SignedIn
            : state.HasToken ? CopiumPanel.PastedToken
            : CopiumPanel.SignedOut;

        string? reason = !cloudSharingOn ? ConsentGate.CloudOffText
            : isDev && !devKeepsToken ? DevNeedsTokenReason
            : null;
        bool canStart = reason == null;
        bool startPanel = panel is CopiumPanel.SignedOut or CopiumPanel.PastedToken;
        string name = string.IsNullOrWhiteSpace(savedTokenName) ? defaultTokenName : savedTokenName!;

        string? failure = state.Status == SignInStatus.Failed ? FailureLine(state.Failure, state.Message) : null;
        bool unsupported = state.Status == SignInStatus.Failed && state.Failure == SignInFailure.Unsupported;

        string? status = panel switch
        {
            CopiumPanel.Finishing => FinishingStatus,
            CopiumPanel.Waiting => state.Retrying ? RetryingStatus : WaitingStatus,
            _ when contacting => StartingStatus,
            _ => null,
        };

        bool hasCode = panel == CopiumPanel.Waiting && state.Status == SignInStatus.Waiting && !string.IsNullOrEmpty(state.Code);
        string? cleanup = string.IsNullOrWhiteSpace(state.RemoteCleanupName) ? null
            : state.RemoteCleanupGuess
                ? L.T("Clink couldn't remove its connection from Copium. Remove the connection probably named “{name}” on copium.pics under ShareX & API.", ("name", Iso(state.RemoteCleanupName)))
                : L.T("Clink couldn't remove its connection from Copium. Remove “{name}” on copium.pics under ShareX & API.", ("name", Iso(state.RemoteCleanupName)));

        string? signedInText = null;
        if (panel == CopiumPanel.SignedIn)
        {
            bool named = !string.IsNullOrWhiteSpace(state.Username);
            string who = named ? Iso(state.Username!) : "";
            signedInText = named
                ? (state.Plus ? L.T("Signed in as {name} · Copium+", ("name", who)) : L.T("Signed in as {name}", ("name", who)))
                : (state.Plus ? L.T("Signed in · Copium+") : L.T("Signed in"));
        }

        return new CopiumCardModel
        {
            Panel = panel,
            SignInVisible = startPanel || panel == CopiumPanel.Finishing,
            SignInEnabled = startPanel && canStart && !contacting,
            SignInDisabledReason = startPanel && !contacting ? reason : null,
            SignInHintVisible = startPanel,
            SignInHintText = contacting ? StartingStatus : SignInHint,
            NameBoxVisible = startPanel,
            NameBoxEnabled = startPanel && canStart && !contacting,
            NameBoxText = name,
            UseTokenLinkVisible = panel == CopiumPanel.SignedOut,
            TokenSectionVisible = panel == CopiumPanel.PastedToken,
            AutoExpandToken = panel == CopiumPanel.SignedOut && unsupported,
            TokenBoxPrefill = panel == CopiumPanel.PastedToken,
            CodeVisible = hasCode,
            CodeText = hasCode ? state.Code : null,
            CodeHintVisible = hasCode,
            OpenAgainVisible = hasCode,
            CancelVisible = panel is CopiumPanel.Waiting or CopiumPanel.Finishing || contacting,
            CancelEnabled = panel == CopiumPanel.Waiting || contacting,
            Contacting = contacting,
            StatusText = status,
            SignedInVisible = panel == CopiumPanel.SignedIn,
            SignedInText = signedInText,
            UnverifiedNoteVisible = panel == CopiumPanel.SignedIn && !state.Verified && state.AccountCheckFailed,
            SignOutVisible = panel == CopiumPanel.SignedIn,
            SignOutEnabled = panel == CopiumPanel.SignedIn,
            PlusCheckEnabled = !linked,
            PlusCheckValueFromAccount = linked,
            PlusCheckValue = linked && state.Plus,
            PlusNote = linked ? PlusFromAccountNote : null,
            NoTokenHintVisible = !state.HasToken,
            FailureText = failure,
            MessageIsWarning = failure != null,
            CleanupVisible = cleanup != null,
            CleanupText = cleanup,
        };
    }

    /// <summary>The one line for a failed sign-in (null failure: the generic line).</summary>
    internal static string FailureLine(SignInFailure? failure, string? message = null) => failure switch
    {
        SignInFailure.Unsupported => L.T("Sign in with Copium isn't available yet. Use an API token instead."),
        SignInFailure.RateLimited => L.T("Too many sign-in attempts. Try again in a few minutes."),
        SignInFailure.Expired => L.T("The code expired. Try again."),
        SignInFailure.Denied => L.T("The sign-in was cancelled on Copium."),
        SignInFailure.Network => L.T("Couldn't reach Copium. Check your connection and try again."),
        SignInFailure.AlreadySignedIn => L.T("You're already signed in. Sign out first to switch accounts."),
        SignInFailure.CloudSharingOff => ConsentGate.CloudOffText,
        SignInFailure.DevInstanceNeedsToken => DevNeedsTokenReason,
        SignInFailure.CouldNotSave => L.T("Couldn't save the sign-in on this PC."),
        _ => L.T("Something went wrong. Try again."),
    };

    /// <summary>What the card says after Sign out; null when there is nothing to say.</summary>
    internal static string? SignOutText(SignOutResult result) => result switch
    {
        SignOutResult.Revoked => L.T("Signed out. Clink's connection was removed from Copium."),
        SignOutResult.NotSupportedDisconnectOnWebsite or SignOutResult.FailedButSignedOutLocally =>
            L.T("Signed out on this PC. Remove the Clink connection on copium.pics under ShareX & API."),
        SignOutResult.Manual => L.T("Signed out on this PC. Your API token still works on copium.pics until you delete it there."),
        _ => null,
    };
}
