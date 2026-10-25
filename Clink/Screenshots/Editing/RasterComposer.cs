using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>What the composer tells an op factory about the level an op is created for: the picture bounds and a stamp that identifies the pixels the op will read (equal stamps = equal input pixels).</summary>
internal readonly record struct OpContext(RectI Bounds, long InputStamp);

/// <summary>
/// One raster-band effect (blur, highlight, spotlight, magnifier, smart erase, inserted image). It reads what the objects below it
/// produced (<c>src</c>) and writes its result into <c>dst</c> (the same size). Pure: no state of its own that changes the pixels.
/// <para><b>Contract.</b> <see cref="Apply"/> writes every pixel of <c>area</c> (the composer passes <c>area</c> inside <see cref="Affects"/>
/// and the picture) and nothing else, and the pixels it writes <b>do not depend on <c>area</c></b>: applying with a small area gives the
/// same bytes inside it as applying with the whole footprint (a blur only up to the +-1 of a running sum that starts somewhere else).
/// <c>dst</c> already holds the pixels of <c>src</c> inside <c>area</c> when <see cref="Apply"/> is called (an inserted image composites onto them);
/// <c>dst</c> is never <c>src</c>.</para>
/// <para><b>Dirty tracking.</b> The composer computes what to recompute itself, from the old and the new ops, with
/// <see cref="Affects"/>, <see cref="AffectedBy"/> (forwards: which output pixels may change when the input changed somewhere) and
/// <see cref="ReadRegion"/> (backwards: which input pixels are needed to produce some output pixels). The defaults are right for a
/// LOCAL op (a pixel depends on the input within <see cref="ReadMargin"/> of it); a NON-LOCAL op (magnify, smart erase, pixelate,
/// spotlight) overrides them.</para>
/// </summary>
internal interface IRasterOp
{
    /// <summary>Where the op can change pixels at most (image coordinates, not clipped to the picture; the composer clips).</summary>
    RectI Affects { get; }
    /// <summary>How many pixels around an output pixel a LOCAL op reads from <c>src</c> (0 for a per-pixel op). Used by the default <see cref="AffectedBy"/> / <see cref="ReadRegion"/>.</summary>
    int ReadMargin { get; }
    void Apply(BgraImage src, BgraImage dst, RectI area);

    /// <summary>
    /// The output region that may differ when the input differs inside <paramref name="changedInput"/>: always at least that region
    /// itself (outside <see cref="Affects"/> the output is the input), plus what depends on it. Default: input plus its margin, inside <see cref="Affects"/>.
    /// </summary>
    RectI AffectedBy(RectI changedInput)
    {
        if (changedInput.IsEmpty) return RectI.Empty;
        return changedInput.Union(changedInput.Inflate(ReadMargin).Intersect(Affects));
    }

    /// <summary>
    /// The input region needed to compute the output inside <paramref name="neededOutput"/>: at least that region itself (a pixel
    /// outside <see cref="Affects"/> is copied), plus what the op reads for the pixels inside <see cref="Affects"/>. Default: the margin around them.
    /// </summary>
    RectI ReadRegion(RectI neededOutput)
    {
        if (neededOutput.IsEmpty) return RectI.Empty;
        var inside = neededOutput.Intersect(Affects);
        return inside.IsEmpty ? neededOutput : neededOutput.Union(inside.Inflate(ReadMargin));
    }
}

/// <summary>
/// The caches an op factory keeps for one composer (spotlight blur levels, freehand highlight masks; see <see cref="RasterOps"/>). Used
/// from the compose thread only.
/// </summary>
internal interface IComposeCaches
{
    /// <summary>A compose begins. <paramref name="interactive"/>: a gesture is under way, so an approximation (re-blending with blur levels of slightly older pixels) is allowed.</summary>
    void BeginCompose(bool interactive);
    /// <summary>A compose ended (also when it failed); <paramref name="raster"/> is the list it was for: what none of its objects needs can go.</summary>
    void EndCompose(IReadOnlyList<EditorObject> raster);
    /// <summary>The objects whose output in the compose that just ended was an approximation (stale blur levels) / was exact (their op ran with fresh caches: it redoes its whole footprint).</summary>
    IReadOnlyCollection<int> StaleIds { get; }
    IReadOnlyCollection<int> FreshIds { get; }
    /// <summary>The bytes held now, and the most held at once (a level set being rebuilt while the others are kept).</summary>
    long Bytes { get; }
    long PeakBytes { get; }
    void Clear();
}

