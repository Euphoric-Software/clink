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
    public static void A_cut_never_splits_a_surrogate_pair()
    {
        // 26 letters then an emoji (2 chars) straddling the cut at 27 chars: the pair goes whole or not at all
        string leaf = DevInstanceMarker.FolderLeaf(@"C:\" + new string('a', 26) + "\U0001F600" + new string('b', 10));
        Check.True(leaf.EndsWith('…'));
        Check.Equal(26 + 1, leaf.Length);
        Check.False(leaf.Any(char.IsSurrogate), "no lone half of a pair");
        string wholePair = DevInstanceMarker.FolderLeaf(@"C:\" + new string('a', 25) + "\U0001F600" + new string('b', 10));
        Check.True(wholePair.Contains("\U0001F600"));
        Check.Equal(28, wholePair.Length);
    }

    [UnitTest]
    public static void Only_secondary_windows_are_tagged()
    {
        Check.False(DevInstanceMarker.ShouldTagTitle(typeof(Clink.MainWindow)), "the main window is titled on its own");
        Check.False(DevInstanceMarker.ShouldTagTitle(typeof(Clink.UI.ToastWindow)));
        Check.False(DevInstanceMarker.ShouldTagTitle(typeof(Clink.UI.StatusDotWindow)));
        Check.False(DevInstanceMarker.ShouldTagTitle(typeof(Clink.UI.Screenshots.RegionOverlayWindow)), "capture overlay");
        Check.True(DevInstanceMarker.ShouldTagTitle(typeof(System.Windows.Window)));
        Check.True(DevInstanceMarker.ShouldTagTitle(typeof(Clink.UI.CopiumConsentWindow)));
        Check.False(DevInstanceMarker.ShouldTagTitle(new Clink.UI.ToastWindow()), "by instance too");
        Check.True(DevInstanceMarker.ShouldTagTitle(new System.Windows.Window()));
    }

    [UnitTest]
    public static void A_tagged_window_is_tagged_again_after_it_sets_a_new_title()
    {
        var w = new System.Windows.Window { Title = "Trim clip" };
        DevInstanceMarker.KeepTitleTagged(w);
        Check.Equal("Trim clip [TEST]", w.Title);
        w.Title = "Trim clip";                       // what the trim window does when it changes mode
        Check.Equal("Trim clip [TEST]", w.Title);
        w.Title = "Trim recording";
        Check.Equal("Trim recording [TEST]", w.Title);
        w.Title = "Already [TEST]";
        Check.Equal("Already [TEST]", w.Title, "once only");
        w.Close();                                   // closed: the watch is gone
        w.Title = "After close";
        Check.Equal("After close", w.Title);
        var toast = new Clink.UI.ToastWindow { Title = "Clink" };
        DevInstanceMarker.KeepTitleTagged(toast);
        toast.Title = "Clink 2";
        Check.Equal("Clink 2", toast.Title, "an excluded window stays as it is");
    }

    [UnitTest]
    public static void A_toast_is_marked_only_in_a_test_instance()
    {
        string before = AppController.InstanceSuffix;
        try
        {
            AppController.InstanceSuffix = "";
            var normal = new Clink.UI.ToastWindow();
            Check.Equal(System.Windows.Visibility.Collapsed, normal.TestTag.Visibility);
            Check.Equal(new System.Windows.Thickness(1), normal.Card.BorderThickness);
            Check.Equal(new System.Windows.Thickness(14, 12, 14, 12), normal.Card.Padding);

            AppController.InstanceSuffix = ".dev.abc123";
            var dev = new Clink.UI.ToastWindow();
            Check.Equal(System.Windows.Visibility.Visible, dev.TestTag.Visibility);
            Check.Equal(new System.Windows.Thickness(3), dev.Card.BorderThickness);
            var brush = (System.Windows.Media.SolidColorBrush)dev.Card.BorderBrush;
            Check.Equal(System.Windows.Media.Color.FromRgb(0xFF, 0xD4, 0x00), brush.Color);
            // 2 px thicker frame, 2 px less padding per side: the inside (and the screenshot button row) is as wide as a normal toast's
            Check.Equal(normal.Card.BorderThickness.Left + normal.Card.Padding.Left, dev.Card.BorderThickness.Left + dev.Card.Padding.Left);
            Check.Equal(normal.Card.BorderThickness.Top + normal.Card.Padding.Top, dev.Card.BorderThickness.Top + dev.Card.Padding.Top);
        }
        finally { AppController.InstanceSuffix = before; }
    }

    [UnitTest]
    public static void A_long_toast_title_is_cut_and_the_test_tag_stays()
    {
        string before = AppController.InstanceSuffix;
        try
        {
            AppController.InstanceSuffix = ".dev.abc123";
            var toast = new Clink.UI.ToastWindow();
            toast.TitleText.Text = new string('W', 200);
            // (a window never shown has no layout of its own: lay its card out at the toast's width)
            toast.Card.Measure(new System.Windows.Size(330, double.PositiveInfinity));
            toast.Card.Arrange(new System.Windows.Rect(0, 0, 330, toast.Card.DesiredSize.Height));
            toast.Card.UpdateLayout();
            Check.Equal(System.Windows.TextTrimming.CharacterEllipsis, toast.TitleText.TextTrimming);
            Check.True(toast.TestTag.ActualWidth > 0, "the tag has its size");
            var tagBox = toast.TestTag.TransformToAncestor(toast.Card).TransformBounds(new System.Windows.Rect(0, 0, toast.TestTag.ActualWidth, toast.TestTag.ActualHeight));
            Check.True(tagBox.Right <= toast.Card.ActualWidth, "the tag is inside the card");
            Check.True(toast.TitleText.ActualWidth + toast.TestTag.ActualWidth < toast.Card.ActualWidth, "the title gave way to it");
        }
        finally { AppController.InstanceSuffix = before; }
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
