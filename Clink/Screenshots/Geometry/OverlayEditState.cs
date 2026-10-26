using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Geometry;

/// <summary>Where a capture is: choosing the area, editing it on the screen (mode "before"), or finished one way or another.</summary>
internal enum OverlayPhase
{
    Selecting,
    /// <summary>The area is inside one monitor and is being edited in that monitor's overlay window.</summary>
    Editing,
    /// <summary>Committed: the picker completes with the area (and, after editing, the document).</summary>
    Done,
    Cancelled,
    /// <summary>Mode "before" with an area over several monitors: no editing in the overlay, the standalone editor window opens instead.</summary>
    SpansMonitors,
}

/// <summary>What has the keyboard in the editing overlay: the canvas, a toolbar button (Enter / Space are its own), or a text box / slider / combo (all its own).</summary>
internal enum OverlayKeyFocus { Canvas, Button, Typing }

/// <summary>What the overlay does with a key: leave it to the focused control, finish, cancel the capture, swallow it, or give it to the editor canvas.</summary>
internal enum OverlayKeyAction { Pass, Commit, Cancel, Swallow, Canvas }

/// <summary>
/// The key rules of the editing overlay, without windows. Esc follows the editor window: it drops a gesture, then deselects, then
/// returns to the Select tool (all three are the canvas's), and cancels the whole capture only when none of that is left.
/// </summary>
internal static class OverlayEditKeys
{
    /// <summary>
    /// What the focused control is for this key: a button base takes Space, but Enter only when it is a real button (or accepts Return);
    /// Enter on a focused toggle / check box is the overlay's (Done).
    /// </summary>
    public static OverlayKeyFocus Classify(System.Windows.Input.Key key, bool typing, bool buttonBase, bool pressesEnter) =>
        typing ? OverlayKeyFocus.Typing
        : buttonBase && (key != System.Windows.Input.Key.Enter || pressesEnter) ? OverlayKeyFocus.Button
        : OverlayKeyFocus.Canvas;

    public static OverlayKeyAction Decide(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods, bool hasGesture, bool hasSelection, bool selectToolActive, OverlayKeyFocus focus, bool isRepeat)
    {
        if (focus == OverlayKeyFocus.Typing) return OverlayKeyAction.Pass;
        bool ctrl = mods.HasFlag(System.Windows.Input.ModifierKeys.Control), alt = mods.HasFlag(System.Windows.Input.ModifierKeys.Alt);
        if (key == System.Windows.Input.Key.Enter && !alt)
        {
            if (focus == OverlayKeyFocus.Button) return OverlayKeyAction.Pass;   // presses the button (the overlay classifies only a real Button so: Enter on a toggle / check box is not a press)
            return isRepeat ? OverlayKeyAction.Swallow : OverlayKeyAction.Commit;   // a key held since the selection must not finish the edit
        }
        if (key == System.Windows.Input.Key.Escape && !ctrl)
            return hasGesture || hasSelection || !selectToolActive ? OverlayKeyAction.Canvas : OverlayKeyAction.Cancel;
        if (key == System.Windows.Input.Key.Space)
            return focus == OverlayKeyFocus.Button ? OverlayKeyAction.Pass : OverlayKeyAction.Swallow;   // (the editor window pans with it; the view is fixed here)
        return OverlayKeyAction.Canvas;
    }
}

/// <summary>
/// The phase logic of the capture overlay, without windows (so it is tested without a screen). The overlay windows feed it the
/// committed selection, the crop the user drags afterwards, Done / Esc and every input (the idle watchdog); they draw what it says.
/// Mode "before" (<paramref name="editBefore"/>): a committed selection does not complete the pick; inside one monitor it starts the
/// editing phase (the crop is the selection, in that monitor's own pixels), over several monitors it ends with
/// <see cref="OverlayPhase.SpansMonitors"/>. Physical pixels throughout.
/// </summary>
internal sealed class OverlayEditState
{
    private readonly Func<long> _clock;
    private long _lastInput;

    public IReadOnlyList<RectI> Monitors { get; }
    public bool EditBefore { get; }
    public OverlayPhase Phase { get; private set; } = OverlayPhase.Selecting;

    /// <summary>The committed selection (virtual-screen pixels).</summary>
    public RectI Selection { get; private set; }
    /// <summary>The monitor of the editing phase; -1 before it.</summary>
    public int MonitorIndex { get; private set; } = -1;
    public RectI MonitorBounds => MonitorIndex >= 0 ? Monitors[MonitorIndex] : RectI.Empty;
    /// <summary>The crop being edited, in the monitor's own pixels (origin = the monitor's top-left).</summary>
    public RectI Crop { get; private set; }
    /// <summary>The crop in virtual-screen pixels.</summary>
    public RectI CropVirtual => Crop.IsEmpty ? RectI.Empty : Crop.Offset(MonitorBounds.X, MonitorBounds.Y);

    /// <summary>The crop changed (a handle was dragged, Reset crop, undo): the toolbar is placed again.</summary>
    public event Action? CropChanged;

