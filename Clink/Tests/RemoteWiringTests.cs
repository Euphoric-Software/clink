using System.IO;
using System.Net.Http;
using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// How AppController feeds the remote status pipe: the snapshot, the event codes, the share progress, and that the upload's progress
/// still reaches every caller. No engine, no pipe server, nothing on the screen (notices go to the controller's probe, toasts are off).
/// </summary>
internal static class RemoteWiringTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void Pump(Func<bool> done, string what) => ScreenshotUploadTests.Pump(done, what);

    /// <summary>A controller whose notices and pipe events land in <paramref name="log"/> ("notice:Title", "event:{json}") instead of anywhere visible.</summary>
    private static AppController App(Settings settings, List<string> log)
    {
        settings.ToastShared.Enabled = false;
        settings.ToastRecording.Enabled = false;
        settings.ClickSound = false;
        var app = new AppController(settings, new Updater(settings));
        app.ProblemNoticeProbe = (title, _) => { lock (log) log.Add("notice:" + title); };
        app.RemoteEventProbe = line => { lock (log) log.Add("event:" + line); };
        app.ClipboardOverride = _ => { };
        app.ShareWindowOpener = _ => true;
        app.GameOrFullScreenProbe = () => false;
        return app;
    }

    private static string[] ShareDone(List<string> log)
    {
        lock (log)
            return log.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).Select(l => J(l["event:".Length..]))
                .Where(j => j.GetProperty("t").GetString() == "shareDone").Select(j => j.GetProperty("outcome").GetString()!).ToArray();
    }

    private static ClipEntry Newest(AppController app, TempDir dir, string name, string? link = null)
    {
        string path = dir.File(name);
        File.WriteAllBytes(path, new byte[64]);
        var entry = app.Library.Add(path, 2, 64, null, Array.Empty<string>());
        entry.CopiumLink = link;
        return entry;
    }

    private sealed class Recorder : IProgress<double>
    {
        public readonly List<double> Values = new();
        public void Report(double value) { lock (Values) Values.Add(value); }
        public double[] All { get { lock (Values) return Values.ToArray(); } }
    }

    // ------------------------------------------------------------------ the snapshot

    [UnitTest]
    public static void Remote_the_snapshot_says_clipsOff_whatever_the_engine_does()
    {
        foreach (RecordingState s in Enum.GetValues<RecordingState>())
            Check.Equal("clipsOff", AppController.RemoteStateCode(false, s), s.ToString());
        Check.Equal("recording", AppController.RemoteStateCode(true, RecordingState.Recording));
        Check.Equal("waitingForGame", AppController.RemoteStateCode(true, RecordingState.WaitingForGame));
        Check.Equal("paused", AppController.RemoteStateCode(true, RecordingState.Paused));
        Check.Equal("error", AppController.RemoteStateCode(true, RecordingState.Error));
        Check.Equal("starting", AppController.RemoteStateCode(true, RecordingState.Starting));

        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        settings.ClipsEnabled = false;
        var log = new List<string>();
        using var app = App(settings, log);
        app.WantRecordingForTests = true;   // a recording wish, which Clips off still wins over
        Check.Equal("clipsOff", app.BuildRemoteSnapshot().State);
        settings.ClipsEnabled = true;
        Check.Equal(AppController.RemoteStateCode(true, app.State), app.BuildRemoteSnapshot().State);
        Check.NotEqual("clipsOff", app.BuildRemoteSnapshot().State);
    }

    [UnitTest]
    public static void Remote_the_snapshot_maps_resuming_queued_saving_and_the_buffer_length()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        settings.BufferSeconds = 48;
        var log = new List<string>();
        using var app = App(settings, log);
        var s = app.BuildRemoteSnapshot();
        Check.False(s.Rec.On);
        Check.False(s.Rec.Resuming);
        Check.False(s.Rec.Queued);
        Check.False(s.Saving);
        Check.Equal(48, s.BufferS, "before the engine first runs: the replay length setting, not EngineConfig's 120");
        settings.BufferSeconds = 1;
        Check.Equal(5, app.BuildRemoteSnapshot().BufferS, "clamped as the engine would be");

        app.WantRecordingForTests = true;   // wanted, the recorder not recording: between two parts
        s = app.BuildRemoteSnapshot();
        Check.True(s.Rec.On);
        Check.True(s.Rec.Resuming);
        app.WantRecordingForTests = false;
        app.QueueRecordingForTests();
        Check.True(app.BuildRemoteSnapshot().Rec.Queued);

        var save = new TaskCompletionSource<ClipEntry?>();
        app.BeginSaveForTests(save.Task);
        Check.True(app.BuildRemoteSnapshot().Saving);
        app.EndSaveForTests();
        Check.False(app.BuildRemoteSnapshot().Saving);
        Check.Equal(new RemoteUpload(false, 0), app.BuildRemoteSnapshot().Upload);
    }

    [UnitTest]
    public static void Remote_upload_percent_is_in_steps_of_5_and_0_without_an_upload()
    {
        Check.Equal(45, AppController.RemoteUploadPercent(true, 47));
        Check.Equal(100, AppController.RemoteUploadPercent(true, 100));
        Check.Equal(0, AppController.RemoteUploadPercent(true, 4));
        Check.Equal(0, AppController.RemoteUploadPercent(false, 80));
        Check.Equal(0, AppController.RemoteUploadPercent(true, int.MinValue));
        Check.Equal(100, AppController.RemoteUploadPercent(true, 1000));
    }

    /// <summary>Reads the whole upload (so its progress reports run), then hangs until cancelled.</summary>
    private sealed class ReadThenHang : HttpMessageHandler
    {
        public readonly TaskCompletionSource Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            Read.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [UnitTest]
    public static void Remote_a_running_upload_is_in_the_snapshot_and_its_caller_still_gets_the_progress()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        settings.CopiumToken = "eup_token";
        settings.CopiumConsent = true;
        var log = new List<string>();
        using var app = App(settings, log);
        var handler = new ReadThenHang();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            var entry = Newest(app, dir, "clip.mp4");
            var caller = new Recorder();
            var upload = app.ShareAsync(entry, caller);
            Pump(() => handler.Read.Task.IsCompleted, "the upload to be read");
            var s = app.BuildRemoteSnapshot();
            Check.True(s.Upload.On, "uploading");
            Check.Equal(100, s.Upload.P, "the whole (small) file went up");
            Check.True(caller.All.Length > 0, "the caller's own progress got the reports");
            Check.Equal(1.0, caller.All[^1]);
            Check.False(app.SetCloudSharing(false));   // the kill switch ends it
            Pump(() => upload.IsCompleted, "the upload to end");
            Check.Equal(new RemoteUpload(false, 0), app.BuildRemoteSnapshot().Upload, "nothing going up any more");
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    [UnitTest]
    public static void Remote_the_upload_tee_forwards_every_report_unchanged_and_in_order()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        var log = new List<string>();
        using var app = App(settings, log);
        var caller = new Recorder();
        var tee = new AppController.RemoteUploadTee(app, caller);
        double[] sent = { 0, 0.004, 0.5, 0.5, 0.47, 0.999, 1.0, 1.0, double.NaN, 2.0, -1 };
        foreach (double v in sent) tee.Report(v);
        Check.Equal(sent, caller.All, "every report, the same values, in order (none dropped, none rounded)");
        var bare = new AppController.RemoteUploadTee(app, null);
        bare.Report(0.5);   // no caller progress: fine
    }

    // ------------------------------------------------------------------ the events

    [UnitTest]
    public static void Remote_share_progress_is_at_most_4_a_second_and_at_least_2_points_apart()
    {
        long now = 0;
        var sent = new List<(long At, int P)>();
        var progress = new AppController.RemoteShareProgress(p => sent.Add((now, p)), () => now);
        void At(long t, double v) { now = t; progress.Report(v); }
        At(0, 0.0);
        At(100, 0.10);    // too soon
        At(300, 0.11);
        At(600, 0.12);    // one point only
        At(900, 0.13);
        At(1000, 1.0);    // too soon
        At(1200, 1.0);
        Check.Equal(new[] { 0, 11, 13, 100 }, sent.Select(x => x.P).ToArray());

        // a fast stream: every 2 ms for 2 s
        sent.Clear();
        progress = new AppController.RemoteShareProgress(p => sent.Add((now, p)), () => now);
        for (int i = 0; i <= 1000; i++) At(10_000 + i * 2, i / 1000.0);
        Check.True(sent.Count is > 0 and <= 9, "at most 4 a second: " + sent.Count);
        for (int i = 1; i < sent.Count; i++)
        {
            Check.True(sent[i].At - sent[i - 1].At >= 250, "250 ms apart");
            Check.True(sent[i].P - sent[i - 1].P >= 2, "2 points apart");
        }
    }

    [UnitTest]
    public static void Remote_share_outcomes_and_clip_not_saved_reasons_are_the_fixed_codes()
    {
        var expected = new Dictionary<ShareOutcome, string>
        {
            [ShareOutcome.LinkCopied] = "linkCopied",
            [ShareOutcome.Uploaded] = "uploaded",
            [ShareOutcome.Refused] = "refused",
            [ShareOutcome.Declined] = "declined",
            [ShareOutcome.FileShareOpened] = "fileShareOpened",
            [ShareOutcome.Failed] = "failed",
            [ShareOutcome.WindowFailed] = "failed",
            [ShareOutcome.None] = "failed",
        };
        foreach (ShareOutcome o in Enum.GetValues<ShareOutcome>())
        {
            Check.True(expected.ContainsKey(o), "a code for " + o);
            string code = AppController.RemoteOutcome(o);
            Check.Equal(expected[o], code, o.ToString());
            Check.Equal(code, J(RemoteProtocol.ShareDone(code)).GetProperty("outcome").GetString(), "the protocol sends it as it is");
        }

        // restarting, paused, waiting for a game, error (not while starting), otherwise not ready: in that order
        Check.Equal("restarting", AppController.ClipNotSavedReason(true, true, true, true, false));
        Check.Equal("paused", AppController.ClipNotSavedReason(false, true, true, true, false));
        Check.Equal("waitingForGame", AppController.ClipNotSavedReason(false, false, true, true, false));
        Check.Equal("error", AppController.ClipNotSavedReason(false, false, false, true, false));
        Check.Equal("notReady", AppController.ClipNotSavedReason(false, false, false, true, true), "an error while it is starting again");
        Check.Equal("notReady", AppController.ClipNotSavedReason(false, false, false, false, false));
        foreach (string r in new[] { "restarting", "paused", "waitingForGame", "error", "notReady" })
            Check.Equal(r, J(RemoteProtocol.ClipNotSaved(r)).GetProperty("reason").GetString(), r);
    }

    [UnitTest]
    public static void Remote_clipNotSaved_comes_after_the_users_notice_and_a_throwing_handler_stays_out_of_ClipNow()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        settings.ClipsEnabled = false;
        var log = new List<string>();
        using var app = App(settings, log);
        app.ClipNotSaved += reason => { lock (log) log.Add("clipNotSaved:" + reason); };
        app.ClipNotSaved += _ => throw new InvalidOperationException("a broken handler");
        app.ClipNow();
        Check.Equal(new[] { "notice:Clips are off", "clipNotSaved:clipsOff" }, log.ToArray());

        log.Clear();
        settings.ClipsEnabled = true;   // no engine runs in a unit test: the press is turned down
        app.ClipNow();
        Check.Equal(2, log.Count, string.Join(" | ", log));
        Check.Equal("notice:Clip not saved", log[0]);
        Check.True(log[1] is "clipNotSaved:waitingForGame" or "clipNotSaved:notReady", log[1]);
    }

    [UnitTest]
    public static void Remote_shareDone_is_sent_once_on_every_way_ShareLast_ends()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var handler = new OfflineHandler();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            void Case(string what, string expected, Action<Settings> setup, Action<AppController, List<string>>? arrange = null, Func<AppController, bool>? until = null)
            {
                var settings = Settings.FromJson("{}");
                setup(settings);
                var log = new List<string>();
                using var app = App(settings, log);
                arrange?.Invoke(app, log);
                app.ShareLast();
                if (until != null) Pump(() => until(app), what);
                Pump(() => ShareDone(log).Length > 0, what + ": its shareDone");
                Check.Equal(new[] { expected }, ShareDone(log), what);
                lock (log)
                {
                    int done = log.FindIndex(l => l.Contains("\"shareDone\"", StringComparison.Ordinal));
                    int notice = log.FindIndex(l => l.StartsWith("notice:", StringComparison.Ordinal));
                    if (notice >= 0) Check.True(notice < done, what + ": the user's notice came first");
                }
            }

            Case("Clips off", "declined", s => s.ClipsEnabled = false);
            Case("cloud sharing off", "declined", s => s.CloudSharingEnabled = false);
            Case("no clips yet", "noClip", _ => { });
            Case("the clip just pressed was turned down", "noClip", _ => { }, (app, log) => { app.ClipNow(); lock (log) log.Clear(); });
            var failedSave = new TaskCompletionSource<ClipEntry?>();
            Case("the clip being saved failed", "noClip", _ => { }, (app, _) =>
            {
                app.BeginSaveForTests(failedSave.Task);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => { app.EndSaveForTests(); failedSave.SetResult(null); });   // runs once ShareLast waits for it
            });
            Case("a link to copy", "linkCopied", _ => { }, (app, _) => Newest(app, dir, "linked.mp4", "https://v.copium.pics/a.mp4"));
            Case("no token: the share window", "fileShareOpened", _ => { }, (app, _) => Newest(app, dir, "local.mp4"));
            Case("Not now", "declined", s => s.CopiumToken = "eup_token", (app, _) =>
            {
                app.ConsentPromptOverride = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow));
                Newest(app, dir, "asked.mp4");
            });
            Case("the upload failed", "failed", s => { s.CopiumToken = "eup_token"; s.CopiumConsent = true; }, (app, _) => Newest(app, dir, "offline.mp4"));
            Check.Equal(1, handler.Requests, "only the failed upload reached the (fake) network");
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("no route");
        }
    }
}
