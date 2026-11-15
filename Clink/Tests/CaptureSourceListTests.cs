using Clink.Audio;
using Clink.Capture;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>The Source picker's list and detail line, and the two follow-ups of the card audio checks (no restart through a recording; the Mic that is the card's audio).</summary>
internal static class CaptureSourceListTests
{
    private static readonly VideoDevice Hd60 = new(@"\\?\usb#vid_0fd9&pid_0084#a", "Elgato HD60 X"), CamLink = new(@"\\?\usb#vid_0fd9&pid_0066#b", "Cam Link 4K");

    [UnitTest]
    public static void The_list_is_the_screen_then_each_card_by_name()
    {
        using var scope = L.Scope("en");
        var items = CaptureSourceList.Build(new[] { Hd60, CamLink }, "monitor", null, null);
        Check.Equal(new[] { "Screen", "Elgato HD60 X", "Cam Link 4K" }, items.Select(i => (i.Label)).ToArray());
        Check.Equal(new[] { "monitor", "device:" + Hd60.Link, "device:" + CamLink.Link }, items.Select(i => i.Source).ToArray());
        Check.Null(items[0].CardName, "the screen has no card name");
        Check.Equal("Elgato HD60 X", items[1].CardName);
    }

    [UnitTest]
    public static void With_no_cards_the_list_is_only_the_screen()
    {
        using var scope = L.Scope("en");
        Check.Equal(1, CaptureSourceList.Build(Array.Empty<VideoDevice>(), "monitor", null, null).Count);
        Check.Equal(1, CaptureSourceList.Build(null, "monitor", "Elgato HD60 X", null).Count, "a name left in the settings is not a card while the screen is the source");
    }

    [UnitTest]
    public static void A_saved_card_that_is_gone_stays_in_the_list_as_not_connected()
    {
        using var scope = L.Scope("en");
        var items = CaptureSourceList.Build(new[] { CamLink }, "device:" + Hd60.Link, "Elgato HD60 X", null);
        Check.Equal(3, items.Count);
        Check.Equal("Elgato HD60 X (not connected)", (items[2].Label));
        Check.Equal("device:" + Hd60.Link, items[2].Source, "selecting it again is the same source");
        Check.Equal("Elgato HD60 X", items[2].CardName, "its name is kept");
    }

    [UnitTest]
    public static void A_saved_card_that_is_listed_appears_once_and_without_a_note()
    {
        using var scope = L.Scope("en");
        var items = CaptureSourceList.Build(new[] { Hd60, Hd60 }, "device:" + Hd60.Link.ToUpperInvariant(), "old name", null);
        Check.Equal(2, items.Count, "a device listed twice shows once; the saved link matches without case");
        Check.Equal("Elgato HD60 X", (items[1].Label), "the listed name wins over the saved one");
    }

    [UnitTest]
    public static void Before_a_listing_the_saved_card_shows_by_name_unless_the_recorder_found_it_missing()
    {
        using var scope = L.Scope("en");
        Check.Equal("Elgato HD60 X", (CaptureSourceList.Build(null, "device:x", "Elgato HD60 X", null)[1].Label), "unknown: no claim");
        Check.Equal("Elgato HD60 X (not connected)", (CaptureSourceList.Build(null, "device:x", "Elgato HD60 X", true)[1].Label), "the recorder says it is missing");
        Check.Equal("Capture card (not connected)", (CaptureSourceList.Build(Array.Empty<VideoDevice>(), "device:x", null, null)[1].Label), "no name was ever saved");
    }

    [UnitTest]
    public static void The_controllers_word_on_the_saved_card_beats_a_stale_listing()
    {
        using var scope = L.Scope("en");
        string saved = "device:" + Hd60.Link;
        var gone = new CaptureChoice(CaptureKind.Monitor, null, null, CaptureFallback.CardMissing, Hd60.Link);
        var back = new CaptureChoice(CaptureKind.Device, Hd60, null, CaptureFallback.None);
        Check.Equal(true, CaptureSourceList.SavedCardMissing(gone, saved));
        Check.Equal(false, CaptureSourceList.SavedCardMissing(back, saved));
        Check.Null(CaptureSourceList.SavedCardMissing(null, saved), "nothing resolved yet");
        Check.Null(CaptureSourceList.SavedCardMissing(back, "device:other"), "the controller resolved another source");
        // unplugged after the list was made: the stale list still has the card
        Check.Equal("Elgato HD60 X (not connected)", CaptureSourceList.Build(new[] { Hd60 }, saved, "Elgato HD60 X", true)[1].Label);
        // plugged in again after the list was made: the list lacks the card
        var items = CaptureSourceList.Build(new[] { CamLink }, saved, "Elgato HD60 X", false);
        Check.Equal("Elgato HD60 X", items[2].Label);
    }

