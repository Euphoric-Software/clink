using System.Diagnostics;
using Clink.Screenshots.Imaging;
using Clink.Services;

namespace Clink.Screenshots.Editing;

/// <summary>What a finished compose hands the UI: the composite (the composer's own buffer, valid only inside the handler), the region that changed since the last delivery, and the version of the request.</summary>
internal sealed record ComposeResult(BgraImage Image, RectI Dirty, long Version, double Ms);

/// <summary>
/// Runs a <see cref="RasterComposer"/> off the UI thread: <b>at most one compose in flight, and it always runs to completion</b>
/// (the composer's caches are never left half-updated by a cancellation). <see cref="Request"/> records the newest state and bumps a
/// version; when the running compose ends, a result whose version is no longer the newest is dropped (never delivered) and the newest
/// state is composed next. A delivered result reaches <see cref="Completed"/> through <c>post</c> (the UI thread), and the next
/// compose starts only after that handler returned, so the UI reads the composite while nothing writes it (a hand-off, with the
/// WriteableBitmap as the second buffer); the handler must copy what it needs.
/// <para>Two debounces: a spotlight radius / gradient change (blur levels to rebuild, ~150 ms at 4K) waits <see cref="LevelsDebounceMs"/>
/// after the last change while the slider moves (a box move only re-blends and is not delayed); and a sharpness change shows the
/// LUT stage at once, then <see cref="SharpenDebounceMs"/> after the last change computes the sharpened base on a worker and composes again,
/// unless a newer request arrived meanwhile (its version differs, and the composer only uses a sharpened base for the exact picture and sharpness).
/// A sharpening that fails is logged once and not tried again for those values (the unsharpened picture stays).</para>
/// <para><b>Failures.</b> A compose that throws leaves the composer empty (it drops its caches); the queue retries the same state once,
/// from scratch, and tells the UI (<see cref="Failed"/>). A second failure in a row stops the retries until the next request, which is
/// then never ignored as "the same state".</para>
/// <para><b>Gestures.</b> A request made during a gesture is <c>interactive</c>: the composer may re-blend a spotlight with slightly
/// stale blur levels. When the gesture ends (the next request is not interactive, even for the same state) a composite that holds such
/// output is composed again with fresh levels.</para>
/// </summary>
internal sealed class ComposeQueue
{
    private readonly RasterComposer _composer;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private DocumentState? _latest, _lastStarted;
    private long _version, _startedVersion;
    private bool _running, _computing, _closed, _force, _latestInteractive, _staleShown, _fullDirtyPending;
    private int _failuresInARow;
    private bool _failureShown;   // the UI was told (Failed) and has not been told it works again (Recovered)
    private long _lastErrorLogged;   // Stopwatch timestamp of the last failure line written
    private bool _streakLogged;
    private int _suppressedLines;
    private long _notBefore;   // Stopwatch timestamp: do not start a compose before this
    private Timer? _timer, _sharpenTimer;
    private readonly ManualResetEventSlim _idle = new(true);
    private int _inFlight, _firstLogged;

    public int SharpenDebounceMs { get; set; } = 60;
    public int LevelsDebounceMs { get; set; } = 120;

    /// <summary>Runs the compose on the calling thread and delivers at once (tests, and the export path). No timers, no worker.</summary>
    public bool Synchronous { get; set; }

    /// <summary>Raised on the <c>post</c> thread for the newest result only.</summary>
    public event Action<ComposeResult>? Completed;

    /// <summary>Raised on the <c>post</c> thread when the compose of the newest state failed: a short message for the status line.</summary>
    public event Action<string>? Failed;

    /// <summary>Raised on the <c>post</c> thread when a compose of the newest state succeeds after <see cref="Failed"/> was raised: the UI clears the message.</summary>
    public event Action? Recovered;

    public const string RetryingNotice = "Preview failed — retrying";
    public const string GaveUpNotice = "Preview failed — it is tried again with the next change (the reason is in the log)";

