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
/// <param name="Package">The package family name: "Contoso.Notes_pzs8sxrjxfjjc".</param>
/// <param name="Folder">That copy's Clink folder: "...\Packages\&lt;Package&gt;\LocalCache\Roaming\Clink".</param>
/// <param name="SavedUtc">When its settings.json was last written.</param>
/// <param name="Differences">What taking it over would change, in the user's words ("video quality", "hotkeys"); empty until compared.</param>
internal sealed record ForeignSettings(string Package, string Folder, DateTime SavedUtc, IReadOnlyList<string> Differences)
{
    /// <summary>"Notes" for "Contoso.Notes_pzs8sxrjxfjjc".</summary>
    public string FriendlyName => AppDataRedirect.FriendlyNameOf(Package);
    public string SettingsPath => Path.Combine(Folder, "settings.json");
    public string ClipsPath => Path.Combine(Folder, "clips.json");
    /// <summary>"&lt;package&gt;|&lt;ticks of SavedUtc&gt;": what <see cref="Settings.ImportDeclined"/> holds once the user turned this copy down.</summary>
    public string DeclineKey => $"{Package}|{SavedUtc.Ticks}";
    /// <summary>"2026-10-21 14:32", local time.</summary>
    public string SavedLocalText => SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>What <see cref="SettingsImport.Import"/> did besides writing the settings.</summary>
/// <param name="BackupPath">Where the settings it replaced were kept (<c>settings.pre-import.json</c>, or the next free <c>settings.pre-import.N.json</c>); null when there were none.</param>
/// <param name="ClipsProblem">Why the clip lists were not merged; null when they were, or the other copy had no clips to merge.</param>
/// <param name="NetworkPathsKept">The folder settings (<c>ScreenshotsFolder</c>, <c>ClipsFolder</c>, <c>ArchivePath</c>) whose network path in the other copy was NOT taken over: ours stayed.</param>
internal sealed record ImportResult(string? BackupPath, string? ClipsProblem, IReadOnlyList<string>? NetworkPathsKept = null)
{
    /// <summary>"Settings imported; the clip list could not be merged: …", or null when it was merged.</summary>
    public string? ClipsNotice => ClipsProblem == null ? null : SettingsImport.ClipsNoticeFor(ClipsProblem);
    /// <summary>"Settings imported; the other copy's network folders were not taken over: …", or null when there were none.</summary>
    public string? NetworkPathsNotice => NetworkPathsKept is { Count: > 0 } kept ? SettingsImport.NetworkPathsNoticeFor(kept) : null;
}

/// <summary>
/// Windows keeps a private copy of %APPDATA% and %LOCALAPPDATA% for an app that runs from a package (MSIX): every write the app
/// or a process it starts makes there lands in <c>%LOCALAPPDATA%\Packages\&lt;package&gt;\LocalCache\{Roaming,Local}</c>, and every read
/// sees that copy first. A Clink started from such an app (an agent such an app runs, say) therefore keeps its settings
/// where a Clink started from Explorer or the Start menu never looks. <see cref="Detect"/> tells which one this process is.
/// </summary>
internal static class AppDataRedirect
{
    // \\?\C:\Users\x\AppData\Local\Packages\Contoso.Notes_pzs8sxrjxfjjc\LocalCache\Roaming\Clink\settings.json -> Contoso.Notes_pzs8sxrjxfjjc
    private static readonly Regex s_package = new(@"\\AppData\\Local\\Packages\\(?<pkg>[^\\]+)\\LocalCache\\(?:Roaming|Local)\\",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The package whose private AppData this process sees ("Contoso.Notes_pzs8sxrjxfjjc"), or null when it sees the real one.</summary>
    public static string? Package { get; private set; }
    /// <summary>"Notes": the package name without the publisher part; null when not redirected.</summary>
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
        Unknown = false;
        try
        {
            string dir = Settings.Dir;
            Directory.CreateDirectory(dir);
            string? folder = FinalFolderOf(dir);
            if (folder == null)
            {
                Unknown = true;
                Log.Warn($"settings: could not tell whether AppData is redirected ({dir})");
                return false;
            }
            string? package = PackageFromFinalPath(folder + "\\");
            if (package == null) return false;
            Package = package;
            FriendlyName = FriendlyNameOf(package);
            FinalFolder = folder;
            Log.Warn($"settings: {dir} is really {folder}: Windows keeps a private copy of AppData for {package}, and Clink started from Explorer or the Start menu does not see it");
            return true;
        }
        catch (Exception ex)
        {
            Unknown = true;
            Log.Warn($"settings: could not tell whether AppData is redirected ({Settings.Dir}): " + ex.Message);
            return false;
        }
    }

    /// <summary>Back to "not found yet". For the unit tests.</summary>
    internal static void Reset() { Package = FriendlyName = FinalFolder = null; Unknown = false; }

    /// <summary>
    /// True when the last <see cref="Detect"/> could not tell whether AppData is redirected (nothing could be opened, or it threw).
    /// The third state next to redirected and not redirected: <see cref="SettingsImport.CanTakeOverAtStart"/> then does not take a
    /// copy over by itself (offering it later, <see cref="SettingsImport.CanOffer"/>, is fine: the user decides).
    /// </summary>
    public static bool Unknown { get; private set; }

