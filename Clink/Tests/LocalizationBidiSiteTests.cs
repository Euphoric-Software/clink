using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clink.Audio;
using Clink.Engine;
using Clink.Screenshots;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>
/// Slice S6 phase B: the places that put user data (game, device and app names, dates, sizes, versions) into sentences keep it in order
/// under a right-to-left language and stay byte-identical in English; the sentence joins use L.JoinSentences; RtlMirror flips a
/// directional glyph only under right-to-left; the tooltip style, the stand-alone menus and the shell texts follow the language.
/// </summary>
internal static class LocalizationBidiSiteTests
{
    private const string Lrm = "\u200E", Rlm = "\u200F", Nbsp = "\u00A0";
    private const string Ltr = "\u200E\u202A", LtrEnd = "\u202C\u200E";

    // ------------------------------------------------------------------ call sites

    [UnitTest]
    public static void Game_names_in_clip_rows_footers_and_toasts_are_isolated_only_in_a_right_to_left_language()
    {
        var clip = new ClipEntry { Path = @"C:\Clips\x.mp4", Game = "Neon Drift", Duration = 90, Size = 190_000_000, Created = new DateTime(2026, 10, 12, 14, 3, 0) };
        string en, enFooter, enSubtitle, enMode;
        using (L.Scope("en"))
        {
            var row = new ClipRow(clip);
            Check.Equal("Neon Drift", row.Title);
            en = row.Meta;
            enFooter = MainWindow.FooterLine("Neon Drift", "NVENC", 29.6, 0);
            enSubtitle = Toasts.ClipSubtitle(clip);
            enMode = MainWindow.ModeStatusText(true, "Neon Drift", "library");
            Check.False(en.Contains(Lrm) || enFooter.Contains(Lrm) || enSubtitle.Contains(Lrm) || enMode.Contains(Lrm), "English has no marks");
            Check.Equal("Neon Drift · 1:30 · 190 MB", enSubtitle);
        }
        using (L.Scope("ar"))
        {
            var row = new ClipRow(clip);
            Check.Equal(Lrm + "Neon Drift" + Lrm, row.Title, "a Latin game name: left-to-right mark");
            Check.True(row.Meta.StartsWith(Lrm, StringComparison.Ordinal) && row.Meta.Contains(Lrm + " · "), "the date is isolated: " + row.Meta);
            Check.True(MainWindow.FooterLine("Neon Drift", "NVENC", 29.6, 0).Contains(Lrm + "Neon Drift" + Lrm), "the footer isolates the game");
            Check.True(Toasts.ClipSubtitle(clip).Contains(Lrm + "Neon Drift" + Lrm), "the toast subtitle isolates the game");
            Check.True(MainWindow.ModeStatusText(true, "Neon Drift", "library").Contains(Lrm + "Neon Drift" + Lrm), "the mode line isolates the game");
            clip.Game = "\u0644\u0639\u0628\u0629";
            Check.Equal(Rlm + "\u0644\u0639\u0628\u0629" + Rlm, new ClipRow(clip).Title, "an Arabic game name: right-to-left mark");
        }
    }

    [UnitTest]
    public static void The_recording_status_line_isolates_the_game_on_screen_and_keeps_the_log_free_of_marks()
    {
        using (L.Scope("ar"))
        {
            var note = RecordingPolicy.StatusLineNote(65, "Neon Drift");
            Check.True(note.Shown.Contains(Lrm + "Neon Drift" + Lrm), "on screen: " + note.Shown);
            Check.False(note.English.Contains(Lrm) || note.English.Contains(Rlm), "the log: " + note.English);
            Check.Contains(AppController.StatusTextFor(true, RecordingState.Recording, false, null, "Neon Drift"), Lrm + "Neon Drift" + Lrm);
        }
        using (L.Scope("en"))
        {
            var note = RecordingPolicy.StatusLineNote(65, "Neon Drift");
            Check.Equal(note.English, note.Shown);
            Check.Equal("Recording · Neon Drift", AppController.StatusTextFor(true, RecordingState.Recording, false, null, "Neon Drift"));
        }
    }

