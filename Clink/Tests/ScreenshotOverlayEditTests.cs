using System.IO;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// Editing on the screen (mode "before") without a screen: the phase logic of the capture overlay (<see cref="OverlayEditState"/>
/// wired to a <see cref="RegionSelection"/> the way the overlay session does), the toolbar placement for the compact toolbar, the
/// setting and its combo. The windows themselves are checked by hand (docs/screenshots-design.md, S5 checklist).
/// </summary>
internal static class ScreenshotOverlayEditTests
{
    private static readonly RectI M0 = new(0, 0, 1920, 1080), M1 = new(1920, 0, 1280, 1024);
    private static readonly IReadOnlyList<RectI> TwoMonitors = new[] { M0, M1 };

    private sealed class Wired
    {
        public readonly RegionSelection Selection;
        public readonly OverlayEditState State;
        public readonly List<OverlayPhase> Outcomes = new();
        public long Now;

        public Wired(bool editBefore, IReadOnlyList<RectI>? monitors = null)
        {
            Selection = new RegionSelection(monitors ?? TwoMonitors);
            State = new OverlayEditState(Selection.Monitors, editBefore, () => Now);
            Selection.Changed += State.NoteInput;
            Selection.Committed += r => Outcomes.Add(State.SelectionCommitted(r));
        }

        public void Drag(int x1, int y1, int x2, int y2)
        {
            Selection.BeginDrag(x1, y1);
            Selection.MoveCursor(x2, y2);
            Selection.EndDrag(x2, y2);
        }
    }

    [UnitTest]
    public static void Without_editing_a_committed_selection_completes_the_pick()
    {
        var w = new Wired(editBefore: false);
        w.Drag(100, 100, 400, 300);
        Check.Equal(new[] { OverlayPhase.Done }, w.Outcomes.ToArray());
        Check.Equal(OverlayPhase.Done, w.State.Phase);
        Check.Equal(-1, w.State.MonitorIndex, "nothing is edited");
        Check.True(w.State.IsEnded);
    }

    [UnitTest]
    public static void In_mode_before_the_mouse_up_enters_the_editing_phase_instead_of_committing()
    {
        var w = new Wired(editBefore: true);
        w.Drag(100, 100, 400, 300);
        Check.Equal(new[] { OverlayPhase.Editing }, w.Outcomes.ToArray());
        Check.True(w.State.IsEditing);
        Check.False(w.State.IsEnded, "the pick is not complete");
        Check.Equal(0, w.State.MonitorIndex);
        Check.Equal(w.Selection.Result, w.State.Crop, "the crop starts as the selection");
        Check.Equal(w.State.Crop, w.State.CropVirtual, "(on the first monitor the two are the same)");
        Check.Equal(new RectI(100, 100, 301, 201), w.State.Crop, "a drag between two pixels holds both");
    }

    [UnitTest]
    public static void The_crop_is_in_the_pixels_of_the_monitor_it_is_on()
    {
        var w = new Wired(editBefore: true);
        w.Drag(2000, 100, 2299, 299);
        Check.Equal(OverlayPhase.Editing, w.State.Phase);
        Check.Equal(1, w.State.MonitorIndex);
        Check.Equal(new RectI(80, 100, 300, 200), w.State.Crop, "monitor-local");
        Check.Equal(new RectI(2000, 100, 300, 200), w.State.CropVirtual, "and as virtual-screen pixels");
        Check.Equal(M1, w.State.MonitorBounds);
    }

    [UnitTest]
    public static void Enter_without_a_band_edits_the_whole_monitor_under_the_pointer()
    {
        var w = new Wired(editBefore: true);
        w.Selection.MoveCursor(2500, 500);
        w.Selection.Confirm();   // Enter
        Check.Equal(OverlayPhase.Editing, w.State.Phase);
        Check.Equal(1, w.State.MonitorIndex);
        Check.Equal(new RectI(0, 0, 1280, 1024), w.State.Crop, "the whole monitor, in its own pixels");
        Check.Equal(PickOutcome.WholeMonitor, w.Selection.Outcome);
    }

