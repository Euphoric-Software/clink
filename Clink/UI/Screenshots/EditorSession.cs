using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Services;

namespace Clink.UI.Screenshots;

internal enum EditorTool { Select, Pen, Line, Arrow, StraightArrow, Rectangle, Text, Crop, Blur, Pixelate, Highlight, Spotlight, Magnify, Erase }

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
        Composer = RasterOps.NewComposer(out var ops);
        Ops = ops;
    }

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private ComposeQueue? _queue;
    private bool _composeSync;
    private bool _warnedMany;

    /// <summary>The raster band is this many objects or more: the editor says so (each one is a pass over its area on every recompose).</summary>
    public const int RasterWarnCount = 50;

    /// <summary>The composer of what is shown (live preview: the sharpening stage is deferred). Used by <see cref="Queue"/> only, on its worker.</summary>
    public RasterComposer Composer { get; }
    internal RasterOps Ops { get; }

    /// <summary>The background composition of <see cref="State"/> for the canvas (made on first use; tests that need no pictures never start a thread).</summary>
    public ComposeQueue Queue => _queue ??= CreateQueue();

    private ComposeQueue CreateQueue()
    {
        var q = new ComposeQueue(Composer, a =>
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(DispatcherPriority.Render, a);
        });
        q.Synchronous = _composeSync;
        Composer.DeferSharpness = !_composeSync;
        q.Failed += message => Notice?.Invoke(message);   // the status line: "Preview failed — retrying"
        q.Recovered += () => Notice?.Invoke("");          // ... and cleared when a compose works again
        return q;
    }

    /// <summary>Composes on the calling thread and shows at once (tests). Set before the canvas attaches.</summary>
    public bool ComposeSynchronously
    {
        get => _composeSync;
        set
        {
            _composeSync = value;
            Composer.DeferSharpness = !value;
            if (_queue != null) _queue.Synchronous = value;
        }
    }

    /// <summary>A short message for the user (the window shows it in its status line).</summary>
    public event Action<string>? Notice;

    /// <summary>Where Ctrl+V reads a picture from (a made-up one in tests).</summary>
    public IImageClipboard Clipboard { get; set; } = new SystemImageClipboard();

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

    private void Changed()
    {
        CheckRasterCount();
        StateChanged?.Invoke();
    }

    private void CheckRasterCount()
    {
        int n = _undo.Current.Raster.Count;
        if (n >= RasterWarnCount && !_warnedMany)
        {
            _warnedMany = true;
            Log.Warn($"screenshot editor: {n} raster objects (blur, highlight, spotlight, magnify, erase, images): editing gets slower with each");
            Notice?.Invoke($"{n} effects on this picture: more of them make editing slower. Consider flattening by saving and reopening.");
        }
        else if (n < RasterWarnCount) _warnedMany = false;
    }

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
        FlushPendingInserts();
        return pushed;
    }

    public void CancelPreview()
    {
        if (_preview == null) return;
        _preview = null;
        FixSelection();
        Changed();
        FlushPendingInserts();
    }

    /// <summary>A one-step edit: <paramref name="next"/> becomes the document.</summary>
    public bool Apply(DocumentState next, int? select = null)
    {
        _preview = null;
        bool pushed = _undo.Push(next);
        if (select is int id && next.Find(id) != null) SelectedId = id;
        FixSelection();
        Changed();
        FlushPendingInserts();   // (a one-step edit drops a gesture's preview: the pictures that waited for it go in now)
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
        FlushPendingInserts();
        return ok;
    }

    public bool Redo()
    {
        _preview = null;
        bool ok = _undo.Redo();
        FixSelection();
        Changed();
        FlushPendingInserts();
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

    // ------------------------------------------------------------------ raster tools: creating objects

    /// <summary>The spotlight gradient's falloff in picture pixels for the current option: that percentage of the picture's shorter side.</summary>
    internal int FalloffPx() => FalloffPx(Options.SpotFalloffPercent, Committed.Base.Bounds);

    internal static int FalloffPx(int percent, RectI bounds) => Math.Max(1, (int)Math.Round(Math.Min(bounds.Width, bounds.Height) * percent / 100.0));

    /// <summary>A new object of the raster tool <paramref name="tool"/> over <paramref name="r"/> with the tool's options; null for a tool that makes none of those.</summary>
    internal EditorObject? NewRasterObject(EditorTool tool, RectI r)
    {
        var o = Options;
        switch (tool)
        {
            case EditorTool.Blur: return new BlurRegion(0, r, o.BlurRadius, false, o.PixelateBlock);
            case EditorTool.Pixelate: return new BlurRegion(0, r, o.BlurRadius, true, o.PixelateBlock);
            case EditorTool.Highlight: return new HighlightRect(0, r, o.HighlightColorValue);
            case EditorTool.Spotlight: return new SpotlightBox(0, r, o.SpotDim, o.SpotRadius, o.SpotGradient, FalloffPx());
            case EditorTool.Erase: return new SmartEraseRect(0, r);
            case EditorTool.Magnify: return NewMagnify(r);
            default: return null;
        }
    }

    /// <summary>A magnifier of <paramref name="source"/>: the enlarged copy appears beside it (see <see cref="MagnifyLayout.PlaceDest"/>).</summary>
    internal MagnifyBox NewMagnify(RectI source)
    {
        var o = Options;
        var dest = MagnifyLayout.PlaceDest(source, o.MagnifyZoom, Committed.Base.Bounds);
        return new MagnifyBox(0, source, dest, o.MagnifyOutlineColorValue, o.MagnifyOutline ? o.MagnifyOutlineWidth : 0, o.MagnifySmooth);
    }

    // ------------------------------------------------------------------ raster tools: options (they also restyle the selected object)

    /// <summary>Blur sigma. While a slider is dragged (<paramref name="final"/> false) only a preview changes; <see cref="CommitPreview"/> at the release is the one undo step.</summary>
    public void SetBlurRadius(int radius, bool final = true)
    {
        Options.BlurRadius = radius;
        Options.Normalize();
        OptionsChanged();
        if (Selected is BlurRegion { Pixelate: false } b) Restyle(Committed.Replace(b with { Radius = Options.BlurRadius }), final);
    }

    public void SetPixelateBlock(int block, bool final = true)
    {
        Options.PixelateBlock = block;
        Options.Normalize();
        OptionsChanged();
        if (Selected is BlurRegion { Pixelate: true } b) Restyle(Committed.Replace(b with { Block = Options.PixelateBlock }), final);
    }

    public void SetHighlightColor(Color c)
    {
        Options.HighlightColor = ScreenshotEditorOptions.ToHex(Color.FromRgb(c.R, c.G, c.B));
        OptionsChanged();
        var rgb = Options.HighlightColorValue;
        if (Selected is HighlightRect h) Apply(Committed.Replace(h with { Color = rgb }));
        else if (Selected is HighlightStroke s) Apply(Committed.Replace(s with { Color = rgb }));
    }

    public void SetHighlightWidth(double width, bool final = true)
    {
        Options.HighlightWidth = width;
        Options.Normalize();
        OptionsChanged();
        if (Selected is HighlightStroke s) Restyle(Committed.Replace(s with { Width = Options.HighlightWidth }), final);
    }

    public void SetHighlightFreehand(bool freehand)
    {
        Options.HighlightFreehand = freehand;
        OptionsChanged();
    }

    public void SetSpotDim(double dim, bool final = true)
    {
        Options.SpotDim = dim;
        Options.Normalize();
        OptionsChanged();
        if (Selected is SpotlightBox sp) Restyle(Committed.Replace(sp with { Dim = Options.SpotDim }), final);
    }

    public void SetSpotRadius(int radius, bool final = true)
    {
        Options.SpotRadius = radius;
        Options.Normalize();
        OptionsChanged();
        if (Selected is SpotlightBox sp) Restyle(Committed.Replace(sp with { Radius = Options.SpotRadius }), final);
    }

    public void SetSpotGradient(bool gradient)
    {
        Options.SpotGradient = gradient;
        OptionsChanged();
        if (Selected is SpotlightBox sp) Apply(Committed.Replace(sp with { Gradient = gradient }));
    }

    public void SetSpotFalloff(int percent, bool final = true)
    {
        Options.SpotFalloffPercent = percent;
        Options.Normalize();
        OptionsChanged();
        if (Selected is SpotlightBox sp) Restyle(Committed.Replace(sp with { FalloffPx = FalloffPx() }), final);
    }

    public void SetMagnifyZoom(double zoom, bool final = true)
    {
        Options.MagnifyZoom = zoom;
        Options.Normalize();
        OptionsChanged();
        if (Selected is MagnifyBox m) Restyle(Committed.Replace(m with { Dest = MagnifyLayout.WithZoom(m.Dest, m.Source, Options.MagnifyZoom, Committed.Base.Bounds) }), final);
    }

    public void SetMagnifyOutline(bool on)
    {
        Options.MagnifyOutline = on;
        OptionsChanged();
        if (Selected is MagnifyBox m) Apply(Committed.Replace(m with { OutlineWidth = on ? Options.MagnifyOutlineWidth : 0 }));
    }

    public void SetMagnifyOutlineColor(Color c)
    {
        Options.MagnifyOutlineColor = ScreenshotEditorOptions.ToHex(c);
        OptionsChanged();
        if (Selected is MagnifyBox m) Apply(Committed.Replace(m with { Outline = Options.MagnifyOutlineColorValue }));
    }

    public void SetMagnifySmooth(bool smooth)
    {
        Options.MagnifySmooth = smooth;
        OptionsChanged();
        if (Selected is MagnifyBox m) Apply(Committed.Replace(m with { Smooth = smooth }));
    }

    public void SetImageOpacity(double opacity, bool final = true)
    {
        Options.ImageOpacity = opacity;
        Options.Normalize();
        OptionsChanged();
        if (Selected is InsertedImage i) Restyle(Committed.Replace(i with { Opacity = Options.ImageOpacity }), final);
    }

    // ------------------------------------------------------------------ adjustments (base picture)

    /// <summary>The brightness / contrast / sharpness of the picture. Dragging a slider (<paramref name="final"/> false) shows a preview; <see cref="CommitPreview"/> at the release is the one undo step.</summary>
    public void SetAdjust(AdjustmentValues values, bool final = true)
    {
        values = new AdjustmentValues(Math.Clamp(values.Brightness, -100, 100), Math.Clamp(values.Contrast, -100, 100), Math.Clamp(values.Sharpness, 0, 200));
        Restyle(values.Equals(Committed.Adjust) ? Committed : Committed with { Adjust = values }, final);
    }

    /// <summary>Back to 0 / 0 / 0: one undo step, or nothing at all (no step, not dirty) when the picture is not adjusted.</summary>
    public bool ResetAdjust()
    {
        if (Committed.Adjust.IsIdentity) { CancelPreview(); return false; }   // (a slider preview, if any, goes: it is back at 0)
        return Apply(Committed with { Adjust = AdjustmentValues.Identity });
    }

    // ------------------------------------------------------------------ inserted images

    /// <summary>
    /// Puts a picture on top of the raster band, scaled to fit within half the picture, around <paramref name="centre"/> (picture
    /// pixels), and selects it. The stored picture is at most the size of the picture it goes on (reduced once, proportions kept: a
    /// bigger one would only cost memory; the callers that can do it on a worker, <see cref="InsertImageFileAsync"/> and
    /// <see cref="PasteImageAsync"/>, have done it already, so here it is a no-op).
    /// </summary>
    public EditorObject InsertImage(BgraImage image, Point centre)
    {
        var picture = Committed.Base;
        image = ImageInput.FitWithin(image, picture.Width, picture.Height);
        var dest = InsertPlacement.Fit(image.Width, image.Height, picture.Width, picture.Height, centre);
        var added = Add(new InsertedImage(0, image, dest, Options.ImageOpacity));
        SetTool(EditorTool.Select);
        return added;
    }

    /// <summary>
    /// Ctrl+V: inserts the clipboard's picture. On the UI thread only the clipboard read itself (and the decoding of the "PNG"
    /// format, whose stream is valid there only); a clipboard bitmap is frozen and converted, scanned for transparency and reduced to
    /// the picture's size on a worker. Ok false (with the reason, also sent to <see cref="Notice"/>) when there is none or it is refused.
    /// </summary>
    public async Task<(bool Ok, string? Error)> PasteImageAsync(Point centre)
    {
        BgraImage? image;
        BitmapSource? bitmap;
        string? error;
        try { (image, bitmap) = Clipboard.Read(out error); }   // the one part that must be on the UI thread (see IImageClipboard.Read)
        catch (Exception ex)
        {
            Log.Warn("screenshot editor: paste: " + ex.Message);
            image = null;
            bitmap = null;
            error = "Could not read the clipboard";
        }
        if (image == null && bitmap == null)
        {
            error ??= "There is no picture on the clipboard";
            Notice?.Invoke(error);
            return (false, error);
        }
        var picture = Committed.Base;
        if (bitmap != null)
        {
            // the frozen bitmap goes to a worker: pixel conversion, the transparency scan and the reduction are not done on the UI thread
            int bw = picture.Width, bh = picture.Height;
            var src = bitmap;
            var (converted, err) = await Task.Run(() =>
            {
                var loaded = ImageInput.FromBitmap(src, out var e);
                return (loaded == null ? null : ImageInput.FitWithin(loaded, bw, bh), e);
            }).ConfigureAwait(false);
            return await OnUi(() => Finish(converted, centre, err ?? "Could not read that picture"));
        }
        var direct = image!;   // (not null: there was a picture and it was not a bitmap)
        if (direct.Width > picture.Width || direct.Height > picture.Height)
        {
            int w = picture.Width, h = picture.Height;
            var reduced = await Task.Run(() => ImageInput.FitWithin(direct, w, h)).ConfigureAwait(false);
            return await OnUi(() => Finish(reduced, centre, null));
        }
        return Finish(direct, centre, null);
    }

    /// <summary>
    /// Inserts a picture file (PNG, JPG, BMP...): read, checked against the size limits (from the header first) and reduced to the
    /// picture's size on a worker, inserted on the UI thread. Ok false with the reason (also sent to <see cref="Notice"/>).
    /// </summary>
    public async Task<(bool Ok, string? Error)> InsertImageFileAsync(string path, Point centre)
    {
        var picture = Committed.Base;
        int w = picture.Width, h = picture.Height;
        var (image, error) = await Task.Run(() =>
        {
            var loaded = ImageInput.LoadFile(path, out var err);
            return (loaded == null ? null : ImageInput.FitWithin(loaded, w, h), err);
        }).ConfigureAwait(false);
        return await OnUi(() => Finish(image, centre, error ?? "Could not read that picture"));
    }

    private (bool Ok, string? Error) Finish(BgraImage? image, Point centre, string? error)
    {
        if (_released) return (false, "The editor was closed");
        if (image == null)
        {
            Notice?.Invoke(error!);
            return (false, error);
        }
        // a gesture is under way (a drag keeps the document it started from and the next object id): the picture waits for its end,
        // so that neither the gesture's result nor the picture is lost and the picture gets its id from the document it lands in
        if (_preview != null) _pendingInserts.Enqueue((image, centre));
        else InsertImage(image, centre);
        return (true, null);
    }

    private readonly Queue<(BgraImage Image, Point Centre)> _pendingInserts = new();

    /// <summary>Inserts the pictures that arrived during a gesture, one undo step each, now that the gesture has ended (no preview).</summary>
    private void FlushPendingInserts()
    {
        while (_preview == null && _pendingInserts.Count > 0 && !_released)
        {
            var (image, centre) = _pendingInserts.Dequeue();
            InsertImage(image, centre);
        }
        if (_released) _pendingInserts.Clear();
    }

    /// <summary>Pictures that finished loading during a gesture and are waiting for it to end (tests).</summary>
    internal int PendingInserts => _pendingInserts.Count;

    private Task<T> OnUi<T>(Func<T> f) => _dispatcher.CheckAccess() ? Task.FromResult(f()) : _dispatcher.InvokeAsync(f).Task;

    private bool _released;

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
            // its own composer, at full quality (both adjustment stages): the display composer belongs to the background worker
            var composer = RasterOps.NewComposer();
            _exported = null;   // the old one goes before the new one is made
            _exported = Clink.Screenshots.Editing.Exporter.Export(doc, composer);
            ExportComposerBytes = composer.Memory.Total;   // transient: held during the export only
            composer.Clear();
            _exportedFor = doc;
        }
        return _exported;
    }

    /// <summary>The bytes the export's own composer held at the end of the last export (it is cleared right after).</summary>
    internal long ExportComposerBytes { get; private set; }

    /// <summary>
    /// What the editor holds, honestly: the base picture(s) and the inserted images reachable from the undo stack, the canvas's
    /// WriteableBitmap (width x height x 4, assuming the canvas is attached), the display composer (its pictures, the spotlight levels
    /// with the peak of a rebuild, the highlight masks), the blur planes kept by <see cref="PlanePool"/>, the exported picture, and
    /// what the export's composer held while it ran. Not while a compose runs (the queue is idle or shut down).
    /// </summary>
    internal EditorMemory Memory()
    {
        // the pictures of every document state the undo stack (and the preview) can reach, each distinct instance once by reference:
        // the base picture(s) (a crop or an adjustment shares it) and every inserted image, including those of undone / replaced states
        var seen = new HashSet<BgraImage>(ReferenceEqualityComparer.Instance);
        long bases = 0, inserted = 0;
        void Count(DocumentState s)
        {
            if (seen.Add(s.Base)) bases += s.Base.Pixels.LongLength;
            foreach (var o in s.Raster)
                if (o is InsertedImage i && seen.Add(i.Image)) inserted += i.Image.Pixels.LongLength;
        }
        foreach (var s in _undo.States) Count(s);
        if (_preview != null) Count(_preview);
        var shown = State.Base;
        return new EditorMemory(Composer.Memory, PlanePool.KeptBytes, _exported?.Pixels.LongLength ?? 0, ExportComposerBytes, bases, (long)shown.Width * shown.Height * 4, inserted);
    }

    internal string MemoryText()
    {
        var m = Memory();
        var d = m.Display;
        static string Mb(long b) => (b / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return $"{Mb(m.Total)} MB held: base picture(s) {Mb(m.Bases)} MB, canvas bitmap {Mb(m.Bitmap)} MB, {d.PictureCount} composer picture(s) {Mb(d.Pictures)} MB, levels+masks {Mb(d.Caches)} MB (peak {Mb(d.PeakCaches)}), " +
               $"inserted images (all undo states) {Mb(m.Inserted)} MB, blur planes {Mb(m.Planes)} MB, exported {Mb(m.Exported)} MB; the export composer held {Mb(m.ExportTransient)} MB while it ran";
    }

    /// <summary>
    /// Drops every cached image (the editor is closing): the background composition is stopped first (a compose under way finishes,
    /// it is never cut off half-way), then the composer and the blur level caches are emptied and the blur plane pool is given back.
    /// </summary>
    public void Release()
    {
        _released = true;
        _pendingInserts.Clear();
        _queue?.Shutdown();
        try { Log.Info("screenshot editor: memory at close: " + MemoryText()); }
        catch (Exception ex) { Log.Warn("screenshot editor: memory: " + ex.Message); }
        _exported = null;
        _exportedFor = null;
        Composer.Clear();
        Ops.SpotCache.Clear();
        PlanePool.Clear();
    }
}

/// <summary>What <see cref="EditorSession.Memory"/> counts. <see cref="Inserted"/> replaces the composer's own count of the shown list's images (they are among these: no double count in <see cref="Total"/>).</summary>
internal readonly record struct EditorMemory(ComposerMemory Display, long Planes, long Exported, long ExportTransient, long Bases, long Bitmap, long Inserted)
{
    public long Total => Display.Pictures + Display.Caches + Planes + Exported + Bases + Bitmap + Inserted;
}
