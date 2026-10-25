using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>
/// The raster-band records as ops of the pixel library (<see cref="BoxBlur"/>, <see cref="Pixelate"/>, <see cref="Highlight"/>,
/// <see cref="Spotlight"/>, <see cref="Magnifier"/>, <see cref="SmartErase"/>, <see cref="Resample"/>), with the footprint and the
/// dirty-tracking rules each needs (see <see cref="IRasterOp"/>). One instance serves one <see cref="RasterComposer"/> (it owns the
/// spotlight level cache and a cache of freehand highlight masks, its <see cref="IComposeCaches"/>); <see cref="NewComposer"/> wires both.
/// <para>The mask cache is bounded by bytes (<see cref="MaskCacheBytes"/>) and, after every compose, keeps only the masks of strokes
/// that are in the list just composed: the preview frames of a freehand drag (a new stroke each) never pile up. The stroke ops hold
/// no mask themselves (they resolve it when applied, rasterizing again after an eviction), so the cap is the real bound.</para>
/// </summary>
internal sealed class RasterOps : IComposeCaches
{
    /// <summary>The ring of pixels around a smart-erase rectangle that the fill reads (the library's default).</summary>
    public const int EraseRing = 3, EraseTolerance = 12;

    /// <summary>The most bytes of highlight masks kept (a test lowers it).</summary>
    public const long DefaultMaskCacheBytes = 32L << 20;
    internal long MaskCacheBytes { get; set; } = DefaultMaskCacheBytes;

    /// <summary>"Everything": larger than any picture; the composer clips it.</summary>
    internal static readonly RectI All = new(-(1 << 29), -(1 << 29), 1 << 30, 1 << 30);

    public SpotlightCache SpotCache { get; } = new();
    private readonly Dictionary<HighlightStroke, AlphaMask> _masks = new();
    private readonly LinkedList<HighlightStroke> _maskOrder = new();   // oldest first
    private long _maskBytes, _maskPeak;

    /// <summary>The masks kept, and their bytes (tests, the memory diagnostic).</summary>
    public int MaskCount => _masks.Count;
    public long MaskBytes => _maskBytes;

    /// <summary>A composer that draws every raster-band record.</summary>
    public static RasterComposer NewComposer(out RasterOps ops)
    {
        ops = new RasterOps();
        var o = ops;
        return new RasterComposer { OpFor = o.Create, Caches = o };
    }

    // ------------------------------------------------------------------ IComposeCaches

    public void BeginCompose(bool interactive) => SpotCache.BeginCompose(interactive);

    public void EndCompose(IReadOnlyList<EditorObject> raster)
    {
        var strokes = new HashSet<HighlightStroke>();
        var spots = new HashSet<int>();
        foreach (var o in raster)
        {
            if (o is HighlightStroke hs) strokes.Add(hs);
            else if (o is SpotlightBox sp) spots.Add(sp.Id);
        }
        // masks of strokes that are not in the list (an earlier preview frame, a deleted stroke) go
        for (var node = _maskOrder.First; node != null;)
        {
            var next = node.Next;
            if (!strokes.Contains(node.Value)) RemoveMask(node);
            node = next;
        }
        SpotCache.EndCompose(spots);
    }

    public IReadOnlyCollection<int> StaleIds => SpotCache.StaleIds;
    public IReadOnlyCollection<int> FreshIds => SpotCache.FreshIds;

    public long Bytes => _maskBytes + SpotCache.Bytes;

    /// <summary>The most held at once: the spotlight peak (a set being built while the others are kept) plus the mask peak.</summary>
    public long PeakBytes => _maskPeak + SpotCache.PeakBytes;

    public void Clear()
    {
        _masks.Clear();
        _maskOrder.Clear();
        _maskBytes = 0;
        SpotCache.Clear();
    }

    private void RemoveMask(LinkedListNode<HighlightStroke> node)
    {
        if (_masks.Remove(node.Value, out var m)) _maskBytes -= m.Coverage.LongLength;
        _maskOrder.Remove(node);
    }

    public static RasterComposer NewComposer() => NewComposer(out _);

    public IRasterOp? Create(EditorObject o, OpContext ctx) => o switch
    {
        BlurRegion b when b.Pixelate => new PixelateOp(b.R, b.Block),
        BlurRegion b => new BlurOp(b.R, Math.Max(0, b.Radius)),
        HighlightRect h => new HighlightRectOp(h.R, h.Color),
        HighlightStroke hs => new HighlightStrokeOp(hs, ctx.Bounds, this),
        SpotlightBox sp => new SpotlightOp(sp, ctx, SpotCache),
        MagnifyBox m => new MagnifyOp(m, ctx.Bounds),
        SmartEraseRect e => new SmartEraseOp(e.R),
        InsertedImage i => new InsertedImageOp(i),
        _ => null,
    };

