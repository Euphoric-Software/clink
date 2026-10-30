using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>"Sign in with Copium" slice C2: the first-link welcome (texts, once-only answer) and the Copium section of the version popup.</summary>
internal static class CopiumWelcomeTests
{
    private sealed class Calls
    {
        public readonly List<(bool Start, bool Auto)> List = new();
        public void Apply(bool start, bool auto) => List.Add((start, auto));
    }

    [UnitTest]
    public static void The_welcome_texts_are_the_agreed_ones_and_the_name_is_isolated_plain_text()
    {
        Check.Equal("You're connected to Copium", CopiumWelcomeModel.Title);
        Check.Equal("Automatically upload every screenshot I take", CopiumWelcomeModel.AutoUploadLabel);
        Check.Equal("Each screenshot is uploaded to your Copium account and gets a link you can share. Anyone with the link can see it.", CopiumWelcomeModel.AutoUploadDetail);
        Check.Equal("Clips are only uploaded when you share them.", CopiumWelcomeModel.ClipsLine);
        Check.Equal("You can change this any time in Clink's settings or from the version number at the bottom of the window.", CopiumWelcomeModel.ChangeLaterLine);
        Check.Equal("Start sharing", CopiumWelcomeModel.StartLabel);
        Check.Equal("Not now", CopiumWelcomeModel.NotNowLabel);
        Check.Equal("Signed in as \u2068Jason\u2069", CopiumWelcomeModel.SignedInLine(" Jason "));
        Check.Equal("Signed in", CopiumWelcomeModel.SignedInLine(null));
        Check.Equal("Signed in", CopiumWelcomeModel.SignedInLine("   "));
        string cut = CopiumWelcomeModel.SignedInLine(new string('x', 100));
        Check.Equal("Signed in as \u2068" + new string('x', 40) + "…\u2069", cut);
        Check.Equal("Signed in as \u2068<b>&amp;\u2069", CopiumWelcomeModel.SignedInLine("<b>&amp;"), "markup stays as typed");
    }

    [UnitTest]
    public static void The_welcome_answer_reaches_the_controller_once_on_every_path()
    {
        // Start sharing, box ticked / unticked
        foreach (bool box in new[] { true, false })
        {
            var c = new Calls();
            var m = new CopiumWelcomeModel(true, c.Apply);
            Check.True(m.StartSharing(box));
            Check.False(m.StartSharing(!box), "a second answer");
            Check.False(m.NotNow(), "Not now after Start sharing");
            Check.False(m.Ended(appShuttingDown: false), "and the window closing after it");
            Check.Equal(1, c.List.Count);
            Check.True(c.List[0].Start);
            Check.Equal(box, c.List[0].Auto);
        }
        // Not now / Esc / the close button / Alt+F4 all end in NotNow() or Ended(false): no consent, the box is ignored
        foreach (int path in new[] { 0, 1 })
        {
            var c = new Calls();
            var m = new CopiumWelcomeModel(true, c.Apply);
            Check.True(path == 0 ? m.NotNow() : m.Ended(appShuttingDown: false));
            m.NotNow(); m.Ended(false); m.StartSharing(true);
            Check.Equal(1, c.List.Count);
            Check.False(c.List[0].Start, "Not now never grants consent");
            Check.False(c.List[0].Auto);
        }
        // app shutdown while open: skipped (nothing applied), and a late answer is dropped too
        var s = new Calls();
        var sm = new CopiumWelcomeModel(true, s.Apply);
        Check.True(sm.Ended(appShuttingDown: true));
        Check.True(sm.Answered);
        Check.False(sm.NotNow());
        Check.False(sm.StartSharing(true));
        Check.Equal(0, s.List.Count);
    }

    [UnitTest]
    public static void The_welcome_answer_is_once_only_under_a_race()
    {
        var c = new Calls();
        var gate = new object();
        var m = new CopiumWelcomeModel(true, (a, b) => { lock (gate) c.Apply(a, b); });
        Parallel.For(0, 64, i => { if (i % 2 == 0) m.NotNow(); else m.StartSharing(true); });
        Check.Equal(1, c.List.Count);
    }