    public OverlayEditState(IReadOnlyList<RectI> monitors, bool editBefore, Func<long>? clock = null)
    {
        Monitors = monitors;
        EditBefore = editBefore;
        _clock = clock ?? (() => Environment.TickCount64);
        _lastInput = _clock();
    }

    public bool IsEditing => Phase == OverlayPhase.Editing;
    public bool IsEnded => Phase is OverlayPhase.Done or OverlayPhase.Cancelled or OverlayPhase.SpansMonitors;

    /// <summary>The selection was committed (mouse up, Enter, whole monitor). Returns the phase it leads to.</summary>
    public OverlayPhase SelectionCommitted(RectI selection)
    {
        if (Phase != OverlayPhase.Selecting) return Phase;
        Selection = selection;
        NoteInput();
        if (!EditBefore) return Phase = OverlayPhase.Done;
        var parts = SelectionMath.SplitByMonitor(selection, Monitors);
        if (parts.Count == 0) return Phase = OverlayPhase.Cancelled;
        if (parts.Count > 1) return Phase = OverlayPhase.SpansMonitors;
        MonitorIndex = parts[0].Index;
        Crop = parts[0].Local;
        return Phase = OverlayPhase.Editing;
    }

    /// <summary>A new crop (monitor pixels), kept inside the monitor; an empty or unchanged one is ignored. Raises <see cref="CropChanged"/>.</summary>
    public void SetCrop(RectI crop)
    {
        if (Phase != OverlayPhase.Editing) return;
        var m = MonitorBounds;
        var c = crop.Intersect(new RectI(0, 0, m.Width, m.Height));
        if (c.IsEmpty || c == Crop) return;
        Crop = c;
        CropChanged?.Invoke();
    }

    /// <summary>Enter / Done: the editing ends with the document. False when not editing.</summary>
    public bool Confirm()
    {
        if (Phase != OverlayPhase.Editing) return false;
        Phase = OverlayPhase.Done;
        return true;
    }

    /// <summary>Esc, a closed window, a display change, the watchdog: the capture ends with nothing (unless it ended already).</summary>
    public void Cancel()
    {
        if (!IsEnded) Phase = OverlayPhase.Cancelled;
    }

    // ------------------------------------------------------------------ idle watchdog

    /// <summary>Any pointer move, drag, key or editor input: the capture is not abandoned.</summary>
    public void NoteInput() => _lastInput = _clock();

    /// <summary>No input for this long while choosing the area: cancelled.</summary>
    public static readonly TimeSpan SelectingIdleTimeout = TimeSpan.FromMinutes(2);
    /// <summary>No input for this long while editing (thinking, typing elsewhere): cancelled. Much longer than choosing an area.</summary>
    public static readonly TimeSpan EditIdleTimeout = TimeSpan.FromMinutes(20);

    /// <summary>The idle limit of the current phase.</summary>
    public TimeSpan IdleLimit => IsEditing ? EditIdleTimeout : SelectingIdleTimeout;

    /// <summary>Never idle while suspended (the insert-image picker or a file dialog is open: the user is busy there, and the overlay sees no input).</summary>
    public bool IsIdle(TimeSpan timeout) => !_suspended && _clock() - _lastInput >= (long)timeout.TotalMilliseconds;

    private bool _suspended;

    /// <summary>A dialog opens: the idle check is off until <see cref="Resume"/>.</summary>
    public void Suspend() => _suspended = true;

    /// <summary>The dialog closed: the idle check is on again, counted from now.</summary>
    public void Resume()
    {
        _suspended = false;
        NoteInput();
    }

    // ------------------------------------------------------------------ insert image (modal dialogs)

    private bool _closePending;

    /// <summary>True while the insert-image picker / file dialog / paste is under way.</summary>
    public bool Inserting { get; private set; }

    public void BeginInsert() => Inserting = true;

    /// <summary>A cancel / close while the dialogs are modal waits: true = deferred (it is run by <see cref="EndInsert"/>'s caller).</summary>
    public bool TryDeferClose()
    {
        if (!Inserting) return false;
        _closePending = true;
        return true;
    }

    /// <summary>The insert ended (the dialogs are closed). True when a close was deferred meanwhile and must run now.</summary>
    public bool EndInsert()
    {
        Inserting = false;
        bool run = _closePending;
        _closePending = false;
        return run;
    }

    // ------------------------------------------------------------------ the floating toolbar

    /// <summary>
    /// Where the toolbar's top-left goes, in the monitor's own pixels: below the crop, else above it, else inside it at the bottom,
    /// left-aligned with it and kept on the monitor (<see cref="ToolbarPlacement.Place"/>).
    /// </summary>
    public (int X, int Y) ToolbarPosition((int Width, int Height) toolbar)
    {
        var m = MonitorBounds;
        return ToolbarPlacement.Place(Crop, toolbar, new RectI(0, 0, m.Width, m.Height));
    }
}