    [UnitTest]
    public static void A_selection_over_several_monitors_is_not_edited_in_the_overlay()
    {
        var w = new Wired(editBefore: true);
        w.Drag(1800, 100, 2100, 300);
        Check.Equal(new[] { OverlayPhase.SpansMonitors }, w.Outcomes.ToArray());
        Check.True(w.State.IsEnded);
        Check.False(w.State.IsEditing);
        Check.Equal(w.Selection.Result, w.State.Selection, "the service composes this rectangle for the editor window");
    }

    [UnitTest]
    public static void A_selection_in_a_gap_between_monitors_or_past_a_monitors_edge_is_handled()
    {
        var apart = new[] { new RectI(0, 0, 100, 100), new RectI(200, 0, 100, 100) };
        var gap = new Wired(true, apart);
        gap.State.SelectionCommitted(new RectI(110, 10, 50, 50));
        Check.Equal(OverlayPhase.Cancelled, gap.State.Phase, "over nothing at all");

        var edge = new Wired(true, apart);
        edge.State.SelectionCommitted(new RectI(80, 10, 50, 50));   // 20 px on the first monitor, the rest in the gap
        Check.Equal(OverlayPhase.Editing, edge.State.Phase);
        Check.Equal(new RectI(80, 10, 20, 50), edge.State.Crop, "only the part on the monitor");
    }

    [UnitTest]
    public static void Dragging_a_crop_handle_updates_the_crop_replaces_the_toolbar_and_stays_on_the_monitor()
    {
        var w = new Wired(true);
        w.Drag(100, 100, 399, 299);   // 300 x 200 at (100,100)
        int changes = 0;
        w.State.CropChanged += () => changes++;
        var bar = (Width: 800, Height: 120);
        Check.Equal((100, 308), w.State.ToolbarPosition(bar), "below the crop");

        w.State.SetCrop(new RectI(100, 100, 500, 400));   // the bottom-right handle dragged
        Check.Equal(1, changes);
        Check.Equal(new RectI(100, 100, 500, 400), w.State.Crop);
        Check.Equal((100, 508), w.State.ToolbarPosition(bar), "follows the crop");

        w.State.SetCrop(new RectI(100, 100, 500, 400));
        Check.Equal(1, changes, "an unchanged crop is not a change");
        w.State.SetCrop(RectI.Empty);
        Check.Equal(new RectI(100, 100, 500, 400), w.State.Crop, "an empty crop is ignored");

        w.State.SetCrop(new RectI(-50, -50, 5000, 5000));   // dragged past the screen
        Check.Equal(new RectI(0, 0, 1920, 1080), w.State.Crop, "kept on the monitor");
        Check.Equal((0, 952), w.State.ToolbarPosition(bar), "a crop that fills the monitor: the toolbar sits inside, at the bottom");
        Check.Equal(2, changes);
    }

    [UnitTest]
    public static void Done_ends_the_editing_once_and_Esc_cancels_it_and_nothing_changes_after_the_end()
    {
        var done = new Wired(true);
        done.Drag(100, 100, 399, 299);
        Check.True(done.State.Confirm(), "Enter / Done");
        Check.Equal(OverlayPhase.Done, done.State.Phase);
        Check.False(done.State.Confirm(), "once");
        done.State.Cancel();
        Check.Equal(OverlayPhase.Done, done.State.Phase, "a cancel after the end changes nothing");
        done.State.SetCrop(new RectI(0, 0, 10, 10));
        Check.Equal(new RectI(100, 100, 300, 200), done.State.Crop, "and neither does the crop");

        var esc = new Wired(true);
        esc.Drag(100, 100, 399, 299);
        esc.State.Cancel();
        Check.Equal(OverlayPhase.Cancelled, esc.State.Phase);
        Check.False(esc.State.Confirm(), "no Done after Esc");

        var early = new Wired(true);
        Check.False(early.State.Confirm(), "nothing to confirm before a selection");
        early.State.Cancel();
        Check.Equal(OverlayPhase.Cancelled, early.State.Phase);
        Check.Equal(OverlayPhase.Cancelled, early.State.SelectionCommitted(new RectI(0, 0, 50, 50)), "a late commit is ignored");
    }

