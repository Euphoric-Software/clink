namespace Clink.Tests;

/// <summary>
/// What the size-target tests need to read the English notes of <c>Capabilities.ResolveBitrate</c>: the length notes and a check on their
/// words. Test-only: the app itself never reads the words of a note (the warning is a flag of <c>ResolveBitrateChecked</c>).
/// </summary>
internal static class SizeTargetNotes
{
    private const string FreeLimitText = "Copium free uploads are limited to 10 min: trim a full-length clip before sharing it, or shorten the replay length";
    private const string PlusLimitText = "Copium Plus uploads are limited to 60 min: trim a full-length clip before sharing it, or shorten the replay length";

    /// <summary>What the Copium free target appends to its note from a replay length of 10 min up, as English reads.</summary>
    public const string CopiumLengthNote = " - " + FreeLimitText;
    /// <summary>The same for the Copium Plus target, from 60 min up.</summary>
    public const string CopiumPlusLengthNote = " - " + PlusLimitText;
    private const string LengthLimitMarker = " uploads are limited to ";

    /// <summary>True for an English size-target note that is a warning: "does not fit", or a length-limit sentence.</summary>
    public static bool IsWarningNote(string? note) =>
        note != null && (note.Contains("does not fit", StringComparison.Ordinal) || note.Contains(LengthLimitMarker, StringComparison.Ordinal));
}