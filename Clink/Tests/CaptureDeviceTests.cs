using Clink.Capture;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Capture cards without a card: the mode pick, the layout from a subtype, the source setting and its keys.</summary>
internal static class CaptureDeviceTests
{
    private static VideoMode M(uint w, uint h, double fps, VideoFormat f = VideoFormat.Nv12, bool hdr = false) => new(w, h, fps, f, hdr);

    [UnitTest]
    public static void The_largest_mode_that_fits_the_preset_is_picked()
    {
        var modes = new[] { M(3840, 2160, 60), M(1920, 1080, 60), M(1280, 720, 60) };
        var (w, h) = VideoModePick.LimitOf("1080p");
        Check.Equal(M(1920, 1080, 60), VideoModePick.Pick(modes, w, h, 60)!.Value, "a 4K card at the 1080p preset");
        (w, h) = VideoModePick.LimitOf("native");
        Check.Equal(M(3840, 2160, 60), VideoModePick.Pick(modes, w, h, 60)!.Value, "native = the card's largest");
        (w, h) = VideoModePick.LimitOf("720p");
        Check.Equal(M(1280, 720, 60), VideoModePick.Pick(modes, w, h, 60)!.Value);
        Check.Equal(M(3840, 2160, 60), VideoModePick.Pick(modes, VideoModePick.LimitOf("nonsense").Width, VideoModePick.LimitOf(null).Height, 60)!.Value, "an unknown preset is native");
    }

    [UnitTest]
    public static void When_nothing_fits_the_smallest_mode_is_used()
    {
        var modes = new[] { M(1920, 1080, 60), M(1280, 720, 30), M(1280, 720, 60) };
        Check.Equal(M(1280, 720, 60), VideoModePick.Pick(modes, 640, 480, 60)!.Value);
    }

    [UnitTest]
    public static void The_frame_rate_closest_to_the_wanted_one_is_picked_and_59_94_counts_as_60()
    {
        var modes = new[] { M(1920, 1080, 30), M(1920, 1080, 59.94), M(1920, 1080, 120) };
        Check.Equal(59.94, VideoModePick.Pick(modes, 1920, 1080, 60)!.Value.Fps);
        Check.Equal(30.0, VideoModePick.Pick(modes, 1920, 1080, 30)!.Value.Fps);
        Check.Equal(120.0, VideoModePick.Pick(modes, 1920, 1080, 144)!.Value.Fps, "the card caps the rate");
        Check.Equal(59.94, VideoModePick.Pick(new[] { M(1920, 1080, 59.94), M(1920, 1080, 30) }, 1920, 1080, 120)!.Value.Fps);
    }

    [UnitTest]
    public static void The_wanted_frame_rate_beats_a_bigger_size()
    {
        var hd60x = new[] { M(3840, 2160, 30), M(1920, 1080, 60) };
        Check.Equal(M(1920, 1080, 60), VideoModePick.Pick(hd60x, uint.MaxValue, uint.MaxValue, 60)!.Value, "native size, 60 wanted");
        Check.Equal(M(3840, 2160, 30), VideoModePick.Pick(hd60x, uint.MaxValue, uint.MaxValue, 30)!.Value, "30 wanted: 4K30 reaches it");
        Check.Equal(M(1920, 1080, 60), VideoModePick.Pick(new[] { M(1920, 1080, 30), M(1280, 720, 60) }.Append(M(1920, 1080, 60)).ToArray(), 1920, 1080, 60)!.Value);
        Check.Equal(M(1280, 720, 60), VideoModePick.Pick(new[] { M(1920, 1080, 30), M(1280, 720, 60) }, 1920, 1080, 60)!.Value);
        Check.Equal(M(1920, 1080, 30), VideoModePick.Pick(new[] { M(1920, 1080, 30), M(1280, 720, 24) }, 1920, 1080, 60)!.Value, "no mode reaches 60: the card's best rate, then size");
    }

