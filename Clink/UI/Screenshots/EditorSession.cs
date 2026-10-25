using System.Windows;
using System.Windows.Media;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;

namespace Clink.UI.Screenshots;

internal enum EditorTool { Select, Pen, Line, Arrow, StraightArrow, Rectangle, Text, Crop }

/// <summary>
/// The editor's state with no visuals: the document (an immutable <see cref="DocumentState"/>), its undo stack, the current tool and
/// tool options, the selection. A gesture (drag, slider, text edit) shows a transient <see cref="SetPreview"/> state that is
/// pushed on the undo stack only when the gesture ends (<see cref="CommitPreview"/>) and dropped when it is cancelled. The canvas
/// and the toolbar are views of this object; the window turns <see cref="CommitRequested"/> / <see cref="CancelRequested"/> into
/// saving and closing. UI thread.
/// </summary>
internal sealed class EditorSession
{
    private readonly UndoStack<DocumentState> _undo;
    private DocumentState? _preview;
    private DocumentState _saved;
    private BgraImage? _exported;
    private DocumentState? _exportedFor;
    private readonly Action<ScreenshotEditorOptions>? _persist;
    private bool _optionsDirty;

    public EditorSession(DocumentState initial, ScreenshotEditorOptions? options = null, Action<ScreenshotEditorOptions>? persist = null, int undoLimit = 200)
    {
        _undo = new UndoStack<DocumentState>(initial, undoLimit);
        _saved = initial;
        Options = (options ?? new ScreenshotEditorOptions()).Clone();
        Options.Normalize();
        _persist = persist;
    }

    public RasterComposer Composer { get; } = new();

    /// <summary>What is shown: the preview of a gesture in progress, else the current document.</summary>
    public DocumentState State => _preview ?? _undo.Current;
    /// <summary>The document as of the last finished gesture.</summary>
    public DocumentState Committed => _undo.Current;
    public bool HasPreview => _preview != null;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;
    public int UndoDepth => _undo.Count;

    public EditorTool Tool { get; private set; } = EditorTool.Select;
    public ScreenshotEditorOptions Options { get; }
    public int? SelectedId { get; private set; }
    public EditorObject? Selected => SelectedId is int id ? State.Find(id) : null;

    /// <summary>True when the document differs from the one that was opened or last saved (undoing back to it makes it false again).</summary>
    public bool IsDirty => !ReferenceEquals(_undo.Current, _saved);

    public event Action? StateChanged;
    public event Action? CommitRequested;
    public event Action? CancelRequested;

    private void Changed() => StateChanged?.Invoke();

    public void RequestCommit() => CommitRequested?.Invoke();
    public void RequestCancel() => CancelRequested?.Invoke();

    /// <summary>Remembers the current document as the saved one (<see cref="IsDirty"/> is false until the next edit).</summary>
    public void MarkSaved()
    {
        _saved = _undo.Current;
        Changed();
    }

    // ------------------------------------------------------------------ tool and selection

    public void SetTool(EditorTool tool)
    {
        if (tool == Tool) return;
        CancelPreview();
        Tool = tool;
        if (tool == EditorTool.Crop) SelectedId = null;   // the selection is hidden in Crop: Delete / arrows must not act on it
        Changed();
    }

    public void Select(int? id)
    {
        if (id is int i && State.Find(i) == null) id = null;
        if (id == SelectedId) return;
        SelectedId = id;
        Changed();
    }

    // ------------------------------------------------------------------ gestures and edits

    /// <summary>Shows <paramref name="state"/> instead of the document until the gesture ends. Nothing goes on the undo stack.</summary>
    public void SetPreview(DocumentState state)
    {
        _preview = state;
        Changed();
    }

    /// <summary>Ends a gesture: the preview becomes the document (one undo step). <paramref name="select"/> selects an object of it. Returns false when there was no preview or it equals the document.</summary>
    public bool CommitPreview(int? select = null)
    {
        var p = _preview;
        _preview = null;
        bool pushed = p != null && _undo.Push(p);
        if (select is int id) SelectedId = _undo.Current.Find(id) != null ? id : SelectedId;
        FixSelection();
        Changed();
        return pushed;
    }

    public void CancelPreview()
    {
        if (_preview == null) return;
        _preview = null;
        FixSelection();
        Changed();
    }

    /// <summary>A one-step edit: <paramref name="next"/> becomes the document.</summary>
    public bool Apply(DocumentState next, int? select = null)
    {
        _preview = null;
        bool pushed = _undo.Push(next);
        if (select is int id && next.Find(id) != null) SelectedId = id;
        FixSelection();
        Changed();
        return pushed;
    }

