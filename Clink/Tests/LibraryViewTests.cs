using System.Collections.Concurrent;
using System.IO;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Imaging;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

// The library pane's two-level switch (Clips | Screenshots, Local | Shared, the Trimmed filter), the screenshots side, the history's
// Remove / Changed and the delete flow (docs: slice S8). The pane's window is covered by the smoke run; the rules are all here.
internal static class LibraryViewTests
{
    private sealed record Item(string Name, bool Local, bool Shared, bool Trim);

    private static readonly Item[] Clips =
    {
        new("a", Local: true, Shared: false, Trim: false),
        new("b", Local: true, Shared: true, Trim: true),
        new("c", Local: true, Shared: false, Trim: true),
        new("d", Local: false, Shared: true, Trim: false),   // shared, the file is gone
        new("e", Local: false, Shared: true, Trim: true),
    };

    private static string[] Names(IEnumerable<Item> items) => items.Select(i => i.Name).ToArray();
    private static List<Item> Pick(LibraryLevel level, bool trimmed) => LibraryView.Pick(Clips, level, trimmed, i => i.Local, i => i.Shared, i => i.Trim);

    // ------------------------------------------------------------------ the switches

    [UnitTest]
    public static void The_pane_opens_on_the_saved_feature_and_level_and_defaults_to_clips_local()
    {
        Check.Equal((LibraryFeature.Clips, LibraryLevel.Local), LibraryView.Resolve(null, null, true, true));
        Check.Equal((LibraryFeature.Clips, LibraryLevel.Local), LibraryView.Resolve("garbage", "", true, true));
        Check.Equal((LibraryFeature.Screenshots, LibraryLevel.Shared), LibraryView.Resolve("screenshots", "shared", true, true));
        Check.Equal((LibraryFeature.Screenshots, LibraryLevel.Shared), LibraryView.Resolve(" Screenshots ", "SHARED", true, true), "case and spaces do not matter");
        Check.Equal((LibraryFeature.Clips, LibraryLevel.Shared), LibraryView.Resolve("clips", "shared", true, true));
        var fresh = Settings.FromJson("{}");
        Check.Equal("clips", fresh.LibraryFeature);
        Check.Equal("local", fresh.LibraryLevel);
    }

    [UnitTest]
    public static void A_feature_that_is_off_falls_back_to_the_one_that_is_on()
    {
        Check.Equal(LibraryFeature.Screenshots, LibraryView.Resolve("clips", "local", clipsOn: false, screenshotsOn: true).Feature, "clips off: the saved clips side shows screenshots");
        Check.Equal(LibraryFeature.Clips, LibraryView.Resolve("screenshots", "local", clipsOn: true, screenshotsOn: false).Feature, "screenshots off: clips");
        Check.Equal(LibraryFeature.Screenshots, LibraryView.Resolve("screenshots", "local", true, true).Feature, "both on: the saved one");
        Check.Equal(LibraryFeature.Screenshots, LibraryView.Fallback(LibraryFeature.Clips, false, true));
        Check.Equal(LibraryFeature.Clips, LibraryView.Fallback(LibraryFeature.Screenshots, true, false));
        Check.Equal(LibraryFeature.Clips, LibraryView.Fallback(LibraryFeature.Clips, true, true), "a choice that can stay, stays");
        // the library itself and the top switch
        Check.True(LibraryView.ShowLibrary(true, false) && LibraryView.ShowLibrary(false, true) && LibraryView.ShowLibrary(true, true));
        Check.False(LibraryView.ShowLibrary(false, false));
        Check.True(LibraryView.ShowFeatureSwitch(true, true), "a choice between two");
        Check.False(LibraryView.ShowFeatureSwitch(true, false), "one feature on: implied, no top row");
        Check.False(LibraryView.ShowFeatureSwitch(false, true));
    }

