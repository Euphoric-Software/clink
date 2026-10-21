using System.Diagnostics;
using System.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Clink.Audio;
using Clink.Capture;
using Clink.Encode;
using Clink.Native;

namespace Clink.Engine;

internal sealed class EngineConfig
{
    public MonitorInfo Monitor = null!;
    public uint OutWidth, OutHeight;
    public uint Fps = 60;
    /// <summary>Video bitrate; fractional values are fine (a Discord size target can land below 1 Mbps).</summary>
    public double BitrateMbps = 25;
    /// <summary>"h264" | "hevc" | "av1" (AV1 is experimental). Normalized by <see cref="NormalizeCodec"/>.</summary>
    public string Codec = "h264";
    /// <summary>Alias of <see cref="Codec"/> == "hevc"; setting it only switches between h264 and hevc (so `Codec = "av1"; Hevc = false` keeps AV1).</summary>
    public bool Hevc { get => Codec == "hevc"; set { if (value) Codec = "hevc"; else if (Codec == "hevc") Codec = "h264"; } }
    public float GopSeconds = 1f;
    public int BufferSeconds = 120;
    public bool CaptureCursor = true;
    // Track 1 is always the mix of game + mic + voice. These flags say whether each part is also written as its own track.
    public bool TrackGame = true, TrackMic = true, TrackVoice = false;
    public bool TrackDesktop = false;      // extra track: everything you hear (loopback of an output device), or an audio input
    public string? AllDeviceId;            // Desktop track: "out:<id>" / bare render id = loopback of that output, "in:<id>" = capture device; null = default output
    public string? MicDeviceId;
    public string GameProcess = "auto";    // process name (no .exe) for the Game track, or "auto" = detect the fullscreen app
    public string VoiceApp = "auto";
    public float HdrKnee = 0.75f, HdrPeak = 4f;
    public string ClipsFolder = "";

    /// <summary>"h264" / "hevc" / "av1" in any case or spacing; anything else (or null) is h264.</summary>
    public static string NormalizeCodec(string? codec)
    {
        string c = (codec ?? "").Trim().ToLowerInvariant().Replace(".", "").Replace(" ", "");
        return c switch { "hevc" or "h265" or "x265" => "hevc", "av1" or "av01" => "av1", _ => "h264" };
    }
}

/// <param name="Detail">Running: what the track captures beyond its name (device, process, fallback), or null. Not running: why.</param>
internal sealed record AudioTrackStatus(string Name, bool Running, string? Detail);

/// <param name="RingBytes">Bytes the ring actually holds (after the RAM clamp / allocation retry).</param>
/// <param name="RingSeconds">Replay length the ring can really keep at the configured bitrate (equals BufferSeconds unless it was clamped).</param>
/// <param name="Codec">"h264" | "hevc" | "av1".</param>
internal sealed record EngineStats(
    string Encoder, long FramesIn, long FramesOut, long Dropped, long CaptureFrames,
    double BufferedSeconds, long RingBytes, IReadOnlyList<AudioTrackStatus> Audio, string? Game, bool Hdr,
    int RingSeconds, string Codec);

/// <summary>Runs the whole replay-buffer pipeline: capture → convert → encode → ring, plus the audio tracks.</summary>
internal sealed unsafe class Engine : IDisposable
{
    private EngineConfig _cfg = new();
    private Thread? _videoThread, _audioThread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _ready = new();

    private D3DDevice? _d3d;
    private MonitorCapture? _capture;
    private FrameConverter? _converter;
    private VideoEncoder? _encoder;
    private RingStore? _ring;
    private readonly List<AudioTrack> _tracks = new();
    private readonly object _audioGate = new();
    private long _pacerDropped;
    private long _ticks;
    private bool _firstTick = true;
    private uint _gamePid; private string? _gameName;
    private uint _voicePid; private string? _voiceName;
    private AudioTrack? _gameTrack, _voiceTrack;
    private int _ringSeconds;
    private sealed record GameHint(uint Pid, string Name);
    private volatile GameHint? _gameHint;   // the app's own game detection (AppController), see SetDetectedGame; read by the audio thread
    private uint _gameHintSeen;             // the hint pid the audio thread last looked at (a new one is adopted once)

