namespace Clink.Engine;

/// <summary>How well one track could be lined up with another (see <see cref="TrackAlignment.Measure"/>).</summary>
/// <param name="Reliable">The lag is clear, or there is nothing to line up (a silent track).</param>
/// <param name="Lag">Frames (48 kHz) to add to the source's offset so that it sits where the Mix has it; 0 when it is not reliable.</param>
/// <param name="Peak">The normalised correlation at the best lag (1 = the source is all there is in the Mix).</param>
/// <param name="Ratio">How much higher the best lag stands than the best one more than <see cref="TrackAlignment.Guard"/> frames away from it.</param>
/// <param name="Why">What made it unclear; "silent" for a source with nothing in it; empty when clear.</param>
internal readonly record struct LagEstimate(bool Reliable, long Lag, double Peak, double Ratio, string Why);

/// <summary>
/// Finds where a source track (Game, Mic or Voice) sits against the Mix it was mixed into, for a clip whose edit lists cannot say
/// (see <see cref="ClipAlignment"/>). The Mix holds the source, so the two are correlated: the source's loudest stretches are cut out
/// of both tracks (the Mix with a margin of <see cref="MaxLag"/> frames each side), cross-correlated with an FFT, and the lag with the
/// highest normalised correlation is the answer, if it is unambiguous: clearly above every other peak of the correlation and not at
/// the edge of the range. Sound that repeats itself (a held note, a hum) correlates at several lags alike and is refused,
/// like a source too quiet to show in the Mix: a lag that is not certain is worse than none, since a source taken out of the Mix a few
/// samples late leaves a trace of itself and adds an echo of its own.
/// </summary>
internal static class TrackAlignment
{
    public const int Rate = 48000;
    /// <summary>
    /// The lags searched, frames each way (0.3 s). An older Clink's trim wrote every track at the time of its first frame, losing how
    /// far apart the tracks of the recording were: 50 ms in a clip of the first release, 100 ms or more for one taken within the first
    /// moments of a capture, when the sources have not all started yet.
    /// </summary>
    public const int MaxLag = 14400;
    /// <summary>Peaks closer than this to the best one do not count as a rival of it: the peak of band-limited sound is a few frames wide.</summary>
    public const int Guard = 64;

    private const int Block = Trimmer.PcmTrackReader.Block;
    private const int WindowBlocks = 117;                       // 119,808 frames, about 2.5 s: with the margins of both sides it fits one 2^18 FFT
    private const int MinWindowFrames = Rate / 2;
    private const int MaxWindows = 6;
    private const int MaxScanBlocks = 20 * 60 * Rate / Block;   // the first twenty minutes are looked through for the loud stretches
    private const double SilentRms = 0.003;                     // about -50 dBFS: nothing to take out of the Mix that anyone would hear
    private const double MinPeak = 0.08, MinRatio = 1.5;

    /// <summary>
    /// The lag of <paramref name="sourceStream"/> against <paramref name="mixStream"/> (reader stream numbers) in a clip whose edit lists
    /// put the two at <paramref name="sourceOffset"/> and <paramref name="mixOffset"/> (frames on the clip's timeline). Decodes the
    /// whole source (up to twenty minutes) to find where it is loud, then a few seconds of each: call it off the UI thread.
    /// </summary>
    public static LagEstimate Measure(string path, uint mixStream, long mixOffset, uint sourceStream, long sourceOffset)
    {
        var energy = Scan(path, sourceStream, sourceOffset);
        int blocks = energy.Length;
        int wb = Math.Min(WindowBlocks, blocks);
        var picked = blocks == 0 ? new List<int>() : LoudestWindows(energy, wb);
        if (picked.Count == 0) return new LagEstimate(true, 0, 0, 0, "silent");
        if (wb * Block < MinWindowFrames) return new LagEstimate(false, 0, 0, 0, "the track is too short to compare");

        int w = wb * Block;
        int ylen = w + 2 * MaxLag;
        int n = 1;
        while (n < ylen) n <<= 1;
        var xl = new float[w];
        var xr = new float[w];
        var yl = new float[ylen];
        var yr = new float[ylen];
        var num = new double[2 * MaxLag + 1];
        var ey = new double[num.Length];
        double ex = 0;
        var spectra = new double[6][];
        for (int i = 0; i < spectra.Length; i++) spectra[i] = new double[n];
        LagEstimate result = default;
        foreach (int b0 in picked)
        {
            long first = sourceOffset + (long)b0 * Block;
            ReadWindow(path, sourceStream, sourceOffset, first, w, xl, xr);
            ReadWindow(path, mixStream, mixOffset, first - MaxLag, ylen, yl, yr);
            Correlate(xl, xr, w, yl, yr, ylen, spectra, num, ey, ref ex);
            result = Decide(num, ex, ey);
            if (result.Reliable) return result;
        }
        return result;
    }

