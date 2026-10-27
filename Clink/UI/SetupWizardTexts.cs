using Clink.Services;

namespace Clink.UI;

/// <summary>
/// Every word of the first-run setup in one place, so a test can check the limits (headings 32, labels 40, hints and warnings 80,
/// buttons 16 characters; paths excluded) and nothing is hard-coded in the window. Pure.
/// </summary>
internal static class SetupWizardTexts
{
    public const int MaxHeading = 32, MaxLabel = 40, MaxHint = 80, MaxButton = 16;
    /// <summary>The Done page's sentence is a short paragraph, not a hint.</summary>
    public const int MaxBody = 120;

    // ---- chrome
    public const string Title = "Set up Clink";
    public static string StepOf(int n, int m) => $"Step {n} of {m}";
    public const string Back = "Back", Next = "Next", Install = "Install", Skip = "Skip setup", Finish = "Finish", TryAgain = "Try again", OpenClink = "Open Clink";

    /// <summary>The header X: named for what it does (it finishes on the Done page, skips elsewhere).</summary>
    public const string CloseName = "Close setup", CloseNameSkips = "Close setup (same as Skip setup)";

    // ---- Location
    public const string LocationHeading = "Where should Clink live?";
    public static string RunningFrom(string place) => $"It is running from {place}.";
    public const string InstallForMeLabel = "Install for me (recommended)";
    public const string InstallForMeHint = "Puts Clink in your user programs folder. No admin rights needed.";
    public const string UseInstalledLabel = "Use the installed Clink (recommended)";
    public static string UseInstalledHint(string version) => $"Clink {version} is already installed for you. Setup opens it.";
    public const string UseInstalledHintNoVersion = "Clink is already installed for you. Setup opens it.";
    public const string DocumentsLabel = "Put it in Documents\\Clink";
    public const string DocumentsHint = "Handy if you like to find the file yourself.";
    public const string ProgramFilesLabel = "Install for all users";
    public const string ProgramFilesHint = "Needs administrator approval. Runs the Clink installer.";
    public const string KeepLabel = "Keep it here";
    public const string KeepInDocumentsLabel = "Keep it in Documents\\Clink";
    public const string KeepHint = "Nothing is moved. You can run setup again later.";
    public const string RecycleHint = "The copy here goes to the Recycle Bin once the new one starts.";
    public const string CannotInstallSelf = "This build cannot install itself.";
    public const string ProgramFilesUnavailable = "Not available in this version.";
    /// <summary>Why "Install for me" is off when the install folder already holds a copy the installer put there.</summary>
    public const string InstalledByInstaller = "Clink is already installed here by its installer.";

    public const string WarnTemp = "This folder is temporary. Clink may be deleted from here.";
    public const string WarnRemovable = "This is a removable drive. Clink stops when it is unplugged.";
    public const string WarnNetwork = "This is a network folder. Clink may start slowly or fail.";
    public const string WarnCloud = "This folder is synced to the cloud. Clink may go offline.";
    public const string WarnDownloads = "Downloads folders are often cleaned up.";
    public const string WarnDuplicate = "This looks like a second download of Clink.";

    public const string BusyInstalling = "Installing Clink…";
    public const string BusyStartingInstalled = "Starting the installed Clink…";
    public static string InstallFailed(string? error) => $"Clink could not be installed: {error}\nIt still runs from here.";
    public static string LaunchFailed(string? error) => $"The installed Clink could not be started: {error}\nThis copy still runs.";

    // ---- Quality
    public const string QualityHeading = "How should clips look?";
    public const string LightLabel = "Light", LightHint = "720p, 30 fps. Small files, easy on older PCs.";
    public const string BalancedLabel = "Balanced (recommended)", BalancedHint = "1080p, 60 fps.";
    public const string HighLabel = "High", HighHint = "1440p, 60 fps. Bigger files.";
    public const string CustomLabel = "Keep my settings";
    public static string CustomHint(string resolution, int fps, int mbps) => $"{resolution}, {fps} fps, {mbps} Mbps.";
    public const string ReplayHeading = "Keep the last";
    public static string ReplayKeep(int seconds) => $"Keep {SetupPresets.ReplayLabel(seconds)}";
    public const string ReplayHint = "Longer replays use more memory.";

    // ---- Hotkey
    public const string HotkeyHeading = "Save a clip with one key";
    public static string HotkeyBody(int replaySeconds) => $"Press it any time to save the last {SetupPresets.ReplayLabel(replaySeconds)}.";
    public const string HotkeyFooter = "Add more hotkeys later in Settings.";

    // ---- Startup
    public const string StartupHeading = "A few last things";
    public const string RunAtStartupLabel = "Start Clink when I sign in";
    public const string StartMenuLabel = "Add Clink to the Start menu";
    public const string DesktopLabel = "Add a desktop shortcut";
    // The same labels with their access key marked ("_" before the letter); a test checks they match the plain ones.
    public const string RunAtStartupAccess = "_Start Clink when I sign in";
    public const string StartMenuAccess = "Add Clink to the Start _menu";
    public const string DesktopAccess = "Add a _desktop shortcut";
    public const string ElevatedShortcutsNote = "Shortcuts are skipped while Clink runs as administrator.";

    // ---- Done
    public const string DoneHeading = "Clink is ready";
    public const string DoneTray = "Clink keeps running in the tray, next to the clock.";
    /// <summary>With clips off there is no hotkey to talk about: only where Clink lives.</summary>
    public static string DoneBody(string? hotkey, bool clipsEnabled = true) => !clipsEnabled ? DoneTray
        : string.IsNullOrEmpty(hotkey)
        ? "Set a hotkey in Settings to save clips."
        : $"Press {hotkey} to save a clip. {DoneTray}";

    // ---- places
    /// <summary>The "{place}" of "It is running from {place}." for a location; a plain path is trimmed in the middle (the window shows the full path as a tooltip).</summary>
    public static string Place(LocationClass location, string? path) => location switch
    {
        LocationClass.Downloads => "your Downloads folder",
        LocationClass.Desktop => "your Desktop",
        LocationClass.Documents => "your Documents folder",
        LocationClass.Temp => "a temporary folder",
        LocationClass.Removable => "a removable drive",
        LocationClass.Network => "a network folder",
        LocationClass.Cloud => "a cloud-synced folder",
        _ => TrimPath(path),
    };

    public static string TrimPath(string? path, int max = 48)
    {
        string p = path ?? "";
        if (p.Length <= max) return p;
        int head = max / 3, tail = max - head - 1;
        return p[..head] + "…" + p[^tail..];
    }

    /// <summary>The one warning for a location (null when there is none). A location warning outranks the duplicate-name one.</summary>
    public static string? WarningFor(LocationClass location, bool duplicateName) => location switch
    {
        LocationClass.Temp => WarnTemp,
        LocationClass.Removable => WarnRemovable,
        LocationClass.Network => WarnNetwork,
        LocationClass.Cloud => WarnCloud,
        LocationClass.Downloads => WarnDownloads,
        _ => duplicateName ? WarnDuplicate : null,
    };
}
