namespace Clink.Services.Localization;

/// <summary>
/// An exception (or any object) that knows the sentence to show the user when its <see cref="Exception.Message"/> is the English one for
/// the log. Only for types that cannot carry the text in <see cref="Exception.Data"/> (<see cref="UserText.Attach{T}(T, Note)"/>).
/// </summary>
internal interface IUserText
{
    /// <summary>The sentence in the language of the UI.</summary>
    string UserText { get; }
}

/// <summary>
/// The one way an exception carries what the user is shown. <see cref="Exception.Message"/> is English and goes to the log (a log is
/// read by the developer whatever language the user picked); the user's sentence rides on the exception in <c>Data["Clink.UserText"]</c>,
/// preferably as a <see cref="Note"/> (catalog key + arguments) so it is translated when it is shown, not when it is thrown.
/// </summary>
internal static class UserText
{
    /// <summary>The <see cref="Exception.Data"/> key.</summary>
    internal const string Key = "Clink.UserText";

    /// <summary><paramref name="ex"/> carrying <paramref name="note"/> as the sentence for the user (shown with <see cref="Of"/>).</summary>
    internal static T Attach<T>(T ex, Note note) where T : Exception
    {
        ex.Data[Key] = note;
        return ex;
    }

    /// <summary><paramref name="ex"/> carrying an already finished <paramref name="text"/> (a fixed English text, or one a library produced) for the user.</summary>
    internal static T Attach<T>(T ex, string text) where T : Exception
    {
        ex.Data[Key] = text;
        return ex;
    }

    /// <summary>
    /// What to show the user for <paramref name="ex"/>: the text attached with <see cref="Attach{T}(T, Note)"/> (a note is rendered now, in
    /// the current language), else <see cref="IUserText.UserText"/>, else <see cref="Exception.Message"/>. A single-inner
    /// <see cref="AggregateException"/> (what <c>Task.Wait</c> throws) is looked through.
    /// </summary>
    internal static string Of(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } a) ex = a.InnerException!;
        return ex.Data[Key] switch
        {
            Note n => n.Shown,
            string s => s,
            _ => ex is IUserText u ? u.UserText : ex.Message,
        };
    }

    /// <summary>
    /// <paramref name="ex"/>'s user text as a note, so a sentence built around it (<c>Upload failed: {error}</c>) keeps an English log line and a
    /// translated display: the attached note itself, else the attached or interface text / the message as it is (<see cref="Note.Verbatim"/>).
    /// </summary>
    internal static Note NoteOf(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } a) ex = a.InnerException!;
        return ex.Data[Key] switch
        {
            Note n => n,
            string s => Note.Verbatim(s),
            _ => Note.Verbatim(ex is IUserText u ? u.UserText : ex.Message),
        };
    }

    /// <summary>The attached text in English (what a log may say), else <see cref="Exception.Message"/>.</summary>
    internal static string EnglishOf(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } a) ex = a.InnerException!;
        return ex.Data[Key] switch { Note n => n.English, string s => s, _ => ex.Message };
    }
}
