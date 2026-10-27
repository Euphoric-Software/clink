using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

// Trimmed clips nested under the full-length clip they were cut from (Local level, Trimmed chip off): ClipGrouping is pure, so
// every rule is checked here. The pane (chevron, indent, scroll position) is covered by the smoke run.
internal static class NestedTrimTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    private static ClipEntry Clip(string path, int minutes, bool trim = false, string? source = null, bool recording = false) =>
        new() { Path = path, Created = T0.AddMinutes(minutes), IsTrim = trim, SourcePath = source, IsRecording = recording };

    private static List<ClipEntry> Newest(params ClipEntry[] entries) => entries.OrderByDescending(e => e.Created).ToList();

    private static List<ClipListItem<ClipEntry>> Flat(IReadOnlyList<ClipEntry> list, params string[] expanded) =>
        ClipGrouping.Flatten(list, e => e, ClipGrouping.NewExpanded(expanded));

    private static string[] Shown(IEnumerable<ClipListItem<ClipEntry>> items) => items.Select(i => System.IO.Path.GetFileName(i.Item.Path)).ToArray();

    private const string A = @"C:\Clips\Game A.mp4";

    [UnitTest]
    public static void A_trim_is_listed_under_the_clip_it_was_cut_from()
    {
        var a = Clip(A, 0);
        var t = Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: true, source: A);
        var list = Newest(a, t);
        var collapsed = Flat(list);
        Check.Equal(new[] { "Game A.mp4" }, Shown(collapsed), "collapsed: only the clip");
        Check.Equal(ClipRowKind.Parent, collapsed[0].Kind);
        Check.Equal(1, collapsed[0].ChildCount);
        Check.False(collapsed[0].Expanded);
        var open = Flat(list, A);
        Check.Equal(new[] { "Game A.mp4", "Game A (trim).mp4" }, Shown(open), "expanded: the trim directly beneath");
        Check.Equal(ClipRowKind.Child, open[1].Kind);
        Check.Equal(1, open[1].Depth);
        Check.Equal(0, open[0].Depth);
        Check.True(open[0].Expanded);
        Check.Equal(open[0].GroupKey, open[1].GroupKey, "the child names its parent");
    }

    [UnitTest]
    public static void A_trim_of_a_trim_goes_under_the_root_and_several_trims_are_newest_first()
    {
        var a = Clip(A, 0);
        var t1 = Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: true, source: A);
        var t2 = Clip(@"C:\Clips\Game A (trim) (trim).mp4", 9, trim: true, source: t1.Path);   // cut from the first trim
        var t3 = Clip(@"C:\Clips\Game A (trim 2).mp4", 7, trim: true, source: A);
        var open = Flat(Newest(a, t1, t2, t3), A);
        Check.Equal(new[] { "Game A.mp4", "Game A (trim) (trim).mp4", "Game A (trim 2).mp4", "Game A (trim).mp4" }, Shown(open), "all under the root, newest first, one level deep");
        Check.Equal(3, open[0].ChildCount);
        Check.True(open.Skip(1).All(i => i.Kind == ClipRowKind.Child && i.Depth == 1));
        // the same answer whatever order the list came in
        var shuffled = new List<ClipEntry> { t1, a, t3, t2 };
        Check.Equal(new[] { "Game A.mp4", "Game A (trim) (trim).mp4", "Game A (trim 2).mp4", "Game A (trim).mp4" }, Shown(Flat(shuffled, A)));
    }

    [UnitTest]
    public static void A_trim_whose_clip_is_not_listed_stays_where_it_is()
    {
        var a = Clip(A, 0);
        var orphan = Clip(@"C:\Clips\Gone (trim).mp4", 10, trim: true, source: @"C:\Clips\Gone.mp4");
        var chainEndsInNothing = Clip(@"C:\Clips\Gone (trim) (trim).mp4", 11, trim: true, source: orphan.Path);
        var t = Clip(@"C:\Clips\Game A (trim).mp4", 3, trim: true, source: A);
        var list = Newest(a, orphan, chainEndsInNothing, t);
        var collapsed = Flat(list);
        Check.Equal(new[] { "Gone (trim) (trim).mp4", "Gone (trim).mp4", "Game A.mp4" }, Shown(collapsed), "today's order, orphans untouched");
        Check.Equal(ClipRowKind.Plain, collapsed[0].Kind);
        Check.Equal(ClipRowKind.Plain, collapsed[1].Kind);
        Check.Equal(ClipRowKind.Parent, collapsed[2].Kind);
        // a clip that is not a trim never nests, whatever its SourcePath says
        var odd = Clip(@"C:\Clips\Other.mp4", 1, trim: false, source: A);
        Check.Equal(2, Flat(Newest(a, odd)).Count);
    }

    [UnitTest]
    public static void Older_trims_without_a_source_are_matched_by_file_name_when_it_is_unambiguous()
    {
        var a = Clip(A, 0);
        var t1 = Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: true);
        var t2 = Clip(@"C:\Clips\Game A (trim 2).mp4", 6, trim: true, source: "");
        var t12 = Clip(@"C:\Clips\Game A (trim 12).mp4", 7, trim: true);
        var open = Flat(Newest(a, t1, t2, t12), A);
        Check.Equal(new[] { "Game A.mp4", "Game A (trim 12).mp4", "Game A (trim 2).mp4", "Game A (trim).mp4" }, Shown(open));

        // two entries for the clip's file name: no guess
        var twin = Clip(A, 1);
        Check.Equal(3, Flat(Newest(a, twin, t1), A).Count, "ambiguous: nothing nests");
        Check.True(Flat(Newest(a, twin, t1), A).All(i => i.Kind == ClipRowKind.Plain));
        // another folder, another extension, no such clip, not a trim: no guess
        Check.Equal(2, Flat(Newest(a, Clip(@"D:\Other\Game A (trim).mp4", 5, trim: true))).Count);
        Check.Equal(2, Flat(Newest(a, Clip(@"C:\Clips\Game A (trim).mkv", 5, trim: true))).Count);
        Check.Equal(1, Flat(Newest(Clip(@"C:\Clips\Nothing (trim).mp4", 5, trim: true))).Count);
        Check.Equal(2, Flat(Newest(a, Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: false))).Count, "only a trim is matched");
        // a SourcePath that names something else is believed; the file name is not consulted
        Check.Equal(2, Flat(Newest(a, Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: true, source: @"C:\Gone\Game A.mp4"))).Count);
    }

    [UnitTest]
    public static void Paths_are_compared_without_regard_to_case_or_spelling()
    {
        var a = Clip(A, 0);
        var t = Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: true, source: @"c:\CLIPS\sub\..\game a.MP4");
        var open = Flat(Newest(a, t), @"C:\clips\GAME A.mp4");
        Check.Equal(2, open.Count, "expanded under a differently spelled key");
        Check.Equal(ClipRowKind.Child, open[1].Kind);
        // legacy match too
        var legacy = Clip(@"c:\clips\game a (TRIM 3).MP4", 6, trim: true);
        Check.Equal(2, Flat(Newest(a, legacy), A).Count);
        Check.Equal(ClipRowKind.Child, Flat(Newest(a, legacy), A)[1].Kind);
    }

    [UnitTest]
    public static void A_loop_or_a_very_long_chain_never_hangs_and_stays_at_the_top()
    {
        var x = Clip(@"C:\Clips\X (trim).mp4", 2, trim: true, source: @"C:\Clips\Y (trim).mp4");
        var y = Clip(@"C:\Clips\Y (trim).mp4", 1, trim: true, source: x.Path);
        var self = Clip(@"C:\Clips\Self (trim).mp4", 3, trim: true, source: @"C:\Clips\Self (trim).mp4");
        var loop = Flat(Newest(x, y, self), x.Path, y.Path, self.Path);
        Check.Equal(3, loop.Count);
        Check.True(loop.All(i => i.Kind == ClipRowKind.Plain), "a cycle has no root");
        // a trim of a trim of ... 40 deep: the first ones reach the root, the far end is not followed
        var root = Clip(A, 0);
        var all = new List<ClipEntry> { root };
        string prev = A;
        for (int k = 0; k < 40; k++)
        {
            var t = Clip($@"C:\Clips\Game A (trim {k}).mp4", 1 + k, trim: true, source: prev);
            all.Add(t);
            prev = t.Path;
        }
        var open = Flat(Newest(all.ToArray()), A);
        Check.True(open.Any(i => i.Item.Path == @"C:\Clips\Game A (trim 0).mp4" && i.Kind == ClipRowKind.Child));
        var far = open.Single(i => i.Item.Path == @"C:\Clips\Game A (trim 39).mp4");
        Check.Equal(ClipRowKind.Plain, far.Kind, "beyond the cap it stays at the top level");
    }

    [UnitTest]
    public static void A_recording_can_hold_trims()
    {
        var rec = Clip(@"C:\Clips\Session.mp4", 0, recording: true);
        var t = Clip(@"C:\Clips\Session (trim).mp4", 4, trim: true, source: rec.Path);
        var open = Flat(Newest(rec, t), rec.Path);
        Check.Equal(new[] { "Session.mp4", "Session (trim).mp4" }, Shown(open));
        Check.Equal(ClipRowKind.Parent, open[0].Kind);
    }

    [UnitTest]
    public static void A_parent_keeps_its_place_and_only_the_expanded_ones_list_their_trims()
    {
        var newest = Clip(@"C:\Clips\Newest.mp4", 100);
        var a = Clip(A, 50);
        var ta = Clip(@"C:\Clips\Game A (trim).mp4", 90, trim: true, source: A);
        var b = Clip(@"C:\Clips\B.mp4", 40);
        var tb = Clip(@"C:\Clips\B (trim).mp4", 120, trim: true, source: b.Path);   // the very newest file, nested under the older B
        var plain = Clip(@"C:\Clips\Plain.mp4", 10);
        var list = Newest(newest, a, ta, b, tb, plain);
        Check.Equal(new[] { "Newest.mp4", "Game A.mp4", "B.mp4", "Plain.mp4" }, Shown(Flat(list)), "collapsed: today's order minus the nested trims");
        Check.Equal(new[] { "Newest.mp4", "Game A.mp4", "B.mp4", "B (trim).mp4", "Plain.mp4" }, Shown(Flat(list, b.Path)), "only B open");
        Check.Equal(new[] { "Newest.mp4", "Game A.mp4", "Game A (trim).mp4", "B.mp4", "B (trim).mp4", "Plain.mp4" }, Shown(Flat(list, A, b.Path)), "both open");
        Check.Equal(new[] { ClipRowKind.Plain, ClipRowKind.Parent, ClipRowKind.Parent, ClipRowKind.Plain }, Flat(list).Select(i => i.Kind).ToArray());
        Check.Equal(0, Flat(new List<ClipEntry>()).Count);
    }

    [UnitTest]
    public static void The_trimmed_filter_and_the_shared_level_stay_flat()
    {
        var a = Clip(A, 0);
        var t1 = Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: true, source: A);
        var t2 = Clip(@"C:\Clips\Game A (trim 2).mp4", 6, trim: true, source: A);
        var all = Newest(a, t1, t2);
        var trims = all.Where(e => e.IsTrim).ToList();
        var chipOn = ClipGrouping.View(all, trims, trimmedOnly: true, e => e, ClipGrouping.NewExpanded(new[] { A }));
        Check.Equal(new[] { "Game A (trim 2).mp4", "Game A (trim).mp4" }, Shown(chipOn), "the trims as they are, in list order, even with the clip open");
        Check.True(chipOn.All(i => i.Kind == ClipRowKind.Plain && i.Depth == 0 && i.ChildCount == 0));
        var chipOff = ClipGrouping.View(all, trims, trimmedOnly: false, e => e, ClipGrouping.NewExpanded());
        Check.Equal(new[] { "Game A.mp4" }, Shown(chipOff));
        // the Shared level never goes through the grouping: the picker lists every shared clip, trims included
        var shared = LibraryView.Pick(all, LibraryLevel.Shared, false, _ => true, _ => true, e => e.IsTrim);
        Check.Equal(3, shared.Count);
        // the "Local N" count is the number of local clips (children included), whatever is open
        Check.Equal(3, LibraryView.Pick(all, LibraryLevel.Local, false, _ => true, _ => false, e => e.IsTrim).Count);
    }

    [UnitTest]
    public static void A_new_trim_opens_its_clip_but_what_was_there_at_the_first_look_does_not()
    {
        var a = Clip(A, 0);
        var old = Clip(@"C:\Clips\Game A (trim).mp4", 5, trim: true, source: A);
        var seen = ClipGrouping.NewExpanded();
        var first = ClipGrouping.TakeNewTrimParents(Newest(a, old), e => e, seen, seed: true);
        Check.Equal(0, first.Count, "the first look only remembers");
        Check.Equal(0, ClipGrouping.TakeNewTrimParents(Newest(a, old), e => e, seen, seed: false).Count, "nothing new");
        var fresh = Clip(@"C:\Clips\Game A (trim 2).mp4", 9, trim: true, source: old.Path);   // cut from the old trim: still opens the root
        var opened = ClipGrouping.TakeNewTrimParents(Newest(a, old, fresh), e => e, seen, seed: false);
        Check.Equal(new[] { ClipGrouping.Key(A) }, opened.ToArray());
        Check.Equal(0, ClipGrouping.TakeNewTrimParents(Newest(a, old, fresh), e => e, seen, seed: false).Count, "only once");
        // a new clip that is not a trim, an orphan trim and an archive move open nothing
        var plain = Clip(@"C:\Clips\Other.mp4", 12);
        var orphan = Clip(@"C:\Clips\Lost (trim).mp4", 13, trim: true, source: @"C:\Gone\Lost.mp4");
        Check.Equal(0, ClipGrouping.TakeNewTrimParents(Newest(a, old, fresh, plain, orphan), e => e, seen, seed: false).Count);
        var moved = Clip(@"\\nas\archive\Game A (trim 3).mp4", 14, trim: true, source: A);
        moved.ArchivedAt = T0;
        Check.Equal(0, ClipGrouping.TakeNewTrimParents(Newest(a, old, fresh, plain, orphan, moved), e => e, seen, seed: false).Count);
    }

    [UnitTest]
    public static void The_chevron_has_a_label_and_a_screen_reader_name_and_a_row_knows_its_place()
    {
        Check.Equal("1 trim", ClipGrouping.TrimsLabel(1));
        Check.Equal("3 trims", ClipGrouping.TrimsLabel(3));
        Check.Equal("Show 3 trims of Game A", ClipGrouping.ToggleName(false, 3, "Game A"));
        Check.Equal("Hide 1 trim of Game A", ClipGrouping.ToggleName(true, 1, "Game A"));
        var row = new ClipRow(new ClipEntry { Path = A, Game = "Game A" });
        Check.False(row.IsParent || row.IsChild || row.IsExpanded, "a row is plain until the pane says otherwise");
        Check.Equal(0.0, row.NestMargin.Left);
        row.SetNesting(ClipRowKind.Parent, 2, expanded: false);
        Check.True(row.IsParent);
        Check.Equal("2 trims", row.TrimsLabel);
        Check.Equal("Show 2 trims of Game A", row.ToggleName);
        row.SetNesting(ClipRowKind.Parent, 2, expanded: true);
        Check.Equal("Hide 2 trims of Game A", row.ToggleName);
        row.SetNesting(ClipRowKind.Child, 0, expanded: false);
        Check.True(row.IsChild);
        Check.Equal(28.0, row.NestMargin.Left, "a trim is indented 28 px");
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        row.SetNesting(ClipRowKind.Child, 0, expanded: false);
        Check.Equal(0, changed.Count, "no change, no notification");
        row.SetNesting(ClipRowKind.Plain, 0, expanded: false);
        Check.True(changed.Contains("IsChild") && changed.Contains("NestMargin"));
    }
}
