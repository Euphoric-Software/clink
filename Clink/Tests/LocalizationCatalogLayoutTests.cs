using System.IO;
using System.Text.Json;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// Where the English keys live: each key in exactly one embedded file (words the areas share are in core.json, once), and the glossary
/// terms that are keys are in core.json.
/// </summary>
internal static class LocalizationCatalogLayoutTests
{
    private const string Prefix = "ClinkLocales/en/";

    /// <summary>File name ("core.json", "main.json", ...) -> its keys, in file order, as embedded in this exe.</summary>
    private static Dictionary<string, List<string>> EmbeddedEnglishFiles()
    {
        var asm = typeof(L).Assembly;
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string raw in asm.GetManifestResourceNames())
        {
            string name = raw.Replace('\\', '/');
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.IndexOf('/', Prefix.Length) >= 0) continue;
            using var stream = asm.GetManifestResourceStream(raw)!;
            using var doc = JsonDocument.Parse(new StreamReader(stream).ReadToEnd());
            files[name[Prefix.Length..]] = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        }
        return files;
    }

    [UnitTest]
    public static void No_English_key_is_in_more_than_one_catalog_file()
    {
        var files = EmbeddedEnglishFiles();
        Check.True(files.ContainsKey("core.json") && files.Count >= 4, "found the embedded English files: " + string.Join(", ", files.Keys));
        var where = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (file, keys) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            foreach (string key in keys)
            {
                if (!where.TryGetValue(key, out var list)) where[key] = list = new List<string>();
                list.Add(file);
            }
        var dups = where.Where(w => w.Value.Count > 1).OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => "\"" + w.Key + "\" is in " + string.Join(" and ", w.Value)).ToArray();
        Check.Equal(Array.Empty<string>(), dups, "keys in more than one Locales/en file (a shared word belongs in core.json only)");
    }

    [UnitTest]
    public static void The_glossary_terms_that_are_English_keys_live_in_core_json()
    {
        var files = EmbeddedEnglishFiles();
        var core = files["core.json"].ToHashSet(StringComparer.Ordinal);
        var terms = L.Embedded.Glossary.Keys.ToList();
        Check.True(terms.Count > 0, "the glossary is embedded");
        var misplaced = new List<string>();
        foreach (string term in terms)
            foreach (var (file, keys) in files)
                if (file != "core.json" && keys.Contains(term, StringComparer.Ordinal)) misplaced.Add("glossary term \"" + term + "\" is a key of " + file + ", not core.json");
        Check.Equal(Array.Empty<string>(), misplaced.ToArray(), "glossary terms outside core.json");
        Check.True(terms.Count(core.Contains) >= 5, "the shared glossary words are in core.json: " + string.Join(", ", terms.Where(core.Contains)));
    }
}
