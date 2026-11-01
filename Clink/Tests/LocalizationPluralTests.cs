using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>CLDR plural categories of whole numbers for the 16 UI languages, and how L.Plural picks a form.</summary>
internal static class LocalizationPluralTests
{
    private static void Expect(string locale, string category, params long[] counts)
    {
        foreach (long n in counts) Check.Equal(category, PluralRules.Category(locale, n), $"{locale} {n}");
    }

    [UnitTest]
    public static void Japanese_korean_and_chinese_have_only_other()
    {
        foreach (var l in new[] { "ja", "ko", "zh-CN" }) Expect(l, "other", 0, 1, 2, 5, 11, 100, 1_000_000);
    }

    [UnitTest]
    public static void English_german_dutch_turkish_spanish_italian_have_one_for_exactly_1()
    {
        foreach (var l in new[] { "en", "de", "nl", "tr", "es", "it" })
        {
            Expect(l, "one", 1);
            Expect(l, "other", 0, 2, 3, 11, 21, 100, 101);
        }
    }

    [UnitTest]
    public static void French_portuguese_and_hindi_have_one_for_0_and_1()
    {
        foreach (var l in new[] { "fr", "pt-BR", "hi" })
        {
            Expect(l, "one", 0, 1);
            Expect(l, "other", 2, 3, 10, 100);
        }
    }

    [UnitTest]
    public static void Exact_millions_are_many_in_spanish_french_italian_and_portuguese_only()
    {
        foreach (var l in new[] { "es", "fr", "it", "pt-BR" })
        {
            Check.Equal("many", PluralRules.CategoryWithMillions(l, 1_000_000), l);
            Check.Equal("many", PluralRules.CategoryWithMillions(l, 3_000_000), l);
            Check.Equal("other", PluralRules.CategoryWithMillions(l, 1_000_001), l);
            Check.Equal("other", PluralRules.CategoryWithMillions(l, 5), l);
        }
        foreach (var l in new[] { "en", "de", "tr", "hi", "ja" }) Check.NotEqual("many", PluralRules.CategoryWithMillions(l, 1_000_000), l);
        Check.Equal("other", PluralRules.CategoryWithMillions("en", 1_000_000));
        Check.Equal("one", PluralRules.CategoryWithMillions("fr", 1));
    }

    [UnitTest]
    public static void Russian_and_ukrainian_follow_the_east_slavic_rules()
    {
        foreach (var l in new[] { "ru", "uk" })
        {
            Expect(l, "one", 1, 21, 31, 101, 1001);
            Expect(l, "few", 2, 3, 4, 22, 23, 24, 102, 1002);
            Expect(l, "many", 0, 5, 6, 10, 11, 12, 13, 14, 15, 19, 20, 25, 30, 100, 111, 112, 113, 114, 211);
        }
    }

    [UnitTest]
    public static void Polish_follows_its_own_rules()
    {
        Expect("pl", "one", 1);
        Expect("pl", "few", 2, 3, 4, 22, 23, 24, 32, 102, 104);
        Expect("pl", "many", 0, 5, 6, 9, 10, 11, 12, 13, 14, 15, 20, 21, 25, 31, 100, 101, 111, 112, 114, 115);
    }

    [UnitTest]
    public static void Arabic_has_six_categories()
    {
        Expect("ar", "zero", 0);
        Expect("ar", "one", 1);
        Expect("ar", "two", 2);
        Expect("ar", "few", 3, 4, 5, 10, 103, 110, 203);
        Expect("ar", "many", 11, 12, 26, 50, 99, 111, 199);
        Expect("ar", "other", 100, 101, 102, 200, 201, 202, 1000);
    }

    [UnitTest]
    public static void A_negative_count_is_judged_by_its_size_and_an_unknown_language_by_English()
    {
        Check.Equal("one", PluralRules.Category("en", -1));
        Check.Equal("few", PluralRules.Category("ru", -3));
        Check.Equal("one", PluralRules.Category("xx", 1));
        Check.Equal("other", PluralRules.Category("xx", 2));
        Check.Equal("other", PluralRules.Category("ja", long.MinValue));
    }

    [UnitTest]
    public static void Every_known_language_has_a_required_category_list_ending_in_other()
    {
        Check.Equal(new[] { "other" }, PluralRules.Required("ja").ToArray());
        Check.Equal(new[] { "other" }, PluralRules.Required("ko").ToArray());
        Check.Equal(new[] { "other" }, PluralRules.Required("zh-CN").ToArray());
        foreach (var l in new[] { "en", "de", "nl", "tr", "hi", "es", "it", "fr", "pt-BR" }) Check.Equal(new[] { "one", "other" }, PluralRules.Required(l).ToArray(), l);
        foreach (var l in new[] { "ru", "uk", "pl" }) Check.Equal(new[] { "one", "few", "many", "other" }, PluralRules.Required(l).ToArray(), l);
        Check.Equal(new[] { "zero", "one", "two", "few", "many", "other" }, PluralRules.Required("ar").ToArray());
        foreach (var k in L.Known)
            Check.True(PluralRules.Required(k.Code).Contains("other"), k.Code);
        // and every category the rules can answer is one the language requires, or the optional "many"
        foreach (var k in L.Known)
            foreach (long n in new long[] { 0, 1, 2, 3, 4, 5, 11, 12, 21, 22, 25, 100, 101, 102, 111, 1000, 1_000_000 })
            {
                string cat = PluralRules.CategoryWithMillions(k.Code, n);
                Check.True(PluralRules.Required(k.Code).Contains(cat) || cat == "many", $"{k.Code} {n} -> {cat}");
            }
    }