    /// <summary>The mask of a stroke: the cached one, else rasterized now (and kept if the byte cap allows). Compose thread.</summary>
    private AlphaMask Mask(HighlightStroke hs, RectI bounds)
    {
        if (_masks.TryGetValue(hs, out var m)) return m;
        var mask = StrokeMask.Rasterize(hs.Pts, hs.Width, bounds);
        long size = mask.Coverage.LongLength;
        // the oldest go until the new one fits (a mask bigger than the whole budget is used but not kept)
        while (_maskOrder.First != null && _maskBytes + size > MaskCacheBytes) RemoveMask(_maskOrder.First);
        if (size <= MaskCacheBytes)
        {
            _masks[hs] = mask;
            _maskOrder.AddLast(hs);
            _maskBytes += size;
            _maskPeak = Math.Max(_maskPeak, _maskBytes);
        }
        return mask;
    }

    // ------------------------------------------------------------------ the ops

    private sealed class BlurOp : IRasterOp
    {
        private readonly RectI _r;
        private readonly double _sigma;
        public BlurOp(RectI r, double sigma) { _r = r; _sigma = sigma; ReadMargin = BoxBlur.ReadMargin(sigma); }
        public RectI Affects => _r;
        public int ReadMargin { get; }
        public void Apply(BgraImage src, BgraImage dst, RectI area) => BoxBlur.Blur(src, dst, area, _r, _sigma);
    }

    private sealed class PixelateOp : IRasterOp
    {
        private readonly RectI _r;
        private readonly int _block;
        public PixelateOp(RectI r, int block) { _r = r; _block = Math.Max(1, block); }
        public RectI Affects => _r;
        public int ReadMargin => 0;
        public void Apply(BgraImage src, BgraImage dst, RectI area) => Pixelate.Apply(src, dst, area, _r, _block);

        /// <summary>The whole blocks (of the grid anchored at the rectangle's corner) that touch <paramref name="x"/>, inside the rectangle.</summary>
        private RectI Snap(RectI x)
        {
            var c = x.Intersect(_r);
            if (c.IsEmpty || _block <= 1) return c;
            long x0 = _r.X + (long)((c.X - _r.X) / _block) * _block;
            long y0 = _r.Y + (long)((c.Y - _r.Y) / _block) * _block;
            long x1 = _r.X + (long)((c.Right - _r.X + _block - 1) / _block) * _block;
            long y1 = _r.Y + (long)((c.Bottom - _r.Y + _block - 1) / _block) * _block;
            return RectI.FromLtrb((int)x0, (int)y0, (int)Math.Min(x1, int.MaxValue / 2), (int)Math.Min(y1, int.MaxValue / 2)).Intersect(_r);
        }

        public RectI AffectedBy(RectI changedInput) => changedInput.IsEmpty ? RectI.Empty : changedInput.Union(Snap(changedInput));
        public RectI ReadRegion(RectI neededOutput) => neededOutput.IsEmpty ? RectI.Empty : neededOutput.Union(Snap(neededOutput));
    }

    private sealed class HighlightRectOp : IRasterOp
    {
        private readonly RectI _r;
        private readonly Color _c;
        public HighlightRectOp(RectI r, Color c) { _r = r; _c = c; }
        public RectI Affects => _r;
        public int ReadMargin => 0;
        public void Apply(BgraImage src, BgraImage dst, RectI area) => Highlight.Rect(src, dst, area, _r, _c);
    }

    /// <summary>Holds the stroke, not its mask (the composer keeps every op, so a held mask would defeat the cache's byte cap): the mask is resolved in <see cref="Apply"/>, from the cache or rasterized again after an eviction.</summary>
    private sealed class HighlightStrokeOp : IRasterOp
    {
        private readonly HighlightStroke _s;
        private readonly RectI _bounds;
        private readonly RasterOps _ops;
        public HighlightStrokeOp(HighlightStroke s, RectI bounds, RasterOps ops)
        {
            _s = s; _bounds = bounds; _ops = ops;
            Affects = StrokeMask.BoundsOf(s.Pts, s.Width, bounds);
        }
        public RectI Affects { get; }
        public int ReadMargin => 0;
        public void Apply(BgraImage src, BgraImage dst, RectI area)
        {
            if (Affects.IsEmpty) return;
            Highlight.Multiply(src, dst, area, _ops.Mask(_s, _bounds), _s.Color);
        }
    }

    private sealed class SpotlightOp : IRasterOp
    {
        private readonly SpotlightBox _s;
        private readonly OpContext _ctx;
        private readonly SpotlightCache _cache;
        public SpotlightOp(SpotlightBox s, OpContext ctx, SpotlightCache cache) { _s = s; _ctx = ctx; _cache = cache; }
        public RectI Affects => _ctx.Bounds;
        public int ReadMargin => 0;

