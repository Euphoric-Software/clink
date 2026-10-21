using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Native;

namespace Clink.Playback;

/// <summary>
/// Preview player for the trim editor: decodes the video to BGRA frames and mixes the audio tracks of the clip
/// (per-track gain/mute, ramped so changes never click) into one WASAPI output. Audio is the master clock; a single
/// worker thread owns all Media Foundation readers, the UI only calls <see cref="Play"/>, <see cref="Pause"/>,
/// <see cref="Seek"/> and <see cref="TryGetFrame"/>.
/// </summary>
internal sealed unsafe class ClipPlayer : IDisposable
{
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;
    private const int Rate = AudioOutput.SampleRate;
    private const int Block = 1024;
    private const int MaxQueue = 4;
    private const double Tol = 0.004;

    private sealed class Frame
    {
        public double Pts;
        public byte[] Data = Array.Empty<byte>();
        public long Seq;
        /// <summary>The frame a seek landed on: shown as soon as it is queued, whatever the clock or a newer seek says.</summary>
        public bool SeekResult;
    }

    /// <summary>One audio stream: its own source reader plus a FIFO of decoded 48 kHz stereo float on the clip timeline.</summary>
    private sealed class AudioTrackState
    {
        public IMFSourceReader* Reader;
        public uint Stream;
        public bool Eos;
        public bool SrcFloat = true;
        public int SrcChannels = 2;
        public int SrcRate = Rate;
        /// <summary>Gain the last mixed block ended on; the next block ramps from here to the requested gain.</summary>
        public float Current;

        private float[] _data = new float[Rate * 2 * 2];
        private int _start, _count;        // frames
        private long _firstFrame;          // absolute 48 kHz frame index of _data[_start]
        public long EndFrame = long.MinValue;

        public void Clear() { _start = 0; _count = 0; _firstFrame = 0; EndFrame = long.MinValue; Eos = false; }

        public void Append(ReadOnlySpan<float> stereo, long frame)
        {
            int n = stereo.Length / 2;
            if (n == 0) return;
            if (_count == 0 || EndFrame == long.MinValue) { _start = 0; _firstFrame = frame; }
            else
            {
                long gap = frame - (_firstFrame + _count);
                if (gap > Rate * 2 || gap < -Rate * 2) { _start = 0; _count = 0; _firstFrame = frame; }
                else if (gap > 48) { AppendSilence((int)gap); }
                else if (gap < -48)
                {
                    int skip = (int)(-gap);
                    if (skip >= n) return;
                    stereo = stereo.Slice(skip * 2);
                    n -= skip;
                }
            }
            Ensure(n);
            stereo.CopyTo(_data.AsSpan((_start + _count) * 2, n * 2));
            _count += n;
            EndFrame = _firstFrame + _count;
        }

        private void AppendSilence(int frames)
        {
            Ensure(frames);
            _data.AsSpan((_start + _count) * 2, frames * 2).Clear();
            _count += frames;
            EndFrame = _firstFrame + _count;
        }

        private void Ensure(int frames)
        {
            int cap = _data.Length / 2;
            int need = _count + frames;
            if (need > cap)
            {
                var nd = new float[Math.Max(need * 2, cap * 2) * 2];
                Array.Copy(_data, _start * 2, nd, 0, _count * 2);
                _data = nd;
                _start = 0;
            }
            else if (_start + need > cap)
            {
                Array.Copy(_data, _start * 2, _data, 0, _count * 2);
                _start = 0;
            }
        }

        /// <summary>
        /// Adds this track's audio for the block [t0, t0+frames) into <paramref name="dst"/>, with the gain ramping linearly
        /// from <paramref name="from"/> at the start of the block to <paramref name="to"/> at its end, so a mute or slider
        /// move is a short fade instead of a step.
        /// </summary>
        public void MixInto(float[] dst, long t0, int frames, float from, float to)
        {
            if (_count == 0) return;
            long lo = Math.Max(t0, _firstFrame), hi = Math.Min(t0 + frames, _firstFrame + _count);
            if (hi <= lo) return;
            int src = (int)(_start + (lo - _firstFrame)) * 2, di = (int)(lo - t0) * 2, n = (int)(hi - lo);
            float step = (to - from) / frames;
            float g = from + step * (int)(lo - t0);
            for (int i = 0; i < n; i++, g += step)
            {
                dst[di + i * 2] += g * _data[src + i * 2];
                dst[di + i * 2 + 1] += g * _data[src + i * 2 + 1];
            }
        }

