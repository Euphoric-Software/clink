using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Clink.Audio;
using Clink.Engine;
using Clink.Native;
using Clink.UI;

namespace Clink.Services;

/// <summary>What Clink is doing right now, for the window, the tray and the status dot.</summary>
internal enum RecordingState { Starting, Recording, WaitingForGame, Paused, Error }

/// <summary>Owns the engine, settings, clip library, hotkeys, game detection, toasts, status dot, archive, tray icon and the single-instance pipe.</summary>
internal sealed class AppController : IDisposable
{
    public const string PipeName = "Clink.Control";
    private const string MutexName = "Clink.SingleInstance.6F1B";
    private static readonly TimeSpan GameExitGrace = TimeSpan.FromSeconds(5);   // keep recording this long after the game's process is gone

    public static AppController Instance { get; private set; } = null!;

    public Settings Settings { get; }
    public ClipLibrary Library { get; } = new();
    public Engine.Engine Engine { get; } = new();
    public Updater Updater { get; }
    public Toasts Toasts { get; }
    public ArchiveService Archive { get; }
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public bool IsPaused { get; private set; }
    public string LastNotice { get; private set; } = "";
    /// <summary>Why the last <see cref="ShareAsync"/> refused to upload (Copium's free limits), for the clip row and the toast to show instead of "Upload failed"; null when it was not refused.</summary>
    public string? ShareRefusal { get; private set; }
    /// <summary>Why the last <see cref="ShareAsync"/> failed (no token, Copium's answer, the network), for the clip row, the toast and the trim editor to show; null when it did not fail.</summary>
    public string? ShareError { get; private set; }
    /// <summary>Version of a downloaded update waiting to be installed, or null.</summary>
    public Version? PendingVersion => Updater.PendingVersion;

    /// <summary>Name of the detected game / custom app (process name), or null while none runs. Set by the 1 s detection poll in both recording modes.</summary>
    public string? CurrentGame { get; private set; }
    /// <summary>"fullscreen" | "library" | "custom": which signal found <see cref="CurrentGame"/>.</summary>
    public string? CurrentGameSource { get; private set; }
    /// <summary>Settings.RecordingMode == "game": the engine runs only while a game or custom app is running.</summary>
    public bool GameOnly => string.Equals(Settings.RecordingMode, "game", StringComparison.OrdinalIgnoreCase);

    public RecordingState State
    {
        get
        {
            if (IsPaused) return RecordingState.Paused;
            if (Engine.IsRunning) return RecordingState.Recording;
            if (GameOnly && _gamePid == 0) return RecordingState.WaitingForGame;
            if (Engine.Error != null) return RecordingState.Error;
            return RecordingState.Starting;
        }
    }

    /// <summary>One line for the UI header: "Recording · Overwatch", "Waiting for a game…", "Paused", "Error", "Starting…".</summary>
    public string StatusText => State switch
    {
        RecordingState.Paused => "Paused",
        RecordingState.Recording => CurrentGame != null ? $"Recording · {CurrentGame}" : "Recording",
        RecordingState.WaitingForGame => "Waiting for a game…",
        RecordingState.Error => "Error",
        _ => "Starting…",
    };

