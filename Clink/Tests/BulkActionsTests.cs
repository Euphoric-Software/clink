using System.IO;
using Clink.Screenshots;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>The selection bar's rules (BulkActions), the multi-file share seam and the bulk "Save copies…" (AppController.SaveCopiesAsync).</summary>
internal static class BulkActionsTests
{
    private static T Wait<T>(Task<T> task)
    {
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the task");   // the test thread has a dispatcher context: a blocking wait would deadlock on it
        return task.Result;
    }

    private static void Wait(Task task)
    {
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the task");
        task.GetAwaiter().GetResult();
    }

    private static string[] Actions(IReadOnlyList<BulkActionItem>? items) => items == null ? Array.Empty<string>() : items.Select(i => i.Action.ToString()).ToArray();

    // ------------------------------------------------------------------ the Share menu and button

    [UnitTest]
    public static void The_share_menu_exists_only_with_a_token_and_cloud_sharing_on()
    {
        Check.Null(BulkActions.ShareMenu(false, true, 3, 0, false), "no token");
        Check.Null(BulkActions.ShareMenu(true, false, 3, 0, false), "cloud off");
        Check.Null(BulkActions.ShareMenu(false, false, 3, 3, false), "neither");
        Check.Equal(new[] { "ShareFiles", "SaveCopies" }, Actions(BulkActions.ShareMenu(true, true, 3, 0, false)), "none linked");
        Check.Equal(new[] { "ShareFiles", "SaveCopies" }, Actions(BulkActions.ShareMenu(true, true, 3, 2, false)), "some linked: no Copy links");
        Check.Equal(new[] { "CopyLinks", "ShareFiles", "SaveCopies" }, Actions(BulkActions.ShareMenu(true, true, 3, 3, false)), "all linked");
        Check.Equal(new[] { "CopyLinks", "ShareFiles", "SaveCopies" }, Actions(BulkActions.ShareMenu(true, true, 1, 1, false)), "one linked clip");
    }

    [UnitTest]
    public static void The_upload_entry_is_offered_only_when_the_upload_hook_is_on_and_a_clip_has_no_link()
    {
        Check.Equal(new[] { "Upload", "ShareFiles", "SaveCopies" }, Actions(BulkActions.ShareMenu(true, true, 3, 1, true)));
        Check.Equal(new[] { "CopyLinks", "ShareFiles", "SaveCopies" }, Actions(BulkActions.ShareMenu(true, true, 3, 3, true)), "everything linked: nothing to upload");
        Check.Equal("Upload", Actions(BulkActions.ShareMenu(true, true, 2, 0, true))[0]);
    }

    [UnitTest]
    public static void The_share_files_entry_is_off_above_the_cap_and_says_why()
    {
        Check.Equal(100, BulkActions.MaxShareFiles);
        var atCap = BulkActions.ShareMenu(true, true, 100, 0, false)!.Single(i => i.Action == BulkAction.ShareFiles);
        Check.True(atCap.Enabled, "100 files go");
        var over = BulkActions.ShareMenu(true, true, 101, 0, false)!.Single(i => i.Action == BulkAction.ShareFiles);
        Check.False(over.Enabled, "101 files do not");
        Check.Equal("Select up to 100 files to share at once", over.ToolTip);
        Check.True(BulkActions.ShareMenu(true, true, 101, 0, false)!.Single(i => i.Action == BulkAction.SaveCopies).Enabled, "saving copies has no cap");
    }

