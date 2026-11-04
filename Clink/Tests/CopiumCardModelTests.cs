using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>The App tab's Copium card: <see cref="CopiumCardModel"/> decides the panel, the texts and every control's flags (no WPF).</summary>
internal static class CopiumCardModelTests
{
    private const string Def = "Clink-PC";
    private const string IsoStart = "\u2068", IsoEnd = "\u2069";

    private static CopiumCardModel M(SignInState s, bool cloud = true, bool dev = false, bool devToken = false, string? saved = null) =>
        CopiumCardModel.From(s, cloud, dev, devToken, Def, saved);

    private static readonly SignInState Out = new(SignInStatus.SignedOut);
    private static readonly SignInState Pasted = new(SignInStatus.SignedOut, HasToken: true);
    private static SignInState Linked(bool plus = false, bool verified = true, string? user = "Jason", SignInStatus status = SignInStatus.SignedIn) =>
        new(status, HasToken: true, LinkedViaConnect: true, Username: user, Plus: plus, Verified: verified);

    [UnitTest]
    public static void Signed_out_offers_sign_in_the_name_box_and_the_token_link_with_the_token_section_collapsed()
    {
        var m = M(Out);
        Check.Equal(CopiumPanel.SignedOut, m.Panel);
        Check.True(m.SignInVisible); Check.True(m.SignInEnabled); Check.Null(m.SignInDisabledReason);
        Check.Equal("Sign in with Copium", CopiumCardModel.SignInLabel);
        Check.Equal("Opens copium.pics in your browser. Approve there and you're done.", m.SignInHintText);
        Check.True(m.SignInHintVisible);
        Check.True(m.NameBoxVisible); Check.True(m.NameBoxEnabled);
        Check.Equal(Def, m.NameBoxText, "the default name");
        Check.True(m.UseTokenLinkVisible);
        Check.Equal("Use an API token instead", CopiumCardModel.UseTokenLink);
        Check.False(m.TokenSectionVisible); Check.False(m.AutoExpandToken); Check.False(m.TokenBoxPrefill);
        Check.False(m.CodeVisible); Check.False(m.CancelVisible); Check.False(m.SignedInVisible); Check.False(m.CleanupVisible);
        Check.Null(m.FailureText); Check.Null(m.StatusText);
        Check.True(m.PlusCheckEnabled); Check.False(m.PlusCheckValueFromAccount); Check.Null(m.PlusNote);
    }

    [UnitTest]
    public static void The_name_box_uses_the_saved_token_name_else_the_default()
    {
        Check.Equal("Gaming rig", M(Out, saved: "Gaming rig").NameBoxText);
        Check.Equal(Def, M(Out, saved: "  ").NameBoxText);
        Check.Equal(Def, M(Out, saved: null).NameBoxText);
        Check.Equal("Clink-PC-dev", CopiumCardModel.From(Out, true, true, true, "Clink-PC-dev", null).NameBoxText);
    }

    [UnitTest]
    public static void Sign_in_is_disabled_with_a_reason_when_cloud_sharing_is_off_or_a_dev_instance_lacks_dev_token()
    {
        var off = M(Out, cloud: false);
        Check.False(off.SignInEnabled); Check.False(off.NameBoxEnabled);
        Check.Equal(ConsentGate.CloudOffText, off.SignInDisabledReason);

        var dev = M(Out, dev: true, devToken: false);
        Check.False(dev.SignInEnabled); Check.False(dev.NameBoxEnabled);
        Check.Equal("Not available in a test instance without --dev-token", dev.SignInDisabledReason);

        var devOk = M(Out, dev: true, devToken: true);
        Check.True(devOk.SignInEnabled); Check.Null(devOk.SignInDisabledReason);

        Check.Equal(ConsentGate.CloudOffText, M(Out, cloud: false, dev: true).SignInDisabledReason, "the kill switch comes first");
        Check.False(M(Pasted, dev: true).SignInEnabled, "also with a pasted token");
        Check.True(M(Out, dev: false, devToken: false).SignInEnabled, "a normal instance ignores --dev-token");
    }

