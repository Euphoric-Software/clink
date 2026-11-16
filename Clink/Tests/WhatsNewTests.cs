using System.IO;
using System.Reflection;
using System.Text.Json;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// The "What's new" changelog: CHANGELOG.md (parser, shipped content, its translations), the rules for what is shown at start, and the two
/// per-install settings that remember what was shown.
/// </summary>
internal static class WhatsNewTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure("not a version: " + text);

    private static Changelog Parse(string text)
    {
        Check.True(Changelog.TryParse(text, out var log, out string? problem), "parse: " + problem);
        return log!;
    }

    private static string? Problem(string text)
    {
        Check.False(Changelog.TryParse(text, out var log, out string? problem), "expected a problem");
        Check.Null(log);
        return problem;
    }

    private const string Sample = """
        # Changelog
        Some words about the file: with | and {braces} that are only a preface.

        ## Unreleased
        - U one
        - U two
        ## 0.5.0 - 2026-12-01
        - Five
        ## 0.4.9
        - Four nine
        ## 0.4.8
        - Four eight
        ## 0.4.7
        - Four seven
        """;

    private static string[] Headings(IEnumerable<ChangelogSection> sections) => sections.Select(s => s.Heading).ToArray();

    // ------------------------------------------------------------------ the parser

    [UnitTest]
    public static void WhatsNew_parser_reads_unreleased_versions_dates_and_skips_the_preface()
    {
        var log = Parse(Sample);
        Check.Equal(new[] { "Unreleased", "0.5.0", "0.4.9", "0.4.8", "0.4.7" }, Headings(log.Sections));
        Check.Equal(new[] { "U one", "U two" }, log.Unreleased!.Entries.ToArray());
        Check.Equal("2026-12-01", log.Sections[1].Date);
        Check.Null(log.Sections[2].Date);
        Check.Equal(new[] { "0.5.0", "0.4.9", "0.4.8", "0.4.7" }, Headings(log.Released));
        Check.Equal(new[] { "U one", "U two", "Five", "Four nine", "Four eight", "Four seven" }, log.AllEntries.ToArray());

        // CRLF and a missing Unreleased
        var crlf = Parse("## 1.0.0\r\n- A\r\n\r\n## 0.9.0 - 2026-01-02\r\n- B\r\n");
        Check.Null(crlf.Unreleased);
        Check.Null(crlf.UnreleasedHash);
        Check.Equal(new[] { "1.0.0", "0.9.0" }, Headings(crlf.Sections));
        Check.Equal(new[] { "A" }, crlf.Sections[0].Entries.ToArray());
    }

    [UnitTest]
    public static void WhatsNew_parser_refuses_anything_that_is_not_the_format()
    {
        var bad = new (string what, string text)[]
        {
            ("empty", ""),
            ("blank", "  \n\n"),
            ("no section", "# Changelog\nWords only\n"),
            ("entry before a section", "- A\n## 1.0.0\n- B\n"),
            ("section without entries", "## 1.0.0\n## 0.9.0\n- B\n"),
            ("last section without entries", "## 1.0.0\n- A\n## 0.9.0\n"),
            ("Unreleased not first", "## 1.0.0\n- A\n## Unreleased\n- B\n"),
            ("Unreleased twice", "## Unreleased\n- A\n## Unreleased\n- B\n"),
            ("duplicate version", "## 1.0.0\n- A\n## 1.0.0\n- B\n"),
            ("ascending", "## 0.9.0\n- A\n## 1.0.0\n- B\n"),
            ("two-part version", "## 1.0\n- A\n"),
            ("v prefix", "## v1.0.0\n- A\n"),
            ("pre-release heading", "## 1.0.0-dev.1\n- A\n"),
            ("leading zero", "## 1.02.0\n- A\n"),
            ("bad date", "## 1.0.0 - 2026-13-45\n- A\n"),
            ("date without dash", "## 1.0.0 2026-01-01\n- A\n"),
            ("deeper heading", "## 1.0.0\n### Fixed\n- A\n"),
            ("text that is not an entry", "## 1.0.0\n- A\nplain words\n"),
            ("star bullet", "## 1.0.0\n* A\n"),
            ("empty entry", "## 1.0.0\n- \n"),
            ("pipe", "## 1.0.0\n- A | B\n"),
            ("open brace", "## 1.0.0\n- A {name}\n"),
            ("close brace", "## 1.0.0\n- A }\n"),
            ("link", "## 1.0.0\n- See [the site](x)\n"),
            ("bare link", "## 1.0.0\n- See https://example.org\n"),
            ("angle bracket", "## 1.0.0\n- <b>A</b>\n"),
            ("backtick", "## 1.0.0\n- Use `x`\n"),
            ("too long", "## 1.0.0\n- " + new string('a', Changelog.MaxEntryLength + 1) + "\n"),
            ("tab inside", "## 1.0.0\n- A\tB\n"),
            ("indented bullet", "## 1.0.0\n  - A\n"),
            ("extra space after the dash", "## 1.0.0\n-  A\n"),
        };
        foreach (var (what, text) in bad)
        {
            Check.False(Changelog.TryParse(text, out var log, out string? problem), what);
            Check.Null(log, what);
            Check.True(!string.IsNullOrEmpty(problem), what + ": says why");
        }
        Check.False(Changelog.TryParse(null, out _, out _), "null");
        Check.Contains(Problem("## 1.0.0\n- A\n- B | C\n")!, "line 3");
        Check.True(Changelog.TryParse("## 1.0.0\n- " + new string('a', Changelog.MaxEntryLength) + "\n", out _, out _), "300 characters are fine");
    }

    [UnitTest]
    public static void WhatsNew_unreleased_hash_follows_the_english_text_only()
    {
        var a = Parse("## Unreleased\n- One\n- Two\n## 1.0.0\n- Old\n");
        var same = Parse("## Unreleased\r\n- One\r\n- Two\r\n## 1.0.0\r\n- Changed old\r\n");
        var edited = Parse("## Unreleased\n- One\n- Two!\n");
        var reordered = Parse("## Unreleased\n- Two\n- One\n");
        string h = a.UnreleasedHash!;
        Check.Equal(64, h.Length);
        Check.True(h.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')), "lower-case hex: " + h);
        Check.Equal(h, same.UnreleasedHash, "line ends and released sections do not count");
        Check.NotEqual(h, edited.UnreleasedHash);
        Check.NotEqual(h, reordered.UnreleasedHash);
        Check.Equal(Changelog.HashOf(new[] { "One", "Two" }), h);
        Check.NotEqual(Changelog.HashOf(new[] { "OneTwo" }), h, "entries are joined, not concatenated");
    }

    // ------------------------------------------------------------------ the shipped file and its translations

    [UnitTest]
    public static void WhatsNew_the_shipped_changelog_is_valid_and_covers_this_build()
    {
        string text = WhatsNew.ReadResource() ?? throw new TestFailure("CHANGELOG.md is not embedded");
        var log = Parse(text);
        Check.True(log.Sections.Count > 0, "has sections");
        Check.NotNull(WhatsNew.Current, "the embedded file parses (the app would show nothing otherwise)");
        string? info = typeof(Changelog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var own = ReleaseVersion.Parse(info) ?? throw new TestFailure("no build version: " + info);
        bool covered = log.Unreleased != null || log.Released.Any(s => s.Version!.Value.Number == own.Number);
        Check.True(covered, $"CHANGELOG.md needs \"## Unreleased\" or \"## {own.Number.ToString(3)}\" for this build ({info})");
        foreach (string entry in log.AllEntries) Check.Null(Changelog.EntryProblem(entry), entry);
    }

    private static IEnumerable<(string Locale, string Area, string Name)> CatalogResources() =>
        typeof(Changelog).Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith("ClinkLocales/", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n.Split('/'))
            .Where(p => p.Length == 3)
            .Select(p => (p[1], p[2][..^5], string.Join("/", p)));

    private static Dictionary<string, string> ReadCatalog(string locale, string area)
    {
        var name = typeof(Changelog).Assembly.GetManifestResourceNames().First(n => n.Replace('\\', '/') == $"ClinkLocales/{locale}/{area}.json");
        using var s = typeof(Changelog).Assembly.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : "");   // (other areas also hold plural objects)
    }

    [UnitTest]
    public static void WhatsNew_every_changelog_entry_has_an_english_catalog_key_and_every_key_an_entry()
    {
        var log = Parse(WhatsNew.ReadResource()!);
        var wanted = log.AllEntries.Select(WhatsNew.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var en = ReadCatalog("en", "changelog");
        var have = en.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Check.Equal(wanted.Except(have).ToArray(), Array.Empty<string>(), "entries without a key in Locales/en/changelog.json");
        Check.Equal(have.Except(wanted).ToArray(), Array.Empty<string>(), "keys in Locales/en/changelog.json that no entry of CHANGELOG.md has");
        Check.Equal(wanted, have);
        foreach (var (key, value) in en) Check.Equal(key[WhatsNew.KeyPrefix.Length..], value, "English value of " + key);
        // and the combined catalog agrees: English shows the text itself
        foreach (string entry in log.AllEntries) Check.Equal(entry, L.TIn("en", WhatsNew.Key(entry)));
    }

    [UnitTest]
    public static void WhatsNew_the_catalog_file_holds_only_changelog_keys_in_all_sixteen_languages()
    {
        var resources = CatalogResources().ToList();
        var enKeys = ReadCatalog("en", "changelog").Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        foreach (var k in L.Known)
        {
            Check.True(resources.Any(r => r.Locale == k.Code && r.Area == "changelog"), k.Code + " has changelog.json");
            var cat = ReadCatalog(k.Code, "changelog");
            Check.True(cat.Keys.All(key => key.StartsWith(WhatsNew.KeyPrefix, StringComparison.Ordinal)), k.Code + ": only \"whatsnew|\" keys");
            Check.Equal(enKeys, cat.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(), k.Code + " has the English keys, no more, no fewer");
            if (k.Code != "en")
                foreach (var (key, value) in cat)
                    Check.NotEqual(key[WhatsNew.KeyPrefix.Length..], value, k.Code + ": translated, not left in English: " + key);
        }
        // a "whatsnew|" key belongs to this one area: no other catalog file may carry one
        foreach (var r in resources.Where(r => r.Area != "changelog"))
            Check.False(ReadCatalog(r.Locale, r.Area).Keys.Any(key => key.StartsWith(WhatsNew.KeyPrefix, StringComparison.Ordinal)), $"{r.Locale}/{r.Area}.json has a whatsnew| key");
    }

    [UnitTest]
    public static void WhatsNew_entries_come_out_in_the_active_language()
    {
        var log = Parse(WhatsNew.ReadResource()!);
        foreach (var k in L.Known.Where(k => k.Code != "en"))
            using (L.Scope(k.Code))
                foreach (string entry in log.AllEntries)
                    Check.NotEqual(entry, WhatsNew.Text(entry), k.Code + ": " + entry);
        using (L.Scope("en"))
            foreach (string entry in log.AllEntries) Check.Equal(entry, WhatsNew.Text(entry));
    }

    // ------------------------------------------------------------------ what is shown at start

    private static IReadOnlyList<ChangelogSection> Decide(Changelog? log, string current, string? seen = null, string? seenHash = null,
        bool devInstance = false, bool testRun = false, bool firstInstall = false) =>
        WhatsNewPolicy.Decide(log, V(current), devInstance, testRun, firstInstall, seen, seenHash);

    [UnitTest]
    public static void WhatsNew_a_dev_instance_or_a_test_run_never_shows_it_on_its_own()
    {
        var log = Parse(Sample);
        Check.Equal(0, Decide(log, "0.5.0", "0.4.7", devInstance: true).Count, "--dev");
        Check.Equal(0, Decide(log, "0.5.0", "0.4.7", testRun: true).Count, "unit tests");
        Check.Equal(0, Decide(log, "0.5.0", firstInstall: true, devInstance: true).Count, "--dev on a first install");
        Check.Equal(0, Decide(log, "0.5.0-dev.3", firstInstall: true, testRun: true).Count, "tests on a first install");
        Check.True(TestGuard.Active, "this test runs in test mode");
        Check.Equal(1, Decide(log, "0.5.0", firstInstall: true).Count, "and the same call without either shows");
    }

    [UnitTest]
    public static void WhatsNew_no_changelog_shows_nothing()
    {
        Check.Equal(0, Decide(null, "0.5.0", "0.4.7").Count);
        Check.Equal(0, Decide(null, "0.5.0", firstInstall: true).Count);
    }

    [UnitTest]
    public static void WhatsNew_a_first_install_shows_the_current_version_only()
    {
        var log = Parse(Sample);
        Check.Equal(new[] { "0.4.8" }, Headings(Decide(log, "0.4.8", firstInstall: true)));
        Check.Equal(new[] { "0.4.8" }, Headings(Decide(log, "0.4.8", "0.4.7", firstInstall: true)), "whatever an imported file said was seen");
        Check.Equal(new[] { "Unreleased" }, Headings(Decide(log, "0.5.0-dev.3", firstInstall: true)), "a dev build: Unreleased");
        Check.Equal(new[] { "Unreleased" }, Headings(Decide(log, "0.5.0-dev.3", firstInstall: true, seenHash: log.UnreleasedHash)), "the hash does not hide a first install");
        Check.Equal(0, Decide(log, "0.6.0", firstInstall: true).Count, "a version the file has no section for");
        Check.Equal(0, Decide(Parse("## 1.0.0\n- A\n"), "1.1.0-dev.2", firstInstall: true).Count, "a dev build with no Unreleased section");
    }

    [UnitTest]
    public static void WhatsNew_nothing_seen_yet_shows_the_current_version_only()
    {
        var log = Parse(Sample);
        Check.Equal(new[] { "0.4.9" }, Headings(Decide(log, "0.4.9")), "an existing user's first build with the feature: not the whole history");
        Check.Equal(new[] { "Unreleased" }, Headings(Decide(log, "0.5.0-dev.1")));
        Check.Equal(new[] { "0.4.9" }, Headings(Decide(log, "0.4.9", "banana")), "a value that is no version counts as nothing seen");
        Check.Equal(new[] { "0.4.9" }, Headings(Decide(log, "0.4.9", "v0.4.8")), "only the canonical form counts");
        Check.Equal(0, Decide(log, "0.4.9-beta.1").Count, "another pre-release has no section of its own");
    }

    [UnitTest]
    public static void WhatsNew_a_downgrade_shows_nothing()
    {
        var log = Parse(Sample);
        Check.Equal(0, Decide(log, "0.4.8", "0.5.0").Count, "release to an older release");
        Check.Equal(0, Decide(log, "0.4.8", "0.5.0-dev.1").Count, "left the dev channel for an older release");
        Check.Equal(0, Decide(log, "0.5.0-dev.2", "0.5.0-dev.9").Count, "an older dev build");
        Check.Equal(0, Decide(log, "0.5.0-dev.2", "0.5.0-dev.9", seenHash: "0".PadRight(64, '0')).Count, "even when the notes changed");
    }

    [UnitTest]
    public static void WhatsNew_every_unseen_version_is_shown_newest_first_with_no_cap()
    {
        var log = Parse(Sample);
        Check.Equal(new[] { "0.5.0", "0.4.9", "0.4.8", "0.4.7" }, Headings(Decide(log, "0.5.0", "0.4.6")));
        Check.Equal(new[] { "0.4.9", "0.4.8" }, Headings(Decide(log, "0.4.9", "0.4.7")), "up to the current version, not beyond");
        Check.Equal(new[] { "0.5.0" }, Headings(Decide(log, "0.5.0", "0.4.9")));
        Check.Equal(0, Decide(log, "0.5.0", "0.5.0").Count, "nothing new");
        Check.Equal(0, Decide(log, "0.4.8", "0.4.8").Count);

        // many versions: no cap
        var text = string.Join("\n", Enumerable.Range(1, 40).Reverse().Select(i => $"## 1.{i}.0\n- Entry {i}"));
        var many = Parse(text);
        var shown = Decide(many, "1.40.0", "1.0.0");
        Check.Equal(40, shown.Count);
        Check.Equal("1.40.0", shown[0].Heading);
        Check.Equal("1.1.0", shown[^1].Heading);
        Check.Equal(10, Decide(many, "1.40.0", "1.30.0").Count);
    }

    [UnitTest]
    public static void WhatsNew_a_dev_build_also_shows_unreleased_when_its_english_text_changed()
    {
        var log = Parse(Sample);
        string h = log.UnreleasedHash!;
        Check.Equal(0, Decide(log, "0.5.0-dev.2", "0.5.0-dev.1", h).Count, "same notes: nothing");
        Check.Equal(new[] { "Unreleased" }, Headings(Decide(log, "0.5.0-dev.2", "0.5.0-dev.2", "f".PadRight(64, 'f'))), "notes changed on the same build");
        Check.Equal(new[] { "Unreleased" }, Headings(Decide(log, "0.5.0-dev.2", "0.5.0-dev.1", null)), "no hash remembered");
        Check.Equal(new[] { "Unreleased" }, Headings(Decide(log, "0.5.0-dev.2", "0.5.0-dev.1", "not a hash")));
        // released versions between seen and current come after Unreleased
        Check.Equal(new[] { "Unreleased", "0.4.9", "0.4.8" }, Headings(Decide(log, "0.5.0-dev.1", "0.4.7", null)));
        Check.Equal(new[] { "0.4.9", "0.4.8" }, Headings(Decide(log, "0.5.0-dev.1", "0.4.7", h)), "released ones are not hidden by the hash");
        // a dev build with no Unreleased section
        var released = Parse("## 1.0.0\n- A\n");
        Check.Equal(0, Decide(released, "1.0.1-dev.1", "1.0.0").Count);
        // a stable build never shows Unreleased
        Check.DoesNotContain(string.Join(",", Headings(Decide(log, "0.5.0", "0.4.7"))), "Unreleased");
    }

    [UnitTest]
    public static void WhatsNew_switching_channel_shows_what_is_new_to_the_channel()
    {
        var log = Parse(Sample);
        string h = log.UnreleasedHash!;
        // dev -> stable: the release of the same number beats its dev builds
        Check.Equal(new[] { "0.5.0" }, Headings(Decide(log, "0.5.0", "0.5.0-dev.4", h)));
        Check.Equal(new[] { "0.5.0", "0.4.9" }, Headings(Decide(log, "0.5.0", "0.4.8", h)));
        // stable -> dev: only the notes of the dev build
        Check.Equal(new[] { "Unreleased" }, Headings(Decide(log, "0.5.0-dev.1", "0.4.9")));
        Check.Equal(0, Decide(log, "0.5.0-dev.1", "0.4.9", h).Count);
    }

    // ------------------------------------------------------------------ marking it seen

    [UnitTest]
    public static void WhatsNew_marking_seen_keeps_the_highest_version_and_the_dev_hash_and_saves()
    {
        var log = Parse(Sample);
        var s = new Settings();
        Check.Null(s.WhatsNewSeen);
        Check.True(WhatsNewPolicy.MarkSeen(s, log, V("0.4.8")));
        Check.Equal("0.4.8", s.WhatsNewSeen);
        Check.Null(s.WhatsNewSeenDevHash, "a release remembers no dev hash");
        Check.False(WhatsNewPolicy.MarkSeen(s, log, V("0.4.8")), "nothing changed, nothing saved");
        Check.False(WhatsNewPolicy.MarkSeen(s, log, V("0.4.7")), "never lowered");
        Check.Equal("0.4.8", s.WhatsNewSeen);

        Check.True(WhatsNewPolicy.MarkSeen(s, log, V("0.5.0-dev.3")));
        Check.Equal("0.5.0-dev.3", s.WhatsNewSeen);
        Check.Equal(log.UnreleasedHash, s.WhatsNewSeenDevHash);
        Check.Equal(0, WhatsNewPolicy.Decide(log, V("0.5.0-dev.3"), false, false, false, s.WhatsNewSeen, s.WhatsNewSeenDevHash).Count, "shown once, not again");

        // saved: another load sees both
        var loaded = Settings.Load();
        Check.Equal("0.5.0-dev.3", loaded.WhatsNewSeen);
        Check.Equal(log.UnreleasedHash, loaded.WhatsNewSeenDevHash);

        // a later dev build with other notes shows Unreleased again, and marking it replaces the hash
        var edited = Parse("## Unreleased\n- Something else\n## 0.4.8\n- X\n");
        Check.Equal(new[] { "Unreleased" }, Headings(WhatsNewPolicy.Decide(edited, V("0.5.0-dev.4"), false, false, false, s.WhatsNewSeen, s.WhatsNewSeenDevHash)));
        Check.True(WhatsNewPolicy.MarkSeen(s, edited, V("0.5.0-dev.4")));
        Check.Equal(edited.UnreleasedHash, s.WhatsNewSeenDevHash);
        Check.Equal("0.5.0-dev.4", s.WhatsNewSeen);
    }

    // ------------------------------------------------------------------ the two settings

    [UnitTest]
    public static void WhatsNew_settings_round_trip_and_stay_out_of_the_file_while_empty()
    {
        string hash = new string('a', 32) + new string('0', 32);
        var s = new Settings { WhatsNewSeen = "0.4.9-dev.37", WhatsNewSeenDevHash = hash };
        var back = Settings.FromJson(s.ToJson());
        Check.Equal("0.4.9-dev.37", back.WhatsNewSeen);
        Check.Equal(hash, back.WhatsNewSeenDevHash);
        Check.Equal("0.4.8", Settings.FromJson("""{ "WhatsNewSeen": "0.4.8" }""").WhatsNewSeen);

        string empty = new Settings().ToJson();
        Check.DoesNotContain(empty, "WhatsNew", "a settings.json of a build without the feature stays what it was");
        Check.Null(Settings.FromJson("{}").WhatsNewSeen);
        Check.Null(Settings.FromJson("{}").WhatsNewSeenDevHash);

        // through the file
        s.Save();
        var loaded = Settings.Load();
        Check.Equal("0.4.9-dev.37", loaded.WhatsNewSeen);
        Check.Equal(hash, loaded.WhatsNewSeenDevHash);
    }

    [UnitTest]
    public static void WhatsNew_a_bad_value_in_a_settings_file_is_read_as_nothing_seen()
    {
        foreach (string bad in new[] { "banana", "", " ", "1.2", "v0.4.8", "0.4.8+abc", " 0.4.8", "0.4.8 ", "0.04.8", "0.4.8-beta.1", "0.4.8-dev.01", "99999999999.0.0", "0.4.8\\n" })
            Check.Null(Settings.FromJson("{ \"WhatsNewSeen\": \"" + bad + "\" }").WhatsNewSeen, "version \"" + bad + "\"");
        foreach (string bad in new[] { "banana", "", "ABCDEF" + new string('0', 58), new string('a', 63), new string('a', 65), new string('g', 64), " " + new string('a', 63), new string('a', 64) + "\\n" })
            Check.Null(Settings.FromJson("{ \"WhatsNewSeenDevHash\": \"" + bad + "\" }").WhatsNewSeenDevHash, "hash \"" + bad + "\"");
        Check.Null(Settings.FromJson("""{ "WhatsNewSeen": null, "WhatsNewSeenDevHash": null }""").WhatsNewSeen);
        // a bad value next to a good one does not cost the good one
        var mixed = Settings.FromJson("{ \"WhatsNewSeen\": \"nope\", \"WhatsNewSeenDevHash\": \"" + new string('b', 64) + "\", \"Fps\": 30 }");
        Check.Null(mixed.WhatsNewSeen);
        Check.Equal(new string('b', 64), mixed.WhatsNewSeenDevHash);
        Check.Equal(30, mixed.Fps);
    }

    [UnitTest]
    public static void WhatsNew_the_settings_belong_to_this_installation_and_are_never_imported()
    {
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.WhatsNewSeen)));
        Check.True(SettingsImport.IsOwnKey(nameof(Settings.WhatsNewSeenDevHash)));
        Check.True(SettingsImport.IsOwnKey("whatsnewseen"), "any case");
        Check.False(SettingsImport.IsOwnKey(nameof(Settings.ShowToast)), "not a catch-all");
        Check.False(SettingsImport.IsOwnKey("WhatsNewSomethingElse"), "the two keys by name, not a prefix");

        string hash = new string('c', 64);
        // an import keeps ours and drops theirs
        string theirs = "{ \"Fps\": 30, \"WhatsNewSeen\": \"9.9.9\", \"WhatsNewSeenDevHash\": \"" + hash + "\" }";
        using var withOurs = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, "{ \"WhatsNewSeen\": \"0.4.8\" }", keepOurToken: false));
        Check.Equal("0.4.8", withOurs.RootElement.GetProperty("WhatsNewSeen").GetString());
        Check.False(withOurs.RootElement.TryGetProperty("WhatsNewSeenDevHash", out _), "ours has none, so it is left out");
        Check.Equal(30, withOurs.RootElement.GetProperty("Fps").GetInt32());
        using var noOurs = JsonDocument.Parse(SettingsImport.KeepOurOwn(theirs, null, keepOurToken: false));
        Check.False(noOurs.RootElement.TryGetProperty("WhatsNewSeen", out _));
        Check.False(noOurs.RootElement.TryGetProperty("WhatsNewSeenDevHash", out _));
        // and the difference they make is not offered as an import
        Check.Equal(0, SettingsImport.Differences("{ \"WhatsNewSeen\": \"0.4.8\" }", "{ \"WhatsNewSeen\": \"9.9.9\", \"WhatsNewSeenDevHash\": \"" + hash + "\" }").Count);
    }
}
