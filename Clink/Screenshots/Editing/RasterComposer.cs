using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Editing;

/// <summary>
/// One raster-band effect (blur, highlight, spotlight, magnifier, smart erase, inserted image: the S4 slice's). It reads what the
/// bands below it produced (<paramref name="src"/>) and writes its result into <paramref name="dst"/>, which already holds a copy of
/// <paramref name="src"/>; it must only write inside <paramref name="area"/> and may read <see cref="ReadMargin"/> pixels beyond
/// <paramref name="area"/>. The margin is what lets the composer recompute only a dirty rectangle. Pure: no state, thread-safe.
/// </summary>
internal interface IRasterOp
{
    /// <summary>Where the op can change pixels at most (image coordinates); the composer clips every <c>Apply</c> to it.</summary>
    RectI Affects { get; }
    /// <summary>How many pixels around an output pixel the op reads from <c>src</c> (0 for a per-pixel op).</summary>
    int ReadMargin { get; }
    void Apply(BgraImage src, BgraImage dst, RectI area);
}

/// <summary>
/// The raster pipeline: <c>Base -> Adjusted -> raster ops in order -> Composite</c> (the vector band is drawn by WPF above this,
/// see <see cref="Exporter"/>). The adjustment stage and the op mapping are seams (<see cref="Adjuster"/>, <see cref="OpFor"/>) that
/// the pixel-library slice fills; with none, the composite is the base itself (no copy). The composer keeps a cache: the adjusted
/// base per (base, values) and the output of every op, so a change to the object k reuses the outputs below k (the "below-cache"), and
/// <see cref="Compose(DocumentState, RectI?)"/> with a dirty rectangle recomputes only that area (inflated by each op's read margin)
/// of the changed op and those above it. The returned image belongs to the composer: it is mutated by the next call, so copy it
/// (or consume it) before composing again. Not thread-safe.
/// </summary>
internal sealed class RasterComposer
{
    /// <summary>The adjustment stage (brightness / contrast / sharpness of the base). Must return the input instance for the identity.</summary>
    public Func<AdjustmentValues, BgraImage, BgraImage> Adjuster { get; set; } = (_, image) => image;

    /// <summary>Maps a raster-band record to its op; null = nothing to do for it (an object the version has no op for).</summary>
    public Func<EditorObject, IRasterOp?> OpFor { get; set; } = _ => null;

    private BgraImage? _base;
    private AdjustmentValues? _adjustValues;
    private BgraImage? _adjusted;
    private readonly List<Level> _levels = new();

    private sealed record Level(EditorObject Object, IRasterOp Op, BgraImage Output);

    /// <summary>Number of ops that were recomputed (fully or partly) by the last call; the tests use it to see the cache work.</summary>
    internal int LastRecomputed { get; private set; }

    public BgraImage Compose(DocumentState state) => Compose(state, null);

    /// <summary>
    /// The composite of the raster band. <paramref name="dirty"/>, when given, promises that the pixels outside it are unchanged
    /// since the previous call apart from what ops above the first changed one do with them, so only it is recomputed; null = everything
    /// that changed. The result for a dirty call is byte-identical to a fresh composer's (tests check random edit sequences).
    /// </summary>
    public BgraImage Compose(DocumentState state, RectI? dirty)
    {
        var adjusted = Adjusted(state, out bool adjustedChanged);
        var ops = new List<(EditorObject Object, IRasterOp Op)>();
        foreach (var o in state.Raster)
        {
            var op = OpFor(o);
            if (op != null) ops.Add((o, op));
        }

        int oldCount = _levels.Count;
        int k = 0;
        if (!adjustedChanged)
            while (k < ops.Count && k < _levels.Count && _levels[k].Object.Equals(ops[k].Object)) k++;
        // a partial recompute needs the same ops in the same order apart from changed ones: the same count, and a dirty area
        bool partial = dirty is { IsEmpty: false } && !adjustedChanged && ops.Count == oldCount;
        LastRecomputed = 0;

        var keep = partial ? _levels.Skip(k).ToList() : new List<Level>();
        if (_levels.Count > k) _levels.RemoveRange(k, _levels.Count - k);
        var input = k == 0 ? adjusted : _levels[k - 1].Output;
        RectI region = dirty ?? RectI.Empty;
        for (int j = k; j < ops.Count; j++)
        {
            var (obj, op) = ops[j];
            BgraImage output;
            if (partial)
            {
                region = region.Inflate(op.ReadMargin).Intersect(input.Bounds);
                output = keep[j - k].Output;   // the old output of this level: right outside the dirty area
                if (!region.IsEmpty)
                {
                    output.Blit(input, region, region.X, region.Y);   // back to the input, then the op on top
                    var area = region.Intersect(op.Affects);
                    if (!area.IsEmpty) op.Apply(input, output, area);
                }
            }
            else
            {
                output = input.Clone();
                var area = op.Affects.Intersect(input.Bounds);
                if (!area.IsEmpty) op.Apply(input, output, area);
            }
            LastRecomputed++;
            _levels.Add(new Level(obj, op, output));
            input = output;
        }
        return input;
    }

    /// <summary>Forgets every cached image (the editor closed or the picture changed).</summary>
    public void Clear()
    {
        _levels.Clear();
        _base = null;
        _adjusted = null;
        _adjustValues = null;
    }

    private BgraImage Adjusted(DocumentState state, out bool changed)
    {
        changed = false;
        if (_adjusted != null && ReferenceEquals(_base, state.Base) && Equals(_adjustValues, state.Adjust)) return _adjusted;
        _adjusted = state.Adjust.IsIdentity ? state.Base : Adjuster(state.Adjust, state.Base);
        _base = state.Base;
        _adjustValues = state.Adjust;
        _levels.Clear();
        changed = true;
        return _adjusted;
    }
}
