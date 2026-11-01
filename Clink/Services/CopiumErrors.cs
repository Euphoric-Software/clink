using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>
/// The two texts of a failed Copium call. <see cref="Exception.Message"/> is English and goes to the log (a log is read by the developer
/// whatever language the user picked); the same sentence rides on the exception as a <see cref="Note"/> (<see cref="Exception.Data"/>,
/// <see cref="Localization.UserText"/>), so it is translated when the user is shown it: <c>UserText.Of(ex)</c>.
/// </summary>
internal static class CopiumErrors
{
    /// <summary>The exception <paramref name="make"/> builds from the note's English text, carrying the note for <see cref="Localization.UserText.Of"/>.</summary>
    internal static T Shown<T>(Func<string, T> make, Note note) where T : Exception => Localization.UserText.Attach(make(note.English), note);
}
