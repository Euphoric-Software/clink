using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clink.Services.Localization;

/// <summary>A UI language. <see cref="Culture"/> is its usual specific culture, a fallback for the UI culture only (never used to format numbers or dates: see <see cref="L.FormatCulture"/>).</summary>
internal readonly record struct LocaleInfo(string Code, string NativeName, bool Rtl, string Culture);

/// <summary>One catalog entry: a text, or the CLDR forms of a plural ("one", "other", ...).</summary>
internal sealed record Entry(string? Text, IReadOnlyDictionary<string, string>? Forms)
{
    public string Primary => Text ?? (Forms != null && Forms.TryGetValue(PluralRules.Other, out var o) ? o : "");
}

/// <summary>The entries of one language, all areas together. Immutable after <see cref="CatalogSet.Build"/>.</summary>
internal sealed class Catalog
{
    public string Locale { get; }
    public IReadOnlyDictionary<string, Entry> Entries { get; }
    public Catalog(string locale, IReadOnlyDictionary<string, Entry> entries) { Locale = locale; Entries = entries; }
}

/// <summary>Raw catalog JSON of one area ("core", ...) of one language.</summary>
internal readonly record struct CatalogSource(string Locale, string Area, string Json);

/// <summary>
/// Every catalog plus the glossary, parsed once and never changed. A language whose catalog does not parse is left out (and logged
/// once): lookups for it then fall back to English and it is never <see cref="Available"/>.
/// </summary>
internal sealed class CatalogSet
{
    public IReadOnlyDictionary<string, Catalog> Locales { get; }
    /// <summary>English term -> language -> the approved translation.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Glossary { get; }
    /// <summary>Everything that was wrong, by language ("*" for the glossary).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Problems { get; }
    /// <summary>The languages that were present in the sources (valid or not).</summary>
    public IReadOnlyList<string> Present { get; }
    public Catalog English { get; }
    public IReadOnlySet<string> Available { get; }
    private readonly Lazy<Catalog> _pseudo;
    /// <summary>The qps-ploc catalog, made from English.</summary>
    public Catalog Pseudo => _pseudo.Value;

