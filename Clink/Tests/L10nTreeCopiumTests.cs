using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Clink.Screenshots;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Localization slice S3b (Copium sharing: the share window, the consent and welcome windows, the share choices and menu, and the texts
/// of ConsentGate / CopiumPopupModel / CopiumWelcomeModel / CopiumClient / CopiumConnect / CopiumSignInSession). Under <c>qps-ploc</c> the
/// built (never shown, no handle) windows are walked by <see cref="L10nTreeHarness"/>, and every code-set text must carry the pseudo frame.
/// </summary>
internal static class L10nTreeCopiumTests
{
    // ------------------------------------------------------------------ rig

    private const string ClipFile = "Neon Drift 2026.mp4";

    /// <summary>Texts that are not translatable on purpose. Each is commented.</summary>
    private static bool Allow(string text)
    {
        string t = text.Trim();
        // the clip's file name on the share window's card (user data), and a Copium address (a URL)
        if (t == ClipFile || t.StartsWith("https://", StringComparison.Ordinal)) return true;
        return false;
    }

    private static void NoneUnlocalized(DependencyObject root, string what)
    {
        var bad = L10nTreeHarness.Unlocalized(root, Allow);
        Check.Equal(Array.Empty<string>(), bad.ToArray(), what + " (layout error: " + L10nTreeHarness.LastLayoutError + ")");
    }

    private static void NoMissing(string what) => Check.Equal(Array.Empty<string>(), L.Missing.OrderBy(k => k, StringComparer.Ordinal).ToArray(), what + ": keys missing from the catalog");

    private static void Marked(string? text, string what)
    {
        Check.True(!string.IsNullOrEmpty(text) && PseudoLocale.IsPseudo(text), what + ": not from the catalog: \"" + text + "\"");
        string rest = L10nTreeHarness.StripFrames(text!);
        Check.True(L10nTreeHarness.IsAllowed(rest) || Allow(rest), what + ": has a hard-coded part: \"" + text + "\"");
    }

    /// <summary>A Copium failure under qps-ploc: what the user is shown (UserText) is from the catalog, the Message (for the log) is plain English.</summary>
    private static void MarkedUser(Exception? ex, string what)
    {
        Check.NotNull(ex, what + ": a failure was expected");
        Marked(UserText.Of(ex!), what);
        Check.False(PseudoLocale.IsPseudo(ex!.Message), what + ": the Message (the log's text) must stay English: \"" + ex.Message + "\"");
        Check.False(ex.Message.Contains("[!!"), what + ": no pseudo frame in the log text");
    }
    private static ClipEntry Clip(bool archived = false, string? link = null) =>
        new() { Path = Path.Combine(Path.GetTempPath(), "clink-l10n-nonexistent", ClipFile), Size = 190_000_000, Duration = 90, Created = DateTime.Now, ArchivedAt = archived ? DateTime.Now : null, CopiumLink = link };

    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void SetState(ShareWindow w, ShareWindow.FileState state)
    {
        typeof(ShareWindow).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, state);
        typeof(ShareWindow).GetMethod("ShowState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);
    }

    // ------------------------------------------------------------------ the share window

    [UnitTest]
    public static void The_share_window_is_localized_in_every_state_of_its_file()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        // local clip: present, looking (archived, not looked at yet), offline, deleted (local), deleted from the archive
        var cases = new (bool Archived, ShareWindow.FileState State, string What)[]
        {
            (false, ShareWindow.FileState.Present, "local, present"),
            (true, ShareWindow.FileState.Unknown, "archived, looking"),
            (true, ShareWindow.FileState.Offline, "archived, offline"),
            (false, ShareWindow.FileState.Missing, "local, deleted"),
            (true, ShareWindow.FileState.Missing, "archived, deleted"),
            (false, ShareWindow.FileState.Offline, "local, offline"),
        };
        foreach (var c in cases)
        {
            var w = new ShareWindow(Clip(c.Archived));
            SetState(w, c.State);
            NoneUnlocalized(w, "the share window: " + c.What);
            Marked(w.Title, "the window title");
            Marked(w.HintText.Text, "the hint under the name (" + c.What + ")");
            Marked(w.MetaText.Text, "the size and length");
            if (c.State == ShareWindow.FileState.Offline) Marked(w.StatusText.Text, "the offline note");
            Check.True(w.CloseX.ToolTip is string tip && PseudoLocale.IsPseudo(tip), "the close tooltip names Esc through the catalog");
            Check.True(w.CopyButton.ToolTip is string copyTip && PseudoLocale.IsPseudo(copyTip) && copyTip.Contains("Ctrl+V") && copyTip.Contains("Ctrl+C"), "the copy tooltip keeps the key names as placeholders");
        }
        NoMissing("the share window");
    }

    [UnitTest]
    public static void The_share_window_intro_is_localized_for_both_audiences()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        Marked(ShareWindow.IntroFor(true), "the intro with a token");
        Marked(ShareWindow.IntroFor(false), "the intro without a token");
        Marked(ShareWindow.MetaFor(5_000_000, 3725), "the meta line");
        Check.Equal("", ShareWindow.MetaFor(0, 0), "an unknown file has no meta line");
        NoMissing("the share window intro");
    }

    // ------------------------------------------------------------------ the consent and welcome windows

    [UnitTest]
    public static void The_consent_window_is_localized_in_both_modes()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        foreach (bool offer in new[] { false, true })
        {
            var consent = new CopiumConsentWindow(CopiumConsentWindow.Mode.Consent, offerAutoUpload: offer, excludeFromCapture: false);
            NoneUnlocalized(consent, "the consent window (auto-upload offered: " + offer + ")");
            Marked(consent.WarningText.Text, "the public-link warning");
            var confirm = new CopiumConsentWindow(CopiumConsentWindow.Mode.ConfirmCloudSharing, offerAutoUpload: offer, excludeFromCapture: false);
            NoneUnlocalized(confirm, "the cloud-sharing confirmation");
            Marked(confirm.Title, "the confirmation title");
            Marked(confirm.BodyText.Text, "the confirmation body");
            Marked(confirm.EnableButton.Content as string, "the confirmation yes");
            Marked(confirm.NotNowButton.Content as string, "the confirmation cancel");
            Check.Equal(CopiumConsentWindow.WarningTextValue, confirm.WarningText.Text, "the same warning in both modes");
        }
        NoMissing("the consent window");
    }

    [UnitTest]
    public static void The_welcome_window_is_localized_for_a_named_an_unnamed_and_a_long_account()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        foreach (string? name in new string?[] { "Jason", null, "   ", new string('x', 100) })
        {
            var w = new CopiumWelcomeWindow(new CopiumWelcomeModel(true, (_, _) => { }), name, () => false);
            // the account name is user data, isolated (FSI...PDI) by the model: it is cut out of the text before the check
            NoneUnlocalized(w, "the welcome window for " + (name ?? "(null)"));
            Marked(w.SignedInText.Text, "the signed-in line for " + (name ?? "(null)"));
            Check.Equal(name?.Trim() is { Length: > 0 }, w.SignedInText.Text.Contains('⁨'), "a name is isolated, no name has no isolate");
            Check.Equal("[!!", w.StartButton.Content is string s ? s[..3] : "", "Start sharing comes from the catalog");
        }
        NoMissing("the welcome window");
    }

    // ------------------------------------------------------------------ share choices and the menu

    [UnitTest]
    public static void The_share_choices_labels_tips_and_menu_items_come_from_the_catalog()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        Marked(ShareChoices.SplitMainLabel, "SplitMainLabel");
        Marked(ShareChoices.SplitMainName, "SplitMainName");
        Marked(ShareChoices.SplitArrowName, "SplitArrowName");
        Marked(ShareChoices.SplitMainTip, "SplitMainTip");
        Marked(ShareChoices.SplitArrowTip, "SplitArrowTip");
        Check.Equal("▾", ShareChoices.SplitArrowLabel, "the arrow is a glyph");
        foreach (bool token in new[] { false, true })
            foreach (bool cloud in new[] { false, true })
                foreach (bool link in new[] { false, true })
                {
                    string at = $" (token {token}, cloud {cloud}, link {link})";
                    Marked(ShareChoices.ButtonLabel(token, cloud, link), "ButtonLabel" + at);
                    Marked(ShareChoices.ToastLabel(token, cloud, link), "ToastLabel" + at);
                    if (ShareChoices.ButtonName(token, cloud, link) is { } name) Marked(name, "ButtonName" + at);
                    // a split button's main half carries the same label as the bar's: ClipsPane.Selection tells them apart by it
                    if (ShareChoices.IsSplit(token, cloud, link)) Check.Equal(ShareChoices.SplitMainLabel, ShareChoices.ButtonLabel(token, cloud, link));
                }
        var menus = new List<(string What, IReadOnlyList<ShareChoiceItem> Items)>
        {
            ("upload menu", ShareChoices.For(true, true, false)!),
            ("linked menu", ShareChoices.For(true, true, true)!),
            ("trim menu", ShareChoices.ForTrim(true, true)!),
        };
        foreach (var (what, items) in menus)
        {
            var menu = new ContextMenu();   // built like ShareMenu.Show does (not opened: that would create a window)
            foreach (var item in items)
            {
                Marked(item.Header, what + " header");
                Marked(item.ToolTip, what + " tip");
                Check.True(item.Header.Contains('_'), what + ": the catalog value carries the access key: " + item.Header);
                var mi = new MenuItem { Header = item.Header, ToolTip = item.ToolTip };
                AutomationProperties.SetName(mi, item.Header.Replace("_", ""));
                Check.True(PseudoLocale.IsPseudo(AutomationProperties.GetName(mi)) && !AutomationProperties.GetName(mi).Contains('_'), what + ": the accessible name loses the underscore");
                menu.Items.Add(mi);
            }
            NoneUnlocalized(menu, what);
        }
        NoMissing("share choices");
    }

    [UnitTest]
    public static void The_share_choices_are_today_s_english_under_en()
    {
        Check.Equal("Share", ShareChoices.SplitMainLabel);
        Check.Equal("Share file", ShareChoices.SplitMainName);
        Check.Equal("Share options", ShareChoices.SplitArrowName);
        Check.Equal("More share options", ShareChoices.SplitArrowTip);
        Check.Equal("Open the share window: drag the file into Discord, a browser or a folder", ShareChoices.SplitMainTip);
        Check.Equal("Copy link", ShareChoices.ButtonLabel(false, true, true));
        Check.Equal("Share to Copium", ShareChoices.ToastLabel(true, false, false));
        Check.Equal(new[] { "_Upload to Copium", "_Share file…", "S_ave a copy…" }, ShareChoices.For(true, true, false)!.Select(i => i.Header).ToArray());
        Check.Equal(new[] { "_Copy link", "_Share file…", "S_ave a copy…" }, ShareChoices.For(true, true, true)!.Select(i => i.Header).ToArray());
        Check.Equal(new[] { "Save & _upload to Copium", "Save & s_hare file…", "Save & save a _copy…" }, ShareChoices.ForTrim(true, true)!.Select(i => i.Header).ToArray());
        Check.Equal("Copy the clip's Copium link", ShareChoices.For(true, true, true)![0].ToolTip);
    }

    [UnitTest]
    public static void The_plus_tick_rule_has_no_text_of_its_own()
    {
        // CopiumPlusTick, ShareMenu and CopiumNudge show nothing themselves (their texts are the callers' catalog texts): the rule is unchanged
        var tick = new CopiumPlusTick();
        Check.True(tick.Selected("copiumplus", fromOpenList: true, alreadyTicked: false));
        Check.False(tick.Selected("copiumplus", fromOpenList: true, alreadyTicked: true));
        var undecided = Settings.FromJson("{}");
        undecided.CopiumConsentScreenshots = CopiumNudge.AfterScreenshots;
        Check.True(CopiumNudge.IsDue(undecided, DateTime.UtcNow), "the nudge rule is unchanged");
    }

    // ------------------------------------------------------------------ code-set texts: gate, popup model, welcome model

    [UnitTest]
    public static void The_gate_the_popup_model_and_the_welcome_model_texts_are_from_the_catalog()
    {
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            Marked(ConsentGate.CloudOffTitle, "CloudOffTitle");
            Marked(ConsentGate.CloudOffText, "CloudOffText");
            Marked(CopiumPopupModel.ToggleLabel, "ToggleLabel");
            Marked(CopiumPopupModel.SignInLink, "SignInLink");
            Marked(CopiumPopupModel.PastedTokenLine, "PastedTokenLine");
            Marked(CopiumPopupModel.From(new SignInState(SignInStatus.SignedOut), false, false).Line, "the cloud-off popup line");
            foreach (var model in new[]
            {
                CopiumPopupModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "Jason"), true, false),
                CopiumPopupModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "  "), true, false),
                CopiumPopupModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true), true, false),
            })
                Marked(model.Line, "the popup account line " + model.Kind);
            Marked(CopiumWelcomeModel.Title, "welcome Title");
            Marked(CopiumWelcomeModel.AutoUploadLabel, "welcome AutoUploadLabel");
            Marked(CopiumWelcomeModel.AutoUploadDetail, "welcome AutoUploadDetail");
            Marked(CopiumWelcomeModel.ClipsLine, "welcome ClipsLine");
            Marked(CopiumWelcomeModel.ChangeLaterLine, "welcome ChangeLaterLine");
            Marked(CopiumWelcomeModel.StartLabel, "welcome StartLabel");
            Marked(CopiumWelcomeModel.NotNowLabel, "welcome NotNowLabel");
            Marked(CopiumWelcomeModel.CloseLabel, "welcome CloseLabel");
            Marked(CopiumWelcomeModel.SignedInLine("Jason"), "SignedInLine(name)");
            Marked(CopiumWelcomeModel.SignedInLine(null), "SignedInLine(none)");
            Marked(CopiumConsentWindow.WarningTextValue, "the consent warning");
            NoMissing("gate and models");
        }
        // English is today's text
        Check.Equal("Cloud sharing is off", ConsentGate.CloudOffTitle);
        Check.Equal("Cloud sharing is off — enable it under App → Cloud sharing", ConsentGate.CloudOffText);
        Check.Equal("Auto-upload screenshots", CopiumPopupModel.ToggleLabel);
        Check.Equal("Sign in with Copium", CopiumPopupModel.SignInLink);
        Check.Equal("Copium: API token", CopiumPopupModel.PastedTokenLine);
        Check.Equal("Copium: ⁨Jason⁩", CopiumPopupModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "Jason"), true, false).Line);
        Check.Equal("Copium: signed in", CopiumPopupModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: ""), true, false).Line);
        Check.Equal("Close", CopiumWelcomeModel.CloseLabel);
        Check.Equal("Signed in as ⁨Jason⁩", CopiumWelcomeModel.SignedInLine("Jason"));
    }

    // ------------------------------------------------------------------ CopiumClient

    private sealed class Canned : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public Canned(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_answer(request));
    }

    private static HttpResponseMessage Reply(int code, string body = "") => new((HttpStatusCode)code) { Content = new StringContent(body) };

    private static Exception? Failure(Func<Task> act)
    {
        try { Task.Run(act).GetAwaiter().GetResult(); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>The failure of an upload of a small file of the given name through a canned server.</summary>
    private static Exception? UploadFails(string file, Func<HttpRequestMessage, HttpResponseMessage> server, string token = "eup_secret")
    {
        using var dir = new TempDir();
        string path = dir.File(file);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var old = CopiumClient.HttpOverride;
        CopiumClient.HttpOverride = new HttpClient(new Canned(server));
        try { return Failure(() => CopiumClient.UploadAsync(token, path, "t", null, CancellationToken.None)); }
        finally { CopiumClient.HttpOverride = old; }
    }

    private sealed class Stall : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [UnitTest]
    public static void The_client_errors_are_from_the_catalog_with_the_servers_text_left_alone()
    {
        string serverText = "Weird {error} text [!! not ours !!]";
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            MarkedUser(Failure(() => CopiumClient.UploadAsync(" ", "x.mp4", null, null, CancellationToken.None)), "no token");
            foreach (string file in new[] { "a.mp4", "a.png" })
            {
                MarkedUser(UploadFails(file, _ => Reply(302)), file + " redirect");
                MarkedUser(UploadFails(file, _ => Reply(413)), file + " 413");
                MarkedUser(UploadFails(file, _ => Reply(401)), file + " 401");
                MarkedUser(UploadFails(file, _ => Reply(403, """{"error":"File blocked"}""")), file + " 403");
                MarkedUser(UploadFails(file, _ => Reply(429)), file + " 429");
                MarkedUser(UploadFails(file, _ => Reply(502, "<html>bad gateway</html>")), file + " 5xx");
                MarkedUser(UploadFails(file, _ => Reply(400, """{"error":"Unsupported FORMAT"}""")), file + " 400 type");
                MarkedUser(UploadFails(file, _ => Reply(418)), file + " other");
                MarkedUser(UploadFails(file, _ => Reply(200, "<html>portal</html>")), file + " not JSON");
                MarkedUser(UploadFails(file, _ => Reply(200, "[]")), file + " not an object");
                MarkedUser(UploadFails(file, _ => Reply(200, "{}")), file + " no link");
                MarkedUser(UploadFails(file, _ => Reply(200, """{"link":"http://insecure.example/x"}""")), file + " bad link");
                MarkedUser(UploadFails(file, _ => throw new HttpRequestException("offline")), file + " offline");
                MarkedUser(UploadFails(file, _ => throw new HttpRequestException("dropped", new IOException())), file + " dropped");
                MarkedUser(UploadFails(file, _ => throw new TaskCanceledException("slow")), file + " timed out");
            }
            // Copium's own error text is the server's: it is not translated, not touched, and not read as a template
            var refused = (CopiumHttpException)UploadFails("a.png", _ => Reply(403, "{\"error\":\"" + serverText.Replace("\"", "'") + "\"}"))!;
            Check.Contains(refused.Message, "Weird {error} text");
            Check.Contains(refused.Message, "not ours");
            Check.Contains(UserText.Of(refused), "Weird {error} text");
            Check.Equal(403, refused.StatusCode);
            // the answer's body that stalls
            var oldTimeout = CopiumClient.BodyTimeout;
            CopiumClient.BodyTimeout = TimeSpan.FromMilliseconds(150);
            try
            {
                var stalled = UploadFails("a.mp4", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Stall()) });
                Check.True(stalled is TimeoutException, "a stalled body times out: " + stalled);
                MarkedUser(stalled, "stalled body");
            }
            finally { CopiumClient.BodyTimeout = oldTimeout; }
            NoMissing("the client");
        }
        // English is today's text
        Check.Equal("Copium refused the upload (HTTP 418)", CopiumClient.ErrorMessage(418, ""));
        Check.Equal("Copium rejected the token (HTTP 401)", CopiumClient.ErrorMessage(401, ""));
        Check.Equal("Clip is larger than your Copium upload limit (HTTP 413)", CopiumClient.ErrorMessage(413, ""));
        Check.Equal("Screenshot is larger than your Copium upload limit (Too big)", CopiumClient.ErrorMessage(413, """{"message":"Too big"}""", "Screenshot"));
        Check.Equal("Too many uploads, wait a bit (HTTP 429)", CopiumClient.ErrorMessage(429, ""));
        Check.Equal("Copium is having trouble (HTTP 503), try again later", CopiumClient.ErrorMessage(503, ""));
        Check.Equal("Copium did not accept the image (Unsupported media)", CopiumClient.ErrorMessage(415, """{"error":"Unsupported media"}""", "Screenshot"));
        var offline = UploadFails("a.mp4", _ => throw new HttpRequestException("offline"));
        Check.Equal("Can't reach Copium - check your connection", offline!.Message);
        Check.Equal("Copium sent an unexpected answer (HTTP 200, no link)", UploadFails("a.mp4", _ => Reply(200, "{}"))!.Message);
        Check.Equal("Copium sent an unexpected redirect (HTTP 302)", UploadFails("a.mp4", _ => Reply(302))!.Message);
        Check.Equal("No Copium token set. Create one under ShareX & API on copium.pics and paste it in Clink.", Failure(() => CopiumClient.UploadAsync("", "x.mp4", null, null, CancellationToken.None))!.Message);
    }

    // ------------------------------------------------------------------ CopiumConnect and the sign-in session

    private static ConnectException? ConnectFails(Func<HttpRequestMessage, HttpResponseMessage> server, Func<Task> act)
    {
        var old = CopiumClient.HttpOverride;
        CopiumClient.HttpOverride = new HttpClient(new Canned(server));
        try { return Failure(act) as ConnectException; }
        finally { CopiumClient.HttpOverride = old; }
    }

    private const string Token = "eup_abcdefghijklmnopqrstuvwxyz0123";

    [UnitTest]
    public static void The_connect_messages_are_plain_english_and_the_card_text_comes_from_the_failure_enum()
    {
        // ConnectException / SignInState messages are never shown (CopiumCardModel.FailureLine reads the enum): they stay English whatever the language
        var start = new ConnectStart("ABCD-EFGH", "poll", new Uri("https://copium.pics/connect?code=ABCD-EFGH"), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(2));
        var oldDelay = CopiumConnect.Delay;
        CopiumConnect.Delay = (_, ct) => Task.Delay(1, ct);
        try
        {
            Func<Task> begin = () => CopiumConnect.StartAsync("PC", "n", CancellationToken.None);
            Func<Task> wait = () => CopiumConnect.WaitAsync(start, CancellationToken.None);
            Func<Task> account = () => CopiumConnect.GetAccountAsync(Token, CancellationToken.None);
            var cases = new (string Expected, Func<HttpRequestMessage, HttpResponseMessage> Server, Func<Task> Act)[]
            {
                ("This version of Copium cannot connect Clink yet", _ => Reply(400, """{"error":"unknown app"}"""), begin),
                ("Copium sent an unexpected answer (HTTP 418)", _ => Reply(418), begin),
                ("Too many tries, wait a few minutes", _ => Reply(429), begin),
                ("Copium is having trouble, try again later", _ => Reply(503), begin),
                ("Copium sent an unexpected redirect (HTTP 302)", _ => Reply(302), begin),
                ("Can't reach Copium - check your connection", _ => throw new HttpRequestException("offline"), begin),
                ("That connection expired - press Sign in again", _ => Reply(410), wait),
                ("The connection was cancelled in the browser", _ => Reply(403, """{"error":"no"}"""), wait),
                ("Copium sent an unexpected answer (token)", _ => Reply(200, """{"token":"x","username":"u"}"""), wait),
                ("Copium does not accept this token", _ => Reply(401), account),
                ("Copium could not be reached properly, try again later", _ => Reply(403, "<html>challenge</html>"), account),
                ("Too many tries, wait a bit", _ => Reply(429), account),
                ("Copium sent an unexpected answer (no username)", _ => Reply(200, """{"username":""}"""), account),
                ("Copium sent an unexpected answer", _ => Reply(200, "nope"), account),
            };
            foreach (string language in new[] { "qps-ploc", "en" })
                using (L.Scope(language))
                {
                    L.ClearMissing();
                    foreach (var (expected, server, act) in cases)
                    {
                        var error = ConnectFails(server, act);
                        Check.NotNull(error, expected + ": a ConnectException was expected");
                        Check.Equal(expected, error!.Message, "plain English under " + language);
                    }
                    var short1 = new ConnectStart("ABCD-EFGH", "poll", start.ApproveUrl, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2));
                    Check.Equal("The code ran out - press Sign in again", ConnectFails(_ => Reply(202), () => CopiumConnect.WaitAsync(short1, CancellationToken.None))!.Message);
                    NoMissing("the connect errors under " + language);
                }
            // the card line is the enum's, from the catalog, for every failure
            using (L.Scope("qps-ploc"))
            {
                L.ClearMissing();
                foreach (SignInFailure failure in Enum.GetValues<SignInFailure>())
                    Marked(CopiumCardModel.FailureLine(failure, "an English message that is never shown"), "the card line for " + failure);
                Marked(CopiumCardModel.FailureLine(null), "the card line for no failure");
                Check.False(CopiumCardModel.FailureLine(SignInFailure.Network, "an English message that is never shown").Contains("never shown"), "the message is not on the card");
                NoMissing("the card failure lines");
            }
        }
        finally { CopiumConnect.Delay = oldDelay; }
    }

    [UnitTest]
    public static void The_sign_in_session_messages_are_plain_english_whatever_the_language()
    {
        CopiumSignInSession Session(Settings s, bool dev = false) => new(s, _ => { }, () => { }) { IsDevInstance = () => dev, DevKeepsToken = () => false };
        var oldDelay = CopiumConnect.Delay;
        var oldHttp = CopiumClient.HttpOverride;
        try
        {
            foreach (string language in new[] { "qps-ploc", "en" })
                using (L.Scope(language))
                {
                    L.ClearMissing();
                    var off = Settings.FromJson("{}");
                    off.CloudSharingEnabled = false;
                    var s1 = Session(off);
                    s1.Start("PC");
                    Check.Equal(SignInStatus.Failed, s1.Current.Status);
                    Check.Equal(SignInFailure.CloudSharingOff, s1.Current.Failure);

                    var s2 = Session(Settings.FromJson("{}"), dev: true);
                    s2.Start("PC");
                    Check.Equal("Not available in a test instance without --dev-token", s2.Current.Message);

                    var linked = Settings.FromJson("{}");
                    linked.SetCopiumToken(Token, "Jason", viaConnect: true);
                    var s3 = Session(linked);
                    s3.Start("PC");
                    Check.Equal(SignInFailure.AlreadySignedIn, s3.Current.Failure);
                    Check.Equal("Already signed in to Copium - sign out first to use another account", s3.Current.Message);

                    CopiumClient.HttpOverride = new HttpClient(new Canned(_ => throw new InvalidOperationException("surprise")));
                    var s4 = Session(Settings.FromJson("{}"));
                    s4.Start("PC");
                    Check.True(s4.WhenIdleAsync().Wait(TimeSpan.FromSeconds(20)), "the failing run ended");
                    Check.Equal(SignInFailure.Unexpected, s4.Current.Failure);
                    Check.Equal("Something went wrong signing in", s4.Current.Message);

                    // what the card shows for each is the enum's text
                    if (language == "qps-ploc")
                    {
                        Marked(CopiumCardModel.FailureLine(s3.Current.Failure, s3.Current.Message), "the card line for 'already signed in'");
                        NoMissing("the sign-in session");
                    }
                }
        }
        finally { CopiumConnect.Delay = oldDelay; CopiumClient.HttpOverride = oldHttp; }
    }

    // ------------------------------------------------------------------ English for the log, the user's language for the screen (CopiumErrors)

    [UnitTest]
    public static void A_copium_failure_logs_english_and_shows_the_users_language()
    {
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            var timeout = CopiumErrors.Shown(m => new TimeoutException(m), Note.Of("Upload timed out after {minutes} min - the connection is too slow or stalled", ("minutes", 30)));
            Check.Equal("Upload timed out after 30 min - the connection is too slow or stalled", timeout.Message);
            Marked(UserText.Of(timeout), "UserText");
            Check.True(!PseudoLocale.IsPseudo(timeout.Message));
            // one wrapper (Task.Run / Wait) is looked through; two are not
            var one = new AggregateException(timeout);
            Check.Equal(UserText.Of(timeout), UserText.Of(one));
            var two = new AggregateException(timeout, new InvalidOperationException("x"));
            Check.Equal(two.Message, UserText.Of(two));
            // an exception nobody attached a text to shows its own message
            Check.Equal("plain", UserText.Of(new InvalidOperationException("plain")));
            // the HTTP refusal carries both too, and the server's text is left alone in both
            var refusal = new CopiumHttpException(503, CopiumClient.ErrorNote(503, "{\"error\":\"down\"}"), "down");
            Check.Equal("Copium is having trouble (down), try again later", refusal.Message);
            Marked(UserText.Of(refusal), "refusal UserText");
            Check.Contains(UserText.Of(refusal), "down");
            NoMissing("CopiumErrors");
        }
        using (L.Scope("en"))
        {
            var e = CopiumErrors.Shown(m => new IOException(m), Note.Of("Could not read the clip: {error}", ("error", "gone")));
            Check.Equal("Could not read the clip: gone", e.Message);
            Check.Equal(e.Message, UserText.Of(e));
            Check.Equal(typeof(IOException), e.GetType());
        }
    }

    [UnitTest]
    public static void A_file_that_cannot_be_read_during_the_upload_says_so_in_both_languages()
    {
        using var dir = new TempDir();
        string path = dir.File("locked.png");
        File.WriteAllBytes(path, new byte[4096]);
        var old = CopiumClient.HttpOverride;
        // the server side reads the body, which reads the file: a byte range locked by another handle makes that read fail (an IOException)
        CopiumClient.HttpOverride = new HttpClient(new Canned(req => { req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult(); return Reply(200, "{}"); }));
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        holder.Lock(0, 4096);
        try
        {
            foreach (string language in new[] { "qps-ploc", "en" })
                using (L.Scope(language))
                {
                    L.ClearMissing();
                    var error = Failure(() => CopiumClient.UploadAsync("eup_secret", path, null, null, CancellationToken.None));
                    Check.True(error is IOException, "an IOException: " + error);
                    Check.True(error!.Message.StartsWith("Could not read the screenshot: ", StringComparison.Ordinal), "English Message: " + error.Message);
                    if (language == "qps-ploc") { MarkedUser(error, "read error"); NoMissing("the read error"); }
                    else Check.Equal(error.Message, UserText.Of(error));
                }
        }
        finally { holder.Unlock(0, 4096); CopiumClient.HttpOverride = old; }
    }

    [UnitTest]
    public static void The_screenshot_service_logs_the_english_message_and_shows_the_users_text()
    {
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => logged.Enqueue(line);
        Log.Line += Collect;
        try
        {
            using var scope = L.Scope("qps-ploc");
            L.ClearMissing();
            using var dir = new TempDir();
            var rig = ScreenshotUploadTests.Make(dir, uploader: (_, _, _, _, _, _) => Task.FromException<CopiumUpload>(
                CopiumErrors.Shown(m => new TimeoutException(m), Note.Of("Upload timed out after {minutes} min - the connection is too slow or stalled", ("minutes", 30)))));
            ScreenshotUploadTests.Capture(rig);
            string english = "Upload timed out after 30 min - the connection is too slow or stalled";
            Check.True(logged.Any(l => l.Contains("screenshot: upload failed: " + english)), "the log has the English Message");
            Check.False(logged.Any(l => l.Contains("screenshot: upload failed: ") && l.Contains("[!!")), "no pseudo text in the log");
            Marked(rig.Toasts.LastProblemText, "the problem toast text");
            NoMissing("the screenshot failure");
        }
        finally { Log.Line -= Collect; }
    }
    // ------------------------------------------------------------------ every key the slice's sources ask for is in a catalog, and every copium.json key is used

    [UnitTest]
    public static void Every_key_in_the_slice_sources_is_in_a_catalog_and_every_copium_key_is_used()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // not run from a checkout: nothing to read
        var copium = L10nSourceScan.AreaKeys(root, "copium");
        var core = L10nSourceScan.AreaKeys(root, "core");
        // a few words are another slice's keys (a key lives in one file only): the sign-in texts S1 wrote in main.json, and the share-window words S2 wrote in clips.json
        // every other catalog file of the same language (a key lives in one file only)
        var others = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Locales", "en"), "*.json"))
        {
            string area = Path.GetFileNameWithoutExtension(file);
            if (area is "copium" or "core") continue;
            others.UnionWith(L10nSourceScan.AreaKeys(root, area));
        }
        var csFiles = new[]
        {
            @"UI\ShareWindow.xaml.cs", @"UI\CopiumConsentWindow.xaml.cs", @"UI\CopiumWelcomeWindow.xaml.cs", @"UI\CopiumPlusTick.cs", @"UI\ShareMenu.cs",
            @"Services\ShareChoices.cs", @"Services\ConsentGate.cs", @"Services\CopiumPopupModel.cs", @"Services\CopiumWelcomeModel.cs", @"Services\CopiumClient.cs",
            @"Services\CopiumConnect.cs", @"Services\CopiumSignInSession.cs", @"Screenshots\CopiumNudge.cs", @"Services\ShareFitModel.cs",
            @"Engine\ShareTranscoder.cs", @"Engine\ShareProbe.cs", @"Services\ShareCache.cs", @"Services\AppController.ShareFit.cs", @"Services\CopiumErrors.cs",   // (the throw sites of the copy-failure reasons: reason|... keys)
        };
        var hits = L10nSourceScan.CSharp(root, csFiles, new[] { L10nSourceScan.T, L10nSourceScan.Plural, L10nSourceScan.NoteOf });   // (Note.Of: ConsentGate, ShareFitModel, CopiumClient and the share-copy throw sites keep their sentences as notes)
        hits.AddRange(L10nSourceScan.Xaml(root, new[] { @"UI\ShareWindow.xaml", @"UI\CopiumConsentWindow.xaml", @"UI\CopiumWelcomeWindow.xaml" }));
        var used = hits.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var problems = hits.Where(h => !copium.Contains(h.Key) && !core.Contains(h.Key) && !others.Contains(h.Key)).Select(h => h.File + ": " + h.Key).Distinct().ToArray();
        Check.True(hits.Count > 80, "found the literals of the slice's sources: " + hits.Count);
        Check.Equal(Array.Empty<string>(), problems, "keys the sources ask for that no catalog has");
        var unused = copium.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Check.Equal(Array.Empty<string>(), unused, "copium.json keys no source of the slice uses");
        var shared = copium.Where(k => core.Contains(k) || others.Contains(k)).ToArray();
        Check.Equal(Array.Empty<string>(), shared, "copium.json keys that another catalog file has too");
    }
}
