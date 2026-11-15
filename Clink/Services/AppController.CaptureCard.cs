using Clink.Capture;
using Clink.Engine;

namespace Clink.Services;

// Capture cards at run time: what is recorded (CaptureTarget), the fallback to a screen while the chosen card is missing, the switch
// back when it returns, and the notices. UI thread, except where a worker lists the devices.
internal sealed partial class AppController
{
    private CaptureChoice? _target;      // what the last start (or source change) resolved to
    private bool _fallbackNoticed;       // the fallback notice was shown for the card that is missing now (CaptureTarget.NextAction)
    private bool _cardPolling;           // a device listing is running on a worker
    private bool _cardSwitching;         // a switch to the fallback screen or back to the card is on its way
    private DateTime _lastCardPoll = DateTime.MinValue, _lastCardGone = DateTime.MinValue;
    private long _cardFramesSeen = -1;   // the card's frame count at the last check (-1: none yet this run)
    private readonly HashSet<string> _hdrWarned = new(StringComparer.OrdinalIgnoreCase);   // cards whose HDR warning was shown this session

    /// <summary>How often the devices are listed: while a missing card is waited for, and while the card on record delivers no frames.</summary>
    private static readonly TimeSpan CardPollInterval = TimeSpan.FromSeconds(3);
    /// <summary>A card that is back is given this long to settle (its driver, the signal) before the engine moves onto it.</summary>
    private static readonly TimeSpan CardReturnDelay = TimeSpan.FromMilliseconds(1500);
    /// <summary>Within this time of the last fast switch away from a card, a card failure waits for the normal retry instead (a card that comes and goes).</summary>
    private static readonly TimeSpan CardGoneRepeat = TimeSpan.FromSeconds(10);

    /// <summary>The capture card itself is what is recorded: it counts as "always" for the recording mode (a console runs no PC game). Not during a fallback.</summary>
    internal bool CardIsTarget => _target?.Kind == CaptureKind.Device;

    /// <summary>
    /// What the settings say to record now. The devices are listed only when the source is a card; a listing that fails keeps the card
    /// (<see cref="CaptureTarget.Resolve(string?, bool, string?, IReadOnlyList{Native.MonitorInfo}, IReadOnlyList{VideoDevice}?, string?, string?)"/>),
    /// except during a fallback for that card: it is not known to be back.
    /// </summary>
    internal CaptureChoice ResolveCapture()
    {
        var devices = CaptureTarget.ParseSource(Settings.CaptureSource) == null ? Array.Empty<VideoDevice>() : VideoDevices.Enumerate();
        return CaptureTarget.Resolve(Settings.CaptureSource, Settings.FollowPrimaryMonitor, Settings.MonitorDevice, Monitors, devices, Settings.CaptureDeviceName,
            _target?.Fallback == CaptureFallback.CardMissing ? _target.MissingLink : null);
    }

    /// <summary>Takes <paramref name="choice"/> as the target and shows the notice <see cref="CaptureTarget.NextAction"/> asks for (one per missing stretch).</summary>
    private SwitchAction ApplyCaptureChoice(CaptureChoice choice)
    {
        var decision = CaptureTarget.NextAction(_target, choice, _fallbackNoticed);
        _target = choice;
        _fallbackNoticed = decision.FallbackNoticed;
        Note? notice = decision.Action switch
        {
            SwitchAction.RestartAndNotifyFallback or SwitchAction.NotifyFallback =>
                Note.Of("Capture card not found: recording {monitor} until it is back", ("monitor", Note.Isolate(choice.Monitor?.Label ?? "-"))),
            SwitchAction.RestartAndNotifyReturn => Note.Of("Capture card is back: recording {card}", ("card", Note.Isolate(choice.Device?.Name ?? ""))),
            _ => null,
        };
        if (notice != null)
        {
            Say(notice);
            NotifyProblem("Clink", notice.Shown);
        }
        return decision.Action;
    }

    /// <summary>
    /// The capture source setting changed (the source picker): restarts only when what is recorded changes. A card that is missing while its
    /// fallback screen is the one recorded already gives the notice and keeps the recorder going.
    /// </summary>
    internal void OnCaptureSourceChanged()
    {
        var action = ApplyCaptureChoice(ResolveCapture());
        if (action is SwitchAction.None or SwitchAction.NotifyFallback && Engine.IsRunning) { RaiseStateChanged(); return; }
        StartEngine();
    }

    /// <summary>Why the card stopped the engine, in the user's language; null when it was not the card.</summary>
    private Note? CardProblemNote()
    {
        string card = _target?.Device?.Name ?? Settings.CaptureDeviceName ?? "";
        return Engine.CardProblem switch
        {
            CaptureCardProblem.NotConnected => Note.Of("Capture card {card} is not connected", ("card", Note.Isolate(card))),
            CaptureCardProblem.InUse => Note.Of("Capture card {card} is in use by another app", ("card", Note.Isolate(card))),
            CaptureCardProblem.Blocked => Note.Of("Windows blocks the capture card: allow camera access for desktop apps in Windows privacy settings"),
            _ => null,
        };
    }

