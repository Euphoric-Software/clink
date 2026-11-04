using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// The archive tests read the problems of <c>ArchiveService</c>'s delete guard and alias check as text (the words of the English UI).
/// Test-only: the app keeps them as <c>ArchiveProblem</c>s, shown translated and logged in English.
/// </summary>
internal static class ArchiveGuardText
{
    /// <summary>Opens the guard; <paramref name="problem"/> is the problem as the dialog shows it.</summary>
    public static ArchiveService.DeleteGuard? Open(string clip, string copy, long length, DateTime lastWriteUtc, out ArchiveService.GuardResult result, out string? problem)
    {
        var guard = ArchiveService.DeleteGuard.OpenCore(clip, copy, length, lastWriteUtc, out result, out ArchiveProblem? why);
        problem = why?.Shown;
        return guard;
    }

    /// <summary>Marks the clip for deletion through the guard; <paramref name="problem"/> is the problem as the dialog shows it.</summary>
    public static bool Delete(ArchiveService.DeleteGuard guard, out ArchiveService.GuardResult result, out string? problem)
    {
        bool done = guard.DeleteCore(out result, out ArchiveProblem? why);
        problem = why?.Shown;
        return done;
    }

    /// <summary>The alias problem as text (null: two different folders).</summary>
    public static string? AliasProblem(string archive, string clips) => ArchiveService.AliasCheck(archive, clips)?.Shown;
}