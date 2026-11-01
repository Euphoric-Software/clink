using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clink.Tests;

/// <summary>A catalog key a source file asks for.</summary>
internal readonly record struct ScanHit(string File, string Key);

/// <summary>
/// The source-scan half of the localization tests: reads the sources of a slice and lists the catalog keys they ask for (C# calls and
/// XAML <c>{l:T '...'}</c>), so a test can compare them with the catalog files. Shared by the per-slice tree tests.
/// </summary>
internal static class L10nSourceScan
{
    /// <summary>The call prefixes whose first string literal is a catalog key (every slice picks the ones its files use).</summary>
    public const string T = "L.T(", Plural = "L.Plural(", TInEn = "L.TIn(\"en\",", TrLocale = "Tr(locale,", Skipped = "Skipped(", SkippedBy = "SkippedBy(", Tip = "Tip(", NoteOf = "Note.Of(", NotePluralOf = "Note.PluralOf(", SayT = "SayT(";

    /// <summary>
    /// The folder that holds Clink.csproj, found by walking up from the test exe. Null when the tests do not run from a checkout; that
    /// is a failure on CI (GITHUB_ACTIONS=true), where finding no sources would mean the scan silently stopped checking.
    /// </summary>
    public static string? Root()
    {
        string? found = Root(AppContext.BaseDirectory);
        if (found == null && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory + ": the key scan read nothing");
        return found;
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/>; at each ancestor D returns D when D\Clink.csproj exists, or D\Clink when D\Clink\Clink.csproj
    /// exists (the repo layout is &lt;repo&gt;\Clink\Clink.csproj, so a published exe under &lt;repo&gt;\dist finds it that way). Null when none.
    /// </summary>
    internal static string? Root(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Clink.csproj"))) return dir.FullName;
            string sub = Path.Combine(dir.FullName, "Clink");
            if (File.Exists(Path.Combine(sub, "Clink.csproj"))) return sub;
        }
        return null;
    }

    /// <summary>The keys (names) of Locales/en/&lt;area&gt;.json, read from the source tree.</summary>
    public static HashSet<string> AreaKeys(string root, string area)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Locales", "en", area + ".json")));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The first string literal after each of the <paramref name="callers"/> (e.g. <see cref="T"/>) in the files, unescaped as C#.</summary>
    public static List<ScanHit> CSharp(string root, IEnumerable<string> files, IEnumerable<string> callers)
    {
        var re = new Regex(@"(?<![\w])(?:" + string.Join("|", callers.Select(Regex.Escape)) + @")\s*\(?\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        var hits = new List<ScanHit>();
        foreach (string file in files)
            foreach (Match m in re.Matches(File.ReadAllText(Path.Combine(root, file))))
                hits.Add(new ScanHit(file, UnescapeCSharp(m.Groups[1].Value)));
        return hits;
    }

    /// <summary>Every <c>{l:T '...'}</c> of the files (the attribute is XML-escaped; an apostrophe is written \', a backslash \\).</summary>
    public static List<ScanHit> Xaml(string root, IEnumerable<string> files)
    {
        var re = new Regex(@"\{l:T '((?:[^'\\]|\\.)*)'\}", RegexOptions.Compiled);
        var hits = new List<ScanHit>();
        foreach (string file in files)
            foreach (Match m in re.Matches(File.ReadAllText(Path.Combine(root, file))))
                hits.Add(new ScanHit(file, UnescapeXaml(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))));
        return hits;
    }

    /// <summary>\n, \", \' and \\ of a C# string literal (one pass: "\\n" is a backslash and an n).</summary>
    internal static string UnescapeCSharp(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n is 'n') { sb.Append('\n'); i++; continue; }
                if (n is '"' or '\'' or '\\') { sb.Append(n); i++; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>\' and \\ of a XAML markup-extension string (nothing else is an escape there).</summary>
    internal static string UnescapeXaml(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '\'' or '\\') { sb.Append(s[i + 1]); i++; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