    [UnitTest]
    public static void Editor_input_resets_the_idle_timer()
    {
        var w = new Wired(true);
        var timeout = TimeSpan.FromMinutes(2);
        w.Now = 1_000;
        w.State.NoteInput();
        w.Drag(100, 100, 399, 299);
        w.Now = 1_000 + 119_000;
        Check.False(w.State.IsIdle(timeout), "just under two minutes");
        w.State.NoteInput();   // a key, a click or a mouse move in the editing window
        w.Now = 1_000 + 119_000 + 119_000;
        Check.False(w.State.IsIdle(timeout), "the input restarted the two minutes");
        w.Now += 2_000;
        Check.True(w.State.IsIdle(timeout), "and without any more the watchdog cancels");
    }

    // ------------------------------------------------------------------ the toolbar

    [UnitTest]
    public static void The_compact_toolbar_is_placed_at_all_four_screen_edges_and_for_a_full_monitor_selection()
    {
        var work = new RectI(0, 0, 1920, 1080);
        var bar = (Width: 900, Height: 150);   // about what the compact toolbar measures (three rows) at 100%
        // bottom edge: below does not fit, so above
        Check.Equal((600, 600 - 8 - 150), ToolbarPlacement.Place(new RectI(600, 600, 400, 480), bar, work), "selection touching the bottom: above it");
        // top edge: below
        Check.Equal((600, 8 + 100), ToolbarPlacement.Place(new RectI(600, 0, 400, 100), bar, work), "selection touching the top: below it");
        // left edge: left-aligned with the selection at the edge
        Check.Equal((0, 508), ToolbarPlacement.Place(new RectI(0, 200, 300, 300), bar, work), "selection touching the left edge");
        // right edge: pulled back so that the toolbar is whole
        Check.Equal((1020, 508), ToolbarPlacement.Place(new RectI(1620, 200, 300, 300), bar, work), "selection touching the right edge");
        // the whole monitor: inside, at the bottom, left-aligned
        Check.Equal((0, 1080 - 8 - 150), ToolbarPlacement.Place(work, bar, work), "a full-monitor selection: inside at the bottom");
        // a selection nearly as tall as the monitor, with room neither above nor below
        Check.Equal((10, 1070 - 8 - 150), ToolbarPlacement.Place(new RectI(10, 20, 1900, 1050), bar, work), "no room above or below: inside, 8 px above the selection's bottom");
        // the state places it in the monitor's own pixels, whatever the monitor's origin: a whole second monitor
        var state = new OverlayEditState(TwoMonitors, true);
        Check.Equal(OverlayPhase.Editing, state.SelectionCommitted(M1));
        Check.Equal((0, 1024 - 8 - 150), state.ToolbarPosition((900, 150)), "inside at the bottom of that monitor");
    }

    // ------------------------------------------------------------------ the setting

    [UnitTest]
    public static void Mode_before_is_a_known_mode_that_survives_a_save_and_a_load()
    {
        Check.Equal(new[] { "none", "before", "after" }, ScreenshotModes.All);
        foreach (string mode in ScreenshotModes.All) Check.Equal(mode, ScreenshotModes.Normalize(mode), mode);
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var s = Settings.FromJson("""{ "ScreenshotMode": " Before " }""");
        Check.Equal("before", s.ScreenshotMode, "tidied, not reset to none");
        s.Save();
        Check.Equal("before", Settings.Load().ScreenshotMode);
    }

