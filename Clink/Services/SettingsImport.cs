using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Clink.Services;

/// <summary>
/// The settings another Clink saved in the private copy of AppData that Windows keeps for a packaged app (see
/// <see cref="AppDataRedirect"/>), found by <see cref="SettingsImport.FindForeignCopies"/>.
/// </summary>
/// <param name="Package">The package family name: "Claude_pzs8sxrjxfjjc".</param>
/// <param name="Folder">That copy's Clink folder: "...\Packages\&lt;Package&gt;\LocalCache\Roaming\Clink".</param>
/// <param name="SavedUtc">When its settings.json was last written.</param>
/// <param name="Differences">What taking it over would change, in the user's words ("video quality", "hotkeys"); empty until compared.</param>
internal sealed record ForeignSettings(string Package, string Folder, DateTime SavedUtc, IReadOnlyList<string> Differences)
{
    /// <summary>"Claude".</summary>
    public string FriendlyName => AppDataRedirect.FriendlyNameOf(Package);
    public string SettingsPath => Path.Combine(Folder, "settings.json");
    public string ClipsPath => Path.Combine(Folder, "clips.json");
    /// <summary>"&lt;package&gt;|&lt;ticks of SavedUtc&gt;": what <see cref="Settings.ImportDeclined"/> holds once the user turned this copy down.</summary>
    public string DeclineKey => $"{Package}|{SavedUtc.Ticks}";
    /// <summary>"2026-10-21 14:32", local time.</summary>
    public string SavedLocalText => SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// Windows keeps a private copy of %APPDATA% and %LOCALAPPDATA% for an app that runs from a package (MSIX): every write the app
/// or a process it starts makes there lands in <c>%LOCALAPPDATA%\Packages\&lt;package&gt;\LocalCache\{Roaming,Local}</c>, and every read
/// sees that copy first. A Clink started from such an app (the Claude desktop app's agents did) therefore keeps its settings
/// where a Clink started from Explorer or the Start menu never looks. <see cref="Detect"/> tells which one this process is.
/// </summary>
internal static class AppDataRedirect
{
    // \\?\C:\Users\x\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Clink\settings.json -> Claude_pzs8sxrjxfjjc
    private static readonly Regex s_package = new(@"\\AppData\\Local\\Packages\\(?<pkg>[^\\]+)\\LocalCache\\(?:Roaming|Local)\\",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The package whose private AppData this process sees ("Claude_pzs8sxrjxfjjc"), or null when it sees the real one.</summary>
    public static string? Package { get; private set; }
    /// <summary>"Claude": the package name without the publisher part; null when not redirected.</summary>
    public static string? FriendlyName { get; private set; }
    /// <summary>Where the settings folder really is (the redirected path) when <see cref="Package"/> is set.</summary>
    public static string? FinalFolder { get; private set; }

    /// <summary>The line shown once per start when redirected; null otherwise.</summary>
    public static string? Notice => Package == null ? null : NoticeFor(FriendlyName ?? Package);

    public static string NoticeFor(string friendlyName) =>
        $"Clink was started from inside {friendlyName}: Windows keeps a private copy of its settings for that app, so changes made now are not seen by Clink started from Explorer or the Start menu";

    /// <summary>
    /// Looks at where <see cref="Settings.Dir"/> really is and sets <see cref="Package"/>, <see cref="FriendlyName"/> and
    /// <see cref="FinalFolder"/>; logs the final path when it is a package's. True when redirected. Never throws.
    /// </summary>
    public static bool Detect()
    {
        Package = FriendlyName = FinalFolder = null;
        try
        {
            string dir = Settings.Dir;
            Directory.CreateDirectory(dir);
            string? folder = FinalFolderOf(dir);
            string? package = folder == null ? null : PackageFromFinalPath(folder + "\\");
            if (package == null) return false;
            Package = package;
            FriendlyName = FriendlyNameOf(package);
            FinalFolder = folder;
            Log.Warn($"settings: {dir} is really {folder}: Windows keeps a private copy of AppData for {package}, and Clink started from Explorer or the Start menu does not see it");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("settings: could not tell whether AppData is redirected: " + ex.Message);
            return false;
        }
    }

    /// <summary>Back to "not found yet". For the unit tests.</summary>
    internal static void Reset() => Package = FriendlyName = FinalFolder = null;

    /// <summary>The package a final path (<c>GetFinalPathNameByHandle</c> form) belongs to, or null when it is an ordinary path.</summary>
    public static string? PackageFromFinalPath(string finalPath)
    {
        var m = s_package.Match(finalPath ?? "");
        return m.Success ? m.Groups["pkg"].Value : null;
    }

    /// <summary>"Claude_pzs8sxrjxfjjc" -> "Claude"; "Microsoft.WindowsTerminal_8wekyb3d8bbwe" -> "WindowsTerminal".</summary>
    public static string FriendlyNameOf(string package)
    {
        int us = package.IndexOf('_');
        string name = us > 0 ? package[..us] : package;
        foreach (string part in name.Split('.').Reverse())
            if (part.Any(char.IsLetter)) return part;
        return name.Length > 0 ? name : package;
    }

    /// <summary>
    /// Where Windows really keeps the files this process creates in <paramref name="dir"/>: the folder part of the final path of a
    /// file created there. A file that the process itself creates always goes to the private copy, while the folder itself, once
    /// it exists in the real AppData too, is opened as the real one (so opening the folder, as one would expect to, tells nothing).
    /// The probe file is deleted when its handle closes. When nothing can be created, an existing settings.json and then the
    /// folder are opened instead. Null when none of that works.
    /// </summary>
    internal static string? FinalFolderOf(string dir)
    {
        string probe = Path.Combine(dir, ".clink-probe-" + Guid.NewGuid().ToString("N"));
        nint h = CreateFile(probe, GENERIC_WRITE | DELETE, FILE_SHARE_ALL, 0, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_DELETE_ON_CLOSE, 0);
        if (h != InvalidHandle)
        {
            try { return ParentOf(FinalPathOf(h)); }
            finally { CloseHandle(h); }
        }
        string settings = Path.Combine(dir, "settings.json");
        if (File.Exists(settings))
        {
            string? viaFile = ParentOf(FinalPathOfExisting(settings));
            if (viaFile != null) return viaFile;
        }
        return FinalPathOfExisting(dir);
    }

    private static string? ParentOf(string? path)
    {
        if (path == null) return null;
        int cut = path.LastIndexOf('\\');
        return cut > 0 ? path[..cut] : path;
    }

    private static string? FinalPathOfExisting(string path)
    {
        // Access 0 is enough to ask where a handle points; BACKUP_SEMANTICS lets a folder be opened.
        nint h = CreateFile(path, 0, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h == InvalidHandle) return null;
        try { return FinalPathOf(h); }
        finally { CloseHandle(h); }
    }

    private static string? FinalPathOf(nint handle)
    {
        var sb = new StringBuilder(512);
        uint n = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);   // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
        if (n > sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
        }
        return n == 0 ? null : sb.ToString();
    }

