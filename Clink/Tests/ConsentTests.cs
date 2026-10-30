using System.IO;
using System.Net.Http;
using Clink.Screenshots;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Slice S7: sharing is opt-in (the "Share with Copium?" gate), the global cloud kill switch with its two-step re-enable, and the
/// Features switches (Clips / Screenshots). The gate is pure (its pop-up is a seam); the controller is built but never started.
/// </summary>
internal static class ConsentTests
{
    private static ConsentGate Gate(Settings s, ConsentChoice answer = ConsentChoice.Enable, bool autoUpload = false, List<ConsentContext>? asked = null, List<string>? notices = null)
    {
        return new ConsentGate(s)
        {
            Prompt = ctx => { asked?.Add(ctx); return Task.FromResult(new ConsentAnswer(answer, autoUpload)); },
            Notice = (_, title, text) => notices?.Add(title + ": " + text),
        };
    }

    /// <summary>Waits for a task by pumping the dispatcher: the test thread has a dispatcher context after the first Pump, and a blocking .Result on an awaiting task would deadlock on it.</summary>
    private static T Wait<T>(Task<T> task)
    {
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the task");
        return task.Result;
    }

    // ------------------------------------------------------------------ the gate

    [UnitTest]
    public static void A_share_without_consent_asks_and_goes_on_only_for_Enable()
    {
        foreach (var ctx in new[] { ConsentContext.ClipShare, ConsentContext.ShareLast, ConsentContext.TrimShare, ConsentContext.ScreenshotUploadManual, ConsentContext.ScreenshotUploadSetting, ConsentContext.TokenSaved })
        {
            var s = Settings.FromJson("{}");
            s.CopiumToken = "eup_x";
            var asked = new List<ConsentContext>();
            Check.False(Wait(Gate(s, ConsentChoice.NotNow, asked: asked).EnsureAsync(ctx)), ctx + ": Not now");
            Check.Equal(new[] { ctx }, asked.ToArray());
            Check.False(s.CopiumConsent, "nothing changed");
            Check.False(s.CopiumConsentDontAsk);
            Check.True(Wait(Gate(s, ConsentChoice.Enable, asked: asked).EnsureAsync(ctx)), ctx + ": Enable");
            Check.True(s.CopiumConsent);
            Check.False(s.ScreenshotUpload, "the automatic upload is its own choice");
            asked.Clear();
            Check.True(Wait(Gate(s, ConsentChoice.NotNow, asked: asked).EnsureAsync(ctx)), "agreed already: no second question");
            Check.Equal(0, asked.Count);
        }
    }

    [UnitTest]
    public static void The_automatic_upload_box_counts_only_where_it_is_offered()
    {
        foreach (var (ctx, offered) in new[] { (ConsentContext.Proactive, true), (ConsentContext.ScreenshotUploadManual, true), (ConsentContext.ClipShare, false), (ConsentContext.ScreenshotUploadSetting, false), (ConsentContext.TokenSaved, false) })
        {
            Check.Equal(offered, ConsentGate.OffersAutoUpload(ctx), ctx.ToString());
            var s = Settings.FromJson("{}");
            Check.True(Wait(Gate(s, ConsentChoice.Enable, autoUpload: true).EnsureAsync(ctx)));
            Check.Equal(offered, s.ScreenshotUpload, ctx + ": ticked in the pop-up");
            var unticked = Settings.FromJson("{}");
            Check.True(Wait(Gate(unticked, ConsentChoice.Enable, autoUpload: false).EnsureAsync(ctx)));
            Check.False(unticked.ScreenshotUpload, ctx + ": not ticked");
        }
    }

    [UnitTest]
    public static void Dont_ask_again_stops_unasked_prompts_but_an_explicit_try_still_asks()
    {
        var s = Settings.FromJson("{}");
        var asked = new List<ConsentContext>();
        Check.False(Wait(Gate(s, ConsentChoice.DontAskAgain, asked: asked).EnsureAsync(ConsentContext.Proactive)));
        Check.True(s.CopiumConsentDontAsk);
        Check.False(s.CopiumConsent);
        Check.False(Wait(Gate(s, ConsentChoice.NotNow, asked: asked).EnsureAsync(ConsentContext.Proactive)));
        Check.Equal(1, asked.Count, "the proactive ask is not shown again");
        Check.False(Wait(Gate(s, ConsentChoice.NotNow, asked: asked).EnsureAsync(ConsentContext.ClipShare)));
        Check.Equal(2, asked.Count, "an explicit share shows it");
        Check.True(Wait(Gate(s, ConsentChoice.Enable, asked: asked).EnsureAsync(ConsentContext.ScreenshotUploadManual)));
        Check.True(s.CopiumConsent, "and Enable still works after Don't ask again");
    }