        public void Discard(long beforeFrame)
        {
            if (_count == 0) return;
            long drop = beforeFrame - _firstFrame;
            if (drop <= 0) return;
            if (drop >= _count) { _start = 0; _count = 0; }
            else { _start += (int)drop; _count -= (int)drop; _firstFrame += drop; }
        }

        /// <summary>Converts a decoded packet to 48 kHz interleaved stereo float.</summary>
        public float[] Convert(byte[] raw)
        {
            int bytesPer = SrcFloat ? 4 : 2;
            int frames = raw.Length / (bytesPer * SrcChannels);
            float[] st;
            if (SrcFloat && SrcChannels == 2)
            {
                st = new float[frames * 2];
                MemoryMarshal.Cast<byte, float>(raw.AsSpan(0, frames * 8)).CopyTo(st);
            }
            else
            {
                st = new float[frames * 2];
                for (int i = 0; i < frames; i++)
                {
                    float l, r;
                    if (SrcFloat)
                    {
                        l = BitConverter.ToSingle(raw, (i * SrcChannels) * 4);
                        r = SrcChannels > 1 ? BitConverter.ToSingle(raw, (i * SrcChannels + 1) * 4) : l;
                    }
                    else
                    {
                        l = BitConverter.ToInt16(raw, (i * SrcChannels) * 2) / 32768f;
                        r = SrcChannels > 1 ? BitConverter.ToInt16(raw, (i * SrcChannels + 1) * 2) / 32768f : l;
                    }
                    st[i * 2] = l; st[i * 2 + 1] = r;
                }
            }
            if (SrcRate == Rate || frames == 0) return st;
            // Fallback only (Clink clips are 48 kHz): linear resample.
            int outFrames = (int)((long)frames * Rate / SrcRate);
            var rs = new float[outFrames * 2];
            double step = (double)SrcRate / Rate;
            for (int i = 0; i < outFrames; i++)
            {
                double pos = i * step;
                int i0 = (int)pos; int i1 = Math.Min(i0 + 1, frames - 1);
                float f = (float)(pos - i0);
                rs[i * 2] = st[i0 * 2] + (st[i1 * 2] - st[i0 * 2]) * f;
                rs[i * 2 + 1] = st[i0 * 2 + 1] + (st[i1 * 2 + 1] - st[i0 * 2 + 1]) * f;
            }
            return rs;
        }
    }

    // ---- video
    private IMFSourceReader* _video;
    private readonly uint _videoStream;
    private int _w, _h, _stride;
    private bool _sizeMismatch, _sizeWarned;
    private bool _videoEos;
    private double _lastVideoPts = -1;
    private double _frameDuration = 1.0 / 60;

    // ---- audio
    private readonly AudioTrackState[] _tracks;
    private AudioOutput? _out;
    private bool _useAudioClock;
    private readonly float[] _mix = new float[Block * 2];
    private long _outFrame;

    // ---- clock (guarded by _clock)
    private readonly object _clock = new();
    private readonly Stopwatch _sw = new();
    private double _seekBase;
    private double _pendingSeek = double.NaN;
    private volatile bool _seeking;   // a seek was requested and no seek has landed since

    // ---- commands (guarded by _gate)
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private bool _wantPlaying, _disposed;
    private bool _playing;            // worker-owned
    private Thread? _thread;

    // ---- decoded frame queue (guarded by _queueLock)
    private readonly object _queueLock = new();
    private readonly List<Frame> _queue = new();
    private readonly Stack<byte[]> _pool = new();
    private byte[]? _shown;
    private long _shownSeq = -1;
    private long _frameSeq;

