using Clink.Capture;
using Clink.Encode;
using Clink.Engine;
using Clink.Native;

namespace Clink.Services;

/// <summary>
/// What the card slot is asked to record, read from the settings on the UI thread. A record: when any of it changes (a setting, another
/// card, the screen whose GPU the card engine uses) the running card engine is restarted onto it. <see cref="SavedName"/> is the card's name
/// as last shown, used when the device listing fails.
/// </summary>
internal sealed record CardRequest(string Link, string? SavedName, MonitorInfo Monitor, uint MaxWidth, uint MaxHeight, uint Fps, double BitrateMbps,
    string Codec, float GopSeconds, int BufferSeconds, bool TrackGame, bool TrackMic, bool TrackVoice, string? MicDeviceId, string VoiceApp,
    int AvOffsetMs, float HdrKnee, float HdrPeak, string ClipsFolder, LiveEncoderTuning? EncoderTuning, long RingBudgetBytes);

/// <summary>What the slot tells the controller (UI thread). The controller words it; the slot has no user text.</summary>
internal enum CardSlotEventKind { Missing, Back, Started, Failed }

internal readonly record struct CardSlotEvent(CardSlotEventKind Kind, string CardName, string Link, EngineError? Error = null, CaptureCardProblem? Problem = null, bool Hdr = false);

/// <summary>The notice a change of the card's presence asks for (<see cref="CardSlot.NextPresence"/>).</summary>
internal enum CardNotice { None, Missing, Back }

internal readonly record struct CardPresenceDecision(CardNotice Notice, bool Noticed);

/// <summary>What <see cref="CardSlot.Sync"/> is asked to do to a running card engine: restart it anyway (display change, resume), or apply a changed request.</summary>
internal readonly record struct CardSyncAsk(bool Restart, bool Apply);

/// <summary>
/// "Both" mode (<see cref="Settings.CaptureBoth"/> with a card as <see cref="Settings.CaptureSource"/>): the second engine, recording the capture
/// card, with a lifecycle of its own beside the controller's screen engine. It always records (no game-only gating), never falls back to a
/// screen: with the card missing it idles, polls the device list every <see cref="PollInterval"/> and starts again once the card is back.
/// Pause, Clips off and a restart of Clink stop it (the controller passes no request). State is kept on the UI thread; engine starts and
/// stops run on a worker behind this slot's own gate. CP3a (no-input pause) and CP5 (card recordings) hook in here later.
/// </summary>
internal sealed class CardSlot : IDisposable
{
    /// <summary>How often the devices are listed while the missing card is waited for, and how often a running card's frame count is checked.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    /// <summary>A card that is back is given this long to settle before its engine starts (as the single-source switch back does).</summary>
    public static readonly TimeSpan ReturnDelay = TimeSpan.FromMilliseconds(1500);
    /// <summary>How often a card that runs without its audio input looks for it again.</summary>
    public static readonly TimeSpan AudioCheckInterval = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------ pure rules

    /// <summary>Both mode: the setting is on and the source names a card. "Both" with the screen as the source is plain single-source screen capture.</summary>
    public static bool IsBoth(bool captureBoth, string? captureSource) => captureBoth && CaptureTarget.ParseSource(captureSource) != null;

    /// <summary>The source the controller's own engine records: the screen in Both mode, else the setting exactly as it is.</summary>
    public static string? PrimarySource(bool captureBoth, string? captureSource) => IsBoth(captureBoth, captureSource) ? CaptureTarget.SourceMonitor : captureSource;

    /// <summary>The card engine should be running (or waiting for its card): Both mode, Clips on, not paused, no restart of Clink under way. The recording mode does not count: the card always records.</summary>
    public static bool Wanted(bool both, bool clipsEnabled, bool paused, bool restarting) => both && clipsEnabled && !paused && !restarting;

    /// <summary>
    /// How long after the last start the slot tries again: 10 s after a run that ended without an error, 30 s / 60 s / 120 s while starts keep
    /// failing, and a third of that after a GPU reset. The same steps as the controller's retry for the screen engine.
    /// </summary>
    public static TimeSpan Backoff(bool failed, bool deviceLost, int failures) =>
        TimeSpan.FromSeconds(!failed ? 10 : (deviceLost ? 10 : 30) << Math.Clamp(failures - 1, 0, 2));

    /// <summary>A running card engine restarts: asked to (display change, resume), or its request changed and the caller applies changes now.</summary>
    public static bool NeedsRestart(bool restart, bool apply, CardRequest? running, CardRequest want) => restart || (apply && !Equals(running, want));

