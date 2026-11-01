using System.IO;
using Windows.Win32.Foundation;
using Clink.Engine;

namespace Clink.Services;

/// <summary>How a share-copy request ended.</summary>
internal enum ShareCopyOutcome
{
    /// <summary>Share <see cref="ShareCopyResult.Path"/>: the smaller copy (made now, or found in the cache). Dispose the result when done with the file.</summary>
    UseCopy,
    /// <summary>Share the original: it fits (<see cref="ShareCopyResult.Error"/> is null), or smaller copies are not available here (Error says why: as administrator, a redirected cache folder).</summary>
    UseOriginal,
    /// <summary>Not even the lowest quality fits; <see cref="ShareCopyResult.MaxSeconds"/> is the length to trim to. The original is not shared.</summary>
    CantFit,
    /// <summary>The copy could not be made (<see cref="ShareCopyResult.Error"/> is the reason, <see cref="ShareCopyResult.ErrorKind"/> the kind). The original is not shared.</summary>
    Failed,
    /// <summary>A game is in front and "Make smaller copies during games" is off: <see cref="ShareCopyResult.Error"/> is the toast text.</summary>
    Refused,
    /// <summary>The asker cancelled (or Clink is quitting): nothing is left behind.</summary>
    Cancelled,
}

/// <summary>Why a share copy was not made (<see cref="ShareCopyResult.ErrorKind"/>).</summary>
internal enum ShareCopyError
{
    None,
    /// <summary>The share cache is not usable: Clink runs as administrator, or the cache folder is redirected (UseOriginal).</summary>
    CacheUnavailable,
    /// <summary>A game is in front and copies during games are off (Refused).</summary>
    GameInFront,
    LowDisk,
    /// <summary>Every attempt came out over the limit (Failed; <see cref="ShareCopyResult.MaxSeconds"/> is the length to trim to).</summary>
    StayedTooBig,
    FileMissing,
    /// <summary>The probe or the planner could not make sense of the clip.</summary>
    Unreadable,
    // The kinds of ShareTranscodeFailure:
    Unsupported,
    DecoderMissing,
    NoHardwareEncoder,
    DiskFull,
    GpuLost,
    Failed,
}

/// <summary>
/// Where a running copy is: Waiting for the one-at-a-time slot (the clip was already looked at and planned), or Working on it
/// (<see cref="ShareCopyProgress.Plan"/> set for a re-encode, null for a stream copy).
/// </summary>
internal enum ShareCopyStage { Waiting, Working }

/// <param name="Fraction">0 .. 1 of the current attempt (a retry starts again at 0, with the new <paramref name="Plan"/>).</param>
internal sealed record ShareCopyProgress(ShareCopyStage Stage, double Fraction, ShareFitPlan? Plan);

/// <summary>
/// What <see cref="AppController.MakeShareCopyAsync"/> gives back. <b>Dispose it</b> (it holds the cache's in-use mark for the copy, so
/// cleanup leaves the file alone while it is shared); it holds no handle on the file itself. Disposing twice is fine.
/// </summary>
/// <param name="Path">The file to share: the copy for UseCopy, the original for every other outcome.</param>
/// <param name="Bytes">The size of <paramref name="Path"/>.</param>
internal sealed record ShareCopyResult(ShareCopyOutcome Outcome, string Path, long Bytes) : IDisposable
{
    /// <summary>The plan the copy was made at (null for a copy found in the cache or made by a stream copy).</summary>
    public ShareFitPlan? Plan { get; init; }
    /// <summary>The copy's quality as the UI says it: "720p60" (empty when unknown).</summary>
    public string Label { get; init; } = "";
    /// <summary>The copy's length in seconds.</summary>
    public double Seconds { get; init; }
    /// <summary>The copy was found in the cache, not made now.</summary>
    public bool Reused { get; init; }
    /// <summary>CantFit and a StayedTooBig failure: the longest clip, in seconds, that would fit.</summary>
    public int MaxSeconds { get; init; }
    public ShareCopyError ErrorKind { get; init; }
    /// <summary>The reason (Failed: completes "Couldn't make a smaller copy: ..."; UseOriginal: why no copy was possible) or, for Refused, the whole text. Null otherwise.</summary>
    public string? Error { get; init; }
    /// <summary>The original's size.</summary>
    public long OriginalBytes { get; init; }
    /// <summary>The cache's in-use mark for the copy (UseCopy only).</summary>
    public IDisposable? Lease { get; init; }

    public bool UsedOriginal => Outcome == ShareCopyOutcome.UseOriginal;