    [UnitTest]
    public static void A_source_change_during_a_recording_does_not_move_the_target_and_a_recorded_card_keeps_its_replay_on_screen_changes()
    {
        Check.False(AppController.SourceChangeAppliesNow(true), "recording: the deferred restart applies it");
        Check.True(AppController.SourceChangeAppliesNow(false));
        Check.False(AppController.ScreenChoiceNeedsRestart(true), "a card is recorded: map / follow box only save");
        Check.True(AppController.ScreenChoiceNeedsRestart(false));
    }

    [UnitTest]
    public static void The_detail_line_says_not_connected_the_problem_or_the_mode_with_the_hdr_warning()
    {
        using var scope = L.Scope("en");
        var sdr = new VideoMode(1920, 1080, 59.94, VideoFormat.Nv12);
        var hdr = new VideoMode(3840, 2160, 60, VideoFormat.P010, true);
        Check.Equal("Not connected: recording the fallback screen instead", CaptureSourceList.Detail(true, "ignored", sdr));
        Check.Equal("in use", CaptureSourceList.Detail(false, "in use", sdr), "a problem outranks the mode");
        Check.Equal("1920×1080  ·  59.94 fps  ·  NV12", (CaptureSourceList.Detail(false, null, sdr)!));
        Check.Equal("3840×2160  ·  60 fps  ·  P010  ·  HDR, recorded as SDR", (CaptureSourceList.Detail(false, null, hdr)!));
        Check.Null(CaptureSourceList.Detail(false, null, null), "nothing to say before the card is open");
    }

    [UnitTest]
    public static void The_detail_and_list_texts_are_translated_and_bidi_safe_in_every_locale()
    {
        foreach (var loc in L.Known.Select(k => k.Code))
        {
            using var scope = L.Scope(loc);
            var mode = new VideoMode(1920, 1080, 60, VideoFormat.Yuy2, true);
            Check.True(!string.IsNullOrWhiteSpace(CaptureSourceList.Detail(false, null, mode)), loc + " mode line");
            Check.True(!string.IsNullOrWhiteSpace(CaptureSourceList.Detail(true, null, null)), loc + " not connected line");
            Check.True(CaptureSourceList.Build(null, "device:x", "Card", true)[1].Label.Contains("Card"), loc + " the card name is in its entry");
        }
    }

    [UnitTest]
    public static void The_card_audio_check_restarts_only_when_nothing_would_be_cut()
    {
        // running, stopping, starting, restarting, switching, paused, recording, wanting a recording, awaiting a technical restart
        Check.True(AppController.CardAudioRestartAllowedFor(true, false, false, false, false, false, false, false, false), "all clear");
        Check.False(AppController.CardAudioRestartAllowedFor(false, false, false, false, false, false, false, false, false), "not running");
        for (int blocker = 1; blocker < 9; blocker++)
        {
            var flags = new bool[9];
            flags[0] = true;
            flags[blocker] = true;
            Check.False(AppController.CardAudioRestartAllowedFor(flags[0], flags[1], flags[2], flags[3], flags[4], flags[5], flags[6], flags[7], flags[8]), "blocked by flag " + blocker);
        }
    }

    [UnitTest]
    public static void A_mic_left_out_because_it_is_the_card_audio_says_so()
    {
        using var scope = L.Scope("en");
        Check.Equal("same as the capture card audio", AudioDetail.SameAsCardAudio.English);
        Check.Equal("same as the capture card audio", EngineStatusText.Detail(AudioDetailKind.SameAsCardAudio, null));
    }
}
