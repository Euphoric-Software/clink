using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Clink.Native;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Screenshots.Sources;
using Clink.Services;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>
/// Slice S6: the Copium client for pictures (content type, endpoint override, error mapping, through a mock HTTP handler), the
/// service's upload path (link on the entry, clipboard, toasts, refusal flag, queue, exit cancellation), the "link your account"
/// hint, the new settings, and the export that keeps only the WPF drawing on the UI thread.
/// </summary>
internal static class ScreenshotUploadTests
{
    // ------------------------------------------------------------------ CopiumClient

    private sealed class MockHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => Json(HttpStatusCode.OK, Ok);
        public readonly List<(Uri? Uri, string? Auth, string Body)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString(), body));
            return Respond(request);
        }
    }

    private const string Ok = """{"id":"abc","link":"https://v.copium.pics/abc.png","url":"https://v.copium.pics/abc.png","pageUrl":"https://copium.pics/p/abc","size":12}""";

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (CopiumUpload? Up, Exception? Error, MockHandler Handler) Upload(string fileName, Func<HttpRequestMessage, HttpResponseMessage>? respond = null, string? imageEndpoint = null)
    {
        using var dir = new TempDir();
        string path = dir.File(fileName);
        File.WriteAllText(path, "not really a picture");
        var handler = new MockHandler();
        if (respond != null) handler.Respond = respond;
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            var up = Task.Run(() => CopiumClient.UploadAsync("eup_secret", path, "A title", null, CancellationToken.None, imageEndpoint)).GetAwaiter().GetResult();
            return (up, null, handler);
        }
        catch (Exception ex) { return (null, ex, handler); }
        finally { CopiumClient.HttpOverride = null; }
    }

    [UnitTest]
    public static void The_content_type_follows_the_extension_and_pictures_are_told_from_clips()
    {
        Check.Equal("video/mp4", CopiumClient.ContentTypeFor(@"C:\c\clip.mp4"));
        Check.Equal("video/mp4", CopiumClient.ContentTypeFor("CLIP.MP4"), "case does not matter");
        Check.Equal("image/png", CopiumClient.ContentTypeFor("a.png"));
        Check.Equal("image/jpeg", CopiumClient.ContentTypeFor("a.jpg"));
        Check.Equal("image/jpeg", CopiumClient.ContentTypeFor("a.JPEG"));
        Check.Equal("image/gif", CopiumClient.ContentTypeFor("a.gif"));
        Check.Equal("image/webp", CopiumClient.ContentTypeFor("a.webp"));
        Check.Equal("application/octet-stream", CopiumClient.ContentTypeFor("a.bin"));
        Check.Equal("application/octet-stream", CopiumClient.ContentTypeFor("noextension"));
        Check.Equal(MediaKind.Image, CopiumClient.KindOf("a.png"));
        Check.Equal(MediaKind.Video, CopiumClient.KindOf("a.mp4"));
        Check.Equal(MediaKind.Video, CopiumClient.KindOf("a.bin"), "anything unknown is handled as a clip");
    }

    [UnitTest]
    public static void The_file_part_carries_the_content_type_of_its_extension_and_the_bearer_token()
    {
        foreach (var (file, type) in new[] { ("Shot 1.png", "image/png"), ("a.jpg", "image/jpeg"), ("clip.mp4", "video/mp4"), ("data.bin", "application/octet-stream") })
        {
            var (up, error, handler) = Upload(file);
            Check.Null(error, file);
            Check.Equal("https://v.copium.pics/abc.png", up!.Link);
            Check.Equal("https://copium.pics/p/abc", up.PageUrl);
            var req = handler.Requests.Single();
            Check.Equal("Bearer eup_secret", req.Auth, file);
            Check.Contains(req.Body, "Content-Type: " + type, file);
            Check.True(req.Body.Contains("filename=" + file) || req.Body.Contains("filename=\"" + file + "\""), file + ": the file name goes out (quoted when it has a space)");
            Check.Equal(CopiumClient.UploadEndpoint, req.Uri!.ToString(), "the usual endpoint");
        }
    }

    [UnitTest]
    public static void The_image_endpoint_override_is_used_for_pictures_only_and_only_when_it_is_absolute_https()
    {
        const string host = "https://img.copium.pics/api/up";
        Check.Equal(host, CopiumClient.EndpointFor(MediaKind.Image, host));
        Check.Equal(host, CopiumClient.EndpointFor(MediaKind.Image, "  " + host + "  "), "trimmed");
        Check.Equal(CopiumClient.UploadEndpoint, CopiumClient.EndpointFor(MediaKind.Video, host), "a clip never uses it");
        foreach (var bad in new[] { null, "", "   ", "http://img.copium.pics/up", "/api/up", "img.copium.pics/up", "ftp://x/y", "https://", "file:///C:/x", "javascript:alert(1)",
                                    "https://evil.example/up", "https://img.example.com/api/up", "https://copium.pics.evil.example/up", "https://notcopium.pics/up", "https://copium.pics@evil.example/up", "https://evil.example/copium.pics" })
            Check.Equal(CopiumClient.UploadEndpoint, CopiumClient.EndpointFor(MediaKind.Image, bad), $"'{bad}' is ignored");

        // end to end: the picture goes to the override, the clip of the same call shape does not
        var (_, e1, picture) = Upload("a.png", imageEndpoint: host);
        Check.Null(e1);
        Check.Equal(host, picture.Requests.Single().Uri!.ToString());
        var (_, e2, clip) = Upload("a.mp4", imageEndpoint: host);
        Check.Null(e2);
        Check.Equal(CopiumClient.UploadEndpoint, clip.Requests.Single().Uri!.ToString());
        var (_, e3, bad2) = Upload("a.png", imageEndpoint: "http://insecure.copium.pics/up");
        Check.Null(e3);
        Check.Equal(CopiumClient.UploadEndpoint, bad2.Requests.Single().Uri!.ToString(), "an http override is ignored");
        var (_, e4, evil) = Upload("a.png", imageEndpoint: "https://evil.example/up");
        Check.Null(e4);
        Check.Equal(CopiumClient.UploadEndpoint, evil.Requests.Single().Uri!.ToString(), "the token never goes to another host");
    }

    [UnitTest]
    public static void Upload_errors_are_mapped_to_one_clear_sentence_with_generic_nouns()
    {
        HttpResponseMessage Status(int code, string body = "") => Json((HttpStatusCode)code, body);

        // a picture Copium does not take (400 / 415) says so and carries the status for the caller
        foreach (int code in new[] { 400, 415 })
        {
            var (_, error, _) = Upload("a.png", _ => Status(code, """{"error":"Unsupported media type"}"""));
            var http = (CopiumHttpException)error!;
            Check.Equal(code, http.StatusCode);
            Check.Equal("Copium did not accept the image (Unsupported media type)", http.Message);
        }
        // a clip with the same status keeps the old wording
        Check.Equal("Copium refused the upload (HTTP 415)", ((CopiumHttpException)Upload("a.mp4", _ => Status(415)).Error!).Message);
        Check.Equal("Screenshot is larger than your Copium upload limit (HTTP 413)", ((CopiumHttpException)Upload("a.png", _ => Status(413)).Error!).Message);
        Check.Equal("Clip is larger than your Copium upload limit (HTTP 413)", ((CopiumHttpException)Upload("a.mp4", _ => Status(413)).Error!).Message);
        var server = (CopiumHttpException)Upload("a.png", _ => Status(500, "<html>bad gateway</html>")).Error!;
        Check.Equal(500, server.StatusCode);
        Check.Equal("Copium is having trouble (HTTP 500), try again later", server.Message);
        Check.Equal("Copium rejected the token (HTTP 401)", ((CopiumHttpException)Upload("a.png", _ => Status(401)).Error!).Message);
        // the shared helper still answers the old two-argument calls
        Check.Equal("Clip is larger than your Copium upload limit (HTTP 413)", CopiumClient.ErrorMessage(413, ""));

        var offline = Upload("a.png", _ => throw new HttpRequestException("no route"));
        Check.False(offline.Error is CopiumHttpException);
        Check.Equal("Can't reach Copium - check your connection", offline.Error!.Message);
        // the token is never part of an error
        Check.DoesNotContain(offline.Error.Message, "eup_secret");
    }

    // ------------------------------------------------------------------ the service

    internal sealed class FakeToasts : IScreenshotToasts
    {
        public readonly List<string> Shown = new();
        public ScreenshotToastState LastState;
        public readonly List<double> Progress = new();
        public void ShowNotice(string title, string text, bool paused = false) => Shown.Add("notice: " + title);
        public void ShowProblem(string title, string text) { Shown.Add("problem: " + title); LastProblemText = text; }
        public string LastProblemText = "";
        public ScreenshotEntry? LastEntry;
        public void ShowScreenshot(ScreenshotEntry entry, ScreenshotToastState state) { Shown.Add("screenshot: " + Path.GetFileName(entry.Path)); LastState = state; LastEntry = entry; }
        public readonly List<Handle> Handles = new();
        public IScreenshotUploadToast ShowScreenshotUploading(ScreenshotEntry entry) { Shown.Add("uploading: " + Path.GetFileName(entry.Path)); var h = new Handle(this); lock (Handles) Handles.Add(h); return h; }

        public sealed class Handle : IScreenshotUploadToast
        {
            private readonly FakeToasts _t;
            public Handle(FakeToasts t) { _t = t; }
            public Action? OnCancel { get; set; }
            public void Report(double fraction) => _t.Progress.Add(fraction);
            public void Succeeded(string link, bool linkCopied) => _t.Shown.Add("shared: " + link + (linkCopied ? " (copied)" : ""));
            public void Failed() => _t.Shown.Add("upload failed");
            public void Declined() => _t.Shown.Add("upload declined");
        }
    }

    internal sealed class OnePicker : IRegionPicker
    {
        public Task<RegionPickResult?> PickAsync(FrozenDesktop desktop, bool loupe, CancellationToken ct, OverlayEditSetup? edit = null) =>
            Task.FromResult<RegionPickResult?>(new RegionPickResult(new RectI(5, 6, 10, 8), 0, PickOutcome.Drag));
        public void Cancel() { }
        public void Close() { }
    }

    internal sealed class FlatSource : IScreenshotSource
    {
        public string Name => "flat";
        public bool Handles(MonitorInfo monitor) => true;
        public FrozenMonitor Capture(MonitorInfo monitor)
        {
            var img = new BgraImage(monitor.Width, monitor.Height);
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++) img.SetPixel(x, y, (byte)(x * 4), (byte)(y * 4), 128);
            return new FrozenMonitor(monitor, img, Name);
        }
    }

    internal sealed class Rig
    {
        public required ScreenshotService Service;
        public required Settings Settings;
        public required FakeToasts Toasts;
        public readonly List<(string Token, string Path, string? Title, string? Url)> Calls = new();
        public readonly List<string> Copied = new();
        /// <summary>The made-up clipboard's change counter: our image copy moves it, so does <see cref="SomeoneCopies"/>.</summary>
        public uint Sequence = 100;
        public int ImageCopies;
        public bool GameInFront;
        public void SomeoneCopies() => Sequence++;
        public DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        public string Folder => ScreenshotService.FolderFor(Settings);
        public string[] Pngs => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.png") : Array.Empty<string>();
    }

    internal static CopiumUpload Done(long size = 10) => new("id1", "https://v.copium.pics/id1.png", "https://v.copium.pics/id1.png", "https://copium.pics/p/id1", null, size);

    internal static Rig Make(TempDir dir, bool token = true, bool upload = true, bool copyLink = true, bool copyImage = false, bool consent = true, bool cloud = true,
        Func<string, string, string?, IProgress<double>?, CancellationToken, string?, Task<CopiumUpload>>? uploader = null)
    {
        Settings.DirOverride = dir.Path;   // the history file and the settings file
        var s = Settings.FromJson("{}");
        s.ClipsFolder = dir.File("clips");
        s.ScreenshotCopyImage = copyImage;  // never the real clipboard: ImageCopier below is a made-up one
        s.CopiumConsent = consent;       // sharing is opt-in: most tests are about what happens after the user said yes
        s.CloudSharingEnabled = cloud;
        s.ClickSound = false;
        s.ScreenshotUpload = upload;
        s.ScreenshotCopyLink = copyLink;
        if (token) s.CopiumToken = "eup_test_token";
        var toasts = new FakeToasts();
        var monitor = new MonitorInfo(0, @"\\.\DISPLAY1", "", 0, 0, 64, 48, true, false, 80, 0, "");
        var service = new ScreenshotService(s, toasts, new OnePicker(), () => null, () => null, new IScreenshotSource[] { new FlatSource() }, () => new[] { monitor }, null);
        var rig = new Rig { Service = service, Settings = s, Toasts = toasts };
        service.Uploader = uploader ?? ((t, p, title, progress, ct, url) =>
        {
            lock (rig.Calls) rig.Calls.Add((t, p, title, url));
            progress?.Report(1);
            return Task.FromResult(Done(new FileInfo(p).Length));
        });
        service.LinkCopier = text => { rig.Copied.Add(text); return true; };
        service.ImageCopier = (_, _) => { rig.ImageCopies++; rig.Sequence++; return true; };
        service.ClipboardSequence = () => rig.Sequence;
        service.UtcNow = () => rig.Now;
        service.GameInFront = () => rig.GameInFront;
        return rig;
    }

    internal static void Pump(Func<bool> done, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Check.Fail("timed out waiting for " + what);
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    /// <summary>A whole capture (the made-up screen, a 10 x 8 pick) and the upload it starts, to the end.</summary>
    internal static void Capture(Rig rig)
    {
        var t = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => t.IsCompleted, "the capture");
        Check.True(t.IsCompletedSuccessfully);
        var queue = rig.Service.UploadQueue;
        Pump(() => queue.IsCompleted && rig.Service.UploadQueue.IsCompleted, "the upload queue");
    }

    internal static ScreenshotEntry EntryFor(TempDir dir, string name, int bytes = 100)
    {
        string path = dir.File(name);
        File.WriteAllBytes(path, new byte[bytes]);
        return new ScreenshotEntry { Path = path, Size = bytes, Width = 4, Height = 4, Created = DateTime.Now };
    }

    [UnitTest]
    public static void A_saved_screenshot_with_a_token_is_uploaded_the_link_replaces_the_clipboard_and_is_kept_in_the_history()
    {
        using var dir = new TempDir();
        var rig = Make(dir, copyImage: true);
        rig.Settings.CopiumImageUploadUrl = "https://img.copium.pics/up";
        Capture(rig);
        string png = Path.GetFileName(rig.Pngs.Single());
        Check.Equal(new[] { "screenshot: " + png, "notice: Screenshots go to img.copium.pics", "uploading: " + png, "shared: https://v.copium.pics/id1.png (copied)" }, rig.Toasts.Shown.ToArray());
        Check.False(rig.Toasts.LastState.CanUpload, "an automatic upload follows: no Upload button");
        var call = rig.Calls.Single();
        Check.Equal("eup_test_token", call.Token);
        Check.Equal(Path.GetFileNameWithoutExtension(png), call.Title, "the title is the file's name");
        Check.Equal("https://img.copium.pics/up", call.Url, "the image override is handed on");
        Check.Equal(new[] { "https://v.copium.pics/id1.png" }, rig.Copied.ToArray());
        Pump(() => rig.Toasts.Progress.Contains(1.0), "the progress to reach the toast", 3000);
        // the link survives a restart
        var reloaded = new ScreenshotHistory(ScreenshotHistory.DefaultFile, rig.Folder).Entries.Single();
        Check.Equal("https://v.copium.pics/id1.png", reloaded.CopiumLink);
        Check.Equal("https://copium.pics/p/id1", reloaded.CopiumPageUrl);
        Check.Equal("id1", reloaded.CopiumId);
    }

    [UnitTest]
    public static void The_link_stays_off_the_clipboard_when_that_option_is_off_and_nothing_uploads_when_upload_is_off()
    {
        using var dir = new TempDir();
        var noLink = Make(dir, copyLink: false);
        Capture(noLink);
        Check.Equal(0, noLink.Copied.Count);
        Check.True(noLink.Toasts.Shown.Last().StartsWith("shared: ") && !noLink.Toasts.Shown.Last().EndsWith("(copied)"), "uploaded, link not copied");

        using var dir2 = new TempDir();
        var off = Make(dir2, upload: false);
        Capture(off);
        Check.Equal(0, off.Calls.Count);
        Check.Equal(1, off.Toasts.Shown.Count, "just the screenshot toast");
        Check.True(off.Toasts.LastState.CanUpload, "a token and no automatic upload: the toast offers Upload");
    }

    [UnitTest]
    public static void A_failed_upload_says_saved_locally_and_leaves_no_link()
    {
        using var dir = new TempDir();
        var rig = Make(dir, uploader: (_, _, _, _, _, _) => Task.FromException<CopiumUpload>(new InvalidOperationException("Copium is having trouble (HTTP 500), try again later")));
        Capture(rig);
        string png = Path.GetFileName(rig.Pngs.Single());
        Check.Equal(new[] { "screenshot: " + png, "uploading: " + png, "upload failed", "problem: Screenshot not uploaded" }, rig.Toasts.Shown.ToArray());
        Check.True(rig.Toasts.LastProblemText.StartsWith("Saved locally. "), rig.Toasts.LastProblemText);
        Check.Equal(0, rig.Copied.Count);
        Check.False(rig.Service.CopiumImagesRefused, "a 500 is no refusal of pictures");
        Check.Null(new ScreenshotHistory(ScreenshotHistory.DefaultFile, rig.Folder).Entries.Single().CopiumLink);
        Check.Equal(1, rig.Pngs.Length, "the file is there");
    }

    [UnitTest]
    public static void Copium_refusing_a_picture_stops_the_automatic_uploads_but_not_a_manual_one()
    {
        using var dir = new TempDir();
        int attempts = 0;
        bool refuse = true;
        var rig = Make(dir, uploader: (t, p, _, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return refuse ? Task.FromException<CopiumUpload>(new CopiumHttpException(415, "Copium did not accept the image (HTTP 415)")) : Task.FromResult(Done());
        });
        Capture(rig);
        Check.True(rig.Service.CopiumImagesRefused);
        Check.Equal(1, attempts);
        Check.Contains(string.Join("|", rig.Toasts.Shown), "problem: Copium did not accept the image");
        Check.Equal(1, rig.Toasts.Shown.Count(s => s.StartsWith("problem")), "one clear toast");

        rig.Toasts.Shown.Clear();
        Capture(rig);   // the next screenshot: saved, offered an Upload button, not uploaded
        Check.Equal(1, attempts, "no automatic upload after a refusal");
        Check.Equal(1, rig.Toasts.Shown.Count, "only the screenshot toast");
        Check.True(rig.Toasts.LastState.CanUpload, "the button is there for a manual try");

        refuse = false;
        var entry = new ScreenshotHistory(ScreenshotHistory.DefaultFile, rig.Folder).Entries.First();
        var manual = rig.Service.UploadAsync(entry, manual: true);
        Pump(() => manual.IsCompleted, "the manual upload");
        Check.True(manual.Result);
        Check.Equal(2, attempts, "a manual upload still goes");
        Check.Equal("https://v.copium.pics/id1.png", entry.CopiumLink);

        // and a queued automatic upload that was asked for before the refusal is skipped too
        Check.False(rig.Service.UploadAsync(new ScreenshotEntry { Path = entry.Path, Size = 5 }, manual: false).Result, "automatic: skipped");
        Check.Equal(2, attempts);
    }

    [UnitTest]
    public static void Uploads_run_one_at_a_time_in_the_order_they_were_asked()
    {
        using var dir = new TempDir();
        var gates = new List<TaskCompletionSource<CopiumUpload>>();
        var order = new List<string>();
        int active = 0, maxActive = 0;
        var rig = Make(dir, uploader: (t, p, _, _, _, _) =>
        {
            var gate = new TaskCompletionSource<CopiumUpload>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates) { gates.Add(gate); order.Add(Path.GetFileName(p)); }
            int now = Interlocked.Increment(ref active);
            lock (gates) maxActive = Math.Max(maxActive, now);
            return gate.Task.ContinueWith(g => { Interlocked.Decrement(ref active); return g.Result; });
        });
        var a = EntryFor(dir, "a.png");
        var b = EntryFor(dir, "b.png");
        var c = EntryFor(dir, "c.png");
        var ta = rig.Service.UploadAsync(a);
        var tb = rig.Service.UploadAsync(b);
        var tc = rig.Service.UploadAsync(c);
        Pump(() => { lock (gates) return gates.Count == 1; }, "the first upload to start");
        var wait = Stopwatch.StartNew();
        Pump(() => wait.ElapsedMilliseconds > 100, "a moment for a wrong second start");
        lock (gates) Check.Equal(1, gates.Count, "the others wait");
        for (int i = 1; i <= 3; i++)
        {
            int n = i;
            Pump(() => { lock (gates) return gates.Count == n; }, $"upload {n} to start");
            lock (gates) gates[n - 1].SetResult(Done());
        }
        Pump(() => ta.IsCompleted && tb.IsCompleted && tc.IsCompleted, "all three");
        Check.Equal(new[] { "a.png", "b.png", "c.png" }, order.ToArray());
        Check.Equal(1, maxActive, "never two at once");
        Check.True(ta.Result && tb.Result && tc.Result);
        Check.True(a.CopiumLink != null && b.CopiumLink != null && c.CopiumLink != null);
    }

    [UnitTest]
    public static void A_picture_over_the_account_size_limit_is_not_uploaded()
    {
        using var dir = new TempDir();
        var rig = Make(dir);
        var big = EntryFor(dir, "big.png");
        using (var fs = new FileStream(big.Path, FileMode.Open, FileAccess.Write)) fs.SetLength(201_000_000);   // sparse: nothing is written
        big.Size = 201_000_000;
        var t = rig.Service.UploadAsync(big);
        Pump(() => t.IsCompleted, "the check");
        Check.False(t.Result);
        Check.Equal(0, rig.Calls.Count, "nothing sent");
        Check.Equal(new[] { "problem: Screenshot not uploaded" }, rig.Toasts.Shown.ToArray());
        Check.Contains(rig.Toasts.LastProblemText, "Saved locally.");

        rig.Settings.CopiumPlus = true;   // 2 GB
        var t2 = rig.Service.UploadAsync(big);
        Pump(() => t2.IsCompleted, "the upload");
        Check.True(t2.Result);
        Check.Equal(1, rig.Calls.Count);
    }

    [UnitTest]
    public static void A_manual_upload_without_a_token_explains_instead_of_failing_silently()
    {
        using var dir = new TempDir();
        var rig = Make(dir, token: false);
        var t = rig.Service.UploadAsync(EntryFor(dir, "a.png"), manual: true);
        Pump(() => t.IsCompleted, "the answer");
        Check.False(t.Result);
        Check.Equal(0, rig.Calls.Count);
        Check.Equal(new[] { "problem: Screenshot not uploaded" }, rig.Toasts.Shown.ToArray());
    }

    [UnitTest]
    public static void Closing_cancels_a_running_upload_within_the_bound()
    {
        using var dir = new TempDir();
        bool started = false;
        var rig = Make(dir, uploader: async (_, _, _, _, ct, _) =>
        {
            started = true;
            await Task.Delay(Timeout.Infinite, ct);   // a well-behaved HTTP call
            return Done();
        });
        var up = rig.Service.UploadAsync(EntryFor(dir, "a.png"));
        Pump(() => started, "the upload to start");
        var sw = Stopwatch.StartNew();
        rig.Service.CancelUploads(TimeSpan.FromSeconds(4));
        Check.True(sw.ElapsedMilliseconds < 2000, $"cancelled promptly ({sw.ElapsedMilliseconds} ms)");
        Pump(() => up.IsCompleted, "the end of the upload");
        Check.False(up.Result);
        Check.Equal(new[] { "uploading: a.png", "upload failed" }, rig.Toasts.Shown.ToArray(), "no error toast for a cancel on exit");
        // later asks are refused at once
        Check.False(rig.Service.UploadAsync(EntryFor(dir, "b.png")).Result);
    }

    [UnitTest]
    public static void Closing_does_not_wait_longer_than_the_bound_for_an_upload_that_ignores_the_cancel()
    {
        using var dir = new TempDir();
        var stuck = new TaskCompletionSource<CopiumUpload>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        var rig = Make(dir, uploader: (_, _, _, _, _, _) => { started = true; return stuck.Task; });
        var up = rig.Service.UploadAsync(EntryFor(dir, "a.png"));
        Pump(() => started, "the upload to start");
        var sw = Stopwatch.StartNew();
        rig.Service.CancelUploads(TimeSpan.FromMilliseconds(300));
        long ms = sw.ElapsedMilliseconds;
        Check.True(ms >= 250 && ms < 2500, $"waited the bound, not forever ({ms} ms)");
        Check.False(up.IsCompleted, "still running: the process exits anyway");
        stuck.SetException(new IOException("gone"));   // let it end
        Pump(() => up.IsCompleted, "the end");
    }

    // ------------------------------------------------------------------ the proactive "Share with Copium?" pop-up (S7)

    [UnitTest]
    public static void The_proactive_prompt_rules_need_no_consent_no_dont_ask_cloud_on_two_screenshots_under_two_showings_a_day_apart()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        Check.True(CopiumNudge.ShouldPrompt(false, false, true, 2, 0, null, now));
        Check.False(CopiumNudge.ShouldPrompt(false, false, true, 1, 0, null, now), "only after the 2nd screenshot");
        Check.False(CopiumNudge.ShouldPrompt(true, false, true, 2, 0, null, now), "already agreed");
        Check.False(CopiumNudge.ShouldPrompt(false, true, true, 2, 0, null, now), "don't ask again");
        Check.False(CopiumNudge.ShouldPrompt(false, false, false, 2, 0, null, now), "cloud sharing off: never");
        Check.True(CopiumNudge.ShouldPrompt(false, false, true, 2, 1, now.AddHours(-24), now), "second time, exactly a day later");
        Check.False(CopiumNudge.ShouldPrompt(false, false, true, 2, 2, now.AddDays(-30), now), "two is the most");
        Check.False(CopiumNudge.ShouldPrompt(false, false, true, 2, 1, now.AddHours(-23), now), "not twice in a day");
        Check.False(CopiumNudge.ShouldPrompt(false, false, true, 2, 1, now.AddHours(1), now), "a last time in the future shows nothing: IsDue starts the gap from now");
        Check.True(CopiumNudge.ShouldPrompt(false, false, true, 2, 1, DateTime.SpecifyKind(now.AddHours(-30), DateTimeKind.Local).ToUniversalTime(), now), "local time stamps compare as UTC");
    }

    [UnitTest]
    public static void The_pop_up_comes_after_the_second_saved_screenshot_at_most_twice_a_day_apart_and_never_over_a_game()
    {
        using var dir = new TempDir();
        var rig = Make(dir, token: false, upload: false, consent: false);
        var asked = new List<ConsentContext>();
        rig.Service.Consent.Prompt = ctx => { asked.Add(ctx); return Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow)); };
        void Shot() { Capture(rig); var t = rig.Service.PromptTask; Pump(() => t.IsCompleted, "the pop-up decision"); }

        Shot();
        Check.Equal(0, asked.Count, "not after the first screenshot");
        rig.GameInFront = true;
        Shot();
        Check.Equal(0, asked.Count, "the second screenshot, but a game is in front: skipped, not counted");
        Check.Equal(0, rig.Settings.CopiumConsentPromptCount);
        rig.GameInFront = false;
        Shot();
        Check.Equal(new[] { ConsentContext.Proactive }, asked.ToArray(), "next time, when the game is not in front");
        Check.Equal(1, rig.Settings.CopiumConsentPromptCount);
        Check.Equal(rig.Now, rig.Settings.CopiumConsentPromptedUtc);
        Check.Equal(1, Settings.FromJson(rig.Settings.ToJson()).CopiumConsentPromptCount, "saved");
        Shot();
        Check.Equal(1, asked.Count, "not twice in a day");
        rig.Now = rig.Now.AddHours(25);
        Shot();
        Check.Equal(2, asked.Count, "a day later: the second and last time");
        rig.Now = rig.Now.AddDays(40);
        Shot();
        Check.Equal(2, asked.Count, "never a third");
        Check.False(rig.Settings.CopiumConsent);
        Check.Equal(0, rig.Calls.Count, "and nothing was uploaded");
        Check.True(rig.Toasts.Shown.All(s => !s.StartsWith("notice")), "the pop-up is a window, not a toast");
    }

    [UnitTest]
    public static void Dont_ask_again_stops_the_proactive_pop_up_but_an_explicit_share_still_asks()
    {
        using var dir = new TempDir();
        var rig = Make(dir, token: true, upload: false, consent: false);
        int asked = 0;
        var answer = ConsentChoice.DontAskAgain;
        rig.Service.Consent.Prompt = _ => { asked++; return Task.FromResult(new ConsentAnswer(answer)); };
        Capture(rig);
        Capture(rig);
        Pump(() => rig.Service.PromptTask.IsCompleted, "the pop-up");
        Check.Equal(1, asked);
        Check.True(rig.Settings.CopiumConsentDontAsk);
        Check.True(Settings.FromJson(rig.Settings.ToJson()).CopiumConsentDontAsk, "saved");
        Check.False(rig.Settings.CopiumConsent);
        rig.Now = rig.Now.AddDays(5);
        Capture(rig);
        Capture(rig);
        Check.Equal(1, asked, "no more unasked pop-ups");
        Check.Equal(0, rig.Calls.Count);
        // an explicit upload (the toast's button) still asks
        answer = ConsentChoice.NotNow;
        var t = rig.Service.UploadAsync(rig.Toasts.LastEntry!, manual: true);
        Pump(() => t.IsCompleted, "the manual upload");
        Check.False(t.Result);
        Check.Equal(2, asked, "an explicit try shows the pop-up");
        Check.Equal(0, rig.Calls.Count, "Not now: nothing uploaded");
    }
    // ------------------------------------------------------------------ settings

    [UnitTest]
    public static void The_upload_settings_default_to_off_and_the_hidden_ones_to_nothing()
    {
        var s = Settings.FromJson("{}");
        Check.False(s.ScreenshotUpload, "sharing is opt-in: nothing uploads on a fresh install");
        Check.True(s.ScreenshotCopyLink);
        Check.Null(s.CopiumImageUploadUrl);
        Check.False(s.CopiumConsent);
        Check.False(s.CopiumConsentDontAsk);
        Check.Null(s.CopiumConsentPromptedUtc);
        Check.Equal(0, s.CopiumConsentPromptCount);
        Check.Equal(0, s.CopiumConsentScreenshots);
        Check.True(s.CloudSharingEnabled);
        Check.True(s.ClipsEnabled);
        Check.True(s.ToastScreenshot.Enabled);
        Check.Equal(6, s.ToastScreenshot.Seconds);

        var bad = Settings.FromJson("""{ "CopiumImageUploadUrl": "   ", "CopiumConsentPromptCount": -4, "CopiumConsentScreenshots": -1, "ToastScreenshot": null }""");
        Check.Null(bad.CopiumImageUploadUrl, "blank is none");
        Check.Equal(0, bad.CopiumConsentPromptCount);
        Check.Equal(0, bad.CopiumConsentScreenshots);
        Check.Equal(6, bad.ToastScreenshot.Seconds, "a null toast setting gets its default");
        // a value that is not a valid URL is kept as typed (it is judged, and ignored with a warning, when it is used)
        Check.Equal("http://x", Settings.FromJson("""{ "CopiumImageUploadUrl": "http://x" }""").CopiumImageUploadUrl);
    }

    [UnitTest]
    public static void The_screenshot_toast_switch_and_the_upload_options_survive_a_round_trip()
    {
        var s = Settings.FromJson("{}");
        s.ToastScreenshot.Enabled = false;
        s.ToastScreenshot.Seconds = 12;
        s.ScreenshotUpload = true;
        s.ScreenshotCopyLink = false;
        s.CopiumImageUploadUrl = "https://img.example.com/up";
        s.CopiumConsent = true;
        s.CopiumConsentDontAsk = true;
        s.CopiumConsentPromptCount = 2;
        s.CopiumConsentScreenshots = 2;
        s.CloudSharingEnabled = false;
        s.CopiumConsentPromptedUtc = new DateTime(2026, 10, 24, 8, 30, 0, DateTimeKind.Utc);
        var back = Settings.FromJson(s.ToJson());
        Check.False(back.ToastScreenshot.Enabled);
        Check.Equal(12, back.ToastScreenshot.Seconds);
        Check.True(back.ScreenshotUpload);
        Check.False(back.ScreenshotCopyLink);
        Check.Equal("https://img.example.com/up", back.CopiumImageUploadUrl);
        Check.True(back.CopiumConsent);
        Check.True(back.CopiumConsentDontAsk);
        Check.Equal(2, back.CopiumConsentPromptCount);
        Check.Equal(2, back.CopiumConsentScreenshots);
        Check.False(back.CloudSharingEnabled);
        Check.Equal(new DateTime(2026, 10, 24, 8, 30, 0, DateTimeKind.Utc), back.CopiumConsentPromptedUtc!.Value.ToUniversalTime());
    }

    [UnitTest]
    public static void The_import_names_the_new_settings_and_ignores_the_consent_state()
    {
        foreach (var key in new[] { nameof(Settings.ScreenshotUpload), nameof(Settings.ScreenshotCopyLink), nameof(Settings.CopiumImageUploadUrl), nameof(Settings.ToastScreenshot),
                                    nameof(Settings.CopiumConsent), nameof(Settings.CopiumConsentDontAsk), nameof(Settings.CopiumConsentPromptedUtc), nameof(Settings.CopiumConsentPromptCount),
                                    nameof(Settings.CopiumConsentScreenshots), nameof(Settings.CloudSharingEnabled), nameof(Settings.ClipsEnabled) })
            Check.True(SettingsImport.HasLabelOrIsVolatile(key), key);
        var ours = Settings.FromJson("{}");
        var theirs = Settings.FromJson("{}");
        theirs.CopiumConsent = true;
        theirs.CopiumConsentDontAsk = true;
        Check.Equal(0, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).Count, "another copy's consent says nothing about this installation");
        theirs.CloudSharingEnabled = false;
        Check.Equal(new[] { "cloud sharing" }, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).ToArray(), "but an incoming kill switch OFF is applied");
        theirs.CloudSharingEnabled = true;
        theirs.ScreenshotUpload = true;
        Check.Equal(0, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).Count, "no consent here: their automatic upload is no difference (it would not apply)");
        ours.CopiumConsent = true;
        Check.Equal(new[] { "screenshots" }, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).ToArray(), "with our yes it is");
        var alerts = Settings.FromJson("{}");
        alerts.ToastScreenshot.Seconds = 15;
        Check.Equal(new[] { "alerts" }, SettingsImport.Differences(ours.ToJson(), alerts.ToJson()).ToArray());
    }

    [UnitTest]
    public static void An_import_does_not_switch_the_automatic_upload_on_without_our_consent()
    {
        var ours = Settings.FromJson("{}");   // no consent here
        var theirs = Settings.FromJson("{}");
        theirs.CopiumConsent = true;
        theirs.ScreenshotUpload = true;
        string kept = SettingsImport.KeepOurOwn(theirs.ToJson(), ours.ToJson(), keepOurToken: false);
        var after = Settings.FromJson(kept);
        Check.False(after.CopiumConsent, "the consent stays ours");
        Check.False(after.ScreenshotUpload, "so their automatic upload is not applied");
        Check.False(kept.Contains("\"ScreenshotUpload\": true", StringComparison.OrdinalIgnoreCase), "not even in the text");
    }
    // ------------------------------------------------------------------ S6 review fixes: endpoint, consent, answer limits

    private static string NewPackageCopy(TempDir root, string settingsJson)
    {
        string folder = Path.Combine(root.Path, "Packages", "Foo_abc", "LocalCache", "Roaming", "Clink");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), settingsJson);
        return folder;
    }

    [UnitTest]
    public static void A_foreign_settings_file_cannot_set_the_image_upload_address()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        Settings.DirOverride = home.Path;
        var ours = Settings.FromJson("{}");
        File.WriteAllText(home.File("settings.json"), ours.ToJson());
        var theirs = Settings.FromJson("{}");
        theirs.CopiumImageUploadUrl = "https://evil.example/up";
        theirs.CopiumTokenProtected = Convert.ToBase64String(new byte[] { 1, 2, 3 });   // protected for somebody else: cannot be decrypted here
        Check.True(SettingsImport.IsTokenUnusable(theirs));
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.CopiumImageUploadUrl)), "this machine's own key");
        Check.Equal(0, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).Count, "no 'screenshots' difference, so nothing is offered for it");

        NewPackageCopy(root, theirs.ToJson());
        SettingsImport.Import(SettingsImport.FindForeignCopies(root.Path).Single());
        Check.Null(Settings.Load().CopiumImageUploadUrl, "no override after the import");

        // ours is kept when we have one
        ours.CopiumImageUploadUrl = "https://img.copium.pics/up";
        File.WriteAllText(home.File("settings.json"), ours.ToJson());
        SettingsImport.Import(SettingsImport.FindForeignCopies(root.Path).Single());
        Check.Equal("https://img.copium.pics/up", Settings.Load().CopiumImageUploadUrl);
    }

    [UnitTest]
    public static void An_automatic_upload_needs_the_setting_the_users_yes_cloud_sharing_on_and_a_token()
    {
        // all four: it goes
        using var dir = new TempDir();
        var rig = Make(dir);
        Capture(rig);
        Check.Equal(1, rig.Calls.Count);
        Check.Equal(0, rig.Toasts.Shown.Count(s => s.StartsWith("notice")), "no announcement toast any more: the pop-up was the announcement");
        // each one missing: it does not (the file, the clipboard and the toast are done all the same)
        var cases = new (string Why, Func<TempDir, Rig> Make)[]
        {
            ("no consent", d => Make(d, consent: false)),
            ("cloud sharing off", d => Make(d, cloud: false)),
            ("setting off", d => Make(d, upload: false)),
            ("no token", d => Make(d, token: false)),
        };
        foreach (var (why, make) in cases)
        {
            using var d2 = new TempDir();
            var r = make(d2);
            Capture(r);
            Check.Equal(0, r.Calls.Count, why + ": nothing uploaded");
            Check.Equal(1, r.Pngs.Length, why + ": the screenshot is saved");
            Check.True(r.Toasts.Shown.Any(s => s.StartsWith("screenshot: ")), why + ": and announced");
        }
        // a manual upload without the user's yes asks first; cloud sharing off refuses without asking
        using var d3 = new TempDir();
        var asked = Make(d3, consent: false, upload: false);
        int popUps = 0;
        asked.Service.Consent.Prompt = _ => { popUps++; return Task.FromResult(new ConsentAnswer(ConsentChoice.Enable)); };
        Capture(asked);
        var t = asked.Service.UploadAsync(asked.Toasts.LastEntry!, manual: true);
        Pump(() => t.IsCompleted, "the manual upload");
        Check.True(t.Result, "Enable: the upload goes on");
        Check.Equal(1, popUps);
        Check.True(asked.Settings.CopiumConsent);
        Check.False(asked.Settings.ScreenshotUpload, "a manual upload does not turn the automatic one on by itself");
        using var d4 = new TempDir();
        var off = Make(d4, cloud: false, upload: false);
        off.Service.Consent.Prompt = _ => { popUps++; return Task.FromResult(new ConsentAnswer(ConsentChoice.Enable)); };
        Capture(off);
        var t2 = off.Service.UploadAsync(off.Toasts.LastEntry!, manual: true);
        Pump(() => t2.IsCompleted, "the refusal");
        Check.False(t2.Result);
        Check.Equal(1, popUps, "cloud sharing off: the pop-up never shows");
        Check.Equal(0, off.Calls.Count);
        Check.False(off.Toasts.LastState.CanUpload, "and the toast has no Upload button");
        Check.True(off.Toasts.Shown.Contains("notice: " + ConsentGate.CloudOffTitle), "the notice says why");
    }

    [UnitTest]
    public static void UploadCoreAsync_refuses_by_itself_when_the_kill_switch_is_off_or_there_is_no_consent()
    {
        // Both flags are flipped while a second upload sits in the queue behind a first one: it passed the entry checks of UploadAsync
        // (agreed, cloud on), so only UploadCoreAsync's own defence can stop it when its turn comes.
        foreach (var (why, flip) in new (string, Action<Settings>)[] { ("kill switch off", s => s.CloudSharingEnabled = false), ("consent withdrawn", s => s.CopiumConsent = false) })
        {
            using var dir = new TempDir();
            var gate = new TaskCompletionSource();
            int started = 0;
            var rig = Make(dir, upload: false, uploader: async (t, p, title, progress, ct, url) =>
            {
                Interlocked.Increment(ref started);
                await gate.Task;
                return Done(new FileInfo(p).Length);
            });
            Capture(rig);
            var first = rig.Toasts.LastEntry!;
            Capture(rig);
            var second = rig.Toasts.LastEntry!;
            Check.False(ReferenceEquals(first, second));
            var t1 = rig.Service.UploadAsync(first, manual: true);
            Pump(() => Volatile.Read(ref started) == 1, why + ": the first upload to be under way");
            var t2 = rig.Service.UploadAsync(second, manual: true);   // agreed and cloud on: accepted into the queue
            Check.False(t2.IsCompleted, why + ": it waits behind the first");
            flip(rig.Settings);
            gate.SetResult();
            Pump(() => t1.IsCompleted && t2.IsCompleted, why + ": both to end");
            Check.False(t2.Result, why + ": the queued upload refused by itself");
            Check.Equal(1, Volatile.Read(ref started), why + ": the uploader was called for the first one only");
        }
    }

    [UnitTest]
    public static void CancelRunningUpload_stops_the_upload_that_is_going_up_when_cloud_sharing_is_turned_off()
    {
        using var dir = new TempDir();
        int started = 0;
        var rig = Make(dir, upload: false, uploader: async (t, p, title, progress, ct, url) =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(Timeout.Infinite, ct);
            return Done();
        });
        Capture(rig);
        var t = rig.Service.UploadAsync(rig.Toasts.LastEntry!, manual: true);
        Pump(() => Volatile.Read(ref started) == 1, "the upload to be under way");
        rig.Service.CancelRunningUpload();
        Pump(() => t.IsCompleted, "the upload to end");
        Check.False(t.Result);
        Check.True(string.IsNullOrEmpty(rig.Toasts.LastEntry!.CopiumLink), "nothing linked");
        rig.Service.CancelRunningUpload();   // nothing running: nothing happens
    }

    [UnitTest]
    public static void A_kill_switch_cancel_is_neutral_not_a_failure_and_the_cloud_off_notice_shows_once_per_flip()
    {
        using var dir = new TempDir();
        int started = 0;
        var rig = Make(dir, upload: false, uploader: async (t, p, title, progress, ct, url) =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(Timeout.Infinite, ct);
            return Done();
        });
        Capture(rig);
        var first = rig.Toasts.LastEntry!;
        Capture(rig);
        var second = rig.Toasts.LastEntry!;
        Capture(rig);
        var third = rig.Toasts.LastEntry!;
        var t1 = rig.Service.UploadAsync(first, manual: true);
        Pump(() => Volatile.Read(ref started) == 1, "the first upload to be under way");
        var t2 = rig.Service.UploadAsync(second, manual: true);   // queued behind it
        var t3 = rig.Service.UploadAsync(third, manual: true);
        rig.Settings.CloudSharingEnabled = false;                 // what SetCloudSharing(false) does, then:
        rig.Service.CancelRunningUpload();
        Pump(() => t1.IsCompleted && t2.IsCompleted && t3.IsCompleted, "all three to end");
        Check.False(t1.Result);
        Check.True(rig.Toasts.Shown.Contains("upload declined"), "the cancelled one is neutral");
        Check.False(rig.Toasts.Shown.Contains("upload failed"), "and not a failure");
        Check.Equal(1, rig.Toasts.Shown.Count(s => s == "notice: " + ConsentGate.CloudOffTitle), "the notice once for the flip, not once per queued screenshot");
        Check.Equal(0, rig.Toasts.Shown.Count(s => s.StartsWith("problem")), "no problem toast");
        Check.Equal(1, Volatile.Read(ref started), "the queued ones never started");
    }

    [UnitTest]
    public static void With_image_copy_off_the_automatic_link_copy_uses_the_same_clipboard_gate()
    {
        using var dir = new TempDir();
        var rig = Make(dir, copyImage: false);
        Capture(rig);
        Check.Equal(new[] { "https://v.copium.pics/id1.png" }, rig.Copied.ToArray(), "the clipboard did not change since the save: the link is copied");

        using var dir2 = new TempDir();
        var changed = Make(dir2, copyImage: false, uploader: null);
        int calls = 0;
        changed.Service.Uploader = (t, p, title, progress, ct, url) =>
        {
            Interlocked.Increment(ref calls);
            changed.SomeoneCopies();   // the user copies something while the upload runs
            return Task.FromResult(Done(new FileInfo(p).Length));
        };
        Capture(changed);
        Check.Equal(1, calls);
        Check.Equal(0, changed.Copied.Count, "the clipboard changed since the save: the link does not replace what was copied");
    }
    [UnitTest]
    public static void Cancel_on_the_uploading_toast_stops_that_upload_and_the_next_one_goes_on()
    {
        using var dir = new TempDir();
        bool started = false;
        int calls = 0;
        var rig = Make(dir, uploader: async (_, _, _, _, ct, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started = true;
                await Task.Delay(Timeout.Infinite, ct);
            }
            return Done();
        });
        var a = EntryFor(dir, "a.png");
        var b = EntryFor(dir, "b.png");
        var ta = rig.Service.UploadAsync(a);
        var tb = rig.Service.UploadAsync(b);
        Pump(() => started, "the first upload to start");
        Check.False(rig.Service.UploadQueue.IsCompleted, "the queue is busy while an upload runs");
        Check.NotNull(rig.Toasts.Handles[0].OnCancel);
        rig.Toasts.Handles[0].OnCancel!();   // the Cancel button
        Pump(() => ta.IsCompleted && tb.IsCompleted, "both uploads to end");
        Check.False(ta.Result);
        Check.True(tb.Result, "the next one went on");
        Check.Null(a.CopiumLink, "nothing linked");
        Check.NotNull(b.CopiumLink);
        Check.False(rig.Toasts.Shown.Any(s => s.StartsWith("problem")), "a cancel is not a failure to report: the toast shows Retry");
        Check.True(rig.Toasts.Shown.Contains("upload failed"), "the toast went back to its saved state");
        Check.False(rig.Service.CopiumImagesRefused);
        Check.True(rig.Service.UploadQueue.IsCompleted);
        // and Retry (a manual upload) works afterwards
        var again = rig.Service.UploadAsync(a, manual: true);
        Pump(() => again.IsCompleted, "the retry");
        Check.True(again.Result);
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => new TaskCompletionSource().Task;
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());

        private sealed class StalledStream : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(new TaskCompletionSource<int>().Task);   // never ends, and ignores the token
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [UnitTest]
    public static void An_answer_whose_body_never_ends_fails_in_time_and_the_next_upload_still_runs()
    {
        var before = CopiumClient.BodyTimeout;
        CopiumClient.BodyTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var dir = new TempDir();
            int n = 0;
            var handler = new MockHandler { Respond = _ => Interlocked.Increment(ref n) == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() } : Json(HttpStatusCode.OK, Ok) };
            CopiumClient.HttpOverride = new HttpClient(handler);
            var rig = Make(dir, uploader: CopiumClient.UploadAsync);
            var a = EntryFor(dir, "a.png");
            var b = EntryFor(dir, "b.png");
            var sw = Stopwatch.StartNew();
            var ta = rig.Service.UploadAsync(a);
            var tb = rig.Service.UploadAsync(b);
            Pump(() => ta.IsCompleted && tb.IsCompleted, "both uploads to end", 20_000);
            Check.True(sw.ElapsedMilliseconds < 8000, $"the stalled answer was given up on ({sw.ElapsedMilliseconds} ms)");
            Check.False(ta.Result);
            Check.True(tb.Result, "the next queued upload ran");
            Check.Null(a.CopiumLink);
            Check.True(rig.Toasts.Shown.Contains("problem: Screenshot not uploaded"));
            Check.Contains(rig.Toasts.LastProblemText, "Saved locally.");
            Check.Contains(rig.Toasts.LastProblemText, "did not finish its answer");
        }
        finally
        {
            CopiumClient.HttpOverride = null;
            CopiumClient.BodyTimeout = before;
        }
    }

    [UnitTest]
    public static void An_answer_is_read_up_to_64_KB_only_and_a_link_must_be_an_absolute_https_address()
    {
        string padding = new('x', 200_000);
        var (up, error, _) = Upload("a.png", _ => Json(HttpStatusCode.OK, "{\"link\":\"https://v.copium.pics/a.png\",\"pad\":\"" + padding + "\"}"));
        Check.Null(up);
        Check.Contains(error!.Message, "unexpected answer", "cut at the cap, so it is no JSON");

        foreach (var bad in new[] { "file:///C:/x", "http://v.copium.pics/a.png", "javascript:alert(1)", "/a.png", "https://copium.pics@evil.example/a" })
        {
            var r = Upload("a.png", _ => Json(HttpStatusCode.OK, "{\"id\":\"1\",\"link\":\"" + bad + "\"}"));
            Check.Null(r.Up, bad);
            Check.Contains(r.Error!.Message, "unexpected answer", bad);
        }
        // the links are only copied and opened: any absolute https address is fine (a custom embed or CDN host); the token goes only to the upload endpoint
        var elsewhere = Upload("a.png", _ => Json(HttpStatusCode.OK, """{"id":"1","link":"https://cdn.example.com/a.png"}"""));
        Check.Null(elsewhere.Error);
        Check.Equal("https://cdn.example.com/a.png", elsewhere.Up!.Link);
        var good = Upload("a.png", _ => Json(HttpStatusCode.OK, """{"id":"1","link":"https://copium.pics/a.png","url":"file:///C:/x","pageUrl":"javascript:1","thumbUrl":"https://img.example.net/t"}"""));
        Check.Null(good.Error);
        Check.Equal("https://copium.pics/a.png", good.Up!.Link);
        Check.Equal("", good.Up.Url, "an address that is not https is dropped");
        Check.Equal("", good.Up.PageUrl);
        Check.Equal("https://img.example.net/t", good.Up.ThumbUrl);
        var fallback = Upload("a.png", _ => Json(HttpStatusCode.OK, """{"id":"1","url":"https://v.copium.pics/u.png"}"""));
        Check.Equal("https://v.copium.pics/u.png", fallback.Up!.Link, "url stands in for a missing link");
    }

    [UnitTest]
    public static void Video_extensions_have_video_content_types()
    {
        Check.Equal("video/quicktime", CopiumClient.ContentTypeFor("a.MOV"));
        Check.Equal("video/x-matroska", CopiumClient.ContentTypeFor("a.mkv"));
        Check.Equal("video/webm", CopiumClient.ContentTypeFor("a.webm"));
        foreach (var f in new[] { "a.mov", "a.mkv", "a.webm", "a.mp4" }) Check.Equal(MediaKind.Video, CopiumClient.KindOf(f), f);
        Check.Equal("application/octet-stream", CopiumClient.ContentTypeFor("a.avi"));
    }

    // ------------------------------------------------------------------ the clipboard and the link

    [UnitTest]
    public static void The_link_replaces_the_clipboard_only_if_nothing_was_copied_since_or_the_upload_was_manual()
    {
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => logged.Enqueue(line);
        Log.Line += Collect;
        try
        {
            // something else was copied while the picture uploaded
            using var dir = new TempDir();
            Rig? r = null;
            r = Make(dir, copyImage: true, uploader: (_, _, _, _, _, _) => { r!.SomeoneCopies(); return Task.FromResult(Done()); });
            Capture(r);
            Check.Equal(1, r.ImageCopies);
            Check.Equal(0, r.Copied.Count, "the user's newer copy is left alone");
            Check.Equal("shared: https://v.copium.pics/id1.png", r.Toasts.Shown.Last(), "the toast says uploaded (its Copy link button is there)");
            Check.True(logged.Any(l => l.Contains("link not copied (clipboard changed)")), "said in the log");

            // a manual upload (Upload / Retry on the toast) copies the link whatever the clipboard holds
            var last = r.Toasts.LastEntry!;
            var manual = r.Service.UploadAsync(new ScreenshotEntry { Path = last.Path, Size = last.Size, WrittenUtc = last.WrittenUtc }, manual: true);
            Pump(() => manual.IsCompleted, "the manual upload");
            Check.Equal(1, r.Copied.Count);

            // with the image copy off the clipboard is gated by the change counter recorded when the screenshot was announced
            using var dir2 = new TempDir();
            var noImage = Make(dir2, copyImage: false);
            Capture(noImage);
            Check.Equal(0, noImage.ImageCopies);
            Check.Equal(1, noImage.Copied.Count, "nothing was copied since: the link is copied");
            Check.Equal("shared: https://v.copium.pics/id1.png (copied)", noImage.Toasts.Shown.Last());
        }
        finally { Log.Line -= Collect; }
    }

    // ------------------------------------------------------------------ what pauses the automatic uploads

    [UnitTest]
    public static void Only_a_refusal_of_the_picture_type_pauses_automatic_uploads()
    {
        // through the real client: the error text decides a 400
        var other = (CopiumHttpException)Upload("a.png", _ => Json((HttpStatusCode)400, """{"error":"Title is too long"}""")).Error!;
        Check.False(CopiumClient.IsTypeRefusal(other.StatusCode, other.ErrorText));
        Check.Equal("Copium refused the upload (Title is too long)", other.Message);
        var type = (CopiumHttpException)Upload("a.png", _ => Json((HttpStatusCode)400, """{"error":"Unsupported file FORMAT"}""")).Error!;
        Check.True(CopiumClient.IsTypeRefusal(type.StatusCode, type.ErrorText));
        Check.Equal("Copium did not accept the image (Unsupported file FORMAT)", type.Message);
        Check.False(CopiumClient.IsTypeRefusal(400, null), "a 400 without a text is not a type refusal");
        Check.True(CopiumClient.IsTypeRefusal(415, null));
        Check.False(CopiumClient.IsTypeRefusal(413, "type"));

        // in the service
        using var dir = new TempDir();
        int attempts = 0;
        Exception? failure = new CopiumHttpException(400, "Copium refused the upload (Title is too long)", "Title is too long");
        var rig = Make(dir, uploader: (_, _, _, _, _, _) => { Interlocked.Increment(ref attempts); return Task.FromException<CopiumUpload>(failure!); });
        Capture(rig);
        Check.False(rig.Service.CopiumImagesRefused, "an unrelated 400 does not pause");
        Check.Equal("problem: Screenshot not uploaded", rig.Toasts.Shown.Last());
        Capture(rig);
        Check.Equal(2, attempts, "the next screenshot is uploaded again");

        failure = new CopiumHttpException(400, "Copium did not accept the image (Unsupported media)", "Unsupported media");
        Capture(rig);
        Check.True(rig.Service.CopiumImagesRefused, "a 400 about the type pauses");
        Check.Equal(3, attempts);
        Capture(rig);
        Check.Equal(3, attempts, "paused");
    }

    [UnitTest]
    public static void A_rejected_token_pauses_the_automatic_uploads_with_one_toast_until_it_changes()
    {
        using var dir = new TempDir();
        int attempts = 0;
        var rig = Make(dir, uploader: (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<CopiumUpload>(new CopiumHttpException(401, "Copium rejected the token (HTTP 401)"));
        });
        Capture(rig);
        Check.Equal(1, attempts);
        Check.True(rig.Service.CopiumTokenRejected);
        Check.False(rig.Service.CopiumImagesRefused, "the pictures were not the problem");
        Check.Equal(1, rig.Toasts.Shown.Count(s => s.StartsWith("problem")));
        Check.Equal("problem: Copium token rejected", rig.Toasts.Shown.Last(s => s.StartsWith("problem")));
        Check.Contains(rig.Toasts.LastProblemText, "Settings");

        rig.Toasts.Shown.Clear();
        Capture(rig);
        Check.Equal(1, attempts, "no more tries with a token that is refused");
        Check.Equal(1, rig.Toasts.Shown.Count, "just the screenshot toast, no second error");
        Check.True(rig.Toasts.LastState.CanUpload, "the button is there for a manual try");

        rig.Settings.CopiumToken = "eup_a_new_token";
        Check.False(rig.Service.CopiumTokenRejected, "a new token lifts the pause");
        Capture(rig);
        Check.Equal(2, attempts);

        // a 403 pauses too
        using var dir2 = new TempDir();
        var r403 = Make(dir2, uploader: (_, _, _, _, _, _) => Task.FromException<CopiumUpload>(new CopiumHttpException(403, "Copium rejected the token (HTTP 403)", "Invalid API token")));
        Capture(r403);
        Check.True(r403.Service.CopiumTokenRejected);

        // a 403 that says nothing about the token (a blocked file, a quota) is one refused upload, not a paused account
        using var dir3 = new TempDir();
        int other = 0;
        var r403b = Make(dir3, uploader: (_, _, _, _, _, _) => { other++; return Task.FromException<CopiumUpload>(new CopiumHttpException(403, "Copium refused the upload (File blocked)", "File blocked")); });
        Capture(r403b);
        Capture(r403b);
        Check.False(r403b.Service.CopiumTokenRejected);
        Check.Equal(2, other, "the next screenshot is tried again");
        Check.True(CopiumClient.IsTokenRejection(401, null));
        Check.True(CopiumClient.IsTokenRejection(403, "Forbidden: bad API token"));
        Check.False(CopiumClient.IsTokenRejection(403, "Invalid storage key"), "a bare 'key' is no token");
        Check.True(CopiumClient.IsTokenRejection(403, "Not authorized"));
        Check.False(CopiumClient.IsTokenRejection(403, "File blocked"));
        Check.True(CopiumClient.IsTokenRejection(403, null), "a bare 403 is a token rejection");
        Check.True(CopiumClient.IsTokenRejection(403, "  "));
        Check.False(CopiumClient.IsTokenRejection(500, null));
        Check.Equal("Copium refused the upload (File blocked)", CopiumClient.ErrorMessage(403, """{"error":"File blocked"}"""));
        Check.Equal("Copium rejected the token (Invalid token)", CopiumClient.ErrorMessage(403, """{"error":"Invalid token"}"""));
    }

    // ------------------------------------------------------------------ the file, the history, the exit

    [UnitTest]
    public static void A_file_that_changed_after_it_was_saved_is_not_uploaded()
    {
        using var dir = new TempDir();
        var rig = Make(dir, upload: false);
        Capture(rig);
        var entry = rig.Toasts.LastEntry!;
        Check.NotNull(entry.WrittenUtc);
        File.AppendAllText(entry.Path, "x");   // somebody wrote to it: the size differs
        var t = rig.Service.UploadAsync(entry, manual: true);
        Pump(() => t.IsCompleted, "the check");
        Check.False(t.Result);
        Check.Equal(0, rig.Calls.Count, "nothing was sent");
        Check.Equal("problem: Screenshot not uploaded", rig.Toasts.Shown.Last());
        Check.Contains(rig.Toasts.LastProblemText, "changed after it was saved");

        // same size, other write time
        using var dir2 = new TempDir();
        var rig2 = Make(dir2, upload: false);
        Capture(rig2);
        var e2 = rig2.Toasts.LastEntry!;
        File.SetLastWriteTimeUtc(e2.Path, e2.WrittenUtc!.Value.AddHours(3));
        var t2 = rig2.Service.UploadAsync(e2, manual: true);
        Pump(() => t2.IsCompleted, "the check");
        Check.False(t2.Result);
        Check.Equal(0, rig2.Calls.Count);

        // untouched: uploaded
        using var dir3 = new TempDir();
        var rig3 = Make(dir3, upload: false);
        Capture(rig3);
        var t3 = rig3.Service.UploadAsync(rig3.Toasts.LastEntry!, manual: true);
        Pump(() => t3.IsCompleted, "the upload");
        Check.True(t3.Result);
        Check.Equal(1, rig3.Calls.Count);
    }

    [UnitTest]
    public static void The_history_records_the_link_on_the_listed_entry_by_path()
    {
        using var dir = new TempDir();
        string file = dir.File("screenshots.json");
        string png = dir.File("a.png");
        File.WriteAllBytes(png, new byte[] { 1 });
        var h = new ScreenshotHistory(file, null);
        h.Add(new ScreenshotEntry { Path = png, Created = DateTime.Now });
        var other = new ScreenshotEntry { Path = png.ToUpperInvariant(), CopiumLink = "https://v.copium.pics/x", CopiumPageUrl = "https://copium.pics/p/x", CopiumId = "x" };
        Check.True(h.Update(other), "found by path, any case");
        Check.Equal("https://v.copium.pics/x", h.Find(png)!.CopiumLink, "copied onto the listed instance");
        var reread = new ScreenshotHistory(file, null).Entries.Single();
        Check.Equal("https://v.copium.pics/x", reread.CopiumLink);
        Check.Equal("x", reread.CopiumId);
        Check.False(h.Update(new ScreenshotEntry { Path = dir.File("unlisted.png"), CopiumLink = "https://v.copium.pics/y" }), "not listed: nothing recorded");
        Check.Equal(1, new ScreenshotHistory(file, null).Entries.Count);
    }

    [UnitTest]
    public static void A_link_is_kept_when_the_screenshots_folder_changes_during_the_upload()
    {
        using var dir = new TempDir();
        Rig? r = null;
        r = Make(dir, uploader: (_, _, _, _, _, _) =>
        {
            r!.Settings.ScreenshotsFolder = dir.File("elsewhere");   // the folder setting changed while it was uploading
            return Task.FromResult(Done());
        });
        Capture(r);
        var saved = new ScreenshotHistory(ScreenshotHistory.DefaultFile, null).Entries;
        Check.Equal(1, saved.Count);
        Check.Equal("https://v.copium.pics/id1.png", saved[0].CopiumLink, "the link is in the history file");
    }

    [UnitTest]
    public static void Any_failure_while_closing_is_no_problem_toast()
    {
        using var dir = new TempDir();
        bool started = false;
        var rig = Make(dir, uploader: async (_, _, _, _, ct, _) =>
        {
            started = true;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new IOException("closed under the upload"); }   // not a cancellation, but caused by one
            return Done();
        });
        var up = rig.Service.UploadAsync(EntryFor(dir, "a.png"));
        Pump(() => started, "the upload to start");
        rig.Service.CancelUploads(TimeSpan.FromSeconds(4));
        Pump(() => up.IsCompleted, "the end of the upload");
        Check.False(up.Result);
        Check.Equal(new[] { "uploading: a.png", "upload failed" }, rig.Toasts.Shown.ToArray(), "no error toast on the way out");
    }

    [UnitTest]
    public static void The_hint_clock_going_back_restarts_the_gap_from_now()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var s = Settings.FromJson("{}");
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        s.CopiumConsentScreenshots = 2;
        s.CopiumConsentPromptCount = 1;
        s.CopiumConsentPromptedUtc = now.AddDays(40);   // the clock was set back
        Check.False(CopiumNudge.IsDue(s, now), "not shown");
        Check.Equal(now, s.CopiumConsentPromptedUtc!.Value.ToUniversalTime(), "the last time is now");
        Check.Equal(now, Settings.Load().CopiumConsentPromptedUtc!.Value.ToUniversalTime(), "saved");
        Check.False(CopiumNudge.IsDue(s, now.AddHours(23)));
        Check.True(CopiumNudge.IsDue(s, now.AddHours(25)));
    }

    // ------------------------------------------------------------------ paths and devices

    [UnitTest]
    public static void Device_and_network_paths_are_refused_for_opening_and_dropped_from_the_known_folders()
    {
        foreach (var p in new[] { @"\??\UNC\h\s\a.png", @"\\?\UNC\h\s\a.png", @"\\.\C:\a.png", @"//?/C:/a.png", @"\??\C:\a.png" })
        {
            Check.True(ScreenshotService.IsUnc(p), p + " is a network / device path");
            Check.True(ScreenshotService.IsDevicePath(p), p);
            Check.False(ScreenshotService.IsOpenable(p, (IEnumerable<string>?)null), p);
            Check.False(ScreenshotService.IsOpenable(p, new[] { Path.GetDirectoryName(p)!, @"\\h\s" }), p + " even inside a folder");
            Check.False(ScreenshotService.IsExplicitEditable(p), p);
        }
        Check.False(ScreenshotService.IsDevicePath(@"\\h\s\a.png"));
        Check.True(ScreenshotService.IsUnc(@"\\h\s\a.png"));
        Check.False(ScreenshotService.IsUnc(@"C:\a.png"));
        Check.False(ScreenshotService.IsExplicitEditable(@"\\h\s\a.png"), "a network path is not an explicit edit");
        Check.True(ScreenshotService.IsOpenable(@"\\h\s\shots\a.png", new[] { @"\\h\s\shots" }), "inside a network folder the user chose");
        Check.False(ScreenshotService.IsOpenable(@"\\h\s\shots\a.png", new[] { @"D:\shots" }));

        using var dir = new TempDir();
        var rig = Make(dir);
        var history = new ScreenshotHistory(ScreenshotHistory.DefaultFile, rig.Folder);
        history.NoteRoot(@"\??\UNC\h\s");
        history.NoteRoot(@"\\?\UNC\h\s");
        history.NoteRoot(dir.File("old"));
        var known = rig.Service.KnownFolders();
        Check.False(known.Any(f => f.StartsWith(@"\??\") || f.StartsWith(@"\\?\")), "the device roots are dropped");
        Check.True(known.Contains(dir.File("old"), StringComparer.OrdinalIgnoreCase), "a local root stays");
    }

    [UnitTest]
    public static void The_control_pipe_name_carries_the_session_id()
    {
        Check.Equal(AppController.PipeName + ".s" + Process.GetCurrentProcess().SessionId, AppController.SessionPipeName);
        Check.True(AppController.SessionPipeName.StartsWith("Clink.Control"));
        Check.False(AppController.SessionPipeName.Contains('\\'));
    }

    // ------------------------------------------------------------------ links and folders from an import

    [UnitTest]
    public static void Only_an_https_address_is_opened_by_the_shell()
    {
        foreach (var bad in new[] { "file:///C:/Windows/System32/calc.exe", @"C:\Windows\System32\calc.exe", "javascript:alert(1)", "http://copium.pics/p/1", "ftp://x/y", "calc.exe", "", "   ", "mailto:a@b.c" })
            Check.False(CopiumClient.IsHttpsUrl(bad, out _), $"'{bad}'");
        Check.False(CopiumClient.IsHttpsUrl(null, out _));
        Check.True(CopiumClient.IsHttpsUrl("https://copium.pics/p/1", out var uri));
        Check.Equal("https://copium.pics/p/1", uri.AbsoluteUri);
    }

    [UnitTest]
    public static void An_import_never_takes_a_network_folder_silently_and_says_which_one_it_kept()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        Settings.DirOverride = home.Path;
        var ours = Settings.FromJson("{}");
        ours.ScreenshotsFolder = @"D:\Shots";
        File.WriteAllText(home.File("settings.json"), ours.ToJson());
        var theirs = Settings.FromJson("{}");
        theirs.ScreenshotsFolder = @"\\nas\share\shots";
        theirs.ArchivePath = @"\\nas\share\archive";
        theirs.ClipsFolder = @"E:\TheirClips";   // a local folder is taken over as before
        theirs.Codec = "hevc";
        NewPackageCopy(root, theirs.ToJson());
        var copy = SettingsImport.FindForeignCopies(root.Path).Single();

        Check.Equal(new[] { "video quality", "clips folder" }, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).ToArray(), "the network folders are no difference to offer");
        var result = SettingsImport.Import(copy);
        var loaded = Settings.Load();
        Check.Equal(@"D:\Shots", loaded.ScreenshotsFolder, "ours stays");
        Check.Null(loaded.ArchivePath, "we had none");
        Check.Equal(@"E:\TheirClips", loaded.ClipsFolder);
        Check.Equal("hevc", loaded.Codec);
        Check.Equal(new[] { "ScreenshotsFolder", "ArchivePath" }, result.NetworkPathsKept!.OrderBy(k => k == "ScreenshotsFolder" ? 0 : 1).ToArray());
        Check.Contains(result.NetworkPathsNotice, "screenshots folder");
        Check.Contains(result.NetworkPathsNotice, "archive location");
        Check.DoesNotContain(result.NetworkPathsNotice, "clips folder");
        Check.Equal(result.NetworkPathsNotice, SettingsImport.TakeNetworkPathsNotice(), "said at the next start");
        Check.Null(SettingsImport.TakeNetworkPathsNotice(), "once");

        // the same text, no network folder: nothing to say
        theirs.ScreenshotsFolder = @"F:\Their shots";
        theirs.ArchivePath = null;
        NewPackageCopy(root, theirs.ToJson());
        Check.Null(SettingsImport.Import(SettingsImport.FindForeignCopies(root.Path).Single()).NetworkPathsNotice);
    }

    [UnitTest]
    public static void A_fresh_install_taking_over_a_copy_keeps_out_its_network_folders_too()
    {
        using var home = new TempDir();
        using var root = new TempDir();
        Settings.DirOverride = home.Path;
        var theirs = Settings.FromJson("{}");
        theirs.ClipsFolder = @"\\nas\clips";
        theirs.ScreenshotsFolder = @"\\nas\shots";
        NewPackageCopy(root, theirs.ToJson());
        SettingsImport.PackagesRoot = Path.Combine(root.Path, "Packages");
        try
        {
            Check.NotNull(SettingsImport.ImportNewest());
            var loaded = Settings.Load();
            Check.False(loaded.ClipsFolder.StartsWith(@"\\"), "the default clips folder instead: " + loaded.ClipsFolder);
            Check.Null(loaded.ScreenshotsFolder);
            Check.Contains(SettingsImport.TakeNetworkPathsNotice(), "clips folder");
        }
        finally { SettingsImport.Reset(); }
    }

    // ------------------------------------------------------------------ pictures from outside

    private static byte[] PngOf(int w, int h) => PngFile.Encode(new BgraImage(w, h).ToBitmapSource());

    private sealed class SwappingStream : Stream
    {
        private readonly byte[] _second;
        private MemoryStream _current;
        private bool _read, _swapped;
        public SwappingStream(byte[] first, byte[] second) { _current = new MemoryStream(first); _second = second; }
        private void Swap() { if (_read && !_swapped) { _swapped = true; _current = new MemoryStream(_second); } }   // the file changed after the header was read
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => _current.Length;
        public override long Position { get => _current.Position; set { Swap(); _current.Position = value; } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { _read = true; return _current.Read(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) { Swap(); return _current.Seek(offset, origin); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [UnitTest]
    public static void A_png_is_decoded_from_the_one_stream_its_header_was_read_from()
    {
        var small = PngOf(2, 2);
        var big = PngOf(40, 30);
        using (var plain = new MemoryStream(small))
        {
            var image = PngFile.Decode(plain);
            Check.Equal((2, 2), (image!.Width, image.Height));
        }
        using (var swapped = new SwappingStream(small, big))
            Check.Null(PngFile.Decode(swapped), "the picture behind the header changed: refused before any pixel is copied");
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.png"), small);
        Check.Equal(2, PngFile.Decode(dir.File("a.png"))!.Width);
        File.WriteAllBytes(dir.File("b.png"), new byte[] { 1, 2, 3 });
        Check.Null(PngFile.Decode(dir.File("b.png")), "not a PNG");
    }

    [UnitTest]
    public static void Files_are_inserted_only_as_png_jpeg_bmp_or_gif_and_the_clipboard_png_only_as_png()
    {
        Check.True(ImageInput.HasKnownMagic(new byte[] { 0x89, 0x50, 0x4E, 0x47, 13, 10 }));
        Check.True(ImageInput.HasKnownMagic(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Check.True(ImageInput.HasKnownMagic("BMxxxx"u8));
        Check.True(ImageInput.HasKnownMagic("GIF89a"u8));
        Check.False(ImageInput.HasKnownMagic(new byte[] { 0x49, 0x49, 0x2A, 0x00 }), "TIFF");
        Check.False(ImageInput.HasKnownMagic("RIFFxxxxWEBP"u8), "WebP");
        Check.False(ImageInput.HasKnownMagic(new byte[] { 0, 0, 1, 0 }), "ICO");
        Check.False(ImageInput.HasKnownMagic(Array.Empty<byte>()));

        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("ok.png"), PngOf(3, 3));
        Check.Equal(3, ImageInput.LoadFile(dir.File("ok.png"), out var err)!.Width);
        Check.Null(err);
        File.WriteAllBytes(dir.File("pic.tif"), new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0 });
        Check.Null(ImageInput.LoadFile(dir.File("pic.tif"), out err));
        Check.Equal(ImageInput.Unreadable, err);
        File.WriteAllBytes(dir.File("empty.png"), Array.Empty<byte>());
        Check.Null(ImageInput.LoadFile(dir.File("empty.png"), out err));
        Check.NotNull(err);

        var jpeg = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        jpeg.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(new BgraImage(4, 4).ToBitmapSource()));
        using var ms = new MemoryStream();
        jpeg.Save(ms);
        File.WriteAllBytes(dir.File("pic.jpg"), ms.ToArray());
        Check.NotNull(ImageInput.LoadFile(dir.File("pic.jpg"), out _), "a JPEG is read");
        ms.Position = 0;
        Check.Null(ImageInput.FromStream(ms, out err, pngOnly: true), "the PNG decoder is not used for a JPEG");
        Check.Equal(ImageInput.Unreadable, err);
        Check.NotNull(ImageInput.FromStream(new MemoryStream(PngOf(2, 2)), out _, pngOnly: true));
    }

    private static byte[] DibHeader(int size, int w, int h)
    {
        var b = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), h);
        return b;
    }

    [UnitTest]
    public static void A_clipboard_bitmap_header_that_does_not_parse_or_has_no_width_is_refused()
    {
        Check.True(ImageInput.DibVerdict(DibHeader(40, 100, 50), out var err));
        Check.Null(err);
        Check.True(ImageInput.DibVerdict(DibHeader(40, 100, -50), out _), "top-down");
        Check.True(ImageInput.DibVerdict(DibHeader(124, 10, 10), out _), "a V5 header");
        Check.False(ImageInput.DibVerdict(DibHeader(40, -5, 50), out err), "negative width");
        Check.Equal(ImageInput.Unreadable, err);
        Check.False(ImageInput.DibVerdict(DibHeader(40, 0, 50), out _), "zero width");
        Check.False(ImageInput.DibVerdict(DibHeader(40, 50, 0), out _), "zero height");
        Check.False(ImageInput.DibVerdict(DibHeader(7, 50, 50), out err), "a header size that is none");
        Check.Equal(ImageInput.Unreadable, err);
        Check.False(ImageInput.DibVerdict(new byte[] { 40, 0, 0 }, out _), "cut short");
        Check.False(ImageInput.DibVerdict(Array.Empty<byte>(), out _));
        Check.False(ImageInput.DibVerdict(DibHeader(40, 20000, 10), out err), "over the limit");
        Check.Equal(ImageInput.TooLarge, err);
    }

    // ------------------------------------------------------------------ privacy and the editor window

    [UnitTest]
    public static void The_live_edit_document_is_dropped_when_the_session_changes_or_after_fifteen_idle_minutes()
    {
        using var dir = new TempDir();
        var rig = Make(dir);
        var doc = DocumentState.Create(new BgraImage(4, 4));
        LastEditInfo Info() => new(doc, dir.File("a.png"), DateTime.UtcNow);

        rig.Service.LastEdit = Info();
        Check.NotNull(rig.Service.LastEdit);
        rig.Now = rig.Now.AddMinutes(14);
        rig.Service.ExpireLastEditIfIdle();
        Check.NotNull(rig.Service.LastEdit, "kept within 15 minutes");
        rig.Service.TouchLastEdit();   // an edit-last / editor interaction
        rig.Now = rig.Now.AddMinutes(14);
        rig.Service.ExpireLastEditIfIdle();
        Check.NotNull(rig.Service.LastEdit, "touched: another 15 minutes");
        rig.Now = rig.Now.AddMinutes(2);
        rig.Service.ExpireLastEditIfIdle();
        Check.Null(rig.Service.LastEdit, "dropped after 15 idle minutes");

        foreach (var reason in new[] { Microsoft.Win32.SessionSwitchReason.SessionLock, Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect, Microsoft.Win32.SessionSwitchReason.SessionLogoff, Microsoft.Win32.SessionSwitchReason.RemoteDisconnect })
        {
            rig.Service.LastEdit = Info();
            Check.NotNull(rig.Service.LastEdit);
            rig.Service.OnSessionSwitch(reason);
            Check.Null(rig.Service.LastEdit, "dropped on " + reason);
        }
    }

    [UnitTest]
    public static void The_editor_window_is_kept_out_of_capture_unless_the_hidden_setting_says_otherwise()
    {
        Check.True(Settings.FromJson("{}").ScreenshotEditorExcludeFromCapture, "on by default");
        Check.False(Settings.FromJson("""{ "ScreenshotEditorExcludeFromCapture": false }""").ScreenshotEditorExcludeFromCapture);
        Check.True(typeof(Clink.UI.ICaptureExcludedWindow).IsAssignableFrom(typeof(ScreenshotEditorWindow)));
        Check.True(new EditorRequest { Document = DocumentState.Create(new BgraImage(4, 4)), Title = "t", Options = new ScreenshotEditorOptions(), SaveAsync = (_, _, _) => Task.FromResult(true) }.ExcludeFromCapture);

        foreach (bool exclude in new[] { true, false })
        {
            var host = new EditorWindowHost { ShowWindows = false };
            var open = host.EditAsync(new EditorRequest
            {
                Document = DocumentState.Create(new BgraImage(8, 8)), Title = "t", IsNewCapture = true, Options = new ScreenshotEditorOptions(),
                SaveAsync = (_, _, _) => Task.FromResult(true), ExcludeFromCapture = exclude,
            });
            Check.Equal(exclude, Clink.UI.CaptureExclusion.IsExcluded(host.Window), "the window and its tooltips follow the setting");
            host.Window!.CloseApproved();
            Pump(() => open.IsCompleted, "the window to close");
        }
    }

    [UnitTest]
    public static void A_close_asked_for_during_a_save_is_carried_out_when_the_save_ends()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new EditorWindowHost { ShowWindows = false };
        var open = host.EditAsync(new EditorRequest
        {
            Document = DocumentState.Create(new BgraImage(8, 8)), Title = "t", IsNewCapture = true, Options = new ScreenshotEditorOptions(),
            SaveAsync = (_, _, _) => gate.Task,
        });
        var window = host.Window!;
        var save = window.SaveCoreAsync(finish: false);
        Check.False(save.IsCompleted);
        _ = window.RequestCloseAsync();   // Esc / the close button while saving
        var moment = Stopwatch.StartNew();
        Pump(() => moment.ElapsedMilliseconds > 100, "a moment for a wrong close");
        Check.False(open.IsCompleted, "still saving: not closed under it");
        gate.SetResult(true);
        Pump(() => save.IsCompleted, "the save");
        Pump(() => open.IsCompleted, "the close that was asked for meanwhile", 5000);
        Check.False(host.IsOpen);
    }

    // ------------------------------------------------------------------ export off the UI thread

    private static DocumentState MixedDoc()
    {
        var img = new BgraImage(160, 100);
        var rnd = new Random(7);
        for (int i = 0; i < img.Pixels.Length; i += 4)
        {
            img.Pixels[i] = (byte)rnd.Next(256); img.Pixels[i + 1] = (byte)rnd.Next(256); img.Pixels[i + 2] = (byte)rnd.Next(256); img.Pixels[i + 3] = 255;
        }
        return DocumentState.Create(img)
            .Add(new BlurRegion(0, new RectI(10, 10, 60, 40), 4, false, 8), out _)
            .Add(new RectOutline(0, new Rect(20, 20, 50, 30), Colors.Red, 3), out _)
            .Add(new TextNote(0, new Point(40, 60), "export", 20, Colors.White, false, TextBackdrop.Box, Colors.Black), out _)
            .Add(new LineShape(0, new Point(5, 90), new Point(150, 70), Colors.Lime, 4, true), out _)
            .WithCrop(new RectI(8, 6, 140, 90));
    }

    [UnitTest]
    public static void The_async_export_is_byte_identical_to_the_synchronous_one()
    {
        var doc = MixedDoc();
        var expected = Exporter.Export(doc);
        var task = Exporter.ExportAsync(doc);
        Pump(() => task.IsCompleted, "the export");
        Check.Equal((140, 90), (task.Result.Width, task.Result.Height));
        Check.Equal(expected.Pixels, task.Result.Pixels, "same bytes");

        // with no vectors there is nothing to render or blend
        var plain = DocumentState.Create(MixedDoc().Base).Add(new BlurRegion(0, new RectI(0, 0, 30, 30), 3, true, 6), out _);
        var t2 = Exporter.ExportAsync(plain);
        Pump(() => t2.IsCompleted, "the plain export");
        Check.Equal(Exporter.Export(plain).Pixels, t2.Result.Pixels);
    }

    [UnitTest]
    public static void The_export_composes_and_blends_on_workers_and_draws_vectors_on_the_calling_thread()
    {
        int caller = Environment.CurrentManagedThreadId;
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Phase, int Thread)>();
        Exporter.Probe = (phase, thread) => seen.Enqueue((phase, thread));
        try
        {
            var task = Exporter.ExportAsync(MixedDoc());
            Pump(() => task.IsCompleted, "the export");
            Check.True(task.IsCompletedSuccessfully);
        }
        finally { Exporter.Probe = null; }
        var phases = seen.ToArray();
        Check.Equal(new[] { "compose", "render", "blend" }, phases.Select(p => p.Phase).OrderBy(p => p == "compose" ? 0 : p == "render" ? 1 : 2).ToArray());
        Check.True(phases.Single(p => p.Phase == "compose").Thread != caller, "the raster compose ran on a worker");
        Check.True(phases.Single(p => p.Phase == "blend").Thread != caller, "the blend ran on a worker");
        Check.Equal(caller, phases.Single(p => p.Phase == "render").Thread, "the WPF render stays on the calling thread");
    }

    [UnitTest]
    public static void The_session_exports_without_blocking_caches_per_document_and_joins_a_running_export()
    {
        var s = new EditorSession(MixedDoc());
        var first = s.ExportAsync();
        var joined = s.ExportAsync();
        Check.True(ReferenceEquals(first, joined), "a second ask for the same document joins the running export");
        Check.False(first.IsCompleted, "the export did not run to the end inside the call");
        Pump(() => first.IsCompleted, "the export");
        Check.True(ReferenceEquals(first.Result, s.ExportAsync().Result), "cached per committed document");
        Check.Equal(Exporter.Export(s.Committed).Pixels, first.Result.Pixels, "the same picture the synchronous export makes");
        Check.True(ReferenceEquals(first.Result, s.Export()), "and the synchronous call shares the cache");
        s.Add(new RectOutline(0, new Rect(1, 1, 5, 5), Colors.Red, 2));
        var next = s.ExportAsync();
        Pump(() => next.IsCompleted, "the next export");
        Check.False(ReferenceEquals(first.Result, next.Result), "a new document is exported again");
        s.Release();
    }

    [UnitTest]
    public static void The_UI_stays_responsive_while_a_large_picture_is_exported()
    {
        // 4K, a blur (raster work) and one rectangle (vector work): the UI thread may only spend its time on the vector render
        var img = new BgraImage(3840, 2160);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = (byte)(i >> 8); img.Pixels[i + 1] = (byte)(i >> 12); img.Pixels[i + 2] = 90; img.Pixels[i + 3] = 255; }
        var doc = DocumentState.Create(img)
            .Add(new BlurRegion(0, new RectI(100, 100, 3000, 1500), 6, false, 8), out _)
            .Add(new RectOutline(0, new Rect(200, 200, 500, 300), Colors.Red, 4), out _);
        long worst = 0;
        var tick = Stopwatch.StartNew();
        long last = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Normal, (_, _) =>
        {
            long now = tick.ElapsedMilliseconds;
            worst = Math.Max(worst, now - last);
            last = now;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        try
        {
            var call = Stopwatch.StartNew();
            var task = Exporter.ExportAsync(doc);
            long callMs = call.ElapsedMilliseconds;   // the part that runs on the UI thread before the first await: the vector render
            last = tick.ElapsedMilliseconds;
            Pump(() => task.IsCompleted, "the 4K export", 60_000);
            Check.True(task.IsCompletedSuccessfully);
            Log.Info($"unittest: 4K export, UI thread part of the call {callMs} ms, longest gap between UI timer ticks {worst} ms, total {tick.ElapsedMilliseconds} ms");
#if !DEBUG
            // timing is judged on the optimized build only (a Debug build's vector render and tick handling are several times slower)
            Check.True(callMs < 1000, $"the call itself (the vector render) took {callMs} ms on the UI thread");
#endif
            // the bytes are right in every build
            Check.Equal(Exporter.Export(doc).Pixels, task.Result.Pixels, "the async export is the synchronous export's picture");
        }
        finally { timer.Stop(); }
#if !DEBUG
        // generous on a loaded CI machine: the point is that the UI is not blocked for the whole (seconds long) raster compose
        Check.True(worst < 1000, $"the UI thread was blocked for {worst} ms at a stretch");
#endif
    }

    // ------------------------------------------------------------------ S7: the S6 review leftovers

    [UnitTest]
    public static void A_redirect_is_never_followed_it_is_an_error_without_the_token()
    {
        var r = Upload("a.png", _ =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.Found);
            m.Headers.Location = new Uri("https://evil.example/steal");
            return m;
        });
        Check.Null(r.Up);
        Check.Contains(r.Error!.Message, "unexpected redirect");
        Check.DoesNotContain(r.Error.Message, "eup_secret");
        Check.Equal(1, r.Handler.Requests.Count, "the redirect target was not requested");
        foreach (int code in new[] { 301, 303, 307, 308 })
            Check.Contains(Upload("a.png", _ => new HttpResponseMessage((HttpStatusCode)code)).Error!.Message, "unexpected redirect", code.ToString());
    }

    [UnitTest]
    public static void The_type_refusal_words_are_whole_words_and_a_403_pauses_only_for_the_token()
    {
        foreach (var ok in new[] { "Unsupported file type", "Invalid MIME", "Bad extension .xyz", "Unsupported image format", "This is UNSUPPORTED" })
            Check.True(CopiumClient.IsTypeRefusal(400, ok), ok);
        foreach (var no in new[] { "Invalid token type", "Title format is wrong", "Mimeograph", "Account type not allowed", "Formatted title too long", "extensions" })
            Check.False(CopiumClient.IsTypeRefusal(400, no), no);
        Check.True(CopiumClient.IsTypeRefusal(415, ""));
        Check.False(CopiumClient.IsTypeRefusal(403, "Unsupported file type"), "only 400 and 415");
    }

    [UnitTest]
    public static void A_clipboard_bitmap_header_must_have_a_size_that_exists()
    {
        foreach (int size in new[] { 40, 52, 56, 64, 108, 124 })
            Check.True(ImageInput.DibVerdict(DibHeader(size, 100, 50), out _), "header size " + size);
        var core = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(core, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(4), 100);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(6), 50);
        Check.True(ImageInput.DibVerdict(core, out _), "the 12-byte core header");
        foreach (int size in new[] { 41, 48, 60, 100, 123, 125, 200, 4096, 0, -40 })
            Check.False(ImageInput.DibVerdict(DibHeader(size, 100, 50), out var err), "header size " + size);
    }

    [UnitTest]
    public static void A_write_time_two_seconds_off_is_still_the_same_file_three_is_not()
    {
        using var dir = new TempDir();
        var rig = Make(dir, upload: false);
        Capture(rig);
        var entry = rig.Toasts.LastEntry!;
        var written = entry.WrittenUtc!.Value;
        File.SetLastWriteTimeUtc(entry.Path, written.AddSeconds(1.5));   // a coarse file system's rounding
        var near = rig.Service.UploadAsync(entry, manual: true);
        Pump(() => near.IsCompleted, "the upload");
        Check.True(near.Result, "1.5 s off: uploaded");

        using var dir2 = new TempDir();
        var rig2 = Make(dir2, upload: false);
        Capture(rig2);
        var e2 = rig2.Toasts.LastEntry!;
        File.SetLastWriteTimeUtc(e2.Path, e2.WrittenUtc!.Value.AddSeconds(-3));
        var far = rig2.Service.UploadAsync(e2, manual: true);
        Pump(() => far.IsCompleted, "the check");
        Check.False(far.Result, "3 s off: not uploaded");
        Check.Contains(rig2.Toasts.LastProblemText, "changed after it was saved");
    }

    [UnitTest]
    public static void A_mapped_network_drive_is_a_network_folder_too()
    {
        var before = ScreenshotService.DriveTypeOf;
        try
        {
            ScreenshotService.DriveTypeOf = root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? 4u : 3u;
            Check.True(ScreenshotService.IsNetworkFolder(@"Z:\Clips"), "a mapped drive");
            Check.True(ScreenshotService.IsNetworkFolder(@"z:\"), "lower case");
            Check.True(ScreenshotService.IsNetworkFolder(@"\\nas\clips"), "a UNC path");
            Check.True(ScreenshotService.IsNetworkFolder(@"\\?\UNC\nas\clips"), "a device path");
            Check.False(ScreenshotService.IsNetworkFolder(@"C:\Clips"), "a local drive");
            Check.False(ScreenshotService.IsNetworkFolder(@"Clips"), "relative");
            Check.False(ScreenshotService.IsNetworkFolder(null));
            Check.False(ScreenshotService.IsNetworkFolder("  "));
            ScreenshotService.DriveTypeOf = _ => throw new InvalidOperationException("no such drive");
            Check.False(ScreenshotService.IsNetworkFolder(@"Q:\x"), "a drive that cannot be looked at counts as local");

            // an import keeps ours instead of taking the mapped-drive folder over
            ScreenshotService.DriveTypeOf = root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? 4u : 3u;
            var kept = new List<string>();
            string text = SettingsImport.KeepOurOwn("""{ "ClipsFolder": "Z:\\Clips", "ArchivePath": "D:\\Archive" }""", """{ "ClipsFolder": "C:\\Clips" }""", false, kept);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            Check.Equal(@"C:\Clips", doc.RootElement.GetProperty("ClipsFolder").GetString());
            Check.Equal(@"D:\Archive", doc.RootElement.GetProperty("ArchivePath").GetString(), "a local drive is taken");
            Check.Equal(new[] { "ClipsFolder" }, kept.ToArray());
        }
        finally { ScreenshotService.DriveTypeOf = before; }
    }

    [UnitTest]
    public static void A_close_deferred_during_a_save_does_not_ask_a_second_question_when_the_quit_resolves_the_editor()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new EditorWindowHost { ShowWindows = false };
        var open = host.EditAsync(new EditorRequest
        {
            Document = DocumentState.Create(new BgraImage(8, 8)), Title = "t", IsNewCapture = true, Options = new ScreenshotEditorOptions(),
            SaveAsync = (_, _, _) => gate.Task,
        });
        var window = host.Window!;
        var save = window.SaveCoreAsync(finish: false);
        _ = window.RequestCloseAsync();                                  // Esc while saving: deferred until the save ends
        var quit = window.ResolveForQuitAsync(userInitiated: false);     // and the app quits meanwhile
        Check.True(window.ResolvingQuit);
        gate.SetResult(true);
        Pump(() => save.IsCompleted && quit.IsCompleted, "the save and the quit");
        var moment = Stopwatch.StartNew();
        Pump(() => moment.ElapsedMilliseconds > 200, "a moment for the deferred close to run");
        Check.True(quit.Result, "the quit may go on");
        Check.True(host.IsOpen, "the deferred close stood aside: the quit owns the window now");
        Check.False(window.ResolvingQuit);
        window.CloseApproved();
        Pump(() => open.IsCompleted, "the window to close");
    }
}