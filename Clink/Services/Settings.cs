using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clink.Services;

/// <summary>
/// What a fresh install starts with (v0.2.5), in one place: the <see cref="Settings"/> initializers, <c>Normalize()</c> and the
/// UI's fallbacks all read it. An existing settings.json keeps every value it holds: a default only fills in a key the file does
/// not have, so changing one here never changes what someone already chose.
/// </summary>
internal static class SettingsDefaults
{
    public const string Resolution = "1080p";
    public const int Fps = 60;
    public const int BitrateMbps = 20;
    public const string Codec = "h264";
    public const string SizeTarget = "none";
    public const bool CaptureCursor = true;
    public const string RecordingMode = "game";
    public const int BufferSeconds = 45;
    public const string Theme = "dark";
    public const string Accent = "cyan";

    /// <summary>The main hotkey of a fresh install, and of a settings file that has no hotkey saving the full replay: Alt+C.</summary>
    public static HotkeyBinding MainHotkey() => new() { Alt = true, Vk = 0x43 };
}

internal sealed class HotkeyBinding
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public int Vk { get; set; } = 0x78; // F9 (what a hand-edited binding without a "Vk" has always meant)

    /// <summary>Keys inside this binding that this version does not know (written by a newer Clink): kept and written back, like <see cref="Settings.Unknown"/>.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    public HotkeyBinding Clone() => new()
    {
        Ctrl = Ctrl, Alt = Alt, Shift = Shift, Win = Win, Vk = Vk,
        Unknown = Unknown == null ? null : new Dictionary<string, JsonElement>(Unknown),
    };

    /// <summary>The same key with the same modifiers (two unset bindings, <see cref="Vk"/> 0, are the same).</summary>
    public bool SameKeys(HotkeyBinding? other) =>
        other != null && Vk == other.Vk && Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift && Win == other.Win;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Hotkeys.KeyName(Vk));
        return string.Join("+", parts);
    }
}