    /// <summary>Diagnostics for the tests.</summary>
    public int Started { get; private set; }
    public int Delivered { get; private set; }
    public int Dropped { get; private set; }
    public int MaxInFlight { get; private set; }
    public int SharpenJobs { get; private set; }
    public int SharpenFailures { get; private set; }
    public int Failures { get; private set; }
    /// <summary>Failure lines written to the log / failures left out of it (see <c>LogFailure</c>).</summary>
    public int ErrorsLogged { get; private set; }
    public int ErrorsSuppressed { get; private set; }
    /// <summary>At most one failure line per this long once a streak began (the first has the stack trace).</summary>
    public int ErrorLogIntervalMs { get; set; } = 5000;
    public double LastMs { get; private set; }
    public long LatestVersion { get { lock (_gate) return _version; } }

    public ComposeQueue(RasterComposer composer, Action<Action> post)
    {
        _composer = composer;
        _post = post;
    }

    public RasterComposer Composer => _composer;

    /// <summary>True while a compose or its delivery is under way, or newer work is waiting (for its debounce too).</summary>
    public bool Busy
    {
        get { lock (_gate) return _running || (_latest != null && (_version != _startedVersion || _force)); }
    }

    /// <summary>
    /// Asks for the composite of <paramref name="state"/>. Cheap and non-blocking; a state equal in all that matters to the newest
    /// request is ignored (unless <paramref name="force"/>, or the gesture just ended on a composite with stale spotlight levels).
    /// <paramref name="interactive"/>: a gesture is under way.
    /// </summary>
    public void Request(DocumentState state, bool force = false, bool interactive = false)
    {
        int delay = 0;
        lock (_gate)
        {
            if (_closed) return;
            if (!force && _latest != null && SameInput(_latest, state))
            {
                _latestInteractive = interactive;
                // the gesture ended on a picture composed with stale spotlight levels: compose it again (a running compose looks at it when it ends)
                if (interactive || !_staleShown || _running) return;
                force = true;
            }
            if (!Synchronous && NeedsLevelRebuild(_lastStarted, state)) delay = LevelsDebounceMs;
            _latest = state;
            _latestInteractive = interactive;
            _version++;
            _force |= force;
            _notBefore = delay > 0 ? Stopwatch.GetTimestamp() + (long)(delay * (Stopwatch.Frequency / 1000.0)) : 0;
        }
        Kick(delay);
    }

    /// <summary>Asks for <paramref name="state"/> again and for the whole picture as its dirty region (the UI lost what it was shown).</summary>
    public void RequestFull(DocumentState state, bool interactive = false)
    {
        lock (_gate) _fullDirtyPending = true;
        Request(state, force: true, interactive);
    }

    /// <summary>What the composer works from: the picture, the raster objects and the adjustment values.</summary>
    internal static bool SameInput(DocumentState a, DocumentState b) =>
        ReferenceEquals(a.Base, b.Base) && (ReferenceEquals(a.Raster, b.Raster) || a.Raster.SequenceEqual(b.Raster)) && a.Adjust.Equals(b.Adjust);

    /// <summary>A spotlight that exists in both states with another blur radius or gradient flag: its levels must be rebuilt (the debounce case).</summary>
    internal static bool NeedsLevelRebuild(DocumentState? composed, DocumentState next)
    {
        if (composed == null) return false;
        foreach (var o in next.Raster)
        {
            if (o is not SpotlightBox sp) continue;
            if (composed.Raster.FirstOrDefault(x => x.Id == sp.Id) is SpotlightBox was && (was.Radius != sp.Radius || was.Gradient != sp.Gradient)) return true;
        }
        return false;
    }

    private void Kick(int delay)
    {
        Action? work;
        lock (_gate)
        {
            if (_closed || _running) return;   // a running job looks for newer work when it ends
            if (delay > 0)
            {
                _timer ??= new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(delay, Timeout.Infinite);
                return;
            }
            work = StartLocked();
        }
        Run(work);
    }

    private void Run(Action? work)
    {
        if (work == null) return;
        if (Synchronous) work();
        else Task.Run(work);
    }

