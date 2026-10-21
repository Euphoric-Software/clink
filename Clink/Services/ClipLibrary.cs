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

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public bool Exists => File.Exists(Path);
}

/// <summary>Index of clips (sidecar JSON next to the settings) merged with whatever is in the clips folder.</summary>
internal sealed class ClipLibrary
{
    private readonly object _gate = new();
    private readonly List<ClipEntry> _entries = new();
    private static string IndexPath => System.IO.Path.Combine(Settings.Dir, "clips.json");
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public event Action? Changed;

    public void Load(string clipsFolder)
    {
        lock (_gate)
        {
            _entries.Clear();
            try
            {
                if (File.Exists(IndexPath))
                    _entries.AddRange(JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(IndexPath), s_json) ?? new());
            }
            catch (Exception ex) { Log.Warn("clips.json: " + ex.Message); }
            _entries.RemoveAll(e => !File.Exists(e.Path));
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

    public IReadOnlyList<ClipEntry> Entries { get { lock (_gate) return _entries.ToList(); } }

    public ClipEntry Add(string path, double duration, long size, string? game, string[] audioTracks)
    {
        var e = new ClipEntry { Path = path, Created = DateTime.Now, Duration = duration, Size = size, Game = game, AudioTracks = audioTracks };
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
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(_entries, s_json));
        }
        catch (Exception ex) { Log.Warn("clips.json save: " + ex.Message); }
    }
}