    [UnitTest]
    public static void The_share_buttons_follow_the_token_and_cloud_setting()
    {
        Check.Equal(new[] { "Share" }, BulkActions.ShareButtonLabels(true, true, 3, 0).ToArray());
        Check.Equal(new[] { "Share" }, BulkActions.ShareButtonLabels(true, true, 3, 3).ToArray(), "the menu holds Copy links itself");
        Check.Equal(new[] { "Share files…" }, BulkActions.ShareButtonLabels(false, true, 3, 0).ToArray(), "no token, none linked");
        Check.Equal(new[] { "Share files…" }, BulkActions.ShareButtonLabels(false, true, 3, 2).ToArray(), "no token, one without a link");
        Check.Equal(new[] { "Share files…", "Copy links" }, BulkActions.ShareButtonLabels(false, true, 3, 3).ToArray(), "no token, cloud on, every clip linked: both, as the clip row offers");
        Check.Equal(new[] { "Copy links" }, BulkActions.ShareButtonLabels(true, false, 3, 3).ToArray(), "cloud off, all linked");
        Check.Equal(new[] { "Copy links" }, BulkActions.ShareButtonLabels(false, false, 2, 2).ToArray());
        Check.Equal(0, BulkActions.ShareButtonLabels(true, false, 3, 2).Count, "cloud off, one without a link: no button");
        Check.Equal(0, BulkActions.ShareButtonLabels(false, false, 3, 0).Count, "cloud off, none linked: no button");
        Check.Equal(0, BulkActions.ShareButtonLabels(false, false, 0, 0).Count, "nothing selected");
        Check.Equal(ShareChoices.SplitMainLabel, BulkActions.ShareButtonLabels(true, true, 1, 0)[0], "the same main label as the clip row");
        Check.True(BulkActions.ShareIsSplit(true, true), "split with a token and cloud sharing on");
        Check.False(BulkActions.ShareIsSplit(false, true), "no token: plain");
        Check.False(BulkActions.ShareIsSplit(true, false), "cloud off: plain");
        Check.Equal(BulkAction.ShareFiles, BulkActions.SplitMainAction, "the main half is the menu\u0027s Share files\u2026");
        Check.Equal("Share files", BulkActions.SplitMainName);
        Check.Equal("Open the share window: drag the files into Discord, a browser or a folder", BulkActions.SplitMainTip);
        Check.True(BulkActions.ShareMenu(true, true, 3, 0, true)!.Any(i => i.Action == BulkActions.SplitMainAction), "the menu still holds Share files\u2026 too");
    }

    [UnitTest]
    public static void The_bar_stays_while_copies_are_saved_even_with_nothing_selected()
    {
        Check.True(BulkActions.BarVisible(3, false), "a selection");
        Check.True(BulkActions.BarVisible(3, true));
        Check.False(BulkActions.BarVisible(0, false), "nothing selected, nothing running");
        Check.True(BulkActions.BarVisible(0, true), "saving holds the only Cancel");
    }
    // ------------------------------------------------------------------ the texts

    [UnitTest]
    public static void The_count_label_has_plurals()
    {
        Check.Equal("6 clips selected", BulkActions.CountLabel(6, false));
        Check.Equal("1 clip selected", BulkActions.CountLabel(1, false));
        Check.Equal("3 screenshots selected", BulkActions.CountLabel(3, true));
        Check.Equal("1 screenshot selected", BulkActions.CountLabel(1, true));
    }

    [UnitTest]
    public static void Links_are_joined_one_per_line_without_empties_or_repeats()
    {
        Check.Equal("https://c/1\r\nhttps://c/2\r\nhttps://c/3",
            BulkActions.JoinLinks(new string?[] { "https://c/1", null, "", "https://c/2", "  ", "https://c/1", "https://c/3" }));
        Check.Equal("", BulkActions.JoinLinks(new string?[] { null, "" }));
        Check.Equal("https://c/9", BulkActions.JoinLinks(new string?[] { "https://c/9" }), "one link has no line break");
    }