        public void Apply(BgraImage src, BgraImage dst, RectI area)
        {
            var levels = _cache.Get(_s.Id, _ctx.InputStamp, Math.Max(0, _s.Radius), _s.Gradient, src);
            Imaging.Spotlight.Blend(src, dst, area, levels, _s.Box, _s.Dim, _s.FalloffPx);
        }

        // every pixel depends on the box (a move or resize re-blends the whole picture) ...
        public RectI AffectedBy(RectI changedInput) => changedInput.IsEmpty ? RectI.Empty : _ctx.Bounds;

        // ... and the blurred levels need the whole input picture, unless they are cached for exactly these input pixels
        public RectI ReadRegion(RectI neededOutput)
        {
            if (neededOutput.IsEmpty) return RectI.Empty;
            return _cache.Has(_s.Id, _ctx.InputStamp, Math.Max(0, _s.Radius), _s.Gradient) ? neededOutput : _ctx.Bounds;
        }
    }

    private sealed class MagnifyOp : IRasterOp
    {
        private readonly MagnifyBox _m;
        private readonly RectI _sourceRead;
        public MagnifyOp(MagnifyBox m, RectI bounds)
        {
            _m = m;
            // what the sampler can read: the source rectangle (and a pixel more for the bilinear mode), clamped to the picture
            if (m.Source.IsEmpty || m.Dest.IsEmpty || bounds.IsEmpty) { _sourceRead = RectI.Empty; return; }
            int x0 = Math.Clamp(m.Source.X - 1, 0, bounds.Width - 1), y0 = Math.Clamp(m.Source.Y - 1, 0, bounds.Height - 1);
            int x1 = Math.Clamp(m.Source.Right, 0, bounds.Width - 1), y1 = Math.Clamp(m.Source.Bottom, 0, bounds.Height - 1);
            _sourceRead = RectI.FromLtrb(x0, y0, x1 + 1, y1 + 1);
        }
        public RectI Affects => _sourceRead.IsEmpty ? RectI.Empty : _m.Dest;
        public int ReadMargin => 0;
        public void Apply(BgraImage src, BgraImage dst, RectI area) => Magnifier.Render(src, dst, area, _m.Source, _m.Dest, _m.Smooth, _m.Outline, _m.OutlineWidth);

        public RectI AffectedBy(RectI changedInput)
        {
            if (changedInput.IsEmpty) return RectI.Empty;
            return changedInput.Intersects(_sourceRead) ? changedInput.Union(_m.Dest) : changedInput;
        }

        public RectI ReadRegion(RectI neededOutput)
        {
            if (neededOutput.IsEmpty) return RectI.Empty;
            return neededOutput.Intersects(_m.Dest) && !_sourceRead.IsEmpty ? neededOutput.Union(_sourceRead) : neededOutput;
        }
    }

    private sealed class SmartEraseOp : IRasterOp
    {
        private readonly RectI _r;
        private readonly RectI _ringBox;
        public SmartEraseOp(RectI r) { _r = r; _ringBox = r.Inflate(EraseRing); }
        public RectI Affects => _r;
        public int ReadMargin => 0;
        public void Apply(BgraImage src, BgraImage dst, RectI area) => SmartErase.Fill(src, dst, area, _r, EraseRing, EraseTolerance);

        // the fill of every pixel depends on the whole ring: a change in the ring redoes the whole rectangle
        public RectI AffectedBy(RectI changedInput)
        {
            if (changedInput.IsEmpty) return RectI.Empty;
            return changedInput.Intersects(_ringBox) ? changedInput.Union(_r) : changedInput;
        }

        public RectI ReadRegion(RectI neededOutput)
        {
            if (neededOutput.IsEmpty) return RectI.Empty;
            return neededOutput.Intersects(_r) ? neededOutput.Union(_ringBox) : neededOutput;
        }
    }

    private sealed class InsertedImageOp : IRasterOp
    {
        private readonly InsertedImage _i;
        public InsertedImageOp(InsertedImage i) { _i = i; }
        public RectI Affects => _i.Image.Width == 0 || _i.Image.Height == 0 || _i.Opacity <= 0 ? RectI.Empty : _i.Dest;
        public int ReadMargin => 0;
        // composites onto the pixels below, which the composer restored in the area first
        public void Apply(BgraImage src, BgraImage dst, RectI area) => Resample.Over(_i.Image, dst, area, _i.Dest, _i.Opacity);
    }
}

