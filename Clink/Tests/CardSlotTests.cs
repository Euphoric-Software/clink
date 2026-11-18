using Clink.Capture;
using Clink.Engine;
using Clink.Native;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Both mode (screen and capture card at once): the card slot's pure rules and the controller's screen choice.</summary>
internal static class CardSlotTests
{
    private const string Card = "device:\\\\?\\usb#vid_0fd9&pid_0066#card";

    private static MonitorInfo Mon(string name = "\\\\.\\DISPLAY1") => new(1, name, "Test", 0, 0, 1920, 1080, true, false, 80, 400, "GPU");

    private static CardRequest Request(string link = "link-a", long budget = 1L << 30) => new(link, "Game Capture HD60 X", Mon(), 1920, 1080, 60, 20, "h264",
        1f, 45, true, true, false, null, "auto", 0, 0.75f, 4f, @"C:\clips", null, budget);

    [UnitTest]
    public static void Both_mode_needs_the_setting_and_a_card_as_the_source()
    {
        Check.True(CardSlot.IsBoth(true, Card), "both with a card");
        Check.False(CardSlot.IsBoth(false, Card), "a card alone (single-source)");
        Check.False(CardSlot.IsBoth(true, "monitor"), "both with the screen as the source is plain screen capture");
        Check.False(CardSlot.IsBoth(true, null), "no source");
        Check.False(CardSlot.IsBoth(true, "device:"), "an empty link");
    }

    [UnitTest]
    public static void The_primary_engine_records_the_screen_in_Both_mode_and_the_setting_as_it_is_otherwise()
    {
        Check.Equal(CaptureTarget.SourceMonitor, CardSlot.PrimarySource(true, Card), "Both: the screen");
        Check.Equal(Card, CardSlot.PrimarySource(false, Card), "single-source card: unchanged");
        Check.Equal("monitor", CardSlot.PrimarySource(false, "monitor"));
        Check.Equal("monitor", CardSlot.PrimarySource(true, "monitor"));
        Check.Null(CardSlot.PrimarySource(false, null), "null stays null (today's path)");
        Check.Equal("garbage", CardSlot.PrimarySource(true, "garbage"), "an unknown source is left to CaptureTarget, as today");
    }

    [UnitTest]
    public static void The_card_is_wanted_in_Both_mode_unless_Clips_are_off_paused_or_restarting()
    {
        Check.True(CardSlot.Wanted(both: true, clipsEnabled: true, paused: false, restarting: false), "records (whatever the recording mode says)");
        Check.False(CardSlot.Wanted(both: false, clipsEnabled: true, paused: false, restarting: false), "single-source: no slot");
        Check.False(CardSlot.Wanted(both: true, clipsEnabled: false, paused: false, restarting: false), "Clips off stops both");
        Check.False(CardSlot.Wanted(both: true, clipsEnabled: true, paused: true, restarting: false), "one pause pauses both");
        Check.False(CardSlot.Wanted(both: true, clipsEnabled: true, paused: false, restarting: true), "a restart of Clink");
    }

    [UnitTest]
    public static void Backoff_matches_the_screen_engines_retry_steps()
    {
        Check.Equal(TimeSpan.FromSeconds(10), CardSlot.Backoff(failed: false, deviceLost: false, failures: 0), "ended without an error");
        Check.Equal(TimeSpan.FromSeconds(30), CardSlot.Backoff(true, false, 0), "first failure");
        Check.Equal(TimeSpan.FromSeconds(30), CardSlot.Backoff(true, false, 1));
        Check.Equal(TimeSpan.FromSeconds(60), CardSlot.Backoff(true, false, 2));
        Check.Equal(TimeSpan.FromSeconds(120), CardSlot.Backoff(true, false, 3));
        Check.Equal(TimeSpan.FromSeconds(120), CardSlot.Backoff(true, false, 50), "capped");
        Check.Equal(TimeSpan.FromSeconds(10), CardSlot.Backoff(true, true, 1), "GPU reset: a third");
        Check.Equal(TimeSpan.FromSeconds(40), CardSlot.Backoff(true, true, 3));
    }

