namespace Clink.Services;

/// <summary>
/// A command line or pipe line split into a verb and an optional source word: "clip", "clip card", "record screen", "share card".
/// Only clip, record and share take a source word; any other line is its own verb, whole (so "show x" stays an unknown command).
/// The source word is not checked here: an unknown one is forwarded and the running Clink says so.
/// </summary>
internal readonly record struct SourceCommand(string Verb, string? Source)
{
    /// <summary>The verbs that take a source word.</summary>
    internal static bool TakesSource(string verb) => verb is "clip" or "record" or "share";

    /// <summary>Splits a pipe line (any case, any spacing). The verb and the source word are lower case.</summary>
    internal static SourceCommand Parse(string? line)
    {
        string whole = (line ?? "").Trim().ToLowerInvariant();
        string[] words = whole.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 1 or 2 && TakesSource(words[0]) && (words.Length == 1 || !words[1].StartsWith("--", StringComparison.Ordinal)))
            return new SourceCommand(words[0], words.Length == 2 ? words[1] : null);
        return new SourceCommand(whole, null);
    }

    /// <summary>The source word that follows <paramref name="switchName"/> on a command line: the next argument unless it is missing, blank or starts with "--".</summary>
    internal static string? WordAfter(string[] args, string switchName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(switchName, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length) return null;
            return IsWord(args[i + 1]) ? args[i + 1].Trim() : null;
        }
        return null;
    }

    /// <summary>True when <paramref name="arg"/> is the source word right behind a clip / record / share switch (a restart drops it with the switch).</summary>
    internal static bool IsWord(string arg) => !string.IsNullOrWhiteSpace(arg) && !arg.StartsWith("--", StringComparison.Ordinal) && !arg.Trim().Any(char.IsWhiteSpace);
}