    [UnitTest]
    public static void The_trimmed_chip_belongs_to_the_clips_side_only()
    {
        Check.True(LibraryView.ShowTrimmedChip(LibraryFeature.Clips));
        Check.False(LibraryView.ShowTrimmedChip(LibraryFeature.Screenshots));
        Check.True(LibraryView.TrimmedOnly(LibraryFeature.Clips, true));
        Check.False(LibraryView.TrimmedOnly(LibraryFeature.Clips, false));
        Check.False(LibraryView.TrimmedOnly(LibraryFeature.Screenshots, true), "a chip left on does not filter the screenshots");
    }

    [UnitTest]
    public static void A_level_lists_its_items_and_the_trimmed_filter_narrows_either_level()
    {
        Check.Equal(new[] { "a", "b", "c" }, Names(Pick(LibraryLevel.Local, false)), "local: the files that are there");
        Check.Equal(new[] { "b", "d", "e" }, Names(Pick(LibraryLevel.Shared, false)), "shared: the links, even without the file");
        Check.Equal(new[] { "b", "c" }, Names(Pick(LibraryLevel.Local, true)), "local, trimmed only");
        Check.Equal(new[] { "b", "e" }, Names(Pick(LibraryLevel.Shared, true)), "shared, trimmed only");
        Check.Equal((3, 3), LibraryView.Counts(Clips, false, i => i.Local, i => i.Shared, i => i.Trim));
        Check.Equal((2, 2), LibraryView.Counts(Clips, true, i => i.Local, i => i.Shared, i => i.Trim), "the counts follow the filter");
        Check.Equal((0, 0), LibraryView.Counts(Array.Empty<Item>(), false, i => i.Local, i => i.Shared, i => i.Trim));
        Check.Equal("Local 12", LibraryView.Label("Local", 12));
        Check.Equal("Shared", LibraryView.Label("Shared", 0), "no number for an empty level");
    }

    [UnitTest]
    public static void The_empty_list_says_what_goes_there()
    {
        Check.Equal("No screenshots yet — press PrintScreen", LibraryView.EmptyText(LibraryFeature.Screenshots, LibraryLevel.Local, false));
        Check.Equal("No shared screenshots", LibraryView.EmptyText(LibraryFeature.Screenshots, LibraryLevel.Shared, false));
        Check.Contains(LibraryView.EmptyText(LibraryFeature.Clips, LibraryLevel.Local, false), "No clips yet");
        Check.Contains(LibraryView.EmptyText(LibraryFeature.Clips, LibraryLevel.Local, true), "No trimmed clips");
        Check.Contains(LibraryView.EmptyText(LibraryFeature.Clips, LibraryLevel.Shared, false), "Nothing shared");
    }

    [UnitTest]
    public static void Upload_shows_only_with_cloud_sharing_on_a_file_and_no_link()
    {
        Check.True(LibraryView.UploadVisible(cloudOn: true, hasLink: false, hasFile: true));
        Check.False(LibraryView.UploadVisible(cloudOn: false, hasLink: false, hasFile: true), "cloud off: no Upload");
        Check.False(LibraryView.UploadVisible(true, hasLink: true, hasFile: true), "it has a link: Copy link instead");
        Check.False(LibraryView.UploadVisible(true, false, hasFile: false), "nothing to upload");
    }

    // ------------------------------------------------------------------ the screenshots side

    private static ScreenshotEntry Shot(string path, int minutes = 0, string? game = null, string? link = null, int w = 0, int h = 0, long size = 0) =>
        new() { Path = path, Created = new DateTime(2026, 10, 25, 14, 3, 0).AddMinutes(-minutes), Game = game, CopiumLink = link, Width = w, Height = h, Size = size };