    [UnitTest]
    public static void A_missing_card_is_told_once_per_stretch_and_its_return_only_after_that()
    {
        var first = CardSlot.NextPresence(missing: true, running: false, noticed: false);
        Check.Equal(CardNotice.Missing, first.Notice, "first time missing");
        Check.True(first.Noticed);
        var again = CardSlot.NextPresence(true, false, first.Noticed);
        Check.Equal(CardNotice.None, again.Notice, "still missing (a retry, pause and resume): no second notice");
        Check.True(again.Noticed);
        var unknown = CardSlot.NextPresence(false, false, again.Noticed);
        Check.Equal(CardNotice.None, unknown.Notice, "a start that failed for another reason says nothing about presence");
        Check.True(unknown.Noticed, "and keeps the stretch open");
        var back = CardSlot.NextPresence(false, true, unknown.Noticed);
        Check.Equal(CardNotice.Back, back.Notice, "running again after the notice: back");
        Check.False(back.Noticed, "the stretch is over");
        var plain = CardSlot.NextPresence(false, true, false);
        Check.Equal(CardNotice.None, plain.Notice, "a normal start is not a return");
        Check.Equal(CardNotice.Missing, CardSlot.NextPresence(true, false, back.Noticed).Notice, "a new unplug is a new stretch");
    }

    [UnitTest]
    public static void Only_a_card_that_is_not_connected_counts_as_gone()
    {
        Check.True(CardSlot.IsGone(CaptureCardProblem.NotConnected));
        Check.False(CardSlot.IsGone(CaptureCardProblem.InUse), "in use: it is there, retry on the backoff");
        Check.False(CardSlot.IsGone(CaptureCardProblem.Blocked));
        Check.False(CardSlot.IsGone(null), "another error");
    }

    [UnitTest]
    public static void A_running_card_restarts_on_request_or_on_a_changed_setting_that_is_applied_now()
    {
        var a = Request();
        Check.False(CardSlot.NeedsRestart(false, true, a, Request()), "the same settings: no restart (a record compares by value)");
        Check.True(CardSlot.NeedsRestart(true, false, a, a), "display change / resume");
        Check.True(CardSlot.NeedsRestart(false, true, a, a with { Fps = 30 }), "a setting changed and is applied");
        Check.False(CardSlot.NeedsRestart(false, false, a, a with { Fps = 30 }), "the once-a-second sync waits for the settings restart");
        Check.True(CardSlot.NeedsRestart(false, true, a, Request("link-b")), "another card");
        Check.True(CardSlot.NeedsRestart(false, true, a, a with { Monitor = Mon("\\\\.\\DISPLAY2") }), "another GPU screen");
        Check.True(CardSlot.NeedsRestart(false, true, null, a), "nothing recorded yet");
    }

    [UnitTest]
    public static void A_restart_or_apply_asked_for_while_busy_is_kept_until_the_slot_is_idle()
    {
        var old = Request();
        var changed = old with { Fps = 30 };
        // The settings restart (apply) arrives while a card start is in flight: kept as pending.
        var pending = CardSlot.Merge(default, restart: false, apply: true);
        // A display change during the same start adds a forced restart; neither is dropped.
        pending = CardSlot.Merge(pending, restart: true, apply: false);
        Check.Equal(new CardSyncAsk(true, true), pending);
        // The first Tick that finds the slot idle passes neither, yet acts on the pending ask.
        var ask = CardSlot.Merge(pending, restart: false, apply: false);
        Check.True(CardSlot.NeedsRestart(ask.Restart, ask.Apply, old, changed), "the changed fps reaches the card");
        Check.True(CardSlot.NeedsRestart(ask.Restart, ask.Apply, old, old), "the forced restart too");
        var applyOnly = CardSlot.Merge(CardSlot.Merge(default, false, true), false, false);
        Check.True(CardSlot.NeedsRestart(applyOnly.Restart, applyOnly.Apply, old, changed), "an apply alone, kept: restarts onto the change");
        Check.False(CardSlot.NeedsRestart(applyOnly.Restart, applyOnly.Apply, old, old), "nothing changed: no restart");
        Check.Equal(default(CardSyncAsk), CardSlot.Merge(default, false, false), "Tick alone asks nothing");
        Check.True(CardSlot.NeedsRestart(false, true, null, old), "another card (no running request known): restart");
    }

