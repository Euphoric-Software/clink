using System.Diagnostics;
using Clink.Engine;

namespace Clink.Tests;

/// <summary>
/// <see cref="RingStore"/>: a clip snapshot must not hold the ring while it copies the clip out, or the encoder thread (which pushes into
/// the ring at the frame rate) stalls and the encoder drops frames; and what it copies must be exactly what was pushed, also when the
/// ring evicts the clip's start while the copy runs.
/// </summary>
internal static class RingStoreTests
{
    private const long Period = 166_667;          // 60 fps, 100 ns units
    private const int KeyEvery = 120;

    private static long Pts(int i) => 1_000_000_000L + i * Period;
    private static int IndexOf(long pts) => (int)((pts - 1_000_000_000L) / Period);

    /// <summary>Sample <paramref name="i"/>'s bytes: its index, then a pattern that depends on it (so a sample overwritten by another is caught).</summary>
    private static void Fill(byte[] buf, int i)
    {
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), i);
        byte b = (byte)(i * 31 + 7);
        buf.AsSpan(4).Fill(b);
        buf[^1] = (byte)(i ^ 0x5A);
    }

    private static void CheckSamples(List<VideoSample> snap, int len, int firstIndex, int lastIndex, string what)
    {
        Check.True(snap.Count > 0, what + ": samples");
        Check.True(snap[0].Keyframe, what + ": starts on a keyframe");
        Check.Equal(Pts(firstIndex), snap[0].Pts, what + ": first sample");
        Check.Equal(Pts(lastIndex), snap[^1].Pts, what + ": last sample");
        Check.Equal(lastIndex - firstIndex + 1, snap.Count, what + ": no gap, no duplicate");
        var want = new byte[len];
        for (int k = 0; k < snap.Count; k++)
        {
            var s = snap[k];
            int i = firstIndex + k;
            Check.Equal(Pts(i), s.Pts, $"{what}: sample {k} pts");
            Check.Equal(i % KeyEvery == 0, s.Keyframe, $"{what}: sample {k} keyframe flag");
            Fill(want, i);
            if (!s.Data.AsSpan().SequenceEqual(want)) Check.Fail($"{what}: sample {k} (index {i}) bytes differ from what was pushed");
        }
    }

    /// <summary>
    /// The P2 harness: a 256 MB ring full of ~50 KB samples, a whole-ring snapshot on this thread while another thread pushes at 60 Hz.
    /// It prints the snapshot time and the longest push wait, and fails when a push waited for the snapshot (see the limit below).
    /// </summary>
    [UnitTest]
    public static void RingStore_snapshot_does_not_block_pushes_for_the_whole_copy()
    {
        const int Len = 50 * 1024;
        using var ring = new RingStore(256L << 20);
        var buf = new byte[Len];
        int n = 0;
        // Fill to well past one lap, so every push from here on evicts (the steady state of a running ring).
        long laps = ring.Capacity / Len * 3 / 2;
        for (; n < laps; n++) { Fill(buf, n); ring.Push(Pts(n), n % KeyEvery == 0, buf); }
        var range = ring.Range()!.Value;
        int oldest = IndexOf(range.oldest), newest = IndexOf(range.newest);
        // Start two GOPs after the oldest keyframe: the pusher evicts ~1 sample per frame, far less than two GOPs during the copy, so nothing the clip needs is evicted.
        int firstKey = (oldest + KeyEvery - 1) / KeyEvery * KeyEvery + 2 * KeyEvery;

        long maxWaitTicks = 0;
        int pushedDuring = 0;
        using var stop = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        int next = n;
        var pusher = new Thread(() =>
        {
            var pb = new byte[Len];
            long period = Stopwatch.Frequency / 60;
            long due = Stopwatch.GetTimestamp();
            started.Set();
            while (!stop.IsSet)
            {
                Fill(pb, next);
                long t0 = Stopwatch.GetTimestamp();
                ring.Push(Pts(next), next % KeyEvery == 0, pb);
                long waited = Stopwatch.GetTimestamp() - t0;
                if (waited > maxWaitTicks) maxWaitTicks = waited;
                next++; pushedDuring++;
                due += period;
                long sleep = (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (sleep > 0) Thread.Sleep((int)sleep);
                else due = Stopwatch.GetTimestamp();
            }
        }) { IsBackground = true, Name = "ring-pusher" };
        pusher.Start();
        started.Wait();
        Thread.Sleep(50);   // a few pushes first

        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var snap = ring.Snapshot(Pts(firstKey), Pts(newest));
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        Thread.Sleep(50);
        stop.Set();
        pusher.Join();

        double snapMs = sw.Elapsed.TotalMilliseconds;
        double waitMs = maxWaitTicks * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  (snapshot {snap.Count} samples, {(long)snap.Count * Len >> 20} MB in {snapMs:0.0} ms; {pushedDuring} pushes, longest push wait {waitMs:0.00} ms; allocated {alloc >> 20} MB)");
        CheckSamples(snap, Len, firstKey, newest, "snapshot");
        // Here the longest wait is about 1 ms against a ~100 ms snapshot (a whole-clip lock waited ~66 ms of an ~82 ms one). The limit
        // only catches that regression, not a GC pause of the pusher on a loaded runner: a push waits for the snapshot when it waits for
        // over half of it, and for at least 50 ms (3 frames).
        double limit = Math.Max(50.0, snapMs / 2);
        Check.True(waitMs <= limit, $"longest push wait {waitMs:0.00} ms is at most {limit:0.0} ms (max of 50 ms and half the {snapMs:0.0} ms snapshot)");
    }

    private const int SmallLen = 64 * 1024;   // 512 samples per lap of a 32 MB ring, 64 per snapshot chunk

    private static RingStore SmallRing(int samples)
    {
        var ring = new RingStore(RingStore.MinBytes);
        var buf = new byte[SmallLen];
        for (int i = 0; i < samples; i++) { Fill(buf, i); ring.Push(Pts(i), i % KeyEvery == 0, buf); }
        return ring;
    }

    [UnitTest]
    public static void RingStore_snapshot_restarts_at_a_surviving_keyframe_when_the_ring_evicts_the_clip_start_mid_copy()
    {
        using var ring = SmallRing(600);
        int oldest = IndexOf(ring.Range()!.Value.oldest);
        Check.True(oldest > 0 && oldest < 120, $"the ring wrapped (oldest sample {oldest})");
        int chunks = 0, expectedFirst = -1;
        ring.BetweenSnapshotChunks = () =>
        {
            if (chunks++ > 0) return;
            // After the first chunk (samples 120-183): 200 more pushes evict about 200 of the oldest, the next ones to copy among them.
            var buf = new byte[SmallLen];
            for (int i = 600; i < 800; i++) { Fill(buf, i); ring.Push(Pts(i), i % KeyEvery == 0, buf); }
            int survivor = IndexOf(ring.Range()!.Value.oldest);
            expectedFirst = (survivor + KeyEvery - 1) / KeyEvery * KeyEvery;
        };
        var snap = ring.Snapshot(Pts(120), Pts(599));
        Check.True(chunks > 1, $"copied in chunks ({chunks} breaks)");
        Check.True(expectedFirst > 183, $"the eviction reached past the first chunk (first keyframe left {expectedFirst})");
        CheckSamples(snap, SmallLen, expectedFirst, 599, "after the eviction");
    }

    [UnitTest]
    public static void RingStore_snapshot_is_empty_when_the_ring_evicts_every_remaining_keyframe_mid_copy()
    {
        using var ring = SmallRing(600);
        int chunks = 0, survivor = -1;
        ring.BetweenSnapshotChunks = () =>
        {
            if (chunks++ > 0) return;
            // After the first chunk: 500 more pushes leave only the newest ~512 samples, so of the clip (120-599) only a tail after its last keyframe (480) is left.
            var buf = new byte[SmallLen];
            for (int i = 600; i < 1100; i++) { Fill(buf, i); ring.Push(Pts(i), i % KeyEvery == 0, buf); }
            survivor = IndexOf(ring.Range()!.Value.oldest);
        };
        var snap = ring.Snapshot(Pts(120), Pts(599));
        Check.True(survivor > 480 && survivor <= 599, $"the eviction took every keyframe of the clip but left samples of it (oldest left {survivor})");
        Check.Equal(0, snap.Count, "no keyframe to start from: an empty clip");
    }

    [UnitTest]
    public static void RingStore_snapshot_without_eviction_copies_the_whole_range_across_chunks()
    {
        using var ring = SmallRing(600);
        int chunks = 0;
        ring.BetweenSnapshotChunks = () => chunks++;
        var snap = ring.Snapshot(Pts(130), Pts(590));   // from the keyframe before 130
        Check.True(chunks >= 6, $"several chunks ({chunks} breaks)");
        CheckSamples(snap, SmallLen, 120, 590, "range");
    }

    [UnitTest]
    public static void RingStore_snapshot_is_empty_when_the_ring_is_cleared_or_disposed_mid_copy()
    {
        using (var ring = SmallRing(600))
        {
            ring.BetweenSnapshotChunks = () =>
            {
                ring.Clear();
                var buf = new byte[SmallLen];
                for (int i = 1000; i < 1300; i++) { Fill(buf, i); ring.Push(Pts(i), i % KeyEvery == 0, buf); }   // another encoder's samples
            };
            Check.Equal(0, ring.Snapshot(Pts(120), Pts(599)).Count, "cleared and refilled");
        }
        var gone = SmallRing(600);
        gone.BetweenSnapshotChunks = gone.Dispose;
        Check.Equal(0, gone.Snapshot(Pts(120), Pts(599)).Count, "disposed");
    }
}
