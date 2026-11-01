using System.Globalization;

namespace Clink.Services;

// The remote status pipe (RemoteServer): the controller builds an immutable RemoteSnapshot on the UI thread and hands it over, runs the
// commands a client sent (queued to the UI thread, from a fixed table), and publishes the few events. Nothing here blocks the UI thread.

internal sealed partial class AppController : IRemoteHost
{
    /// <summary>The remote status pipe: <c>Clink.Remote{suffix}.s{session}</c>.</summary>
    internal static string RemotePipeName => "Clink.Remote" + InstanceSuffix + ".s" + SessionId.ToString(CultureInfo.InvariantCulture);

    private RemoteServer? _remote;
    private Action<string>? _remoteClipNotSaved;   // the ClipNotSaved handler StartRemote added (StopRemote takes it off)
    private int _remoteUploadP;          // 0..100: how far the upload going now is (written by the upload's thread)
    private bool _remoteFault;           // a snapshot could not be built or handed over: said once

    /// <summary>A clip was not saved; the code says why ("restarting", "paused", "waitingForGame", "error", "notReady", "clipsOff", "failed"). Raised on the UI thread, after the user was told.</summary>
    internal event Action<string>? ClipNotSaved;

    /// <summary>Test seam: sees every event line handed to the pipe (also when no pipe server runs).</summary>
    internal Action<string>? RemoteEventProbe { get; set; }

    string IRemoteHost.Version => Updater.Current.ToString();
    bool IRemoteHost.IsDev => IsDev;
    bool IRemoteHost.Elevated => Updater.IsElevated();
    bool IRemoteHost.ClipsEnabled => Settings.ClipsEnabled;
    void IRemoteHost.Run(string command) => _ui.BeginInvoke(() => RunCommand(command));

    private void StartRemote()
    {
        try
        {
            var server = new RemoteServer(this, RemotePipeName);
            _remote = server;
            _remoteClipNotSaved = reason => server.PublishEvent(RemoteProtocol.ClipNotSaved(reason));
            ClipNotSaved += _remoteClipNotSaved;
            server.Start();
        }
        catch (Exception ex) { Log.Warn("remote: not started: " + ex.Message); _remote = null; }
    }

    private void StopRemote()
    {
        if (_remoteClipNotSaved != null) { ClipNotSaved -= _remoteClipNotSaved; _remoteClipNotSaved = null; }
        var server = _remote;
        _remote = null;
        try { server?.Dispose(); } catch (Exception ex) { Log.Warn("remote: stop: " + ex.Message); }
    }

    /// <summary>What a remote client sees now. UI thread. Booleans, whole numbers and fixed codes only.</summary>
    internal RemoteSnapshot BuildRemoteSnapshot()
    {
        bool uploading = _uploads.Count > 0;
        return new RemoteSnapshot(
            RemoteStateCode(Settings.ClipsEnabled, State),
            new RemoteRec(IsManualRecording, Engine.Recording is { } rec ? (int)Math.Max(0, rec.Seconds) : 0, _wantRecording && !Engine.IsRecording, RecordingQueued, _finishing),
            // Before the engine first runs its RingSeconds is EngineConfig's default; the slider's value is what it will keep.
            Engine.IsRunning ? Engine.RingSeconds : Math.Clamp(Settings.BufferSeconds, 5, 3600),
            Volatile.Read(ref _saving) == 1,
            Settings.ScreenshotsEnabled,
            Settings.CloudSharingEnabled,
            HasCopiumToken,
            new RemoteUpload(uploading, RemoteUploadPercent(uploading, Volatile.Read(ref _remoteUploadP))),
            PendingVersion != null,
            Updater.IsElevated());
    }

    /// <summary>The snapshot's "state": Clips off wins over whatever the engine is doing.</summary>
    internal static string RemoteStateCode(bool clipsEnabled, RecordingState state) => !clipsEnabled ? "clipsOff" : state switch
    {
        RecordingState.Recording => "recording",
        RecordingState.WaitingForGame => "waitingForGame",
        RecordingState.Paused => "paused",
        RecordingState.Error => "error",
        _ => "starting",
    };