    [UnitTest]
    public static void Starting_without_a_code_keeps_the_start_panel_with_a_disabled_sign_in_and_a_cancel()
    {
        var starting = M(new SignInState(SignInStatus.Starting));
        Check.Equal(CopiumPanel.SignedOut, starting.Panel);
        Check.True(starting.Contacting);
        Check.False(starting.CodeVisible); Check.False(starting.OpenAgainVisible);
        Check.True(starting.CancelVisible); Check.True(starting.CancelEnabled);
        Check.Equal("Contacting Copium…", starting.StatusText);
        Check.Equal("Contacting Copium…", starting.SignInHintText);
        Check.True(starting.SignInVisible); Check.False(starting.SignInEnabled); Check.Null(starting.SignInDisabledReason);
        Check.True(starting.NameBoxVisible); Check.False(starting.NameBoxEnabled);
        var idle = M(Out);
        Check.Equal(idle.TokenSectionVisible, starting.TokenSectionVisible);
        Check.Equal(idle.UseTokenLinkVisible, starting.UseTokenLinkVisible);
        Check.Equal(idle.AutoExpandToken, starting.AutoExpandToken);

        var pasted = M(new SignInState(SignInStatus.Starting, HasToken: true));
        Check.Equal(CopiumPanel.PastedToken, pasted.Panel);
        Check.True(pasted.TokenSectionVisible); Check.False(pasted.UseTokenLinkVisible);
        Check.False(pasted.SignInEnabled); Check.True(pasted.CancelVisible);

        Check.False(idle.Contacting);
        Check.False(M(new SignInState(SignInStatus.Waiting, Code: "X")).Contacting);
    }

    [UnitTest]
    public static void Waiting_with_a_code_shows_the_code_the_hint_open_again_and_cancel_with_a_live_status()
    {
        var waiting = M(new SignInState(SignInStatus.Waiting, Code: "AB2C-DE9F"));
        Check.Equal(CopiumPanel.Waiting, waiting.Panel);
        Check.True(waiting.CodeVisible); Check.Equal("AB2C-DE9F", waiting.CodeText);
        Check.True(waiting.CodeHintVisible);
        Check.Equal("Check that this code matches the one on the Copium page.", CopiumCardModel.CodeCheckHint);
        Check.True(waiting.OpenAgainVisible); Check.Equal("Open the page again", CopiumCardModel.OpenAgainLabel);
        Check.True(waiting.CancelVisible); Check.True(waiting.CancelEnabled); Check.Equal("Cancel", CopiumCardModel.CancelLabel);
        Check.Equal("Waiting for you to approve in your browser…", waiting.StatusText);

        var retry = M(new SignInState(SignInStatus.Waiting, Code: "AB2C-DE9F", Retrying: true));
        Check.Equal("Can't reach Copium right now. Still trying…", retry.StatusText);
        Check.True(retry.CodeVisible, "the code stays up while retrying");

        var waitingWithPasted = M(new SignInState(SignInStatus.Waiting, Code: "X", HasToken: true));
        Check.Equal(CopiumPanel.Waiting, waitingWithPasted.Panel);
        Check.False(waitingWithPasted.TokenSectionVisible);
    }

    [UnitTest]
    public static void Finishing_is_a_starting_state_with_a_connect_token_and_disables_cancel_and_sign_in()
    {
        var m = M(Linked(status: SignInStatus.Starting));
        Check.Equal(CopiumPanel.Finishing, m.Panel);
        Check.Equal("Finishing sign-in…", m.StatusText);
        Check.False(m.Contacting);
        Check.True(m.CancelVisible); Check.False(m.CancelEnabled);
        Check.True(m.SignInVisible); Check.False(m.SignInEnabled);
        Check.False(m.SignOutVisible);
        Check.False(m.CodeVisible);
        Check.False(m.TokenBoxPrefill);
    }

