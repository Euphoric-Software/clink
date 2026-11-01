using System.Globalization;
using Clink.Services;

namespace Clink.Tests;

/// <summary>ShareFit: which smaller copy a clip gets for a share limit, and the retries after a size overshoot (all hand-computed).</summary>
internal static class ShareFitTests
{
    private const long Mb = 1_000_000;

    private static ShareFitResult PlanH264(int w, int h, double fps, double seconds, long sourceBytes, long limit) =>
        ShareFit.Plan(w, h, fps, seconds, sourceBytes, limit, "h264");

    private static ShareFitPlan Planned(ShareFitResult r)
    {
        Check.Equal(ShareFitKind.Plan, r.Kind);
        Check.NotNull(r.Plan);
        return r.Plan!;
    }

    private static void Expect(ShareFitPlan p, int w, int h, int fps, double mbps, string label)
    {
        Check.Equal(label, p.Label);
        Check.Equal(w, p.Width);
        Check.Equal(h, p.Height);
        Check.Equal(fps, p.Fps);
        Check.Near(mbps, p.VideoMbps, 1e-9, "video Mbps");
        Check.Equal((long)Math.Round(mbps * 1_000_000), p.VideoBitrateBps, "video bps");
    }

    [UnitTest]
    public static void Twenty_MB_over_45_s_of_1080p60_with_the_Mix_is_720p60_at_3_1_Mbps()
    {
        // budget: 20e6 x 0.95 x 8 / 45 s = 3 377 778 bps; less the Mix's 192 000 = 3 185 778 bps -> floored to 3.1 Mbps.
        // 1080p60 needs 5, 720p60 needs 2.5 -> 720p60. Size: (3.1e6 + 192 000) x 45 / 8 = 18 517 500 bytes.
        var p = Planned(PlanH264(1920, 1080, 60, 45, 110 * Mb, 20 * Mb));
        Expect(p, 1280, 720, 60, 3.1, "720p60");
        Check.Equal("h264", p.Codec);
        Check.Equal(18_517_500L, p.EstimatedBytes);
        Check.Equal(1, p.Attempt);
        // what a retry needs is kept in the plan
        Check.Equal(45.0, p.DurationSeconds);
        Check.Equal(192, p.AudioKbps);
        Check.Equal(1.0, p.SourceKeyframeSeconds);
        Check.Equal(20 * Mb, p.LimitBytes);
    }

    [UnitTest]
    public static void A_source_at_or_under_the_limit_needs_no_copy()
    {
        Check.Equal(ShareFitKind.NoCopyNeeded, PlanH264(1920, 1080, 60, 45, 19_900_000, 20 * Mb).Kind, "under");
        Check.Equal(ShareFitKind.NoCopyNeeded, PlanH264(1920, 1080, 60, 45, 20 * Mb, 20 * Mb).Kind, "exactly at the limit");
        Check.Equal(ShareFitKind.NoCopyNeeded, PlanH264(1920, 1080, 60, 0, 99 * Mb, 0).Kind, "no limit");
        Check.Equal(ShareFitKind.NoCopyNeeded, PlanH264(0, 0, 0, 0, 0, 0).Kind, "no limit, nothing known");
        Check.Null(PlanH264(1920, 1080, 60, 45, 10 * Mb, 20 * Mb).Plan);
    }