/// <summary>What a composer holds (bytes): its own full-size pictures, its op caches (spotlight levels, highlight masks; the peak counts a set being rebuilt), the inserted images of the current list.</summary>
internal readonly record struct ComposerMemory(long Pictures, int PictureCount, long Caches, long PeakCaches, long Inserted)
{
    public long Total => Pictures + Caches + Inserted;
}

/// <summary>
/// The raster pipeline: <c>Base -> Adjusted -> raster ops in order -> Composite</c> (the vector band is drawn by WPF above this, see
/// <see cref="Exporter"/>).
/// <para><b>Memory is bounded and does not grow with the number of objects.</b> The composer holds the adjusted base (when it is not
/// the identity: the sharpened base and the LUT result), the composite, the "below cache" (the composite of everything under the object
/// that is being edited), and two scratch pictures it uses when more than one op has to be redone; never a picture per op.
/// <see cref="HeldBuffers"/> counts them; <see cref="Memory"/> / <see cref="HeldBytes"/> add the op caches (spotlight levels, highlight
/// masks) and the inserted images. When no op draws, the working pictures are let go.</para>
/// <para><b>A failed compose leaves nothing behind</b>: every cache is dropped and the exception goes on, so the next compose of the
/// same state starts from scratch (the list of objects is committed only once a compose succeeded).</para>
/// <para><b>Incremental recomposition.</b> Given the previous and the new list of objects the composer finds the common prefix and
/// suffix, computes the region where the final picture may differ from the old composite from the ops' <see cref="IRasterOp.Affects"/>
/// and <see cref="IRasterOp.AffectedBy"/> (never from a caller's rectangle), brings the below cache to the first changed object, and
/// redoes the ops from there inside that region: from the below cache onto the composite when it is one op (dragging the object you
/// just drew), through the scratch pictures when several (editing an object that has others above it), each op over the region its
/// successors need (<see cref="IRasterOp.ReadRegion"/>). The result equals a from-scratch composition (the tests check random edit
/// sequences with non-local ops); blur and the unsharp mask may differ by +-1 where they were redone over a smaller area.</para>
/// <para>The returned image belongs to the composer: the next call changes it. Not thread-safe: <see cref="ComposeQueue"/> runs it
/// one compose at a time on a worker and hands the result to the UI thread.</para>
/// </summary>
internal sealed class RasterComposer
{
    /// <summary>Creates the op of a raster-band record for a level; null = nothing to do for it (a record this version has no op for).</summary>
    public Func<EditorObject, OpContext, IRasterOp?> OpFor { get; set; } = (_, _) => null;

    /// <summary>The op factory's caches: told before / after every compose that does any work (they pin and trim themselves), counted in <see cref="Memory"/>, dropped by <see cref="Clear"/>.</summary>
    public IComposeCaches? Caches { get; set; }

    /// <summary>
    /// Live-preview mode: a sharpened base that is not cached yet is not computed by <see cref="Compose"/> (the LUT of the unsharpened
    /// base is shown); <see cref="PendingSharpen"/> says so and <see cref="PrepareSharpened"/> computes the sharpened base off to the
    /// side, which the next compose then uses. A brightness / contrast change over a cached sharpened base is one LUT pass. False (the
    /// default, and the export) applies both stages at once, in the same order.
    /// </summary>
    public bool DeferSharpness { get; set; }

    /// <summary>A gesture is under way (set by the queue before each compose): the caches may approximate (stale spotlight levels for at most <see cref="SpotlightCache.RebuildDebounceMs"/>); the first compose without it redoes what was approximated.</summary>
    public bool Interactive { get; set; }