    [UnitTest]
    public static void The_lowest_rate_at_or_above_the_wanted_one_is_picked_else_the_highest_below()
    {
        Check.Equal(120.0, VideoModePick.Pick(new[] { M(1920, 1080, 30), M(1920, 1080, 120) }, 1920, 1080, 60)!.Value.Fps);
        Check.Equal(120.0, VideoModePick.Pick(new[] { M(1920, 1080, 120), M(1920, 1080, 90), M(1920, 1080, 30) }, 1920, 1080, 60)!.Value.Fps, "90 is not a whole multiple of 60: 120 is");
        Check.Equal(120.0, VideoModePick.Pick(new[] { M(1920, 1080, 90), M(1920, 1080, 120) }, 1920, 1080, 60)!.Value.Fps);
        Check.Equal(60.0, VideoModePick.Pick(new[] { M(1920, 1080, 120), M(1920, 1080, 60), M(1920, 1080, 30) }, 1920, 1080, 60)!.Value.Fps);
    }

    [UnitTest]
    public static void A_rate_reaches_the_wanted_one_only_near_a_whole_multiple_of_it()
    {
        Check.True(VideoModePick.Reaches(60, 60));
        Check.True(VideoModePick.Reaches(59.94, 60), "59.94 is 60");
        Check.True(VideoModePick.Reaches(119.88, 60), "twice 59.94");
        Check.True(VideoModePick.Reaches(240, 60));
        Check.True(VideoModePick.Reaches(29.97, 30));
        Check.False(VideoModePick.Reaches(90, 60), "one and a half");
        Check.False(VideoModePick.Reaches(240, 144), "240 / 144 = 1.67");
        Check.False(VideoModePick.Reaches(30, 60), "below");
        Check.False(VideoModePick.Reaches(60, 0), "no wanted rate");
    }

    [UnitTest]
    public static void A_rate_above_that_is_no_whole_multiple_loses_to_the_best_rate_below()
    {
        // 144 wanted: 240 would land unevenly on the 144 grid, so the 120 mode wins (and its larger size with it).
        Check.Equal(M(2560, 1440, 120), VideoModePick.Pick(new[] { M(1920, 1080, 240), M(2560, 1440, 120) }, uint.MaxValue, uint.MaxValue, 144)!.Value);
        Check.Equal(M(1920, 1080, 120), VideoModePick.Pick(new[] { M(1920, 1080, 240), M(1920, 1080, 120), M(1920, 1080, 60) }, 1920, 1080, 144)!.Value, "the best below, not the lowest");
        Check.Equal(90.0, VideoModePick.Pick(new[] { M(1920, 1080, 90) }, 1920, 1080, 60)!.Value.Fps, "nothing reaches it nor is below it: the card's only rate");
    }

    [UnitTest]
    public static void A_card_is_recorded_at_its_size_scaled_into_the_preset_and_at_most_its_own_rate()
    {
        Check.Equal((1920u, 1080u), VideoModePick.OutputSize(1920, 1080, 1920, 1080));
        Check.Equal((1920u, 1080u), VideoModePick.OutputSize(1920, 1080, uint.MaxValue, uint.MaxValue), "native");
        Check.Equal((1280u, 720u), VideoModePick.OutputSize(1920, 1080, 1280, 720), "a card with no mode inside the preset");
        Check.Equal((960u, 720u), VideoModePick.OutputSize(1440, 1080, 1280, 720), "4:3 keeps its shape");
        Check.Equal((720u, 480u), VideoModePick.OutputSize(720, 480, 1920, 1080), "never scaled up");
        Check.Equal((720u, 576u), VideoModePick.OutputSize(721, 577, 1920, 1080), "even sizes for the encoder");
        Check.Equal(60u, VideoModePick.EncodeFps(59.94, 60));
        Check.Equal(60u, VideoModePick.EncodeFps(120, 60), "a faster card is decimated");
        Check.Equal(30u, VideoModePick.EncodeFps(29.97, 60), "a slower card lowers the clip's rate");
        Check.Equal(15u, VideoModePick.EncodeFps(5, 60), "never below 15");
    }