    /// <summary>Clip length in seconds (0 if unknown).</summary>
    public double Duration { get; }
    /// <summary>Nominal length of one video frame in seconds (from the stream's frame rate; 1/60 if it has none).</summary>
    public double FrameDuration => _frameDuration;
    public int TrackCount => _tracks.Length;
    public int VideoWidth => _w;
    public int VideoHeight => _h;
    /// <summary>Per-track linear gain (1 = unchanged); read live by the mixer, applied as a short ramp.</summary>
    public float[] Gain { get; }
    /// <summary>Per-track mute; read live by the mixer, applied as a short ramp.</summary>
    public bool[] Muted { get; }
    public bool IsPlaying { get { lock (_gate) return _wantPlaying; } }
    /// <summary>Set when the worker died; the player is dead from then on.</summary>
    public string? LastError { get; private set; }
    /// <summary>Set when there is no usable output device; the preview keeps running silently on its own clock.</summary>
    public string? AudioError { get; private set; }
    /// <summary>Raised (on the worker thread) when playback reached the end of the clip; the player is paused then.</summary>
    public event Action? Ended;

    public ClipPlayer(string path, int previewMaxWidth)
    {
        Mf.EnsureStarted();
        IMFAttributes* attrs = Mf.CreateAttributes(2);
        Mf.SetU32(attrs, PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
        IMFSourceReader* reader;
        HRESULT hr = PInvoke.MFCreateSourceReaderFromURL(path, attrs, &reader);
        attrs->Release();
        hr.Check("MFCreateSourceReaderFromURL");
        _video = reader;
        _tracks = Array.Empty<AudioTrackState>();
        Gain = Array.Empty<float>();
        Muted = Array.Empty<bool>();
        try
        {
            var audioIdx = new List<uint>();
            uint videoIdx = uint.MaxValue;
            int nw = 0, nh = 0;
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                if (_video->GetNativeMediaType(i, 0, &t).Failed) break;
                var a = (IMFAttributes*)t;
                Guid major = Mf.GetGuid(a, PInvoke.MF_MT_MAJOR_TYPE);
                if (major == PInvoke.MFMediaType_Video && videoIdx == uint.MaxValue)
                {
                    videoIdx = i;
                    ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE);
                    nw = (int)(size >> 32); nh = (int)(size & 0xFFFFFFFF);
                    ulong rate = Mf.GetU64(a, PInvoke.MF_MT_FRAME_RATE);
                    uint num = (uint)(rate >> 32), den = (uint)(rate & 0xFFFFFFFF);
                    if (num > 0 && den > 0 && den < num) _frameDuration = (double)den / num;
                }
                else if (major == PInvoke.MFMediaType_Audio) audioIdx.Add(i);
                t->Release();
            }
            if (videoIdx == uint.MaxValue) throw new InvalidOperationException("No video stream in file");
            _videoStream = videoIdx;
            _video->SetStreamSelection(AllStreams, false);
            _video->SetStreamSelection(videoIdx, true).Check("SetStreamSelection(video)");

            // RGB32 output, scaled down for the preview when the source is wider than the viewer needs.
            int sw = nw, sh = nh;
            if (previewMaxWidth > 0 && nw > previewMaxWidth && nh > 0)
            {
                sw = previewMaxWidth & ~1;
                sh = Math.Max(2, (int)Math.Round((double)nh * sw / nw) & ~1);
            }
            hr = SetVideoType(sw, sh, sw != nw);
            if (hr.Failed && sw != nw) hr = SetVideoType(nw, nh, false);
            hr.Check("SetCurrentMediaType(RGB32)");
            RefreshVideoType(initial: true);

            PROPVARIANT pv = default;
            Guid durKey = PInvoke.MF_PD_DURATION;
            if (_video->GetPresentationAttribute(MediaSource, &durKey, &pv).Succeeded && pv.vt == VARENUM.VT_UI8) Duration = (long)pv.uhVal / 10_000_000.0;
            PInvoke.PropVariantClear(ref pv);

            // Every slot is populated before any reader is created so that ReleaseAll can walk the array after a failure
            // part-way through (a null Reader is simply skipped).
            _tracks = new AudioTrackState[audioIdx.Count];
            for (int i = 0; i < _tracks.Length; i++) _tracks[i] = new AudioTrackState { Stream = audioIdx[i] };
            Gain = new float[_tracks.Length];
            Array.Fill(Gain, 1f);
            Muted = new bool[_tracks.Length];
            foreach (var ts in _tracks)
            {
                IMFSourceReader* ar;
                PInvoke.MFCreateSourceReaderFromURL(path, null, &ar).Check("MFCreateSourceReaderFromURL(audio)");
                ts.Reader = ar; // owned by ReleaseAll from here on
                ar->SetStreamSelection(AllStreams, false);
                ar->SetStreamSelection(ts.Stream, true).Check("SetStreamSelection(audio)");
                ConfigureAudio(ts);
            }
            Log.Info($"Preview: {Path.GetFileName(path)} {nw}x{nh} -> {_w}x{_h} @ {1 / _frameDuration:0.##} fps, {_tracks.Length} audio track(s), {Duration:0.00}s");

            _pendingSeek = 0; // show the first frame right away
            _seeking = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "Clink.Preview", Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    private HRESULT SetVideoType(int w, int h, bool withSize)
    {
        IMFMediaType* want = Mf.CreateMediaType();
        try
        {
            var a = (IMFAttributes*)want;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            if (withSize) Mf.SetSize(a, PInvoke.MF_MT_FRAME_SIZE, (uint)w, (uint)h);
            return _video->SetCurrentMediaType(_videoStream, null, want);
        }
        finally { want->Release(); }
    }

    private void RefreshVideoType(bool initial)
    {
        IMFMediaType* cur;
        if (_video->GetCurrentMediaType(_videoStream, &cur).Failed) return;
        try
        {
            var a = (IMFAttributes*)cur;
            ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE);
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            int stride = unchecked((int)Mf.GetU32(a, PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(w * 4)));
            if (initial)
            {
                if (w <= 0 || h <= 0) throw new InvalidOperationException("Decoder reported an empty frame size");
                _w = w; _h = h; _stride = stride == 0 ? w * 4 : stride;
            }
            else
            {
                _sizeMismatch = w != _w || h != _h;
                if (!_sizeMismatch) _stride = stride == 0 ? w * 4 : stride;
                else if (!_sizeWarned) { _sizeWarned = true; Log.Warn($"Preview: frame size changed to {w}x{h} mid-stream, frames skipped"); }
            }
        }
        finally { cur->Release(); }
    }