    /// <summary>The sharpening stage of the base (sharpness, base) -> sharpened (a seam: a test counts the calls or makes it fail).</summary>
    internal Func<int, BgraImage, BgraImage> SharpenBase { get; set; } = Adjuster.Sharpen;

    private sealed class Entry
    {
        public required EditorObject Obj;
        public IRasterOp? Op;
        public RectI Affects;      // the op's footprint clipped to the picture
        public long OutStamp;      // identifies the pixels this level produced
    }

    private List<Entry> _ops = new();
    private bool _valid;
    private RectI _bounds;
    private long _stampCounter;

    private BgraImage? _base;
    private AdjustmentValues? _values;
    private BgraImage? _adjusted;          // the base, the sharpened base or _lut
    private bool _adjustedComplete = true;
    private long _adjStamp;
    private (BgraImage Base, int Sharpness, BgraImage Image)? _sharpBase;   // stage 1, cached per (picture, sharpness)
    private (BgraImage Base, int Sharpness)? _sharpFailed;                  // the sharpening failed for exactly this: never tried again
    private BgraImage? _lut;               // stage 2 output buffer (owned; reused by the next brightness / contrast change)
    private readonly HashSet<int> _staleIds = new();   // objects whose output in the composite used stale spotlight levels

    private BgraImage? _f;                 // the composite buffer (owned)
    private BgraImage? _composite;         // what Compose returns: _f, or the adjusted picture itself when no op does anything
    private BgraImage? _below;             // the below cache buffer (owned)
    private bool _belowAlias;              // the below cache is the adjusted picture itself (nothing real under level K)
    private int _belowK = -1;              // the below cache holds the output of the entries [0, K); -1 = not valid
    private BgraImage? _w, _s;             // scratch

    private RectI _dirty;
    private bool _fullDirty = true;

    /// <summary>Number of ops applied by the last <see cref="Compose"/> to the composite (a test sees the cache work through it).</summary>
    internal int LastRecomputed { get; private set; }
    /// <summary>Number of ops applied by the last <see cref="Compose"/> to bring the below cache to its level.</summary>
    internal int LastBelowWork { get; private set; }

    /// <summary>The region of the composite that changed since the last <see cref="TakeDirty"/> (the whole picture after a rebuild); empty when nothing did.</summary>
    public RectI TakeDirty()
    {
        var d = _fullDirty ? _bounds : _dirty;
        _fullDirty = false;
        _dirty = RectI.Empty;
        return d;
    }

    /// <summary>The next <see cref="TakeDirty"/> reports the whole picture (the UI lost what it was shown). Compose thread, between composes.</summary>
    public void MarkAllDirty() => _fullDirty = true;

    /// <summary>The number of full-picture buffers the composer holds: the sharpened base, the LUT result, composite, below cache, two scratch. Independent of the number of objects.</summary>
    public int HeldBuffers => Held().Count;

    /// <summary>Every byte the composer holds or pins: <see cref="ComposerMemory.Total"/> (pictures, op caches, inserted images).</summary>
    public long HeldBytes => Memory.Total;

    /// <summary>What the composer holds, by kind. Not while a compose runs (the caches are the compose thread's).</summary>
    public ComposerMemory Memory
    {
        get
        {
            var pictures = Held();
            long inserted = 0;
            var seen = new HashSet<BgraImage>(ReferenceEqualityComparer.Instance);
            foreach (var e in _ops)
                if (e.Obj is InsertedImage i && seen.Add(i.Image)) inserted += i.Image.Pixels.LongLength;
            return new ComposerMemory(pictures.Sum(b => (long)b.Pixels.LongLength), pictures.Count, Caches?.Bytes ?? 0, Caches?.PeakBytes ?? 0, inserted);
        }
    }

