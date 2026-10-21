using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clink.Services;

internal sealed class HotkeyBinding
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public int Vk { get; set; } = 0x78; // F9

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
    public string? MonitorDevice { get; set; }                 // "\\.\DISPLAY1"; null = primary
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 25;
    public string Codec { get; set; } = "h264";                // h264 | hevc
    public string Resolution { get; set; } = "native";         // native | 1440p | 1080p | 720p
    public int BufferSeconds { get; set; } = 120;
    public bool CaptureCursor { get; set; } = true;
    public HotkeyBinding Hotkey { get; set; } = new();
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
    public string RecordingMode { get; set; } = "game";
    /// <summary>Exe names (without .exe) that always count as a game, windowed or not.</summary>
    public List<string> CustomApps { get; set; } = new();
    /// <summary>Hotkey list. Missing from the file (v1) = migrate from <see cref="Hotkey"/> (one "clip" binding using the replay length); empty = no hotkeys, by choice.</summary>
    public List<HotkeyAction> Hotkeys { get; set; } = new();
    /// <summary>top-left | top | top-right | left | right | bottom-left | bottom | bottom-right (on the captured monitor).</summary>
    public string ToastPosition { get; set; } = "bottom-right";
    public ToastSetting ToastClip { get; set; } = new() { Enabled = true, Seconds = 7 };
    public ToastSetting ToastShared { get; set; } = new() { Enabled = true, Seconds = 4 };
    public ToastSetting ToastRecording { get; set; } = new() { Enabled = true, Seconds = 4 };
    /// <summary>Always-on status dot (green recording / amber waiting for a game / red error) in a corner of the captured monitor.</summary>
    public bool StatusDot { get; set; } = false;
    public string StatusDotPosition { get; set; } = "top-right";
    /// <summary>none | discord10 | discord50 | discord500: pick the bitrate so a clip of the replay length fits the limit.</summary>
    public string SizeTarget { get; set; } = "none";
    /// <summary>Archive (NAS / share / second drive): clips move there in the background after N days and/or when the local folder exceeds N GB (whichever first).</summary>
    public bool ArchiveEnabled { get; set; }
    public string? ArchivePath { get; set; }
    public int ArchiveAfterDays { get; set; } = 0;
    public double ArchiveWhenLocalExceedsGB { get; set; } = 0;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public string? PendingUpdateVersion { get; set; }          // written by the updater: version and SHA-256 of the download it verified in %LOCALAPPDATA%\Clink\updates;
    public string? PendingUpdateSha256 { get; set; }           // the file is hashed again against this before it is ever installed

    [JsonIgnore]
    public string? CopiumToken
    {
        get => Unprotect(CopiumTokenProtected);
        set => CopiumTokenProtected = Protect(value);
    }

    /// <summary>
    /// settings.json carried a <see cref="Hotkeys"/> key. An empty list is then the user's choice (every row removed) and is
    /// kept; only a file without the key (v1) gets the single <see cref="Hotkey"/> migrated into the list.
    /// </summary>
    [JsonIgnore] public bool HotkeysFromFile { get; private set; }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object s_saveGate = new();        // Save() is also called from the updater's background thread

    public static Settings Load()
    {
        Settings s;
        try
        {
            s = AtomicFile.ReadWithFallback(FilePath, Parse) ?? new Settings();
        }
        catch (Exception ex) { Log.Warn("Settings load failed: " + ex.Message); s = new Settings(); }
        s.Normalize();
        return s;
    }

    /// <summary>Deserializes one settings file and applies the migrations that need to know which keys the file had.</summary>
    private static Settings Parse(string json)
    {
        var s = JsonSerializer.Deserialize<Settings>(json, s_json) ?? new Settings();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                s.HotkeysFromFile = HasKey(doc.RootElement, nameof(Hotkeys));
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

    /// <summary>
    /// A hand-edited or merged settings.json can carry null where an object or list is expected ("Hotkeys": null); nothing
    /// downstream null-checks these, so they are given their defaults once here instead of crashing Start() half-way.
    /// </summary>
    private void Normalize()
    {
        Hotkey ??= new HotkeyBinding();
        Hotkeys ??= new List<HotkeyAction>();
        Hotkeys.RemoveAll(h => h == null);
        foreach (var h in Hotkeys) { h.Keys ??= new HotkeyBinding { Vk = 0 }; h.Action ??= "clip"; }
        CustomApps ??= new List<string>();
        CustomApps.RemoveAll(a => a == null);
        ToastClip ??= new ToastSetting { Enabled = true, Seconds = 7 };
        ToastShared ??= new ToastSetting { Enabled = true, Seconds = 4 };
        ToastRecording ??= new ToastSetting { Enabled = true, Seconds = 4 };
        Codec ??= "h264";
        Resolution ??= "native";
        GameProcess ??= "auto";
        VoiceApp ??= "auto";
        RecordingMode ??= "game";
        ToastPosition ??= "bottom-right";
        StatusDotPosition ??= "top-right";
        SizeTarget ??= "none";
        if (string.IsNullOrWhiteSpace(ClipsFolder)) ClipsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink");
    }

    public void Save()
    {
        try
        {
            lock (s_saveGate)
            {
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
