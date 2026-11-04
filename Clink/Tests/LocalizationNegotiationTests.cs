using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>Which UI language is chosen from the setting and the Windows display-language list (mirrors Copium's matchLanguage).</summary>
internal static class LocalizationNegotiationTests
{
    private static IReadOnlySet<string> AllKnown() => L.Known.Select(k => k.Code).ToHashSet(StringComparer.Ordinal);
    private static IReadOnlySet<string> Only(params string[] codes) => new HashSet<string>(codes, StringComparer.Ordinal);
    private static string Auto(IReadOnlySet<string> available, params string[] preferred) => LanguageNegotiation.Resolve("auto", preferred, available);

    [UnitTest]
    public static void Exact_matches_ignore_case_and_underscores()
    {
        var all = AllKnown();
        Check.Equal("de", Auto(all, "de"));
        Check.Equal("pt-BR", Auto(all, "pt-br"));
        Check.Equal("pt-BR", Auto(all, "pt_BR"));
        Check.Equal("zh-CN", Auto(all, "ZH-cn"));
    }

    [UnitTest]
    public static void Traditional_chinese_is_never_answered_with_simplified_and_the_next_language_is_tried()
    {
        var all = AllKnown();
        foreach (var t in new[] { "zh-TW", "zh-Hant", "zh-Hant-HK", "zh-HK", "zh-MO", "zh-Hant-TW", "zh-tw" })
        {
            Check.Null(LanguageNegotiation.Match(t, all), t);
            Check.Equal("en", Auto(all, t), t + " alone");
            Check.Equal("fr", Auto(all, t, "fr-FR", "de"), t + " then french");
        }
    }

    [UnitTest]
    public static void Simplified_chinese_forms_and_bare_zh_are_zh_CN()
    {
        var all = AllKnown();
        foreach (var t in new[] { "zh", "zh-Hans", "zh-SG", "zh-Hans-CN", "zh-CN", "zh-Hans-SG" }) Check.Equal("zh-CN", Auto(all, t), t);
    }

    [UnitTest]
    public static void Every_portuguese_is_pt_BR_and_regions_of_other_languages_find_their_language()
    {
        var all = AllKnown();
        Check.Equal("pt-BR", Auto(all, "pt-PT"));
        Check.Equal("pt-BR", Auto(all, "pt"));
        Check.Equal("en", Auto(all, "en-GB"));
        Check.Equal("es", Auto(all, "es-MX"));
        Check.Equal("de", Auto(all, "de-AT"));
        Check.Equal("ar", Auto(all, "ar-EG"));
    }

    [UnitTest]
    public static void A_language_Clink_does_not_have_falls_through_to_the_next_then_to_english()
    {
        var all = AllKnown();
        Check.Equal("en", Auto(all, "sr-Latn"));
        Check.Equal("en", Auto(all, "sr-Latn", "fi-FI"));
        Check.Equal("ja", Auto(all, "sr-Latn", "fi-FI", "ja-JP", "de-DE"));
        Check.Equal("en", Auto(all));
        Check.Equal("en", Auto(all, "", "  ", "not a tag", "123"));
    }

    [UnitTest]
    public static void A_language_that_is_not_available_yet_is_skipped()
    {
        var some = Only("en", "fr");
        Check.Equal("fr", Auto(some, "de-DE", "fr-FR", "ja"));
        Check.Equal("en", Auto(some, "de-DE", "ja"));
        Check.Equal("en", Auto(Only("en"), "de-DE"));
        Check.Equal("en", Auto(Only("en"), "zh-Hans", "pt-PT"), "no pt-BR, no zh-CN yet");
        Check.Equal("pt-BR", Auto(Only("en", "pt-BR"), "pt-PT"));
    }

    [UnitTest]
    public static void An_explicit_available_setting_wins_over_windows()
    {
        var all = AllKnown();
        Check.Equal("de", LanguageNegotiation.Resolve("de", new[] { "fr-FR" }, all));
        Check.Equal("pt-BR", LanguageNegotiation.Resolve("pt-br", new[] { "fr-FR" }, all));
        Check.Equal("en", LanguageNegotiation.Resolve("en", new[] { "fr-FR" }, all), "English can be chosen on a French PC");
        Check.Equal("zh-CN", LanguageNegotiation.Resolve("zh-CN", new[] { "zh-TW" }, all));
    }

    [UnitTest]
    public static void An_explicit_setting_that_is_unavailable_or_invalid_behaves_like_auto()
    {
        var some = Only("en", "fr");
        Check.Equal("fr", LanguageNegotiation.Resolve("de", new[] { "fr-FR" }, some), "de not available");
        Check.Equal("en", LanguageNegotiation.Resolve("de", new[] { "ja" }, some));
        Check.Equal("fr", LanguageNegotiation.Resolve("klingon", new[] { "fr-FR" }, some));
        Check.Equal("fr", LanguageNegotiation.Resolve("", new[] { "fr-FR" }, some));
        Check.Equal("fr", LanguageNegotiation.Resolve(null, new[] { "fr-FR" }, some));
        Check.Equal("fr", LanguageNegotiation.Resolve("AUTO", new[] { "fr-FR" }, some));
    }

    [UnitTest]
    public static void The_pseudo_locale_is_never_chosen()
    {
        var all = AllKnown();
        Check.Equal("en", LanguageNegotiation.Resolve("qps-ploc", new[] { "qps-ploc" }, all));
        Check.Equal("en", Auto(all, "qps-ploc", "qps-plocm"));
        Check.Equal("de", Auto(all, "qps-ploc", "de"));
        Check.False(L.Available.Contains(L.PseudoCode));
        Check.False(L.Known.Any(k => k.Code == L.PseudoCode));
        var withPseudo = Only("en", L.PseudoCode);
        Check.Equal("en", Auto(withPseudo, "qps-ploc"), "even if someone made it available");
    }

    [UnitTest]
    public static void With_the_real_catalogs_all_sixteen_languages_are_available_and_regional_tags_resolve()
    {
        Check.Equal(16, L.Available.Count, "every Known language is Available");
        foreach (var k in L.Known) Check.True(L.Available.Contains(k.Code), k.Code);
        var real = L.Available;
        Check.Equal("de", Auto(real, "de-AT"));
        Check.Equal("pt-BR", Auto(real, "pt-PT"));
        Check.Equal("ar", Auto(real, "ar-SA"));
        Check.Equal("en", Auto(real, "zh-TW"), "Traditional Chinese alone");
        Check.Equal("ja", Auto(real, "zh-TW", "ja-JP"), "zh-TW falls to the next language");
        Check.Equal("zh-CN", Auto(real, "zh-SG"));
    }

    [UnitTest]
    public static void Init_with_the_real_catalogs_chooses_among_the_available_languages_only()
    {
        // Only languages whose catalogs are complete can be chosen, whatever the setting and the PC say.
        string code = L.Init("ja");
        Check.True(L.Available.Contains(code), "chosen " + code + " is available");
        Check.Equal(code, L.Code);
        L.Init("auto");
        Check.True(L.Available.Contains(L.Code));
        L.Init("klingon");
        Check.True(L.Available.Contains(L.Code));
    }
}
