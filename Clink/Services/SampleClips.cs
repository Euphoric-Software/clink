using System.Globalization;
using System.IO;
using System.Text.Json;
using Clink.Audio;
using Clink.Encode;
using Clink.Engine;

namespace Clink.Services;

/// <summary>One sample clip of the documentation set (<see cref="SampleClips"/>). <see cref="TrimOf"/> is the index of the clip this one is cut from, -1 for a recording.</summary>
internal sealed record SampleSpec(string Game, SampleScene Scene, DateTime Created, double Seconds, int TrimOf, double StartAt, bool Shared, int Seed)
{
    public bool IsTrim => TrimOf >= 0;
}

/// <summary>
/// <c>Clink.exe --dev-make-samples &lt;clips folder&gt; [--index &lt;clips.json&gt;]</c>: writes a set of short, good-looking sample clips (no screen is
/// captured: the frames are drawn, <see cref="SampleFrames"/>, encoded on the CPU, <see cref="SoftwareH264Encoder"/>, and muxed with the
/// recording engine's own <see cref="ClipWriter"/>) and the clip index that lists them, for the documentation screenshots
/// (tools/screenshots). Some of the clips are trims of others (so the nested list shows) and a couple are marked shared. The plan and the
/// index are pure and checked by the unit tests; the drawing and encoding are checked by running it. Exits when done; touches nothing but the
/// two paths it is given, and refuses folders that hold anything of the real Clink's.
/// </summary>
internal static class SampleClips
{
    public const int Width = 1280, Height = 720;
    public const uint Fps = 30;
    private const uint BitrateBps = 6_000_000;
    private const string FakeLinkBase = "https://copium.example/v/";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    // ------------------------------------------------------------------ the plan (pure)

    /// <summary>
    /// The sample set as of <paramref name="now"/>: seven recordings of seven made-up games, 2.5 to 4 s each, newest first within the last
    /// week, and two trims (one of the newest recording, one of the Hyperwarp one), two of the clips marked shared. Nothing is later than <paramref name="now"/>.
    /// </summary>
    public static List<SampleSpec> Plan(DateTime now)
    {
        DateTime At(double hoursAgo, int second) => new DateTime(now.AddHours(-hoursAgo).Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Local).AddSeconds(second);
        return new List<SampleSpec>
        {
            new("NeonDrift", SampleScene.SynthwaveDrive, At(0.7, 12), 4.0, -1, 0, false, 101),     // 0
            new("Cellwars", SampleScene.Cells, At(3.3, 41), 3.5, -1, 0, false, 202),               // 1
            new("Voidspire", SampleScene.Spire, At(26, 5), 3.0, -1, 0, true, 303),                 // 2
            new("Hyperwarp", SampleScene.Warp, At(31, 48), 4.0, -1, 0, false, 404),                // 3
            new("Shardfall", SampleScene.Shards, At(52, 27), 3.0, -1, 0, false, 505),              // 4
            new("Lumenrun", SampleScene.Plasma, At(97, 33), 2.5, -1, 0, false, 606),               // 5
            new("Orbitfall", SampleScene.Orbits, At(149, 9), 3.0, -1, 0, false, 707),              // 6
            new("NeonDrift", SampleScene.SynthwaveDrive, At(0.5, 30), 2.0, 0, 1.0, true, 101),     // 7: trim of 0
            new("Hyperwarp", SampleScene.Warp, At(30.5, 14), 2.0, 3, 1.5, false, 404),             // 8: trim of 3
        };
    }

    /// <summary>The file name of each planned clip: "Game yyyy-MM-dd HH-mm-ss.mp4" like the engine's, and "&lt;source&gt; (trim).mp4" / "(trim 2)" like the trim editor's.</summary>
    public static List<string> FileNames(IReadOnlyList<SampleSpec> plan)
    {
        var names = new List<string>();
        var trimsOf = new Dictionary<int, int>();
        for (int i = 0; i < plan.Count; i++)
        {
            var s = plan[i];
            if (!s.IsTrim) { names.Add($"{s.Game} {s.Created.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}.mp4"); continue; }
            if (s.TrimOf >= i) throw new ArgumentException($"clip {i} is cut from clip {s.TrimOf}, which comes later");
            trimsOf[s.TrimOf] = trimsOf.GetValueOrDefault(s.TrimOf) + 1;
            string stem = Path.GetFileNameWithoutExtension(names[s.TrimOf]);
            int n = trimsOf[s.TrimOf];
            names.Add(n == 1 ? $"{stem} (trim).mp4" : $"{stem} (trim {n}).mp4");
        }
        return names;
    }