    [UnitTest]
    public static void Signed_in_via_connect_shows_the_account_plus_and_sign_out_and_hides_the_token_box()
    {
        var plain = M(Linked());
        Check.Equal(CopiumPanel.SignedIn, plain.Panel);
        Check.Equal("Signed in as " + IsoStart + "Jason" + IsoEnd, plain.SignedInText);
        Check.True(plain.SignOutVisible); Check.True(plain.SignOutEnabled);
        Check.Equal("Sign out", CopiumCardModel.SignOutLabel);
        Check.False(plain.UnverifiedNoteVisible);
        Check.False(plain.TokenSectionVisible); Check.False(plain.TokenBoxPrefill); Check.False(plain.UseTokenLinkVisible);
        Check.False(plain.SignInVisible); Check.False(plain.NameBoxVisible);

        var plus = M(Linked(plus: true));
        Check.Equal("Signed in as " + IsoStart + "Jason" + IsoEnd + " · Copium+", plus.SignedInText);

        Check.False(M(Linked(verified: false)).UnverifiedNoteVisible, "not checked yet (nothing verifies at start) is not a failure");
        var unverified = M(Linked(verified: false) with { AccountCheckFailed = true });
        Check.True(unverified.UnverifiedNoteVisible);
        Check.False(M(Linked(verified: true) with { AccountCheckFailed = true }).UnverifiedNoteVisible, "a verified account shows no note");
        Check.Equal("Couldn't check your account just now.", CopiumCardModel.UnverifiedNote);

        Check.Equal("Signed in", M(Linked(user: "")).SignedInText, "no name known");
        Check.Equal("Signed in", M(Linked(user: null)).SignedInText);
    }

    [UnitTest]
    public static void The_copium_plus_box_is_read_only_and_follows_the_account_while_linked()
    {
        var linkedPlus = M(Linked(plus: true));
        Check.False(linkedPlus.PlusCheckEnabled); Check.True(linkedPlus.PlusCheckValueFromAccount); Check.True(linkedPlus.PlusCheckValue);
        Check.Equal("From your Copium account", linkedPlus.PlusNote);
        var linkedFree = M(Linked(plus: false));
        Check.False(linkedFree.PlusCheckEnabled); Check.False(linkedFree.PlusCheckValue);
        var pasted = M(Pasted);
        Check.True(pasted.PlusCheckEnabled); Check.False(pasted.PlusCheckValueFromAccount); Check.Null(pasted.PlusNote);
    }

    [UnitTest]
    public static void A_pasted_token_shows_the_token_section_and_still_offers_sign_in()
    {
        var m = M(Pasted);
        Check.Equal(CopiumPanel.PastedToken, m.Panel);
        Check.True(m.TokenSectionVisible); Check.True(m.TokenBoxPrefill);
        Check.False(m.UseTokenLinkVisible);
        Check.True(m.SignInVisible); Check.True(m.SignInEnabled); Check.True(m.NameBoxVisible);
        Check.False(m.SignOutVisible);
        Check.False(M(Pasted, cloud: false).SignInEnabled);
    }

