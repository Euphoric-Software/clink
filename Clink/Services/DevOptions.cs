using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Clink.Services;

/// <summary>
/// <c>Clink.exe --dev [folder] [--dev-hotkeys]</c>: a second Clink for testing, next to the real one. It keeps everything in its
/// own folder (settings, clip index, log, default clips folder; default <c>%TEMP%\clink-dev</c>) and has its own single-instance
/// mutex and control pipe, so it never forwards a command to the real Clink, and the real one never sees its commands. It has no
/// updater and no Run-key entry, and binds no hotkeys unless <c>--dev-hotkeys</c> is given. Not in <c>--help</c>.
/// </summary>
internal sealed record DevOptions(string Folder, bool BindHotkeys)
{
    /// <summary>Where a bare <c>--dev</c> keeps its data.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "clink-dev");

    /// <summary>
    /// The options of a <c>--dev</c> anywhere on the command line (the folder is the argument right after it, unless that is another
    /// switch), or null for a normal start. Throws when the folder is not a valid path.
    /// </summary>
    public static DevOptions? Parse(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        string folder = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && args[i + 1].Trim().Length > 0
            ? args[i + 1].Trim().Trim('"')
            : DefaultFolder;
        bool bindHotkeys = args.Any(a => a.Equals("--dev-hotkeys", StringComparison.OrdinalIgnoreCase));
        return new DevOptions(Path.GetFullPath(folder), bindHotkeys);
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
}