    [UnitTest]
    public static void A_screenshot_row_has_a_title_and_a_meta_line()
    {
        Check.Equal("Screenshot", ScreenshotRow.TitleFor(Shot(@"C:\s\a.png")));
        Check.Equal("Screenshot", ScreenshotRow.TitleFor(Shot(@"C:\s\a.png", game: "  ")));
        Check.Equal("Overwatch", ScreenshotRow.TitleFor(Shot(@"C:\s\a.png", game: "Overwatch")));
        var e = Shot(@"C:\s\a.png", size: 412_000);
        string time = e.Created.ToString("MMM d, HH:mm");
        Check.Equal($"1920×1080 · {Capabilities.FormatBytes(412_000)} · {time}", ScreenshotRow.MetaFor(e, 1920, 1080));
        Check.Equal($"{Capabilities.FormatBytes(412_000)} · {time}", ScreenshotRow.MetaFor(e, 0, 0), "an unknown size is left out");
        var row = new ScreenshotRow(new ScreenshotItem(e, true, 1280, 720));
        Check.Equal("Screenshot", row.Title);
        Check.True(row.Meta.StartsWith("1280×720 · "), row.Meta);
        var gone = new ScreenshotRow(new ScreenshotItem(Shot(@"C:\s\b.png", link: "https://v.copium.pics/x.png"), false, 0, 0));
        Check.True(gone.Meta.EndsWith("· file deleted"), gone.Meta);
        Check.False(gone.HasLocalFile);
        Check.True(gone.HasLink);
    }

    [UnitTest]
    public static void Screenshots_are_local_when_the_file_is_there_and_shared_when_they_have_a_link()
    {
        var items = new[]
        {
            new ScreenshotItem(Shot(@"C:\s\new.png", 0), true, 10, 10),
            new ScreenshotItem(Shot(@"C:\s\shared.png", 1, link: "https://v.copium.pics/1.png"), true, 10, 10),
            new ScreenshotItem(Shot(@"C:\s\missing.png", 2), false, 10, 10),
            new ScreenshotItem(Shot(@"C:\s\sharedgone.png", 3, link: "https://v.copium.pics/2.png"), false, 10, 10),
        };
        string[] P(LibraryLevel l) => ScreenshotListModel.Pick(items, l).Select(i => Path.GetFileName(i.Entry.Path)).ToArray();
        Check.Equal(new[] { "new.png", "shared.png" }, P(LibraryLevel.Local), "newest first (the history's order); the missing file is excluded");
        Check.Equal(new[] { "shared.png", "sharedgone.png" }, P(LibraryLevel.Shared), "a shared one is listed by its link even without the file");
        Check.Equal((2, 2), LibraryView.Counts(items, false, ScreenshotListModel.IsLocal, ScreenshotListModel.IsShared, _ => false));
    }

    [UnitTest]
    public static void The_screenshot_list_reads_the_history_and_the_files_on_a_worker_never_on_the_calling_thread()
    {
        int caller = Environment.CurrentManagedThreadId;
        var calls = new ConcurrentQueue<(string What, int Thread)>();
        var entries = new List<ScreenshotEntry> { Shot(@"C:\s\a.png", size: 5, w: 40, h: 30), Shot(@"C:\s\b.png"), Shot(@"C:\s\gone.png") };
        var model = new ScreenshotListModel(
            () => { calls.Enqueue(("history", Environment.CurrentManagedThreadId)); return entries; },
            p => { calls.Enqueue(("exists " + Path.GetFileName(p), Environment.CurrentManagedThreadId)); return !p.EndsWith("gone.png"); },
            p => { calls.Enqueue(("size " + Path.GetFileName(p), Environment.CurrentManagedThreadId)); return (640, 480); },
            p => { calls.Enqueue(("thumbnail " + Path.GetFileName(p), Environment.CurrentManagedThreadId)); return null; });
        Check.Equal(0, calls.Count, "making the model reads nothing");
        var load = model.LoadAsync();
        Check.True(load.Wait(5000));
        var items = load.Result;
        Check.Equal(new[] { "a.png", "b.png", "gone.png" }, items.Select(i => Path.GetFileName(i.Entry.Path)).ToArray());
        Check.Equal(new[] { true, true, false }, items.Select(i => i.Exists).ToArray());
        Check.Equal((40, 30), (items[0].Width, items[0].Height), "the history's size is used");
        Check.Equal((640, 480), (items[1].Width, items[1].Height), "an entry without one is read from the PNG header");
        Check.Equal((0, 0), (items[2].Width, items[2].Height), "no header read for a file that is gone");
        var thumb = model.ThumbnailAsync(@"C:\s\a.png");
        Check.True(thumb.Wait(5000));
        Check.Null(thumb.Result);
        Check.Equal(6, calls.Count, "history, 3 file checks, 1 header read (the other two have a size or no file), 1 thumbnail");
        Check.True(calls.All(c => c.Thread != caller), "everything ran on a worker: " + string.Join(", ", calls.Where(c => c.Thread == caller).Select(c => c.What)));
    }

