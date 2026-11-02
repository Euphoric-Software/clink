using System.Globalization;
using System.Text.RegularExpressions;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// The rules every catalog must keep (checked against what is embedded, and against made-up catalogs so the checks themselves are
/// tested), the fallback chain, the pseudo-locale and the number/size/time helpers.
/// </summary>
internal static class LocalizationCatalogTests
{
    /// <summary>
    /// Switched on by the slice that ships the last language: every Known language must then be Available (embedded and complete).
    /// Until then a language may be missing or partial: it simply is not offered.
    /// </summary>
    internal static readonly bool RequireAllKnownAvailable = false;

    // ------------------------------------------------------------------ the rules

    private static readonly Regex s_protected = new(@"https?://[^\s]+|Copium\+?|Clink|\.mp4\b|\.png\b|--[a-z][a-z0-9-]*|eup_\w+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string EnglishOf(string key) => L.EnglishOf(key);

    private static IEnumerable<string> Protected(string english) =>
        s_protected.Matches(english).Select(m => Regex.Replace(m.Value, @"[).,;:!?]+$", "")).Where(s => s.Length > 0).Distinct();

    private static bool MayOmitCount(string form) => form is "zero" or "one" or "two";

    private static string[] AllowedForms(string locale)
    {
        var allowed = PluralRules.Required(locale).ToList();
        if (locale is "es" or "fr" or "it" or "pt-BR") allowed.Add("many");
        return allowed.ToArray();
    }

    /// <summary>What is wrong with the English source catalog itself (empty = fine).</summary>
    internal static List<string> EnglishProblems(Catalog en)
    {
        var bad = new List<string>();
        foreach (var (key, e) in en.Entries)
        {
            string english = EnglishOf(key);
            if (e.Text != null)
            {
                if (e.Text.Length == 0) bad.Add($"en \"{key}\": empty value");
                if (e.Text != english) bad.Add($"en \"{key}\": the value must be the key (or the text after \"context|\"): \"{e.Text}\"");
            }
            else
            {
                CheckForms(bad, "en", key, e.Forms!, "en", e.Forms!.GetValueOrDefault("other"));
                if (e.Forms!.GetValueOrDefault("other") != english) bad.Add($"en \"{key}\": the \"other\" form must be the key text \"{english}\"");
            }
            if (key.Contains('{') && !Regex.IsMatch(key, @"^(?:[^{}]|\{[A-Za-z_][A-Za-z0-9_]*\})*$")) bad.Add($"en \"{key}\": placeholders are {{name}} only, no format specifiers or literal braces");
        }
        return bad;
    }

    private static void CheckForms(List<string> bad, string locale, string key, IReadOnlyDictionary<string, string> forms, string pluralLocale, string? other)
    {
        var allowed = AllowedForms(pluralLocale);
        foreach (var need in PluralRules.Required(pluralLocale))
            if (!forms.ContainsKey(need)) bad.Add($"{locale} \"{key}\": missing the plural form \"{need}\"");
        foreach (var have in forms.Keys)
            if (!allowed.Contains(have)) bad.Add($"{locale} \"{key}\": the plural form \"{have}\" is not used by this language");
        if (other == null) return;
        var otherPh = L.PlaceholdersOf(other);
        foreach (var (form, text) in forms)
        {
            var ph = L.PlaceholdersOf(text);
            bool same = ph.SetEquals(otherPh) || (MayOmitCount(form) && ph.SetEquals(otherPh.Where(p => p != "count")));
            if (!same) bad.Add($"{locale} \"{key}\": form \"{form}\" has the placeholders {{{string.Join(",", ph)}}}, the \"other\" form has {{{string.Join(",", otherPh)}}}");
        }
    }

    /// <summary>What is wrong with a translation compared with English (empty = fine).</summary>
    internal static List<string> TranslationProblems(Catalog en, Catalog loc, string locale, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> glossary)
    {
        var bad = new List<string>();
        foreach (var key in en.Entries.Keys)
            if (!loc.Entries.ContainsKey(key)) bad.Add($"{locale}: missing \"{key}\"");
        foreach (var key in loc.Entries.Keys)
            if (!en.Entries.ContainsKey(key)) bad.Add($"{locale}: \"{key}\" is not in English");
        foreach (var (key, e) in loc.Entries)
        {
            if (!en.Entries.TryGetValue(key, out var source)) continue;
            string english = EnglishOf(key);
            if ((e.Text == null) != (source.Text == null)) { bad.Add($"{locale} \"{key}\": a text where English has a plural, or the other way round"); continue; }
            if (e.Text != null)
            {
                if (e.Text.Length == 0) bad.Add($"{locale} \"{key}\": empty value");
                if (!L.PlaceholdersOf(e.Text).SetEquals(L.PlaceholdersOf(source.Text!))) bad.Add($"{locale} \"{key}\": placeholders differ from English");
                foreach (var p in Protected(english))
                    if (!e.Text.Contains(p, StringComparison.Ordinal)) bad.Add($"{locale} \"{key}\": \"{p}\" must stay as is");
                if (glossary.TryGetValue(key, out var terms) && terms.TryGetValue(locale, out var approved) && e.Text != approved)
                    bad.Add($"{locale} \"{key}\": the glossary says \"{approved}\", not \"{e.Text}\"");
            }
            else
            {
                CheckForms(bad, locale, key, e.Forms!, locale, e.Forms!.GetValueOrDefault("other"));
                var enOther = L.PlaceholdersOf(source.Forms!["other"]);
                foreach (var (form, text) in e.Forms!)
                {
                    var ph = L.PlaceholdersOf(text);
                    if (!(ph.SetEquals(enOther) || (MayOmitCount(form) && ph.SetEquals(enOther.Where(p => p != "count"))))) bad.Add($"{locale} \"{key}\": form \"{form}\" placeholders differ from English");
                    foreach (var p in Protected(english))
                        if (form == "other" && !text.Contains(p, StringComparison.Ordinal)) bad.Add($"{locale} \"{key}\": \"{p}\" must stay as is");
                }
            }
        }
        return bad;
    }

    private static CatalogSet Make(params (string locale, string json)[] files) =>
        CatalogSet.Build(files.Select(f => new CatalogSource(f.locale, "t", f.json)));

    // ------------------------------------------------------------------ what is embedded

    [UnitTest]
    public static void The_embedded_catalogs_and_glossary_load_without_problems()
    {
        var set = L.Embedded;
        Check.Equal(0, set.Problems.Count, string.Join("; ", set.Problems.SelectMany(p => p.Value)));
        Check.True(set.Locales.ContainsKey("en"), "English is embedded");
        foreach (var code in set.Present) Check.True(L.Known.Any(k => k.Code == code), "folder \"" + code + "\" is a Known language");
        Check.True(set.English.Entries.Count >= 5, "English has the unit templates");
        foreach (var unit in new[] { "unit|{n} kB", "unit|{n} MB", "unit|{n} GB", "unit|{n} s", "unit|{n} h" })
            Check.True(set.English.Entries.ContainsKey(unit), unit);
    }

    [UnitTest]
    public static void English_follows_the_source_rules()
    {
        Check.Equal(Array.Empty<string>(), EnglishProblems(L.Embedded.English).ToArray());
    }

    [UnitTest]
    public static void Every_embedded_language_matches_English_key_for_key()
    {
        var set = L.Embedded;
        foreach (var (code, cat) in set.Locales)
        {
            if (code == "en") continue;
            Check.Equal(Array.Empty<string>(), TranslationProblems(set.English, cat, code, set.Glossary).ToArray(), code);
        }
    }

    /// <summary>The keys whose translation has not the blank lines ("\n\n" paragraphs) or line breaks ("\n") of the English text: the layout (a message box, a hint) is built around them.</summary>
    private static List<string> LineBreakProblems(Catalog english, Catalog translated, string code)
    {
        static int Count(string text, string what) => (text.Length - text.Replace(what, "").Length) / what.Length;
        static IEnumerable<string> Texts(Entry e) => e.Text != null ? new[] { e.Text } : e.Forms!.Values;
        var problems = new List<string>();
        foreach (var (key, entry) in translated.Entries)
        {
            if (!english.Entries.TryGetValue(key, out var en)) continue;
            string reference = en.Primary;
            foreach (string text in Texts(entry))
            {
                int blank = Count(text, "\n\n"), lines = Count(text, "\n");
                if (blank != Count(reference, "\n\n") || lines != Count(reference, "\n"))
                    problems.Add($"{code}: \"{key}\" has {blank} blank line(s) and {lines} line break(s), English has {Count(reference, "\n\n")} and {Count(reference, "\n")}");
            }
        }
        return problems;
    }

    [UnitTest]
    public static void Every_translation_keeps_the_blank_lines_and_line_breaks_of_its_English_text()
    {
        var set = L.Embedded;
        foreach (var (code, cat) in set.Locales)
        {
            if (code == "en") continue;
            Check.Equal(Array.Empty<string>(), LineBreakProblems(set.English, cat, code).ToArray(), code);
        }
        // the check itself: a paragraph that was joined, and one that was added, are found; the same breaks pass
        var made = CatalogSet.Build(new[]
        {
            new CatalogSource("en", "t", """{ "a": "x\n\ny", "b": "p\nq", "c": "plain" }"""),
            new CatalogSource("de", "t", """{ "a": "x y", "b": "p\nq", "c": "one\n\ntwo" }"""),
        });
        var problems = LineBreakProblems(made.English, made.Locales["de"], "de");
        Check.Equal(2, problems.Count, string.Join(" | ", problems));
        Check.True(problems.Any(p => p.Contains("\"a\"")) && problems.Any(p => p.Contains("\"c\"")), string.Join(" | ", problems));
    }

    [UnitTest]
    public static void The_glossary_covers_known_languages_and_is_consistent_with_English()
    {
        var set = L.Embedded;
        Check.True(set.Glossary.Count >= 9, "the seeded terms");
        foreach (var term in new[] { "Upload", "Copy link", "Share", "Share link", "Sign in", "Sign out", "Delete", "Cancel", "Language" })
        {
            Check.True(set.Glossary.TryGetValue(term, out var per), "glossary has " + term);
            foreach (var k in L.Known.Where(k => k.Code != "en"))
                Check.True(per!.TryGetValue(k.Code, out var text) && text.Length > 0, term + " in " + k.Code);
        }
        foreach (var (term, per) in set.Glossary)
            foreach (var code in per.Keys) Check.True(L.Known.Any(k => k.Code == code && code != "en"), "glossary language " + code);
    }

    [UnitTest]
    public static void Available_is_English_plus_the_complete_languages_and_never_the_pseudo_locale()
    {
        var set = L.Embedded;
        Check.True(set.Available.Contains("en"));
        Check.False(set.Available.Contains(L.PseudoCode));
        foreach (var code in set.Available)
        {
            Check.True(L.Known.Any(k => k.Code == code), code + " is Known");
            if (code != "en") Check.True(set.English.Entries.Keys.All(set.Locales[code].Entries.ContainsKey), code + " has every English key");
        }
        foreach (var k in L.Known.Where(k => !set.Available.Contains(k.Code)))
            Check.True(!set.Locales.TryGetValue(k.Code, out var c) || !set.English.Entries.Keys.All(c.Entries.ContainsKey), k.Code + " is offered only when complete");
        if (RequireAllKnownAvailable)
            Check.Equal(L.Known.Select(k => k.Code).OrderBy(c => c).ToArray(), set.Available.OrderBy(c => c).ToArray(), "every Known language is Available");
    }

    [UnitTest]
    public static void Known_has_the_sixteen_languages_with_native_names_and_only_arabic_is_right_to_left()
    {
        Check.Equal(new[] { "en", "es", "fr", "de", "pt-BR", "it", "nl", "pl", "tr", "ru", "uk", "ar", "hi", "ja", "ko", "zh-CN" }, L.Known.Select(k => k.Code).ToArray());
        Check.Equal(new[] { "ar" }, L.Known.Where(k => k.Rtl).Select(k => k.Code).ToArray());
        Check.Equal("Português (Brasil)", L.Known.First(k => k.Code == "pt-BR").NativeName);
        Check.Equal("简体中文", L.Known.First(k => k.Code == "zh-CN").NativeName);
        foreach (var k in L.Known) Check.NotNull(CultureInfo.GetCultureInfo(k.Culture), k.Code);
    }

    // ------------------------------------------------------------------ the rules themselves, on made-up catalogs

    [UnitTest]
    public static void The_english_rules_catch_a_key_that_differs_from_its_value_and_a_wrong_context_value()
    {
        var good = Make(("en", """{ "Save": "Save", "menu|Open": "Open", "{count} clips": { "one": "{count} clip", "other": "{count} clips" }, "x|{n} items": { "one": "One item", "other": "{n} items" } }""")).English;
        // (a "one" form may drop {count}; but "x|{n} items" uses {n}, not {count}, so its "One item" form drops a placeholder)
        var problems = EnglishProblems(good);
        Check.Equal(1, problems.Count, string.Join("; ", problems));
        Check.Contains(problems[0], "x|{n} items");

        var bad = Make(("en", """{ "Save": "Store", "menu|Open": "menu|Open", "{count} clips": { "one": "{count} clip", "other": "{count} clip" }, "Odd {0:N}": "Odd {0:N}" }""")).English;
        var p = EnglishProblems(bad);
        Check.True(p.Any(x => x.Contains("\"Save\"")), "key != value");
        Check.True(p.Any(x => x.Contains("\"menu|Open\"")), "context value must be the part after |");
        Check.True(p.Any(x => x.Contains("\"{count} clips\"") && x.Contains("other")), "plural other must be the key");
        Check.True(p.Any(x => x.Contains("Odd {0:N}") && x.Contains("placeholders")), "format specifier");
    }

    [UnitTest]
    public static void The_translation_rules_catch_missing_extra_empty_and_mismatched_entries()
    {
        var en = Make(("en", """{ "Save {name}": "Save {name}", "Open Clink": "Open Clink", "Get https://copium.example/x": "Get https://copium.example/x", "Export .mp4 --flag": "Export .mp4 --flag", "Share": "Share", "{count} clips": { "one": "{count} clip", "other": "{count} clips" } }""")).English;
        var glossary = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["Share"] = new Dictionary<string, string> { ["de"] = "Teilen" } };

        var fine = Make(("de", """{ "Save {name}": "{name} sichern", "Open Clink": "Clink öffnen", "Get https://copium.example/x": "Hole https://copium.example/x", "Export .mp4 --flag": "Export .mp4 --flag", "Share": "Teilen", "{count} clips": { "one": "{count} Clip", "other": "{count} Clips" } }""")).Locales["de"];
        Check.Equal(Array.Empty<string>(), TranslationProblems(en, fine, "de", glossary).ToArray());

        var broken = Make(("de", """{ "Save {name}": "Sichern", "Open Clink": "Öffnen", "Get https://copium.example/x": "Hole", "Export .mp4 --flag": "Export", "Share": "Weitergeben", "Extra": "Extra" }""")).Locales["de"];
        var withHalfPlural = new Dictionary<string, Entry>(broken.Entries) { ["{count} clips"] = new Entry(null, new Dictionary<string, string> { ["one"] = "{count} Clip" }) };
        var p = TranslationProblems(en, new Catalog("de", withHalfPlural), "de", glossary);
        Check.True(p.Any(x => x.Contains("\"Save {name}\"") && x.Contains("placeholders")), "placeholder lost");
        Check.True(p.Any(x => x.Contains("\"Open Clink\"") && x.Contains("Clink")), "product name lost");
        Check.True(p.Any(x => x.Contains("https://copium.example/x")), "url lost");
        Check.True(p.Any(x => x.Contains("--flag")) && p.Any(x => x.Contains(".mp4")), "flag and extension lost");
        Check.True(p.Any(x => x.Contains("glossary") && x.Contains("Teilen")), "glossary term");
        Check.True(p.Any(x => x.Contains("\"Extra\"") && x.Contains("not in English")), "extra key");
        Check.True(p.Any(x => x.Contains("missing the plural form \"other\"")), "required plural category");

        var missing = Make(("de", """{ "Share": "Teilen" }""")).Locales["de"];
        Check.True(TranslationProblems(en, missing, "de", glossary).Count(x => x.Contains("missing \"")) == 5, "five keys missing");

        var arabic = Make(("ar", """{ "{count} clips": { "one": "مقطع", "other": "{count} مقطع" } }""")).Locales["ar"];
        Check.True(TranslationProblems(en, arabic, "ar", glossary).Any(x => x.Contains("missing the plural form \"zero\"")), "arabic needs six forms");

        var dropsCount = Make(("fr", """{ "{count} clips": { "one": "{count} clip", "other": "des clips" } }""")).Locales["fr"];
        Check.True(TranslationProblems(en, dropsCount, "fr", glossary).Any(x => x.Contains("placeholders")), "other must keep {count}");
        var oneWithoutCount = Make(("fr", """{ "{count} clips": { "one": "un clip", "other": "{count} clips" } }""")).Locales["fr"];
        Check.False(TranslationProblems(en, oneWithoutCount, "fr", glossary).Any(x => x.Contains("{count} clips\": form \"one\"")), "one may drop {count}");
    }

