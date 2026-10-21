using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clink.Services;

internal sealed class ClipEntry
{
    public string Path { get; set; } = "";
    public DateTime Created { get; set; }
    public double Duration { get; set; }
    public long Size { get; set; }
    public string? Game { get; set; }
    public string[]? AudioTracks { get; set; }
    public string? CopiumLink { get; set; }
    public string? CopiumPageUrl { get; set; }
    public string? CopiumId { get; set; }
    /// <summary>Made by the trim editor (from <see cref="SourcePath"/>).</summary>
    public bool IsTrim { get; set; }
    public string? SourcePath { get; set; }
    /// <summary>Set when the archive service moved the file to the archive location (<see cref="Path"/> then points there).</summary>
    public DateTime? ArchivedAt { get; set; }

    [JsonIgnore] public bool IsShared => !string.IsNullOrEmpty(CopiumLink);
    [JsonIgnore] public bool IsArchived => ArchivedAt != null;

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public bool Exists => File.Exists(Path);
}

/// <summary>Index of clips (sidecar JSON next to the settings) merged with whatever is in the clips folder.</summary>
internal sealed class ClipLibrary
{
    private readonly object _gate = new();
    private readonly List<ClipEntry> _entries = new();
    private static string IndexPath => IndexPathOverride ?? System.IO.Path.Combine(Settings.Dir, "clips.json");
    /// <summary>Where the index lives; set by test harnesses so they never touch the real clips.json.</summary>
    internal static string? IndexPathOverride { get; set; }
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public event Action? Changed;

    public void Load(string clipsFolder)
    {
        lock (_gate)
        {
            _entries.Clear();
            try
            {
                // The index is the only record of archived clips and of shared ones whose local file is gone: a damaged
                // clips.json (crash mid-write) falls back to the previous version kept beside it.
                var loaded = AtomicFile.ReadWithFallback(IndexPath, json => JsonSerializer.Deserialize<List<ClipEntry>>(json, s_json));
                if (loaded != null) _entries.AddRange(loaded.Where(e => e != null));
            }
            catch (Exception ex) { Log.Warn("clips.json: " + ex.Message); }
            // A deleted local file drops its entry, except: shared clips stay (the Cloud tab lists them by link), and archived
            // clips are not checked here at all, because File.Exists on an unreachable share can block for seconds at startup;
            // the library rows say "archive offline" instead.
            _entries.RemoveAll(e => string.IsNullOrEmpty(e.Path) || (!e.IsShared && !e.IsArchived && !File.Exists(e.Path)));
            // Pick up clips that were made elsewhere / before the index existed.
            try
            {
                if (Directory.Exists(clipsFolder))
                    foreach (var f in Directory.EnumerateFiles(clipsFolder, "*.mp4"))
                        if (!_entries.Any(e => string.Equals(e.Path, f, StringComparison.OrdinalIgnoreCase)))
                        {
                            var fi = new FileInfo(f);
                            _entries.Add(new ClipEntry { Path = f, Created = fi.CreationTime, Size = fi.Length });
                        }
            }
            catch (Exception ex) { Log.Warn("scan clips: " + ex.Message); }
            _entries.Sort((a, b) => b.Created.CompareTo(a.Created));
        }
        Changed?.Invoke();
    }

    /// <summary>Snapshot, newest first.</summary>
    public IReadOnlyList<ClipEntry> Entries { get { lock (_gate) return _entries.ToList(); } }

    /// <summary>The entry for a file path (case-insensitive), or null.</summary>
    public ClipEntry? Find(string path)
    {
        lock (_gate) return _entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="isTrim">Made by the trim editor; <paramref name="sourcePath"/> is the clip it was cut from.</param>
    public ClipEntry Add(string path, double duration, long size, string? game, string[] audioTracks, bool isTrim = false, string? sourcePath = null)
    {
        var e = new ClipEntry
        {
            Path = path, Created = DateTime.Now, Duration = duration, Size = size, Game = game, AudioTracks = audioTracks,
            IsTrim = isTrim, SourcePath = isTrim ? sourcePath : null,
        };
        lock (_gate)
        {
            _entries.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, e);
            SaveLocked();
        }
        Changed?.Invoke();
        return e;
    }

    public void Update(ClipEntry e)
    {
        lock (_gate) SaveLocked();
        Changed?.Invoke();
    }

    public void Remove(ClipEntry e)
    {
        lock (_gate) { _entries.Remove(e); SaveLocked(); }
        Changed?.Invoke();
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(IndexPath) ?? Settings.Dir);
            AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(_entries, s_json));
        }
        catch (Exception ex) { Log.Warn("clips.json save: " + ex.Message); }
    }
}
