using System.IO;
using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>"Save a copy…" of a clip file: the pure rules and the safe copy (never leaves a truncated target).</summary>
internal static class SaveCopy
{
    public static string OwnFileText => OwnFileNote.Shown;
    public static string InsideClipsText => InsideClipsNote.Shown;
    private static Note OwnFileNote => Note.Of("That is the clip's own file");
    private static Note InsideClipsNote => Note.Of("Choose a folder outside Clink's clips folder");

    public static string DefaultFileName(string clipPath) => Path.GetFileName(clipPath);

    /// <summary>The folder the save dialog opens in: the remembered one while it still exists, else My Videos.</summary>
    public static string InitialFolder(string? remembered, string fallbackMyVideos) =>
        !string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered) ? remembered : fallbackMyVideos;

    /// <summary>Null when <paramref name="target"/> may be written, else why not.</summary>
    public static string? TargetProblem(string target, string source, string? clipsFolder) => TargetProblemNote(target, source, clipsFolder)?.Shown;

    /// <summary><see cref="TargetProblem"/> as a note (English in the log, the language of the UI on screen).</summary>
    public static Note? TargetProblemNote(string target, string source, string? clipsFolder)
    {
        string t, s;
        try { t = Path.GetFullPath(target); s = Path.GetFullPath(source); }
        catch (Exception) { return Note.Of("That is not a valid file name"); }
        if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return OwnFileNote;
        if (!string.IsNullOrWhiteSpace(clipsFolder))
        {
            string root;
            try { root = Path.GetFullPath(clipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; }
            catch (Exception) { return null; }
            if (t.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return InsideClipsNote;
        }
        return null;
    }

    /// <summary>
    /// Copies <paramref name="src"/> to <paramref name="dst"/> through "dst.clinkpart" and moves it into place only when complete;
    /// on failure or cancel the part file is removed and an existing target stays as it was. Progress 0..1, ending at 1.
    /// <paramref name="overwrite"/> false (the bulk save): a file that is at <paramref name="dst"/> when the copy is complete is never
    /// replaced; the move then fails with an <see cref="IOException"/> and the part file is removed.
    /// </summary>
    public static async Task CopyAsync(string src, string dst, IProgress<double>? progress, CancellationToken ct, bool overwrite = true)
    {
        string part = dst + ".clinkpart";
        TestGuard.Check(dst);
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
            File.Move(part, dst, overwrite);
            progress?.Report(1);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>How many other names a bulk copy tries when files keep appearing under the name it picked.</summary>
    public const int MaxNameTries = 20;

    /// <summary>
    /// Copies <paramref name="src"/> into <paramref name="folder"/> under a name that clashes with nothing (<see cref="BulkActions.UniqueName"/>)
    /// and never replaces a file: when one appears under the picked name before the copy is moved into place, the next name is taken
    /// and the copy repeated (at most <see cref="MaxNameTries"/> times). A directory under a name clashes like a file. Any other failure
    /// (the source unreadable, the folder locked or gone) is thrown at once, not retried. <paramref name="exists"/> is a test seam
    /// (default: a file or a directory is there). Returns the target path.
    /// </summary>
    public static async Task<string> CopyUniqueAsync(string src, string folder, string fileName, ISet<string> taken, IProgress<double>? progress,
        CancellationToken ct, Func<string, bool>? exists = null)
    {
        exists ??= Taken;
        for (int attempt = 0; ; attempt++)
        {
            string target = Path.Combine(folder, BulkActions.UniqueName(folder, fileName, exists, taken));   // the name is in taken: a retry moves on
            try
            {
                await CopyAsync(src, target, progress, ct, overwrite: false).ConfigureAwait(false);
                return target;
            }
            catch (IOException) when (attempt < MaxNameTries && Taken(target)) { }
        }
    }

    /// <summary>Something (a file or a directory) is at <paramref name="path"/>: a bulk copy must not use that name.</summary>
    private static bool Taken(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }
}
