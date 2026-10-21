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
    public string? AllDeviceId { get; set; }                   // render endpoint id for the "All audio" loopback; null = default output
    public string GameProcess { get; set; } = "auto";          // process name (no .exe) for the Game track, or "auto" = the fullscreen app on the captured monitor
    public string? MicDeviceId { get; set; }
    public string VoiceApp { get; set; } = "auto";
    public bool ClickSound { get; set; } = true;
    public double ClickVolume { get; set; } = 0.5;
    public string ClipsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink");
    public string? CopiumTokenProtected { get; set; }
    public bool StartMinimized { get; set; }
    public bool RunAtStartup { get; set; }
    public bool ShowToast { get; set; } = true;
    public float HdrKnee { get; set; } = 0.75f;
    public float HdrPeak { get; set; } = 4f;
    public float KeyframeSeconds { get; set; } = 1f;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool AutoUpdate { get; set; } = true;               // check GitHub on start + every 6 h, download new releases and install on next start; off = never contact GitHub unprompted
    public string? UpdateRepo { get; set; }                    // "owner/repo"; overrides the UpdateRepo baked into the exe at build time (validated, logged at start, shown in the version tooltip)
    public DateTime? LastUpdateCheck { get; set; }
    public string? PendingUpdateVersion { get; set; }          // written by the updater: version and SHA-256 of the download it verified in %LOCALAPPDATA%\Clink\updates;
    public string? PendingUpdateSha256 { get; set; }           // the file is hashed again against this before it is ever installed

    [JsonIgnore]
    public string? CopiumToken
    {
        get => Unprotect(CopiumTokenProtected);
        set => CopiumTokenProtected = Protect(value);
    }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clink");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object s_saveGate = new();        // Save() is also called from the updater's background thread

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), s_json) ?? new Settings();
        }
        catch (Exception ex) { Log.Warn("Settings load failed: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            lock (s_saveGate)
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, s_json));
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