    private CatalogSet(Dictionary<string, Catalog> locales, Dictionary<string, IReadOnlyDictionary<string, string>> glossary, Dictionary<string, IReadOnlyList<string>> problems, List<string> present)
    {
        Locales = locales;
        Glossary = glossary;
        Problems = problems;
        Present = present;
        English = locales.TryGetValue("en", out var en) ? en : new Catalog("en", new Dictionary<string, Entry>());
        var avail = new HashSet<string>(StringComparer.Ordinal) { "en" };
        // another language counts only when English has entries to be complete against (an empty English must not make everything "complete")
        if (English.Entries.Count > 0)
            foreach (var k in L.Known)
                if (k.Code != "en" && locales.TryGetValue(k.Code, out var c) && English.Entries.Keys.All(c.Entries.ContainsKey)) avail.Add(k.Code);
        Available = avail;
        _pseudo = new Lazy<Catalog>(() =>
        {
            try { return PseudoLocale.From(English); }
            catch (Exception ex) { Log.Error("[l10n] could not make the pseudo-locale", ex); return new Catalog(L.PseudoCode, new Dictionary<string, Entry>()); }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Builds the set. Never throws. Rules: invalid JSON or a root that is not an object drops that file, and for a language other than English
    /// the whole language (English keeps the entries of its other files); a bad entry (empty key or value, bad plural) is skipped and
    /// recorded in <see cref="Problems"/>; the same key twice with the SAME value (e.g. in two areas) is fine, with different values the
    /// key is dropped and recorded (the language is then incomplete, so not Available, and English text shows for that key).
    /// English is never dropped: its valid entries stay whatever else is wrong.
    /// </summary>
    public static CatalogSet Build(IEnumerable<CatalogSource> sources, string? glossaryJson = null)
    {
        try { return BuildCore(sources, glossaryJson); }
        catch (Exception ex)
        {
            Log.Error("[l10n] could not build the catalogs, using English keys", ex);
            return new CatalogSet(new Dictionary<string, Catalog>(), new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                new Dictionary<string, IReadOnlyList<string>> { ["*"] = new[] { "internal error: " + ex.GetType().Name + ": " + ex.Message } }, new List<string>());
        }
    }

    private static bool SameEntry(Entry a, Entry b) =>
        a.Text != null ? a.Text == b.Text
        : b.Forms != null && a.Forms!.Count == b.Forms.Count && a.Forms.All(f => b.Forms.TryGetValue(f.Key, out var v) && v == f.Value);

    private static CatalogSet BuildCore(IEnumerable<CatalogSource> sources, string? glossaryJson)
    {
        var entries = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var present = new List<string>();
        var broken = new HashSet<string>(StringComparer.Ordinal);                // languages with a structurally invalid file (dropped, unless English)
        var conflicted = new HashSet<(string, string)>();                         // (locale, key) with two different values: dropped
        void Problem(string locale, string what)
        {
            if (!problems.TryGetValue(locale, out var l)) problems[locale] = l = new List<string>();
            l.Add(what);
        }
        foreach (var src in sources)
        {
            if (!present.Contains(src.Locale)) present.Add(src.Locale);
            if (!entries.TryGetValue(src.Locale, out var map)) entries[src.Locale] = map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            string at = src.Locale + "/" + src.Area;
            try
            {
                using var doc = JsonDocument.Parse(src.Json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { Problem(src.Locale, at + ": the root is not an object"); broken.Add(src.Locale); continue; }
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.Length == 0) { Problem(src.Locale, at + ": an empty key"); continue; }
                    if (conflicted.Contains((src.Locale, p.Name))) { Problem(src.Locale, at + ": duplicate key \"" + p.Name + "\" (dropped)"); continue; }
                    Entry? made = null;
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        string v = p.Value.GetString() ?? "";
                        if (v.Length == 0) Problem(src.Locale, at + ": empty value for \"" + p.Name + "\"");
                        else made = new Entry(v, null);
                    }
                    else if (p.Value.ValueKind == JsonValueKind.Object)
                    {
                        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
                        bool ok = true;
                        foreach (var f in p.Value.EnumerateObject())
                        {
                            if (Array.IndexOf(PluralRules.AllCategories, f.Name) < 0) { Problem(src.Locale, at + ": \"" + p.Name + "\" has the unknown plural category \"" + f.Name + "\""); ok = false; }
                            else if (f.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(f.Value.GetString())) { Problem(src.Locale, at + ": \"" + p.Name + "\" form \"" + f.Name + "\" is not a non-empty string"); ok = false; }
                            else if (!forms.TryAdd(f.Name, f.Value.GetString()!)) { Problem(src.Locale, at + ": \"" + p.Name + "\" repeats the form \"" + f.Name + "\""); ok = false; }
                        }
                        if (ok && !forms.ContainsKey(PluralRules.Other)) { Problem(src.Locale, at + ": \"" + p.Name + "\" has no \"other\" form"); ok = false; }
                        if (ok) made = new Entry(null, forms);
                    }
                    else Problem(src.Locale, at + ": \"" + p.Name + "\" is neither a string nor an object of plural forms");
                    if (made == null) continue;
                    if (!map.TryGetValue(p.Name, out var old)) map[p.Name] = made;
                    else if (!SameEntry(old, made))
                    {
                        map.Remove(p.Name);
                        conflicted.Add((src.Locale, p.Name));
                        Problem(src.Locale, at + ": duplicate key \"" + p.Name + "\" with a different value (dropped)");
                    }
                }
            }
            catch (JsonException ex) { Problem(src.Locale, at + ": not valid JSON (" + ex.Message + ")"); broken.Add(src.Locale); }
        }

        var locales = new Dictionary<string, Catalog>(StringComparer.Ordinal);
        foreach (var (locale, map) in entries)
        {
            if (broken.Contains(locale) && locale != "en") continue;   // an unreadable language is English; English itself keeps what is valid
            locales[locale] = new Catalog(locale, map);
        }

        var glossary = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (glossaryJson != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(glossaryJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) Problem("*", "glossary: the root is not an object");
                else
                    foreach (var term in doc.RootElement.EnumerateObject())
                    {
                        if (term.Value.ValueKind != JsonValueKind.Object) { Problem("*", "glossary: \"" + term.Name + "\" is not an object"); continue; }
                        var per = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var l in term.Value.EnumerateObject())
                        {
                            if (l.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(l.Value.GetString())) per[l.Name] = l.Value.GetString()!;
                            else Problem("*", "glossary: \"" + term.Name + "\" / " + l.Name + " is not a non-empty string");
                        }
                        glossary[term.Name] = per;
                    }
            }
            catch (JsonException ex) { Problem("*", "glossary: not valid JSON (" + ex.Message + ")"); }
        }

