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
    /// <summary>The file's last-write time (UTC) right after it was saved, in memory only: an upload checks the file still has it (and <see cref="Size"/>) before sending it.</summary>
    [JsonIgnore] public DateTime? WrittenUtc { get; set; }
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
        /// <summary>Every folder screenshots were saved to, so an old screenshot stays openable after the folder setting changed.</summary>
        public List<string>? Roots { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _file;
    private readonly string? _rebuildFolder;
    private List<ScreenshotEntry>? _entries;   // newest first; null until loaded
    private Dictionary<string, JsonElement>? _unknown;
    private List<string> _roots = new();

    /// <summary>At most this many save folders are remembered.</summary>
    public const int MaxRoots = 16;

    /// <summary>
    /// Every folder a screenshot was saved to (the oldest first), as recorded by <see cref="NoteRoot"/> / <see cref="Add"/>: the folder
    /// of an entry in the file is not a root. The file is hand-editable, so a root in it is not proof of anything: a forged local root
    /// only exposes the regular <c>.png</c> files under it to the editor, and network (UNC) roots are filtered out by
    /// <c>ScreenshotService.KnownFolders</c> unless the current folder is one. The app may open a PNG inside any root, not only in the
    /// folder that is set now.
    /// </summary>
    public IReadOnlyList<string> Roots
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return new List<string>(_roots);
            }
        }
    }

    /// <summary>Records <paramref name="folder"/> as a screenshots folder (written with the file when it is new).</summary>
    public void NoteRoot(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        lock (_gate)
        {
            EnsureLoaded();
            if (_roots.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
            _roots.Add(folder);
            if (_roots.Count > MaxRoots) _roots.RemoveRange(0, _roots.Count - MaxRoots);
            try { Save(); } catch (Exception ex) { Log.Warn("screenshots: history could not be saved: " + ex.Message); }
        }
    }

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
        try
        {
            AddCore(entry);
        }
        finally { RaiseChanged(); }
    }

    private void AddCore(ScreenshotEntry entry)
    {
        lock (_gate)
        {
            var list = EnsureLoaded();
            list.RemoveAll(e => PathsEqual(e.Path, entry.Path));
            list.Insert(0, entry);
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(entry.Path);
                if (!string.IsNullOrEmpty(dir) && !_roots.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    _roots.Add(dir);
                    if (_roots.Count > MaxRoots) _roots.RemoveRange(0, _roots.Count - MaxRoots);
                }
            }
            catch (ArgumentException) { }
            if (list.Count > Limit) list.RemoveRange(Limit, list.Count - Limit);
            Save();
        }
    }

    /// <summary>
    /// Records a Copium link: finds the entry LISTED here by its path (the caller's instance may be another object for the same file,
    /// for example when the list was loaded again after the folder setting changed), copies the link fields onto it and writes the
    /// file. False (and a warning unless <paramref name="warn"/> is off) when no entry has that path: nothing is written.
    /// </summary>
    public bool Update(ScreenshotEntry entry, bool warn = true)
    {
        bool ok = false;
        try { return ok = UpdateCore(entry, warn); }
        finally { if (ok) RaiseChanged(); }
    }

    private bool UpdateCore(ScreenshotEntry entry, bool warn)
    {
        lock (_gate)
        {
            var listed = EnsureLoaded().FirstOrDefault(e => PathsEqual(e.Path, entry.Path));
            if (listed == null)
            {
                if (warn) Log.Warn("screenshots: the link was not recorded in the history: " + System.IO.Path.GetFileName(entry.Path) + " is not listed");
                return false;
            }
            if (!ReferenceEquals(listed, entry))
            {
                listed.CopiumLink = entry.CopiumLink;
                listed.CopiumPageUrl = entry.CopiumPageUrl;
                listed.CopiumId = entry.CopiumId;
            }
            Save();
            return true;
        }
    }

    /// <summary>
    /// Takes the entry for <paramref name="path"/> out of the list and writes the file (the PNG itself is the caller's to deal with).
    /// False when no entry has that path: nothing is written and <see cref="Changed"/> is not raised.
    /// </summary>
    public bool Remove(string path)
    {
        bool removed = false;
        try
        {
            lock (_gate)
            {
                var list = EnsureLoaded();
                removed = list.RemoveAll(e => PathsEqual(e.Path, path)) > 0;
                if (removed) Save();
            }
            return removed;
        }
        finally { if (removed) RaiseChanged(); }
    }

    /// <summary>
    /// Raised (on whatever thread made the change, never while the history's lock is held) after a screenshot was added, got its Copium
    /// link, or was removed: the library list follows it. Handlers must marshal to the UI thread themselves.
    /// </summary>
    public event Action? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn("screenshots: history change handler: " + ex.Message); }
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
                _roots = (model.Roots ?? new()).Where(r => !string.IsNullOrWhiteSpace(r)).Take(MaxRoots).ToList();
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
        // (A network path is not probed while the folder in use is a local one: a hand-edited file cannot make the app reach out to a
        // server; such an entry is kept as it is and can never be opened, see ScreenshotService.KnownFolders.)
        // (An entry with a Copium link stays when its file is gone: the link is still good, and the library's Shared list shows it.)
        loaded = loaded.Where(e => !string.IsNullOrEmpty(e.CopiumLink) || (IsUnc(e.Path) && !IsUnc(_rebuildFolder)) || SafeExists(e.Path)).Take(Limit).ToList();
        _entries = loaded;
        if (rebuild && loaded.Count > 0 || before != loaded.Count && before > 0)
        {
            try { Save(); } catch (Exception ex) { Log.Warn("screenshots: history could not be saved: " + ex.Message); }
        }
        return _entries;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static bool IsUnc(string? path) => ScreenshotService.IsUnc(path);   // network and device paths alike

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
        var model = new FileModel { Entries = _entries, Roots = _roots.Count > 0 ? _roots : null, Unknown = _unknown };
        AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(model, s_json));
    }
}
