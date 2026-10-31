using Clink.UI;

namespace Clink.Tests;

/// <summary>The library's multi-selection model: toggle, ranges from the anchor, select all, pruning to the visible rows, and when it says it changed.</summary>
internal static class LibrarySelectionTests
{
    private sealed class Item(string name) { public override string ToString() => name; }

    private static List<Item> Items(int n) => Enumerable.Range(1, n).Select(i => new Item("r" + i)).ToList();

    [UnitTest]
    public static void Toggle_flips_a_row_and_makes_it_the_anchor()
    {
        var rows = Items(4);
        var sel = new LibrarySelection<Item>();
        Check.False(sel.Active, "empty is not active");
        sel.Apply(rows[1], SelectGesture.Toggle, rows);
        Check.True(sel.Active);
        Check.Equal(1, sel.Count);
        Check.True(sel.Contains(rows[1]));
        Check.True(ReferenceEquals(rows[1], sel.Anchor), "the anchor");
        sel.Apply(rows[1], SelectGesture.Toggle, rows);
        Check.Equal(0, sel.Count);
        Check.False(sel.Active);
        Check.True(ReferenceEquals(rows[1], sel.Anchor), "a toggled-off row is still the anchor");
    }

    [UnitTest]
    public static void A_range_replaces_the_selection_in_either_direction()
    {
        var rows = Items(6);
        var sel = new LibrarySelection<Item>();
        sel.Apply(rows[1], SelectGesture.Toggle, rows);
        sel.Apply(rows[4], SelectGesture.Range, rows);
        Check.Equal(new[] { rows[1], rows[2], rows[3], rows[4] }, sel.InOrder(rows).ToArray(), "down");
        Check.True(ReferenceEquals(rows[1], sel.Anchor), "a range keeps its anchor");
        sel.Apply(rows[0], SelectGesture.Range, rows);
        Check.Equal(new[] { rows[0], rows[1] }, sel.InOrder(rows).ToArray(), "up from the same anchor replaces the earlier range");
        Check.False(sel.Contains(rows[4]));
    }

    [UnitTest]
    public static void AddRange_adds_to_what_is_selected()
    {
        var rows = Items(8);
        var sel = new LibrarySelection<Item>();
        sel.Apply(rows[0], SelectGesture.Toggle, rows);
        sel.Apply(rows[5], SelectGesture.Toggle, rows);
        sel.Apply(rows[7], SelectGesture.AddRange, rows);
        Check.Equal(new[] { rows[0], rows[5], rows[6], rows[7] }, sel.InOrder(rows).ToArray());
        sel.Apply(rows[3], SelectGesture.AddRange, rows);
        Check.Equal(new[] { rows[0], rows[3], rows[4], rows[5], rows[6], rows[7] }, sel.InOrder(rows).ToArray(), "from the anchor (r6) up to r4 is added");
    }

    [UnitTest]
    public static void A_range_with_no_anchor_or_an_anchor_that_is_not_listed_acts_as_a_toggle()
    {
        var rows = Items(5);
        var sel = new LibrarySelection<Item>();
        sel.Apply(rows[3], SelectGesture.Range, rows);
        Check.Equal(new[] { rows[3] }, sel.InOrder(rows).ToArray(), "no anchor");
        sel.Clear();
        sel.Apply(rows[3], SelectGesture.AddRange, rows);
        Check.Equal(1, sel.Count);
        // the anchor is not in the visible list any more
        sel.Apply(rows[4], SelectGesture.Toggle, rows);
        var fewer = rows.Take(4).ToList();
        sel.Apply(rows[1], SelectGesture.Range, fewer);
        Check.True(sel.Contains(rows[1]) && sel.Contains(rows[4]) && sel.Contains(rows[3]), "toggled on, nothing else changed");
        Check.Equal(3, sel.Count);
    }