    [UnitTest]
    public static void The_screenshots_tab_combo_has_an_enabled_item_for_each_of_the_three_modes()
    {
        // MainWindow fills the combo from this list (enabled items, Tag = value), so the list is what the user can pick
        var choices = ScreenshotModes.Choices;
        Check.Equal(3, choices.Length, "one item per mode");
        Check.Equal(new[] { "after", "before", "none" }, choices.Select(c => c.Value).OrderBy(x => x).ToArray());
        Check.Equal(ScreenshotModes.All.OrderBy(x => x).ToArray(), choices.Select(c => c.Value).OrderBy(x => x).ToArray(), "every known mode");
        foreach (var (value, label, tip) in choices)
        {
            Check.Equal(value, ScreenshotModes.Normalize(value), "the value is stored as it is: " + value);
            Check.False(string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(tip), "label and tooltip: " + value);
            Check.False((label + tip).Contains("coming soon", StringComparison.OrdinalIgnoreCase), "no 'coming soon' left: " + value);
        }
        Check.Equal(3, choices.Select(c => c.Label).Distinct().Count(), "distinct labels");
        Check.Equal("Edit on screen", choices.First(c => c.Value == ScreenshotModes.Before).Label);
    }

    // ------------------------------------------------------------------ keys, idle, rebase

    private static OverlayKeyAction Key(System.Windows.Input.Key key, bool gesture = false, bool selection = false, bool selectTool = true,
        OverlayKeyFocus focus = OverlayKeyFocus.Canvas, bool repeat = false, System.Windows.Input.ModifierKeys mods = System.Windows.Input.ModifierKeys.None) =>
        OverlayEditKeys.Decide(key, mods, gesture, selection, selectTool, focus, repeat);

    [UnitTest]
    public static void Esc_drops_a_gesture_then_deselects_then_resets_the_tool_and_only_then_cancels_the_capture()
    {
        var esc = System.Windows.Input.Key.Escape;
        // the canvas does each step; walk them the way it does
        bool gesture = true, selection = true, selectTool = false;
        var steps = new List<OverlayKeyAction>();
        for (int i = 0; i < 4; i++)
        {
            var a = Key(esc, gesture, selection, selectTool);
            steps.Add(a);
            if (a != OverlayKeyAction.Canvas) break;
            if (gesture) gesture = false; else if (selection) selection = false; else selectTool = true;
        }
        Check.Equal(new[] { OverlayKeyAction.Canvas, OverlayKeyAction.Canvas, OverlayKeyAction.Canvas, OverlayKeyAction.Cancel }, steps.ToArray(), "gesture, deselect, tool reset, then cancel");
        Check.Equal(OverlayKeyAction.Canvas, Key(esc, gesture: true), "a drag alone");
        Check.Equal(OverlayKeyAction.Canvas, Key(esc, selection: true), "a selection alone");
        Check.Equal(OverlayKeyAction.Canvas, Key(esc, selectTool: false), "another tool alone");
        Check.Equal(OverlayKeyAction.Cancel, Key(esc, focus: OverlayKeyFocus.Button), "a focused button does not keep Esc from cancelling");
        Check.Equal(OverlayKeyAction.Pass, Key(esc, focus: OverlayKeyFocus.Typing), "a text box / slider / combo has its own Esc");
        Check.Equal(OverlayKeyAction.Canvas, Key(esc, mods: System.Windows.Input.ModifierKeys.Control), "Ctrl+Esc is not a cancel");
    }

    [UnitTest]
    public static void Enter_and_Space_reach_a_focused_button_and_a_held_Enter_does_not_finish_the_edit()
    {
        var enter = System.Windows.Input.Key.Enter;
        var space = System.Windows.Input.Key.Space;
        Check.Equal(OverlayKeyAction.Commit, Key(enter), "Enter finishes");
        Check.Equal(OverlayKeyAction.Swallow, Key(enter, repeat: true), "a held Enter (from choosing the area) does not");
        Check.Equal(OverlayKeyAction.Pass, Key(enter, focus: OverlayKeyFocus.Button), "Enter on a button presses it");
        Check.Equal(OverlayKeyAction.Pass, Key(enter, focus: OverlayKeyFocus.Button, repeat: true));
        Check.Equal(OverlayKeyAction.Pass, Key(enter, focus: OverlayKeyFocus.Typing), "typing");
        Check.Equal(OverlayKeyAction.Canvas, Key(enter, mods: System.Windows.Input.ModifierKeys.Alt), "Alt+Enter is not Done");
        Check.Equal(OverlayKeyAction.Swallow, Key(space), "the view is fixed: Space does nothing");
        Check.Equal(OverlayKeyAction.Pass, Key(space, focus: OverlayKeyFocus.Button), "Space on a button presses it");
        Check.Equal(OverlayKeyAction.Pass, Key(space, focus: OverlayKeyFocus.Typing));
        Check.Equal(OverlayKeyAction.Canvas, Key(System.Windows.Input.Key.Delete), "other keys are the canvas's");
        Check.Equal(OverlayKeyAction.Pass, Key(System.Windows.Input.Key.Delete, focus: OverlayKeyFocus.Typing));
    }