    // ------------------------------------------------------------------ fallback, missing keys, broken catalogs

    [UnitTest]
    public static void A_key_a_language_lacks_falls_back_to_english_and_a_key_english_lacks_is_returned_and_recorded()
    {
        var set = Make(
            ("en", """{ "Save": "Save", "Open": "Open", "menu|Close": "Close", "Hi {name}": "Hi {name}" }"""),
            ("de", """{ "Save": "Sichern", "menu|Close": "Schließen", "Hi {name}": "Hallo {name}" }"""));
        using var _ = L.UseCatalogs(set);
        L.ClearMissing();
        using (L.Scope("de"))
        {
            Check.Equal("Sichern", L.T("Save"));
            Check.Equal("Open", L.T("Open"), "not in de: English");
            Check.Equal("Schließen", L.T("menu|Close"));
            Check.Equal("Hallo Ann", L.T("Hi {name}", ("name", "Ann")));
            Check.Equal(0, L.Missing.Count, "a fallback to English is not a missing key");
            Check.Equal("Nowhere {x}", L.T("Nowhere {x}"));
            Check.Equal("Nowhere 5", L.T("Nowhere {x}", ("x", 5)), "placeholders are still filled");
            Check.True(L.Missing.Contains("Nowhere {x}"));
        }
        using (L.Scope("en"))
        {
            Check.Equal("Close", L.T("menu|Close"), "a context key shows the text after |");
            Check.Equal("Hi {name}", L.T("Hi {name}"), "no args, no change");
            Check.Equal("Hi {other}", L.T("Hi {other}", ("name", "x")), "an unknown placeholder stays");
        }
        using (L.Scope("fr"))   // no fr catalog at all
            Check.Equal("Save", L.T("Save"));
        Check.Equal("Sichern", L.TIn("de", "Save"), "TIn ignores the active language");
        Check.Equal("Save", L.TIn("en", "Save"));
        Check.Equal("Save", L.TIn("xx", "Save"), "an unknown language is English");
        Check.Equal(2, L.Missing.Count, string.Join(", ", L.Missing));   // "Nowhere {x}" and "Hi {other}" (English lacks both)
        L.ClearMissing();
        Check.Equal(0, L.Missing.Count);
    }

