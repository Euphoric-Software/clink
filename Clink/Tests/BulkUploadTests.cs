using System.IO;
using System.Net;
using System.Net.Http;
using Clink.Screenshots;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>"Upload and copy links" (AppController.ShareManyAsync / UploadShotsAsync, the quiet single share, the screenshot batch mode) and its pure texts. Offline: a mock HTTP handler, a made-up clipboard and notice.</summary>
internal static class BulkUploadTests
{
    private static T Wait<T>(Task<T> task)
    {
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the task");   // the test thread has a dispatcher context: a blocking wait would deadlock on it
        return task.Result;
    }

    /// <summary>Answers every upload with "https://v.copium.pics/up{n}.mp4" (n = the request's number); counts them; can hold the first and call back.</summary>
    private sealed class Mock : HttpMessageHandler
    {
        public int Requests;
        public Action<int>? OnRequest;
        public Task? HoldFirst;
        public bool FailFirst;
        public HttpStatusCode? FirstStatus;   // answer the first request with this error status
        public readonly HashSet<int> FailAt = new();   // requests (1-based) that die with a network error
        public readonly Dictionary<int, HttpStatusCode> StatusAt = new();   // requests answered with this error status
        public readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Requests);
            if (n == 1) FirstStarted.TrySetResult();
            OnRequest?.Invoke(n);
            if (FailAt.Contains(n)) throw new HttpRequestException("no route");
            if (StatusAt.TryGetValue(n, out var at)) return new HttpResponseMessage(at) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            if (n == 1 && FailFirst) throw new HttpRequestException("no route");
            if (n == 1 && FirstStatus is { } status) return new HttpResponseMessage(status) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            if (n == 1 && HoldFirst != null) await HoldFirst;
            string link = $"https://v.copium.pics/up{n}.mp4";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"id{{n}}","link":"{{link}}","url":"{{link}}","size":64}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Settings Settings;
        public readonly AppController App;
        public readonly Mock Http = new();
        public readonly List<string> Clipboard = new();
        public readonly List<(string Title, string Text)> Notices = new();
        public readonly List<ConsentContext> Asked = new();
        public ConsentChoice Answer = ConsentChoice.Enable;
        private int _n;

        public Rig(bool token = true, bool consent = false, bool cloud = true)
        {
            Settings.DirOverride = Dir.Path;
            Settings = Settings.FromJson("{}");
            if (token) Settings.CopiumToken = "eup_token";
            Settings.CopiumConsent = consent;
            Settings.CloudSharingEnabled = cloud;
            Settings.ToastShared.Enabled = false;
            App = new AppController(Settings, new Updater(Settings));
            App.ClipboardOverride = Clipboard.Add;
            App.BulkNoticeSink = (t, x) => Notices.Add((t, x));
            App.ConsentPromptOverride = ctx => { Asked.Add(ctx); return Task.FromResult(new ConsentAnswer(Answer)); };
            CopiumClient.HttpOverride = new HttpClient(Http);
        }

        public ClipEntry Clip(string? link = null, long size = 64)
        {
            string path = Dir.File($"clip{++_n}.mp4");
            File.WriteAllBytes(path, new byte[64]);
            return new ClipEntry { Path = path, Size = size, Duration = 2, Created = DateTime.Now, CopiumLink = link };
        }

        public Task<BulkShareResult> Many(IReadOnlyList<ClipEntry> entries, Func<int, bool>? begin = null, CancellationToken ct = default) =>
            App.ShareManyAsync(entries, begin, _ => null, ct);

        public void Dispose()
        {
            CopiumClient.HttpOverride = null;
            App.Dispose();
            Dir.Dispose();
        }
    }

    [UnitTest]
    public static void Consent_is_asked_once_up_front_for_all_clips_and_each_clip_uploads_in_order()
    {
        using var rig = new Rig();
        var clips = new[] { rig.Clip(), rig.Clip(), rig.Clip() };
        var r = Wait(rig.Many(clips));
        Check.Equal(new[] { ConsentContext.ClipShare }, rig.Asked.ToArray(), "asked once, for three clips");
        Check.True(rig.Settings.CopiumConsent);
        Check.Equal(3, rig.Http.Requests);
        Check.Equal(3, r.Uploaded);
        Check.False(r.Declined);
        Check.Equal(new[] { "https://v.copium.pics/up1.mp4", "https://v.copium.pics/up2.mp4", "https://v.copium.pics/up3.mp4" }, r.Links.ToArray(), "sequential, in selection order");
        foreach (var c in clips) Check.False(string.IsNullOrEmpty(c.CopiumLink), "the entry carries its link");

        rig.Asked.Clear();
        var again = Wait(rig.Many(new[] { rig.Clip() }));
        Check.Equal(0, rig.Asked.Count, "agreed already: no question");
        Check.Equal(1, again.Uploaded);
    }

    [UnitTest]
    public static void A_declined_question_uploads_nothing_and_writes_nothing()
    {
        using var rig = new Rig();
        rig.Answer = ConsentChoice.NotNow;
        var r = Wait(rig.Many(new[] { rig.Clip(), rig.Clip() }));
        Check.True(r.Declined);
        Check.Equal(0, rig.Http.Requests);
        Check.Equal(0, r.Uploaded);
        Check.Equal(0, rig.Clipboard.Count, "no clipboard write");
        Check.Equal(0, rig.Notices.Count, "no notice");
        Check.Equal("Not uploaded: sharing with Copium is not enabled", BulkActions.UploadStatus(r, false));
        Check.Equal(1, rig.Asked.Count);
    }

    [UnitTest]
    public static void Without_a_token_or_with_cloud_sharing_off_nothing_is_sent_and_the_result_says_why()
    {
        using (var rig = new Rig(token: false))
        {
            var r = Wait(rig.Many(new[] { rig.Clip() }));
            Check.Equal(AppController.BulkNoTokenText, r.FirstError);
            Check.Equal(0, rig.Http.Requests);
            Check.Equal(0, rig.Asked.Count, "no question without a token");
            Check.Equal(0, rig.Clipboard.Count);
            Check.Equal(AppController.BulkNoTokenText, BulkActions.UploadStatus(r, false));
        }
        using (var rig = new Rig(cloud: false))
        {
            var r = Wait(rig.Many(new[] { rig.Clip() }));
            Check.Equal(AppController.CloudOffText, r.FirstError);
            Check.Equal(0, rig.Http.Requests);
            Check.Equal(0, rig.Asked.Count);
            Check.Equal(0, rig.Clipboard.Count);
        }
    }

    [UnitTest]
    public static void All_links_go_to_the_clipboard_once_in_selection_order_with_one_notice()
    {
        using var rig = new Rig(consent: true);
        var a = rig.Clip("https://v.copium.pics/old.mp4");
        var b = rig.Clip();
        var c = rig.Clip();
        var r = Wait(rig.Many(new[] { b, a, c }));
        Check.Equal(2, rig.Http.Requests, "the linked clip is not uploaded again");
        Check.Equal(1, r.AlreadyLinked);
        Check.Equal(2, r.Uploaded);
        Check.Equal(1, rig.Clipboard.Count, "one write");
        Check.Equal("https://v.copium.pics/up1.mp4\r\nhttps://v.copium.pics/old.mp4\r\nhttps://v.copium.pics/up2.mp4", rig.Clipboard[0], "selection order, existing and new");
        Check.Equal(1, rig.Notices.Count, "one notice");
        Check.Equal("Links copied", rig.Notices[0].Title);
        Check.Equal("Uploaded 2 clips. 3 links are on the clipboard.", rig.Notices[0].Text);
    }

    [UnitTest]
    public static void A_quiet_single_share_neither_copies_nor_toasts_and_never_opens_a_window()
    {
        using var rig = new Rig(consent: true);
        int windows = 0;
        rig.App.ShareWindowOpener = _ => { windows++; return true; };
        var linked = rig.Clip("https://v.copium.pics/old.mp4");
        Check.True(Wait(rig.App.ShareAsync(linked, quiet: true)));
        Check.Equal(ShareOutcome.LinkCopied, rig.App.LastShareOutcome);
        var fresh = rig.Clip();
        Check.True(Wait(rig.App.ShareAsync(fresh, quiet: true)));
        Check.Equal(ShareOutcome.Uploaded, rig.App.LastShareOutcome);
        Check.Equal(0, rig.Clipboard.Count, "quiet: no clipboard write");
        // (the Shared toast is not observable here: it is the same branch as the clipboard write, and the toast setting is off in the rig)
        Check.Equal(1, rig.Http.Requests);

        // the same share, not quiet, copies (unchanged behaviour)
        Check.True(Wait(rig.App.ShareAsync(rig.Clip())));
        Check.Equal(1, rig.Clipboard.Count);

        rig.Settings.CopiumToken = null;
        Check.False(Wait(rig.App.ShareAsync(rig.Clip(), quiet: true)));
        Check.Equal(0, windows, "no token: a quiet share opens no window");
        Check.False(Wait(rig.App.ShareAsync(rig.Clip())));
        Check.Equal(1, windows, "and a normal one still does");
    }

    [UnitTest]
    public static void Too_large_busy_and_linked_clips_are_skipped_without_a_request_and_the_summary_says_so()
    {
        using var rig = new Rig(consent: true);
        var linked = rig.Clip("https://v.copium.pics/old.mp4");
        var big = rig.Clip(size: 201L * 1000 * 1000);
        var busy = rig.Clip();
        var ok = rig.Clip();
        var began = new List<int>();
        var r = Wait(rig.Many(new[] { linked, big, busy, ok }, i => { began.Add(i); return i != 2; }));
        Check.Equal(1, rig.Http.Requests, "only the free clip went up");
        Check.Equal(new[] { 2, 3 }, began.ToArray(), "begin is asked for the clips that would upload (not the linked or the too large)");
        Check.Equal(1, r.Refused);
        Check.Equal(1, r.Busy);
        Check.Equal(1, r.Uploaded);
        Check.Equal(1, r.AlreadyLinked);
        Check.Equal(4, r.Total);
        Check.True(string.IsNullOrEmpty(big.CopiumLink) && string.IsNullOrEmpty(busy.CopiumLink));
        Check.Equal("https://v.copium.pics/old.mp4\r\nhttps://v.copium.pics/up1.mp4", rig.Clipboard.Single());
        Check.Equal("Uploaded 1 of 3 clips. 2 links are on the clipboard. 1 is too large to share. 1 was busy.", rig.Notices.Single().Text);
    }

    [UnitTest]
    public static void With_nothing_uploaded_there_is_no_clipboard_write_and_the_notice_says_why()
    {
        using var rig = new Rig(consent: true);
        var big = rig.Clip(size: 201L * 1000 * 1000);
        var r = Wait(rig.Many(new[] { big }));
        Check.Equal(0, rig.Clipboard.Count);
        Check.Equal(0, rig.Http.Requests);
        Check.Equal("Nothing uploaded", rig.Notices.Single().Title);
        Check.Equal("Nothing was uploaded. 1 is too large to share.", rig.Notices.Single().Text);
        Check.Equal(1, r.Refused);
    }

    [UnitTest]
    public static void A_failed_upload_is_counted_the_rest_go_on_and_the_first_error_is_told()
    {
        using var rig = new Rig(consent: true);
        rig.Http.FailFirst = true;
        var r = Wait(rig.Many(new[] { rig.Clip(), rig.Clip() }));
        Check.Equal(1, r.Failed);
        Check.Equal(1, r.Uploaded);
        Check.NotNull(r.FirstError);
        Check.Contains(rig.Notices.Single().Text, "1 link is on the clipboard.");
        Check.Contains(rig.Notices.Single().Text, r.FirstError!.TrimEnd('.'));
    }

    [UnitTest]
    public static void A_rejected_token_stops_the_batch_after_the_first_request_and_says_so()
    {
        using var rig = new Rig(consent: true);
        rig.Http.FirstStatus = HttpStatusCode.Unauthorized;
        var clips = new[] { rig.Clip(), rig.Clip(), rig.Clip() };
        var r = Wait(rig.Many(clips));
        Check.Equal(1, rig.Http.Requests, "the batch stopped instead of trying every clip");
        Check.Equal(0, r.Uploaded);
        Check.False(string.IsNullOrWhiteSpace(r.FirstError));
        Check.Contains(rig.Notices.Single().Text, r.FirstError!.TrimEnd('.'));
        Check.Equal(0, rig.Clipboard.Count);
    }

    [UnitTest]
    public static void A_failed_clipboard_write_says_the_links_were_not_copied()
    {
        using var rig = new Rig(consent: true);
        rig.App.ClipboardOverride = _ => throw new InvalidOperationException("clipboard busy");
        var clips = new[] { rig.Clip(), rig.Clip() };
        var r = Wait(rig.Many(clips));
        Check.Equal(2, r.Uploaded);
        Check.Equal("Links not copied", rig.Notices.Single().Title);
        const string allLinked = "Uploaded 2 clips. 2 links are ready but couldn't be copied to the clipboard. Use Copy links to try again.";
        Check.Equal(allLinked, rig.Notices.Single().Text);
        Check.Equal(allLinked, BulkActions.UploadStatus(r, false), "the bar says the same");
        Check.True(r.NotCopied);
        Check.False(string.IsNullOrEmpty(clips[0].CopiumLink), "the links stay on the entries");
        Check.True(rig.App.TryCopyToClipboard("x") == false);
    }

    [UnitTest]
    public static void A_failed_clipboard_write_does_not_promise_Copy_links_when_some_clip_has_no_link()
    {
        using var rig = new Rig(consent: true);
        rig.App.ClipboardOverride = _ => throw new InvalidOperationException("clipboard busy");
        var r = Wait(rig.Many(new[] { rig.Clip(), rig.Clip(size: 201L * 1000 * 1000) }));
        const string partial = "Uploaded 1 of 2 clips. 1 link is ready but couldn't be copied to the clipboard. 1 is too large to share.";
        Check.Equal("Links not copied", rig.Notices.Single().Title);
        Check.Equal(partial, rig.Notices.Single().Text);
        Check.Equal(partial, BulkActions.UploadStatus(r, false));
        Check.Equal("Uploaded 1 clip. 1 link is ready but couldn't be copied to the clipboard. Use Copy links to try again.",
            BulkActions.UploadSummary(1, 0, 1, 0, 0, 0, null, false, false, notCopied: true, canRetryCopy: true));
    }

    [UnitTest]
    public static void A_stop_after_a_failure_tells_both_and_how_many_were_not_tried()
    {
        using var rig = new Rig(consent: true);
        rig.Http.FailAt.Add(2);
        rig.Http.StatusAt[3] = HttpStatusCode.Unauthorized;
        var r = Wait(rig.Many(new[] { rig.Clip(), rig.Clip(), rig.Clip(), rig.Clip(), rig.Clip() }));
        Check.Equal(3, rig.Http.Requests);
        Check.Equal(1, r.Uploaded);
        Check.Equal(1, r.Failed);
        Check.Equal(2, r.NotTried);
        Check.False(string.IsNullOrWhiteSpace(r.StopError));
        string text = rig.Notices.Single().Text;
        Check.Contains(text, r.FirstError!.TrimEnd('.'));
        Check.Contains(text, "Stopped: " + r.StopError!.TrimEnd('.') + ".");
        Check.True(text.EndsWith(" 2 were not tried."), text);
        Check.Equal(text, BulkActions.UploadStatus(r, false));
    }

    [UnitTest]
    public static void Consent_is_not_asked_when_nothing_will_upload_because_everything_is_linked_or_too_large()
    {
        using var rig = new Rig();
        var r = Wait(rig.Many(new[] { rig.Clip("https://v.copium.pics/old.mp4"), rig.Clip(size: 201L * 1000 * 1000) }));
        Check.Equal(0, rig.Asked.Count, "no question: no upload");
        Check.Equal(1, r.AlreadyLinked);
        Check.Equal(1, r.Refused);
        Check.Equal(0, rig.Http.Requests);
    }

    [UnitTest]
    public static void A_failing_screenshot_batch_shows_no_problem_toast_per_picture_and_one_notice()
    {
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, uploader: (_, _, _, _, _, _) => Task.FromException<CopiumUpload>(new InvalidOperationException("Copium is having trouble (HTTP 500)")));
        using var app = new AppController(rig.Settings, new Updater(rig.Settings));
        app.UseScreenshotsForTests(rig.Service);
        var clipboard = new List<string>();
        var notices = new List<(string, string)>();
        app.ClipboardOverride = clipboard.Add;
        app.BulkNoticeSink = (t, x) => notices.Add((t, x));
        var shots = new[] { ScreenshotUploadTests.EntryFor(dir, "a.png"), ScreenshotUploadTests.EntryFor(dir, "b.png"), ScreenshotUploadTests.EntryFor(dir, "c.png") };
        var r = Wait(app.UploadShotsAsync(shots, null, _ => null, CancellationToken.None));
        Check.Equal(3, r.Failed);
        Check.Equal(0, rig.Toasts.Shown.Count(s => s.StartsWith("problem")), "no problem toast in a batch");
        Check.Equal(1, notices.Count, "one closing notice");
        Check.Contains(r.FirstError ?? "", "Copium is having trouble");
        Check.Equal("Nothing was uploaded. 3 failed: Copium is having trouble (HTTP 500).", notices[0].Item2);
        Check.False(notices[0].Item2.Contains("Saved locally"), "a batch text, not a toast text");
        Check.Equal(0, clipboard.Count);
    }

    [UnitTest]
    public static void Cancel_stops_after_the_current_item_and_keeps_what_was_uploaded()
    {
        using var rig = new Rig(consent: true);
        using var cts = new CancellationTokenSource();
        rig.Http.OnRequest = n => { if (n == 1) cts.Cancel(); };   // Cancel pressed while the first upload is under way
        var clips = new[] { rig.Clip(), rig.Clip(), rig.Clip() };
        var r = Wait(rig.Many(clips, ct: cts.Token));
        Check.True(r.Cancelled);
        Check.Equal(1, rig.Http.Requests, "the item in hand finished, the others never started");
        Check.Equal(1, r.Uploaded);
        Check.Equal("https://v.copium.pics/up1.mp4", rig.Clipboard.Single(), "what was uploaded is copied");
        Check.False(string.IsNullOrEmpty(clips[0].CopiumLink));
        Check.True(string.IsNullOrEmpty(clips[1].CopiumLink));
        Check.Contains(rig.Notices.Single().Text, "Cancelled.");
    }

    [UnitTest]
    public static void A_clip_a_single_share_is_uploading_is_waited_for_and_uses_that_result()
    {
        using var rig = new Rig(consent: true);
        var gate = new TaskCompletionSource();
        rig.Http.HoldFirst = gate.Task;
        var a = rig.Clip();
        var b = rig.Clip();
        var single = rig.App.ShareAsync(a);   // not quiet: copies its own link when done
        ScreenshotUploadTests.Pump(() => rig.Http.FirstStarted.Task.IsCompleted, "the single upload to be under way");
        var began = new List<int>();
        var many = rig.Many(new[] { a, b }, i => { began.Add(i); return true; });
        Check.False(many.IsCompleted, "waits for the running upload");
        gate.SetResult();
        var r = Wait(many);
        Check.True(Wait(single));
        Check.Equal(2, rig.Http.Requests, "the clip went up once");
        Check.Equal(new[] { -1, 1 }, began.ToArray(), "no turn is taken for the clip the single share owns: the previous row is released (-1) before waiting");
        Check.Equal(2, r.Uploaded);
        Check.Equal("https://v.copium.pics/up1.mp4\r\nhttps://v.copium.pics/up2.mp4", rig.Clipboard.Last());
    }

    // ------------------------------------------------------------------ screenshots

    private static Task<CopiumUpload> PerFile(string token, string path, string? title, IProgress<double>? progress, CancellationToken ct, string? url)
    {
        progress?.Report(1);
        string link = "https://v.copium.pics/" + Path.GetFileName(path);
        return Task.FromResult(new CopiumUpload("id", link, link, "", null, 1));
    }

    [UnitTest]
    public static void A_batch_screenshot_upload_copies_no_link_and_shows_no_success_toast_but_the_default_still_does()
    {
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, uploader: PerFile);
        var one = ScreenshotUploadTests.EntryFor(dir, "a.png");
        var ok = Wait(rig.Service.UploadAsync(one, manual: true, batch: true));
        Check.True(ok);
        Check.Equal("https://v.copium.pics/a.png", one.CopiumLink);
        Check.Equal(0, rig.Copied.Count, "batch: the link is not copied");
        Check.Equal(0, rig.Toasts.Shown.Count, "batch: no uploading toast, no success toast");

        var two = ScreenshotUploadTests.EntryFor(dir, "b.png");
        Check.True(Wait(rig.Service.UploadAsync(two, manual: true)));
        Check.Equal(new[] { "https://v.copium.pics/b.png" }, rig.Copied.ToArray(), "default: the link is copied");
        Check.True(rig.Toasts.Shown.Any(s => s.StartsWith("shared: ")), "default: the success toast");
    }

    [UnitTest]
    public static void UploadShotsAsync_copies_all_screenshot_links_once()
    {
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, uploader: PerFile);
        using var app = new AppController(rig.Settings, new Updater(rig.Settings));
        app.UseScreenshotsForTests(rig.Service);
        var clipboard = new List<string>();
        var notices = new List<(string, string)>();
        app.ClipboardOverride = clipboard.Add;
        app.BulkNoticeSink = (t, x) => notices.Add((t, x));
        var a = ScreenshotUploadTests.EntryFor(dir, "a.png");
        var b = ScreenshotUploadTests.EntryFor(dir, "b.png");
        b.CopiumLink = "https://v.copium.pics/old.png";
        var c = ScreenshotUploadTests.EntryFor(dir, "c.png");
        var began = new List<int>();
        var r = Wait(app.UploadShotsAsync(new[] { a, b, c }, i => { began.Add(i); return true; }, _ => null, CancellationToken.None));
        Check.Equal(2, r.Uploaded);
        Check.Equal(1, r.AlreadyLinked);
        Check.Equal(new[] { 0, 2 }, began.ToArray());
        Check.Equal(1, clipboard.Count);
        Check.Equal("https://v.copium.pics/a.png\r\nhttps://v.copium.pics/old.png\r\nhttps://v.copium.pics/c.png", clipboard[0]);
        Check.Equal(0, rig.Copied.Count, "the service itself copied nothing");
        Check.Equal("Uploaded 2 screenshots. 3 links are on the clipboard.", notices.Single().Item2);
        Check.Equal("Links copied", notices.Single().Item1);
    }

    // ------------------------------------------------------------------ the pure texts

    [UnitTest]
    public static void The_share_menu_offers_Upload_and_copy_links_first_when_a_clip_has_no_link()
    {
        var menu = BulkActions.ShareMenu(true, true, 3, 1, true)!;
        Check.Equal(BulkAction.Upload, menu[0].Action);
        Check.Equal("_Upload and copy links", menu[0].Header);
        Check.Equal("Upload the clips that have no link yet, then copy all links", menu[0].ToolTip);
        Check.Equal("Upload the screenshots that have no link yet, then copy all links", BulkActions.ShareMenu(true, true, 3, 1, true, screenshots: true)![0].ToolTip);
        Check.Equal("CopyLinks", BulkActions.ShareMenu(true, true, 3, 3, true)![0].Action.ToString(), "all linked: Copy links");
        Check.Null(BulkActions.ShareMenu(false, true, 3, 0, true), "no token: no menu");
        Check.True(BulkActions.UploadOffered(true, true, 3, 0));
        Check.True(BulkActions.UploadOffered(true, true, 3, 2));
        Check.False(BulkActions.UploadOffered(true, true, 3, 3), "all linked");
        Check.False(BulkActions.UploadOffered(false, true, 3, 0), "no token");
        Check.False(BulkActions.UploadOffered(true, false, 3, 0), "cloud off");
        Check.False(BulkActions.UploadOffered(true, true, 0, 0), "nothing selected");
        Check.Equal("Upload and copy links", BulkActions.UploadLabel);
    }

    [UnitTest]
    public static void The_upload_summary_reads_for_clips_and_screenshots()
    {
        Check.Equal("Uploaded 5 of 6 clips. 5 links are on the clipboard. 1 is too large to share.", BulkActions.UploadSummary(5, 0, 6, 1, 0, 0, null, false, false));
        Check.Equal("Uploaded 6 clips. 6 links are on the clipboard.", BulkActions.UploadSummary(6, 0, 6, 0, 0, 0, null, false, false));
        Check.Equal("Uploaded 1 clip. 1 link is on the clipboard.", BulkActions.UploadSummary(1, 0, 1, 0, 0, 0, null, false, false));
        Check.Equal("Uploaded 3 screenshots. 3 links are on the clipboard.", BulkActions.UploadSummary(3, 0, 3, 0, 0, 0, null, false, true));
        Check.Equal("Uploaded 2 clips. 3 links are on the clipboard.", BulkActions.UploadSummary(2, 1, 3, 0, 0, 0, null, false, false), "an existing link counts as on the clipboard");
        Check.Equal("3 links are on the clipboard.", BulkActions.UploadSummary(0, 3, 3, 0, 0, 0, null, false, false), "everything linked already");
        Check.Equal("Uploaded 1 of 3 clips. 1 link is on the clipboard. 2 were busy.", BulkActions.UploadSummary(1, 0, 3, 0, 2, 0, null, false, false));
        Check.Equal("Uploaded 1 of 2 clips. 1 link is on the clipboard. 1 was busy.", BulkActions.UploadSummary(1, 0, 2, 0, 1, 0, null, false, false));
        Check.Equal("Uploaded 1 of 2 clips. 1 link is on the clipboard. 2 are too large to share.", BulkActions.UploadSummary(1, 0, 2, 2, 0, 0, null, false, false));
        Check.Equal("Uploaded 1 of 2 clips. 1 link is on the clipboard. Can't reach Copium - check your connection.", BulkActions.UploadSummary(1, 0, 2, 0, 0, 1, "Can't reach Copium - check your connection", false, false));
        Check.Equal("Uploaded 1 of 3 clips. 1 link is on the clipboard. 2 failed.", BulkActions.UploadSummary(1, 0, 3, 0, 0, 2, null, false, false));
        Check.Equal("Uploaded 1 of 3 clips. 1 link is on the clipboard. 2 failed: Boom.", BulkActions.UploadSummary(1, 0, 3, 0, 0, 2, "Boom", false, false), "several failures: the count and the first reason");
        Check.Equal("Uploaded 1 of 3 clips. 1 link is on the clipboard. Cancelled.", BulkActions.UploadSummary(1, 0, 3, 0, 0, 0, null, true, false));
        Check.Equal("Nothing was uploaded. 1 is too large to share.", BulkActions.UploadSummary(0, 0, 1, 1, 0, 0, null, false, false));
        Check.Equal("Nothing was uploaded. Cancelled.", BulkActions.UploadSummary(0, 0, 2, 0, 0, 0, null, true, false));
        Check.Equal("Uploaded 1 of 4 clips. 1 link is on the clipboard. Can't reach Copium. Stopped: Copium rejected the token (HTTP 401). 2 were not tried.",
            BulkActions.UploadSummary(1, 0, 4, 0, 0, 1, "Can't reach Copium", false, false, "Copium rejected the token (HTTP 401)", 2));
        Check.Equal("Uploaded 1 of 3 clips. 1 link is on the clipboard. Copium rejected the token (HTTP 401). 1 was not tried.",
            BulkActions.UploadSummary(1, 0, 3, 0, 0, 0, null, false, false, "Copium rejected the token (HTTP 401)", 1), "a stop without a failure: the reason alone");
        Check.Equal("Nothing was uploaded. 2 failed: Boom. Stopped: Sign in. 1 was not tried.",
            BulkActions.UploadSummary(0, 0, 4, 0, 0, 2, "Boom", false, false, "Sign in", 1));
        Check.Equal("Uploading 2 of 6…", BulkActions.UploadingStatus(2, 6));
        Check.Equal("Uploading 2 of 6… 40%", BulkActions.UploadingStatus(2, 6, 0.4));
        Check.Equal("Uploading 2 of 6…", BulkActions.UploadingStatus(2, 6, 0), "no percentage before the first report");
        Check.True(BulkActions.BarVisible(0, true), "the bar stays while uploading");
    }
}