    [UnitTest]
    public static void Input_that_cant_be_planned_for_is_refused_when_a_copy_is_needed()
    {
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1080, 60, 0, 99 * Mb, 20 * Mb), "duration 0");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1080, 60, double.NaN, 99 * Mb, 20 * Mb), "duration NaN");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1080, 0, 45, 99 * Mb, 20 * Mb), "fps 0");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1080, double.NaN, 45, 99 * Mb, 20 * Mb), "fps NaN");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1080, double.PositiveInfinity, 45, 99 * Mb, 20 * Mb), "fps infinite");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(0, 1080, 60, 45, 99 * Mb, 20 * Mb), "width 0");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 0, 60, 45, 99 * Mb, 20 * Mb), "height 0");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1, 1080, 60, 45, 99 * Mb, 20 * Mb), "width 1");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1, 60, 45, 99 * Mb, 20 * Mb), "height 1");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1080, 60, 45, 0, 20 * Mb), "source bytes 0");
        Check.Throws<ArgumentOutOfRangeException>(() => PlanH264(1920, 1080, 60, 45, -5, 20 * Mb), "source bytes negative");
    }

    [UnitTest]
    public static void Dimensions_stay_even_and_at_least_2()
    {
        // a 2-pixel side is the smallest there is: 2x1000 stays 2x1000, and 3x1001 loses its odd pixels to the same
        Expect(Planned(PlanH264(2, 1000, 60, 45, 150 * Mb, 20 * Mb)), 2, 1000, 60, 3.1, "2p60");
        Expect(Planned(PlanH264(3, 1001, 60, 45, 150 * Mb, 20 * Mb)), 2, 1000, 60, 3.1, "2p60");
    }

    [UnitTest]
    public static void A_30_fps_source_gets_30_fps_rungs()
    {
        // 20 MB / 45 s: 3.1 Mbps. 1080p30 needs 3.0 -> kept at 1080p30 (a 60 fps source would drop to 720p60).
        Expect(Planned(PlanH264(1920, 1080, 30, 45, 110 * Mb, 20 * Mb)), 1920, 1080, 30, 3.1, "1080p30");
        // 14 MB / 45 s: 14e6 x 0.95 x 8 / 45 = 2 364 444 - 192 000 = 2 172 444 bps -> 2.1 Mbps. 1080p30 needs 3.0, 720p30 needs 1.5.
        Expect(Planned(PlanH264(1920, 1080, 30, 45, 110 * Mb, 14 * Mb)), 1280, 720, 30, 2.1, "720p30");
        // a 24 fps source keeps 24: the 30 fps rungs at its own rate
        Expect(Planned(PlanH264(1920, 1080, 24, 45, 110 * Mb, 14 * Mb)), 1280, 720, 24, 2.1, "720p24");
        // 29.97 is 30 (the 30 fps rungs), 59.94 is 60
        Expect(Planned(PlanH264(1920, 1080, 29.97, 45, 110 * Mb, 20 * Mb)), 1920, 1080, 30, 3.1, "1080p30");
        Expect(Planned(PlanH264(1920, 1080, 59.94, 45, 110 * Mb, 20 * Mb)), 1280, 720, 60, 3.1, "720p60");
    }

    [UnitTest]
    public static void Sources_over_60_fps_are_cut_evenly_and_odd_rates_keep_their_own_rate_on_the_fast_rungs()
    {
        // 60 fps class (20 MB / 45 s = 3.1 Mbps -> 720p): 50 stays 50, 120 -> 60 (every 2nd), 144 -> 48 (every 3rd),
        // 90 -> 45 is under 48 so 60 (uneven, by timestamp)
        Expect(Planned(PlanH264(1920, 1080, 50, 45, 110 * Mb, 20 * Mb)), 1280, 720, 50, 3.1, "720p50");
        Expect(Planned(PlanH264(1920, 1080, 120, 45, 110 * Mb, 20 * Mb)), 1280, 720, 60, 3.1, "720p60");
        Expect(Planned(PlanH264(1920, 1080, 144, 45, 110 * Mb, 20 * Mb)), 1280, 720, 48, 3.1, "720p48");
        Expect(Planned(PlanH264(1920, 1080, 90, 45, 110 * Mb, 20 * Mb)), 1280, 720, 60, 3.1, "720p60");
        // 30 fps class (20 MB / 139 s = 0.9 Mbps -> 480p, the 30 fps rung): 50 -> 25, 48 -> 24, 120 -> 30 (every 4th),
        // 144 -> 24 (every 6th; 144 / 5 is not whole), 165 -> 30 (165 / 6 is not whole, 165 / 7 is under 24)
        Expect(Planned(PlanH264(1920, 1080, 50, 139, 700 * Mb, 20 * Mb)), 854, 480, 25, 0.9, "480p25");
        Expect(Planned(PlanH264(1920, 1080, 48, 139, 700 * Mb, 20 * Mb)), 854, 480, 24, 0.9, "480p24");
        Expect(Planned(PlanH264(1920, 1080, 120, 139, 700 * Mb, 20 * Mb)), 854, 480, 30, 0.9, "480p30");
        Expect(Planned(PlanH264(1920, 1080, 144, 139, 700 * Mb, 20 * Mb)), 854, 480, 24, 0.9, "480p24");
        Expect(Planned(PlanH264(1920, 1080, 165, 139, 700 * Mb, 20 * Mb)), 854, 480, 30, 0.9, "480p30");
    }

    [UnitTest]
    public static void The_60_fps_ladder_ends_at_480p60_then_480p30()
    {
        // 20 MB / 100 s: 152e6 / 100 = 1 520 000 - 192 000 = 1 328 000 bps -> 1.3 Mbps: 720p60 needs 2.5, 480p60 needs 1.2.
        // 480 short side of 1920x1080: 1920 x 480 / 1080 = 853.3 -> 854.
        Expect(Planned(PlanH264(1920, 1080, 60, 100, 110 * Mb, 20 * Mb)), 854, 480, 60, 1.3, "480p60");
        // 20 MB / 139 s: 152e6 / 139 = 1 093 525 - 192 000 = 901 525 bps -> 0.9 Mbps: 480p60 needs 1.2, 480p30 needs 0.7.
        Expect(Planned(PlanH264(1920, 1080, 60, 139, 110 * Mb, 20 * Mb)), 854, 480, 30, 0.9, "480p30");
    }

    [UnitTest]
    public static void An_ultrawide_gets_even_dimensions_and_a_higher_floor()
    {
        // 3440x1440 is 3440 x 9 / (1440 x 16) = 1.34375 of a 16:9 frame. At 20 MB / 45 s (3.1 Mbps): 720p60 needs
        // 2.5 x 1.34375 = 3.359 > 3.1, so 480p60 (1.2 x 1.34375 = 1.6125): 3440 x 480 / 1440 = 1146.67 -> 1146 x 480.
        var p = Planned(PlanH264(3440, 1440, 60, 45, 150 * Mb, 20 * Mb));
        Expect(p, 1146, 480, 60, 3.1, "480p60");
        Check.Near(1.6125, p.FloorMbps, 1e-9);
        // 33 MB / 45 s: 33e6 x 0.95 x 8 / 45 = 5 573 333 - 192 000 = 5 381 333 bps -> 5.3 Mbps. A 16:9 clip keeps 1080p60 (5.0),
        // the ultrawide needs 5 x 1.34375 = 6.71875 for 2580x1080, so 720p60 (3.359): 3440 x 720 / 1440 = 1720.
        Expect(Planned(PlanH264(1920, 1080, 60, 45, 150 * Mb, 33 * Mb)), 1920, 1080, 60, 5.3, "1080p60");
        Expect(Planned(PlanH264(3440, 1440, 60, 45, 150 * Mb, 33 * Mb)), 1720, 720, 60, 5.3, "720p60");
        // 50 MB / 45 s: 8.444 - 0.192 = 8.25 -> 8.2 Mbps; 1080p60 for the ultrawide: 3440 x 1080 / 1440 = 2580 x 1080.
        Expect(Planned(PlanH264(3440, 1440, 60, 45, 150 * Mb, 50 * Mb)), 2580, 1080, 60, 8.2, "1080p60");
    }

    [UnitTest]
    public static void The_floor_of_an_ultrawide_is_rounded_up_to_a_tenth_for_the_trim_hint()
    {
        // lowest rung of 3440x1440: 480p30 at 0.7 x 1.34375 = 0.940625 -> 1.0 (the budget is floored to a tenth).
        // 20 MB: 152e6 bits / (1.0e6 + 192 000) = 127.5 s -> 127, less the 1 s keyframe interval -> 126.
        Check.Equal(126, ShareFit.MaxSecondsFor(3440, 1440, 60, 20 * Mb, "h264"));
        // 126 + 1 = 127 s: 152e6 / 127 = 1 196 850 - 192 000 = 1 004 850 bps -> 1.0 Mbps >= 0.9406: 480p30 at 1.0
        var p = Planned(PlanH264(3440, 1440, 60, 127, 700 * Mb, 20 * Mb));
        Expect(p, 1146, 480, 30, 1.0, "480p30");
        Check.Near(0.940625, p.FloorMbps, 1e-9);
        // 128 s: 152e6 / 128 = 1 187 500 - 192 000 = 995 500 bps -> 0.9 < 0.9406
        Check.Equal(ShareFitKind.CantFit, PlanH264(3440, 1440, 60, 128, 700 * Mb, 20 * Mb).Kind);
    }

    [UnitTest]
    public static void A_4_3_source_has_lower_floors()
    {
        // 1440x1080 is 1440 x 9 / (1080 x 16) = 0.75 of 16:9. 25 MB / 45 s = 4.0 Mbps: 1080p60 needs 5 x 0.75 = 3.75 -> kept
        // (a 16:9 clip drops to 720p60 here)
        var p = Planned(PlanH264(1440, 1080, 60, 45, 150 * Mb, 25 * Mb));
        Expect(p, 1440, 1080, 60, 4.0, "1080p60");
        Check.Near(3.75, p.FloorMbps, 1e-9);
        Expect(Planned(PlanH264(1920, 1080, 60, 45, 150 * Mb, 25 * Mb)), 1280, 720, 60, 4.0, "720p60");
        // 20 MB (3.1 Mbps): 3.75 > 3.1 -> 720p60 at 1.875: 1440 x 720 / 1080 = 960
        Expect(Planned(PlanH264(1440, 1080, 60, 45, 150 * Mb, 20 * Mb)), 960, 720, 60, 3.1, "720p60");
    }

    [UnitTest]
    public static void A_720p_source_is_never_upscaled()
    {
        // 50 MB / 45 s: 8.2 Mbps would pay for 1080p60, but the source is 720p: it starts at 720p60.
        Expect(Planned(PlanH264(1280, 720, 60, 45, 150 * Mb, 50 * Mb)), 1280, 720, 60, 8.2, "720p60");
        // odd source sizes end up even: the odd pixel is dropped (1281x721 -> 1280x720), not a rounding of 1281 x 720 / 721
        Expect(Planned(PlanH264(1281, 721, 60, 45, 150 * Mb, 50 * Mb)), 1280, 720, 60, 8.2, "720p60");
        // a 640x360 source is its own only rung: floors scaled by area (360/480)^2 = 0.5625 -> 0.675 at 60 fps; 3.1 Mbps is plenty
        Expect(Planned(PlanH264(640, 360, 60, 45, 150 * Mb, 20 * Mb)), 640, 360, 60, 3.1, "360p60");
    }

    [UnitTest]
    public static void An_in_between_source_keeps_its_own_size_when_the_limit_allows()
    {
        // 1600x900: floors = 720p's scaled by area (900/720)^2 = 1.5625 -> 2.5 x 1.5625 = 3.90625 at 60 fps (under 1080p's 5).
        // 50 MB / 45 s = 8.2 Mbps -> 900p60 at the source's own 1600x900
        var p = Planned(PlanH264(1600, 900, 60, 45, 150 * Mb, 50 * Mb));
        Expect(p, 1600, 900, 60, 8.2, "900p60");
        Check.Near(3.90625, p.FloorMbps, 1e-9);
        // 1 GB: far above the floor; still 900p, and never over 1080
        Expect(Planned(PlanH264(1600, 900, 60, 45, 1_200 * Mb, 1_000 * Mb)), 1600, 900, 60, 168.6, "900p60");
        // 20 MB / 45 s = 3.1 Mbps < 3.906 -> the next rung down, 720p60: 1600 x 720 / 900 = 1280
        Expect(Planned(PlanH264(1600, 900, 60, 45, 150 * Mb, 20 * Mb)), 1280, 720, 60, 3.1, "720p60");
        // at 30 fps the floor is 1.5 x 1.5625 = 2.34375: 3.1 Mbps keeps 900p30
        Expect(Planned(PlanH264(1600, 900, 30, 45, 150 * Mb, 20 * Mb)), 1600, 900, 30, 3.1, "900p30");
        // just under 1080: 1920x1078 floors are 720p's x (1078/720)^2 = 5.6 but at most 1080p's 5.0, times its shape
        // 1920 x 9 / (1078 x 16) = 1.00186 -> 5.0093
        Check.Near(5.0093, Planned(PlanH264(1920, 1078, 60, 45, 150 * Mb, 50 * Mb)).FloorMbps, 1e-3);
    }

    [UnitTest]
    public static void Odd_sizes_under_480p_are_made_even_and_have_a_30_fps_rung()
    {
        // 641x361 -> 640x360 (odd pixels dropped), floor 60 = 0.675 x 641 x 9 / (361 x 16) = 0.674, floor 30 = 0.3933
        Expect(Planned(PlanH264(641, 361, 60, 45, 150 * Mb, 20 * Mb)), 640, 360, 60, 3.1, "360p60");
        // 639x359 -> 638x358
        Expect(Planned(PlanH264(639, 359, 60, 45, 150 * Mb, 20 * Mb)), 638, 358, 60, 3.1, "358p60");
        // 20 MB / 200 s: 152e6 / 200 = 760 000 - 192 000 = 568 000 bps -> 0.5 Mbps: under the 60 fps floor 0.674, over the 30 fps one
        Expect(Planned(PlanH264(641, 361, 60, 200, 700 * Mb, 20 * Mb)), 640, 360, 30, 0.5, "360p30");
        // a portrait one: 361x641 -> 360x640
        Expect(Planned(PlanH264(361, 641, 60, 200, 700 * Mb, 20 * Mb)), 360, 640, 30, 0.5, "360p30");
    }

    [UnitTest]
    public static void A_portrait_source_is_named_by_its_short_side()
    {
        // 1080x1920 at 20 MB / 45 s (3.1 Mbps): same as landscape, 720p60 -> 720x1280
        Expect(Planned(PlanH264(1080, 1920, 60, 45, 110 * Mb, 20 * Mb)), 720, 1280, 60, 3.1, "720p60");
        // and 50 MB keeps the full 1080x1920 (8.2 Mbps)
        Expect(Planned(PlanH264(1080, 1920, 60, 45, 110 * Mb, 50 * Mb)), 1080, 1920, 60, 8.2, "1080p60");
    }

    [UnitTest]
    public static void A_clip_too_long_for_the_limit_cant_fit_and_says_how_long_would()
    {
        // 20 MB / 600 s: 152e6 / 600 = 253 333 - 192 000 = 61 333 bps = 0.06 Mbps, which rounds to a 0.0 budget, under 480p30's 0.7 -> can't fit.
        // Longest clip at 480p30: 152e6 bits / (0.7e6 + 192 000) = 170.4 s, less the 1 s source keyframe interval -> 169 s.
        var r = PlanH264(1920, 1080, 60, 600, 700 * Mb, 20 * Mb);
        Check.Equal(ShareFitKind.CantFit, r.Kind);
        Check.Null(r.Plan);
        Check.Equal(169, r.MaxSeconds);
        Check.Equal(169, ShareFit.MaxSecondsFor(1920, 1080, 60, 20 * Mb, "h264"));
        // following the hint: trimmed to 169 s, the copy starts up to 1 s early -> 170 s, which fits at 480p30:
        // 152e6 / 170 = 894 117 - 192 000 = 702 117 bps -> 0.7
        Expect(Planned(PlanH264(1920, 1080, 60, 170, 700 * Mb, 20 * Mb)), 854, 480, 30, 0.7, "480p30");
        // one more second does not: 152e6 / 171 = 888 888 - 192 000 = 696 888 bps -> 0.6
        Check.Equal(ShareFitKind.CantFit, PlanH264(1920, 1080, 60, 171, 700 * Mb, 20 * Mb).Kind);
    }

    [UnitTest]
    public static void The_trim_hint_allows_for_the_source_keyframe_interval()
    {
        // 2.5 s keyframes: 170.4 - 2.5 = 167.9 -> 167. 167 + 2.5 = 169.5 s: 152e6 / 169.5 = 896 755 - 192 000 = 704 755 -> 0.7 fits;
        // 170.5 s: 152e6 / 170.5 = 891 496 - 192 000 = 699 496 -> 0.6 does not.
        var r = ShareFit.Plan(1920, 1080, 60, 600, 700 * Mb, 20 * Mb, "h264", sourceKeyframeSeconds: 2.5);
        Check.Equal(167, r.MaxSeconds);
        Check.Equal(167, ShareFit.MaxSecondsFor(1920, 1080, 60, 20 * Mb, "h264", sourceKeyframeSeconds: 2.5));
        Check.Equal(ShareFitKind.Plan, ShareFit.Plan(1920, 1080, 60, 169.5, 700 * Mb, 20 * Mb, "h264", sourceKeyframeSeconds: 2.5).Kind);
        Check.Equal(ShareFitKind.CantFit, ShareFit.Plan(1920, 1080, 60, 170.5, 700 * Mb, 20 * Mb, "h264", sourceKeyframeSeconds: 2.5).Kind);
        // clamped like the engine's GOP (0.25 .. 5): 10 -> 5 -> 165; 0 -> 0.25 -> 170; NaN -> the 1 s default -> 169
        Check.Equal(165, ShareFit.MaxSecondsFor(1920, 1080, 60, 20 * Mb, "h264", sourceKeyframeSeconds: 10));
        Check.Equal(170, ShareFit.MaxSecondsFor(1920, 1080, 60, 20 * Mb, "h264", sourceKeyframeSeconds: 0));
        Check.Equal(169, ShareFit.MaxSecondsFor(1920, 1080, 60, 20 * Mb, "h264", sourceKeyframeSeconds: double.NaN));
    }

    [UnitTest]
    public static void One_GB_over_45_s_of_a_1440p60_source_is_capped_at_1080p60()
    {
        // 1e9 x 0.95 x 8 / 45 = 168 888 889 bps - 192 000 = 168 696 889 -> 168.6 Mbps: far above every floor, but 1080p is the top.
        // 2560x1440 -> 1920x1080 (2560 x 1080 / 1440 = 1920). Size: (168.6e6 + 192 000) x 45 / 8 = 949 455 000 bytes.
        var p = Planned(PlanH264(2560, 1440, 60, 45, 1_200 * Mb, 1_000 * Mb));
        Expect(p, 1920, 1080, 60, 168.6, "1080p60");
        Check.Equal(949_455_000L, p.EstimatedBytes);
    }

    [UnitTest]
    public static void HEVC_and_AV1_floors_are_70_percent_of_the_H264_ones()
    {
        // 25 MB / 45 s: 25e6 x 0.95 x 8 / 45 = 4 222 222 - 192 000 = 4 030 222 bps -> 4.0 Mbps.
        // H.264 1080p60 needs 5 -> 720p60. HEVC and AV1 need 5 x 0.7 = 3.5 -> 1080p60.
        Expect(Planned(PlanH264(1920, 1080, 60, 45, 110 * Mb, 25 * Mb)), 1280, 720, 60, 4.0, "720p60");
        foreach (string codec in new[] { "hevc", "av1" })
        {
            var p = Planned(ShareFit.Plan(1920, 1080, 60, 45, 110 * Mb, 25 * Mb, codec));
            Expect(p, 1920, 1080, 60, 4.0, "1080p60");
            Check.Equal(codec, p.Codec);
            Check.Near(3.5, p.FloorMbps, 1e-9);
        }
        // the lowest rung is 0.7 x 0.7 = 0.49 Mbps: longest clip in 20 MB = 152e6 / (0.5e6 + 192 000) = 219.6 -> 219, less 1 s -> 218
        // (the floor is rounded up to 0.5 because the budget is floored to a tenth)
        Check.Equal(218, ShareFit.MaxSecondsFor(1920, 1080, 60, 20 * Mb, "hevc"));
        // an unknown codec is H.264
        Check.Equal("h264", Planned(ShareFit.Plan(1920, 1080, 60, 45, 110 * Mb, 25 * Mb, "vp9")).Codec);
    }

    [UnitTest]
    public static void A_different_audio_rate_changes_the_budget()
    {
        // three tracks (576 kbps): 3 377 778 - 576 000 = 2 801 778 bps -> 2.8 Mbps -> 720p60 (2.5)
        Expect(Planned(ShareFit.Plan(1920, 1080, 60, 45, 110 * Mb, 20 * Mb, "h264", 576)), 1280, 720, 60, 2.8, "720p60");
    }

    // 45 s of 1080p60 H.264 at 3.0 Mbps video plus 5 Mix/mic/game tracks of 192 kbps: 16 875 000 + 5 400 000 = 22 275 000 bytes.
    private const long FiveTrackBytes = 22_275_000;

    [UnitTest]
    public static void An_H264_source_whose_video_fits_with_just_the_Mix_is_only_remuxed()
    {
        // source video 22 275 000 - 5 x 1 080 000 = 16 875 000; + the Mix 1 080 000 = 17 955 000 <= 19 000 000 (20 MB x 0.95)
        var r = ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "h264", sourceAudioTracks: 5, sourceCodec: "h264");
        Check.Equal(ShareFitKind.RemuxOnly, r.Kind);
        Check.Null(r.Plan);
        Check.Equal(17_955_000L, r.EstimatedBytes);
        // the codec ids are not case sensitive; hevc to hevc is a remux too
        Check.Equal(ShareFitKind.RemuxOnly, ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "h264", 192, 5, "H264").Kind);
        Check.Equal(ShareFitKind.RemuxOnly, ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "hevc", 192, 5, "hevc").Kind);
        // too big even so (5 tracks at 4.0 Mbps video: 22.5 MB + 1.08 MB > 19 MB): re-encode
        long big = 22_500_000 + 5_400_000;
        Check.Equal(ShareFitKind.Plan, ShareFit.Plan(1920, 1080, 60, 45, big, 20 * Mb, "h264", 192, 5, "h264").Kind);
        // asked not to remux (it came out too big): a re-encode plan
        Check.Equal(ShareFitKind.Plan, ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "h264", 192, 5, "h264", allowRemux: false).Kind);
    }

    [UnitTest]
    public static void A_source_in_another_codec_is_re_encoded_at_no_more_than_its_own_bitrate()
    {
        // HEVC source, H.264 out: no remux. Budget 3.1 Mbps; the source video is 3.0 Mbps
        // ((22 275 000 x 8 / 45) - 5 x 192 000 = 3 000 000 bps), and HEVC needs 1/0.7 of that as H.264 (4.28), so the
        // cap does not bind -> 720p60 at the budget, 3.1
        var r = ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "h264", sourceAudioTracks: 5, sourceCodec: "hevc");
        var p = Planned(r);
        Expect(p, 1280, 720, 60, 3.1, "720p60");
        Check.Equal(3_100_000L, p.VideoBitrateBps);
        Check.Equal(18_517_500L, p.EstimatedBytes);   // (3.1e6 + 192 000) x 45 / 8
        // unknown or unrecognised source codecs are not remuxed either
        Expect(Planned(ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "h264", 192, 5, null)), 1280, 720, 60, 3.0, "720p60");
        Expect(Planned(ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "h264", 192, 5, "vp9")), 1280, 720, 60, 3.0, "720p60");
        // the output codec decides: an H.264 source for an HEVC copy is re-encoded (HEVC floors: 1080p60 needs 3.5 > 3.1,
        // 720p60 needs 1.75), still capped at the source's 3.0
        var hevc = Planned(ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "hevc", 192, 5, "h264"));
        Expect(hevc, 1280, 720, 60, 3.0, "720p60");
        Check.Equal("hevc", hevc.Codec);
    }

    private static ShareFitPlan FirstPlan() => Planned(PlanH264(1920, 1080, 60, 45, 110 * Mb, 20 * Mb));   // 720p60 at 3.1

    [UnitTest]
    public static void An_overshoot_within_the_rung_lowers_the_bitrate_by_the_formula()
    {
        // audio: 192 000 x 45 / 8 = 1 080 000 bytes. Target total 20e6 x 0.95 = 19 000 000 -> target video 17 920 000.
        // Actual 21 000 000 -> actual video 19 920 000. New = 3.1 x (17 920 000 / 19 920 000) x 0.97 = 2.7051 -> 2.7 Mbps,
        // still above 720p60's 2.5. Size: (2.7e6 + 192 000) x 45 / 8 = 16 267 500.
        var r = ShareFit.NextAttempt(FirstPlan(), 21_000_000);
        var p = Planned(r);
        Expect(p, 1280, 720, 60, 2.7, "720p60");
        Check.Equal(2, p.Attempt);
        Check.Equal(16_267_500L, p.EstimatedBytes);
        Check.Equal(20 * Mb, p.LimitBytes);
        Check.Equal(45.0, p.DurationSeconds);
    }

    [UnitTest]
    public static void An_overshoot_below_the_floor_steps_down_a_rung()
    {
        // Actual 26 000 000 -> actual video 24 920 000. New = 3.1 x (17 920 000 / 24 920 000) x 0.97 = 2.1624 -> 2.1 Mbps,
        // under 720p60's 2.5 and over 480p60's 1.2 -> 480p60 at 2.1. Size: (2.1e6 + 192 000) x 45 / 8 = 12 892 500.
        var p = Planned(ShareFit.NextAttempt(FirstPlan(), 26_000_000));
        Expect(p, 854, 480, 60, 2.1, "480p60");
        Check.Equal(12_892_500L, p.EstimatedBytes);
        Check.Equal(2, p.Attempt);

        // Actual 60 000 000 -> video 58 920 000. New = 3.1 x (17 920 000 / 58 920 000) x 0.97 = 0.9146 -> 0.9 Mbps:
        // under 480p60's 1.2, over 480p30's 0.7 -> 480p30 at 0.9
        Expect(Planned(ShareFit.NextAttempt(FirstPlan(), 60_000_000)), 854, 480, 30, 0.9, "480p30");
    }

    [UnitTest]
    public static void A_30_fps_source_steps_down_to_720p30_on_a_retry()
    {
        // 20 MB / 45 s of 1080p30: 3.1 Mbps, 1080p30 (floor 3.0). Actual 21 000 000 -> 2.7 Mbps (as above): under 3.0 and over
        // 720p30's 1.5 -> 720p30 at 2.7. Never a 60 fps rung for a 30 fps source.
        var first = Planned(PlanH264(1920, 1080, 30, 45, 110 * Mb, 20 * Mb));
        Expect(first, 1920, 1080, 30, 3.1, "1080p30");
        var p = Planned(ShareFit.NextAttempt(first, 21_000_000));
        Expect(p, 1280, 720, 30, 2.7, "720p30");
        Check.Equal(16_267_500L, p.EstimatedBytes);
    }

    [UnitTest]
    public static void A_retry_that_would_need_less_than_the_lowest_floor_cant_fit()
    {
        // Actual 200 000 000 -> video 198 920 000. New = 3.1 x (17 920 000 / 198 920 000) x 0.97 = 0.2709 -> 0.2 Mbps < 0.7.
        // Overshoot k = 198 920 000 / (3.1e6 x 45 / 8 = 17 437 500) = 11.408. Longest clip: 152e6 / (0.7e6 x 11.408 + 192 000)
        // = 152e6 / 8 177 621 = 18.59, less the 1 s keyframe interval -> 17 (under the clip's own 44).
        var r = ShareFit.NextAttempt(FirstPlan(), 200_000_000);
        Check.Equal(ShareFitKind.CantFit, r.Kind);
        Check.Null(r.Plan);
        Check.Equal(17, r.MaxSeconds);
    }

    [UnitTest]
    public static void The_hint_after_a_failed_retry_allows_for_the_overshoot_and_stays_under_the_clips_length()
    {
        // 170 s of 1080p60 at 20 MB is 480p30 at 0.7 (video 14 875 000 bytes, Mix 4 080 000). It came out at 20 100 000:
        // video 16 020 000, k = 1.07698. Retry: 0.7 x (14 920 000 / 16 020 000) x 0.97 = 0.632 -> 0.6 < 0.7: can't fit.
        // Longest clip: 152e6 / (0.7e6 x 1.07698 + 192 000) = 152e6 / 945 882 = 160.70, less 1 s -> 159 (not 169, which
        // assumed the encoder hits its bitrate, the thing that just failed). The clip's own 170 s allows up to 169.
        var first = Planned(PlanH264(1920, 1080, 60, 170, 700 * Mb, 20 * Mb));
        Expect(first, 854, 480, 30, 0.7, "480p30");
        var r = ShareFit.NextAttempt(first, 20_100_000);
        Check.Equal(ShareFitKind.CantFit, r.Kind);
        Check.Equal(159, r.MaxSeconds);
    }

    [UnitTest]
    public static void The_fourth_attempt_fails()
    {
        var second = Planned(ShareFit.NextAttempt(FirstPlan(), 26_000_000));   // 480p60 at 2.1, attempt 2
        // Actual 20 500 000 -> video 19 420 000. 2.1 x (17 920 000 / 19 420 000) x 0.97 = 1.8797 -> 1.8 Mbps, over 480p60's 1.2.
        // Size: (1.8e6 + 192 000) x 45 / 8 = 11 205 000.
        var third = Planned(ShareFit.NextAttempt(second, 20_500_000));
        Expect(third, 854, 480, 60, 1.8, "480p60");
        Check.Equal(3, third.Attempt);
        Check.Equal(11_205_000L, third.EstimatedBytes);

        // a retry after the third attempt is the fourth: Failed. k = 19 920 000 / (1.8e6 x 45 / 8 = 10 125 000) = 1.9674;
        // 152e6 / (0.7e6 x 1.9674 + 192 000) = 96.9, less 1 s -> 95, but under the clip's own length less the 1 s keyframe interval: ceil(45 - 1) - 1 = 43.
        var r = ShareFit.NextAttempt(third, 21_000_000);
        Check.Equal(ShareFitKind.Failed, r.Kind);
        Check.Null(r.Plan);
        Check.Equal(43, r.MaxSeconds);
    }

    [UnitTest]
    public static void A_copy_that_fits_is_reported_as_CopyFits_and_a_bad_size_is_refused()
    {
        Check.Equal(ShareFitKind.CopyFits, ShareFit.NextAttempt(FirstPlan(), 19_500_000).Kind);
        Check.Equal(ShareFitKind.CopyFits, ShareFit.NextAttempt(FirstPlan(), 20 * Mb).Kind, "exactly at the limit");
        Check.Equal(ShareFitKind.NoCopyNeeded, PlanH264(1920, 1080, 60, 45, 19_500_000, 20 * Mb).Kind);
        Check.NotEqual(ShareFitKind.NoCopyNeeded, ShareFitKind.CopyFits);
        Check.Throws<ArgumentOutOfRangeException>(() => ShareFit.NextAttempt(FirstPlan(), 0), "zero bytes");
        Check.Throws<ArgumentOutOfRangeException>(() => ShareFit.NextAttempt(FirstPlan(), -1), "negative bytes");
    }

    private static ShareFitResult[] SampleResults()
    {
        var first = Planned(PlanH264(2560, 1440, 59.94, 73.5, 300 * Mb, 50 * Mb));
        return new[]
        {
            PlanH264(2560, 1440, 59.94, 73.5, 300 * Mb, 50 * Mb),
            PlanH264(1920, 1080, 29.97, 45, 110 * Mb, 20 * Mb),
            PlanH264(1920, 1080, 144, 139, 700 * Mb, 20 * Mb),
            PlanH264(1920, 1080, 60, 600, 700 * Mb, 20 * Mb),
            ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "h264", 192, 5, "h264"),
            ShareFit.Plan(1920, 1080, 60, 45, FiveTrackBytes, 20 * Mb, "av1", 192, 5, "hevc"),
            ShareFit.NextAttempt(first, 55 * Mb),
            ShareFit.NextAttempt(first, 400 * Mb),
        };
    }

    [UnitTest]
    public static void The_planner_gives_the_same_answer_every_time_in_any_culture()
    {
        var before = SampleResults();
        Check.Equal("1080p30", Planned(before[1]).Label);
        Check.Equal(before, SampleResults(), "same culture");

        var savedCulture = CultureInfo.CurrentCulture;
        var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            var german = new CultureInfo("de-DE");
            Check.Equal(",", german.NumberFormat.NumberDecimalSeparator, "the test culture uses a decimal comma");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;
            var after = SampleResults();
            Check.Equal(before, after, "de-DE");
            Check.Equal("1080p30", Planned(after[1]).Label);
            Check.Equal("480p24", Planned(after[2]).Label);
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }
}
