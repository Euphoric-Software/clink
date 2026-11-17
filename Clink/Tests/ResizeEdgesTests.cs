using System.Windows;
using System.Windows.Shell;
using Clink.Screenshots.Editing;
using Clink.UI;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>
/// The resizable windows (borderless and transparent) are sizeable from all four edges and corners. A transparent window has no native sizing
/// border, and CanResizeWithGrip only gives the bottom-right grip; WindowChrome's ResizeBorderThickness supplies the other edges. The mouse hit-testing
/// itself (WM_NCHITTEST) cannot be driven from a unit test, so this checks the configuration that produces it. Windows are built, never shown.
/// </summary>
internal static class ResizeEdgesTests
{
    private static void CheckResizable(Window w, string name)
    {
        try
        {
            Check.Equal(ResizeMode.CanResize, w.ResizeMode, name + " resizes from the edges, not only a grip");
            var chrome = WindowChrome.GetWindowChrome(w);
            Check.True(chrome != null, name + " has a WindowChrome");
            var t = chrome!.ResizeBorderThickness;
            Check.True(t.Left >= 4 && t.Top >= 4 && t.Right >= 4 && t.Bottom >= 4, name + " has a grabbable border on all four sides: " + t);
            Check.Equal(0.0, chrome.CaptionHeight, name + " keeps its own header drag (no native caption)");
            var g = chrome.GlassFrameThickness;
            Check.True(g.Left == 0 && g.Top == 0 && g.Right == 0 && g.Bottom == 0, name + " draws no glass frame: " + g);
            Check.True(w.Background is System.Windows.Media.SolidColorBrush { Color.A: > 0 }, name + " is not alpha-0, so its rounded corners take clicks (corner resize)");
            Check.True(w.MinWidth > 0 && w.MinHeight > 0, name + " keeps its minimum size");
        }
        finally { w.Close(); }
    }

    [UnitTest]
    public static void The_main_window_resizes_from_all_four_edges_and_corners() => CheckResizable(new MainWindow(), "MainWindow");

    [UnitTest]
    public static void The_notices_and_insert_image_windows_resize_from_all_four_edges_and_corners()
    {
        CheckResizable(new ThirdPartyNoticesWindow(), "ThirdPartyNoticesWindow");
        CheckResizable(new InsertImagePicker(new InsertPickerModel(null), canPaste: true), "InsertImagePicker");
    }
}