    /// <summary>
    /// The clip index for the plan: one entry per clip with its real <paramref name="results"/> (duration, size), a trim's
    /// <see cref="ClipEntry.SourcePath"/> set to the clip it was cut from, and a made-up link on the shared ones.
    /// </summary>
    public static List<ClipEntry> BuildIndex(IReadOnlyList<SampleSpec> plan, string clipsFolder, IReadOnlyList<(double Seconds, long Bytes)> results)
    {
        if (results.Count != plan.Count) throw new ArgumentException("one result per planned clip");
        var names = FileNames(plan);
        var entries = new List<ClipEntry>();
        for (int i = 0; i < plan.Count; i++)
        {
            var s = plan[i];
            string id = "c" + (1000 + i * 37).ToString(CultureInfo.InvariantCulture);
            entries.Add(new ClipEntry
            {
                Path = Path.Combine(clipsFolder, names[i]), Created = s.Created, Duration = results[i].Seconds, Size = results[i].Bytes, Game = s.Game,
                AudioTracks = Array.Empty<string>(), IsTrim = s.IsTrim,
                SourcePath = s.IsTrim ? Path.Combine(clipsFolder, names[s.TrimOf]) : null,
                CopiumLink = s.Shared ? FakeLinkBase + id : null, CopiumPageUrl = s.Shared ? FakeLinkBase + id : null, CopiumId = s.Shared ? id : null,
            });
        }
        return entries.OrderByDescending(e => e.Created).ToList();
    }

    // ------------------------------------------------------------------ the command line (pure)