        foreach (var (locale, list) in problems)
            Log.Warn($"[l10n] {(locale == "*" ? "glossary" : "catalog " + locale)} problem: {list[0]}" + (list.Count > 1 ? $" (+{list.Count - 1} more)" : ""));
        return new CatalogSet(locales, glossary, problems.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value), present);
    }
}

/// <summary>
/// The text of the UI. English source text is the key; <c>L.T("Save clip")</c> returns the active language's text for it, English
/// when that language has none (a key English lacks too is returned as is and listed in <see cref="Missing"/>). Placeholders are
/// <c>{name}</c> (no format specifiers); plurals are catalog objects of CLDR forms (<see cref="Plural"/>). A key may carry a context,
/// <c>"context|English text"</c>: its English value is the text after the first '|' (also what shows when no catalog has the key). Catalogs
/// are embedded JSON, Locales/&lt;language&gt;/&lt;area&gt;.json.
/// <para>SLICE RULE: never bind a raw number or date in XAML ({Binding Size}, StringFormat on a DateTime ...). WPF formats bindings with the
/// element's xml:lang, which App sets to the UI language, so a number or date would be written in that language's culture and calendar
/// (Hijri under ar) instead of the user's regional format. Format through L.Number/Bytes/Duration/Date (FormatCulture) in code and bind
/// the string, or set ConverterCulture explicitly.</para>
/// <para>The XmlLanguage override in App is a FrameworkElement metadata default, so it reaches every FrameworkElement. Run, Span and
/// Hyperlink are FrameworkContentElements: they have their own Language property, which inherits from the parent through the logical tree,
/// so text inside a TextBlock follows it; a content element that has no parent (or a FlowDocument's) does not.</para>
/// </summary>
internal static class L
{
    public const string PseudoCode = "qps-ploc";
    /// <summary>The pseudo-locale in a right-to-left language (the same pseudo catalog): tests and --dev-language only. Not in <see cref="Known"/> or <see cref="Available"/>.</summary>
    public const string PseudoRtlCode = "qps-plocm";

    /// <summary>The 16 target languages in the picker's order (English first, the source).</summary>
    public static readonly IReadOnlyList<LocaleInfo> Known = new[]
    {
        new LocaleInfo("en", "English", false, "en-US"),
        new LocaleInfo("es", "Español", false, "es-ES"),
        new LocaleInfo("fr", "Français", false, "fr-FR"),
        new LocaleInfo("de", "Deutsch", false, "de-DE"),
        new LocaleInfo("pt-BR", "Português (Brasil)", false, "pt-BR"),
        new LocaleInfo("it", "Italiano", false, "it-IT"),
        new LocaleInfo("nl", "Nederlands", false, "nl-NL"),
        new LocaleInfo("pl", "Polski", false, "pl-PL"),
        new LocaleInfo("tr", "Türkçe", false, "tr-TR"),
        new LocaleInfo("ru", "Русский", false, "ru-RU"),
        new LocaleInfo("uk", "Українська", false, "uk-UA"),
        new LocaleInfo("ar", "العربية", true, "ar-SA"),
        new LocaleInfo("hi", "हिन्दी", false, "hi-IN"),
        new LocaleInfo("ja", "日本語", false, "ja-JP"),
        new LocaleInfo("ko", "한국어", false, "ko-KR"),
        new LocaleInfo("zh-CN", "简体中文", false, "zh-CN"),
    };