    /// <summary>How many of <paramref name="count"/> card frames (at <paramref name="cardFps"/>, each moved by <paramref name="jitter"/>(i)) a <paramref name="clipFps"/> clip takes.</summary>
    private static int FramesTaken(double cardFps, uint clipFps, int count, Func<int, long>? jitter = null, long start = 1_000_000_000)
    {
        long period = 10_000_000L / clipFps, cardPeriod = (long)(10_000_000 / cardFps);
        long nextDue = long.MinValue;
        int taken = 0;
        for (int i = 0; i < count; i++)
            if (VideoModePick.TakeFrame(start + (long)(i * 10_000_000.0 / cardFps) + (jitter?.Invoke(i) ?? 0), period, cardPeriod, ref nextDue)) taken++;
        return taken;
    }

    [UnitTest]
    public static void A_card_frame_is_taken_on_the_clips_grid_of_due_times()
    {
        Check.Equal(600, FramesTaken(60, 60, 600), "k = 1: every frame");
        Check.Equal(600, FramesTaken(59.94, 60, 600), "59.94 for 60: every frame");
        Check.Equal(300, FramesTaken(120, 60, 600), "k = 2: every second frame");
        Check.Equal(150, FramesTaken(60, 15, 600), "k = 4: every fourth frame (not 20 fps)");
        Check.Equal(150, FramesTaken(240, 60, 600), "k = 4 at 240 fps");
    }

    [UnitTest]
    public static void A_card_frame_that_arrives_early_or_late_is_still_taken()
    {
        // The frames are stamped when they arrive: within half a card period of the grid, none is lost at equal rates.
        Check.Equal(600, FramesTaken(60, 60, 600, i => i % 2 == 0 ? 40_000 : -40_000), "+-4 ms alternating at 60 fps");
        Check.Equal(600, FramesTaken(60, 60, 600, i => (i * 7919 % 9 - 4) * 10_000), "up to +-4 ms scattered");
        Check.Equal(300, FramesTaken(120, 60, 600, i => i % 2 == 0 ? 25_000 : -25_000), "k = 2 with +-2.5 ms");
    }

    [UnitTest]
    public static void After_a_gap_without_frames_the_grid_starts_again_instead_of_taking_a_burst()
    {
        long period = 10_000_000L / 60, cardPeriod = 10_000_000L / 120;
        long nextDue = long.MinValue;
        int taken = 0;
        for (int i = 0; i < 120; i++) if (VideoModePick.TakeFrame(i * cardPeriod, period, cardPeriod, ref nextDue)) taken++;
        long after = 50_000_000;   // 5 s of no signal
        for (int i = 0; i < 120; i++) if (VideoModePick.TakeFrame(after + i * cardPeriod, period, cardPeriod, ref nextDue)) taken++;
        Check.Equal(120, taken, "every second frame before and after the gap");
    }

    [UnitTest]
    public static void A_card_failure_is_told_by_its_HRESULT_or_by_the_card_missing_from_the_list()
    {
        Check.Equal(CaptureCardProblem.NotConnected, CaptureCardException.Classify(unchecked((int)0xC00D3EA2), true), "invalidated while streaming");
        Check.Equal(CaptureCardProblem.NotConnected, CaptureCardException.Classify(unchecked((int)0x8007048F), true), "device not connected");
        Check.Equal(CaptureCardProblem.NotConnected, CaptureCardException.Classify(unchecked((int)0x8007001F), null), "a device that stopped working (USB pulled)");
        Check.Equal(CaptureCardProblem.InUse, CaptureCardException.Classify(unchecked((int)0xC00D3EA3), true), "preempted");
        Check.Equal(CaptureCardProblem.InUse, CaptureCardException.Classify(unchecked((int)0xC00D3704), true), "failed to start streaming");
        Check.Equal(CaptureCardProblem.InUse, CaptureCardException.Classify(unchecked((int)0x800700AA), null), "busy");
        Check.Equal(CaptureCardProblem.Blocked, CaptureCardException.Classify(unchecked((int)0x80070005), true), "camera privacy");
        Check.Equal(CaptureCardProblem.NotConnected, CaptureCardException.Classify(unchecked((int)0x80004005), false), "any error once the card is not listed");
        Check.Null(CaptureCardException.Classify(unchecked((int)0x80004005), true), "listed: a plain error");
        Check.Null(CaptureCardException.Classify(unchecked((int)0x80004005), null), "the listing failed: not taken for unplugged");
        var ex = new CaptureCardException(CaptureCardProblem.InUse, "Elgato HD60 X");
        Check.Equal("Capture card Elgato HD60 X is in use by another app", ex.Message);
        Check.Equal("Elgato HD60 X", ex.Card);
    }