    [UnitTest]
    public static void SelectAll_selects_every_visible_row_and_Clear_empties_it()
    {
        var rows = Items(5);
        var sel = new LibrarySelection<Item>();
        sel.SelectAll(rows);
        Check.Equal(5, sel.Count);
        sel.Clear();
        Check.Equal(0, sel.Count);
        Check.True(sel.Anchor == null, "Clear drops the anchor");
    }

    [UnitTest]
    public static void Prune_drops_what_is_no_longer_listed_keeps_the_order_and_resets_a_vanished_anchor()
    {
        var rows = Items(6);
        var sel = new LibrarySelection<Item>();
        sel.SelectAll(rows);
        sel.Apply(rows[2], SelectGesture.Toggle, rows);   // r3 off, anchor r3
        sel.Apply(rows[2], SelectGesture.Toggle, rows);   // r3 on again
        var visible = new List<Item> { rows[5], rows[0], rows[3] };   // another order, fewer rows
        Check.True(sel.Prune(visible), "something was dropped");
        Check.Equal(3, sel.Count);
        Check.Equal(new[] { rows[5], rows[0], rows[3] }, sel.InOrder(visible).ToArray(), "InOrder follows the visible list");
        Check.True(sel.Anchor == null, "the anchor row (r3) is gone: reset");
        Check.False(sel.Prune(visible), "nothing more to drop");
    }

    [UnitTest]
    public static void Prune_keeps_the_anchor_while_the_anchor_row_is_still_listed()
    {
        var rows = Items(6);
        var sel = new LibrarySelection<Item>();
        sel.SelectAll(rows);
        sel.Apply(rows[3], SelectGesture.Toggle, rows);   // r4 off, anchor r4
        Check.True(sel.Prune(rows.Take(5).ToList()), "r6 dropped");
        Check.True(ReferenceEquals(rows[3], sel.Anchor), "something else was dropped, the anchor row is still there");
        sel.Apply(rows[0], SelectGesture.Range, rows.Take(5).ToList());
        Check.Equal(new[] { rows[0], rows[1], rows[2], rows[3] }, sel.InOrder(rows).ToArray(), "a Shift range still starts from it");
        Check.True(sel.Prune(rows.Take(3).ToList()));
        Check.True(sel.Anchor == null, "now the anchor row itself is gone");
    }

    [UnitTest]
    public static void InOrder_follows_the_list_and_leaves_out_what_is_not_listed()
    {
        var rows = Items(4);
        var sel = new LibrarySelection<Item>();
        sel.Apply(rows[3], SelectGesture.Toggle, rows);
        sel.Apply(rows[0], SelectGesture.Toggle, rows);
        Check.Equal(new[] { rows[0], rows[3] }, sel.InOrder(rows).ToArray());
        Check.Equal(new[] { rows[3] }, sel.InOrder(new[] { rows[1], rows[3] }).ToArray(), "r1 is not selected, r0 is not listed");
    }

    [UnitTest]
    public static void Changed_fires_only_when_the_set_changes()
    {
        var rows = Items(4);
        var sel = new LibrarySelection<Item>();
        int fired = 0;
        sel.Changed += () => fired++;
        sel.Clear();
        Check.Equal(0, fired, "clearing nothing");
        sel.Apply(rows[0], SelectGesture.Toggle, rows);
        Check.Equal(1, fired);
        sel.Apply(rows[2], SelectGesture.Range, rows);
        Check.Equal(2, fired, "range r1..r3 changed the set");
        sel.Apply(rows[2], SelectGesture.Range, rows);
        Check.Equal(2, fired, "the same range again changes nothing");
        sel.Apply(rows[2], SelectGesture.AddRange, rows);
        Check.Equal(2, fired, "adding what is there changes nothing");
        sel.SelectAll(rows);
        Check.Equal(3, fired);
        sel.SelectAll(rows);
        Check.Equal(3, fired, "all selected already");
        sel.Prune(rows);
        Check.Equal(3, fired, "nothing to prune");
        sel.Clear();
        Check.Equal(4, fired);
        sel.Clear();
        Check.Equal(4, fired);
    }
}