    [UnitTest]
    public static void L_Plural_picks_the_form_fills_count_and_falls_back_to_other()
    {
        var sources = new[]
        {
            new CatalogSource("en", "t", """{ "{count} clips": { "one": "{count} clip", "other": "{count} clips" } }"""),
            new CatalogSource("ru", "t", """{ "{count} clips": { "one": "{count} клип", "few": "{count} клипа", "many": "{count} клипов", "other": "{count} клипа" } }"""),
            new CatalogSource("ar", "t", """{ "{count} clips": { "zero": "لا مقاطع", "one": "مقطع", "two": "مقطعان", "few": "{count} مقاطع", "many": "{count} مقطعا", "other": "{count} مقطع" } }"""),
            new CatalogSource("fr", "t", """{ "{count} clips": { "one": "{count} clip", "other": "{count} clips" } }"""),
        };
        using var _ = L.UseCatalogs(CatalogSet.Build(sources));
        using (L.Scope("en"))
        {
            Check.Equal("1 clip", L.Plural("{count} clips", 1));
            Check.Equal("0 clips", L.Plural("{count} clips", 0));
            Check.Equal("5 clips", L.Plural("{count} clips", 5));
            Check.Equal("1234 clips", L.Plural("{count} clips", 1234), "plain digits, no grouping");
        }
        using (L.Scope("ru"))
        {
            Check.Equal("21 клип", L.Plural("{count} clips", 21));
            Check.Equal("22 клипа", L.Plural("{count} clips", 22));
            Check.Equal("11 клипов", L.Plural("{count} clips", 11));
            Check.Equal("0 клипов", L.Plural("{count} clips", 0));
        }
        using (L.Scope("ar"))
        {
            Check.Equal("لا مقاطع", L.Plural("{count} clips", 0), "a zero form may omit {count}");
            Check.Equal("مقطع", L.Plural("{count} clips", 1));
            Check.Equal("مقطعان", L.Plural("{count} clips", 2));
            Check.Equal("5 مقاطع", L.Plural("{count} clips", 5));
            Check.Equal("11 مقطعا", L.Plural("{count} clips", 11));
            Check.Equal("100 مقطع", L.Plural("{count} clips", 100));
        }
        using (L.Scope("fr"))
        {
            Check.Equal("0 clip", L.Plural("{count} clips", 0));
            Check.Equal("2 clips", L.Plural("{count} clips", 2));
            Check.Equal("3000000 clips", L.Plural("{count} clips", 3_000_000), "no \"many\" form: other");
        }
        using (L.Scope("de"))   // not in the catalogs: English
            Check.Equal("1 clip", L.Plural("{count} clips", 1));
    }

    [UnitTest]
    public static void A_caller_supplied_count_text_wins_and_the_category_still_comes_from_the_number()
    {
        var sources = new[]
        {
            new CatalogSource("en", "t", """{ "{count} clips": { "one": "{count} clip", "other": "{count} clips" } }"""),
            new CatalogSource("ru", "t", """{ "{count} clips": { "one": "{count} клип", "few": "{count} клипа", "many": "{count} клипов", "other": "{count} клипа" } }"""),
        };
        using var _ = L.UseCatalogs(CatalogSet.Build(sources));
        using (L.Scope("en"))
        {
            Check.Equal("1,234 clips", L.Plural("{count} clips", 1234, ("count", "1,234")));
            Check.Equal("1 clip", L.Plural("{count} clips", 1, ("count", "1")), "category one from the number 1");
            Check.Equal("one clip", L.Plural("{count} clips", 1, ("count", "one")), "the text comes from the argument");
        }
        using (L.Scope("ru"))
            Check.Equal("21 000 клип", L.Plural("{count} clips", 21, ("count", "21 000")), "ru category one from 21, text from the caller");
    }

    [UnitTest]
    public static void A_plural_that_falls_back_to_english_uses_the_english_categories()
    {
        // ru has no catalog entry: the English forms are used, and 21 is "other" in English (one only for exactly 1), not ru's "one"
        var sources = new[]
        {
            new CatalogSource("en", "t", """{ "{count} clips": { "one": "{count} clip", "other": "{count} clips" } }"""),
            new CatalogSource("ru", "t", """{ "Other": "Другое" }"""),
        };
        using var _ = L.UseCatalogs(CatalogSet.Build(sources));
        using (L.Scope("ru"))
        {
            Check.Equal("21 clips", L.Plural("{count} clips", 21));
            Check.Equal("1 clip", L.Plural("{count} clips", 1));
            Check.Equal("2 clips", L.Plural("{count} clips", 2));
        }
    }
}