    [UnitTest]
    public static void The_idle_watchdog_is_off_while_a_dialog_is_open_and_counts_again_from_its_close()
    {
        var w = new Wired(true);
        var timeout = TimeSpan.FromMinutes(2);
        w.Drag(100, 100, 399, 299);
        w.Now = 1_000;
        w.State.NoteInput();
        w.State.Suspend();
        w.Now += 30 * 60_000;
        Check.False(w.State.IsIdle(timeout), "half an hour in the Insert-image dialog: not idle");
        w.State.Resume();
        Check.False(w.State.IsIdle(timeout), "closing the dialog is input");
        w.Now += 119_000;
        Check.False(w.State.IsIdle(timeout));
        w.Now += 2_000;
        Check.True(w.State.IsIdle(timeout), "and after the dialog, two quiet minutes cancel as before");
    }

    [UnitTest]
    public static void Rebase_with_objects_straddling_the_crop_edge_keeps_the_sizes_and_exports()
    {
        var img = new BgraImage(200, 150);
        for (int y = 0; y < 150; y++)
            for (int x = 0; x < 200; x++)
                img.SetPixel(x, y, (byte)(x % 256), (byte)(y * 3 % 256), (byte)((x * y) % 256));
        var crop = new RectI(50, 40, 100, 70);
        var doc = DocumentState.Create(img).WithCrop(crop);
        EditorObject[] objects =
        {
            new BlurRegion(0, new RectI(30, 30, 40, 30), 2, false, 4),   // pixelate across the left/top edge
            new BlurRegion(0, new RectI(130, 90, 40, 40), 0, true, 4),   // blur across the right/bottom edge
            new SmartEraseRect(0, new RectI(40, 60, 20, 20)),
            new SpotlightBox(0, new RectI(120, 30, 60, 40), 0.5, 10, false, 0),
            new RectOutline(0, new System.Windows.Rect(140, 100, 30, 30), System.Windows.Media.Colors.Red, 3),
        };
        foreach (var o in objects) doc = doc.Add(o, out _);
        var rebased = doc.Rebase(crop);
        Check.Equal(100, rebased.Base.Width);
        Check.Equal(70, rebased.Base.Height);
        Check.Equal(new RectI(0, 0, 100, 70), rebased.Crop);
        Check.Equal(doc.Raster.Count + doc.Vector.Count, rebased.Raster.Count + rebased.Vector.Count);
        var expected = Exporter.Export(doc);
        var actual = Exporter.Export(rebased);
        Check.Equal((100, 70), (expected.Width, expected.Height));
        Check.Equal((100, 70), (actual.Width, actual.Height));   // (no byte equality: the effects at the edge read the cut picture)
    }

    [UnitTest]
    public static void The_idle_limit_while_editing_is_twenty_minutes_and_while_selecting_two()
    {
        var w = new Wired(true);
        Check.Equal(TimeSpan.FromMinutes(2), w.State.IdleLimit, "selecting");
        w.Now = 1_000;
        w.State.NoteInput();
        w.Now += 3 * 60_000;
        Check.True(w.State.IsIdle(w.State.IdleLimit), "three quiet minutes while selecting: idle");
        w.Drag(100, 100, 399, 299);
        Check.True(w.State.IsEditing);
        Check.Equal(TimeSpan.FromMinutes(20), w.State.IdleLimit, "editing");
        w.Now += 19 * 60_000;
        Check.False(w.State.IsIdle(w.State.IdleLimit), "19 quiet minutes while editing: not idle");
        w.Now += 2 * 60_000;
        Check.True(w.State.IsIdle(w.State.IdleLimit), "21: idle");
    }

