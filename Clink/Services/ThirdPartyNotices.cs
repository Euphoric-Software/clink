using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Clink.Services;

/// <summary>One section of THIRD-PARTY-NOTICES.txt: the header block's fields and the licence text under it.</summary>
internal sealed record NoticeSection(string Id, string Title, string IncludedIn, string Source, string Body);

/// <summary>
/// The third-party notices (<c>THIRD-PARTY-NOTICES.txt</c> at the repository root) as the exe carries them: an embedded resource, one
/// superset file for every download. tools\notices\make-notices.ps1 builds and checks it. The App tab's "Third-party notices" link
/// shows it (<see cref="Clink.UI.ThirdPartyNoticesWindow"/>).
/// </summary>
internal static class ThirdPartyNotices
{
    internal const string ResourceName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>The line the file's preface carries; its value is the .NET runtime the notices were pinned to (the one a self-contained Clink.exe bundles).</summary>
    internal const string RuntimeLinePrefix = "Pinned .NET runtime version (self-contained Clink.exe): ";

    /// <summary>The rule line of a header block: exactly 80 "=" (tools\notices\make-notices.ps1 writes and checks the same line).</summary>
    internal static readonly string RuleLine = new('=', 80);

    /// <summary>The sections every build must show (the superset file has more: Clink's own licence first, and the build-only and installer notes).</summary>
    internal static readonly IReadOnlyList<(string Id, string Title)> RequiredSections = new (string, string)[]
    {
        ("1", ".NET Runtime licence"),
        ("2", ".NET Runtime third-party notices"),
        ("3", "Windows Desktop runtime licence (WPF and Windows Forms)"),
        ("4", "WPF third-party notices"),
        ("5", "Windows Forms third-party notices"),
        ("6", "CsWin32 licence (MIT)"),
        ("6b", "CsWin32 NOTICE (build-time dependencies of the CsWin32 source generator; not shipped in Clink)"),
        ("7", "C#/WinRT runtime (WinRT.Runtime.dll 2.2.0.48161) licence (MIT)"),
        ("8", "Windows SDK for .NET (Microsoft.Windows.SDK.NET.dll 10.0.26100.55)"),
    };

    private static readonly Lazy<string> s_text = new(ReadResource);

    /// <summary>The notices text, read once. If the resource is missing or unreadable, a one-line explanation instead (never throws).</summary>
    internal static string Load() => s_text.Value;

    private static string ReadResource()
    {
        try
        {
            using var s = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) return $"The {ResourceName} resource is missing from this build.";
            using var reader = new StreamReader(s, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) { return $"{ResourceName} could not be read ({ex.GetType().Name})."; }
    }

    private static bool IsRule(string line) => string.Equals(line, RuleLine, StringComparison.Ordinal);

    /// <summary>
    /// Splits the file into its sections. A header is five lines: a rule of "=", "[id] title", "Included in: ...", "Source: ...", a rule.
    /// The body is what follows up to the next header, without the blank lines around it. Pure; CRLF or LF.
    /// </summary>
    internal static List<NoticeSection> ParseSections(string? text)
    {
        var sections = new List<NoticeSection>();
        if (string.IsNullOrEmpty(text)) return sections;
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var headerMatch = new Regex(@"^\[([^\]]+)\] (.+)$");
        var starts = new List<int>();
        for (int i = 0; i + 4 < lines.Length; i++)
            if (IsRule(lines[i]) && headerMatch.IsMatch(lines[i + 1]) && lines[i + 2].StartsWith("Included in: ", StringComparison.Ordinal)
                && lines[i + 3].StartsWith("Source: ", StringComparison.Ordinal) && IsRule(lines[i + 4]))
                starts.Add(i);
        for (int s = 0; s < starts.Count; s++)
        {
            int first = starts[s];
            int end = s + 1 < starts.Count ? starts[s + 1] : lines.Length;
            var m = headerMatch.Match(lines[first + 1]);
            string body = string.Join("\n", lines.Skip(first + 5).Take(end - first - 5)).Trim('\n');
            sections.Add(new NoticeSection(m.Groups[1].Value, m.Groups[2].Value,
                lines[first + 2]["Included in: ".Length..], lines[first + 3]["Source: ".Length..], body));
        }
        return sections;
    }

    /// <summary>The .NET runtime version the notices were pinned to (from the preface line), or null if the line is missing or not a version.</summary>
    internal static Version? RuntimeVersionInNotices(string? text = null)
    {
        foreach (string raw in (text ?? Load()).Replace("\r\n", "\n").Split('\n'))
            if (raw.StartsWith(RuntimeLinePrefix, StringComparison.Ordinal))
                return Version.TryParse(raw[RuntimeLinePrefix.Length..].Trim(), out var v) ? v : null;
        return null;
    }
}