    /// <summary>The mean square of each 1024-frame block of the source on its own timeline (what the reader hands out, holes filled).</summary>
    private static float[] Scan(string path, uint stream, long offset)
    {
        var energy = new List<float>();
        var block = new float[2 * Block];
        using var reader = new Trimmer.PcmTrackReader(path, stream, offset, offset, long.MaxValue / 4);
        int got;
        while (energy.Count < MaxScanBlocks && (got = reader.ReadBlock(block)) > 0)
        {
            double sum = 0;
            for (int i = 0; i < got * 2; i++) sum += (double)block[i] * block[i];
            energy.Add((float)(sum / (got * 2)));
        }
        return energy.ToArray();
    }

    /// <summary>The first blocks of up to <see cref="MaxWindows"/> windows of <paramref name="wb"/> blocks that do not overlap, loudest first; none that is silent.</summary>
    private static List<int> LoudestWindows(float[] energy, int wb)
    {
        var prefix = new double[energy.Length + 1];
        for (int i = 0; i < energy.Length; i++) prefix[i + 1] = prefix[i] + energy[i];
        int starts = energy.Length - wb + 1;
        var score = new double[starts];
        for (int s = 0; s < starts; s++) score[s] = prefix[s + wb] - prefix[s];
        var taken = new bool[starts];
        var picked = new List<int>();
        while (picked.Count < MaxWindows)
        {
            int best = -1;
            for (int s = 0; s < starts; s++)
                if (!taken[s] && (best < 0 || score[s] > score[best])) best = s;
            if (best < 0 || score[best] / wb < SilentRms * SilentRms) break;
            picked.Add(best);
            for (int s = Math.Max(0, best - wb + 1); s <= Math.Min(starts - 1, best + wb - 1); s++) taken[s] = true;
        }
        return picked;
    }

    /// <summary><paramref name="frames"/> frames of the stream from timeline frame <paramref name="first"/> on, as two channels; zeros before the stream and after its end.</summary>
    private static void ReadWindow(string path, uint stream, long offset, long first, int frames, float[] left, float[] right)
    {
        Array.Clear(left, 0, left.Length);
        Array.Clear(right, 0, right.Length);
        using var reader = new Trimmer.PcmTrackReader(path, stream, offset, first, long.MaxValue / 4);
        var block = new float[2 * Block];
        int at = 0, got;
        while (at < frames && (got = reader.ReadBlock(block)) > 0)
        {
            int count = Math.Min(got, frames - at);
            for (int i = 0; i < count; i++)
            {
                left[at + i] = block[2 * i];
                right[at + i] = block[2 * i + 1];
            }
            at += got;   // a block that came up short was the last
        }
    }

