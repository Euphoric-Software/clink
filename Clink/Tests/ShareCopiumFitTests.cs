using System.IO;
using System.Net;
using System.Net.Http;
using Clink.Engine;
using Clink.Screenshots;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Copium uploads of over-limit clips through a smaller copy (<c>AppController.ShareAsync</c> with allowFit): a mock HTTP handler for
/// Copium, a fake transcoder and a cache in a temp folder. No network, no GPU, no Media Foundation. The over-size clips are files of
/// 201 MB (zeros, never read: the fake transcoder writes its own output), 5 s long, so the 200 MB free limit is crossed by a byte-size margin only.
/// </summary>
internal static class ShareCopiumFitTests
{
    private const long OverSize = 201_000_000;   // Copium free takes 200 000 000
    private const long CopySize = 1_000_000;

    private static T Wait<T>(Task<T> task)
    {
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the task", 30_000);   // the test thread has a dispatcher context: a blocking wait would deadlock on it
        return task.Result;
    }

    private sealed class Mock : HttpMessageHandler
    {
        public int Requests;
        public bool ReadBody = true;   // off for a 201 MB original: the handler then never streams the file
        public readonly List<long> BodyBytes = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Requests);
            long length = ReadBody && request.Content != null ? (await request.Content.ReadAsByteArrayAsync(ct)).LongLength : 0;
            lock (BodyBytes) BodyBytes.Add(length);
            string link = $"https://v.copium.pics/up{n}.mp4";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"id{{n}}","link":"{{link}}","url":"{{link}}","size":64}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Fake : IShareTranscoder
    {
        public double Duration = 5;
        public Func<int, long> TranscodeSize = _ => CopySize;
        public Func<string, CancellationToken, Task>? Hold;   // awaited once the output file is written
        public Func<int, long>? ReportedSize;   // what the result says the file's size is (default: the real size); the cache then keeps the real one
        public double? CopyDuration;   // the finished copy's length (default: Duration)
        public int Probes, Transcodes;
        public readonly List<string> Outputs = new();
        public readonly TaskCompletionSource Writing = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ShareProbeResult Probe(string path, double keyframeSeconds)
        {
            Interlocked.Increment(ref Probes);
            return new ShareProbeResult(Duration, "h264", 1920, 1080, 60, 1, ShareProbe.ClampKeyframe(keyframeSeconds));
        }

        public async Task<ShareTranscodeResult> TranscodeAsync(string sourcePath, string outputPath, ShareTranscodeRequest request, IProgress<double>? progress, CancellationToken ct)
        {
            int n;
            lock (Outputs) { n = ++Transcodes; Outputs.Add(outputPath); }
            long size = TranscodeSize(n);
            using (var fs = File.Create(outputPath)) fs.SetLength(size);
            Writing.TrySetResult();
            progress?.Report(0.5);
            if (Hold != null) await Hold(sourcePath, ct);
            ct.ThrowIfCancellationRequested();
            progress?.Report(1.0);
            return new ShareTranscodeResult(outputPath, ReportedSize?.Invoke(n) ?? size, TimeSpan.FromSeconds(CopyDuration ?? Duration), 300, request.Width, request.Height, request.Fps, new[] { "Mix" }, "Fake") { MeasuredFps = request.Fps };
        }

        public Task<ShareTranscodeResult> RemuxMixOnlyAsync(string sourcePath, string outputPath, IProgress<double>? progress, CancellationToken ct)
            => throw new InvalidOperationException("a 1080p60 clip this big needs a re-encode");
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Settings S;
        public readonly AppController App;
        public readonly Mock Http = new();
        public readonly Fake Transcoder = new();
        public readonly ShareCache Cache;
        public readonly List<string> Clipboard = new();
        public bool Game;
        public readonly List<string> Toasts = new();   // the "upload started" toasts (the real ones would open windows)
        public readonly List<string> Notices = new();   // every footer line, in order
        private readonly string? _savedOverride;
        private int _n;

        public Rig(bool elevated = false, bool plus = false)
        {
            _savedOverride = Settings.DirOverride;   // as the runner left it
            Settings.DirOverride = Dir.Path;
            S = Settings.FromJson("{}");
            S.CopiumToken = "eup_token";
            S.CopiumConsent = true;
            S.CloudSharingEnabled = true;
            S.CopiumPlus = plus;
            S.ToastShared.Enabled = false;
            Cache = new ShareCache(Dir.File("cache"), isElevated: () => elevated);
            App = new AppController(S, new Updater(S))
            {
                ClipboardOverride = Clipboard.Add,
                ShareCacheOverride = Cache,
                ShareTranscoderImpl = Transcoder,
                FreeSpaceProbe = _ => long.MaxValue,
                AdapterLuidOverride = () => null,
                GameOrFullScreenProbe = () => Game,
                BulkNoticeSink = (_, _) => { },   // never the real toast
                UploadToastSink = (_, text) => { lock (Toasts) Toasts.Add(text); },
            };
            App.Notice += msg => { lock (Notices) Notices.Add(msg); };
            CopiumClient.HttpOverride = new HttpClient(Http);
        }

        public ClipEntry Clip(long size, double seconds = 5)
        {
            Directory.CreateDirectory(Dir.File("clips"));
            string path = Path.Combine(Dir.File("clips"), $"clip{++_n}.mp4");
            using (var fs = File.Create(path)) fs.SetLength(size);
            return new ClipEntry { Path = path, Size = size, Duration = seconds, Created = DateTime.Now };
        }

        /// <summary>Every file left in the cache folder that is a part file (an unfinished copy).</summary>
        public string[] Parts() => Directory.Exists(Cache.Root)
            ? Directory.EnumerateFiles(Cache.Root, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

        public void Dispose()
        {
            CopiumClient.HttpOverride = null;
            try { App.Dispose(); } catch { }
            Cache.Dispose();
            Settings.DirOverride = _savedOverride;
            Dir.Dispose();
        }

        public string[] ToastsNow() { lock (Toasts) return Toasts.ToArray(); }
        public string[] NoticesNow() { lock (Notices) return Notices.ToArray(); }
        public string Key(ClipEntry e) => ShareCache.KeyFor(e.Path, ShareFitModel.ForCopium(S.CopiumPlus).LimitBytes, ShareFitModel.Codec, ShareFitModel.CacheVariant);
    }

    [UnitTest]
    public static void A_clip_within_the_limit_is_uploaded_as_it_is_and_no_copy_is_made()
    {
        using var rig = new Rig();
        var clip = rig.Clip(5000);
        Check.True(Wait(rig.App.ShareAsync(clip)));
        Check.Equal(1, rig.Http.Requests);
        Check.True(rig.Http.BodyBytes[0] >= 5000 && rig.Http.BodyBytes[0] < 6000, "the original went up: " + rig.Http.BodyBytes[0]);
        Check.Equal((0, 0), (rig.Transcoder.Probes, rig.Transcoder.Transcodes), "nothing was looked at or made");
        Check.False(Directory.Exists(rig.Cache.Root), "and the cache was not touched");
        Check.Equal("https://v.copium.pics/up1.mp4", clip.CopiumLink);
        Check.Equal(ShareOutcome.Uploaded, rig.App.LastShareOutcome);
    }

    [UnitTest]
    public static void A_clip_over_the_size_limit_uploads_its_smaller_copy_and_the_link_stays_on_the_original()
    {
        using var rig = new Rig();
        var clip = rig.Clip(OverSize);
        var texts = new List<string>();
        var progress = new SyncProgress(p => { lock (texts) texts.Add(rig.App.ShareProgressText(clip, p)); });
        Check.True(Wait(rig.App.ShareAsync(clip, progress)));
        Check.Equal(1, rig.Transcoder.Transcodes);
        Check.Equal(1, rig.Http.Requests);
        Check.True(rig.Http.BodyBytes[0] >= CopySize && rig.Http.BodyBytes[0] < CopySize + 1000, "the copy went up, not the original: " + rig.Http.BodyBytes[0]);
        Check.Equal("https://v.copium.pics/up1.mp4", clip.CopiumLink);
        Check.Equal("id1", clip.CopiumId);
        Check.Equal(ShareOutcome.Uploaded, rig.App.LastShareOutcome);
        Check.Null(rig.App.ShareRefusal);
        Check.Equal(new[] { "https://v.copium.pics/up1.mp4" }, rig.Clipboard.ToArray());
        Check.True(File.Exists(clip.Path), "the original is untouched");
        Check.Equal(OverSize, new FileInfo(clip.Path).Length);
        string[] seen;
        lock (texts) seen = texts.ToArray();
        string all = string.Join(" | ", seen);
        var t = ShareFitModel.ForCopium(false);
        Check.True(seen.Length > 0 && !seen[0].StartsWith("Uploading… ", StringComparison.Ordinal), "the first line is the copy's, not the upload's: " + all);
        Check.True(seen.Contains(ShareFitModel.WorkingText(t, null, 0.5)), "the copy's progress: " + all);
        Check.True(seen.Contains(ShareFitModel.WorkingText(t, null, 1.0)), all);
        Check.True(seen[^1].StartsWith("Uploading… "), "and then the upload's: " + all);
        Check.True(seen.SkipWhile(s => !s.StartsWith("Uploading… ", StringComparison.Ordinal)).All(s => s.StartsWith("Uploading… ", StringComparison.Ordinal)), "never back to the copy: " + all);
        Check.False(rig.App.IsUploading(clip), "nothing is left running");
    }

    [UnitTest]
    public static void A_folder_scanned_clip_has_its_length_read_and_is_refused_when_that_is_over_the_limit()
    {
        using var rig = new Rig();
        rig.Transcoder.Duration = 700;   // 11:40: over Copium free's 10 minutes
        var scanned = rig.Clip(OverSize, seconds: 0);
        Check.False(Wait(rig.App.ShareAsync(scanned)));
        Check.Equal(1, rig.Transcoder.Probes);
        Check.Equal(0, rig.Transcoder.Transcodes);
        Check.Equal(0, rig.Http.Requests);
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Contains(rig.App.ShareRefusal, "Over Copium's free limit");
        Check.Contains(rig.App.LastNotice, "this clip is");

        rig.Transcoder.Duration = 5;
        var shortScanned = rig.Clip(OverSize, seconds: 0);
        Check.True(Wait(rig.App.ShareAsync(shortScanned)), "a short scanned clip is copied and uploaded");
        Check.Equal(1, rig.Transcoder.Transcodes);
    }

    [UnitTest]
    public static void A_clip_over_the_length_limit_is_still_refused_without_a_copy()
    {
        using var rig = new Rig();
        var clip = rig.Clip(OverSize, seconds: 601);
        Check.False(Wait(rig.App.ShareAsync(clip)));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Equal("Over Copium's free limit (200 MB / 10 min)", rig.App.ShareRefusal);
        var longSmall = rig.Clip(5000, seconds: 601);
        Check.False(Wait(rig.App.ShareAsync(longSmall)), "small but long");
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Equal((0, 0, 0), (rig.Transcoder.Probes, rig.Transcoder.Transcodes, rig.Http.Requests));
        Check.Null(clip.CopiumLink);
    }

    [UnitTest]
    public static void The_refusal_kind_says_whether_the_account_limit_or_the_smaller_copy_refused()
    {
        using (var rig = new Rig())
        {
            var longClip = rig.Clip(OverSize, seconds: 601);   // over the length limit: no copy can help
            Check.False(Wait(rig.App.ShareAsync(longClip)));
            Check.Equal(ShareRefusalKind.OverLimit, rig.App.RefusalKind);
            rig.Transcoder.Duration = 700;
            Check.False(Wait(rig.App.ShareAsync(rig.Clip(OverSize, seconds: 0))));   // the length is read from the file: also the account's limit
            Check.Equal(ShareRefusalKind.OverLimit, rig.App.RefusalKind);
            rig.Transcoder.Duration = 5;
            Check.True(Wait(rig.App.ShareAsync(rig.Clip(OverSize))));
            Check.Equal(ShareRefusalKind.None, rig.App.RefusalKind, "an upload clears it");
        }
        using (var rig = new Rig())
        {
            rig.Transcoder.TranscodeSize = _ => OverSize;   // the copy stays too big: a smaller copy could not be made
            Check.False(Wait(rig.App.ShareAsync(rig.Clip(OverSize))));
            Check.Equal(ShareRefusalKind.SmallerCopy, rig.App.RefusalKind);
            Check.True(rig.App.ShareRefusal!.EndsWith(".", StringComparison.Ordinal), "a smaller-copy refusal is a whole sentence: " + rig.App.ShareRefusal);
        }
        using (var rig = new Rig(elevated: true))
        {
            Check.False(Wait(rig.App.ShareAsync(rig.Clip(OverSize))));   // no smaller copies as administrator
            Check.Equal(ShareRefusalKind.SmallerCopy, rig.App.RefusalKind);
            Check.True(rig.App.ShareRefusal!.EndsWith(".", StringComparison.Ordinal), "so is this one: " + rig.App.ShareRefusal);
        }
        using (var rig = new Rig())
        {
            rig.Game = true;
            rig.S.ShareCopiesDuringGames = false;
            Check.False(Wait(rig.App.ShareAsync(rig.Clip(OverSize), announce: true, context: ConsentContext.ShareLast)));
            Check.Equal(ShareRefusalKind.Phrase, rig.App.RefusalKind, "a game in front is a phrase, not a sentence");
        }
    }

    [UnitTest]
    public static void A_copy_that_comes_out_longer_than_the_length_limit_is_refused_as_over_the_limit()
    {
        using var rig = new Rig();
        rig.Transcoder.CopyDuration = 700;   // the clip says 5 s, so nothing is refused up front; the finished copy is 11:40, over Copium free's 10 minutes
        var clip = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(clip)));
        Check.Equal(1, rig.Transcoder.Transcodes);
        Check.Equal(0, rig.Http.Requests);
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Equal(ShareRefusalKind.OverLimit, rig.App.RefusalKind, "the account's limit, so the advice is to tighten the selection");
        Check.Equal("Over Copium's free limit (200 MB / 10 min)", rig.App.ShareRefusal);
        Check.Equal(0, rig.Parts().Length, "the unusable copy is not kept as a part file");
    }

    [UnitTest]
    public static void A_clip_that_cannot_fit_even_as_a_copy_is_refused_as_a_smaller_copy_problem()
    {
        using var rig = new Rig();
        rig.Transcoder.Duration = 3000;   // the clip says 5 s, the file says 50 minutes: no copy of it fits in 200 MB, the planner answers CantFit before any re-encode
        var clip = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(clip)));
        Check.Equal(0, rig.Transcoder.Transcodes, "nothing was encoded");
        Check.Equal(0, rig.Http.Requests);
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Equal(ShareRefusalKind.SmallerCopy, rig.App.RefusalKind);
        Check.Contains(rig.App.ShareRefusal, "can't fit in 200 MB");
    }

    [UnitTest]
    public static void The_refusal_helpers_split_the_size_and_the_length_limit()
    {
        Check.True(AppController.ShareSizeOver(200_000_001, plus: false));
        Check.False(AppController.ShareSizeOver(200_000_000, plus: false), "exactly at the limit");
        Check.False(AppController.ShareSizeOver(200_000_001, plus: true));
        Check.True(AppController.ShareSizeOver(2_000_000_001, plus: true));
        Check.Null(AppController.ShareLengthRefusalFor(600, plus: false));
        Check.Equal("Over Copium's free limit (200 MB / 10 min)", AppController.ShareLengthRefusalFor(601, plus: false));
        Check.Null(AppController.ShareLengthRefusalFor(0, plus: false), "no length known");
        Check.Null(AppController.ShareLengthRefusalFor(3600, plus: true));
        Check.NotNull(AppController.ShareLengthRefusalFor(3601, plus: true));
        Check.NotNull(AppController.ShareRefusalFor(200_000_001, 5, plus: false), "the combined helper still refuses both");
        Check.NotNull(AppController.ShareRefusalFor(1, 601, plus: false));
    }

    [UnitTest]
    public static void The_kill_switch_during_the_copy_cancels_it_and_nothing_is_uploaded_or_left_behind()
    {
        using var rig = new Rig();
        rig.Transcoder.Hold = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var clip = rig.Clip(OverSize);
        var upload = rig.App.ShareAsync(clip);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        Check.True(rig.Parts().Length > 0, "the copy is being written");
        Check.True(rig.App.IsUploading(clip));
        rig.App.SetCloudSharing(false);
        Check.False(Wait(upload));
        Check.Equal(0, rig.Http.Requests);
        Check.Equal(ShareOutcome.Declined, rig.App.LastShareOutcome);
        Check.Equal(0, rig.Parts().Length, "the part file is gone");
        Check.False(File.Exists(rig.Transcoder.Outputs[0]));
        Check.Null(clip.CopiumLink);
        Check.False(rig.App.IsUploading(clip));
        Check.Null(rig.App.ShareRefusal);
    }

    [UnitTest]
    public static void A_second_upload_of_the_same_clip_joins_the_copy_and_the_upload()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip(OverSize);
        var first = rig.App.ShareAsync(clip);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        var second = rig.App.ShareAsync(clip);
        ScreenshotUploadTests.Pump(() => rig.App.LastNotice.StartsWith("Already"), "the second ask to join", 5000);
        Check.False(second.IsCompleted, "it waits for the first");
        gate.SetResult();
        Check.True(Wait(first));
        Check.True(Wait(second));
        Check.Equal(1, rig.Transcoder.Transcodes);
        Check.Equal(1, rig.Http.Requests);
    }

    [UnitTest]
    public static void Bulk_upload_still_refuses_an_over_limit_clip_without_a_copy()
    {
        using var rig = new Rig();
        var big = rig.Clip(OverSize);
        var small = rig.Clip(5000);
        var r = Wait(rig.App.ShareManyAsync(new[] { big, small }, null, _ => null, CancellationToken.None));
        Check.Equal(0, rig.Transcoder.Transcodes);
        Check.Equal(0, rig.Transcoder.Probes);
        Check.Equal(1, rig.Http.Requests);
        Check.Equal(1, r.Refused);
        Check.Null(big.CopiumLink);
        Check.NotNull(small.CopiumLink);
        // and the quiet single call the bulk step makes, with allowFit off
        var big2 = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(big2, quiet: true, allowFit: false)));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Equal(0, rig.Transcoder.Transcodes);
    }

    [UnitTest]
    public static void A_copy_that_stays_too_big_refuses_with_the_models_text_and_uploads_nothing()
    {
        using var rig = new Rig();
        rig.Transcoder.TranscodeSize = _ => OverSize;
        var clip = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(clip)));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        var t = ShareFitModel.ForCopium(false);
        string failed = ShareFitModel.FailedText(t, ShareFitModel.StayedTooBigReason, OverSize);
        Check.True(rig.App.ShareRefusal!.StartsWith(failed, StringComparison.Ordinal), rig.App.ShareRefusal);
        Check.Equal(rig.App.ShareRefusal, rig.App.LastNotice, "the footer and the hotkey toast say the same");
        Check.Equal(0, rig.Http.Requests);
        Check.Null(clip.CopiumLink);
        Check.Equal(0, rig.Parts().Length);
        Check.False(rig.App.IsUploading(clip));
    }

    [UnitTest]
    public static void With_copies_during_games_off_a_game_in_front_refuses_with_the_game_text_and_with_it_on_the_copy_is_made()
    {
        using var rig = new Rig();
        rig.Game = true;
        rig.S.ShareCopiesDuringGames = false;
        var clip = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(clip, announce: true, context: ConsentContext.ShareLast)));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        string gameText = ShareFitModel.GameRefusalText(ShareFitModel.ForCopium(false));
        Check.Equal(gameText, rig.App.ShareRefusal);
        Check.Equal(gameText, rig.App.LastNotice);
        Check.Equal((0, 0), (rig.Transcoder.Transcodes, rig.Http.Requests));

        // a clip that fits is not a copy job: the game does not matter
        Check.True(Wait(rig.App.ShareAsync(rig.Clip(5000))));

        rig.S.ShareCopiesDuringGames = true;
        Check.True(Wait(rig.App.ShareAsync(clip, announce: true, context: ConsentContext.ShareLast)));
        Check.Equal(1, rig.Transcoder.Transcodes);
        Check.NotNull(clip.CopiumLink);
        Check.Null(rig.App.ShareRefusal);
    }

    [UnitTest]
    public static void As_administrator_the_original_goes_up_only_when_it_fits()
    {
        using var rig = new Rig(elevated: true);
        var small = rig.Clip(5000);
        Check.True(Wait(rig.App.ShareAsync(small)), "it fits: no copy needed");
        var big = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(big)), "too big and no copies here: Copium would refuse it");
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        string unavailable = ShareFitModel.UnavailableText(ShareFitModel.ForCopium(false), ShareUnavailableKind.Elevated, OverSize);
        Check.Equal(unavailable, rig.App.ShareRefusal);
        Check.Equal(unavailable, rig.App.LastNotice);
        Check.Equal(1, rig.Http.Requests);
        Check.Equal(0, rig.Transcoder.Transcodes);
        Check.Null(big.CopiumLink);
    }

    [UnitTest]
    public static void Copium_Plus_checks_its_own_size_limit_before_it_makes_a_copy()
    {
        using var rig = new Rig(plus: true);
        rig.Http.ReadBody = false;
        Check.True(Wait(rig.App.ShareAsync(rig.Clip(OverSize))), "201 MB is nothing for Plus: uploaded as it is");
        Check.Equal((0, 0, 1), (rig.Transcoder.Probes, rig.Transcoder.Transcodes, rig.Http.Requests));
    }

    [UnitTest]
    public static void ShareProgressText_says_copying_while_the_copy_is_made_and_uploading_otherwise()
    {
        using var rig = new Rig();
        var clip = rig.Clip(5000);
        Check.Equal($"Uploading… {0.4:P0}", rig.App.ShareProgressText(clip, 0.4), "no copy running: the upload line");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var big = rig.Clip(OverSize);
        var upload = rig.App.ShareAsync(big);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        Check.Equal(ShareFitModel.WorkingText(ShareFitModel.ForCopium(false), null, 0.4), rig.App.ShareProgressText(big, 0.4));
        Check.Equal($"Uploading… {0.4:P0}", rig.App.ShareProgressText(clip, 0.4), "another clip is not copying");
        gate.SetResult();
        Check.True(Wait(upload));
        Check.Equal($"Uploading… {0.4:P0}", rig.App.ShareProgressText(big, 0.4), "and the phase is forgotten when it is done");
    }

    [UnitTest]
    public static void The_clip_row_the_toast_and_the_trim_editor_take_their_progress_line_from_ShareProgressText()
    {
        string? root = FindSourceFolder();
        if (root == null) return;   // not run from a checkout: nothing to read
        foreach (string file in new[] { @"UI\ClipsPane.xaml.cs", @"UI\ToastWindow.xaml.cs", @"UI\TrimWindow.xaml.cs" })
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            Check.Contains(text, "ShareProgressText(", file);
            Check.False(text.Contains("Uploading… {p:P0}", StringComparison.Ordinal), file + " still builds its own upload line");
            // the toast keeps its own L.T line (UploadingText); the clip list and the trim editor must not build theirs: a literal there is a second copy of the words
            if (file != @"UI\ToastWindow.xaml.cs")
                Check.False(text.Contains("L.T(\"Uploading\u2026 {percent}\"", StringComparison.Ordinal), file + " builds its own L.T upload line instead of ShareProgressText");
        }
        // the library bar's bulk uploads (clips and screenshots) use the same line too
        string selection = File.ReadAllText(Path.Combine(root, @"UI\ClipsPane.Selection.cs"));
        Check.Contains(selection, "ShareProgressText(", "ClipsPane.Selection.cs (clips)");
        Check.Contains(selection, "UploadingPercentText(", "ClipsPane.Selection.cs (screenshots)");
        Check.False(selection.Contains("L.T(\"Uploading\u2026 {percent}\"", StringComparison.Ordinal), "ClipsPane.Selection.cs builds its own L.T upload line");
    }

    private sealed class Refuse(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [UnitTest]
    public static void A_failed_upload_is_shown_in_the_users_language_while_the_log_keeps_the_english_text()
    {
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => logged.Enqueue(line);
        Log.Line += Collect;
        try
        {
            using var rig = new Rig();
            CopiumClient.HttpOverride = new HttpClient(new Refuse(HttpStatusCode.ServiceUnavailable, "{\"error\":\"down\"}"));
            using var scope = L.Scope("qps-ploc");
            L.ClearMissing();
            var clip = rig.Clip(5000);
            Check.False(Wait(rig.App.ShareAsync(clip)));
            Check.Equal(ShareOutcome.Failed, rig.App.LastShareOutcome);
            // what the clip row and the toast show (ShareError) and the footer line (LastNotice) are from the catalog; the server's own words are left alone
            Check.True(PseudoLocale.IsPseudo(rig.App.ShareError ?? ""), "ShareError is translated: " + rig.App.ShareError);
            Check.Contains(rig.App.ShareError!, "down");
            Check.True(PseudoLocale.IsPseudo(rig.App.LastNotice), "the footer is translated: " + rig.App.LastNotice);
            // the log: English, whatever the language
            Check.True(logged.Any(l => l.Contains("notice: Upload failed: Copium is having trouble (down), try again later")), "the notice is logged in English");
            Check.False(logged.Any(l => l.Contains("notice: Upload failed") && l.Contains("[!!")), "no pseudo text in the log");
            Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every key is in the English catalog");
        }
        finally { Log.Line -= Collect; }
    }

    [UnitTest]
    public static void The_copying_toast_and_footer_come_with_the_first_copy_report()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip(OverSize);
        var upload = rig.App.ShareAsync(clip, announce: true, context: ConsentContext.ShareLast);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        Check.Equal(new[] { AppController.CopyingToastText }, rig.ToastsNow(), "one toast, once the copy had started");
        Check.True(rig.NoticesNow().Contains(AppController.CopyingToastText), "and the footer");
        gate.SetResult();
        Check.True(Wait(upload));
        Check.Equal(new[] { AppController.CopyingToastText }, rig.ToastsNow(), "the upload that follows does not toast again");
    }

    [UnitTest]
    public static void A_refusal_known_at_once_is_not_preceded_by_the_copying_toast_or_footer()
    {
        // a game in front with copies during games off
        using (var rig = new Rig())
        {
            rig.Game = true;
            rig.S.ShareCopiesDuringGames = false;
            Check.False(Wait(rig.App.ShareAsync(rig.Clip(OverSize), announce: true, context: ConsentContext.ShareLast)));
            Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
            Check.Equal(0, rig.ToastsNow().Length, "no toast from Clink itself: the caller shows the refusal");
            Check.False(rig.NoticesNow().Contains(AppController.CopyingToastText), string.Join(" | ", rig.NoticesNow()));
            Check.Equal(ShareFitModel.GameRefusalText(ShareFitModel.ForCopium(false)), rig.App.LastNotice);
        }
        // as administrator: no job, no mark, no cache folder
        using (var rig = new Rig(elevated: true))
        {
            var clip = rig.Clip(OverSize);
            Check.False(Wait(rig.App.ShareAsync(clip, announce: true, context: ConsentContext.ShareLast)));
            Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
            Check.Equal(0, rig.ToastsNow().Length);
            Check.False(rig.NoticesNow().Contains(AppController.CopyingToastText), string.Join(" | ", rig.NoticesNow()));
            Check.Equal(ShareFitModel.UnavailableText(ShareFitModel.ForCopium(false), ShareUnavailableKind.Elevated, OverSize), rig.App.LastNotice);
            Check.False(Directory.Exists(rig.Cache.Root), "the cache was not touched");
            Check.False(rig.Cache.IsInUse(rig.Key(clip)), "no mark");
            Check.Equal((0, 0, 0), (rig.Transcoder.Probes, rig.Transcoder.Transcodes, rig.Http.Requests));
        }
        // a scanned clip whose probed length is too long
        using (var rig = new Rig())
        {
            rig.Transcoder.Duration = 700;
            Check.False(Wait(rig.App.ShareAsync(rig.Clip(OverSize, seconds: 0), announce: true, context: ConsentContext.ShareLast)));
            Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
            Check.Equal(0, rig.ToastsNow().Length);
            Check.False(rig.NoticesNow().Contains(AppController.CopyingToastText), string.Join(" | ", rig.NoticesNow()));
        }
    }

    private const string ConsentOffText = "Not shared: sharing with Copium is not enabled";
    private const string SignInChangedText = "Not shared: the Copium sign-in changed while the copy was made";
    private const string PlanChangedText = "Not shared: your Copium plan changed while the copy was made. Share it again";

    private static void DeclinedDuringTheCopy(Action<Rig> change, string expectedNotice)
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip(OverSize);
        var upload = rig.App.ShareAsync(clip);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        string key = rig.Key(clip);
        Check.True(rig.Cache.IsInUse(key), "marked while it is made");
        change(rig);
        gate.SetResult();
        Check.False(Wait(upload));
        Check.Equal(0, rig.Http.Requests, "nothing went up");
        Check.Equal(ShareOutcome.Declined, rig.App.LastShareOutcome);
        Check.Equal(expectedNotice, rig.App.LastNotice);
        Check.Null(clip.CopiumLink);
        Check.Null(rig.App.ShareRefusal);
        Check.False(rig.Cache.IsInUse(key), "the lease is released");
        Check.False(rig.App.IsUploading(clip));
        Check.Equal($"Uploading… {0.2:P0}", rig.App.ShareProgressText(clip, 0.2), "the share phase was cleared");
    }

    // ------------------------------------------------------------------ the reason reaches the caller of that call (hotkey, toast)

    private static ClipEntry InLibrary(Rig rig, long size)
    {
        var made = rig.Clip(size);
        var entry = rig.App.Library.Add(made.Path, 5, size, null, Array.Empty<string>());
        entry.Size = size;
        return entry;
    }

    private static void Settle(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ScreenshotUploadTests.Pump(() => sw.ElapsedMilliseconds > ms, "the hotkey path to settle");
    }

    /// <summary>The hotkey while the copy is made, <paramref name="change"/> applied meanwhile: the problems it raised.</summary>
    private static (List<(string Title, string Text)> Problems, Rig Rig, ClipEntry Clip) HotkeyDuringTheCopy(Action<Rig> change)
    {
        var rig = new Rig();
        try
        {
            var problems = new List<(string, string)>();
            rig.App.ProblemSink = (t, x) => { lock (problems) problems.Add((t, x)); };
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
            var clip = InLibrary(rig, OverSize);
            rig.App.ShareLast();
            ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
            change(rig);
            gate.SetResult();
            ScreenshotUploadTests.Pump(() => !rig.App.IsUploading(clip), "the upload to end");
            Settle(300);
            return (problems, rig, clip);
        }
        catch
        {
            rig.Dispose();   // a timed-out Pump throws: put Settings.DirOverride and CopiumClient.HttpOverride back
            throw;
        }
    }

    [UnitTest]
    public static void The_hotkey_says_so_when_the_sign_in_changed_during_the_copy()
    {
        var (problems, rig, clip) = HotkeyDuringTheCopy(r => r.S.CopiumToken = "eup_other");
        using (rig)
        {
            Check.Equal("Clip not uploaded|" + SignInChangedText, string.Join(";", problems.Select(p => p.Title + "|" + p.Text)), "one problem notification with the reason");
            Check.Equal(0, rig.Http.Requests, "nothing was uploaded");
            Check.Null(clip.CopiumLink);
        }
    }

    [UnitTest]
    public static void The_hotkey_says_so_when_the_plan_changed_during_the_copy()
    {
        var (problems, rig, clip) = HotkeyDuringTheCopy(r => r.S.CopiumPlus = true);
        using (rig)
        {
            Check.Equal("Clip not uploaded|" + PlanChangedText, string.Join(";", problems.Select(p => p.Title + "|" + p.Text)));
            Check.Equal(0, rig.Http.Requests);
            Check.Null(clip.CopiumLink);
        }
    }

    [UnitTest]
    public static void The_hotkey_stays_silent_when_the_user_declined_the_consent_prompt()
    {
        using var rig = new Rig();
        rig.S.CopiumConsent = false;
        var problems = new List<(string, string)>();
        rig.App.ProblemSink = (t, x) => { lock (problems) problems.Add((t, x)); };
        int asked = 0;
        rig.App.ConsentPromptOverride = _ => { asked++; return Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow)); };
        InLibrary(rig, 5000);
        rig.App.ShareLast();
        ScreenshotUploadTests.Pump(() => rig.App.LastShareOutcome == ShareOutcome.Declined, "the answer");
        Settle(300);
        Check.Equal(1, asked);
        Check.Equal(0, problems.Count, "the user's own Not now is no problem");
        Check.Equal(0, rig.Http.Requests);
    }

    [UnitTest]
    public static void Two_shares_at_once_each_get_their_own_result()
    {
        using var rig = new Rig();
        var gates = new Dictionary<string, TaskCompletionSource>();
        var declined = rig.Clip(OverSize);
        var uploaded = rig.Clip(OverSize);
        foreach (var c in new[] { declined, uploaded }) gates[c.Path] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (src, ct) => gates[src].Task.WaitAsync(ct);
        var reasonsA = new List<string>();
        var reasonsB = new List<string>();
        var a = rig.App.ShareAsync(declined, declinedAfterCopy: reasonsA.Add);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the first copy to start");
        var b = rig.App.ShareAsync(uploaded, declinedAfterCopy: reasonsB.Add);   // waits for the copy slot behind the first
        ScreenshotUploadTests.Pump(() => rig.App.ShareProgressText(uploaded, 0) == ShareFitModel.WaitingText, "the second clip to wait", 10_000);
        rig.S.CopiumToken = "eup_other";   // changed while the first copy is made...
        gates[declined.Path].SetResult();
        Check.False(Wait(a));
        rig.S.CopiumToken = "eup_token";   // ...and back before the second one is done
        gates[uploaded.Path].SetResult();
        Check.True(Wait(b));
        Check.Equal(new[] { SignInChangedText }, reasonsA.ToArray(), "the declined share got its reason");
        Check.Equal(0, reasonsB.Count, "the uploaded one got none");
        Check.Null(declined.CopiumLink);
        Check.NotNull(uploaded.CopiumLink);
        Check.Equal(1, rig.Http.Requests);
    }

    [UnitTest]
    public static void The_toast_Share_button_says_Not_shared_with_the_reason_when_the_copy_was_not_uploaded()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip(OverSize);
        var toast = new ToastWindow(null!, "clip", clip, null, "Clip saved", "", TimeSpan.FromSeconds(7), false);
        var choice = toast.RunChoiceAsync(ShareChoice.Upload);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        rig.S.CopiumToken = "eup_other";
        gate.SetResult();
        ScreenshotUploadTests.Pump(() => choice.IsCompleted, "the toast's share");
        choice.GetAwaiter().GetResult();
        Check.Equal(SignInChangedText, toast.ShareButton.ToolTip as string, "the reason is the tooltip");
        Check.Equal(SignInChangedText, toast.Subtitle.Text, "and the line below");
        Check.True(toast.ShareArrow.Visibility == System.Windows.Visibility.Visible, "a token and cloud sharing on: the split button");
        Check.Equal(ShareChoices.ToastLabel(true, true, false), toast.ShareButton.Content as string, "a split button keeps its Share label");
        Check.True(toast.ShareButton.IsEnabled, "and can be pressed again");
        toast.Dismiss();
    }

    [UnitTest]
    public static void A_plain_toast_Share_button_reads_Not_shared_with_the_reason_in_its_tooltip()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip(OverSize);
        rig.S.CopiumToken = "";   // built without a token: the plain button
        var toast = new ToastWindow(null!, "clip", clip, null, "Clip saved", "", TimeSpan.FromSeconds(7), false);
        Check.True(toast.ShareArrow.Visibility != System.Windows.Visibility.Visible, "no token: the plain button");
        rig.S.CopiumToken = "eup_token";   // signed in before the share
        var choice = toast.RunChoiceAsync(ShareChoice.Upload);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        rig.S.CopiumToken = "eup_other";
        gate.SetResult();
        ScreenshotUploadTests.Pump(() => choice.IsCompleted, "the toast's share");
        choice.GetAwaiter().GetResult();
        Check.Equal("Not shared", toast.ShareButton.Content as string);
        Check.Equal(SignInChangedText, toast.ShareButton.ToolTip as string, "the reason is the tooltip");
        Check.Equal(SignInChangedText, toast.Subtitle.Text, "and the line below");
        Check.True(toast.ShareButton.IsEnabled, "and can be pressed again");
        toast.Dismiss();
    }

    [UnitTest]
    public static void Cloud_sharing_turned_off_during_the_copy_hides_the_toast_Share_button_and_keeps_the_reason()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip(OverSize);
        var toast = new ToastWindow(null!, "clip", clip, null, "Clip saved", "", TimeSpan.FromSeconds(7), false);
        Check.True(toast.ShareGroup.Visibility == System.Windows.Visibility.Visible, "cloud sharing on: the Share button shows");
        var choice = toast.RunChoiceAsync(ShareChoice.Upload);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        rig.S.CloudSharingEnabled = false;
        gate.SetResult();
        ScreenshotUploadTests.Pump(() => choice.IsCompleted, "the toast's share");
        choice.GetAwaiter().GetResult();
        Check.Equal(AppController.CloudOffText, toast.Subtitle.Text, "the reason stays in the line below");
        Check.True(toast.ShareGroup.Visibility == System.Windows.Visibility.Collapsed, "nothing left to press");
        Check.Equal(0, rig.Http.Requests, "nothing went up");
        toast.Dismiss();
    }

    [UnitTest]
    public static void Consent_turned_off_during_the_copy_uploads_nothing() => DeclinedDuringTheCopy(rig => rig.S.CopiumConsent = false, ConsentOffText);

    [UnitTest]
    public static void A_token_changed_during_the_copy_uploads_nothing_and_says_the_sign_in_changed() => DeclinedDuringTheCopy(rig => rig.S.CopiumToken = "eup_other", SignInChangedText);

    [UnitTest]
    public static void A_plan_changed_during_the_copy_uploads_nothing_and_says_so() => DeclinedDuringTheCopy(rig => rig.S.CopiumPlus = true, PlanChangedText);

    [UnitTest]
    public static void A_refusal_known_at_once_never_shows_the_copying_status_through_the_progress_callback()
    {
        using var rig = new Rig();
        rig.Game = true;
        rig.S.ShareCopiesDuringGames = false;
        var clip = rig.Clip(OverSize);
        var texts = new List<string>();
        var progress = new SyncProgress(p => { lock (texts) texts.Add(rig.App.ShareProgressText(clip, p)); });
        Check.False(Wait(rig.App.ShareAsync(clip, progress)));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        string all;
        lock (texts) all = string.Join(" | ", texts);
        Check.Equal(0, texts.Count, "no progress at all before the refusal: " + all);
    }

    [UnitTest]
    public static void A_copy_waiting_for_the_slot_does_not_toast_copying_when_a_game_then_refuses_it()
    {
        using var rig = new Rig();
        rig.S.ShareCopiesDuringGames = false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var holder = rig.Clip(OverSize);
        var waiter = rig.Clip(OverSize);
        var one = rig.App.ShareAsync(holder);   // announce off: no toast of its own
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the first copy to start");
        int footerCopyingBefore = rig.NoticesNow().Count(n => n == AppController.CopyingToastText);   // the holder's own
        var texts = new List<string>();
        var progress = new SyncProgress(p => { lock (texts) texts.Add(rig.App.ShareProgressText(waiter, p)); });
        var two = rig.App.ShareAsync(waiter, progress, announce: true, context: ConsentContext.ShareLast);
        ScreenshotUploadTests.Pump(() => rig.App.ShareProgressText(waiter, 0) == ShareFitModel.WaitingText, "the second clip to wait", 10_000);
        rig.Game = true;   // in front by the time the slot frees
        gate.SetResult();
        Check.True(Wait(one));
        Check.False(Wait(two));   // (LastShareOutcome / LastNotice are shared with the holder's upload, which finishes in any order: not read here)
        string gameText = ShareFitModel.GameRefusalText(ShareFitModel.ForCopium(false));
        Check.Equal(gameText, rig.App.ShareRefusal);
        Check.True(rig.NoticesNow().Contains(gameText), "the footer said it: " + string.Join(" | ", rig.NoticesNow()));
        string[] seen;
        lock (texts) seen = texts.ToArray();
        Check.True(seen.Contains(ShareFitModel.WaitingText), "the waiting text was shown: " + string.Join(" | ", seen));
        Check.False(seen.Any(s => s.StartsWith("Making a smaller copy", StringComparison.Ordinal)), "never the copying status: " + string.Join(" | ", seen));
        Check.Equal(0, rig.ToastsNow().Length, "no copying toast");
        Check.Equal(footerCopyingBefore, rig.NoticesNow().Count(n => n == AppController.CopyingToastText), "and no second copying footer");
        Check.Equal(1, rig.Transcoder.Transcodes, "only the holder's copy was made");
    }

    [UnitTest]
    public static void A_second_ask_joining_an_upload_whose_copy_stays_too_big_gets_the_same_refusal()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.TranscodeSize = _ => OverSize;
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip(OverSize);
        var first = rig.App.ShareAsync(clip);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the copy to start");
        var second = rig.App.ShareAsync(clip, announce: true, context: ConsentContext.ShareLast);
        ScreenshotUploadTests.Pump(() => rig.App.LastNotice.StartsWith("Already"), "the second ask to join", 5000);
        Check.True(rig.ToastsNow().Contains("Already uploading to Copium…"), "the joiner's toast goes through the sink: " + string.Join(" | ", rig.ToastsNow()));
        gate.SetResult();
        Check.False(Wait(first));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        string refusal = rig.App.ShareRefusal!;
        Check.False(Wait(second));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome, "the joiner got the copy's outcome");
        Check.Equal(refusal, rig.App.ShareRefusal, "and its text");
        Check.True(refusal.StartsWith(ShareFitModel.FailedText(ShareFitModel.ForCopium(false), ShareFitModel.StayedTooBigReason, OverSize), StringComparison.Ordinal), refusal);
        Check.Equal(0, rig.Http.Requests);
    }

    [UnitTest]
    public static void A_copy_that_comes_out_over_the_size_or_the_length_is_not_uploaded()
    {
        using var rig = new Rig();
        var t = ShareFitModel.ForCopium(false);
        // the transcoder says it fits, but the file the cache keeps is over the limit
        rig.Transcoder.TranscodeSize = _ => OverSize;
        rig.Transcoder.ReportedSize = _ => CopySize;
        var big = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(big)));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Equal(ShareFitModel.FailedText(t, ShareFitModel.StayedTooBigReason, OverSize), rig.App.ShareRefusal);
        Check.Equal(rig.App.ShareRefusal, rig.App.LastNotice);
        Check.False(rig.Cache.IsInUse(rig.Key(big)), "the lease is released");

        // a copy that is the right size but longer than the length limit
        rig.Transcoder.TranscodeSize = _ => CopySize;
        rig.Transcoder.ReportedSize = null;
        rig.Transcoder.CopyDuration = 700;
        var longCopy = rig.Clip(OverSize);
        Check.False(Wait(rig.App.ShareAsync(longCopy)));
        Check.Equal(ShareOutcome.Refused, rig.App.LastShareOutcome);
        Check.Equal(AppController.ShareLengthRefusalFor(700, plus: false), rig.App.ShareRefusal);
        Check.False(rig.Cache.IsInUse(rig.Key(longCopy)), "the lease is released");
        Check.Equal(0, rig.Http.Requests, "nothing went up");
        Check.Null(big.CopiumLink);
        Check.Null(longCopy.CopiumLink);
    }

    [UnitTest]
    public static void A_copy_waiting_for_the_slot_shows_the_waiting_text_until_it_starts()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transcoder.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var first = rig.Clip(OverSize);
        var second = rig.Clip(OverSize);
        var one = rig.App.ShareAsync(first);
        ScreenshotUploadTests.Pump(() => rig.Transcoder.Writing.Task.IsCompleted, "the first copy to start");
        var two = rig.App.ShareAsync(second);
        ScreenshotUploadTests.Pump(() => rig.App.ShareProgressText(second, 0.2) == ShareFitModel.WaitingText, "the second clip to wait", 10_000);
        Check.Equal(ShareFitModel.WaitingText, rig.App.ShareProgressText(second, 0.2));
        Check.Equal(ShareFitModel.WorkingText(ShareFitModel.ForCopium(false), null, 0.2), rig.App.ShareProgressText(first, 0.2), "the one holding the slot is working");
        gate.SetResult();
        Check.True(Wait(one));
        Check.True(Wait(two));
        Check.Equal(2, rig.Transcoder.Transcodes);
        Check.Equal($"Uploading… {0.2:P0}", rig.App.ShareProgressText(second, 0.2), "no longer waiting");
    }

    [UnitTest]
    public static void A_stale_entry_size_under_the_limit_uploads_the_over_size_original_as_it_is()
    {
        // Today's behaviour, not a promise: the check trusts entry.Size, so a clip whose file grew (or whose size was never right)
        // goes up as it is and Copium is left to refuse it. No probe, no copy.
        using var rig = new Rig();
        rig.Http.ReadBody = false;
        var clip = rig.Clip(OverSize);
        clip.Size = 5000;
        Check.True(Wait(rig.App.ShareAsync(clip)));
        Check.Equal((0, 0, 1), (rig.Transcoder.Probes, rig.Transcoder.Transcodes, rig.Http.Requests));
        Check.Equal(ShareOutcome.Uploaded, rig.App.LastShareOutcome);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static string? FindSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Clink.csproj"))) return dir.FullName;
        return null;
    }
}