    /// <summary>
    /// A restart or an apply asked for while the slot is busy (a start, a stop, the wait before a returned card starts) is kept, merged with
    /// later asks, and acted on by the first call that finds the slot idle (the once-a-second Tick passes neither): it is never lost.
    /// </summary>
    public static CardSyncAsk Merge(CardSyncAsk pending, bool restart, bool apply) => new(pending.Restart || restart, pending.Apply || apply);

    /// <summary>The result of a start, a stall check or a poll is about this card only when it is still the card that is wanted (another one was picked meanwhile: ignore it).</summary>
    public static bool SameCard(string? link, string? wantedLink) => link != null && wantedLink != null && string.Equals(link, wantedLink, StringComparison.OrdinalIgnoreCase);

    /// <summary>Each ring's RAM budget in Both mode: half of the one-ring limit, so both together stay within it.</summary>
    public static long RingBudget(long maxBytes) => maxBytes / 2;

    /// <summary>The card's failure means it is gone (unplugged, not listed): wait for it instead of retrying on the backoff.</summary>
    public static bool IsGone(CaptureCardProblem? problem) => problem == CaptureCardProblem.NotConnected;

    /// <summary>
    /// The notice for the card's presence now. <paramref name="missing"/>: it was found missing; <paramref name="running"/>: its engine runs.
    /// One "not found" per missing stretch (pause, resume, retries do not repeat it); "back" only after a "not found" was shown.
    /// </summary>
    public static CardPresenceDecision NextPresence(bool missing, bool running, bool noticed)
    {
        if (missing) return new(noticed ? CardNotice.None : CardNotice.Missing, true);
        if (running) return new(noticed ? CardNotice.Back : CardNotice.None, false);
        return new(CardNotice.None, noticed);   // starting, failed for another reason: nothing is known about its presence
    }

    /// <summary>The card engine's configuration: the card, no Desktop track, no PC game (name or audio), named after the card, half the ring budget.</summary>
    public static EngineConfig ConfigFor(CardRequest r, VideoDevice card, string? cardAudioId) => new()
    {
        Monitor = r.Monitor,
        Device = card,
        DeviceMaxWidth = r.MaxWidth, DeviceMaxHeight = r.MaxHeight,
        OutWidth = Math.Min(r.MaxWidth, 3840u), OutHeight = Math.Min(r.MaxHeight, 2160u),   // the card's mode decides; set by the engine when it opens the card
        Fps = r.Fps,
        BitrateMbps = r.BitrateMbps,
        Codec = r.Codec,
        GopSeconds = r.GopSeconds,
        BufferSeconds = r.BufferSeconds,
        TrackDesktop = false, TrackGame = r.TrackGame, TrackMic = r.TrackMic, TrackVoice = r.TrackVoice,
        MicDeviceId = r.MicDeviceId,
        GameProcess = "auto",
        VoiceApp = r.VoiceApp,
        CardAudioId = cardAudioId,
        CardAvOffsetMs = r.AvOffsetMs,
        HdrKnee = r.HdrKnee, HdrPeak = r.HdrPeak,
        ClipsFolder = r.ClipsFolder,
        EncoderTuning = r.EncoderTuning,
        RingBudgetBytes = r.RingBudgetBytes,
        ClipBaseName = card.Name, RecordingBaseName = card.Name,
        IgnoreGameHint = true,
    };

    // ------------------------------------------------------------------ the slot

    /// <summary>The card engine. Public for the later checkpoints (clips, recordings, stats per source).</summary>
    public Engine.Engine Engine { get; } = new();
    /// <summary>The slot changed state (UI thread).</summary>
    public event Action? Changed;

    private readonly Action<Action> _post;             // runs an action on the UI thread
    private readonly Action<CardSlotEvent> _report;    // UI thread
    private readonly SemaphoreSlim _gate = new(1, 1);  // Engine.Start / Stop of this engine are not thread-safe against each other
    private int _gen;                                  // bumped by every start / stop: an older worker's result is ignored
    private CardRequest? _want;                        // what the controller asked for last (null: not wanted)
    private CardRequest? _active;                      // what the running engine (or the start in flight) was built from
    private bool _starting, _stopping, _missing, _noticed, _polling, _returning, _disposed;
    private CardSyncAsk _pending;                      // a restart / apply asked for while busy: acted on once idle (see Merge)
    private int _failures;                             // starts that failed in a row (backoff, one notice per streak)
    private DateTime _lastStartAt = DateTime.MinValue, _lastPoll = DateTime.MinValue, _nextAudioCheck;
    private long _framesSeen = -1;                     // the card's frame count at the last stall check (-1: none yet this run)
    private VideoDevice? _withoutAudio;                // the running card when none of its audio inputs was found
    private int _audioChecking;