    [UnitTest]
    public static void Only_results_about_the_wanted_card_count()
    {
        Check.True(CardSlot.SameCard("link-a", "LINK-A"), "links compare without case");
        Check.False(CardSlot.SameCard("link-a", "link-b"), "another card was picked meanwhile: ignore the old card's result");
        Check.False(CardSlot.SameCard("link-a", null), "not wanted any more");
        Check.False(CardSlot.SameCard(null, "link-a"), "no running card");
    }

    [UnitTest]
    public static void Each_ring_gets_half_of_the_one_ring_limit_in_Both_mode()
    {
        Check.Equal(4L << 30, CardSlot.RingBudget(8L << 30));
        long half = CardSlot.RingBudget(RingStore.MaxBytes());
        Check.True(half * 2 <= RingStore.MaxBytes(), "together within the limit");
    }

    [UnitTest]
    public static void The_card_engine_is_named_after_the_card_ignores_the_PC_game_and_has_no_Desktop_track()
    {
        var r = Request(budget: 3L << 30);
        var card = new VideoDevice("link-a", "Game Capture HD60 X");
        var cfg = CardSlot.ConfigFor(r, card, "{0.0.1.00000000}.{card-audio}");
        Check.True(ReferenceEquals(card, cfg.Device), "the card");
        Check.True(ReferenceEquals(r.Monitor, cfg.Monitor), "its GPU is the screen's");
        Check.True(cfg.IgnoreGameHint, "never the PC game's name or sound");
        Check.False(cfg.TrackDesktop, "no Desktop track");
        Check.Equal("Game Capture HD60 X", cfg.ClipBaseName);
        Check.Equal("Game Capture HD60 X", cfg.RecordingBaseName);
        Check.Equal(3L << 30, cfg.RingBudgetBytes ?? 0, "the ring budget");
        Check.Equal("{0.0.1.00000000}.{card-audio}", cfg.CardAudioId, "the card's HDMI audio is the Game track");
        Check.Equal("auto", cfg.GameProcess);
        Check.True(cfg.TrackGame && cfg.TrackMic && !cfg.TrackVoice, "the shared track switches");
        Check.Equal(1920u, cfg.DeviceMaxWidth);
        Check.Equal(60u, cfg.Fps);
        Check.Equal(45, cfg.BufferSeconds);
        Check.Null(CardSlot.ConfigFor(r, card, null).CardAudioId, "no card audio found: the engine leaves the Game track empty (IgnoreGameHint)");
        Check.False(ReferenceEquals(CardSlot.ConfigFor(r, card, null), CardSlot.ConfigFor(r, card, null)), "a fresh config per start (Engine.Start writes into it)");
    }

    [UnitTest]
    public static void In_Both_mode_the_controller_resolves_the_screen_and_never_a_card_fallback()
    {
        var settings = Settings.FromJson("{}");
        settings.CaptureSource = Card;
        settings.CaptureBoth = true;
        using var app = new AppController(settings, new Updater(settings));
        Check.True(app.BothMode);
        var choice = app.ResolveCapture();   // (lists no devices: the screen's source is "monitor")
        Check.Equal(CaptureKind.Monitor, choice.Kind, "the screen");
        Check.Equal(CaptureFallback.None, choice.Fallback, "not a fallback: the card slot records the card");
        Check.Null(choice.MissingLink);
        Check.False(app.CardIsTarget, "nothing resolved into the target yet");
        settings.CaptureBoth = false;
        Check.False(app.BothMode, "single-source card");
        Check.Null(app.CardSlotForSource, "no slot is ever made outside Both mode");
    }
}