/// <summary>One hotkey: a key combo, what it does, and (for "clip") how many seconds to save (0 = the replay-length slider value).</summary>
internal sealed class HotkeyAction
{
    public HotkeyBinding Keys { get; set; } = new();
    /// <summary>clip | pause | share-last | record | screenshot | screenshot-edit-last; see <c>AppController.CommandFor</c>. Anything else is ignored (and logged) when pressed.</summary>
    public string Action { get; set; } = "clip";
    public int Seconds { get; set; } = 0;
    /// <summary>Keys inside this hotkey that this version does not know (written by a newer Clink): kept and written back, like <see cref="Settings.Unknown"/>.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class ToastSetting
{
    public bool Enabled { get; set; } = true;
    public int Seconds { get; set; } = 5;
    /// <summary>Keys inside this toast setting that this version does not know (written by a newer Clink): kept and written back, like <see cref="Settings.Unknown"/>.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class Settings
{
    /// <summary>The settings.json layout this version writes: 1 = v0.1 (one Hotkey), 2 = v0.2 (the Hotkeys list), 3 = v0.2.5 (the main hotkey first, Theme, Accent).</summary>
    public const int CurrentSchema = 3;
    /// <summary>
    /// Layout version of the file. A file without it is 2 when it has a Hotkeys list, else 1. Saving always writes
    /// <see cref="CurrentSchema"/>, over a higher number too: a newer Clink that reads the file after this version has rewritten it
    /// must see "3" and check its own rules again, not trust a number that no longer describes what is in the file.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchema;

    public string? MonitorDevice { get; set; }                 // "\\.\DISPLAY1"; null = primary
    public int Fps { get; set; } = SettingsDefaults.Fps;
    public int BitrateMbps { get; set; } = SettingsDefaults.BitrateMbps;
    public string Codec { get; set; } = SettingsDefaults.Codec;                // h264 | hevc
    public string Resolution { get; set; } = SettingsDefaults.Resolution;      // native | 1440p | 1080p | 720p
    public int BufferSeconds { get; set; } = SettingsDefaults.BufferSeconds;
    public bool CaptureCursor { get; set; } = SettingsDefaults.CaptureCursor;
    /// <summary>The v1 single hotkey. Since v0.2.5 it mirrors the keys of <see cref="Hotkeys"/>[0], the main hotkey, whenever those are set.</summary>
    public HotkeyBinding Hotkey { get; set; } = SettingsDefaults.MainHotkey();
    public bool TrackDesktop { get; set; } = false;             // extra "Desktop" track (everything you hear); track 1 is always the game+mic+voice mix
    public bool TrackGame { get; set; } = true;
    public bool TrackMic { get; set; } = true;
    public bool TrackVoice { get; set; } = false;
    public string? AllDeviceId { get; set; }                   // Desktop track: "out:<id>" / bare render endpoint id = loopback of that output, "in:<id>" = a capture device (Wave Link mix, virtual cable); null = default output
    public string GameProcess { get; set; } = "auto";          // process name (no .exe) for the Game track, or "auto" = the fullscreen app on the captured monitor
    public string? MicDeviceId { get; set; }
    public string VoiceApp { get; set; } = "auto";
    public bool ClickSound { get; set; } = true;
    public double ClickVolume { get; set; } = 0.5;
    public string ClipsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink");
    public string? CopiumTokenProtected { get; set; }
    public bool StartMinimized { get; set; }
    public bool RunAtStartup { get; set; }
    public bool ShowToast { get; set; } = true;                // v1; a file with ShowToast=false and no ToastClip key turns the clip toast off on load
    public float HdrKnee { get; set; } = 0.75f;
    public float HdrPeak { get; set; } = 4f;
    public float KeyframeSeconds { get; set; } = 1f;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool AutoUpdate { get; set; } = true;               // check GitHub on start + every 6 h, download new releases and install on next start; off = never contact GitHub unprompted
    public bool UseDevBuilds { get; set; }                     // install dev-branch prereleases (Dev Build in the version popup); per installation, never imported
    public string? UpdateRepo { get; set; }                    // "owner/repo"; overrides the UpdateRepo baked into the exe at build time (validated, logged at start, shown in the version tooltip)
    public DateTime? LastUpdateCheck { get; set; }

    // ---- v2 (see docs/SPEC-v2.md) ----
    /// <summary>"always" = record whenever Clink runs; "game" = only while a detected game or a custom app is running.</summary>
    public string RecordingMode { get; set; } = SettingsDefaults.RecordingMode;
    /// <summary>Exe names (without .exe) that always count as a game, windowed or not.</summary>
    public List<string> CustomApps { get; set; } = new();
    /// <summary>
    /// Hotkey list. Since v0.2.5 <c>Hotkeys[0]</c> is always the main hotkey: a "clip" binding with Seconds 0 (the full replay),
    /// which may be unset (Keys.Vk 0); <c>Hotkeys[1..]</c> are additional ones: a "clip" with Seconds &gt; 0 (a fixed shorter
    /// length), "pause" or "share-last". The list is stored as before, so an older Clink still reads it. <see cref="EnsureHotkeyModel"/>
    /// puts a file's list in this shape on load.
    /// </summary>
    public List<HotkeyAction> Hotkeys { get; set; } = new();
    /// <summary>top-left | top | top-right | left | right | bottom-left | bottom | bottom-right (on the captured monitor).</summary>
    public string ToastPosition { get; set; } = "bottom-right";
    public ToastSetting ToastClip { get; set; } = new() { Enabled = true, Seconds = 7 };
    public ToastSetting ToastShared { get; set; } = new() { Enabled = true, Seconds = 4 };
    public ToastSetting ToastRecording { get; set; } = new() { Enabled = true, Seconds = 4 };
    /// <summary>Always-on status dot (green recording / amber waiting for a game / red error) in a corner of the captured monitor.</summary>
    public bool StatusDot { get; set; } = false;
    public string StatusDotPosition { get; set; } = "top-right";
    /// <summary>none | discord10 | discord50 | copium200 | discord500 | copiumplus (<see cref="Capabilities.SizeTargets"/>): pick the bitrate so a clip of the replay length (at most the service's length limit, 10 min for copium200, 60 min for copiumplus) fits the limit. New settings start on none.</summary>
    public string SizeTarget { get; set; } = SettingsDefaults.SizeTarget;
    /// <summary>The user has Copium+: Share no longer refuses clips over Copium's free limits (200 MB / 10 min per upload).</summary>
    public bool CopiumPlus { get; set; }
    /// <summary>The "Set up your audio" banner on the Capture tab was dismissed (the audio group was hovered over or an audio option changed); it never shows again.</summary>
    public bool AudioSetupSeen { get; set; }
    /// <summary>Archive (NAS / share / second drive): clips move there in the background after N days and/or when the local folder exceeds N GB (whichever first).</summary>
    public bool ArchiveEnabled { get; set; }
    public string? ArchivePath { get; set; }
    public int ArchiveAfterDays { get; set; } = 0;
    public double ArchiveWhenLocalExceedsGB { get; set; } = 0;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public string? PendingUpdateVersion { get; set; }          // written by the updater: version and SHA-256 of the download it verified in %LOCALAPPDATA%\Clink\updates;
    public string? PendingUpdateSha256 { get; set; }           // the file is hashed again against this before it is ever installed
    public string? PendingUpdateFrom { get; set; }             // version of the exe that made that download: only that exe may go from a dev build back to it at startup

    // ---- v3 (v0.2.5) ----
    /// <summary>dark | light</summary>
    public string Theme { get; set; } = SettingsDefaults.Theme;
    /// <summary>cyan | blue | violet | pink | orange | green</summary>
    public string Accent { get; set; } = SettingsDefaults.Accent;
    /// <summary>
    /// The copies of the settings that another Clink saved in a packaged app's private AppData (SettingsImport) which the user turned
    /// down: "&lt;package&gt;|&lt;ticks of the time it was saved&gt;", one per package, ';' between them. A copy saved again later is a
    /// different one and is offered again.
    /// </summary>
    public string? ImportDeclined { get; set; }

    // ---- v0.4 screenshots (additive: no schema change, see SeedHotkeyDefaults) ----
    /// <summary>Master switch of the screenshot tools: off = the screenshot hotkey rows stay in the list but are not bound, the tray items are hidden and <c>--screenshot</c> only says they are off.</summary>
    public bool ScreenshotsEnabled { get; set; } = true;
    /// <summary>Where screenshots are saved; null = a <c>Screenshots</c> folder inside <see cref="ClipsFolder"/> (resolved when used).</summary>
    public string? ScreenshotsFolder { get; set; }
    /// <summary>none | before | after (<see cref="Clink.Screenshots.ScreenshotModes"/>): what happens after the region is picked. "none" saves at once; "after" opens the editor on the picture first; "before" (editing on the screen) is not in this version and acts like "none".</summary>
    public string ScreenshotMode { get; set; } = Clink.Screenshots.ScreenshotModes.None;
    /// <summary>The screenshot editor's last-used tool options (colour, width, text size / backdrop): the next editor opens the way the last one was left.</summary>
    public Clink.Screenshots.Editing.ScreenshotEditorOptions ScreenshotEditor { get; set; } = new();
    public bool ScreenshotCopyImage { get; set; } = true;
    public bool ScreenshotLoupe { get; set; } = true;
    public ToastSetting ToastScreenshot { get; set; } = new() { Enabled = true, Seconds = 6 };
    /// <summary>
    /// The hotkey actions whose default binding was offered once (<see cref="SeedHotkeyDefaults"/>), whether or not it was added: a
    /// user who deletes the row does not get it back. Newer Clink versions keep it too.
    /// </summary>
    public List<string> SeededHotkeyDefaults { get; set; } = new();

    /// <summary>
    /// Keys in settings.json that this version does not know (written by a newer Clink). They are kept and written back by
    /// <see cref="Save"/>, so going back and forth between versions does not drop the newer one's settings.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore]
    public string? CopiumToken
    {
        get => Unprotect(CopiumTokenProtected);
        set => CopiumTokenProtected = Protect(value);
    }

    /// <summary>
    /// settings.json carried a <see cref="Hotkeys"/> key. An empty list is then the user's choice (every row removed), not a v1
    /// file: <see cref="EnsureHotkeyModel"/> still gives the list its main hotkey, but never fills it from <see cref="Hotkey"/>.
    /// </summary>
    [JsonIgnore] public bool HotkeysFromFile { get; private set; }

    /// <summary>settings.json (or its .bak) existed when <see cref="Load"/> ran: false on a first start.</summary>
    [JsonIgnore] public bool LoadedFromFile { get; private set; }

    /// <summary>
    /// What was loaded is not what the file holds (an older schema, a first start, a hotkey list put in shape): the caller saves
    /// once, so the next load finds the file as it is now. <c>AppController.Start</c> does.
    /// </summary>
    [JsonIgnore] public bool Migrated { get; private set; }

    /// <summary>
    /// The file had no hotkey that saves the full replay, and Alt+C was taken by another binding, so the main hotkey was added
    /// unset: the user is told once to set it (Capture → Hotkeys).
    /// </summary>
    [JsonIgnore] public bool MainHotkeyNeedsSetup { get; private set; }

    /// <summary>The file had no hotkey that saves the full replay, so the main hotkey was added on Alt+C: the user is told once.</summary>
    [JsonIgnore] public bool MainHotkeyAssigned { get; private set; }

    /// <summary>The main hotkey: <c>Hotkeys[0]</c>, "clip" with Seconds 0 (the full replay). Its keys may be unset.</summary>
    [JsonIgnore] public HotkeyAction MainHotkey => Hotkeys[0];

    /// <summary>The schema number the loaded file had (see <see cref="SchemaVersion"/>); 0 when nothing was read from a file.</summary>
    private int _fileSchema;
    /// <summary>The text of a settings file of schema 1 or 2 that was loaded: <see cref="Save"/> keeps it once as settings.pre-v3.json before the file is rewritten in schema 3.</summary>
    private string? _preV3Text;
    private bool _hotkeysInShape;   // EnsureHotkeyModel has run: Hotkeys is in schema-3 shape, whatever schema the file had
    private readonly List<string> _unreadable = new();
    private readonly List<int> _droppedRows = new();   // 1-based rows of the Hotkeys list that could not be read and were left out

    /// <summary>
    /// settings.json (or its backup) parsed, but these values could not be read: they kept their defaults ("Fps"), or, for the
    /// hotkey list, the row was left out and the others kept ("Hotkeys row 2"). Empty when every value was fine. The file itself
    /// stays as it is until the next save, and a copy of it is kept as settings.json.unreadable.
    /// </summary>
    [JsonIgnore] public IReadOnlyList<string> UnreadableValues => _unreadable.Concat(_droppedRows.Select(r => $"Hotkeys row {r}")).ToList();

    /// <summary>
    /// settings.json exists but could not be read (a hand edit with a typo), so Clink started from the backup or the defaults, or it
    /// was read except for some values that kept their defaults: a message for the user, or null.
    /// </summary>
    [JsonIgnore] public string? LoadProblem { get; private set; }

    /// <summary>settings.json itself was skipped (it did not parse), as opposed to read with some values that kept their defaults.</summary>
    [JsonIgnore] public bool FileWasSkipped { get; private set; }

    /// <summary>
    /// Set by a first start that took over the settings another Clink had saved inside a packaged app's private AppData
    /// (<see cref="SettingsImport.ImportNewest"/>); <c>AppController.Start</c> says so once.
    /// </summary>
    [JsonIgnore] public ForeignSettings? ImportedFrom { get; set; }

    /// <summary>Where the settings live instead of <c>%APPDATA%\Clink</c>: set by <c>--dev &lt;folder&gt;</c> and by the unit tests, null otherwise.</summary>
    internal static string? DirOverride { get; set; }
    /// <summary>Stands in for <see cref="RealDir"/> in the unit tests (a made-up <c>%APPDATA%\Clink</c> to check a <c>--dev</c> folder against); null otherwise.</summary>
    internal static string? RealDirOverride { get; set; }
    /// <summary>The real settings folder, <c>%APPDATA%\Clink</c>, whatever <see cref="DirOverride"/> says: what a <c>--dev</c> folder must never be.</summary>
    internal static string RealDir => RealDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink");
    public static string Dir => DirOverride ?? RealDir;
    public static string FilePath => Path.Combine(Dir, "settings.json");

    // Lenient reading, because README invites hand edits: a trailing comma, a // comment or a number in quotes must not make
    // Clink drop the whole file for the backup. Writing is unaffected (no comments, no trailing commas, numbers as numbers),
    // except that a NaN / Infinity is written as the string "NaN" (which this reads back) instead of making Save throw.
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    private static readonly JsonDocumentOptions s_doc = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly object s_saveGate = new();        // Save() is also called from the updater's background thread
    private static volatile bool s_frozen;

    /// <summary>
    /// From now on <see cref="Save"/> writes nothing. An import replaces settings.json behind the running Clink's back and then
    /// restarts it; a save that is still pending (the window's 800 ms timer, the updater) must not put the old settings back.
    /// </summary>
    public static void FreezeSaves()
    {
        s_frozen = true;
        Log.Info("settings: saves are frozen");
    }

    /// <summary>Undoes <see cref="FreezeSaves"/> (an import that failed before it replaced anything, and the unit tests).</summary>
    internal static void ThawSaves() => s_frozen = false;

    internal static bool SavesFrozen => s_frozen;

    // The properties a settings file can set: public with a public setter, not [JsonIgnore], not the extension data.
    private static Dictionary<string, PropertyInfo>? s_bindable;
    private static Dictionary<string, PropertyInfo> Bindable => s_bindable ??= typeof(Settings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.SetMethod!.IsPublic && p.GetIndexParameters().Length == 0
                    && p.GetCustomAttribute<JsonIgnoreAttribute>() == null && p.GetCustomAttribute<JsonExtensionDataAttribute>() == null)
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    // The floating-point settings (Window*, HdrKnee, HdrPeak, KeyframeSeconds, ClickVolume, ArchiveWhenLocalExceedsGB, ...) and
    // what a fresh Settings holds in them: a NaN / Infinity is put back to that when settings are loaded and before every save (see Sanitize).
    private static PropertyInfo[]? s_floating;
    private static PropertyInfo[] FloatingProperties => s_floating ??= Bindable.Values
        .Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float) || p.PropertyType == typeof(double?) || p.PropertyType == typeof(float?))
        .ToArray();
    private static Settings? s_defaults;
    private static Settings Defaults => s_defaults ??= new Settings();

