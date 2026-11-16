using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>
/// One section of CHANGELOG.md: a released version (<see cref="Version"/>, an "X.Y.Z" release, with an optional date) or the
/// "Unreleased" notes (<see cref="Version"/> null). <see cref="Entries"/> are the English texts, one per bullet.
/// </summary>
internal sealed record ChangelogSection(ReleaseVersion? Version, string? Date, IReadOnlyList<string> Entries)
{
    public bool IsUnreleased => Version == null;
    /// <summary>"Unreleased" or the version number; for tests and logs (the window builds its own heading through the catalog).</summary>
    public string Heading => Version is { } v ? v.ToString() : "Unreleased";
}

/// <summary>
/// CHANGELOG.md, parsed. Format: anything before the first "## " line is a preface and ignored; then an optional "## Unreleased", then
/// "## X.Y.Z" or "## X.Y.Z - YYYY-MM-DD" sections, strictly descending, no duplicates; each section holds one or more entries, one line
/// each, "- text". An entry is 1 to <see cref="MaxEntryLength"/> characters with no "|", braces, brackets, angle brackets, backticks,
/// links or control characters (it is a catalog key: "whatsnew|" + the text). Anything else is a problem and the whole file is refused.
/// </summary>
internal sealed class Changelog
{
    internal const int MaxEntryLength = 300;
    private const string UnreleasedHeading = "Unreleased";

    private static readonly Regex s_heading = new(@"^## (?:(?<u>Unreleased)|(?<v>\d+\.\d+\.\d+)(?: - (?<d>\d{4}-\d{2}-\d{2}))?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Newest first: the Unreleased section (if any), then the releases, highest first.</summary>
    public IReadOnlyList<ChangelogSection> Sections { get; }

    private Changelog(List<ChangelogSection> sections) { Sections = sections; }

    public ChangelogSection? Unreleased => Sections.Count > 0 && Sections[0].IsUnreleased ? Sections[0] : null;

    /// <summary>The released sections, highest version first.</summary>
    public IEnumerable<ChangelogSection> Released => Sections.Where(s => !s.IsUnreleased);

    /// <summary>Every entry text of the file, in file order, repeats included.</summary>
    public IEnumerable<string> AllEntries => Sections.SelectMany(s => s.Entries);

    /// <summary>SHA-256 (lower-case hex) of the English Unreleased entries joined by LF; null without an Unreleased section. A translation fix never changes it.</summary>
    public string? UnreleasedHash => Unreleased is { } u ? HashOf(u.Entries) : null;

    internal static string HashOf(IEnumerable<string> entries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).ToLowerInvariant();

    /// <summary>The problem with one entry text, or null when it is fine.</summary>
    internal static string? EntryProblem(string text)
    {
        if (text.Length == 0) return "empty entry";
        if (text.Length > MaxEntryLength) return $"entry longer than {MaxEntryLength} characters";
        if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])) return "entry starts or ends with a space";
        foreach (char c in text)
        {
            if (char.IsControl(c)) return "entry has a control character";
            if (c is '|' or '{' or '}' or '[' or ']' or '<' or '>' or '`') return $"entry has the character '{c}'";
        }
        if (text.Contains("://", StringComparison.Ordinal)) return "entry has a link";
        return null;
    }

    /// <summary>Parses <paramref name="text"/> (CRLF or LF). On failure <paramref name="problem"/> says what and where, and the result is null.</summary>
    public static bool TryParse(string? text, out Changelog? changelog, out string? problem)
    {
        (changelog, problem) = Parse(text);
        return changelog != null;
    }

    private static (Changelog? Log, string? Problem) Parse(string? text)
    {
        string? problem = null;
        if (string.IsNullOrWhiteSpace(text)) return (null, "the changelog is empty");
        var sections = new List<ChangelogSection>();
        string? heading = null;
        ReleaseVersion? version = null;
        string? date = null;
        var entries = new List<string>();
        bool inSection = false;

        bool Close(int line)
        {
            if (!inSection) return true;
            if (entries.Count == 0) { problem = $"line {line}: the section \"{heading}\" has no entries"; return false; }
            sections.Add(new ChangelogSection(version, date, entries.ToList()));
            entries.Clear();
            return true;
        }

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            int n = i + 1;
            if (line.StartsWith("## ", StringComparison.Ordinal) || line == "##")
            {
                if (!Close(n)) return (null, problem);
                var m = s_heading.Match(line);
                if (!m.Success) return (null, $"line {n}: not a section heading (\"## Unreleased\", \"## X.Y.Z\" or \"## X.Y.Z - YYYY-MM-DD\"): {line}");
                inSection = true;
                date = null;
                if (m.Groups["u"].Success)
                {
                    if (sections.Count > 0) return (null, $"line {n}: \"Unreleased\" must be the first section");
                    heading = UnreleasedHeading;
                    version = null;
                    continue;
                }
                var v = ReleaseVersion.ParseCanonical(m.Groups["v"].Value);
                if (v == null) return (null, $"line {n}: not a version: {m.Groups["v"].Value}");
                if (m.Groups["d"].Success)
                {
                    if (!DateOnly.TryParseExact(m.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return (null, $"line {n}: not a date: {m.Groups["d"].Value}");
                    date = m.Groups["d"].Value;
                }
                var previous = sections.LastOrDefault(s => !s.IsUnreleased)?.Version;
                if (previous is { } p && v.Value >= p) return (null, $"line {n}: versions must be strictly descending ({v.Value} after {p})");
                heading = v.Value.ToString();
                version = v;
                continue;
            }
            if (line.Length == 0) continue;
            if (!inSection)   // the preface: free text, but not an entry that no section owns
            {
                if (line.StartsWith("- ", StringComparison.Ordinal)) return (null, $"line {n}: an entry before the first section");
                continue;
            }
            if (!line.StartsWith("- ", StringComparison.Ordinal)) return (null, $"line {n}: expected an entry (\"- text\") or a section heading: {line}");
            string entry = line[2..];
            string? bad = EntryProblem(entry);
            if (bad != null) return (null, $"line {n}: {bad}");
            entries.Add(entry);
        }
        if (!Close(lines.Length)) return (null, problem);
        if (sections.Count == 0) return (null, "the changelog has no sections");
        return (new Changelog(sections), null);
    }
}