    [UnitTest]
    public static void An_unreadable_catalog_drops_the_whole_language_and_a_bad_entry_drops_only_itself()
    {
        // structurally invalid: the language is dropped (English is used), the problem is recorded
        var unreadable = new (string what, string json)[]
        {
            ("not json", "{ nope"),
            ("root is an array", """["a"]"""),
            ("comment", "{ /* no */ \"Save\": \"S\" }"),
        };
        foreach (var (what, json) in unreadable)
        {
            CatalogSet set = null!;
            try { set = Make(("en", """{ "Save": "Save" }"""), ("de", json)); }
            catch (Exception ex) { Check.Fail(what + ": building threw " + ex.GetType().Name); }
            Check.False(set.Locales.ContainsKey("de"), what + ": de is dropped");
            Check.True(set.Problems.ContainsKey("de") && set.Problems["de"].Count > 0, what + ": the problem is recorded");
            Check.False(set.Available.Contains("de"), what + ": not Available");
            Check.True(set.Locales.ContainsKey("en"), what + ": English is untouched");
            using var _ = L.UseCatalogs(set);
            using var __ = L.Scope("de");
            Check.Equal("Save", L.T("Save"), what + ": English is used");
        }
        // a bad entry: only that entry is skipped (and recorded); the language stays but is incomplete, so not Available
        var badEntries = new (string what, string json)[]
        {
            ("duplicate key, different values", """{ "Save": "Sichern", "Save": "Speichern", "Other": "x" }"""),
            ("empty value", """{ "Save": "", "Other": "x" }"""),
            ("number value", """{ "Save": 5, "Other": "x" }"""),
            ("plural without other", """{ "Save": { "one": "x" }, "Other": "x" }"""),
            ("unknown plural category", """{ "Save": { "other": "x", "several": "y" }, "Other": "x" }"""),
            ("empty plural form", """{ "Save": { "other": "" }, "Other": "x" }"""),
            ("empty key", """{ "": "x", "Other": "x" }"""),
        };
        foreach (var (what, json) in badEntries)
        {
            var set = Make(("en", """{ "Save": "Save", "Other": "Other" }"""), ("de", json));
            Check.True(set.Locales.ContainsKey("de"), what + ": de is kept");
            Check.True(set.Locales["de"].Entries.ContainsKey("Other"), what + ": the good entry is kept");
            Check.False(set.Locales["de"].Entries.ContainsKey("Save") && what != "empty key", what + ": the bad entry is dropped");
            Check.True(set.Problems.ContainsKey("de") && set.Problems["de"].Count > 0, what + ": the problem is recorded");
            Check.False(set.Available.Contains("de"), what + ": incomplete, so not Available");
            using var _ = L.UseCatalogs(set);
            using var __ = L.Scope("de");
            Check.Equal("Save", L.T("Save"), what + ": English is used for the dropped entry");
            Check.Equal("x", L.T("Other"), what + ": ...and the translation where it is good") ;
        }
        // a broken area breaks the whole (non-English) language, a good one next to it does not rescue it
        var two = CatalogSet.Build(new[] { new CatalogSource("de", "a", """{ "A": "a" }"""), new CatalogSource("de", "b", "{ nope") });
        Check.False(two.Locales.ContainsKey("de"));
        // a broken glossary is dropped, the catalogs stay
        var gl = CatalogSet.Build(new[] { new CatalogSource("en", "a", """{ "A": "A" }""") }, "[1]");
        Check.True(gl.Problems.ContainsKey("*"));
        Check.Equal(0, gl.Glossary.Count);
        Check.True(gl.Locales.ContainsKey("en"));
        // no English at all: keys come back as they are
        using var none = L.UseCatalogs(CatalogSet.Build(Array.Empty<CatalogSource>()));
        Check.Equal("Anything", L.T("Anything"));
    }