    [UnitTest]
    public static void The_welcome_never_touches_clips_and_the_box_starts_from_the_controller()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var fresh = Settings.FromJson("{}");
        using var app = new AppController(fresh, new Updater(fresh));
        Check.True(app.WelcomeInitialAutoUpload, "no prior consent: ticked");
        var m = new CopiumWelcomeModel(app.WelcomeInitialAutoUpload, app.ApplyWelcome);
        Check.True(m.InitialAutoUpload);
        string clipSettings(Settings s) => $"{s.ClipsFolder}|{s.Fps}|{s.CopiumPlus}|{s.CloudSharingEnabled}";
        string before = clipSettings(fresh);
        m.StartSharing(true);
        Check.True(fresh.CopiumConsent);
        Check.True(fresh.ScreenshotUpload);
        Check.Equal(before, clipSettings(fresh), "clips and everything else untouched");

        // prior consent with the box off: the box starts as the user left it
        var agreed = Settings.FromJson("{}");
        agreed.CopiumConsent = true;
        agreed.ScreenshotUpload = false;
        using var app2 = new AppController(agreed, new Updater(agreed));
        Check.False(app2.WelcomeInitialAutoUpload);
        // Not now keeps consent, changes nothing else
        new CopiumWelcomeModel(app2.WelcomeInitialAutoUpload, app2.ApplyWelcome).NotNow();
        Check.True(agreed.CopiumConsent);
        Check.False(agreed.ScreenshotUpload);
    }

    [UnitTest]
    public static void The_welcome_window_shows_the_texts_and_answers_once_when_closed_any_way()
    {
        var c = new Calls();
        var m = new CopiumWelcomeModel(true, c.Apply);
        var w = new CopiumWelcomeWindow(m, "Jason", () => false);
        Check.Equal("You're connected to Copium", w.TitleText.Text);
        Check.Equal("Signed in as \u2068Jason\u2069", w.SignedInText.Text);
        Check.True(w.AutoUploadCheck.IsChecked == true, "pre-ticked");
        Check.Equal("Automatically upload every screenshot I take", w.AutoUploadLabel.Text);
        Check.Equal("Start sharing", w.StartButton.Content as string);
        Check.Equal("Not now", w.NotNowButton.Content as string);
        Check.False(w.StartButton.IsDefault, "Enter must never grant consent");
        Check.True(w.NotNowButton.IsDefault, "Enter is Not now");
        Check.True(w.NotNowButton.IsCancel, "Esc is Not now");
        Check.False(System.Windows.Input.KeyboardNavigation.GetAcceptsReturn(w.AutoUploadCheck), "Enter on the first-focus box must reach Not now, not toggle the box");
        Check.Equal(0, c.List.Count, "nothing is applied until the window ends");
        w.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));   // never shown: the input guard ignores it
        Check.Equal(0, c.List.Count, "a click on a window that was never shown applies nothing");
        w.Close();   // the close button / Alt+F4 / a shutdown that is not ours
        Check.Equal(1, c.List.Count);
        Check.False(c.List[0].Start, "closing is Not now");

        var c2 = new Calls();
        var w2 = new CopiumWelcomeWindow(new CopiumWelcomeModel(false, c2.Apply), "x", () => false);
        Check.True(w2.AutoUploadCheck.IsChecked == false, "initial value from the controller");
        w2.NotNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Equal(1, c2.List.Count, "the click answered and closed; the close added nothing");
        Check.False(c2.List[0].Start);

        var c3 = new Calls();
        var w3 = new CopiumWelcomeWindow(new CopiumWelcomeModel(true, c3.Apply), "x", () => true);
        w3.Close();
        Check.Equal(0, c3.List.Count, "closed by the app's shutdown: skipped");
    }

    [UnitTest]
    public static void A_start_click_inside_the_guard_is_ignored_and_after_it_starts_sharing_once_with_the_box()
    {
        long now = 1_000;
        var c = new Calls();
        var w = new CopiumWelcomeWindow(new CopiumWelcomeModel(true, c.Apply), "x", () => false) { Clock = () => now };
        w.AutoUploadCheck.IsChecked = false;
        w.MarkShown();
        now += 100;
        w.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Equal(0, c.List.Count, "100 ms after it appeared: not an answer");
        now += 400;
        w.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        w.Close();
        Check.Equal(1, c.List.Count, "once");
        Check.True(c.List[0].Start);
        Check.False(c.List[0].Auto, "the box value (unticked) is what is applied");

        var c2 = new Calls();
        now = 1_000;
        var w2 = new CopiumWelcomeWindow(new CopiumWelcomeModel(true, c2.Apply), "x", () => false) { Clock = () => now };
        w2.MarkShown();
        now += 500;
        w2.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.True(c2.List.Count == 1 && c2.List[0].Start && c2.List[0].Auto, "ticked box: auto-upload");
    }

    // ------------------------------------------------------------------ the version popup

    private static SignInState Connect(string? name = "Jason") => new(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: name);

    [UnitTest]
    public static void The_version_popup_shows_the_copium_entry_for_each_state()
    {
        var connect = CopiumPopupModel.From(Connect(), cloudSharingOn: true, screenshotUploadShown: false);
        Check.Equal(CopiumPopupKind.Connect, connect.Kind);
        Check.Equal("Copium: \u2068Jason\u2069", connect.Line);
        Check.True(connect.ToggleVisible);
        Check.True(connect.ToggleEnabled);
        Check.False(connect.ToggleChecked);
        Check.False(connect.LinkVisible);
        Check.Equal("Auto-upload screenshots", CopiumPopupModel.ToggleLabel);

        var on = CopiumPopupModel.From(Connect(), true, screenshotUploadShown: true);
        Check.True(on.ToggleChecked, "mirrors the Screenshots tab's box (setting and consent)");

        var pasted = CopiumPopupModel.From(new SignInState(SignInStatus.SignedOut, HasToken: true), true, true);
        Check.Equal(CopiumPopupKind.PastedToken, pasted.Kind);
        Check.Equal("Copium: API token", pasted.Line);
        Check.True(pasted.ToggleVisible);
        Check.True(pasted.ToggleChecked);

        var none = CopiumPopupModel.From(new SignInState(SignInStatus.SignedOut), true, false);
        Check.Equal(CopiumPopupKind.SignedOut, none.Kind);
        Check.True(none.LinkVisible);
        Check.False(none.ToggleVisible);
        Check.Null(none.Line);
        Check.Equal("Sign in with Copium", CopiumPopupModel.SignInLink);
        var waiting = CopiumPopupModel.From(new SignInState(SignInStatus.Waiting, Code: "AB2C-DE9F"), true, false);
        Check.True(waiting.LinkVisible, "still just the link while a sign-in runs: it shows the card");

        var off = CopiumPopupModel.From(Connect(), cloudSharingOn: false, screenshotUploadShown: true);
        Check.Equal(CopiumPopupKind.CloudOff, off.Kind);
        Check.Equal(ConsentGate.CloudOffText, off.Line);
        Check.True(off.LineMuted);
        Check.False(off.ToggleVisible);
        Check.False(off.LinkVisible);

        // a Connect sign-in whose token is not stored yet is not "signed in"
        Check.Equal(CopiumPopupKind.SignedOut, CopiumPopupModel.From(new SignInState(SignInStatus.Starting, LinkedViaConnect: true), true, false).Kind);
        Check.Equal("Copium: signed in", CopiumPopupModel.From(Connect(null), true, false).Line);
        Check.Equal("Copium: \u2068" + new string('y', 40) + "…\u2069", CopiumPopupModel.From(Connect(new string('y', 90)), true, false).Line);
    }

    [UnitTest]
    public static void The_popup_toggle_is_available_without_consent_because_ticking_it_asks()
    {
        // consent is asked by the Screenshots box's handler when the box is ticked; the model offers the toggle either way
        var m = CopiumPopupModel.From(Connect(), true, screenshotUploadShown: false);
        Check.True(m.ToggleEnabled);
        Check.False(m.ToggleChecked, "no consent: shown unticked");
        Check.False(MainWindow.ScreenshotChecksFor(new Settings { ScreenshotUpload = true, CopiumConsent = false }).Upload, "the box the popup mirrors never shows ticked without consent");
    }
}
