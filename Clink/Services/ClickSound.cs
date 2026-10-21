using System.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace Clink.Services;

/// <summary>Synthesizes the short "click" played when a clip is saved; no asset files needed.</summary>
internal static unsafe class ClickSound
{
    // Pinned for good: with SND_ASYNC PlaySound keeps reading the image after the call returns, and a GC must not move it then.
    private static byte[]? s_wav;
    private static double s_volume = -1;

    public static void Play(double volume)
    {
        try
        {
            var previous = s_wav;   // a volume change replaces it while it may still be playing: alive until the next PlaySound stopped it
            if (s_wav == null || Math.Abs(s_volume - volume) > 0.001) { s_wav = Build(volume); s_volume = volume; }
            fixed (byte* p = s_wav)
                PInvoke.PlaySound(new PCWSTR((char*)p), HMODULE.Null, SND_FLAGS.SND_MEMORY | SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_NODEFAULT);
            GC.KeepAlive(previous);
        }
        catch (Exception ex) { Log.Warn("Click sound: " + ex.Message); }
    }

    private static byte[] Build(double volume)
    {
        const int rate = 48000;
        int n = rate * 70 / 1000; // 70 ms
        var samples = new short[n];
        var rnd = new Random(7);
        double v = Math.Clamp(volume, 0, 1) * 0.9;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate;
            // transient "tick": 3 ms of filtered noise + two short decaying tones (mechanical click character)
            double noise = t < 0.003 ? (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 900) * 0.6 : 0;
            double tone1 = Math.Sin(2 * Math.PI * 2600 * t) * Math.Exp(-t * 180) * 0.7;
            double tone2 = Math.Sin(2 * Math.PI * 1300 * t) * Math.Exp(-t * 90) * 0.35;
            double s = (noise + tone1 + tone2) * v;
            samples[i] = (short)Math.Clamp(s * 32767, short.MinValue, short.MaxValue);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = n * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        var wav = GC.AllocateUninitializedArray<byte>((int)ms.Length, pinned: true);
        ms.GetBuffer().AsSpan(0, wav.Length).CopyTo(wav);
        return wav;
    }
}