    [UnitTest]
    public static void Audio_entries_keep_their_spaces_together_and_isolate_an_app_or_device_name()
    {
        var game = new AudioTrackStatus(TrackKind.Game, true, AudioDetail.EverythingExcept("Discord"));
        var mic = new AudioTrackStatus(TrackKind.Mic, false, AudioDetail.Named("USB Microphone"), MixOnly: true);
        using (L.Scope("en"))
        {
            Check.Equal("●" + Nbsp + "Game" + Nbsp + "(everything" + Nbsp + "except" + Nbsp + "Discord)", EngineStatusText.AudioEntry(game));
            Check.Equal("○" + Nbsp + "Mic" + Nbsp + "(USB" + Nbsp + "Microphone,"+ Nbsp + "mix" + Nbsp + "only)", EngineStatusText.AudioEntry(mic));
            Check.Equal("●" + Nbsp + "Game" + Nbsp + "(everything" + Nbsp + "except" + Nbsp + "Discord)   ○" + Nbsp + "Mic" + Nbsp + "(USB" + Nbsp + "Microphone," + Nbsp + "mix" + Nbsp + "only)", EngineStatusText.AudioLine(new[] { game, mic }));
        }
        using (L.Scope("ar"))
        {
            string entry = EngineStatusText.AudioEntry(mic);
            Check.True(entry.Contains(Lrm + "USB" + Nbsp + "Microphone" + Lrm) || entry.Contains(Lrm + "USB Microphone" + Lrm) || entry.Contains("USB" + Nbsp + "Microphone"), "the device name is in the entry: " + entry);
            Check.False(entry.Contains(' '), "no breaking space in an entry: " + entry);
            Check.True(EngineStatusText.Detail(AudioDetailKind.EverythingExcept, "Discord").Contains(Lrm + "Discord" + Lrm), "the app name is isolated");
        }
    }

    [UnitTest]
    public static void Library_rows_wrap_dimensions_and_dates_for_a_right_to_left_language()
    {
        var shot = new ScreenshotEntry { Path = @"C:\Shots\a.png", Width = 2560, Height = 1440, Size = 1_200_000, Created = new DateTime(2026, 10, 12, 14, 3, 0) };
        string en;
        using (L.Scope("en")) { en = ScreenshotRow.MetaFor(shot, 2560, 1440); Check.False(en.Contains(Lrm)); Check.True(en.StartsWith("2560×1440 · ", StringComparison.Ordinal)); }
        using (L.Scope("ar"))
        {
            string ar = ScreenshotRow.MetaFor(shot, 2560, 1440);
            Check.True(ar.StartsWith(Ltr + "2560×1440" + LtrEnd + " · ", StringComparison.Ordinal), "dimensions: " + ar);
            Check.True(ar.EndsWith(Lrm, StringComparison.Ordinal), "the date is isolated: " + ar);
            shot.Game = "Neon Drift";
            Check.Equal(Lrm + "Neon Drift" + Lrm, ScreenshotRow.TitleFor(shot));
        }
    }

    [UnitTest]
    public static void Version_and_adapter_arguments_of_notes_are_marked_for_the_screen_only()
    {
        var set = CatalogSet.Build(new[] { new CatalogSource("en", "t", """{ "v{version} on {adapter}": "v{version} on {adapter}", "{app} copy": "{app} copy" }""") });
        using var _ = L.UseCatalogs(set);
        var note = Note.Of("v{version} on {adapter}", ("version", Note.Ltr("0.4.5")), ("adapter", Note.Ltr("NVIDIA GeForce RTX 4090")));
        using (L.Scope("en")) Check.Equal("v0.4.5 on NVIDIA GeForce RTX 4090", note.Shown);
        using (L.Scope("ar"))
        {
            Check.Equal("v" + Ltr + "0.4.5" + LtrEnd + " on " + Ltr + "NVIDIA GeForce RTX 4090" + LtrEnd, note.Shown);
            Check.Equal("v0.4.5 on NVIDIA GeForce RTX 4090", note.English);
        }
        var app = Note.Of("{app} copy", ("app", Note.Isolate("Notepad")));
        using (L.Scope("ar")) Check.Equal(Lrm + "Notepad" + Lrm + " copy", app.Shown);
        Check.Equal("Notepad copy", app.English);
    }

