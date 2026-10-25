using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clink.Services;

namespace Clink.Screenshots;

/// <summary>One saved screenshot. Never a <c>ClipEntry</c>: screenshots have their own folder and their own history.</summary>
internal sealed class ScreenshotEntry
{
    public string Path { get; set; } = "";
    public DateTime Created { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Size { get; set; }
    public string? Game { get; set; }
    public string? CopiumLink { get; set; }
    public string? CopiumPageUrl { get; set; }
    public string? CopiumId { get; set; }
    /// <summary>The path of the screenshot this one was edited from (edit-last saves a new file and leaves the original).</summary>
    public string? EditedFrom { get; set; }
    /// <summary>Keys this version does not know (written by a newer Clink): kept and written back.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// <c>&lt;Settings.Dir&gt;\screenshots.json</c>: the screenshots Clink saved, newest first, at most <see cref="Cap"/>. Written with
/// <see cref="AtomicFile"/>; it sits in the settings folder, so the unit tests' scratch folder and a <c>--dev</c> folder isolate
/// it by themselves. A file that is missing or cannot be read is rebuilt from the PNGs in the screenshots folder (newest by modified
/// time), and entries whose file is gone are dropped when it is loaded. Thread-safe: the commit runs on a worker.
/// </summary>
internal sealed class ScreenshotHistory
{
    public const int Cap = 500;

    /// <summary>How many entries are kept: <see cref="Cap"/>; a test lowers it.</summary>
    internal int Limit { get; set; } = Cap;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private sealed class FileModel
    {
        public List<ScreenshotEntry>? Entries { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _file;
    private readonly string? _rebuildFolder;
    private List<ScreenshotEntry>? _entries;   // newest first; null until loaded
    private Dictionary<string, JsonElement>? _unknown;

    /// <summary>The history file of the current settings folder.</summary>
    public static string DefaultFile => System.IO.Path.Combine(Settings.Dir, "screenshots.json");

    /// <param name="file">The json file (see <see cref="DefaultFile"/>).</param>
    /// <param name="rebuildFolder">Where to look for PNGs when the file is missing or unreadable; null = nowhere.</param>
    public ScreenshotHistory(string file, string? rebuildFolder)
    {
        _file = file;
        _rebuildFolder = rebuildFolder;
    }

    /// <summary>A copy of the entries, newest first (loads the file on first use).</summary>
    public IReadOnlyList<ScreenshotEntry> Entries
    {
        get { lock (_gate) return EnsureLoaded().ToList(); }
    }

    public ScreenshotEntry? Newest
    {
        get { lock (_gate) return EnsureLoaded().FirstOrDefault(); }
    }

    /// <summary>Puts a screenshot at the top and writes the file; the oldest beyond <see cref="Cap"/> leave the list (their PNGs stay on disk).</summary>
    public void Add(ScreenshotEntry entry)
    {
        lock (_gate)
        {
            var list = EnsureLoaded();
            list.RemoveAll(e => PathsEqual(e.Path, entry.Path));
            list.Insert(0, entry);
            if (list.Count > Limit) list.RemoveRange(Limit, list.Count - Limit);
            Save();
        }
    }

    /// <summary>Writes the file after an entry changed (a Copium link was added).</summary>
    public void Update(ScreenshotEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            Save();
        }
    }

    public ScreenshotEntry? Find(string path)
    {
        lock (_gate) return EnsureLoaded().FirstOrDefault(e => PathsEqual(e.Path, path));
    }

    private static bool PathsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private List<ScreenshotEntry> EnsureLoaded()
    {
        if (_entries != null) return _entries;
        var loaded = new List<ScreenshotEntry>();
        bool rebuild = false;
        try
        {
            var model = AtomicFile.ReadWithFallback(_file, text => JsonSerializer.Deserialize<FileModel>(text, s_json) ?? new FileModel());
            if (model == null) rebuild = true;   // no file yet (or a first start after the feature appeared)
            else
            {
                _unknown = model.Unknown;
                loaded = (model.Entries ?? new()).Where(e => e != null && !string.IsNullOrWhiteSpace(e.Path)).ToList();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("screenshots: history could not be read (" + ex.Message + "); rebuilding it from the folder");
            rebuild = true;
        }
        if (rebuild) loaded = ScanFolder();
        // A file the user deleted is no longer a screenshot.
        int before = loaded.Count;
        loaded = loaded.Where(e => { try { return File.Exists(e.Path); } catch { return false; } }).Take(Limit).ToList();
        _entries = loaded;
        if (rebuild && loaded.Count > 0 || before != loaded.Count && before > 0)
        {
            try { Save(); } catch (Exception ex) { Log.Warn("screenshots: history could not be saved: " + ex.Message); }
        }
        return _entries;
    }

    private List<ScreenshotEntry> ScanFolder()
    {
        var list = new List<ScreenshotEntry>();
        try
        {
            if (_rebuildFolder == null || !Directory.Exists(_rebuildFolder)) return list;
            foreach (var f in new DirectoryInfo(_rebuildFolder).EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(Cap))
            {
                var size = PngFile.ReadSize(f.FullName);
                list.Add(new ScreenshotEntry { Path = f.FullName, Created = f.LastWriteTime, Width = size?.Width ?? 0, Height = size?.Height ?? 0, Size = f.Length });
            }
            Log.Info($"screenshots: history rebuilt from {list.Count} file(s) in {_rebuildFolder}");
        }
        catch (Exception ex) { Log.Warn("screenshots: could not scan " + _rebuildFolder + ": " + ex.Message); }
        return list;
    }

    private void Save()
    {
        string? dir = System.IO.Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var model = new FileModel { Entries = _entries, Unknown = _unknown };
        AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(model, s_json));
    }
}