    public CardSlot(Action<Action> post, Action<CardSlotEvent> report)
    {
        _post = post;
        _report = report;
        Engine.StateChanged += () => { try { _post(() => Changed?.Invoke()); } catch (Exception ex) { Log.Warn("card slot: state: " + ex.Message); } };
    }

    /// <summary>The card is missing and waited for (the screen engine records alone).</summary>
    public bool IsMissing => _missing;
    /// <summary>A start or stop of the card engine is under way.</summary>
    public bool IsBusy => _starting || _stopping;

    /// <summary>
    /// Once a second from the controller's Tick and after anything that may change what is wanted (UI thread). <paramref name="want"/> null:
    /// stop. Otherwise start, wait for the missing card, retry on the backoff, restart when the request changed (or <paramref name="restart"/>:
    /// a display change or resume), and watch a running card for a silent unplug and for its audio input. <paramref name="apply"/> false (the
    /// once-a-second Tick): a changed request is not applied to a running engine yet; the controller's settings restart (debounced) applies it.
    /// </summary>
    public void Sync(CardRequest? want, DateTime now, bool restart = false, bool apply = true)
    {
        if (_disposed) return;
        if (want == null)
        {
            _want = null;
            if (_starting || _missing || _returning || (Engine.IsRunning && !_stopping)) Stop();
            return;
        }
        if (_want != null && !SameCard(_want.Link, want.Link))
        {
            // Another card: a stretch of its own, and nothing the old card's engine says (its CardProblem, its frames) is about this one.
            _noticed = false;
            _missing = false;
            _active = null;
            apply = true;   // never left recording the old card until a later apply
        }
        _want = want;
        var ask = Merge(_pending, restart, apply);
        if (_starting || _stopping || _returning) { _pending = ask; return; }   // acted on once idle (a later Tick)
        _pending = default;
        if (Engine.IsRunning)
        {
            if (NeedsRestart(ask.Restart, ask.Apply, _active, want)) { Start(want, now); return; }
            CheckStall(now);
            CheckAudio(now);
            return;
        }
        if (_missing) { PollReturn(want, now); return; }
        if (_active != null && IsGone(Engine.CardProblem)) { EnterMissing(now); return; }   // unplugged while it ran, or its start found it gone
        if (NeedsRestart(ask.Restart, ask.Apply, _active, want) || now - _lastStartAt > Backoff(Engine.ErrorInfo != null, Engine.DeviceLost, _failures))
        {
            if (Engine.ErrorInfo != null) Log.Info("Card slot: retrying the capture card after: " + Engine.Error);
            Start(want, now);
        }
    }

    private void Start(CardRequest want, DateTime now)
    {
        int gen = Interlocked.Increment(ref _gen);
        _starting = true;
        _stopping = false;
        _pending = default;   // this start reads the newest request
        _active = want;
        _lastStartAt = now;
        _framesSeen = -1;
        Task.Run(() =>
        {
            VideoDevice? card = null;
            bool notListed = false;
            string? audioId = null;
            try
            {
                var devices = VideoDevices.Enumerate();   // null: the listing failed, which says nothing about the card: open it and see
                card = devices == null
                    ? new VideoDevice(want.Link, string.IsNullOrWhiteSpace(want.SavedName) ? want.Link : want.SavedName)
                    : devices.FirstOrDefault(d => string.Equals(d.Link, want.Link, StringComparison.OrdinalIgnoreCase));
                notListed = card == null;
                if (card != null)
                {
                    var audio = CardAudio.Find(card);
                    audioId = audio?.Id;
                    Log.Info(audio != null ? $"Card slot: Game track: capture card audio '{audio.Name}' ({audio.Id})" : $"Card slot: no audio input found for card {card.Name}");
                }
            }
            catch (Exception ex) { Log.Warn("card slot: listing: " + ex.Message); }
            if (card != null && !notListed)
            {
                var cfg = ConfigFor(want, card, audioId);
                try
                {
                    string effective = Capabilities.EffectiveCodec(cfg.Codec, cfg.OutWidth, cfg.OutHeight);
                    if (effective != cfg.Codec) { Log.Info($"Card slot: codec {cfg.Codec} not available here, recording {effective}"); cfg.Codec = effective; }
                }
                catch (Exception ex) { Log.Warn("card slot: codec check: " + ex.Message); }
                _gate.Wait();
                try { if (gen == Volatile.Read(ref _gen)) { Log.Info($"Card slot: starting capture card {card.Name}"); Engine.Start(cfg); } }
                catch (Exception ex) { Log.Error("Card engine start", ex); }
                finally { _gate.Release(); }
            }
            Post(() => OnStarted(gen, want.Link, card, notListed, audioId == null));
        });
    }