    [UnitTest]
    public static void A_real_png_gets_a_small_frozen_thumbnail_and_its_size_from_the_header()
    {
        using var dir = new TempDir();
        string png = dir.File("t.png");
        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(400, 200, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(png)) enc.Save(fs);
        var model = new ScreenshotListModel(() => new[] { new ScreenshotEntry { Path = png, Size = new FileInfo(png).Length } });
        var items = model.LoadAsync().Result;
        Check.Equal((400, 200), (items[0].Width, items[0].Height));
        var thumb = model.ThumbnailAsync(png).Result;
        Check.NotNull(thumb);
        Check.Equal(176, thumb!.PixelWidth);
        Check.True(thumb.IsFrozen);
    }

    // ------------------------------------------------------------------ history: Remove + Changed

    [UnitTest]
    public static void Remove_takes_the_entry_out_writes_the_file_and_raises_Changed()
    {
        using var dir = new TempDir();
        string file = dir.File("screenshots.json");
        string a = dir.File("a.png"), b = dir.File("b.png");
        File.WriteAllBytes(a, new byte[4]); File.WriteAllBytes(b, new byte[4]);
        var history = new ScreenshotHistory(file, null);
        int changed = 0;
        history.Changed += () => changed++;
        history.Add(new ScreenshotEntry { Path = a });
        history.Add(new ScreenshotEntry { Path = b });
        Check.Equal(2, changed, "a save raises it");
        Check.True(history.Remove(a.ToUpperInvariant()), "case-insensitive, like the rest of the history");
        Check.Equal(3, changed);
        Check.Equal(new[] { b }, history.Entries.Select(e => e.Path).ToArray());
        Check.Equal(new[] { b }, new ScreenshotHistory(file, null).Entries.Select(e => e.Path).ToArray(), "persisted");
        Check.False(history.Remove(a), "no such entry");
        Check.Equal(3, changed, "nothing changed: nothing raised");
        history.Update(history.Entries[0]);
        Check.Equal(4, changed, "a link recorded raises it too");
    }

    [UnitTest]
    public static void Changed_is_raised_outside_the_history_lock()
    {
        using var dir = new TempDir();
        string a = dir.File("a.png");
        File.WriteAllBytes(a, new byte[4]);
        var history = new ScreenshotHistory(dir.File("screenshots.json"), null);
        IReadOnlyList<ScreenshotEntry>? seen = null;
        // read from ANOTHER thread inside the handler: it would deadlock if the lock were still held
        history.Changed += () => seen = Task.Run(() => history.Entries).Result;
        history.Add(new ScreenshotEntry { Path = a });
        Check.Equal(1, seen?.Count);
        history.Remove(a);
        Check.Equal(0, seen?.Count);
    }

    // ------------------------------------------------------------------ delete flow (a made-up Recycle Bin)

    private static void Pump(Task t) => ScreenshotUploadTests.Pump(() => t.IsCompleted, "the task");

