using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace Clink.Engine;

internal sealed class VideoSample
{
    public long Pts;
    public bool Keyframe;
    public byte[] Data = Array.Empty<byte>();
}

/// <summary>
/// Fixed-capacity circular byte buffer for encoded video with per-sample descriptors. Zero GC pressure while
/// recording; a snapshot copies the requested range out so the ring can keep running.
/// </summary>
internal sealed unsafe class RingStore : IDisposable
{
    /// <param name="Seq">Push order (never reused, not even after <see cref="Clear"/>): an entry is still in the ring while its Seq is at least the oldest entry's.</param>
    private struct Entry { public long Offset; public int Length; public long Pts; public bool Key; public long Seq; }

    public const long MinBytes = 32L << 20;
    /// <summary>Most bytes <see cref="Snapshot"/> copies per hold of the lock (a push waits for at most one such copy).</summary>
    internal const int SnapshotChunkBytes = 4 << 20;

    private byte* _buf;
    private readonly long _cap;
    private long _head;              // next write offset
    private Entry[] _entries = new Entry[4096];
    private int _first;              // index of oldest entry in the circular entries array
    private int _count;
    private long _nextSeq;
    private readonly object _gate = new();

    /// <summary>Tests only: runs between two chunks of a <see cref="Snapshot"/>, outside the lock.</summary>
    internal Action? BetweenSnapshotChunks;

    /// <summary>Bytes actually allocated (may be less than asked for when the allocation had to be retried smaller).</summary>
    public long Capacity => _cap;
    /// <summary>Bytes asked for in the constructor.</summary>
    public long Requested { get; }
    public long SamplesStored { get { lock (_gate) return _count; } }

    // ------------------------------------------------------------------ sizing (shared with Capabilities so the UI estimate matches)

    /// <summary>Ring size for a replay length: bitrate × (seconds + 4 s slack) × 1.3 headroom for bitrate overshoot, at least <see cref="MinBytes"/>.</summary>
    /// <summary>Safety margin over the nominal bitrate (CBR overshoot on keyframes) and the extra seconds kept so a clip can start on a keyframe.</summary>
    private const double Margin = 1.1;
    private const int ExtraSeconds = 3;

    public static long BytesFor(double bitrateMbps, int seconds)
    {
        double bytes = bitrateMbps * 1_000_000 / 8.0 * (Math.Max(0, seconds) + ExtraSeconds) * Margin;
        return Math.Max(MinBytes, (long)Math.Ceiling(bytes));
    }

    /// <summary>Inverse of <see cref="BytesFor"/>: how many seconds of replay a ring of this size holds at the bitrate.</summary>
    public static int SecondsFor(double bitrateMbps, long bytes)
    {
        if (bitrateMbps <= 0) return 0;
        double seconds = bytes / (bitrateMbps * 1_000_000 / 8.0 * Margin) - ExtraSeconds;
        return (int)Math.Max(0, Math.Floor(seconds));
    }

    private static long s_ram;

    /// <summary>Installed physical RAM (GlobalMemoryStatusEx; falls back to what the GC reports).</summary>
    public static long PhysicalRamBytes()
    {
        if (s_ram > 0) return s_ram;
        long ram = 0;
        try
        {
            MEMORYSTATUSEX ms = default;
            ms.dwLength = (uint)sizeof(MEMORYSTATUSEX);
            if (PInvoke.GlobalMemoryStatusEx(&ms)) ram = (long)ms.ullTotalPhys;
        }
        catch { }
        if (ram <= 0)
        {
            try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { }
        }
        if (ram <= 0) ram = 8L << 30;
        return s_ram = ram;
    }

    /// <summary>The most the ring may take: half of physical RAM.</summary>
    public static long MaxBytes() => Math.Max(MinBytes, PhysicalRamBytes() / 2);

    /// <summary>
    /// Allocates the ring. Asks for <paramref name="capacityBytes"/> (at least <see cref="MinBytes"/>); when the allocation fails it is
    /// retried at half the size down to the minimum instead of crashing, so a too-large replay length degrades to a shorter one.
    /// </summary>
    public RingStore(long capacityBytes)
    {
        long want = Math.Max(capacityBytes, MinBytes);
        Requested = want;
        long cap = want;
        while (true)
        {
            try
            {
                _buf = (byte*)NativeMemory.Alloc((nuint)cap);
                break;
            }
            catch (OutOfMemoryException) when (cap > MinBytes)
            {
                long smaller = Math.Max(MinBytes, cap / 2);
                Log.Warn($"Ring buffer: could not allocate {cap >> 20} MB, retrying with {smaller >> 20} MB");
                cap = smaller;
            }
        }
        _cap = cap;
    }

    private ref Entry At(int i) => ref _entries[(_first + i) % _entries.Length];

