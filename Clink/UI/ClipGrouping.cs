using System.IO;
using System.Text.RegularExpressions;
using Clink.Services;

namespace Clink.UI;

/// <summary>What a row of the Local clips list is: a clip on its own, a clip with trims under it, or one of those trims.</summary>
internal enum ClipRowKind { Plain, Parent, Child }

/// <summary>
/// One row of the flat Local list. <see cref="GroupKey"/> is the <see cref="ClipGrouping.Key"/> of the parent (its own for a
/// Parent, the parent's for a Child, its own for a Plain row). <see cref="Expanded"/> is true for a Parent whose trims are listed.
/// </summary>
internal sealed record ClipListItem<T>(T Item, ClipRowKind Kind, int Depth, int ChildCount, string GroupKey, bool Expanded);

/// <summary>
/// Which trimmed clips are listed under the full-length clip they were cut from (Local level, Trimmed chip off). Pure: no disk, no
/// window. A trim's parent is the clip its <see cref="ClipEntry.SourcePath"/> names; a trim of a trim goes under the root of the
/// chain, flat (depth 1), newest first. Older trims without a SourcePath are matched by their file name ("X (trim).mp4" and
/// "X (trim 2).mp4" belong to "X.mp4" in the same folder) only when that is unambiguous. Anything that cannot be placed stays at
/// the top level exactly where it was.
/// </summary>
internal static class ClipGrouping
{
    /// <summary>A source chain longer than this is not followed (the trim stays at the top level).</summary>
    public const int MaxDepth = 32;

    private static readonly Regex s_legacy = new(@"^(?<stem>.+) \(trim(?: \d+)?\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A path as the grouping compares it: full path, no trailing separator; comparisons are case-insensitive (use <see cref="NewExpanded"/> for the set).</summary>
    public static string Key(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path.Trim(); }
    }

    /// <summary>The set of expanded parents (by <see cref="Key"/>), case-insensitive.</summary>
    public static HashSet<string> NewExpanded(IEnumerable<string>? paths = null) =>
        new((paths ?? Array.Empty<string>()).Select(Key), StringComparer.OrdinalIgnoreCase);

    /// <summary>"1 trim", "3 trims".</summary>
    public static string TrimsLabel(int count) => count == 1 ? "1 trim" : $"{count} trims";

    /// <summary>The screen reader name of the chevron: "Show 3 trims of Game" / "Hide 1 trim of Game".</summary>
    public static string ToggleName(bool expanded, int count, string title) => $"{(expanded ? "Hide" : "Show")} {TrimsLabel(count)} of {title}";

    /// <summary>
    /// A chevron's state changed: show or hide the trims only when the toggle now differs from the model (the model setting the
    /// toggle comes back here and is ignored), the row is a parent, and the Trimmed chip is off (flat list: no chevrons act).
    /// </summary>
    public static bool ShouldToggle(bool? toggleChecked, bool modelExpanded, bool isParent, bool trimmedChipOn) =>
        isParent && !trimmedChipOn && (toggleChecked == true) != modelExpanded;

    private sealed class Plan
    {
        public ClipEntry[] Entries = Array.Empty<ClipEntry>();
        public string[] Keys = Array.Empty<string>();
        public int[] Root = Array.Empty<int>();   // index of the parent clip, or -1 for a top-level row
    }

    private static Plan Analyze<T>(IReadOnlyList<T> items, Func<T, ClipEntry> entryOf)
    {
        int n = items.Count;
        var plan = new Plan { Entries = new ClipEntry[n], Keys = new string[n], Root = new int[n] };
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            plan.Entries[i] = entryOf(items[i]);
            plan.Keys[i] = Key(plan.Entries[i].Path);
            if (!byKey.TryAdd(plan.Keys[i], i)) dup.Add(plan.Keys[i]);
        }