    private const uint GENERIC_WRITE = 0x40000000, DELETE = 0x00010000, FILE_SHARE_ALL = 7;
    private const uint CREATE_NEW = 1, OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
    private const nint InvalidHandle = -1;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(nint file, [Out] StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>
/// Settings that another copy of Clink saved in a packaged app's private AppData (<see cref="AppDataRedirect"/>): finding them,
/// saying what they would change, taking them over, and remembering that the user turned one down.
/// A fresh settings store takes the newest copy over by itself (<see cref="ImportNewest"/>, from App.RunNormal); an existing one
/// is never changed without the user's say: <see cref="Scan"/> looks, sets <see cref="Pending"/> and raises
/// <see cref="PendingChanged"/>, <c>AppController.ImportForeignSettings</c> imports, <see cref="Decline"/> stops asking about that copy.
/// </summary>
internal static class SettingsImport
{
    private const long MaxFileBytes = 4_000_000;   // a settings.json is a few kB and a clip index a few hundred kB: anything bigger is not one

    /// <summary>
    /// The folder that holds the package folders, instead of <c>%LOCALAPPDATA%\Packages</c>: the hidden <c>--packages-root</c> test aid
    /// of <c>--dev</c> and the unit tests. Null in a real run. Without it a <c>--dev</c> instance looks nowhere, so a test never takes
    /// over (and then archives, with) a real user's settings.
    /// </summary>
    internal static string? PackagesRoot { get; set; }

    /// <summary>
    /// What a <c>--dev</c> instance does to every settings file it loads (<see cref="DevOptions.Isolate"/>), so that <see cref="Scan"/>
    /// compares the other copy as it would be once it has been taken over and loaded: without it the clips folder, the archive and
    /// the rest that the guards change would differ for ever, and the same copy would be offered again after every import. Null in a
    /// real run.
    /// </summary>
    internal static Action<Settings>? ForeignGuard { get; set; }

    private static ForeignSettings? s_pending;

    /// <summary>The newest other copy whose settings differ from these and that the user has not turned down; null when there is none (yet).</summary>
    public static ForeignSettings? Pending => Volatile.Read(ref s_pending);

    /// <summary>
    /// <see cref="Pending"/> changed. Raised on the UI thread when the app is running (the caller's thread otherwise), whichever
    /// thread <see cref="Scan"/> worked on.
    /// </summary>
    public static event Action? PendingChanged;

    // ------------------------------------------------------------------ finding copies

    /// <summary>
    /// The settings other Clink copies saved in packaged apps' private AppData, newest first:
    /// <c>&lt;root&gt;\Packages\*\LocalCache\Roaming\Clink\settings.json</c>, with <paramref name="localAppDataRoot"/> (default
    /// <c>%LOCALAPPDATA%</c>) as the root, or <see cref="PackagesRoot"/>. A cheap directory listing; <c>Differences</c> is empty (see
    /// <see cref="Differences"/>). Never throws.
    /// </summary>
    public static IReadOnlyList<ForeignSettings> FindForeignCopies(string? localAppDataRoot = null)
    {
        var found = new List<ForeignSettings>();
        try
        {
            string packages = localAppDataRoot != null ? Path.Combine(localAppDataRoot, "Packages")
                : PackagesRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages)) return found;
            foreach (string pkgDir in Directory.EnumerateDirectories(packages))
            {
                try
                {
                    string folder = Path.Combine(pkgDir, "LocalCache", "Roaming", "Clink");
                    var file = new FileInfo(Path.Combine(folder, "settings.json"));
                    if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes) continue;
                    found.Add(new ForeignSettings(Path.GetFileName(pkgDir), folder, file.LastWriteTimeUtc, Array.Empty<string>()));
                }
                catch (Exception ex) { Log.Warn($"import: cannot look at {pkgDir}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn("import: cannot look for other copies of the settings: " + ex.Message); }
        return found.OrderByDescending(f => f.SavedUtc).ToList();
    }

    // ------------------------------------------------------------------ what differs

    // Setting -> what the user calls it. Order = the order the labels are listed in. Keys not listed here are "other settings".
    private static readonly (string Label, string[] Keys)[] s_labels =
    {
        ("video quality", new[] { nameof(Settings.Resolution), nameof(Settings.Fps), nameof(Settings.BitrateMbps), nameof(Settings.Codec), nameof(Settings.HdrKnee), nameof(Settings.HdrPeak), nameof(Settings.KeyframeSeconds), nameof(Settings.CaptureCursor) }),
        ("size target", new[] { nameof(Settings.SizeTarget) }),
        ("monitor", new[] { nameof(Settings.MonitorDevice) }),
        ("replay length", new[] { nameof(Settings.BufferSeconds) }),
        ("recording mode", new[] { nameof(Settings.RecordingMode), nameof(Settings.CustomApps) }),
        ("hotkeys", new[] { nameof(Settings.Hotkey), nameof(Settings.Hotkeys) }),
        ("audio", new[] { nameof(Settings.TrackDesktop), nameof(Settings.TrackGame), nameof(Settings.TrackMic), nameof(Settings.TrackVoice), nameof(Settings.AllDeviceId), nameof(Settings.GameProcess), nameof(Settings.MicDeviceId), nameof(Settings.VoiceApp) }),
        ("alerts", new[] { nameof(Settings.ClickSound), nameof(Settings.ClickVolume), nameof(Settings.ShowToast), nameof(Settings.ToastPosition), nameof(Settings.ToastClip), nameof(Settings.ToastShared), nameof(Settings.ToastRecording), nameof(Settings.StatusDot), nameof(Settings.StatusDotPosition) }),
        ("archive", new[] { nameof(Settings.ArchiveEnabled), nameof(Settings.ArchivePath), nameof(Settings.ArchiveAfterDays), nameof(Settings.ArchiveWhenLocalExceedsGB) }),
        ("clips folder", new[] { nameof(Settings.ClipsFolder) }),
        ("Copium token", Array.Empty<string>()),   // compared decrypted, below
        ("Copium+", new[] { nameof(Settings.CopiumPlus) }),
        ("startup", new[] { nameof(Settings.StartMinimized), nameof(Settings.RunAtStartup) }),
        ("updates", new[] { nameof(Settings.AutoUpdate), nameof(Settings.UpdateRepo) }),
        ("theme", new[] { nameof(Settings.Theme), nameof(Settings.Accent) }),
    };
    private const string OtherLabel = "other settings";

    // Differences that say nothing: where the window sits, when updates were last looked for, what the updater holds, whether the
    // audio banner was dismissed, the schema number, and which copy was turned down.
    private static bool IsVolatile(string key) =>
        key.StartsWith("Window", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("PendingUpdate", StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.LastUpdateCheck), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.SchemaVersion), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.AudioSetupSeen), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.ImportDeclined), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.CopiumTokenProtected), StringComparison.OrdinalIgnoreCase);   // the DPAPI text differs on every save