    /// <summary>Adds an object on top of its band and selects it. Returns it as stored (with its id).</summary>
    public EditorObject Add(EditorObject proto)
    {
        var next = Committed.Add(proto, out var added);
        Apply(next, added.Id);
        return added;
    }

    public bool Undo()
    {
        _preview = null;
        bool ok = _undo.Undo();
        FixSelection();
        Changed();
        return ok;
    }

    public bool Redo()
    {
        _preview = null;
        bool ok = _undo.Redo();
        FixSelection();
        Changed();
        return ok;
    }

    private void FixSelection()
    {
        if (SelectedId is int id && State.Find(id) == null) SelectedId = null;
    }

    public bool Delete()
    {
        if (SelectedId is not int id) return false;
        var next = Committed.Remove(id);
        SelectedId = null;
        return Apply(next);
    }

    /// <summary>Copies the selected object 12 px down and right (a fresh id) and selects the copy.</summary>
    public bool Duplicate()
    {
        if (Selected is not { } o) return false;
        var moved = ObjectOps.Translate(o, 12, 12);
        var next = Committed.Add(moved, out var added);
        return Apply(next, added.Id);
    }

    public bool Nudge(double dx, double dy)
    {
        if (Selected is not { } o) return false;
        return Apply(Committed.Replace(ObjectOps.Translate(o, dx, dy)));
    }

    /// <summary>Moves the selected object <paramref name="delta"/> places up (+) or down (-) in its band.</summary>
    public bool Reorder(int delta)
    {
        if (SelectedId is not int id) return false;
        var next = Committed.Reorder(id, delta);
        return !ReferenceEquals(next, Committed) && Apply(next);
    }

    public bool ResetCrop() => Apply(Committed.WithCrop(Committed.Base.Bounds));

    // ------------------------------------------------------------------ tool options (they also restyle the selected object)

    public void SetColor(Color c)
    {
        if (Tool == EditorTool.Text || Selected is TextNote)
        {
            Options.TextColor = ScreenshotEditorOptions.ToHex(c);
        }
        else Options.Color = ScreenshotEditorOptions.ToHex(c);
        OptionsChanged();
        if (Selected is { } o && ObjectOps.WithColor(o, c) is { } recoloured) Apply(Committed.Replace(recoloured));
    }

    /// <summary>Sets the line width (and widens the selected object). While a slider is dragged (<paramref name="final"/> false) only a preview changes; <see cref="CommitPreview"/> at the release makes the one undo step.</summary>
    public void SetWidth(double width, bool final = true)
    {
        Options.Width = width;
        Options.Normalize();
        OptionsChanged();
        if (Selected is { } o && ObjectOps.WithWidth(o, Options.Width) is { } wider) Restyle(Committed.Replace(wider), final);
    }

    public void SetFontSize(double size, bool final = true)
    {
        Options.FontSize = size;
        Options.Normalize();
        OptionsChanged();
        if (Selected is TextNote t) Restyle(Committed.Replace(t with { Size = Options.FontSize }), final);
    }

    private void Restyle(DocumentState next, bool final)
    {
        if (final) Apply(next);
        else SetPreview(next);
    }

    public void SetBold(bool bold)
    {
        Options.Bold = bold;
        OptionsChanged();
        if (Selected is TextNote t) Apply(Committed.Replace(t with { Bold = bold }));
    }

    public void SetBackdrop(TextBackdrop backdrop)
    {
        Options.TextBackdrop = backdrop.ToString().ToLowerInvariant();
        OptionsChanged();
        if (Selected is TextNote t) Apply(Committed.Replace(t with { Backdrop = backdrop, BackdropColor = Options.BackdropColorValue }));
    }

    public TextBackdrop BackdropOption => Options.TextBackdrop switch { "box" => TextBackdrop.Box, "none" => TextBackdrop.None, _ => TextBackdrop.Outline };

    private void OptionsChanged()
    {
        _optionsDirty = true;
        Changed();
    }

    /// <summary>Writes the tool options to the settings when they changed (the window does it when it closes).</summary>
    public void FlushOptions()
    {
        if (!_optionsDirty) return;
        _optionsDirty = false;
        _persist?.Invoke(Options.Clone());
    }

    // ------------------------------------------------------------------ output

    /// <summary>The finished picture of the current document (cached until the document changes). The preview is not exported.</summary>
    public BgraImage Export()
    {
        var doc = Committed;
        if (_exported == null || !ReferenceEquals(_exportedFor, doc))
        {
            _exported = Clink.Screenshots.Editing.Exporter.Export(doc, Composer);
            _exportedFor = doc;
        }
        return _exported;
    }

    /// <summary>Drops every cached image (the editor is closing).</summary>
    public void Release()
    {
        _exported = null;
        _exportedFor = null;
        Composer.Clear();
    }
}
