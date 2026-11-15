using Clink.Capture;
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

    // ---- capture cards ----
    private static readonly VideoDevice CardA = new(@"\\?\usb#vid_0fd9&pid_0066#a", "Elgato HD60 X");
    private static readonly VideoDevice CardB = new(@"\\?\usb#vid_0fd9&pid_006d#b", "Elgato Cam Link 4K");
    private static readonly IReadOnlyList<VideoDevice> NoCards = new List<VideoDevice>();
    private static CaptureChoice Resolve(string? source, bool follow, string? saved, params VideoDevice[] cards)
        => CaptureTarget.Resolve(source, follow, saved, new List<MonitorInfo> { D1, D2, D3 }, cards);

    [UnitTest]
    public static void A_screen_source_gives_the_screen_whatever_cards_are_connected()
    {
        foreach (string? source in new[] { null, "", "monitor", "garbage", "device:" })
        {
            var r = Resolve(source, false, D2.DeviceName, CardA);
            Check.Equal(CaptureKind.Monitor, r.Kind, source);
            Check.Equal(D2.DeviceName, r.Monitor!.DeviceName, source);
            Check.Equal(CaptureFallback.None, r.Fallback, source);
        }
        Check.Equal(D1.DeviceName, Resolve("monitor", true, D2.DeviceName).Monitor!.DeviceName, "following the main display");
    }

    [UnitTest]
    public static void A_connected_card_is_the_target_and_its_link_is_matched_without_case()
    {
        var r = Resolve("device:" + CardA.Link, false, D2.DeviceName, CardB, CardA);
        Check.Equal(CaptureKind.Device, r.Kind);
        Check.Equal(CardA.Link, r.Device!.Link);
        Check.Null(r.Monitor);
        Check.Equal(CaptureFallback.None, r.Fallback);
        Check.Equal(CaptureKind.Device, Resolve("device:" + CardA.Link.ToUpperInvariant(), true, null, CardA).Kind);
    }

    [UnitTest]
    public static void A_missing_card_falls_back_to_the_last_selected_screen_or_to_the_main_display_when_following()
    {
        var saved = Resolve("device:" + CardA.Link, false, D3.DeviceName, CardB);
        Check.Equal(CaptureKind.Monitor, saved.Kind);
        Check.Equal(D3.DeviceName, saved.Monitor!.DeviceName, "the last selected screen");
        Check.Equal(CaptureFallback.CardMissing, saved.Fallback);
        var follow = Resolve("device:" + CardA.Link, true, D3.DeviceName);
        Check.Equal(D1.DeviceName, follow.Monitor!.DeviceName, "the main display");
        Check.Equal(CaptureFallback.CardMissing, follow.Fallback);
        var none = CaptureTarget.Resolve("device:" + CardA.Link, true, null, new List<MonitorInfo>(), NoCards);
        Check.Null(none.Monitor, "no screen at all");
        Check.Equal(CaptureFallback.CardMissing, none.Fallback);
    }

    [UnitTest]
    public static void An_empty_saved_screen_name_is_not_a_saved_screen()
    {
        var nameless = new MonitorInfo(9, "", "", 0, 0, 1920, 1080, false, false, 80, 0, "");
        var list = new List<MonitorInfo> { nameless, D1 };
        Check.Equal(D1.DeviceName, CaptureTarget.Resolve(false, "", list)!.DeviceName);
        Check.Equal(D1.DeviceName, CaptureTarget.Resolve(false, null, list)!.DeviceName);
    }

    private static CaptureChoice OnCard(VideoDevice c) => new(CaptureKind.Device, c, null, CaptureFallback.None);
    private static CaptureChoice OnScreen(MonitorInfo m) => new(CaptureKind.Monitor, null, m, CaptureFallback.None);
    private static CaptureChoice Fallback(MonitorInfo m) => new(CaptureKind.Monitor, null, m, CaptureFallback.CardMissing);

    [UnitTest]
    public static void The_same_target_needs_no_action()
    {
        Check.Equal(SwitchAction.None, CaptureTarget.NextAction(OnScreen(D1), OnScreen(D1), false).Action);
        Check.Equal(SwitchAction.None, CaptureTarget.NextAction(OnCard(CardA), OnCard(CardA), false).Action);
        Check.Equal(SwitchAction.None, CaptureTarget.NextAction(Fallback(D1), Fallback(D1), true).Action);
    }

    [UnitTest]
    public static void Starting_with_a_missing_card_asks_for_the_fallback_notice_once()
    {
        var first = CaptureTarget.NextAction(null, Fallback(D1), false);
        Check.Equal(SwitchAction.RestartAndNotifyFallback, first.Action);
        Check.True(first.FallbackNoticed);
        Check.Equal(SwitchAction.Restart, CaptureTarget.NextAction(null, OnScreen(D1), false).Action, "a normal start");
        Check.Equal(SwitchAction.Restart, CaptureTarget.NextAction(null, OnCard(CardA), false).Action);
    }

    [UnitTest]
    public static void Unplugging_notifies_once_and_a_screen_change_while_it_is_missing_restarts_quietly()
    {
        var gone = CaptureTarget.NextAction(OnCard(CardA), Fallback(D1), false);
        Check.Equal(SwitchAction.RestartAndNotifyFallback, gone.Action);
        Check.True(gone.FallbackNoticed);
        var again = CaptureTarget.NextAction(Fallback(D1), Fallback(D1), gone.FallbackNoticed);
        Check.Equal(SwitchAction.None, again.Action, "the display change re-resolved to the same screen");
        var moved = CaptureTarget.NextAction(Fallback(D1), Fallback(D2), again.FallbackNoticed);
        Check.Equal(SwitchAction.Restart, moved.Action, "a different fallback screen, no second notice");
        Check.True(moved.FallbackNoticed);
    }

    [UnitTest]
    public static void The_card_coming_back_asks_for_the_return_notice_and_a_later_unplug_notices_again()
    {
        var back = CaptureTarget.NextAction(Fallback(D1), OnCard(CardA), true);
        Check.Equal(SwitchAction.RestartAndNotifyReturn, back.Action);
        Check.False(back.FallbackNoticed, "the missing streak ended");
        Check.Equal(SwitchAction.RestartAndNotifyFallback, CaptureTarget.NextAction(OnCard(CardA), Fallback(D1), back.FallbackNoticed).Action, "a new streak");
    }

    [UnitTest]
    public static void Choosing_another_source_restarts_without_a_notice_and_ends_the_streak()
    {
        var toScreen = CaptureTarget.NextAction(Fallback(D1), OnScreen(D2), true);
        Check.Equal(SwitchAction.Restart, toScreen.Action, "the user picked a screen while the card was missing");
        Check.False(toScreen.FallbackNoticed);
        Check.Equal(SwitchAction.Restart, CaptureTarget.NextAction(OnCard(CardA), OnScreen(D1), false).Action);
        Check.Equal(SwitchAction.Restart, CaptureTarget.NextAction(OnScreen(D1), OnCard(CardA), false).Action);
        Check.Equal(SwitchAction.Restart, CaptureTarget.NextAction(OnCard(CardA), OnCard(CardB), false).Action, "another card");
    }
}