/// <summary>
/// The blurred levels of the spotlights, per spotlight object, valid for exactly the input pixels they were built from (the
/// composer's stamp of the level below), the radius and the gradient flag. A move or resize of the box finds them and only re-blends;
/// a change of the pixels below, the radius or the gradient builds them again. At most <see cref="MaxEntries"/> sets are kept between
/// composes (a 4K gradient set is 33 MB, a fixed one 8 MB), and only for spotlights of the list just composed; the ones used by the
/// compose in progress are never dropped during it. The old set of a spotlight is dropped BEFORE its new one is built, so a rebuild
/// holds the other sets plus the new one (<see cref="PeakBytes"/> records the most held at once).
/// <para><b>During a gesture</b> (an interactive compose) a change of the pixels below a spotlight (dragging an object under it) does
/// not rebuild its levels on every frame: for <see cref="RebuildDebounceMs"/> after a build, the levels of the slightly older pixels
/// are re-blended (the blur under the box is visibly a little stale) and the spotlight is reported in <see cref="StaleIds"/>; the first
/// compose without the gesture rebuilds them.</para>
/// Used from the compose thread only.
/// </summary>
internal sealed class SpotlightCache
{
    public const int MaxEntries = 2;

    /// <summary>During a gesture, levels are rebuilt at most once per this many milliseconds (counted from the end of the last build).</summary>
    public const int RebuildDebounceMs = 120;

    private sealed class Entry
    {
        public int Id; public long Stamp; public int Radius; public bool Gradient; public SpotlightLevels Levels = null!; public long Used; public long BuiltAt;
    }

    private readonly Dictionary<int, Entry> _entries = new();
    private readonly HashSet<int> _stale = new();
    private long _tick;
    private bool _interactive;

    /// <summary>How many times levels were built (a test sees "only a re-blend" through it).</summary>
    public int Builds { get; private set; }
    /// <summary>How many times stale levels were re-blended during a gesture instead of a build.</summary>
    public int StaleReuses { get; private set; }
    public int Count => _entries.Count;
    public long Bytes => _entries.Values.Sum(e => e.Levels.Bytes);
    /// <summary>The most bytes held at once (right after a build, before the trim at the end of the compose).</summary>
    public long PeakBytes { get; private set; }
    /// <summary>The spotlights the last compose re-blended with stale levels.</summary>
    public IReadOnlyCollection<int> StaleIds => _stale;
    /// <summary>The spotlights the last compose blended with levels of exactly their input.</summary>
    public IReadOnlyCollection<int> FreshIds => _fresh;
    private readonly HashSet<int> _fresh = new();

    public void BeginCompose(bool interactive)
    {
        _interactive = interactive;
        _stale.Clear();
        _fresh.Clear();
    }

    /// <summary>The compose ended: only the sets of <paramref name="liveIds"/> stay, at most <see cref="MaxEntries"/> of them (the most recently used).</summary>
    public void EndCompose(IReadOnlySet<int>? liveIds = null)
    {
        if (liveIds != null)
            foreach (int id in _entries.Keys.Where(k => !liveIds.Contains(k)).ToList()) _entries.Remove(id);
        if (_entries.Count <= MaxEntries) return;
        foreach (var e in _entries.Values.OrderByDescending(e => e.Used).Skip(MaxEntries).ToList()) _entries.Remove(e.Id);
    }

    public bool Has(int id, long stamp, int radius, bool gradient)
    {
        if (_entries.TryGetValue(id, out var e) && e.Stamp == stamp && e.Radius == radius && e.Gradient == gradient)
        {
            e.Used = ++_tick;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The levels for the input <paramref name="src"/>; built (from the whole of <paramref name="src"/>) when the cache has none that
    /// fit, except during a gesture within <see cref="RebuildDebounceMs"/> of the last build of this spotlight with the same radius and
    /// gradient flag: then its levels of the older pixels (see the class remarks).
    /// </summary>
    public SpotlightLevels Get(int id, long stamp, int radius, bool gradient, BgraImage src)
    {
        if (Has(id, stamp, radius, gradient)) { _fresh.Add(id); return _entries[id].Levels; }
        if (_entries.TryGetValue(id, out var was))
        {
            if (_interactive && was.Radius == radius && was.Gradient == gradient && was.Levels.Width == src.Width && was.Levels.Height == src.Height
                && Stopwatch.GetElapsedTime(was.BuiltAt).TotalMilliseconds < RebuildDebounceMs)
            {
                was.Used = ++_tick;
                _stale.Add(id);
                StaleReuses++;
                return was.Levels;
            }
            _entries.Remove(id);   // of no further use: let it go before the new set is allocated
        }
        var levels = Spotlight.Build(src, radius, gradient);
        levels.Version = stamp;
        Builds++;
        _entries[id] = new Entry { Id = id, Stamp = stamp, Radius = radius, Gradient = gradient, Levels = levels, Used = ++_tick, BuiltAt = Stopwatch.GetTimestamp() };
        PeakBytes = Math.Max(PeakBytes, Bytes);
        _fresh.Add(id);
        return levels;
    }

    public void Clear()
    {
        _entries.Clear();
        _stale.Clear();
        _fresh.Clear();
    }
}