    [UnitTest]
    public static void With_cloud_sharing_off_the_gate_refuses_with_the_notice_and_never_shows_the_pop_up()
    {
        var s = Settings.FromJson("{}");
        s.CloudSharingEnabled = false;
        var asked = new List<ConsentContext>();
        var notices = new List<string>();
        foreach (var ctx in Enum.GetValues<ConsentContext>())
            Check.False(Wait(Gate(s, ConsentChoice.Enable, asked: asked, notices: notices).EnsureAsync(ctx)), ctx.ToString());
        Check.Equal(0, asked.Count, "no pop-up");
        Check.False(s.CopiumConsent);
        Check.Equal(Enum.GetValues<ConsentContext>().Length, notices.Count);
        Check.Equal("Cloud sharing is off: Cloud sharing is off — enable it under App → Cloud sharing", notices[0]);
        // even with the user's yes on file
        s.CopiumConsent = true;
        Check.False(Wait(Gate(s, asked: asked).EnsureAsync(ConsentContext.ShareLast)), "the kill switch outranks the consent");
        Check.False(new ConsentGate(s).AutoUploadAllowed);
        s.ScreenshotUpload = true;
        Check.False(new ConsentGate(s).AutoUploadAllowed);
        s.CloudSharingEnabled = true;
        Check.True(new ConsentGate(s).AutoUploadAllowed, "setting + consent + cloud on");
        s.CopiumConsent = false;
        Check.False(new ConsentGate(s).AutoUploadAllowed);
    }

    [UnitTest]
    public static void The_pop_up_is_shown_once_per_attempt_and_a_failing_one_is_a_no()
    {
        var s = Settings.FromJson("{}");
        var pending = new TaskCompletionSource<ConsentAnswer>();
        int shown = 0;
        var gate = new ConsentGate(s) { Prompt = _ => { shown++; return pending.Task; } };
        var a = gate.EnsureAsync(ConsentContext.ClipShare);
        var b = gate.EnsureAsync(ConsentContext.ShareLast);
        Check.False(a.IsCompleted || b.IsCompleted);
        pending.SetResult(new ConsentAnswer(ConsentChoice.Enable));
        Check.True(Wait(a) && Wait(b));
        Check.Equal(1, shown, "the second caller waited for the same answer");

        var broken = Settings.FromJson("{}");
        var bad = new ConsentGate(broken) { Prompt = _ => throw new InvalidOperationException("no window") };
        Check.False(Wait(bad.EnsureAsync(ConsentContext.ClipShare)));
        Check.False(broken.CopiumConsent);
        Check.False(bad.EnsureAsync(ConsentContext.ClipShare).IsFaulted, "and the gate is usable again");
    }

    [UnitTest]
    public static void Enable_without_a_token_opens_the_token_setup_for_screenshots_and_keeps_the_consent()
    {
        foreach (var (ctx, opens) in new[] { (ConsentContext.ScreenshotUploadManual, true), (ConsentContext.Proactive, true), (ConsentContext.ScreenshotUploadSetting, true), (ConsentContext.ClipShare, false), (ConsentContext.ShareLast, false), (ConsentContext.TrimShare, false) })
        {
            var s = Settings.FromJson("{}");
            int setups = 0;
            var gate = Gate(s);
            gate.OpenTokenSetup = () => setups++;
            Check.True(Wait(gate.EnsureAsync(ctx)));
            Check.Equal(opens ? 1 : 0, setups, ctx + " (a clip has its own no-token path: the share window)");
            Check.True(s.CopiumConsent, "kept");
        }
        var withToken = Settings.FromJson("{}");
        withToken.CopiumToken = "eup_x";
        int again = 0;
        var g2 = Gate(withToken);
        g2.OpenTokenSetup = () => again++;
        Check.True(Wait(g2.EnsureAsync(ConsentContext.Proactive)));
        Check.Equal(0, again, "a token is there: nothing to set up");
    }