    private HashSet<BgraImage> Held()
    {
        var set = new HashSet<BgraImage>(ReferenceEqualityComparer.Instance);
        void Add(BgraImage? b) { if (b != null && !ReferenceEquals(b, _base)) set.Add(b); }
        Add(_adjusted); Add(_sharpBase?.Image); Add(_lut); Add(_f); Add(_below); Add(_w); Add(_s);
        return set;
    }

    /// <summary>Set (with the picture and the values) while the adjusted picture lacks the sharpening stage because of <see cref="DeferSharpness"/>.</summary>
    public (BgraImage Base, AdjustmentValues Values)? PendingSharpen =>
        !_adjustedComplete && _adjusted != null && _base != null && _values != null ? (_base, _values) : null;

    /// <summary>
    /// Computes the sharpened base for <see cref="PendingSharpen"/>; the next compose with the same picture and sharpness uses it. False
    /// when nothing is pending. Not concurrent with <see cref="Compose"/>. When the sharpening throws, the (picture, sharpness) pair is
    /// remembered and never tried again (the unsharpened picture stays, nothing is pending any more) and the exception goes on.
    /// </summary>
    public bool PrepareSharpened()
    {
        if (PendingSharpen is not { } key) return false;
        BgraImage sharpened;
        try { sharpened = SharpenBase(key.Values.Sharpness, key.Base); }
        catch
        {
            _sharpFailed = (key.Base, key.Values.Sharpness);
            _adjustedComplete = true;
            throw;
        }
        _sharpBase = (key.Base, key.Values.Sharpness, sharpened);
        return true;
    }

    /// <summary>True when the sharpened base for this picture and sharpness is cached.</summary>
    internal bool SharpenedReady(BgraImage baseImage, int sharpness) => _sharpBase is { } s && ReferenceEquals(s.Base, baseImage) && s.Sharpness == sharpness;

    private bool SharpenFailed(BgraImage baseImage, int sharpness) => _sharpFailed is { } f && ReferenceEquals(f.Base, baseImage) && f.Sharpness == sharpness;

    /// <summary>True while the composite holds output made with stale spotlight levels (a gesture); the next non-interactive compose redoes it.</summary>
    public bool HasStaleOutput => _staleIds.Count > 0;

    public BgraImage Compose(DocumentState state)
    {
        try { return ComposeCore(state); }
        catch
        {
            // nothing half-updated survives: the next compose (of this state or another) starts from scratch
            Invalidate();
            throw;
        }
    }

