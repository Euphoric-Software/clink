using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Geometry;

/// <summary>How a committed selection was made: a drag released, Enter / Space on a band, or the whole monitor (Enter / Space with no band).</summary>
internal enum PickOutcome { Drag, Keyboard, WholeMonitor }

/// <summary>
/// The state shared by the overlay windows (one per monitor): where the pointer is, the rubber band being dragged and how it ends.
/// Pure (no windows, no input API), in physical virtual-screen pixels, so the whole interaction is tested without a screen. The
/// windows feed it the pointer and keys and draw what <see cref="Selection"/> says; they subscribe to <see cref="Changed"/>.
/// </summary>
internal sealed class RegionSelection
{
    private int _anchorX, _anchorY;

    public IReadOnlyList<RectI> Monitors { get; }
    public RectI VirtualBounds { get; }

    /// <summary>The rubber band while dragging, or the finished selection; <see cref="RectI.Empty"/> when there is none.</summary>
    public RectI Selection { get; private set; }
    public bool Dragging { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }

    /// <summary>The selection was committed (<see cref="Committed"/> raised) or cancelled: nothing more is accepted.</summary>
    public bool Finished { get; private set; }

    /// <summary>The rectangle that was committed; <see cref="RectI.Empty"/> after a cancel or before the end.</summary>
    public RectI Result { get; private set; }

    /// <summary>How the committed rectangle was made (meaningless before a commit).</summary>
    public PickOutcome Outcome { get; private set; }

    public event Action? Changed;
    public event Action<RectI>? Committed;
    public event Action? Cancelled;

    public RegionSelection(IReadOnlyList<RectI> monitors)
    {
        Monitors = monitors;
        VirtualBounds = SelectionMath.VirtualBounds(monitors);
    }

    /// <summary>The pointer moved (also while not dragging: the loupe follows it). While dragging the band follows; <paramref name="square"/> keeps it square.</summary>
    public void MoveCursor(int x, int y, bool square = false)
    {
        if (Finished) return;
        CursorX = x; CursorY = y;
        if (Dragging) Selection = BandTo(x, y, square);
        Changed?.Invoke();
    }

    /// <summary>The left button went down at the pointer: a new band starts there (the previous selection goes).</summary>
    public void BeginDrag(int x, int y)
    {
        if (Finished) return;
        _anchorX = x; _anchorY = y;
        CursorX = x; CursorY = y;
        Dragging = true;
        Selection = RectI.Empty;
        Changed?.Invoke();
    }

    /// <summary>
    /// The left button went up. A band of at least <see cref="SelectionMath.MinDragSize"/> on both sides is committed, if
    /// <paramref name="commit"/> (screenshot mode none) or else kept as the selection; a smaller one was a click and clears the selection.
    /// Returns true when it committed.
    /// </summary>
    public bool EndDrag(int x, int y, bool square = false, bool commit = true)
    {
        if (Finished || !Dragging) return false;
        CursorX = x; CursorY = y;
        Selection = BandTo(x, y, square);
        Dragging = false;
        if (!IsUsable(Selection))
        {
            Selection = RectI.Empty;
            Changed?.Invoke();
            return false;
        }
        if (!commit) { Changed?.Invoke(); return false; }
        Commit(Selection, PickOutcome.Drag);
        return true;
    }

    /// <summary>Right-click: clears the band or selection being made; with none, cancels the whole capture. Returns true when it cancelled.</summary>
    public bool RightClick()
    {
        if (Finished) return false;
        if (Dragging || !Selection.IsEmpty)
        {
            Dragging = false;
            Selection = RectI.Empty;
            Changed?.Invoke();
            return false;
        }
        Cancel();
        return true;
    }

    /// <summary>Enter / Space: commits the selection, or with none the whole monitor under the pointer.</summary>
    public void Confirm()
    {
        if (Finished) return;
        var band = Dragging ? BandTo(CursorX, CursorY, false) : Selection;
        Dragging = false;
        if (IsUsable(band)) { Selection = band; Commit(band, PickOutcome.Keyboard); return; }
        CommitMonitorAt(CursorX, CursorY);
    }

    /// <summary>Commits the whole monitor holding (or nearest to) the point.</summary>
    public void CommitMonitorAt(int x, int y)
    {
        if (Finished) return;
        int i = SelectionMath.MonitorIndexAt(x, y, Monitors);
        if (i < 0) { Cancel(); return; }
        Dragging = false;
        Selection = Monitors[i];
        Commit(Monitors[i], PickOutcome.WholeMonitor);
    }

    /// <summary>
    /// Esc, or the screen layout changed: the capture ends with nothing. <see cref="Cancelled"/> is raised even when a
    /// <see cref="Changed"/> subscriber throws (the exception still goes up afterwards): once <see cref="Finished"/> is set nothing else
    /// ends the capture, so its end must never be lost.
    /// </summary>
    public void Cancel()
    {
        if (Finished) return;
        Finished = true;
        Dragging = false;
        Selection = RectI.Empty;
        Result = RectI.Empty;
        try { Changed?.Invoke(); }
        finally { Cancelled?.Invoke(); }
    }

    private static bool IsUsable(RectI r) => r.Width >= SelectionMath.MinDragSize && r.Height >= SelectionMath.MinDragSize;

    private RectI BandTo(int x, int y, bool square)
    {
        if (square) (x, y) = SelectionMath.SquareCorner(_anchorX, _anchorY, x, y, VirtualBounds);
        return SelectionMath.Clamp(SelectionMath.Band(_anchorX, _anchorY, x, y), VirtualBounds);
    }

    /// <summary>Ends with <paramref name="rect"/>; <see cref="Committed"/> is raised even when a <see cref="Changed"/> subscriber throws (see <see cref="Cancel"/>).</summary>
    private void Commit(RectI rect, PickOutcome outcome)
    {
        Finished = true;
        Result = rect;
        Outcome = outcome;
        try { Changed?.Invoke(); }
        finally { Committed?.Invoke(rect); }
    }
}