    [UnitTest]
    public static void Enter_on_a_focused_toggle_commits_but_Space_on_it_does_not()
    {
        var K = System.Windows.Input.Key.Enter;
        Check.Equal(OverlayKeyFocus.Canvas, OverlayEditKeys.Classify(K, typing: false, buttonBase: true, pressesEnter: false), "a toggle: Enter is not its press");
        Check.Equal(OverlayKeyFocus.Button, OverlayEditKeys.Classify(K, false, true, true), "a real button");
        Check.Equal(OverlayKeyFocus.Button, OverlayEditKeys.Classify(System.Windows.Input.Key.Space, false, true, false), "Space presses any button base");
        Check.Equal(OverlayKeyFocus.Typing, OverlayEditKeys.Classify(K, true, true, true));
        Check.Equal(OverlayKeyAction.Commit, Key(K, focus: OverlayEditKeys.Classify(K, false, true, false)));
        Check.Equal(OverlayKeyAction.Pass, Key(System.Windows.Input.Key.Space, focus: OverlayEditKeys.Classify(System.Windows.Input.Key.Space, false, true, false)));
    }

    [UnitTest]
    public static void A_close_during_the_insert_dialogs_waits_until_they_return()
    {
        var w = new Wired(true);
        w.Drag(100, 100, 399, 299);
        Check.False(w.State.TryDeferClose(), "no insert: closes at once");
        w.State.BeginInsert();
        Check.True(w.State.TryDeferClose(), "the picker is modal: deferred");
        Check.True(w.State.TryDeferClose());
        Check.True(w.State.EndInsert(), "the dialog returned: the deferred close runs");
        Check.False(w.State.EndInsert(), "once");
        w.State.BeginInsert();
        Check.False(w.State.EndInsert(), "nothing was deferred: nothing to run");
        Check.False(w.State.TryDeferClose());
    }