    [UnitTest]
    public static void The_consent_choices_are_saved()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var s = Settings.FromJson("{}");
        Wait(Gate(s, ConsentChoice.Enable, autoUpload: true).EnsureAsync(ConsentContext.Proactive));
        var back = Settings.Load();
        Check.True(back.CopiumConsent);
        Check.True(back.ScreenshotUpload);
        var other = Settings.FromJson("{}");
        Wait(Gate(other, ConsentChoice.DontAskAgain).EnsureAsync(ConsentContext.Proactive));
        Check.True(Settings.Load().CopiumConsentDontAsk);
    }

    // ------------------------------------------------------------------ own keys are never imported

    [UnitTest]
    public static void The_per_install_consent_keys_are_never_imported()
    {
        string theirs = """
        {
          "CopiumConsent": true, "CopiumConsentDontAsk": true, "CopiumConsentPromptCount": 2, "CopiumConsentPromptedUtc": "2026-10-01T00:00:00Z", "CopiumConsentScreenshots": 2,
          "CopiumNudgeShown": 3, "CopiumNudgeDismissed": true, "CopiumNudgeLastShownUtc": "2026-10-01T00:00:00Z", "CopiumAutoUploadNoticeShown": true,
          "ScreenshotEditorExcludeFromCapture": false, "CloudSharingEnabled": false, "Fps": 120
        }
        """;
        foreach (var key in new[] { "CopiumConsent", "CopiumConsentDontAsk", "CopiumConsentPromptCount", "CopiumConsentPromptedUtc", "CopiumConsentScreenshots", "CopiumNudgeShown", "CopiumNudgeDismissed",
                                    "CopiumNudgeLastShownUtc", "ScreenshotEditorExcludeFromCapture", "CloudSharingEnabled" })
            Check.True(SettingsImport.IsOwnKey(key), key);
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.ClipsEnabled)), "the feature switch is importable");
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.ScreenshotUpload)));
        string kept = SettingsImport.KeepOurOwn(theirs, "{}", false);
        using var doc = System.Text.Json.JsonDocument.Parse(kept);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Check.Equal(new[] { "CloudSharingEnabled", "Fps" }, names.ToArray(), "everything of this installation was dropped from their copy, except an incoming kill switch OFF (it can only tighten)");
        // and ours stays when we have one
        string ours = """{ "CopiumConsent": true, "CloudSharingEnabled": false }""";
        using var doc2 = System.Text.Json.JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, ours, false));
        Check.True(doc2.RootElement.GetProperty("CopiumConsent").GetBoolean());
        Check.False(doc2.RootElement.GetProperty("CloudSharingEnabled").GetBoolean());
        Check.False(doc2.RootElement.TryGetProperty("CopiumConsentDontAsk", out _));
        // a difference in them is no difference to offer
        var a = Settings.FromJson("{}");
        var b = Settings.FromJson("{}");
        b.CopiumConsent = true;
        Check.Equal(0, SettingsImport.Differences(a.ToJson(), b.ToJson()).Count);
        b.ClipsEnabled = false;
        Check.Equal(new[] { "features" }, SettingsImport.Differences(a.ToJson(), b.ToJson()).ToArray());
    }

    [UnitTest]
    public static void An_incoming_cloud_sharing_off_is_kept_but_an_incoming_on_never_is()
    {
        // kept: an admin pre-provisions a work machine with the switch off
        string off = """{ "CloudSharingEnabled": false, "Fps": 120 }""";
        foreach (string? ours in new[] { null, "{}", """{ "CloudSharingEnabled": true }""" })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(SettingsImport.KeepOurOwn(off, ours, false));
            Check.False(doc.RootElement.GetProperty("CloudSharingEnabled").GetBoolean(), "incoming off is kept (ours: " + (ours ?? "none") + ")");
        }
        // never: an incoming on is replaced by ours, or dropped
        string on = """{ "CloudSharingEnabled": true, "Fps": 120 }""";
        using var kept = System.Text.Json.JsonDocument.Parse(SettingsImport.KeepOurOwn(on, """{ "CloudSharingEnabled": false }""", false));
        Check.False(kept.RootElement.GetProperty("CloudSharingEnabled").GetBoolean(), "ours stays off");
        using var dropped = System.Text.Json.JsonDocument.Parse(SettingsImport.KeepOurOwn(on, "{}", false));
        Check.False(dropped.RootElement.TryGetProperty("CloudSharingEnabled", out _), "an incoming on is dropped");
        // the offer: only an incoming off over our on is a difference
        var onSettings = Settings.FromJson("{}");
        var offSettings = Settings.FromJson("{}");
        offSettings.CloudSharingEnabled = false;
        Check.Equal(new[] { "cloud sharing" }, SettingsImport.Differences(onSettings.ToJson(), offSettings.ToJson()).ToArray());
        Check.Equal(0, SettingsImport.Differences(offSettings.ToJson(), onSettings.ToJson()).Count, "their on over our off is nothing to offer");
    }

    [UnitTest]
    public static void The_automatic_upload_never_comes_on_without_the_checkbox_or_the_consent()
    {
        // Enable from a clip share with a stale ScreenshotUpload=true (an S6-era setting): the Enable does not leave it armed
        foreach (var ctx in new[] { ConsentContext.ClipShare, ConsentContext.ShareLast, ConsentContext.TrimShare, ConsentContext.TokenSaved, ConsentContext.ScreenshotUploadSetting })
        {
            var s = Settings.FromJson("{}");
            s.ScreenshotUpload = true;
            Check.True(Wait(Gate(s, ConsentChoice.Enable, autoUpload: true).EnsureAsync(ctx)));
            Check.False(s.ScreenshotUpload, ctx + ": the consent does not arm the automatic upload");
            Check.False(new ConsentGate(s).AutoUploadAllowed);
        }
        var screenshot = Settings.FromJson("{}");
        screenshot.ScreenshotUpload = true;
        Check.True(Wait(Gate(screenshot, ConsentChoice.Enable, autoUpload: false).EnsureAsync(ConsentContext.ScreenshotUploadManual)));
        Check.False(screenshot.ScreenshotUpload, "the box was not ticked");

        // no clip upload and no stale setting through the controller either
        using var dir = new TempDir();
        var settings = Settings.FromJson("{}");
        settings.CopiumToken = "eup_token";
        settings.ScreenshotUpload = true;
        using var app = new AppController(settings, new Updater(settings));
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            app.ConsentPromptOverride = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.Enable));
            Wait(app.ShareAsync(Clip(dir)));
            Check.True(settings.CopiumConsent);
            Check.False(settings.ScreenshotUpload, "Enable from a clip share: no automatic screenshot upload");
        }
        finally { CopiumClient.HttpOverride = null; }

        // Normalize clears it without the consent, and keeps it with it
        var stale = Settings.FromJson("""{ "ScreenshotUpload": true }""");
        Check.False(stale.ScreenshotUpload, "no consent: normalized off");
        var agreed = Settings.FromJson("""{ "ScreenshotUpload": true, "CopiumConsent": true }""");
        Check.True(agreed.ScreenshotUpload);
        stale.ScreenshotUpload = true;
        stale.Normalize();
        Check.False(stale.ScreenshotUpload);
    }

    [UnitTest]
    public static void The_consent_window_defaults_to_Not_now_and_ignores_input_when_it_has_just_opened()
    {
        var window = new CopiumConsentWindow(CopiumConsentWindow.Mode.Consent, offerAutoUpload: false, excludeFromCapture: false);
        Check.False(window.EnableButton.IsDefault, "Enter must never give consent");
        Check.True(window.NotNowButton.IsDefault);
        Check.True(window.NotNowButton.IsCancel, "Esc is Not now");
        Check.False(window.EnableButton.IsCancel);
        Check.False(window.DontAskButton.IsDefault);
        var confirm = new CopiumConsentWindow(CopiumConsentWindow.Mode.ConfirmCloudSharing, offerAutoUpload: false, excludeFromCapture: false);
        Check.False(confirm.EnableButton.IsDefault);
        Check.Equal(ConsentChoice.NotNow, window.Answer.Choice, "closed another way: Not now");
        Check.Equal(400, (int)CopiumConsentWindow.InputDelay.TotalMilliseconds);
        Check.False(CopiumConsentWindow.AcceptsInput(1000, 1000), "the first instant");
        Check.False(CopiumConsentWindow.AcceptsInput(1000, 1399));
        Check.True(CopiumConsentWindow.AcceptsInput(1000, 1400));
        Check.False(CopiumConsentWindow.AcceptsInput(long.MaxValue, 5000), "not loaded yet");
        Check.Contains(window.DontAskButton.ToolTip as string, "with a token saved, still asks each time until you say yes");
    }

    [UnitTest]
    public static void The_gate_raises_ConsentChanged_and_the_screenshot_checks_mirror_the_settings()
    {
        var s = Settings.FromJson("{}");
        int raised = 0;
        var gate = Gate(s, ConsentChoice.Enable, autoUpload: true);
        gate.ConsentChanged += () => raised++;
        Check.False(MainWindow.ScreenshotChecksFor(Settings.FromJson("{}")).Upload, "defaults: upload off");
        Check.True(Wait(gate.EnsureAsync(ConsentContext.ScreenshotUploadManual)));
        Check.Equal(1, raised, "Enable");
        Check.True(s.ScreenshotUpload, "the pop-up turned the automatic upload on");
        var (upload, _) = MainWindow.ScreenshotChecksFor(s);
        Check.True(upload, "the tab's box mirrors the setting after ConsentChanged");

        var other = Settings.FromJson("{}");
        int raised2 = 0;
        var dont = Gate(other, ConsentChoice.DontAskAgain);
        dont.ConsentChanged += () => raised2++;
        Check.False(Wait(dont.EnsureAsync(ConsentContext.Proactive)));
        Check.Equal(1, raised2, "Don't ask again");
        var notNow = Gate(Settings.FromJson("{}"), ConsentChoice.NotNow);
        int raised3 = 0;
        notNow.ConsentChanged += () => raised3++;
        Wait(notNow.EnsureAsync(ConsentContext.ClipShare));
        Check.Equal(0, raised3, "Not now changes nothing");

        // an armed setting without the yes is never shown armed
        var armed = Settings.FromJson("{}");
        armed.ScreenshotUpload = true;
        armed.CopiumConsent = false;
        Check.False(MainWindow.ScreenshotChecksFor(armed).Upload);
    }

    [UnitTest]
    public static void A_declined_share_is_neutral_in_the_clip_list_and_only_a_failure_is_a_problem()
    {
        Check.Equal(("Link copied to clipboard", false), ClipsPane.ShareStatusFor(true, ShareOutcome.Uploaded, null, null, true));
        Check.Equal(((string?)null, false), ClipsPane.ShareStatusFor(false, ShareOutcome.FileShareOpened, null, null, true));
        Check.Equal(("Not shared", false), ClipsPane.ShareStatusFor(false, ShareOutcome.Declined, null, null, true));
        Check.Equal(("Not shared: cloud sharing is off", false), ClipsPane.ShareStatusFor(false, ShareOutcome.Declined, null, AppController.CloudOffText, false));
        Check.Equal(("Upload failed: no route", true), ClipsPane.ShareStatusFor(false, ShareOutcome.Failed, null, "no route", true));
        Check.Equal(("Upload failed", true), ClipsPane.ShareStatusFor(false, ShareOutcome.Failed, null, null, true));
        Check.Equal(("Over limit", true), ClipsPane.ShareStatusFor(false, ShareOutcome.Refused, "Over limit", null, true));
        Check.Equal((AppController.ShareWindowFailedText, true), ClipsPane.ShareStatusFor(false, ShareOutcome.WindowFailed, null, null, true));
    }

    [UnitTest]
    public static void Turning_cloud_sharing_off_cancels_a_running_clip_upload()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        settings.CopiumToken = "eup_token";
        settings.CopiumConsent = true;
        using var app = new AppController(settings, new Updater(settings));
        var started = new TaskCompletionSource();
        CopiumClient.HttpOverride = new HttpClient(new Hanging(started));
        try
        {
            var entry = Clip(dir);
            var upload = app.ShareAsync(entry);
            Pump(() => started.Task.IsCompleted, "the upload to be under way");
            Check.False(upload.IsCompleted);
            Check.False(app.SetCloudSharing(false));
            Pump(() => upload.IsCompleted, "the upload to be cancelled");
            Check.False(upload.Result);
            Check.Equal(ShareOutcome.Declined, app.LastShareOutcome);
            Check.Equal(AppController.CloudOffText, app.ShareError);
            Check.True(string.IsNullOrEmpty(entry.CopiumLink), "nothing was linked");
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    private sealed class Hanging : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started;
        public Hanging(TaskCompletionSource started) { _started = started; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);   // until the kill switch cancels it
            throw new InvalidOperationException("unreachable");
        }
    }

    private static void Pump(Func<bool> done, string what) => ScreenshotUploadTests.Pump(done, what);

    [UnitTest]
    public static void Turning_Clips_off_during_a_manual_recording_stops_it_and_says_so()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        using var app = new AppController(settings, new Updater(settings));
        settings.ToastRecording.Enabled = false;
        settings.ToastShared.Enabled = false;
        app.WantRecordingForTests = true;   // a manual recording was asked for (no engine runs in a unit test)
        try
        {
            Check.True(app.SetClipsEnabled(false));
            Check.False(app.WantRecordingForTests, "the recording wish ended");
            Check.False(app.IsManualRecording);
            Check.Equal("Recording stopped", app.LastNotice);
        }
        finally { Hotkeys.Rebind(Array.Empty<HotkeyAction>()); }
    }

    [UnitTest]
    public static void A_bare_403_is_a_token_rejection()
    {
        Check.True(CopiumClient.IsTokenRejection(403, null));
        Check.True(CopiumClient.IsTokenRejection(403, ""));
        Check.False(CopiumClient.IsTokenRejection(403, "File blocked"));
        Check.Contains(CopiumClient.ErrorMessage(403, ""), "rejected the token");
    }

    // ------------------------------------------------------------------ clip sharing through the controller

    private static ClipEntry Clip(TempDir dir)
    {
        string path = dir.File("clip.mp4");
        File.WriteAllBytes(path, new byte[64]);
        return new ClipEntry { Path = path, Size = 64, Duration = 2, Created = DateTime.Now };
    }

    private sealed class Offline : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("no route");   // never a real network, never the clipboard
        }
    }

    [UnitTest]
    public static void ShareAsync_asks_first_and_uploads_only_after_Enable()
    {
        using var dir = new TempDir();
        var settings = Settings.FromJson("{}");
        settings.CopiumToken = "eup_token";
        using var app = new AppController(settings, new Updater(settings));
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            var asked = new List<ConsentContext>();
            var answer = ConsentChoice.NotNow;
            app.ConsentPromptOverride = ctx => { asked.Add(ctx); return Task.FromResult(new ConsentAnswer(answer)); };
            var entry = Clip(dir);

            Check.False(Wait(app.ShareAsync(entry)));
            Check.Equal(new[] { ConsentContext.ClipShare }, asked.ToArray());
            Check.Equal(ShareOutcome.Declined, app.LastShareOutcome);
            Check.Equal(0, handler.Requests, "Not now: nothing was sent");
            Check.Null(app.ShareError, "and it is no failure");

            Check.False(Wait(app.ShareAsync(entry, context: ConsentContext.TrimShare)));
            Check.Equal(ConsentContext.TrimShare, asked.Last(), "each attempt asks with its own context");
            Check.Equal(2, asked.Count, "asked again, once per attempt");

            answer = ConsentChoice.Enable;
            Check.False(Wait(app.ShareAsync(entry, context: ConsentContext.ShareLast)), "the offline mock fails the upload itself");
            Check.True(settings.CopiumConsent);
            Check.Equal(1, handler.Requests, "after Enable the upload was tried");
            Check.Equal(ShareOutcome.Failed, app.LastShareOutcome);
            Check.Contains(app.ShareError, "reach Copium");

            asked.Clear();
            Wait(app.ShareAsync(entry));
            Check.Equal(0, asked.Count, "agreed already: no pop-up");
            Check.Equal(2, handler.Requests);
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    [UnitTest]
    public static void ShareAsync_without_a_token_opens_the_drag_window_with_no_pop_up_and_never_uploads()
    {
        using var dir = new TempDir();
        var settings = Settings.FromJson("{}");
        using var app = new AppController(settings, new Updater(settings));
        int popUps = 0;
        app.ConsentPromptOverride = _ => { popUps++; return Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow)); };
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        int windows = 0;
        app.ShareWindowOpener = _ => { windows++; return true; };   // (no window in a unit test)
        try
        {
            for (int i = 1; i <= 2; i++)
            {
                Check.False(Wait(app.ShareAsync(Clip(dir))));
                Check.Equal(0, popUps, "no token: nothing to agree to yet, no question (try " + i + ")");
                Check.Equal(ShareOutcome.FileShareOpened, app.LastShareOutcome);
                Check.Equal(i, windows, "the local drag window opens every time");
            }
            settings.CopiumConsentDontAsk = true;   // after Don't ask again too
            Check.False(Wait(app.ShareAsync(Clip(dir), context: ConsentContext.ShareLast)));
            Check.Equal(0, popUps);
            Check.Equal(3, windows);
            Check.Equal(0, handler.Requests, "and nothing is uploaded");
            Check.False(settings.CopiumConsent);
            // cloud sharing off still refuses first
            settings.CloudSharingEnabled = false;
            Check.False(Wait(app.ShareAsync(Clip(dir))));
            Check.Equal(ShareOutcome.Declined, app.LastShareOutcome);
            Check.Equal(3, windows, "no window while cloud sharing is off");
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    [UnitTest]
    public static void Share_last_with_a_token_but_no_consent_over_a_game_does_not_prompt()
    {
        using var dir = new TempDir();
        var settings = Settings.FromJson("{}");
        settings.CopiumToken = "eup_token";
        using var app = new AppController(settings, new Updater(settings));
        int popUps = 0;
        app.ConsentPromptOverride = _ => { popUps++; return Task.FromResult(new ConsentAnswer(ConsentChoice.Enable)); };
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            app.GameOrFullScreenProbe = () => true;
            Check.False(Wait(app.ShareAsync(Clip(dir), announce: true, context: ConsentContext.ShareLast)));
            Check.Equal(0, popUps, "no dialog over the game");
            Check.Equal("Sharing needs your OK once — open Clink and press Share", app.ShareError);
            Check.Equal(app.ShareError, app.LastNotice);
            Check.Equal(ShareOutcome.Failed, app.LastShareOutcome, "Failed: ShareLast raises its notice");
            Check.False(settings.CopiumConsent);
            Check.Equal(0, handler.Requests);
            // the main window's Share (not announce) still asks, game or not; away from the game the hotkey asks too
            Check.False(Wait(app.ShareAsync(Clip(dir))));
            Check.Equal(1, popUps);
            app.GameOrFullScreenProbe = () => false;
            settings.CopiumConsent = false;
            Wait(app.ShareAsync(Clip(dir), announce: true, context: ConsentContext.ShareLast));
            Check.Equal(2, popUps, "no game in front: the pop-up shows");
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    [UnitTest]
    public static void Share_last_with_cloud_off_copies_a_link_the_clip_already_has_and_refuses_otherwise()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        settings.CloudSharingEnabled = false;
        settings.CopiumToken = "eup_token";
        using var app = new AppController(settings, new Updater(settings));
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        var copied = new List<string>();
        app.ClipboardOverride = copied.Add;   // never the real clipboard
        settings.ToastShared.Enabled = false;
        try
        {
            var linked = Newest(app, dir, "linked.mp4");
            linked.CopiumLink = "https://v.copium.pics/abc.mp4";
            app.Say("x");
            app.ShareLast();
            Check.Equal(ShareOutcome.LinkCopied, app.LastShareOutcome, "an existing link is only copied");
            Check.Equal(new[] { "https://v.copium.pics/abc.mp4" }, copied.ToArray());
            Check.Equal(0, handler.Requests);

            Newest(app, dir, "unlinked.mp4");
            app.Say("x");
            app.ShareLast();
            Check.Equal(AppController.CloudOffText, app.LastNotice, "a clip without a link is refused");
            Check.Equal(0, handler.Requests);
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    /// <summary>A clip file that becomes the library's newest entry.</summary>
    private static ClipEntry Newest(AppController app, TempDir dir, string name)
    {
        string path = dir.File(name);
        File.WriteAllBytes(path, new byte[64]);
        return app.Library.Add(path, 2, 64, null, Array.Empty<string>());
    }

    [UnitTest]
    public static void With_cloud_sharing_off_ShareAsync_ShareLast_and_the_pipe_refuse_with_the_notice()
    {
        using var dir = new TempDir();
        var settings = Settings.FromJson("{}");
        settings.CopiumToken = "eup_token";
        settings.CopiumConsent = true;
        settings.CloudSharingEnabled = false;
        using var app = new AppController(settings, new Updater(settings));
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        int popUps = 0;
        app.ConsentPromptOverride = _ => { popUps++; return Task.FromResult(new ConsentAnswer(ConsentChoice.Enable)); };
        try
        {
            var entry = Clip(dir);
            Check.False(Wait(app.ShareAsync(entry)));
            Check.Equal(AppController.CloudOffText, app.ShareError);
            Check.Equal(AppController.CloudOffText, app.LastNotice);
            Check.Equal(ShareOutcome.Declined, app.LastShareOutcome);
            Check.Equal("Cloud sharing is off — enable it under App → Cloud sharing", AppController.CloudOffText);
            // the hotkey, the tray item and the pipe all end in ShareLast
            app.Say("something else");
            app.RunCommand("share");
            Check.Equal(AppController.CloudOffText, app.LastNotice, "the pipe's share says so");
            app.Say("something else");
            app.ShareLast();
            Check.Equal(AppController.CloudOffText, app.LastNotice, "and so does the hotkey / tray");
            Check.Equal(0, handler.Requests);
            Check.Equal(0, popUps, "the pop-up never shows");
            // a clip that has a link keeps it copyable: that is no network (checked with the link-copy branch only up to the outcome)
            Check.Equal(AppController.CommandFor("share-last"), AppController.HotkeyCommand.ShareLast);
            var bound = AppController.BindableHotkeys(new Settings { Hotkeys = { new HotkeyAction { Action = "share-last", Keys = new HotkeyBinding { Vk = 0x75 } } } });
            Check.Equal(1, bound.Count, "the share-last hotkey stays bound with cloud sharing off: its press says why nothing was shared");
            var (_, share, _) = AppController.TrayVisibility(settings);
            Check.False(share, "the tray's Share last clip is hidden");
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    // ------------------------------------------------------------------ the two-step kill switch

    [UnitTest]
    public static void Cloud_sharing_turns_off_at_once_and_on_only_after_the_confirmation()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        using var app = new AppController(settings, new Updater(settings));
        int confirms = 0, changes = 0;
        bool confirm = false;
        app.ConfirmCloudSharingOn = () => { confirms++; return confirm; };
        app.CloudSharingChanged += () => changes++;
        var logged = new List<string>();
        void Collect(string line) => logged.Add(line);
        Log.Line += Collect;
        try
        {
            Check.False(app.SetCloudSharing(false), "off needs no question");
            Check.Equal(0, confirms);
            Check.False(settings.CloudSharingEnabled);
            Check.False(Settings.Load().CloudSharingEnabled, "saved");
            Check.Equal(1, changes);

            Check.False(app.SetCloudSharing(true), "Cancel: it stays off");
            Check.Equal(1, confirms);
            Check.False(settings.CloudSharingEnabled);
            Check.False(Settings.Load().CloudSharingEnabled, "and the file was not written");
            Check.Equal(1, changes);

            confirm = true;
            Check.True(app.SetCloudSharing(true));
            Check.Equal(2, confirms);
            Check.True(settings.CloudSharingEnabled);
            Check.True(Settings.Load().CloudSharingEnabled);
            Check.Equal(2, changes);

            Check.True(app.SetCloudSharing(true), "already on: nothing to ask");
            Check.Equal(2, confirms);
            Check.True(logged.Any(l => l.Contains("cloud sharing: turned off")));
            Check.True(logged.Any(l => l.Contains("cloud sharing: turning it on was cancelled")));
            Check.True(logged.Any(l => l.Contains("cloud sharing: turned on")));
        }
        finally { Log.Line -= Collect; }
    }

    [UnitTest]
    public static void The_pop_up_and_the_confirmation_show_the_same_warning_and_the_right_buttons()
    {
        Check.Equal("Anything you upload is immediately publicly accessible to anyone who has the link. Links are not private and cannot be recalled by Clink.", CopiumConsentWindow.WarningTextValue);
        var screenshot = new CopiumConsentWindow(CopiumConsentWindow.Mode.Consent, offerAutoUpload: true, excludeFromCapture: true);
        Check.Equal(CopiumConsentWindow.WarningTextValue, screenshot.WarningText.Text);
        Check.Equal("Share with Copium?", screenshot.TitleText.Text);
        Check.Equal("Enable Copium sharing", screenshot.EnableButton.Content);
        Check.Equal("Not now", screenshot.NotNowButton.Content);
        Check.Equal("Don't ask again", screenshot.DontAskButton.Content);
        Check.Equal(System.Windows.Visibility.Visible, screenshot.AutoUploadCheck.Visibility);
        Check.Equal("Also upload every screenshot I take automatically", screenshot.AutoUploadCheck.Content);
        Check.False(screenshot.AutoUploadCheck.IsChecked == true, "unchecked by default");
        Check.True(screenshot.Topmost);
        Check.True(CaptureExclusion.IsExcluded(screenshot), "kept out of captures when a screenshot action asks");

        var clip = new CopiumConsentWindow(CopiumConsentWindow.Mode.Consent, offerAutoUpload: false, excludeFromCapture: false);
        Check.Equal(System.Windows.Visibility.Collapsed, clip.AutoUploadCheck.Visibility);
        Check.False(clip.Topmost);
        Check.False(CaptureExclusion.IsExcluded(clip));

        var confirm = new CopiumConsentWindow(CopiumConsentWindow.Mode.ConfirmCloudSharing, offerAutoUpload: true, excludeFromCapture: false);
        Check.Equal(CopiumConsentWindow.WarningTextValue, confirm.WarningText.Text, "the same warning");
        Check.Equal("Yes, allow cloud sharing", confirm.EnableButton.Content);
        Check.Equal("Cancel", confirm.NotNowButton.Content);
        Check.Equal(System.Windows.Visibility.Collapsed, confirm.DontAskButton.Visibility);
        Check.Equal(System.Windows.Visibility.Collapsed, confirm.AutoUploadCheck.Visibility);
    }
    // ------------------------------------------------------------------ features

    [UnitTest]
    public static void The_features_default_on_and_both_off_keeps_screenshots()
    {
        var s = Settings.FromJson("{}");
        Check.True(s.ClipsEnabled);
        Check.True(s.ScreenshotsEnabled);
        var both = Settings.FromJson("""{ "ClipsEnabled": false, "ScreenshotsEnabled": false }""");
        Check.False(both.ClipsEnabled);
        Check.True(both.ScreenshotsEnabled, "the last feature stays on");
        var clipsOnly = Settings.FromJson("""{ "ClipsEnabled": true, "ScreenshotsEnabled": false }""");
        Check.True(clipsOnly.ClipsEnabled);
        Check.False(clipsOnly.ScreenshotsEnabled);
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.ClipsEnabled)));
    }

    private static Settings WithAllRows(Action<Settings>? tweak = null)
    {
        var s = Settings.FromJson("{}");
        s.Hotkeys.Clear();
        int vk = 0x70;
        foreach (var action in new[] { "clip", "pause", "share-last", "record", "screenshot", "screenshot-edit-last" })
            s.Hotkeys.Add(new HotkeyAction { Action = action, Keys = new HotkeyBinding { Vk = vk++ } });
        tweak?.Invoke(s);
        return s;
    }

    [UnitTest]
    public static void Clips_off_unbinds_the_clip_rows_and_hides_the_clip_tray_items()
    {
        var on = AppController.BindableHotkeys(WithAllRows()).Select(h => h.Action).ToArray();
        Check.Equal(new[] { "clip", "pause", "share-last", "record", "screenshot", "screenshot-edit-last" }, on);
        var s = WithAllRows(x => x.ClipsEnabled = false);
        Check.Equal(new[] { "screenshot", "screenshot-edit-last" }, AppController.BindableHotkeys(s).Select(h => h.Action).ToArray(), "only the screenshot rows are bound");
        Check.Equal(6, s.Hotkeys.Count, "the rows stay in the list and in settings.json");
        var shots = WithAllRows(x => x.ScreenshotsEnabled = false);
        Check.Equal(new[] { "clip", "pause", "share-last", "record" }, AppController.BindableHotkeys(shots).Select(h => h.Action).ToArray());

        Check.Equal((true, true, true), AppController.TrayVisibility(WithAllRows()));
        Check.Equal((false, false, true), AppController.TrayVisibility(WithAllRows(x => x.ClipsEnabled = false)));
        Check.Equal((true, true, false), AppController.TrayVisibility(WithAllRows(x => x.ScreenshotsEnabled = false)));
        Check.Equal((true, false, true), AppController.TrayVisibility(WithAllRows(x => x.CloudSharingEnabled = false)), "cloud off hides only Share last clip");
    }

    [UnitTest]
    public static void Clips_off_never_starts_the_engine_and_the_clip_commands_say_so()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        settings.ClipsEnabled = false;
        using var app = new AppController(settings, new Updater(settings));
        int gen = app.EngineStartGeneration;
        app.StartEngine();
        Check.False(app.Engine.IsRunning);
        Check.Equal(gen, app.EngineStartGeneration, "no start was asked for");
        Check.Equal("Clips are off", app.StatusText);

        foreach (var cmd in new[] { "clip", "trim", "share", "record", "pause", "resume" })
        {
            Check.True(AppController.IsClipsCommand(cmd), cmd);
            app.Say("x");
            app.RunCommand(cmd);
            Check.Equal(AppController.ClipsOffText, app.LastNotice, "--" + cmd);
        }
        Check.False(app.IsPaused, "pause did not pause anything");
        Check.False(AppController.IsClipsCommand("screenshot"));
        Check.False(AppController.IsClipsCommand("show"));
        app.Say("x");
        app.ClipNow();
        Check.Equal(AppController.ClipsOffText, app.LastNotice);
        app.Say("x");
        app.ToggleRecording();
        Check.Equal(AppController.ClipsOffText, app.LastNotice);
        app.Say("x");
        app.TogglePause();
        Check.Equal(AppController.ClipsOffText, app.LastNotice);
        // a startup command (--clip when no Clink was running) gets the same answer, later
        Check.Equal("Clips are turned off — Settings → App → Features", AppController.ClipsOffText);
    }

    [UnitTest]
    public static void The_features_switch_live_and_the_last_one_cannot_be_turned_off()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        using var app = new AppController(settings, new Updater(settings));
        int changes = 0;
        app.FeaturesChanged += () => changes++;
        try
        {
            Check.True(app.SetClipsEnabled(false));
            Check.False(settings.ClipsEnabled);
            Check.False(Settings.Load().ClipsEnabled, "saved");
            Check.Equal(1, changes);
            Check.Equal("Clips are off", app.StatusText);
            Check.False(app.SetScreenshotsEnabled(false), "the last feature stays on");
            Check.True(settings.ScreenshotsEnabled);
            Check.Equal(1, changes);
            Check.True(app.SetScreenshotsEnabled(true), "already on");
            // back on: the engine is asked to start, which a unit test must not let happen: a paused (dev) controller does not start it
            AppController.InstanceSuffix = ".dev.test";
            app.ApplyStartPause();
            int gen = app.EngineStartGeneration;
            Check.True(app.SetClipsEnabled(true));
            Check.True(settings.ClipsEnabled);
            Check.Equal(gen, app.EngineStartGeneration, "paused: nothing started");
            Check.Equal(2, changes);
            settings.ScreenshotsEnabled = false;
            Check.False(app.SetClipsEnabled(false), "and now Clips is the last one");
            Check.True(settings.ClipsEnabled);
            Check.Equal(2, changes);
        }
        finally { Hotkeys.Rebind(Array.Empty<HotkeyAction>()); }
    }
}
