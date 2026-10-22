using System.Globalization;

namespace Clink.Engine;

/// <summary>
/// The headless trim: <c>Clink.exe --trim &lt;in&gt; &lt;out&gt; &lt;startSec&gt; &lt;endSec&gt; [--gain Name=volume]... [--drop Name]...</c>.
/// Without options it is the plain cut. <c>--gain</c> sets a track's volume the way the editor's slider does (a factor, or a percentage
/// with a % sign: <c>Game=0</c>, <c>Game=0.5</c>, <c>Game=50%</c>) and <c>--drop</c> leaves a track out of the file. The plan is the
/// editor's (<see cref="MixPlan"/>), so Game, Mic and Voice shape "All audio" as well, and the work is the same. Track names are the
/// file's own (Mix, Game, Mic, Voice, Desktop), in any case.
/// </summary>
internal static class TrimCli
{
    public static void Run(string[] args)
    {
        string src = args[1], dst = args[2];
        double start = double.Parse(args[3]), end = double.Parse(args[4]);
        var gains = new List<(string name, float gain)>();
        var drops = new List<string>();
        for (int i = 5; i < args.Length; i++)
        {
            if (args[i].Equals("--gain", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) gains.Add(ParseGain(args[++i]));
            else if (args[i].Equals("--drop", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) drops.Add(args[++i]);
            else throw new ArgumentException($"Unknown option '{args[i]}' (expected --gain Name=volume or --drop Name)");
        }
        if (gains.Count == 0 && drops.Count == 0)
        {
            Trimmer.Trim(src, dst, start, end, null);
            return;
        }

        var tracks = Trimmer.ReadAudioTracks(src) ?? throw new InvalidOperationException("--gain and --drop need a clip written by Clink (its audio tracks have names)");
        string[] names = tracks.Select(t => t.Name).ToArray();
        var gain = Enumerable.Repeat(1f, names.Length).ToArray();
        var include = Enumerable.Repeat(true, names.Length).ToArray();
        foreach (var (name, volume) in gains) gain[IndexOf(names, name)] = volume;
        foreach (string name in drops) include[IndexOf(names, name)] = false;
        var roles = MixPlan.Roles(names, names.Length);
        // The editor's plan: a source that cannot be lined up with the Mix (a clip an older Clink trimmed) is not part of it.
        var align = ClipAlignment.Read(src);
        if (align.Unlined.Length > 0)
        {
            Console.WriteLine($"  {(align.Untrusted ? ClipAlignment.UntrustedNote : ClipAlignment.OlderClinkNote)} ({string.Join(", ", align.Unlined.Select(i => names[i]))}: their volumes only change their own tracks)");
            roles = MixPlan.WithoutTracks(roles, align.Unlined);
        }
        var mix = MixPlan.Export(roles, include, gain);
        for (int i = 0; i < mix.Length; i++) Console.WriteLine("  " + Describe(i, names, mix[i]));
        Trimmer.Trim(src, dst, start, end, names, mix);
    }

    /// <summary>"Game=0.5" or "Game=50%" as a track name and a linear volume (0 and up).</summary>
    internal static (string name, float gain) ParseGain(string text)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0 || eq == text.Length - 1) throw new ArgumentException($"--gain expects Name=volume, not '{text}'");
        string value = text[(eq + 1)..].Trim();
        bool percent = value.EndsWith('%');
        if (percent) value = value[..^1];
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || v < 0 || float.IsInfinity(v))
            throw new ArgumentException($"--gain {text}: the volume is a number from 0 up (0.5 or 50%)");
        return (text[..eq].Trim(), percent ? v / 100f : v);
    }

    private static int IndexOf(string[] names, string name)
    {
        int i = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new ArgumentException($"The clip has no track '{name}' (it has {string.Join(", ", names)})");
        return i;
    }

    private static string Describe(int index, string[] names, TrackMix m)
    {
        string head = $"track {index + 1} {names[index]}:";
        if (!m.Include) return head + " dropped";
        if (m.Sum == null) return head + (Math.Abs(m.Gain - 1f) < 0.001f ? " copied" : $" volume x{m.Gain:0.00}");
        string parts = m.Sum.Length == 0 ? "silence" : string.Join(" + ", m.Sum.Select(s => $"{names[s.Track]} x{s.Gain:0.00}"));
        return head + $" rendered from {parts} (master x{m.Gain:0.00})";
    }
}
