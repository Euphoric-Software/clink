using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Clink.Services;

/// <summary>
/// <c>Clink.exe --dev [folder] [--dev-hotkeys] [--dev-record] [--dev-token]</c> (also <c>--dev=&lt;folder&gt;</c>): a second Clink for
/// testing, next to the real one. It keeps everything in its own folder (settings, clip index, log, clips; default
/// <c>%TEMP%\clink-dev</c>) and has its own single-instance mutex and control pipe, so it never forwards a command to the real Clink,
/// and the real one never sees its commands. It has no updater and no Run-key entry; it binds no hotkeys unless
/// <c>--dev-hotkeys</c> is given, does not record (it starts paused: it would record next to the user's game) unless
/// <c>--dev-record</c> is given, and has no Copium token unless <c>--dev-token</c> is given. Whatever is in its settings file,
/// <see cref="Isolate"/> keeps it away from the real clips, archive, updates and account on every start. Not in <c>--help</c>.
/// <c>--dev-quiet</c> (see <see cref="QuietMode"/>) keeps the instance out of the way of the person at the PC (no focus, nothing on screen, no sound, no capture
/// unless <c>--dev-quiet-record</c>); like every dev switch, it and <c>--dev-quiet-record</c> are an error without <c>--dev</c>.
/// <c>--dev-no-marker</c> and <c>--dev-demo-status &lt;game&gt;</c> (documentation screenshots, see tools/screenshots) additionally need <c>--dev-quiet</c>.
/// Two more switches exist only for testing how settings are taken over from another copy of Clink (SettingsImport), which a
/// <c>--dev</c> instance otherwise never looks for: <c>--packages-root &lt;folder&gt;</c> names a fake <c>%LOCALAPPDATA%\Packages</c> to
/// look in (never the real one), and <c>--dev-accept-import</c> accepts what is offered there at once.
/// Every <c>--dev*</c> switch is checked, and a mistake (an unknown one, one without <c>--dev</c>, a switch that only looks like one
/// because of a different dash, a slash or a stray space, a folder that is the real Clink's or holds any of it) is an error before
/// anything is started (exit code 2, before the single-instance mutex): a typo must not turn into a normal start that talks to the
/// real Clink.
/// </summary>
internal sealed record DevOptions(string Folder, bool BindHotkeys)
{
    /// <summary>Hidden <c>--packages-root &lt;folder&gt;</c>: the folder holding the package folders, in place of <c>%LOCALAPPDATA%\Packages</c>.</summary>
    public string? PackagesRoot { get; init; }
    /// <summary>Hidden <c>--dev-accept-import</c>: import the offered copy of the settings without asking, then restart.</summary>
    public bool AcceptImport { get; init; }
    /// <summary><c>--dev-token</c>: keep the Copium token the settings hold (otherwise the instance has none, and cannot share to the real account).</summary>
    public bool AllowToken { get; init; }
    /// <summary><c>--dev-record</c>: do not start paused.</summary>
    public bool Record { get; init; }
    /// <summary><c>--dev-quiet</c> (with <c>--dev</c>): a test instance that never gets in the way of the person at the PC (<see cref="QuietMode"/>).</summary>
    public bool Quiet { get; init; }
    /// <summary><c>--dev-quiet-record</c>: a quiet instance records after all (otherwise it starts paused, whatever <c>--dev-record</c> says).</summary>
    public bool QuietRecord { get; init; }
    /// <summary><c>--dev-no-marker</c> (with <c>--dev</c> and <c>--dev-quiet</c>): the TEST frame, badge and title text are not drawn (documentation screenshots).</summary>
    public bool NoMarker { get; init; }
    /// <summary><c>--dev-demo-status "game"</c> (with <c>--dev</c> and <c>--dev-quiet</c>): the header shows "Recording · game" without recording anything (a display override only).</summary>
    public string? DemoStatus { get; init; }

    /// <summary>Where a bare <c>--dev</c> keeps its data.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "clink-dev");