    /// <summary>
    /// What taking <paramref name="theirJson"/> over would change in <paramref name="ourJson"/>, as labels in the user's words
    /// ("video quality", "replay length", "hotkeys", "audio", "alerts", "archive", "clips folder", "Copium token", "theme", ...).
    /// Both are read the way Clink loads settings (a missing key is its default, hotkeys are put in shape), so only a setting that
    /// would really change is listed. Where the window sits, <c>LastUpdateCheck</c>, <c>PendingUpdate*</c>, <c>AudioSetupSeen</c>,
    /// <c>SchemaVersion</c> and <c>ImportDeclined</c> are ignored. The Copium token is compared decrypted (the protected text
    /// differs on every save) and neither it nor any other value is ever logged or returned.
    /// <paramref name="guard"/> is applied to the other copy once it is read (<see cref="ForeignGuard"/>).
    /// </summary>
    public static IReadOnlyList<string> Differences(string ourJson, string theirJson, Action<Settings>? guard = null)
    {
        var ours = Settings.FromJson(ourJson);
        var theirs = Settings.FromJson(theirJson);
        guard?.Invoke(theirs);
        var a = ours.ToElement().EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var b = theirs.ToElement().EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>();
        foreach (string key in a.Keys.Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsVolatile(key)) continue;
            if (a.TryGetValue(key, out var x) && b.TryGetValue(key, out var y) && JsonSame(x, y)) continue;
            labels.Add(s_labels.FirstOrDefault(l => l.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)).Label ?? OtherLabel);
        }
        if (!string.Equals(ours.CopiumToken ?? "", theirs.CopiumToken ?? "", StringComparison.Ordinal)) labels.Add("Copium token");
        var ordered = s_labels.Select(l => l.Label).Where(labels.Contains).ToList();
        if (labels.Contains(OtherLabel)) ordered.Add(OtherLabel);
        return ordered;
    }

    private static bool JsonSame(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var bp = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
                int n = 0;
                foreach (var p in a.EnumerateObject())
                {
                    n++;
                    if (!bp.TryGetValue(p.Name, out var other) || !JsonSame(p.Value, other)) return false;
                }
                return n == bp.Count;
            case JsonValueKind.Array:
                var al = a.EnumerateArray().ToList();
                var bl = b.EnumerateArray().ToList();
                return al.Count == bl.Count && al.Zip(bl).All(x => JsonSame(x.First, x.Second));
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Number: return a.GetDouble() == b.GetDouble();
            default: return true;   // null, true, false: the kind says it all
        }
    }

    // ------------------------------------------------------------------ an existing store: look, offer, decline

    /// <summary>
    /// Looks for another copy of the settings that differs from <paramref name="ours"/> and that the user has not turned down, and
    /// sets <see cref="Pending"/> to the newest one (or null), raising <see cref="PendingChanged"/> when that changed. Takes the
    /// snapshot of the running settings on the calling thread (call it on the UI thread) and does the file work on a worker; the
    /// returned task completes when <see cref="Pending"/> is up to date. Never changes anything.
    /// </summary>
    public static Task Scan(Settings ours)
    {
        string ourJson = ours.ToJson();
        string declined = ours.ImportDeclined ?? "";
        var guard = ForeignGuard;
        return Task.Run(() =>
        {
            try
            {
                foreach (var copy in FindForeignCopies())
                {
                    if (IsDeclined(declined, copy)) continue;
                    string? theirs = ReadCapped(copy.SettingsPath);
                    if (theirs == null) continue;
                    IReadOnlyList<string> differences;
                    try { differences = Differences(ourJson, theirs, guard); }
                    catch (Exception ex) { Log.Warn($"import: the settings in {copy.Package} cannot be read ({ex.Message})"); continue; }
                    if (differences.Count == 0) continue;
                    var offer = copy with { Differences = differences };
                    Log.Info($"import: the settings saved {offer.SavedLocalText} inside {offer.Package} differ from these in: {string.Join(", ", differences)}");
                    SetPending(offer);
                    return;
                }
                SetPending(null);
            }
            catch (Exception ex) { Log.Warn("import: looking for other copies of the settings failed: " + ex.Message); }
        });
    }

    private static void SetPending(ForeignSettings? value)
    {
        var old = Interlocked.Exchange(ref s_pending, value);
        if (old?.DeclineKey == value?.DeclineKey) return;
        var handler = PendingChanged;
        if (handler == null) return;
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null || ui.CheckAccess()) handler();
        else ui.BeginInvoke(handler);
    }

    // Settings.ImportDeclined holds one key per package, ';' between them: turning down one package's copy must not make another's, or
    // the same one's earlier answer, come back. A package's newer key replaces its older one.
    internal static bool IsDeclined(string? declined, ForeignSettings copy) =>
        !string.IsNullOrEmpty(declined) && declined.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(copy.DeclineKey, StringComparer.OrdinalIgnoreCase);

    internal static string AddDecline(string? declined, ForeignSettings copy)
    {
        var keep = (declined ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => !k.StartsWith(copy.Package + "|", StringComparison.OrdinalIgnoreCase)).ToList();
        keep.Add(copy.DeclineKey);
        return string.Join(";", keep);
    }

    /// <summary>The user turned this copy down: remembered in the settings (<see cref="Settings.ImportDeclined"/>) and not offered again unless that copy is saved again.</summary>
    public static void Decline(Settings ours, ForeignSettings copy)
    {
        ours.ImportDeclined = AddDecline(ours.ImportDeclined, copy);
        ours.Save();
        if (Pending?.DeclineKey == copy.DeclineKey) SetPending(null);
        Log.Info($"import: the settings saved {copy.SavedLocalText} inside {copy.Package} were turned down");
    }

    // ------------------------------------------------------------------ taking a copy over

    /// <summary>
    /// A fresh settings store (nothing in <see cref="Settings.Dir"/> yet) takes over the newest copy that can be read: see
    /// <see cref="Import"/>. Returns that copy, or null when there is none. Called before anything else has read the settings.
    /// </summary>
    public static ForeignSettings? ImportNewest()
    {
        foreach (var copy in FindForeignCopies())
        {
            try
            {
                Import(copy);
                return copy;
            }
            catch (Exception ex) { Log.Warn($"import: the settings in {copy.Package} cannot be used ({ex.Message})"); }
        }
        return null;
    }

    /// <summary>
    /// Replaces the settings with <paramref name="copy"/>'s: its settings.json is written over this one (atomically; what it replaces
    /// is kept first as settings.pre-import.json) and its clip index is merged into this one (<see cref="MergeClips"/>). Throws when
    /// the copy cannot be read, before anything is changed. The running Clink has to restart to use the result
    /// (<c>AppController.ImportForeignSettings</c> freezes saves first and restarts after).
    /// </summary>
    public static void Import(ForeignSettings copy)
    {
        string theirs = ReadCapped(copy.SettingsPath) ?? throw new IOException("settings.json is missing or too big");
        _ = Settings.FromJson(theirs);   // throws when it is not JSON, or not a JSON object

        string dir = Settings.Dir;
        Directory.CreateDirectory(dir);
        if (File.Exists(Settings.FilePath))
        {
            try { File.Copy(Settings.FilePath, Path.Combine(dir, "settings.pre-import.json"), overwrite: true); }
            catch (Exception ex) { Log.Warn("import: could not keep the current settings first: " + ex.Message); }
        }
        AtomicFile.WriteAllText(Settings.FilePath, theirs);
        try { MergeClipIndex(copy); }
        catch (Exception ex) { Log.Warn("import: the clip index was not merged: " + ex.Message); }   // the clips folder is scanned anyway
        if (Pending?.DeclineKey == copy.DeclineKey) SetPending(null);
        Log.Info($"import: took over the settings saved {copy.SavedLocalText} inside {copy.Package}"
            + (copy.Differences.Count > 0 ? $" (changes: {string.Join(", ", copy.Differences)})" : ""));
    }

    private static void MergeClipIndex(ForeignSettings copy)
    {
        string? theirs = File.Exists(copy.ClipsPath) ? ReadCapped(copy.ClipsPath) : null;
        if (theirs == null) return;
        string mine = ClipLibrary.IndexPathOverride ?? Path.Combine(Settings.Dir, "clips.json");
        string? ours = File.Exists(mine) ? File.ReadAllText(mine) : null;
        AtomicFile.WriteAllText(mine, MergeClips(ours, theirs));   // a clips.json of ours that is not JSON throws first: nothing is replaced
    }

    /// <summary>
    /// The clip index of this Clink and another copy's as one: every clip once (by path, ignoring case), ours first. Where both
    /// know a clip, ours is kept and the other copy only fills what ours leaves empty: the Copium link, page and id, when it was
    /// archived, its audio tracks, its length and size, whether it is a trim and what it was cut from. Fields this version does
    /// not know are carried along. Both texts are JSON arrays of clips as ClipLibrary writes them; <paramref name="ours"/> may be null.
    /// </summary>
    internal static string MergeClips(string? ours, string theirs)
    {
        var merged = new JsonArray();
        var byPath = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in ClipObjects(ours))
        {
            string? path = PathOf(clip);
            if (path == null) merged.Add(clip.DeepClone());
            else if (!byPath.ContainsKey(path)) { var mine = (JsonObject)clip.DeepClone(); byPath[path] = mine; merged.Add(mine); }
        }
        foreach (var clip in ClipObjects(theirs))
        {
            string? path = PathOf(clip);
            if (path == null) continue;
            if (byPath.TryGetValue(path, out var mine))
            {
                foreach (var field in clip)
                    if (field.Value != null && IsEmpty(mine[field.Key]) && !IsEmpty(field.Value)) mine[field.Key] = field.Value.DeepClone();
            }
            else { var copy = (JsonObject)clip.DeepClone(); byPath[path] = copy; merged.Add(copy); }
        }
        return merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<JsonObject> ClipObjects(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<JsonObject>();
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (node is not JsonArray array) throw new JsonException("a clip index is a JSON array");
        return array.OfType<JsonObject>().ToList();
    }

    private static string? PathOf(JsonObject clip)
    {
        var path = clip.FirstOrDefault(p => p.Key.Equals("Path", StringComparison.OrdinalIgnoreCase)).Value;
        return path is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    /// <summary>null, "", 0, false, an empty list or object, and a date that is not one (0001-01-01) say nothing.</summary>
    private static bool IsEmpty(JsonNode? node)
    {
        switch (node)
        {
            case null: return true;
            case JsonArray a: return a.Count == 0;
            case JsonObject o: return o.Count == 0;
            case JsonValue v:
                if (v.TryGetValue<bool>(out var flag)) return !flag;
                if (v.TryGetValue<string>(out var text)) return text.Length == 0 || text.StartsWith("0001-01-01", StringComparison.Ordinal);
                if (v.TryGetValue<double>(out var number)) return number == 0;
                return false;
            default: return false;
        }
    }

    private static string? ReadCapped(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"import: cannot read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Back to a fresh start: no test root, nothing pending. For the unit tests.</summary>
    internal static void Reset()
    {
        PackagesRoot = null;
        ForeignGuard = null;
        Interlocked.Exchange(ref s_pending, null);
    }
}
