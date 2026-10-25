using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Clink.Native;
using Clink.Screenshots;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Screenshots.Sources;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// <see cref="ScreenshotService.CaptureRegionAsync"/> end to end with a made-up screen, picker and toasts: every way a capture ends
/// leaves the service ready for the next one, and a second trigger while the overlay is up cancels it. Runs on the test runner's UI
/// thread; the awaits are driven by pumping its dispatcher. (The real overlay windows are not opened here: no test puts topmost
/// full-screen windows over the desktop of whoever runs them.)
/// </summary>
internal static class ScreenshotCaptureTests
{
    private sealed class FakeToasts : IScreenshotToasts
    {
        public readonly List<string> Shown = new();
        public void ShowNotice(string title, string text, bool paused = false) => Shown.Add("notice: " + title);
        public void ShowProblem(string title, string text) => Shown.Add("problem: " + title);
        public void ShowScreenshot(ScreenshotEntry entry, ScreenshotToastState state) => Shown.Add("screenshot: " + Path.GetFileName(entry.Path));
    }

    private sealed class FakeSource : IScreenshotSource
    {
        private int _captures;
        public int Captures => Volatile.Read(ref _captures);
        public Exception? Throw;
        public string Name => "fake";
        public bool Handles(MonitorInfo monitor) => true;
        public FrozenMonitor Capture(MonitorInfo monitor)
        {
            Interlocked.Increment(ref _captures);
            if (Throw != null) throw Throw;
            var img = new BgraImage(monitor.Width, monitor.Height);
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    img.SetPixel(x, y, (byte)(x * 4), (byte)(y * 4), 128);
            return new FrozenMonitor(monitor, img, Name);
        }
    }

    private sealed class FakePicker : IRegionPicker
    {
        public int Picks, Cancels, Closes;
        public Exception? Throw;
        public TaskCompletionSource<RegionPickResult?>? Pending;
        public CancellationToken Token;

        public Task<RegionPickResult?> PickAsync(FrozenDesktop desktop, bool loupe, CancellationToken ct)
        {
            Picks++;
            Token = ct;
            if (Throw != null) throw Throw;
            if (ct.IsCancellationRequested) return Task.FromResult<RegionPickResult?>(null);
            var tcs = new TaskCompletionSource<RegionPickResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetResult(null));
            Pending = tcs;
            return tcs.Task;
        }