    private const string Dev = "--dev", DevEquals = "--dev=", Hotkeys = "--dev-hotkeys", Token = "--dev-token", Record_ = "--dev-record", Quiet_ = "--dev-quiet", QuietRecord_ = "--dev-quiet-record",
        NoMarker_ = "--dev-no-marker", DemoStatus_ = "--dev-demo-status", AcceptImport_ = "--dev-accept-import", PackagesRoot_ = "--packages-root";

    private static bool Is(string arg, string name) => arg.Equals(name, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ look-alike switches

    /// <summary>
    /// Every dash a keyboard, a word processor or a web page can produce, as the hyphen-minus: U+2010..U+2015 (hyphen, non-breaking
    /// hyphen, figure dash, en dash, em dash, horizontal bar), U+2212 (minus sign) and the small and full-width forms U+FE58, U+FE63, U+FF0D.
    /// </summary>
    private static bool IsDash(char c) => c is >= (char)0x2010 and <= (char)0x2015 or (char)0x2212 or (char)0xFE58 or (char)0xFE63 or (char)0xFF0D;

    /// <summary>An argument as a person reads it: invisible characters (zero-width spaces, soft hyphens, control codes) dropped, every dash a '-', blanks at both ends trimmed.</summary>
    private static string Plain(string arg)
    {
        var sb = new StringBuilder(arg.Length);
        foreach (char c in arg)
        {
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Control) continue;
            sb.Append(IsDash(c) ? '-' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>The name of an argument without what is put in front of it: "--dev", "-dev", "---dev", "/dev", " --dev", "\"--dev\"" and any of these with an en or em dash for a hyphen are all "dev".</summary>
    private static string Normalized(string arg) => Plain(arg).TrimStart(' ', '-', '/', '"', '\'');

    /// <summary>
    /// The argument is, or is meant to be, a dev switch: whatever is put in front of it, its name starts with "dev" or "packages-root".
    /// Used on what is not exactly one of the switches: such an argument is a mistake, never something to pass over (a normal
    /// start would take the real Clink's mutex or forward a command to it).
    /// </summary>
    private static bool LooksLikeDevSwitch(string arg)
    {
        string name = Normalized(arg);
        return name.StartsWith("dev", StringComparison.OrdinalIgnoreCase) || name.StartsWith("packages-root", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An argument that can be the value of a switch: not blank, not another switch (with any kind of dash), not a dev switch spelled wrongly.</summary>
    private static bool IsValue(string arg)
    {
        string plain = Plain(arg);
        return plain.Length > 0 && plain[0] != '-' && !LooksLikeDevSwitch(arg);
    }

    /// <summary>An argument for an error message, with anything that is not plain ASCII written as \uXXXX (a dash that looks like another, an invisible character).</summary>
    private static string Shown(string arg)
    {
        var sb = new StringBuilder("'");
        foreach (char c in arg)
        {
            if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    private static string Unquote(string value) => value.Trim().Trim('"');

    /// <summary>
    /// The options of the <c>--dev</c> switches anywhere on the command line, or null for a normal start (no dev switch at all).
    /// The folder is the argument right after <c>--dev</c> (unless that is another switch: the default folder then), or what follows
    /// <c>--dev=</c>. Throws <see cref="ArgumentException"/>, with a message for the person who typed the command line, for: an unknown
    /// <c>--dev…</c> switch, <c>--dev</c> twice, <c>--dev=</c> with nothing after it, a dev switch (or <c>--packages-root</c>) without
    /// <c>--dev</c>, a folder that is not a valid path or that is not safe for a dev instance (<see cref="RefuseUnsafeFolder"/>), and
    /// a packages root that is the real one. Every argument whose name, once the dashes, slashes, quotes, blanks and invisible
    /// characters in front of it are taken away (<c>-dev</c>, <c>---dev</c>, <c>/dev</c>, an en or em dash, <c>" --dev"</c>), starts with
    /// "dev" or "packages-root" must be exactly one of the switches, or it is an error: anything else would be skipped as an
    /// argument Clink does not know, and the start would be a normal one.
    /// </summary>
    public static DevOptions? Parse(string[] args)
    {
        bool sawDev = false, hotkeys = false, token = false, record = false, accept = false, sawRoot = false, quiet = false, quietRecord = false, noMarker = false, sawDemo = false;
        string? folderArg = null, rootArg = null, demoStatus = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (Is(a, Dev))
            {
                if (sawDev) throw new ArgumentException("--dev is given more than once");
                sawDev = true;
                if (i + 1 < args.Length && IsValue(args[i + 1])) folderArg = args[++i];
            }
            else if (a.StartsWith(DevEquals, StringComparison.OrdinalIgnoreCase))
            {
                if (sawDev) throw new ArgumentException("--dev is given more than once");
                sawDev = true;
                folderArg = a[DevEquals.Length..];
                if (Unquote(folderArg).Length == 0) throw new ArgumentException("--dev= needs a folder after the =");
            }
            else if (Is(a, Hotkeys)) hotkeys = true;
            else if (Is(a, Token)) token = true;
            else if (Is(a, Record_)) record = true;
            else if (Is(a, Quiet_)) quiet = true;               // an error without --dev, like the others
            else if (Is(a, QuietRecord_)) quietRecord = true;
            else if (Is(a, NoMarker_)) noMarker = true;          // quiet-only, like the next one
            else if (Is(a, DemoStatus_))
            {
                sawDemo = true;
                if (i + 1 < args.Length && IsValue(args[i + 1])) demoStatus = Unquote(args[++i]);
                if (string.IsNullOrWhiteSpace(demoStatus)) throw new ArgumentException("--dev-demo-status needs a game name after it");
            }
            else if (Is(a, AcceptImport_)) accept = true;
            else if (Is(a, PackagesRoot_))
            {
                sawRoot = true;
                if (i + 1 < args.Length && IsValue(args[i + 1])) rootArg = args[++i];
            }
            else if (LooksLikeDevSwitch(a))
                throw new ArgumentException($"{Shown(a)} is not a dev switch (they are --dev [folder] or --dev=<folder>, --dev-hotkeys, --dev-record, --dev-quiet, --dev-quiet-record, --dev-no-marker, --dev-demo-status <game>, --dev-token, --dev-accept-import and --packages-root <folder>, "
                    + "written with two plain hyphens and nothing around them; a folder is written with its path, like .\\devdata)");
        }
        if (!sawDev)
        {
            if (hotkeys || token || record || accept || sawRoot || quiet || quietRecord || noMarker || sawDemo)
                throw new ArgumentException("--dev-hotkeys, --dev-record, --dev-quiet, --dev-quiet-record, --dev-no-marker, --dev-demo-status, --dev-token, --dev-accept-import and --packages-root only work together with --dev [folder]");
            return null;
        }

        if ((noMarker || sawDemo) && !quiet)
            throw new ArgumentException("--dev-no-marker and --dev-demo-status only work together with --dev [folder] and --dev-quiet");

        if (noMarker && quietRecord)
            throw new ArgumentException("--dev-no-marker cannot be used with --dev-quiet-record: a recording instance must keep its TEST marker");

        string folder = folderArg == null ? DefaultFolder : Unquote(folderArg);
        try { folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"the data folder '{folder}' is not a valid path");
        }
        RefuseUnsafeFolder(folder);

        string? root = null;
        if (rootArg != null)
        {
            try { root = Path.GetFullPath(Unquote(rootArg)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException($"--packages-root '{rootArg}' is not a valid path");
            }
            string realPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (SamePath(root, realPackages))
                throw new ArgumentException("--packages-root is the real %LOCALAPPDATA%\\Packages: a test must not take over the settings of whoever owns this machine (make a fake folder)");
        }
        return new DevOptions(folder, hotkeys) { PackagesRoot = root, AcceptImport = accept, AllowToken = token, Record = record, Quiet = quiet, QuietRecord = quietRecord, NoMarker = noMarker, DemoStatus = demoStatus };
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or inside it.</summary>
    private static bool Within(string child, string parent)
    {
        string c = Path.GetFullPath(child).TrimEnd('\\', '/'), p = Path.GetFullPath(parent).TrimEnd('\\', '/');
        return c.Equals(p, StringComparison.OrdinalIgnoreCase) || c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRoot(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        return root != null && full.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ a folder that is safe for a dev instance

    /// <summary>
    /// What the real Clink and the Windows around it keep that a dev folder must neither be nor hold: the real settings folder
    /// (<see cref="Settings.RealDir"/>), the real Clink's local data (the updates), the folder that holds the packaged apps' private
    /// AppData, the default clips folder, and the clips folder and archive location the real settings.json names. A dev instance
    /// treats everything inside its own folder as its own (its clips are listed, trimmed, shared, archived and deleted from there),
    /// so a folder that held any of these would make the real clips fair game.
    /// </summary>
    internal static List<(string Path, string What)> RealPlaces()
    {
        var places = new List<(string Path, string What)>();
        void Add(string? path, string what)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { places.Add((System.IO.Path.GetFullPath(path), what)); } catch (Exception) { }
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        Add(Settings.RealDir, "the real Clink's settings folder");
        if (local.Length > 0)
        {
            Add(System.IO.Path.Combine(local, "Clink"), "the real Clink's local data folder");
            Add(System.IO.Path.Combine(local, "Packages"), "%LOCALAPPDATA%\\Packages");
        }
        if (videos.Length > 0) Add(System.IO.Path.Combine(videos, "Clink"), "the default clips folder");
        var (clips, archive) = SettingsImport.SavedLocations(System.IO.Path.Combine(Settings.RealDir, "settings.json"));
        Add(clips, "the real clips folder");
        Add(archive, "the real archive folder");
        return places;
    }

    /// <summary>
    /// Refuses a data folder a dev instance must not live in, as an <see cref="ArgumentException"/> that says why (exit code 2).
    /// Judged as given and, when a junction or symbolic link sends it elsewhere, where it really is. Refused: the real settings
    /// folder; anything under <c>%LOCALAPPDATA%\Packages</c> or whose final path is a packaged app's private AppData (what Windows
    /// shows for a folder made inside such an app: <see cref="AppDataRedirect.PackageFromFinalPath"/>); a drive's root and the user
    /// profile folder; any folder that holds one of <see cref="RealPlaces"/> (a parent of the real settings, clips or archive); and an
    /// existing folder that holds other files and no Clink data. So the dev folder is always a leaf folder Clink makes, an empty one,
    /// or one an earlier dev run made, and "inside the dev folder" (<see cref="IsInsideFolder"/>, what <see cref="Isolate"/> lets a
    /// clips folder or archive location be) never reaches anything the real Clink uses.
    /// </summary>
    private static void RefuseUnsafeFolder(string folder)
    {
        string? resolved = AppDataRedirect.ResolvedPath(folder);
        var candidates = new List<string> { folder };
        if (resolved != null && !SamePath(resolved, folder)) candidates.Add(resolved);

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = RealPlaces();
        foreach (string path in candidates)
        {
            string name = path == folder ? $"the data folder {folder}" : $"the data folder {folder} (really {path})";
            if (SamePath(path, Settings.RealDir))
                throw new ArgumentException(path == folder
                    ? $"the data folder is the real Clink's settings folder ({Settings.RealDir}): a --dev instance needs a folder of its own"
                    : $"{name} is the real Clink's settings folder ({Settings.RealDir}): a --dev instance needs a folder of its own");
            if (local.Length > 0 && Within(path, System.IO.Path.Combine(local, "Packages")))
                throw new ArgumentException($"{name} is under %LOCALAPPDATA%\\Packages, where Windows keeps the private copies of AppData that packaged apps (an MSIX/Store desktop app, say) get: a --dev instance needs a folder of its own somewhere else");
            if (AppDataRedirect.PackageFromFinalPath(path + "\\") is { } package)
                throw new ArgumentException($"{name} is inside {package}'s private copy of AppData: a --dev instance needs a folder of its own somewhere else (the Temp folder is fine)");
            if (IsRoot(path))
                throw new ArgumentException($"{name} is a drive's root: a --dev instance keeps its settings, clips and log in its folder, so it needs one of its own (like {DefaultFolder})");
            if (profile.Length > 0 && SamePath(path, profile))
                throw new ArgumentException($"{name} is your user profile folder: a --dev instance keeps its settings, clips and log in its folder, so it needs one of its own (like {DefaultFolder})");
            foreach (var (place, what) in places)
                if (Within(place, path))
                    throw new ArgumentException($"{name} holds {what} ({place}): a --dev instance treats everything in its folder as its own, so it needs a folder that holds nothing of the real Clink's (like {DefaultFolder})");
        }
        if (HoldsOtherFiles(folder))
            throw new ArgumentException($"the data folder {folder} already holds other files and no Clink data: a --dev instance keeps its settings, clips and log in its folder, so use a new or an empty one (or one an earlier --dev run made)");
    }

    /// <summary>The folder exists, has something in it, and nothing in it is Clink's own (settings, clip index, log, clips).</summary>
    private static bool HoldsOtherFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            bool any = false;
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                any = true;
                string name = System.IO.Path.GetFileName(entry);
                if (name.StartsWith("settings.json", StringComparison.OrdinalIgnoreCase) || name.StartsWith("settings.pre-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("clips.json", StringComparison.OrdinalIgnoreCase) || name.Equals("clips", StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith("clink", StringComparison.OrdinalIgnoreCase) && name.Contains(".log", StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            return any;
        }
        catch (Exception) { return false; }   // a folder that cannot be listed is not this check's business
    }

    /// <summary>
    /// True when <paramref name="path"/> is the dev folder or inside it (a path that cannot be resolved is not). The dev folder itself
    /// was vetted by <see cref="Parse"/> (<see cref="RefuseUnsafeFolder"/>): it holds nothing of the real Clink's, so "inside it" is
    /// never the real clips folder or archive.
    /// </summary>
    internal bool IsInsideFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string root = Folder.TrimEnd('\\', '/');
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Keeps a dev instance away from the real world, whatever its settings file says: a copied fixture, an unreadable file, a
    /// settings file taken over from another copy of Clink, or a folder that already held a real Clink's. Applied in memory to the
    /// loaded settings on every start and again after any import (the file is not rewritten): no automatic updates and no
    /// run-at-startup; the clips folder inside the dev folder (so the clip index never lists, nor the trim editor or Share touch,
    /// the real clips) and a screenshots folder outside it back to the default (a Screenshots folder inside the clips folder); the archive off, and its location forgotten, unless that is inside the dev folder (it moves and deletes
    /// clips, and writes to the location, which a manual "Archive now" does whatever the switch says); no Copium token
    /// unless <c>--dev-token</c> (so nothing is shared to the real account). On a fresh store (<paramref name="freshStore"/>) the
    /// "Recording started" toast is also off, so a recording dev instance does not put toasts over a game.
    /// Returns what it changed, one phrase each (no values worth hiding: a token is only said to be dropped), for the log.
    /// </summary>
    public List<string> Isolate(Settings s, bool freshStore)
    {
        var changed = new List<string>();
        if (s.AutoUpdate) { s.AutoUpdate = false; changed.Add("automatic updates off"); }
        if (s.RunAtStartup) { s.RunAtStartup = false; changed.Add("run at startup off"); }
        if (!IsInsideFolder(s.ClipsFolder))
        {
            changed.Add($"clips folder {Path.Combine(Folder, "clips")} instead of {s.ClipsFolder}");
            s.ClipsFolder = Path.Combine(Folder, "clips");
        }
        if (!string.IsNullOrWhiteSpace(s.ScreenshotsFolder) && !IsInsideFolder(s.ScreenshotsFolder))
        {
            // null means "Screenshots inside the clips folder", which is in the dev folder by now.
            changed.Add("screenshots folder reset (it is outside the dev folder)");
            s.ScreenshotsFolder = null;
        }
        if (!IsInsideFolder(s.ArchivePath))
        {
            // Off, and the location forgotten: a manual "Archive now" does not need the switch, only the location.
            if (s.ArchiveEnabled || !string.IsNullOrWhiteSpace(s.ArchivePath)) changed.Add("archive off and its location cleared (it is outside the dev folder)");
            s.ArchiveEnabled = false;
            s.ArchivePath = null;
        }
        if (!AllowToken && s.CopiumTokenProtected != null) { s.CopiumTokenProtected = null; changed.Add("no Copium token (--dev-token keeps it)"); }
        if (freshStore && s.ToastRecording.Enabled) { s.ToastRecording.Enabled = false; changed.Add("\"Recording started\" toast off"); }
        return changed;
    }

    /// <summary>
    /// Goes after the mutex and pipe names (<see cref="AppController.InstanceSuffix"/>): ".dev." and eight hex digits of the folder's
    /// full path. One dev instance per data folder, so two testers with two folders do not meet, and a second <c>--dev</c> on the
    /// same folder forwards to the first (as a second Clink does); the path is case-insensitive, like the file system.
    /// </summary>
    public string InstanceSuffix
    {
        get
        {
            string key = Folder.TrimEnd('\\', '/').ToLowerInvariant();
            return ".dev." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 4).ToLowerInvariant();
        }
    }

    // ------------------------------------------------------------------ restarting

    /// <summary>
    /// The switches that make a restarted process the same dev instance: <c>--dev &lt;folder&gt;</c> and the flags that are on
    /// (<c>--dev-hotkeys</c>, <c>--dev-record</c>, <c>--dev-token</c>, <c>--dev-quiet</c>, <c>--dev-quiet-record</c>, <c>--packages-root</c>), but not <c>--dev-accept-import</c>, which
    /// is for one start. Every restart (an import, an update) puts these on the new command line: a restart without
    /// <c>--dev</c> would be a normal start, which takes the real Clink's mutex or forwards to it.
    /// </summary>
    public static List<string> Switches(string folder, bool hotkeys, bool record, bool token, string? packagesRoot, bool quiet = false, bool quietRecord = false,
        bool noMarker = false, string? demoStatus = null)
    {
        var list = new List<string> { Dev, folder };
        if (hotkeys) list.Add(Hotkeys);
        if (record) list.Add(Record_);
        if (token) list.Add(Token);
        if (quiet) list.Add(Quiet_);
        if (quietRecord) list.Add(QuietRecord_);
        if (noMarker) list.Add(NoMarker_);
        if (demoStatus != null) { list.Add(DemoStatus_); list.Add(demoStatus); }
        if (packagesRoot != null) { list.Add(PackagesRoot_); list.Add(packagesRoot); }
        return list;
    }

    /// <summary>A command line without any dev switch or value (<c>--dev x</c>, <c>--dev=x</c>, the flags, <c>--packages-root x</c>): the rest of it, in order.</summary>
    public static List<string> WithoutSwitches(IEnumerable<string> args)
    {
        var rest = new List<string>();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (Is(a, Dev) || Is(a, PackagesRoot_) || Is(a, DemoStatus_)) { if (i + 1 < list.Count && IsValue(list[i + 1])) i++; }
            else if (a.StartsWith(DevEquals, StringComparison.OrdinalIgnoreCase) || Is(a, Hotkeys) || Is(a, Token) || Is(a, Record_) || Is(a, Quiet_) || Is(a, QuietRecord_) || Is(a, NoMarker_) || Is(a, AcceptImport_)) { }
            else rest.Add(a);
        }
        return rest;
    }
}
