using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// For the localization slices: walks a window or element tree that was built but never shown, under <c>L.Scope("qps-ploc")</c>, and
/// lists every user-visible string that did not come from the catalog (it lacks the pseudo-locale's "[!! ... !!]" frame), so a hard-coded
/// literal in XAML or code-behind is found by a test. Use:
/// <code>
/// using (L.Scope("qps-ploc")) { var w = new SomeWindow(); var bad = L10nTreeHarness.Unlocalized(w); Check.Equal(Array.Empty&lt;string&gt;(), bad.ToArray()); }
/// </code>
/// Before walking, the root is measured and arranged off screen (1200 x 900) and every FrameworkElement gets ApplyTemplate, so item
/// containers, ContentPresenters and DataTemplates have produced their elements. Checked: TextBlock text (each Run), string Content,
/// string Header, MenuItem gesture text, string items of an ItemsControl, ToolTip (a string, or a ToolTip/element tree), the
/// ContextMenu and its items, Window.Title, AutomationProperties.Name and HelpText. A text with pseudo-frames is checked WITHOUT them:
/// <c>L.T("Saved") + " to folder"</c> is flagged because " to folder" is hard-coded. Skipped: text without a letter, product names, unit
/// names and hotkeys (see <see cref="IsAllowed"/>), and anything the caller's <c>allow</c> predicate accepts (user data such as a clip name).
/// LIMIT: only what exists after construction and layout is seen. Text set later (Loaded handlers, timers, events, async results, a
/// pop-up that is opened on a click) is found only if the area test triggers it first, e.g. by calling the method that sets it. Item
/// templates inside Collapsed panels are not generated (a collapsed element is not measured), so their items are not seen: make the
/// panel visible in the test first. Hyperlink (a FrameworkContentElement) is walked too, with its ToolTip and ContextMenu.
/// </summary>
internal static class L10nTreeHarness
{
    private static readonly HashSet<string> s_words = new(StringComparer.OrdinalIgnoreCase)
    {
        "Clink", "Copium", "Copium+", "Discord", "Windows", "Nitro", "HDR", "H.264", "HEVC", "H264", "MP4", "PNG", "GIF", "FPS",
    };   // no unit words (kB, MB, Mbps, s, h ...): they must come from L.Bytes/L.Duration, so raw Capabilities.Format* output is flagged