    private void OnStarted(int gen, string link, VideoDevice? card, bool notListed, bool noAudio)
    {
        if (_disposed || gen != _gen) return;   // a newer start or stop took over
        _starting = false;
        var now = DateTime.UtcNow;
        if (!SameCard(link, _want?.Link))
        {
            // Another card was picked while this one started: its result says nothing about the new one (no notice). The pending apply
            // (or the changed request) restarts onto the new card at the next sync.
            _pending = Merge(_pending, restart: false, apply: true);
            Changed?.Invoke();
            return;
        }
        if (notListed) EnterMissing(now);
        else if (Engine.IsRunning && card != null)
        {
            _failures = 0;
            _withoutAudio = noAudio ? card : null;
            _nextAudioCheck = now + AudioCheckInterval;
            var d = NextPresence(missing: false, running: true, _noticed);
            _noticed = d.Noticed;
            _missing = false;
            _report(new CardSlotEvent(CardSlotEventKind.Started, card.Name, card.Link, Hdr: Engine.CardMode is { Hdr: true }));
            if (d.Notice == CardNotice.Back) _report(new CardSlotEvent(CardSlotEventKind.Back, card.Name, card.Link));
        }
        else if (IsGone(Engine.CardProblem)) EnterMissing(now);
        else if (_failures++ == 0)
            _report(new CardSlotEvent(CardSlotEventKind.Failed, card?.Name ?? _active?.SavedName ?? "", _active?.Link ?? "", Engine.ErrorInfo, Engine.CardProblem));
        Changed?.Invoke();
    }

    /// <summary>The card is not there: its engine stops (when it runs), the slot waits for it, and the first time in this stretch the user is told.</summary>
    private void EnterMissing(DateTime now)
    {
        string link = _want?.Link ?? _active?.Link ?? "";
        if ((Engine.IsRunning && !_stopping) || _starting) StopEngine();
        _missing = true;
        _lastPoll = now;   // the first look after one interval
        var d = NextPresence(missing: true, running: false, _noticed);
        _noticed = d.Noticed;
        Log.Info("Card slot: capture card not found, waiting for it (the screen keeps recording)");
        if (d.Notice == CardNotice.Missing) _report(new CardSlotEvent(CardSlotEventKind.Missing, _want?.SavedName ?? "", link));
        Changed?.Invoke();
    }

    /// <summary>While the card is missing: every <see cref="PollInterval"/> it is looked for; once it is listed, after <see cref="ReturnDelay"/>, its engine starts.</summary>
    private void PollReturn(CardRequest want, DateTime now)
    {
        if (_polling || now - _lastPoll < PollInterval) return;
        _lastPoll = now;
        _polling = true;
        string link = want.Link;
        Task.Run(() =>
        {
            bool? listed;
            try { listed = VideoDevices.IsConnected(link); }
            catch (Exception ex) { Log.Warn("card slot: check: " + ex.Message); listed = null; }
            Post(() =>
            {
                _polling = false;
                if (_disposed || listed != true || !_missing || _want == null || !string.Equals(_want.Link, link, StringComparison.OrdinalIgnoreCase)) return;
                Log.Info($"Card slot: capture card connected again: starting it in {ReturnDelay.TotalSeconds:0.#} s");
                _returning = true;
                Task.Delay(ReturnDelay).ContinueWith(_ => Post(() =>
                {
                    if (!_returning) return;   // stopped meanwhile (Stop clears it)
                    _returning = false;
                    if (_disposed || !_missing || _want == null || !string.Equals(_want.Link, link, StringComparison.OrdinalIgnoreCase)) return;   // another card meanwhile
                    _missing = false;
                    Start(_want, DateTime.UtcNow);
                }), TaskScheduler.Default);
            });
        });
    }