    public static Settings Load()
    {
        Settings? s = null;
        string? unreadable = null;   // why settings.json itself did not parse, when it exists and did not
        bool existed = File.Exists(FilePath) || File.Exists(AtomicFile.BackupPath(FilePath));
        try
        {
            s = AtomicFile.ReadWithFallback(FilePath, Parse, why => unreadable = why);
        }
        catch (Exception ex) { Log.Warn("Settings load failed: " + ex.Message); }
        bool fromBackup = s != null && unreadable != null;
        s ??= new Settings();
        s.LoadedFromFile = existed;
        s.FileWasSkipped = unreadable != null;
        // Said at start (AppController.Start): a hand edit that does not parse must not just silently have no effect.
        var problems = new List<string>();
        if (unreadable != null) problems.Add($"settings.json was not used ({unreadable}): Clink started with {(fromBackup ? "the previous settings" : "default settings")}");
        if (s._unreadable.Count > 0 || s._droppedRows.Count > 0)
        {
            // It parsed, but some values did not make sense: those keep their defaults (a hotkey row that does not make sense is left
            // out, the others stay). The file is left as it is until the next save, so what it said is kept beside it first.
            // (When the file itself was skipped, ReadWithFallback has kept it already.)
            bool kept = unreadable == null && KeepUnreadableCopy();
            var what = new List<string>();
            if (s._unreadable.Count > 0) what.Add($"kept the defaults for {string.Join(", ", s._unreadable)} (unreadable values)");
            if (s._droppedRows.Count > 0) what.Add($"left out hotkey row{(s._droppedRows.Count > 1 ? "s" : "")} {string.Join(", ", s._droppedRows)} (unreadable)");
            problems.Add("settings.json: " + string.Join("; ", what) + (kept ? "; the original is kept as settings.json.unreadable" : ""));
        }
        if (problems.Count > 0) s.LoadProblem = string.Join(". ", problems);
        s.Normalize();
        s.SeedHotkeyDefaults();   // after Normalize, not inside it: FromJson (import comparisons) normalizes too and must not add rows
        // A NaN / Infinity the file holds (an earlier version wrote them as text) is put back to the default here, before anything
        // reads it: BuildConfig, the encoder and the window never see it, not only the next save.
        s.Sanitize(quiet: false);
        // A file of an older schema, or no usable file at all (a first start): the caller saves once, in the current layout.
        if (s._fileSchema < CurrentSchema) s.Migrated = s.SchemaMigrated = true;
        s.SchemaVersion = CurrentSchema;   // what a save writes, whatever number the file had (see SchemaVersion)
        return s;
    }