    // Hotkeys are accepted only as a combo: "+"-joined, at least one modifier, every other part a key name. A bare "Delete", "Pause",
    // "Home", "Tab", "Control" ... is a word that needs translating (or the area test's allow predicate, if it is a hotkey cap).
    private static readonly HashSet<string> s_modifiers = new(StringComparer.OrdinalIgnoreCase) { "Ctrl", "Control", "Alt", "Shift", "Win" };
    private static readonly HashSet<string> s_keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Esc", "Escape", "Tab", "Enter", "Space", "Del", "Delete", "Ins", "Insert", "Home", "End", "PgUp", "PgDn", "PageUp", "PageDown",
        "Up", "Down", "Left", "Right", "PrtSc", "PrintScreen", "Pause", "ScrollLock", "NumLock", "Caps", "CapsLock", "Backspace", "Menu",
    };
    // an innermost frame: no "[!! " inside it (frames nest when a pseudo text is a placeholder value of another)
    private static readonly Regex s_frame = new(@"\[!! (?:(?!\[!! ).)*? !!\]", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The text with every pseudo frame (and the frames inside frames) cut out.</summary>
    internal static string StripFrames(string text)
    {
        string r = text, p;
        do { p = r; r = s_frame.Replace(r, " "); } while (r != p);
        return r;
    }

    private static readonly Dictionary<string, HashSet<string>> s_dateCache = new();

    // month / day names and AM/PM of the format culture (what L.Date writes); cached per culture
    private static HashSet<string> DateTokens()
    {
        var c = L.FormatCulture;
        lock (s_dateCache)
        {
            if (!s_dateCache.TryGetValue(c.Name, out var set)) s_dateCache[c.Name] = set = BuildDateTokens(c);
            return set;
        }
    }

    private static HashSet<string> BuildDateTokens(System.Globalization.CultureInfo c)
    {
        var f = c.DateTimeFormat;
        var all = new List<string>();
        foreach (var arr in new[] { f.MonthNames, f.AbbreviatedMonthNames, f.MonthGenitiveNames, f.AbbreviatedMonthGenitiveNames, f.DayNames, f.AbbreviatedDayNames, f.ShortestDayNames })
            all.AddRange(arr);
        all.Add(f.AMDesignator);
        all.Add(f.PMDesignator);
        return all.SelectMany(s => Regex.Split(s, @"[\s/,\-]+")).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);   // case-sensitive: the shortest day name "S" must not let a unit "s" through
    }

    /// <summary>"Window.Content: text" lines for every visible string of <paramref name="root"/>'s trees that is not pseudo-localized and not allowed.</summary>
    public static IReadOnlyList<string> Unlocalized(DependencyObject root, Func<string, bool>? allow = null)
    {
        var found = new List<string>();
        var seen = new HashSet<DependencyObject>();
        Layout(root);
        Walk(root, "", found, seen, allow);
        return found;
    }

    /// <summary>Why the last off-screen layout was refused (null: it was not); for diagnosing a tree that shows fewer elements than expected.</summary>
    public static string? LastLayoutError { get; private set; }

    private static void Layout(DependencyObject root)
    {
        if (root is not UIElement ui) return;
        try
        {
            var size = new Size(1200, 900);
            ui.Measure(size);
            ui.Arrange(new Rect(new Point(0, 0), size));
            ui.UpdateLayout();
        }
        catch (Exception lex)
        {
            LastLayoutError = lex.GetType().Name + ": " + lex.Message;
            // a Window that was never shown may refuse; its content can still be laid out
            if (root is Window { Content: UIElement c })
                try { c.Measure(new Size(1200, 900)); c.Arrange(new Rect(0, 0, 1200, 900)); c.UpdateLayout(); } catch { }
        }
    }

    /// <summary>True for text that needs no translation: empty, no letter at all (numbers, punctuation, symbols), a product or unit name, or a hotkey combo ("Alt+C", "Ctrl + Shift + F9", "Ctrl+Delete").</summary>
    public static bool IsAllowed(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || !t.Any(char.IsLetter)) return true;
        if (t.Contains('+') && IsCombo(t)) return true;
        var tokens = Regex.Split(t, @"[\s/,\-]+").Where(s => s.Length > 0).ToList();
        var dates = DateTokens();
        // a single character counts only as an uppercase letter (a key cap: "C", "F"); a number with letters ("45s") is not accepted
        return tokens.Count > 0 && tokens.All(tok => s_words.Contains(tok) || dates.Contains(tok) || !tok.Any(char.IsLetter) || (tok.Length == 1 && char.IsUpper(tok[0])) || Regex.IsMatch(tok, @"^F\d{1,2}$"));
    }

    private static bool IsCombo(string t)
    {
        var parts = Regex.Split(t, @"\s*\+\s*");
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
        return parts.Any(p => s_modifiers.Contains(p)) && parts.All(p => s_modifiers.Contains(p) || IsKey(p));
    }

    private static bool IsKey(string p) =>
        s_keys.Contains(p) || p.Length == 1 || Regex.IsMatch(p, @"^(F\d{1,2}|Num[0-9*+./\-])$", RegexOptions.IgnoreCase);

    private static readonly Regex s_bidiMarks = new("[\\u200E\\u200F\\u202A-\\u202E\\u2066-\\u2069]", RegexOptions.Compiled);

    private static void Check_(string where, string? text, List<string> found, Func<string, bool>? allow)
    {
        if (text == null) return;
        text = s_bidiMarks.Replace(text, "");   // direction marks (L.Ltr / L.Isolate) are not text that needs translating
        string rest = StripFrames(text);   // what is left when every catalog segment is cut out must need no translation
        if (IsAllowed(rest) || (allow != null && (allow(text) || allow(rest)))) return;
        found.Add(where + ": " + text);
    }

    private static void Walk(DependencyObject node, string path, List<string> found, HashSet<DependencyObject> seen, Func<string, bool>? allow)
    {
        if (!seen.Add(node)) return;
        string here = path + (path.Length == 0 ? "" : "/") + node.GetType().Name + (node is FrameworkElement { Name.Length: > 0 } named ? "#" + named.Name : "");

        if (node is FrameworkElement fe0) { try { fe0.ApplyTemplate(); } catch { } }

        switch (node)
        {
            case TextBlock tb:
                foreach (var run in Runs(tb.Inlines)) Check_(here + ".Text", run.Text, found, allow);
                break;
            case Window w:
                Check_(here + ".Title", w.Title, found, allow);
                break;
        }
        if (node is ContentControl cc && cc.Content is string content) Check_(here + ".Content", content, found, allow);
        if (node is HeaderedContentControl hcc && hcc.Header is string hdr) Check_(here + ".Header", hdr, found, allow);
        if (node is HeaderedItemsControl hic && hic.Header is string ihdr) Check_(here + ".Header", ihdr, found, allow);
        if (node is MenuItem mi) Check_(here + ".InputGestureText", mi.InputGestureText, found, allow);
        if (node is ItemsControl ic)
            foreach (object item in ic.Items)
                if (item is string s) Check_(here + ".Item", s, found, allow);
        if (node is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip) Check_(here + ".ToolTip", tip, found, allow);
            else if (fe.ToolTip is DependencyObject tipTree) Walk(tipTree, here + "/ToolTip", found, seen, allow);
            if (fe.ContextMenu is { } menu) Walk(menu, here + "/ContextMenu", found, seen, allow);
        }
        else if (node is FrameworkContentElement fce)   // Hyperlink, Run, Span ...
        {
            if (fce.ToolTip is string ctip) Check_(here + ".ToolTip", ctip, found, allow);
            else if (fce.ToolTip is DependencyObject ctipTree) Walk(ctipTree, here + "/ToolTip", found, seen, allow);
            if (fce.ContextMenu is { } cmenu) Walk(cmenu, here + "/ContextMenu", found, seen, allow);
        }
        Check_(here + ".AutomationName", AutomationProperties.GetName(node), found, allow);
        Check_(here + ".AutomationHelp", AutomationProperties.GetHelpText(node), found, allow);

        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject d) Walk(d, here, found, seen, allow);
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(node); i < n; i++) Walk(VisualTreeHelper.GetChild(node, i), here, found, seen, allow);
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run r) yield return r;
            else if (inline is Span s) foreach (var inner in Runs(s.Inlines)) yield return inner;
        }
    }

    // ------------------------------------------------------------------ self-test

    private static IDisposable SampleCatalog() => L.UseCatalogs(CatalogSet.Build(new[]
    {
        new CatalogSource("en", "sample", """{ "Save clip": "Save clip", "Cancel": "Cancel", "Don't save": "Don't save" }"""),
    }));

    [UnitTest]
    public static void The_harness_flags_a_hard_coded_literal_and_passes_a_localized_one()
    {
        using var catalog = SampleCatalog();
        using var scope = L.Scope("qps-ploc");
        var localized = (StackPanel)System.Windows.Markup.XamlReader.Parse(
            """<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:l="http://schemas.euphoricsoftware.com/clink"><TextBlock Text="{l:T 'Save clip'}"/><Button Content="{l:T 'Cancel'}" ToolTip="{l:T 'Don\'t save'}"/><TextBlock Text="12"/><TextBlock Text="Alt+C"/></StackPanel>""");
        Check.Equal(Array.Empty<string>(), Unlocalized(localized).ToArray(), "every string went through L");

        var hardCoded = new StackPanel();
        hardCoded.Children.Add(new TextBlock { Text = "Save clip" });
        hardCoded.Children.Add(new Button { Content = "Cancel", ToolTip = L.T("Cancel") });
        var bad = Unlocalized(hardCoded);
        Check.True(bad.Any(b => b.EndsWith("TextBlock.Text: Save clip", StringComparison.Ordinal)), "the literal TextBlock: " + string.Join(" | ", bad));
        Check.True(bad.Any(b => b.EndsWith("Button.Content: Cancel", StringComparison.Ordinal)), "the literal Button content: " + string.Join(" | ", bad));
        Check.False(bad.Any(b => b.Contains("ToolTip")), "the localized tooltip passes: " + string.Join(" | ", bad));

        Check.Equal(0, Unlocalized(hardCoded, text => text is "Save clip" or "Cancel").Count, "the caller's allow-list is honoured");
    }

    [UnitTest]
    public static void The_harness_checks_titles_headers_inlines_and_automation_text()
    {
        var w = new Window { Title = "Hard title" };
        var sp = new StackPanel();
        var tb = new TextBlock();
        tb.Inlines.Add(new Run("First run"));
        tb.Inlines.Add(new Bold(new Run("bold run")));
        sp.Children.Add(tb);
        sp.Children.Add(new Expander { Header = "Hard header" });
        var named = new Border();
        AutomationProperties.SetName(named, "Hard name");
        AutomationProperties.SetHelpText(named, "Hard help");
        sp.Children.Add(named);
        w.Content = sp;
        var texts = Unlocalized(w).Select(s => s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..]).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Check.Equal(new[] { "First run", "Hard header", "Hard help", "Hard name", "Hard title", "bold run" }, texts);
    }

    [UnitTest]
    public static void The_allow_rules_pass_numbers_product_names_and_hotkey_combos_only()
    {
        foreach (var ok in new[] { "", "  ", "12", "1:30", "...", "Clink", "Copium+", "Alt+C", "Ctrl + Shift + F9", "F12", "-", "Ctrl+Delete", "Alt+Shift+Home", "Win+Up", "Ctrl + Menu", "Shift+Tab", "Ctrl+Num5" })
            Check.True(IsAllowed(ok), "\"" + ok + "\"");
        foreach (var no in new[] { "Save", "Clip saved", "Alt+Save", "Open Clink", "Delete+Home", "Ctrl+", "45 s", "820 kB", "9.5 MB", "1.2 GB", "5 Mbps", "1 h", "45s", "a" })
            Check.False(IsAllowed(no), "\"" + no + "\"");
    }

    [UnitTest]
    public static void Nested_pseudo_frames_are_stripped_and_a_hard_coded_tail_is_still_flagged()
    {
        using var catalog = L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "sample", """{ "{size} free": "{size} free", "unit|{n} kB": "{n} kB", "Save clip": "Save clip" }"""),
        }));
        using var scope = L.Scope("qps-ploc");
        string nested = L.T("{size} free", ("size", L.Bytes(5000)));
        Check.True(nested.StartsWith("[!! [!! ", StringComparison.Ordinal), "the text really is nested: " + nested);
        Check.True(IsAllowed(StripFrames(nested)), "a nested text strips to nothing: " + StripFrames(nested));
        string tail = nested + " (approx)";
        Check.False(IsAllowed(StripFrames(tail)), "a hard-coded tail after nested frames is flagged: " + StripFrames(tail));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = nested });
        sp.Children.Add(new TextBlock { Text = tail });
        var bad = Unlocalized(sp);
        Check.Equal(1, bad.Count, string.Join(" | ", bad));
        Check.Contains(bad[0], "(approx)");
    }

    [UnitTest]
    public static void Raw_sizes_are_flagged_but_L_output_and_L_dates_are_not()
    {
        using var catalog = L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "sample", """{ "unit|{n} kB": "{n} kB", "unit|{n} MB": "{n} MB", "unit|{n} s": "{n} s" }"""),
        }));
        using var scope = L.Scope("qps-ploc");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "820 kB" });                                  // raw (as Capabilities.FormatBytes)
        sp.Children.Add(new TextBlock { Text = L.Bytes(820000) });                            // through L
        sp.Children.Add(new TextBlock { Text = L.Duration(45) });
        sp.Children.Add(new TextBlock { Text = L.Date(new DateTime(2026, 3, 5, 13, 30, 0), L.DateKind.LongDate) });
        sp.Children.Add(new TextBlock { Text = L.Date(new DateTime(2026, 3, 5, 13, 30, 0), L.DateKind.ShortDateTime) });
        var bad = Unlocalized(sp);
        Check.Equal(1, bad.Count, string.Join(" | ", bad));
        Check.Contains(bad[0], "820 kB");
        Check.True(PseudoLocale.IsPseudo(L.Bytes(820000)));
    }

    [UnitTest]
    public static void A_bare_key_name_is_not_a_hotkey()
    {
        foreach (var word in new[] { "Delete", "Pause", "Home", "End", "Up", "Menu", "Tab", "Enter", "Space", "Insert", "Control", "Escape" })
            Check.False(IsAllowed(word), "\"" + word + "\" alone is a word");
    }

    [UnitTest]
    public static void A_hard_coded_fragment_next_to_a_catalog_text_is_flagged()
    {
        using var catalog = SampleCatalog();
        using var scope = L.Scope("qps-ploc");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = L.T("Save clip") + " to folder" });
        sp.Children.Add(new TextBlock { Text = L.T("Save clip") + ": 5 / 10" });
        sp.Children.Add(new TextBlock { Text = L.T("Cancel") + " Alt+C" });
        var bad = Unlocalized(sp);
        Check.Equal(1, bad.Count, string.Join(" | ", bad));
        Check.Contains(bad[0], "to folder");
    }

    [UnitTest]
    public static void Context_menus_and_tooltip_trees_are_walked()
    {
        using var catalog = SampleCatalog();
        using var scope = L.Scope("qps-ploc");
        var root = new Border
        {
            ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "Hard menu item" }, new MenuItem { Header = L.T("Save clip"), InputGestureText = "Ctrl+S" } } },
            ToolTip = new StackPanel { Children = { new TextBlock { Text = "Hard tip body" } } },
        };
        var texts = Unlocalized(root).Select(s => s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..]).Distinct().ToArray();
        Check.Equal(new[] { "Hard menu item", "Hard tip body" }, texts.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        var viaToolTip = new Border { ToolTip = new ToolTip { Content = "Hard tool tip" } };
        Check.True(Unlocalized(viaToolTip).Any(b => b.EndsWith("Hard tool tip", StringComparison.Ordinal)), "a ToolTip control with string content");
    }

    private sealed record Row(string Name);

    [UnitTest]
    public static void Items_and_data_templates_are_materialised_before_walking()
    {
        using var catalog = SampleCatalog();
        using var scope = L.Scope("qps-ploc");
        // an ItemsControl with an explicit template: no theme is needed to produce the item elements
        var list = (ItemsControl)System.Windows.Markup.XamlReader.Parse(
            """<ItemsControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemsControl.Template><ControlTemplate TargetType="ItemsControl"><ItemsPresenter/></ControlTemplate></ItemsControl.Template><ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding Name}"/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>""");
        list.ItemsSource = new[] { new Row("Hard row") };
        var bad = Unlocalized(list);
        Check.True(bad.Any(b => b.EndsWith("TextBlock.Text: Hard row", StringComparison.Ordinal)), "a templated item: " + string.Join(" | ", bad) + " / layout error: " + LastLayoutError);

        var strings = new ComboBox();
        strings.Items.Add("Hard choice");
        strings.Items.Add(L.T("Save clip"));
        var s = Unlocalized(strings);
        Check.True(s.Any(x => x.Contains(".Item: Hard choice")), string.Join(" | ", s));
        Check.False(s.Any(x => x.Contains("[!!")), "the catalog item passes: " + string.Join(" | ", s));
    }
}