    public event Action? StateChanged;
    public event Action<string>? Notice;
    public event Action<ClipEntry>? ClipSaved;
    public event Action? ShowRequested;
    /// <summary>"Clink.exe --trim-last" (or the pipe command "trim"): open the trim editor on the newest clip.</summary>
    public event Action? TrimLastRequested;
    /// <summary>A toast's Trim button: open the trim editor on this clip. When nobody listens the toast opens the editor itself.</summary>
    public event Action<ClipEntry>? TrimRequested;
    /// <summary>An update became ready to install (or the pending state changed otherwise). Raised on the UI thread.</summary>
    public event Action? UpdateStateChanged;

    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);   // Engine.Start / Stop are not thread-safe against each other
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _statusItem, _clipItem, _pauseItem, _updateItem;
    private DispatcherTimer? _timer;
    private StatusDotWindow? _dot;
    private static Mutex? s_mutex;
    private int _saving;
    // Copium uploads in flight (UI thread); Announced: started by ShareLast, which reports a failure itself.
    private readonly Dictionary<ClipEntry, (Task<bool> Task, bool Announced)> _uploads = new(ReferenceEqualityComparer.Instance);
    private DateTime _lastClipAt = DateTime.MinValue;
    private int _startGen;
    private bool _starting;
    private bool _stopping;                 // a StopEngine is in flight: Engine.IsRunning reads true until its teardown has finished
    private DateTime _lastStartAt = DateTime.MinValue;
    private int _startFailures;             // engine starts that failed in a row (Tick's retry backoff, one balloon per streak)
    private bool _disposed;
    private uint _gamePid;
    private DateTime? _gameStopAt;          // game-only mode: stop the engine at this time unless a game comes back
    private string? _announceGame;          // show the "Recording started" toast for this game once the engine is up
    private int _detecting;
    private string _trayText = "";

    public AppController(Settings settings, Updater updater)
    {
        Settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        Instance = this;
        Updater = updater;
        Toasts = new Toasts(settings, () => SelectedMonitor);
        Archive = new ArchiveService(settings, Library);
        Updater.UpdateAvailable += info => _ui.BeginInvoke(() => OnUpdateAvailable(info));
        Updater.UpdateReady += version => _ui.BeginInvoke(() => OnUpdateReady(version));
    }

    // ------------------------------------------------------------------ single instance

    /// <summary>Returns true if this process should run; otherwise the command was forwarded to the running instance.</summary>
    public static bool AcquireSingleInstance(string[] args)
    {
        bool createdNew;
        try { s_mutex = new Mutex(true, MutexName, out createdNew); }
        catch (UnauthorizedAccessException) { createdNew = false; }   // an elevated Clink owns it: it is running all the same
        if (createdNew) return true;
        SendCommand(CommandFromArgs(args) ?? "show");
        return false;
    }

    /// <summary>The pipe command a command line asks for: "clip" (--clip), "trim" (--trim-last), "share" (--share-last), or null.</summary>
    public static string? CommandFromArgs(string[] args) =>
        args.Any(a => a.Equals("--clip", StringComparison.OrdinalIgnoreCase)) ? "clip"
        : args.Any(a => a.Equals("--trim-last", StringComparison.OrdinalIgnoreCase)) ? "trim"
        : args.Any(a => a.Equals("--share-last", StringComparison.OrdinalIgnoreCase)) ? "share"
        : null;

    /// <summary>Gives up the single-instance mutex so a replacement process can start (used when handing over to an update).</summary>
    public static void ReleaseSingleInstance()
    {
        try { s_mutex?.Dispose(); } catch { }
        s_mutex = null;
    }

    public static void SendCommand(string cmd)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            w.WriteLine(cmd);
            w.Flush();
        }
        catch (Exception ex) { Log.Warn("pipe send: " + ex.Message); }
    }

    private async Task PipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var r = new StreamReader(server);
                string? line = await r.ReadLineAsync(ct);
                if (line != null) _ = _ui.BeginInvoke(() => RunCommand(line));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("pipe server: " + ex.Message); await Task.Delay(500, ct).ContinueWith(_ => { }); }
        }
    }

    /// <summary>A control command from the pipe ("clip", "show", "trim", "share", "pause", "resume"). UI thread.</summary>
    internal void RunCommand(string cmd)
    {
        switch (cmd.Trim().ToLowerInvariant())
        {
            case "clip": ClipNow(); break;
            case "show": ShowRequested?.Invoke(); break;
            case "trim": TrimLastRequested?.Invoke(); break;
            case "share": ShareLast(); break;
            case "pause": if (!IsPaused) TogglePause(); break;
            case "resume": if (IsPaused) TogglePause(); break;
            default: Log.Warn("pipe: unknown command " + (cmd.Length > 40 ? cmd[..40] + "…" : cmd)); break;
        }
    }

    /// <summary>
    /// "--clip" / "--share-last" / "--trim-last" on a start that found no Clink running: runs it once the app is up. A clip
    /// has nothing to save yet (nothing was recording), so that one says so instead of failing silently.
    /// </summary>
    internal void RunStartupCommand(string cmd)
    {
        if (cmd != "clip") { RunCommand(cmd); return; }
        ShowBalloon("Clink started", "It was not running, so there was nothing to save yet. "
            + (GameOnly ? "It records while a game runs." : "It is recording now."));
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        Monitors = Native.Monitors.Enumerate();
        Library.Load(Settings.ClipsFolder);
        MigrateHotkeys();
        Hotkeys.Install(Settings.Hotkeys, a => _ui.BeginInvoke(() => OnHotkey(a)));
        // Hardware encoder enumeration loads vendor DLLs and can take a while on some machines: warm it on a worker so the
        // window's codec list and the first engine start find the answer ready instead of probing on the UI thread.
        Task.Run(() => { try { _ = Capabilities.Codecs; } catch (Exception ex) { Log.Warn("codec probe: " + ex.Message); } });
        Engine.StateChanged += () => _ui.BeginInvoke(RaiseStateChanged);
        _ = PipeServerAsync(_cts.Token);
        CreateTray();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), _ui);
        _timer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (Settings.RunAtStartup) SetRunAtStartup(true); // keep the Run entry pointing at wherever the exe lives now
        if (GameOnly) { Log.Info("Recording mode: only while a game or custom app runs"); PollGame(); }
        else StartEngine();
        Archive.Start();
        ApplyStatusDot();
        Updater.StartBackgroundChecks();
        // A downloaded update was skipped at startup (install failed earlier, read-only folder, elevated): tell the user, once per start.
        if (Updater.InstallProblem is string problem) { Say(problem); ShowBalloon("Clink update not installed", problem); }
        RaiseStateChanged();
    }

    /// <summary>Once a second on the UI thread: game detection, the game-only start/stop rules, and a StateChanged for the stats line.</summary>
    private void Tick()
    {
        PollGame();
        var now = DateTime.UtcNow;
        if (GameOnly)
        {
            // The mode may have been switched while the engine was running with no game: give it the same grace as a game exit.
            if (_gamePid == 0 && Engine.IsRunning && !_stopping && _gameStopAt == null && !IsPaused) _gameStopAt = now + GameExitGrace;
            if (_gameStopAt is DateTime t && now >= t)
            {
                _gameStopAt = null;
                if (_gamePid == 0 && Engine.IsRunning) { Log.Info("No game running, stopping the recorder"); StopEngine(); }
            }
        }
        // A recorder that should be running but is not: the mode was just switched, a start failed (encoder sessions taken by
        // another recorder, GPU or monitor still settling after resume / a display change) or it failed mid-run. Retry: 10 s
        // after a start that stopped without an error, 30 s / 30 s / 1 min / 2 min while starts keep failing; after a GPU reset
        // (driver crash / TDR, Engine.DeviceLost) a third of that, so recording resumes on a fresh device as soon as the driver
        // is back. StartEngine raises one balloon per failure streak.
        bool wanted = !IsPaused && (!GameOnly || _gamePid != 0);
        int failedWait = Engine.DeviceLost ? 10 : 30;
        var backoff = TimeSpan.FromSeconds(Engine.Error == null ? 10 : failedWait << Math.Clamp(_startFailures - 1, 0, 2));
        if (wanted && !Engine.IsRunning && !_starting && now - _lastStartAt > backoff)
        {
            if (Engine.DeviceLost) Say("The GPU was reset, restarting capture");
            if (Engine.Error != null) Log.Info("Retrying the recorder after: " + Engine.Error);
            StartEngine();
        }
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------ game detection (docs/SPEC-v2.md §5)

    /// <summary>Kicks off one detection pass on a worker thread (skipped while the previous one is still running).</summary>
    private void PollGame()
    {
        var mon = SelectedMonitor;
        if (mon == null) return;
        if (Interlocked.Exchange(ref _detecting, 1) == 1) return;
        uint pid = _gamePid;
        var custom = Settings.CustomApps?.ToArray() ?? Array.Empty<string>();
        Task.Run(() =>
        {
            bool alive = false;
            GameProcessTracker.GameResult? found = null;
            try
            {
                alive = pid != 0 && GameProcessTracker.IsAlive(pid);
                if (!alive) found = GameProcessTracker.DetectGame(mon, custom);
            }
            catch (Exception ex) { Log.Warn("game poll: " + ex.Message); }
            _ui.BeginInvoke(() => { _detecting = 0; OnGamePoll(pid, alive, found); });
        });
    }

    private void OnGamePoll(uint polledPid, bool alive, GameProcessTracker.GameResult? found)
    {
        if (polledPid != _gamePid) return;   // state moved on while we were looking
        if (alive) { _gameStopAt = null; return; }   // a game is "gone" when its process exits, not when it loses focus
        if (_gamePid != 0)
        {
            Log.Info($"Game exited: {CurrentGame}");
            GameProcessTracker.Forget(_gamePid);   // its pid may be handed to an unrelated process within seconds
            _gamePid = 0; CurrentGame = null; CurrentGameSource = null; _announceGame = null;
            Engine.SetDetectedGame(0, null);
            if (GameOnly) _gameStopAt = DateTime.UtcNow + GameExitGrace;
            RaiseStateChanged();
        }
        if (found is { } g)
        {
            _gamePid = g.Pid; CurrentGame = g.Name; CurrentGameSource = g.Source; _gameStopAt = null;
            Engine.SetDetectedGame(g.Pid, g.Name);   // the Game audio track follows this detection, not only the focused window (SPEC-v2 §5)
            Log.Info($"Game detected: {g.Name} pid={g.Pid} ({g.Source})");
            if (!IsPaused)
            {
                if (Engine.IsRunning && !_stopping) Toasts.ShowRecordingStarted(g.Name);     // always mode, or still in the grace period
                else
                {
                    // The toast follows once the engine is up: started here (game-only; this also overrides a grace-period stop
                    // that is still tearing down), or by Start()/Tick in always mode.
                    _announceGame = g.Name;
                    if (GameOnly) StartEngine();
                }
            }
            RaiseStateChanged();
        }
        Hotkeys.GamePid = _gamePid;   // a hotkey fires with extra modifiers held (crouch + ScrollLock) only in the game
    }

    // ------------------------------------------------------------------ updates

    private static bool MainWindowVisible => System.Windows.Application.Current?.MainWindow?.IsVisible == true;

    /// <summary>
    /// The user has the window in front of them: shown, not minimized, focused. Not <see cref="MainWindowVisible"/>, which a
    /// window minimized to the taskbar, or open behind a borderless game after an alt-tab, still is.
    /// </summary>
    private static bool MainWindowInFront =>
        System.Windows.Application.Current?.MainWindow is { IsVisible: true, IsActive: true, WindowState: not System.Windows.WindowState.Minimized };

    /// <summary>
    /// Why something asked for from the hotkey, the tray or the command line (no UI of their own) did not happen, shown once
    /// where the user is looking; the caller has already put it in the footer. Nothing more while the window is in front; a
    /// Clink toast (wrapped, at least 8 s) while the detected game is in front, since Windows holds tray balloons back over a
    /// game; a tray balloon otherwise.
    /// </summary>
    private void NotifyProblem(string title, string text)
    {
        if (MainWindowInFront) return;
        if (Hotkeys.GameInFront()) Toasts.ShowProblem(title, text);
        else ShowBalloon(title, text);
    }

    private void OnUpdateAvailable(UpdateInfo info)
    {
        // With auto-update off this only happens from a manual check, and the version click opens the release page itself.
        Log.Info($"update: v{info.Version} ({info.Tag}) available{(Settings.AutoUpdate ? "" : " (auto-update is off)")}");
    }

    private void OnUpdateReady(Version version)
    {
        if (_updateItem != null) { _updateItem.Text = $"Update to v{version}"; _updateItem.Visible = true; }
        string? problem = Updater.InstallProblemFor(Environment.ProcessPath ?? "");
        string msg = problem == null
            ? $"v{version} downloaded - installs on next start, or click 'Update to v{version}'"
            : $"v{version} downloaded, but {problem}. Install it by hand from {Updater.UpdatesDir}";
        Say(msg);
        if (!MainWindowVisible) ShowBalloon("Clink update ready", msg);
        UpdateStateChanged?.Invoke();
    }

    /// <summary>Installs the downloaded update now: hands over to the new exe and quits.</summary>
    public void UpdateNow()
    {
        string? pending = Updater.PendingPath;
        if (pending == null || !File.Exists(pending)) { Say("No update downloaded yet"); return; }
        string? problem = Updater.InstallProblemFor(Environment.ProcessPath ?? "");
        if (problem != null) { Say($"Cannot install v{Updater.PendingVersion}: {problem}. Install it by hand from {Updater.UpdatesDir}"); return; }
        try
        {
            Log.Info($"update: installing v{Updater.PendingVersion} from {pending}");
            // Come back the way we are now: visible if the window is open, otherwise in the tray.
            Updater.LaunchApply(pending, Environment.ProcessPath!, new[] { MainWindowVisible ? "--show" : "--minimized" });
        }
        catch (Exception ex)
        {
            Log.Error("update: launch failed", ex);
            Say("Update failed to start: " + ex.Message);
            return;
        }
        Quit();
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke(async () =>
        {
            await Task.Delay(1500);
            Monitors = Native.Monitors.Enumerate();
            Say("Display changed, restarting capture");
            StartEngine();
            ApplyStatusDot();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Monitor handles can change across sleep (a monitor that did not wake up, a dock): restart on fresh ones.
        if (e.Mode == PowerModes.Resume) _ui.BeginInvoke(async () => { await Task.Delay(3000); Monitors = Native.Monitors.Enumerate(); StartEngine(); });
    }

    public MonitorInfo? SelectedMonitor =>
        Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, Settings.MonitorDevice, StringComparison.OrdinalIgnoreCase))
        ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();

    /// <summary>Audio tracks in a clip: the mix plus each enabled part (the size arithmetic in <see cref="Capabilities"/> needs the count).</summary>
    public int AudioTrackCount => 1 + (Settings.TrackGame ? 1 : 0) + (Settings.TrackMic ? 1 : 0) + (Settings.TrackVoice ? 1 : 0) + (Settings.TrackDesktop ? 1 : 0);

    /// <summary>
    /// The engine configuration from the settings. The bitrate is what the Capture tab shows: the manual value, or the one a
    /// size target picks for the replay length. <see cref="EngineConfig.Codec"/> is the user's choice as written; it is
    /// resolved against the GPU's encoders (<see cref="Capabilities.EffectiveCodec"/>) by <see cref="StartEngine"/> off the UI thread.
    /// </summary>
    public EngineConfig BuildConfig()
    {
        var mon = SelectedMonitor ?? throw new InvalidOperationException("No monitor found");
        (uint w, uint h) = OutputSize(mon, Settings.Resolution);
        int secs = Math.Clamp(Settings.BufferSeconds, 5, 3600);
        var (mbps, _) = Capabilities.ResolveBitrate(Settings, AudioTrackCount, secs);
        return new EngineConfig
        {
            Monitor = mon,
            OutWidth = w,
            OutHeight = h,
            Fps = (uint)Math.Clamp(Settings.Fps, 15, 120),
            BitrateMbps = Math.Clamp(mbps, Capabilities.MinTargetMbps, Capabilities.MaxTargetMbps),
            // Any case or spacing ("HEVC", "h.265") is accepted, like the combos do; AV1 goes through as "av1".
            Codec = EngineConfig.NormalizeCodec(Settings.Codec),
            GopSeconds = Math.Clamp(Settings.KeyframeSeconds, 0.25f, 5f),
            BufferSeconds = secs,
            CaptureCursor = Settings.CaptureCursor,
            TrackDesktop = Settings.TrackDesktop, TrackGame = Settings.TrackGame, TrackMic = Settings.TrackMic, TrackVoice = Settings.TrackVoice,
            AllDeviceId = Settings.AllDeviceId,
            MicDeviceId = Settings.MicDeviceId,
            GameProcess = GameProcessTracker.NormalizeName(Settings.GameProcess),
            VoiceApp = GameProcessTracker.NormalizeName(Settings.VoiceApp),
            HdrKnee = Settings.HdrKnee, HdrPeak = Settings.HdrPeak,
            ClipsFolder = Settings.ClipsFolder,
        };
    }

    public static (uint w, uint h) OutputSize(MonitorInfo mon, string resolution)
    {
        int targetH = resolution.Trim().ToLowerInvariant() switch { "1440p" => 1440, "1080p" => 1080, "720p" => 720, _ => 0 };
        double w = mon.Width, h = mon.Height;
        // The preset names the short side: a portrait 1080×1920 monitor is already 1080p, and its 720p is 720×1280.
        double shortSide = Math.Min(w, h);
        if (targetH > 0 && targetH < shortSide) { double k = targetH / shortSide; w *= k; h *= k; }
        return ((uint)Math.Round(w / 2) * 2, (uint)Math.Round(h / 2) * 2);
    }

    // ------------------------------------------------------------------ engine

    /// <summary>
    /// (Re)starts the engine with the current settings. In game-only mode with no game running this stops it instead, so the
    /// UI can call it after any settings change (including the recording mode) without caring which mode is on.
    /// </summary>
    public void StartEngine()
    {
        if (IsPaused) return;
        if (GameOnly && _gamePid == 0)
        {
            if (Engine.IsRunning) StopEngine();
            RaiseStateChanged();
            return;
        }
        int gen = Interlocked.Increment(ref _startGen);
        _lastStartAt = DateTime.UtcNow;   // also when the config fails: Tick retries on its backoff, not every second
        EngineConfig cfg;
        try { cfg = BuildConfig(); }
        catch (Exception ex) { Say("Cannot start: " + ex.Message); if (_stopping) StopEngine(); return; }   // the new generation cancelled that stop: re-issue it
        _stopping = false;
        _starting = true;
        Task.Run(() =>
        {
            _engineGate.Wait();
            try
            {
                if (gen != _startGen) return;
                // A codec this GPU cannot encode (or H.264 above 4096 px) falls back to one it can (the Capture tab says so); the
                // probe runs here, not on the UI thread.
                try
                {
                    string effective = Capabilities.EffectiveCodec(cfg.Codec, cfg.OutWidth, cfg.OutHeight);
                    if (effective != cfg.Codec)
                    {
                        Log.Info(Capabilities.IsSupported(cfg.Codec)
                            ? $"H.264 cannot encode {cfg.OutWidth}x{cfg.OutHeight}, recording {effective}"
                            : $"Codec {cfg.Codec} has no hardware encoder here, recording {effective}");
                        cfg.Codec = effective;
                    }
                }
                catch (Exception ex) { Log.Warn("codec check: " + ex.Message); }
                try { Engine.Start(cfg); }
                catch (Exception ex) { Log.Error("Engine start", ex); }
            }
            finally { _engineGate.Release(); }
            _ui.BeginInvoke(() =>
            {
                if (gen == _startGen)
                {
                    // Only the newest start reports: a superseded one would show an older start's error.
                    _starting = false;
                    if (Engine.IsRunning) _startFailures = 0;
                    else if (Engine.Error != null)
                    {
                        Say("Recording failed: " + Engine.Error);
                        // The window is usually in the tray while a game runs, so the footer alone is invisible. Once per
                        // streak: Tick keeps retrying.
                        if (_startFailures++ == 0) NotifyProblem("Clink is not recording", Engine.Error);
                    }
                }
                if (Engine.IsRunning && _announceGame is string game) { _announceGame = null; Toasts.ShowRecordingStarted(game); }
                RaiseStateChanged();
            });
        });
    }

    private void StopEngine()
    {
        int gen = Interlocked.Increment(ref _startGen);   // a start that has not run yet is cancelled; a later start cancels this stop
        _starting = false;
        _startFailures = 0;
        _stopping = true;
        Task.Run(() =>
        {
            bool stopped = false;
            _engineGate.Wait();
            try { if (gen == _startGen) { Engine.Stop(); stopped = true; } }
            catch (Exception ex) { Log.Warn("Engine stop: " + ex.Message); }
            finally { _engineGate.Release(); }
            // The recorder is idle now: give the last clips' buffers back to the OS. They are gen2 / large-object garbage that
            // an idle tray app may not collect for hours (a ~20 ms pause here costs nothing).
            if (stopped) { try { GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true); } catch { } }
            _ui.BeginInvoke(() =>
            {
                if (gen == _startGen) _stopping = false;
                RaiseStateChanged();
            });
        });
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused) { StopEngine(); Say("Recording paused"); }
        else
        {
            Say(GameOnly && _gamePid == 0 ? "Recording resumed: waiting for a game" : "Recording resumed");
            _announceGame = CurrentGame;   // "Recording started · Game" once the engine is back up, when a game is running
            StartEngine();
        }
        if (_pauseItem != null) _pauseItem.Text = IsPaused ? "Resume recording" : "Pause recording";
        RaiseStateChanged();
    }

    public void Say(string msg)
    {
        LastNotice = msg;
        Log.Info("notice: " + msg);
        Notice?.Invoke(msg);
    }

    /// <summary>Updates the tray tooltip / menu and the status dot, then raises <see cref="StateChanged"/>. UI thread.</summary>
    private void RaiseStateChanged()
    {
        UpdateTray();
        if (_dot != null && Settings.StatusDot) { try { _dot.Apply(State); } catch { } }
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ hotkeys (docs/SPEC-v2.md §6)

    /// <summary>
    /// A settings file without a v2 hotkey list (v1, or a fresh install) gets the single v1 hotkey as its one "clip" binding
    /// (replay-length slider value). Runs once at start: a list the file does have is the user's, an empty one included.
    /// </summary>
    private void MigrateHotkeys()
    {
        if (Settings.HotkeysFromFile || Settings.Hotkeys.Count > 0) return;
        Settings.Hotkeys.Add(new HotkeyAction { Keys = Settings.Hotkey, Action = "clip", Seconds = 0 });
        Settings.Save();
        Log.Info($"hotkeys: migrated {Settings.Hotkey} → clip");
    }

    /// <summary>Re-reads <see cref="Settings.Hotkeys"/> after the list changed in the UI (an empty list means no hotkeys).</summary>
    public void ReloadHotkeys()
    {
        Hotkeys.Rebind(Settings.Hotkeys);
        UpdateTrayHotkey();
    }

    /// <summary>The keys of the first "clip" binding, for labels ("Press F9 to save a clip"); null when no hotkey is bound (the list may be empty by choice).</summary>
    public string? ClipHotkeyLabel =>
        (Settings.Hotkeys.FirstOrDefault(h => Hotkeys.IsClip(h.Action) && h.Keys.Vk > 0) ?? Settings.Hotkeys.FirstOrDefault(h => h.Keys.Vk > 0))?.Keys.ToString();

    /// <summary>"Clip now (F9)" for the tray menu, or just "Clip now" without a binding.</summary>
    private string ClipTrayText => ClipHotkeyLabel is string l ? $"Clip now ({l})" : "Clip now";

    private void OnHotkey(HotkeyAction a)
    {
        switch ((a.Action ?? "clip").Trim().ToLowerInvariant())
        {
            case "pause":
                TogglePause();
                // In a game nothing else shows it: a press by mistake would stop recording without a sign. A resume with a game
                // running gets its "Recording started" toast once the engine is up.
                if (!MainWindowInFront && Settings.ToastRecording.Enabled && (IsPaused || CurrentGame == null))
                    Toasts.ShowNotice(IsPaused ? "Recording paused" : "Recording resumed", IsPaused ? $"Press {a.Keys} to resume" : GameOnly ? "Waiting for a game" : "Replay buffer restarted");
                break;
            case "share-last": case "share": ShareLast(); break;
            default: ClipNow(a.Seconds > 0 ? a.Seconds : null); break;
        }
    }

    // ------------------------------------------------------------------ clips

    /// <param name="seconds">Replay length to save; null = the replay-length slider (<see cref="Settings.BufferSeconds"/>).</param>
    public async void ClipNow(int? seconds = null)
    {
        if (!Engine.IsRunning)
        {
            bool waiting = !IsPaused && GameOnly && _gamePid == 0;
            // A recorder that failed to start says why: "yet" would promise a start that may never come.
            string why = IsPaused ? "Recording is paused"
                : waiting ? "Not recording: waiting for a game"
                : Engine.Error is string err && !_starting ? "Recording failed: " + err
                : "Not recording yet";
            Say(why);
            // The hotkey, the tray item and --clip have no UI of their own and the window is usually in the tray, minimized or
            // behind the game while one runs: a footer line alone would make a missed clip look saved, so say it where the user
            // notices unless the window is in front of them (with an error sound when the click sound is on: no click played).
            if (!MainWindowInFront)
            {
                if (Settings.ClickSound) System.Media.SystemSounds.Exclamation.Play();
                NotifyProblem("Clip not saved", waiting ? why + ". If your game is not detected, add it under Capture → Recording → Custom apps." : why);
            }
            return;
        }
        if ((DateTime.UtcNow - _lastClipAt).TotalMilliseconds < 700) return;
        if (Interlocked.Exchange(ref _saving, 1) == 1) return;
        _lastClipAt = DateTime.UtcNow;
        if (Settings.ClickSound) ClickSound.Play(Settings.ClickVolume);
        try
        {
            int? length = seconds is int s && s > 0 ? Math.Clamp(s, 1, 3600) : null;
            // The engine names the clip after its own game; the app's detection fills in when it has none (device Game track, custom app).
            var result = await Engine.SaveClipAsync(length, CurrentGame);
            // The names of the tracks the file really has, in file order (the trim editor labels and keeps tracks by them).
            var entry = Library.Add(result.Path, result.DurationSeconds, result.SizeBytes, result.Game, result.AudioTracks);
            Say($"Clip saved ({result.DurationSeconds:0}s)");
            Toasts.ShowClipSaved(entry);
            Archive.NotifyClipSaved();
            ClipSaved?.Invoke(entry);
            // The save copied the clip into managed buffers that are all garbage now: a background GC lets the next save reuse
            // that space instead of growing the process by another clip.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        }
        catch (Exception ex)
        {
            Log.Error("Save clip", ex);
            Say("Clip failed: " + ex.Message);
            // The click has already played: without this a failed save looks like a saved clip.
            NotifyProblem("Clip not saved", ex.Message);
        }
        finally { _saving = 0; }
    }

    /// <summary>Shares the newest clip (the "share last clip" hotkey / tray item): uploads it, or copies its link if it already has one.</summary>
    public async void ShareLast()
    {
        // "Clip, then share" (a Stream Deck multi-action, --clip then --share-last, two quick presses) means the clip being saved
        // right now: the library only gets it once the save is done, so wait for that instead of sharing the one before it.
        for (int i = 0; i < 100 && Volatile.Read(ref _saving) == 1; i++) await Task.Delay(100);
        var entry = Library.Entries.FirstOrDefault(e => e.IsShared || e.IsArchived || e.Exists);
        bool ok = false, joined = false;
        if (entry == null) Say("No clips to share yet");
        else
        {
            // A second press during the same upload gets the same result: the press that started it reports a failure, once.
            joined = _uploads.TryGetValue(entry, out var running) && running.Announced;
            ok = await ShareAsync(entry, announce: true);
        }
        // The hotkey, the tray item and --share-last have no UI of their own, and the window is usually in the tray, minimized or
        // behind the game: a refusal (or a failure) that only reached the footer would be invisible (NotifyProblem: a toast over
        // the game, a tray balloon otherwise).
        if (!ok && !joined && LastNotice.Length > 0) NotifyProblem("Clip not uploaded", LastNotice);
    }

    /// <param name="announce">From the hotkey / tray / --share-last, which have no progress UI of their own: unless the window is in front, a toast says the upload started.</param>
    public async Task<bool> ShareAsync(ClipEntry entry, IProgress<double>? progress = null, bool announce = false)
    {
        ShareRefusal = null;
        ShareError = null;
        if (!string.IsNullOrEmpty(entry.CopiumLink))
        {
            CopyToClipboard(entry.CopiumLink);
            Say("Link copied");
            Toasts.ShowShared(entry, entry.CopiumLink);
            return true;
        }
        string? token = Settings.CopiumToken;
        if (string.IsNullOrWhiteSpace(token)) { ShareError = "Add your Copium token first (Clip tab)"; Say(ShareError); ShowRequested?.Invoke(); return false; }
        // Copium refuses files over 200 MB or 10 min without Copium+: say so now instead of uploading for a while and failing.
        if (!Settings.CopiumPlus && (entry.Size > Capabilities.CopiumFreeBytes || entry.Duration > Capabilities.CopiumFreeSeconds))
        {
            // Rounded up: a full 10:00 replay saves a moment over 10 min (it starts on a keyframe) and must not read "10:00".
            int secs = (int)Math.Ceiling(Math.Max(0, entry.Duration));
            string length = entry.Duration > 0 ? $" / {secs / 60}:{secs % 60:00}" : "";   // folder-scanned clips have no duration
            ShareRefusal = "Over Copium's free limit (200 MB / 10 min)";
            Say($"Copium free accepts up to 200 MB / 10 min; this clip is {Capabilities.FormatBytes(entry.Size)}{length} - trim it, or tick 'I have Copium+' if you do");
            return false;
        }
        // One upload per clip: the hotkey, the tray, the toast and the clip list can all ask for the same clip while it is going
        // up (nothing marks it shared until it is done). A second ask waits for the running upload and gets its result.
        if (_uploads.TryGetValue(entry, out var running))
        {
            Say("Already uploading this clip…");
            if (announce && !MainWindowInFront) Toasts.ShowUploading(entry, "Already uploading to Copium…");
            return await running.Task;
        }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uploads[entry] = (done.Task, announce);
        bool ok = false;
        try
        {
            Say("Uploading to Copium…");
            if (announce && !MainWindowInFront) Toasts.ShowUploading(entry);
            string title = (entry.Game ?? "Clip") + " " + entry.Created.ToString("yyyy-MM-dd HH:mm");
            string path = entry.Path;
            // Off the UI thread: opening an archived clip on an unreachable share blocks in the FileStream constructor for ~20 s.
            // The callers' Progress<T> were made on the UI thread and still report there.
            var up = await Task.Run(() => CopiumClient.UploadAsync(token, path, title, progress, _cts.Token));
            entry.CopiumLink = up.Link;
            entry.CopiumPageUrl = up.PageUrl;
            entry.CopiumId = up.Id;
            Library.Update(entry);
            CopyToClipboard(up.Link);
            Say("Uploaded, link copied");
            Toasts.ShowShared(entry, up.Link);
            ok = true;
        }
        catch (Exception ex)
        {
            Log.Error("Copium upload", ex);
            ShareRefusal = null;   // another clip's Share may have been refused while this one was going up
            ShareError = ex.Message;
            Say("Upload failed: " + ex.Message);
        }
        finally
        {
            _uploads.Remove(entry);
            done.SetResult(ok);
        }
        return ok;
    }

    /// <summary>Asks the window to open the trim editor on a clip. False when no window has subscribed to <see cref="TrimRequested"/>.</summary>
    public bool RequestTrim(ClipEntry entry)
    {
        var h = TrimRequested;
        if (h == null) return false;
        _ui.BeginInvoke(() => h(entry));
        return true;
    }

    public void CopyToClipboard(string text)
    {
        try { _ui.Invoke(() => Clipboard.SetDataObject(text, true)); }
        catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); }
    }

    public void OpenFolder(string? file = null)
    {
        try
        {
            if (file != null && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else { Directory.CreateDirectory(Settings.ClipsFolder); Process.Start("explorer.exe", $"\"{Settings.ClipsFolder}\""); }
        }
        catch (Exception ex) { Say("Could not open folder: " + ex.Message); }
    }

    public void SaveSettings() => Settings.Save();

    public void SetRunAtStartup(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("Clink", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("Clink", false);
        }
        catch (Exception ex) { Say("Startup setting failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ status dot (docs/SPEC-v2.md §7)

    /// <summary>Shows or hides the status dot per <see cref="Settings.StatusDot"/> / <see cref="Settings.StatusDotPosition"/> on the captured monitor. UI thread.</summary>
    public void ApplyStatusDot()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(ApplyStatusDot); return; }
        try
        {
            if (!Settings.StatusDot) { _dot?.Hide(); return; }
            _dot ??= new StatusDotWindow();
            _dot.Apply(State);
            _dot.Place(SelectedMonitor, Settings.StatusDotPosition);
        }
        catch (Exception ex) { Log.Warn("status dot: " + ex.Message); }
    }

    // ------------------------------------------------------------------ tray

    private void CreateTray()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            _statusItem = new System.Windows.Forms.ToolStripMenuItem("Starting…") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _clipItem = new System.Windows.Forms.ToolStripMenuItem(ClipTrayText, null, (_, _) => _ui.BeginInvoke(() => ClipNow()));
            menu.Items.Add(_clipItem);
            menu.Items.Add("Share last clip", null, (_, _) => _ui.BeginInvoke(ShareLast));
            menu.Items.Add("Show Clink", null, (_, _) => ShowRequested?.Invoke());
            _pauseItem = new System.Windows.Forms.ToolStripMenuItem("Pause recording", null, (_, _) => TogglePause());
            menu.Items.Add(_pauseItem);
            menu.Items.Add("Open clips folder", null, (_, _) => OpenFolder());
            _updateItem = new System.Windows.Forms.ToolStripMenuItem("Update Clink", null, (_, _) => UpdateNow()) { Visible = false };
            menu.Items.Add(_updateItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Clink",
                Visible = true,
                ContextMenuStrip = menu,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowRequested?.Invoke(); };
        }
        catch (Exception ex) { Log.Warn("tray: " + ex.Message); }
    }

    private void UpdateTray()
    {
        if (_tray == null) return;
        string status = StatusText;
        if (State == RecordingState.Error && Engine.Error != null) status = "Error: " + Engine.Error;
        string text = "Clink · " + status;
        if (text.Length > 120) text = text[..119] + "…";   // NotifyIcon.Text throws above 127 characters
        if (text != _trayText)
        {
            _trayText = text;
            try { _tray.Text = text; } catch { }
            if (_statusItem != null) _statusItem.Text = status;
        }
    }

    public void UpdateTrayHotkey() { if (_clipItem != null) _clipItem.Text = ClipTrayText; }

    public void ShowBalloon(string title, string text)
    {
        if (text.Length > 250) text = text[..249] + "…";   // Shell_NotifyIcon cuts balloon text at 256 characters
        try { _tray?.ShowBalloonTip(3000, title, text, System.Windows.Forms.ToolTipIcon.None); } catch { }
    }

    public void Quit()
    {
        _ui.BeginInvoke(() =>
        {
            // A throw while cleaning up must not leave a process with no tray icon running on.
            try { Dispose(); }
            finally { System.Windows.Application.Current.Shutdown(); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;   // Quit disposes, then App.OnExit does again
        _disposed = true;
        Log.Info("Quitting");
        _cts.Cancel();
        _timer?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { Hotkeys.Uninstall(); } catch { }
        try { Archive.Stop(); } catch { }
        try { Toasts.CloseAll(); } catch { }
        try { _dot?.Close(); _dot = null; } catch { }
        try { Updater.Dispose(); } catch { }
        try { Engine.Dispose(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        ReleaseSingleInstance();
    }
}
