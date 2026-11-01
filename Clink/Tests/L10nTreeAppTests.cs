using System.IO;
using System.Text.Json;
using System.Windows;
using Clink.Capture;
using Clink.Engine;
using Clink.Screenshots;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Localization slice S3a (the app controller's notices, toasts, status dot, tray menu and updater texts). Under <c>qps-ploc</c> the built
/// (never shown) toasts and status dot are walked by <see cref="L10nTreeHarness"/>, the tray strip is built and read without being shown,
/// and the texts the code makes (notices, refusals, status, updater messages) must come from the catalog; under <c>en</c> they must be
/// exactly today's English. Log lines of notices stay English whatever the language is.
/// </summary>
internal static class L10nTreeAppTests
{
    // ------------------------------------------------------------------ rig

    private sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Settings Settings;
        public readonly AppController App;
        private readonly string? _oldIndex = ClipLibrary.IndexPathOverride;

        public Env(bool token = true, bool cloud = true)
        {
            if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
            ClipLibrary.IndexPathOverride = Dir.File("clips.json");
            Settings = Settings.FromJson("{}");
            Settings.CloudSharingEnabled = cloud;
            Settings.ClickSound = false;
            if (token) Settings.CopiumToken = "test-token";
            App = new AppController(Settings, new Updater(Settings));
        }

        public void Dispose()
        {
            App.Dispose();
            ClipLibrary.IndexPathOverride = _oldIndex;
            Dir.Dispose();
        }
    }

    /// <summary>Texts that are not translatable on purpose, or that belong to another slice (named below). Each is commented.</summary>
    private static bool Allow(string text)
    {
        string t = text.Trim();
        // the yellow "TEST" tag of a --dev instance (DevInstanceMarker): dev-only, never translated
        if (t == "TEST") return true;
        // user data: fake game and file names, and a Copium link (a URL)
        if (t.StartsWith("Neon Drift", StringComparison.Ordinal) || t.StartsWith("https://", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// What ShareChoices (slice S3b, not this one) puts on the toast's Share button and its arrow: exempt by exact text, and only there
    /// (<see cref="NoneUnlocalized"/> takes those two buttons out of the walk), so "Copy link" elsewhere must still come from the catalog.
    /// </summary>
    private static readonly HashSet<string> ShareChoicesTexts = new(StringComparer.Ordinal)
    {
        "Share", "Copy link", "Share to Copium", "▾", "Share file", "Share options", "More share options",
        "Open the share window: drag the file into Discord, a browser or a folder",
    };

    private static void NoneUnlocalized(DependencyObject root, string what)
    {
        var held = new List<(FrameworkElement Button, object? Content, object? Tip, string? Name)>();
        if (root is ToastWindow toast)
            foreach (var button in new FrameworkElement[] { toast.ShareButton, toast.ShareArrow })
            {
                var content = (button as System.Windows.Controls.ContentControl)?.Content;
                var tip = button.ToolTip;
                var name = System.Windows.Automation.AutomationProperties.GetName(button);
                foreach (var s in new[] { content as string, tip as string, name })
                    Check.True(string.IsNullOrEmpty(s) || PseudoLocale.IsPseudo(s) || ShareChoicesTexts.Contains(s), what + ": the share button has a text of its own: \"" + s + "\"");
                held.Add((button, content, tip, name));
                button.ClearValue(System.Windows.Controls.ContentControl.ContentProperty);
                button.ToolTip = null;
                button.ClearValue(System.Windows.Automation.AutomationProperties.NameProperty);
            }
        try
        {
            var bad = L10nTreeHarness.Unlocalized(root, Allow);
            Check.Equal(Array.Empty<string>(), bad.ToArray(), what + " (layout error: " + L10nTreeHarness.LastLayoutError + ")");
        }
        finally
        {
            foreach (var h in held)
            {
                ((System.Windows.Controls.ContentControl)h.Button).Content = h.Content;
                h.Button.ToolTip = h.Tip;
                if (!string.IsNullOrEmpty(h.Name)) System.Windows.Automation.AutomationProperties.SetName(h.Button, h.Name);
            }
        }
    }

    private static void NoMissing(string what) => Check.Equal(Array.Empty<string>(), L.Missing.OrderBy(k => k, StringComparer.Ordinal).ToArray(), what + ": keys missing from the catalog");

    /// <summary>The text came from the catalog (pseudo-framed) and has nothing hard-coded around it.</summary>
    private static void Marked(string? text, string what)
    {
        Check.True(!string.IsNullOrEmpty(text) && PseudoLocale.IsPseudo(text), what + ": not from the catalog: \"" + text + "\"");
        string rest = L10nTreeHarness.StripFrames(text!);
        Check.True(L10nTreeHarness.IsAllowed(rest) || Allow(rest), what + ": has a hard-coded part: \"" + text + "\"");
    }

    /// <summary>The text made by <paramref name="make"/> is <paramref name="english"/> in English and from the catalog under the pseudo-locale.</summary>
    private static void English_and_pseudo(string english, Func<string?> make, string what)
    {
        using (L.Scope("en")) Check.Equal(english, make(), what + " (en)");
        using (L.Scope("qps-ploc")) Marked(make(), what + " (qps)");
    }

    private static List<string> Logged(Action action)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => lines.Enqueue(line);
        Log.Line += Collect;
        try { action(); }
        finally { Log.Line -= Collect; }
        return lines.ToList();
    }

    private static ToastWindow Toast(string kind, string title, string subtitle, ClipEntry? entry = null, string? link = null, ScreenshotEntry? shot = null, bool canUpload = false) =>
        new(null!, kind, entry, link, title, subtitle, TimeSpan.FromSeconds(7), false, shot, canUpload);

    private static ClipEntry SampleClip(bool shared = false) =>
        new() { Path = @"C:\Clips\Neon Drift 2026-10-20 21-04-11.mp4", Game = "Neon Drift", Duration = 90, Size = 190_000_000, Created = DateTime.Now, CopiumLink = shared ? "https://copium.pics/v/abc" : null };

    private static ScreenshotEntry SampleShot(string? link = null) =>
        new() { Path = @"C:\Shots\Screenshot 2026-10-20 21-04-11.png", Width = 2560, Height = 1440, Size = 1_200_000, Created = DateTime.Now, CopiumLink = link, CopiumPageUrl = link };

    // ------------------------------------------------------------------ toasts

    [UnitTest]
    public static void The_clip_saved_and_shared_toasts_are_localized_for_every_share_button_set()
    {
        foreach (var (token, cloud) in new[] { (false, true), (true, true), (true, false) })
            foreach (bool linked in new[] { false, true })
            {
                using var env = new Env(token, cloud);
                using var scope = L.Scope("qps-ploc");
                L.ClearMissing();
                var clip = SampleClip(linked);
                string title = L.T("Clip saved"), subtitle = Toasts.ClipSubtitle(clip);
                Marked(title, "title");
                Marked(subtitle, "subtitle");
                NoneUnlocalized(Toast("clip", title, subtitle, clip), $"the clip toast (token {token}, cloud {cloud}, linked {linked})");
                NoMissing("clip toast");
            }
        using (var env = new Env())
        using (var scope = L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            var shared = SampleClip(true);
            NoneUnlocalized(Toast("shared", L.T("Shared · link copied"), shared.CopiumLink!, shared, shared.CopiumLink), "the shared toast");
            // a clip without a game name has the shorter subtitle
            Marked(Toasts.ClipSubtitle(new ClipEntry { Path = @"C:\Clips\x.mp4", Duration = 65, Size = 5_000_000 }), "subtitle without a game");
            NoMissing("shared toast");
        }
    }

    [UnitTest]
    public static void The_recording_notice_problem_and_border_toasts_are_localized()
    {
        using var env = new Env();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        NoneUnlocalized(Toast("recording", L.T("Recording started"), "Neon Drift"), "recording started");
        NoneUnlocalized(Toast("record", L.T("Recording started"), L.T("Press {keys} to stop", ("keys", L.Ltr("Ctrl+F9")))), "manual recording started (hotkey)");
        NoneUnlocalized(Toast("record", L.T("Recording started"), L.T("{game} · {hint}", ("game", "Neon Drift"), ("hint", L.T("Stop it from the tray menu")))), "manual recording started (tray)");
        NoneUnlocalized(Toast("recorded", L.T("Recording saved"), L.T("{game} · {summary}", ("game", "Neon Drift"), ("summary", RecordingPolicy.SavedSummary(95, 2_500_000)))), "recording saved");
        NoneUnlocalized(Toast("paused", L.T("Recording paused"), L.T("Press {keys} to resume", ("keys", L.Ltr("Ctrl+F9")))), "paused");
        NoneUnlocalized(Toast("info", L.T("Recording resumed"), L.T("Replay buffer restarted")), "resumed");
        NoneUnlocalized(Toast("info", L.T("Uploading to Copium…"), Toasts.ClipSubtitle(SampleClip()), SampleClip()), "uploading");
        NoneUnlocalized(Toast("info", L.T("Signed in to Copium as {name}", ("name", "Jane")), L.T("Your Copium account is connected")), "signed in");
        NoneUnlocalized(Toast("problem", L.T("Clip not saved"), L.T("Not recording: waiting for a game. If your game is not detected, add it under Capture → Recording → Custom apps.")), "problem");
        foreach (var state in new[] { CaptureBorderState.UnsupportedOs, CaptureBorderState.Denied, CaptureBorderState.Unconfirmed })
        {
            var (t, x) = AppController.BorderNoticeTexts(state);
            Marked(t, "border title " + state);
            Marked(x, "border text " + state);
            NoneUnlocalized(Toast("border", t, x), "border notice " + state);
        }
        // the update notices the updater raises (title and text), as the tray balloon and the problem toast show them
        var cap = new UpdateNotice("download:1.2.3", Note.Of("Clink update not downloaded"),
            Note.PluralOf("Downloading v{version} failed {count} times ({reason}). To retry, click Check now under Alerts → Updates.", 3, ("version", "1.2.3"), ("reason", "HTTP 503")));
        Marked(cap.Title, "update title");
        Marked(cap.Text, "update text");
        NoneUnlocalized(Toast("problem", cap.Title, cap.Text), "update notice toast");
        NoMissing("recording / problem / border toasts");
    }

    [UnitTest]
    public static void The_screenshot_toast_is_localized_in_every_state_of_an_upload()
    {
        using var env = new Env();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var shot = SampleShot();
        string title = L.T("Screenshot saved"), sub = ScreenshotService.Subtitle(shot, new ScreenshotToastState(true, false));
        var w = Toast("screenshot", title, sub, shot: shot, canUpload: true);
        NoneUnlocalized(w, "saved, with the Upload button");
        Marked((string)w.ShotUploadButton.Content, "Upload button");

        w.CancelHandler = () => { };
        w.BeginUpload();
        NoneUnlocalized(w, "uploading (Cancel)");
        Marked((string)w.ShotUploadButton.Content, "Cancel button");
        Marked(w.TitleText.Text, "uploading title");
        w.SetUploadProgress(0.4);
        Marked(w.Subtitle.Text, "progress line");
        NoneUnlocalized(w, "uploading 40%");

        w.UploadFailed();
        NoneUnlocalized(w, "failed (Retry)");
        Marked((string)w.ShotUploadButton.Content, "Retry button");

        var linked = Toast("screenshot", title, sub, shot: SampleShot("https://copium.pics/s/abc"), canUpload: true);
        NoneUnlocalized(linked, "saved and already on Copium (Copy link)");

        var ok = Toast("screenshot", title, sub, shot: shot, canUpload: true);
        ok.BeginUpload();
        ok.UploadSucceeded("https://copium.pics/s/abc", linkCopied: true);
        Marked(ok.TitleText.Text, "uploaded title");
        NoneUnlocalized(ok, "uploaded, link copied");

        var declined = Toast("screenshot", title, sub, shot: shot, canUpload: true);
        declined.BeginUpload();
        declined.UploadDeclined();
        NoneUnlocalized(declined, "upload stopped (cloud sharing off)");

        // the widest status keeps the button as wide as a translated "Uploading… 100%" needs
        Marked(ToastWindow.WidestShareStatus, "widest share status");
        NoMissing("screenshot toast");
    }

    [UnitTest]
    public static void The_uploading_status_is_the_English_it_was_and_keeps_the_regional_percent_format()
    {
        using var scope = L.Scope("en");
        Check.Equal("Uploading… " + 1.0.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), ToastWindow.WidestShareStatus);
        Check.Equal("Clip saved", L.T("Clip saved"));
        Check.Equal("Shared · link copied", L.T("Shared · link copied"));
        Check.Equal("Neon Drift · 1:30 · 190 MB", Toasts.ClipSubtitle(SampleClip()));
        Check.Equal("1:05 · 5 MB", Toasts.ClipSubtitle(new ClipEntry { Path = @"C:\Clips\x.mp4", Duration = 65, Size = 5_000_000 }));
    }

    // ------------------------------------------------------------------ status dot, tray

    [UnitTest]
    public static void The_status_dot_window_is_localized_and_stays_offscreen()
    {
        using var env = new Env();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var dot = new StatusDotWindow();
        Marked(dot.Title, "window title");
        NoneUnlocalized(dot, "the status dot");
        Check.False(dot.IsVisible, "never shown");
        NoMissing("status dot");
        using (L.Scope("en")) Check.Equal("Clink status", new StatusDotWindow().Title);
    }

    [UnitTest]
    public static void The_tray_menu_is_localized_and_the_tooltip_is_cut_after_translating()
    {
        using var env = new Env();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        using var menu = env.App.BuildTrayMenu();
        var items = menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToList();
        Check.True(items.Count >= 10, "the menu has its items: " + items.Count);
        foreach (var item in items) Marked(item.Text, "tray item");
        // with hotkeys in the labels
        env.App.UpdateTrayHotkey();
        foreach (var item in items) Marked(item.Text, "tray item after the hotkeys were read");
        // the status of the tray: every state it can be in
        Marked(env.App.StatusText, "status (starting)");
        // the tooltip is cut at 120 characters whatever the language and the length of the translated status
        string longStatus = L.T("Waiting for a game…") + L.T("Recording (resuming…)") + L.T("Recording (resuming…)") + L.T("Recording (resuming…)");
        string tip = AppController.TrayText("Clink", longStatus);
        Check.Equal(120, tip.Length, "cut to 120");
        Check.True(tip.EndsWith("…", StringComparison.Ordinal));
        Check.Equal("Clink · Paused", AppController.TrayText("Clink", "Paused"));
        NoMissing("tray");
    }

    // ------------------------------------------------------------------ the texts the code makes

    [UnitTest]
    public static void The_status_line_is_localized_for_each_state_the_controller_knows()
    {
        using var env = new Env();
        env.Settings.RecordingMode = "always";   // (the default waits for a game)
        English_and_pseudo("Starting…", () => env.App.StatusText, "starting");
        env.Settings.ClipsEnabled = false;
        English_and_pseudo("Clips are off", () => env.App.StatusText, "clips off");
        env.Settings.ClipsEnabled = true;
        env.Settings.RecordingMode = "game";
        English_and_pseudo("Waiting for a game…", () => env.App.StatusText, "waiting for a game");
        env.Settings.RecordingMode = "always";
        AppController.InstanceSuffix = ".dev.test";
        env.App.ApplyStartPause();
        English_and_pseudo("Paused", () => env.App.StatusText, "paused");
    }

    [UnitTest]
    public static void The_copium_limit_refusals_are_whole_sentences()
    {
        const long MB = 1_000_000;
        English_and_pseudo("Over Copium's free limit (200 MB / 10 min)", () => AppController.ShareRefusalFor(201 * MB, 30, plus: false), "free limit");
        English_and_pseudo("Over Copium Plus's limit (2 GB / 60 min)", () => AppController.ShareRefusalFor(2_001 * MB, 600, plus: true), "plus limit");
        English_and_pseudo("Copium free accepts up to 200 MB / 10 min; this clip is 211 MB / 10:03 - trim it, or tick 'I have Copium+' if you do", () => AppController.ShareRefusalNotice(211 * MB, 602.4, plus: false), "free notice");
        English_and_pseudo("Copium free accepts up to 200 MB / 10 min; this clip is 250 MB - trim it, or tick 'I have Copium+' if you do", () => AppController.ShareRefusalNotice(250 * MB, 0, plus: false), "free notice without a length");
        English_and_pseudo("Copium Plus accepts up to 2 GB / 60 min; this clip is 3 GB / 61:40 - trim it first", () => AppController.ShareRefusalNotice(3_000 * MB, 3700, plus: true), "plus notice");
        English_and_pseudo("Copium Plus accepts up to 2 GB / 60 min; this clip is 3 GB - trim it first", () => AppController.ShareRefusalNotice(3_000 * MB, 0, plus: true), "plus notice without a length");
        English_and_pseudo("Sign in with Copium under App → Copium to upload", () => AppController.BulkNoTokenText, "bulk without a token");
        English_and_pseudo("The folder is no longer there", () => AppController.FolderGoneText, "folder gone");
        English_and_pseudo("Clips are turned off — Settings → App → Features", () => AppController.ClipsOffText, "clips off");
    }

    [UnitTest]
    public static void The_import_question_does_not_name_the_message_box_buttons_and_lists_the_changes_in_the_language()
    {
        var some = new ForeignSettings("Pkg_x", @"C:\x", new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc), new[] { "video quality", "hotkeys" });
        var none = new ForeignSettings("Pkg_x", @"C:\x", new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc), Array.Empty<string>());
        string saved = some.SavedLocalText, app = some.FriendlyName;
        string rest = "Import them and restart Clink? Your current settings are kept as settings.pre-import.json (or the next free settings.pre-import.N.json).\n\n"
            + "If you say no, Clink keeps your settings and stops asking about this copy. To decide later, cancel or close this message.";
        string head = $"Clink also has settings saved {saved} by a copy of Clink that was started from inside {app}.\n\n";
        English_and_pseudo(head + "They differ from yours in: video quality, hotkeys.\n\n" + rest, () => AppController.ImportQuestion(some), "with differences");
        English_and_pseudo(head + "They differ from yours in: a few settings.\n\n" + rest, () => AppController.ImportQuestion(none), "without differences");
        using (L.Scope("en"))
        {
            string q = AppController.ImportQuestion(some);
            Check.False(q.Contains("Yes:") || q.Contains("No:") || q.Contains("Cancel:"), "the Win32 buttons follow the Windows language: not named");
        }
        // each label is translated on its own and joined with the language's separator
        using (L.Scope("qps-ploc"))
        {
            string q = AppController.ImportQuestion(some);
            Check.Contains(q, SettingsImport.LabelList(some.Differences));
        }
    }

    [UnitTest]
    public static void The_older_copy_message_names_the_version_and_the_path()
    {
        var running = new[] { new AppController.RunningClink(111, 1, @"C:\Old\Clink.exe", "0.4.0") };
        var own = ReleaseVersion.Parse("0.4.5");
        English_and_pseudo(@"An older Clink (v0.4.0) is still running from C:\Old\Clink.exe. Right-click its tray icon, choose Quit, then start Clink again.",
            () => AppController.OlderRunningCopy(running, @"C:\New\Clink.exe", own, 222, 1, null, dev: false), "older copy");
    }

    [UnitTest]
    public static void A_notice_is_shown_in_the_language_and_logged_in_English()
    {
        using var env = new Env();
        var stubbed = Toasts.ShowOverride;
        Toasts.ShowOverride = _ => { };   // a problem notice may be a toast over a game: none is shown in a test
        try { Notice_test(env); }
        finally { Toasts.ShowOverride = stubbed; }
    }

    private static void Notice_test(Env env)
    {
        var shown = new List<string>();
        env.App.Notice += shown.Add;
        env.Settings.ClipsEnabled = false;
        List<string> log;
        using (L.Scope("qps-ploc"))
        {
            log = Logged(() => env.App.RunCommand("clip"));
            Marked(env.App.LastNotice, "LastNotice");
            Check.Equal(env.App.LastNotice, shown.Last(), "the Notice event carries the shown text");
        }
        Check.True(log.Any(l => l.Contains("notice: Clips are turned off — Settings → App → Features")), "the log line is English: " + string.Join(" | ", log));
        Check.False(log.Any(l => PseudoLocale.IsPseudo(l)), "and nothing of the pseudo-locale reached the log");

        // arguments are filled in the language and in English alike; a note inside a note keeps each side
        var note = Note.Of("Update failed to start: {error}", ("error", Note.Of("could not start the updater process")));
        using (L.Scope("qps-ploc"))
        {
            Marked(note.Shown, "nested note");
            Check.Equal("Update failed to start: could not start the updater process", note.English);
        }
        log = Logged(() => { using (L.Scope("qps-ploc")) env.App.SayT("Update failed: {error}", ("error", "boom")); });
        Check.True(log.Any(l => l.EndsWith("notice: Update failed: boom", StringComparison.Ordinal)), "SayT logs English: " + string.Join(" | ", log));
    }

    [UnitTest]
    public static void A_clip_that_cannot_be_saved_says_why_in_the_language_and_the_share_window_failure_is_whole()
    {
        using var env = new Env();
        var saved = Toasts.ShowOverride;
        Toasts.ShowOverride = _ => { };
        env.Settings.RecordingMode = "always";   // (the default waits for a game)
        try
        {
            env.App.RunCommand("clip");   // the recorder is not up
            Check.Equal("Not recording yet", env.App.LastNotice);
            using (L.Scope("qps-ploc")) { env.App.RunCommand("clip"); Marked(env.App.LastNotice, "not recording yet"); }
            env.Settings.RecordingMode = "game";
            env.App.RunCommand("clip");
            Check.Equal("Not recording: waiting for a game", env.App.LastNotice);
            using (L.Scope("qps-ploc")) { env.App.RunCommand("clip"); Marked(env.App.LastNotice, "waiting for a game"); }
            env.Settings.RecordingMode = "always";
            env.App.ShareWindowOpener = _ => false;
            using (L.Scope("qps-ploc")) { env.App.ShareFile(SampleClip()); Marked(env.App.ShareError, "share window failure"); }
            env.App.ShareFile(SampleClip());
            Check.Equal(AppController.ShareWindowFailedText, env.App.ShareError, "in English the share error is the same text as AppController.ShareWindowFailedText (the clip list's message for this failure)");
        }
        finally { Toasts.ShowOverride = saved; }
    }

    [UnitTest]
    public static void A_plural_notice_picks_the_form_of_the_language_and_logs_the_English_other_form()
    {
        var note = Note.PluralOf("Saving {count} copies…", 3);
        using (L.Scope("en")) { Check.Equal("Saving 3 copies…", note.Shown); Check.Equal("Saving 3 copies…", note.English); }
        using (L.Scope("qps-ploc")) { Marked(note.Shown, "plural"); Check.Equal("Saving 3 copies…", note.English); }
        // a language with more forms: 21 is "one" in Russian
        var en = L.Embedded.English.Entries.ToDictionary(kv => kv.Key, kv => kv.Value.Text != null ? (object)kv.Value.Text : kv.Value.Forms!);
        var ru = new Dictionary<string, object> { ["Saving {count} copies…"] = new Dictionary<string, string> { ["one"] = "o {count}", ["few"] = "f {count}", ["many"] = "m {count}", ["other"] = "x {count}" } };
        using var catalog = L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "all", JsonSerializer.Serialize(en)),
            new CatalogSource("ru", "t", JsonSerializer.Serialize(ru)),
        }));
        using var scope = L.Scope("ru");
        Check.Equal("o 21", Note.PluralOf("Saving {count} copies…", 21).Shown);
        Check.Equal("f 3", Note.PluralOf("Saving {count} copies…", 3).Shown);
        Check.Equal("m 11", Note.PluralOf("Saving {count} copies…", 11).Shown);
        Check.Equal("Saving 21 copies…", Note.PluralOf("Saving {count} copies…", 21).English);
    }

    [UnitTest]
    public static void A_note_is_English_for_the_log_even_in_a_right_to_left_language_and_shows_the_marks_only_to_the_user()
    {
        const string marks = "\u202A\u202B\u202C\u200E\u200F\u2066\u2067\u2068\u2069";
        var note = Note.Of("Saved a copy to {path}", ("path", Note.Ltr(@"C:\Clips\x.mp4")));
        var nested = Note.Of("Update failed: {error}", ("error", Note.Of("Clink.exe's folder ({folder}) is not writable", ("folder", Note.Ltr(@"C:\Apps")))));
        using (L.Scope("ar"))
        {
            Check.True(L.IsRtl, "the synthetic scope is right to left");
            foreach (var n in new[] { note, nested })
            {
                Check.False(n.English.Any(marks.Contains), "English has no direction mark: " + n.English);
                Check.False(n.ToString().Any(marks.Contains), "ToString is English: " + n);
                Check.Equal(n.English, n.ToString());
            }
            Check.True(note.Shown.Contains("\u202A" + @"C:\Clips\x.mp4" + "\u202C"), "Shown has the marks");
            Check.True(nested.Shown.Contains("\u202A" + @"C:\Apps" + "\u202C"), "a nested note's Shown has them too");
            // even a mark that got into a plain string argument does not reach the English
            Check.False(Note.Of("Cannot start: {error}", ("error", L.Ltr("boom"))).English.Any(marks.Contains), "the safety net strips stray marks");
        }
        Check.Equal(@"Saved a copy to C:\Clips\x.mp4", note.English);
        using (L.Scope("en")) Check.Equal(@"Saved a copy to C:\Clips\x.mp4", note.Shown);
        // the arguments are copied when the note is made
        var args = new (string, object?)[] { ("error", "first") };
        var copy = Note.Of("Cannot start: {error}", args);
        args[0] = ("error", "second");
        Check.Equal("Cannot start: first", copy.English);
        Check.Equal("boom\u202A", Note.Verbatim("boom\u202A").English);   // verbatim is exactly what it was given
        using (L.Scope("qps-ploc")) { Check.Equal("a {b}", Note.Verbatim("a {b}").Shown); Check.Equal("a {b}", Note.Verbatim("a {b}").English); }
    }

    [UnitTest]
    public static void The_status_line_covers_recording_the_game_resuming_and_an_error_and_the_tray_shows_the_error()
    {
        using var env = new Env();
        // the states are internal to the controller: its status text for a given state, with and without a game
        English_and_pseudo("Recording", () => AppController.StatusTextFor(true, RecordingState.Recording, false, null, null), "recording");
        English_and_pseudo("Recording · Neon Drift", () => AppController.StatusTextFor(true, RecordingState.Recording, false, null, "Neon Drift"), "recording a game");
        English_and_pseudo("Recording (resuming…)", () => AppController.StatusTextFor(true, RecordingState.Recording, true, null, null), "resuming");
        English_and_pseudo("Error", () => AppController.StatusTextFor(true, RecordingState.Error, false, null, null), "error");
        English_and_pseudo("Error: disk full", () => AppController.TrayStatusFor("Error", RecordingState.Error, new EngineError(EngineErrorKind.Other, "disk full")), "tray error with its text");
    }

    [UnitTest]
    public static void Bulk_paths_log_English_and_show_the_language()
    {
        using var env = new Env(token: false);
        var saved = Toasts.ShowOverride;
        Toasts.ShowOverride = _ => { };
        try
        {
            var clips = new List<ClipEntry> { SampleClip() };
            BulkShareResult? result = null;
            List<string> log;
            using (L.Scope("qps-ploc"))
            {
                log = Logged(() => result = env.App.ShareManyAsync(clips, null, _ => null, CancellationToken.None).GetAwaiter().GetResult());
                Marked(result!.FirstError, "no token: the result error is in the language");
            }
            Check.True(log.Any(l => l.Contains("notice: Sign in with Copium under App → Copium to upload")), "English in the log: " + string.Join(" | ", log));
            Check.False(log.Any(PseudoLocale.IsPseudo), "no pseudo text in the log: " + string.Join(" | ", log));
            env.Settings.CloudSharingEnabled = false;
            using (L.Scope("qps-ploc"))
                log = Logged(() => env.App.ShareManyAsync(clips, null, _ => null, CancellationToken.None).GetAwaiter().GetResult());
            Check.False(log.Any(PseudoLocale.IsPseudo), "cloud off: no pseudo text in the log: " + string.Join(" | ", log));
            // a folder that is not there: the reason is shown in the language and logged in English
            env.Settings.CloudSharingEnabled = true;
            string gone = Path.Combine(env.Dir.Path, "nothing here");
            env.App.SaveFolderPicker = () => gone;
            BulkSaveResult? save = null;
            using (L.Scope("qps-ploc"))
            {
                log = Logged(() => save = env.App.SaveCopiesAsync(clips, null, null, CancellationToken.None).GetAwaiter().GetResult());
                Marked(save!.FirstError, "folder problem in the result");
            }
            Check.True(log.Any(l => l.Contains("notice: Not saved: That folder is not there")), "English in the log: " + string.Join(" | ", log));
            Check.False(log.Any(PseudoLocale.IsPseudo), "no pseudo text in the log: " + string.Join(" | ", log));
        }
        finally { Toasts.ShowOverride = saved; env.App.SaveFolderPicker = null; }
    }

    [UnitTest]
    public static void No_string_literal_goes_straight_into_Say_in_the_S3a_files()
    {
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        var files = new[] { "App.xaml.cs", "Services/AppController.cs", "Services/AppController.Connect.cs", "Services/AppController.Consent.cs", "Services/AppController.Recording.cs",
            "Services/AppController.SaveCopy.cs", "Services/AppController.Screenshots.cs", "Services/AppController.ShareMany.cs", "Services/Updater.cs", "UI/ToastWindow.xaml.cs" };
        var bad = new List<string>();
        foreach (var f in files)
        {
            string path = Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new TestFailure("source not found: " + path);
            int n = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                n++;
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\bSay\(\s*\$?@?""")) bad.Add(f + ":" + n + ": " + line.Trim());
            }
        }
        Check.Equal(Array.Empty<string>(), bad.ToArray(), "Say(\"…\") logs what it is given: use SayT / Say(Note)");
    }

    // ------------------------------------------------------------------ the updater

    [UnitTest]
    public static void The_updater_keeps_LastError_in_English_and_has_a_display_text_and_a_dev_mode_flag()
    {
        var settings = Settings.FromJson("{}");
        AppController.InstanceSuffix = ".dev.test";
        var dev = new Updater(settings);
        Check.Equal("updates are off in dev mode", dev.LastError, "English, for the log");
        Check.True(dev.LastErrorIsDevMode, "the flag, not the text, says dev mode");
        Check.True(dev.CheckFailed);
        using (L.Scope("en")) Check.Equal("updates are off in dev mode", dev.LastErrorText);
        using (L.Scope("qps-ploc"))
        {
            Marked(dev.LastErrorText, "LastErrorText");
            Check.Equal("updates are off in dev mode", dev.LastError, "LastError does not follow the language");
            // MainWindow does not show dev mode as a problem: decided by the flag, so it holds in every language
            Check.Null(MainWindow.UpdateProblemLine(null, dev.LastErrorText, true, true, dev.LastErrorIsDevMode));
            Check.NotNull(MainWindow.UpdateProblemLine(null, "boom", true, true, lastErrorIsDevMode: false));
            Marked(MainWindow.UpdateProblemLine(null, dev.LastErrorText, true, false, dev.LastErrorIsDevMode), "a non-dev problem line");
        }
        English_and_pseudo("Updates are off in dev mode", () => Updater.DevModeDisplay, "DevModeDisplay");
        English_and_pseudo("Updates aren't available in this build", () => Updater.NoUpdatesDisplay, "NoUpdatesDisplay");
        Check.Equal("Updates are off in dev mode", Updater.DevModeNote, "the constant is the English");
        Check.Equal(Updater.MisconfiguredNote, Note.Of("update configuration invalid: this build's signed update settings (feed.json) are broken, so it does not check for or install updates (details in the log)").English);
    }

    [UnitTest]
    public static void The_updater_install_problems_and_refusals_are_whole_sentences()
    {
        var settings = Settings.FromJson("{}");
        var up = new Updater(settings);
        var version = ReleaseVersion.Parse("1.2.3")!.Value;
        Updater.IsElevatedOverride = true;
        try
        {
            English_and_pseudo("Clink is running as administrator and does not install updates from an elevated process", () => Updater.InstallProblemFor(@"C:\x\Clink.exe"), "elevated reason");
            var refused = new Updater.ApplyRefusedException("m", "its start-up check failed (exit code 5)");
            English_and_pseudo("Update to v1.2.3 not installed: its start-up check failed (exit code 5). To retry, click Check now under Alerts → Updates.", () => Updater.RefusalText(refused, version), "refusal");
            var deferred = new Updater.ApplyRefusedException("m", "a check or download is running; try again in a moment", deferred: true);
            English_and_pseudo("Update to v1.2.3 was not installed this time: a check or download is running; try again in a moment. It will be tried again.", () => Updater.RefusalText(deferred, version), "deferred refusal");
            // the elevated refusal carries its reason as a note: the user reads it in their language, the exception stays English
            var elevated = new Updater.ApplyRefusedException("Clink is running as administrator and does not install updates from an elevated process", "x") { ReasonNote = Note.Of("Clink is running as administrator and does not install updates from an elevated process") };
            using (L.Scope("qps-ploc")) Marked(Updater.RefusalText(elevated, version), "elevated refusal");
        }
        finally { Updater.IsElevatedOverride = null; }
        var launch = new Updater.UpdateStartException("the update process exited right away (exit code 7)", Note.Of("the update process exited right away (exit code {code})", ("code", 7)));
        Check.Equal("the update process exited right away (exit code 7)", launch.Message, "the exception stays English");
        using (L.Scope("qps-ploc")) Marked(((Note)Updater.UserErrorOf(launch)).Shown, "launch error");
        Check.Equal("plain", (string)Updater.UserErrorOf(new IOException("plain")));
        GC.KeepAlive(up);
    }

    // ------------------------------------------------------------------ the catalog and the sources agree

    [UnitTest]
    public static void Every_key_in_the_slice_sources_is_in_app_or_core_json_and_every_app_key_is_used()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // not run from a checkout: nothing to read
        var app = L10nSourceScan.AreaKeys(root, "app");
        // words the slice shares with another area (Paused, Trim, Upload failed: {error} ...) are used from there, not repeated in app.json
        var core = new[] { "core", "main", "clips", "editor" }.SelectMany(area => L10nSourceScan.AreaKeys(root, area)).ToHashSet(StringComparer.Ordinal);
        var hits = L10nSourceScan.CSharp(root, new[]
        {
            @"Services\AppController.cs", @"Services\AppController.Connect.cs", @"Services\AppController.Consent.cs", @"Services\AppController.Recording.cs",
            @"Services\AppController.SaveCopy.cs", @"Services\AppController.Screenshots.cs", @"Services\AppController.ShareMany.cs",
            @"UI\ToastWindow.xaml.cs", @"UI\StatusDotWindow.xaml.cs", @"Services\Updater.cs", "App.xaml.cs",
        }, new[] { L10nSourceScan.T, L10nSourceScan.Plural, "SayT(", "Note.Of(", "Note.PluralOf(" });
        hits.AddRange(L10nSourceScan.Xaml(root, new[] { @"UI\ToastWindow.xaml", @"UI\StatusDotWindow.xaml" }));
        Check.True(hits.Count > 200, "found the literals of the slice's sources: " + hits.Count);
        var problems = hits.Where(h => !app.Contains(h.Key) && !core.Contains(h.Key)).Select(h => h.File + ": " + h.Key).Distinct().ToArray();
        Check.Equal(Array.Empty<string>(), problems, "keys the sources ask for that no catalog file has");
        Check.Equal(Array.Empty<string>(), app.Where(core.Contains).OrderBy(k => k, StringComparer.Ordinal).ToArray(), "app.json keys another area has already");
        var used = hits.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var unused = app.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Check.Equal(Array.Empty<string>(), unused, "app.json keys no source of the slice uses");
    }

    [UnitTest]
    public static void The_app_catalog_is_well_formed_and_agrees_with_the_other_areas()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Locales", "en", "app.json")));
        var problems = new List<string>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                if (p.Value.GetString() != L.EnglishOf(p.Name)) problems.Add("en value differs from the key: " + p.Name);
            }
            else
            {
                var forms = p.Value.EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetString() ?? "");
                if (!forms.ContainsKey("one") || !forms.ContainsKey("other")) problems.Add("plural without one+other: " + p.Name);
                foreach (var f in forms) if (!f.Value.Contains("{count}")) problems.Add("plural form without {count}: " + p.Name + " / " + f.Key);
            }
        }
        Check.Equal(Array.Empty<string>(), problems.ToArray());
    }
}