/// <summary>The shipped changelog: the embedded CHANGELOG.md, parsed once, and the catalog key of an entry.</summary>
internal static class WhatsNew
{
    internal const string ResourceName = "CHANGELOG.md";
    /// <summary>The context of every entry's catalog key: "whatsnew|" + the English entry text (Locales/&lt;language&gt;/changelog.json).</summary>
    internal const string KeyPrefix = "whatsnew|";

    private static readonly Lazy<Changelog?> s_current = new(Load);

    /// <summary>The embedded changelog, or null when it is missing or invalid (logged once; the window then simply does not show).</summary>
    internal static Changelog? Current => s_current.Value;

    internal static string Key(string english) => KeyPrefix + english;

    /// <summary>An entry in the active language (English where the catalog lacks it).</summary>
    internal static string Text(string english) => L.T(Key(english));

    /// <summary>The embedded file's text; null when the resource is missing.</summary>
    internal static string? ReadResource()
    {
        using var s = typeof(WhatsNew).Assembly.GetManifestResourceStream(ResourceName);
        if (s == null) return null;
        using var reader = new StreamReader(s, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Changelog? Load()
    {
        try
        {
            string? text = ReadResource();
            if (text == null) { Log.Warn($"what's new: the {ResourceName} resource is missing from this build"); return null; }
            if (Changelog.TryParse(text, out var log, out string? problem)) return log;
            Log.Warn("what's new: " + ResourceName + " is invalid: " + problem);
        }
        catch (Exception ex) { Log.Warn("what's new: " + ResourceName + " could not be read: " + ex.Message); }
        return null;
    }
}

/// <summary>What the "What's new" window shows on its own, and when a version counts as seen. Pure apart from <see cref="MarkSeen"/>.</summary>
internal static class WhatsNewPolicy
{
    private static readonly Regex s_hash = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary><paramref name="text"/> when it is exactly "X.Y.Z" or "X.Y.Z-dev.N", otherwise null (how <see cref="Settings.WhatsNewSeen"/> is read).</summary>
    internal static string? CanonicalVersionOrNull(string? text) => ReleaseVersion.ParseCanonical(text)?.ToString();

    /// <summary><paramref name="text"/> when it is 64 lower-case hex digits, otherwise null (how <see cref="Settings.WhatsNewSeenDevHash"/> is read).</summary>
    internal static string? HashOrNull(string? text) => text != null && s_hash.IsMatch(text) ? text : null;

    /// <summary>
    /// The sections to show at start, newest first (empty = show nothing). First match wins:
    /// (1) a --dev instance or a unit-test run never shows it on its own; (2) no changelog: nothing;
    /// (3) a first install shows the current version (a dev build: Unreleased); (4) nothing seen yet (an existing user's first build with
    /// the feature) shows the current version only; (5) a downgrade shows nothing; (6) otherwise every released version above the seen one
    /// up to the current one, no cap; a dev build also shows Unreleased when the hash of its English text is not the one last shown.
    /// </summary>
    internal static IReadOnlyList<ChangelogSection> Decide(Changelog? log, ReleaseVersion current, bool devInstance, bool testRun,
        bool firstInstall, string? seenVersion, string? seenDevHash)
    {
        var none = Array.Empty<ChangelogSection>();
        if (devInstance || testRun) return none;
        if (log == null) return none;
        if (firstInstall || ReleaseVersion.ParseCanonical(seenVersion) is not { } seen) return CurrentOnly(log, current);
        if (current < seen) return none;
        var show = new List<ChangelogSection>();
        if (current.IsDevBuild && log.Unreleased is { } unreleased && log.UnreleasedHash != seenDevHash) show.Add(unreleased);
        show.AddRange(log.Released.Where(s => s.Version!.Value > seen && s.Version.Value <= current));
        return show;
    }

    private static IReadOnlyList<ChangelogSection> CurrentOnly(Changelog log, ReleaseVersion current)
    {
        if (current.IsDevBuild) return log.Unreleased is { } u ? new[] { u } : Array.Empty<ChangelogSection>();
        if (current.IsPrerelease) return Array.Empty<ChangelogSection>();
        return log.Released.Where(s => s.Version!.Value.CompareTo(current) == 0).Take(1).ToList();
    }

    /// <summary>
    /// Records that the window has been shown for <paramref name="current"/> (automatically or opened by hand): the seen version becomes
    /// the higher of the two, and a dev build also remembers the hash of its Unreleased text. Saves when something changed; returns
    /// whether it did. Call it only once the window is on screen.
    /// </summary>
    internal static bool MarkSeen(Settings settings, Changelog? log, ReleaseVersion current)
    {
        bool changed = false;
        if (ReleaseVersion.ParseCanonical(settings.WhatsNewSeen) is not { } seen || current > seen)
        {
            settings.WhatsNewSeen = current.ToString();
            changed = true;
        }
        if (current.IsDevBuild && log?.UnreleasedHash is { } hash && settings.WhatsNewSeenDevHash != hash)
        {
            settings.WhatsNewSeenDevHash = hash;
            changed = true;
        }
        if (changed) settings.Save();
        return changed;
    }
}