    private BgraImage ComposeCore(DocumentState state)
    {
        LastRecomputed = 0;
        LastBelowWork = 0;
        var adjusted = Adjusted(state, out bool adjChanged);
        var bounds = adjusted.Bounds;
        var raster = state.Raster;
        int n = raster.Count;
        bool restart = adjChanged || !_valid || bounds != _bounds || _composite == null;
        // The gesture is over and the composite holds output made with stale spotlight levels: redo from the first such object.
        int staleFrom = !restart && !Interactive && _staleIds.Count > 0 ? FirstIndexOf(_ops, _staleIds) : -1;
        if (!Interactive && staleFrom < 0) _staleIds.Clear();

        // The common prefix and suffix of the old and the new list of objects.
        int p = 0, s = 0;
        var old = restart ? new List<Entry>() : _ops;
        if (!restart)
        {
            while (p < n && p < old.Count && SameObject(old[p].Obj, raster[p])) p++;
            if (staleFrom >= 0) p = Math.Min(p, staleFrom);
            else if (p == n && n == old.Count) return _composite!;   // nothing changed
            int maxS = Math.Min(n, old.Count) - p;
            if (staleFrom < 0)
                while (s < maxS && SameObject(old[old.Count - 1 - s].Obj, raster[n - 1 - s])) s++;
            if (_belowK > p) _belowK = -1;   // the below cache holds objects that changed
        }
        else
        {
            _belowK = -1;
            _fullDirty = true;
        }

        Caches?.BeginCompose(Interactive);
        bool ok = false;
        try
        {
            var entries = new List<Entry>(n);
            for (int i = 0; i < p; i++) entries.Add(old[i]);
            for (int i = p; i < n; i++)
            {
                long input = i == 0 ? _adjStamp : entries[i - 1].OutStamp;
                var op = OpFor(raster[i], new OpContext(bounds, input));
                entries.Add(new Entry { Obj = raster[i], Op = op, Affects = op == null ? RectI.Empty : op.Affects.Intersect(bounds), OutStamp = ++_stampCounter });
            }

            int lastReal = LastReal(entries);
            if (lastReal < 0)
            {
                // nothing draws: the composite is the adjusted picture itself, and the working pictures are let go
                bool was = _composite != null && !ReferenceEquals(_composite, adjusted);
                _ops = entries;
                _valid = true;
                _bounds = bounds;
                _belowK = -1;
                _belowAlias = false;
                _below = _w = _s = _f = null;
                if (was || restart || !ReferenceEquals(_composite, adjusted)) _fullDirty = true;
                _composite = adjusted;
                ok = true;
                return adjusted;
            }

            if (restart || ReferenceEquals(_composite, adjusted))
            {
                var built = Rebuild(entries, adjusted, bounds, lastReal);
                _ops = entries;
                _valid = true;
                _bounds = bounds;
                _fullDirty = true;
                ok = true;
                return built;
            }

            // Incremental: where may the final picture differ from the old composite?
            var eOld = Walk(old, p, old.Count - s, bounds);
            var eNew = Walk(entries, p, n - s, bounds);
            var e = eOld.Union(eNew).Intersect(bounds);
            if (!e.IsEmpty)
            {
                Incremental(entries, adjusted, bounds, p, e);
                _dirty = _dirty.Union(e);
            }
            _ops = entries;
            ok = true;
            return _composite!;
        }
        finally
        {
            Caches?.EndCompose(raster);
            if (ok)
            {
                if (restart || !Interactive) _staleIds.Clear();
                if (Caches != null)
                {
                    // a spotlight is redone over the whole picture whenever its op runs: fresh levels make its output exact again
                    _staleIds.ExceptWith(Caches.FreshIds);
                    _staleIds.UnionWith(Caches.StaleIds);
                }
            }
        }
    }

    private static int FirstIndexOf(List<Entry> list, HashSet<int> ids)
    {
        for (int i = 0; i < list.Count; i++) if (ids.Contains(list[i].Obj.Id)) return i;
        return -1;
    }

    private static bool SameObject(EditorObject a, EditorObject b) => ReferenceEquals(a, b) || a.Equals(b);

    private static int LastReal(List<Entry> entries)
    {
        for (int i = entries.Count - 1; i >= 0; i--) if (entries[i].Op != null && !entries[i].Affects.IsEmpty) return i;
        return -1;
    }

    private static bool AnyReal(List<Entry> entries, int from, int to)
    {
        for (int i = from; i < to; i++) if (entries[i].Op != null && !entries[i].Affects.IsEmpty) return true;
        return false;
    }

    /// <summary>
    /// The region of the output of the last op where the list differs from the "reference" chain made of the matched ops only: an
    /// unmatched op adds its footprint, a matched op (the same object, in the same order, in the common suffix) carries the
    /// difference through itself. The old and the new list each differ from that reference, so their union covers every pixel that
    /// differs between them.
    /// </summary>
    private static RectI Walk(List<Entry> list, int from, int matchedFrom, RectI bounds)
    {
        var e = RectI.Empty;
        for (int t = from; t < list.Count; t++)
        {
            var en = list[t];
            if (en.Op == null) continue;
            if (t >= matchedFrom) { if (!e.IsEmpty) e = en.Op.AffectedBy(e).Intersect(bounds); }
            else e = e.Union(en.Affects);
        }
        return e;
    }

    // ------------------------------------------------------------------ from scratch

