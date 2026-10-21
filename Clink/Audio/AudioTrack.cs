using Clink.Encode;

namespace Clink.Audio;

internal readonly record struct AacFrame(long Pts, byte[] Data);

/// <summary>
/// One recorded audio track: keeps a gap-free 48 kHz timeline (silence is inserted when the source delivers
/// nothing), encodes to AAC and holds the recent frames in memory for the replay buffer.
/// </summary>
internal sealed unsafe class AudioTrack : IDisposable
{
    public const int Rate = WasapiSource.SampleRate;
    private const int FrameSamples = AacEncoder.FrameSamples;

    public string Name { get; }
    public WasapiSource? Source { get; set; }
    public bool Enabled { get; set; } = true;

    private readonly AacEncoder _enc = new(Rate, 2, 24000);
    private readonly float[] _acc = new float[FrameSamples * 2];
    private int _accFrames;
    private long _basePts;
    private long _samplePos;      // frames pushed since _basePts
    private bool _started;
    private readonly List<AacFrame> _frames = new();
    private readonly object _gate = new();
    private long _frameIndex;     // AAC frames emitted
    private long _firstFramePts;  // pts of input frame 0 (computed, not the encoder's)

    public AudioTrack(string name) { Name = name; }

    public byte[] AudioSpecificConfig => _enc.AudioSpecificConfig;
    public int Bitrate => _enc.BytesPerSecond * 8;
    public bool Started => _started;
    public long TimelineEnd => _started ? PtsAt(_samplePos) : 0;
    public long FrameCount { get { lock (_gate) return _frames.Count; } }

    private long PtsAt(long samplePos) => _basePts + samplePos * 10_000_000L / Rate;

    public void OnPacket(float* stereo, int frames, long qpc, bool silent)
    {
        if (!Enabled) return;
        if (!_started) { _basePts = qpc; _samplePos = 0; _started = true; }
        long expected = PtsAt(_samplePos);
        long gap = qpc - expected;
        if (gap > 300_000) // > 30 ms late: fill the hole with silence
        {
            if (gap > 100_000_000) // > 10 s: machine slept or source was dead; restart the timeline
            {
                Log.Info($"Audio '{Name}': timeline jump {gap / 10_000} ms, resyncing");
                ResetTimeline(qpc);
            }
            else PushSilence(gap * Rate / 10_000_000);
        }
        else if (gap < -300_000) // overlapping: drop the already-covered part
        {
            long skip = (-gap) * Rate / 10_000_000;
            if (skip >= frames) return;
            stereo += skip * 2;
            frames -= (int)skip;
        }
        Push(stereo, frames);
    }

    private void ResetTimeline(long pts)
    {
        _basePts = pts;
        _samplePos = 0;
        _accFrames = 0;
        _frameIndex = 0;
        lock (_gate) _frames.Clear();
    }

    /// <summary>Keeps the timeline moving while a source is silent/absent (called periodically).</summary>
    public void PadSilenceTo(long until)
    {
        if (!Enabled) return;
        if (!_started) { _basePts = until; _samplePos = 0; _started = true; return; }
        long expected = PtsAt(_samplePos);
        long gap = until - expected;
        if (gap <= 0) return;
        if (gap > 100_000_000) { ResetTimeline(until); return; }
        PushSilence(gap * Rate / 10_000_000);
    }

    private void PushSilence(long frames)
    {
        float* zero = stackalloc float[512 * 2];
        new Span<float>(zero, 512 * 2).Clear();
        while (frames > 0)
        {
            int n = (int)Math.Min(512, frames);
            Push(zero, n);
            frames -= n;
        }
    }

    private void Push(float* stereo, int frames)
    {
        while (frames > 0)
        {
            int n = Math.Min(frames, FrameSamples - _accFrames);
            new ReadOnlySpan<float>(stereo, n * 2).CopyTo(_acc.AsSpan(_accFrames * 2));
            _accFrames += n;
            stereo += n * 2;
            frames -= n;
            _samplePos += n;
            if (_accFrames == FrameSamples) FlushFrame();
        }
    }

    private void FlushFrame()
    {
        long pts = PtsAt(_samplePos - FrameSamples);
        if (_frameIndex == 0) _firstFramePts = pts;
        Span<byte> pcm = stackalloc byte[FrameSamples * 4];
        for (int i = 0; i < FrameSamples * 2; i++)
        {
            float v = _acc[i];
            int s = (int)MathF.Round(Math.Clamp(v, -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)s; pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        _accFrames = 0;
        long thisIndex = _frameIndex++;
        _enc.Encode(pcm, pts, (encPts, data) =>
        {
            lock (_gate) _frames.Add(new AacFrame(encPts, data));
        });
    }

    public void Trim(long olderThan)
    {
        lock (_gate)
        {
            int n = 0;
            while (n < _frames.Count && _frames[n].Pts < olderThan) n++;
            if (n > 0) _frames.RemoveRange(0, n);
        }
    }

    public List<AacFrame> Snapshot(long from, long to)
    {
        lock (_gate)
        {
            long frameDur = FrameSamples * 10_000_000L / Rate;
            return _frames.Where(f => f.Pts + frameDur > from && f.Pts <= to).ToList();
        }
    }

    public void Dispose()
    {
        Source?.Dispose();
        _enc.Dispose();
    }
}