    private static bool KeepUnreadableCopy()
    {
        try { File.Copy(FilePath, FilePath + ".unreadable", overwrite: true); return true; }
        catch (Exception ex) { Log.Warn("settings.json: could not keep a copy: " + ex.Message); return false; }
    }

    /// <summary>
    /// Reads settings from JSON text the way <see cref="Load"/> does (tolerant, normalized) but touches no file and logs nothing:
    /// for comparing another copy of the settings with the running ones (SettingsImport). Throws when the text is not a JSON object.
    /// </summary>
    internal static Settings FromJson(string json)
    {
        var s = ParseCore(json, quiet: true);
        s.Normalize();
        // As Load does, so two copies are compared as they would be once loaded: a copy saved before the default hotkeys were offered
        // would otherwise differ from ours for good (an import writes the copy's text, and the next load seeds it again).
        s.SeedHotkeyDefaults();
        s.Sanitize(quiet: true);
        return s;
    }

    /// <summary>These settings as the JSON text <see cref="Save"/> would write (without touching the file or the values).</summary>
    internal string ToJson()
    {
        lock (s_saveGate) return JsonSerializer.Serialize(this, s_json);
    }

    /// <summary>These settings as a JSON tree, for comparing two copies key by key.</summary>
    internal JsonElement ToElement()
    {
        lock (s_saveGate) return JsonSerializer.SerializeToElement(this, s_json);
    }