    [UnitTest]
    public static void At_the_same_mode_NV12_beats_YUY2_beats_P010_beats_RGB32_beats_MJPG()
    {
        var order = new[] { VideoFormat.Nv12, VideoFormat.Yuy2, VideoFormat.P010, VideoFormat.Rgb32, VideoFormat.Mjpg };
        for (int i = 0; i < order.Length; i++)
        {
            var modes = order.Skip(i).Select(f => M(1920, 1080, 60, f)).ToArray();
            Check.Equal(order[i], VideoModePick.Pick(modes, 1920, 1080, 60)!.Value.Format);
        }
    }

    [UnitTest]
    public static void A_frame_rate_match_beats_a_cheaper_layout_and_a_non_HDR_mode_beats_an_HDR_one()
    {
        var modes = new[] { M(1920, 1080, 5, VideoFormat.Yuy2), M(1920, 1080, 60, VideoFormat.Mjpg) };
        Check.Equal(VideoFormat.Mjpg, VideoModePick.Pick(modes, 1920, 1080, 60)!.Value.Format);
        var hdr = new[] { M(3840, 2160, 60, VideoFormat.P010, hdr: true), M(3840, 2160, 60, VideoFormat.P010) };
        Check.False(VideoModePick.Pick(hdr, 3840, 2160, 60)!.Value.Hdr);
    }

    [UnitTest]
    public static void Unusable_modes_are_left_out_and_an_empty_card_gives_none()
    {
        Check.Null(VideoModePick.Pick(Array.Empty<VideoMode>(), 1920, 1080, 60));
        Check.Null(VideoModePick.Pick(new[] { M(1920, 1080, 60, VideoFormat.Other), M(0, 0, 60), M(1920, 1080, 0) }, 1920, 1080, 60));
        Check.Equal(M(1280, 720, 60), VideoModePick.Pick(new[] { M(1920, 1080, 60, VideoFormat.Other), M(1280, 720, 60) }, 1920, 1080, 60)!.Value);
    }

