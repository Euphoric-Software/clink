using Clink.Audio;
using Clink.Capture;
using Clink.Engine;
using Clink.Services.Localization;

namespace Clink.Services;

// "Both" mode (Settings.CaptureBoth with a capture card as the source): the controller's own engine records the screen, and a CardSlot
// records the card beside it with its own lifecycle. Single-source mode never creates the slot. UI thread.
internal sealed partial class AppController
{
    private CardSlot? _cardSlot;   // created the first time Both mode is wanted; null in single-source mode

    /// <summary>Both mode: the screen and the capture card are recorded at once (<see cref="CardSlot.IsBoth"/>).</summary>
    internal bool BothMode => CardSlot.IsBoth(Settings.CaptureBoth, Settings.CaptureSource);

    /// <summary>The card slot in Both mode (null before Both mode was first wanted). For the later checkpoints: clips, recordings and status per source.</summary>
    internal CardSlot? CardSlotForSource => _cardSlot;

    /// <summary>The source the controller's own engine records: the screen in Both mode, else <see cref="Settings.CaptureSource"/> as it is.</summary>
    private string? PrimaryCaptureSource => CardSlot.PrimarySource(Settings.CaptureBoth, Settings.CaptureSource);

    /// <summary>What the card slot should record now, from the settings; null when it should not run (not Both mode, Clips off, paused, a restart of Clink).</summary>
    private CardRequest? CardRequestNow()
    {
        if (!CardSlot.Wanted(BothMode, Settings.ClipsEnabled, IsPaused, _restarting)) return null;
        string? link = CaptureTarget.ParseSource(Settings.CaptureSource);
        var mon = SelectedMonitor;   // the card engine's D3D device goes on this screen's GPU
        if (link == null || mon == null) return null;
        var (maxW, maxH) = VideoModePick.LimitOf(Settings.Resolution);
        int secs = Math.Clamp(Settings.BufferSeconds, 5, 3600);
        int tracks = AudioTrackCount - (Settings.TrackDesktop ? 1 : 0);   // a card clip has no Desktop track
        var (mbps, _) = Capabilities.ResolveBitrate(Settings, tracks, secs);
        return new CardRequest(link, Settings.CaptureDeviceName, mon, maxW, maxH,
            (uint)Math.Clamp(Settings.Fps, 15, 120),
            Math.Clamp(mbps, Capabilities.MinTargetMbps, Capabilities.MaxTargetMbps),
            EngineConfig.NormalizeCodec(Settings.Codec),
            Math.Clamp(Settings.KeyframeSeconds, 0.25f, 5f),
            secs,
            Settings.TrackGame, Settings.TrackMic, Settings.TrackVoice,
            Settings.MicDeviceId,
            GameProcessTracker.NormalizeName(Settings.VoiceApp),
            CardAudio.ClampOffsetMs(Settings.CaptureDeviceAvOffsetMs),
            Settings.HdrKnee, Settings.HdrPeak,
            Settings.ClipsFolder,
            Clink.Encode.EncoderExperiments.Resolve(Settings.EncoderExperiment, Capabilities.SizeTargetBytes(Settings.SizeTarget) > 0),
            CardSlot.RingBudget(RingStore.MaxBytes()));
    }

    /// <summary>
    /// Brings the card slot in line with the settings and the controller's state: start, stop, restart on a changed setting, wait for a missing
    /// card. Cheap and a no-op in single-source mode (no slot is created). <paramref name="restart"/>: a display change or resume.
    /// <paramref name="apply"/> false (Tick): a changed setting waits for the controller's restart (debounced in the window) to reach a running card.
    /// </summary>
    private void SyncCardSlot(bool restart = false, bool apply = true)
    {
        try
        {
            var want = CardRequestNow();
            if (_cardSlot == null)
            {
                if (want == null || _disposed) return;
                _cardSlot = new CardSlot(a => _ui.BeginInvoke(a), OnCardSlotEvent);
                _cardSlot.Changed += RaiseStateChanged;
            }
            _cardSlot.Sync(want, DateTime.UtcNow, restart, apply);
        }
        catch (Exception ex) { Log.Warn("card slot: " + ex.Message); }
    }

    /// <summary>A restart of Clink: the card engine stops on this thread (see <see cref="StopForRestart"/>).</summary>
    private void StopCardSlotNow() => _cardSlot?.StopNow(RecorderStopLimit);

    /// <summary>What the card slot reports, in words: one notice per missing stretch, its return, an HDR card, a failed start.</summary>
    private void OnCardSlotEvent(CardSlotEvent e)
    {
        Note? note = e.Kind switch
        {
            CardSlotEventKind.Missing => Note.Of("Capture card not found: only the screen is recorded until it is back"),
            CardSlotEventKind.Back => Note.Of("Capture card is back"),
            CardSlotEventKind.Started when e.Hdr && _hdrWarned.Add(e.Link) => Note.Of("Capture card {card} sends HDR: it is recorded as SDR", ("card", Note.Isolate(e.CardName))),
            CardSlotEventKind.Failed => CardSlotProblemNote(e),
            _ => null,
        };
        if (note == null) return;
        Say(note);
        NotifyProblem("Clink", note.Shown);
    }

    /// <summary>Why the card engine did not start: the card's own reason (in use, blocked), else the engine's error, naming the card.</summary>
    private static Note CardSlotProblemNote(CardSlotEvent e) => e.Problem switch
    {
        CaptureCardProblem.InUse => Note.Of("Capture card {card} is in use by another app", ("card", Note.Isolate(e.CardName))),
        CaptureCardProblem.Blocked => Note.Of("Windows blocks the capture card: allow camera access for desktop apps in Windows privacy settings"),
        _ => Note.Of("Capture card {card} is not recorded: {error}", ("card", Note.Isolate(e.CardName)),
            ("error", e.Error is { } error ? EngineStatusText.ErrorNote(error) : Note.Of("Error"))),
    };
}