    /// <summary>Deserializes one settings file and applies the migrations that need to know which keys the file had.</summary>
    private static Settings Parse(string json) => ParseCore(json, quiet: false);

    /// <summary>
    /// Binds the keys of a settings file one by one, so that a value that cannot be read (<c>"Fps": "sixty"</c>, <c>"BufferSeconds": [1]</c>)
    /// costs that one setting its default and nothing else; it is logged, and listed in <see cref="UnreadableValues"/>. Keys this
    /// version has no setting for go to <see cref="Unknown"/>. Text that is not JSON at all still throws, which is what sends
    /// <see cref="AtomicFile.ReadWithFallback"/> to the backup.
    /// </summary>
    private static Settings ParseCore(string json, bool quiet)
    {
        using var doc = JsonDocument.Parse(json, s_doc);
        var root = doc.RootElement;
        var s = new Settings { _fileSchema = 1 };
        if (root.ValueKind == JsonValueKind.Null) return s;   // a file that says "null" has always been an empty one
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException($"expected a JSON object, found {root.ValueKind.ToString().ToLowerInvariant()}");
        foreach (var p in root.EnumerateObject())
        {
            if (!Bindable.TryGetValue(p.Name, out var prop))
            {
                (s.Unknown ??= new())[p.Name] = p.Value.Clone();   // the document is disposed below: keep a copy of the element
                continue;
            }
            try
            {
                // null is a value for a text or an optional number, not for a number or a switch: PropertyInfo.SetValue would turn it into 0 / false.
                if (p.Value.ValueKind == JsonValueKind.Null && prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) == null)
                    throw new JsonException("null for a setting that has to have a value");
                if (prop.Name == nameof(Hotkeys) && p.Value.ValueKind == JsonValueKind.Array)
                    prop.SetValue(s, ReadHotkeyRows(p.Value, s, quiet));   // row by row: one that makes no sense costs only itself
                else
                    prop.SetValue(s, p.Value.Deserialize(prop.PropertyType, s_json));
            }
            catch (Exception ex)
            {
                if (!s._unreadable.Contains(prop.Name)) s._unreadable.Add(prop.Name);
                if (!quiet) Log.Warn($"settings: {prop.Name} could not be read ({(ex is JsonException ? "wrong kind of value" : ex.Message)}); it keeps its default");
            }
        }
        s.HotkeysFromFile = HasKey(root, nameof(Hotkeys));
        s._fileSchema = SchemaOf(root);
        // v1 "ShowToast": false becomes the clip toast's switch when the file has no v2 toast setting yet.
        if (!s.ShowToast && !HasKey(root, nameof(ToastClip)))
        {
            s.ToastClip ??= new ToastSetting { Seconds = 7 };
            s.ToastClip.Enabled = false;
            if (!quiet) Log.Info("settings: migrated ShowToast=false → ToastClip.Enabled=false");
        }
        if (s._fileSchema < CurrentSchema) s._preV3Text = json;
        return s;
    }

    /// <summary>
    /// The rows of a "Hotkeys" array, one by one. A row that cannot be read (<c>{ "Keys": { "Vk": "x" } }</c>, a number where a
    /// binding should be) is left out and logged, and listed in <see cref="UnreadableValues"/> as "Hotkeys row N"; the rows around it
    /// are kept, in order. A JSON null stays a null row, which <see cref="Normalize"/> removes as it always did.
    /// </summary>
    private static List<HotkeyAction> ReadHotkeyRows(JsonElement array, Settings s, bool quiet)
    {
        var rows = new List<HotkeyAction>();
        int number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            try { rows.Add(item.Deserialize<HotkeyAction>(s_json)!); }
            catch (Exception ex)
            {
                s._droppedRows.Add(number);
                if (!quiet) Log.Warn($"settings: hotkey row {number} could not be read ({(ex is JsonException ? "wrong kind of value" : ex.Message)}); it is left out and the other rows are kept");
            }
        }
        return rows;
    }

    private static bool HasKey(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The schema a settings file is in: its SchemaVersion key, else 2 when it has a Hotkeys list (v0.2), else 1 (v0.1).</summary>
    private static int SchemaOf(JsonElement obj)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!string.Equals(p.Name, nameof(SchemaVersion), StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n) && n > 0) return n;
            if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) return n;
        }
        return HasKey(obj, nameof(Hotkeys)) ? 2 : 1;
    }

    /// <summary>
    /// A hand-edited or merged settings.json can carry null where an object or list is expected ("Hotkeys": null); nothing
    /// downstream null-checks these, so they are given their defaults once here instead of crashing Start() half-way. Ends with
    /// <see cref="EnsureHotkeyModel"/>.
    /// </summary>
    internal void Normalize()
    {
        Hotkey ??= SettingsDefaults.MainHotkey();
        Hotkeys ??= new List<HotkeyAction>();
        Hotkeys.RemoveAll(h => h == null);
        foreach (var h in Hotkeys) { h.Keys ??= new HotkeyBinding { Vk = 0 }; h.Action ??= "clip"; }
        CustomApps ??= new List<string>();
        CustomApps.RemoveAll(a => a == null);
        ToastClip ??= new ToastSetting { Enabled = true, Seconds = 7 };
        ToastShared ??= new ToastSetting { Enabled = true, Seconds = 4 };
        ToastRecording ??= new ToastSetting { Enabled = true, Seconds = 4 };
        Codec ??= SettingsDefaults.Codec;
        Resolution ??= SettingsDefaults.Resolution;
        GameProcess ??= "auto";
        VoiceApp ??= "auto";
        RecordingMode ??= SettingsDefaults.RecordingMode;
        ToastPosition ??= "bottom-right";
        StatusDotPosition ??= "top-right";
        SizeTarget ??= SettingsDefaults.SizeTarget;
        if (string.IsNullOrWhiteSpace(Theme)) Theme = SettingsDefaults.Theme;
        if (string.IsNullOrWhiteSpace(Accent)) Accent = SettingsDefaults.Accent;
        if (string.IsNullOrWhiteSpace(ClipsFolder)) ClipsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink");
        if (string.IsNullOrWhiteSpace(ScreenshotsFolder)) ScreenshotsFolder = null;
        // A relative path (a hand edit, an import) would resolve against the current directory, which is System32 at sign-in: the default instead.
        else if (!IsFullPath(ScreenshotsFolder.Trim())) ScreenshotsFolder = null;
        ScreenshotMode = Clink.Screenshots.ScreenshotModes.Normalize(ScreenshotMode);
        ScreenshotEditor ??= new Clink.Screenshots.Editing.ScreenshotEditorOptions();
        ScreenshotEditor.Normalize();
        ToastScreenshot ??= new ToastSetting { Enabled = true, Seconds = 6 };
        SeededHotkeyDefaults ??= new List<string>();
        SeededHotkeyDefaults.RemoveAll(a => string.IsNullOrWhiteSpace(a));
        EnsureHotkeyModel();
    }

    private static bool IsFullPath(string path)
    {
        try { return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0; }
        catch { return false; }
    }

    /// <summary>
    /// Test seam, true in the app: <see cref="SeedHotkeyDefaults"/> does something. The unit-test runner turns it off for every test
    /// (the older tests pin exactly which hotkey rows a given file loads to); the seeding tests switch it on themselves.
    /// </summary>
    internal static bool SeedDefaults { get; set; } = true;

    /// <summary>The default hotkeys a version offers once, as (action, keys): bare PrintScreen takes a screenshot, Shift+PrintScreen edits the last one.</summary>
    internal static IEnumerable<(string Action, HotkeyBinding Keys)> HotkeyDefaultsToSeed()
    {
        yield return ("screenshot", new HotkeyBinding { Vk = 0x2C });   // VK_SNAPSHOT
        yield return ("screenshot-edit-last", new HotkeyBinding { Shift = true, Vk = 0x2C });
    }

    /// <summary>
    /// Offers each default hotkey (<see cref="HotkeyDefaultsToSeed"/>) once, instead of a schema migration (a schema bump would make
    /// every schema-3 file look "older" to <see cref="EnsureHotkeyModel"/>): for an action not yet in <see cref="SeededHotkeyDefaults"/>
    /// a row is appended only if no row has that action and no row uses those keys; the action is recorded as seeded either way. So a
    /// user's own PrintScreen binding is respected, a deleted row never comes back, and a second load adds nothing. Raises
    /// <see cref="Migrated"/> when anything changed, so the caller saves once. Called by <see cref="Load"/> after <see cref="Normalize"/>.
    /// </summary>
    internal void SeedHotkeyDefaults()
    {
        if (!SeedDefaults) return;
        bool changed = false;
        foreach (var (action, keys) in HotkeyDefaultsToSeed())
        {
            if (SeededHotkeyDefaults.Contains(action, StringComparer.OrdinalIgnoreCase)) continue;
            bool actionFree = !Hotkeys.Any(h => string.Equals((h.Action ?? "").Trim(), action, StringComparison.OrdinalIgnoreCase));
            bool keysFree = !Hotkeys.Any(h => h.Keys.SameKeys(keys));
            if (actionFree && keysFree)
            {
                Hotkeys.Add(new HotkeyAction { Keys = keys, Action = action, Seconds = 0 });
                _addedDefaults.Add(action);
            }
            SeededHotkeyDefaults.Add(action);
            _offeredDefaults.Add(action);
            changed = true;
        }
        if (changed) Migrated = true;
    }

    private readonly List<string> _offeredDefaults = new(), _addedDefaults = new();

    /// <summary>The default hotkeys this load offered (<see cref="SeedHotkeyDefaults"/>), added or not: what made <see cref="Migrated"/> true besides <see cref="SchemaMigrated"/>.</summary>
    [JsonIgnore] public IReadOnlyList<string> OfferedHotkeyDefaults => _offeredDefaults;

    /// <summary>The default hotkeys this load added as rows (their keys were free): the user is told once (<c>AppController.SeededHotkeyNotice</c>).</summary>
    [JsonIgnore] public IReadOnlyList<string> AddedHotkeyDefaults => _addedDefaults;

    /// <summary><see cref="Migrated"/> for a reason other than offering default hotkeys: an older schema, a first start, a hotkey list put in shape.</summary>
    [JsonIgnore] public bool SchemaMigrated { get; private set; }

    /// <summary>
    /// Puts <see cref="Hotkeys"/> in the shape the rest of Clink relies on: <c>Hotkeys[0]</c> is the main hotkey, a "clip" with
    /// Seconds 0 (the full replay) that may be unset, and every later row is an additional hotkey. Runs on every load and does
    /// nothing to a list that is already in shape (the flags below are only ever raised, never cleared). Nothing that is bound
    /// changes what it does: a key that fires something today fires the same thing afterwards.
    /// <list type="number">
    /// <item>No Hotkeys key and an empty list (a v0.1 file, or a first start): the main is the <see cref="Hotkey"/> (the file's own key for v0.1, Alt+C on a first start).</item>
    /// <item>A Hotkeys list that is empty ("no hotkeys, by choice" in v0.2.4, or a list that could not be read): an unset main (<see cref="MainHotkeyNeedsSetup"/>). Alt+C is not bound, and swallowed system-wide, for someone who chose to have no hotkeys.</item>
    /// <item>In a file of schema 3 or higher (or a list already in shape), a full-replay "clip" row that already leads the list is the main, with keys or cleared, and nothing is promoted over it. v0.2.4's Add hotkey made Seconds-0 rows, so a cleared main with another full-replay row below it is common; reading that row as the main would undo the user's choice and push the cleared row down. Older files keep the rules below.</item>
    /// <item>Otherwise the main is the first "clip" row with Seconds 0 that has keys and that no earlier row with the same keys shadows (the first match fires, so putting it ahead of such a row would change what the key does); it moves to index 0 and keeps its keys.</item>
    /// <item>No such row. In a file of schema 3 a full-replay row without keys is the main the user cleared and stays the main. In an older file a full-replay row without keys is the "Default" row that v0.2.4's Add hotkey makes, never given keys, and nobody's choice; and a main is also needed when every clip hotkey has a fixed length, or the full-replay row is shadowed. The main is that unset row, or a new row at index 0, with Alt+C (<see cref="MainHotkeyAssigned"/>) unless a row already uses those keys, then unset (<see cref="MainHotkeyNeedsSetup"/>).</item>
    /// <item>The main is forced to "clip" with Seconds 0, and <see cref="Hotkey"/> follows its keys when it has any.</item>
    /// </list>
    /// <see cref="Migrated"/> is raised when anything changed, so the caller saves once.
    /// </summary>
    internal void EnsureHotkeyModel()
    {
        // Clink.Services.Hotkeys, not the Hotkeys property of this class.
        static bool FullReplay(HotkeyAction h) => global::Clink.Services.Hotkeys.IsClip(h.Action) && h.Seconds <= 0;
        bool changed = false;
        bool older = !_hotkeysInShape && _fileSchema > 0 && _fileSchema < CurrentSchema;   // rows written by v0.1 / v0.2.x

        void MoveToFront(int i)
        {
            if (i <= 0) return;
            var row = Hotkeys[i];
            Hotkeys.RemoveAt(i);
            Hotkeys.Insert(0, row);
            changed = true;
        }

        if (Hotkeys.Count == 0)
        {
            if (!HotkeysFromFile)
            {
                Hotkeys.Add(new HotkeyAction { Keys = Hotkey.Clone(), Action = "clip", Seconds = 0 });
                if (Hotkey.Vk <= 0) MainHotkeyNeedsSetup = true;   // a hand-edited v0.1 file with no key at all
            }
            else
            {
                Hotkeys.Add(new HotkeyAction { Keys = new HotkeyBinding { Vk = 0 }, Action = "clip", Seconds = 0 });
                MainHotkeyNeedsSetup = true;
            }
            changed = true;
        }
        else if (!older && FullReplay(Hotkeys[0]))
        {
            // Schema 3 or higher, and a full-replay clip row already leads the list: it is the main, set or cleared. Another full-replay
            // row below it (with keys or without) is an additional hotkey and is left where it is.
        }
        else
        {
            int lead = -1;
            for (int i = 0; i < Hotkeys.Count && lead < 0; i++)
            {
                var h = Hotkeys[i];
                if (!FullReplay(h) || h.Keys.Vk <= 0) continue;
                bool shadowed = false;
                for (int j = 0; j < i && !shadowed; j++) shadowed = Hotkeys[j].Keys.SameKeys(h.Keys);
                if (!shadowed) lead = i;
            }
            if (lead >= 0) MoveToFront(lead);
            else
            {
                int unset = Hotkeys.FindIndex(h => FullReplay(h) && h.Keys.Vk <= 0);
                if (unset >= 0 && !older) MoveToFront(unset);   // schema 3: the main the user cleared
                else
                {
                    // Alt+C unless one of the rows (a pause, a share, a shorter clip) is already on it: it must not be bound twice.
                    var alt = SettingsDefaults.MainHotkey();
                    bool free = !Hotkeys.Any(h => h.Keys.SameKeys(alt));
                    HotkeyAction main;
                    if (unset >= 0) { main = Hotkeys[unset]; MoveToFront(unset); }   // the "Default" row nobody gave keys
                    else { main = new HotkeyAction { Keys = new HotkeyBinding { Vk = 0 }, Action = "clip", Seconds = 0 }; Hotkeys.Insert(0, main); }
                    if (free) { main.Keys = alt; MainHotkeyAssigned = true; } else MainHotkeyNeedsSetup = true;
                    changed = true;
                }
            }
        }
        var m = Hotkeys[0];
        if (m.Action != "clip") { m.Action = "clip"; changed = true; }
        if (m.Seconds != 0) { m.Seconds = 0; changed = true; }
        if (m.Keys.Vk > 0 && !Hotkey.SameKeys(m.Keys)) { Hotkey = m.Keys.Clone(); changed = true; }
        _hotkeysInShape = true;   // what is here now is schema-3 shape: a second pass must not read it as an older file's rows
        if (changed) Migrated = SchemaMigrated = true;
    }

    /// <summary>
    /// Writes settings.json. Always on the UI thread: a save asked for from another thread (the updater's, after a check or a
    /// download) is queued there, so it never reads the hotkey or custom-app lists while the UI changes them ("Collection was
    /// modified"). Does nothing while <see cref="FreezeSaves"/> is in force.
    /// </summary>
    public void Save()
    {
        if (s_frozen) return;
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui != null && !ui.CheckAccess() && !ui.HasShutdownStarted)
        {
            ui.BeginInvoke(new Action(Save));
            return;
        }
        try
        {
            lock (s_saveGate)
            {
                if (s_frozen) return;
                Sanitize();
                SchemaVersion = CurrentSchema;
                Directory.CreateDirectory(Dir);
                KeepPreV3Copy();
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, s_json));
            }
        }
        catch (Exception ex) { Log.Warn("Settings save failed: " + ex.Message); }
    }

    /// <summary>
    /// A NaN or Infinity in a floating-point setting (a window position that was never set, a hand edit, the text "NaN" an earlier
    /// version wrote) is put back to what a fresh Settings holds there (null for the window's position and size, which means "let
    /// Windows place it"): such a value would be written as the text "NaN", which is no number to anything else that reads the file,
    /// and means nothing as a position, a volume or an encoder setting. Run when settings are loaded (<see cref="Load"/>,
    /// <see cref="FromJson"/>) and again before every save (under the save gate). <paramref name="quiet"/>: say nothing in the log.
    /// </summary>
    private void Sanitize(bool quiet = false)
    {
        foreach (var p in FloatingProperties)
        {
            bool nonFinite = p.GetValue(this) switch { double d => !double.IsFinite(d), float f => !float.IsFinite(f), _ => false };
            if (!nonFinite) continue;
            p.SetValue(this, p.GetValue(Defaults));
            if (!quiet) Log.Warn($"settings: {p.Name} was not a number, it is back to its default");
        }
    }

    /// <summary>
    /// The first save in schema 3 of a file that was in schema 1 or 2: what the old file said is kept beside it as
    /// settings.pre-v3.json, once. An existing copy is never overwritten, so it stays the file Clink found before it first changed it.
    /// </summary>
    private void KeepPreV3Copy()
    {
        string? old = _preV3Text;
        if (old == null) return;
        _preV3Text = null;
        try
        {
            string path = Path.Combine(Dir, "settings.pre-v3.json");
            if (File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] bytes = new UTF8Encoding(false).GetBytes(old);
            fs.Write(bytes, 0, bytes.Length);
            Log.Info($"settings: the schema-{_fileSchema} file is kept as settings.pre-v3.json");
        }
        catch (IOException) { }   // it appeared meanwhile: leave it alone
        catch (Exception ex) { Log.Warn("settings: could not keep settings.pre-v3.json: " + ex.Message); }
    }

    private static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}