    public void Push(long pts, bool keyframe, ReadOnlySpan<byte> data)
    {
        if (data.Length > _cap / 2) return; // absurd sample; drop
        lock (_gate)
        {
            if (_buf == null) return;
            if (_count == 0) _head = 0;
            long oldHead = _head;
            if (_head + data.Length > _cap)
            {
                // Wrap: everything older than the lap that ended at oldHead is now unreachable.
                while (_count > 0 && At(0).Offset >= oldHead) Pop();
                _head = 0;
            }
            // Evict entries that overlap the write region.
            while (_count > 0)
            {
                ref var e = ref At(0);
                bool overlaps = e.Offset < _head + data.Length && e.Offset + e.Length > _head;
                if (!overlaps) break;
                Pop();
            }
            data.CopyTo(new Span<byte>(_buf + _head, data.Length));
            if (_count == _entries.Length) Grow();
            ref var ne = ref _entries[(_first + _count) % _entries.Length];
            ne.Offset = _head; ne.Length = data.Length; ne.Pts = pts; ne.Key = keyframe; ne.Seq = _nextSeq++;
            _count++;
            _head += data.Length;
        }
    }

    private void Pop() { _first = (_first + 1) % _entries.Length; _count--; }

    private void Grow()
    {
        var n = new Entry[_entries.Length * 2];
        for (int i = 0; i < _count; i++) n[i] = At(i);
        _entries = n;
        _first = 0;
    }

    /// <summary>Oldest and newest timestamps currently held, or null when empty.</summary>
    public (long oldest, long newest)? Range()
    {
        lock (_gate)
        {
            if (_count == 0) return null;
            return (At(0).Pts, At(_count - 1).Pts);
        }
    }

    /// <summary>
    /// Copies out all samples from the last keyframe at or before <paramref name="fromPts"/> (or the first keyframe after it) up to
    /// <paramref name="toPts"/>. A clip is hundreds of MB, and the encoder thread pushes into the ring at the frame rate, so the bytes are
    /// copied in chunks of at most <see cref="SnapshotChunkBytes"/>, taking the lock once per chunk. The ring only ever drops its oldest
    /// entries: when it has dropped the next ones to copy in the meantime (a clip as long as the ring), the clip starts again at the first
    /// keyframe still held (empty when none is).
    /// </summary>
    public List<VideoSample> Snapshot(long fromPts, long toPts)
    {
        var list = new List<VideoSample>();
        Entry[] want;
        lock (_gate)
        {
            if (_buf == null) return list;
            int start = -1;
            for (int i = 0; i < _count; i++)
            {
                ref var e = ref At(i);
                if (e.Pts > fromPts) break;
                if (e.Key) start = i;
            }
            if (start < 0)
                for (int i = 0; i < _count; i++) if (At(i).Key && At(i).Pts >= fromPts) { start = i; break; }
            if (start < 0) return list;
            int end = start;
            while (end < _count && At(end).Pts <= toPts) end++;
            want = new Entry[end - start];
            for (int i = 0; i < want.Length; i++) want[i] = At(start + i);
        }
        int at = 0;
        while (at < want.Length)
        {
            if (at > 0) BetweenSnapshotChunks?.Invoke();
            int stop = at;
            long bytes = 0;
            while (stop < want.Length && (stop == at || bytes + want[stop].Length <= SnapshotChunkBytes)) bytes += want[stop++].Length;
            var arrays = new byte[stop - at][];   // allocated outside the lock
            for (int k = 0; k < arrays.Length; k++) arrays[k] = GC.AllocateUninitializedArray<byte>(want[at + k].Length);
            lock (_gate)
            {
                if (_buf == null) return new List<VideoSample>();   // disposed meanwhile
                long oldestSeq = _count > 0 ? At(0).Seq : _nextSeq;
                if (want[at].Seq < oldestSeq)
                {
                    // Evicted (or cleared) meanwhile: what was copied no longer joins up with what is left. Entries newer than a
                    // surviving one survive too, so the first keyframe at or after the oldest survivor is where the clip can start.
                    list.Clear();
                    while (at < want.Length && (want[at].Seq < oldestSeq || !want[at].Key)) at++;
                    continue;
                }
                for (int k = 0; k < arrays.Length; k++)
                {
                    ref var e = ref want[at + k];
                    new ReadOnlySpan<byte>(_buf + e.Offset, e.Length).CopyTo(arrays[k]);
                }
            }
            for (int k = 0; k < arrays.Length; k++)
            {
                ref var e = ref want[at + k];
                list.Add(new VideoSample { Pts = e.Pts, Keyframe = e.Key, Data = arrays[k] });
            }
            at = stop;
        }
        return list;
    }

    public void Clear()
    {
        lock (_gate) { _count = 0; _first = 0; _head = 0; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_buf != null) { NativeMemory.Free(_buf); _buf = null; }
            _count = 0;
        }
    }
}