    /// <summary>The package a final path (<c>GetFinalPathNameByHandle</c> form) belongs to, or null when it is an ordinary path.</summary>
    public static string? PackageFromFinalPath(string finalPath)
    {
        var m = s_package.Match(finalPath ?? "");
        return m.Success ? m.Groups["pkg"].Value : null;
    }

    /// <summary>"Contoso.Notes_pzs8sxrjxfjjc" -> "Notes"; "Microsoft.WindowsTerminal_8wekyb3d8bbwe" -> "WindowsTerminal".</summary>
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
        nint h = CreateFileExt(probe, GENERIC_WRITE | DELETE, FILE_SHARE_ALL, 0, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_DELETE_ON_CLOSE, 0);
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

    /// <summary>
    /// Where <paramref name="path"/> really is once the junctions and symbolic links in it are followed: Windows' final path of the
    /// part of it that exists, with the rest (a folder that is yet to be created) added back. A <c>--dev</c> folder that is a
    /// junction into a packaged app's AppData, or into the real settings folder, is judged by that, not by what it is called. In the
    /// <c>C:\...</c> / <c>\\server\share\...</c> form, not <c>\\?\...</c>. Null when no part of it exists or Windows will not say; never throws.
    /// </summary>
    internal static string? ResolvedPath(string path)
    {
        try
        {
            string existing = Path.GetFullPath(path);
            var missing = new Stack<string>();
            while (!Directory.Exists(existing))
            {
                string? parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent)) return null;
                missing.Push(Path.GetFileName(existing));
                existing = parent;
            }
            string? final = FinalPathOfExisting(existing);
            if (final == null) return null;
            if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
            else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
            foreach (string part in missing) final = Path.Combine(final, part);   // a stack reads the part nearest the existing folder first
            return final;
        }
        catch (Exception) { return null; }
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
        nint h = CreateFileExt(path, 0, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h == InvalidHandle) return null;
        try { return FinalPathOf(h); }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// <paramref name="path"/> in the <c>\\?\C:\...</c> / <c>\\?\UNC\server\share\...</c> form, which CreateFileW takes past MAX_PATH even
    /// where LongPathsEnabled is off. A relative path is made absolute first (the \\?\ form is never relative and is not normalised by
    /// Windows); a path that already starts with <c>\\?\</c> or <c>\\.\</c> is returned as it is. An empty path is returned as it is.
    /// </summary>
    internal static string ExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (IsDeviceForm(path)) return path;
        string full = Path.GetFullPath(path);
        if (IsDeviceForm(full)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <summary>True when a ':' follows the drive (or share) part of the path: an alternate data stream ("C:\a\b:stream"), never a folder.</summary>
    internal static bool HasStreamColon(string path)
    {
        try
        {
            string full = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(full) ?? "";
            return full[root.Length..].Contains(':');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsDeviceForm(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>
    /// True for a path in the device / extended form (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>, <c>//?/</c>; this covers GLOBALROOT and
    /// Volume{GUID} too), as given and once <see cref="Path.GetFullPath(string)"/> has had its say. A data folder or index written like
    /// that is refused outright: its text says nothing about where it is. A path .NET cannot make sense of counts as one.
    /// </summary>
    internal static bool IsDevicePath(string path)
    {
        try
        {
            foreach (string p in new[] { path.Trim(), Path.GetFullPath(path.Trim()) })
            {
                string n = p.Replace('/', '\\');
                if (n.StartsWith(@"\\?\", StringComparison.Ordinal) || n.StartsWith(@"\\.\", StringComparison.Ordinal) || n.StartsWith(@"\??\", StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }   // the caller's own GetFullPath says "not a valid path"
    }

    private static nint CreateFileExt(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template)
    {
        string ext;
        try { ext = ExtendedPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return InvalidHandle; }
        return CreateFile(ext, access, share, security, disposition, flags | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, template);   // never lets a server impersonate us
    }

    private static string? FinalPathOf(nint handle)
    {
        // FILE_NAME_NORMALIZED (0) first; some file systems cannot normalise, so then FILE_NAME_OPENED (8) rather than "unknown".
        return FinalPathOf(handle, 0) ?? FinalPathOf(handle, FILE_NAME_OPENED);
    }

    private static string? FinalPathOf(nint handle, uint flags)
    {
        var sb = new StringBuilder(512);
        uint n = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, flags);   // VOLUME_NAME_DOS
        if (n > sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, flags);
        }
        return n == 0 || n > sb.Capacity ? null : sb.ToString();
    }

    private const uint FILE_NAME_OPENED = 0x8, SECURITY_SQOS_PRESENT = 0x00100000, SECURITY_IDENTIFICATION = 0x00010000;

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

    /// <summary>"4,000,000", the same on every machine (the log and the notices are read by people who speak to each other).</summary>
    private static string Bytes(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

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
                    if (!file.Exists || file.Length == 0) continue;
                    if (file.Length > MaxFileBytes)
                    {
                        Log.Warn($"import: the settings.json in {folder} is {Bytes(file.Length)} bytes, more than the {Bytes(MaxFileBytes)} that are read: skipped");
                        continue;
                    }
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
        ("alerts", new[] { nameof(Settings.ClickSound), nameof(Settings.ClickVolume), nameof(Settings.ShowToast), nameof(Settings.ToastPosition), nameof(Settings.ToastClip), nameof(Settings.ToastShared), nameof(Settings.ToastRecording), nameof(Settings.ToastScreenshot), nameof(Settings.StatusDot), nameof(Settings.StatusDotPosition) }),
        ("archive", new[] { nameof(Settings.ArchiveEnabled), nameof(Settings.ArchivePath), nameof(Settings.ArchiveAfterDays), nameof(Settings.ArchiveWhenLocalExceedsGB) }),
        ("clips folder", new[] { nameof(Settings.ClipsFolder) }),
        ("features", new[] { nameof(Settings.ClipsEnabled) }),
        ("library view", new[] { nameof(Settings.LibraryFeature), nameof(Settings.LibraryLevel) }),
        ("cloud sharing", new[] { nameof(Settings.CloudSharingEnabled) }),   // (only an incoming "off" counts: see KeepOurOwn / Differences)
        ("screenshots", new[] { nameof(Settings.ScreenshotsEnabled), nameof(Settings.ScreenshotsFolder), nameof(Settings.ScreenshotMode), nameof(Settings.ScreenshotCopyImage), nameof(Settings.ScreenshotLoupe), nameof(Settings.ScreenshotUpload), nameof(Settings.ScreenshotCopyLink) }),
        ("Copium token", Array.Empty<string>()),   // compared decrypted, below
        (UnusableTokenNote, Array.Empty<string>()),
        ("Copium+", new[] { nameof(Settings.CopiumPlus) }),
        ("startup", new[] { nameof(Settings.StartMinimized), nameof(Settings.RunAtStartup) }),
        ("updates", new[] { nameof(Settings.AutoUpdate) }),
        ("theme", new[] { nameof(Settings.Theme), nameof(Settings.Accent) }),
    };
    private const string OtherLabel = "other settings";
    /// <summary>Said in place of "Copium token" when the other copy holds a token that this user on this machine cannot decrypt: an import keeps ours.</summary>
    internal const string UnusableTokenNote = "the incoming Copium token cannot be used on this machine";

    /// <summary>
    /// The settings that belong to this installation and are never taken over: where the window sits, when updates were last looked
    /// for and from where, what the updater holds, and which copies of the settings were turned down. <see cref="Import"/> keeps
    /// ours (or leaves them out when we have none), and <see cref="Differences"/> ignores them, so an import never leaves a
    /// difference behind that would offer the same copy again.
    /// </summary>
    internal static bool IsOwnKey(string key) =>
        key.StartsWith("Window", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("PendingUpdate", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("FeedFloor", StringComparison.OrdinalIgnoreCase)   // the signed feed's anti-replay floors: another copy's must never lower or raise ours
        || key.Equals(nameof(Settings.LastUpdateCheck), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.UpdateRepo), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.UseDevBuilds), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.CopiumImageUploadUrl), StringComparison.OrdinalIgnoreCase)   // where the token is sent: this machine's own choice, never another copy's
        || key.Equals(nameof(Settings.ImportDeclined), StringComparison.OrdinalIgnoreCase)
        // consent and sharing controls: what this person agreed to on this machine is never another copy's to decide
        || key.Equals(nameof(Settings.CloudSharingEnabled), StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CopiumConsent", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CopiumNudge", StringComparison.OrdinalIgnoreCase)
        || key.Equals("CopiumAutoUploadNoticeShown", StringComparison.OrdinalIgnoreCase)   // S6's one-time notice: gone, but a settings.json written by S6 still carries it
        || key.Equals(nameof(Settings.ScreenshotEditorExcludeFromCapture), StringComparison.OrdinalIgnoreCase);

    private static readonly string[] s_folderKeys = { nameof(Settings.ScreenshotsFolder), nameof(Settings.ClipsFolder), nameof(Settings.ArchivePath) };

    /// <summary>
    /// Puts our value back in <paramref name="theirs"/> for each folder setting (<c>ScreenshotsFolder</c>, <c>ClipsFolder</c>,
    /// <c>ArchivePath</c>) where the other copy holds a network or device path that differs from ours: the same rule as
    /// <see cref="KeepOurOwn"/>, on loaded settings. Returns the names of the settings it kept.
    /// </summary>
    private static List<string> KeepOurNetworkPaths(Settings ours, Settings theirs)
    {
        var kept = new List<string>();
        void One(string name, string? mine, string? their, Action<string?> set)
        {
            if (!Screenshots.ScreenshotService.IsNetworkFolder(their)) return;
            if (string.Equals(mine?.Trim(), their!.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            set(mine);
            kept.Add(name);
        }
        One(nameof(Settings.ScreenshotsFolder), ours.ScreenshotsFolder, theirs.ScreenshotsFolder, v => theirs.ScreenshotsFolder = v);
        One(nameof(Settings.ClipsFolder), ours.ClipsFolder, theirs.ClipsFolder, v => theirs.ClipsFolder = v ?? ours.ClipsFolder);
        One(nameof(Settings.ArchivePath), ours.ArchivePath, theirs.ArchivePath, v => theirs.ArchivePath = v);
        return kept;
    }

    private static string FriendlyFolder(string key) => key switch
    {
        nameof(Settings.ScreenshotsFolder) => "screenshots folder",
        nameof(Settings.ClipsFolder) => "clips folder",
        nameof(Settings.ArchivePath) => "archive location",
        _ => key,
    };

    /// <summary>"Settings imported; a network folder in the other copy is never taken over silently, so your own stayed: screenshots folder, archive location".</summary>
    internal static string NetworkPathsNoticeFor(IReadOnlyList<string> kept) =>
        "Settings imported; a network folder in the other copy is never taken over silently, so your own stayed: " + string.Join(", ", kept.Select(FriendlyFolder))
        + " (set a network folder yourself under Settings if you want one)";

    internal static string NetworkPathsNoticePath => Path.Combine(Settings.Dir, "import-paths.notice");

    /// <summary>Said once at the next start (<c>AppController.Start</c>; an import restarts Clink, so it is kept in a file): which network folders an import did not take over.</summary>
    public static string? TakeNetworkPathsNotice()
    {
        try
        {
            string path = NetworkPathsNoticePath;
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            Log.Warn("import: the note about network folders cannot be read: " + ex.Message);
            return null;
        }
    }

    /// <summary>The settings hold a protected Copium token that cannot be decrypted here (another user's, or another machine's).</summary>
    internal static bool IsTokenUnusable(Settings s) => !string.IsNullOrEmpty(s.CopiumTokenProtected) && s.CopiumToken == null;

    // Differences that say nothing: this installation's own settings (above), the schema number, whether the audio banner was
    // dismissed, and the protected Copium token (its DPAPI text differs on every save: the token is compared decrypted).
    private static bool IsVolatile(string key) =>
        IsOwnKey(key)
        || key.Equals(nameof(Settings.SchemaVersion), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.AudioSetupSeen), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.SeededHotkeyDefaults), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.ScreenshotEditor), StringComparison.OrdinalIgnoreCase)   // the editor's last-used colour and width
        || key.Equals(nameof(Settings.CaptureBorderNoticeDismissed), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(Settings.CopiumTokenProtected), StringComparison.OrdinalIgnoreCase);

    /// <summary>A settings key that an import offer names in the user's words or ignores: anything else would be listed as "other settings".</summary>
    internal static bool HasLabelOrIsVolatile(string key) =>
        IsVolatile(key) || s_labels.Any(l => l.Keys.Contains(key, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// What taking <paramref name="theirJson"/> over would change in <paramref name="ourJson"/>, as labels in the user's words
    /// ("video quality", "replay length", "hotkeys", "audio", "alerts", "archive", "clips folder", "Copium token", "theme", ...).
    /// Both are read the way Clink loads settings (a missing key is its default, hotkeys are put in shape), so only a setting that
    /// would really change is listed. This installation's own settings (<see cref="IsOwnKey"/>: where the window sits,
    /// <c>LastUpdateCheck</c>, <c>UpdateRepo</c>, <c>UseDevBuilds</c>, <c>PendingUpdate*</c>, <c>ImportDeclined</c>), <c>AudioSetupSeen</c> and
    /// <c>SchemaVersion</c> are ignored. The Copium token is compared decrypted (the protected text differs on every save) and neither
    /// it nor any other value is ever logged or returned. A token the other copy holds that cannot be decrypted here (another
    /// user or machine) is no difference: an import keeps ours, and the list says so (<see cref="UnusableTokenNote"/>) instead of
    /// "Copium token", but only beside a difference that is worth an offer, so it is never an offer of its own.
    /// <paramref name="guard"/> is applied to the other copy once it is read (<see cref="ForeignGuard"/>).
    /// </summary>
    public static IReadOnlyList<string> Differences(string ourJson, string theirJson, Action<Settings>? guard = null)
    {
        var ours = Settings.FromJson(ourJson);
        var theirs = Settings.FromJson(theirJson);
        guard?.Invoke(theirs);
        // The consent is ours (IsOwnKey): the automatic upload is judged against it, not against the other copy's yes.
        theirs.CopiumConsent = ours.CopiumConsent;
        if (!theirs.CopiumConsent) theirs.ScreenshotUpload = false;
        KeepOurNetworkPaths(ours, theirs);   // as an import would: a network folder is not taken over, so it is no difference to offer
        var a = ours.ToElement().EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var b = theirs.ToElement().EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>();
        foreach (string key in a.Keys.Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (key.Equals(nameof(Settings.CloudSharingEnabled), StringComparison.OrdinalIgnoreCase))
            {
                // only an incoming "off" over our "on" is a change an import would make (see KeepOurOwn)
                if (ours.CloudSharingEnabled && !theirs.CloudSharingEnabled) labels.Add("cloud sharing");
                continue;
            }
            if (IsVolatile(key)) continue;
            if (a.TryGetValue(key, out var x) && b.TryGetValue(key, out var y) && JsonSame(x, y)) continue;
            labels.Add(s_labels.FirstOrDefault(l => l.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)).Label ?? OtherLabel);
        }
        if (IsTokenUnusable(theirs)) { if (labels.Count > 0) labels.Add(UnusableTokenNote); }
        else if (!string.Equals(ours.CopiumToken ?? "", theirs.CopiumToken ?? "", StringComparison.Ordinal)) labels.Add("Copium token");
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

    /// <summary>Offers <paramref name="copy"/> again at once, as <see cref="Pending"/>: an import of it was taken back (the new Clink could not be started).</summary>
    internal static void Offer(ForeignSettings copy) => SetPending(copy);

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
    /// Replaces the settings with <paramref name="copy"/>'s: its settings.json is written over this one (atomically) and its clip
    /// index is merged into this one. Throws when the copy cannot be read, or the current settings cannot be kept first, before
    /// anything is changed. What is replaced is kept first, never over an earlier copy: settings.pre-import.json, then
    /// settings.pre-import.1.json, .2.json, and so on. This installation's own values stay (<see cref="IsOwnKey"/>: the window's
    /// place, the updater's state, which copies were turned down), and so does our Copium token when the copy's cannot be decrypted
    /// here. The clip lists are merged as <see cref="TryMergeClips"/> says; when that does not work the settings are imported all
    /// the same, the reason is in the result and in <see cref="ClipsNotice"/>, and a marker makes every later start try again
    /// (<see cref="RetryPendingClipMerge"/>). The running Clink has to restart to use the result (<c>AppController.ImportForeignSettings</c>
    /// freezes the settings and the clip index first, and restarts after).
    /// </summary>
    public static ImportResult Import(ForeignSettings copy)
    {
        string theirs = ReadCapped(copy.SettingsPath) ?? throw new IOException("settings.json is missing or too big");
        var incoming = Settings.FromJson(theirs);   // throws when it is not JSON, or not a JSON object

        string dir = Settings.Dir;
        Directory.CreateDirectory(dir);
        var networkKept = new List<string>();
        string text = KeepOurOwn(theirs, ReadOurSettings(), keepOurToken: IsTokenUnusable(incoming), networkKept);
        string? backup = File.Exists(Settings.FilePath) ? KeepCurrentSettings(dir) : null;   // throws when it cannot be kept: nothing is changed yet
        AtomicFile.WriteAllText(Settings.FilePath, text);

        if (networkKept.Count > 0)
        {
            string note = NetworkPathsNoticeFor(networkKept);
            Log.Warn("import: " + note);
            try { File.WriteAllText(NetworkPathsNoticePath, note); }
            catch (Exception ex) { Log.Warn("import: could not note the network folders that were kept: " + ex.Message); }
        }
        string? clipsProblem = MergeClipIndex(copy.Folder);
        if (Pending?.DeclineKey == copy.DeclineKey) SetPending(null);
        Log.Info($"import: took over the settings saved {copy.SavedLocalText} inside {copy.Package}"
            + (copy.Differences.Count > 0 ? $" (changes: {string.Join(", ", copy.Differences)})" : "")
            + (backup != null ? $"; the replaced settings are kept as {Path.GetFileName(backup)}" : "")
            + (clipsProblem != null ? "; the clip list was not merged" : ""));
        return new ImportResult(backup, clipsProblem, networkKept);
    }

    // ------------------------------------------------------------------ what an import keeps of ours

    private static readonly JsonDocumentOptions s_lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The text of our settings.json (its .bak when that does not read), or null when there is none that is a JSON object.</summary>
    private static string? ReadOurSettings()
    {
        try
        {
            return AtomicFile.ReadWithFallback(Settings.FilePath, text =>
            {
                using var doc = JsonDocument.Parse(text, s_lenient);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("not a JSON object");
                return text;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("import: the current settings cannot be read, so none of their values are kept back: " + ex.Message);
            return null;
        }
    }

    private static KeyValuePair<string, JsonNode?>? Find(JsonObject? obj, string key)
    {
        if (obj == null) return null;
        foreach (var p in obj) if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>
    /// <paramref name="theirs"/> (a settings.json) with this installation's own values put back from <paramref name="ours"/>: every
    /// key <see cref="IsOwnKey"/> says is ours is taken from ours, or left out when ours has none. With
    /// <paramref name="keepOurToken"/> the Copium token is ours too. The text comes back as it was when none of that changes anything.
    /// </summary>
    internal static string KeepOurOwn(string theirs, string? ours, bool keepOurToken, List<string>? networkPathsKept = null)
    {
        if (JsonNode.Parse(theirs, documentOptions: s_lenient) is not JsonObject t) return theirs;
        JsonObject? o = null;
        if (ours != null)
        {
            try { o = JsonNode.Parse(ours, documentOptions: s_lenient) as JsonObject; }
            catch (JsonException) { }
        }
        string before = t.ToJsonString();

        foreach (string key in t.Select(p => p.Key).Where(IsOwnKey).ToList())
        {
            // The kill switch is the one own key that can be tightened by a copy: an incoming "off" (an admin pre-provisioning a work
            // machine) is kept, an incoming "on" never is (that needs the confirmation on this machine).
            if (key.Equals(nameof(Settings.CloudSharingEnabled), StringComparison.OrdinalIgnoreCase)
                && t[key] is JsonValue incoming && incoming.TryGetValue<bool>(out bool incomingOn) && !incomingOn) continue;
            if (Find(o, key) is { } mine) t[key] = mine.Value?.DeepClone();   // in place: the order of the keys stays theirs
            else t.Remove(key);
        }
        if (o != null)
            foreach (var p in o.ToList().Where(p => IsOwnKey(p.Key)))
                if (Find(t, p.Key) == null) t[p.Key] = p.Value?.DeepClone();

        // A network folder (UNC, device path) in the other copy is not taken over silently: ours stays (or the default, when we have none).
        foreach (string key in t.Select(p => p.Key).Where(k => s_folderKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            if (t[key] is not JsonValue v || !v.TryGetValue<string>(out var value) || !Screenshots.ScreenshotService.IsNetworkFolder(value)) continue;
            var mine = Find(o, key);
            if (mine is { Value: JsonValue mv } && mv.TryGetValue<string>(out var ourValue) && string.Equals(ourValue?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase)) continue;   // the same as ours: nothing to refuse
            if (mine is { Value: not null }) t[key] = mine.Value.Value!.DeepClone(); else t.Remove(key);
            networkPathsKept?.Add(s_folderKeys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)));
        }

        // The consent stays ours (above), so the automatic upload does not arrive switched on without our yes.
        bool ourConsent = Find(o, nameof(Settings.CopiumConsent)) is { Value: JsonValue cv } && cv.TryGetValue<bool>(out bool yes) && yes;
        if (!ourConsent && Find(t, nameof(Settings.ScreenshotUpload)) is { } su && su.Value is JsonValue sv && sv.TryGetValue<bool>(out bool sUp) && sUp)
            t[su.Key] = false;

        if (keepOurToken)
        {
            const string token = nameof(Settings.CopiumTokenProtected);
            foreach (string key in t.Select(p => p.Key).Where(k => k.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList()) t.Remove(key);
            if (Find(o, token) is { Value: not null } mine) t[mine.Key] = mine.Value!.DeepClone();
        }
        string after = t.ToJsonString();
        return after == before ? theirs : t.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Copies the current settings.json to the first free of settings.pre-import.json, settings.pre-import.1.json, .2.json, ...: a
    /// copy of what an earlier import replaced is never overwritten. Returns the path; throws when none could be made.
    /// </summary>
    private static string KeepCurrentSettings(string dir)
    {
        for (int n = 0; n < 1000; n++)
        {
            string path = Path.Combine(dir, n == 0 ? "settings.pre-import.json" : $"settings.pre-import.{n}.json");
            if (File.Exists(path)) continue;
            try
            {
                File.Copy(Settings.FilePath, path, overwrite: false);
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }   // made by someone else meanwhile: the next name
            catch (Exception ex) { throw new IOException("the current settings could not be kept first: " + ex.Message, ex); }
        }
        throw new IOException($"there are too many settings.pre-import files in {dir}");
    }

    // ------------------------------------------------------------------ merging the clip lists

    /// <summary>"Settings imported; the clip list could not be merged: …".</summary>
    internal static string ClipsNoticeFor(string problem) => "Settings imported; the clip list could not be merged: " + problem;

    private static string? s_clipsNotice;

    /// <summary>
    /// Said once to the user (<c>AppController.Start</c>): the settings were imported but the clip list could not be merged, and why.
    /// Set by an import that did not merge, and by the first retry that does not (<see cref="RetryPendingClipMerge"/>).
    /// </summary>
    public static string? TakeClipsNotice() => Interlocked.Exchange(ref s_clipsNotice, null);

    internal sealed record ClipsMarker(string From, string Reason, int Attempts);

    /// <summary>"clips merge pending from &lt;folder&gt;": what a start looks for to finish a merge that did not work.</summary>
    internal static string ClipsMarkerPath => Path.Combine(Settings.Dir, "clips-merge.pending");

    private static ClipsMarker? ReadClipsMarker()
    {
        try
        {
            if (!File.Exists(ClipsMarkerPath)) return null;
            var marker = JsonSerializer.Deserialize<ClipsMarker>(File.ReadAllText(ClipsMarkerPath));
            if (marker != null && !string.IsNullOrWhiteSpace(marker.From)) return marker;
        }
        catch (Exception ex) { Log.Warn("import: the note about a pending clip merge cannot be read: " + ex.Message); }
        ForgetPendingClipMerge();
        return null;
    }

    private static void WriteClipsMarker(ClipsMarker marker)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(ClipsMarkerPath, JsonSerializer.Serialize(marker));
        }
        catch (Exception ex) { Log.Warn("import: could not note the pending clip merge: " + ex.Message); }
    }

    /// <summary>Drops the note about a clip merge still to do (it is done, there is nothing to merge from any more, or the import was taken back).</summary>
    internal static void ForgetPendingClipMerge()
    {
        try { if (File.Exists(ClipsMarkerPath)) File.Delete(ClipsMarkerPath); }
        catch (Exception ex) { Log.Warn("import: could not remove the note about a pending clip merge: " + ex.Message); }
    }

    /// <summary>The pending clip merge, if any: the folder it merges from and why it has not worked yet. For the unit tests.</summary>
    internal static (string From, string Reason, int Attempts)? PendingClipMerge()
    {
        var m = ReadClipsMarker();
        return m == null ? null : (m.From, m.Reason, m.Attempts);
    }

    /// <summary>Merges <paramref name="folder"/>'s clip list into ours and keeps the marker in step. Returns why it could not, or null. Never throws.</summary>
    private static string? MergeClipIndex(string folder)
    {
        string? problem = TryMergeClips(folder);
        if (problem == null) { ForgetPendingClipMerge(); return null; }
        WriteClipsMarker(new ClipsMarker(folder, problem, 1));
        Interlocked.Exchange(ref s_clipsNotice, ClipsNoticeFor(problem));
        return problem;
    }

    /// <summary>
    /// Called at every start, before the clip index is read, and independent of whether the settings still differ from any other
    /// copy's: a merge that did not work (<see cref="Import"/> left a marker) is tried again from the folder the marker names. Done
    /// means the marker is gone. A copy that has no clip list any more ends it too. The first retry that fails again says so
    /// (<see cref="TakeClipsNotice"/>, the import's own notice having died with the process that restarted); later ones only log.
    /// </summary>
    public static void RetryPendingClipMerge()
    {
        var marker = ReadClipsMarker();
        if (marker == null) return;
        if (!File.Exists(Path.Combine(marker.From, "clips.json")))
        {
            Log.Info($"import: {marker.From} has no clip list any more: no clip merge left to finish");
            ForgetPendingClipMerge();
            return;
        }
        string? problem = TryMergeClips(marker.From);
        if (problem == null)
        {
            Log.Info($"import: finished the clip merge from {marker.From} that was pending");
            ForgetPendingClipMerge();
            return;
        }
        if (marker.Attempts <= 1) Interlocked.Exchange(ref s_clipsNotice, ClipsNoticeFor(problem));
        WriteClipsMarker(marker with { Reason = problem, Attempts = marker.Attempts + 1 });
    }

    /// <summary>
    /// Merges the clip list in <paramref name="folder"/> into ours (<see cref="MergeClips"/>) and writes it. Returns null when that
    /// is done, or when the other copy has no clip list; otherwise the reason it was not (also logged), and nothing is changed:
    /// the other copy's list is too big (<see cref="MaxFileBytes"/>) or does not read, ours does not read (the .bak is tried before
    /// giving up on it), the two cannot be combined, or ours cannot be written. The clips folder is scanned at every start anyway,
    /// so a clip is never lost, only its link, game and length. Never throws.
    /// </summary>
    internal static string? TryMergeClips(string folder)
    {
        static string Skipped(string reason)
        {
            Log.Warn("import: the clip list was not merged: " + reason);
            return reason;
        }

        string theirsPath = Path.Combine(folder, "clips.json");
        string theirs;
        try
        {
            var info = new FileInfo(theirsPath);
            if (!info.Exists) { Log.Info("import: the other copy has no clip list: nothing to merge"); return null; }
            if (info.Length > MaxFileBytes) return Skipped($"the other copy's clip list is too big ({Bytes(info.Length)} bytes; more than {Bytes(MaxFileBytes)} is not read)");
            theirs = File.ReadAllText(theirsPath);
            _ = ClipObjects(theirs, null, "");
        }
        catch (Exception ex) { return Skipped($"the other copy's clip list cannot be read ({Describe(ex)})"); }

        string mine = ClipLibrary.IndexPathOverride ?? Path.Combine(Settings.Dir, "clips.json");
        string? ours;
        try { ours = AtomicFile.ReadWithFallback(mine, text => { _ = ClipObjects(text, null, ""); return text; }); }
        catch (Exception ex) { return Skipped($"your clip list cannot be read, nor its backup ({Describe(ex)})"); }

        string merged;
        var left = new List<string>();
        try { merged = MergeClips(ours, theirs, left); }
        catch (Exception ex) { return Skipped($"the clip lists could not be combined ({Describe(ex)})"); }
        foreach (string reason in left) Log.Info("import: clip list: " + reason);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mine) ?? Settings.Dir);
            AtomicFile.WriteAllText(mine, merged);
        }
        catch (Exception ex) { return Skipped($"your clip list cannot be written ({Describe(ex)})"); }
        return null;
    }

    private static string Describe(Exception ex) => ex switch
    {
        JsonException { LineNumber: long line } => $"not valid JSON: error on line {line + 1}",
        _ => ex.Message,
    };

    /// <summary>
    /// The clip index of this Clink and another copy's as one: every clip once (by path), ours first. Paths are compared as the
    /// file system would (<see cref="NormalizePath"/>: slashes either way, <c>.</c> and <c>..</c>, a trailing separator, the
    /// <c>\\?\</c> prefix, upper or lower case); a short 8.3 name or a mapped drive for a network path stays a different path.
    /// Where both know a clip, ours is kept and the other copy only fills what ours leaves empty: the Copium link, page and id, when
    /// it was archived, its audio tracks, its length and size, whether it is a trim and what it was cut from. Fields this version
    /// does not know are carried along. Both texts are JSON arrays of clips as ClipLibrary writes them; <paramref name="ours"/> may
    /// be null. What is left out is added to <paramref name="skipped"/>, one sentence each: an entry that is no clip, one without a
    /// path (theirs), a path listed twice (ours).
    /// </summary>
    internal static string MergeClips(string? ours, string theirs, List<string>? skipped = null)
    {
        var merged = new JsonArray();
        var byPath = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        int twice = 0, noPath = 0;
        foreach (var clip in ClipObjects(ours, skipped, "your clip list"))
        {
            string? key = KeyOf(clip);
            if (key == null) merged.Add(clip.DeepClone());   // our own entry with no path: kept as it is
            else if (byPath.ContainsKey(key)) twice++;
            else { var mine = (JsonObject)clip.DeepClone(); byPath[key] = mine; merged.Add(mine); }
        }
        if (twice > 0) skipped?.Add($"your clip list names {twice} clip{(twice == 1 ? "" : "s")} twice: the repeats are dropped");
        foreach (var clip in ClipObjects(theirs, skipped, "the other copy's clip list"))
        {
            string? key = KeyOf(clip);
            if (key == null) { noPath++; continue; }
            if (byPath.TryGetValue(key, out var mine))
            {
                foreach (var field in clip)
                    if (field.Value != null && IsEmpty(mine[field.Key]) && !IsEmpty(field.Value)) mine[field.Key] = field.Value.DeepClone();
            }
            else { var copy = (JsonObject)clip.DeepClone(); byPath[key] = copy; merged.Add(copy); }
        }
        if (noPath > 0) skipped?.Add($"the other copy's clip list has {noPath} entr{(noPath == 1 ? "y" : "ies")} without a path: left out");
        return merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The clips of a clip index text (a JSON array; empty text is no clips). Throws when it is not a JSON array. An entry that
    /// is not a clip (a number, a list) is left out and, when <paramref name="skipped"/> is given, said so there ("whose").
    /// </summary>
    private static List<JsonObject> ClipObjects(string? json, List<string>? skipped, string whose)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<JsonObject>();
        var node = JsonNode.Parse(json, documentOptions: s_lenient);
        if (node is not JsonArray array) throw new JsonException("a clip index is a JSON array");
        var clips = array.OfType<JsonObject>().ToList();
        int notClips = array.Count - clips.Count;
        if (notClips > 0) skipped?.Add($"{whose} has {notClips} entr{(notClips == 1 ? "y" : "ies")} that {(notClips == 1 ? "is" : "are")} not a clip: left out");
        return clips;
    }

    /// <summary>The clip's path as a comparison key (<see cref="NormalizePath"/>), or null when it has none.</summary>
    private static string? KeyOf(JsonObject clip)
    {
        var path = clip.FirstOrDefault(p => p.Key.Equals("Path", StringComparison.OrdinalIgnoreCase)).Value;
        return path is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? NormalizePath(s) : null;
    }

    /// <summary>
    /// A path as the file system sees it, for comparing two clip lists: slashes either way are one, <c>.</c> and <c>..</c> are
    /// resolved, a trailing separator and the <c>\\?\</c> (and <c>\\?\UNC\</c>) prefix go. A path that cannot be resolved is compared
    /// as it is, with backslashes. The case is the caller's business (the keys are compared ignoring it).
    /// </summary>
    internal static string NormalizePath(string path)
    {
        string p = path.Trim();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        p = p.Replace('/', '\\');
        try { p = Path.GetFullPath(p); } catch (Exception) { }
        string root = Path.GetPathRoot(p) ?? "";
        return p.Length > root.Length ? p.TrimEnd('\\') : p;
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

    /// <summary>
    /// The text of a file of at most <see cref="MaxFileBytes"/>; null when it is not there, or too big (said in the log: a file
    /// that big is not Clink's), or cannot be read (also said).
    /// </summary>
    internal static string? ReadCapped(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > MaxFileBytes)
            {
                Log.Warn($"import: {Path.GetFileName(path)} in {Path.GetDirectoryName(path)} is {Bytes(info.Length)} bytes, more than the {Bytes(MaxFileBytes)} that are read: skipped");
                return null;
            }
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"import: cannot read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The clips folder and the archive location a settings.json names, as Clink would load them (the default clips folder when it
    /// names none); (null, null) when there is no such file or it does not read. Nothing is written or logged.
    /// </summary>
    internal static (string? Clips, string? Archive) SavedLocations(string settingsPath)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaxFileBytes) return (null, null);
            var s = Settings.FromJson(File.ReadAllText(settingsPath));
            return (s.ClipsFolder, s.ArchivePath);
        }
        catch (Exception) { return (null, null); }
    }

    // ------------------------------------------------------------------ when it is done

    /// <summary>
    /// A start with no settings of its own yet (<paramref name="freshStore"/>) takes over the newest copy another Clink saved inside
    /// a packaged app's private AppData (<see cref="ImportNewest"/>), unless this process is itself inside such an app (what it
    /// would find is its own kind of copy), and unless it is a <c>--dev</c> instance without a test packages root
    /// (<see cref="PackagesRoot"/>): a test never takes over the settings of the person whose machine it runs on.
    /// </summary>
    internal static bool CanTakeOverAtStart(bool freshStore, bool isDev) =>
        freshStore && AppDataRedirect.Package == null && !AppDataRedirect.Unknown && (!isDev || PackagesRoot != null);

    /// <summary>
    /// A start with settings of its own that were not just taken over from a copy looks for other copies to offer
    /// (<see cref="Scan"/>), on the same terms as <see cref="CanTakeOverAtStart"/>: not from inside a packaged app, and a
    /// <c>--dev</c> instance only where <see cref="PackagesRoot"/> says.
    /// </summary>
    internal static bool CanOffer(Settings settings, bool isDev) =>
        AppDataRedirect.Package == null && settings.LoadedFromFile && settings.ImportedFrom == null && (!isDev || PackagesRoot != null);

    /// <summary>Back to a fresh start: no test root, nothing pending, nothing to say. For the unit tests.</summary>
    internal static void Reset()
    {
        PackagesRoot = null;
        ForeignGuard = null;
        Interlocked.Exchange(ref s_pending, null);
        Interlocked.Exchange(ref s_clipsNotice, null);
    }
}
