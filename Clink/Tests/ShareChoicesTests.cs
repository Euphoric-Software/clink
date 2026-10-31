using System.IO;
using System.Net.Http;
using Clink.Screenshots;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The video Share button's menu (ShareChoices), Save a copy (SaveCopy, AppController.SaveCopyAsync) and Share file.</summary>
internal static class ShareChoicesTests
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

    private sealed class SyncProgress: IProgress<double>
    {
        public readonly List<double> Values = new();
        public void Report(double value) => Values.Add(value);
    }

    private sealed class Offline : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("no route");
        }
    }

    // ------------------------------------------------------------------ the choice table

    [UnitTest]
    public static void The_menu_applies_only_with_a_token_and_cloud_sharing_on()
    {
        Check.Null(ShareChoices.For(false, false, false));
        Check.Null(ShareChoices.For(false, true, false), "no token");
        Check.Null(ShareChoices.For(false, true, true), "no token, with a link");
        Check.Null(ShareChoices.For(true, false, false), "cloud off");
        Check.Null(ShareChoices.For(true, false, true), "cloud off, with a link");
        Check.Equal(new[] { ShareChoice.Upload, ShareChoice.ShareFile, ShareChoice.SaveCopy }, ShareChoices.For(true, true, false)!.Select(i => i.Choice).ToArray());
        Check.Equal(new[] { ShareChoice.CopyLink, ShareChoice.ShareFile, ShareChoice.SaveCopy }, ShareChoices.For(true, true, true)!.Select(i => i.Choice).ToArray());
    }

    [UnitTest]
    public static void Menu_items_have_their_headers_access_keys_and_tooltips()
    {
        var items = ShareChoices.For(true, true, false)!;
        Check.Equal("_Upload to Copium", items[0].Header);
        Check.Equal("Upload to your Copium account and copy the link", items[0].ToolTip);
        Check.Equal("_Share file\u2026", items[1].Header);
        Check.Equal("Drag or copy the file into Discord, a browser or a folder", items[1].ToolTip);
        Check.Equal("S_ave a copy\u2026", items[2].Header);
        Check.Equal("Save a copy of the clip file to another folder", items[2].ToolTip);
        var linked = ShareChoices.For(true, true, true)!;
        Check.Equal("_Copy link", linked[0].Header);
        Check.Equal("Copy the clip's Copium link", linked[0].ToolTip);
        Check.Equal(3, items.Select(i => i.Header.Split('_')[1][0]).Distinct().Count(), "distinct access keys");
    }

    [UnitTest]
    public static void Button_and_toast_labels_for_all_eight_combinations()
    {
        foreach (bool token in new[] { false, true })
            foreach (bool cloud in new[] { false, true })
                foreach (bool link in new[] { false, true })
                {
                    bool menu = token && cloud;
                    string row = menu ? "Share \u25BE" : link ? "Copy link" : "Share";
                    string toast = menu ? "Share \u25BE" : link ? "Copy link" : token ? "Share to Copium" : "Share";
                    Check.Equal(row, ShareChoices.ButtonLabel(token, cloud, link), $"row {token}/{cloud}/{link}");
                    Check.Equal(toast, ShareChoices.ToastLabel(token, cloud, link), $"toast {token}/{cloud}/{link}");
                    Check.Equal(menu ? "Share options" : null, ShareChoices.ButtonName(token, cloud, link), $"name {token}/{cloud}/{link}");
                }
    }

    // ------------------------------------------------------------------ SaveCopy (pure)

    [UnitTest]
    public static void TargetProblem_refuses_the_own_file_and_anything_in_the_clips_folder()
    {
        using var dir = new TempDir();
        string clips = dir.File("Clink");
        string src = Path.Combine(clips, "a.mp4");
        Check.Equal(SaveCopy.OwnFileText, SaveCopy.TargetProblem(src, src, clips));
        Check.Equal(SaveCopy.OwnFileText, SaveCopy.TargetProblem(src.ToUpperInvariant(), src, clips), "case");
        Check.Equal(SaveCopy.OwnFileText, SaveCopy.TargetProblem(Path.Combine(clips, "x", "..", "a.mp4"), src, clips), "dot-dot");
        Check.Equal(SaveCopy.InsideClipsText, SaveCopy.TargetProblem(Path.Combine(clips, "b.mp4"), src, clips));
        Check.Equal(SaveCopy.InsideClipsText, SaveCopy.TargetProblem(Path.Combine(clips, "Recordings", "b.mp4"), src, clips));
        Check.Equal(SaveCopy.InsideClipsText, SaveCopy.TargetProblem(Path.Combine(clips, "b.mp4"), src, clips + Path.DirectorySeparatorChar), "trailing separator on the folder");
        Check.Null(SaveCopy.TargetProblem(Path.Combine(dir.File("Clink2"), "b.mp4"), src, clips), "a sibling with the same prefix is outside");
        Check.Null(SaveCopy.TargetProblem(dir.File("copy.mp4"), src, clips));
        Check.Null(SaveCopy.TargetProblem(dir.File("copy.mp4"), src, null));
    }

    [UnitTest]
    public static void DefaultFileName_and_InitialFolder()
    {
        Check.Equal("clip 1.mp4", SaveCopy.DefaultFileName(@"D:\Videos\Clink\clip 1.mp4"));
        using var dir = new TempDir();
        string fallback = dir.File("MyVideos");
        Check.Equal(fallback, SaveCopy.InitialFolder(null, fallback));
        Check.Equal(fallback, SaveCopy.InitialFolder("", fallback));
        Check.Equal(fallback, SaveCopy.InitialFolder("   ", fallback));
        Check.Equal(fallback, SaveCopy.InitialFolder(dir.File("gone"), fallback), "a folder that is gone");
        Check.Equal(dir.Path, SaveCopy.InitialFolder(dir.Path, fallback));
    }

    [UnitTest]
    public static void CopyAsync_copies_the_bytes_replaces_a_target_and_reports_progress_ending_at_one()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4"), dst = dir.File("dst.mp4");
        var bytes = new byte[(3 << 20) + 123];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(src, bytes);
        File.WriteAllText(dst, "old");
        var progress = new SyncProgress();
        Wait(SaveCopy.CopyAsync(src, dst, progress, CancellationToken.None));
        Check.True(File.ReadAllBytes(dst).AsSpan().SequenceEqual(bytes), "same bytes, the old target replaced");
        Check.False(File.Exists(dst + ".clinkpart"), "no part file left");
        Check.Equal(1.0, progress.Values.Last(), "ends at 1");
        Check.True(progress.Values.Zip(progress.Values.Skip(1)).All(p => p.Second >= p.First), "never goes back");
    }

    [UnitTest]
    public static void CopyAsync_cancelled_leaves_no_part_file_and_the_target_unchanged()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4"), dst = dir.File("dst.mp4");
        File.WriteAllBytes(src, new byte[(2 << 20) + 5]);
        File.WriteAllText(dst, "keep me");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = Check.Throws<OperationCanceledException>(() => SaveCopy.CopyAsync(src, dst, null, cts.Token).GetAwaiter().GetResult());
        Check.NotNull(ex);
        Check.False(File.Exists(dst + ".clinkpart"));
        Check.Equal("keep me", File.ReadAllText(dst));
        // a missing source fails the same way, nothing is created
        string gone = dir.File("never.mp4");
        Check.Throws<IOException>(() => SaveCopy.CopyAsync(dir.File("nothing.mp4"), gone, null, CancellationToken.None).GetAwaiter().GetResult());
        Check.False(File.Exists(gone));
        Check.False(File.Exists(gone + ".clinkpart"));
    }

    [UnitTest]
    public static void CopyAsync_that_cannot_move_into_place_keeps_the_target_and_leaves_no_part_file()
    {
        using var dir = new TempDir();
        string src = dir.File("src.mp4"), dst = dir.File("dst.mp4");
        File.WriteAllBytes(src, new byte[1024]);
        File.WriteAllText(dst, "keep me");
        File.SetAttributes(dst, FileAttributes.ReadOnly);
        try
        {
            Check.Throws<UnauthorizedAccessException>(() => Wait(SaveCopy.CopyAsync(src, dst, null, CancellationToken.None)));
            Check.Equal("keep me", File.ReadAllText(dst));
            Check.False(File.Exists(dst + ".clinkpart"), "no part file left");
        }
        finally { File.SetAttributes(dst, FileAttributes.Normal); }
    }

    [UnitTest]
    public static void The_share_window_intro_depends_on_the_token()
    {
        Check.Equal("Drag the file where you want it, or copy it:", Clink.UI.ShareWindow.IntroFor(true));
        Check.True(Clink.UI.ShareWindow.IntroFor(false).StartsWith("No Copium token yet"));
    }

    // ------------------------------------------------------------------ the controller

    private static ClipEntry Newest(AppController app, TempDir dir, string name, int size = 4096)
    {
        string path = dir.File(name);
        var bytes = new byte[size];
        new Random(3).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return app.Library.Add(path, 2, size, null, Array.Empty<string>());
    }

    [UnitTest]
    public static void SaveCopyAsync_cancelled_missing_and_refused_write_nothing()
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
            var entry = Newest(app, dir, "clip.mp4");
            string target = dir.File("out.mp4");

            app.SaveTargetPicker = _ => null;
            Check.Equal(SaveOutcome.Cancelled, Wait(app.SaveCopyAsync(entry, null, null)).Outcome);
            Check.False(File.Exists(target));

            string gonePath = dir.File("gone.mp4");
            var missing = new ClipEntry { Path = gonePath, Size = 10, Duration = 1, Created = DateTime.Now };
            app.SaveTargetPicker = _ => target;
            var failed = Wait(app.SaveCopyAsync(missing, null, null));
            Check.Equal(SaveOutcome.Failed, failed.Outcome);
            Check.NotNull(failed.Error);
            Check.False(File.Exists(target));

            app.SaveTargetPicker = _ => Path.Combine(clips, "copy.mp4");
            var inside = Wait(app.SaveCopyAsync(entry, null, null));
            Check.Equal(SaveOutcome.Refused, inside.Outcome);
            Check.Equal(SaveCopy.InsideClipsText, inside.Error);
            Check.False(File.Exists(Path.Combine(clips, "copy.mp4")));

            app.SaveTargetPicker = _ => entry.Path;
            var own = Wait(app.SaveCopyAsync(entry, null, null));
            Check.Equal(SaveOutcome.Refused, own.Outcome);
            Check.Equal(SaveCopy.OwnFileText, own.Error);
            Check.Null(own.SavedTo);
        }
        finally { Settings.DirOverride = savedOverride; }
    }

    [UnitTest]
    public static void SaveCopyAsync_saves_a_copy_without_touching_the_library_and_remembers_the_folder()
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
            var entry = Newest(app, dir, "clip.mp4");
            string linkBefore = entry.CopiumLink ?? "";
            string pathBefore = entry.Path;
            var entriesBefore = app.Library.Entries.ToList();
            string outDir = dir.File("out");
            Directory.CreateDirectory(outDir);
            string target = Path.Combine(outDir, "copy.mp4");
            app.SaveTargetPicker = e => { Check.True(ReferenceEquals(e, entry)); return target; };
            var progress = new SyncProgress();
            var saved = Wait(app.SaveCopyAsync(entry, null, progress));
            Check.Equal(SaveOutcome.Saved, saved.Outcome);
            Check.Null(saved.Error);
            Check.Equal(target, saved.SavedTo);
            Check.True(File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(entry.Path)));
            Check.Equal(1.0, progress.Values.Last());
            Check.True(entriesBefore.SequenceEqual(app.Library.Entries), "the library is as it was");
            Check.Equal(pathBefore, entry.Path);
            Check.Equal(linkBefore, entry.CopiumLink ?? "");
            Check.Equal(ScreenshotService.IsOnLocalFixedDrive(target) ? outDir : null, settings.SaveCopyFolder);
        }
        finally { Settings.DirOverride = savedOverride; }
    }

    [UnitTest]
    public static void ShareFile_opens_the_share_window_with_a_token_and_sends_nothing()
    {
        using var dir = new TempDir();
        var settings = Settings.FromJson("{}");
        settings.CopiumToken = "eup_token";
        settings.CopiumConsent = true;
        using var app = new AppController(settings, new Updater(settings));
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            var opened = new List<ClipEntry>();
            bool result = true;
            app.ShareWindowOpener = e => { opened.Add(e); return result; };
            string path = dir.File("clip.mp4");
            File.WriteAllBytes(path, new byte[64]);
            var entry = new ClipEntry { Path = path, Size = 64, Duration = 2, Created = DateTime.Now };
            Check.True(app.ShareFile(entry));
            Check.Equal(ShareOutcome.FileShareOpened, app.LastShareOutcome);
            Check.Equal(1, opened.Count);
            Check.Equal(0, handler.Requests, "no upload");
            result = false;
            Check.False(app.ShareFile(entry));
            Check.Equal(ShareOutcome.WindowFailed, app.LastShareOutcome);
            Check.Equal(AppController.ShareWindowFailedText, app.ShareError);
            Check.Equal(0, handler.Requests);
        }
        finally { CopiumClient.HttpOverride = null; }
    }
}