        public void Cancel() { Cancels++; Pending?.TrySetResult(null); }
        public void Close() => Closes++;
    }

    private sealed class Rig
    {
        public required ScreenshotService Service;
        public required Settings Settings;
        public required FakeToasts Toasts;
        public required FakePicker Picker;
        public required FakeSource Source;
        public required string Folder;
        public string[] Pngs => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.png") : Array.Empty<string>();
    }

    private static Rig Make(TempDir dir)
    {
        Settings.DirOverride = dir.Path;   // the history file
        var s = Settings.FromJson("{}");
        s.ClipsFolder = dir.File("clips");
        s.ScreenshotCopyImage = false;      // never the real clipboard
        s.ClickSound = false;
        var toasts = new FakeToasts();
        var picker = new FakePicker();
        var source = new FakeSource();
        var monitor = new MonitorInfo(0, @"\\.\DISPLAY1", "", 0, 0, 64, 48, true, false, 80, 0, "");
        var service = new ScreenshotService(s, toasts, picker, () => null, () => null, new IScreenshotSource[] { source }, () => new[] { monitor });
        return new Rig { Service = service, Settings = s, Toasts = toasts, Picker = picker, Source = source, Folder = ScreenshotService.FolderFor(s) };
    }

    /// <summary>Runs the UI thread's dispatcher until <paramref name="done"/> (the awaits of the service continue there).</summary>
    private static void Pump(Func<bool> done, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Check.Fail("timed out waiting for " + what);
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    [UnitTest]
    public static void A_cancelled_capture_leaves_the_service_ready_and_saves_nothing()
    {
        using var dir = new TempDir();
        var rig = Make(dir);
        var t = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => rig.Picker.Picks == 1 || t.IsCompleted, "the overlay");
        Check.True(rig.Service.IsBusy, "busy while the overlay is up");
        Check.Equal(1, rig.Source.Captures);
        rig.Picker.Pending!.SetResult(null);   // Esc
        Pump(() => t.IsCompleted, "the end of the capture");
        Check.True(t.IsCompletedSuccessfully);
        Check.False(rig.Service.IsBusy, "ready for the next one");
        Check.Equal(1, rig.Picker.Closes, "the overlay is closed by the service");
        Check.Equal(0, rig.Pngs.Length);
        Check.Equal(0, rig.Toasts.Shown.Count);
    }

    [UnitTest]
    public static void A_second_trigger_while_the_overlay_is_up_cancels_it_and_never_captures()
    {
        using var dir = new TempDir();
        var rig = Make(dir);
        var first = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => rig.Picker.Picks == 1 || first.IsCompleted, "the overlay");

        var second = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Cli);   // PrintScreen again, or --screenshot
        Check.True(second.IsCompletedSuccessfully, "the second trigger returns at once");
        Check.Equal(1, rig.Picker.Cancels, "it cancels the overlay");
        Check.True(rig.Picker.Token.IsCancellationRequested, "and the pick's token");
        Pump(() => first.IsCompleted, "the cancelled capture");
        Check.True(first.IsCompletedSuccessfully);
        Check.False(rig.Service.IsBusy);
        Check.Equal(1, rig.Source.Captures, "no second copy of the screen");
        Check.Equal(1, rig.Picker.Picks, "no second overlay");
        Check.Equal(0, rig.Pngs.Length, "nothing saved");

        // The next trigger is a capture again, and a commit saves the picture.
        var third = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => rig.Picker.Picks == 2 || third.IsCompleted, "the next overlay");
        rig.Picker.Pending!.SetResult(new RegionPickResult(new RectI(5, 6, 10, 8), 0, PickOutcome.Drag));
        Pump(() => third.IsCompleted, "the save");
        Check.True(third.IsCompletedSuccessfully);
        Check.Equal(2, rig.Picker.Closes, "closed after the commit too");
        Check.Equal(1, rig.Pngs.Length);
        Check.Equal((10, 8), PngFile.ReadSize(rig.Pngs[0])!.Value);
        Check.Equal(new[] { "screenshot: " + Path.GetFileName(rig.Pngs[0]) }, rig.Toasts.Shown.ToArray());
    }

    [UnitTest]
    public static void A_trigger_during_the_tray_delay_cancels_before_the_screen_is_copied()
    {
        using var dir = new TempDir();
        var rig = Make(dir);
        var sw = Stopwatch.StartNew();
        var tray = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Tray);
        Check.False(tray.IsCompleted);
        Check.Equal(0, rig.Source.Captures, "the tray menu fades out first");
        Check.True(rig.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey).IsCompletedSuccessfully, "the second trigger returns at once");
        Pump(() => tray.IsCompleted, "the cancelled tray capture");
        Check.Equal(0, rig.Source.Captures, "cancelled before the copy");
        Check.Equal(0, rig.Picker.Picks);
        Check.False(rig.Service.IsBusy);

        var again = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Tray);
        sw.Restart();
        Pump(() => rig.Picker.Picks == 1 || again.IsCompleted, "the tray capture");
        Check.True(sw.ElapsedMilliseconds >= ScreenshotService.TrayDelay.TotalMilliseconds - 30, $"waited {sw.ElapsedMilliseconds} ms for the menu");
        Check.Equal(1, rig.Source.Captures);
        rig.Picker.Cancel();
        Pump(() => again.IsCompleted, "the end");
        Check.False(rig.Service.IsBusy);
    }

    [UnitTest]
    public static void A_failing_overlay_or_screen_copy_never_leaves_the_service_busy()
    {
        using var dir = new TempDir();

        var overlay = Make(dir);
        overlay.Picker.Throw = new InvalidOperationException("the overlay could not be created");
        var t1 = overlay.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => t1.IsCompleted, "the failed overlay");
        Check.True(t1.IsFaulted, "the error goes up to CaptureRegion, which reports it");
        Check.False(overlay.Service.IsBusy);
        Check.Equal(1, overlay.Picker.Closes, "and whatever was opened is closed");

        var copy = Make(dir);
        copy.Source.Throw = new ScreenshotCaptureException("The screen could not be read");
        var t2 = copy.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => t2.IsCompleted, "the failed copy");
        Check.True(t2.IsCompletedSuccessfully);
        Check.Equal(new[] { "problem: Screenshot not taken" }, copy.Toasts.Shown.ToArray());
        Check.Equal(0, copy.Picker.Picks, "no overlay over nothing");
        Check.Equal(0, copy.Picker.Closes);
        Check.False(copy.Service.IsBusy);

        var bug = Make(dir);
        bug.Source.Throw = new InvalidOperationException("a bug");
        var t3 = bug.Service.CaptureRegionAsync(ScreenshotTrigger.Cli);
        Pump(() => t3.IsCompleted, "the crashed copy");
        Check.True(t3.IsFaulted);
        Check.False(bug.Service.IsBusy);

        // and after each of them the next trigger works
        bug.Source.Throw = null;
        var t4 = bug.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => bug.Picker.Picks == 1 || t4.IsCompleted, "the next overlay");
        Check.Equal(1, bug.Picker.Picks);
        bug.Picker.Cancel();
        Pump(() => t4.IsCompleted, "the end");
    }

    [UnitTest]
    public static void With_the_screenshot_tools_off_a_trigger_copies_nothing()
    {
        using var dir = new TempDir();
        var rig = Make(dir);
        rig.Settings.ScreenshotsEnabled = false;
        foreach (var trigger in new[] { ScreenshotTrigger.Hotkey, ScreenshotTrigger.Tray, ScreenshotTrigger.Cli })
        {
            var t = rig.Service.CaptureRegionAsync(trigger);
            Check.True(t.IsCompletedSuccessfully, trigger + ": at once");
        }
        Check.Equal(0, rig.Source.Captures);
        Check.Equal(0, rig.Picker.Picks);
        Check.Equal(3, rig.Toasts.Shown.Count(s => s == "notice: Screenshots are off"));
        Check.False(rig.Service.IsBusy);
    }
}