    [UnitTest]
    public static void Every_failure_has_its_one_line_and_unsupported_opens_the_token_section()
    {
        (SignInFailure f, string text)[] lines =
        {
            (SignInFailure.Unsupported, "Sign in with Copium isn't available yet. Use an API token instead."),
            (SignInFailure.RateLimited, "Too many sign-in attempts. Try again in a few minutes."),
            (SignInFailure.Expired, "The code expired. Try again."),
            (SignInFailure.Denied, "The sign-in was cancelled on Copium."),
            (SignInFailure.Network, "Couldn't reach Copium. Check your connection and try again."),
            (SignInFailure.Unexpected, "Something went wrong. Try again."),
            (SignInFailure.AlreadySignedIn, "You're already signed in. Sign out first to switch accounts."),
            (SignInFailure.CloudSharingOff, ConsentGate.CloudOffText),
            (SignInFailure.DevInstanceNeedsToken, "Not available in a test instance without --dev-token"),
            (SignInFailure.CouldNotSave, "Couldn't save the sign-in on this PC."),
        };
        foreach (var (f, text) in lines)
        {
            var m = M(new SignInState(SignInStatus.Failed, Failure: f, Message: "internal detail"));
            Check.Equal(CopiumPanel.SignedOut, m.Panel, f.ToString());
            Check.Equal(text, m.FailureText, f.ToString());
            Check.Equal(f == SignInFailure.Unsupported, m.AutoExpandToken, f.ToString());
            Check.True(m.MessageVisible, f.ToString()); Check.True(m.MessageIsWarning, "a failure is a warning: " + f);
            Check.True(m.SignInVisible, "the user can try again: " + f);
        }
        Check.Equal(Enum.GetValues<SignInFailure>().Length, lines.Length, "every failure is covered");
        Check.Equal("Something went wrong. Try again.", CopiumCardModel.FailureLine(null));
        Check.Null(M(Out).FailureText);
        Check.Null(M(Linked()).FailureText, "a SignedIn state has no failure line");
    }

    [UnitTest]
    public static void A_failed_state_that_still_has_a_connect_token_keeps_the_signed_in_line()
    {
        var m = M(Linked(plus: true, status: SignInStatus.Failed) with { Failure = SignInFailure.AlreadySignedIn });
        Check.Equal(CopiumPanel.SignedIn, m.Panel);
        Check.Equal("Signed in as " + IsoStart + "Jason" + IsoEnd + " · Copium+", m.SignedInText);
        Check.Equal("You're already signed in. Sign out first to switch accounts.", m.FailureText);
        Check.True(m.SignOutEnabled);
        Check.False(m.AutoExpandToken);

        var pasted = M(Pasted with { Status = SignInStatus.Failed, Failure = SignInFailure.Denied });
        Check.Equal(CopiumPanel.PastedToken, pasted.Panel);
        Check.Equal("The sign-in was cancelled on Copium.", pasted.FailureText);
        Check.True(pasted.MessageVisible); Check.True(pasted.MessageIsWarning);
    }

    [UnitTest]
    public static void A_pasted_token_with_unsupported_sign_in_shows_the_warning_and_a_signed_out_card_without_failure_has_none()
    {
        var m = M(Pasted with { Status = SignInStatus.Failed, Failure = SignInFailure.Unsupported });
        Check.Equal(CopiumPanel.PastedToken, m.Panel);
        Check.True(m.MessageVisible); Check.True(m.MessageIsWarning);
        Check.Equal("Sign in with Copium isn't available yet. Use an API token instead.", m.FailureText);
        Check.True(m.TokenSectionVisible);
        // Sign-out result texts are not failures: the window shows them neutral (MessageIsWarning is false without a failure line).
        foreach (var s in new[] { Out, Pasted, Linked() })
        {
            var n = M(s);
            Check.False(n.MessageVisible); Check.False(n.MessageIsWarning);
        }
        Check.True(CopiumCardModel.SignOutText(SignOutResult.Revoked) != null);
    }

    [UnitTest]
    public static void The_cleanup_notice_names_the_connection_and_hedges_a_fallback_name()
    {
        var known = M(Out with { RemoteCleanupName = "Gaming rig" });
        Check.True(known.CleanupVisible);
        Check.Equal("Clink couldn't remove its connection from Copium. Remove “" + IsoStart + "Gaming rig" + IsoEnd + "” on copium.pics under ShareX & API.", known.CleanupText);

        var guess = M(Out with { RemoteCleanupName = Def, RemoteCleanupGuess = true });
        Check.Equal("Clink couldn't remove its connection from Copium. Remove the connection probably named “" + IsoStart + Def + IsoEnd + "” on copium.pics under ShareX & API.", guess.CleanupText);

        Check.False(M(Out).CleanupVisible);
        Check.False(M(Out with { RemoteCleanupName = "  " }).CleanupVisible);
        Check.True(M(Linked() with { RemoteCleanupName = "X" }).CleanupVisible, "also while signed in");
        Check.Equal("https://copium.pics/sharex", CopiumCardModel.CleanupUrl);
        Check.Equal("Open copium.pics", CopiumCardModel.CleanupOpenLabel);
        Check.Equal("Dismiss", CopiumCardModel.CleanupDismissLabel);
    }

