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
/// Two more switches exist only for testing how settings are taken over from another copy of Clink (SettingsImport), which a
/// <c>--dev</c> instance otherwise never looks for: <c>--packages-root &lt;folder&gt;</c> names a fake <c>%LOCALAPPDATA%\Packages</c> to
/// look in (never the real one), and <c>--dev-accept-import</c> accepts what is offered there at once.
/// Every <c>--dev*</c> switch is checked, and a mistake (an unknown one, one without <c>--dev</c>, a folder that is the real Clink's)
/// is an error before anything is started: a typo must not turn into a normal start that talks to the real Clink.
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

    /// <summary>Where a bare <c>--dev</c> keeps its data.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "clink-dev");

    private const string Dev = "--dev", DevEquals = "--dev=", Hotkeys = "--dev-hotkeys", Token = "--dev-token", Record_ = "--dev-record",
        AcceptImport_ = "--dev-accept-import", PackagesRoot_ = "--packages-root";

    private static bool Is(string arg, string name) => arg.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>An argument that can be the value of a switch: not another switch, not empty.</summary>
    private static bool IsValue(string arg) => !arg.StartsWith("--", StringComparison.Ordinal) && arg.Trim().Length > 0;

    private static string Unquote(string value) => value.Trim().Trim('"');

    /// <summary>
    /// The options of the <c>--dev</c> switches anywhere on the command line, or null for a normal start (no dev switch at all).
    /// The folder is the argument right after <c>--dev</c> (unless that is another switch: the default folder then), or what follows
    /// <c>--dev=</c>. Throws <see cref="ArgumentException"/>, with a message for the person who typed the command line, for: an unknown
    /// <c>--dev…</c> switch, <c>--dev</c> twice, <c>--dev=</c> with nothing after it, a dev switch (or <c>--packages-root</c>) without
    /// <c>--dev</c>, a folder that is not a valid path or that is the real settings folder, and a packages root that is the real one.
    /// </summary>
    public static DevOptions? Parse(string[] args)
    {
        bool sawDev = false, hotkeys = false, token = false, record = false, accept = false, sawRoot = false;
        string? folderArg = null, rootArg = null;
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
            else if (Is(a, AcceptImport_)) accept = true;
            else if (Is(a, PackagesRoot_))
            {
                sawRoot = true;
                if (i + 1 < args.Length && IsValue(args[i + 1])) rootArg = args[++i];
            }
            else if (a.StartsWith(Dev, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{a} is not a dev switch (they are --dev [folder] or --dev=<folder>, --dev-hotkeys, --dev-record, --dev-token, --dev-accept-import)");
        }
        if (!sawDev)
        {
            if (hotkeys || token || record || accept || sawRoot)
                throw new ArgumentException("--dev-hotkeys, --dev-record, --dev-token, --dev-accept-import and --packages-root only work together with --dev [folder]");
            return null;
        }

        string folder = folderArg == null ? DefaultFolder : Unquote(folderArg);
        try { folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"the data folder '{folder}' is not a valid path");
        }
        if (SamePath(folder, Settings.RealDir))
            throw new ArgumentException($"the data folder is the real Clink's settings folder ({Settings.RealDir}): a --dev instance needs a folder of its own");

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
        return new DevOptions(folder, hotkeys) { PackagesRoot = root, AcceptImport = accept, AllowToken = token, Record = record };
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is the dev folder or inside it (a path that cannot be resolved is not).</summary>
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
    /// the real clips); the archive off, and its location forgotten, unless that is inside the dev folder (it moves and deletes
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
    /// (<c>--dev-hotkeys</c>, <c>--dev-record</c>, <c>--dev-token</c>, <c>--packages-root</c>), but not <c>--dev-accept-import</c>, which
    /// is for one start. Every restart (an import, an update) puts these on the new command line: a restart without
    /// <c>--dev</c> would be a normal start, which takes the real Clink's mutex or forwards to it.
    /// </summary>
    public static List<string> Switches(string folder, bool hotkeys, bool record, bool token, string? packagesRoot)
    {
        var list = new List<string> { Dev, folder };
        if (hotkeys) list.Add(Hotkeys);
        if (record) list.Add(Record_);
        if (token) list.Add(Token);
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
            if (Is(a, Dev) || Is(a, PackagesRoot_)) { if (i + 1 < list.Count && IsValue(list[i + 1])) i++; }
            else if (a.StartsWith(DevEquals, StringComparison.OrdinalIgnoreCase) || Is(a, Hotkeys) || Is(a, Token) || Is(a, Record_) || Is(a, AcceptImport_)) { }
            else rest.Add(a);
        }
        return rest;
    }
}