    [UnitTest]
    public static void Rebase_moves_every_kind_of_object_and_exports_the_same_pixels_as_the_cropped_original()
    {
        var gradient = new BgraImage(120, 90);
        for (int y = 0; y < 90; y++)
            for (int x = 0; x < 120; x++)
                gradient.SetPixel(x, y, (byte)(x * 2), (byte)(y * 2 + 10), (byte)((x + y) % 256));
        var insert = new BgraImage(6, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 6; x++)
                insert.SetPixel(x, y, 200, (byte)(x * 40), 30);
        var crop = new RectI(20, 15, 80, 60);
        var red = System.Windows.Media.Colors.Red;
        System.Windows.Point P(double x, double y) => new(x, y);
        var doc = DocumentState.Create(gradient).WithCrop(crop);
        EditorObject[] objects =
        {
            new PenStroke(0, System.Collections.Immutable.ImmutableArray.Create(P(40, 30), P(50, 35), P(60, 30)), red, 3),
            new LineShape(0, P(35, 25), P(85, 60), red, 2, true),
            new FreehandArrow(0, System.Collections.Immutable.ImmutableArray.Create(P(45, 55), P(55, 62), P(70, 58)), red, 3),
            new RectOutline(0, new System.Windows.Rect(30, 28, 20, 12), red, 2),
            new TextNote(0, P(50, 40), "Hi", 16, red, true, TextBackdrop.None, System.Windows.Media.Colors.Black),
            new BlurRegion(0, new RectI(45, 35, 15, 12), 2, false, 4),
            new BlurRegion(0, new RectI(60, 40, 16, 12), 0, true, 4),
            new HighlightRect(0, new RectI(70, 25, 15, 8), System.Windows.Media.Colors.Yellow),
            new HighlightStroke(0, System.Collections.Immutable.ImmutableArray.Create(P(35, 50), P(60, 52)), 6, System.Windows.Media.Colors.Yellow),
            new SpotlightBox(0, new RectI(50, 30, 30, 20), 0.5, 0, false, 0),
            new MagnifyBox(0, new RectI(30, 40, 8, 6), new RectI(75, 55, 16, 12), red, 2, false),
            new SmartEraseRect(0, new RectI(38, 22, 6, 5)),
            new InsertedImage(0, insert, new RectI(80, 45, 6, 4), 1),
        };
        foreach (var o in objects) doc = doc.Add(o, out _);

        var rebased = doc.Rebase(crop);
        Check.Equal(80, rebased.Base.Width);
        Check.Equal(60, rebased.Base.Height);
        Check.Equal(new RectI(0, 0, 80, 60), rebased.Crop, "the crop is the whole new base");
        Check.Equal(doc.Raster.Count, rebased.Raster.Count);
        Check.Equal(doc.Vector.Count, rebased.Vector.Count);
        Check.Equal(doc.NextId, rebased.NextId);
        Check.Equal(doc.Base.GetPixel(crop.X, crop.Y), rebased.Base.GetPixel(0, 0), "the base is the cropped picture");

        var pen = (PenStroke)rebased.Vector[0];
        Check.Equal(P(20, 15), pen.Pts[0]);
        var line = (LineShape)rebased.Vector[1];
        Check.Equal((P(15, 10), P(65, 45)), (line.A, line.B));
        Check.Equal(P(25, 40), ((FreehandArrow)rebased.Vector[2]).Pts[0]);
        Check.Equal(new System.Windows.Rect(10, 13, 20, 12), ((RectOutline)rebased.Vector[3]).R);
        Check.Equal(P(30, 25), ((TextNote)rebased.Vector[4]).Origin);
        var raster = rebased.Raster;
        Check.Equal(new RectI(25, 20, 15, 12), ((BlurRegion)raster[0]).R);
        Check.Equal(new RectI(40, 25, 16, 12), ((BlurRegion)raster[1]).R);
        Check.Equal(new RectI(50, 10, 15, 8), ((HighlightRect)raster[2]).R);
        Check.Equal(P(15, 35), ((HighlightStroke)raster[3]).Pts[0]);
        Check.Equal(new RectI(30, 15, 30, 20), ((SpotlightBox)raster[4]).Box);
        var mag = (MagnifyBox)raster[5];
        Check.Equal((new RectI(10, 25, 8, 6), new RectI(55, 40, 16, 12)), (mag.Source, mag.Dest));
        Check.Equal(new RectI(18, 7, 6, 5), ((SmartEraseRect)raster[6]).R);
        Check.Equal(new RectI(60, 30, 6, 4), ((InsertedImage)raster[7]).Dest);

        // everything but the text is byte for byte the cropped original's
        var noText = DocumentState.Create(gradient).WithCrop(crop);
        foreach (var o in objects) if (o is not TextNote) noText = noText.Add(o, out _);
        var expected0 = Exporter.Export(noText);
        var actual0 = Exporter.Export(noText.Rebase(crop));
        Check.Equal((expected0.Width, expected0.Height), (actual0.Width, actual0.Height));
        Check.True(expected0.AsSpan().SequenceEqual(actual0.AsSpan()), "without text the export is byte for byte the cropped original's");

        // the text rasterizer may place glyphs on a slightly different sub-pixel grid after the move: one level of tolerance
        var expected = Exporter.Export(doc);
        var actual = Exporter.Export(rebased);
        Check.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int worst = 0;
        var ea = expected.AsSpan();
        var aa = actual.AsSpan();
        for (int i = 0; i < ea.Length; i++) worst = Math.Max(worst, Math.Abs(ea[i] - aa[i]));
        Check.True(worst <= 1, "with text the export differs by at most 1 level per channel (was " + worst + ")");

        Check.True(ReferenceEquals(doc, doc.Rebase(RectI.Empty)), "an empty crop changes nothing");
    }
}
