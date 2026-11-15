using Clink.Native;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Which screen is captured: the follow-the-main-display rule and the settings migration behind it.</summary>
internal static class CaptureTargetTests
{
    private static MonitorInfo Mon(int n, bool primary) => new(n, $@"\\.\DISPLAY{n}", "", n * 100, 0, 1920, 1080, primary, false, 80, 0, "");

    private static readonly MonitorInfo D1 = Mon(1, true), D2 = Mon(2, false), D3 = Mon(3, false);

    [UnitTest]
    public static void Following_the_main_display_picks_the_primary_whatever_is_saved()
    {
        var list = new List<MonitorInfo> { D2, D1, D3 };
        Check.Equal(D1.DeviceName, CaptureTarget.Resolve(true, null, list)!.DeviceName);
        Check.Equal(D1.DeviceName, CaptureTarget.Resolve(true, D3.DeviceName, list)!.DeviceName, "the saved screen is only remembered");
    }

    [UnitTest]
    public static void Following_picks_the_new_primary_when_the_main_display_changes()
    {
        var swapped = new List<MonitorInfo> { D1 with { IsPrimary = false }, D2 with { IsPrimary = true }, D3 };
        Check.Equal(D2.DeviceName, CaptureTarget.Resolve(true, D1.DeviceName, swapped)!.DeviceName);
    }

    [UnitTest]
    public static void Not_following_picks_the_saved_screen_even_when_it_is_not_the_primary()
    {
        var list = new List<MonitorInfo> { D1, D2, D3 };
        Check.Equal(D3.DeviceName, CaptureTarget.Resolve(false, D3.DeviceName, list)!.DeviceName);
        Check.Equal(D3.DeviceName, CaptureTarget.Resolve(false, D3.DeviceName.ToLowerInvariant(), list)!.DeviceName, "the name is compared without case");
        var swapped = new List<MonitorInfo> { D1 with { IsPrimary = false }, D2 with { IsPrimary = true }, D3 };
        Check.Equal(D3.DeviceName, CaptureTarget.Resolve(false, D3.DeviceName, swapped)!.DeviceName, "a new main display does not move it");
    }

    [UnitTest]
    public static void Not_following_falls_back_to_the_primary_when_the_saved_screen_is_missing_or_never_picked()
    {
        var list = new List<MonitorInfo> { D2, D1 };
        Check.Equal(D1.DeviceName, CaptureTarget.Resolve(false, D3.DeviceName, list)!.DeviceName, "saved screen unplugged");
        Check.Equal(D1.DeviceName, CaptureTarget.Resolve(false, null, list)!.DeviceName, "nothing saved");
        Check.Equal(D1.DeviceName, CaptureTarget.Resolve(false, "", list)!.DeviceName, "empty name");
    }

    [UnitTest]
    public static void With_no_primary_the_first_screen_is_used_and_an_empty_list_gives_none()
    {
        var noPrimary = new List<MonitorInfo> { D2, D3 };
        Check.Equal(D2.DeviceName, CaptureTarget.Resolve(true, null, noPrimary)!.DeviceName);
        Check.Equal(D2.DeviceName, CaptureTarget.Resolve(false, "gone", noPrimary)!.DeviceName);
        Check.Null(CaptureTarget.Resolve(true, null, new List<MonitorInfo>()));
        Check.Null(CaptureTarget.Resolve(false, D1.DeviceName, new List<MonitorInfo>()));
    }

    [UnitTest]
    public static void A_file_without_the_follow_key_follows_only_when_no_screen_was_ever_picked()
    {
        Check.True(Settings.FromJson("""{ "MonitorDevice": null }""").FollowPrimaryMonitor, "never picked: as before");
        Check.True(Settings.FromJson("{}").FollowPrimaryMonitor, "no keys at all");
        Check.False(Settings.FromJson("""{ "MonitorDevice": "\\\\.\\DISPLAY2" }""").FollowPrimaryMonitor, "a picked screen is kept");
        Check.True(new Settings().FollowPrimaryMonitor, "fresh install");
    }

    [UnitTest]
    public static void The_follow_key_in_a_file_wins_over_the_migration_and_is_saved()
    {
        Check.True(Settings.FromJson("""{ "MonitorDevice": "\\\\.\\DISPLAY2", "FollowPrimaryMonitor": true }""").FollowPrimaryMonitor);
        var off = Settings.FromJson("""{ "MonitorDevice": null, "FollowPrimaryMonitor": false }""");
        Check.False(off.FollowPrimaryMonitor);
        Check.Contains(off.ToJson(), "\"FollowPrimaryMonitor\": false");
        Check.Contains(Settings.FromJson("""{ "MonitorDevice": "\\\\.\\DISPLAY2" }""").ToJson(), "\"FollowPrimaryMonitor\": false", "the migrated value is written on the next save");
    }

    [UnitTest]
    public static void A_follow_key_of_the_wrong_kind_costs_only_itself()
    {
        var s = Settings.FromJson("""{ "MonitorDevice": "\\\\.\\DISPLAY2", "FollowPrimaryMonitor": "yes" }""");
        Check.True(s.FollowPrimaryMonitor, "the default");
        Check.Equal(@"\\.\DISPLAY2", s.MonitorDevice);
    }
}
