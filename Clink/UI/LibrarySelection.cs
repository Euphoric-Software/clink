namespace Clink.UI;

/// <summary>How a click on a row's checkbox or body changes the selection (Ctrl = Toggle, Shift = Range, Ctrl+Shift = AddRange).</summary>
internal enum SelectGesture { Toggle, Range, AddRange }

/// <summary>
/// The multi-selection of one library list: a set of rows (compared by reference) plus the anchor a Shift range starts from. Pure (no
/// WPF types): the pane owns one per list kind and syncs its rows from it. Selecting never reaches outside the visible rows
/// (<see cref="Prune"/> after every list change), and <see cref="Changed"/> fires only when the set itself changed.
/// </summary>
internal sealed class LibrarySelection<T> where T : class
{
    private readonly HashSet<T> _set = new(ReferenceEqualityComparer.Instance);

    public int Count => _set.Count;

    /// <summary>At least one row is selected: the list is in selection mode.</summary>
    public bool Active => _set.Count > 0;

    /// <summary>The row the next Shift range starts from (the last one toggled), or null.</summary>
    public T? Anchor { get; private set; }

    public bool Contains(T item) => _set.Contains(item);

    /// <summary>Raised after the set changed (not for an anchor-only change).</summary>
    public event Action? Changed;

    /// <summary>
    /// Applies a gesture on <paramref name="item"/>. Toggle flips the row and makes it the anchor. Range replaces the selection with
    /// the rows from the anchor to the row (either direction, in <paramref name="visible"/> order); AddRange adds them. A range with no
    /// anchor (or an anchor that is not in the visible list) acts as a Toggle.
    /// </summary>
    public void Apply(T item, SelectGesture gesture, IReadOnlyList<T> visible)
    {
        int to = IndexOf(visible, item);
        int from = gesture == SelectGesture.Toggle || Anchor == null ? -1 : IndexOf(visible, Anchor);
        if (gesture == SelectGesture.Toggle || from < 0 || to < 0)
        {
            bool changed = _set.Remove(item) ? true : _set.Add(item);
            Anchor = item;
            if (changed) Changed?.Invoke();
            return;
        }
        int lo = Math.Min(from, to), hi = Math.Max(from, to);
        bool any = false;
        if (gesture == SelectGesture.Range)
        {
            var keep = new HashSet<T>(ReferenceEqualityComparer.Instance);
            for (int i = lo; i <= hi; i++) keep.Add(visible[i]);
            if (!_set.SetEquals(keep)) { _set.Clear(); foreach (var r in keep) _set.Add(r); any = true; }
        }
        else
        {
            for (int i = lo; i <= hi; i++) any |= _set.Add(visible[i]);
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>Selects every visible row.</summary>
    public void SelectAll(IReadOnlyList<T> visible)
    {
        bool any = false;
        foreach (var r in visible) any |= _set.Add(r);
        if (any) Changed?.Invoke();
    }

    public void Clear()
    {
        Anchor = null;
        if (_set.Count == 0) return;
        _set.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops what is no longer in <paramref name="visible"/>; the anchor is reset only when the anchor row itself is gone. True when the set changed.</summary>
    public bool Prune(IReadOnlyList<T> visible)
    {
        var shown = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var r in visible) shown.Add(r);
        int removed = _set.RemoveWhere(r => !shown.Contains(r));
        if (Anchor != null && !shown.Contains(Anchor)) Anchor = null;
        if (removed == 0) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The selected rows in list order (rows that are not in <paramref name="visible"/> are left out).</summary>
    public List<T> InOrder(IReadOnlyList<T> visible)
    {
        var list = new List<T>(_set.Count);
        foreach (var r in visible) if (_set.Contains(r)) list.Add(r);
        return list;
    }

    private static int IndexOf(IReadOnlyList<T> list, T? item)
    {
        if (item == null) return -1;
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }
}
