using System.IO;
using System.Text;

namespace Clink.Services;

/// <summary>
/// Crash-safe text files for the two JSON stores (settings.json, clips.json): the new content goes to a ".tmp" next to the
/// file, is flushed to disk, and then replaces the file in one rename, keeping the previous version as ".bak". A power loss
/// in the middle leaves either the old file or the new one, never a truncated one; <see cref="ReadWithFallback"/> reads the
/// ".bak" when the primary is missing or does not parse.
/// </summary>
internal static class AtomicFile
{
    public static string BackupPath(string path) => path + ".bak";

    public static void WriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        string bak = BackupPath(path);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);   // on disk before the rename, so a crash cannot leave an empty file under the real name
        }
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, bak, ignoreMetadataErrors: true); return; }
            catch (Exception ex) { Log.Warn($"{Path.GetFileName(path)}: replace failed ({ex.Message}), moving instead"); }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Parses <paramref name="path"/> with <paramref name="parse"/>; when that fails (or the file is missing) the ".bak" is tried.
    /// Returns null when neither exists; throws only when both exist and neither parses (the primary's error).
    /// A primary that exists but does not parse (often a hand edit with a typo) is copied to ".unreadable" first: the next two
    /// saves would otherwise rotate it out through the ".bak" and the edit would be gone. <paramref name="unreadable"/> is then
    /// told why ("error on line 12; kept as settings.json.unreadable").
    /// </summary>
    public static T? ReadWithFallback<T>(string path, Func<string, T?> parse, Action<string>? unreadable = null) where T : class
    {
        Exception? primary = null;
        if (File.Exists(path))
        {
            try { return parse(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                primary = ex;
                Log.Warn($"{Path.GetFileName(path)}: {ex.Message}; trying the backup");
                string why = ex is System.Text.Json.JsonException { LineNumber: long line } ? $"error on line {line + 1}" : ex.Message;
                try { File.Copy(path, path + ".unreadable", overwrite: true); why += $"; kept as {Path.GetFileName(path)}.unreadable"; }
                catch (Exception cex) { Log.Warn($"{Path.GetFileName(path)}: could not keep a copy: {cex.Message}"); }
                unreadable?.Invoke(why);
            }
        }
        string bak = BackupPath(path);
        if (File.Exists(bak))
        {
            try
            {
                var result = parse(File.ReadAllText(bak));
                if (primary != null) Log.Warn($"{Path.GetFileName(path)}: recovered from {Path.GetFileName(bak)}");
                return result;
            }
            catch (Exception ex) { Log.Warn($"{Path.GetFileName(bak)}: {ex.Message}"); }
        }
        if (primary != null) throw primary;
        return null;
    }
}
