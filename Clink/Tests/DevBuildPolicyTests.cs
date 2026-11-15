using Clink.Services;

namespace Clink.Tests;

// The Dev Build channel's pure rules: how often it checks, and the hidden box's 10-click reveal. No network, no UI.
internal static class DevBuildPolicyTests
{
    [UnitTest]
    public static void Interval_is_six_hours_on_stable_and_about_two_minutes_on_dev()
    {
        Check.Equal(TimeSpan.FromHours(6), DevBuildPolicy.Interval(false, 0, 0.5));
        Check.Equal(TimeSpan.FromHours(6), DevBuildPolicy.Interval(false, 5, 0.9), "stable ignores failures and jitter");
        Check.Equal(TimeSpan.FromMinutes(2), DevBuildPolicy.Interval(true, 0, 0.5), "dev: 2 min without jitter");
        var lo = DevBuildPolicy.Interval(true, 0, 0);
        var hi = DevBuildPolicy.Interval(true, 0, 1);
        Check.True(lo >= TimeSpan.FromSeconds(107) && lo < TimeSpan.FromMinutes(2), "jitter down to -10 %");
        Check.True(hi > TimeSpan.FromMinutes(2) && hi <= TimeSpan.FromSeconds(133), "jitter up to +10 %");
    }

    [UnitTest]
    public static void Dev_interval_backs_off_after_failures_and_is_capped()
    {
        Check.Equal(TimeSpan.FromMinutes(4), DevBuildPolicy.Interval(true, 1, 0.5));
        Check.Equal(TimeSpan.FromMinutes(8), DevBuildPolicy.Interval(true, 2, 0.5));
        Check.Equal(DevBuildPolicy.DevMaxBackoff, DevBuildPolicy.Interval(true, 3, 0.5), "16 min is cut to the cap");
        Check.Equal(DevBuildPolicy.DevMaxBackoff, DevBuildPolicy.Interval(true, 100, 1), "never above the cap, jitter included");
        Check.Equal(TimeSpan.FromMinutes(2), DevBuildPolicy.Interval(true, -3, 0.5), "a negative count counts as none");
    }

    [UnitTest]
    public static void Failed_passes_count_up_to_a_cap_and_a_good_pass_resets()
    {
        int n = 0;
        n = DevBuildPolicy.NextFailures(n, true); Check.Equal(1, n);
        n = DevBuildPolicy.NextFailures(n, true); Check.Equal(2, n);
        for (int i = 0; i < 50; i++) n = DevBuildPolicy.NextFailures(n, true);
        Check.Equal(10, n, "capped");
        Check.Equal(0, DevBuildPolicy.NextFailures(n, false), "a pass without a problem resets");
    }

    [UnitTest]
    public static void Loop_tick_is_finer_on_dev_than_the_interval()
    {
        Check.True(DevBuildPolicy.LoopTick(true) < DevBuildPolicy.Interval(true, 0, 0), "the tick must not hide the 2 min interval");
        Check.True(DevBuildPolicy.LoopTick(true) < DevBuildPolicy.LoopTick(false));
    }

    [UnitTest]
    public static void Box_is_visible_when_revealed_or_ticked()
    {
        Check.False(DevBuildPolicy.BoxVisible(false, false), "hidden by default");
        Check.True(DevBuildPolicy.BoxVisible(true, false), "revealed this session");
        Check.True(DevBuildPolicy.BoxVisible(false, true), "a dev user can always switch back");
        Check.True(DevBuildPolicy.BoxVisible(true, true));
    }

    [UnitTest]
    public static void Ten_quick_clicks_reveal_and_a_gap_resets()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var c = new SecretClickCounter();
        for (int i = 0; i < 9; i++) Check.False(c.Click(t0.AddMilliseconds(i * 500)), $"click {i + 1} is not enough");
        Check.True(c.Click(t0.AddMilliseconds(9 * 500)), "the 10th click reveals");

        // A gap over 1.5 s starts the count again.
        c = new SecretClickCounter();
        for (int i = 0; i < 9; i++) c.Click(t0.AddMilliseconds(i * 100));
        Check.False(c.Click(t0.AddMilliseconds(800 + 1600)), "gap of 1.6 s: this is click 1 again");
        for (int i = 1; i < 9; i++) Check.False(c.Click(t0.AddMilliseconds(2400 + i * 100)));
        Check.True(c.Click(t0.AddMilliseconds(2400 + 900)), "10 clicks counted from the restart");

        // Exactly the maximum gap still counts; a clock that steps back restarts.
        c = new SecretClickCounter();
        for (int i = 0; i < 9; i++) c.Click(t0.AddMilliseconds(i * 1500));
        Check.True(c.Click(t0.AddMilliseconds(9 * 1500)), "gaps of exactly 1.5 s count");
        c = new SecretClickCounter();
        for (int i = 0; i < 9; i++) c.Click(t0.AddMilliseconds(i * 100));
        Check.False(c.Click(t0.AddSeconds(-5)), "time going backwards resets");
    }
}
