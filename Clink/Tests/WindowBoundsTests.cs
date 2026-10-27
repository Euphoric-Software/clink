using System.Windows;
using Clink;

namespace Clink.Tests;

/// <summary>The main window's position clamp: the drag area stays reachable on a monitor.</summary>
internal static class WindowBoundsTests
{
    [UnitTest]
    public static void The_drag_area_is_kept_reachable_on_the_work_area()
    {
        var area = new Rect(0, 0, 1920, 1040);
        // inside: untouched
        Check.Equal(new Point(100, 50), MainWindow.KeepDragAreaReachable(area, new Point(100, 50), 1100));
        // too far right (wide window: the drag area starts 470 in, 120 of it must stay on the area)
        var p = MainWindow.KeepDragAreaReachable(area, new Point(5000, 50), 1100);
        Check.Equal(1920 - 120 - 470.0, p.X);
        // too far left: 120 DIP of the window's right part stays
        p = MainWindow.KeepDragAreaReachable(area, new Point(-5000, 50), 1100);
        Check.Equal(120 - 1100.0, p.X);
        // above the area or below it: the header row stays on it
        Check.Equal(0.0, MainWindow.KeepDragAreaReachable(area, new Point(100, -300), 1100).Y);
        Check.Equal(1040 - 44.0, MainWindow.KeepDragAreaReachable(area, new Point(100, 5000), 1100).Y);
        // narrow window (the whole top row drags): from 0
        p = MainWindow.KeepDragAreaReachable(area, new Point(5000, 50), 500);
        Check.Equal(1920 - 120.0, p.X);
    }
}