    [UnitTest]
    public static void A_copy_never_takes_the_name_of_a_file_on_disk_or_in_the_batch()
    {
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"D:\Out", "clip.mp4"),
            Path.Combine(@"D:\Out", "clip (2).mp4"),
        };
        var taken = new HashSet<string>();
        Func<string, bool> exists = onDisk.Contains;
        Check.Equal("clip (3).mp4", BulkActions.UniqueName(@"D:\Out", "clip.mp4", exists, taken), "clashes with two files on disk");
        Check.Equal("clip (4).mp4", BulkActions.UniqueName(@"D:\Out", "clip.mp4", exists, taken), "and with the one chosen a moment ago");
        Check.Equal("other.mp4", BulkActions.UniqueName(@"D:\Out", "other.mp4", exists, taken), "a free name is kept");
        Check.Equal("other (2).mp4", BulkActions.UniqueName(@"D:\Out", "other.mp4", exists, taken), "the same name twice in a batch");
        Check.Equal("Other (3).mp4", BulkActions.UniqueName(@"D:\Out", "Other.mp4", exists, taken), "case does not matter");
        Check.Equal("noext (2)", BulkActions.UniqueName(@"D:\Out", "noext", exists, new HashSet<string> { "NOEXT" }), "a name without an extension");
    }

    [UnitTest]
    public static void The_summaries_say_what_happened()
    {
        Check.Equal("Saved 3 copies.", BulkActions.SaveSummary(3, 3, null));
        Check.Equal("Saved 1 copy.", BulkActions.SaveSummary(1, 1, null));
        Check.Equal("Saved 2 of 3 copies. Access denied.", BulkActions.SaveSummary(2, 3, "Access denied"));
        Check.Equal("Nothing was saved. Choose a folder outside Clink's clips folder.", BulkActions.SaveSummary(0, 3, SaveCopy.InsideClipsText));
        Check.Equal("Nothing was saved.", BulkActions.SaveSummary(0, 2, null));
        Check.Equal("Saved 3 of 5 copies. 2 could not be reached.", BulkActions.SaveSummary(3, 5, null, 2), "unreachable rows are in the total");
        Check.Equal("Saved 4 of 5 copies. 1 could not be reached.", BulkActions.SaveSummary(4, 5, null, 1));
        Check.Equal("Saved 2 of 5 copies. Access denied. 1 could not be reached. 1 was busy.", BulkActions.SaveSummary(2, 5, "Access denied", 1, 1));
        Check.Equal("Nothing was saved. 2 were busy.", BulkActions.SaveSummary(0, 2, null, 0, 2));
        Check.Null(BulkActions.ShareFilesSummary(4, 4), "all there: no warning");
        Check.Equal("2 files could not be reached and were left out.", BulkActions.ShareFilesSummary(4, 6));
        Check.Equal("1 file could not be reached and was left out.", BulkActions.ShareFilesSummary(3, 4));
        Check.Equal("None of the files could be reached.", BulkActions.ShareFilesSummary(0, 3));
    }

    [UnitTest]
    public static void The_multi_file_window_is_titled_with_the_count_and_kind()
    {
        Check.Equal("Share 6 clips", ShareFilesWindow.TitleFor(6, false));
        Check.Equal("Share 1 clip", ShareFilesWindow.TitleFor(1, false));
        Check.Equal("Share 6 screenshots", ShareFilesWindow.TitleFor(6, true));
        Check.Equal("Share 1 screenshot", ShareFilesWindow.TitleFor(1, true));
    }

    [UnitTest]
    public static void Several_files_drag_as_one_copy_only_drop_in_the_order_given()
    {
        string[] paths = { @"C:\Clips\b.mp4", @"C:\Clips\a.mp4", @"D:\Other\c.mp4" };
        var data = ShareWindow.FileData(paths);
        Check.Equal(paths, data.GetData(System.Windows.DataFormats.FileDrop) as string[], "the list in the order given");
        var stream = data.GetData(ShareWindow.PreferredDropEffect) as Stream;
        Check.NotNull(stream, "the preferred effect");
        using var bytes = new MemoryStream();
        stream!.Position = 0;
        stream.CopyTo(bytes);
        Check.Equal(new byte[] { 1, 0, 0, 0 }, bytes.ToArray(), "copy, never move");
        Check.Equal(new[] { @"C:\x.mp4" }, ShareWindow.FileData(@"C:\x.mp4").GetData(System.Windows.DataFormats.FileDrop) as string[], "one path is as it was");
    }

    // ------------------------------------------------------------------ the controller

    [UnitTest]
    public static void OpenShareFilesWindow_hands_the_files_and_kind_to_the_opener()
    {
        var settings = Settings.FromJson("{}");
        using var app = new AppController(settings, new Updater(settings));
        var got = new List<(int Count, bool Shots)>();
        app.ShareFilesWindowOpener = (items, shots) => { got.Add((items.Count, shots)); return items.Count > 1; };
        var one = new[] { new ShareFileItem(@"C:\a.mp4", false, 10, 1) };
        var two = new[] { one[0], new ShareFileItem(@"C:\b.mp4", true, 20, 2) };
        Check.False(app.OpenShareFilesWindow(one, false), "the opener's answer");
        Check.True(app.OpenShareFilesWindow(two, true));
        Check.Equal(new[] { (1, false), (2, true) }, got.ToArray());
    }

    private static ClipEntry Source(AppController app, string folder, string name, int size, int seed)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return app.Library.Add(path, 2, size, null, Array.Empty<string>());
    }

    [UnitTest]
    public static void SaveCopiesAsync_copies_each_clip_without_overwriting_and_remembers_the_folder()
    {
        using var dir = new TempDir();
        string? savedOverride = Settings.DirOverride;
        Settings.DirOverride = dir.Path;
        try
        {
            string clips = dir.File("clips");
            Directory.CreateDirectory(clips);
            var settings = Settings.FromJson("{}");
            settings.ClipsFolder = clips;
            using var app = new AppController(settings, new Updater(settings));
            var a = Source(app, dir.File("src1"), "clip.mp4", 5000, 1);
            var b = Source(app, dir.File("src2"), "clip.mp4", 6000, 2);
            var c = Source(app, dir.File("src3"), "other.mp4", 7000, 3);
            string outDir = dir.File("out");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "other.mp4"), "precious");
            app.SaveFolderPicker = () => outDir;
            var seen = new List<int>();
            var result = Wait(app.SaveCopiesAsync(new[] { a, b, c }, null, (i, p) => { lock (seen) seen.Add(i); }, CancellationToken.None));
            Check.Equal(3, result.Saved);
            Check.Equal(3, result.Total);
            Check.False(result.Cancelled);
            Check.Null(result.FirstError);
            Check.Equal(outDir, result.Folder);
            Check.True(File.ReadAllBytes(Path.Combine(outDir, "clip.mp4")).AsSpan().SequenceEqual(File.ReadAllBytes(a.Path)), "first copy");
            Check.True(File.ReadAllBytes(Path.Combine(outDir, "clip (2).mp4")).AsSpan().SequenceEqual(File.ReadAllBytes(b.Path)), "the second clip of that name does not overwrite the first");
            Check.True(File.ReadAllBytes(Path.Combine(outDir, "other (2).mp4")).AsSpan().SequenceEqual(File.ReadAllBytes(c.Path)), "a file already there is left alone");
            Check.Equal("precious", File.ReadAllText(Path.Combine(outDir, "other.mp4")), "never overwritten");
            Check.Equal(0, Directory.GetFiles(outDir, "*.clinkpart").Length, "no part files");
            lock (seen) Check.True(seen.Contains(0) && seen.Contains(1) && seen.Contains(2), "progress named every clip");
            Check.Equal(ScreenshotService.IsOnLocalFixedDrive(outDir) ? outDir : null, settings.SaveCopyFolder);
        }
        finally { Settings.DirOverride = savedOverride; }
    }

    [UnitTest]
    public static void SaveCopiesAsync_refuses_the_clips_folder_and_writes_nothing()
    {
        using var dir = new TempDir();
        string? savedOverride = Settings.DirOverride;
        Settings.DirOverride = dir.Path;
        try
        {
            string clips = dir.File("clips");
            Directory.CreateDirectory(Path.Combine(clips, "sub"));
            var settings = Settings.FromJson("{}");
            settings.ClipsFolder = clips;
            using var app = new AppController(settings, new Updater(settings));
            var a = Source(app, dir.File("src"), "a.mp4", 3000, 4);
            var b = Source(app, dir.File("src"), "b.mp4", 3000, 5);
            foreach (string target in new[] { clips, Path.Combine(clips, "sub") })
            {
                app.SaveFolderPicker = () => target;
                var result = Wait(app.SaveCopiesAsync(new[] { a, b }, null, null, CancellationToken.None));
                Check.Equal(0, result.Saved, target);
                Check.Equal(SaveCopy.InsideClipsText, result.FirstError);
                Check.False(result.Cancelled);
                Check.Equal(0, Directory.GetFiles(target).Length, "nothing written into " + target);
            }
            Check.Null(settings.SaveCopyFolder, "a refused folder is not remembered");
            string gone = dir.File("not-there");
            app.SaveFolderPicker = () => gone;
            var missing = Wait(app.SaveCopiesAsync(new[] { a }, null, null, CancellationToken.None));
            Check.Equal(0, missing.Saved);
            Check.NotNull(missing.FirstError, "a folder that is not there");
            Check.False(Directory.Exists(gone), "and it is not created");
            app.SaveFolderPicker = () => null;
            var cancelled = Wait(app.SaveCopiesAsync(new[] { a }, null, null, CancellationToken.None));
            Check.True(cancelled.Cancelled, "the dialog was cancelled");
            Check.Null(cancelled.Folder);
        }
        finally { Settings.DirOverride = savedOverride; }
    }

    [UnitTest]
    public static void SaveCopiesAsync_cancelled_midway_keeps_the_finished_copies_and_leaves_no_part_file()
    {
        using var dir = new TempDir();
        string? savedOverride = Settings.DirOverride;
        Settings.DirOverride = dir.Path;
        try
        {
            string clips = dir.File("clips");
            Directory.CreateDirectory(clips);
            var settings = Settings.FromJson("{}");
            settings.ClipsFolder = clips;
            using var app = new AppController(settings, new Updater(settings));
            var a = Source(app, dir.File("src"), "a.mp4", 3_000_000, 6);
            var b = Source(app, dir.File("src"), "b.mp4", 3_000_000, 7);
            var c = Source(app, dir.File("src"), "c.mp4", 3_000_000, 8);
            string outDir = dir.File("out");
            Directory.CreateDirectory(outDir);
            app.SaveFolderPicker = () => outDir;
            using var cts = new CancellationTokenSource();
            var result = Wait(app.SaveCopiesAsync(new[] { a, b, c }, null, (i, p) => { if (i == 1 && p == 0) cts.Cancel(); }, cts.Token));
            Check.True(result.Cancelled);
            Check.Equal(1, result.Saved, "the first copy was finished");
            Check.True(File.Exists(Path.Combine(outDir, "a.mp4")));
            Check.False(File.Exists(Path.Combine(outDir, "b.mp4")), "the cancelled one is not left half written");
            Check.False(File.Exists(Path.Combine(outDir, "c.mp4")), "the rest were not started");
            Check.Equal(0, Directory.GetFiles(outDir, "*.clinkpart").Length, "no part files");
        }
        finally { Settings.DirOverride = savedOverride; }
    }

    [UnitTest]
    public static void SaveCopiesAsync_counts_a_missing_clip_in_the_total_with_its_reason()
    {
        using var dir = new TempDir();
        string? savedOverride = Settings.DirOverride;
        Settings.DirOverride = dir.Path;
        try
        {
            string clips = dir.File("clips");
            Directory.CreateDirectory(clips);
            var settings = Settings.FromJson("{}");
            settings.ClipsFolder = clips;
            using var app = new AppController(settings, new Updater(settings));
            var a = Source(app, dir.File("src"), "a.mp4", 3000, 11);
            var b = Source(app, dir.File("src"), "b.mp4", 3000, 12);
            var c = Source(app, dir.File("src"), "c.mp4", 3000, 13);
            File.Delete(b.Path);
            string outDir = dir.File("out");
            Directory.CreateDirectory(outDir);
            app.SaveFolderPicker = () => outDir;
            var result = Wait(app.SaveCopiesAsync(new[] { a, b, c }, null, null, CancellationToken.None));
            Check.Equal(2, result.Saved);
            Check.Equal(3, result.Total, "every asked-for clip is in the total");
            Check.Equal(1, result.Unreachable);
            Check.Null(result.FirstError, "a missing file is counted, not an error text");
            Check.Equal("Saved 2 of 3 copies. 1 could not be reached.", BulkActions.SaveSummary(result.Saved, result.Total, result.FirstError, result.Unreachable));
            Check.Equal(0, Directory.GetFiles(outDir, "*.clinkpart").Length, "no part files");
        }
        finally { Settings.DirOverride = savedOverride; }
    }

    [UnitTest]
    public static void CopyUniqueAsync_never_replaces_a_file_that_appears_after_the_name_was_picked()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });
        string outDir = dir.File("out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "clip.mp4"), "arrived meanwhile");
        // the existence check does not see it (it appeared after the check): the move must refuse and the next name be used
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string target = Wait(SaveCopy.CopyUniqueAsync(src, outDir, "clip.mp4", taken, null, CancellationToken.None, exists: _ => false));
        Check.Equal(Path.Combine(outDir, "clip (2).mp4"), target);
        Check.Equal("arrived meanwhile", File.ReadAllText(Path.Combine(outDir, "clip.mp4")), "never overwritten");
        Check.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target));
        Check.Equal(0, Directory.GetFiles(outDir, "*.clinkpart").Length, "no part files");
    }

    [UnitTest]
    public static void CopyAsync_without_overwrite_keeps_an_existing_target_and_the_default_still_replaces()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4"), dst = dir.File("dst.mp4");
        File.WriteAllBytes(src, new byte[] { 9, 9 });
        File.WriteAllText(dst, "keep me");
        Check.Throws<IOException>(() => Wait(SaveCopy.CopyAsync(src, dst, null, CancellationToken.None, overwrite: false)));
        Check.Equal("keep me", File.ReadAllText(dst));
        Check.False(File.Exists(dst + ".clinkpart"), "no part file left");
        Wait(SaveCopy.CopyAsync(src, dst, null, CancellationToken.None));
        Check.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(dst), "the single save (confirmed by the user) still replaces");
    }

    [UnitTest]
    public static void CopyUniqueAsync_gives_up_after_a_bounded_number_of_names()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4");
        File.WriteAllBytes(src, new byte[] { 1 });
        string outDir = dir.File("out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "x.mp4"), "0");
        for (int n = 2; n <= SaveCopy.MaxNameTries + 3; n++) File.WriteAllText(Path.Combine(outDir, $"x ({n}).mp4"), "0");
        Check.Throws<IOException>(() => Wait((Task)SaveCopy.CopyUniqueAsync(src, outDir, "x.mp4", new HashSet<string>(), null, CancellationToken.None, exists: _ => false)));
        Check.Equal("0", File.ReadAllText(Path.Combine(outDir, "x.mp4")));
    }

    [UnitTest]
    public static void CopyUniqueAsync_treats_a_directory_under_the_name_as_a_clash()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4");
        File.WriteAllBytes(src, new byte[] { 5, 6 });
        string outDir = dir.File("out");
        Directory.CreateDirectory(Path.Combine(outDir, "clip.mp4"));   // a folder named like the clip
        string target = Wait(SaveCopy.CopyUniqueAsync(src, outDir, "clip.mp4", new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, CancellationToken.None));
        Check.Equal(Path.Combine(outDir, "clip (2).mp4"), target);
        Check.True(Directory.Exists(Path.Combine(outDir, "clip.mp4")), "the folder is left alone");
        // the same when the folder appears after the name was picked: the move refuses and the retry sees the directory
        string late = Wait(SaveCopy.CopyUniqueAsync(src, outDir, "clip.mp4", new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, CancellationToken.None, exists: _ => false));
        Check.Equal(Path.Combine(outDir, "clip (3).mp4"), late, "clip.mp4 (the folder) and clip (2).mp4 (the first copy) are refused by the move, then the next name");
        Check.True(Directory.Exists(Path.Combine(outDir, "clip.mp4")), "the folder is still a folder");
        Check.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(Path.Combine(outDir, "clip (2).mp4")), "the first copy is untouched");
        Check.Equal(0, Directory.GetFiles(outDir, "*.clinkpart").Length, "no part files");
    }

    [UnitTest]
    public static void CopyUniqueAsync_fails_once_on_an_error_that_is_not_a_name_clash()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        string outDir = dir.File("out");
        Directory.CreateDirectory(outDir);
        // the source held open by another program (a sharing violation): no name is to blame, so no other name is tried
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check.Throws<IOException>(() => Wait((Task)SaveCopy.CopyUniqueAsync(src, outDir, "clip.mp4", taken, null, CancellationToken.None)));
        Check.Equal(new[] { "clip.mp4" }, taken.ToArray(), "one name tried, no loop");
        // the destination side locked (its part file held open): the same
        taken.Clear();
        using (new FileStream(Path.Combine(outDir, "clip.mp4.clinkpart"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Check.Throws<IOException>(() => Wait((Task)SaveCopy.CopyUniqueAsync(src, outDir, "clip.mp4", taken, null, CancellationToken.None)));
        Check.Equal(new[] { "clip.mp4" }, taken.ToArray(), "one name tried, no loop");
        Check.Equal(0, Directory.GetFiles(outDir, "*.mp4").Length, "nothing written");
    }

    [UnitTest]
    public static void SaveCopiesAsync_stops_when_the_folder_disappears_and_skips_a_row_that_is_busy_when_its_turn_comes()
    {
        using var dir = new TempDir();
        string? savedOverride = Settings.DirOverride;
        Settings.DirOverride = dir.Path;
        try
        {
            string clips = dir.File("clips");
            Directory.CreateDirectory(clips);
            var settings = Settings.FromJson("{}");
            settings.ClipsFolder = clips;
            using var app = new AppController(settings, new Updater(settings));
            var a = Source(app, dir.File("src"), "a.mp4", 3000, 21);
            var b = Source(app, dir.File("src"), "b.mp4", 3000, 22);
            var c = Source(app, dir.File("src"), "c.mp4", 3000, 23);
            var d = Source(app, dir.File("src"), "d.mp4", 3000, 24);
            // a row the user started something else on: skipped as busy, never copied, no progress for it
            string outDir = dir.File("out");
            Directory.CreateDirectory(outDir);
            app.SaveFolderPicker = () => outDir;
            var seen = new List<int>();
            var asked = new List<int>();
            var result = Wait(app.SaveCopiesAsync(new[] { a, b, c }, null, (i, p) => { lock (seen) seen.Add(i); }, CancellationToken.None,
                i => { asked.Add(i); return i != 1; }));
            Check.Equal(new[] { 0, 1, 2 }, asked.ToArray(), "every row's turn is asked, in order");
            Check.Equal(2, result.Saved);
            Check.Equal(3, result.Total);
            Check.Equal(1, result.Busy);
            Check.False(File.Exists(Path.Combine(outDir, "b.mp4")), "the busy one is not copied");
            lock (seen) Check.False(seen.Contains(1), "and gets no progress");
            Check.Equal("Saved 2 of 3 copies. 1 was busy.", BulkActions.SaveSummary(result.Saved, result.Total, result.FirstError, result.Unreachable, result.Busy));
            // the folder is unplugged after the first copy: the batch stops with one reason, the rest are not counted as missing
            string usb = dir.File("usb");
            Directory.CreateDirectory(usb);
            app.SaveFolderPicker = () => usb;
            var started = new List<int>();
            var gone = Wait(app.SaveCopiesAsync(new[] { a, b, c, d }, null, (i, p) =>
            {
                lock (started) { if (started.Contains(i)) return; started.Add(i); }
                if (i == 1 && p == 0) Directory.Delete(usb, true);
            }, CancellationToken.None));
            Check.Equal(1, gone.Saved);
            Check.Equal(AppController.FolderGoneText, gone.FirstError);
            Check.Equal(0, gone.Unreachable, "the sources are fine: not counted as unreachable");
            Check.False(gone.Cancelled);
            lock (started) Check.Equal(new[] { 0, 1 }, started.ToArray(), "no copy after the folder went");
            Check.False(Directory.Exists(usb), "the folder is not created again");
            Check.Equal("Saved 1 of 4 copies. The folder is no longer there.", BulkActions.SaveSummary(gone.Saved, gone.Total, gone.FirstError, gone.Unreachable));
        }
        finally { Settings.DirOverride = savedOverride; }
    }
}