    /// <summary>The engine came up: an HDR card is recorded as SDR, said once per card per session.</summary>
    private void OnCardEngineStarted()
    {
        _cardFramesSeen = -1;
        if (Engine.CardMode is { Hdr: true } && _target?.Device is { } card && _hdrWarned.Add(card.Link))
        {
            var note = Note.Of("Capture card {card} sends HDR: it is recorded as SDR", ("card", Note.Isolate(card.Name)));
            Say(note);
            NotifyProblem("Clink", note.Shown);
        }
    }

    /// <summary>
    /// The engine's state changed (UI thread): the card on record was unplugged, so move to the fallback screen now, not after the retry
    /// backoff. Also when a running card fails for a reason its error does not name (an unplug can end a read with any HRESULT): the
    /// switch restarts on the card when it is still listed. Not after a GPU reset (Tick's retry handles that).
    /// </summary>
    private void OnEngineStateForCard()
    {
        if (!CardIsTarget || _cardSwitching || _restarting || Engine.StatusKind != EngineStatusKind.Error || Engine.DeviceLost) return;
        if (Engine.CardProblem is not (CaptureCardProblem.NotConnected or null)) return;   // in use, blocked: the card is there
        if (Engine.CardProblem == null && _starting) return;   // a start that failed for another reason: StartEngine reports it, Tick retries
        if (DateTime.UtcNow - _lastCardGone < CardGoneRepeat) return;   // it came and went again: Tick's retry takes it from here
        _ = SwitchAfterCardGoneAsync();
    }

    /// <summary>Once a second from Tick: lists the devices every few seconds while a missing card is waited for, or while the card on record sends no frames.</summary>
    private void CheckCaptureCard(DateTime now)
    {
        if (_target == null || _cardPolling || _cardSwitching || _restarting || IsPaused || !Settings.ClipsEnabled) return;
        if (now - _lastCardPoll < CardPollInterval) return;
        if (_target.Fallback == CaptureFallback.CardMissing && _target.MissingLink is string missing)
        {
            _lastCardPoll = now;
            _ = PollCardAsync(_target, missing, waitingForReturn: true);
        }
        else if (CardIsTarget && Engine.IsRunning && !_starting && !_stopping)
        {
            // A card pulled while no frame was being read may never fail its read: no new frame for a while and not listed any more = gone.
            _lastCardPoll = now;
            long frames = Engine.GetStats().CaptureFrames;
            bool stalled = frames == _cardFramesSeen;
            _cardFramesSeen = frames;
            if (stalled) _ = PollCardAsync(_target, _target.Device!.Link, waitingForReturn: false);
        }
    }

    private async Task PollCardAsync(CaptureChoice polled, string link, bool waitingForReturn)
    {
        _cardPolling = true;
        bool? listed;
        try { listed = await Task.Run(() => VideoDevices.IsConnected(link)); }
        catch (Exception ex) { Log.Warn("capture card check: " + ex.Message); listed = null; }
        finally { _cardPolling = false; }
        if (_disposed || !ReferenceEquals(_target, polled) || listed == null) return;   // the target moved on meanwhile, or the listing failed (unknown)
        if (waitingForReturn && listed == true) await SwitchToReturnedCardAsync();
        else if (!waitingForReturn && listed == false && DateTime.UtcNow - _lastCardGone >= CardGoneRepeat) await SwitchAfterCardGoneAsync();
    }

    /// <summary>The card on record is gone: restart at once (a recording continues as a new part) on what the settings resolve to now, the fallback screen.</summary>
    private async Task SwitchAfterCardGoneAsync()
    {
        _cardSwitching = true;
        _lastCardGone = DateTime.UtcNow;
        try
        {
            string? link = _target?.Device?.Link;
            Log.Info("Capture card gone: switching to the fallback screen");
            BeginTechnicalRestart(TimeSpan.FromSeconds(2));
            // Windows may list a card for a moment after it was pulled: give the list up to ~1.5 s to drop it.
            for (int i = 0; link != null && i < 4; i++)
            {
                if (await Task.Run(() => VideoDevices.IsConnected(link)) != true) break;
                await Task.Delay(400);
            }
            if (_disposed) return;
            MarkTechnicalRestart();
            StartEngine();
        }
        catch (Exception ex) { Log.Warn("capture card switch: " + ex.Message); }
        finally { _cardSwitching = false; }
    }

    /// <summary>The missing card is connected again: after <see cref="CardReturnDelay"/>, restart onto it (StartEngine shows the "back" notice).</summary>
    private async Task SwitchToReturnedCardAsync()
    {
        _cardSwitching = true;
        try
        {
            Log.Info($"Capture card connected again: switching back in {CardReturnDelay.TotalSeconds:0.#} s");
            BeginTechnicalRestart(CardReturnDelay);   // at once, not after the delay: a recording that ends in between must not start its next part on the old engine
            await Task.Delay(CardReturnDelay);
            if (_disposed) return;
            MarkTechnicalRestart();
            StartEngine();
        }
        catch (Exception ex) { Log.Warn("capture card switch: " + ex.Message); }
        finally { _cardSwitching = false; }
    }
}