        // the clip this trim was cut from (one step), or -1
        int Direct(int i)
        {
            var e = plan.Entries[i];
            if (!e.IsTrim) return -1;
            if (!string.IsNullOrWhiteSpace(e.SourcePath)) return byKey.TryGetValue(Key(e.SourcePath), out int src) ? src : -1;
            // older trims: "<stem> (trim).mp4" / "<stem> (trim N).mp4" beside "<stem>.mp4", when that is the only candidate
            string ext = Path.GetExtension(e.Path ?? "");
            if (!string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase)) return -1;
            var m = s_legacy.Match(Path.GetFileNameWithoutExtension(e.Path) ?? "");
            string? dir = Path.GetDirectoryName(e.Path);
            if (!m.Success || string.IsNullOrEmpty(dir)) return -1;
            string cand = Key(Path.Combine(dir, m.Groups["stem"].Value + ext));
            return !dup.Contains(cand) && byKey.TryGetValue(cand, out int parent) ? parent : -1;
        }

        for (int i = 0; i < n; i++)
        {
            plan.Root[i] = -1;
            if (!plan.Entries[i].IsTrim) continue;
            var seen = new HashSet<int> { i };
            int cur = i;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                int p = Direct(cur);
                if (p < 0 || !seen.Add(p)) break;   // no source here, or a cycle
                if (!plan.Entries[p].IsTrim) { plan.Root[i] = p; break; }
                cur = p;
            }
        }
        return plan;
    }

    /// <summary>
    /// The flat list the Local level shows. <paramref name="items"/> is the Local list in its own order (Created desc); top-level
    /// rows keep that order, a parent stays where it was and, when its <see cref="Key"/> is in <paramref name="expanded"/>, its
    /// trims follow it directly, newest first. Collapsed parents list none of their trims.
    /// </summary>
    public static List<ClipListItem<T>> Flatten<T>(IReadOnlyList<T> items, Func<T, ClipEntry> entryOf, ICollection<string> expanded)
    {
        var plan = Analyze(items, entryOf);
        var kids = new Dictionary<int, List<int>>();
        for (int i = 0; i < items.Count; i++)
            if (plan.Root[i] >= 0)
            {
                if (!kids.TryGetValue(plan.Root[i], out var list)) kids[plan.Root[i]] = list = new List<int>();
                list.Add(i);
            }
        var result = new List<ClipListItem<T>>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            if (plan.Root[i] >= 0) continue;
            if (!kids.TryGetValue(i, out var children))
            {
                result.Add(new ClipListItem<T>(items[i], ClipRowKind.Plain, 0, 0, plan.Keys[i], false));
                continue;
            }
            bool open = expanded.Contains(plan.Keys[i]);
            result.Add(new ClipListItem<T>(items[i], ClipRowKind.Parent, 0, children.Count, plan.Keys[i], open));
            if (!open) continue;
            foreach (int c in children.OrderByDescending(c => plan.Entries[c].Created))   // stable: equal times keep the list order
                result.Add(new ClipListItem<T>(items[c], ClipRowKind.Child, 1, 0, plan.Keys[i], false));
        }
        return result;
    }

    /// <summary>The trims <see cref="Flatten"/> puts under <paramref name="parent"/> (also while it is collapsed; trims of trims included), newest first.</summary>
    public static List<T> TrimsOf<T>(IReadOnlyList<T> items, Func<T, ClipEntry> entryOf, T parent) where T : class
    {
        string key = Key(entryOf(parent).Path);
        return Flatten(items, entryOf, NewExpanded(new[] { key }))
            .Where(f => f.Kind == ClipRowKind.Child && string.Equals(f.GroupKey, key, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Item).ToList();
    }

    /// <summary>
    /// The rows of the Local level. With the Trimmed filter on, <paramref name="filtered"/> (the trims, already picked) is listed flat,
    /// exactly as before; otherwise <paramref name="all"/> (every local clip) is nested by <see cref="Flatten"/>.
    /// </summary>
    public static List<ClipListItem<T>> View<T>(IReadOnlyList<T> all, IReadOnlyList<T> filtered, bool trimmedOnly, Func<T, ClipEntry> entryOf, ICollection<string> expanded)
    {
        if (!trimmedOnly) return Flatten(all, entryOf, expanded);
        return filtered.Select(i => new ClipListItem<T>(i, ClipRowKind.Plain, 0, 0, Key(entryOf(i).Path), false)).ToList();
    }

    /// <summary>
    /// The archive service moves a clip by changing its path (same entry object), which would change its <see cref="Key"/> and
    /// close it. Carries the open state from the key an item had at the last call (<paramref name="lastKeys"/>, by reference) to
    /// its key now, and forgets items no longer listed.
    /// </summary>
    public static void CarryExpanded<T>(IReadOnlyList<T> items, Func<T, ClipEntry> entryOf, Dictionary<T, string> lastKeys, ICollection<string> expanded) where T : class
    {
        var present = new HashSet<T>(items, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        foreach (var gone in lastKeys.Keys.Where(k => !present.Contains(k)).ToList()) lastKeys.Remove(gone);
        foreach (var item in items)
        {
            string key = Key(entryOf(item).Path);
            if (lastKeys.TryGetValue(item, out var old) && !string.Equals(old, key, StringComparison.OrdinalIgnoreCase) && expanded.Remove(old))
                expanded.Add(key);
            lastKeys[item] = key;
        }
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal <paramref name="rows"/> (by reference) with simple Insert/RemoveAt steps (not always
    /// the fewest: a row moved down costs a RemoveAt plus an Insert), so a bound list keeps its scroll position. When that would
    /// take more than <paramref name="maxEdits"/> steps, or more than half the list (but at least 8), it is Clear + Add instead (<c>int.MaxValue</c> for <paramref name="maxEdits"/>: never).
    /// </summary>
    public static void SyncRows<T>(IList<T> target, IReadOnlyList<T> rows, int maxEdits = MaxEdits) where T : class
    {
        int limit = maxEdits == int.MaxValue ? int.MaxValue : Math.Min(maxEdits, Math.Max(8, Math.Max(target.Count, rows.Count) / 2));
        if (limit != int.MaxValue && SyncCore(new List<T>(target), rows, limit) > limit)
        {
            target.Clear();
            foreach (var r in rows) target.Add(r);
            return;
        }
        SyncCore(target, rows, int.MaxValue);
    }

    /// <summary>Most single-item edits <see cref="SyncRows"/> makes before it rebuilds the list.</summary>
    public const int MaxEdits = 64;

    /// <summary>The edits a trial run makes before it gives up (more than <paramref name="limit"/>); for tests.</summary>
    internal static int TrialEdits<T>(IList<T> list, IReadOnlyList<T> rows, int limit) where T : class => SyncCore(new List<T>(list), rows, limit);

    private static int SyncCore<T>(IList<T> list, IReadOnlyList<T> rows, int limit) where T : class
    {
        int edits = 0;
        var want = new HashSet<T>(rows, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        for (int i = list.Count - 1; i >= 0; i--)
            if (!want.Contains(list[i])) { list.RemoveAt(i); edits++; }
        if (edits > limit) return edits;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], rows[i])) continue;
            for (int j = i + 1; j < list.Count; j++)
                if (ReferenceEquals(list[j], rows[i])) { list.RemoveAt(j); edits++; break; }
            list.Insert(i, rows[i]);
            edits++;
            if (edits > limit) return edits;
        }
        while (list.Count > rows.Count) { list.RemoveAt(list.Count - 1); edits++; }   // duplicates left over
        return edits;
    }
    /// <summary>
    /// Remembers the clips of <paramref name="items"/> in <paramref name="seen"/> and returns the parents of the trims that were not
    /// in it before (to open them, so a trim just made shows). With <paramref name="seed"/> nothing is returned (the first look at
    /// the list: whatever is there is not new). Archived entries are never new (an archive move changes a path, it makes no clip).
    /// </summary>
    public static List<string> TakeNewTrimParents<T>(IReadOnlyList<T> items, Func<T, ClipEntry> entryOf, HashSet<string> seen, bool seed)
    {
        var plan = Analyze(items, entryOf);
        var open = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            bool isNew = seen.Add(plan.Keys[i]);
            if (!isNew || seed || plan.Root[i] < 0 || plan.Entries[i].IsArchived) continue;
            string key = plan.Keys[plan.Root[i]];
            if (!open.Contains(key, StringComparer.OrdinalIgnoreCase)) open.Add(key);
        }
        return open;
    }
}