    public void Dispose() => Lease?.Dispose();

    /// <summary>
    /// For an asker that goes away without cancelling (a closed window): <paramref name="pending"/> is the task
    /// <see cref="AppController.MakeShareCopyAsync"/> or <see cref="AppController.FindShareCopyAsync"/> gave (that very task, not a wrapper
    /// such as Task.Run's: the asker is known by it). At once, the asker's progress listener is removed from the job (the job no longer
    /// references it, and no new Report call reaches it; one already under way on the worker thread may still finish, and a
    /// <c>Progress&lt;T&gt;</c> may still deliver what it had posted, so a closed window ignores late callbacks) and the asker's in-use mark is
    /// released; the job runs on, and the result is disposed when it arrives. Calling it twice is fine. (Cancelling is the token passed to
    /// those methods, and stops the job.)
    /// </summary>
    public static void Abandon(Task pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        AppController.LeaveShareAsk(pending);
        _ = pending.ContinueWith(static t => { if (t is Task<ShareCopyResult> { IsCompletedSuccessfully: true } r) r.Result?.Dispose(); }, TaskScheduler.Default);
    }
}

internal sealed partial class AppController
{
    /// <summary>Test seam (and Copium, 2e): the probe, the re-encode and the stream copy. Default: the real <see cref="ShareTranscoder"/>.</summary>
    internal IShareTranscoder ShareTranscoderImpl { get; set; } = DefaultShareTranscoder.Instance;

    /// <summary>Test seam: the cache the copies go to (null = <see cref="ShareCache.Current"/>).</summary>
    internal ShareCache? ShareCacheOverride { get; set; }

    /// <summary>Test seam: the free bytes on the drive of the path (default: the drive's; unknown counts as plenty).</summary>
    internal Func<string, long> FreeSpaceProbe { get; set; } = DefaultFreeSpace;

    /// <summary>Test seam: the adapter the copy runs on (null = <see cref="ShareAdapterLuid"/>'s own lookup).</summary>
    internal Func<LUID?>? AdapterLuidOverride { get; set; }

    private readonly SemaphoreSlim _shareGate = new(1, 1);   // one transcode at a time: a second job waits
    private readonly Dictionary<string, ShareJob> _shareJobs = new(StringComparer.Ordinal);   // by cache key (source path, size, time, limit); guarded by itself

    private ShareCache ShareCacheInUse => ShareCacheOverride ?? ShareCache.Current;

    /// <summary>Whether smaller copies can be made here (false as administrator or with a redirected cache folder), for the cache in use (the test seam's when set).</summary>
    internal bool ShareCopiesAvailable => ShareCacheInUse.IsAvailable;

    /// <summary>The adapter that drives the selected monitor (the capture adapter, so the recording's encoder and the copy's share a GPU); null when it cannot be told.</summary>
    internal LUID? ShareAdapterLuid()
    {
        if (AdapterLuidOverride is { } forced) return forced();
        try
        {
            var mon = SelectedMonitor;
            if (mon != null && Capabilities.TryGetAdapterLuid(mon.Handle, out LUID luid, out _)) return luid;
        }
        catch (Exception ex) { Log.Warn("share copy: adapter lookup: " + ex.Message); }
        return null;
    }