    private BgraImage Rebuild(List<Entry> entries, BgraImage adjusted, RectI bounds, int lastReal)
    {
        // below = the output of everything under the last real op (the likeliest object to edit next), then the last op onto the composite
        BuildBelow(entries, adjusted, bounds, lastReal, countAsRecomputed: true);
        var below = BelowImage(adjusted);
        _f = EnsureSized(_f, bounds);
        _f.Blit(below, bounds, 0, 0);
        var en = entries[lastReal];
        en.Op!.Apply(below, _f, en.Affects);
        LastRecomputed++;
        _composite = _f;
        return _f;
    }

    private BgraImage BelowImage(BgraImage adjusted) => _belowAlias ? adjusted : _below!;

    /// <summary>Makes the below cache the output of the entries [0, k) from the adjusted picture.</summary>
    private void BuildBelow(List<Entry> entries, BgraImage adjusted, RectI bounds, int k, bool countAsRecomputed)
    {
        _belowK = -1;
        if (!AnyReal(entries, 0, k)) { _belowAlias = true; _belowK = k; return; }
        _below = EnsureSized(_below, bounds);
        _below.Blit(adjusted, bounds, 0, 0);
        _belowAlias = false;
        bool first = true;
        for (int i = 0; i < k; i++)
        {
            var en = entries[i];
            if (en.Op == null || en.Affects.IsEmpty) continue;
            // the first op reads the adjusted picture straight (the below cache holds its pixels): no round trip through the scratch
            if (first) en.Op.Apply(adjusted, _below, en.Affects);
            else ApplyInPlace(en.Op, _below, en.Affects);
            first = false;
            if (countAsRecomputed) LastRecomputed++; else LastBelowWork++;
        }
        _belowK = k;
    }

    private void ApplyInPlaceEntry(Entry en, BgraImage buf, bool countAsRecomputed)
    {
        if (en.Op == null || en.Affects.IsEmpty) return;
        ApplyInPlace(en.Op, buf, en.Affects);
        if (countAsRecomputed) LastRecomputed++; else LastBelowWork++;
    }

    /// <summary>Applies <paramref name="op"/> to <paramref name="buf"/> over <paramref name="area"/> as if in place: through the scratch picture, which holds the pixels below, receives the op's output, and is copied back.</summary>
    private void ApplyInPlace(IRasterOp op, BgraImage buf, RectI area)
    {
        if (area.IsEmpty) return;
        _s = EnsureSized(_s, buf.Bounds);
        _s.Blit(buf, area, area.X, area.Y);
        op.Apply(buf, _s, area);
        buf.Blit(_s, area, area.X, area.Y);
    }

    private static BgraImage EnsureSized(BgraImage? b, RectI bounds) =>
        b != null && b.Width == bounds.Width && b.Height == bounds.Height ? b : new BgraImage(bounds.Width, bounds.Height);

    // ------------------------------------------------------------------ incremental

    private void Incremental(List<Entry> entries, BgraImage adjusted, RectI bounds, int f, RectI e)
    {
        int n = entries.Count;
        // the below cache at level f: the output of [0, f)
        if (!AnyReal(entries, 0, f)) { _belowAlias = true; _belowK = f; }
        else if (!_belowAlias && _below != null && _belowK >= 0 && _belowK <= f)
        {
            int from = _belowK;
            _belowK = -1;   // not valid until it is brought to f
            for (int i = from; i < f; i++) ApplyInPlaceEntry(entries[i], _below, countAsRecomputed: false);
            _belowK = f;
        }
        else BuildBelow(entries, adjusted, bounds, f, countAsRecomputed: false);
        var below = BelowImage(adjusted);
        var f0 = _f!;

        int tailReal = 0, only = -1;
        for (int j = f; j < n; j++) if (entries[j].Op != null && !entries[j].Affects.IsEmpty) { tailReal++; only = j; }

        if (tailReal == 0)
        {
            f0.Blit(below, e, e.X, e.Y);
            return;
        }
        if (tailReal == 1)
        {
            f0.Blit(below, e, e.X, e.Y);
            var en = entries[only];
            var area = e.Intersect(en.Affects);
            if (!area.IsEmpty) { en.Op!.Apply(below, f0, area); LastRecomputed++; }
            return;
        }

        // several ops: each is redone over what its successors read, in a scratch picture, then the result region goes to the composite
        var rout = new RectI[n];
        var cur = e;
        for (int j = n - 1; j >= f; j--)
        {
            rout[j] = cur;
            var en = entries[j];
            if (en.Op != null && !en.Affects.IsEmpty) cur = en.Op.ReadRegion(cur).Union(cur).Intersect(bounds);
        }
        _w = EnsureSized(_w, bounds);
        _w.Blit(below, cur, cur.X, cur.Y);
        bool first = true;
        for (int j = f; j < n; j++)
        {
            var en = entries[j];
            if (en.Op == null || en.Affects.IsEmpty) continue;
            var area = rout[j].Intersect(en.Affects);
            if (area.IsEmpty) continue;
            // the first op reads the below cache straight (the scratch holds its pixels over everything that is read): no extra copies
            if (first) en.Op.Apply(below, _w, area);
            else ApplyInPlace(en.Op, _w, area);
            first = false;
            LastRecomputed++;
        }
        f0.Blit(_w, e, e.X, e.Y);
    }

