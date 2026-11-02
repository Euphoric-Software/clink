using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// What WPF does with bidirectional marks in right-to-left text (it decides how L.Isolate wraps), and the direction helpers of L:
/// Isolate, JoinSentences, the sentence terminators of Note, the pseudo right-to-left locale and the English log text.
/// </summary>
internal static class LocalizationBidiTests
{
    private const string Size = "1920\u00D71080";
    private const string ArabicBefore = "\u0627\u0628\u062C ";
    private const string ArabicAfter = " \u062F\u0647\u0648";

    /// <summary>Left edge and width of every character of <paramref name="text"/> laid out in a right-to-left flow.</summary>
    private static (double X, double W)[] Positions(string text)
    {
        var ft = new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.RightToLeft, new Typeface("Segoe UI"), 16, Brushes.Black, 1.0);
        var r = new (double, double)[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            var g = ft.BuildHighlightGeometry(new Point(0, 0), i, 1);
            r[i] = g == null ? (double.NaN, 0) : (g.Bounds.Left, g.Bounds.Width);
        }
        return r;
    }

    /// <summary>Whether "1920" is drawn to the left of "1080" (the size reads left to right) in <paramref name="text"/>, which holds <see cref="Size"/> once.</summary>
    private static bool SizeReadsLeftToRight(string text)
    {
        int a = text.IndexOf(Size, StringComparison.Ordinal);
        var p = Positions(text);
        return p[a].X < p[a + 5].X;
    }

    private static readonly (string Name, string Open, string Close)[] Forms =
    {
        ("raw", "", ""),
        ("LRE-PDF", "\u202A", "\u202C"),
        ("FSI-PDI", "\u2068", "\u2069"),
        ("LRI-PDI", "\u2066", "\u2069"),
        ("LRO-PDF", "\u202D", "\u202C"),
        ("LRM", "\u200E", "\u200E"),
        ("LRM+LRE-PDF", "\u200E\u202A", "\u202C\u200E"),
    };

    [UnitTest]
    public static void WPF_ignores_embeddings_and_isolates_but_honours_the_left_to_right_mark()
    {
        foreach (var (name, open, close) in Forms)
        {
            // the marks themselves take no room, in any form
            string text = ArabicBefore + open + Size + close + ArabicAfter;
            var p = Positions(text);
            for (int i = 0; i < text.Length; i++)
                if ("\u202A\u202C\u2066\u2068\u2069\u202D\u200E".IndexOf(text[i]) >= 0) Check.Equal(0.0, p[i].W, name + ": the mark at " + i + " is zero-width");

            // in plain left-to-right text nothing moves
            Check.True(SizeReadsLeftToRight("abc " + open + Size + close + " def"), name + ": Latin text keeps the size in order");

            // in Arabic text the neutral sign flips the two numbers (1080 x 1920) ...
            bool ltr = SizeReadsLeftToRight(text);
            if (name.StartsWith("LRM")) Check.True(ltr, name + " keeps 1920x1080 in order in Arabic text");
            else Check.False(ltr, name + ": WPF does not apply this mark, the size is flipped in Arabic text (if this fails, WPF honours it now: L.Isolate may use it)");
        }
    }

    [UnitTest]
    public static void Isolate_keeps_a_size_in_order_in_right_to_left_text_and_changes_nothing_in_left_to_right()
    {
        using (L.Scope("en"))
        {
            Check.Equal(Size, L.Isolate(Size));
            Check.Equal("", L.Isolate(""));
            Check.Equal("abc", L.Isolate("abc"));
        }
        foreach (string code in new[] { "ar", L.PseudoRtlCode })
            using (L.Scope(code))
            {
                Check.Equal("\u200E" + Size + "\u200E", L.Isolate(Size), code + ": numbers and Latin text: left-to-right mark");
                Check.Equal("\u200E" + "C:\\Clips" + "\u200E", L.Isolate("C:\\Clips"));
                Check.Equal("\u200F" + "\u0645\u0631\u062D\u0628\u0627" + "\u200F", L.Isolate("\u0645\u0631\u062D\u0628\u0627"), "starts with an Arabic letter: right-to-left mark");
                Check.Equal("\u200F" + "12 \u0645\u0631" + "\u200F", L.Isolate("12 \u0645\u0631"), "digits are not strong: the first letter decides");
                Check.Equal("", L.Isolate(""));
                Check.True(SizeReadsLeftToRight(ArabicBefore + L.Isolate(Size) + ArabicAfter), code + ": the isolated size reads 1920x1080 in Arabic text");
            }
        Check.False(SizeReadsLeftToRight(ArabicBefore + Size + ArabicAfter), "without it the Arabic text flips it");
    }

    [UnitTest]
    public static void A_note_isolates_an_argument_for_the_screen_and_not_for_the_log()
    {
        var set = CatalogSet.Build(new[] { new CatalogSource("en", "t", """{ "Monitor {size}": "Monitor {size}" }""") });
        using var _ = L.UseCatalogs(set);
        var note = Note.Of("Monitor {size}", ("size", Note.Isolate(Size)));
        using (L.Scope("en")) Check.Equal("Monitor " + Size, note.Shown);
        using (L.Scope("ar"))
        {
            Check.Equal("Monitor \u200E" + Size + "\u200E", note.Shown, "right-to-left: marked");
            Check.Equal("Monitor " + Size, note.English, "the log has no marks");
            Check.Equal("Monitor " + Size, note.ToString());
        }
    }

    [UnitTest]
    public static void Ltr_keeps_a_size_in_order_in_right_to_left_text_and_changes_nothing_in_left_to_right()
    {
        using (L.Scope("en"))
        {
            Check.Equal(Size, L.Ltr(Size));
            Check.Equal("", L.Ltr(""));
            Check.Equal("abc", L.Ltr("abc"));
        }
        foreach (string code in new[] { "ar", L.PseudoRtlCode })
            using (L.Scope(code))
            {
                Check.Equal("\u200E\u202A" + Size + "\u202C\u200E", L.Ltr(Size), code + ": left-to-right mark, embedding, text, end of embedding, mark");
                Check.Equal("", L.Ltr(""));
                Check.True(SizeReadsLeftToRight(ArabicBefore + L.Ltr(Size) + ArabicAfter), code + ": the size reads 1920x1080 in Arabic text");
                Check.Equal("\u200E\u202A" + "C:\\Users\\x" + "\u202C\u200E", L.Ltr("C:\\Users\\x"));
            }
    }

    [UnitTest]
    public static void A_note_with_a_left_to_right_argument_is_marked_for_the_screen_only_in_right_to_left()
    {
        var set = CatalogSet.Build(new[] { new CatalogSource("en", "t", """{ "Monitor {size}": "Monitor {size}" }""") });
        using var _ = L.UseCatalogs(set);
        var note = Note.Of("Monitor {size}", ("size", Note.Ltr(Size)));
        using (L.Scope("en"))
        {
            Check.Equal("Monitor " + Size, note.Shown);
            Check.Equal("Monitor " + Size, note.English);
        }
        using (L.Scope("ar"))
        {
            Check.Equal("Monitor \u200E\u202A" + Size + "\u202C\u200E", note.Shown, "right-to-left: marked");
            Check.Equal("Monitor " + Size, note.English, "the log has no marks");
            Check.Equal("Monitor " + Size, note.ToString());
        }
    }

    [UnitTest]
    public static void Isolate_first_strong_letter_skips_digits_and_the_byte_order_mark_and_knows_the_astral_right_to_left_blocks()
    {
        using var _ = L.Scope("ar");
        const string Rtl = "\u200F", Ltr = "\u200E";
        Check.Equal(Ltr + "\u0661\u0662 abc" + Ltr, L.Isolate("\u0661\u0662 abc"), "Arabic-Indic digits are not strong: the first letter decides");
        Check.Equal(Ltr + "\u06F1\u06F2 abc" + Ltr, L.Isolate("\u06F1\u06F2 abc"), "extended Arabic-Indic digits are not strong");
        Check.Equal(Rtl + "\u0661 \u0627" + Rtl, L.Isolate("\u0661 \u0627"));
        Check.Equal(Ltr + "\uFEFFabc" + Ltr, L.Isolate("\uFEFFabc"), "the byte order mark is not strong");
        Check.Equal(Rtl + "\uFEFF\u0627" + Rtl, L.Isolate("\uFEFF\u0627"));
        Check.Equal(Rtl + "\uD802\uDC00 a" + Rtl, L.Isolate("\uD802\uDC00 a"), "U+10800 (Cypriot syllabary block start, right-to-left)");
        Check.Equal(Rtl + "\uD83A\uDD00 a" + Rtl, L.Isolate("\uD83A\uDD00 a"), "U+1E900 Adlam");
        Check.Equal(Ltr + "\uD835\uDC00 a" + Ltr, L.Isolate("\uD835\uDC00 a"), "a mathematical letter outside the right-to-left blocks is left-to-right");
        Check.Equal(Ltr + "\uD83D" + Ltr, L.Isolate("\uD83D"), "a lone surrogate does not throw");
    }

    [UnitTest]
    public static void Isolate_first_strong_treats_strong_right_to_left_punctuation_as_right_to_left_and_skips_the_neutral_and_weak_signs()
    {
        using var _ = L.Scope("ar");
        const string Rtl = "\u200F", Ltr = "\u200E";
        // strong right-to-left punctuation (bidi class AL or R) decides before the Latin text that follows
        foreach (int cp in new[] { 0x061B, 0x061F, 0x06D4, 0x060D, 0x0608, 0x060B, 0x05BE, 0x05C0, 0x05C3, 0x05C6, 0x05F3, 0x05F4 })
        {
            string s = char.ConvertFromUtf32(cp) + " abc";
            Check.Equal(Rtl + s + Rtl, L.Isolate(s), "U+" + cp.ToString("X4") + " is strong right-to-left");
        }
        // marks and format characters, digits and number signs, and the neutral or weak punctuation do not decide: the next strong character does
        foreach (int cp in new[] { 0x064E, 0x0610, 0x0600, 0x06DD, 0x0660, 0x0669, 0x06F0, 0x06F9, 0x066B, 0x066C, 0x060C, 0x0609, 0x060A, 0x066A, 0x0606, 0x0607, 0x060E, 0x060F, 0x06DE, 0x06E9, 0xFD3E, 0xFD3F, 0xFEFF })
        {
            string s = char.ConvertFromUtf32(cp) + " abc";
            Check.Equal(Ltr + s + Ltr, L.Isolate(s), "U+" + cp.ToString("X4") + " does not decide (left-to-right text follows)");
            string r = char.ConvertFromUtf32(cp) + " \u0627";
            Check.Equal(Rtl + r + Rtl, L.Isolate(r), "U+" + cp.ToString("X4") + " does not decide (an Arabic letter follows)");
        }
        // plain ASCII punctuation and spaces before the first letter were never strong
        Check.Equal(Ltr + "(1) abc" + Ltr, L.Isolate("(1) abc"));
        Check.Equal(Rtl + "(1) \u0627" + Rtl, L.Isolate("(1) \u0627"));
    }

    [UnitTest]
    public static void Inside_InEnglish_the_language_answers_English()
    {
        using (L.Scope("ar"))
        {
            Check.Equal("ar", L.Code);
            Check.True(L.IsRtl);
            Check.Equal("ar", L.UiCulture.TwoLetterISOLanguageName);
            string inside = L.InEnglish(() => string.Join("|", L.Code, L.IsRtl, L.UiCulture.TwoLetterISOLanguageName, L.Isolate(Size), L.Ltr("x")));
            Check.Equal("en|False|en|" + Size + "|x", inside);
            Check.Equal("ar", L.Code, "and back");
        }
        using (L.Scope(L.PseudoRtlCode))
            Check.Equal("en|False", L.InEnglish(() => L.Code + "|" + L.IsRtl));
    }

    [UnitTest]
    public static void The_pseudo_right_to_left_locale_is_a_scope_only_language()
    {
        Check.False(L.Known.Any(k => k.Code == L.PseudoRtlCode), "not a Known language");
        Check.False(L.Available.Contains(L.PseudoRtlCode), "not Available");
        Check.Equal(null, L.CanonicalCode(L.PseudoRtlCode), "not a setting value");
        Check.Equal("qps-plocm", L.PseudoRtlCode);
        var set = CatalogSet.Build(new[] { new CatalogSource("en", "t", """{ "Save clip": "Save clip", "Cancel": "Cancel" }""") });
        using var _ = L.UseCatalogs(set);
        Check.False(set.Available.Contains(L.PseudoRtlCode));
        using (L.Scope(L.PseudoRtlCode))
        {
            Check.Equal("qps-plocm", L.Code);
            Check.True(L.IsRtl);
            Check.Equal("en", L.UiCulture.TwoLetterISOLanguageName, "the pseudo languages use the English culture");
            Check.True(PseudoLocale.IsPseudo(L.T("Save clip")), "pseudo-localized text: " + L.T("Save clip"));
            Check.Equal(L.TIn(L.PseudoCode, "Save clip"), L.T("Save clip"), "the same catalog as qps-ploc");
        }
        using (L.Scope(L.PseudoCode))
        {
            Check.False(L.IsRtl, "qps-ploc stays left-to-right");
            Check.Equal("qps-ploc", L.Code);
        }
        using (L.Scope("ar")) Check.False(PseudoLocale.IsPseudo(L.T("Save clip")), "ar is not pseudo");
    }

    [UnitTest]
    public static void JoinSentences_skips_empty_parts_and_uses_the_separator_of_the_language()
    {
        using (L.Scope("en"))
        {
            Check.Equal("One. Two.", L.JoinSentences("One.", null, "", "Two."));
            Check.Equal("", L.JoinSentences());
            Check.Equal("", L.JoinSentences(null, ""));
            Check.Equal("A", L.JoinSentences("A"));
            Check.Equal("A B C", L.JoinSentences(new List<string?> { "A", "B", "C" }));
            Check.Equal("A B", L.JoinSentences(new[] { "A", "", "B" }));
            Check.Equal("\u3002 x", L.JoinSentences("\u3002", "x"), "English always puts a space");
        }
        foreach (string code in new[] { "ja", "zh-CN" })
            using (L.Scope(code))
            {
                Check.Equal("\u4E00\u3002\u4E8C\uFF01\u4E09\uFF1F\u56DB\uFF09\u4E94\u300D\u516D", L.JoinSentences("\u4E00\u3002", "\u4E8C\uFF01", "", "\u4E09\uFF1F", null, "\u56DB\uFF09", "\u4E94\u300D", "\u516D"), code + ": no space after a full-width terminator");
                Check.Equal("A. B", L.JoinSentences("A.", "B"), code + ": a space after a Latin stop");
                Check.Equal("\u4E00 B", L.JoinSentences("\u4E00", "B"), code + ": a space when there is no terminator");
            }
        foreach (string code in new[] { "ko", "ar", "hi" })
            using (L.Scope(code)) Check.Equal("\u4E00\u3002 \u4E8C", L.JoinSentences("\u4E00\u3002", "\u4E8C"), code + ": always a space");
        using (L.Scope("ja")) Check.Equal("\u4E00\u3002 \u4E8C", L.InEnglish(() => L.JoinSentences("\u4E00\u3002", "\u4E8C")), "the log joins the English way");
    }

    [UnitTest]
    public static void A_note_without_its_final_period_also_drops_a_full_width_or_devanagari_stop()
    {
        var set = CatalogSet.Build(new[]
        {
            new CatalogSource("en", "t", """{ "Saved.": "Saved.", "Done...": "Done..." }"""),
            new CatalogSource("ja", "t", """{ "Saved.": "\u4FDD\u5B58\u3057\u307E\u3057\u305F\u3002" }"""),
            new CatalogSource("zh-CN", "t", """{ "Saved.": "\u5DF2\u4FDD\u5B58\uFF0E" }"""),
            new CatalogSource("hi", "t", """{ "Saved.": "\u0938\u0939\u0947\u091C\u093E\u0964" }"""),
        });
        using var _ = L.UseCatalogs(set);
        var saved = Note.Of("Saved.");
        using (L.Scope("en"))
        {
            Check.Equal("Saved.", saved.Shown);
            Check.Equal("Saved", saved.WithoutFinalPeriod().Shown);
            Check.Equal("Saved", saved.WithoutFinalPeriod().English);
            Check.Equal("Done", Note.Of("Done...").WithoutFinalPeriod().Shown);
        }
        using (L.Scope("ja"))
        {
            Check.Equal("\u4FDD\u5B58\u3057\u307E\u3057\u305F", saved.WithoutFinalPeriod().Shown);
            Check.Equal("Saved", saved.WithoutFinalPeriod().English, "the log stays English");
        }
        using (L.Scope("zh-CN")) Check.Equal("\u5DF2\u4FDD\u5B58", saved.WithoutFinalPeriod().Shown);
        using (L.Scope("hi")) Check.Equal("\u0938\u0939\u0947\u091C\u093E", saved.WithoutFinalPeriod().Shown);
    }
}