    private static long DefaultFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception) { return long.MaxValue; }
    }

    private sealed class ShareJob
    {
        public readonly string Key;
        public readonly CancellationTokenSource Cts;
        public Task<ShareCopyResult> Task = null!;
        private readonly object _gate = new();
        private readonly List<Listening> _listeners = new();
        private ShareCopyProgress? _last;

        public ShareJob(string key, CancellationToken quit)
        {
            Key = key;
            Cts = CancellationTokenSource.CreateLinkedTokenSource(quit);   // Clink quitting cancels it
        }

        public void Cancel()
        {
            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// An asker listens from now on; a late one is told where the job is at once. Disposing what this returns removes the listener: no
        /// Report call to it starts after that, and the job drops its reference to it.
        /// Each listener gets the reports in the job's order: the catch-up is queued before any later report can reach the listener (both
        /// under the job's lock), and one thread at a time delivers a listener's queue, outside every lock.
        /// </summary>
        public IDisposable Join(IProgress<ShareCopyProgress>? listener)
        {
            if (listener == null) return NoListener.Instance;
            var l = new Listening(this, listener);
            lock (_gate)
            {
                _listeners.Add(l);
                if (_last != null) l.Queue(_last);
            }
            l.Deliver();
            return l;
        }

        public void Report(ShareCopyProgress progress)
        {
            Listening[] now;
            lock (_gate)
            {
                _last = progress;
                now = _listeners.ToArray();
                foreach (var l in now) l.Queue(progress);   // in the job's order, behind a catch-up that is still being delivered
            }
            foreach (var l in now) l.Deliver();
        }

        private void Remove(Listening l)
        {
            lock (_gate) _listeners.Remove(l);
        }

        private sealed class Listening(ShareJob job, IProgress<ShareCopyProgress> sink) : IDisposable
        {
            private ShareJob? _job = job;
            private IProgress<ShareCopyProgress>? _sink = sink;
            private readonly object _gate = new();   // guards _pending and _delivering (never held while the sink runs)
            private readonly Queue<ShareCopyProgress> _pending = new();
            private bool _delivering;

            public void Queue(ShareCopyProgress progress)
            {
                lock (_gate) { if (Volatile.Read(ref _sink) != null) _pending.Enqueue(progress); }
            }

            /// <summary>Delivers what is queued, in order; returns at once when another thread is delivering (it takes what was queued).</summary>
            public void Deliver()
            {
                lock (_gate) { if (_delivering) return; _delivering = true; }
                while (true)
                {
                    ShareCopyProgress next;
                    lock (_gate)
                    {
                        if (_pending.Count == 0 || Volatile.Read(ref _sink) == null) { _pending.Clear(); _delivering = false; return; }
                        next = _pending.Dequeue();
                    }
                    var sink = Volatile.Read(ref _sink);   // null once removed: no call starts after that
                    if (sink == null) continue;
                    try { sink.Report(next); }
                    catch (Exception ex) { Log.Warn("share copy: a progress handler failed: " + ex.Message); }
                }
            }

            public void Dispose()
            {
                Volatile.Write(ref _sink, null);
                lock (_gate) _pending.Clear();
                Interlocked.Exchange(ref _job, null)?.Remove(this);
            }
        }

        private sealed class NoListener : IDisposable
        {
            public static readonly NoListener Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// One asker (a window, an upload): its listener and its in-use mark, until it is done or leaves (<see cref="ShareCopyResult.Abandon"/>).
    /// The asker's own task does not hold the listener, so a closed window is not referenced by the job after it left.
    /// </summary>
    private sealed class ShareAsk(IProgress<ShareCopyProgress>? listener)
    {
        private readonly object _gate = new();
        private IProgress<ShareCopyProgress>? _listener = listener;
        private IDisposable? _listening, _lease;
        private bool _left;

        /// <summary>Listens to <paramref name="job"/> (unless the asker left already).</summary>
        public void Listen(ShareJob job)
        {
            IProgress<ShareCopyProgress>? l;
            lock (_gate) { if (_left || _listener == null) return; l = _listener; }
            var listening = job.Join(l);
            bool drop;
            lock (_gate) { drop = _left; if (!drop) _listening = listening; }
            if (drop) listening.Dispose();
        }

        /// <summary>Keeps the asker's mark, so leaving releases it at once (it is also the result's <see cref="ShareCopyResult.Lease"/>).</summary>
        public void Hold(IDisposable lease)
        {
            bool drop;
            lock (_gate) { drop = _left; if (!drop) _lease = lease; }
            if (drop) lease.Dispose();
        }

        /// <summary>The job is over: stop listening (the mark stays with the result).</summary>
        public void Done()
        {
            IDisposable? listening;
            lock (_gate) { listening = _listening; _listening = null; _listener = null; }
            listening?.Dispose();
        }

        /// <summary>The asker went away without cancelling: stop listening and release its mark.</summary>
        public void Leave()
        {
            IDisposable? listening, lease;
            lock (_gate) { _left = true; _listener = null; listening = _listening; lease = _lease; _listening = null; _lease = null; }
            listening?.Dispose();
            lease?.Dispose();
        }
    }

    /// <summary>The asker behind each pending task of <see cref="MakeShareCopyAsync"/> / <see cref="FindShareCopyAsync"/>, for <see cref="ShareCopyResult.Abandon"/>.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Task, ShareAsk> s_shareAsks = new();

    internal static void LeaveShareAsk(Task pending)
    {
        if (s_shareAsks.TryGetValue(pending, out var ask)) ask.Leave();
        else Log.Warn("share copy: Abandon was given a task that is not a share-copy ask (a wrapper?): the asker stays listening and keeps its mark until it ends");
    }

    /// <summary>An IProgress that calls back on the reporting thread (Progress&lt;T&gt; posts to the thread pool and can reorder).</summary>
    private sealed class ShareFractionProgress(Action<double> on) : IProgress<double>
    {
        public void Report(double value) => on(value);
    }

    /// <summary>What the job keeps track of for its clean-up.</summary>
    private sealed class ShareJobState
    {
        public string? Part;
        public bool Finished, HoldsGate;
    }

    /// <summary>
    /// Makes the smaller copy of <paramref name="entry"/> that fits <paramref name="target"/> (or finds the one made before), without
    /// touching the original. Never throws for a failure to make the copy: the <see cref="ShareCopyResult.Outcome"/> says what happened, and
    /// the caller disposes the result.
    /// <list type="bullet">
    /// <item>One job per clip and limit: a second asker for the same clip and limit joins the running job (and takes its own in-use mark).</item>
    /// <item>One transcode at a time: a job that has to wait reports <see cref="ShareCopyStage.Waiting"/>.</item>
    /// <item><paramref name="ct"/> is the asker's explicit cancel (a Cancel button): it cancels the job and deletes the unfinished copy.
    /// The job is shared, so this cancels it for <b>every</b> asker that joined it (each gets <see cref="ShareCopyOutcome.Cancelled"/>).
    /// That cannot surprise anyone today: the askers of one job are for the same clip and the same limit, there is one Share window per
    /// clip (and so one Discord asker per clip), and Copium's limits (200 MB / 2 GB) are none of Discord's (20 MB / 50 MB / 1 GB), so a
    /// Copium upload and a Share window never share a job.
    /// An asker that just goes away (the window closed) does not cancel: it passes the task to <see cref="ShareCopyResult.Abandon"/>,
    /// which stops its listening and releases its mark; the job finishes in the background and the copy is ready next time.</item>
    /// <item>Clink quitting cancels every job, and <see cref="WaitForShareCopies"/> waits for them.</item>
    /// <item>A clip that cannot fit (<see cref="ShareCopyOutcome.CantFit"/>), cannot be read, or would be refused is answered at once:
    /// the clip is looked at and planned before the job queues for the one-at-a-time slot; only the copy itself waits.</item>
    /// </list>
    /// A clip that fits the limit, or when smaller copies are not available here (as administrator, a redirected cache folder), is
    /// <see cref="ShareCopyOutcome.UseOriginal"/>. Progress is reported from the worker thread (a report that comes while an asker's
    /// catch-up is being delivered follows it on that asker's thread, so each asker sees the job's order). An asker that comes while a
    /// cancelled job of the same clip is still cleaning up waits for it (its own <paramref name="ct"/> ends that wait as Cancelled, before
    /// anything is started) and then starts afresh.
    /// </summary>
    public Task<ShareCopyResult> MakeShareCopyAsync(ClipEntry entry, ShareFitTarget target, IProgress<ShareCopyProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        var ask = new ShareAsk(progress);
        var task = MakeShareCopyCoreAsync(ask, entry, target, ct);
        s_shareAsks.AddOrUpdate(task, ask);
        return task;
    }

    /// <summary>
    /// What a Share window asks when it opens: the final result <see cref="MakeShareCopyAsync"/> would give, when that is known without
    /// ever starting work (no probe, no copy); null when only making the copy would tell.
    /// <list type="bullet">
    /// <item>The clip fits the limit: <see cref="ShareCopyOutcome.UseOriginal"/> with no <see cref="ShareCopyResult.Error"/>.</item>
    /// <item>The clip's file is gone: <see cref="ShareCopyOutcome.Failed"/> with <see cref="ShareCopyError.FileMissing"/>.</item>
    /// <item>A job for this clip and limit is running: joins it with its own in-use mark, reports where it is at once and then its
    /// progress, and gives its result (as <see cref="MakeShareCopyAsync"/> would).</item>
    /// <item>Smaller copies are not available here (as administrator, a redirected cache folder) and no job is running:
    /// <see cref="ShareCopyOutcome.UseOriginal"/> with <see cref="ShareCopyError.CacheUnavailable"/> and
    /// <see cref="ShareCache.ElevatedReason"/>.</item>
    /// <item>A finished copy is in the cache and can be reused: it, as <see cref="ShareCopyOutcome.UseCopy"/> with
    /// <see cref="ShareCopyResult.Reused"/> and a mark.</item>
    /// <item>Otherwise (no copy and no job: nothing made yet, a job that is being cancelled, a cache folder that may still be being
    /// cleaned up): null, and the window offers to make the copy.</item>
    /// </list>
    /// <paramref name="ct"/> is the same explicit cancel as <see cref="MakeShareCopyAsync"/>'s: it stops a joined job (for every asker).
    /// A window that closes passes the task to <see cref="ShareCopyResult.Abandon"/>. Dispose a non-null result.
    /// </summary>
    public Task<ShareCopyResult?> FindShareCopyAsync(ClipEntry entry, ShareFitTarget target, IProgress<ShareCopyProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        var ask = new ShareAsk(progress);
        var task = AskShareCopyAsync(ask, entry, target, start: false, ct);
        s_shareAsks.AddOrUpdate(task, ask);
        return task;
    }

    private async Task<ShareCopyResult> MakeShareCopyCoreAsync(ShareAsk ask, ClipEntry entry, ShareFitTarget target, CancellationToken ct)
        => (await AskShareCopyAsync(ask, entry, target, start: true, ct).ConfigureAwait(false))!;

    /// <summary>
    /// One asker: <paramref name="start"/> true makes the copy (<see cref="MakeShareCopyAsync"/>, never null), false gives the same final
    /// result when it is known without work (<see cref="FindShareCopyAsync"/>: fits, gone, no cache here, a running job, a cached copy)
    /// and null otherwise.
    /// </summary>
    private async Task<ShareCopyResult?> AskShareCopyAsync(ShareAsk ask, ClipEntry entry, ShareFitTarget target, bool start, CancellationToken ct)
    {
        string src = entry.Path;
        double entrySeconds = entry.Duration;
        long limit = target.LimitBytes;
        var cache = ShareCacheInUse;

        // Off the caller's (UI) thread: an archived clip may sit on a dropped network share, and the mark can wait up to 5 s for a
        // cleanup of this key. The mark is this asker's own: the copy stays while it is shared, whoever else leaves.
        var (key, size, lease, settled) = await Task.Run<(string?, long, IDisposable?, bool)>(() =>
        {
            var (k, s) = KeyAndSize(src, limit);
            if (k == null || limit <= 0 || s <= limit) return (k, s, (IDisposable?)null, false);
            var mark = cache.MarkInUse(k, out bool st);
            return (k, s, mark, st);
        }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            // Find gives what Make would, when that is known without work: the same results for a missing file and a clip that fits.
            if (key == null)
                return new ShareCopyResult(ShareCopyOutcome.Failed, src, 0) { ErrorKind = ShareCopyError.FileMissing, Error = "the clip's file is not there any more", OriginalBytes = entry.Size };
            var original = new ShareCopyResult(ShareCopyOutcome.UseOriginal, src, size) { OriginalBytes = size };
            if (lease == null) return original;   // it fits: nothing to make, the original is shared
            ask.Hold(lease);
            if (start && ct.IsCancellationRequested) return original with { Outcome = ShareCopyOutcome.Cancelled };

            ShareJob? job = null;
            while (job == null)
            {
                Task? gone = null;
                ShareJob? joined = null;
                lock (_shareJobs)
                {
                    if (_shareJobs.TryGetValue(key, out var running))
                    {
                        if (running.Cts.IsCancellationRequested) gone = running.Task;   // being cancelled: its part file is not ours to touch yet
                        else joined = job = running;
                    }
                    else if (start)
                    {
                        job = new ShareJob(key, _cts.Token);
                        // Listening before the job starts, so the asker sees every report. No callback runs here: the job has reported
                        // nothing yet, so there is no catch-up to deliver.
                        ask.Listen(job);
                        _shareJobs[key] = job;
                        var started = job;
                        started.Task = Task.Run(() => RunShareJobAsync(started, cache, src, entrySeconds, size, target));
                    }
                }
                if (joined != null)
                {
                    // Outside the lock: joining calls the asker's listener (the catch-up). A job that ended meanwhile is harmless to join:
                    // it reports nothing more, and its task has the result.
                    ask.Listen(joined);
                }
                if (job != null) break;
                if (!start)
                {
                    if (gone != null) return null;   // a job that is going away: nothing to show (the button comes back)
                    // No job. Smaller copies not available here: what the job would answer at once, without touching the cache.
                    if (!cache.IsAvailable) return original with { ErrorKind = ShareCopyError.CacheUnavailable, Error = ShareCache.ElevatedReason };
                    // A finished copy, if the cache has one (looked at off the caller's thread).
                    if (!settled) return null;
                    string name = Path.GetFileName(src);
                    var found = await Task.Run(() => ReusableCopy(cache, key, name, original), CancellationToken.None).ConfigureAwait(false);
                    if (found == null) return null;
                    var reused = found with { Lease = lease };
                    lease = null;   // the result owns it now
                    return reused;
                }
                // A job of this clip is being cancelled: wait for it to clean up, unless this asker cancels first (nothing is started then).
                try { await gone!.WaitAsync(ct).ConfigureAwait(false); } catch (Exception) { }
                if (ct.IsCancellationRequested) return original with { Outcome = ShareCopyOutcome.Cancelled };
            }

            ShareCopyResult made;
            using (ct.Register(static j => ((ShareJob)j!).Cancel(), job))   // an explicit cancel stops the job (for every asker)
            {
                try { made = await job.Task.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Warn("share copy: the job failed: " + ex.Message);
                    made = new ShareCopyResult(ShareCopyOutcome.Failed, src, size) { ErrorKind = ShareCopyError.Failed, Error = "something went wrong while making the copy" };
                }
            }
            if (made.Outcome != ShareCopyOutcome.UseCopy) return made with { OriginalBytes = size };
            var result = made with { OriginalBytes = size, Lease = lease };
            lease = null;   // the result owns it now
            return result;
        }
        finally
        {
            ask.Done();
            lease?.Dispose();
        }
    }

    /// <summary>The cache key of the clip for this limit and its size now; (null, 0) when the file is not there or cannot be looked at.</summary>
    private static (string? Key, long Size) KeyAndSize(string src, long limitBytes)
    {
        try
        {
            var info = new FileInfo(src);
            if (!info.Exists) return (null, 0);
            return (ShareCache.KeyFor(src, limitBytes, ShareFitModel.Codec, ShareFitModel.CacheVariant), info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, 0);
        }
    }

    /// <summary>The finished copy for <paramref name="key"/> as a UseCopy result (Reused, its label and length read from it), or null. The caller holds a mark.</summary>
    private ShareCopyResult? ReusableCopy(ShareCache cache, string key, string name, ShareCopyResult original)
    {
        if (!cache.TryGetReusable(key, name, out string reusable)) return null;
        long bytes;
        try { bytes = new FileInfo(reusable).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        string label = ""; double seconds = 0;
        try
        {
            var p = ShareTranscoderImpl.Probe(reusable, Settings.KeyframeSeconds);
            label = p.Label;
            seconds = p.DurationSeconds;
        }
        catch (Exception ex) { Log.Warn("share copy: could not read the cached copy: " + ex.Message); }
        Log.Info($"share copy: reusing the copy of {name} ({bytes / 1048576.0:0.0} MB)");
        return original with { Outcome = ShareCopyOutcome.UseCopy, Path = reusable, Bytes = bytes, Reused = true, Label = label, Seconds = seconds };
    }

    /// <summary>Quit: gives the running share copies a moment (at most <paramref name="limit"/>) to see the cancel and delete their part files.</summary>
    private void WaitForShareCopies(TimeSpan limit)
    {
        Task[] running;
        lock (_shareJobs) running = _shareJobs.Values.Select(j => (Task)j.Task).ToArray();
        if (running.Length == 0) return;
        try { Task.WaitAll(running, limit); }
        catch (Exception) { }   // a job never throws; a late one is only logged
    }

    private bool ShareGameBlocks()
    {
        if (Settings.ShareCopiesDuringGames) return false;
        try { return GameOrFullScreenInFront(); }
        catch (Exception ex)
        {
            Log.Warn("share copy: game check: " + ex.Message);
            return false;
        }
    }

    /// <summary>The job: never throws; every way out is a result (without a lease, which each asker adds).</summary>
    private async Task<ShareCopyResult> RunShareJobAsync(ShareJob job, ShareCache cache, string src, double entrySeconds, long size, ShareFitTarget target)
    {
        var state = new ShareJobState();
        var original = new ShareCopyResult(ShareCopyOutcome.UseOriginal, src, size) { OriginalBytes = size };
        ShareCopyResult Failed(ShareCopyError kind, string reason, int maxSeconds = 0)
            => original with { Outcome = ShareCopyOutcome.Failed, ErrorKind = kind, Error = reason, MaxSeconds = maxSeconds };
        try
        {
            return await ShareCopyCoreAsync(job, cache, state, src, entrySeconds, size, target, original, Failed);
        }
        catch (OperationCanceledException)
        {
            return original with { Outcome = ShareCopyOutcome.Cancelled };
        }
        catch (ShareCacheUnavailableException ex)
        {
            Log.Info("share copy: no cache: " + ex.Message);
            return original with { ErrorKind = ShareCopyError.CacheUnavailable, Error = ex.Message };
        }
        catch (ShareTranscodeException ex)
        {
            Log.Warn($"share copy of {Path.GetFileName(src)} failed ({ex.Kind}): {ex.Message}");
            return Failed(ErrorKindOf(ex.Kind), ex.Message);
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"share copy of {Path.GetFileName(src)}: the clip or the plan was not usable: {ex.Message}");
            return Failed(ShareCopyError.Unreadable, "the clip could not be read");
        }
        catch (Exception ex)
        {
            Log.Error("share copy", ex);
            return Failed(ShareCopyError.Failed, "something went wrong while making the copy");
        }
        finally
        {
            if (state.Part != null && !state.Finished)
            {
                try { File.Delete(state.Part); } catch (Exception) { }   // an unfinished copy is never left in the cache
            }
            if (state.HoldsGate) _shareGate.Release();
            lock (_shareJobs)
            {
                if (_shareJobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job)) _shareJobs.Remove(job.Key);
            }
            try { job.Cts.Dispose(); } catch (Exception) { }
        }
    }

    private async Task<ShareCopyResult> ShareCopyCoreAsync(ShareJob job, ShareCache cache, ShareJobState state, string src, double entrySeconds, long size,
        ShareFitTarget target, ShareCopyResult original, Func<ShareCopyError, string, int, ShareCopyResult> failed)
    {
        var ct = job.Cts.Token;
        var impl = ShareTranscoderImpl;
        string name = Path.GetFileName(src);
        long limit = target.LimitBytes;
        ct.ThrowIfCancellationRequested();

        string key = job.Key;
        using var jobLease = cache.MarkInUse(key, out bool settled);   // the job's own mark: cleanup leaves the key alone even when every asker has left
        if (!cache.IsAvailable) return original with { ErrorKind = ShareCopyError.CacheUnavailable, Error = ShareCache.ElevatedReason };

        // settled false: a cleanup of this key's folder may still be going on, so what is in it is not reused (the copy is made again).
        if (settled && ReusableCopy(cache, key, name, original) is { } reused) return reused;
        if (!settled) Log.Info($"share copy: the cache folder of {name} may still be being cleaned up; making the copy again");

        // Everything that can answer at once runs before the queue: the game check, the disk, the probe and the plan. A clip that
        // can't fit, can't be read or has no video is never kept waiting behind another copy.
        if (ShareGameBlocks()) return GameRefusal(original, target);
        if (FreeSpaceProbe(cache.Root) < ShareFitModel.RequiredFreeBytes(limit))
            return failed(ShareCopyError.LowDisk, ShareFitModel.LowDiskReason(limit), 0);

        var probe = impl.Probe(src, Settings.KeyframeSeconds);
        ct.ThrowIfCancellationRequested();
        double duration = probe.DurationSeconds > 0 ? probe.DurationSeconds : entrySeconds;
        double fps = probe.Fps > 0 ? probe.Fps : 30;   // the planner needs a rate; the transcoder picks frames by the clip's own timestamps
        int tracks = probe.AudioTracks;
        int audioKbps = tracks > 0 ? Capabilities.AudioKbps : 0;
        if (duration <= 0) return failed(ShareCopyError.Unreadable, "the clip's length could not be read", 0);

        ShareFitResult Plan(bool allowRemux) => ShareFit.Plan(probe.Width, probe.Height, fps, duration, size, limit, ShareFitModel.Codec, audioKbps, tracks,
            probe.Codec, probe.KeyframeSeconds, allowRemux);
        var step = Plan(allowRemux: true);
        if (step.Kind == ShareFitKind.NoCopyNeeded) return original;
        if (step.Kind == ShareFitKind.CantFit) return original with { Outcome = ShareCopyOutcome.CantFit, MaxSeconds = step.MaxSeconds };

        // One copy at a time: only the copy itself waits for the slot ("Waiting..." only while it does).
        bool waited = !_shareGate.Wait(0);
        if (waited)
        {
            job.Report(new ShareCopyProgress(ShareCopyStage.Waiting, 0, null));
            await _shareGate.WaitAsync(ct);   // a cancel while waiting ends here, holding nothing
        }
        state.HoldsGate = true;
        if (ShareGameBlocks()) return GameRefusal(original, target);   // a game started while this one waited (or since the first check)
        if (waited && FreeSpaceProbe(cache.Root) < ShareFitModel.RequiredFreeBytes(limit))   // the copy before this one used some
            return failed(ShareCopyError.LowDisk, ShareFitModel.LowDiskReason(limit), 0);
        ct.ThrowIfCancellationRequested();

        job.Report(new ShareCopyProgress(ShareCopyStage.Working, 0, step.Plan));
        state.Part = cache.BeginCopy(key, name);   // the only path written to
        ShareFitPlan? made = null;
        ShareTranscodeResult? file = null;
        bool encoded = false;   // a re-encode was made (and came out too big): running out of rungs after that is a failure, not "can't fit"

        if (step.Kind == ShareFitKind.RemuxOnly)
        {
            file = await impl.RemuxMixOnlyAsync(src, state.Part, new ShareFractionProgress(f => job.Report(new ShareCopyProgress(ShareCopyStage.Working, f, null))), ct);
            if (file.SizeBytes > limit)
            {
                Log.Info($"share copy: the stream copy of {name} came out at {file.SizeBytes / 1048576.0:0.0} MB, over the limit; re-encoding");
                file = null;
                step = Plan(allowRemux: false);
            }
        }

        while (file == null && step.Kind == ShareFitKind.Plan)
        {
            var plan = step.Plan!;
            job.Report(new ShareCopyProgress(ShareCopyStage.Working, 0, plan));
            var request = new ShareTranscodeRequest(plan.Width, plan.Height, plan.Fps, plan.VideoBitrateBps) { Adapter = ShareAdapterLuid() };
            var result = await impl.TranscodeAsync(src, state.Part, request, new ShareFractionProgress(f => job.Report(new ShareCopyProgress(ShareCopyStage.Working, f, plan))), ct);
            encoded = true;
            Log.Info($"share copy of {name}: attempt {plan.Attempt} {plan.Label} at {plan.VideoMbps:0.#} Mbps -> {result.SizeBytes / 1048576.0:0.0} MB " +
                     $"(limit {limit / 1048576.0:0.0} MB; kept {result.MeasuredFps:0.##} fps of a planned {plan.Fps})");
            var next = ShareFit.NextAttempt(plan, result.SizeBytes);
            if (next.Kind == ShareFitKind.CopyFits) { file = result; made = plan; break; }
            step = next;
        }

        if (file == null)
        {
            // Out of attempts, or out of rungs after a re-encode came out too big: the copy stayed too big (Failed, never CantFit).
            // CantFit is only the planner's answer before any re-encode (here: the re-plan after a stream copy that was too big).
            if (step.Kind == ShareFitKind.Failed || (step.Kind == ShareFitKind.CantFit && encoded))
                return failed(ShareCopyError.StayedTooBig, ShareFitModel.StayedTooBigReason, step.MaxSeconds);
            return original with { Outcome = ShareCopyOutcome.CantFit, MaxSeconds = step.MaxSeconds };
        }

        ct.ThrowIfCancellationRequested();
        if (!cache.MarkFinished(key, name)) return failed(ShareCopyError.Failed, "the copy could not be saved", 0);
        state.Finished = true;
        string final = cache.PathFor(key, name);
        long finalBytes = file.SizeBytes;
        try { finalBytes = new FileInfo(final).Length; } catch (Exception) { }
        return original with
        {
            Outcome = ShareCopyOutcome.UseCopy,
            Path = final,
            Bytes = finalBytes,
            Plan = made,
            Label = made?.Label ?? ShareProbe.LabelFor(file.Width, file.Height, file.Fps),
            Seconds = file.Duration.TotalSeconds,
        };
    }

    private static ShareCopyResult GameRefusal(ShareCopyResult original, ShareFitTarget target)
        => original with { Outcome = ShareCopyOutcome.Refused, ErrorKind = ShareCopyError.GameInFront, Error = ShareFitModel.GameRefusalText(target) };

    private static ShareCopyError ErrorKindOf(ShareTranscodeFailure kind) => kind switch
    {
        ShareTranscodeFailure.Unsupported => ShareCopyError.Unsupported,
        ShareTranscodeFailure.DecoderMissing => ShareCopyError.DecoderMissing,
        ShareTranscodeFailure.NoHardwareEncoder => ShareCopyError.NoHardwareEncoder,
        ShareTranscodeFailure.DiskFull => ShareCopyError.DiskFull,
        ShareTranscodeFailure.GpuLost => ShareCopyError.GpuLost,
        _ => ShareCopyError.Failed,
    };
}
