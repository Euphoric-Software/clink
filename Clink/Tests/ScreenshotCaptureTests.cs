using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Clink.Native;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
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

    private static Rig Make(TempDir dir, IScreenshotEditor? editor = null)
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
        var service = new ScreenshotService(s, toasts, picker, () => null, () => null, new IScreenshotSource[] { source }, () => new[] { monitor }, editor);
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

    // ------------------------------------------------------------------ the editor (mode "after", edit last, edit a file)

    /// <summary>A made-up editor window: remembers the request, and runs a script that plays the user (Save, Done, or nothing = Discard).</summary>
    private sealed class FakeEditor : IScreenshotEditor
    {
        public EditorRequest? Request;
        public int Opens, Activations;
        public bool Open;
        public Func<EditorRequest, Task>? Script;
        public bool IsOpen => Open;
        public void Activate() => Activations++;

        public bool Refuse;   // as the real window host does when one is open already
        public Exception? Throw;   // the window could not be shown
        public async Task<bool> EditAsync(EditorRequest request)
        {
            if (Throw != null) throw Throw;
            if (Refuse) { Activations++; return false; }
            Opens++;
            Request = request;
            Open = true;
            try { if (Script != null) await Script(request); }
            finally { Open = false; }
            return true;
        }
    }

    private static readonly RegionPickResult Area = new(new RectI(5, 6, 10, 8), 0, PickOutcome.Drag);

    /// <summary>Runs one capture of <see cref="Area"/> to the end (the picker answers at once).</summary>
    private static void Capture(Rig rig)
    {
        var t = rig.Service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
        Pump(() => rig.Picker.Pending != null && !rig.Picker.Pending.Task.IsCompleted || t.IsCompleted, "the overlay");
        rig.Picker.Pending?.TrySetResult(Area);
        Pump(() => t.IsCompleted, "the end of the capture");
        Check.True(t.IsCompletedSuccessfully);
    }

    private static string Name(ScreenshotEntry? e) => e == null ? "" : Path.GetFileName(e.Path);

    [UnitTest]
    public static void Mode_after_opens_the_editor_on_the_crop_and_saves_nothing_until_Done()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);
        rig.Settings.ScreenshotMode = "after";
        int pngsWhileOpen = -1, toastsWhileOpen = -1;
        editor.Script = r =>
        {
            pngsWhileOpen = rig.Pngs.Length;
            toastsWhileOpen = rig.Toasts.Shown.Count;
            return Task.CompletedTask;   // the user closes it: Discard
        };
        Capture(rig);
        Check.Equal(1, editor.Opens);
        Check.Equal(0, pngsWhileOpen, "nothing is written before Done");
        Check.Equal(0, toastsWhileOpen);
        var r0 = editor.Request!;
        Check.True(r0.IsNewCapture, "a capture that is not saved yet");
        Check.Equal((10, 8), (r0.Document.Base.Width, r0.Document.Base.Height), "the crop, composed");
        Check.Equal(r0.Document.Base.Bounds, r0.Document.Crop, "the whole picture is the crop");
        Check.Equal(((byte)20, (byte)24, (byte)128, (byte)255), r0.Document.Base.GetPixel(0, 0), "the pixels of the area (5,6) of the screen");
        Check.Equal(1, rig.Picker.Closes, "the overlay is closed first");
        Check.Equal(0, rig.Pngs.Length, "Discard saves nothing");
        Check.Equal(0, rig.Toasts.Shown.Count);
        Check.False(rig.Service.IsBusy);
    }

    [UnitTest]
    public static void A_new_capture_whose_editor_fails_or_refuses_is_saved_as_it_is()
    {
        foreach (bool refuse in new[] { false, true })
        {
            using var dir = new TempDir();
            var editor = new FakeEditor();
            if (refuse) editor.Refuse = true; else editor.Throw = new InvalidOperationException("no window");
            var rig = Make(dir, editor);
            rig.Settings.ScreenshotMode = "after";
            Capture(rig);
            string what = refuse ? "refused" : "threw";
            Check.Equal(1, rig.Pngs.Length, what + ": the capture is saved");
            Check.Equal((10, 8), PngFile.ReadSize(rig.Pngs[0])!.Value, what);
            Check.Equal(1, new ScreenshotHistory(ScreenshotHistory.DefaultFile, null).Entries.Count, what + ": and in the history");
            Check.True(rig.Toasts.Shown.Any(s => s.StartsWith("screenshot: ")), what + ": with its toast");
            Check.False(rig.Service.IsBusy);
        }
    }

    [UnitTest]
    public static void Done_saves_the_edited_picture_once_with_history_and_toast_and_Save_keeps_the_editor_open()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);
        rig.Settings.ScreenshotMode = "after";
        bool saved1 = false, saved2 = false;
        int pngsAfterSave = -1, toastsAfterSave = -1;
        editor.Script = async r =>
        {
            var edited = r.Document.Add(new RectOutline(0, new System.Windows.Rect(1, 1, 6, 5), System.Windows.Media.Colors.Red, 2), out _);
            var image = Exporter.Export(edited);
            saved1 = await r.SaveAsync(image, edited, false);            // Ctrl+S
            pngsAfterSave = rig.Pngs.Length;
            toastsAfterSave = rig.Toasts.Shown.Count;
            saved2 = await r.SaveAsync(image, edited, true);             // Done, nothing changed since
        };
        Capture(rig);
        Check.True(saved1 && saved2);
        Check.Equal(1, pngsAfterSave, "Save writes the file");
        Check.Equal(0, toastsAfterSave, "but the toast belongs to Done");
        Check.Equal(1, rig.Pngs.Length, "Done after Save writes nothing again");
        Check.Equal(1, rig.Toasts.Shown.Count);
        Check.Equal("screenshot: " + Path.GetFileName(rig.Pngs[0]), rig.Toasts.Shown[0]);
        Check.Equal((10, 8), PngFile.ReadSize(rig.Pngs[0])!.Value);
        var png = PngFile.Decode(rig.Pngs[0])!;
        Check.Equal(((byte)0, (byte)0, (byte)255, (byte)255), png.GetPixel(1, 1), "the edit is in the file (the rectangle's top-left corner)");
        Check.Equal(((byte)56, (byte)52, (byte)128, (byte)255), png.GetPixel(9, 7), "and the rest is the capture (screen pixel 14,13)");
        var service = rig.Service;
        Check.NotNull(service.LastEdit);
        Check.Equal(rig.Pngs[0], service.LastEdit!.Path);
    }

    [UnitTest]
    public static void Save_then_edit_then_Save_is_still_one_png_with_the_new_pixels_and_one_history_entry()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);
        rig.Settings.ScreenshotMode = "after";
        DateTime firstTime = default;
        editor.Script = async r =>
        {
            var one = r.Document.Add(new RectOutline(0, new System.Windows.Rect(1, 1, 6, 5), System.Windows.Media.Colors.Red, 2), out _);
            await r.SaveAsync(Exporter.Export(one), one, false);
            firstTime = new ScreenshotHistory(ScreenshotHistory.DefaultFile, null).Entries.Single().Created;
            await Task.Delay(30);
            var two = one.Add(new RectOutline(0, new System.Windows.Rect(3, 3, 4, 3), System.Windows.Media.Colors.Lime, 2), out _);
            await r.SaveAsync(Exporter.Export(two), two, false);
        };
        Capture(rig);
        Check.Equal(1, rig.Pngs.Length, "the second Save replaces the file");
        var entries = new ScreenshotHistory(ScreenshotHistory.DefaultFile, null).Entries;
        Check.Equal(1, entries.Count, "one history entry");
        Check.Equal(rig.Pngs[0], entries[0].Path);
        Check.True(entries[0].Created >= firstTime, "with the time of the last save");
        Check.Equal(0, rig.Toasts.Shown.Count, "Save never toasts");
        var png = PngFile.Decode(rig.Pngs[0])!;
        Check.Equal(((byte)0, (byte)0, (byte)255, (byte)255), png.GetPixel(1, 1), "the first edit is in the file");
        Check.Equal(((byte)0, (byte)255, (byte)0, (byte)255), png.GetPixel(3, 3), "and so is the second");
    }

    [UnitTest]
    public static void Discard_after_a_Save_keeps_the_file_and_shows_no_toast()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);
        rig.Settings.ScreenshotMode = "after";
        editor.Script = async r =>
        {
            var d = r.Document.Add(new RectOutline(0, new System.Windows.Rect(1, 1, 6, 5), System.Windows.Media.Colors.Red, 2), out _);
            await r.SaveAsync(Exporter.Export(d), d, false);
            // the user then closes without Done: nothing more happens
        };
        Capture(rig);
        Check.Equal(1, rig.Pngs.Length, "the saved file stays");
        Check.Equal(0, rig.Toasts.Shown.Count, "no toast");
        Check.Equal(1, new ScreenshotHistory(ScreenshotHistory.DefaultFile, null).Entries.Count);
    }

    [UnitTest]
    public static void A_tampered_history_entry_outside_every_recorded_folder_is_refused_and_a_network_path_is_never_touched()
    {
        using var dir = new TempDir();
        var rig = Make(dir, new FakeEditor());
        Directory.CreateDirectory(rig.Folder);
        string good = Path.Combine(rig.Folder, "Screenshot 2026.png");
        File.WriteAllBytes(good, new byte[] { 1 });
        string otherDir = dir.File("other");
        Directory.CreateDirectory(otherDir);
        string outside = Path.Combine(otherDir, "planted.png");
        File.WriteAllBytes(outside, new byte[] { 1 });
        string unc = @"\\clink-test-server.invalid\share\shots\a.png";
        string json = "{ \"entries\": [ { \"path\": " + System.Text.Json.JsonSerializer.Serialize(outside) + ", \"created\": \"2026-01-01T00:00:00\" },"
            + " { \"path\": " + System.Text.Json.JsonSerializer.Serialize(unc) + ", \"created\": \"2026-01-01T00:00:00\" } ],"
            + " \"roots\": [ " + System.Text.Json.JsonSerializer.Serialize(@"\\clink-test-server.invalid\share\shots") + " ] }";
        File.WriteAllText(ScreenshotHistory.DefaultFile, json);

        var sw = Stopwatch.StartNew();
        var listed = new ScreenshotHistory(ScreenshotHistory.DefaultFile, rig.Folder).Entries.Select(e => e.Path).ToList();
        Check.True(listed.Contains(unc), "the network entry is kept: it was not probed (a probe would have found nothing and dropped it)");
        Check.True(sw.ElapsedMilliseconds < 3000, $"and no server was waited for ({sw.ElapsedMilliseconds} ms)");
        Check.True(rig.Service.IsOpenableEntry(good), "the screenshots folder");
        Check.False(rig.Service.IsOpenableEntry(outside), "an entry's own folder is not a recorded folder");
        Check.False(rig.Service.KnownFolders().Contains(otherDir, StringComparer.OrdinalIgnoreCase));
        Check.False(rig.Service.IsOpenableEntry(unc), "a network path while the folder is local, although a root names it");
        Check.False(rig.Service.KnownFolders().Any(f => f.StartsWith(@"\\")), "not even as a known folder");
    }

    [UnitTest]
    public static void The_insert_picker_lists_only_openable_history_entries_and_never_the_file_being_edited()
    {
        using var dir = new TempDir();
        var rig = Make(dir, new FakeEditor());
        Directory.CreateDirectory(rig.Folder);
        string a = Path.Combine(rig.Folder, "a.png"), b = Path.Combine(rig.Folder, "b.png"), c = Path.Combine(rig.Folder, "c.png");
        foreach (var f in new[] { a, b, c }) File.WriteAllBytes(f, new byte[] { 1 });
        string otherDir = dir.File("other");
        Directory.CreateDirectory(otherDir);
        string outside = Path.Combine(otherDir, "planted.png");
        File.WriteAllBytes(outside, new byte[] { 1 });
        string unc = @"\\clink-test-server.invalid\share\shots\a.png";
        string Entry(string p) => "{ \"path\": " + System.Text.Json.JsonSerializer.Serialize(p) + ", \"created\": \"2026-01-01T00:00:00\" }";
        File.WriteAllText(ScreenshotHistory.DefaultFile, "{ \"entries\": [ " + string.Join(", ", new[] { a, outside, unc, b, c }.Select(Entry)) + " ],"
            + " \"roots\": [ " + System.Text.Json.JsonSerializer.Serialize(@"\\clink-test-server.invalid\share\shots") + " ] }");

        var sw = Stopwatch.StartNew();
        var (entries, exclude) = rig.Service.RecentForPicker(b.ToUpperInvariant(), null, "");
        Check.Equal(new[] { a, c }, entries.Select(e => e.Path).ToArray(), "inside the screenshots folders only (no planted file, no network path), without the file being edited");
        Check.Equal(1, exclude.Count, "the file being edited is passed on to the picker");
        Check.True(sw.ElapsedMilliseconds < 3000, $"no server was waited for ({sw.ElapsedMilliseconds} ms)");
    }

    [UnitTest]
    public static void A_refused_editor_open_says_so_instead_of_opening_a_second_one()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor { Refuse = true };
        var rig = Make(dir, editor);
        Directory.CreateDirectory(rig.Folder);
        string file = Path.Combine(rig.Folder, "Screenshot 2026.png");
        File.WriteAllBytes(file, PngFile.Encode(new BgraImage(6, 4).ForceOpaque().ToBitmapSource()));
        var t = rig.Service.EditFileAsync(file, explicitPath: false);
        Pump(() => t.IsCompleted, "refused");
        Check.Equal(0, editor.Opens);
        Check.Equal(1, editor.Activations, "the open one is brought forward");
        Check.Equal("notice: Editor already open", rig.Toasts.Shown[^1]);
    }

    [UnitTest]
    public static void A_capture_made_while_an_editor_is_open_is_saved_as_it_is()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor { Open = true };
        var rig = Make(dir, editor);
        rig.Settings.ScreenshotMode = "after";
        Capture(rig);
        Check.Equal(0, editor.Opens, "one editor at a time");
        Check.Equal(1, rig.Pngs.Length, "the capture is not lost");
        Check.Equal(1, rig.Toasts.Shown.Count);

        // and without an editor at all (none given) mode "after" is a plain save
        using var dir2 = new TempDir();
        var plain = Make(dir2);
        plain.Settings.ScreenshotMode = "after";
        Capture(plain);
        Check.Equal(1, plain.Pngs.Length);
    }

    [UnitTest]
    public static void Edit_last_reopens_the_live_document_and_saves_a_new_file_next_to_the_original()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);
        rig.Settings.ScreenshotMode = "after";
        DocumentState? firstDoc = null;
        editor.Script = async r =>
        {
            firstDoc = r.Document.Add(new RectOutline(0, new System.Windows.Rect(1, 1, 6, 5), System.Windows.Media.Colors.Red, 2), out _);
            await r.SaveAsync(Exporter.Export(firstDoc), firstDoc, true);
        };
        Capture(rig);
        string original = rig.Pngs.Single();
        byte[] originalBytes = File.ReadAllBytes(original);

        DocumentState? reopened = null;
        bool wasNew = true;
        editor.Script = async r =>
        {
            reopened = r.Document;
            wasNew = r.IsNewCapture;
            var more = r.Document.Add(new LineShape(0, new System.Windows.Point(0, 0), new System.Windows.Point(9, 7), System.Windows.Media.Colors.Blue, 1, false), out _);
            await r.SaveAsync(Exporter.Export(more), more, true);
        };
        var t = rig.Service.EditLastAsync();
        Pump(() => t.IsCompleted, "edit last");
        Check.True(t.IsCompletedSuccessfully);
        Check.Equal(2, editor.Opens);
        Check.True(ReferenceEquals(firstDoc, reopened), "the live document: its rectangle is still an object");
        Check.False(wasNew, "an edit of a saved picture");
        Check.Equal(2, rig.Pngs.Length, "saved as a new file");
        Check.True(File.Exists(original));
        Check.Equal(originalBytes, File.ReadAllBytes(original), "the original is untouched");
        var history = new ScreenshotHistory(ScreenshotHistory.DefaultFile, null);
        Check.Equal(2, history.Entries.Count);
        Check.Equal(original, history.Entries[0].EditedFrom, "the newest entry says what it was edited from");
        Check.Null(history.Entries[1].EditedFrom);
        Check.Equal(2, rig.Toasts.Shown.Count);
    }

    [UnitTest]
    public static void Edit_last_after_a_restart_opens_the_flattened_png()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);
        rig.Settings.ScreenshotMode = "after";
        editor.Script = async r =>
        {
            var doc = r.Document.Add(new RectOutline(0, new System.Windows.Rect(1, 1, 6, 5), System.Windows.Media.Colors.Red, 2), out _);
            await r.SaveAsync(Exporter.Export(doc), doc, true);
        };
        Capture(rig);
        string file = rig.Pngs.Single();

        rig.Service.LastEdit = null;   // what a restart leaves
        EditorRequest? seen = null;
        editor.Script = r => { seen = r; return Task.CompletedTask; };
        var t = rig.Service.EditLastAsync();
        Pump(() => t.IsCompleted, "edit last");
        Check.NotNull(seen);
        Check.Equal(0, seen!.Document.Vector.Count, "flattened: the rectangle is part of the pixels now");
        Check.Equal((10, 8), (seen.Document.Base.Width, seen.Document.Base.Height));
        Check.Equal(((byte)0, (byte)0, (byte)255, (byte)255), seen.Document.Base.GetPixel(1, 1), "and the pixels are the saved ones");
        Check.Equal(Path.GetFileName(file), seen.Title);
        Check.False(seen.IsNewCapture);

        // a file that changed since the editor saved it is not the live document either
        editor.Script = async r =>
        {
            var doc = r.Document.Add(new RectOutline(0, new System.Windows.Rect(2, 2, 3, 3), System.Windows.Media.Colors.Red, 2), out _);
            await r.SaveAsync(Exporter.Export(doc), doc, true);
        };
        rig.Service.LastEdit = null;
        t = rig.Service.EditLastAsync();
        Pump(() => t.IsCompleted, "second edit");
        Check.NotNull(rig.Service.LastEdit);
        File.SetLastWriteTimeUtc(rig.Service.LastEdit!.Path, DateTime.UtcNow.AddHours(1));
        seen = null;
        editor.Script = r => { seen = r; return Task.CompletedTask; };
        t = rig.Service.EditLastAsync();
        Pump(() => t.IsCompleted, "third edit");
        Check.Equal(0, seen!.Document.Vector.Count, "the file was touched: the PNG is what counts");
    }

    [UnitTest]
    public static void Edit_last_with_the_editor_open_brings_it_forward_and_with_nothing_to_edit_says_so()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);

        var none = rig.Service.EditLastAsync();
        Pump(() => none.IsCompleted, "no screenshot");
        Check.Equal(0, editor.Opens);
        Check.Equal(new[] { "notice: No screenshot to edit" }, rig.Toasts.Shown.ToArray());

        editor.Open = true;
        var open = rig.Service.EditLastAsync();
        Check.True(open.IsCompletedSuccessfully, "at once");
        Check.Equal(1, editor.Activations);
        Check.Equal(0, editor.Opens, "no second editor");
        editor.Open = false;

        rig.Settings.ScreenshotsEnabled = false;
        var off = rig.Service.EditLastAsync();
        Check.True(off.IsCompletedSuccessfully);
        Check.Equal(0, editor.Opens);
        Check.Equal("notice: Screenshots are off", rig.Toasts.Shown[^1]);

        // no editor at all
        var bare = Make(dir);
        var t = bare.Service.EditLastAsync();
        Check.True(t.IsCompletedSuccessfully);
        Check.Equal("notice: Editor not available", bare.Toasts.Shown[^1]);
    }

    [UnitTest]
    public static void A_png_given_on_the_command_line_opens_from_anywhere_and_a_history_entry_only_from_a_screenshots_folder()
    {
        using var dir = new TempDir();
        var editor = new FakeEditor();
        var rig = Make(dir, editor);
        string source = dir.File("elsewhere.png");
        File.WriteAllBytes(source, PngFile.Encode(new BgraImage(6, 4).ForceOpaque().ToBitmapSource()));

        EditorRequest? seen = null;
        editor.Script = r => { seen = r; return Task.CompletedTask; };
        var t = rig.Service.EditFileAsync(source, explicitPath: true);
        Pump(() => t.IsCompleted, "explicit file");
        Check.NotNull(seen);
        Check.Equal((6, 4), (seen!.Document.Base.Width, seen.Document.Base.Height));
        Check.Equal("elsewhere.png", seen.Title);

        seen = null;
        t = rig.Service.EditFileAsync(source, explicitPath: false);   // not from the toast / history: outside the screenshots folders
        Pump(() => t.IsCompleted, "history file outside the folders");
        Check.Null(seen);
        Check.Equal("problem: Screenshot not opened", rig.Toasts.Shown[^1]);

        // refused: not a png, relative, a png that is no picture, missing
        string text = dir.File("notes.txt");
        File.WriteAllText(text, "hello");
        string fake = dir.File("fake.png");
        File.WriteAllText(fake, "this is not a png");
        foreach (var bad in new[] { text, "shot.png", fake, dir.File("missing.png") })
        {
            int before = rig.Toasts.Shown.Count;
            t = rig.Service.EditFileAsync(bad, explicitPath: true);
            Pump(() => t.IsCompleted, "refused " + bad);
            Check.Null(seen);
            Check.Equal(before + 1, rig.Toasts.Shown.Count, $"'{bad}' says why");
            Check.Equal("problem: Screenshot not opened", rig.Toasts.Shown[^1]);
        }
        Check.Equal(1, editor.Opens);

        // a screenshot in the screenshots folder is fine from the toast
        Directory.CreateDirectory(rig.Folder);
        string inFolder = Path.Combine(rig.Folder, "Screenshot 2026.png");
        File.Copy(source, inFolder);
        t = rig.Service.EditFileAsync(inFolder, explicitPath: false);
        Pump(() => t.IsCompleted, "folder file");
        Check.NotNull(seen);
        Check.Equal("Screenshot 2026.png", seen!.Title);
    }

    [UnitTest]
    public static void The_command_line_asks_for_the_last_screenshot_or_a_given_png()
    {
        Check.Equal("screenshot-edit-last", AppController.CommandFromArgs(new[] { "--screenshot-edit-last" }));
        Check.Equal("screenshot-edit-last", AppController.CommandFromArgs(new[] { "--dev", "x", "--SCREENSHOT-EDIT-LAST" }));
        Check.Equal("screenshot", AppController.CommandFromArgs(new[] { "--screenshot" }), "the plain one is as before");
        Check.Null(AppController.CommandFromArgs(new[] { "--screenshot-edit-lastt" }), "only the exact switch");

        string full = Path.GetFullPath(@"C:\pics\my shot.png");
        Check.Equal("screenshot-edit " + full, AppController.CommandFromArgs(new[] { "--screenshot-edit", full }));
        Check.Equal("screenshot-edit " + Path.GetFullPath("rel.png"), AppController.CommandFromArgs(new[] { "--SCREENSHOT-EDIT", "rel.png" }), "made absolute where it was typed");
        Check.Equal("screenshot-edit " + full, AppController.CommandFromArgs(new[] { "--dev", "D:\\d", "--screenshot-edit", full, "--minimized" }));
        Check.Null(AppController.CommandFromArgs(new[] { "--screenshot-edit" }), "no path");
        Check.Null(AppController.CommandFromArgs(new[] { "--screenshot-edit", "--minimized" }), "a switch is not a path");

        Check.Equal(@"C:\Pics\A b.PNG", AppController.EditPathOf(@"screenshot-edit C:\Pics\A b.PNG"), "the pipe keeps the case and the spaces");
        Check.Equal(@"C:\p.png", AppController.EditPathOf("SCREENSHOT-EDIT \"C:\\p.png\""));
        Check.Null(AppController.EditPathOf("screenshot-edit"));
        Check.Null(AppController.EditPathOf("screenshot-edit-last"));
        Check.Null(AppController.EditPathOf("screenshot"));
        Check.Null(AppController.EditPathOf("screenshot-edit   "));
        Check.Equal(0, AppController.RestartArguments(new[] { "--screenshot-edit", @"C:\a.png", "--screenshot-edit-last", "--minimized" }).Count(a => a != "--show"), "a command is for one start only");
        Check.Equal(AppController.HotkeyCommand.ScreenshotEditLast, AppController.CommandFor("screenshot-edit-last"));
        Check.Equal("Shift+PrintScreen", AppController.ScreenshotEditHotkeyLabelFor(new[] { new HotkeyAction { Keys = new HotkeyBinding { Shift = true, Vk = 0x2C }, Action = "screenshot-edit-last" } }));
        Check.Null(AppController.ScreenshotEditHotkeyLabelFor(new[] { new HotkeyAction { Keys = new HotkeyBinding { Vk = 0 }, Action = "screenshot-edit-last" } }));
    }
}