    private void OnTimer()
    {
        Action? work = null;
        lock (_gate)
        {
            if (_closed || _running) return;
            long wait = _notBefore - Stopwatch.GetTimestamp();
            if (wait > 0) { _timer?.Change((int)Math.Max(1, wait * 1000 / Stopwatch.Frequency), Timeout.Infinite); return; }
            work = StartLocked();
        }
        Run(work);
    }

    /// <summary>Marks a compose of the newest state as running and returns the work to run (outside the lock); null when there is nothing newer.</summary>
    private Action? StartLocked()
    {
        if (_latest == null || (_version == _startedVersion && !_force)) return null;
        _running = true;
        _force = false;
        var state = _latest;
        long version = _version;
        bool interactive = _latestInteractive, full = _fullDirtyPending;
        _fullDirtyPending = false;
        _startedVersion = version;
        _lastStarted = state;
        Started++;
        _idle.Reset();
        return () => Job(state, version, interactive, full);
    }

    private void Job(DocumentState state, long version, bool interactive, bool full)
    {
        BgraImage? image = null;
        Exception? error = null;
        bool stale = false;
        var t0 = Stopwatch.GetTimestamp();
        lock (_gate) { _computing = true; }
        int now = Interlocked.Increment(ref _inFlight);
        lock (_gate) { MaxInFlight = Math.Max(MaxInFlight, now); }
        try
        {
            _composer.Interactive = interactive;
            if (full) _composer.MarkAllDirty();
            image = _composer.Compose(state);
            stale = _composer.HasStaleOutput;
        }
        catch (Exception ex) { error = ex; }
        finally { Interlocked.Decrement(ref _inFlight); }
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        lock (_gate) { _computing = false; LastMs = ms; }
        _idle.Set();
        if (error != null) LogFailure(error);
        else if (ms >= 100 || Interlocked.Exchange(ref _firstLogged, 1) == 0)
            Log.Info($"screenshot editor: compose v{version} {state.Base.Width}x{state.Base.Height}, {state.Raster.Count} raster object(s), {ms:0} ms ({_composer.LastRecomputed} op(s) applied{(_composer.PendingSharpen != null ? ", sharpen pending" : "")}{(stale ? ", stale spotlight levels" : "")})");
        if (Synchronous) Deliver(version, image, ms, error, stale);
        else _post(() => Deliver(version, image, ms, error, stale));
    }

    private void Deliver(long version, BgraImage? image, double ms, Exception? error, bool stale)
    {
        bool current;
        lock (_gate) { current = !_closed && version == _version; }
        if (current && image != null)
        {
            try
            {
                var dirty = _composer.TakeDirty();
                Delivered++;
                Completed?.Invoke(new ComposeResult(image, dirty, version, ms));
            }
            catch (Exception ex) { Log.Error("screenshot editor: showing the composite", ex); }
        }
        else if (image != null) Dropped++;
        string? notice = null;
        bool again, recovered = false;
        lock (_gate)
        {
            _running = false;
            if (error != null)
            {
                Failures++;
                if (current)
                {
                    _failuresInARow++;
                    // once: the same state again, from scratch (the composer dropped its caches); then wait for the next request,
                    // and do not take that one for "the same state" (_latest is forgotten)
                    if (_failuresInARow == 1) { _force = true; notice = RetryingNotice; }
                    else { _latest = null; notice = GaveUpNotice; }
                }
            }
            else
            {
                _failuresInARow = 0;
                _streakLogged = false;
                if (current)
                {
                    _staleShown = stale;
                    if (_failureShown && image != null) { _failureShown = false; recovered = true; }
                }
                // the gesture ended while this compose with stale spotlight levels ran: the same state again, with fresh ones
                if (stale && current && !_latestInteractive) _force = true;
            }
            again = !_closed && _latest != null && (_version != _startedVersion || _force);
            if (!_closed && !again && _composer.PendingSharpen != null && !Synchronous) ArmSharpen(version);
        }
        if (recovered)
        {
            try { Recovered?.Invoke(); }
            catch (Exception ex) { Log.Warn("screenshot editor: reporting a recovered compose: " + ex.Message); }
        }
        if (notice != null)
        {
            lock (_gate) _failureShown = true;
            try { Failed?.Invoke(notice); }
            catch (Exception ex) { Log.Warn("screenshot editor: reporting a failed compose: " + ex.Message); }
        }
        if (again && !Synchronous)
        {
            long wait;
            lock (_gate) wait = _notBefore - Stopwatch.GetTimestamp();
            if (wait > 0) Kick((int)Math.Max(1, wait * 1000 / Stopwatch.Frequency));
            else Kick(0);
        }
        else if (again) Kick(0);
    }