    /// <summary>
    /// <c>--dev-make-samples &lt;clips folder&gt; [--index &lt;file&gt;]</c> (the first argument is the switch). The index defaults to
    /// <c>clips.json</c> beside the clips folder, which is where a <c>--dev &lt;folder&gt;</c> instance whose clips are in <c>&lt;folder&gt;\clips</c> reads it.
    /// Throws <see cref="ArgumentException"/> for anything else.
    /// </summary>
    public static (string Folder, string Index) ParseArgs(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("--dev-make-samples needs the clips folder to write: Clink.exe --dev-make-samples <clips folder> [--index <clips.json>]");
        string folder = Path.GetFullPath(args[1].Trim().Trim('"'));
        string? index = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i].Equals("--index", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && index == null) index = Path.GetFullPath(args[++i].Trim().Trim('"'));
            else throw new ArgumentException($"--dev-make-samples: unexpected argument '{args[i]}'");
        }
        index ??= Path.Combine(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder, "clips.json");
        return (folder, index);
    }

    /// <summary>Written into a clips folder by a samples run: only such a folder (or an empty / new one) is written into again.</summary>
    public const string MarkerName = ".clink-samples";

    /// <summary>
    /// Why the folder (or the index) must not be written, or null. Judged as given and, when a junction or symbolic link sends it
    /// elsewhere, where it really is (<see cref="AppDataRedirect.ResolvedPath"/>). Refused: anything the real Clink keeps
    /// (<see cref="DevOptions.RealPlaces"/>) or that holds / lies inside it, a drive's root, and the user profile folder or a folder holding it.
    /// </summary>
    public static string? Refusal(string folder, string index, IEnumerable<(string Path, string What)> realPlaces)
    {
        var places = realPlaces.ToList();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string f in Candidates(folder))
        {
            string shown = SamePath(f, folder) ? folder : $"{folder} (really {f})";
            string? root = Path.GetPathRoot(Path.GetFullPath(f));
            if (root != null && SamePath(f, root)) return $"{shown} is a drive's root";
            if (profile.Length > 0 && Within(profile, f)) return $"{shown} is or holds the user profile folder";
            foreach (var (place, what) in places)
                if (Within(f, place) || Within(place, f)) return $"{shown} is, holds or lies inside {what} ({place})";
        }
        foreach (string i in Candidates(index))
            foreach (var (place, what) in places)
                if (Within(i, place)) return $"{index} lies inside {what} ({place})";
        return null;
    }

    private static List<string> Candidates(string path)
    {
        var list = new List<string> { Path.GetFullPath(path) };
        string? resolved = AppDataRedirect.ResolvedPath(path);
        if (resolved != null && !SamePath(resolved, list[0])) list.Add(resolved);
        return list;
    }

    /// <summary>
    /// Why the target cannot be written (it exists and is not ours), or null: a clips folder that holds anything must carry the
    /// <see cref="MarkerName"/> file an earlier samples run left, and an existing index must be one of that run's (it lists only clips of this folder).
    /// </summary>
    public static string? TargetProblem(string folder, string index)
    {
        if (File.Exists(folder)) return $"{folder} is a file";
        if (Directory.Exists(index)) return $"{index} is a folder";
        bool marked = File.Exists(Path.Combine(folder, MarkerName));
        if (Directory.Exists(folder) && !marked && Directory.EnumerateFileSystemEntries(folder).Any())
            return $"{folder} is not empty and was not made by --dev-make-samples (it has no {MarkerName} file): pick an empty or new folder";
        if (File.Exists(index) && (File.GetAttributes(index) & FileAttributes.ReparsePoint) != 0)
            return $"{index} is a link (junction or symbolic link): pick a plain file for --index";
        if (File.Exists(index) && !(marked && IndexIsOurs(index, folder)))
            return $"{index} exists and was not written by --dev-make-samples for {folder}: pick another --index";
        return null;
    }

    /// <summary>The file is a clip index that lists clips of <paramref name="folder"/> and nothing else (what a samples run writes).</summary>
    public static bool IndexIsOurs(string indexPath, string folder)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(indexPath));
            return entries != null && entries.Count > 0 && entries.All(e => !string.IsNullOrEmpty(e.Path) && Within(e.Path, folder));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string child, string parent)
    {
        string c = Path.GetFullPath(child).TrimEnd('\\', '/'), p = Path.GetFullPath(parent).TrimEnd('\\', '/');
        return c.Equals(p, StringComparison.OrdinalIgnoreCase) || c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the work

    /// <summary>
    /// <c>Clink.exe --dev-make-samples ...</c> from <c>App.OnStartup</c>: false when <paramref name="args"/> is not that command. Otherwise runs
    /// it (log in the temp folder, not the real settings folder's; console of the parent attached; MTA thread for Media Foundation) and gives
    /// its exit code. No window, no capture, no single-instance mutex.
    /// </summary>
    public static bool TryRunCli(string[] args, Action attachConsole, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !args[0].Equals("--dev-make-samples", StringComparison.OrdinalIgnoreCase)) return false;
        Log.Init(Path.Combine(Path.GetTempPath(), "clink-samples"), Log.CliFile);
        attachConsole();
        int code = 0;
        var t = new Thread(() => { code = Run(args, Console.WriteLine); });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        exitCode = code;
        return true;
    }

    /// <summary>The command: 0 done, 2 bad arguments, an unusable path or a refused folder, 5 failed while writing. Run it on an MTA thread.</summary>
    public static int Run(string[] args, Action<string> say)
    {
        string folder, index;
        try
        {
            try { (folder, index) = ParseArgs(args); }
            catch (ArgumentException ex) { say("Clink: " + ex.Message); return 2; }
            if (Refusal(folder, index, DevOptions.RealPlaces()) is { } why) { say("Clink: --dev-make-samples refuses: " + why); return 2; }
            if (TargetProblem(folder, index) is { } problem) { say("Clink: --dev-make-samples refuses: " + problem); return 2; }
        }
        catch (Exception ex) when (ex is NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            say("Clink: --dev-make-samples cannot use that path: " + ex.Message);
            return 2;
        }
        catch (Exception ex)   // nothing may escape the worker thread of TryRunCli
        {
            Log.Error("[samples] checking the paths failed", ex);
            say("Clink: --dev-make-samples cannot check those paths: " + ex.Message);
            return 2;
        }
        try
        {
            Generate(folder, index, DateTime.Now, say);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("[samples] failed", ex);
            say("Clink: --dev-make-samples failed: " + ex.Message);
            return 5;
        }
    }
    /// <summary>Draws, encodes and writes every planned clip into <paramref name="folder"/>, then the index. Existing files of the same names are replaced.</summary>
    public static void Generate(string folder, string indexPath, DateTime now, Action<string> say)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MarkerName), "made by Clink.exe --dev-make-samples; a later run may write into this folder again");
        var plan = Plan(now);
        var names = FileNames(plan);
        var results = new List<(double Seconds, long Bytes)>();
        var canvas = new SampleCanvas(Width, Height);
        var nv12 = new byte[Width * Height * 3 / 2];
        for (int i = 0; i < plan.Count; i++)
        {
            var s = plan[i];
            string path = Path.Combine(folder, names[i]);
            int frames = (int)Math.Round(s.Seconds * Fps);
            const long origin = 1_000_000_000L;
            using var enc = new SoftwareH264Encoder(Width, Height, Fps, BitrateBps, gopSeconds: 1.0);
            for (int f = 0; f < frames; f++)
            {
                SampleFrames.Draw(canvas, s.Scene, s.StartAt + f / (double)Fps, s.Seed);
                canvas.ToNv12(nv12);
                enc.Encode(nv12, origin + (long)Math.Round(f * 10_000_000.0 / Fps));
            }
            var video = enc.Finish();
            if (video.Count == 0 || !video[0].Keyframe) throw new InvalidOperationException($"the encoder gave {video.Count} samples for {names[i]} (the first {(video.Count > 0 && video[0].Keyframe ? "is" : "is not")} a keyframe)");
            ClipWriter.Write(path, video, "h264", Width, Height, enc.SequenceHeader, new List<(AudioTrackDesc desc, List<AacFrame> frames)>(), origin, Fps);
            File.SetCreationTime(path, s.Created);
            File.SetLastWriteTime(path, s.Created);
            long size = new FileInfo(path).Length;
            results.Add((frames / (double)Fps, size));
            say($"{names[i]}  {frames / (double)Fps:0.0} s  {size / 1024.0 / 1024.0:0.0} MB");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath) ?? folder);
        File.WriteAllText(indexPath, JsonSerializer.Serialize(BuildIndex(plan, folder, results), s_json));
        say($"index: {indexPath}");
    }
}
