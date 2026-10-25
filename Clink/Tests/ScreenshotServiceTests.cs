using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Clink.Screenshots;
using Clink.Screenshots.Imaging;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The screenshot subsystem apart from the screen: file names, the history file, the PNG on disk, the clipboard retries, the hotkey/CLI commands.</summary>
internal static class ScreenshotServiceTests
{
    private static BgraImage Picture(int w, int h)
    {
        var img = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, (byte)(x * 3), (byte)(y * 5), (byte)(x + y));
        return img;
    }

    private static readonly DateTime When = new(2026, 10, 24, 21, 4, 11);

    // ------------------------------------------------------------------ naming

    [UnitTest]
    public static void A_screenshot_is_named_after_the_game_or_Screenshot_and_the_time()
    {
        Check.Equal("Screenshot 2026-10-24 21-04-11", ScreenshotNaming.BaseName(null, When));
        Check.Equal("Screenshot 2026-10-24 21-04-11", ScreenshotNaming.BaseName("  ", When));
        Check.Equal("Overwatch 2026-10-24 21-04-11", ScreenshotNaming.BaseName("Overwatch", When));
        Check.Equal("Screenshot 2026-01-02 03-04-05", ScreenshotNaming.BaseName(null, new DateTime(2026, 1, 2, 3, 4, 5)), "zero padded, 24 hour clock");
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");   // another calendar and digits
            Check.Equal("Screenshot 2026-10-24 21-04-11", ScreenshotNaming.BaseName(null, When), "not the user's culture");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [UnitTest]
    public static void A_game_name_is_made_a_safe_single_file_name()
    {
        Check.Equal("Screenshot", ScreenshotNaming.Sanitize(null));
        Check.Equal("Screenshot", ScreenshotNaming.Sanitize("\t "));
        Check.Equal("a_b_c_d_e_f_g_h_i", ScreenshotNaming.Sanitize("a<b>c:d\"e/f\\g|h?i"), "characters Windows forbids become underscores");
        Check.Equal("Half-Life_ 2", ScreenshotNaming.Sanitize("Half-Life: 2"));
        Check.False(ScreenshotNaming.Sanitize("../../windows").Contains('/'), "separators never survive");
        Check.False(ScreenshotNaming.Sanitize("..\\..\\windows").Contains('\\'));
        Check.False(ScreenshotNaming.Sanitize("..\\..\\windows").StartsWith('.'), "no leading dots");
        Check.Equal("Game", ScreenshotNaming.Sanitize("  ..Game.. "), "trimmed, no leading or trailing dots");
        Check.Equal("Screenshot", ScreenshotNaming.Sanitize("...."));
        Check.Equal("a_b", ScreenshotNaming.Sanitize("a\u0001b"), "control characters");
        Check.Equal(ScreenshotNaming.MaxGameLength, ScreenshotNaming.Sanitize(new string('x', 200)).Length, "cut to 60 characters");
        Check.Equal("_CON", ScreenshotNaming.Sanitize("CON"));
        Check.Equal("_nul", ScreenshotNaming.Sanitize("nul"));
        Check.Equal("_COM1.txt", ScreenshotNaming.Sanitize("COM1.txt"), "also with an extension");
        Check.Equal("_LPT9", ScreenshotNaming.Sanitize("LPT9"));
        Check.Equal("CONSOLE", ScreenshotNaming.Sanitize("CONSOLE"), "only the whole device name");
        Check.Equal("Elden Ring", ScreenshotNaming.Sanitize("Elden Ring"));
    }

    [UnitTest]
    public static void A_taken_name_gets_the_next_free_number_before_the_extension()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Next() { string p = ScreenshotNaming.UniquePath(@"D:\Shots", "Screenshot 2026-10-24 21-04-11", taken.Contains); taken.Add(p); return p; }
        Check.Equal(@"D:\Shots\Screenshot 2026-10-24 21-04-11.png", Next());
        Check.Equal(@"D:\Shots\Screenshot 2026-10-24 21-04-11 (1).png", Next());
        Check.Equal(@"D:\Shots\Screenshot 2026-10-24 21-04-11 (2).png", Next());
        taken.Remove(@"D:\Shots\Screenshot 2026-10-24 21-04-11 (1).png");
        Check.Equal(@"D:\Shots\Screenshot 2026-10-24 21-04-11 (1).png", Next(), "the first free one, not the next after the last");

        using var dir = new TempDir();
        File.WriteAllText(dir.File("S 2026.png"), "x");
        Check.Equal(dir.File("S 2026 (1).png"), ScreenshotNaming.UniquePath(dir.Path, "S 2026"), "against the file system by default");
    }

    // ------------------------------------------------------------------ the real screen

    [UnitTest]
    public static void The_GDI_source_copies_every_monitor_at_its_physical_size_fully_opaque()
    {
        var monitors = Clink.Native.Monitors.Enumerate();
        if (monitors.Count == 0) Check.Skip("no monitor in this session");
        var desktop = Clink.Screenshots.Sources.FrozenDesktop.CaptureAll(monitors, new Clink.Screenshots.Sources.IScreenshotSource[] { new Clink.Screenshots.Sources.GdiScreenshotSource() });
        Check.Equal(monitors.Count, desktop.Monitors.Count);
        foreach (var m in desktop.Monitors)
        {
            Check.Equal((m.Monitor.Width, m.Monitor.Height), (m.Image.Width, m.Image.Height), m.Monitor.ShortName + ": one image pixel per physical pixel");
            Check.Equal("gdi", m.SourceName);
            Check.False(m.Image.HasTransparency(), m.Monitor.ShortName + ": alpha forced to 255");
        }
        var primary = desktop.Monitors.FirstOrDefault(m => m.Monitor.IsPrimary) ?? desktop.Monitors[0];   // a headless runner may report none as primary
        var crop = desktop.Compose(new RectI(primary.Bounds.X + 10, primary.Bounds.Y + 20, 33, 17));
        Check.Equal((33, 17), (crop.Width, crop.Height));
        Check.Equal(primary.Image.GetPixel(10, 20), crop.GetPixel(0, 0), "a crop is cut from the frozen pixels");
    }

    // ------------------------------------------------------------------ PNG on disk

    [UnitTest]
    public static void A_saved_screenshot_is_a_complete_PNG_with_the_pixels_and_size_of_the_picture()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Path, "Screenshots");   // does not exist yet: created on demand
        var history = new ScreenshotHistory(dir.File("screenshots.json"), folder);
        var img = Picture(37, 21).ForceOpaque();
        var saved = ScreenshotService.SaveToDisk(img, folder, null, When, history);

        Check.Equal(Path.Combine(folder, "Screenshot 2026-10-24 21-04-11.png"), saved.Entry.Path);
        Check.True(File.Exists(saved.Entry.Path));
        Check.Equal(new[] { saved.Entry.Path }, Directory.GetFiles(folder), "no .partial left behind");
        Check.Equal(37, saved.Entry.Width);
        Check.Equal(21, saved.Entry.Height);
        Check.Equal(new FileInfo(saved.Entry.Path).Length, saved.Entry.Size);
        Check.Equal(File.ReadAllBytes(saved.Entry.Path), saved.Png, "the clipboard gets the very bytes of the file");
        Check.Equal((37, 21), PngFile.ReadSize(saved.Entry.Path)!.Value, "the size is in the header");

        using var fs = File.OpenRead(saved.Entry.Path);
        var decoded = BgraImage.From(BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
        Check.Equal(img.Pixels, decoded.Pixels, "pixel for pixel, alpha 255");
        Check.Equal(255, (int)decoded.GetPixel(0, 0).A);

        Check.Equal(new[] { saved.Entry.Path }, history.Entries.Select(e => e.Path).ToArray());
        Check.Throws<InvalidOperationException>(() => ScreenshotService.SaveToDisk(new BgraImage(0, 0), folder, null, When, history), "an empty picture is refused");
    }

    [UnitTest]
    public static void Two_screenshots_in_the_same_second_both_survive_and_the_game_names_the_file()
    {
        using var dir = new TempDir();
        var history = new ScreenshotHistory(dir.File("screenshots.json"), dir.Path);
        var a = ScreenshotService.SaveToDisk(Picture(8, 8), dir.Path, "Overwatch", When, history);
        var b = ScreenshotService.SaveToDisk(Picture(9, 9), dir.Path, "Overwatch", When, history);
        Check.Equal(Path.Combine(dir.Path, "Overwatch 2026-10-24 21-04-11.png"), a.Entry.Path);
        Check.Equal(Path.Combine(dir.Path, "Overwatch 2026-10-24 21-04-11 (1).png"), b.Entry.Path);
        Check.Equal("Overwatch", a.Entry.Game);
        Check.Equal(new[] { b.Entry.Path, a.Entry.Path }, history.Entries.Select(e => e.Path).ToArray(), "newest first");
        Check.Null(ScreenshotService.SaveToDisk(Picture(2, 2), dir.Path, "", When, history).Entry.Game);
    }

    [UnitTest]
    public static void A_failed_write_leaves_no_partial_file()
    {
        using var dir = new TempDir();
        string path = dir.File("a.png");
        Directory.CreateDirectory(path);   // a directory under the final name: the move cannot succeed
        Check.Throws<IOException>(() => PngFile.WriteAtomic(path, new byte[] { 1, 2, 3 }));
        Check.Equal(0, Directory.GetFiles(dir.Path, "*.partial").Length, "cleaned up");

        File.WriteAllText(dir.File("b.png"), "theirs");
        Check.Throws<IOException>(() => PngFile.WriteAtomic(dir.File("b.png"), new byte[] { 1, 2, 3 }), "a name taken by now is never overwritten");
        Check.Equal("theirs", File.ReadAllText(dir.File("b.png")));
        Check.Equal(0, Directory.GetFiles(dir.Path, "*.partial").Length);
    }

    [UnitTest]
    public static void Eight_screenshots_saved_at_once_in_the_same_second_all_survive_under_their_own_names()
    {
        using var dir = new TempDir();
        var history = new ScreenshotHistory(dir.File("screenshots.json"), dir.Path);
        var pictures = Enumerable.Range(0, 8).Select(i => Picture(6 + i, 5).ForceOpaque()).ToArray();
        using var start = new ManualResetEventSlim();
        var tasks = pictures.Select(p => Task.Run(() => { start.Wait(); return ScreenshotService.SaveToDisk(p, dir.Path, "Overwatch", When, history); })).ToArray();
        start.Set();
        Check.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), "saves finished");
        var paths = tasks.Select(t => t.Result.Entry.Path).ToArray();
        Check.Equal(8, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "eight different names");
        var expected = new[] { "Overwatch 2026-10-24 21-04-11.png" }.Concat(Enumerable.Range(1, 7).Select(n => $"Overwatch 2026-10-24 21-04-11 ({n}).png")).ToArray();
        Check.Equal(expected, paths.Select(Path.GetFileName).OrderBy(n => n!.Length).ThenBy(n => n, StringComparer.Ordinal).ToArray(), "the plain name and (1) to (7)");
        foreach (var t in tasks)
            Check.Equal((t.Result.Entry.Width, t.Result.Entry.Height), PngFile.ReadSize(t.Result.Entry.Path)!.Value, "each file holds its own picture");
        Check.Equal(0, Directory.GetFiles(dir.Path, "*.partial").Length, "no temporary file left");
        Check.Equal(8, history.Entries.Count);
    }

    [UnitTest]
    public static void The_screenshots_folder_defaults_to_a_subfolder_of_the_clips_folder()
    {
        var s = Settings.FromJson("""{ "ClipsFolder": "D:\\Clips" }""");
        Check.Equal(@"D:\Clips\Screenshots", ScreenshotService.FolderFor(s));
        s.ScreenshotsFolder = @"E:\Shots";
        Check.Equal(@"E:\Shots", ScreenshotService.FolderFor(s));
        s.ScreenshotsFolder = "  ";
        Check.Equal(@"D:\Clips\Screenshots", ScreenshotService.FolderFor(s), "blank = default");
    }

    [UnitTest]
    public static void The_toast_says_what_happened_to_the_clipboard()
    {
        var e = new ScreenshotEntry { Width = 1280, Height = 720 };
        Check.Equal("1280 x 720 · copied to clipboard", ScreenshotService.Subtitle(e, new ScreenshotToastState(true, false)));
        Check.Equal("1280 x 720 · clipboard busy, not copied", ScreenshotService.Subtitle(e, new ScreenshotToastState(false, true)));
        Check.Equal("1280 x 720", ScreenshotService.Subtitle(e, new ScreenshotToastState(false, false)), "copying is switched off");
    }

    // ------------------------------------------------------------------ history

    private static ScreenshotEntry Shot(TempDir dir, string name, int w = 10, int h = 10, bool create = true)
    {
        string path = dir.File(name);
        if (create) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return new ScreenshotEntry { Path = path, Created = When, Width = w, Height = h, Size = 3 };
    }

    [UnitTest]
    public static void The_history_is_newest_first_and_a_path_is_listed_once()
    {
        using var dir = new TempDir();
        var history = new ScreenshotHistory(dir.File("screenshots.json"), dir.Path);
        Check.Null(history.Newest);
        var a = Shot(dir, "a.png"); var b = Shot(dir, "b.png"); var c = Shot(dir, "c.png");
        history.Add(a); history.Add(b); history.Add(c);
        Check.Equal(new[] { c.Path, b.Path, a.Path }, history.Entries.Select(e => e.Path).ToArray());
        Check.Equal(c.Path, history.Newest!.Path);
        history.Add(new ScreenshotEntry { Path = a.Path.ToUpperInvariant(), Width = 99 });
        Check.Equal(3, history.Entries.Count, "the same file again replaces its entry");
        Check.Equal(99, history.Newest!.Width);
        Check.NotNull(history.Find(b.Path));
        Check.Null(history.Find(dir.File("zzz.png")));
    }

    [UnitTest]
    public static void The_history_keeps_500_entries_and_drops_the_oldest()
    {
        Check.Equal(500, ScreenshotHistory.Cap);
        using var dir = new TempDir();
        var history = new ScreenshotHistory(dir.File("screenshots.json"), null) { Limit = 5 };   // the rule with fewer files
        for (int i = 0; i < 8; i++) history.Add(Shot(dir, $"t{i}.png"));
        Check.Equal(5, history.Entries.Count);
        Check.Equal(dir.File("t7.png"), history.Newest!.Path);
        Check.Equal(dir.File("t3.png"), history.Entries[^1].Path, "t0..t2 are out");
        var reload = new ScreenshotHistory(dir.File("screenshots.json"), null) { Limit = 5 };
        Check.Equal(5, reload.Entries.Count, "and so does the file");
        Check.True(File.Exists(dir.File("t0.png")), "their pngs are not deleted");
    }

    [UnitTest]
    public static void Entries_whose_file_is_gone_are_dropped_when_the_history_is_loaded()
    {
        using var dir = new TempDir();
        var file = dir.File("screenshots.json");
        var history = new ScreenshotHistory(file, null);
        var keep = Shot(dir, "keep.png"); var gone = Shot(dir, "gone.png");
        history.Add(keep); history.Add(gone);
        File.Delete(gone.Path);
        var reloaded = new ScreenshotHistory(file, null);
        Check.Equal(new[] { keep.Path }, reloaded.Entries.Select(e => e.Path).ToArray());
        Check.DoesNotContain(File.ReadAllText(file), "gone.png", "and the file is rewritten without it");
    }

    [UnitTest]
    public static void The_history_round_trips_every_field_and_keeps_keys_it_does_not_know()
    {
        using var dir = new TempDir();
        var file = dir.File("screenshots.json");
        var shot = Shot(dir, "a.png", 1280, 720);
        shot.Game = "Overwatch"; shot.CopiumLink = "https://c/x"; shot.CopiumPageUrl = "https://c/p/x"; shot.CopiumId = "x"; shot.EditedFrom = dir.File("orig.png");
        new ScreenshotHistory(file, null).Add(shot);

        // a newer version added keys at both levels
        string text = File.ReadAllText(file).Replace("\"Entries\"", "\"Future\": 7, \"Entries\"").Replace("\"Width\": 1280", "\"Width\": 1280, \"Tags\": [\"a\"]");
        File.WriteAllText(file, text);
        var loaded = new ScreenshotHistory(file, null);
        var e = loaded.Entries.Single();
        Check.Equal((shot.Path, 1280, 720, 3L, "Overwatch"), (e.Path, e.Width, e.Height, e.Size, e.Game));
        Check.Equal((shot.CopiumLink, shot.CopiumPageUrl, shot.CopiumId, shot.EditedFrom), (e.CopiumLink, e.CopiumPageUrl, e.CopiumId, e.EditedFrom));
        Check.Equal(When, e.Created);
        loaded.Add(Shot(dir, "b.png"));
        string again = File.ReadAllText(file);
        Check.Contains(again, "\"Future\"");
        Check.Contains(again, "\"Tags\"");
    }

    [UnitTest]
    public static void A_missing_or_unreadable_history_is_rebuilt_from_the_pngs_in_the_folder()
    {
        using var dir = new TempDir();
        string folder = dir.File("shots");
        Directory.CreateDirectory(folder);
        var oldest = ScreenshotService.SaveToDisk(Picture(5, 4), folder, null, When.AddMinutes(-2), new ScreenshotHistory(dir.File("other.json"), null));
        File.SetLastWriteTimeUtc(oldest.Entry.Path, DateTime.UtcNow.AddMinutes(-2));
        var newest = ScreenshotService.SaveToDisk(Picture(7, 3), folder, "Game", When, new ScreenshotHistory(dir.File("other2.json"), null));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a screenshot");

        var fresh = new ScreenshotHistory(dir.File("screenshots.json"), folder);   // no file yet
        Check.Equal(new[] { newest.Entry.Path, oldest.Entry.Path }, fresh.Entries.Select(e => e.Path).ToArray(), "newest first by modified time, pngs only");
        Check.Equal((7, 3), (fresh.Newest!.Width, fresh.Newest.Height));
        Check.True(File.Exists(dir.File("screenshots.json")), "and the rebuilt history is saved");

        File.WriteAllText(dir.File("broken.json"), "{ this is not json");
        var fromBroken = new ScreenshotHistory(dir.File("broken.json"), folder);
        Check.Equal(2, fromBroken.Entries.Count, "an unreadable file is rebuilt from the folder too");
        Check.True(File.Exists(dir.File("broken.json.unreadable")), "after the broken one was kept");

        Check.Equal(0, new ScreenshotHistory(dir.File("nowhere.json"), dir.File("no such folder")).Entries.Count);
    }

    [UnitTest]
    public static void The_default_history_file_lives_in_the_settings_folder_so_a_test_or_dev_folder_isolates_it()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        Check.Equal(Path.Combine(dir.Path, "screenshots.json"), ScreenshotHistory.DefaultFile);
    }

    // ------------------------------------------------------------------ clipboard

    /// <summary>
    /// The upper bound for <see cref="ScreenshotClipboard.Copy"/> against a clipboard another thread holds open WITHOUT a window
    /// (the case the open-window pre-check cannot see): WPF's Clipboard.SetDataObject retries OleSetClipboard itself (10 x 100 ms),
    /// so about one second is inherent; Clink adds no wait of its own (it used to add 5 x 60 ms on top of each of those).
    /// </summary>
    internal const int BusyClipboardBoundMs = 2000;

    [UnitTest]
    public static void A_busy_clipboard_fails_the_copy_without_a_wait_of_our_own()
    {
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool held = false;
        string owner = "unknown";
        var holder = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 200 && !held; i++) { held = global::Windows.Win32.PInvoke.OpenClipboard(default); if (!held) Thread.Sleep(50); }
                if (!held)
                {
                    try
                    {
                        var w = global::Windows.Win32.PInvoke.GetOpenClipboardWindow();
                        if (!w.IsNull)
                        {
                            global::Windows.Win32.PInvoke.GetWindowThreadProcessId(w, out uint pid);
                            string name; try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
                            owner = $"{name} (pid {pid})";
                        }
                        else owner = "a program with no clipboard window";
                    }
                    catch { }
                }
            }
            finally
            {
                opened.Set();
                if (held) release.Wait(30_000);
                if (held) global::Windows.Win32.PInvoke.CloseClipboard();
            }
        }) { IsBackground = true };
        holder.Start();
        try
        {
            opened.Wait(15_000);
            if (!held) Check.Fail($"could not open the clipboard for 10 s; held by {owner}");
            var img = Picture(4, 3).ForceOpaque();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool copied = ScreenshotClipboard.Copy(img.ToBitmapSource(), PngFile.Encode(img.ToBitmapSource()));
            long ms = sw.ElapsedMilliseconds;
            Log.Info($"[unittest] a busy clipboard failed the copy in {ms} ms");
            Check.False(copied, "busy: not copied");
            Check.True(ms < BusyClipboardBoundMs, $"gave up after {ms} ms (bound {BusyClipboardBoundMs} ms)");
        }
        finally
        {
            release.Set();
            holder.Join(5_000);
        }
    }

    // ------------------------------------------------------------------ black pictures and the Open button

    private static Clink.Screenshots.Sources.FrozenMonitor FakeMonitor(RectI r, BgraImage img) =>
        new(new Clink.Native.MonitorInfo(0, "\\\\.\\DISPLAY" + r.X, "", r.X, r.Y, r.Width, r.Height, r.X == 0, false, 80, 0, ""), img, "test");

    [UnitTest]
    public static void A_black_crop_of_a_lit_screen_gets_the_normal_toast_and_a_black_monitor_or_game_the_warning()
    {
        var terminal = new BgraImage(100, 50).ForceOpaque();          // black, with a lit corner (a terminal window beside other windows)
        for (int y = 40; y < 50; y++) for (int x = 90; x < 100; x++) terminal.SetPixel(x, y, 220, 220, 220);
        var allBlack = new BgraImage(80, 50).ForceOpaque();           // an exclusive-fullscreen game's monitor
        var desk = new Clink.Screenshots.Sources.FrozenDesktop(new[] { FakeMonitor(new RectI(0, 0, 100, 50), terminal), FakeMonitor(new RectI(100, 0, 80, 50), allBlack) }, When);
        var darkCrop = new RectI(10, 10, 30, 20);
        Check.False(ScreenshotService.WarnBlack(desk, darkCrop, gameOnIt: false), "a black terminal: normal toast");
        Check.True(ScreenshotService.WarnBlack(desk, darkCrop, gameOnIt: true), "black where the game is: the warning");
        Check.True(ScreenshotService.WarnBlack(desk, new RectI(110, 10, 30, 20), gameOnIt: false), "a monitor black as a whole: the warning");
        Check.False(ScreenshotService.WarnBlack(desk, new RectI(90, 40, 20, 10), gameOnIt: true), "a lit pixel in the picture: never");
    }

    [UnitTest]
    public static void Only_a_png_inside_the_screenshots_folder_is_handed_to_the_shell()
    {
        Check.True(ScreenshotService.IsOpenable(@"D:\Shots\Screenshot 2026.png", @"D:\Shots"));
        Check.True(ScreenshotService.IsOpenable(@"D:\Shots\sub\a.PNG", @"D:\Shots\"), "a subfolder, any case");
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\evil.exe", @"D:\Shots"), "not a png");
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a.png.lnk", @"D:\Shots"));
        Check.False(ScreenshotService.IsOpenable(@"D:\Other\a.png", @"D:\Shots"), "another folder");
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots2\a.png", @"D:\Shots"), "a folder that only starts the same");
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\..\Windows\a.png", @"D:\Shots"), "no way out with ..");
        Check.False(ScreenshotService.IsOpenable(@"a.png", @"D:\Shots"), "relative");
        Check.False(ScreenshotService.IsOpenable(@"\\server\share\a.png", @"D:\Shots"));
        Check.False(ScreenshotService.IsOpenable(null, @"D:\Shots"));
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a.png", ""));
    }

    [UnitTest]
    public static void The_open_rule_refuses_streams_devices_and_links_and_knows_earlier_screenshot_folders()
    {
        var folders = new[] { @"D:\Shots", @"E:\Old shots\" };
        Check.True(ScreenshotService.IsOpenable(@"D:\Shots\a.png", folders));
        Check.True(ScreenshotService.IsOpenable(@"E:\Old shots\2025\b.PNG", folders), "a folder that was the screenshots folder once");
        Check.False(ScreenshotService.IsOpenable(@"F:\Elsewhere\c.png", folders));
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a.png:evil.exe", folders), "an alternate data stream after the name");
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a.png:Zone.Identifier", folders));
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a.png::$DATA", folders));
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a:b.png", folders), "a colon in a name");
        Check.False(ScreenshotService.IsOpenable(@"\\?\D:\Shots\a.png", folders), "a device path");
        Check.False(ScreenshotService.IsOpenable(@"\\.\D:\Shots\a.png", folders));
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a.png", Array.Empty<string>()), "no folder at all");
        Check.False(ScreenshotService.IsOpenable(@"D:\Shots\a.png", new[] { "", "relative" }), "unusable folders are skipped");
        Check.True(ScreenshotService.IsOpenable(@"C:\anywhere\mine.png", (IEnumerable<string>?)null), "a path the user typed: anywhere");
        Check.False(ScreenshotService.IsOpenable(@"C:\anywhere\mine.txt", (IEnumerable<string>?)null));
        Check.False(ScreenshotService.IsOpenable(@"C:\anywhere\mine.png:s", (IEnumerable<string>?)null));
    }

    [UnitTest]
    public static void The_file_rule_wants_an_existing_regular_png_not_a_directory_or_a_link()
    {
        using var dir = new TempDir();
        string good = dir.File("shot.png");
        File.WriteAllBytes(good, new byte[] { 1 });
        Directory.CreateDirectory(dir.File("folder.png"));
        Check.True(ScreenshotService.IsOpenableFile(good, new[] { dir.Path }));
        Check.True(ScreenshotService.IsExplicitEditable(good), "explicit: anywhere");
        Check.False(ScreenshotService.IsOpenableFile(dir.File("missing.png"), new[] { dir.Path }), "gone");
        Check.False(ScreenshotService.IsOpenableFile(dir.File("folder.png"), new[] { dir.Path }), "a directory called .png");
        Check.False(ScreenshotService.IsOpenableFile(good, new[] { dir.File("other") }), "outside the folders");
        Check.False(ScreenshotService.IsExplicitEditable(@"shot.png"), "relative");
        Check.False(ScreenshotService.IsExplicitEditable(null));
        string link = dir.File("link.png");
        try
        {
            File.CreateSymbolicLink(link, good);
            Check.False(ScreenshotService.IsOpenableFile(link, new[] { dir.Path }), "a symbolic link");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* no right to make links on this machine: the rest was checked */ }
    }

    [UnitTest]
    public static void History_remembers_every_folder_screenshots_were_saved_to()
    {
        using var dir = new TempDir();
        string oldFolder = dir.File("old"), newFolder = dir.File("new");
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newFolder);
        string file = dir.File("screenshots.json");
        var h = new ScreenshotHistory(file, null);
        h.NoteRoot(oldFolder);
        File.WriteAllBytes(Path.Combine(newFolder, "a.png"), new byte[] { 1 });
        h.Add(new ScreenshotEntry { Path = Path.Combine(newFolder, "a.png"), Created = DateTime.Now });
        var reread = new ScreenshotHistory(file, null);
        Check.True(reread.Roots.Contains(oldFolder, StringComparer.OrdinalIgnoreCase), "the earlier folder survives a restart");
        Check.True(reread.Roots.Contains(newFolder, StringComparer.OrdinalIgnoreCase));
        h.NoteRoot(oldFolder.ToUpperInvariant());
        Check.Equal(2, new ScreenshotHistory(file, null).Roots.Count, "a folder is recorded once, any case");
    }

    // ------------------------------------------------------------------ hotkeys and the command line

    [UnitTest]
    public static void Every_known_hotkey_action_has_its_command_and_an_unknown_one_is_never_a_clip()
    {
        foreach (var (action, command) in new (string?, AppController.HotkeyCommand)[]
        {
            (null, AppController.HotkeyCommand.Clip), ("", AppController.HotkeyCommand.Clip), ("  ", AppController.HotkeyCommand.Clip), ("clip", AppController.HotkeyCommand.Clip), ("CLIP", AppController.HotkeyCommand.Clip), (" clip ", AppController.HotkeyCommand.Clip),
            ("pause", AppController.HotkeyCommand.Pause), ("Pause", AppController.HotkeyCommand.Pause),
            ("share-last", AppController.HotkeyCommand.ShareLast), ("share", AppController.HotkeyCommand.ShareLast),
            ("record", AppController.HotkeyCommand.Record),
            ("screenshot", AppController.HotkeyCommand.Screenshot), ("Screenshot", AppController.HotkeyCommand.Screenshot),
            ("screenshot-edit-last", AppController.HotkeyCommand.ScreenshotEditLast),
        })
            Check.Equal(command, AppController.CommandFor(action), $"'{action}'");

        foreach (string typo in new[] { "clipp", "pauze", "screenshots", "screen-shot", "share_last", "reccord", "x", "0", "clip now" })
        {
            Check.Equal(AppController.HotkeyCommand.Unknown, AppController.CommandFor(typo), $"'{typo}' is unknown");
            Check.NotEqual(AppController.HotkeyCommand.Clip, AppController.CommandFor(typo), $"'{typo}' must never save a clip");
        }
    }

    [UnitTest]
    public static void The_command_line_asks_for_a_screenshot_with_dash_dash_screenshot()
    {
        Check.Equal("screenshot", AppController.CommandFromArgs(new[] { "--screenshot" }));
        Check.Equal("screenshot", AppController.CommandFromArgs(new[] { "--SCREENSHOT" }));
        Check.Equal("screenshot", AppController.CommandFromArgs(new[] { "--dev", "--screenshot" }));
        Check.Equal("clip", AppController.CommandFromArgs(new[] { "--clip" }), "the others are as before");
        Check.Equal("share", AppController.CommandFromArgs(new[] { "--share-last" }));
        Check.Equal("trim", AppController.CommandFromArgs(new[] { "--trim-last" }));
        Check.Equal("record", AppController.CommandFromArgs(new[] { "--record" }));
        Check.Null(AppController.CommandFromArgs(new[] { "--screenshots" }), "only the exact switch");
        Check.Null(AppController.CommandFromArgs(Array.Empty<string>()));
        Check.False(AppController.RestartArguments(new[] { "--screenshot", "--minimized" }).Contains("--screenshot"), "a command is for one start only: a restart does not take another screenshot");
    }

    [UnitTest]
    public static void With_the_screenshot_tools_off_their_hotkey_rows_stay_but_are_not_bound()
    {
        var s = Settings.FromJson("{}");
        s.Hotkeys.Add(new HotkeyAction { Keys = new HotkeyBinding { Vk = 0x2C }, Action = "screenshot" });
        s.Hotkeys.Add(new HotkeyAction { Keys = new HotkeyBinding { Vk = 0x2C, Shift = true }, Action = "screenshot-edit-last" });
        s.Hotkeys.Add(new HotkeyAction { Keys = new HotkeyBinding { Vk = 0x79 }, Action = "pause" });
        int rows = s.Hotkeys.Count;
        Check.Equal(new[] { "clip", "screenshot", "screenshot-edit-last", "pause" }, AppController.BindableHotkeys(s).Select(h => h.Action).ToArray(),
            "on: everything is bound, edit-last too (it opens the editor)");
        s.ScreenshotsEnabled = false;
        Check.Equal(new[] { "clip", "pause" }, AppController.BindableHotkeys(s).Select(h => h.Action).ToArray());
        Check.Equal(rows, s.Hotkeys.Count, "the rows are still in settings");
        // a row with an unknown action stays in the list but is not bound: it does nothing when pressed, so it must not swallow the key either
        s.Hotkeys.Add(new HotkeyAction { Keys = new HotkeyBinding { Vk = 0x7A }, Action = "typo" });
        Check.Equal(2, AppController.BindableHotkeys(s).Count);
        Check.False(AppController.BindableHotkeys(s).Any(h => h.Action == "typo"));
        s.ScreenshotsEnabled = true;
        Check.Equal(new[] { "clip", "screenshot", "screenshot-edit-last", "pause" }, AppController.BindableHotkeys(s).Select(h => h.Action).ToArray());
        Check.Equal(rows + 1, s.Hotkeys.Count, "the typo row is still in settings");
    }

    [UnitTest]
    public static void The_tray_label_follows_the_screenshot_binding()
    {
        var list = new List<HotkeyAction>
        {
            new() { Keys = new HotkeyBinding { Alt = true, Vk = 0x43 }, Action = "clip" },
            new() { Keys = new HotkeyBinding { Vk = 0 }, Action = "screenshot" },
        };
        Check.Null(AppController.ScreenshotHotkeyLabelFor(list), "an unset row names no key");
        list.Add(new HotkeyAction { Keys = new HotkeyBinding { Ctrl = true, Vk = 0x2C }, Action = "screenshot" });
        Check.Equal("Ctrl+PrintScreen", AppController.ScreenshotHotkeyLabelFor(list));
        list.Insert(0, new HotkeyAction { Keys = new HotkeyBinding { Vk = 0x2C }, Action = "Screenshot" });
        Check.Equal("PrintScreen", AppController.ScreenshotHotkeyLabelFor(list), "the first set row");
        Check.Null(AppController.ScreenshotHotkeyLabelFor(Array.Empty<HotkeyAction>()));
    }

    [UnitTest]
    public static void The_screenshot_modes_are_none_before_after_and_anything_else_is_none()
    {
        Check.Equal("none", ScreenshotModes.Normalize(null));
        Check.Equal("none", ScreenshotModes.Normalize(""));
        Check.Equal("none", ScreenshotModes.Normalize("bogus"));
        Check.Equal("after", ScreenshotModes.Normalize(" After "));
        Check.Equal("before", ScreenshotModes.Normalize("BEFORE"));
        Check.Equal("none", ScreenshotModes.Normalize("none"));
    }
}