    /// <summary>
    /// The first failure of a streak is logged with its stack trace; after that at most one line per <see cref="ErrorLogIntervalMs"/>,
    /// with the number left out since the previous line. A success ends the streak.
    /// </summary>
    private void LogFailure(Exception ex)
    {
        bool first;
        int suppressed = 0;
        lock (_gate)
        {
            first = !_streakLogged;
            if (!first && Stopwatch.GetElapsedTime(_lastErrorLogged).TotalMilliseconds < ErrorLogIntervalMs) { _suppressedLines++; ErrorsSuppressed++; return; }
            suppressed = _suppressedLines;
            _suppressedLines = 0;
            _streakLogged = true;
            _lastErrorLogged = Stopwatch.GetTimestamp();
            ErrorsLogged++;
        }
        if (first) Log.Error("screenshot editor: compose (its caches were dropped)", ex);
        else Log.Error($"screenshot editor: compose failed again ({suppressed} similar failure(s) not logged since the last line): {ex.GetType().Name}: {ex.Message}");
    }

    private void ArmSharpen(long version)
    {
        _sharpenTimer?.Dispose();
        _sharpenTimer = new Timer(_ => OnSharpenTimer(version), null, SharpenDebounceMs, Timeout.Infinite);
    }

    private void OnSharpenTimer(long armedAt)
    {
        lock (_gate)
        {
            // a newer request since the timer was armed: its compose arms its own timer when it is delivered
            if (_closed || _running || _version != armedAt || _composer.PendingSharpen == null) return;
            _running = true;
            _idle.Reset();
            SharpenJobs++;
        }
        Task.Run(() =>
        {
            var t0 = Stopwatch.GetTimestamp();
            lock (_gate) { _computing = true; }
            bool ok = false;
            try { ok = _composer.PrepareSharpened(); }
            catch (Exception ex)
            {
                // the composer remembers the (picture, sharpness): it is not pending any more and never tried again
                lock (_gate) SharpenFailures++;
                Log.Warn("screenshot editor: sharpening failed; the picture is shown without it for these values: " + ex.Message);
            }
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            lock (_gate) { _computing = false; }
            _idle.Set();
            if (ms >= 100) Log.Info($"screenshot editor: sharpen {ms:0} ms");
            _post(() =>
            {
                bool go;
                lock (_gate)
                {
                    _running = false;
                    if (ok) _force = true;   // the composer has the sharpened base now: compose again with the same state
                    // (ok or not) a picture shown with stale spotlight levels whose gesture is over must be composed again
                    if (_staleShown && !_latestInteractive) _force = true;
                    go = !_closed && _latest != null && (_force || _version != _startedVersion);
                }
                if (go) Kick(0);
            });
        });
    }

    /// <summary>
    /// Stops delivering and waits for the compose under way (it is never cancelled half-way; at most a few seconds). The composer's
    /// caches are cleared by the caller afterwards.
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _closed = true;
            _timer?.Dispose();
            _sharpenTimer?.Dispose();
            _timer = _sharpenTimer = null;
        }
        _idle.Wait(TimeSpan.FromSeconds(15));
    }

    /// <summary>Waits until no compose is running or waiting (tests; never call it on a thread the <c>post</c> needs).</summary>
    public bool WaitIdle(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool quiet;
            lock (_gate) quiet = !_running && !_computing && (_latest == null || (_version == _startedVersion && !_force)) && (_composer.PendingSharpen == null || _closed);
            if (quiet) return true;
            Thread.Sleep(5);
        }
        return false;
    }
}
