using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Services;

namespace Clink.UI.Screenshots;

/// <summary>
/// The editing surface: shows an <see cref="EditorSession"/> (the raster composite in a WriteableBitmap, one DrawingVisual per vector
/// object, an overlay for selection / crop in screen units, the inline text box) and turns the mouse and keys into session edits.
/// The view is a zoom (<see cref="Zoom"/> = physical pixels per picture pixel, so 100% is exactly 1:1 on a high-DPI screen) and an
/// offset; everything is hit-tested in picture pixels by the pure <see cref="HitTest"/>.
/// </summary>
internal sealed class EditorCanvas : FrameworkElement
{
    private const double MinZoom = 0.02, MaxZoom = 32, MinCrop = 8;
    private static readonly Brush s_dim = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));

    private EditorSession? _session;
    private readonly ContainerVisual _world = new();
    private readonly DrawingVisual _imageVisual = new();
    private readonly ContainerVisual _vectors = new();
    private readonly DrawingVisual _overlay = new();
    private WriteableBitmap? _bitmap;
    private DocumentState? _shownFor;
    private readonly Dictionary<int, (EditorObject Obj, DrawingVisual Visual)> _visuals = new();
    private List<int> _order = new();

    private double _zoom = 1;                // physical pixels per picture pixel
    private Point _offset;                   // DIPs: where the picture's top-left is
    private bool _fit = true;

    private TextBox? _text;
    private int? _editingId;                 // the note being edited (hidden while the box is up)
    private Point _textOrigin;               // picture pixels
    private Point _textInset;                // where the first character sits inside the box, in the box's own units
    private bool _textClosing;
    private TextNote? _textOriginal;

    private Action<Point, ModifierKeys>? _move;
    private Action<Point>? _up;
    private bool _space;
    private RectI? _ghostSource;             // the source frame of a magnifier being dragged out

    public EditorCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        _vectors.Clip = null;
        _world.Children.Add(_imageVisual);
        _world.Children.Add(_vectors);
        AddVisualChild(_world);
        AddVisualChild(_overlay);
        // The pasteboard and the accent are looked up on every render: a theme change only has to ask for one.
        Loaded += (_, _) => { ThemeManager.ThemeChanged -= OnThemeChanged; ThemeManager.ThemeChanged += OnThemeChanged; };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        InvalidateVisual();
        RedrawOverlay();
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    // ------------------------------------------------------------------ hookup

    public EditorSession? Session => _session;

    public void Attach(EditorSession session)
    {
        if (_session != null) Unhook(_session);
        _session = session;
        _session.StateChanged += OnSessionChanged;
        _session.Queue.Completed += OnComposed;
        _session.Queue.Failed += OnComposeFailed;
        _shownFor = null;
        _bitmap = null;
        OnSessionChanged();
        if (ActualWidth > 0) ZoomToFit();
    }

    private void Unhook(EditorSession s)
    {
        s.StateChanged -= OnSessionChanged;
        s.Queue.Completed -= OnComposed;
        s.Queue.Failed -= OnComposeFailed;
    }

    /// <summary>A compose failed: the next session change asks again even for the same state (the queue no longer takes it for one it has).</summary>
    private void OnComposeFailed(string _) => _shownFor = null;

    public void Detach()
    {
        if (_session != null) Unhook(_session);
        CancelTextQuietly();
        _session = null;
        _visuals.Clear();
        _vectors.Children.Clear();
        _bitmap = null;
        _shownFor = null;
        using (_imageVisual.RenderOpen()) { }
    }

    /// <summary>Raised when the zoom or the offset changed.</summary>
    public event Action? ViewChanged;

    /// <summary>The I key: the window opens the picker of pictures to insert. Ctrl+V: the window pastes the clipboard's picture.</summary>
    internal event Action? InsertImageRequested, PasteRequested;

    /// <summary>The middle of what is shown, in picture pixels, kept inside the picture (where an inserted picture goes).</summary>
    internal Point ViewCentreDoc
    {
        get
        {
            var img = ImageSize;
            var c = ToDoc(new Point(ActualWidth / 2, ActualHeight / 2));
            return new Point(Math.Clamp(c.X, 0, img.Width), Math.Clamp(c.Y, 0, img.Height));
        }
    }

    public bool IsTextEditing => _text != null;

    // ------------------------------------------------------------------ the view

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Physical pixels per picture pixel (1 = 100%).</summary>
    public double Zoom => _zoom;

    /// <summary>DIPs per picture pixel: what the pure hit tests are told.</summary>
    public double ScaleDip => _zoom / Dpi;

    private Point ToDoc(Point view) => new((view.X - _offset.X) / ScaleDip, (view.Y - _offset.Y) / ScaleDip);
    private Point ToView(Point doc) => new(_offset.X + doc.X * ScaleDip, _offset.Y + doc.Y * ScaleDip);

    private Size ImageSize => _session == null ? new Size(1, 1) : new Size(_session.State.Base.Width, _session.State.Base.Height);

    public void ZoomToFit()
    {
        _fit = true;
        if (_session == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double dpi = Dpi, margin = 16;
        double availW = Math.Max(1, ActualWidth * dpi - 2 * margin), availH = Math.Max(1, ActualHeight * dpi - 2 * margin);
        var img = ImageSize;
        _zoom = Math.Clamp(Math.Min(1.0, Math.Min(availW / img.Width, availH / img.Height)), MinZoom, MaxZoom);
        CenterImage();
        ApplyView();
    }

    public void ZoomActual() => ZoomTo(1.0, new Point(ActualWidth / 2, ActualHeight / 2));

    public void ZoomTo(double zoom, Point anchorView)
    {
        if (_session == null) return;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _fit = false;
        var doc = ToDoc(anchorView);
        _zoom = zoom;
        double s = ScaleDip;
        _offset = new Point(anchorView.X - doc.X * s, anchorView.Y - doc.Y * s);
        ClampOffset();
        ApplyView();
    }

    public void ZoomBy(double factor, Point anchorView) => ZoomTo(_zoom * factor, anchorView);

    private void CenterImage()
    {
        double s = ScaleDip;
        var img = ImageSize;
        _offset = new Point((ActualWidth - img.Width * s) / 2, (ActualHeight - img.Height * s) / 2);
    }

    /// <summary>Keeps the picture reachable: centred when it is smaller than the view, otherwise no further than a margin off the edges.</summary>
    private void ClampOffset()
    {
        double s = ScaleDip;
        var img = ImageSize;
        double w = img.Width * s, h = img.Height * s, keep = 48;
        double x = w <= ActualWidth ? (ActualWidth - w) / 2 : Math.Clamp(_offset.X, ActualWidth - w - keep, keep);
        double y = h <= ActualHeight ? (ActualHeight - h) / 2 : Math.Clamp(_offset.Y, ActualHeight - h - keep, keep);
        _offset = new Point(x, y);
    }

    public void PanBy(double dx, double dy)
    {
        _fit = false;
        _offset = new Point(_offset.X + dx, _offset.Y + dy);
        ClampOffset();
        ApplyView();
    }

    private void ApplyView()
    {
        double dpi = Dpi, s = _zoom / dpi;
        // whole physical pixels for the picture's corner: at 100% every picture pixel is exactly one screen pixel
        _offset = new Point(Math.Round(_offset.X * dpi) / dpi, Math.Round(_offset.Y * dpi) / dpi);
        _world.Transform = new MatrixTransform(s, 0, 0, s, _offset.X, _offset.Y);
        // HighQuality when shrinking, NearestNeighbor at 1:1 and from 2x up (crisp pixels), Linear in between
        RenderOptions.SetBitmapScalingMode(_imageVisual, _zoom < 1 ? BitmapScalingMode.HighQuality : _zoom == 1 || _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        if (_text != null) { ((ScaleTransform)_text.LayoutTransform).ScaleX = s; ((ScaleTransform)_text.LayoutTransform).ScaleY = s; InvalidateArrange(); }
        RedrawOverlay();
        ViewChanged?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_session == null) return;
        if (_fit) ZoomToFit();
        else { ClampOffset(); ApplyView(); }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_fit) ZoomToFit(); else ApplyView();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // The pasteboard behind the picture; it also makes the whole surface hit-testable for the mouse.
        dc.DrawRectangle(TryFindResource("Panel") as Brush ?? Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    // ------------------------------------------------------------------ visual tree

    protected override int VisualChildrenCount => _text != null ? 3 : 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _world,
        1 => _overlay,
        2 when _text != null => _text,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        _text?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_text != null)
        {
            double s = ScaleDip;
            var at = ToView(_textOrigin);
            _text.Arrange(new Rect(new Point(at.X - _textInset.X * s, at.Y - _textInset.Y * s), _text.DesiredSize));
        }
        return finalSize;
    }

    // ------------------------------------------------------------------ session -> visuals

    private void OnSessionChanged()
    {
        if (_session == null) return;
        UpdateBitmap();
        SyncVectors();
        RedrawOverlay();
    }

    /// <summary>
    /// Makes sure the bitmap exists and asks the session's queue for the composite when what the composer works from changed (the
    /// picture, the raster objects, the adjustments) or a gesture began / ended (a gesture's composes may re-blend with stale blur
    /// levels; the first one after it has fresh ones). The composite arrives later, from a worker, in <see cref="OnComposed"/>; until
    /// then the previous picture stays. The first time, a document without effects shows its base at once (that is what it looks
    /// like); one with raster objects or adjustments shows NOTHING until its first composite (<see cref="FirstCompositePending"/>):
    /// the raw base would flash what a blur or an erase hides.
    /// </summary>
    private void UpdateBitmap()
    {
        var state = _session!.State;
        var baseImage = state.Base;
        bool recreated = false;
        if (_bitmap == null || _bitmap.PixelWidth != baseImage.Width || _bitmap.PixelHeight != baseImage.Height)
        {
            recreated = true;
            _bitmap = new WriteableBitmap(baseImage.Width, baseImage.Height, 96, 96, PixelFormats.Bgra32, null);   // transparent: the pasteboard shows
            using (var dc = _imageVisual.RenderOpen()) dc.DrawImage(_bitmap, new Rect(0, 0, baseImage.Width, baseImage.Height));
            _vectors.Clip = new RectangleGeometry(new Rect(0, 0, baseImage.Width, baseImage.Height));
            FirstCompositePending = !state.Raster.IsEmpty || !state.Adjust.IsIdentity;
            if (!FirstCompositePending) _bitmap.WritePixels(new Int32Rect(0, 0, baseImage.Width, baseImage.Height), baseImage.Pixels, baseImage.Stride, 0);
            _shownFor = null;
        }
        bool interactive = _session.HasPreview;
        if (recreated)
        {
            // the new bitmap is blank: the queue may hold a composite of exactly this state already (it would ignore an equal request,
            // and its dirty region was taken), so ask for the whole picture again
            _shownFor = state;
            _shownInteractive = interactive;
            _session.Queue.RequestFull(state, interactive);
            return;
        }
        if (_shownFor != null && interactive == _shownInteractive && ComposeQueue.SameInput(_shownFor, state))
        {
            _shownFor = state;
            return;
        }
        _shownFor = state;
        _shownInteractive = interactive;
        _session.Queue.Request(state, interactive: interactive);
    }

    private bool _shownInteractive;

    /// <summary>The bitmap has not received its first composite yet (a document with effects shows nothing until then).</summary>
    internal bool FirstCompositePending { get; private set; }

    /// <summary>A finished composite (UI thread): the changed part goes into the bitmap. The image is the composer's own buffer: read here, nowhere else.</summary>
    private void OnComposed(ComposeResult r)
    {
        var bitmap = _bitmap;
        if (_session == null || bitmap == null) return;
        var image = r.Image;
        if (image.Width != bitmap.PixelWidth || image.Height != bitmap.PixelHeight)
        {
            // a composite of another size (the picture changed under it): its dirty area was taken, so ask for the whole of the current one
            _session.Queue.RequestFull(_session.State, _session.HasPreview);
            return;
        }
        var d = r.Dirty.Intersect(image.Bounds);
        if (d.IsEmpty) return;
        bitmap.WritePixels(new Int32Rect(d.X, d.Y, d.Width, d.Height), image.Pixels, image.Stride, d.X, d.Y);
        FirstCompositePending = false;
        ComposedCount++;
    }

    /// <summary>Test seam: how many composites were written into the bitmap.</summary>
    internal int ComposedCount { get; private set; }

    /// <summary>A pixel of the bitmap that is shown (test seam).</summary>
    internal (byte B, byte G, byte R, byte A)? ShownPixel(int x, int y)
    {
        var b = _bitmap;
        if (b == null || x < 0 || y < 0 || x >= b.PixelWidth || y >= b.PixelHeight) return null;
        var px = new byte[4];
        b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2], px[3]);
    }
    private void SyncVectors()
    {
        var vec = _session!.State.Vector;
        bool changed = false;
        var order = new List<int>(vec.Count);
        var live = new HashSet<int>();
        foreach (var o in vec)
        {
            if (o.Id == _editingId) continue;
            live.Add(o.Id);
            order.Add(o.Id);
            if (!_visuals.TryGetValue(o.Id, out var e) || !ReferenceEquals(e.Obj, o))
            {
                _visuals[o.Id] = (o, VectorRenderer.CreateVisual(o));
                changed = true;
            }
        }
        foreach (int gone in _visuals.Keys.Where(k => !live.Contains(k)).ToList()) { _visuals.Remove(gone); changed = true; }
        if (changed || !order.SequenceEqual(_order))
        {
            _vectors.Children.Clear();
            foreach (int id in order) _vectors.Children.Add(_visuals[id].Visual);
            _order = order;
        }
    }

    private Brush AccentBrush => TryFindResource("Accent") as Brush ?? Brushes.DeepSkyBlue;

    private void RedrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_session == null) return;
        var state = _session.State;
        double s = ScaleDip;
        var imageRect = new Rect(ToView(new Point(0, 0)), new Size(state.Base.Width * s, state.Base.Height * s));
        var crop = state.Crop;
        bool cropTool = _session.Tool == EditorTool.Crop;
        if (crop != state.Base.Bounds || cropTool)
        {
            var cropRect = new Rect(ToView(new Point(crop.X, crop.Y)), new Size(crop.Width * s, crop.Height * s));
            dc.DrawGeometry(s_dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(imageRect), new RectangleGeometry(cropRect)));
            dc.DrawRectangle(null, new Pen(Brushes.White, 1), cropRect);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) }, cropRect);
            if (cropTool) foreach (var (_, at) in HitTest.RectHandles(new Rect(crop.X, crop.Y, crop.Width, crop.Height))) DrawHandle(dc, ToView(at));
        }
        if (_session.Selected is { } sel && sel.Id != _editingId && !cropTool)
        {
            var handles = HitTest.HandlesOf(sel);
            if (sel is not LineShape)
            {
                var b = HitTest.Bounds(sel, VectorRenderer.TextBounds);
                if (!b.IsEmpty)
                {
                    var vr = new Rect(ToView(b.TopLeft), ToView(b.BottomRight));
                    vr.Inflate(2, 2);
                    dc.DrawRectangle(null, new Pen(Brushes.White, 1), vr);
                    dc.DrawRectangle(null, new Pen(AccentBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }, vr);
                }
            }
            foreach (var (_, at) in handles) DrawHandle(dc, ToView(at));
            if (sel is MagnifyBox mag) DrawSourceFrame(dc, mag.Source, "Source");
        }
        if (_ghostSource is { } ghost && _move != null) DrawSourceFrame(dc, ghost, null);
    }

    /// <summary>The dashed frame of what a magnifier enlarges: an editing aid, drawn on the overlay only, so it is never part of the exported picture.</summary>
    private void DrawSourceFrame(DrawingContext dc, RectI source, string? label)
    {
        if (source.IsEmpty) return;
        var vr = new Rect(ToView(new Point(source.X, source.Y)), ToView(new Point(source.Right, source.Bottom)));
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), vr);
        dc.DrawRectangle(null, new Pen(Brushes.Black, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) }, vr);
        if (label == null || vr.Width < 40) return;
        var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var pos = new Point(vr.Left + 2, Math.Max(0, vr.Top - text.Height - 1));
        dc.DrawRectangle(s_dim, null, new Rect(pos.X - 2, pos.Y, text.Width + 4, text.Height));
        dc.DrawText(text, pos);
    }

    private void DrawHandle(DrawingContext dc, Point at)
    {
        const double size = 8;
        dc.DrawRectangle(Brushes.White, new Pen(AccentBrush, 1.5), new Rect(at.X - size / 2, at.Y - size / 2, size, size));
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_session == null) return;
        Focus();
        var view = e.GetPosition(this);
        if (_space) { BeginPan(view); e.Handled = true; return; }
        bool wasEditing = IsTextEditing;
        if (wasEditing) CommitText();
        var doc = ToDoc(view);
        CaptureMouse();
        switch (_session.Tool)
        {
            case EditorTool.Select: BeginSelect(doc, e.ClickCount); break;
            case EditorTool.Pen: BeginStroke(doc, arrow: false); break;
            case EditorTool.Arrow: BeginStroke(doc, arrow: true); break;
            case EditorTool.Line: BeginLine(doc, arrow: false); break;
            case EditorTool.StraightArrow: BeginLine(doc, arrow: true); break;
            case EditorTool.Rectangle: BeginRect(doc); break;
            case EditorTool.Text: ReleaseMouseCapture(); BeginTextAt(doc); break;
            case EditorTool.Crop: BeginCrop(doc); break;
            // freehand is the toolbar's toggle only: Shift means "square" with every rectangle tool, the highlight's too
            case EditorTool.Highlight when _session.Options.HighlightFreehand: BeginHighlightStroke(doc); break;
            case EditorTool.Blur or EditorTool.Pixelate or EditorTool.Highlight or EditorTool.Spotlight or EditorTool.Magnify or EditorTool.Erase: BeginRasterRect(_session.Tool, doc); break;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session == null) return;
        var view = e.GetPosition(this);
        if (_move != null) { _move(view, Keyboard.Modifiers); return; }
        UpdateCursor(ToDoc(view));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndGesture(e.GetPosition(this));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle) EndGesture(e.GetPosition(this));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && _session != null)
        {
            Focus();
            BeginPan(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_move != null)
        {
            _move = null;
            _up = null;
            _session?.CancelPreview();
        }
    }

    internal void EndGesture(Point view)
    {
        var up = _up;
        _move = null;
        _up = null;
        up?.Invoke(view);
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_session != null) UpdateCursor(ToDoc(view));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session == null) return;
        var view = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15, view);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) PanBy(e.Delta * 0.5, 0);
        else PanBy(0, e.Delta * 0.5);
        e.Handled = true;
    }

    /// <summary>Drops a gesture that is under way without committing it (its preview goes).</summary>
    private void CancelGesture()
    {
        if (_move == null && _up == null) return;
        _move = null;
        _up = null;
        _session?.CancelPreview();
    }

    internal void BeginPan(Point startView)
    {
        CancelGesture();
        var startOffset = _offset;
        CaptureMouse();
        Cursor = Cursors.ScrollAll;
        _move = (v, _) =>
        {
            _fit = false;
            _offset = new Point(startOffset.X + v.X - startView.X, startOffset.Y + v.Y - startView.Y);
            ClampOffset();
            ApplyView();
        };
        _up = _ => { };
    }

    /// <summary>Space held: left-drag pans (the hand cursor shows it).</summary>
    public bool SpaceDown
    {
        get => _space;
        set
        {
            if (_space == value) return;
            _space = value;
            if (_move == null) Cursor = value ? Cursors.Hand : Cursors.Arrow;
        }
    }

    private void UpdateCursor(Point doc)
    {
        if (_session == null) return;
        if (_space) { Cursor = Cursors.Hand; return; }
        switch (_session.Tool)
        {
            case EditorTool.Select:
                var hit = HitTest.Hit(_session.State, _session.Selected, doc, ScaleDip, VectorRenderer.TextBounds);
                bool overSource = _session.Selected is MagnifyBox mg && OverMagnifySource(mg, doc);
                Cursor = hit.Handle != Handle.None ? CursorFor(hit.Handle) : hit.Object != null || overSource ? Cursors.SizeAll : Cursors.Arrow;
                break;
            case EditorTool.Text: Cursor = Cursors.IBeam; break;
            case EditorTool.Crop:
                var c = _session.State.Crop;
                var h = HitTest.HitHandle(HitTest.RectHandles(new Rect(c.X, c.Y, c.Width, c.Height)), doc, ScaleDip);
                Cursor = h != Handle.None ? CursorFor(h) : Cursors.Cross;
                break;
            default: Cursor = Cursors.Cross; break;
        }
    }

    private static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.Left or Handle.Right => Cursors.SizeWE,
        _ => Cursors.SizeAll,
    };

    // ------------------------------------------------------------------ tools

    private static Point Snap45(Point a, Point b)
    {
        var v = b - a;
        if (v.Length < 1e-9) return b;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(a.X + Math.Cos(angle) * v.Length, a.Y + Math.Sin(angle) * v.Length);
    }

    internal void BeginSelect(Point doc, int clicks)
    {
        var s = _session!;
        if (s.Selected is MagnifyBox mg && OverMagnifySource(mg, doc))
        {
            BeginMoveSource(mg, doc);
            return;
        }
        var hit = HitTest.Hit(s.State, s.Selected, doc, ScaleDip, VectorRenderer.TextBounds);
        if (hit.Object == null) { s.Select(null); ReleaseMouseCapture(); return; }
        var obj = hit.Object;
        s.Select(obj.Id);
        if (clicks >= 2 && obj is TextNote note && hit.Handle == Handle.None)
        {
            ReleaseMouseCapture();
            BeginTextEdit(note);
            return;
        }
        var baseState = s.Committed;
        var start = doc;
        bool moved = false;
        if (hit.Handle != Handle.None)
        {
            var handle = hit.Handle;
            _move = (v, mods) =>
            {
                var to = ToDoc(v);
                if (obj is LineShape && mods.HasFlag(ModifierKeys.Shift)) to = Snap45(handle == Handle.Start ? ((LineShape)obj).B : ((LineShape)obj).A, to);
                bool keep = mods.HasFlag(ModifierKeys.Shift) || obj is InsertedImage;
                moved = true;
                s.SetPreview(baseState.Replace(ObjectOps.Resize(obj, handle, to, keep)));
            };
        }
        else
        {
            _move = (v, _) =>
            {
                var d = ToDoc(v) - start;
                if (!moved && d.Length * ScaleDip < 3) return;   // a click is not a move
                moved = true;
                s.SetPreview(baseState.Replace(ObjectOps.Translate(obj, d.X, d.Y)));
            };
        }
        _up = _ => { if (moved) s.CommitPreview(); else s.CancelPreview(); };
    }

    /// <summary>The point is in the dashed source frame of the selected magnifier and not on its enlarged copy or one of that copy's handles: a drag moves the source.</summary>
    private bool OverMagnifySource(MagnifyBox m, Point doc)
    {
        if (HitTest.HitHandle(HitTest.HandlesOf(m), doc, ScaleDip) != Handle.None) return false;
        return m.Source.Contains((int)Math.Floor(doc.X), (int)Math.Floor(doc.Y)) && !m.Dest.Contains((int)Math.Floor(doc.X), (int)Math.Floor(doc.Y));
    }

    private void BeginMoveSource(MagnifyBox m, Point start)
    {
        var s = _session!;
        var baseState = s.Committed;
        var bounds = baseState.Base.Bounds;
        bool moved = false;
        _move = (v, _) =>
        {
            var d = ToDoc(v) - start;
            if (!moved && d.Length * ScaleDip < 3) return;
            moved = true;
            s.SetPreview(baseState.Replace(m with { Source = MagnifyLayout.MoveSource(m.Source, (int)Math.Round(d.X), (int)Math.Round(d.Y), bounds) }));
        };
        _up = _ => { if (moved) s.CommitPreview(); else s.CancelPreview(); };
    }

    /// <summary>A drag with a rectangle-like raster tool (blur, pixelate, highlight, spotlight, magnify, erase): the object follows the drag, one undo step at the release; a click without a drag makes nothing.</summary>
    internal void BeginRasterRect(EditorTool tool, Point doc)
    {
        var s = _session!;
        var bounds = s.Committed.Base.Bounds;
        int id = s.Committed.NextId;
        Point Clamp(Point p) => new(Math.Clamp(p.X, 0, bounds.Width), Math.Clamp(p.Y, 0, bounds.Height));
        var start = Clamp(doc);
        RectI r = RectI.Empty;
        _move = (v, mods) =>
        {
            var p = Clamp(ToDoc(v));
            if (mods.HasFlag(ModifierKeys.Shift))
            {
                double side = Math.Max(Math.Abs(p.X - start.X), Math.Abs(p.Y - start.Y));
                p = Clamp(new Point(start.X + (p.X < start.X ? -side : side), start.Y + (p.Y < start.Y ? -side : side)));
            }
            var rr = ToRectI(new Rect(start, p), bounds);
            if (rr.IsEmpty) return;
            r = rr;
            var proto = s.NewRasterObject(tool, rr);
            if (proto == null) return;
            if (tool == EditorTool.Magnify) _ghostSource = rr;
            s.SetPreview(s.Committed.Add(proto, out _));
        };
        _up = _ =>
        {
            _ghostSource = null;
            if (r.Width >= MinRasterDrag && r.Height >= MinRasterDrag && s.HasPreview) s.CommitPreview(id);
            else s.CancelPreview();
        };
    }

    private const int MinRasterDrag = 3;

    /// <summary>Freehand highlight: the marker follows the pointer; the stroke is a raster object (multiplied onto the picture).</summary>
    internal void BeginHighlightStroke(Point doc)
    {
        var s = _session!;
        var color = s.Options.HighlightColorValue;
        double width = s.Options.HighlightWidth;
        var pts = new List<Point> { doc };
        int id = s.Committed.NextId;
        double minStep = Math.Max(0.5, 1.5 / ScaleDip);
        void Preview() => s.SetPreview(s.Committed.Add(new HighlightStroke(0, pts.ToImmutableArray(), width, color), out _));
        Preview();
        _move = (v, _) =>
        {
            var d = ToDoc(v);
            if ((d - pts[^1]).Length < minStep) return;
            pts.Add(d);
            Preview();
        };
        _up = v =>
        {
            var d = ToDoc(v);
            if ((d - pts[^1]).Length > 0.01) pts.Add(d);
            var kept = StrokeSmoothing.Decimate(pts, 0.5);
            pts.Clear();
            pts.AddRange(kept);
            if (ArrowGeometry.Length(pts) < 2) { s.CancelPreview(); return; }
            Preview();
            s.CommitPreview(id);
        };
    }

    private void BeginStroke(Point doc, bool arrow)
    {
        var s = _session!;
        var color = s.Options.ColorValue;
        double width = s.Options.Width;
        var pts = new List<Point> { doc };
        int id = s.Committed.NextId;
        double minStep = Math.Max(0.5, 1.5 / ScaleDip);
        void Preview()
        {
            EditorObject proto = arrow ? new FreehandArrow(0, pts.ToImmutableArray(), color, width) : new PenStroke(0, pts.ToImmutableArray(), color, width);
            s.SetPreview(s.Committed.Add(proto, out _));
        }
        Preview();
        _move = (v, _) =>
        {
            var d = ToDoc(v);
            if ((d - pts[^1]).Length < minStep) return;
            pts.Add(d);
            Preview();
        };
        _up = v =>
        {
            var d = ToDoc(v);
            if ((d - pts[^1]).Length > 0.01) pts.Add(d);
            var kept = StrokeSmoothing.Decimate(pts, 0.5);   // the stored stroke carries no points a half pixel apart
            pts.Clear();
            pts.AddRange(kept);
            Preview();
            if (arrow && ArrowGeometry.Length(pts) < 2) s.CancelPreview();
            else s.CommitPreview(id);
        };
    }

    private void BeginLine(Point doc, bool arrow)
    {
        var s = _session!;
        var color = s.Options.ColorValue;
        double width = s.Options.Width;
        int id = s.Committed.NextId;
        var b = doc;
        void Preview() => s.SetPreview(s.Committed.Add(new LineShape(0, doc, b, color, width, arrow), out _));
        _move = (v, mods) =>
        {
            b = ToDoc(v);
            if (mods.HasFlag(ModifierKeys.Shift)) b = Snap45(doc, b);
            Preview();
        };
        _up = _ => { if ((b - doc).Length >= 2) s.CommitPreview(id); else s.CancelPreview(); };
    }

    private void BeginRect(Point doc)
    {
        var s = _session!;
        var color = s.Options.ColorValue;
        double width = s.Options.Width;
        int id = s.Committed.NextId;
        var r = new Rect(doc, doc);
        _move = (v, mods) =>
        {
            var p = ToDoc(v);
            if (mods.HasFlag(ModifierKeys.Shift))
            {
                double side = Math.Max(Math.Abs(p.X - doc.X), Math.Abs(p.Y - doc.Y));
                p = new Point(doc.X + (p.X < doc.X ? -side : side), doc.Y + (p.Y < doc.Y ? -side : side));
            }
            r = new Rect(doc, p);
            s.SetPreview(s.Committed.Add(new RectOutline(0, r, color, width), out _));
        };
        _up = _ => { if (r.Width >= 2 && r.Height >= 2) s.CommitPreview(id); else s.CancelPreview(); };
    }

    private static RectI ToRectI(Rect r, RectI bounds)
    {
        int x0 = (int)Math.Round(r.Left), y0 = (int)Math.Round(r.Top), x1 = (int)Math.Round(r.Right), y1 = (int)Math.Round(r.Bottom);
        return RectI.FromLtrb(x0, y0, x1, y1).Intersect(bounds);
    }

    /// <summary>Test seams: a gesture is under way / feed it a mouse position (view units) / finish it.</summary>
    internal bool HasGesture => _move != null;
    internal void DragTo(Point view, ModifierKeys mods = ModifierKeys.None) => _move?.Invoke(view, mods);
    internal Point DocToView(Point doc) => ToView(doc);

    internal void BeginCrop(Point doc)
    {
        var s = _session!;
        var baseState = s.Committed;
        var bounds = baseState.Base.Bounds;
        var crop = baseState.Crop;
        var cropRect = new Rect(crop.X, crop.Y, crop.Width, crop.Height);
        Point Clamp(Point p) => new(Math.Clamp(p.X, 0, bounds.Width), Math.Clamp(p.Y, 0, bounds.Height));
        void Show(Rect r)
        {
            var ri = ToRectI(r, bounds);
            if (ri.Width >= MinCrop && ri.Height >= MinCrop) s.SetPreview(baseState.WithCrop(ri));
        }
        var handle = HitTest.HitHandle(HitTest.RectHandles(cropRect), doc, ScaleDip);
        if (handle != Handle.None)
            _move = (v, mods) => Show(HitTest.ResizeRect(cropRect, handle, Clamp(ToDoc(v)), mods.HasFlag(ModifierKeys.Shift), MinCrop));
        else if (cropRect.Contains(doc) && crop != bounds)   // the whole picture is not a crop to move: a drag starts a new one
            _move = (v, _) =>
            {
                var d = ToDoc(v) - doc;
                double x = Math.Clamp(cropRect.X + d.X, 0, bounds.Width - cropRect.Width), y = Math.Clamp(cropRect.Y + d.Y, 0, bounds.Height - cropRect.Height);
                Show(new Rect(x, y, cropRect.Width, cropRect.Height));
            };
        else
        {
            var start = Clamp(doc);
            _move = (v, _) => Show(new Rect(start, Clamp(ToDoc(v))));
        }
        _up = _ => s.CommitPreview();
    }

    // ------------------------------------------------------------------ inline text

    private static Brush Solid(Color c) => Frozen(new SolidColorBrush(c));

    private void BeginTextAt(Point doc)
    {
        var s = _session!;
        if (HitTest.Topmost(s.State, doc, ScaleDip, VectorRenderer.TextBounds) is TextNote existing)
        {
            s.Select(existing.Id);
            BeginTextEdit(existing);
            return;
        }
        s.Select(null);
        _textOriginal = null;
        _editingId = null;
        OpenTextBox(doc, s.Options.FontSize, s.Options.Bold, s.Options.TextColorValue, "");
    }

    private void BeginTextEdit(TextNote note)
    {
        _textOriginal = note;
        _editingId = note.Id;
        SyncVectors();
        OpenTextBox(note.Origin, note.Size, note.Bold, note.Fill, note.Text);
        RedrawOverlay();
    }

    private void OpenTextBox(Point origin, double size, bool bold, Color fill, string text)
    {
        _textOrigin = origin;
        _textInset = new Point(3, 3);
        double s = ScaleDip;
        var template = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
            "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
            "<ScrollViewer x:Name='PART_ContentHost' Padding='{TemplateBinding Padding}' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' /></Border></ControlTemplate>");
        var tb = new TextBox
        {
            Template = template,
            AcceptsReturn = true,
            AcceptsTab = false,
            Background = Brushes.Transparent,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = Math.Max(1, size),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = Solid(fill),
            CaretBrush = Solid(fill),
            SelectionBrush = AccentBrush,
            MinWidth = 40,
            TextWrapping = TextWrapping.NoWrap,
            LayoutTransform = new ScaleTransform(s, s),
            Text = text,
            Tag = "EditorText",
        };
        tb.ContextMenu = BuildTextMenu(tb);
        tb.PreviewKeyDown += TextBox_PreviewKeyDown;
        tb.LostKeyboardFocus += TextBox_LostKeyboardFocus;
        _text = tb;
        AddVisualChild(tb);
        AddLogicalChild(tb);
        InvalidateMeasure();
        UpdateLayout();
        // where the first character really sits inside the box (border + padding + the text view's own indent), so the text does not move when the box goes away
        var r = tb.GetRectFromCharacterIndex(0);
        if (!r.IsEmpty && r.X < 40 && r.Y < 40) _textInset = r.TopLeft;
        InvalidateArrange();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_text != tb) return;
            tb.Focus();
            Keyboard.Focus(tb);
            tb.CaretIndex = tb.Text.Length;
            tb.SelectAll();
        });
    }

    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelText();
            e.Handled = true;
        }
    }

    private void TextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // The right-click menu (Paste...) takes the keyboard focus while it is open: that is not the end of the editing.
        if (_textClosing || sender != _text || MenuOpen(_text)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (_text == sender && !MenuOpen(_text)) CommitText(); });
    }

    private static bool MenuOpen(TextBox tb) => tb.ContextMenu?.IsOpen == true;

    /// <summary>The box's own right-click menu (so that "its menu is open" can be told, which the built-in one does not allow).</summary>
    private static ContextMenu BuildTextMenu(TextBox tb)
    {
        var menu = new ContextMenu();
        foreach (var cmd in new RoutedUICommand[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste, ApplicationCommands.SelectAll })
            menu.Items.Add(new MenuItem { Command = cmd, CommandTarget = tb });
        return menu;
    }

    /// <summary>Ends the inline editor and puts the text on the picture (a new note, a changed one; an emptied one is removed). Nothing when no box is up.</summary>
    public void CommitText()
    {
        if (_text == null || _session == null) return;
        var s = _session;
        string text = _text.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        var original = _textOriginal;
        var origin = _textOrigin;
        var tb = _text;
        CloseTextBox();
        if (original != null)
        {
            if (text.Length == 0) { s.Select(original.Id); s.Delete(); }
            else if (text != original.Text) s.Apply(s.Committed.Replace(original with { Text = text }), original.Id);
            else { SyncVectors(); RedrawOverlay(); }
        }
        else if (text.Length > 0)
        {
            var o = s.Options;
            s.Add(new TextNote(0, origin, text, tb.FontSize, o.TextColorValue, tb.FontWeight == FontWeights.Bold, s.BackdropOption, o.BackdropColorValue));
        }
        else RedrawOverlay();
        Focus();
    }

    /// <summary>Esc in the box: a new empty note is dropped, an edit is reverted.</summary>
    public void CancelText()
    {
        if (_text == null) return;
        CloseTextBox();
        SyncVectors();
        RedrawOverlay();
        Focus();
    }

    private void CancelTextQuietly()
    {
        if (_text == null) return;
        CloseTextBox();
    }

    private void CloseTextBox()
    {
        var tb = _text;
        if (tb == null) return;
        _textClosing = true;
        _text = null;
        _editingId = null;
        _textOriginal = null;
        tb.PreviewKeyDown -= TextBox_PreviewKeyDown;
        tb.LostKeyboardFocus -= TextBox_LostKeyboardFocus;
        RemoveLogicalChild(tb);
        RemoveVisualChild(tb);
        InvalidateMeasure();
        _textClosing = false;
    }

    // ------------------------------------------------------------------ keys

    /// <summary>
    /// The editor's keys (the window calls this for every key that no text box or slider wants): Delete / Backspace, arrows (nudge 1 px,
    /// Shift 10), Ctrl+D, PageUp / PageDown, Esc (deselect, back to Select), tool letters, Ctrl+0 / Ctrl+1, Ctrl+Z / Y. Copy, Save
    /// and Esc-to-close belong to the window. True when the key was used.
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys mods)
    {
        var s = _session;
        if (s == null || IsTextEditing) return false;
        if (_move != null)
        {
            // a gesture is under way: Esc cancels it, every other key is swallowed (it must not change the document under the drag)
            if (key == Key.Escape)
            {
                _move = null;
                _up = null;
                ReleaseMouseCapture();
                s.CancelPreview();
            }
            return true;
        }
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        if (alt) return false;
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z: if (shift) s.Redo(); else s.Undo(); return true;
                case Key.Y: s.Redo(); return true;
                case Key.D: s.Duplicate(); return true;
                case Key.V: PasteRequested?.Invoke(); return true;
                case Key.D0 or Key.NumPad0: ZoomToFit(); return true;
                case Key.D1 or Key.NumPad1: ZoomActual(); return true;
                case Key.OemPlus or Key.Add: ZoomBy(1.25, new Point(ActualWidth / 2, ActualHeight / 2)); return true;
                case Key.OemMinus or Key.Subtract: ZoomBy(1 / 1.25, new Point(ActualWidth / 2, ActualHeight / 2)); return true;
            }
            return false;
        }
        double step = shift ? 10 : 1;
        switch (key)
        {
            case Key.Delete or Key.Back: return s.Delete();
            case Key.Left: return s.Nudge(-step, 0) || s.Selected != null;
            case Key.Right: return s.Nudge(step, 0) || s.Selected != null;
            case Key.Up: return s.Nudge(0, -step) || s.Selected != null;
            case Key.Down: return s.Nudge(0, step) || s.Selected != null;
            case Key.PageUp: return s.Reorder(1) || s.Selected != null;
            case Key.PageDown: return s.Reorder(-1) || s.Selected != null;
            case Key.Escape:
                if (s.Selected != null) { s.Select(null); return true; }
                if (s.Tool != EditorTool.Select) { s.SetTool(EditorTool.Select); return true; }
                return false;   // the window closes
            case Key.Enter when s.Tool == EditorTool.Crop: s.SetTool(EditorTool.Select); return true;
            case Key.V: s.SetTool(EditorTool.Select); return true;
            case Key.P: s.SetTool(EditorTool.Pen); return true;
            case Key.A: s.SetTool(shift ? EditorTool.StraightArrow : EditorTool.Arrow); return true;
            case Key.L: s.SetTool(EditorTool.Line); return true;
            case Key.R: s.SetTool(EditorTool.Rectangle); return true;
            case Key.T: s.SetTool(EditorTool.Text); return true;
            case Key.C: s.SetTool(EditorTool.Crop); return true;
            case Key.B: s.SetTool(EditorTool.Blur); return true;
            case Key.X: s.SetTool(EditorTool.Pixelate); return true;
            case Key.H: s.SetTool(EditorTool.Highlight); return true;
            case Key.S: s.SetTool(EditorTool.Spotlight); return true;
            case Key.M: s.SetTool(EditorTool.Magnify); return true;
            case Key.E: s.SetTool(EditorTool.Erase); return true;
            case Key.I: InsertImageRequested?.Invoke(); return true;
        }
        return false;
    }
}