    /// <summary>Makes <see cref="ScreenshotService.DriveTypeOf"/> answer <paramref name="type"/> (GetDriveType: 3 fixed, 2 removable, 4 remote, 5 optical) until disposed.</summary>
    private static IDisposable Drive(uint type)
    {
        var old = ScreenshotService.DriveTypeOf;
        ScreenshotService.DriveTypeOf = _ => type;
        return new Restore(() => ScreenshotService.DriveTypeOf = old);
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    [UnitTest]
    public static void Delete_sends_the_file_to_the_recycler_and_the_entry_leaves_the_history()
    {
        using var drive = Drive(3);
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        ScreenshotUploadTests.Capture(rig);
        var entry = rig.Service.HistoryEntries.Single();
        Check.True(File.Exists(entry.Path));
        var recycled = new List<string>();
        nint seenOwner = -1;
        rig.Service.Recycler = (p, o) => { recycled.Add(p); seenOwner = o; File.Delete(p); };   // (no real Recycle Bin in a test)
        // the live "edit last" document of this very file must not outlive it
        rig.Service.LastEdit = new LastEditInfo(DocumentState.Create(new BgraImage(4, 4)), entry.Path, DateTime.UtcNow);
        int raised = 0;
        rig.Service.HistoryChanged += () => Interlocked.Increment(ref raised);
        var t = rig.Service.DeleteAsync(entry, (nint)1234);
        Pump(t);
        Check.Equal(ScreenshotDeleteResult.Deleted, t.Result);
        Check.Equal(new[] { Path.GetFullPath(entry.Path) }, recycled.ToArray(), "recycled (by full path), not deleted by Clink");
        Check.Equal((nint)1234, seenOwner, "the pane's window is the shell's owner");
        Check.Equal(0, rig.Service.HistoryEntries.Count, "the entry is gone");
        Check.True(raised >= 1, "the list is told");
        Check.Null(rig.Service.LastEdit, "the live document of the deleted file was dropped");
    }

    [UnitTest]
    public static void Delete_only_recycles_a_file_on_a_local_fixed_drive()
    {
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        ScreenshotUploadTests.Capture(rig);
        var entry = rig.Service.HistoryEntries.Single();
        var recycled = new List<string>();
        rig.Service.Recycler = (p, _) => { recycled.Add(p); File.Delete(p); };
        // removable (2), remote (4), optical (5), RAM disk-less unknowns (0, 1): the Recycle Bin would delete for good
        foreach (uint type in new uint[] { 0, 1, 2, 4, 5 })
        {
            using var drive = Drive(type);
            var t = rig.Service.DeleteAsync(entry);
            Pump(t);
            Check.Equal(ScreenshotDeleteResult.Refused, t.Result, "drive type " + type);
            Check.Equal(0, recycled.Count, "the recycler is not called, drive type " + type);
            Check.True(File.Exists(entry.Path));
            Check.Equal(1, rig.Service.HistoryEntries.Count);
        }
        Check.False(ScreenshotService.IsOnLocalFixedDrive(@"\\host\share\a.png"), "a network path");
        Check.False(ScreenshotService.IsOnLocalFixedDrive(@"\\?\C:\a.png"), "a device path");
        using (Drive(3))
        {
            Check.True(ScreenshotService.IsOnLocalFixedDrive(@"C:\a.png"));
            var t = rig.Service.DeleteAsync(entry);
            Pump(t);
            Check.Equal(ScreenshotDeleteResult.Deleted, t.Result);
            Check.Equal(new[] { entry.Path }, recycled.ToArray());
        }
        Check.Equal("Delete is only available for screenshots on a local fixed drive", ScreenshotRow.DeleteProblemText(ScreenshotDeleteResult.Refused, onLocalFixedDrive: false));
        Check.Contains(ScreenshotRow.DeleteProblemText(ScreenshotDeleteResult.Failed, true), "could not be moved to the Recycle Bin");
        Check.Contains(ScreenshotRow.DeleteProblemText(ScreenshotDeleteResult.ListNotUpdated, true), "list could not be updated");
        Check.Contains(ScreenshotRow.DeleteProblemText(ScreenshotDeleteResult.EditorOpen, true), "close the editor first");
        string ask = ScreenshotRow.ConfirmText(new ScreenshotEntry { Path = @"C:\s\a.png" }, true);
        Check.Contains(ask, "Move 'a.png' to the Recycle Bin?");
        Check.Contains(ask, "Windows asks again before anything is deleted for good");
    }

    [UnitTest]
    public static void A_network_entry_is_refused_without_the_disk_being_looked_at()
    {
        using var drive = Drive(3);
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        var recycled = new List<string>();
        rig.Service.Recycler = (p, _) => recycled.Add(p);
        var t = rig.Service.DeleteAsync(new ScreenshotEntry { Path = @"\\clink-test-no-such-host\share\x.png" });   // a probe would wait for the network
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Pump(t);
        Check.Equal(ScreenshotDeleteResult.Refused, t.Result);
        Check.Equal(0, recycled.Count);
        Check.True(sw.ElapsedMilliseconds < 2000, "no network access: " + sw.ElapsedMilliseconds + " ms");
    }

    [UnitTest]
    public static void Deleting_a_shared_screenshot_recycles_the_file_but_keeps_the_entry_and_its_link()
    {
        using var drive = Drive(3);
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        ScreenshotUploadTests.Capture(rig);
        var entry = rig.Service.HistoryEntries.Single();
        entry.CopiumLink = "https://v.copium.pics/x.png";   // (the history's own instance)
        rig.Service.Recycler = (p, _) => File.Delete(p);
        var t = rig.Service.DeleteAsync(entry);
        Pump(t);
        Check.Equal(ScreenshotDeleteResult.Deleted, t.Result);
        Check.False(File.Exists(entry.Path));
        var kept = rig.Service.HistoryEntries.Single();
        Check.Equal("https://v.copium.pics/x.png", kept.CopiumLink, "the entry stays for its link (Copy link / Open page)");
        var row = new ScreenshotRow(new ScreenshotItem(kept, false, 0, 0));
        Check.False(row.DeleteVisible);
        Check.True(row.RemoveVisible, "Remove from list takes the place of Delete");
        Check.Equal(0, ScreenshotListModel.Pick(new[] { new ScreenshotItem(kept, false, 0, 0) }, LibraryLevel.Local).Count, "not listed under Local without its file");
        // "Remove from list": the file is gone, so the entry goes
        var t2 = rig.Service.DeleteAsync(kept);
        Pump(t2);
        Check.Equal(ScreenshotDeleteResult.Deleted, t2.Result);
        Check.Equal(0, rig.Service.HistoryEntries.Count);
    }

    [UnitTest]
    public static void The_history_keeps_an_entry_with_a_link_when_its_file_is_gone()
    {
        using var dir = new TempDir();
        string file = dir.File("screenshots.json");
        var history = new ScreenshotHistory(file, null);
        history.Add(new ScreenshotEntry { Path = dir.File("shared-gone.png"), CopiumLink = "https://v.copium.pics/a.png" });
        history.Add(new ScreenshotEntry { Path = dir.File("plain-gone.png") });
        var again = new ScreenshotHistory(file, null).Entries;
        Check.Equal(new[] { "shared-gone.png" }, again.Select(e => Path.GetFileName(e.Path)).ToArray(), "the one with a link is kept, the other is dropped");
    }

    [UnitTest]
    public static void A_row_that_is_uploading_or_deleting_offers_neither_delete_nor_upload_and_an_overwritten_file_drops_its_thumbnail()
    {
        var e = Shot(@"C:\s\a.png", size: 10);
        var row = new ScreenshotRow(new ScreenshotItem(e, true, 4, 4));
        Check.True(row.DeleteVisible);
        row.IsUploading = true;
        Check.False(row.DeleteVisible, "no delete while it uploads (the file is held open)");
        row.IsUploading = false;
        row.IsDeleting = true;
        Check.False(row.DeleteVisible);
        Check.False(row.UploadVisible);
        row.IsDeleting = false;
        row.Thumbnail = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        row.ThumbnailRequested = true;
        row.Update(new ScreenshotItem(Shot(@"C:\s\a.png", size: 10), true, 4, 4));
        Check.NotNull(row.Thumbnail, "same file: the picture stays");
        row.Update(new ScreenshotItem(Shot(@"C:\s\a.png", size: 99), true, 4, 4));   // an editor saved over it
        Check.Null(row.Thumbnail, "another file under the same name: the old picture does not stay up");
        Check.False(row.ThumbnailRequested, "and it is asked for again");
    }

    [UnitTest]
    public static void The_list_never_touches_a_path_that_is_not_a_png_in_a_screenshots_folder()
    {
        var calls = new ConcurrentQueue<string>();
        var entries = new List<ScreenshotEntry>
        {
            Shot(@"\\clink-test-no-such-host\share\a.png"), Shot(@"\\?\UNC\host\share\b.png"), Shot(@"C:\elsewhere\c.png"), Shot(@"C:\s\notes.txt"), Shot(@"C:\s\ok.png"),
        };
        var model = new ScreenshotListModel(() => entries,
            p => { calls.Enqueue("exists " + p); return true; },
            p => { calls.Enqueue("size " + p); return (1, 1); },
            p => { calls.Enqueue("decode " + p); return null; },
            folders: () => new[] { @"C:\s" });
        var items = model.LoadAsync().Result;
        Check.Equal(new[] { @"C:\s\ok.png" }, items.Select(i => i.Entry.Path).ToArray(), "only the entry the path rule allows is listed");
        Check.Equal(new[] { @"exists C:\s\ok.png", @"size C:\s\ok.png" }, calls.ToArray(), "no file check, header read or thumbnail for the others");
    }

    [UnitTest]
    public static void A_row_upload_only_takes_a_png_inside_the_screenshots_folders()
    {
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        string outside = dir.File("outside.png");
        File.WriteAllBytes(outside, new byte[100]);
        string text = ScreenshotUploadTests.EntryFor(dir, "notes.txt").Path;
        var inside = ScreenshotUploadTests.EntryFor(dir, "inside.png");
        foreach (var e in new[] { new ScreenshotEntry { Path = outside, Size = 100 }, new ScreenshotEntry { Path = text, Size = 100 }, new ScreenshotEntry { Path = @"\\clink-test-no-such-host\s\x.png", Size = 100 } })
        {
            var t = rig.Service.UploadAsync(e, manual: true, askAs: ConsentContext.ScreenshotUploadLibrary);
            Pump(t);
            Check.False(t.Result, e.Path);
        }
        Check.Equal(0, rig.Calls.Count, "the uploader was never called");
        var ok = rig.Service.UploadAsync(inside, manual: true, askAs: ConsentContext.ScreenshotUploadLibrary);
        Pump(ok);
        Check.True(ok.Result);
        Check.Equal(1, rig.Calls.Count);
        Check.True(ConsentGate.OffersAutoUpload(ConsentContext.ScreenshotUploadLibrary));
    }

    [UnitTest]
    public static void A_recycler_that_fails_keeps_the_file_and_the_entry()
    {
        using var drive = Drive(3);
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        ScreenshotUploadTests.Capture(rig);
        var entry = rig.Service.HistoryEntries.Single();
        rig.Service.Recycler = (_, _) => throw new IOException("the Recycle Bin is not available");
        var t = rig.Service.DeleteAsync(entry);
        Pump(t);
        Check.Equal(ScreenshotDeleteResult.Failed, t.Result);
        Check.True(File.Exists(entry.Path), "the file stays");
        Check.Equal(1, rig.Service.HistoryEntries.Count, "and the entry");
    }

    [UnitTest]
    public static void An_entry_whose_file_is_gone_is_just_removed_and_a_file_outside_the_screenshot_folders_is_never_touched()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        Settings.DirOverride = dir.Path;
        string outside = other.File("not-a-screenshot.png");
        File.WriteAllBytes(outside, new byte[4]);
        string gone = Path.Combine(dir.Path, "clips", "Screenshots", "gone.png");
        // the history file is written first (a made-up one that names a file in another folder), then the service loads it
        // (written by hand: Add would record its folder as a screenshots folder)
        File.WriteAllText(ScreenshotHistory.DefaultFile, "{ \"Entries\": [ { \"Path\": " + System.Text.Json.JsonSerializer.Serialize(outside) + " } ] }");
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        var recycled = new List<string>();
        rig.Service.Recycler = (p, _) => recycled.Add(p);
        var entry = rig.Service.HistoryEntries.Single(e => e.Path == outside);
        var t = rig.Service.DeleteAsync(entry);
        Pump(t);
        Check.Equal(ScreenshotDeleteResult.Refused, t.Result, "not in a screenshots folder");
        Check.Equal(0, recycled.Count);
        Check.True(File.Exists(outside));
        Check.True(rig.Service.HistoryEntries.Any(e => e.Path == outside), "still listed");
        // a listed entry whose file was deleted outside Clink: no recycler, the entry goes
        var ghost = new ScreenshotEntry { Path = gone };
        var t2 = rig.Service.DeleteAsync(ghost);
        Pump(t2);
        Check.Equal(ScreenshotDeleteResult.Deleted, t2.Result);
        Check.Equal(0, recycled.Count, "nothing to recycle");
    }