    /// <summary>A card pulled while no frame was being read may never fail its read: no new frame for a while and not listed any more = gone.</summary>
    private void CheckStall(DateTime now)
    {
        if (_active is not { } running || _polling || now - _lastPoll < PollInterval) return;   // (no running card known: another was picked, not applied yet)
        _lastPoll = now;
        long frames;
        try { frames = Engine.GetStats().CaptureFrames; } catch (Exception ex) { Log.Warn("card slot: stats: " + ex.Message); return; }
        bool stalled = frames == _framesSeen;
        _framesSeen = frames;
        if (!stalled) return;
        _polling = true;
        int gen = _gen;
        string link = running.Link;   // the card the engine records, not the newest request
        Task.Run(() =>
        {
            bool? listed;
            try { listed = VideoDevices.IsConnected(link); }
            catch (Exception ex) { Log.Warn("card slot: check: " + ex.Message); listed = null; }
            Post(() =>
            {
                _polling = false;
                if (_disposed || gen != _gen || listed != false || !Engine.IsRunning || !SameCard(link, _active?.Link) || !SameCard(link, _want?.Link)) return;
                Log.Info("Card slot: capture card gone (no frames, not listed)");
                EnterMissing(DateTime.UtcNow);
            });
        });
    }

    /// <summary>A card that records without sound because its audio input was not there at the start: about every 5 s it is looked for, and once found the card engine restarts onto it.</summary>
    private void CheckAudio(DateTime now)
    {
        var card = _withoutAudio;
        if (card == null || now < _nextAudioCheck) return;
        _nextAudioCheck = now + AudioCheckInterval;
        if (Interlocked.Exchange(ref _audioChecking, 1) == 1) return;
        int gen = _gen;
        Task.Run(() =>
        {
            string? found = null;
            try { found = CardAudio.Find(card)?.Name; }
            catch (Exception ex) { Log.Warn("card slot: audio check: " + ex.Message); }
            finally { Interlocked.Exchange(ref _audioChecking, 0); }
            if (found == null) return;
            Post(() =>
            {
                if (_disposed || gen != _gen || _withoutAudio != card || _want == null || !Engine.IsRunning || _starting || _stopping) return;
                _withoutAudio = null;   // once: the start finds it again, or sets this again
                Log.Info($"Card slot: audio input found for card {card.Name}: '{found}', restarting onto it");
                Start(_want, DateTime.UtcNow);
            });
        });
    }

    /// <summary>Not wanted any more (pause, Clips off, single-source mode): the engine stops, and the waiting for a missing card ends. The "not found" notice stays given for this stretch.</summary>
    private void Stop()
    {
        _pending = default;
        _missing = false;
        _returning = false;
        _active = null;
        _failures = 0;
        _lastStartAt = DateTime.MinValue;   // wanted again (resume): start at once, not after the backoff
        Log.Info("Card slot: stopping the capture card");
        StopEngine();
        Changed?.Invoke();
    }

    private void StopEngine()
    {
        int gen = Interlocked.Increment(ref _gen);   // a start that has not run yet is cancelled; a later start cancels this stop
        _starting = false;
        _stopping = true;
        _withoutAudio = null;
        Task.Run(() =>
        {
            _gate.Wait();
            try { if (gen == Volatile.Read(ref _gen)) Engine.Stop(); }
            catch (Exception ex) { Log.Warn("Card engine stop: " + ex.Message); }
            finally { _gate.Release(); }
            Post(() =>
            {
                if (gen == _gen) _stopping = false;
                Changed?.Invoke();
            });
        });
    }

    /// <summary>A restart of Clink: the card engine stops now, on the calling thread, waiting at most <paramref name="limit"/> for a start in flight.</summary>
    public void StopNow(TimeSpan limit)
    {
        _want = null;
        _pending = default;
        _missing = _returning = _starting = _stopping = false;   // (a stop in flight is superseded: this one waits for the engine itself)
        _active = null;
        _failures = 0;
        _lastStartAt = DateTime.MinValue;   // a restart that did not happen (ContinueAfterFailedRestart) starts the card at once
        Interlocked.Increment(ref _gen);
        try
        {
            if (_gate.Wait(limit)) { try { Engine.Stop(); } finally { _gate.Release(); } }
            else Log.Warn("restart: the capture card engine was still starting; it is left to the shutdown");
        }
        catch (Exception ex) { Log.Warn("restart: could not stop the capture card engine: " + ex.Message); }
    }

    private void Post(Action action)
    {
        try { _post(action); } catch (Exception ex) { Log.Warn("card slot: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _gen);
        try { Engine.Dispose(); } catch (Exception ex) { Log.Warn("Exit: capture card engine: " + ex.Message); }
    }
}