    [UnitTest]
    public static void Sign_out_results_have_their_texts()
    {
        Check.Equal("Signed out. Clink's connection was removed from Copium.", CopiumCardModel.SignOutText(SignOutResult.Revoked));
        string web = "Signed out on this PC. Remove the Clink connection on copium.pics under ShareX & API.";
        Check.Equal(web, CopiumCardModel.SignOutText(SignOutResult.NotSupportedDisconnectOnWebsite));
        Check.Equal(web, CopiumCardModel.SignOutText(SignOutResult.FailedButSignedOutLocally));
        Check.Equal("Signed out on this PC. Your API token still works on copium.pics until you delete it there.", CopiumCardModel.SignOutText(SignOutResult.Manual));
        Check.Null(CopiumCardModel.SignOutText(SignOutResult.NotSignedIn));
    }

    [UnitTest]
    public static void Names_are_isolated_so_a_right_to_left_name_cannot_reorder_the_words_around_it()
    {
        string rtl = "אבג";
        string text = M(Linked(user: rtl)).SignedInText!;
        Check.True(text.Contains(IsoStart + rtl + IsoEnd));
        Check.False(text.Contains("<") || text.Contains("{"), "plain text, no markup is built");
        Check.Equal("<b>x</b>", M(Linked(user: "<b>x</b>")).SignedInText![(("Signed in as " + IsoStart).Length)..^IsoEnd.Length], "markup stays as typed");
    }

    [UnitTest]
    public static void The_start_hint_is_the_normal_one_when_sign_in_is_enabled_and_the_reason_only_when_it_is_not()
    {
        var on = M(Out);
        Check.True(on.SignInEnabled);
        Check.Null(on.SignInDisabledReason);
        Check.True(on.SignInHintVisible);
        Check.Equal("Opens copium.pics in your browser. Approve there and you're done.", on.SignInHintText);
        var off = M(Out, dev: true);
        Check.False(off.SignInEnabled);
        Check.Equal(CopiumCardModel.DevNeedsTokenReason, off.SignInDisabledReason);
    }

    [UnitTest]
    public static void The_no_token_share_advice_shows_only_while_no_token_is_stored()
    {
        Check.True(M(Out).NoTokenHintVisible);
        Check.False(M(Pasted).NoTokenHintVisible);
        Check.False(M(Linked()).NoTokenHintVisible);
        Check.False(M(Linked() with { RemoteCleanupName = "X" }).NoTokenHintVisible, "a failed-revoke sign-in keeps its own cleanup advice only");
        Check.True(M(Out with { RemoteCleanupName = "X" }).NoTokenHintVisible);
    }

    [UnitTest]
    public static void The_size_target_leaves_Copium_Plus_to_the_account_while_signed_in_with_Connect()
    {
        Check.True(CopiumPlusTick.AccountOwnsPlus(true, true));
        Check.False(CopiumPlusTick.AccountOwnsPlus(true, false), "no token: nothing owns it");
        Check.False(CopiumPlusTick.AccountOwnsPlus(false, true), "a pasted token: the user ticks it");
        var tick = new CopiumPlusTick();
        Check.False(tick.Selected("copiumplus", true, alreadyTicked: CopiumPlusTick.AccountOwnsPlus(true, true)), "a pick from the list does not tick it");
        Check.False(tick.FocusLeft("copiumplus", alreadyTicked: true));
        Check.True(tick.Selected("copiumplus", true, alreadyTicked: CopiumPlusTick.AccountOwnsPlus(false, true)));
    }
}