    /// <summary>The snapshot's "upload.p": in steps of 5 (fewer state lines), 0 with no upload going.</summary>
    internal static int RemoteUploadPercent(bool uploading, int percent) => uploading ? Math.Clamp(percent, 0, 100) / 5 * 5 : 0;

    /// <summary>The "clipNotSaved" reason for a clip press the recorder could not take, in the order ClipNow says why.</summary>
    internal static string ClipNotSavedReason(bool restarting, bool paused, bool waitingForGame, bool engineError, bool starting) =>
        restarting ? "restarting" : paused ? "paused" : waitingForGame ? "waitingForGame" : engineError && !starting ? "error" : "notReady";

    /// <summary>Hands the current snapshot to the pipe server (it sends only a changed one). UI thread; never blocks on a client.</summary>
    private void PublishRemote()
    {
        var server = _remote;
        if (server == null) return;
        try { server.Publish(BuildRemoteSnapshot()); }
        catch (Exception ex)
        {
            if (_remoteFault) return;
            _remoteFault = true;
            Log.Warn("remote: status not published: " + ex.Message);
        }
    }

    private void PublishRemoteEvent(string line)
    {
        try
        {
            RemoteEventProbe?.Invoke(line);
            _remote?.PublishEvent(line);
        }
        catch (Exception) { }
    }

    /// <summary>Raises <see cref="ClipNotSaved"/>; a handler that throws never reaches ClipNow (an async void).</summary>
    private void RaiseClipNotSaved(string reason)
    {
        try { ClipNotSaved?.Invoke(reason); }
        catch (Exception ex) { Log.Warn("remote: clipNotSaved: " + ex.Message); }
    }

    /// <summary>The "shareDone" outcome code for how a Share ended.</summary>
    internal static string RemoteOutcome(ShareOutcome outcome) => outcome switch
    {
        ShareOutcome.LinkCopied => "linkCopied",
        ShareOutcome.Uploaded => "uploaded",
        ShareOutcome.Refused => "refused",
        ShareOutcome.Declined => "declined",
        ShareOutcome.FileShareOpened => "fileShareOpened",
        _ => "failed",
    };

    /// <summary>Progress of a share for "shareProgress" events: whole percent, at most 4 a second, at least 2 points apart.</summary>
    internal sealed class RemoteShareProgress : IProgress<double>
    {
        private readonly Action<int> _publish;
        private readonly Func<long> _clock;
        private int _lastP = -100;
        private long _lastTick = long.MinValue / 2;

        /// <param name="clock">Milliseconds (a test brings its own; default <see cref="Environment.TickCount64"/>).</param>
        public RemoteShareProgress(Action<int> publish, Func<long>? clock = null)
        {
            _publish = publish;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        public void Report(double value)
        {
            int p = (int)Math.Clamp(Math.Round(value * 100), 0, 100);
            long now = _clock();
            lock (this)
            {
                if (p < _lastP + 2 || now - _lastTick < 250) return;
                _lastP = p;
                _lastTick = now;
            }
            _publish(p);
        }
    }

    /// <summary>Passes every report on to the caller's own progress (unchanged, in order) and remembers it for the snapshot's "upload.p".</summary>
    internal sealed class RemoteUploadTee : IProgress<double>
    {
        private readonly AppController _app;
        private readonly IProgress<double>? _inner;

        public RemoteUploadTee(AppController app, IProgress<double>? inner)
        {
            _app = app;
            _inner = inner;
            Volatile.Write(ref app._remoteUploadP, 0);
        }

        public void Report(double value)
        {
            try { Volatile.Write(ref _app._remoteUploadP, (int)Math.Clamp(Math.Round(value * 100), 0, 100)); }
            finally { _inner?.Report(value); }
        }
    }

    // ---- test seams (no engine runs in a unit test)

    /// <summary>A start was asked for while the recorder was not ready (what <see cref="RecordingQueued"/> reads).</summary>
    internal void QueueRecordingForTests() => _recordWhenReadyUntil = DateTime.UtcNow + TimeSpan.FromSeconds(30);

    /// <summary>A clip save is in flight (ShareLast waits for <paramref name="save"/>); <see cref="EndSaveForTests"/> ends it.</summary>
    internal void BeginSaveForTests(Task<ClipEntry?> save) { _saveTask = save; _saving = 1; }

    internal void EndSaveForTests() => _saving = 0;
}