    private const string ResourcePrefix = "ClinkLocales/";

    private sealed record State(string Code, LocaleInfo Info, bool Pseudo);

    private static volatile State s_state = MakeState("en");
    [ThreadStatic] private static State? t_state;   // InEnglish: this thread answers in English while a note composes its log text
    private static State Current => t_state ?? s_state;
    private static volatile CatalogSet? s_override;
    private static readonly Lazy<CatalogSet> s_embedded = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, byte> s_missing = new(StringComparer.Ordinal);

    private static CatalogSet Set => s_override ?? s_embedded.Value;

    // ------------------------------------------------------------------ state

    /// <summary>The active language code: one of <see cref="Known"/>, or "qps-ploc" inside a test <see cref="Scope"/>.</summary>
    public static string Code => Current.Code;
    public static bool IsRtl => Current.Info.Rtl;
    /// <summary>The culture for resources and system text (CurrentUICulture is set to it at start). English inside <see cref="InEnglish"/>.</summary>
    public static CultureInfo UiCulture => CultureFor(Current, ui: true);
    /// <summary>
    /// The culture numbers, dates and units are written in: ALWAYS the user's regional format (CultureInfo.CurrentCulture, calendar
    /// included), whatever the UI language is (the Windows convention). So today's English output is the same on every PC. The language's
    /// own culture (<see cref="UiCulture"/>, <see cref="LocaleInfo.Culture"/>) is for resources and system UI text only, never for formatting.
    /// </summary>
    public static CultureInfo FormatCulture => CultureInfo.CurrentCulture;

    /// <summary>The languages that can be picked: Known, with an embedded, valid catalog that has every English key. English is always one.</summary>
    public static IReadOnlySet<string> Available => Set.Available;

    /// <summary>Keys that were asked for and found in no catalog (English included), since start.</summary>
    public static IReadOnlyCollection<string> Missing => s_missing.Keys.ToArray();

    /// <summary>
    /// Picks the language from the Settings.Language value ("auto" or a code) and the Windows display-language list, and makes it
    /// the active one. Never throws. Returns the code chosen.
    /// </summary>
    public static string Init(string? settingValue)
    {
        string code = "en";
        try { code = LanguageNegotiation.Resolve(settingValue, LanguageNegotiation.WindowsPreferred(), Available); }
        catch (Exception ex) { Log.Error("[l10n] could not choose a language, using English", ex); }
        s_state = MakeState(code);
        return code;
    }

    /// <summary>
    /// For the hidden <c>--dev-language</c> switch of a <c>--dev</c> instance: makes <paramref name="code"/> (a Known code, "qps-ploc" or
    /// "qps-plocm") the active language even when it is not <see cref="Available"/> (its text is then English, or pseudo for the pseudo
    /// codes). Any other code is English. Returns the code now active. Never throws.
    /// </summary>
    internal static string SetForDev(string code)
    {
        s_state = MakeState(code);
        return s_state.Code;
    }

    /// <summary>
    /// Whether the app sets the UI culture and the XAML language for the language that <see cref="Init"/> resolved. Merges into dev stay
    /// invisible: with the setting on "auto" (or an unknown / unavailable code) and English resolved, Windows' own UI culture is left alone, so
    /// WPF's built-in texts stay in the user's Windows language as before. Any other language, or an explicitly chosen one (including "en"), is applied.
    /// </summary>
    public static bool ShouldApplyUiCulture(string? setting, string resolved) =>
        !resolved.Equals("en", StringComparison.OrdinalIgnoreCase) || CanonicalCode(setting) is "en";

    /// <summary>The canonical code ("pt-BR") of a Known code written in any case, or null.</summary>
    public static string? CanonicalCode(string? code)
    {
        if (code == null) return null;
        string t = code.Trim().Replace('_', '-');
        foreach (var k in Known) if (k.Code.Equals(t, StringComparison.OrdinalIgnoreCase)) return k.Code;
        return null;
    }

    /// <summary>For tests: makes <paramref name="locale"/> (a Known code or "qps-ploc") the active language until disposed. Any other code is English.</summary>
    public static IDisposable Scope(string locale)
    {
        var previous = s_state;
        s_state = MakeState(locale);
        return new Undo(() => s_state = previous);
    }

