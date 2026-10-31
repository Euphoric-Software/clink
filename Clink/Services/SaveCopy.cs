using System.IO;

namespace Clink.Services;

/// <summary>"Save a copy…" of a clip file: the pure rules and the safe copy (never leaves a truncated target).</summary>
internal static class SaveCopy
{
    public const string OwnFileText = "That is the clip's own file";
    public const string InsideClipsText = "Choose a folder outside Clink's clips folder";

    public static string DefaultFileName(string clipPath) => Path.GetFileName(clipPath);

    /// <summary>The folder the save dialog opens in: the remembered one while it still exists, else My Videos.</summary>
    public static string InitialFolder(string? remembered, string fallbackMyVideos) =>
        !string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered) ? remembered : fallbackMyVideos;

    /// <summary>Null when <paramref name="target"/> may be written, else why not.</summary>
    public static string? TargetProblem(string target, string source, string? clipsFolder)
    {
        string t, s;
        try { t = Path.GetFullPath(target); s = Path.GetFullPath(source); }
        catch (Exception) { return "That is not a valid file name"; }
        if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return OwnFileText;
        if (!string.IsNullOrWhiteSpace(clipsFolder))
        {
            string root;
            try { root = Path.GetFullPath(clipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; }
            catch (Exception) { return null; }
            if (t.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return InsideClipsText;
        }
        return null;
    }

    /// <summary>
    /// Copies <paramref name="src"/> to <paramref name="dst"/> through "dst.clinkpart" and moves it into place only when complete;
    /// on failure or cancel the part file is removed and an existing target stays as it was. Progress 0..1, ending at 1.
    /// </summary>
    public static async Task CopyAsync(string src, string dst, IProgress<double>? progress, CancellationToken ct)
    {
        string part = dst + ".clinkpart";
        try
        {
            var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    long total = input.Length, done = 0;
                    var buffer = new byte[1 << 20];
                    int n;
                    progress?.Report(0);
                    while ((n = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        if (total > 0 && done < total) progress?.Report((double)done / total);
                    }
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    output.Flush(true);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(part, dst, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }
}