    [UnitTest]
    public static void The_same_key_in_two_areas_is_fine_with_the_same_value_and_a_problem_with_another()
    {
        var same = CatalogSet.Build(new[] { new CatalogSource("en", "a", """{ "A": "A", "{count} x": { "one": "{count} x", "other": "{count} xs" } }"""), new CatalogSource("en", "b", """{ "A": "A", "{count} x": { "other": "{count} xs", "one": "{count} x" } }""") });
        Check.Equal(0, same.Problems.Count, string.Join("; ", same.Problems.SelectMany(p => p.Value)));
        Check.True(same.English.Entries.ContainsKey("A") && same.English.Entries.ContainsKey("{count} x"));

        var differs = CatalogSet.Build(new[] { new CatalogSource("de", "a", """{ "A": "a", "B": "b" }"""), new CatalogSource("de", "b", """{ "A": "other" }"""), new CatalogSource("de", "c", """{ "A": "a" }""") });
        Check.True(differs.Problems.ContainsKey("de"));
        Check.True(differs.Locales["de"].Entries.ContainsKey("B"), "the rest of the language stays");
        Check.False(differs.Locales["de"].Entries.ContainsKey("A"), "the conflicting key is dropped, even when a third area repeats the first value");

        var differsEn = CatalogSet.Build(new[] { new CatalogSource("en", "a", """{ "A": "A", "B": "B" }"""), new CatalogSource("en", "b", """{ "A": "Other" }""") });
        Check.True(differsEn.Problems.ContainsKey("en"));
        Check.True(differsEn.Locales["en"].Entries.ContainsKey("B"), "English keeps its valid entries");
    }