    [UnitTest]
    public static void The_icon_only_close_buttons_of_the_toast_the_share_window_and_the_trim_window_are_named_Close()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        static Button[] Crosses(DependencyObject root)
        {
            var found = new List<Button>();
            void Visit(DependencyObject d)
            {
                if (d is FrameworkElement fe) { try { fe.ApplyTemplate(); } catch { } }
                if (d is Button { Content: "\u2715" } b) found.Add(b);
                foreach (object child in LogicalTreeHelper.GetChildren(d)) if (child is DependencyObject c) Visit(c);
            }
            Visit(root);
            return found.ToArray();
        }
        var clip = new ClipEntry { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-l10n-nonexistent", "x.mp4"), Size = 1000, Duration = 5, Created = DateTime.Now };
        using var rig = new TrimShareSplitTests.Rig(true, true);
        foreach (string language in new[] { "en", L.PseudoCode })
            using (L.Scope(language))
            {
                var windows = new List<(string, Window)> { ("share", new ShareWindow(clip)), ("toast", new ToastWindow(null!, "info", clip, null, "Clip saved", "x", TimeSpan.FromSeconds(7), false, null, false)) };
                using var trim = new TrimShareSplitTests.Win(rig.Entry);
                windows.Add(("trim", trim.Window));
                foreach (var (name, window) in windows)
                {
                    var crosses = Crosses(window);
                    Check.Equal(1, crosses.Length, name + ": one cross button");
                    string accessible = System.Windows.Automation.AutomationProperties.GetName(crosses[0]);
                    if (language == "en") Check.Equal("Close", accessible, name);
                    else Check.True(PseudoLocale.IsPseudo(accessible), name + ": the name is from the catalog: " + accessible);
                }
            }
    }

    // ------------------------------------------------------------------ the sources

