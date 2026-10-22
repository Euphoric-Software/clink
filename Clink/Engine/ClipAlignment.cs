using System.Collections.Concurrent;
using System.IO;

namespace Clink.Engine;

/// <summary>
/// Where each audio track of a clip sits on the clip's timeline, in 48 kHz frames, and whether the tracks can be added to and
/// subtracted from each other. Mixing tracks together (the trim editor's All audio follows the Game / Mic / Voice sliders) needs them
/// on one timeline to the sample: a source taken out of the baked Mix by subtracting it only cancels when both are in line.
/// <list type="bullet">
/// <item>A file written by this Clink (<see cref="Marked"/>: <see cref="Encode.Mp4Muxer.AlignedBrand"/>) says its edit lists are
/// true; a recording and a trim of one both keep every track's offset there, so they are used as they are.</item>
/// <item>Anything else (v0.2.4 and earlier, and the trims they made, which wrote every track at Media Foundation's time and so lost
/// how the tracks were placed against each other) is checked: a Mix that can only be corrected, not rebuilt, has each of its
/// sources lined up with it by cross-correlation (<see cref="TrackAlignment"/>), and a source whose lag cannot be found clearly
/// is left out (<see cref="Unlined"/>): the sliders of that source then only change its own track.</item>
/// </list>
/// </summary>
internal sealed class ClipAlignment
{
    public const int Rate = 48000;

    /// <summary>What the editor says (and a trim refuses with) when a source cannot be lined up with the Mix.</summary>
    public const string OlderClinkNote = "Recorded or trimmed by an older Clink: sources cannot be changed inside All audio";

    /// <summary>The audio tracks' names in file order; empty for a file Clink did not write (nothing is known about it).</summary>
    public string[] Names { get; init; } = Array.Empty<string>();
    /// <summary>Where each track's first sample sits on the clip's timeline, in frames (negative: the first frames are before the clip origin). Corrected for a checked file.</summary>
    public long[] OffsetFrames { get; init; } = Array.Empty<long>();
    /// <summary>The file says its edit lists are alignment-true (written by a Clink that records them to the sample).</summary>
    public bool Marked { get; init; }
    /// <summary>The file has no such mark and its sources were checked against the Mix by correlation.</summary>
    public bool Calibrated { get; init; }
    /// <summary>Source tracks (indexes in file order) that could not be lined up with the Mix: they must not be added to or taken out of it.</summary>
    public int[] Unlined { get; init; } = Array.Empty<int>();

    /// <summary>A trim of this file may say its edit lists are true: they were, or its sources were lined up and all of them could be.</summary>
    public bool Aligned => Marked || (Calibrated && Unlined.Length == 0);

    /// <summary>The track's offset on the clip timeline in 100 ns (what the muxer and the readers' timestamps use).</summary>
    public static long To100ns(long frames) => (long)Math.Round(frames * (10_000_000.0 / Rate));

    private static readonly ConcurrentDictionary<string, (long Size, long Ticks, ClipAlignment Value)> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The alignment of a clip, from its edit lists, its mark and (for an old file with a Mix that has to be corrected) a correlation
    /// of the Mix with each source: decoding a few seconds of each, so call it off the UI thread. Remembered per file while the file is
    /// unchanged, so the player and the trim that follows it only check once.
    /// </summary>
    public static ClipAlignment Read(string path)
    {
        long size, ticks;
        try
        {
            var fi = new FileInfo(path);
            size = fi.Length;
            ticks = fi.LastWriteTimeUtc.Ticks;
        }
        catch (Exception) { return new ClipAlignment(); }
        if (s_cache.TryGetValue(path, out var hit) && hit.Size == size && hit.Ticks == ticks) return hit.Value;
        var value = Compute(path);
        if (value.Calibrated || value.Unlined.Length == 0) s_cache[path] = (size, ticks, value);   // a check that could not run is tried again next time
        return value;
    }

    private static ClipAlignment Compute(string path)
    {
        var file = Trimmer.ReadClipTracks(path);
        if (file == null) return new ClipAlignment();   // not a Clink file: its tracks stand on their own
        string[] names = file.Audio.Select(t => t.Name).ToArray();
        long[] frames = file.Audio.Select(t => t.StartFrames).ToArray();
        var roles = MixPlan.Roles(names, names.Length);
        var plain = new ClipAlignment { Names = names, OffsetFrames = frames, Marked = file.Marked };
        // A rebuilt Mix does not use the baked one, and without a source there is nothing to add or take out.
        if (file.Marked || !MixPlan.CanRemix(roles) || MixPlan.CanRebuild(roles)) return plain;
        return Calibrate(path, plain, roles);
    }

    private static ClipAlignment Calibrate(string path, ClipAlignment file, TrackRole[] roles)
    {
        var frames = (long[])file.OffsetFrames.Clone();
        var unlined = new List<int>();
        string name = Path.GetFileName(path);
        try
        {
            var streams = Trimmer.AudioStreamsOf(path);
            if (streams.Count != frames.Length) throw new InvalidOperationException("the track count differs");
            var sources = Enumerable.Range(1, roles.Length - 1).Where(i => roles[i] is TrackRole.Game or TrackRole.Mic or TrackRole.Voice).ToArray();
            // Each source is checked on its own (a reader of its own each), side by side: a long clip takes seconds to look through.
            var found = new LagEstimate[sources.Length];
            Parallel.For(0, sources.Length, k => found[k] = TrackAlignment.Measure(path, streams[0], file.OffsetFrames[0], streams[sources[k]], file.OffsetFrames[sources[k]]));
            for (int k = 0; k < sources.Length; k++)
            {
                int i = sources[k];
                var e = found[k];
                string result = !e.Reliable ? $"not clear ({e.Why})" : e.Why.Length > 0 ? e.Why : $"lag {e.Lag} frames";
                Log.Info($"alignment of {name}: {file.Names[i]} against the Mix: {result}, peak {e.Peak:0.000}, {e.Ratio:0.0}x its rival");
                if (e.Reliable) frames[i] = file.OffsetFrames[i] + e.Lag;
                else unlined.Add(i);
            }
        }
        catch (Exception ex)
        {
            // Nothing could be checked (a file that will not decode): no source is lined up, and none is added to the Mix.
            Log.Warn($"alignment of {name}: {(ex is AggregateException all ? all.Flatten().InnerException?.Message : null) ?? ex.Message}");
            unlined.Clear();
            for (int i = 1; i < roles.Length; i++)
                if (roles[i] is TrackRole.Game or TrackRole.Mic or TrackRole.Voice) unlined.Add(i);
            return new ClipAlignment { Names = file.Names, OffsetFrames = file.OffsetFrames, Unlined = unlined.ToArray() };
        }
        return new ClipAlignment { Names = file.Names, OffsetFrames = frames, Calibrated = true, Unlined = unlined.ToArray() };
    }
}