    public string Status { get; private set; } = "Stopped";
    public string? Error { get; private set; }
    public bool IsRunning { get; private set; }
    /// <summary>
    /// The engine stopped because the GPU was lost (driver crash / TDR, driver update), and has not recorded since: set by the
    /// failure, kept through failed restarts, cleared once it records again. The controller keeps restarting it while set.
    /// </summary>
    public bool DeviceLost { get; private set; }
    public string? GameName => _gameName;
    public event Action? StateChanged;

    public EngineConfig Config => _cfg;

    public void Start(EngineConfig cfg)
    {
        Stop();
        cfg.Codec = EngineConfig.NormalizeCodec(cfg.Codec);
        _cfg = cfg;
        _ringSeconds = cfg.BufferSeconds;
        _stop = false;
        Error = null;
        // Per-run state: the game/voice picks may have changed (a device now, another app), and the stats count this run only.
        _gamePid = 0; _gameName = null; _voicePid = 0; _voiceName = null; _gameHintSeen = 0;
        Interlocked.Exchange(ref _pacerDropped, 0);
        _ticks = 0;
        _firstTick = true;
        _ready.Reset();
        SetStatus("Starting…");
        _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "Clink.Video", Priority = ThreadPriority.AboveNormal };
        _videoThread.SetApartmentState(ApartmentState.MTA);
        _videoThread.Start();
        _ready.Wait(15000);
    }

    public void Stop()
    {
        if (_videoThread == null) return;
        _stop = true;
        _videoThread.Join(5000);
        _audioThread?.Join(3000);
        _videoThread = null;
        _audioThread = null;
        IsRunning = false;
        SetStatus("Stopped");
    }

    /// <summary>
    /// The app's own game detection (fullscreen, game-library window or custom app; pid 0 = none running). With the game
    /// process on "auto" the Game track records it unless a fullscreen window on the captured monitor is another app (a
    /// windowed custom app or library game need not be the focused window); with the Game track on a device it still names
    /// the game. Any thread.
    /// </summary>
    public void SetDetectedGame(uint pid, string? name) => _gameHint = pid != 0 && !string.IsNullOrEmpty(name) ? new GameHint(pid, name) : null;

    private void SetStatus(string s) { Status = s; StateChanged?.Invoke(); }

    private void VideoLoop()
    {
        HANDLE timer = default;
        try
        {
            var m = _cfg.Monitor;
            _d3d = D3DDevice.CreateForMonitor(m.Handle);
            Log.Info($"D3D device on {_d3d.AdapterName}; monitor {m.Label} HDR={m.IsHdr} sdrWhite={m.SdrWhiteNits}nits");
            _capture = new MonitorCapture(_d3d, m.Handle, m.IsHdr, _cfg.CaptureCursor, _cfg.Fps);
            _converter = new FrameConverter(_d3d, _cfg.OutWidth, _cfg.OutHeight)
            {
                IsHdr = m.IsHdr, SdrWhiteNits = m.SdrWhiteNits, Knee = _cfg.HdrKnee, Peak = _cfg.HdrPeak,
            };
            double mbps = Math.Clamp(_cfg.BitrateMbps, 0.1, 500);
            uint bitrate = (uint)Math.Round(mbps * 1_000_000);
            _encoder = new VideoEncoder(_d3d, new VideoEncoderConfig(_cfg.OutWidth, _cfg.OutHeight, _cfg.Fps, bitrate, _cfg.Codec, _cfg.GopSeconds));

            // Ring: what the replay length needs at this bitrate, clamped to half of physical RAM; a failed allocation is retried
            // smaller inside RingStore. The effective replay length is what the ring really holds, and is what the UI shows.
            long want = RingStore.BytesFor(mbps, _cfg.BufferSeconds);
            long max = RingStore.MaxBytes();
            long ringBytes = Math.Min(want, max);
            if (ringBytes < want)
                Log.Warn($"Ring buffer clamped to {ringBytes >> 20} MB (half of {RingStore.PhysicalRamBytes() >> 20} MB physical RAM); {_cfg.BufferSeconds}s at {mbps:0.#} Mbps would need {want >> 20} MB");
            _ring = new RingStore(ringBytes);
            if (_ring.Capacity < ringBytes)
                Log.Warn($"Ring buffer allocation fell back to {_ring.Capacity >> 20} MB of the {ringBytes >> 20} MB wanted");
            _ringSeconds = _ring.Capacity < want ? Math.Min(_cfg.BufferSeconds, RingStore.SecondsFor(mbps, _ring.Capacity)) : _cfg.BufferSeconds;
            if (_ringSeconds < _cfg.BufferSeconds)
                Log.Warn($"Replay length limited to {_ringSeconds}s (asked for {_cfg.BufferSeconds}s)");
            else
                Log.Info($"Ring buffer {_ring.Capacity >> 20} MB for {_cfg.BufferSeconds}s at {mbps:0.#} Mbps ({_cfg.Codec}{(_cfg.Codec == "av1" ? ", experimental" : "")})");
            var ring = _ring;
            _encoder.OnOutput = (pts, key, data) => ring.Push(pts, key, data);

            StartAudio();

            IsRunning = true;
            DeviceLost = false;
            SetStatus("Recording");
            _ready.Set();

            timer = PInvoke.CreateWaitableTimerEx(null, null, PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, 0x1F0003);
            if (timer.IsNull) timer = PInvoke.CreateWaitableTimerEx(null, null, 0, 0x1F0003);
            long period = 10_000_000L / _cfg.Fps;
            long next = ComUtil.QpcNow100ns() + period;
            while (!_stop)
            {
                long now = ComUtil.QpcNow100ns();
                long wait = next - now;
                if (wait > 0)
                {
                    long due = -wait;
                    PInvoke.SetWaitableTimer(timer, &due, 0, null, null, false);
                    PInvoke.WaitForSingleObject(timer, 200);
                }
                else if (-wait > 50_000_000) // > 5 s behind: sleep/stall; drop stale buffer and resync
                {
                    Log.Info("Pacer stalled; resyncing");
                    _ring.Clear();
                    next = ComUtil.QpcNow100ns();
                }
                Tick();
                next += period;
                long behind = ComUtil.QpcNow100ns() - next;
                if (behind > period * 4)
                {
                    // We're behind (this thread or the GPU stalled): skip ahead instead of bursting, and count the frames the clip
                    // misses as dropped. More than 5 s behind is a sleep/resume, not dropped frames.
                    if (behind < 50_000_000) Interlocked.Add(ref _pacerDropped, behind / period);
                    next = ComUtil.QpcNow100ns() + period;
                }
                if (++_ticks % (_cfg.Fps * 10) == 0)
                {
                    ThrowIfDeviceLost();   // also catches a lost GPU while the encoder pool is empty and nothing reaches Convert
                    var enc = _encoder; var cap = _capture;
                    if (enc != null && cap != null)
                        Log.Info($"stats: capture {cap.FramesReceived} (copied {cap.FramesCopied}), encoded in={enc.FramesIn} out={enc.FramesOut}, dropped pacer={Interlocked.Read(ref _pacerDropped)} queue-full={enc.Dropped}, ring holds {RingRangeSeconds:0.0}s");
                }
            }
        }
        catch (Exception ex)
        {
            // A lost GPU (driver crash / TDR, driver update) fails whichever call touches it next: say that instead of the call's
            // HRESULT, and flag it so the controller starts the engine again on a fresh device.
            HRESULT removed = _d3d?.RemovedReason ?? default;
            if (removed.Failed) DeviceLost = true;
            Error = removed.Failed ? "The GPU was reset (graphics driver crash or TDR)" : ex.Message;
            Log.Error(removed.Failed ? $"Engine stopped: GPU device removed (0x{(uint)removed.Value:X8})" : "Engine failed", ex);
            SetStatus("Error");
            _ready.Set();
        }
        finally
        {
            _stop = true;
            if (!timer.IsNull) PInvoke.CloseHandle(timer);
            try { _audioThread?.Join(3000); } catch { }
            TearDown();
            IsRunning = false;
        }
    }

    private void Tick()
    {
        var capture = _capture!; var enc = _encoder!; var conv = _converter!;
        if (!capture.HasFrame) return;
        long t = ComUtil.QpcNow100ns();
        bool firstTick = _firstTick;
        _firstTick = false;
        if (firstTick) Log.Info("Pacer: first tick, acquiring encoder input");
        if (!enc.TryAcquireInput(out IMFSample* sample, out ID3D11Texture2D* tex, out uint sub))
        {
            Interlocked.Increment(ref _pacerDropped);
            return;
        }
        if (firstTick) Log.Info($"Pacer: got input texture (subresource {sub}), converting");
        bool ok;
        try
        {
            ok = capture.WithLatest((src, w, h) => conv.Convert((ID3D11Texture2D*)src, tex, sub));
            if (firstTick) Log.Info($"Pacer: converted={ok}, submitting");
        }
        catch (Exception ex)
        {
            Log.Warn("Convert failed: " + ex.Message);
            ok = false;
        }
        tex->Release();
        if (ok) enc.Submit(sample, t);
        else sample->Release();
        // After a GPU reset every convert fails: stop rather than show "Recording" while nothing reaches the ring.
        if (!ok) ThrowIfDeviceLost();
    }

    private void ThrowIfDeviceLost()
    {
        HRESULT reason = _d3d?.RemovedReason ?? default;
        if (reason.Failed) throw new InvalidOperationException($"GPU device removed (0x{(uint)reason.Value:X8})");
    }

    private void TearDown()
    {
        try { _capture?.Dispose(); } catch (Exception ex) { Log.Warn("capture dispose: " + ex.Message); }
        _capture = null;
        try { _encoder?.Dispose(); } catch (Exception ex) { Log.Warn("encoder dispose: " + ex.Message); }
        _encoder = null;
        try { _converter?.Dispose(); } catch { }
        _converter = null;
        lock (_audioGate)
        {
            foreach (var t in _tracks) { try { t.Dispose(); } catch { } }
            _tracks.Clear();
            _gameTrack = _voiceTrack = null;
            _gameIsDevice = _voiceIsDevice = false;
        }
        try { _ring?.Dispose(); } catch { }
        _ring = null;
        try { _d3d?.Dispose(); } catch { }
        _d3d = null;
    }

    // ------------------------------------------------------------------ audio

    private void StartAudio()
    {
        lock (_audioGate)
        {
            // Game, mic and voice are always captured: track 1 is their mix. The flags only decide whether each is also
            // written as its own track. A game/voice "process" setting may instead name an audio device (in:/out: prefix).
            // Each track joins _tracks as soon as it exists, so TearDown disposes a partly built set when a later one fails.
            _gameTrack = new AudioTrack("Game") { Enabled = _cfg.TrackGame };
            _tracks.Add(_gameTrack);
            var mic = new AudioTrack("Mic") { Source = new EndpointSource(false, _cfg.MicDeviceId, "Microphone"), Enabled = _cfg.TrackMic };
            _tracks.Add(mic);
            _voiceTrack = new AudioTrack("Voice") { Enabled = _cfg.TrackVoice };
            _tracks.Add(_voiceTrack);
            if (TryDeviceSource(_cfg.GameProcess, "Game device", out var gameDevice)) { _gameTrack.Source = gameDevice; _gameIsDevice = true; }
            if (TryDeviceSource(_cfg.VoiceApp, "Voice device", out var voiceDevice)) { _voiceTrack.Source = voiceDevice; _voiceIsDevice = true; }
            var mix = new AudioTrack("Mix", new[] { _gameTrack, mic, _voiceTrack }) { Enabled = true };
            _tracks.Insert(0, mix);   // always track 1
            if (_cfg.TrackDesktop)
            {
                // "in:<id>" = a capture device (a Wave Link mix, a virtual cable), "out:<id>" = that output's loopback; a bare id from
                // an older settings file is an output loopback, null the default output.
                if (!TryDeviceSource(_cfg.AllDeviceId, "Desktop device", out var desktopDevice)) desktopDevice = new EndpointSource(true, _cfg.AllDeviceId, "System loopback");
                _tracks.Add(new AudioTrack("Desktop") { Source = desktopDevice });
            }
        }
        if (_tracks.Count == 0) return;
        _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "Clink.Audio", Priority = ThreadPriority.Highest };
        _audioThread.SetApartmentState(ApartmentState.MTA);
        _audioThread.Start();
    }

    private void AudioLoop()
    {
        uint taskIndex = 0;
        var avrt = PInvoke.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        var lastPad = Stopwatch.StartNew();
        var lastProc = Stopwatch.StartNew();
        var lastVoice = new Stopwatch();
        UpdateProcessSources(true);
        try
        {
            while (!_stop)
            {
                lock (_audioGate)
                {
                    foreach (var t in _tracks)
                    {
                        var src = t.Source;
                        if (src == null) continue;
                        if (src.EnsureStarted()) src.Pump(t.OnPacket);
                    }
                    if (lastPad.ElapsedMilliseconds >= 100)
                    {
                        lastPad.Restart();
                        long now = ComUtil.QpcNow100ns();
                        long keep = now - (_cfg.BufferSeconds + 6) * 10_000_000L;
                        long until = now - 3_000_000;
                        foreach (var t in _tracks) t.PadSilenceTo(until);   // sources first, so the mix can read them
                        foreach (var t in _tracks) t.MixUpTo(until);
                        foreach (var t in _tracks) t.Trim(keep);
                    }
                }
                if (lastProc.ElapsedMilliseconds >= 1000)
                {
                    lastProc.Restart();
                    UpdateProcessSources(false);
                }
                Thread.Sleep(8);
            }
        }
        catch (Exception ex) { Log.Error("Audio loop", ex); }
        finally
        {
            if (!avrt.IsNull) PInvoke.AvRevertMmThreadCharacteristics(avrt);
        }
    }

    private int _voiceScan;
    private bool _gameIsDevice, _voiceIsDevice;

    /// <summary>"in:&lt;endpoint id&gt;" = a capture device, "out:&lt;endpoint id&gt;" = loopback of an output; anything else is a process name or "auto".</summary>
    private static bool TryDeviceSource(string? value, string name, out EndpointSource? source)
    {
        source = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string v = value.Trim();
        if (v.StartsWith("in:", StringComparison.OrdinalIgnoreCase) && v.Length > 3) { source = new EndpointSource(false, v[3..], name); return true; }
        if (v.StartsWith("out:", StringComparison.OrdinalIgnoreCase) && v.Length > 4) { source = new EndpointSource(true, v[4..], name); return true; }
        return false;
    }

    private void UpdateProcessSources(bool initial)
    {
        if (_gameTrack == null && _voiceTrack == null) return;
        var m = _cfg.Monitor;
        try
        {
            // Voice app (checked every 5 s). With the voice track on a device, auto-detection still runs so the game
            // track's "everything except the voice app" fallback keeps working.
            if (initial || _voiceScan++ % 5 == 0 || (_voicePid != 0 && !GameProcessTracker.IsAlive(_voicePid)))
            {
                var v = GameProcessTracker.FindVoiceApp(_voiceIsDevice ? "auto" : _cfg.VoiceApp);
                uint newVoice = v?.pid ?? 0;
                if (newVoice != _voicePid) { _voicePid = newVoice; _voiceName = v?.name; Log.Info($"Voice app: {(_voiceName ?? "none")} pid={_voicePid}"); }
            }
            // Game
            var hint = _gameHint;
            if (_gameIsDevice)
            {
                // The track records a device; the app's detection still says which game is running (stats, clip names).
                uint pid = hint?.Pid ?? 0;
                if (pid != _gamePid) { _gamePid = pid; _gameName = hint?.Name; }
            }
            else if (!GameProcessTracker.IsAuto(_cfg.GameProcess))
            {
                var chosen = GameProcessTracker.FindProcessByName(_cfg.GameProcess);
                uint pid = chosen?.pid ?? 0;
                if (pid != _gamePid) { _gamePid = pid; _gameName = chosen?.name; Log.Info(pid != 0 ? $"Game (manual): {_gameName} pid={pid}" : $"Game (manual): {_cfg.GameProcess} not running"); }
            }
            else
            {
                var fg = GameProcessTracker.Detect(m.Left, m.Top, m.Left + m.Width, m.Top + m.Height, m.Handle);
                bool newHint = hint != null && hint.Pid != _gameHintSeen;   // taken once when the app finds a new game, again whenever there is none
                _gameHintSeen = hint?.Pid ?? 0;
                if (fg is { Fullscreen: true } f && f.Pid != _gamePid) { _gamePid = f.Pid; _gameName = f.Name; Log.Info($"Game detected: {f.Name} pid={f.Pid}"); }
                else if (_gamePid != 0 && !GameProcessTracker.IsAlive(_gamePid)) { Log.Info($"Game exited: {_gameName}"); GameProcessTracker.Forget(_gamePid); _gamePid = 0; _gameName = null; }
                // The app's detection (docs/SPEC-v2.md §5): a game library window that is not focused, or a custom app. Never the
                // voice app or another ignored process (a custom app can be any typed name, Discord included): the Mix would sum
                // the voice tree twice and lose the "everything except" fallback that holds the game audio.
                else if (hint != null && hint.Pid != _gamePid && hint.Pid != _voicePid && !GameProcessTracker.IsIgnored(hint.Name)
                         && (_gamePid == 0 || newHint) && fg is not { Fullscreen: true } && GameProcessTracker.IsAlive(hint.Pid))
                {
                    _gamePid = hint.Pid; _gameName = hint.Name;
                    Log.Info($"Game from detection: {hint.Name} pid={hint.Pid}");
                }
                else if (_gamePid == 0 && fg is { } g && g.InLibrary)
                {
                    // A windowed foreground app counts only when it is a game (installed in a game library): "everything (except
                    // the voice app)" beats recording whatever window happens to be focused.
                    _gamePid = g.Pid; _gameName = g.Name;
                    Log.Info($"Foreground app used as game: {g.Name} pid={g.Pid} (game library)");
                }
            }
        }
        catch (Exception ex) { Log.Warn("process tracking: " + ex.Message); }

        lock (_audioGate)
        {
            if (_gameTrack != null && !_gameIsDevice)
            {
                // No game: everything except the voice app, or with no voice app everything except Clink itself (which keeps the
                // click sound out of the clip).
                (uint pid, bool include) want = _gamePid != 0 ? (_gamePid, true) : (_voicePid != 0 ? _voicePid : (uint)Environment.ProcessId, false);
                var cur = _gameTrack.Source as ProcessSource;
                bool same = cur != null && cur.ProcessId == want.pid && cur.Include == want.include;
                if (!same)
                {
                    cur?.Dispose();
                    // The detail is what the status line appends to the track name, so the "everything except" fallback is visibly
                    // not the same thing as capturing the chosen game.
                    string except = _voicePid != 0 ? $"everything except {_voiceName}" : "everything";
                    _gameTrack.Source = want.include
                        ? new ProcessSource(want.pid, true, $"Game ({_gameName})", _gameName)
                        : new ProcessSource(want.pid, false, _voicePid != 0 ? $"Everything except {_voiceName}" : "Everything", except);
                }
            }
            if (_voiceTrack != null && !_voiceIsDevice)
            {
                var cur = _voiceTrack.Source as ProcessSource;
                if (_voicePid == 0) { if (cur != null) { cur.Dispose(); _voiceTrack.Source = null; } }
                else if (cur == null || cur.ProcessId != _voicePid) { cur?.Dispose(); _voiceTrack.Source = new ProcessSource(_voicePid, true, $"Voice ({_voiceName})", _voiceName); }
            }
        }
    }

    // ------------------------------------------------------------------ clips

    /// <summary>Seconds a clip can take right now: what the ring holds, capped at the replay length (at low motion the ring holds far more).</summary>
    public double BufferedSeconds => Math.Min(RingRangeSeconds, RingSeconds);

    /// <summary>Time span the ring actually holds (for the stats log).</summary>
    private double RingRangeSeconds
    {
        get
        {
            var r = _ring?.Range();
            return r == null ? 0 : (r.Value.newest - r.Value.oldest) / 10_000_000.0;
        }
    }

    public EngineStats GetStats()
    {
        List<AudioTrackStatus> audio;
        lock (_audioGate)
            audio = _tracks.Select(t =>
            {
                if (t.IsMix) return new AudioTrackStatus(t.Name, true, "game + mic + voice, always track 1");
                string? detail = t.Source == null ? "waiting for app" : t.Source.IsRunning ? t.Source.Detail : t.Source.LastError;
                if (!t.Enabled) detail = detail == null ? "mix only" : detail + ", mix only";
                return new AudioTrackStatus(t.Name, t.Source?.IsRunning == true, detail);
            }).ToList();
        return new EngineStats(_encoder?.EncoderName ?? "", _encoder?.FramesIn ?? 0, _encoder?.FramesOut ?? 0,
            (_encoder?.Dropped ?? 0) + Interlocked.Read(ref _pacerDropped), _capture?.FramesReceived ?? 0,
            BufferedSeconds, _ring?.Capacity ?? 0, audio, _gameName, _capture?.IsHdr ?? false,
            _ring != null ? _ringSeconds : _cfg.BufferSeconds, _cfg.Codec);
    }

    /// <summary>Replay length the ring really holds (BufferSeconds unless the RAM clamp or an allocation retry shortened it).</summary>
    public int RingSeconds => _ring != null ? _ringSeconds : _cfg.BufferSeconds;

    public Task<ClipResult> SaveClipAsync(int? seconds = null, string? gameHint = null) => Task.Run(() => SaveClip(seconds, gameHint));

    /// <param name="gameHint">The game the app detected (what the UI shows); names the clip only when the engine has no game of its own.</param>
    public ClipResult SaveClip(int? seconds = null, string? gameHint = null)
    {
        var ring = _ring; var enc = _encoder;
        if (!IsRunning || ring == null || enc == null) throw new InvalidOperationException("Not recording");
        long end = ComUtil.QpcNow100ns();
        // Never more than the buffer holds: the audio timelines keep BufferSeconds (+6 s), and the video ring may hold a little
        // more than that at low motion, so a longer hotkey length would open with silent video.
        int want = Math.Clamp(seconds ?? _cfg.BufferSeconds, 1, Math.Max(1, RingSeconds));
        long length = want * 10_000_000L;
        long from = end - length;

        // Let the audio timelines catch up to "now" (packets lag by a few tens of ms).
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            bool allCaughtUp;
            lock (_audioGate) allCaughtUp = _tracks.All(t => !t.Enabled || t.TimelineEnd >= end);
            if (allCaughtUp) break;
            Thread.Sleep(15);
        }

        var video = ring.Snapshot(from, end);
        if (video.Count < 2) throw new InvalidOperationException("Nothing recorded yet, wait a moment and try again.");
        long origin = video[0].Pts;

        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio;
        lock (_audioGate)
        {
            audio = _tracks.Where(t => t.Enabled).Select(t => (
                new AudioTrackDesc { Name = t.Name, AudioSpecificConfig = t.AudioSpecificConfig, Bitrate = t.Bitrate, SampleRate = AudioTrack.Rate, Channels = 2 },
                t.Snapshot(origin, end))).ToList();
        }

        // The engine's own pick wins (live fullscreen detection or the manual Game process); the hint covers the rest (device
        // Game track, a save before the next poll picks the hint up).
        string game = _gameName ?? (string.IsNullOrEmpty(gameHint) ? "" : gameHint);
        Directory.CreateDirectory(_cfg.ClipsFolder);
        string baseName = string.IsNullOrEmpty(game) ? "Clip" : SanitizeName(game);
        string path = Path.Combine(_cfg.ClipsFolder, $"{baseName} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        int n = 1;
        while (File.Exists(path)) path = Path.Combine(_cfg.ClipsFolder, $"{baseName} {DateTime.Now:yyyy-MM-dd HH-mm-ss} ({n++}).mp4");

        var t0 = Stopwatch.StartNew();
        int frames = video.Count;
        // The writer streams the samples into the file and drops each one's bytes as it goes, so a long clip needs one copy in
        // managed memory (the snapshot), not two.
        string[] audioNames = ClipWriter.Write(path, video, enc.Codec, (int)_cfg.OutWidth, (int)_cfg.OutHeight, enc.SequenceHeader, audio, origin, _cfg.Fps);
        video.Clear();
        double duration = (end - origin) / 10_000_000.0;
        long size = new FileInfo(path).Length;
        Log.Info($"Clip saved: {path} ({duration:0.0}s, {size / 1_048_576.0:0.0} MB, {frames} frames, {audioNames.Length} audio tracks, {enc.Codec}{(enc.IsAv1 ? " experimental" : "")}, mux {t0.ElapsedMilliseconds} ms)");
        return new ClipResult(path, duration, size, string.IsNullOrEmpty(game) ? null : game, audioNames);
    }

    private static string SanitizeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 40 ? s[..40] : s;
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }
}