    /// <summary>
    /// Adds this window's cross-correlation Σ x[t]·y[t + MaxLag + L] for L = -MaxLag..MaxLag to <paramref name="num"/>, the energy of the
    /// stretch of y it is laid over to <paramref name="ey"/> and the energy of x to <paramref name="ex"/>.
    /// </summary>
    private static void Correlate(float[] xl, float[] xr, int w, float[] yl, float[] yr, int ylen, double[][] s, double[] num, double[] ey, ref double ex)
    {
        int n = s[0].Length;
        double[] pr = s[0], pi = s[1], ar = s[2], ai = s[3], br = s[4], bi = s[5];
        Array.Clear(pr);
        Array.Clear(pi);
        var prefix = new double[ylen + 1];
        for (int channel = 0; channel < 2; channel++)
        {
            var x = channel == 0 ? xl : xr;
            var y = channel == 0 ? yl : yr;
            Array.Clear(ar);
            Array.Clear(ai);
            Array.Clear(br);
            Array.Clear(bi);
            for (int t = 0; t < w; t++)
            {
                ar[t] = x[t];
                ex += (double)x[t] * x[t];
            }
            for (int u = 0; u < ylen; u++)
            {
                br[u] = y[u];
                prefix[u + 1] += (double)y[u] * y[u];
            }
            Fft.Transform(ar, ai, inverse: false);
            Fft.Transform(br, bi, inverse: false);
            for (int k = 0; k < n; k++)
            {
                pr[k] += ar[k] * br[k] + ai[k] * bi[k];     // conj(X) * Y
                pi[k] += ar[k] * bi[k] - ai[k] * br[k];
            }
        }
        Fft.Transform(pr, pi, inverse: true);
        for (int u = 0; u < ylen; u++) prefix[u + 1] += prefix[u];
        for (int lag = -MaxLag; lag <= MaxLag; lag++)
        {
            int i = lag + MaxLag;
            num[i] += pr[i];
            ey[i] += prefix[i + w] - prefix[i];
        }
    }

    private static LagEstimate Decide(double[] num, double ex, double[] ey)
    {
        int count = num.Length;
        var rho = new double[count];
        int best = 0;
        for (int i = 0; i < count; i++)
        {
            rho[i] = ex > 0 && ey[i] > 0 ? num[i] / Math.Sqrt(ex * ey[i]) : 0;
            if (rho[i] > rho[best]) best = i;
        }
        double peak = rho[best], rival = 0;
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(i - best) <= Guard) continue;
            // A rival is a peak of its own: the slope of the main peak running on past the guard is not.
            if (i > 0 && i < count - 1 && rho[i] >= rho[i - 1] && rho[i] >= rho[i + 1]) rival = Math.Max(rival, rho[i]);
        }
        double ratio = rival > 1e-9 ? Math.Min(peak / rival, 999) : 999;
        long lag = best - MaxLag;
        // Sound that has nothing in common with the track has no peak above the others (the best of thousands of lags is not far
        // above the second best), and sound that repeats itself has several alike; only a lag clearly above every other is one.
        string why = peak < MinPeak ? "the source hardly shows in the mix"
            : best < 3 || best > count - 4 ? $"the lag is beyond the {MaxLag * 1000 / Rate} ms searched"
            : ratio < MinRatio ? "the sound repeats itself, or has nothing in common with the mix, so the lag is not clear"
            : "";
        return why.Length == 0 ? new LagEstimate(true, lag, peak, ratio, "") : new LagEstimate(false, 0, peak, ratio, why);
    }

    /// <summary>An in-place radix-2 complex FFT, double precision (the sums are over some 100,000 samples).</summary>
    private static class Fft
    {
        private static readonly Dictionary<int, (double[] Cos, double[] Sin)> s_tables = new();

        private static (double[] Cos, double[] Sin) Table(int n)
        {
            lock (s_tables)
            {
                if (s_tables.TryGetValue(n, out var t)) return t;
                var cos = new double[n / 2];
                var sin = new double[n / 2];
                for (int k = 0; k < n / 2; k++)
                {
                    cos[k] = Math.Cos(2 * Math.PI * k / n);
                    sin[k] = Math.Sin(2 * Math.PI * k / n);
                }
                return s_tables[n] = (cos, sin);
            }
        }

        /// <param name="re">Real parts; the length is a power of two.</param>
        /// <param name="im">Imaginary parts.</param>
        /// <param name="inverse">The inverse transform, scaled by 1/n.</param>
        public static void Transform(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            var (cos, sin) = Table(n);
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1, step = n / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double wr = cos[k * step], wi = inverse ? sin[k * step] : -sin[k * step];
                        int a = i + k, b = a + half;
                        double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
            if (inverse)
            {
                double scale = 1.0 / n;
                for (int i = 0; i < n; i++)
                {
                    re[i] *= scale;
                    im[i] *= scale;
                }
            }
        }
    }
}