    [UnitTest]
    public static void English_is_never_dropped_and_other_languages_need_a_non_empty_English()
    {
        var set = CatalogSet.Build(new[]
        {
            new CatalogSource("en", "good", """{ "A": "A", "B": "B" }"""),
            new CatalogSource("en", "bad", "{ nope"),
            new CatalogSource("en", "worse", """{ "C": "" }"""),
            new CatalogSource("de", "t", """{ "A": "a", "B": "b" }"""),
        });
        Check.True(set.Locales.ContainsKey("en"), "English stays");
        Check.Equal(2, set.English.Entries.Count, "its valid entries stay");
        Check.True(set.Problems["en"].Count >= 2, "and the problems are recorded");
        Check.True(set.Available.Contains("de"), "de is complete against the English that is left");

        var noEnglish = CatalogSet.Build(new[] { new CatalogSource("de", "t", """{ "A": "a" }"""), new CatalogSource("fr", "t", """{ }""") });
        Check.Equal(0, noEnglish.English.Entries.Count);
        Check.Equal(new[] { "en" }, noEnglish.Available.ToArray(), "without English entries no other language is Available");
        var emptyEnglish = CatalogSet.Build(new[] { new CatalogSource("en", "t", "{ }"), new CatalogSource("de", "t", """{ "A": "a" }""") });
        Check.Equal(new[] { "en" }, emptyEnglish.Available.ToArray());
    }

    [UnitTest]
    public static void Building_catalogs_never_throws_even_for_odd_input()
    {
        CatalogSet set = null!;
        try { set = CatalogSet.Build(new CatalogSource[] { new("en", "a", null!) }, "{ \"x\": 5 }"); }
        catch (Exception ex) { Check.Fail("Build threw " + ex.GetType().Name); }
        Check.NotNull(set);
        using var _ = L.UseCatalogs(set);
        Check.Equal("Anything", L.T("Anything"), "L.T works on a set that failed to build");
    }
    [UnitTest]
    public static void A_language_is_available_only_when_it_has_every_english_key()
    {
        var set = Make(
            ("en", """{ "A": "A", "B": "B" }"""),
            ("de", """{ "A": "a", "B": "b" }"""),
            ("fr", """{ "A": "a" }"""),
            ("xx", """{ "A": "a", "B": "b" }"""));
        Check.True(set.Available.Contains("en"));
        Check.True(set.Available.Contains("de"));
        Check.False(set.Available.Contains("fr"), "incomplete");
        Check.False(set.Available.Contains("xx"), "not a Known language");
    }