    // ------------------------------------------------------------------ the adjusted picture

    /// <summary>
    /// Stage 1, the sharpened base (cached per picture and sharpness; in the live mode prepared on a worker, see
    /// <see cref="PrepareSharpened"/>), then stage 2, the brightness / contrast LUT over it into the composer's own buffer. The identity
    /// is the base itself.
    /// </summary>
    private BgraImage Adjusted(DocumentState state, out bool changed)
    {
        changed = false;
        var v = state.Adjust;
        var b = state.Base;
        if (_adjusted != null && ReferenceEquals(_base, b) && v.Equals(_values) && (_adjustedComplete || !SharpenedReady(b, v.Sharpness)))
            return _adjusted;   // the same (or still waiting for its sharpened base)

        // stage 1: the sharpened base
        if (_sharpBase is { } old && !(ReferenceEquals(old.Base, b) && old.Sharpness == v.Sharpness)) _sharpBase = null;   // of no further use
        var src = b;
        bool complete = true;
        if (v.Sharpness > 0)
        {
            if (SharpenedReady(b, v.Sharpness)) src = _sharpBase!.Value.Image;
            else if (SharpenFailed(b, v.Sharpness)) { }   // it failed for exactly this: the unsharpened picture (the queue logged it once)
            else if (DeferSharpness) complete = false;   // the LUT of the base now; the sharpened base follows from a worker
            else
            {
                src = SharpenBase(v.Sharpness, b);
                _sharpBase = (b, v.Sharpness, src);
            }
        }

        // stage 2: brightness and contrast over it
        BgraImage img;
        if (v.Brightness == 0 && v.Contrast == 0)
        {
            img = src;
            _lut = null;
        }
        else
        {
            _lut = EnsureSized(_lut, b.Bounds);
            Adjuster.LutInto(v, src, _lut);
            img = _lut;
        }
        _base = b;
        _values = v;
        _adjusted = img;
        _adjustedComplete = complete;
        _adjStamp = ++_stampCounter;
        _ops = new List<Entry>();
        changed = true;
        return img;
    }

    /// <summary>Drops every cached image and the op caches (after a failed compose: nothing half-updated may be used again).</summary>
    private void Invalidate()
    {
        _ops = new List<Entry>();
        _valid = false;
        _base = null;
        _values = null;
        _adjusted = null;
        _adjustedComplete = true;
        _sharpBase = null;
        _lut = null;
        _f = null;
        _composite = null;
        _below = null;
        _belowAlias = false;
        _belowK = -1;
        _w = null;
        _s = null;
        _dirty = RectI.Empty;
        _fullDirty = true;
        _staleIds.Clear();
        Caches?.Clear();
    }

    /// <summary>Forgets every cached image and the op caches (the editor closed or the picture changed).</summary>
    public void Clear()
    {
        Invalidate();
        _sharpFailed = null;
    }
}
