using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>
/// The two texts of a failed Copium call. <see cref="Exception.Message"/> is English and goes to the log (a log is read by the developer
/// whatever language the user picked); the same sentence in the user's language rides on the exception (<see cref="Exception.Data"/>) for
/// whatever the user is shown: <see cref="UserText"/>. Same idea as <c>Updater</c>'s fixed download texts.
/// </summary>
internal static class CopiumErrors
{
    private const string Key = "Clink.Copium.UserText";

    /// <summary>The exception <paramref name="make"/> builds from the English text, carrying <paramref name="text"/> in the current language for <see cref="UserText"/>.</summary>
    internal static T Shown<T>(Func<string, T> make, Func<string, string> text) where T : Exception
    {
        var ex = make(text("en"));
        ex.Data[Key] = text(L.Code);
        return ex;
    }

    /// <summary>Attaches an already built user-language text to <paramref name="ex"/> (<see cref="CopiumHttpException"/>).</summary>
    internal static void Attach(Exception ex, string text) => ex.Data[Key] = text;

    /// <summary>What to show the user for <paramref name="ex"/>: the text attached in their language, else the exception's own message.</summary>
    internal static string UserText(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } a) ex = a.InnerException!;
        return ex.Data[Key] as string ?? ex.Message;
    }

    /// <summary>One template in one language: the same key gives the English <see cref="Exception.Message"/> and the text for the UI.</summary>
    internal static string Tr(string locale, string key, params (string Name, object? Value)[] a) => L.TIn(locale, key, a);
}
