using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Windows.Win32.Foundation;
using Clink.Engine;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// The share-copy job (<c>AppController.MakeShareCopyAsync</c>, <c>FindShareCopyAsync</c>), with a fake transcoder and a cache in a temp folder: no GPU, no Media
/// Foundation. The clips are files of the right size (zeros); the fake probe says what they are. The limit is 2 MB and the clips last 5 s,
/// so a 1080p60 clip plans to 720p60 at 2.8 Mbps. One test at the end runs the real thing and needs CLINK_TESTS_GPU=1.
/// </summary>
internal static class ShareCopyJobTests
{
    // ------------------------------------------------------------------ the fake and the rig

    private sealed class Fake : IShareTranscoder
    {
        public Func<string, ShareProbeResult> ProbeFor = _ => new ShareProbeResult(5, "h264", 1920, 1080, 60, 1, 1.0);
        public Func<int, long> TranscodeSize = _ => 1_800_000;
        public long RemuxSize = 1_800_000;
        /// <summary>Awaited once the output file is written (the job is then "in the middle of the transcode").</summary>
        public Func<string, CancellationToken, Task>? Hold;
        public Action<string>? OnProbe;
        public int CleanupDelayMs;
        public int Transcodes, Remuxes, Probes, Concurrent, MaxConcurrent;
        public volatile bool SawCancel, CleanedUp;
        public readonly List<ShareTranscodeRequest> Requests = new();
        public readonly List<string> Outputs = new();

        public ShareProbeResult Probe(string path, double keyframeSeconds)
        {
            Interlocked.Increment(ref Probes);
            OnProbe?.Invoke(path);
            return ProbeFor(path) with { KeyframeSeconds = ShareProbe.ClampKeyframe(keyframeSeconds) };
        }

        public Task<ShareTranscodeResult> TranscodeAsync(string sourcePath, string outputPath, ShareTranscodeRequest request, IProgress<double>? progress, CancellationToken ct)
        {
            int n;
            lock (Requests) { Requests.Add(request); n = ++Transcodes; Outputs.Add(outputPath); }
            return Run(sourcePath, outputPath, TranscodeSize(n), request.Width, request.Height, request.Fps, progress, ct);
        }

        public Task<ShareTranscodeResult> RemuxMixOnlyAsync(string sourcePath, string outputPath, IProgress<double>? progress, CancellationToken ct)
        {
            lock (Requests) { Remuxes++; Outputs.Add(outputPath); }
            var p = ProbeFor(sourcePath);
            return Run(sourcePath, outputPath, RemuxSize, p.Width, p.Height, (int)Math.Round(p.Fps), progress, ct);
        }

        private async Task<ShareTranscodeResult> Run(string src, string dst, long size, int w, int h, int fps, IProgress<double>? progress, CancellationToken ct)
        {
            int c = Interlocked.Increment(ref Concurrent);
            lock (Requests) MaxConcurrent = Math.Max(MaxConcurrent, c);
            try
            {
                using (var fs = File.Create(dst)) fs.SetLength(size);   // the real transcoder writes its output here (and is not expected to leave it on a cancel; the fake does, to see the job clean up)
                progress?.Report(0.5);
                if (Hold != null) await Hold(src, ct);
                ct.ThrowIfCancellationRequested();
                progress?.Report(1.0);
                return new ShareTranscodeResult(dst, size, TimeSpan.FromSeconds(5), 300, w, h, fps, new[] { "Mix" }, "Fake") { MeasuredFps = fps };
            }
            catch (OperationCanceledException)
            {
                SawCancel = true;
                if (CleanupDelayMs > 0) await Task.Delay(CleanupDelayMs);
                CleanedUp = true;
                throw;
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }
    }

    private sealed class Sink : IProgress<ShareCopyProgress>
    {
        private readonly List<ShareCopyProgress> _seen = new();
        private int _calls;
        /// <summary>Runs before a report is recorded, with the number of the call (1 for the first): a test can hold a delivery there.</summary>
        public Action<int>? Before;
        public void Report(ShareCopyProgress value)
        {
            Before?.Invoke(Interlocked.Increment(ref _calls));
            lock (_seen) _seen.Add(value);
        }
        public ShareCopyProgress[] Seen { get { lock (_seen) return _seen.ToArray(); } }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Fake Fake = new();
        public readonly Settings S;
        public readonly ShareCache Cache;
        public readonly AppController App;
        public readonly ShareFitTarget Target = new(2_000_000, 0, "Discord test (2 MB)");

        public Rig(Func<bool>? elevated = null)
        {
            Settings.DirOverride = Dir.Path;
            S = Settings.FromJson("{}");
            S.ClipsFolder = Dir.File("clips");
            Cache = new ShareCache(Dir.File("cache"), isElevated: elevated ?? (() => false));
            App = new AppController(S, new Updater(S))
            {
                ShareCacheOverride = Cache,
                ShareTranscoderImpl = Fake,
                FreeSpaceProbe = _ => long.MaxValue,
                AdapterLuidOverride = () => null,
                GameOrFullScreenProbe = () => false,
            };
        }

        public ClipEntry Clip(string name, long size, double seconds = 5)
        {
            Directory.CreateDirectory(Dir.File("clips"));
            string path = Path.Combine(Dir.File("clips"), name);
            using (var fs = File.Create(path)) fs.SetLength(size);
            return new ClipEntry { Path = path, Size = size, Duration = seconds };
        }

        public string Key(ClipEntry e) => ShareCache.KeyFor(e.Path, Target.LimitBytes, "h264", "fit-v1");
        public string Part(ClipEntry e) => Cache.PartPathFor(Key(e), Path.GetFileName(e.Path));
        public string Final(ClipEntry e) => Cache.PathFor(Key(e), Path.GetFileName(e.Path));

        /// <summary>Asks from a pool thread (no synchronization context to come back to), as the UI's await would from its own.</summary>
        public Task<ShareCopyResult> Ask(ClipEntry e, IProgress<ShareCopyProgress>? progress = null, CancellationToken ct = default)
            => Task.Run(() => App.MakeShareCopyAsync(e, Target, progress, ct));

        public void Dispose()
        {
            try { App.Dispose(); } catch { }
            Cache.Dispose();
            Dir.Dispose();
        }
    }

    private static T Wait<T>(Task<T> task, string what = "the copy")
    {
        if (!task.Wait(20000)) Check.Fail("timed out waiting for " + what);
        return task.Result;
    }

