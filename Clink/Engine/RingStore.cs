using System.Runtime.InteropServices;

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
    private struct Entry { public long Offset; public int Length; public long Pts; public bool Key; }

    private byte* _buf;
    private readonly long _cap;
    private long _head;              // next write offset
    private Entry[] _entries = new Entry[4096];
    private int _first;              // index of oldest entry in the circular entries array
    private int _count;
    private readonly object _gate = new();

    public long Capacity => _cap;
    public long SamplesStored { get { lock (_gate) return _count; } }

    public RingStore(long capacityBytes)
    {
        _cap = Math.Max(capacityBytes, 16 << 20);
        _buf = (byte*)NativeMemory.Alloc((nuint)_cap);
    }

    private ref Entry At(int i) => ref _entries[(_first + i) % _entries.Length];

    public void Push(long pts, bool keyframe, ReadOnlySpan<byte> data)
    {
        if (data.Length > _cap / 2) return; // absurd sample; drop
        lock (_gate)
        {
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
            ne.Offset = _head; ne.Length = data.Length; ne.Pts = pts; ne.Key = keyframe;
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

    /// <summary>Copies out all samples from the last keyframe at or before <paramref name="fromPts"/> (or the first keyframe after it).</summary>
    public List<VideoSample> Snapshot(long fromPts, long toPts)
    {
        lock (_gate)
        {
            int start = -1;
            for (int i = 0; i < _count; i++)
            {
                ref var e = ref At(i);
                if (e.Pts > fromPts) break;
                if (e.Key) start = i;
            }
            if (start < 0)
                for (int i = 0; i < _count; i++) if (At(i).Key && At(i).Pts >= fromPts) { start = i; break; }
            var list = new List<VideoSample>();
            if (start < 0) return list;
            for (int i = start; i < _count; i++)
            {
                ref var e = ref At(i);
                if (e.Pts > toPts) break;
                var data = new byte[e.Length];
                new ReadOnlySpan<byte>(_buf + e.Offset, e.Length).CopyTo(data);
                list.Add(new VideoSample { Pts = e.Pts, Keyframe = e.Key, Data = data });
            }
            return list;
        }
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