    [UnitTest]
    public static void A_media_subtype_names_its_layout()
    {
        static Guid Fourcc(string c) => new(BitConverter.ToUInt32(System.Text.Encoding.ASCII.GetBytes(c)), 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        Check.Equal(VideoFormat.Nv12, VideoModePick.FormatOf(Fourcc("NV12")));
        Check.Equal(VideoFormat.Yuy2, VideoModePick.FormatOf(Fourcc("YUY2")));
        Check.Equal(VideoFormat.P010, VideoModePick.FormatOf(Fourcc("P010")));
        Check.Equal(VideoFormat.Mjpg, VideoModePick.FormatOf(Fourcc("MJPG")));
        Check.Equal(VideoFormat.Other, VideoModePick.FormatOf(Fourcc("H264")));
        Check.Equal(VideoFormat.Rgb32, VideoModePick.FormatOf(new Guid(22, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71)));
        Check.Equal(VideoFormat.Other, VideoModePick.FormatOf(Guid.Empty));
        Check.Equal(VideoFormat.Other, VideoModePick.FormatOf(new Guid("32315634-1234-5678-9abc-def012345678")), "not the media-type base GUID");
    }

    [UnitTest]
    public static void The_source_setting_is_a_screen_or_a_card_link()
    {
        Check.Null(CaptureTarget.ParseSource(null));
        Check.Null(CaptureTarget.ParseSource(""));
        Check.Null(CaptureTarget.ParseSource("monitor"));
        Check.Null(CaptureTarget.ParseSource("device:"), "no link");
        Check.Null(CaptureTarget.ParseSource("device:   "));
        Check.Null(CaptureTarget.ParseSource("garbage"));
        Check.Equal(@"\\?\usb#vid_0fd9#1", CaptureTarget.ParseSource(@"device:\\?\usb#vid_0fd9#1"));
        Check.Equal(@"\\?\usb#vid_0fd9#1", CaptureTarget.ParseSource(@"DEVICE:\\?\usb#vid_0fd9#1"));
        Check.Equal("monitor", CaptureTarget.FormatSource(null));
        Check.Equal("monitor", CaptureTarget.FormatSource(" "));
        Check.Equal(@"device:\\?\usb#vid_0fd9#1", CaptureTarget.FormatSource(@"\\?\usb#vid_0fd9#1"));
        Check.Equal(@"\\?\usb#vid_0fd9#1", CaptureTarget.ParseSource(CaptureTarget.FormatSource(@"\\?\usb#vid_0fd9#1")));
    }

    [UnitTest]
    public static void A_file_without_the_card_keys_captures_the_screen_and_writes_none_of_the_optional_ones()
    {
        var s = Settings.FromJson("{}");
        Check.Equal("monitor", s.CaptureSource);
        Check.Null(s.CaptureDeviceName);
        Check.Equal(0, s.CaptureDeviceAvOffsetMs);
        string json = s.ToJson();
        Check.Contains(json, "\"CaptureSource\": \"monitor\"");
        Check.False(json.Contains("CaptureDeviceName"), "left out while unset");
        Check.False(json.Contains("CaptureDeviceAvOffsetMs"), "hidden: left out while 0");
    }

    [UnitTest]
    public static void The_card_keys_round_trip()
    {
        var s = Settings.FromJson("""{ "CaptureSource": "device:\\\\?\\usb#a", "CaptureDeviceName": "Elgato HD60 X", "CaptureDeviceAvOffsetMs": -40 }""");
        Check.Equal(@"device:\\?\usb#a", s.CaptureSource);
        Check.Equal("Elgato HD60 X", s.CaptureDeviceName);
        Check.Equal(-40, s.CaptureDeviceAvOffsetMs);
        var again = Settings.FromJson(s.ToJson());
        Check.Equal(s.CaptureSource, again.CaptureSource);
        Check.Equal(s.CaptureDeviceName, again.CaptureDeviceName);
        Check.Equal(-40, again.CaptureDeviceAvOffsetMs);
    }

    [UnitTest]
    public static void Card_keys_of_the_wrong_kind_cost_only_themselves()
    {
        var s = Settings.FromJson("""{ "CaptureSource": 5, "CaptureDeviceName": [1], "CaptureDeviceAvOffsetMs": "late", "MonitorDevice": "\\\\.\\DISPLAY2" }""");
        Check.Equal("monitor", s.CaptureSource);
        Check.Null(s.CaptureDeviceName);
        Check.Equal(0, s.CaptureDeviceAvOffsetMs);
        Check.Equal(@"\\.\DISPLAY2", s.MonitorDevice);
        Check.Equal("monitor", Settings.FromJson("""{ "CaptureSource": null }""").CaptureSource, "a null source is the screen");
    }

    [UnitTest]
    public static void A_null_or_blank_source_is_normalized_to_the_screen()
    {
        Check.Equal("monitor", Settings.FromJson("""{ "CaptureSource": null }""").CaptureSource);
        Check.Equal("monitor", Settings.FromJson("""{ "CaptureSource": "  " }""").CaptureSource);
        Check.Equal("device:x", Settings.FromJson("""{ "CaptureSource": "device:x" }""").CaptureSource);
    }

    [UnitTest]
    public static void The_card_keys_are_imported_with_the_monitor()
    {
        var ours = Settings.FromJson("{}");
        var theirs = Settings.FromJson("""{ "CaptureSource": "device:x" }""");
        var labels = SettingsImport.Differences(ours.ToJson(), theirs.ToJson());
        Check.True(labels.Contains("monitor"), "a different source is a difference of the monitor group");
    }
}