    private sealed class Undo : IDisposable
    {
        private Action? _undo;
        public Undo(Action undo) => _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }

    private static State MakeState(string code)
    {
        if (code.Equals(PseudoCode, StringComparison.OrdinalIgnoreCase)) return new State(PseudoCode, new LocaleInfo(PseudoCode, "Pseudo", false, "en-US"), true);
        if (code.Equals(PseudoRtlCode, StringComparison.OrdinalIgnoreCase)) return new State(PseudoRtlCode, new LocaleInfo(PseudoRtlCode, "Pseudo RTL", true, "en-US"), true);
        foreach (var k in Known) if (k.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) return new State(k.Code, k, false);
        return new State("en", Known[0], false);
    }

    private static CultureInfo CultureFor(State st, bool ui)
    {
        try { return CultureInfo.GetCultureInfo(ui && !st.Pseudo ? st.Info.Code : st.Info.Culture); }
        catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en-US"); }
    }


    // ------------------------------------------------------------------ translating

    /// <summary>The text for <paramref name="key"/> in the active language, with <c>{name}</c> placeholders filled from <paramref name="args"/>.</summary>
    public static string T(string key, params (string Name, object? Value)[] args) => Translate(Current, key, args);

    /// <summary>
    /// Runs <paramref name="compose"/> with this thread answering <see cref="T"/>, <see cref="Plural"/>, <see cref="Bytes"/> and the other
    /// catalog calls in English whatever the active language is (a log line is written for the developer). Only for <see cref="Note.Build"/>.
    /// </summary>
    internal static string InEnglish(Func<string> compose)
    {
        var before = t_state;
        t_state = MakeState("en");
        try { return compose(); }
        finally { t_state = before; }
    }

    /// <summary>Like <see cref="T"/> in a given language (a Known code or "qps-ploc"), whatever the active one is.</summary>
    public static string TIn(string locale, string key, params (string Name, object? Value)[] args) => Translate(MakeState(locale), key, args);

    /// <summary>
    /// A plural entry for a whole <paramref name="count"/>: the form its CLDR category selects (a missing form is "other"); <c>{count}</c>
    /// is filled in as plain digits unless <paramref name="args"/> has its own.
    /// </summary>
    public static string Plural(string key, long count, params (string Name, object? Value)[] args)
    {
        var st = Current;
        // a caller-supplied {count} (e.g. "1,234" with separators) wins; the category always comes from the number
        bool hasCount = Array.Exists(args, a => a.Name == "count");
        var all = new (string Name, object? Value)[args.Length + (hasCount ? 0 : 1)];
        if (!hasCount) all[0] = ("count", count);
        Array.Copy(args, 0, all, hasCount ? 0 : 1, args.Length);
        var entry = Find(st, key, out string entryLocale);
        if (entry == null) return Fill(EnglishOf(key), all, st);
        if (entry.Forms == null) return Fill(entry.Text!, all, st);
        // the plural rules of the language the entry is written in (English forms fall back with English's categories)
        string cat = PluralRules.CategoryWithMillions(entryLocale, count);
        string form = entry.Forms.TryGetValue(cat, out var f) ? f : entry.Forms[PluralRules.Other];
        return Fill(form, all, st);
    }

    private static string Translate(State st, string key, (string Name, object? Value)[] args)
    {
        var entry = Find(st, key);
        return Fill(entry?.Primary ?? EnglishOf(key), args, st);
    }

    private static readonly Regex s_context = new(@"^[^\s|{}]+\|", RegexOptions.Compiled);

    /// <summary>The English text a key stands for: the part after "context|", or the key itself. What is shown for a key no catalog has.</summary>
    internal static string EnglishOf(string key) => s_context.Match(key) is { Success: true } m ? key[m.Length..] : key;

    private static Entry? Find(State st, string key) => Find(st, key, out _);