    private static void ConfigureAudio(AudioTrackState t)
    {
        if (!TrySetAudioType(t, PInvoke.MFAudioFormat_Float, full: true) &&
            !TrySetAudioType(t, PInvoke.MFAudioFormat_PCM, full: true) &&
            !TrySetAudioType(t, PInvoke.MFAudioFormat_Float, full: false) &&
            !TrySetAudioType(t, PInvoke.MFAudioFormat_PCM, full: false))
            throw new InvalidOperationException("Audio stream cannot be decoded to PCM");
        IMFMediaType* cur;
        t.Reader->GetCurrentMediaType(t.Stream, &cur).Check("GetCurrentMediaType(audio)");
        try
        {
            var a = (IMFAttributes*)cur;
            t.SrcFloat = Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE) == PInvoke.MFAudioFormat_Float;
            t.SrcChannels = Math.Max(1, (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2));
            t.SrcRate = Math.Max(1, (int)Mf.GetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate));
            if (!t.SrcFloat && Mf.GetU32(a, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16) != 16)
                throw new InvalidOperationException("Unsupported PCM sample size");
            if (t.SrcRate != Rate || t.SrcChannels != 2 || !t.SrcFloat)
                Log.Info($"Preview audio stream {t.Stream}: decoding as {(t.SrcFloat ? "float" : "pcm16")} {t.SrcChannels}ch {t.SrcRate} Hz (converted)");
        }
        finally { cur->Release(); }
    }

    private static bool TrySetAudioType(AudioTrackState t, Guid subtype, bool full)
    {
        IMFMediaType* mt = Mf.CreateMediaType();
        try
        {
            var a = (IMFAttributes*)mt;
            Mf.SetGuid(a, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            Mf.SetGuid(a, PInvoke.MF_MT_SUBTYPE, subtype);
            if (full)
            {
                uint bytes = subtype == PInvoke.MFAudioFormat_Float ? 4u : 2u;
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, bytes * 8);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, bytes * 2);
                Mf.SetU32(a, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytes * 2 * Rate);
                Mf.SetU32(a, PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            }
            return t.Reader->SetCurrentMediaType(t.Stream, null, mt).Succeeded;
        }
        finally { mt->Release(); }
    }

    // ------------------------------------------------------------------ public control (UI thread)

    /// <summary>Current playback position in seconds (reflects a requested seek immediately).</summary>
    public double Position
    {
        get
        {
            lock (_clock)
            {
                if (!double.IsNaN(_pendingSeek)) return _pendingSeek;
                var o = _out;
                double t = _useAudioClock && o != null ? o.PlayedSeconds : _sw.Elapsed.TotalSeconds;
                double p = _seekBase + t;
                return Duration > 0 ? Math.Min(p, Duration) : p;
            }
        }
    }

    public void Play()
    {
        lock (_gate) { if (_disposed) return; _wantPlaying = true; }
        _wake.Set();
    }

    public void Pause()
    {
        lock (_gate) { if (_disposed) return; _wantPlaying = false; }
        _wake.Set();
    }

    /// <summary>Jumps to <paramref name="seconds"/>; rapid calls coalesce (only the latest target is applied).</summary>
    public void Seek(double seconds)
    {
        if (double.IsNaN(seconds)) return;
        lock (_clock)
        {
            _pendingSeek = Math.Max(0, Duration > 0 ? Math.Min(seconds, Duration) : seconds);
            _seeking = true;
        }
        _wake.Set();
    }

    /// <summary>
    /// Returns the newest decoded frame due at <paramref name="position"/> (dropping older ones). The array stays valid until
    /// the next successful call. Returns false when there is nothing new to show.
    /// </summary>
    public bool TryGetFrame(double position, out byte[] bgra, out double pts)
    {
        bgra = Array.Empty<byte>();
        pts = 0;
        // Frames decoded ahead of a seek that has not landed yet would flash the wrong picture. Once a seek has landed its
        // own frame (SeekResult) is shown whatever the clock says, even if a newer seek is already queued behind it, so a
        // drag keeps updating the picture instead of freezing until the mouse stops.
        if (_seeking) return false;
        lock (_queueLock)
        {
            while (_queue.Count >= 2 && _queue[1].Pts <= position + Tol)
            {
                var old = _queue[0];
                _queue.RemoveAt(0);
                ReturnLocked(old.Data);
            }
            if (_queue.Count > 0)
            {
                var head = _queue[0];
                if (head.Seq != _shownSeq && (head.SeekResult || head.Pts <= position + Tol))
                {
                    // The previously shown buffer is no longer queued (dropped above or by a seek): recycle it now.
                    byte[]? prev = _shown;
                    _shown = head.Data;
                    _shownSeq = head.Seq;
                    if (prev != null && prev != head.Data) ReturnLocked(prev);
                    bgra = head.Data;
                    pts = head.Pts;
                    return true;
                }
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ worker

    private void Worker()
    {
        try
        {
            if (_tracks.Length > 0)
            {
                try { _out = new AudioOutput(); lock (_clock) _useAudioClock = true; }
                catch (Exception ex)
                {
                    Log.Warn("Preview audio output unavailable, playing silently: " + ex.Message);
                    _out = null;
                    AudioError = "no output device, the preview is silent";
                }
                // The first ReadSample starts a reader's media source (a few hundred ms for all tracks); do it here
                // rather than inside the user's first seek.
                foreach (var t in _tracks) ReadAudio(t);
            }
            while (true)
            {
                bool wantPlay, exit;
                lock (_gate) { exit = _disposed; wantPlay = _wantPlaying; }
                if (exit) break;
                double seek;
                lock (_clock) { seek = _pendingSeek; }
                if (!double.IsNaN(seek))
                {
                    DoSeek(seek);
                    // The frame for this target is queued (flagged SeekResult): let the UI show it even when the user has
                    // already asked for another position, otherwise a drag never updates the picture.
                    _seeking = false;
                    continue;
                }
                if (wantPlay != _playing)
                {
                    _playing = wantPlay;
                    if (wantPlay) StartClock(); else StopClock();
                }
                if (_playing)
                {
                    var o = _out;
                    if (_useAudioClock && o != null)
                    {
                        // Keep a short lead queued (TargetFrames, ~80 ms): enough that video decoding can never starve the
                        // audio clock, short enough that a mute or volume change is heard right away. The last write blocks
                        // until a block has drained below that level, which is what paces the loop.
                        int fill = o.TargetFrames;
                        int topUp = 0;
                        while (!o.Failed && o.PendingFrames + Block <= fill && topUp++ < 16) { MixBlock(); o.Write(_mix, fill); }
                        if (!o.Failed) { MixBlock(); o.Write(_mix, fill); }
                        if (o.Failed) DropAudioOutput("the output device stopped responding, the preview is silent");
                    }
                    else Thread.Sleep(4);
                    FillVideo();
                    CheckEnded();
                }
                else
                {
                    FillVideo();
                    _wake.WaitOne(30);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Preview player", ex);
        }
        finally { ReleaseAll(); }
    }

    private void StartClock()
    {
        var o = _out;
        if (_useAudioClock && o != null)
        {
            // Prime the (empty or drained) buffer before starting so the engine has data on its first pass.
            if (!o.Failed && o.PendingFrames + Block <= o.TargetFrames) { MixBlock(); o.Write(_mix, o.TargetFrames); }
            o.Start();
            if (!o.IsStarted) DropAudioOutput(o.Failed ? "the output device stopped responding, the preview is silent" : "the output device could not start, the preview is silent");
        }
        if (!_useAudioClock) lock (_clock) _sw.Start();
    }

    private void StopClock()
    {
        _out?.Stop();
        lock (_clock) _sw.Stop();
    }

    /// <summary>
    /// Switches to the stopwatch clock from the current position and discards the output after a device failure, so the
    /// preview carries on silently instead of spinning on an output that never blocks and a clock that never moves.
    /// </summary>
    private void DropAudioOutput(string why)
    {
        var o = _out;
        double pos = Position;
        lock (_clock)
        {
            if (double.IsNaN(_pendingSeek)) _seekBase = pos; // otherwise the pending seek sets the base
            _useAudioClock = false;
            _sw.Reset();
            if (_playing) _sw.Start();
        }
        _out = null;
        try { o?.Dispose(); } catch (Exception ex) { Log.Warn("Preview audio output dispose: " + ex.Message); }
        AudioError ??= why;
        Log.Warn("Preview: " + why);
    }

    private void DoSeek(double requested)
    {
        double target = Duration > 0 ? Math.Clamp(requested, 0, Duration) : Math.Max(0, requested);
        _out?.Reset();
        lock (_clock)
        {
            _seekBase = target;
            _sw.Reset();
            if (_pendingSeek == requested) _pendingSeek = double.NaN; // a newer request stays pending
        }

        // Audio: restart every track at the target; the FIFOs fill lazily from the mixer. Pointless without an output.
        if (_out != null)
        {
            foreach (var t in _tracks)
            {
                SeekReader(t.Reader, target);
                t.Clear();
            }
        }
        _outFrame = FrameOf(target);

        // Video: show the frame that is due at the target (newest pts <= target), decoding ahead only as far as needed.
        Frame? candidate = null;
        bool reseek = false, settled;
        lock (_queueLock)
        {
            // Keep the newest frame at/before the target (it is the one to display) and anything decoded ahead of it.
            int keep = -1;
            for (int i = 0; i < _queue.Count; i++) if (_queue[i].Pts <= target + Tol) keep = i;
            for (int i = 0; i < keep; i++) ReturnLocked(_queue[i].Data);
            if (keep > 0) _queue.RemoveRange(0, keep);
            // Settled when a later frame proves nothing undecoded lies between the candidate and the target.
            settled = keep >= 0 && (_queue.Count >= 2 || _videoEos);
            if (settled) _queue[0].SeekResult = true;
            else
            {
                if (keep >= 0) { candidate = _queue[0]; _queue.RemoveAt(0); }
                // Decoding forward from where the reader is (~3 ms/frame) beats a re-seek (~150 ms fixed, then it
                // decodes from the previous keyframe anyway) for hops up to about a GOP.
                bool canContinue = _queue.Count == 0 && !_videoEos && _lastVideoPts >= 0 && target >= _lastVideoPts - Tol && target - _lastVideoPts < 1.5;
                reseek = !canContinue;
                ClearQueueLocked();
                if (reseek && candidate != null) { ReturnLocked(candidate.Data); candidate = null; }
            }
        }
        if (!settled)
        {
            if (reseek)
            {
                // Seek a hair past the target: decoded timestamps sit a millisecond or two after the compressed
                // ones, and landing on the previous keyframe would mean decoding a whole GOP for nothing.
                SeekReader(_video, target + Tol);
                _videoEos = false;
                _lastVideoPts = -1;
            }
            while (!_videoEos)
            {
                var f = DecodeVideo();
                if (f == null) break;
                if (f.Pts <= target + Tol)
                {
                    if (candidate != null) Return(candidate.Data);
                    candidate = f;
                    continue;
                }
                if (candidate == null) { f.Pts = target; f.SeekResult = true; } // clip starts after the target: show its first frame now
                else { candidate.SeekResult = true; Enqueue(candidate); }
                Enqueue(f);
                candidate = null;
                break;
            }
            if (candidate != null) { candidate.SeekResult = true; Enqueue(candidate); }
        }
        if (_playing) StartClock();
    }

    private static void SeekReader(IMFSourceReader* reader, double seconds)
    {
        PROPVARIANT pos = default;
        pos.vt = VARENUM.VT_I8;
        pos.hVal = (long)(seconds * 10_000_000);
        Guid timeFormat = Guid.Empty;
        HRESULT hr = reader->SetCurrentPosition(&timeFormat, &pos);
        if (hr.Failed) Log.Warn($"Preview seek failed 0x{(uint)hr.Value:X8}");
    }

    private void MixBlock()
    {
        long t0 = _outFrame;
        long until = t0 + Block;
        Array.Clear(_mix);
        for (int i = 0; i < _tracks.Length; i++)
        {
            var t = _tracks[i];
            int reads = 0;
            while (!t.Eos && t.EndFrame < until && reads++ < 512) ReadAudio(t);
            float target = Muted[i] ? 0f : Gain[i];
            float from = t.Current;
            if (from != 0f || target != 0f) t.MixInto(_mix, t0, Block, from, target);
            t.Current = target;
            t.Discard(until);
        }
        for (int i = 0; i < _mix.Length; i++)
        {
            float v = _mix[i];
            _mix[i] = v / MathF.Max(1f, MathF.Abs(v)); // soft clip, the same curve the export uses
        }
        _outFrame = until;
    }

    private void ReadAudio(AudioTrackState t)
    {
        uint idx, flags; long ts; IMFSample* sample;
        HRESULT hr = t.Reader->ReadSample(t.Stream, 0, &idx, &flags, &ts, &sample);
        if (hr.Failed) { Log.Warn($"Preview audio ReadSample failed 0x{(uint)hr.Value:X8}"); t.Eos = true; return; }
        if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0) t.Eos = true;
        if (sample == null) return;
        try
        {
            byte[] raw = Mf.ReadSampleBytes(sample);
            if (raw.Length == 0) return;
            t.Append(t.Convert(raw), FrameOf(ts));
        }
        finally { sample->Release(); }
    }

    private void FillVideo()
    {
        long start = Stopwatch.GetTimestamp();
        while (!_videoEos && QueueCount() < MaxQueue)
        {
            var f = DecodeVideo();
            if (f == null) break;
            if (_playing && QueueCount() > 0 && f.Pts < Position - 0.05) { Return(f.Data); } // decoder is behind: catch up
            else Enqueue(f);
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > 12) break; // keep the audio loop fed
        }
    }

    private void CheckEnded()
    {
        if (_useAudioClock) foreach (var t in _tracks) if (!t.Eos) return;
        double pos = Position;
        // Video is done when the clock passed its last frame; if the queue has not been drained to end-of-stream
        // (nobody pulling frames) fall back to the reported duration.
        bool videoDone = _videoEos ? pos >= _lastVideoPts - Tol : Duration > 0 && pos >= Duration - Tol;
        if (!videoDone) return;
        _playing = false;
        lock (_gate) _wantPlaying = false;
        StopClock();
        try { Ended?.Invoke(); } catch (Exception ex) { Log.Warn("Preview Ended handler: " + ex.Message); }
    }

    /// <summary>Decodes the next video frame into a pooled BGRA buffer; null at end of stream.</summary>
    private Frame? DecodeVideo()
    {
        while (true)
        {
            uint idx, flags; long ts; IMFSample* sample;
            HRESULT hr = _video->ReadSample(_videoStream, 0, &idx, &flags, &ts, &sample);
            if (hr.Failed) { Log.Warn($"Preview video ReadSample failed 0x{(uint)hr.Value:X8}"); _videoEos = true; return null; }
            if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) RefreshVideoType(initial: false);
            if ((flags & (uint)(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) != 0)
            {
                if (sample != null) sample->Release();
                _videoEos = true;
                return null;
            }
            if (sample == null) continue;
            try
            {
                if (_sizeMismatch) continue;
                byte[] buf = Rent();
                if (!CopyFrame(sample, buf)) { Return(buf); continue; }
                _lastVideoPts = ts / 10_000_000.0;
                return new Frame { Pts = _lastVideoPts, Data = buf, Seq = _frameSeq++ };
            }
            finally { sample->Release(); }
        }
    }

    private bool CopyFrame(IMFSample* sample, byte[] dst)
    {
        IMFMediaBuffer* buf;
        if (sample->ConvertToContiguousBuffer(&buf).Failed) return false;
        try
        {
            byte* p; uint max, cur;
            if (buf->Lock(&p, &max, &cur).Failed) return false;
            try
            {
                int absStride = Math.Abs(_stride);
                int rowBytes = _w * 4;
                if (absStride < rowBytes || (long)absStride * _h > cur) return false;
                fixed (byte* d = dst)
                {
                    if (_stride == rowBytes) Buffer.MemoryCopy(p, d, dst.Length, (long)rowBytes * _h);
                    else
                    {
                        bool bottomUp = _stride < 0;
                        for (int y = 0; y < _h; y++)
                        {
                            int srcRow = bottomUp ? (_h - 1 - y) : y;
                            Buffer.MemoryCopy(p + (long)srcRow * absStride, d + (long)y * rowBytes, rowBytes, rowBytes);
                        }
                    }
                }
                return true;
            }
            finally { buf->Unlock(); }
        }
        finally { buf->Release(); }
    }

    // ------------------------------------------------------------------ queue / pool helpers

    private int QueueCount() { lock (_queueLock) return _queue.Count; }
    private void Enqueue(Frame f) { lock (_queueLock) _queue.Add(f); }

    private byte[] Rent()
    {
        lock (_queueLock) { if (_pool.Count > 0) return _pool.Pop(); }
        return new byte[_w * _h * 4];
    }

    private void Return(byte[] buf) { lock (_queueLock) ReturnLocked(buf); }

    /// <summary>Recycles a frame buffer, except the one the UI is currently displaying (it is recycled when replaced).</summary>
    private void ReturnLocked(byte[] buf)
    {
        if (buf == _shown) return;
        if (buf.Length == _w * _h * 4 && _pool.Count < 8) _pool.Push(buf);
    }

    private void ClearQueueLocked()
    {
        foreach (var f in _queue) ReturnLocked(f.Data);
        _queue.Clear();
    }

    private static long FrameOf(double seconds) => (long)Math.Round(seconds * Rate);
    private static long FrameOf(long ts100ns) => (long)Math.Round(ts100ns * (Rate / 10_000_000.0));

    // ------------------------------------------------------------------ teardown

    /// <summary>Releases everything; each step is isolated so one failure cannot leak the rest (and the file with it).</summary>
    private void ReleaseAll()
    {
        lock (_clock) _useAudioClock = false;
        var o = _out;
        _out = null;
        try { o?.Dispose(); } catch (Exception ex) { Log.Warn("Preview audio output dispose: " + ex.Message); }
        foreach (var t in _tracks)
        {
            try { ComUtil.Release(ref t.Reader); } catch (Exception ex) { Log.Warn("Preview audio reader release: " + ex.Message); }
        }
        try { ComUtil.Release(ref _video); } catch (Exception ex) { Log.Warn("Preview video reader release: " + ex.Message); }
        lock (_queueLock) { _queue.Clear(); _pool.Clear(); }
    }

    /// <summary>
    /// Stops the worker and releases the readers, and with them the clip file. Returns false when the worker did not stop
    /// in time: the file is then still open until it does, so a caller about to replace it must not go ahead.
    /// </summary>
    public bool Close()
    {
        bool first;
        lock (_gate) { first = !_disposed; _disposed = true; }
        if (first)
        {
            _wake.Set();
            var o = _out;
            o?.Stop(); // a Write waiting for buffer room gives up once the stream is stopped
        }
        var th = _thread;
        if (th == null) { if (first) ReleaseAll(); return true; }
        if (Thread.CurrentThread == th) return false;
        if (th.Join(5000)) return true;
        Log.Warn("Preview player thread did not stop in time");
        return false;
    }

    public void Dispose() => Close();
}
