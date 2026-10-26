using System.Globalization;
using System.IO;

namespace Clink.Screenshots;

/// <summary>
/// The file name of a screenshot, pure: <c>"{Game or Screenshot} yyyy-MM-dd HH-mm-ss.png"</c>, with <c>" (n)"</c> before the
/// extension when that name is taken. The game part comes from a window title / process name, so it is never trusted: it is cut
/// down to something that is a safe single file name.
/// </summary>
internal static class ScreenshotNaming
{
    public const string DefaultName = "Screenshot";
    public const int MaxGameLength = 60;

    private static readonly string[] s_reserved =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A game name as a file-name part: characters Windows does not allow in a name (and control characters) become '_', the result
    /// is trimmed of blanks and of leading / trailing dots, cut to <see cref="MaxGameLength"/> characters, and a reserved device name
    /// (CON, NUL, COM1, ... also with an extension) gets a '_' in front. Nothing usable left: <see cref="DefaultName"/>.
    /// </summary>
    public static string Sanitize(string? game)
    {
        if (string.IsNullOrWhiteSpace(game)) return DefaultName;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = game.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i]) || Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] is '/' or '\\' or ':') chars[i] = '_';
        string s = new string(chars).Trim().Trim('.').Trim();
        if (s.Length > MaxGameLength) s = s[..MaxGameLength].TrimEnd().TrimEnd('.');
        if (s.Length == 0) return DefaultName;
        string stem = s.Contains('.') ? s[..s.IndexOf('.')] : s;
        if (s_reserved.Contains(stem.Trim(), StringComparer.OrdinalIgnoreCase)) s = "_" + s;
        return s;
    }

    /// <summary>"Overwatch 2026-10-24 21-04-11" (no extension, no uniqueness).</summary>
    public static string BaseName(string? game, DateTime when) =>
        $"{Sanitize(game)} {when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// <c>folder\{BaseName}.png</c>, or <c>folder\{BaseName} (1).png</c>, <c>(2)</c>, ... for the first name that is free.
    /// <paramref name="exists"/> says whether a path is taken (default: the file system), so it is tested without one.
    /// </summary>
    public static string UniquePath(string folder, string baseName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string path = Path.Combine(folder, baseName + ".png");
        for (int n = 1; exists(path); n++) path = Path.Combine(folder, $"{baseName} ({n.ToString(CultureInfo.InvariantCulture)}).png");
        return path;
    }
}