    /// <summary>The entry for a key and the language it is written in ("en" for the English fallback and for the pseudo-locale).</summary>
    private static Entry? Find(State st, string key, out string entryLocale)
    {
        var set = Set;
        entryLocale = "en";
        if (st.Pseudo)
        {
            if (set.Pseudo.Entries.TryGetValue(key, out var p)) return p;
        }
        else
        {
            if (st.Code != "en" && set.Locales.TryGetValue(st.Code, out var cat) && cat.Entries.TryGetValue(key, out var e)) { entryLocale = st.Code; return e; }
            if (set.English.Entries.TryGetValue(key, out var en)) return en;
        }
        s_missing.TryAdd(key, 0);
        return null;
    }

    private static string Fill(string template, (string Name, object? Value)[] args, State st)
    {
        if (args.Length == 0 || template.IndexOf('{') < 0) return template;
        var sb = new StringBuilder(template.Length + 16);
        var culture = FormatCulture;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            int end;
            if (c == '{' && (end = template.IndexOf('}', i + 1)) > i + 1)
            {
                string name = template.Substring(i + 1, end - i - 1);
                int idx = Array.FindIndex(args, a => a.Name == name);
                if (idx >= 0)
                {
                    sb.Append(args[idx].Value switch
                    {
                        null => "",
                        string s => s,
                        IFormattable f => f.ToString(null, culture),
                        var o => o.ToString(),
                    });
                    i = end;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The names of the <c>{placeholders}</c> in a catalog text.</summary>
    internal static IReadOnlySet<string> PlaceholdersOf(string text) =>
        Regex.Matches(text, @"\{([A-Za-z_][A-Za-z0-9_]*)\}").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    // ------------------------------------------------------------------ numbers, sizes, times (units are catalog templates in en/core.json)

    public static string Number(double value) => value.ToString("#,0.##", FormatCulture);

    /// <summary>"820 kB", "9.5 MB", "190 MB", "1.2 GB" (decimal units, as Capabilities.FormatBytes).</summary>
    public static string Bytes(long bytes) => BytesCore(null, bytes);

    /// <summary><see cref="Bytes"/> in <paramref name="locale"/> (a log line is written in English whatever language the UI has); the number still follows <see cref="FormatCulture"/>.</summary>
    public static string BytesIn(string locale, long bytes) => BytesCore(locale, bytes);

    /// <summary>
    /// The one place that picks the unit and the digits of a size, for <see cref="Bytes"/> (the active language, <paramref name="locale"/> null)
    /// and <see cref="BytesIn"/>. The digits follow <see cref="FormatCulture"/> in both, so the numbers in a log line are written in the
    /// regional format of this PC (like every number in a note); only the unit word differs by language.
    /// </summary>
    private static string BytesCore(string? locale, long bytes)
    {
        var fc = FormatCulture;
        if (bytes < 0) bytes = 0;
        (string key, string n) = bytes switch
        {
            < 1_000_000 => ("unit|{n} kB", (bytes / 1_000.0).ToString("0", fc)),
            < 10_000_000 => ("unit|{n} MB", (bytes / 1_000_000.0).ToString("0.#", fc)),
            < 1_000_000_000 => ("unit|{n} MB", (bytes / 1_000_000.0).ToString("0", fc)),
            < 10_000_000_000 => ("unit|{n} GB", (bytes / 1_000_000_000.0).ToString("0.#", fc)),
            _ => ("unit|{n} GB", (bytes / 1_000_000_000.0).ToString("0", fc)),
        };
        return locale == null ? T(key, ("n", n)) : TIn(locale, key, ("n", n));
    }

    /// <summary>"15 s", "1:30", "10:00", "1 h", "1:05:00" (as Capabilities.FormatDuration).</summary>
    public static string Duration(int seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60) return T("unit|{n} s", ("n", seconds.ToString(FormatCulture)));
        if (seconds < 3600) return $"{seconds / 60}:{seconds % 60:00}";
        if (seconds % 3600 == 0) return T("unit|{n} h", ("n", (seconds / 3600).ToString(FormatCulture)));
        return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    /// <summary>A clip's length as players show it: "0:42", "12:05", "1:00:03" (as Capabilities.FormatClipLength).</summary>
    public static string ClipLength(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public enum DateKind { ShortDate, LongDate, Time, ShortDateTime }

    public static string Date(DateTime value, DateKind kind) => value.ToString(kind switch
    {
        DateKind.ShortDate => "d",
        DateKind.LongDate => "D",
        DateKind.Time => "t",
        _ => "g",
    }, FormatCulture);

    /// <summary>
    /// Keeps left-to-right text (a file name, a path, a hotkey, a URL) intact inside right-to-left text. Wraps it in U+200E (left-to-right mark) on both sides when
    /// the language is right-to-left; returns it unchanged otherwise. WPF ignores LRE/LRO/LRI/FSI...PDF/PDI (see <see cref="Isolate"/>), so the mark is what keeps the text in order.
    /// </summary>
    public static string Ltr(string text) => IsRtl && text.Length > 0 ? "\u200E" + text + "\u200E" : text;

    /// <summary>
    /// Keeps a piece of text whose direction is not known in advance (a monitor name, a window title, a size such as 1920x1080 whose neutral
    /// sign would otherwise flip the two numbers) in its own direction inside right-to-left text; returns it unchanged otherwise.
    /// MEASURED (Tests/LocalizationBidiTests): WPF's text formatter ignores the embedding and isolate controls (LRE/RLE/LRO/RLO...PDF and
    /// LRI/RLI/FSI...PDI have no effect on the order; the marks are only zero-width), but it does honour the left-to-right and right-to-left
    /// marks, which make the digits of what follows strong. So the text is wrapped in U+200E (or U+200F when its first strong letter is
    /// right-to-left) on both sides. The marks are zero-width and <see cref="Note"/> strips them from the English log text.
    /// </summary>
    public static string Isolate(string text)
    {
        if (!IsRtl || text.Length == 0) return text;
        char mark = FirstStrongIsRtl(text) ? '\u200F' : '\u200E';
        return mark + text + mark;
    }

    private static bool FirstStrongIsRtl(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF') return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    /// <summary>
    /// Joins sentences (or clauses) with the separator of the active language: skips null and empty parts and puts a space between the
    /// others; Japanese and Chinese write no space after a sentence that ends with a full-width stop, exclamation, question mark or closing bracket.
    /// </summary>
    public static string JoinSentences(IEnumerable<string?> parts)
    {
        bool tight = Code is "ja" or "zh-CN";
        var sb = new StringBuilder();
        string? previous = null;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (previous != null && !(tight && "\u3002\uFF01\uFF1F\uFF09\u300D".IndexOf(previous[^1]) >= 0)) sb.Append(' ');
            sb.Append(part);
            previous = part;
        }
        return sb.ToString();
    }

    public static string JoinSentences(params string?[] parts) => JoinSentences((IEnumerable<string?>)parts);

    // ------------------------------------------------------------------ loading

    // Catalog areas: Locales/<language>/<area>.json (core.json, then one file per UI area). Words several areas share (Cancel, Delete,
    // Share, Copy link, Upload, Sign in/Sign out, OK, Close, Retry, units) belong in core.json, once: a key in two areas must have
    // the identical value (see CatalogSet.Build), and one place keeps the glossary term the same everywhere.

    /// <summary>Never throws (the Lazy would cache an exception): any failure gives an empty set, so English keys are shown.</summary>
    private static CatalogSet LoadEmbedded()
    {
        try { return LoadEmbeddedCore(); }
        catch (Exception ex)
        {
            Log.Error("[l10n] could not load the catalogs, using English keys", ex);
            return CatalogSet.Build(Array.Empty<CatalogSource>());
        }
    }

    private static CatalogSet LoadEmbeddedCore()
    {
        var sources = new List<CatalogSource>();
        string? glossary = null;
        try
        {
            var asm = typeof(L).Assembly;
            foreach (string raw in asm.GetManifestResourceNames())
            {
                string name = raw.Replace('\\', '/');
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                string[] parts = name[ResourcePrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                using var stream = asm.GetManifestResourceStream(raw);
                if (stream == null) continue;
                string json = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                if (parts.Length == 1 && parts[0].Equals("glossary.json", StringComparison.Ordinal)) glossary = json;
                else if (parts.Length == 2 && parts[1].EndsWith(".json", StringComparison.Ordinal)) sources.Add(new CatalogSource(parts[0], parts[1][..^5], json));
            }
        }
        catch (Exception ex) { Log.Error("[l10n] could not read the embedded catalogs, using English keys", ex); }
        return CatalogSet.Build(sources, glossary);
    }

    // ------------------------------------------------------------------ test hooks

    /// <summary>The catalogs embedded in this exe (what the app runs on), whatever a test has swapped in.</summary>
    internal static CatalogSet Embedded => s_embedded.Value;

    /// <summary>The catalogs in use now (the embedded ones unless a test has swapped others in).</summary>
    internal static CatalogSet Catalogs => Set;

    /// <summary>For tests: use <paramref name="set"/> instead of the embedded catalogs until disposed.</summary>
    internal static IDisposable UseCatalogs(CatalogSet set)
    {
        var previous = s_override;
        s_override = set;
        return new Undo(() => s_override = previous);
    }

    internal static void ClearMissing() => s_missing.Clear();

    /// <summary>For the test runner: what to put back after a test (opaque).</summary>
    internal static object Capture() => (s_state, s_override);

    /// <summary>For the test runner: puts back what <see cref="Capture"/> returned.</summary>
    internal static void Restore(object captured)
    {
        var (state, set) = ((State, CatalogSet?))captured;
        s_state = state;
        s_override = set;
        s_missing.Clear();
    }

    /// <summary>For the test runner: English, the embedded catalogs and no recorded misses.</summary>
    internal static void PinEnglish()
    {
        s_state = MakeState("en");
        s_override = null;
        s_missing.Clear();
    }
}

/// <summary>
/// qps-ploc: the English catalog made unmistakable and longer. Letters get accents, about 35% of padding is added, the text is wrapped in
/// "[!! ... !!]"; placeholders, product names, URLs, file extensions and --flags stay as they are, and plural structure is kept.
/// </summary>
internal static class PseudoLocale
{
    public const string Open = "[!! ", Close = " !!]";

    private const string Plain = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Accented = "áƀçďéƒğĥíĵķĺḿñóṕɋŕšţúṽŵẋýžÅƁÇĎÉƑĞĤÍĴĶĹḾÑÓṔɊŔŠŢÚṼŴẊÝŽ";

    private static readonly Regex s_keep = new(@"\{[A-Za-z_][A-Za-z0-9_]*\}|https?://\S+|Copium\+?|Clink|\.(?:mp4|png)\b|--[a-z][a-z0-9-]*|eup_\w+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Transform(string text)
    {
        var sb = new StringBuilder(text.Length * 2 + 8);
        sb.Append(Open);
        int letters = 0, pos = 0;
        foreach (Match m in s_keep.Matches(text))
        {
            Accent(text.AsSpan(pos, m.Index - pos), sb, ref letters);
            sb.Append(m.Value);
            pos = m.Index + m.Length;
        }
        Accent(text.AsSpan(pos), sb, ref letters);
        sb.Append('~', (int)Math.Ceiling(letters * 0.35));
        sb.Append(Close);
        return sb.ToString();
    }

    private static void Accent(ReadOnlySpan<char> s, StringBuilder sb, ref int letters)
    {
        foreach (char c in s)
        {
            int i = Plain.IndexOf(c);
            if (i >= 0) { sb.Append(Accented[i]); letters++; }
            else sb.Append(c);
        }
    }

    public static bool IsPseudo(string? text) => text != null && text.Contains(Open.Trim(), StringComparison.Ordinal) && text.Contains(Close.Trim(), StringComparison.Ordinal);

    public static Catalog From(Catalog english)
    {
        var map = new Dictionary<string, Entry>(english.Entries.Count, StringComparer.Ordinal);
        foreach (var (key, e) in english.Entries)
            map[key] = e.Text != null
                ? new Entry(Transform(e.Text), null)
                : new Entry(null, e.Forms!.ToDictionary(f => f.Key, f => Transform(f.Value)));
        return new Catalog(L.PseudoCode, map);
    }
}