    private static void Spin(Func<bool> done, string what, int limitMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > limitMs) Check.Fail("timed out waiting for " + what);
            Thread.Sleep(2);
        }
    }

    // ------------------------------------------------------------------ the job

    [UnitTest]
    public static void A_clip_within_the_limit_is_shared_as_it_is_without_any_work()
    {
        using var rig = new Rig();
        var clip = rig.Clip("small.mp4", 1_500_000);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseOriginal, r.Outcome);
        Check.True(r.UsedOriginal);
        Check.Null(r.Error, "it fits: no reason");
        Check.Equal(clip.Path, r.Path);
        Check.Equal(1_500_000L, r.Bytes);
        Check.Null(r.Lease);
        Check.Equal((0, 0, 0), (rig.Fake.Probes, rig.Fake.Transcodes, rig.Fake.Remuxes), "nothing was looked at or made");
        Check.Equal(ShareFitStateKind.Fits, ShareFitModel.StateFor(r).Kind);
        Check.False(Directory.Exists(rig.Cache.Root), "and the cache was not touched");

        using var exact = Wait(rig.Ask(rig.Clip("exact.mp4", 2_000_000)));
        Check.True(exact.UsedOriginal, "exactly the limit fits");
    }

    [UnitTest]
    public static void A_clip_that_is_not_there_fails_with_the_file_missing_kind()
    {
        using var rig = new Rig();
        var gone = new ClipEntry { Path = rig.Dir.File("clips\\gone.mp4"), Size = 5_000_000, Duration = 5 };
        using var r = Wait(rig.Ask(gone));
        Check.Equal(ShareCopyOutcome.Failed, r.Outcome);
        Check.Equal(ShareCopyError.FileMissing, r.ErrorKind);
        Check.Equal("the clip's file is not there any more", r.Error);
        Check.Equal(0, rig.Fake.Probes);
    }

    [UnitTest]
    public static void An_over_limit_clip_is_re_encoded_cached_and_found_again()
    {
        using var rig = new Rig();
        var clip = rig.Clip("big clip.mp4", 6_000_000);
        var sink = new Sink();
        string key = rig.Key(clip);
        var r = Wait(rig.Ask(clip, sink));
        Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
        Check.Equal(rig.Final(clip), r.Path);
        Check.True(r.Path.EndsWith("big clip.mp4", StringComparison.OrdinalIgnoreCase), "the original name, so Discord shows it");
        Check.True(File.Exists(r.Path) && !File.Exists(rig.Part(clip)), "the part file became the copy");
        Check.Equal(1_800_000L, r.Bytes);
        Check.Equal(6_000_000L, r.OriginalBytes);
        Check.Equal("720p60", r.Plan!.Label);
        Check.Equal("720p60", r.Label);
        Check.Near(5, r.Seconds, 0.001);
        Check.False(r.Reused);
        Check.True(r.Lease != null && rig.Cache.IsInUse(key), "the copy is in use while the result lives");
        Check.Equal(ShareFitStateKind.Done, ShareFitModel.StateFor(r).Kind);
        Check.Equal((1, 0, 1), (rig.Fake.Transcodes, rig.Fake.Remuxes, rig.Fake.Probes));
        var req = rig.Fake.Requests.Single();
        Check.Equal((1280, 720, 60), (req.Width, req.Height, req.Fps));
        Check.True(req.VideoBitrateBps is > 2_700_000 and <= 2_800_000, $"{req.VideoBitrateBps} bps");
        Check.Null(req.Adapter, "no adapter known: the transcoder takes the first");
        Check.Equal(new[] { rig.Part(clip) }, rig.Fake.Outputs.Distinct().ToArray(), "written only to the path BeginCopy gave");
        var seen = sink.Seen;
        Check.True(seen.Length >= 2 && seen.All(p => p.Stage == ShareCopyStage.Working), "progress is reported");
        Check.True(seen.Any(p => p.Plan?.Label == "720p60"), "with the plan");
        r.Dispose();
        r.Dispose();
        Check.False(rig.Cache.IsInUse(key), "disposing releases the mark (twice is fine)");

        // The next ask finds the copy: no probe, no transcode.
        rig.Fake.ProbeFor = p => p.StartsWith(rig.Cache.Root, StringComparison.OrdinalIgnoreCase) ? new ShareProbeResult(5, "h264", 1280, 720, 60, 1, 1.0) : new ShareProbeResult(5, "h264", 1920, 1080, 60, 1, 1.0);
        using var again = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseCopy, again.Outcome);
        Check.True(again.Reused);
        Check.Equal(r.Path, again.Path);
        Check.Equal("720p60", again.Label, "the label is read from the copy");
        Check.Equal(1, rig.Fake.Transcodes, "no second transcode");

        // Another clip, another copy; changing the original makes a new key.
        File.AppendAllText(clip.Path, "x");
        clip.Size++;
        using var changed = Wait(rig.Ask(clip));
        Check.False(changed.Reused, "a changed original is not the same copy");
        Check.Equal(2, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void A_stream_copy_that_fits_is_the_copy_and_one_that_is_still_over_is_re_encoded()
    {
        using var rig = new Rig();
        // 5 audio tracks: keeping only the Mix should bring 2.3 MB to about 1.8 MB.
        rig.Fake.ProbeFor = _ => new ShareProbeResult(5, "h264", 1920, 1080, 60, 5, 1.0);
        var fits = rig.Clip("fits.mp4", 2_300_000);
        rig.Fake.RemuxSize = 1_700_000;
        using (var r = Wait(rig.Ask(fits)))
        {
            Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
            Check.Equal((1, 0), (rig.Fake.Remuxes, rig.Fake.Transcodes), "only the stream copy");
            Check.Null(r.Plan, "no plan for a stream copy");
            Check.Equal("1080p60", r.Label, "the source's own size and rate");
            Check.Equal(1_700_000L, r.Bytes);
        }

        var over = rig.Clip("over.mp4", 2_300_000);
        rig.Fake.RemuxSize = 2_100_000;   // still over
        using (var r = Wait(rig.Ask(over)))
        {
            Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
            Check.Equal((2, 1), (rig.Fake.Remuxes, rig.Fake.Transcodes), "the stream copy came out over the limit, so it was re-encoded");
            Check.Equal("720p60", r.Plan!.Label);
            Check.Equal(1, r.Plan.Attempt, "the first plan of the re-encode (the remux does not count)");
            Check.True(File.Exists(r.Path) && new FileInfo(r.Path).Length == 1_800_000, "the re-encode replaced the stream copy");
            Check.True(rig.Fake.Outputs.Skip(1).All(o => o == rig.Part(over)), "both wrote the same part path");
        }
    }

    [UnitTest]
    public static void A_copy_that_comes_out_too_big_is_planned_again_at_a_lower_bitrate()
    {
        using var rig = new Rig();
        rig.Fake.TranscodeSize = n => n == 1 ? 3_000_000 : 1_900_000;
        var clip = rig.Clip("retry.mp4", 6_000_000);
        var sink = new Sink();
        using var r = Wait(rig.Ask(clip, sink));
        Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
        Check.Equal(2, rig.Fake.Transcodes, "two attempts");
        Check.Equal(2, r.Plan!.Attempt);
        var a = rig.Fake.Requests;
        Check.True(a[1].VideoBitrateBps < a[0].VideoBitrateBps, $"{a[1].VideoBitrateBps} < {a[0].VideoBitrateBps}");
        Check.Equal(1_900_000L, r.Bytes);
        Check.True(sink.Seen.Select(p => p.Plan?.Attempt).Where(x => x != null).Distinct().Count() == 2, "progress shows both plans");
        Check.False(File.Exists(rig.Part(clip)), "no part file left");
    }

    [UnitTest]
    public static void A_copy_that_stays_too_big_fails_it_is_not_a_cant_fit()
    {
        using var rig = new Rig();
        rig.Fake.TranscodeSize = _ => 3_000_000;
        var clip = rig.Clip("stubborn.mp4", 6_000_000);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.Failed, r.Outcome);
        Check.Equal(ShareCopyError.StayedTooBig, r.ErrorKind);
        Check.Equal(ShareFitModel.StayedTooBigReason, r.Error);
        Check.Equal(ShareFit.MaxAttempts, rig.Fake.Transcodes, "three attempts, then it stops");
        Check.False(File.Exists(rig.Part(clip)) || File.Exists(rig.Final(clip)), "no copy is kept");
        Check.Equal(clip.Path, r.Path, "the original is named, not shared");
        var state = ShareFitModel.StateFor(r);
        Check.Equal(ShareFitStateKind.Failed, state.Kind);
        Check.True(ShareFitModel.Text(state, rig.Target, 6_000_000, 5)!.StartsWith("Couldn't make a smaller copy: the copy stayed too big. The original is 6 MB, over Discord test (2 MB)."));
        Check.False(rig.Cache.IsInUse(rig.Key(clip)), "no mark left");
    }

    [UnitTest]
    public static void A_clip_too_long_for_the_lowest_quality_cant_fit_and_the_text_says_where_to_trim()
    {
        using var rig = new Rig();
        rig.Fake.ProbeFor = _ => new ShareProbeResult(600, "h264", 1920, 1080, 60, 1, 1.0);
        var clip = rig.Clip("long.mp4", 6_000_000, seconds: 0);   // the library does not know the length (a scanned clip): the probe does
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.CantFit, r.Outcome);
        int expected = ShareFit.MaxSecondsFor(1920, 1080, 60, 2_000_000, "h264", Capabilities.AudioKbps, 1.0);
        Check.Equal(expected, r.MaxSeconds);
        Check.True(r.MaxSeconds > 0 && r.MaxSeconds < 60, $"{r.MaxSeconds} s");
        Check.Equal(0, rig.Fake.Transcodes);
        var text = ShareFitModel.Text(ShareFitModel.StateFor(r), rig.Target, 6_000_000, 600)!;
        Check.Equal($"A 10:00 clip can't fit in 2 MB. Trim it to about {Capabilities.FormatClipLength(expected)}.", text);
        Check.False(Directory.Exists(rig.Cache.Root) && Directory.GetFiles(rig.Cache.Root, "*", SearchOption.AllDirectories).Length > 0, "nothing in the cache");
    }

    [UnitTest]
    public static void The_clips_length_comes_from_the_probe_and_the_keyframe_interval_from_the_setting()
    {
        using var rig = new Rig();
        rig.S.KeyframeSeconds = 3f;
        double? keyframe = null;
        rig.Fake.OnProbe = _ => keyframe = rig.S.KeyframeSeconds;
        // The library says 600 s (wrong); the header says 5 s: the header decides, so the clip is planned (not "can't fit").
        var clip = rig.Clip("scanned.mp4", 6_000_000, seconds: 600);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
        Check.Near(5, r.Plan!.DurationSeconds, 0.001);
        Check.Near(3, r.Plan.SourceKeyframeSeconds, 0.001, "the setting's keyframe interval");
        Check.Equal(3.0, keyframe);
    }

    [UnitTest]
    public static void Cancelling_stops_the_job_and_deletes_the_part()
    {
        using var rig = new Rig();
        var clip = rig.Clip("cancel.mp4", 6_000_000);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        using var cts = new CancellationTokenSource();
        string key = rig.Key(clip);
        var ask = rig.Ask(clip, null, cts.Token);
        Spin(() => rig.Fake.Transcodes == 1 && File.Exists(rig.Part(clip)), "the transcode to be under way");
        cts.Cancel();
        using var r = Wait(ask);
        Check.Equal(ShareCopyOutcome.Cancelled, r.Outcome);
        Check.True(rig.Fake.SawCancel, "the transcoder saw the cancel");
        Check.False(File.Exists(rig.Part(clip)), "the part is deleted");
        Check.False(File.Exists(rig.Final(clip)));
        Check.False(rig.Cache.IsInUse(key), "and the marks are released");
        Check.Equal(ShareFitStateKind.Over, ShareFitModel.StateFor(r).Kind);

        // A new ask starts a new job.
        gate.SetResult();
        using var again = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseCopy, again.Outcome, again.Error);
        Check.Equal(2, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void Too_little_disk_space_fails_before_anything_is_made()
    {
        using var rig = new Rig();
        long asked = 0;
        rig.App.FreeSpaceProbe = path => { asked++; return ShareFitModel.RequiredFreeBytes(2_000_000) - 1; };
        var clip = rig.Clip("disk.mp4", 6_000_000);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.Failed, r.Outcome);
        Check.Equal(ShareCopyError.LowDisk, r.ErrorKind);
        Check.Equal(ShareFitModel.LowDiskReason(2_000_000), r.Error);
        Check.True(r.Error!.Contains("not enough free disk space"));
        Check.Equal((0, 0), (rig.Fake.Probes, rig.Fake.Transcodes));
        Check.Equal(1L, asked);

        // Exactly enough is enough (2.2 x the limit + 64 MB).
        rig.App.FreeSpaceProbe = _ => ShareFitModel.RequiredFreeBytes(2_000_000);
        using var ok = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseCopy, ok.Outcome, ok.Error);
    }

    [UnitTest]
    public static void A_second_asker_joins_the_running_job_with_its_own_mark()
    {
        using var rig = new Rig();
        var clip = rig.Clip("joined.mp4", 6_000_000);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        string key = rig.Key(clip);
        var sinkA = new Sink();
        var sinkB = new Sink();
        var a = rig.Ask(clip, sinkA);
        Spin(() => rig.Fake.Transcodes == 1, "the first asker's transcode");
        var b = rig.Ask(clip, sinkB);
        Spin(() => sinkB.Seen.Length > 0, "the late asker to be told where the job is");
        gate.SetResult();
        using var ra = Wait(a);
        using var rb = Wait(b);
        Check.Equal(ShareCopyOutcome.UseCopy, ra.Outcome, ra.Error);
        Check.Equal(ShareCopyOutcome.UseCopy, rb.Outcome, rb.Error);
        Check.Equal(ra.Path, rb.Path);
        Check.Equal(1, rig.Fake.Transcodes, "one transcode for both");
        Check.True(ra.Lease != null && rb.Lease != null && !ReferenceEquals(ra.Lease, rb.Lease), "each has its own mark");
        Check.True(rig.Cache.IsInUse(key));
        ra.Dispose();
        Check.True(rig.Cache.IsInUse(key), "still in use by the other");
        rb.Dispose();
        Check.False(rig.Cache.IsInUse(key), "released once both are done");
    }

    [UnitTest]
    public static void An_asker_that_goes_away_does_not_stop_the_job()
    {
        using var rig = new Rig();
        var clip = rig.Clip("background.mp4", 6_000_000);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        string key = rig.Key(clip);
        var ask = rig.App.MakeShareCopyAsync(clip, rig.Target, new Sink(), default);   // the task itself: Abandon knows the asker by it
        Spin(() => rig.Fake.Transcodes == 1, "the transcode to be under way");
        ShareCopyResult.Abandon(ask);   // the window was closed: no cancel
        ShareCopyResult.Abandon(ask);   // twice is fine
        Thread.Sleep(100);
        Check.False(rig.Fake.SawCancel, "nothing cancelled the job");
        Check.True(rig.Cache.IsInUse(key), "the cache keeps the folder meanwhile (the job's own mark)");
        gate.SetResult();
        Spin(() => File.Exists(rig.Final(clip)) && !rig.Cache.IsInUse(key), "the job to finish in the background and the marks to go");
        Check.Equal(1, rig.Fake.Transcodes);

        // Ready the next time Share is opened.
        using var next = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseCopy, next.Outcome);
        Check.True(next.Reused, "found, not made again");
        Check.Equal(1, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void Quitting_cancels_a_running_job_and_waits_for_it_to_clean_up()
    {
        using var rig = new Rig();
        var clip = rig.Clip("quit.mp4", 6_000_000);
        rig.Fake.Hold = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        rig.Fake.CleanupDelayMs = 300;   // the transcoder takes a moment to remove its files
        var ask = rig.Ask(clip);
        Spin(() => rig.Fake.Transcodes == 1 && File.Exists(rig.Part(clip)), "the transcode to be under way");
        var sw = Stopwatch.StartNew();
        rig.App.Dispose();
        sw.Stop();
        Check.True(rig.Fake.CleanedUp, "Dispose waited until the job had cleaned up");
        Check.True(sw.ElapsedMilliseconds >= 250, $"it did wait ({sw.ElapsedMilliseconds} ms)");
        Check.False(File.Exists(rig.Part(clip)), "the unfinished copy is gone");
        using var r = Wait(ask);
        Check.Equal(ShareCopyOutcome.Cancelled, r.Outcome);
    }

    [UnitTest]
    public static void Only_one_copy_is_made_at_a_time_and_the_second_job_says_it_is_waiting()
    {
        using var rig = new Rig();
        var a = rig.Clip("first.mp4", 6_000_000);
        var b = rig.Clip("second.mp4", 6_100_000);
        var gateA = new TaskCompletionSource();
        rig.Fake.Hold = (src, ct) => src == a.Path ? gateA.Task.WaitAsync(ct) : Task.CompletedTask;
        var sinkA = new Sink();
        var sinkB = new Sink();
        var askA = rig.Ask(a, sinkA);
        Spin(() => rig.Fake.Transcodes == 1, "the first transcode");
        var askB = rig.Ask(b, sinkB);
        Spin(() => sinkB.Seen.Any(p => p.Stage == ShareCopyStage.Waiting), "the second job to say it waits");
        Thread.Sleep(100);
        Check.Equal(1, rig.Fake.Transcodes, "the second has not started");
        Check.Equal(ShareFitStateKind.Waiting, ShareFitModel.StateFor(sinkB.Seen.First(p => p.Stage == ShareCopyStage.Waiting)).Kind);
        Check.False(askB.IsCompleted);
        gateA.SetResult();
        using var ra = Wait(askA);
        using var rb = Wait(askB);
        Check.Equal(ShareCopyOutcome.UseCopy, ra.Outcome, ra.Error);
        Check.Equal(ShareCopyOutcome.UseCopy, rb.Outcome, rb.Error);
        Check.Equal(2, rig.Fake.Transcodes);
        Check.Equal(1, rig.Fake.MaxConcurrent, "never two at once");
        var seenB = sinkB.Seen;
        int waiting = Array.FindIndex(seenB, p => p.Stage == ShareCopyStage.Waiting);
        int working = Array.FindIndex(seenB, p => p.Stage == ShareCopyStage.Working);
        Check.True(waiting >= 0 && working > waiting, "Waiting first, then Working");
        Check.False(sinkA.Seen.Any(p => p.Stage == ShareCopyStage.Waiting), "the first never waited");
    }

    [UnitTest]
    public static void Running_as_administrator_shares_the_original_and_names_the_reason()
    {
        Updater.IsElevatedOverride = true;   // the existing seam: the cache's own test of elevation follows it
        using var rig = new Rig(elevated: ShareCache.ProcessIsElevated);
        var clip = rig.Clip("admin.mp4", 6_000_000);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseOriginal, r.Outcome);
        Check.Equal(ShareCopyError.CacheUnavailable, r.ErrorKind);
        Check.Equal("not available while Clink runs as administrator", r.Error);
        Check.Equal(clip.Path, r.Path);
        Check.Equal((0, 0, 0), (rig.Fake.Probes, rig.Fake.Transcodes, rig.Fake.Remuxes));
        Check.False(Directory.Exists(rig.Cache.Root), "an elevated Clink never writes the cache");
        var state = ShareFitModel.StateFor(r);
        Check.Equal(ShareFitStateKind.Unavailable, state.Kind);
        Check.Equal("Smaller copies are not available while Clink runs as administrator. The original is 6 MB, over Discord test (2 MB).",
            ShareFitModel.Text(state, rig.Target, 6_000_000, 5));
    }

    [UnitTest]
    public static void A_cache_that_turns_out_unusable_shares_the_original_with_its_message()
    {
        bool elevated = false;
        using var rig = new Rig(elevated: () => elevated);
        rig.Fake.OnProbe = _ => elevated = true;   // the process becomes unusable for the cache between the check and the write
        var clip = rig.Clip("late.mp4", 6_000_000);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseOriginal, r.Outcome);
        Check.Equal(ShareCopyError.CacheUnavailable, r.ErrorKind);
        Check.Equal(ShareCache.ElevatedReason, r.Error, "the ShareCacheUnavailableException's message");
        Check.Equal(0, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void With_a_game_in_front_and_copies_during_games_off_the_job_refuses()
    {
        using var rig = new Rig();
        rig.App.GameOrFullScreenProbe = () => true;
        rig.S.ShareCopiesDuringGames = false;
        var clip = rig.Clip("game.mp4", 6_000_000);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.Refused, r.Outcome);
        Check.Equal(ShareCopyError.GameInFront, r.ErrorKind);
        Check.Equal("Too big for Discord test (2 MB): open Clink to make a smaller copy", r.Error);
        Check.Equal((0, 0), (rig.Fake.Probes, rig.Fake.Transcodes), "nothing was made");
        var state = ShareFitModel.StateFor(r);
        Check.Equal(ShareFitStateKind.Refused, state.Kind);
        Check.Equal(r.Error, ShareFitModel.Text(state, rig.Target, 6_000_000, 5));

        // With the setting on (the default) a game does not stop it.
        rig.S.ShareCopiesDuringGames = true;
        using var on = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseCopy, on.Outcome, on.Error);

        // A copy that is already made is shared during a game either way.
        rig.S.ShareCopiesDuringGames = false;
        using var cached = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.UseCopy, cached.Outcome);
        Check.True(cached.Reused);
        Check.Equal(1, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void The_copy_runs_on_the_adapter_of_the_selected_monitor()
    {
        using var rig = new Rig();
        rig.App.AdapterLuidOverride = () => new LUID { LowPart = 7, HighPart = 1 };
        using var r = Wait(rig.Ask(rig.Clip("gpu.mp4", 6_000_000)));
        Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
        var luid = rig.Fake.Requests.Single().Adapter;
        Check.True(luid is { LowPart: 7, HighPart: 1 }, "the request names the adapter");
    }

    [UnitTest]
    public static void A_failed_transcode_is_a_failure_with_its_reason_and_leaves_nothing()
    {
        using var rig = new Rig();
        var clip = rig.Clip("broken.mp4", 6_000_000);
        rig.Fake.Hold = (_, _) => throw new ShareTranscodeException(ShareTranscodeFailure.DecoderMissing, "Windows has no HEVC video decoder");
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.Failed, r.Outcome);
        Check.Equal(ShareCopyError.DecoderMissing, r.ErrorKind);
        Check.Equal("Windows has no HEVC video decoder", r.Error);
        Check.False(File.Exists(rig.Part(clip)), "the part is deleted");
        Check.Equal("Couldn't make a smaller copy: Windows has no HEVC video decoder. The original is 6 MB, over Discord test (2 MB).",
            ShareFitModel.Text(ShareFitModel.StateFor(r), rig.Target, 6_000_000, 5));
    }

    // ------------------------------------------------------------------ "stayed too big" and "can't fit"

    [UnitTest]
    public static void A_re_encode_that_is_too_big_with_no_rung_left_below_fails_it_is_not_a_cant_fit()
    {
        using var rig = new Rig();
        // 13 s at 2 MB: the first plan is 480p30 just above its 0.7 Mbps floor; 3 MB out puts the retry under the floor (no rung left).
        rig.Fake.ProbeFor = _ => new ShareProbeResult(13, "h264", 1920, 1080, 60, 1, 1.0);
        rig.Fake.TranscodeSize = _ => 3_000_000;
        var clip = rig.Clip("floor.mp4", 6_000_000, seconds: 13);
        double keyframe = ShareProbe.ClampKeyframe(rig.S.KeyframeSeconds);
        var plan = ShareFit.Plan(1920, 1080, 60, 13, 6_000_000, 2_000_000, "h264", Capabilities.AudioKbps, 1, "h264", keyframe).Plan!;
        Check.Equal("480p30", plan.Label, "the rig is what the reviewer saw");
        var next = ShareFit.NextAttempt(plan, 3_000_000);
        Check.Equal(ShareFitKind.CantFit, next.Kind, "the planner itself answers CantFit here");

        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.Failed, r.Outcome, "a re-encode was tried and came out too big: Failed, never CantFit");
        Check.Equal(ShareCopyError.StayedTooBig, r.ErrorKind);
        Check.Equal(ShareFitModel.StayedTooBigReason, r.Error);
        Check.Equal(next.MaxSeconds, r.MaxSeconds);
        Check.True(r.MaxSeconds > 0 && r.MaxSeconds < 13, $"{r.MaxSeconds} s to trim to");
        Check.Equal(1, rig.Fake.Transcodes, "one attempt, then no rung is left");
        var req = rig.Fake.Requests.Single();
        Check.Equal((480, 30), (req.Height, req.Fps));
        Check.False(File.Exists(rig.Part(clip)) || File.Exists(rig.Final(clip)), "no copy is kept");
        var state = ShareFitModel.StateFor(r);
        Check.Equal(ShareFitStateKind.Failed, state.Kind);
        Check.Equal(ShareFitModel.StayedTooBigText(rig.Target, 6_000_000, r.MaxSeconds), ShareFitModel.Text(state, rig.Target, 6_000_000, 13));
        Check.True(ShareFitModel.Text(state, rig.Target, 6_000_000, 13)!.Contains("Trim it to about"), "the text says where to trim");
    }

    [UnitTest]
    public static void A_stream_copy_that_is_too_big_for_a_clip_too_long_to_re_encode_is_a_cant_fit()
    {
        using var rig = new Rig();
        // 20 s, 5 audio tracks, 3 MB: keeping only the Mix is estimated to fit, but no rung of a re-encode fits 20 s in 2 MB.
        rig.Fake.ProbeFor = _ => new ShareProbeResult(20, "h264", 1920, 1080, 60, 5, 1.0);
        rig.Fake.RemuxSize = 2_100_000;
        double keyframe = ShareProbe.ClampKeyframe(rig.S.KeyframeSeconds);
        Check.Equal(ShareFitKind.RemuxOnly, ShareFit.Plan(1920, 1080, 60, 20, 3_000_000, 2_000_000, "h264", Capabilities.AudioKbps, 5, "h264", keyframe).Kind);
        var replan = ShareFit.Plan(1920, 1080, 60, 20, 3_000_000, 2_000_000, "h264", Capabilities.AudioKbps, 5, "h264", keyframe, allowRemux: false);
        Check.Equal(ShareFitKind.CantFit, replan.Kind);
        var clip = rig.Clip("remux long.mp4", 3_000_000, seconds: 20);
        using var r = Wait(rig.Ask(clip));
        Check.Equal(ShareCopyOutcome.CantFit, r.Outcome, "no re-encode was made: the planner's CantFit stands");
        Check.Equal(replan.MaxSeconds, r.MaxSeconds);
        Check.Equal((1, 0), (rig.Fake.Remuxes, rig.Fake.Transcodes));
        Check.False(File.Exists(rig.Part(clip)) || File.Exists(rig.Final(clip)), "the stream copy is not kept");
    }

    // ------------------------------------------------------------------ finding a copy without starting one (the window opening)

    [UnitTest]
    public static void Finding_a_copy_when_there_is_none_starts_nothing()
    {
        using var rig = new Rig();
        var clip = rig.Clip("none.mp4", 6_000_000);
        var sink = new Sink();
        Check.Null(Wait(rig.App.FindShareCopyAsync(clip, rig.Target, sink)), "nothing cached, nothing running: null (the window offers to make the copy)");
        Check.Equal((0, 0, 0), (rig.Fake.Probes, rig.Fake.Transcodes, rig.Fake.Remuxes), "the clip was not even looked at");
        Check.Equal(0, sink.Seen.Length);
        Check.False(rig.Cache.IsInUse(rig.Key(clip)), "no mark left");
        Check.False(Directory.Exists(rig.Cache.Root), "and the cache was not touched");
        Check.True(rig.App.ShareCopiesAvailable, "copies can be made here");
    }

    /// <summary>What Find and Make agree on, field by field (Find's result must be the one Make would give).</summary>
    private static void SameResult(ShareCopyResult make, ShareCopyResult? find, string what)
    {
        Check.NotNull(find, what + ": Find gives a result, not null");
        Check.Equal(make.Outcome, find!.Outcome, what);
        Check.Equal(make.ErrorKind, find.ErrorKind, what);
        Check.Equal(make.Error, find.Error, what);
        Check.Equal(make.Path, find.Path, what);
        Check.Equal(make.Bytes, find.Bytes, what);
        Check.Equal(make.OriginalBytes, find.OriginalBytes, what);
        Check.Null(find.Lease, what + ": no mark");
    }

    [UnitTest]
    public static void Finding_a_copy_of_a_clip_that_fits_gives_the_original_as_make_would()
    {
        using var rig = new Rig();
        var clip = rig.Clip("small.mp4", 1_000_000);
        var sink = new Sink();
        using var found = Wait(rig.App.FindShareCopyAsync(clip, rig.Target, sink));
        Check.NotNull(found);
        Check.Equal(ShareCopyOutcome.UseOriginal, found!.Outcome);
        Check.Equal(ShareCopyError.None, found.ErrorKind);
        Check.Null(found.Error, "it fits: no reason");
        Check.Equal(ShareFitStateKind.Fits, ShareFitModel.StateFor(found).Kind);
        using var made = Wait(rig.Ask(clip));
        SameResult(made, found, "a clip that fits");
        Check.Equal((0, 0, 0), (rig.Fake.Probes, rig.Fake.Transcodes, rig.Fake.Remuxes), "no work for either");
        Check.Equal(0, sink.Seen.Length);
        Check.False(Directory.Exists(rig.Cache.Root), "the cache was not touched");
    }

    [UnitTest]
    public static void Finding_a_copy_of_a_clip_that_is_gone_gives_the_file_missing_failure_as_make_would()
    {
        using var rig = new Rig();
        var gone = new ClipEntry { Path = rig.Dir.File("clips\\gone.mp4"), Size = 6_000_000, Duration = 5 };
        using var found = Wait(rig.App.FindShareCopyAsync(gone, rig.Target, new Sink()));
        Check.NotNull(found);
        Check.Equal(ShareCopyOutcome.Failed, found!.Outcome);
        Check.Equal(ShareCopyError.FileMissing, found.ErrorKind);
        Check.Equal("the clip's file is not there any more", found.Error);
        Check.Equal(6_000_000L, found.OriginalBytes, "the library's size");
        using var made = Wait(rig.Ask(gone));
        SameResult(made, found, "a clip that is gone");
        Check.Equal((0, 0, 0), (rig.Fake.Probes, rig.Fake.Transcodes, rig.Fake.Remuxes));
    }

    [UnitTest]
    public static void Finding_a_copy_as_administrator_gives_the_original_with_the_reason_as_make_would()
    {
        Updater.IsElevatedOverride = true;   // the existing seam: the cache's own test of elevation follows it
        using var rig = new Rig(elevated: ShareCache.ProcessIsElevated);
        Check.False(rig.App.ShareCopiesAvailable, "no copies as administrator");
        var clip = rig.Clip("admin.mp4", 6_000_000);
        var sink = new Sink();
        using var found = Wait(rig.App.FindShareCopyAsync(clip, rig.Target, sink));
        Check.NotNull(found);
        Check.Equal(ShareCopyOutcome.UseOriginal, found!.Outcome);
        Check.Equal(ShareCopyError.CacheUnavailable, found.ErrorKind);
        Check.Equal(ShareCache.ElevatedReason, found.Error);
        Check.Equal(clip.Path, found.Path);
        Check.Equal(ShareFitStateKind.Unavailable, ShareFitModel.StateFor(found).Kind);
        Check.Equal((0, 0, 0), (rig.Fake.Probes, rig.Fake.Transcodes, rig.Fake.Remuxes), "nothing was looked at");
        Check.Equal(0, sink.Seen.Length);
        Check.False(rig.Cache.IsInUse(rig.Key(clip)), "no mark left");
        using var made = Wait(rig.Ask(clip));
        SameResult(made, found, "as administrator");
        Check.False(Directory.Exists(rig.Cache.Root), "an elevated Clink never writes the cache");
    }

    [UnitTest]
    public static void Finding_a_copy_joins_the_running_job_and_follows_it_to_its_result()
    {
        using var rig = new Rig();
        var clip = rig.Clip("running.mp4", 6_000_000);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        string key = rig.Key(clip);
        var maker = new Sink();
        var make = rig.Ask(clip, maker);
        // The fake reports 0.5 only after it has written its output: wait for the maker to have seen it, so the job is at 0.5 when the
        // window joins (Transcodes == 1 alone comes before that report).
        Spin(() => maker.Seen.Any(p => p.Fraction >= 0.5), "the transcode to be under way, at 0.5");
        var sink = new Sink();
        var find = rig.App.FindShareCopyAsync(clip, rig.Target, sink);
        Spin(() => sink.Seen.Length > 0, "the window that opened to be told where the job is");
        var first = sink.Seen[0];
        Check.Equal(ShareCopyStage.Working, first.Stage);
        Check.Near(0.5, first.Fraction, 1e-9, "where the job is now, at once");
        Check.Equal("720p60", first.Plan?.Label);
        Check.False(find.IsCompleted);
        gate.SetResult();
        using var rf = Wait(find)!;
        using var rm = Wait(make);
        Check.NotNull(rf);
        Check.Equal(ShareCopyOutcome.UseCopy, rf.Outcome, rf.Error);
        Check.False(rf.Reused, "the copy that was being made");
        Check.Equal(rm.Path, rf.Path);
        Check.True(sink.Seen.Any(p => p.Fraction >= 1.0), "its progress was followed");
        Check.Equal(1, rig.Fake.Transcodes, "finding never starts a second one");
        Check.True(rf.Lease != null && rm.Lease != null && !ReferenceEquals(rf.Lease, rm.Lease), "with its own mark");
        rm.Dispose();
        Check.True(rig.Cache.IsInUse(key), "still in use by the window");
        rf.Dispose();
        Check.False(rig.Cache.IsInUse(key));
    }

    [UnitTest]
    public static void Finding_a_copy_finds_the_one_in_the_cache_as_reused()
    {
        using var rig = new Rig();
        var clip = rig.Clip("cached.mp4", 6_000_000);
        string key = rig.Key(clip);
        string made;
        using (var r = Wait(rig.Ask(clip))) { Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error); made = r.Path; }
        rig.Fake.ProbeFor = p => p.StartsWith(rig.Cache.Root, StringComparison.OrdinalIgnoreCase) ? new ShareProbeResult(5, "h264", 1280, 720, 60, 1, 1.0) : new ShareProbeResult(5, "h264", 1920, 1080, 60, 1, 1.0);
        var found = Wait(rig.App.FindShareCopyAsync(clip, rig.Target, new Sink()))!;
        Check.NotNull(found);
        using (found)
        {
            Check.Equal(ShareCopyOutcome.UseCopy, found.Outcome);
            Check.True(found.Reused);
            Check.Equal(made, found.Path);
            Check.Equal("720p60", found.Label, "read from the copy");
            Check.Equal(1_800_000L, found.Bytes);
            Check.Equal(6_000_000L, found.OriginalBytes);
            Check.True(found.Lease != null && rig.Cache.IsInUse(key), "with a mark");
        }
        Check.False(rig.Cache.IsInUse(key), "disposing releases it");
        Check.Equal(1, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void An_asker_that_left_gets_no_more_progress_and_the_job_lets_go_of_it()
    {
        using var rig = new Rig();
        var clip = rig.Clip("left.mp4", 6_000_000);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        string key = rig.Key(clip);
        var maker = new Sink();
        var make = rig.App.MakeShareCopyAsync(clip, rig.Target, maker, default);
        Spin(() => rig.Fake.Transcodes == 1 && maker.Seen.Length > 0, "the transcode to be under way");
        var finder = new Sink();
        var find = rig.App.FindShareCopyAsync(clip, rig.Target, finder);
        Spin(() => finder.Seen.Length > 0, "the window that opened to be told where the job is");
        var (forgotten, weak) = FindWithASinkOnlyTheJobCouldHold(rig, clip);

        // Every window closes: no cancel.
        ShareCopyResult.Abandon(make);
        ShareCopyResult.Abandon(find);
        ShareCopyResult.Abandon(forgotten);
        int seenMaker = maker.Seen.Length, seenFinder = finder.Seen.Length;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check.False(weak.IsAlive, "the job no longer references the listener of an asker that left");
        Check.True(rig.Cache.IsInUse(key), "the job's own mark keeps the folder");
        gate.SetResult();   // the job reports 100% from here on
        Spin(() => File.Exists(rig.Final(clip)) && !rig.Cache.IsInUse(key), "the job to finish in the background and every mark to go");
        Spin(() => make.IsCompleted && find.IsCompleted, "the askers' tasks to end");
        Thread.Sleep(50);
        Check.False(rig.Fake.SawCancel, "nothing cancelled the job");
        Check.Equal(seenMaker, maker.Seen.Length, "no Report reached the button's listener after it left");
        Check.Equal(seenFinder, finder.Seen.Length, "nor the opened window's");
        Check.Equal(1, rig.Fake.Transcodes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<ShareCopyResult?> Task, WeakReference Sink) FindWithASinkOnlyTheJobCouldHold(Rig rig, ClipEntry clip)
    {
        var sink = new Sink();
        var task = rig.App.FindShareCopyAsync(clip, rig.Target, sink);
        Spin(() => sink.Seen.Length > 0, "the third window to be told where the job is");
        return (task, new WeakReference(sink));
    }

    // ------------------------------------------------------------------ the queue, cancels and the cache's own clean-up

    [UnitTest]
    public static void A_clip_that_cant_fit_or_cant_be_read_is_answered_at_once_while_another_copy_runs()
    {
        using var rig = new Rig();
        var busy = rig.Clip("busy.mp4", 6_000_000);
        var longClip = rig.Clip("long.mp4", 6_000_000);
        var noVideo = rig.Clip("audio only.mp4", 6_000_000);
        rig.Fake.ProbeFor = p => p == longClip.Path ? new ShareProbeResult(600, "h264", 1920, 1080, 60, 1, 1.0)
            : p == noVideo.Path ? throw new ShareTranscodeException(ShareTranscodeFailure.Unsupported, "the clip has no video")
            : new ShareProbeResult(5, "h264", 1920, 1080, 60, 1, 1.0);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var askBusy = rig.Ask(busy);
        Spin(() => rig.Fake.Transcodes == 1, "the first copy to hold the slot");

        var sinkLong = new Sink();
        using var cant = Wait(rig.Ask(longClip, sinkLong), "the can't-fit answer (it must not wait for the copy in front)");
        Check.Equal(ShareCopyOutcome.CantFit, cant.Outcome);
        Check.True(cant.MaxSeconds > 0);
        Check.False(sinkLong.Seen.Any(p => p.Stage == ShareCopyStage.Waiting), "it never said Waiting");
        var sinkNone = new Sink();
        using var none = Wait(rig.Ask(noVideo, sinkNone), "the no-video answer");
        Check.Equal(ShareCopyOutcome.Failed, none.Outcome);
        Check.Equal(ShareCopyError.Unsupported, none.ErrorKind);
        Check.Equal("the clip has no video", none.Error);
        Check.Equal(0, sinkNone.Seen.Length);
        Check.False(askBusy.IsCompleted, "the copy in front is still being made");

        gate.SetResult();
        using var rb = Wait(askBusy);
        Check.Equal(ShareCopyOutcome.UseCopy, rb.Outcome, rb.Error);
        Check.Equal(1, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void Cancelling_a_copy_that_waits_ends_it_and_every_asker_of_it_and_frees_the_queue()
    {
        using var rig = new Rig();
        var a = rig.Clip("front.mp4", 6_000_000);
        var b = rig.Clip("queued.mp4", 6_100_000);
        var gateA = new TaskCompletionSource();
        rig.Fake.Hold = (src, ct) => src == a.Path ? gateA.Task.WaitAsync(ct) : Task.CompletedTask;
        var askA = rig.Ask(a);
        Spin(() => rig.Fake.Transcodes == 1, "the first copy to hold the slot");
        var sinkB = new Sink();
        using var cts = new CancellationTokenSource();
        var askB = rig.Ask(b, sinkB, cts.Token);
        Spin(() => sinkB.Seen.Any(p => p.Stage == ShareCopyStage.Waiting), "the second to say it waits");
        var opened = new Sink();
        var findB = rig.App.FindShareCopyAsync(b, rig.Target, opened);
        Spin(() => opened.Seen.Length > 0, "a window that opens on the queued clip to be told");
        Check.Equal(ShareCopyStage.Waiting, opened.Seen[0].Stage, "Waiting..., at once");

        cts.Cancel();   // one asker's Cancel
        using var rb = Wait(askB);
        Check.Equal(ShareCopyOutcome.Cancelled, rb.Outcome);
        using var fb = Wait(findB)!;
        Check.Equal(ShareCopyOutcome.Cancelled, fb!.Outcome, "the job is shared: a Cancel ends it for every asker");
        Check.False(askA.IsCompleted, "the copy in front was not touched");
        Check.False(rig.Fake.SawCancel);
        Check.Equal(1, rig.Fake.Transcodes, "the cancelled one never started");
        Check.False(File.Exists(rig.Part(b)) || File.Exists(rig.Final(b)), "nothing of it was written");
        Check.False(rig.Cache.IsInUse(rig.Key(b)), "and its marks are gone");

        gateA.SetResult();
        using var ra = Wait(askA);
        Check.Equal(ShareCopyOutcome.UseCopy, ra.Outcome, ra.Error);
        using var again = Wait(rig.Ask(b));   // the slot is free again (the cancelled job did not hold it)
        Check.Equal(ShareCopyOutcome.UseCopy, again.Outcome, again.Error);
        Check.Equal(2, rig.Fake.Transcodes);
        Check.Equal(1, rig.Fake.MaxConcurrent);
    }

    [UnitTest]
    public static void A_game_that_starts_while_a_copy_waits_refuses_it_when_its_turn_comes()
    {
        using var rig = new Rig();
        var a = rig.Clip("front.mp4", 6_000_000);
        var b = rig.Clip("queued.mp4", 6_100_000);
        var gateA = new TaskCompletionSource();
        rig.Fake.Hold = (src, ct) => src == a.Path ? gateA.Task.WaitAsync(ct) : Task.CompletedTask;
        rig.S.ShareCopiesDuringGames = false;   // no game yet: the first check passes
        bool game = false;
        rig.App.GameOrFullScreenProbe = () => Volatile.Read(ref game);
        var askA = rig.Ask(a);
        Spin(() => rig.Fake.Transcodes == 1, "the first copy to hold the slot");
        var sinkB = new Sink();
        var askB = rig.Ask(b, sinkB);
        Spin(() => sinkB.Seen.Any(p => p.Stage == ShareCopyStage.Waiting), "the second to wait");
        Check.Equal(2, rig.Fake.Probes, "the second was looked at before it queued");
        Volatile.Write(ref game, true);   // a game comes to the front
        gateA.SetResult();
        using var ra = Wait(askA);
        Check.Equal(ShareCopyOutcome.UseCopy, ra.Outcome, "the copy that was being made finishes");
        using var rb = Wait(askB);
        Check.Equal(ShareCopyOutcome.Refused, rb.Outcome, "the second check, once its turn came");
        Check.Equal(ShareCopyError.GameInFront, rb.ErrorKind);
        Check.Equal(ShareFitModel.GameRefusalText(rig.Target), rb.Error);
        Check.Equal(1, rig.Fake.Transcodes);
        Check.False(File.Exists(rig.Part(b)) || File.Exists(rig.Final(b)));
    }

    [UnitTest]
    public static void An_asker_of_a_job_that_is_being_cancelled_waits_for_it_and_then_starts_afresh()
    {
        using var rig = new Rig();
        var clip = rig.Clip("rejoin.mp4", 6_000_000);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        rig.Fake.CleanupDelayMs = 1500;   // the cancelled transcode takes a while to remove its files
        bool cleanedBeforeSecond = false;
        rig.Fake.TranscodeSize = n => { if (n == 2) cleanedBeforeSecond = rig.Fake.CleanedUp; return 1_800_000; };
        using var cts = new CancellationTokenSource();
        var first = rig.Ask(clip, null, cts.Token);
        Spin(() => rig.Fake.Transcodes == 1, "the transcode to be under way");
        cts.Cancel();
        Spin(() => rig.Fake.SawCancel, "the job to see the cancel");

        var found = Wait(rig.App.FindShareCopyAsync(clip, rig.Target, null), "the look-up during the cancel");
        Check.Null(found, "a job that is going away is nothing to show");
        gate.SetResult();   // the next job's transcode runs straight through
        var second = rig.Ask(clip);
        Check.False(rig.Fake.CleanedUp, "the second asker came while the first job was still cleaning up");
        using var r2 = Wait(second);
        using var r1 = Wait(first);
        Check.Equal(ShareCopyOutcome.Cancelled, r1.Outcome);
        Check.Equal(ShareCopyOutcome.UseCopy, r2.Outcome, r2.Error);
        Check.True(cleanedBeforeSecond, "the new job started only once the cancelled one had cleaned up");
        Check.Equal(2, rig.Fake.Transcodes);
        Check.True(File.Exists(r2.Path) && !File.Exists(rig.Part(clip)), "the new copy is there, no part left");
    }

    [UnitTest]
    public static void An_asker_that_waits_for_a_cancelled_job_and_cancels_too_ends_at_once_and_starts_nothing()
    {
        using var rig = new Rig();
        var clip = rig.Clip("rejoin cancel.mp4", 6_000_000);
        rig.Fake.Hold = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        rig.Fake.CleanupDelayMs = 3000;   // the cancelled transcode takes a long while to remove its files
        using var cts1 = new CancellationTokenSource();
        var first = rig.Ask(clip, null, cts1.Token);
        Spin(() => rig.Fake.Transcodes == 1, "the transcode to be under way");
        cts1.Cancel();
        Spin(() => rig.Fake.SawCancel, "the job to see the cancel");

        using var cts2 = new CancellationTokenSource();
        var sink = new Sink();
        var second = rig.Ask(clip, sink, cts2.Token);
        Thread.Sleep(150);   // the second asker waits for the cancelled job (whether or not it has got there yet, the answer is the same)
        Check.False(second.IsCompleted, "it waits for the cancelled job to clean up");
        var sw = Stopwatch.StartNew();
        cts2.Cancel();   // its own Cancel
        using var r2 = Wait(second);
        sw.Stop();
        Check.Equal(ShareCopyOutcome.Cancelled, r2.Outcome);
        Check.False(rig.Fake.CleanedUp, $"it ended before the cancelled job had cleaned up ({sw.ElapsedMilliseconds} ms)");
        Check.False(first.IsCompleted, "the cancelled job is still cleaning up");

        using var r1 = Wait(first);
        Check.Equal(ShareCopyOutcome.Cancelled, r1.Outcome);
        Thread.Sleep(100);
        Check.Equal((1, 1), (rig.Fake.Probes, rig.Fake.Transcodes), "the second asker started no job of its own");
        Check.Equal(0, sink.Seen.Length, "nor reported anything");
        Check.False(File.Exists(rig.Part(clip)) || File.Exists(rig.Final(clip)), "nothing is left");
        Check.False(rig.Cache.IsInUse(rig.Key(clip)), "and no mark");
    }

    [UnitTest]
    public static void A_late_asker_is_told_where_the_job_was_before_anything_newer_even_when_the_catch_up_is_slow()
    {
        using var rig = new Rig();
        var a = rig.Clip("front.mp4", 6_000_000);
        var b = rig.Clip("queued.mp4", 6_100_000);
        var gateA = new TaskCompletionSource();
        var gateB = new TaskCompletionSource();
        rig.Fake.Hold = (src, ct) => (src == a.Path ? gateA : gateB).Task.WaitAsync(ct);
        var askA = rig.Ask(a);
        Spin(() => rig.Fake.Transcodes == 1, "the first copy to hold the slot");
        var sinkB = new Sink();
        var askB = rig.Ask(b, sinkB);
        Spin(() => sinkB.Seen.Any(p => p.Stage == ShareCopyStage.Waiting), "the second to say it waits");

        // A window opens on the queued clip; its listener is slow with the first thing it is told (the catch-up: Waiting).
        using var inCatchUp = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var late = new Sink { Before = n => { if (n == 1) { inCatchUp.Set(); letGo.Wait(20000); } } };
        var find = rig.App.FindShareCopyAsync(b, rig.Target, late);
        try
        {
            Check.True(inCatchUp.Wait(10000), "the catch-up is being delivered");
            // Meanwhile the job moves on: its turn comes, it reports Working and then 0.5 (the other asker sees both).
            gateA.SetResult();
            Spin(() => sinkB.Seen.Any(p => p.Stage == ShareCopyStage.Working && p.Fraction >= 0.5), "the queued job to be working, at 0.5");
        }
        finally { letGo.Set(); }
        Spin(() => late.Seen.Any(p => p.Fraction >= 0.5), "the late window to catch up");
        var seen = late.Seen;
        Check.Equal(ShareCopyStage.Waiting, seen[0].Stage, "the catch-up first");
        int lastWaiting = Array.FindLastIndex(seen, p => p.Stage == ShareCopyStage.Waiting);
        int firstWorking = Array.FindIndex(seen, p => p.Stage == ShareCopyStage.Working);
        Check.True(lastWaiting == 0 && firstWorking == 1, "no stale Waiting after Working: " + string.Join(", ", seen.Select(p => $"{p.Stage} {p.Fraction}")));
        for (int i = 2; i < seen.Length; i++) Check.True(seen[i].Fraction >= seen[i - 1].Fraction, "in the job's order");

        gateB.SetResult();
        using var ra = Wait(askA);
        using var rb = Wait(askB);
        using var rf = Wait(find)!;
        Check.Equal(ShareCopyOutcome.UseCopy, rb.Outcome, rb.Error);
        Check.Equal(ShareCopyOutcome.UseCopy, rf!.Outcome, rf.Error);
    }

    [UnitTest]
    public static void A_copy_whose_folder_may_still_be_being_cleaned_up_is_made_again_not_reused()
    {
        using var rig = new Rig();
        var clip = rig.Clip("settled.mp4", 6_000_000);
        string key = rig.Key(clip);
        using (var made = Wait(rig.Ask(clip))) Check.Equal(ShareCopyOutcome.UseCopy, made.Outcome, made.Error);
        Check.True(File.Exists(rig.Final(clip)) && !rig.Cache.IsInUse(key));

        // Cleanup starts deleting the (old) copy's folder and is held there, so a mark for the key cannot settle.
        rig.Cache.MarkWait = TimeSpan.FromMilliseconds(400);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.Cache.BeforeEachDeleteHook = path =>
        {
            if (entered.IsSet || !path.Contains(key, StringComparison.Ordinal)) return;
            entered.Set();
            release.Wait(15000);
        };
        var run = Task.Run(() => rig.Cache.Cleanup(DateTime.UtcNow + TimeSpan.FromDays(3)));
        try
        {
            Check.True(entered.Wait(5000), "cleanup started deleting the copy's folder");
            Check.True(rig.Cache.IsBeingDeleted(key));

            // The asker's mark waits (here 400 ms) on a pool thread, never on the caller's (a window's UI thread).
            var sw = Stopwatch.StartNew();
            var ask = rig.App.MakeShareCopyAsync(clip, rig.Target, null, default);
            long ms = sw.ElapsedMilliseconds;
            Check.True(ms < 300, $"the call returned at once, not after the 400 ms mark wait ({ms} ms)");
            Check.False(ask.IsCompleted);

            using var r = Wait(ask);
            Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
            Check.False(r.Reused, "not settled: what is in the folder is not trusted");
            Check.Equal(2, rig.Fake.Transcodes, "the copy was made again");
            release.Set();
            Check.True(run.Wait(10000), "cleanup ended");
            Check.True(File.Exists(r.Path) && new FileInfo(r.Path).Length == 1_800_000, "the new copy is there: the delete stopped once the key was in use");
        }
        finally
        {
            release.Set();
            run.Wait(10000);
        }
    }

    // ------------------------------------------------------------------ the real thing (CLINK_TESTS_GPU=1)

    [UnitTest]
    public static void A_real_clip_over_the_limit_gets_a_real_smaller_copy_end_to_end()
    {
        if (!ShareTranscoderTests.GpuTestsOn) { ShareTranscoderTests.NotRun(nameof(A_real_clip_over_the_limit_gets_a_real_smaller_copy_end_to_end)); return; }
        using var dir = new TempDir();
        string src = "";
        ShareTranscoderTests.OnMta(() => src = ShareTranscoderTests.MakeClip(dir, "game clip.mp4", 1280, 720, 60, 5.0, "Mix", "Game"));
        long srcBytes = new FileInfo(src).Length;
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        using var cache = new ShareCache(dir.File("cache"), isElevated: () => false);
        using var app = new AppController(settings, new Updater(settings)) { ShareCacheOverride = cache, GameOrFullScreenProbe = () => false };
        var target = new ShareFitTarget(1_000_000, 0, "Discord test (1 MB)");
        Check.True(srcBytes > target.LimitBytes, $"the synthetic clip ({srcBytes} B) is over the 1 MB limit");
        var entry = new ClipEntry { Path = src, Size = srcBytes, Duration = 0 };
        var sink = new Sink();
        var sw = Stopwatch.StartNew();
        using var r = Task.Run(() => app.MakeShareCopyAsync(entry, target, sink, default)).GetAwaiter().GetResult();
        sw.Stop();
        if (r.Outcome == ShareCopyOutcome.Failed && r.ErrorKind is ShareCopyError.NoHardwareEncoder or ShareCopyError.DecoderMissing)
            Check.Skip($"CLINK_TESTS_GPU=1 but {r.Error}");
        Check.Equal(ShareCopyOutcome.UseCopy, r.Outcome, r.Error);
        Console.WriteLine($"      end to end: {srcBytes / 1024} KB -> {r.Bytes / 1024} KB as {r.Label} in {sw.ElapsedMilliseconds} ms ({sink.Seen.Length} progress reports)");
        Check.True(r.Bytes <= target.LimitBytes && r.Bytes > 0, $"{r.Bytes} B is within {target.LimitBytes} B");
        Check.True(File.Exists(r.Path) && r.Path.StartsWith(cache.Root, StringComparison.OrdinalIgnoreCase), "the copy is in the cache");
        Check.True(r.Path.EndsWith("game clip.mp4", StringComparison.OrdinalIgnoreCase), "under the original name");
        Check.NotNull(r.Plan, "a re-encode");
        var copy = ShareProbe.Read(r.Path, 1.0);
        Check.Equal("h264", copy.Codec);
        Check.Equal(1, copy.AudioTracks, "only the Mix");
        Check.Equal(new[] { "Mix" }, Trimmer.ReadAudioTrackNames(r.Path)!);
        Check.Equal((r.Plan!.Width, r.Plan.Height), (copy.Width, copy.Height));
        Check.True(copy.Width < 1280, "smaller than the original");
        Check.Near(5, copy.DurationSeconds, 1.5, "the same length");
        Check.True(new FileInfo(src).Length == srcBytes, "the original is untouched");
        Check.Equal(Array.Empty<string>(), Directory.GetFiles(Path.GetDirectoryName(r.Path)!).Where(f => !string.Equals(f, r.Path, StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToArray()!, "no part or temporary file left");

        // The same clip again is found in the cache.
        using var again = Task.Run(() => app.MakeShareCopyAsync(entry, target, null, default)).GetAwaiter().GetResult();
        Check.True(again.Reused && again.Outcome == ShareCopyOutcome.UseCopy, "reused");
    }
}
