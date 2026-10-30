using System.IO;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

// The Delete button of a clip row: ClipDeleter is plain code (no window), so the rules are checked here with a made-up Recycle Bin.
// The pop-up and the pane are covered by the smoke run.
internal static class ClipDeleteTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly string Clips, Archive;
        public readonly ClipLibrary Library = new();
        public readonly ClipDeleter Deleter;
        public readonly List<string> Recycled = new();
        private readonly string? _oldIndex = ClipLibrary.IndexPathOverride;
        public int Changed;
        public bool Fail, TrimOpen;

        public Rig(bool archive = true)
        {
            Clips = Dir.File("clips");
            Archive = Dir.File("archive");
            Directory.CreateDirectory(Clips);
            Directory.CreateDirectory(Archive);
            ClipLibrary.IndexPathOverride = Dir.File("clips.json");
            Library.Changed += () => Changed++;
            Deleter = new ClipDeleter(Library, () => Clips, () => archive ? Archive : null)
            {
                Recycler = (p, _) => { if (Fail) throw new IOException("shell refused"); Recycled.Add(p); File.Delete(p); },
                IsFixedDrive = _ => true,
                IsTrimOpen = _ => TrimOpen,
            };
        }

        public ClipEntry Clip(string path, bool shared = false, bool trim = false, string? source = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var e = Library.Add(path, 5, 3, "Game", Array.Empty<string>(), isTrim: trim, sourcePath: source);
            if (shared) e.CopiumLink = "https://copium.pics/x";
            return e;
        }

        public ClipDeleteResult Delete(ClipEntry e)
        {
            var t = Deleter.DeleteAsync(e, (nint)77);
            ScreenshotUploadTests.Pump(() => t.IsCompleted, "the delete");
            return t.Result;
        }

        public void Dispose() { ClipLibrary.IndexPathOverride = _oldIndex; Dir.Dispose(); }
    }

    [UnitTest]
    public static void A_local_clip_goes_to_the_recycler_and_its_entry_leaves_the_library()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        int before = rig.Changed;
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(e));
        Check.Equal(new[] { Path.GetFullPath(e.Path) }, rig.Recycled.ToArray(), "recycled by full path, never deleted by Clink");
        Check.Equal(0, rig.Library.Entries.Count, "the entry is gone");
        Check.True(rig.Changed > before, "the list is told");
    }

    [UnitTest]
    public static void A_shared_clip_keeps_its_entry_and_its_link()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Clips, "s.mp4"), shared: true);
        Check.True(ClipDeleter.KeepEntry(e));
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(e));
        Check.Equal(1, rig.Recycled.Count);
        Check.False(File.Exists(e.Path));
        var left = rig.Library.Entries.Single();
        Check.Equal("https://copium.pics/x", left.CopiumLink);
        Check.False(ClipDeleter.KeepEntry(new ClipEntry { Path = "x.mp4" }), "an unshared clip leaves");
    }

    [UnitTest]
    public static void A_clip_in_a_subfolder_or_in_the_archive_folder_may_be_deleted()
    {
        using var rig = new Rig();
        var sub = rig.Clip(Path.Combine(rig.Clips, "Recordings", "r.mp4"));
        var arch = rig.Clip(Path.Combine(rig.Archive, "old.mp4"));
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(sub));
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(arch));
        Check.Equal(2, rig.Recycled.Count);
    }

    [UnitTest]
    public static void A_path_outside_the_clips_and_archive_folders_or_not_an_mp4_is_refused_untouched()
    {
        using var rig = new Rig(archive: false);
        var outside = rig.Clip(Path.Combine(rig.Dir.Path, "elsewhere", "o.mp4"));
        var notMp4 = rig.Clip(Path.Combine(rig.Clips, "n.mkv"));
        var climb = rig.Clip(Path.Combine(rig.Clips, "..", "elsewhere", "c.mp4"));   // resolves outside
        var archived = rig.Clip(Path.Combine(rig.Archive, "a.mp4"));   // no archive folder configured
        foreach (var e in new[] { outside, notMp4, climb, archived })
        {
            Check.Equal(ClipDeleteResult.Refused, rig.Delete(e), e.Path);
            Check.True(File.Exists(e.Path), "the file stays: " + e.Path);
        }
        Check.Equal(0, rig.Recycled.Count, "nothing was recycled");
        Check.Equal(4, rig.Library.Entries.Count, "no entry was touched");
        Check.False(ClipDeleter.IsAllowedPath(@"\\host\share\clips\a.mp4", @"\\host\share\clips", null), "a network path");
        Check.False(ClipDeleter.IsAllowedPath(Path.Combine(rig.Clips, "a.mp4:evil.exe"), rig.Clips, null), "an alternate stream");
        Check.False(ClipDeleter.IsAllowedPath(rig.Clips + ".mp4", rig.Clips, null), "a sibling that only shares the prefix");
        Check.False(ClipDeleter.IsAllowedPath("a.mp4", rig.Clips, null), "a relative path");
    }

    [UnitTest]
    public static void A_file_off_a_local_fixed_drive_is_refused_and_never_deleted_for_good()
    {
        using var rig = new Rig();
        rig.Deleter.IsFixedDrive = _ => false;
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        Check.Equal(ClipDeleteResult.NotLocal, rig.Delete(e));
        Check.Equal(0, rig.Recycled.Count);
        Check.True(File.Exists(e.Path));
        Check.Equal(1, rig.Library.Entries.Count);
    }

    [UnitTest]
    public static void A_clip_on_an_unavailable_drive_keeps_its_entry_even_though_the_file_cannot_be_seen()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Archive, "a.mp4"));
        rig.Deleter.IsFixedDrive = _ => false;   // GetDriveType of an offline mapped drive or an unplugged disk
        rig.Deleter.Probe = _ => (ClipDeleter.FileProbe.Unavailable, 0);   // and Exists would say false
        Check.Equal(ClipDeleteResult.NotLocal, rig.Delete(e));
        Check.Equal(0, rig.Recycled.Count);
        Check.Equal(1, rig.Library.Entries.Count, "the entry is kept");
        Check.Contains(ClipDeleter.ProblemText(ClipDeleteResult.NotLocal), "network or removable drive");
    }

    [UnitTest]
    public static void A_clip_whose_folder_is_missing_keeps_its_entry()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Archive, "Sub", "a.mp4"));
        Directory.Delete(Path.Combine(rig.Archive, "Sub"), true);
        Check.Equal(ClipDeleteResult.Unreachable, rig.Delete(e));
        Check.Equal(0, rig.Recycled.Count);
        Check.Equal(1, rig.Library.Entries.Count, "unknown whether the file is gone: the entry stays");
    }

    [UnitTest]
    public static void A_file_missing_from_a_folder_that_is_there_is_gone_and_a_shared_entry_stays()
    {
        using var rig = new Rig();
        var plain = rig.Clip(Path.Combine(rig.Clips, "p.mp4"));
        var shared = rig.Clip(Path.Combine(rig.Clips, "s.mp4"), shared: true);
        File.Delete(plain.Path);
        File.Delete(shared.Path);
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(plain));
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(shared));
        Check.Equal(0, rig.Recycled.Count);
        Check.Equal(shared.Path, rig.Library.Entries.Single().Path, "the shared entry keeps its link");
    }

    [UnitTest]
    public static void A_clip_that_is_uploading_is_not_deleted()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        rig.Deleter.IsUploading = x => ReferenceEquals(x, e);
        Check.Equal(ClipDeleteResult.Uploading, rig.Delete(e));
        Check.Equal(0, rig.Recycled.Count);
        Check.True(File.Exists(e.Path));
        Check.Contains(ClipDeleter.ProblemText(ClipDeleteResult.Uploading), "uploading");
    }

    [UnitTest]
    public static void Only_cloud_placeholder_reparse_points_may_be_deleted()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        rig.Deleter.Probe = p => (ClipDeleter.FileProbe.Present, FileAttributes.Archive | FileAttributes.ReparsePoint);
        foreach (uint tag in new uint[] { 0x9000001A, 0x9000101A, 0x9000F01A })   // IO_REPARSE_TAG_CLOUD, CLOUD_1, CLOUD_F
        {
            Check.True(ClipDeleter.IsCloudTag(tag), "cloud tag 0x" + tag.ToString("X"));
        }
        foreach (uint tag in new uint[] { 0xA000000C, 0xA0000003, 0x80000023, 0 })   // symlink, mount point, app exec link, unreadable
        {
            Check.False(ClipDeleter.IsCloudTag(tag), "not cloud: 0x" + tag.ToString("X"));
            rig.Deleter.ReparseTagOf = _ => tag;
            Check.Equal(ClipDeleteResult.Link, rig.Delete(e), "0x" + tag.ToString("X"));
            Check.Equal(1, rig.Library.Entries.Count);
        }
        Check.Equal(0, rig.Recycled.Count);
        rig.Deleter.ReparseTagOf = _ => 0x9000701A;
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(e));
        Check.Equal(1, rig.Recycled.Count);
        Check.Contains(ClipDeleter.ProblemText(ClipDeleteResult.Link), "shortcut to another location");
    }

    [UnitTest]
    public static void A_folder_that_is_a_drive_root_or_has_a_trailing_separator_matches()
    {
        Check.True(ClipDeleter.IsAllowedPath(@"X:\a.mp4", @"X:\", null), "drive-root clips folder");
        Check.True(ClipDeleter.IsAllowedPath(@"X:\Sub\a.mp4", null, @"X:\"), "drive-root archive folder");
        Check.True(ClipDeleter.IsAllowedPath(@"X:\Clips\a.mp4", @"X:\Clips\", null), "trailing separator");
        Check.False(ClipDeleter.IsAllowedPath(@"X:\Clips2\a.mp4", @"X:\Clips\", null), "a sibling");
        Check.False(ClipDeleter.IsAllowedPath(@"Y:\a.mp4", @"X:\", null), "another drive");
    }

    [UnitTest]
    public static void The_delete_button_template_keeps_the_glyph_font_and_follows_the_buttons_foreground()
    {
        var app = System.Windows.Application.Current ?? throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var test = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.HotPink);
        var button = new System.Windows.Controls.Button
        {
            Style = (System.Windows.Style)app.Resources["DangerGlyphButton"],
            Content = "\uE74D",
            Foreground = test,
        };
        button.ApplyTemplate();
        button.Measure(new System.Windows.Size(200, 100));
        button.Arrange(new System.Windows.Rect(0, 0, 200, 100));
        button.UpdateLayout();
        System.Windows.Controls.TextBlock? tb = null;
        void Find(System.Windows.DependencyObject d)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d) && tb == null; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                if (c is System.Windows.Controls.TextBlock found) tb = found; else Find(c);
            }
        }
        Find(button);
        Check.NotNull(tb, "the glyph is drawn by a TextBlock");
        Check.Equal(button.FontFamily.Source, tb!.FontFamily.Source, "the glyph keeps the button icon font, not the app-wide text font");
        Check.True(ReferenceEquals(test, tb.Foreground), "the glyph follows the button Foreground (so the hover colour reaches it)");
    }

    [UnitTest]
    public static void A_failed_recycle_keeps_the_file_and_the_entry_and_says_so()
    {
        using var rig = new Rig();
        rig.Fail = true;
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        Check.Equal(ClipDeleteResult.Failed, rig.Delete(e));
        Check.True(File.Exists(e.Path));
        Check.Equal(1, rig.Library.Entries.Count);
        Check.Contains(ClipDeleter.ProblemText(ClipDeleteResult.Failed), "move this clip to the Recycle Bin");
    }

    [UnitTest]
    public static void Every_problem_text_is_a_sentence_ending_in_a_period()
    {
        foreach (var r in Enum.GetValues<ClipDeleteResult>())
        {
            if (r == ClipDeleteResult.Deleted) continue;
            string t = ClipDeleter.ProblemText(r);
            Check.True(t.Length > 0 && t.EndsWith('.'), r + ": " + t);
        }
    }

    [UnitTest]
    public static void A_network_path_is_not_local_before_anything_is_asked_and_nothing_is_recycled()
    {
        using var rig = new Rig();
        var e = new ClipEntry { Path = @"\\nas\clips\archive\a.mp4" };
        Check.Equal(ClipDeleteResult.NotLocal, rig.Deleter.Precheck(e), "UNC, even for an archive folder on the share");
        var unc = new ClipDeleter(rig.Library, () => rig.Clips, () => @"\\nas\clips\archive") { Recycler = (p, _) => rig.Recycled.Add(p), IsFixedDrive = _ => true };
        Check.Equal(ClipDeleteResult.NotLocal, unc.Precheck(e));
        Check.Equal(ClipDeleteResult.NotLocal, rig.Delete(e));
        Check.Equal(0, rig.Recycled.Count);
        var local = rig.Clip(Path.Combine(rig.Clips, "m.mp4"));
        rig.Deleter.IsFixedDrive = _ => false;   // a mapped or removable drive is refused before the pop-up too
        Check.Equal(ClipDeleteResult.NotLocal, rig.Deleter.Precheck(local));
    }

    [UnitTest]
    public static void A_path_with_a_wildcard_is_not_allowed()
    {
        Check.False(ClipDeleter.IsAllowedPath(@"X:\Clips\*.mp4", @"X:\Clips", null), "star");
        Check.False(ClipDeleter.IsAllowedPath(@"X:\Clips\a?.mp4", @"X:\Clips", null), "question mark");
        Check.True(ClipDeleter.IsAllowedPath(@"X:\Clips\a.mp4", @"X:\Clips", null));
    }

    [UnitTest]
    public static void A_clip_the_archive_moved_during_the_delete_keeps_its_entry()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        string moved = Path.Combine(rig.Archive, "a.mp4");
        rig.Deleter.Recycler = (p, _) => { e.Path = moved; };   // the archive service moves it while the shell call runs
        Check.Equal(ClipDeleteResult.Unreachable, rig.Delete(e));
        Check.Equal(1, rig.Library.Entries.Count, "the entry stays with the file that survived");
    }

    [UnitTest]
    public static void A_clip_open_in_the_trim_window_is_not_deleted()
    {
        using var rig = new Rig();
        rig.TrimOpen = true;
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        Check.Equal(ClipDeleteResult.TrimOpen, rig.Delete(e));
        Check.Equal(ClipDeleteResult.TrimOpen, rig.Deleter.Precheck(e));
        Check.True(File.Exists(e.Path));
        Check.Equal(0, rig.Recycled.Count);
        Check.Contains(ClipDeleter.ProblemText(ClipDeleteResult.TrimOpen), "Close the trim window");
        rig.TrimOpen = false;
        Check.Null(rig.Deleter.Precheck(e));
    }

    [UnitTest]
    public static void A_clip_whose_file_is_already_gone_just_leaves_the_library()
    {
        using var rig = new Rig();
        var e = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        File.Delete(e.Path);
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(e));
        Check.Equal(0, rig.Recycled.Count);
        Check.Equal(0, rig.Library.Entries.Count);
    }

    [UnitTest]
    public static void The_confirmation_is_asked_until_the_box_was_ticked_and_cancel_never_saves_it()
    {
        var s = new Settings();
        Check.False(s.ClipDeleteConfirmDismissed, "off by default");
        Check.True(ClipDeleter.ConfirmNeeded(s));
        Check.True(ClipDeleter.Remember(confirmed: true, doNotAskAgain: true));
        Check.False(ClipDeleter.Remember(confirmed: true, doNotAskAgain: false));
        Check.False(ClipDeleter.Remember(confirmed: false, doNotAskAgain: true), "Cancel with the box ticked saves nothing");
        s.ClipDeleteConfirmDismissed = true;
        Check.False(ClipDeleter.ConfirmNeeded(s), "skipped once dismissed");
    }

    [UnitTest]
    public static void The_confirmation_text_names_the_trims_that_stay_and_the_link_that_keeps_working()
    {
        string plain = ClipDeleter.ConfirmBody("Game", false, false);
        Check.Contains(plain, "Game will be moved to the Recycle Bin");
        Check.False(plain.Contains("trims"), "no trims line");
        Check.False(plain.Contains("link"), "no link line");
        Check.Contains(ClipDeleter.ConfirmBody("Game", true, false), "trims stay");
        Check.Contains(ClipDeleter.ConfirmBody("Game", false, true), "shared link keeps working");
    }

    [UnitTest]
    public static void Reset_dont_show_again_clears_the_delete_flag_and_the_import_ignores_it()
    {
        var s = new Settings { ClipDeleteConfirmDismissed = true, CaptureBorderNoticeDismissed = true };
        AppController.ClearDismissedFlags(s);
        Check.False(s.ClipDeleteConfirmDismissed);
        Check.False(s.CaptureBorderNoticeDismissed);
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.ClipDeleteConfirmDismissed)), "not offered as an import difference");
    }

    [UnitTest]
    public static void The_trims_of_a_deleted_clip_become_top_level_rows()
    {
        using var rig = new Rig();
        var parent = rig.Clip(Path.Combine(rig.Clips, "p.mp4"));
        var trim = rig.Clip(Path.Combine(rig.Clips, "p (trim).mp4"), trim: true, source: parent.Path);
        Check.True(ClipDeleter.HasTrims(rig.Library.Entries, parent));
        Check.False(ClipDeleter.HasTrims(rig.Library.Entries, trim));
        var before = ClipGrouping.Flatten(rig.Library.Entries, e => e, ClipGrouping.NewExpanded());
        Check.Equal(1, before.Count, "nested under its clip");
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(parent));
        var left = rig.Library.Entries;
        Check.Equal(1, left.Count);
        Check.Equal(parent.Path, left[0].SourcePath, "the trim's source is not rewritten");
        var after = ClipGrouping.Flatten(left, e => e, ClipGrouping.NewExpanded());
        Check.Equal(1, after.Count);
        Check.Equal(ClipRowKind.Plain, after[0].Kind, "now a top-level row");
        Check.True(File.Exists(trim.Path), "the trim file is untouched");
    }

    // ---- delete the clip and its trims ----

    private static (ClipEntry Parent, ClipEntry[] Trims) Family(Rig rig, int trims, bool trimOfTrim = false)
    {
        var parent = rig.Clip(Path.Combine(rig.Clips, "p.mp4"));
        var list = new List<ClipEntry>();
        for (int i = 1; i <= trims; i++) list.Add(rig.Clip(Path.Combine(rig.Clips, $"p (trim {i}).mp4"), trim: true, source: parent.Path));
        if (trimOfTrim) list.Add(rig.Clip(Path.Combine(rig.Clips, "p (trim 1) (trim).mp4"), trim: true, source: list[0].Path));
        return (parent, list.ToArray());
    }

    private static List<ClipEntry> Under(Rig rig, ClipEntry parent) => ClipGrouping.TrimsOf(rig.Library.Entries, e => e, parent);

    private static ClipDeleter.TreeOutcome DeleteTree(Rig rig, ClipEntry parent, IReadOnlyList<ClipEntry> trims)
    {
        var t = rig.Deleter.DeleteWithTrimsAsync(parent, trims, (nint)77);
        ScreenshotUploadTests.Pump(() => t.IsCompleted, "the delete with trims");
        return t.Result;
    }

    [UnitTest]
    public static void The_trims_mode_is_chosen_only_for_a_row_with_nested_trims()
    {
        using var rig = new Rig();
        var (parent, trims) = Family(rig, 2);
        var lone = rig.Clip(Path.Combine(rig.Clips, "lone.mp4"));
        Check.True(ClipDeleter.TrimsMode(Under(rig, parent).Count));
        Check.False(ClipDeleter.TrimsMode(Under(rig, lone).Count), "a plain clip");
        Check.False(ClipDeleter.TrimsMode(Under(rig, trims[0]).Count), "a trim has none");
        Check.False(ClipDeleter.TrimsMode(0));
    }

    [UnitTest]
    public static void A_dismissed_confirmation_does_not_skip_the_pop_up_for_a_clip_with_trims()
    {
        var s = new Settings { ClipDeleteConfirmDismissed = true };
        Check.False(ClipDeleter.ConfirmNeeded(s, 0), "a plain clip still skips");
        Check.False(ClipDeleter.ConfirmNeeded(s), "the default is the plain clip");
        Check.True(ClipDeleter.ConfirmNeeded(s, 1), "one trim asks");
        Check.True(ClipDeleter.ConfirmNeeded(s, 3), "three trims ask");
    }

    [UnitTest]
    public static void Clip_and_trims_recycles_every_trim_then_the_clip_and_removes_the_entries()
    {
        using var rig = new Rig();
        var (parent, trims) = Family(rig, 2);
        var outcome = DeleteTree(rig, parent, Under(rig, parent));
        Check.Equal(ClipDeleteResult.Deleted, outcome.Result);
        Check.Equal(3, outcome.Deleted);
        Check.Equal(3, outcome.Total);
        Check.Equal(3, rig.Recycled.Count);
        Check.Equal(Path.GetFullPath(parent.Path), rig.Recycled[2], "the clip goes last");
        Check.True(rig.Recycled.Take(2).All(p => trims.Any(t => string.Equals(Path.GetFullPath(t.Path), p, StringComparison.OrdinalIgnoreCase))), "the trims go first");
        Check.Equal(0, rig.Library.Entries.Count, "no entry is left");
    }

    [UnitTest]
    public static void Clip_and_trims_keeps_the_entry_of_a_shared_clip_or_trim()
    {
        using var rig = new Rig();
        var (parent, trims) = Family(rig, 2);
        parent.CopiumLink = "https://copium.pics/p";
        trims[1].CopiumLink = "https://copium.pics/t";
        Check.Equal(ClipDeleteResult.Deleted, DeleteTree(rig, parent, Under(rig, parent)).Result);
        Check.Equal(3, rig.Recycled.Count);
        var left = rig.Library.Entries;
        Check.Equal(2, left.Count, "the two shared entries stay for their links");
        Check.True(left.All(e => e.IsShared));
    }

    [UnitTest]
    public static void A_trim_of_a_trim_shown_under_the_clip_is_deleted_with_it()
    {
        using var rig = new Rig();
        var (parent, trims) = Family(rig, 1, trimOfTrim: true);
        var under = Under(rig, parent);
        Check.Equal(2, under.Count, "both trims sit under the root");
        Check.Equal(ClipDeleteResult.Deleted, DeleteTree(rig, parent, under).Result);
        Check.Equal(3, rig.Recycled.Count);
        Check.Equal(0, rig.Library.Entries.Count);
        Check.False(File.Exists(trims[1].Path), "the trim of the trim is gone");
    }

    [UnitTest]
    public static void Clip_and_trims_stops_at_the_first_failure_and_says_how_many_went()
    {
        using var rig = new Rig();
        var (parent, _) = Family(rig, 2);
        var under = Under(rig, parent);
        // the second trim's file is a folder: that delete is refused when it is reached (after the first trim has gone)
        string second = under[1].Path;
        rig.Deleter.Probe = p => string.Equals(p, second, StringComparison.OrdinalIgnoreCase)
            ? (ClipDeleter.FileProbe.Present, FileAttributes.Directory)
            : ClipDeleter.DefaultProbe(p);
        var outcome = DeleteTree(rig, parent, under);
        Check.Equal(ClipDeleteResult.Refused, outcome.Result);
        Check.Equal(1, outcome.Deleted);
        Check.Equal(3, outcome.Total);
        Check.Equal(1, rig.Recycled.Count, "only the first trim went");
        Check.True(File.Exists(parent.Path), "the clip is untouched");
        Check.True(File.Exists(second), "the failing trim is untouched");
        Check.Equal(2, rig.Library.Entries.Count, "the deleted trim's entry left, the others stay");
        string text = ClipDeleter.TreeProblemText(outcome);
        Check.True(outcome.OnTrim, "the failing file was a trim");
        Check.Equal("Deleted 1 of 3 files. One of its trims is outside the clips and archive folders. The clip and the remaining trims were kept.", text);
    }

    [UnitTest]
    public static void The_problem_text_names_the_trim_when_a_trim_failed_and_the_clip_when_the_clip_failed()
    {
        using var rig = new Rig();
        var (parent, _) = Family(rig, 2);
        // a trim's shell call fails after one trim went
        int calls = 0;
        var inner = rig.Deleter.Recycler;
        rig.Deleter.Recycler = (p, o) => { if (++calls == 2) throw new IOException("shell refused"); inner(p, o); };
        var trimFail = DeleteTree(rig, parent, Under(rig, parent));
        Check.True(trimFail.OnTrim);
        Check.Equal("Deleted 1 of 3 files. One of its trims couldn't be moved to the Recycle Bin. The clip and the remaining trims were kept.", ClipDeleter.TreeProblemText(trimFail));
        Check.Equal("Nothing was deleted: one of its trims is uploading.", ClipDeleter.TreeProblemText(new ClipDeleter.TreeOutcome(ClipDeleteResult.Uploading, 0, 3, OnTrim: true)));

        // the clip's own shell call fails after both trims went
        using var rig2 = new Rig();
        var (parent2, _) = Family(rig2, 2);
        var inner2 = rig2.Deleter.Recycler;
        int calls2 = 0;
        rig2.Deleter.Recycler = (p, o) => { if (++calls2 == 3) throw new IOException("shell refused"); inner2(p, o); };
        var clipFail = DeleteTree(rig2, parent2, Under(rig2, parent2));
        Check.False(clipFail.OnTrim);
        Check.Equal(2, clipFail.Deleted);
        Check.Equal("Deleted 2 of 3 files. The clip couldn't be moved to the Recycle Bin, so it was kept.", ClipDeleter.TreeProblemText(clipFail));
        Check.Equal("Nothing was deleted: the clip is open in the trim window.", ClipDeleter.TreeProblemText(new ClipDeleter.TreeOutcome(ClipDeleteResult.TrimOpen, 0, 3)));
    }

    [UnitTest]
    public static void Clip_and_trims_stops_when_the_clip_is_blocked_after_the_pop_up_and_touches_nothing()
    {
        using var rig = new Rig();
        var (parent, _) = Family(rig, 2);
        var under = Under(rig, parent);
        rig.Deleter.IsTrimOpen = e => e == parent;   // the parent's trim window was opened while the pop-up was up
        var outcome = DeleteTree(rig, parent, under);
        Check.Equal(ClipDeleteResult.TrimOpen, outcome.Result);
        Check.Equal(0, outcome.Deleted);
        Check.False(outcome.OnTrim, "it is the clip that is blocked");
        Check.Equal(0, rig.Recycled.Count);
        Check.Equal(3, rig.Library.Entries.Count, "all entries are left");
        Check.True(File.Exists(parent.Path) && under.All(t => File.Exists(t.Path)), "all files are left");
    }

    [UnitTest]
    public static void A_trim_whose_file_is_already_gone_counts_as_deleted()
    {
        using var rig = new Rig();
        var (parent, _) = Family(rig, 2);
        var under = Under(rig, parent);
        File.Delete(under[0].Path);
        var outcome = DeleteTree(rig, parent, under);
        Check.Equal(ClipDeleteResult.Deleted, outcome.Result);
        Check.Equal(3, outcome.Deleted);
        Check.Equal(3, outcome.Total);
        Check.Equal(2, rig.Recycled.Count, "only the two files that were there went to the bin");
        Check.Equal(0, rig.Library.Entries.Count);
    }

    [UnitTest]
    public static void The_trims_of_one_clip_do_not_include_the_trims_of_another()
    {
        using var rig = new Rig();
        var a = rig.Clip(Path.Combine(rig.Clips, "a.mp4"));
        var a1 = rig.Clip(Path.Combine(rig.Clips, "a (trim).mp4"), trim: true, source: a.Path);
        var b = rig.Clip(Path.Combine(rig.Clips, "b.mp4"));
        var b1 = rig.Clip(Path.Combine(rig.Clips, "b (trim).mp4"), trim: true, source: b.Path);
        var b2 = rig.Clip(Path.Combine(rig.Clips, "b (trim 2).mp4"), trim: true, source: b.Path);
        var underA = Under(rig, a);
        Check.Equal(1, underA.Count);
        Check.True(ReferenceEquals(underA[0], a1), "A's own trim");
        var underB = Under(rig, b);
        Check.Equal(2, underB.Count);
        Check.True(underB.All(t => ReferenceEquals(t, b1) || ReferenceEquals(t, b2)), "only B's trims");
    }

    [UnitTest]
    public static void The_trims_mode_pop_up_defaults_to_cancel_and_turns_the_blocked_option_off_with_the_reason()
    {
        if (System.Windows.Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var open = new ClipDeleteConfirmWindow("Body", null, 2, null);
        Check.True(open.Choice == ClipDeleteChoice.Cancel, "Cancel until a button is pressed");
        Check.False(open.DoNotAskAgain);
        Check.True(open.DoNotAskCheck.Visibility == System.Windows.Visibility.Collapsed, "no Do not ask again in the trims mode");
        Check.True(open.WithTrimsButton.IsEnabled, "enabled when nothing blocks");
        Check.Equal("Delete clip and 2 trims", open.WithTrimsButton.Content as string);

        string reason = ClipDeleter.TrimsBlockedText(ClipDeleteResult.Uploading)!;
        var blocked = new ClipDeleteConfirmWindow("Body", null, 2, reason);
        Check.True(blocked.Choice == ClipDeleteChoice.Cancel);
        Check.False(blocked.WithTrimsButton.IsEnabled, "the with-trims button is off");
        Check.Equal(reason, System.Windows.Automation.AutomationProperties.GetHelpText(blocked.WithTrimsButton));
        Check.Equal(reason, blocked.BlockedText.Text);
        Check.True(blocked.ClipOnlyButton.IsEnabled, "the clip alone still goes");
    }

    [UnitTest]
    public static void A_shell_failure_on_a_trim_leaves_the_clip_and_the_rest()
    {
        using var rig = new Rig();
        var (parent, _) = Family(rig, 2);
        rig.Fail = true;
        var outcome = DeleteTree(rig, parent, Under(rig, parent));
        Check.Equal(ClipDeleteResult.Failed, outcome.Result);
        Check.Equal(0, outcome.Deleted);
        Check.Equal(0, rig.Recycled.Count);
        Check.Equal(3, rig.Library.Entries.Count);
        Check.True(File.Exists(parent.Path));
    }

    [UnitTest]
    public static void A_blocked_trim_disables_the_with_trims_option_with_a_one_line_reason_and_the_clip_alone_still_goes()
    {
        using var rig = new Rig();
        var (parent, trims) = Family(rig, 2);
        var under = Under(rig, parent);
        Check.True(rig.Deleter.PrecheckTrims(under) == null, "nothing blocks");
        Check.True(ClipDeleter.TrimsBlockedText(null) == null, "null when nothing is blocked");
        Check.Equal("Its trims can't be deleted right now: one is already being deleted.", ClipDeleter.TrimsBlockedText(null, oneDeleting: true));

        rig.Deleter.IsTrimOpen = e => e == under[1];
        var open = rig.Deleter.PrecheckTrims(under);
        Check.Equal(ClipDeleteResult.TrimOpen, open);
        Check.Equal("Its trims can't be deleted right now: one is open in the trim window.", ClipDeleter.TrimsBlockedText(open));
        var blocked = DeleteTree(rig, parent, under);
        Check.Equal(ClipDeleteResult.TrimOpen, blocked.Result);
        Check.Equal(0, rig.Recycled.Count, "nothing is touched when one trim is blocked");

        rig.Deleter.IsTrimOpen = _ => false;
        rig.Deleter.IsUploading = e => e == under[0];
        var up = rig.Deleter.PrecheckTrims(under);
        Check.Equal(ClipDeleteResult.Uploading, up);
        Check.Contains(ClipDeleter.TrimsBlockedText(up) ?? "", "one is uploading.");

        rig.Deleter.IsUploading = _ => false;
        rig.Deleter.IsFixedDrive = p => !string.Equals(p, under[0].Path, StringComparison.OrdinalIgnoreCase);
        var net = rig.Deleter.PrecheckTrims(under);
        Check.Equal(ClipDeleteResult.NotLocal, net);
        Check.Contains(ClipDeleter.TrimsBlockedText(net) ?? "", "network or removable drive.");

        foreach (var r in Enum.GetValues<ClipDeleteResult>())
        {
            if (r == ClipDeleteResult.Deleted) continue;
            string line = ClipDeleter.TrimsBlockedText(r) ?? "";
            Check.True(line.StartsWith("Its trims can't be deleted right now: ") && line.EndsWith(".") && line.Count(c => c == '.') == 1, "one sentence for " + r);
        }

        // the clip alone is the ordinary delete: it still works while a trim is open, and the trims stay
        rig.Deleter.IsFixedDrive = _ => true;
        rig.Deleter.IsTrimOpen = e => e == under[1];
        Check.Equal(ClipDeleteResult.Deleted, rig.Delete(parent));
        Check.True(File.Exists(trims[0].Path) && File.Exists(trims[1].Path), "the trims stay");
    }

    [UnitTest]
    public static void The_trims_body_says_1_trim_or_2_trims_and_keeps_the_link_line()
    {
        string one = ClipDeleter.ConfirmBodyWithTrims("Game", 1, false);
        Check.Contains(one, "Game has 1 trim.");
        Check.Contains(one, "the clip and its trim?");
        Check.Contains(one, "Everything goes to the Recycle Bin, where you can restore it.");
        Check.False(one.Contains("link"), "no link line when not shared");
        string two = ClipDeleter.ConfirmBodyWithTrims("Game", 2, true);
        Check.Contains(two, "Game has 2 trims.");
        Check.Contains(two, "the clip and its trims?");
        Check.Contains(two, "The shared link keeps working.");
    }
}