    [UnitTest]
    public static void An_entry_whose_path_is_a_directory_is_removed_from_the_list_and_the_disk_is_left_alone()
    {
        using var drive = Drive(3);
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        ScreenshotUploadTests.Capture(rig);
        var real = rig.Service.HistoryEntries.Single();
        string folder = Path.Combine(Path.GetDirectoryName(real.Path)!, "looks-like-a-shot.png");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "keep");
        var recycled = new List<string>();
        rig.Service.Recycler = (p, _) => recycled.Add(p);
        var t = rig.Service.DeleteAsync(new ScreenshotEntry { Path = folder });
        Pump(t);
        Check.Equal(ScreenshotDeleteResult.Deleted, t.Result, "the entry goes (Remove from list)");
        Check.Equal(0, recycled.Count, "the recycler is never called for it");
        Check.True(File.Exists(Path.Combine(folder, "inside.txt")), "and the directory is untouched");
        Check.True(File.Exists(real.Path), "other screenshots too");
    }

    [UnitTest]
    public static void A_thumbnail_decoded_for_the_old_file_is_not_shown_after_the_file_changed()
    {
        var row = new ScreenshotRow(new ScreenshotItem(Shot(@"C:\s\a.png", size: 10), true, 4, 4));
        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        int v = row.ThumbnailVersion;
        row.Update(new ScreenshotItem(Shot(@"C:\s\a.png", size: 10), true, 4, 4));
        Check.True(row.TrySetThumbnail(bmp, v), "an update that left the file alone keeps the decode valid");
        row.Thumbnail = null;
        int old = row.ThumbnailVersion;
        row.Update(new ScreenshotItem(Shot(@"C:\s\a.png", size: 99), true, 4, 4));   // an editor saved over it while the decode ran
        Check.False(row.TrySetThumbnail(bmp, old), "the stale picture is dropped");
        Check.Null(row.Thumbnail);
        Check.True(row.TrySetThumbnail(bmp, row.ThumbnailVersion), "a decode of the new file is shown");
    }

    // ------------------------------------------------------------------ settings

    [UnitTest]
    public static void The_library_choice_is_saved_normalised_labelled_and_importable()
    {
        var s = Settings.FromJson("""{ "LibraryFeature": "Screenshots", "LibraryLevel": "shared" }""");
        Check.Equal("screenshots", s.LibraryFeature);
        Check.Equal("shared", s.LibraryLevel);
        var junk = Settings.FromJson("""{ "LibraryFeature": "elsewhere", "LibraryLevel": null }""");
        Check.Equal("clips", junk.LibraryFeature);
        Check.Equal("local", junk.LibraryLevel);
        var back = Settings.FromJson(s.ToJson());
        Check.Equal(("screenshots", "shared"), (back.LibraryFeature, back.LibraryLevel), "round trip");
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.LibraryFeature)));
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.LibraryLevel)));
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.LibraryFeature)), "importable");
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.LibraryLevel)), "importable");
    }
}
