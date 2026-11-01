namespace Clink.Services.Localization;

/// <summary>
/// CLDR plural categories for whole numbers, hand-coded for the 16 UI languages (https://cldr.unicode.org/index/cldr-spec/plural-rules).
/// Counts are always integers here, so the "other" category of ru/uk/pl (fractions) is never chosen for them.
/// </summary>
internal static class PluralRules
{
    public const string Zero = "zero", One = "one", Two = "two", Few = "few", Many = "many", Other = "other";

    /// <summary>Every category name a catalog may use, in CLDR order.</summary>
    public static readonly string[] AllCategories = { Zero, One, Two, Few, Many, Other };

    /// <summary>
    /// The categories a catalog must give for a language (a "many" for es/fr/it/pt-BR, which CLDR 42+ uses for exact millions only,
    /// is optional and falls back to "other"). An unknown language uses English's.
    /// </summary>
    public static IReadOnlyList<string> Required(string locale) => Family(locale) switch
    {
        Rule.None => new[] { Other },
        Rule.Slavic => new[] { One, Few, Many, Other },
        Rule.Arabic => new[] { Zero, One, Two, Few, Many, Other },
        _ => new[] { One, Other },
    };

    /// <summary>The CLDR category of an integer count in the language (a negative count is judged by its size).</summary>
    public static string Category(string locale, long count)
    {
        ulong n = count < 0 ? (count == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)(-count)) : (ulong)count;
        switch (Family(locale))
        {
            case Rule.None:
                return Other;
            case Rule.OneIsOne:       // en, de, nl, tr, es (+ many for exact millions below), it
                return n == 1 ? One : Other;
            case Rule.ZeroOrOne:      // fr, pt-BR, hi: 0 and 1 are "one"
                return n <= 1 ? One : Other;
            case Rule.Slavic:
            {
                bool pl = Language(locale) == "pl";
                ulong m10 = n % 10, m100 = n % 100;
                if (pl)
                {
                    if (n == 1) return One;
                    if (m10 is >= 2 and <= 4 && m100 is not (>= 12 and <= 14)) return Few;
                    return Many;   // everything else that is whole: 0, 5-21, 25-31, ...
                }
                if (m10 == 1 && m100 != 11) return One;
                if (m10 is >= 2 and <= 4 && m100 is not (>= 12 and <= 14)) return Few;
                return Many;       // ru, uk: 0, 5-20, 25-30, ...
            }
            case Rule.Arabic:
            {
                if (n == 0) return Zero;
                if (n == 1) return One;
                if (n == 2) return Two;
                ulong m100 = n % 100;
                if (m100 is >= 3 and <= 10) return Few;
                if (m100 is >= 11 and <= 99) return Many;
                return Other;
            }
            default:
                return n == 1 ? One : Other;
        }
    }

    /// <summary>
    /// The category used to pick a form: <paramref name="count"/>'s own, except that the languages with a million-rule "many"
    /// (es, fr, it, pt-BR) report it only for exact non-zero millions.
    /// </summary>
    public static string CategoryWithMillions(string locale, long count)
    {
        string c = Category(locale, count);
        if (c == Other && count != 0 && count % 1_000_000 == 0 && Language(locale) is "es" or "fr" or "it" or "pt") return Many;
        return c;
    }

    private enum Rule { OneIsOne, ZeroOrOne, Slavic, Arabic, None }

    private static string Language(string locale)
    {
        int dash = locale.IndexOfAny(new[] { '-', '_' });
        return (dash < 0 ? locale : locale[..dash]).ToLowerInvariant();
    }

    private static Rule Family(string locale) => Language(locale) switch
    {
        "ja" or "ko" or "zh" => Rule.None,
        "fr" or "pt" or "hi" => Rule.ZeroOrOne,
        "ru" or "uk" or "pl" => Rule.Slavic,
        "ar" => Rule.Arabic,
        _ => Rule.OneIsOne,
    };
}