    [UnitTest]
    public static void Sentences_are_joined_with_JoinSentences_and_the_updater_numbers_follow_the_regional_format()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;
        // string.Join(" ", ...) / L.T(...) + " " + ... outside this list is a sentence join that would put a space after a Japanese or Chinese full stop
        var allowed = new[] { ("Engine/ClipSource.cs", "string.Join(\" \", t)"), ("Services/Localization/StatusText.cs", "(track.Running ? \"●\" : \"○\") + \" \" +") };   // a timestamp list; a glyph and a name, not sentences
        var offenders = new List<string>();
        int seen = 0;
        foreach (string file in System.IO.Directory.EnumerateFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            string rel = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("Tests/", StringComparison.Ordinal)) continue;
            seen++;
            foreach (string raw in System.IO.File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                bool join = line.Contains("string.Join(\" \",", StringComparison.Ordinal)
                    || (line.Contains("L.T(", StringComparison.Ordinal) || line.Contains("L.Plural(", StringComparison.Ordinal)) && (line.Contains("+ \" \"", StringComparison.Ordinal) || line.Contains("\" \" +", StringComparison.Ordinal));
                if (join && !allowed.Any(a => a.Item1 == rel && line.Contains(a.Item2, StringComparison.Ordinal))) offenders.Add(rel + ": " + line);
            }
        }
        Check.True(seen > 100, "the scan read the sources: " + seen);
        Check.Equal(Array.Empty<string>(), offenders.ToArray(), "sentence joins outside L.JoinSentences");

        // the updater's numbers inside sentences: the regional separator (a deliberate English change on a PC with a decimal comma)
        var bad = System.IO.File.ReadLines(System.IO.Path.Combine(root, "Services", "Updater.cs"))
            .Where(l => (l.Contains("(\"rate\",", StringComparison.Ordinal) || l.Contains("(\"seconds\",", StringComparison.Ordinal)) && !l.Contains("L.FormatCulture", StringComparison.Ordinal))
            .Select(l => l.Trim()).ToArray();
        Check.Equal(Array.Empty<string>(), bad, "{rate}/{seconds} arguments without L.FormatCulture");
    }

    [UnitTest]
    public static void Directional_glyphs_are_marked_and_the_other_glyphs_are_not()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;
        string pane = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "UI", "ClipsPane.xaml"));
        Check.True(System.Text.RegularExpressions.Regex.IsMatch(pane, "x:Name=\"Chevron\"[^>]*l:RtlMirror\\.Glyph=\"True\""), "the clip row's chevron mirrors");
        string bar = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "UI", "Screenshots", "EditorToolbar.xaml"));
        foreach (string name in new[] { "UndoButton", "RedoButton" })
            Check.True(System.Text.RegularExpressions.Regex.IsMatch(bar, "x:Name=\"" + name + "\"[^>]*l:RtlMirror\\.Glyph=\"True\""), name + " mirrors");
        Check.Equal(3, System.Text.RegularExpressions.Regex.Matches(pane + bar, "RtlMirror\\.Glyph=").Count, "only the chevron, Undo and Redo");
    }

    // ------------------------------------------------------------------ RtlMirror, tooltips, menus, shell texts

    [UnitTest]
    public static void RtlMirror_flips_horizontally_under_a_right_to_left_flow_and_not_otherwise()
    {
        using (L.Scope("qps-plocm"))
        {
            var glyph = new TextBlock { Text = "\uE76C" };
            var root = new Border { Child = glyph, FlowDirection = FlowDirection.RightToLeft };
            RtlMirror.SetGlyph(glyph, true);
            Check.Equal(-1.0, glyph.RenderTransform.Value.M11, "right to left: flipped");
            Check.Equal(new Point(0.5, 0.5), glyph.RenderTransformOrigin, "around its middle");
            RtlMirror.SetGlyph(glyph, false);
            Check.True(glyph.RenderTransform.Value.IsIdentity, "off again");
            Check.Equal(new Point(0, 0), glyph.RenderTransformOrigin, "and the origin it set is put back");

            // the editor's Undo and Redo: built before they are in any tree, flipped when they load under a right-to-left window root
            var toolbar = new EditorToolbar();
            var host = new Border { Child = toolbar, FlowDirection = FlowDirection.RightToLeft };
            toolbar.UndoButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            toolbar.RedoButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check.Equal(-1.0, toolbar.UndoButton.RenderTransform.Value.M11, "Undo");
            Check.Equal(-1.0, toolbar.RedoButton.RenderTransform.Value.M11, "Redo");
            Check.True(toolbar.WidthText.FlowDirection == FlowDirection.LeftToRight, "the value text stays left to right inside a right-to-left toolbar");
        }

        // English: nothing is touched, whatever the flow
        var textBlock = new TextBlock { Text = "\uE76C" };
        var enRoot = new Border { Child = textBlock, FlowDirection = FlowDirection.RightToLeft };
        RtlMirror.SetGlyph(textBlock, true);
        textBlock.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check.True(textBlock.RenderTransform.Value.IsIdentity, "English: identity");
        Check.Equal(new Point(0, 0), textBlock.RenderTransformOrigin, "English: origin untouched");
        // switching it off in English does nothing either: an origin or a transform the element has of its own stays
        textBlock.RenderTransformOrigin = new Point(1, 1);
        textBlock.RenderTransform = new ScaleTransform(2, 2);
        RtlMirror.SetGlyph(textBlock, false);
        Check.Equal(new Point(1, 1), textBlock.RenderTransformOrigin, "English: SetGlyph(false) leaves the element's own origin");
        Check.Equal(2.0, textBlock.RenderTransform.Value.M11, "English: SetGlyph(false) leaves the element's own transform");
        var english = new EditorToolbar();
        var enHost = new Border { Child = english };
        Check.True(english.UndoButton.RenderTransform.Value.IsIdentity && english.RedoButton.RenderTransform.Value.IsIdentity, "English: identity");
    }

    [UnitTest]
    public static void RtlMirror_keeps_nothing_alive_that_outlives_the_element()
    {
        // Undo/Redo are marked in every editor and overlay window: a strong reference kept by the property (a value-changed handler)
        // would keep each closed window alive. Build, drop, collect, offscreen.
        static WeakReference[] Build(string locale)
        {
            using (L.Scope(locale))
            {
                var toolbar = new EditorToolbar();
                var host = new Border { Child = toolbar };
                if (L.IsRtl) host.FlowDirection = FlowDirection.RightToLeft;
                toolbar.UndoButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                return new[] { new WeakReference(toolbar), new WeakReference(toolbar.UndoButton), new WeakReference(host) };
            }
        }
        Build("en");   // the first toolbar ever built in a process stays alive (WPF keeps its first-use resource state); it is not the one under test
        foreach (string locale in new[] { "en", "qps-plocm" })
        {
            var refs = Build(locale);
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Check.False(refs.Any(r => r.IsAlive), locale + ": the toolbar and its Undo button are collectable");
        }
    }
    [UnitTest]
    public static void The_tooltip_style_is_right_to_left_in_a_right_to_left_language_and_removed_again()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        Check.False(RtlWindows.SetsRightToLeft(Application.Current.TryFindResource(typeof(ToolTip)) as Style), "not set before");
        try
        {
            using (L.Scope("en")) { RtlWindows.ApplyAppStyles(); Check.False(RtlWindows.SetsRightToLeft(Application.Current.TryFindResource(typeof(ToolTip)) as Style), "nothing in a left-to-right language"); }
            using (L.Scope("qps-plocm"))
            {
                RtlWindows.ApplyAppStyles();
                var style = (Style)Application.Current.TryFindResource(typeof(ToolTip));
                Check.True(RtlWindows.SetsRightToLeft(style), "the app tooltip style sets FlowDirection");
                Check.True(style.BasedOn != null, "on top of the theme's tooltip style (padding, colours)");
                // a tooltip of a control that is pinned left to right still reads right to left
                var pinned = new Border { FlowDirection = FlowDirection.LeftToRight };
                var tip = new ToolTip { Content = "x" };
                pinned.ToolTip = tip;
                tip.ApplyTemplate();
                Check.Equal(FlowDirection.RightToLeft, tip.FlowDirection, "the style beats the inherited left-to-right");
            }
        }
        finally { RtlWindows.RemoveAppStyles(); }
        Check.False(RtlWindows.SetsRightToLeft(Application.Current.TryFindResource(typeof(ToolTip)) as Style), "removed");
    }

    [UnitTest]
    public static void The_stand_alone_menus_take_the_flow_direction_of_their_owner()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // (ShareMenu.Show opens a popup, which a test must not do)
        string share = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "UI", "ShareMenu.cs"));
        Check.Contains(share, "FlowDirection = target.FlowDirection");
        using (L.Scope("qps-plocm"))
        {
            var canvas = new EditorCanvas();
            Check.Equal(FlowDirection.LeftToRight, canvas.FlowDirection, "the canvas is pinned");
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "UI", "Screenshots", "EditorCanvas.cs"));
            Check.Contains(text, "if (L.IsRtl) menu.FlowDirection = FlowDirection.RightToLeft;");
        }
    }

    [UnitTest]
    public static void Shell_texts_start_with_a_right_to_left_mark_only_in_a_right_to_left_language()
    {
        using (L.Scope("en")) { Check.Equal("Clink · Paused", AppController.RtlFirst("Clink · Paused")); Check.Equal("", AppController.RtlFirst("")); }
        using (L.Scope("ar")) { Check.Equal(Rlm + "Clink · Paused", AppController.RtlFirst("Clink · Paused")); Check.Equal("", AppController.RtlFirst("")); }
        using (L.Scope("ar"))
        {
            // the tray tooltip stays inside the 127 characters NotifyIcon.Text allows
            string tip = AppController.TrayText("Clink", new string('x', 300));
            Check.True(AppController.RtlFirst(tip).Length <= 127, "tooltip length " + AppController.RtlFirst(tip).Length);
        }
    }

    [UnitTest]
    public static void The_tray_tooltip_is_cut_on_the_plain_text_never_inside_a_surrogate_pair_or_an_embedding()
    {
        static bool LoneSurrogate(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i])) { if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true; i++; }
                else if (char.IsLowSurrogate(s[i])) return true;
            }
            return false;
        }
        foreach (string language in new[] { "en", "qps-plocm" })
            using (L.Scope(language))
            {
                // every position of an astral character around the cut (the name and " \u00B7 " take 8 characters)
                for (int at = 105; at <= 118; at++)
                {
                    string tip = AppController.TrayText("Clink", new string('x', at - 8) + "\U0001F600" + new string('y', 200));
                    Check.False(LoneSurrogate(tip), language + ": a surrogate pair is split at " + at);
                    Check.True(AppController.RtlFirst(tip).Length <= 127, language + ": length " + AppController.RtlFirst(tip).Length);
                }
                // a left-to-right embedding (L.Ltr) that the cut lands in is closed
                string embedded = AppController.TrayText("Clink", new string('x', 100) + L.Ltr(new string('p', 200)));
                Check.Equal(embedded.Count(c => c == '\u202A'), embedded.Count(c => c == '\u202C'), language + ": every LRE has its PDF in " + embedded.Length + " characters");
                Check.True(AppController.RtlFirst(embedded).Length <= 127, language + ": length " + AppController.RtlFirst(embedded).Length);
                Check.Equal("Clink \u00B7 Paused", AppController.TrayText("Clink", "Paused"), language + ": a short text is untouched");
            }
    }

    [UnitTest]
    public static void The_direction_of_a_text_ignores_marks_format_characters_and_punctuation_inside_the_right_to_left_blocks()
    {
        using (L.Scope("qps-plocm"))
        {
            // the Arabic comma (CS), per mille and percent signs (ET), the neutral signs (ON), the harakat and other marks (Mn), the number signs and the end of ayah (Cf): in the Arabic block, not letters
            foreach (string neutral in new[] { "\u060C", "\u0609", "\u066A", "\u0606", "\u060E", "\u06DE", "\u06E9", "\u064B", "\u0650", "\u065F", "\u0600", "\u0605", "\u06DD", "\u0615" })
            {
                Check.Equal('\u200E', L.Isolate(neutral + "abc")[0], "U+" + ((int)neutral[0]).ToString("X4") + " does not decide: the Latin letter after it does");
                Check.Equal('\u200F', L.Isolate(neutral + "\u05D0bc")[0], "U+" + ((int)neutral[0]).ToString("X4") + " does not decide: the Hebrew letter after it does");
            }
            // the strong right-to-left punctuation (AL, R) does decide, before a Latin letter: the Arabic semicolon, question mark, full stop, afghani sign; the Hebrew maqaf, geresh
            foreach (string strong in new[] { "\u061B", "\u061F", "\u06D4", "\u060B", "\u05BE", "\u05F3" })
                Check.Equal('\u200F', L.Isolate(strong + "abc")[0], "U+" + ((int)strong[0]).ToString("X4") + " is strong right to left");
            Check.Equal('\u200F', L.Isolate("\u0627bc")[0], "an Arabic letter decides");
            Check.Equal('\u200E', L.Isolate("\u0661\u0662 abc")[0], "digits do not decide");
            // U+FDCF, U+FDFE and U+FDFF (ON) and the Rumi numeral symbols U+10E60-U+10E7E (AN, astral) do not decide either
            foreach (string neutral in new[] { "\uFDCF", "\uFDFE", "\uFDFF", "\U00010E60", "\U00010E7E", "\U00010E6A" })
            {
                string shown = "U+" + char.ConvertToUtf32(neutral, 0).ToString("X4");
                Check.Equal('\u200E', L.Isolate(neutral + "abc")[0], shown + " does not decide: the Latin letter after it does");
                Check.Equal('\u200F', L.Isolate(neutral + "\u05D0bc")[0], shown + " does not decide: the Hebrew letter after it does");
            }
        }
    }
}
