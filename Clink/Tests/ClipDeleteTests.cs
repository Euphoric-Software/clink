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
}