    [UnitTest]
    public static void Catalogs_are_safe_to_read_from_many_threads()
    {
        var set = Make(("en", """{ "Save": "Save", "{count} clips": { "one": "{count} clip", "other": "{count} clips" } }"""));
        using var _ = L.UseCatalogs(set);
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 8, i =>
        {
            for (int n = 0; n < 2000; n++)
            {
                if (L.TIn("en", "Save") != "Save") errors.Add("T");
                if (L.Plural("{count} clips", n % 3) is not ("0 clips" or "1 clip" or "2 clips")) errors.Add("Plural " + n);
            }
        });
        Check.Equal(0, errors.Count, string.Join(",", errors.Take(3)));
    }

    // ------------------------------------------------------------------ pseudo-locale

    [UnitTest]
    public static void The_pseudo_locale_accents_pads_wraps_and_keeps_placeholders_and_product_names()
    {
        string src = "Save {count} clips to Clink and Copium+ as .mp4 with --fast at https://copium.example/a";
        string ps = PseudoLocale.Transform(src);
        Check.True(ps.StartsWith("[!! ", StringComparison.Ordinal) && ps.EndsWith(" !!]", StringComparison.Ordinal), ps);
        Check.True(PseudoLocale.IsPseudo(ps));
        foreach (var keep in new[] { "{count}", "Clink", "Copium+", ".mp4", "--fast", "https://copium.example/a" }) Check.Contains(ps, keep);
        string plain = PseudoLocale.Transform("Save the clip right now");
        Check.True(plain.Length == 23 + 8 + 7, $"padded by about 35% of the letters: 23 -> {plain.Length}");
        string body = ps[4..^4];
        foreach (var keep in new[] { "{count}", "Clink", "Copium+", ".mp4", "--fast", "https://copium.example/a" }) body = body.Replace(keep, "");
        Check.False(body.Any(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') && c != '~'), "every other letter is accented: " + body);
        Check.True(body.Contains('Š') || body.Contains('š'));
        Check.False(PseudoLocale.IsPseudo("Save clip"));
        Check.True(PseudoLocale.Transform("").StartsWith("[!!"));
    }

    [UnitTest]
    public static void The_pseudo_locale_is_made_from_english_with_plural_structure_and_context_values()
    {
        var set = Make(("en", """{ "Save": "Save", "menu|Open": "Open", "{count} clips": { "one": "{count} clip", "other": "{count} clips" } }"""));
        using var _ = L.UseCatalogs(set);
        using (L.Scope("qps-ploc"))
        {
            Check.Equal("qps-ploc", L.Code);
            Check.True(PseudoLocale.IsPseudo(L.T("Save")));
            string open = L.T("menu|Open");
            Check.True(PseudoLocale.IsPseudo(open) && !open.Contains("menu"), "the context is not shown: " + open);
            Check.Equal("[!! ", L.Plural("{count} clips", 1)[..4]);
            Check.Contains(L.Plural("{count} clips", 1), "1");
            Check.False(L.Plural("{count} clips", 1).Contains("clips"), "singular form: " + L.Plural("{count} clips", 1));
            Check.Contains(L.Plural("{count} clips", 5), "5");
            Check.Equal("Unknown key", L.T("Unknown key"), "a key English lacks is returned as is, not framed");
            Check.False(L.IsRtl);
            Check.Equal("en", L.UiCulture.TwoLetterISOLanguageName);
        }
        Check.False(L.Available.Contains("qps-ploc"));
        Check.Equal("en", L.Code, "the scope has ended");
    }

    // ------------------------------------------------------------------ direction, culture, numbers

    [UnitTest]
    public static void Ltr_is_the_identity_in_left_to_right_languages_and_wraps_in_right_to_left_ones()
    {
        foreach (var code in new[] { "en", "de", "ja", "qps-ploc" })
            using (L.Scope(code))
            {
                Check.False(L.IsRtl, code);
                Check.Equal(@"C:\Clips\a.mp4", L.Ltr(@"C:\Clips\a.mp4"), code);
            }
        using (L.Scope("ar"))
        {
            Check.True(L.IsRtl);
            Check.Equal("\u200E\u202AAlt+C\u202C\u200E", L.Ltr("Alt+C"));
            Check.Equal("", L.Ltr(""));
            Check.Equal("ar", L.UiCulture.TwoLetterISOLanguageName);
        }
    }

    private static T WithCulture<T>(string name, Func<T> body)
    {
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); return body(); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [UnitTest]
    public static void The_format_culture_is_always_the_PC_regional_format_whatever_the_ui_language()
    {
        foreach (var ui in new[] { "en", "de", "pt-BR", "zh-CN", "ar", "qps-ploc" })
            using (L.Scope(ui))
                foreach (var pc in new[] { "en-GB", "en-US", "de-DE", "ja-JP", "fr-FR" })
                    Check.Equal(pc, WithCulture(pc, () => L.FormatCulture.Name), "UI " + ui + ", PC " + pc);
        // so English output is the same on every PC that has the same regional format, and a German PC keeps its German numbers in English
        using (L.Scope("en"))
        {
            Check.Equal("1.234,5", WithCulture("de-DE", () => L.Number(1234.5)));
            Check.Equal("1,234.5", WithCulture("en-US", () => L.Number(1234.5)));
        }
        using (L.Scope("de"))
            Check.Equal("1,234.5", WithCulture("en-US", () => L.Number(1234.5)), "a German UI on an English-format PC writes English-format numbers");
    }

    [UnitTest]
    public static void Formatting_under_the_arabic_ui_never_uses_the_arabic_culture_calendar()
    {
        var date = new DateTime(2026, 3, 5, 13, 30, 0);
        using (L.Scope("ar"))
        {
            Check.Equal("ar", L.UiCulture.TwoLetterISOLanguageName);
            foreach (var pc in new[] { "en-US", "de-DE" })
            {
                string shortDate = WithCulture(pc, () => L.Date(date, L.DateKind.ShortDate));
                string longDate = WithCulture(pc, () => L.Date(date, L.DateKind.LongDate));
                Check.Contains(shortDate, "2026", pc + " short date: " + shortDate);
                Check.Contains(longDate, "2026", pc + " long date: " + longDate);
                Check.Contains(WithCulture(pc, () => L.TIn("ar", "on {d}", ("d", date))), "2026", pc + " via TIn");
                Check.Equal(date.ToString("d", CultureInfo.GetCultureInfo(pc)), shortDate);
            }
            // no code path formats with the locale's own culture: a Hijri year (14xx/15xx) never appears for a Gregorian PC
            string viaT = WithCulture("en-US", () => L.T("on {d}", ("d", date)));
            Check.DoesNotContain(viaT, "144");
        }
    }
    [UnitTest]
    public static void Bytes_duration_and_clip_length_match_the_english_output_of_Capabilities()
    {
        WithCulture("en-US", () =>
        {
            foreach (long b in new long[] { -5, 0, 1, 499, 500, 999, 820_000, 999_499, 999_999, 1_000_000, 9_500_000, 9_949_999, 10_000_000, 190_000_000, 999_999_999, 1_000_000_000, 1_200_000_000, 9_999_999_999, 10_000_000_000, 12_000_000_000 })
                Check.Equal(Capabilities.FormatBytes(b), L.Bytes(b), "bytes " + b);
            foreach (int s in new[] { -3, 0, 1, 15, 59, 60, 61, 90, 599, 600, 3599, 3600, 3601, 3900, 3605, 7200, 86400 })
                Check.Equal(Capabilities.FormatDuration(s), L.Duration(s), "duration " + s);
            foreach (double s in new[] { -1, 0, 0.4, 42, 59.9, 60, 725, 3599, 3600, 3603, 7322.5 })
                Check.Equal(Capabilities.FormatClipLength(s), L.ClipLength(s), "clip length " + s);
            return 0;
        });
        Check.Equal("9.5 MB", WithCulture("en-US", () => L.Bytes(9_500_000)));
        Check.Equal("1:05:00", L.Duration(3900));
        Check.Equal("1 h", L.Duration(3600));
        Check.Equal("15 s", L.Duration(15));
    }

    [UnitTest]
    public static void Numbers_and_dates_follow_the_format_culture()
    {
        using (L.Scope("de"))
        {
            Check.Equal("9,5 MB", WithCulture("de-DE", () => L.Bytes(9_500_000)), "units are English until de has its catalog; the number is German");
            Check.Equal("1.234,5", WithCulture("de-DE", () => L.Number(1234.5)));
            Check.Equal("05.03.2026", WithCulture("de-DE", () => L.Date(new DateTime(2026, 3, 5), L.DateKind.ShortDate)));
        }
        using (L.Scope("en"))
        {
            Check.Equal("1,234.5", WithCulture("en-US", () => L.Number(1234.5)));
            Check.Equal("3/5/2026", WithCulture("en-US", () => L.Date(new DateTime(2026, 3, 5), L.DateKind.ShortDate)));
            Check.Equal("Thursday, March 5, 2026", WithCulture("en-US", () => L.Date(new DateTime(2026, 3, 5), L.DateKind.LongDate)));
            Check.Equal(new DateTime(2026, 3, 5, 13, 30, 0).ToString("t", CultureInfo.GetCultureInfo("en-US")), WithCulture("en-US", () => L.Date(new DateTime(2026, 3, 5, 13, 30, 0), L.DateKind.Time)));
            Check.Equal(new DateTime(2026, 3, 5, 13, 30, 0).ToString("g", CultureInfo.GetCultureInfo("en-US")), WithCulture("en-US", () => L.Date(new DateTime(2026, 3, 5, 13, 30, 0), L.DateKind.ShortDateTime)));
        }
    }

    [UnitTest]
    public static void Init_sets_the_active_language_and_the_scope_restores_it()
    {
        L.Init("auto");
        string chosen = L.Code;
        using (L.Scope("ja")) Check.Equal("ja", L.Code);
        Check.Equal(chosen, L.Code);
        using (L.Scope("pt-br")) Check.Equal("pt-BR", L.Code, "case-insensitive");
        using (L.Scope("zz")) Check.Equal("en", L.Code, "unknown: English");
        Check.Equal("pt-BR", L.CanonicalCode("pt-br"));
        Check.Equal("zh-CN", L.CanonicalCode(" ZH_cn "));
        Check.Null(L.CanonicalCode("auto"));
        Check.Null(L.CanonicalCode("qps-ploc"));
        Check.Null(L.CanonicalCode(null));
    }

    [UnitTest]
    public static void The_UI_culture_is_applied_unless_the_setting_is_auto_and_English_was_resolved()
    {
        Check.False(L.ShouldApplyUiCulture("auto", "en"), "auto -> en: Windows' culture stays");
        Check.False(L.ShouldApplyUiCulture(null, "en"));
        Check.False(L.ShouldApplyUiCulture("xx", "en"), "an unknown code is auto");
        Check.False(L.ShouldApplyUiCulture("de", "en"), "an explicit language that was unavailable fell back through auto");
        Check.True(L.ShouldApplyUiCulture("en", "en"), "explicit English");
        Check.True(L.ShouldApplyUiCulture("EN", "en"));
        Check.True(L.ShouldApplyUiCulture("auto", "de"));
        Check.True(L.ShouldApplyUiCulture("ar", "ar"));
        Check.True(L.ShouldApplyUiCulture("de", "pt-BR"));
    }

    [UnitTest]
    public static void A_missing_context_key_shows_its_English_text_never_the_context_prefix()
    {
        using var _ = L.UseCatalogs(CatalogSet.Build(Array.Empty<CatalogSource>()));
        Check.Equal("820 kB", L.Bytes(820000));
        Check.Equal("15 s", L.Duration(15));
        Check.Equal("Nothing here", L.T("Nothing here"), "a plain missing key is returned as is");
        Check.Equal("Hello 3", L.T("greeting|Hello {n}", ("n", 3)));
        Check.Equal("3 clips", L.Plural("plural|{count} clips", 3));
    }

    [UnitTest]
    public static void The_runner_pins_English_for_every_test()
    {
        // leave a language scope and a swapped-in catalog set undisposed, as a test that forgot them would, then run the runner's reset
        var leakedCatalogs = L.UseCatalogs(Make(("en", """{ "Save clip": "Save clip" }"""), ("de", """{ "Save clip": "Clip sichern" }""")));
        var leakedScope = L.Scope("de");
        Check.Equal("Clip sichern", L.T("Save clip"), "the leak is real");
        TestRunner.ResetSharedForTest();   // the runner's own reset: deleting its L.PinEnglish() call fails this test
        Check.Equal("en", L.Code);
        Check.Equal("Save clip", L.T("Save clip"));
        Check.True(ReferenceEquals(L.Catalogs, L.Embedded), "the embedded catalogs are back");
        GC.KeepAlive(leakedCatalogs); GC.KeepAlive(leakedScope);
    }

    // ------------------------------------------------------------------ the {l:T} markup extension

    [UnitTest]
    public static void The_markup_extension_gives_the_catalog_text_wherever_a_string_goes()
    {
        var set = Make(("en", """{ "Save clip": "Save clip", "Don't save": "Don't save", "Clip window": "Clip window", "Name": "Name", "Settings": "Settings", "Tip": "Tip", "Styled": "Styled", "Inside": "Inside" }"""));
        using var _ = L.UseCatalogs(set);
        const string xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:l="http://schemas.euphoricsoftware.com/clink"
                    Title="{l:T 'Clip window'}">
              <StackPanel>
                <StackPanel.Resources>
                  <Style x:Key="S" TargetType="TextBlock"><Setter Property="Text" Value="{l:T 'Styled'}"/></Style>
                  <ControlTemplate x:Key="T" TargetType="Button"><TextBlock Text="{l:T 'Inside'}"/></ControlTemplate>
                </StackPanel.Resources>
                <TextBlock x:Name="A" Text="{l:T 'Save clip'}" ToolTip="{l:T 'Tip'}"/>
                <Button x:Name="B" Content="{l:T 'Don\'t save'}" AutomationProperties.Name="{l:T 'Name'}"/>
                <Expander x:Name="C" Header="{l:T 'Settings'}"/>
                <TextBlock x:Name="D" Style="{StaticResource S}"/>
                <Button x:Name="E" Template="{StaticResource T}"/>
                <TextBlock x:Name="F" Text="{l:T Key='Save clip'}"/>
              </StackPanel>
            </Window>
            """;
        string[] Read(string locale)
        {
            using var __ = L.Scope(locale);
            var w = (System.Windows.Window)System.Windows.Markup.XamlReader.Parse(xaml);
            var sp = (System.Windows.Controls.StackPanel)w.Content;
            var d = (System.Windows.Controls.TextBlock)sp.Children[3];
            var e = (System.Windows.Controls.Button)sp.Children[4];
            e.ApplyTemplate();
            var inside = (System.Windows.Controls.TextBlock)System.Windows.Media.VisualTreeHelper.GetChild(e, 0);
            return new[]
            {
                w.Title,
                ((System.Windows.Controls.TextBlock)sp.Children[0]).Text,
                (string)((System.Windows.Controls.TextBlock)sp.Children[0]).ToolTip,
                (string)((System.Windows.Controls.Button)sp.Children[1]).Content,
                System.Windows.Automation.AutomationProperties.GetName(sp.Children[1]),
                (string)((System.Windows.Controls.Expander)sp.Children[2]).Header,
                d.Text,
                inside.Text,
                ((System.Windows.Controls.TextBlock)sp.Children[5]).Text,
            };
        }
        Check.Equal(new[] { "Clip window", "Save clip", "Tip", "Don't save", "Name", "Settings", "Styled", "Inside", "Save clip" }, Read("en"));
        string[] ps = Read("qps-ploc");
        Check.True(ps.All(PseudoLocale.IsPseudo), string.Join(" | ", ps));
        Check.Contains(ps[3], "Ďóñ'ţ", "the apostrophe survives the escaping");
    }
}