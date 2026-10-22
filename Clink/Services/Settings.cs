using System.Globalization;
using System.IO;
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

    public HotkeyBinding Clone() => new() { Ctrl = Ctrl, Alt = Alt, Shift = Shift, Win = Win, Vk = Vk };

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
    /// <summary>clip | pause | share-last</summary>
    public string Action { get; set; } = "clip";
    public int Seconds { get; set; } = 0;
}

internal sealed class ToastSetting
{
    public bool Enabled { get; set; } = true;
    public int Seconds { get; set; } = 5;
}

internal sealed class Settings
{
    /// <summary>The settings.json layout this version writes: 1 = v0.1 (one Hotkey), 2 = v0.2 (the Hotkeys list), 3 = v0.2.5 (the main hotkey first, Theme, Accent).</summary>
    public const int CurrentSchema = 3;
    /// <summary>
    /// Layout version of the file. A file without it is 2 when it has a Hotkeys list, else 1; saving writes at least
    /// <see cref="CurrentSchema"/>, and a number from a newer Clink is kept as it is.
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

    // ---- v3 (v0.2.5) ----
    /// <summary>dark | light</summary>
    public string Theme { get; set; } = SettingsDefaults.Theme;
    /// <summary>cyan | blue | violet | pink | orange | green</summary>
    public string Accent { get; set; } = SettingsDefaults.Accent;
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

    /// <summary>settings.json exists but could not be read (a hand edit with a typo), so Clink started from the backup or the defaults: a message for the user, or null.</summary>
    [JsonIgnore] public string? LoadProblem { get; private set; }

    /// <summary>Where the settings live instead of <c>%APPDATA%\Clink</c>: set by <c>--dev &lt;folder&gt;</c> and by the unit tests, null otherwise.</summary>
    internal static string? DirOverride { get; set; }
    public static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink");
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
        // Said at start (AppController.Start): a hand edit that does not parse must not just silently have no effect.
        if (unreadable != null) s.LoadProblem = $"settings.json was not used ({unreadable}): Clink started with {(fromBackup ? "the previous settings" : "default settings")}";
        s.Normalize();
        // A file of an older schema, or no usable file at all (a first start): the caller saves once, in the current layout.
        if (s._fileSchema < CurrentSchema) s.Migrated = true;
        s.SchemaVersion = Math.Max(s._fileSchema, CurrentSchema);
        return s;
    }

    /// <summary>Deserializes one settings file and applies the migrations that need to know which keys the file had.</summary>
    private static Settings Parse(string json)
    {
        var s = JsonSerializer.Deserialize<Settings>(json, s_json) ?? new Settings();
        s._fileSchema = 1;
        try
        {
            using var doc = JsonDocument.Parse(json, s_doc);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                s.HotkeysFromFile = HasKey(doc.RootElement, nameof(Hotkeys));
                s._fileSchema = SchemaOf(doc.RootElement);
                // v1 "ShowToast": false becomes the clip toast's switch when the file has no v2 toast setting yet.
                if (!s.ShowToast && !HasKey(doc.RootElement, nameof(ToastClip)))
                {
                    s.ToastClip ??= new ToastSetting { Seconds = 7 };
                    s.ToastClip.Enabled = false;
                    Log.Info("settings: migrated ShowToast=false → ToastClip.Enabled=false");
                }
            }
        }
        catch (Exception ex) { Log.Warn("settings: key scan: " + ex.Message); }
        return s;
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
        EnsureHotkeyModel();
    }

    /// <summary>
    /// Puts <see cref="Hotkeys"/> in the shape the rest of Clink relies on: <c>Hotkeys[0]</c> is the main hotkey, a "clip" with
    /// Seconds 0 (the full replay) that may be unset, and every later row is an additional hotkey. Runs on every load and does
    /// nothing to a list that is already in shape (the flags below are only ever raised, never cleared).
    /// <list type="number">
    /// <item>An empty list from a file without a Hotkeys key (v0.1, or a first start): the main is the <see cref="Hotkey"/> (Alt+C on a first start, the file's own key for v0.1).</item>
    /// <item>Otherwise the main is the first "clip" row with Seconds 0 that has keys, else the first one without; it moves to index 0 and keeps its keys. Nobody's binding changes what it does.</item>
    /// <item>No such row (every clip hotkey has a fixed length, or the list is empty): a main is inserted at index 0 and every other row stays as it is. It gets Alt+C (<see cref="MainHotkeyAssigned"/>) unless a row already uses those keys, then it is unset (<see cref="MainHotkeyNeedsSetup"/>).</item>
    /// <item>The main is forced to "clip" with Seconds 0, and <see cref="Hotkey"/> follows its keys when it has any.</item>
    /// </list>
    /// <see cref="Migrated"/> is raised when anything changed, so the caller saves once.
    /// </summary>
    internal void EnsureHotkeyModel()
    {
        bool changed = false;
        if (Hotkeys.Count == 0 && !HotkeysFromFile)
        {
            Hotkeys.Add(new HotkeyAction { Keys = Hotkey.Clone(), Action = "clip", Seconds = 0 });
            if (Hotkey.Vk <= 0) MainHotkeyNeedsSetup = true;   // a hand-edited v0.1 file with no key at all
            changed = true;
        }
        else
        {
            // Clink.Services.Hotkeys, not the Hotkeys property of this class.
            static bool FullReplayClip(HotkeyAction h) => global::Clink.Services.Hotkeys.IsClip(h.Action) && h.Seconds <= 0;
            int main = Hotkeys.FindIndex(h => FullReplayClip(h) && h.Keys.Vk > 0);
            if (main < 0) main = Hotkeys.FindIndex(FullReplayClip);
            if (main > 0)
            {
                var row = Hotkeys[main];
                Hotkeys.RemoveAt(main);
                Hotkeys.Insert(0, row);
                changed = true;
            }
            else if (main < 0)
            {
                // Alt+C unless one of the rows (a pause, a share, a shorter clip) is already on it: it must not be bound twice.
                var alt = SettingsDefaults.MainHotkey();
                bool taken = Hotkeys.Any(h => h.Keys.SameKeys(alt));
                Hotkeys.Insert(0, new HotkeyAction { Keys = taken ? new HotkeyBinding { Vk = 0 } : alt, Action = "clip", Seconds = 0 });
                if (taken) MainHotkeyNeedsSetup = true; else MainHotkeyAssigned = true;
                changed = true;
            }
        }
        var m = Hotkeys[0];
        if (m.Action != "clip") { m.Action = "clip"; changed = true; }
        if (m.Seconds != 0) { m.Seconds = 0; changed = true; }
        if (m.Keys.Vk > 0 && !Hotkey.SameKeys(m.Keys)) { Hotkey = m.Keys.Clone(); changed = true; }
        if (changed) Migrated = true;
    }

    public void Save()
    {
        try
        {
            lock (s_saveGate)
            {
                if (SchemaVersion < CurrentSchema) SchemaVersion = CurrentSchema;
                Directory.CreateDirectory(Dir);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, s_json));
            }
        }
        catch (Exception ex) { Log.Warn("Settings save failed: " + ex.Message); }
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
