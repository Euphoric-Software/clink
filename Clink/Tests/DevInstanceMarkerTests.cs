using Clink.Services;

namespace Clink.Tests;

/// <summary>How a <c>--dev</c> test instance marks itself: window title, header badge and its tooltip, tray text, the tag on other windows, and which header part gives way first.</summary>
internal static class DevInstanceMarkerTests
{
    [UnitTest]
    public static void The_folder_name_is_the_last_part_of_the_dev_folder()
    {
        Check.Equal("clink-test", DevInstanceMarker.FolderLeaf(@"C:\work\clink-test"));
        Check.Equal("clink-test", DevInstanceMarker.FolderLeaf(@"C:\work\clink-test\"));
        Check.Equal("clink-test", DevInstanceMarker.FolderLeaf(@"C:\work\clink-test\\"));
        Check.Equal("clink-test", DevInstanceMarker.FolderLeaf("  C:/work/clink-test  "));
        Check.Equal("x", DevInstanceMarker.FolderLeaf("x"));
        Check.Equal("C:", DevInstanceMarker.FolderLeaf(@"C:\"));
        Check.Equal("dev", DevInstanceMarker.FolderLeaf(""));
        Check.Equal("dev", DevInstanceMarker.FolderLeaf(null));
        Check.Equal("dev", DevInstanceMarker.FolderLeaf("   "));
    }

    [UnitTest]
    public static void A_very_long_folder_name_is_cut_with_an_ellipsis()
    {
        string leaf = DevInstanceMarker.FolderLeaf(@"C:\" + new string('a', 60));
        Check.Equal(28, leaf.Length);
        Check.True(leaf.EndsWith('…'));
    }

    [UnitTest]
    public static void The_texts_of_a_test_instance()
    {
        const string folder = @"D:\t\clink-test";
        Check.Equal("Clink TEST (clink-test)", DevInstanceMarker.WindowTitle(folder));
        Check.Equal("TEST · clink-test", DevInstanceMarker.BadgeText(folder, shortForm: false));
        Check.Equal("TEST", DevInstanceMarker.BadgeText(folder, shortForm: true));
        Check.Equal(@"Test instance (--dev D:\t\clink-test). Your main Clink is separate.", DevInstanceMarker.BadgeTooltip(folder));
        Check.Equal("TEST · Clink (dev) · Recording", DevInstanceMarker.TrayText("Clink (dev) · Recording"));
        Check.Equal("#FFD400", DevInstanceMarker.YellowHex);
    }

    [UnitTest]
    public static void Other_windows_get_the_test_tag_once()
    {
        Check.Equal("Trim clip [TEST]", DevInstanceMarker.TagTitle("Trim clip"));
        Check.Equal("Trim clip [TEST]", DevInstanceMarker.TagTitle("Trim clip [TEST]"));
        Check.Equal("", DevInstanceMarker.TagTitle(""));
        Check.Equal("", DevInstanceMarker.TagTitle(null));
    }

    [UnitTest]
    public static void A_short_header_drops_the_status_text_before_it_cuts_the_badge()
    {
        // lead 60 (dot and "Clink"), full badge 130, at least 90 for the status text
        Check.Equal((true, false), DevInstanceMarker.FitHeader(400, 60, 130, 90, hasBadge: true));   // room for all
        Check.Equal((true, false), DevInstanceMarker.FitHeader(280, 60, 130, 90, hasBadge: true));   // exactly enough
        Check.Equal((false, false), DevInstanceMarker.FitHeader(279, 60, 130, 90, hasBadge: true));  // status text goes first
        Check.Equal((false, false), DevInstanceMarker.FitHeader(190, 60, 130, 90, hasBadge: true));  // the badge still fits whole
        Check.Equal((false, true), DevInstanceMarker.FitHeader(189, 60, 130, 90, hasBadge: true));   // only then "TEST"
        Check.Equal((false, true), DevInstanceMarker.FitHeader(50, 60, 130, 90, hasBadge: true));
    }

    [UnitTest]
    public static void Without_a_badge_the_status_text_is_always_shown()
    {
        Check.Equal((true, false), DevInstanceMarker.FitHeader(10, 60, 130, 90, hasBadge: false));
        Check.Equal((true, false), DevInstanceMarker.FitHeader(500, 60, 130, 90, hasBadge: false));
    }
}
